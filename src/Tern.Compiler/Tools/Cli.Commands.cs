using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tools;

public static partial class Cli
{
    /// <summary>
    /// The three commands, writing to one output and one error stream. <paramref name="jsonOut"/> is whether
    /// <c>--json</c> was given anywhere, so that a command-line problem is JSON too (review code-quality-tools 13).
    /// </summary>
    private sealed class Commands(TextWriter output, TextWriter error, bool jsonOut)
    {
        // ---------- tern <file.tern> ----------

        public int Compile(string[] arguments)
        {
            if (Read(Command.Compile, arguments, out var bad) is not { } args) return Usage1(bad!);
            bool json = args.Has("--json");
            string? input = args.Files.FirstOrDefault(), outputPath = args.Value("-o");
            if (string.IsNullOrWhiteSpace(input)) return Usage1("No source file given");
            if (args.WarningProblem is { } both) return Usage1(both);
            if (args.Platform(out var noTarget) is not { } platform) return Usage1(noTarget);
            if (outputPath is not null && OutputProblem(outputPath) is { } badOutput) return Fail(json, Diagnostic.Error(input, badOutput), badOutput);
            var result = CompileFile(input, platform, json, MaxErrors(args), args.Warnings);
            if (result is null) return 1;
            outputPath ??= Path.ChangeExtension(input, result.HaltAddress < 0 ? platform.BinaryExtension : platform.FileExtension);

            // Every path first, so that no output overwrites the source or another output (review code-quality-tools 3).
            var program = new List<(string Path, Action<string> Write)>
            {
                (outputPath, p => File.WriteAllBytes(p, result.File)),
                (Path.ChangeExtension(outputPath, ".lst"), p => File.WriteAllText(p, result.Listing)),
                (Path.ChangeExtension(outputPath, platform.Labels.Extension), p => File.WriteAllText(p, result.Labels)),
                (Path.ChangeExtension(outputPath, ".lines"), p => File.WriteAllText(p, LineTable.Format(LineTable.Rows(result, Path.GetFileName(input))))),
            };
            // The overlays and the API module, which the text output names one by one.
            var extra = new List<(string Path, Action<string> Write)>();
            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
            foreach (var overlay in result.Overlays)
                extra.Add((Path.Combine(directory, overlay.Name + platform.BinaryExtension), p => File.WriteAllBytes(p, overlay.File)));
            if (result.ApiModule is not null)
                extra.Add((Path.Combine(directory, result.ApiModuleName + ".tern"), p => File.WriteAllText(p, result.ApiModule)));
            var writes = program.Concat(extra).ToList();
            if (Clash(result.SourceFiles.Append(input), writes.Select(w => w.Path).ToList()) is { } clash) return Fail(json, Diagnostic.Error(input, clash), clash);
            foreach (var (path, write) in writes)
            {
                try { write(path); }
                catch (Exception e) when (FileProblem(e) is { } why)
                {
                    string text = $"Cannot write {path}: {why}";
                    return Fail(json, Diagnostic.Error(input, text), text);
                }
            }

            if (json)
            {
                output.WriteLine(Diagnostic.ToJson(true, writes.Select(w => w.Path).ToList(), result.Report, Warnings(result, input)));
                return 0;
            }
            foreach (var (path, _) in extra) output.WriteLine($"Wrote {path}");
            output.WriteLine($"Wrote {outputPath}");
            output.Write(result.Report);
            return 0;
        }

        // ---------- tern run ----------

        public int Run(string[] arguments)
        {
            if (Read(Command.Run, arguments, out var bad) is not { } args) return Usage1(bad!);
            bool json = args.Has("--json");
            string? input = args.Files.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(input)) return Usage1("No source file given");
            if (args.WarningProblem is { } both) return Usage1(both);
            if (args.Platform(out var noTarget) is not { } platform) return Usage1(noTarget);

            var program = CompileFile(input, platform, json, MaxErrors(args), args.Warnings);
            if (program is null) return 1;
            List<MemoryDump> dumps;
            try { dumps = args.All("--dump").Select(d => MemoryDump.Parse(d, program)).ToList(); }
            catch (FormatException e) { return Fail(json, Diagnostic.Error(input, "--dump " + e.Message), "--dump " + e.Message); }
            RunResult run;
            try { run = Runner.Run(program, RunOptionsOf(args) with { MainFile = Path.GetFileName(input) }); }
            catch (RunError e) { return Fail(json, Diagnostic.Error(input, e.Message), e.Message); }
            bool screen = args.Has("--screen");
            var lines = args.Has("--profile") ? LineTable.Rows(program, Path.GetFileName(input), run.Profile) : null;
            output.Write(json ? RunReport.Json(run, dumps, screen, lines, Warnings(program, input)) + Environment.NewLine : RunReport.Text(run, dumps, screen, lines));
            return !run.Returned ? 2 : run.IndexErrors.Count > 0 ? 3 : 0;
        }

        // ---------- tern bench ----------

        public int Bench(string[] arguments)
        {
            if (Read(Command.Bench, arguments, out var bad) is not { } args) return Usage1(bad!);
            bool json = args.Has("--json");
            // The variants in the order given: files and --code snippets.
            var variants = args.Items.Where(x => x.Option is null || x.Option.Name == "--code")
                .Select(x => x.Option is null ? (Path: x.Text, Code: (string?)null) : (Path: (string?)null, Code: x.Text)).ToList();
            if (variants.Count == 0) return Usage1("No variant given: name .tern files or give --code");
            if (args.Platform(out var noTarget) is not { } platform) return Usage1(noTarget);

            var declarations = args.All("--decl").ToList();
            var dumps = args.All("--dump").ToList();
            int? maxErrors = MaxErrors(args) is > 0 and var n ? n : null;
            var results = new List<BenchResult>();
            int snippets = 0;
            // A variant is named by its file name, or by its path as given when two have the same name.
            var duplicateNames = variants.Where(v => v.Path is not null).GroupBy(v => Path.GetFileName(v.Path!))
                .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            foreach (var (path, code) in variants)
            {
                BenchVariant variant;
                if (code is not null) variant = BenchVariant.Snippet($"code {++snippets}", declarations, code);
                else
                {
                    string name = duplicateNames.Contains(Path.GetFileName(path!)) ? path! : Path.GetFileName(path!);
                    try { variant = new BenchVariant(name, File.ReadAllText(path!), path); }
                    catch (Exception e) when (FileProblem(e) is { } why)
                    {
                        string text = ReadProblem(path!, why);
                        return Fail(json, Diagnostic.Error(path!, text), text);
                    }
                }
                results.Add(Tools.Bench.Measure(variant, args.Value("--fn"), dumps, RunOptionsOf(args), maxErrors, platform));
            }
            output.Write(json ? Tools.Bench.Json(results) + Environment.NewLine : Tools.Bench.Text(results));
            return results.Any(r => r.Failed) ? 1 : 0;
        }

        // ---------- shared ----------

        private static int MaxErrors(Arguments args) => (int)args.Count("--max-errors", 0);

        /// <summary><c>--cycles</c>, <c>--irq</c> and <c>--set</c>, which <c>tern run</c> and <c>tern bench</c> share.</summary>
        private static RunOptions RunOptionsOf(Arguments args) => new()
        {
            MaxCycles = args.Count("--cycles", new RunOptions().MaxCycles),
            IrqEvery = args.Count("--irq", 0),
            Sets = args.All("--set").Select(MemorySet.Parse).ToList(),
        };

        private CompileResult? CompileFile(string input, Platform platform, bool json, int maxErrors, WarningMode warnings)
        {
            string source;
            try
            {
                source = File.ReadAllText(input);
            }
            catch (Exception e) when (FileProblem(e) is { } why)
            {
                string text = ReadProblem(input, why);
                Fail(json, Diagnostic.Error(input, text), text);
                return null;
            }
            try
            {
                var result = TernCompiler.Compile(source, platform, sourcePath: input, maxErrors: maxErrors > 0 ? maxErrors : null, warnings: warnings);
                // In JSON the warnings are in the result's diagnostics.
                if (!json && result.Warnings.Count > 0) Print(result.Warnings, false);
                return result;
            }
            catch (CompileError e)
            {
                // Every error found, with any warnings, sorted by place (spec 13); the text ends with their numbers.
                if (json) output.WriteLine(Diagnostic.ToJson(false, [], null, Diagnostic.All(e, input)));
                else Print(e.WithWarnings, e.MoreNotReported);
                return null;
            }

            void Print(IReadOnlyList<CompileError> list, bool more)
            {
                foreach (var x in list)
                {
                    var d = Diagnostic.From(x, input);
                    error.WriteLine($"{d.File}:{d.Line}:{d.Column}: {d.SeverityWord}: {x.Description}");
                }
                error.WriteLine(Diagnostic.Count(list.Count(x => !x.IsWarning), more, maxErrors, list.Count(x => x.IsWarning)));
            }
        }

        private static List<Diagnostic> Warnings(CompileResult result, string input) => result.Warnings.Select(w => Diagnostic.From(w, input)).ToList();

        private int Fail(bool json, Diagnostic d, string text)
        {
            if (json) output.WriteLine(Diagnostic.ToJson(false, [], null, [d]));
            else error.WriteLine(d.Line == 0 ? text : $"{d.File}:{d.Line}:{d.Column}: error: {text}");
            return 1;
        }

        public int Usage1(string problem)
        {
            string text = $"{problem}. Run 'tern --help' for the options";
            // With --json, a command-line problem is JSON too, with no file (review code-quality-tools 13).
            if (jsonOut) output.WriteLine(Diagnostic.ToJson(false, [], null, [Diagnostic.Error("", text)]));
            else error.WriteLine(text + ".");
            return 1;
        }
    }
}
