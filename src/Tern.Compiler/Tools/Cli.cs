using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tools;

/// <summary>
/// The <c>tern</c> command line: argument parsing and printing for compiling, <c>tern run</c> and
/// <c>tern bench</c>. In the compiler library so that tests can run the exact commands the skill shows.
/// Paths are relative to the current directory.
/// </summary>
public static class Cli
{
    public const string Usage = """
        Usage:
          tern --target <machine> [--json] [--max-errors <n>] [--no-warnings | --warnings-as-errors] <file.tern> [-o <output>]
          tern run --target <machine> <file.tern> [options]
          tern bench --target <machine> [options] <file.tern>... | --code <statements>...

        Every command names the machine it builds for: --target c64 (Commodore 64),
        --target atari (Atari 8-bit with 48 KB or more) or --target apple2 (Apple II with
        64 KB under ProDOS 8).

        tern --target <machine> <file.tern> compiles a Tern program. Writes:
          <output>.prg   the program
          <output>.lst   listing with addresses, bytes and cycles
          <output>.vs    VICE label file (load in the monitor with: ll "<output>.vs")
          <output>.lines bytes, cycles and addresses of each source line
        On the Atari the program is <output>.xex and the labels <output>.lbl (Altirra loads
        them with the program); on the Apple II the program is <output>.system, a ProDOS
        system file, and the labels <output>.sym (in AppleWin's debugger: symuser load
        "<path>", the path in quotes and relative to AppleWin's folder).

        A program with 'export' lines and no main is a binary for another program to call;
        it also writes <name>_api.tern, the module a Tern program imports to call it. A program
        with main and 'export f, g @ address' writes <name>_api.tern for its plug-ins.

          --json           print the result as JSON: ok, the files written, the report's
                           lines, and diagnostics (file, line, column, severity, message,
                           suggestion)
          --max-errors <n> report the first n errors; without it, every error found is reported
          --no-warnings    do not report warnings
          --warnings-as-errors
                           report each warning as an error, which stops the build

        Errors are listed by place (the main file first, then modules in the order first
        imported; by line and column), then their number. Errors of the later stages
        (memory allocation, code generation, layout) come only when there are no others.
        Warnings are for code that does nothing: an unused local, declaration or import,
        and a statement that is never reached. A name starting with '_' is never warned
        as unused. Warnings come only when the checks found no error, listed by place with
        the errors; they do not stop the build.

        tern run <file.tern> compiles the program and runs it in the built-in emulator until
        main returns, then prints the cycles taken and the registers. Memory is plain RAM (no
        VIC-II, SID or CIA; no ANTIC, GTIA, POKEY or PIA); a few ROM routines have stand-ins,
        and other ROM calls stop the run. On the C64: CHROUT, GETIN and PLOT; SETNAM, SETLFS
        and LOAD load the program's own overlay files ("LEVEL1" is level1.prg). On the Atari:
        CIOV for put bytes on IOCB 0 (the screen editor), and SETVBV. On the Apple II:
        COUT, HOME, the startup code's ROM calls, and the MLI's CLOSE and QUIT.
        The emulator reports an array index outside its array and an 'indexed match' value
        outside its span, with the source line (a match value outside stops the run). Exit
        code: 0 when main returned, 2 when it did not, 3 when it did but an index was outside.
          --cycles <n>    stop after n cycles (default 20000000)
          --irq <n>        raise an IRQ every n cycles, through the vector at $FFFE
          --set <a>=<v>    write the byte v at address a before the run, e.g. a joystick
                           value: --set $DC00=$7B; may be given more than once
          --dump <what>    show memory after the run: a variable (score, main.count), a
                           variable with a byte count (table:4), $C000-$C0FF or $C000:16;
                           may be given more than once
          --screen         show the text screen ($0400 on the C64 and the Apple II, SAVMSC
                           on the Atari)
          --profile        show the cycles of each function and each source line
          --max-errors <n>, --no-warnings, --warnings-as-errors  as for tern <file.tern>
          --json           print the result as JSON

        tern bench compiles and runs each variant (as tern run does) and compares them:
        program bytes, the measured function's bytes, its calls and cycles (with what it
        calls), and the memory chosen for its variables and the program's globals. A variant
        whose run finds an array index outside its array fails, and the errors are printed.
        Warnings are not reported, since compared snippets often leave values unused.
          <file.tern>      a variant: a whole program
          --code <stmts>   a variant: statements run as the body of main (';' or newlines
                           between statements); may be given more than once
          --decl <decls>   declarations before main for every --code variant (globals,
                           imports, functions); may be given more than once
          --fn <name>      measure this function instead of main
          --dump <what>    show a value after each run, to check the variants agree (as for
                           tern run)
          --cycles <n>, --irq <n>, --set <a>=<v>, --max-errors <n>, --json  as for tern run
        """;

    /// <summary><c>--no-warnings</c> and <c>--warnings-as-errors</c> (spec 13); giving both is an error.</summary>
    private sealed class WarningSwitches
    {
        private bool _off, _asErrors;

        public WarningMode Mode => _off ? WarningMode.Off : _asErrors ? WarningMode.AsErrors : WarningMode.Report;

        public string? Problem => _off && _asErrors ? "--no-warnings and --warnings-as-errors cannot be given together" : null;

        public bool Parse(string argument)
        {
            if (argument == "--no-warnings") _off = true;
            else if (argument == "--warnings-as-errors") _asErrors = true;
            else return false;
            return true;
        }
    }

    /// <summary>The value of <c>--target</c> (spec 12.2), which every command must give.</summary>
    private sealed class TargetSwitch
    {
        private string? _name;
        private bool _twice;

        public bool Parse(string[] arguments, ref int i)
        {
            if (arguments[i] != "--target" || i + 1 >= arguments.Length) return false;
            _twice |= _name is not null;
            _name = arguments[++i];
            return true;
        }

        /// <summary>The platform, or null with <paramref name="problem"/> saying what is wrong.</summary>
        public Platform? Platform(out string problem)
        {
            problem = _twice ? "--target is given more than once: give one target"
                : _name is null ? $"No target given: write {Compiler.Platform.Choices}"
                : $"Unknown target '{_name}': write {Compiler.Platform.Choices}";
            return _name is null || _twice ? null : Compiler.Platform.ByName(_name);
        }
    }

    /// <summary>Runs one <c>tern</c> command; returns the process exit code.</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length < 1 || args[0] is "-h" or "--help")
        {
            output.WriteLine(Usage);
            return args.Length < 1 ? 1 : 0;
        }
        return args[0] switch
        {
            "run" => RunCommand(args[1..]),
            "bench" => BenchCommand(args[1..]),
            _ => CompileCommand(args),
        };

        // ---------- tern <file.tern> ----------

        int CompileCommand(string[] arguments)
        {
            bool json = false;
            int maxErrors = 0;
            var warnings = new WarningSwitches();
            var target = new TargetSwitch();
            string? input = null, outputPath = null;
            for (int i = 0; i < arguments.Length; i++)
            {
                if (arguments[i] == "--json") json = true;
                else if (target.Parse(arguments, ref i)) { }
                else if (warnings.Parse(arguments[i])) { }
                else if (arguments[i] == "-o" && i + 1 < arguments.Length) outputPath = arguments[++i];
                else if (arguments[i] == "--max-errors" && i + 1 < arguments.Length && int.TryParse(arguments[i + 1], out maxErrors) && maxErrors > 0) i++;
                else if (input is null && !arguments[i].StartsWith('-')) input = arguments[i];
                else return Usage1($"Unknown argument: {arguments[i]}");
            }
            if (input is null) return Usage1("No source file given");
            if (warnings.Problem is { } both) return Usage1(both);
            if (target.Platform(out var noTarget) is not { } platform) return Usage1(noTarget);
            var result = CompileFile(input, platform, json, maxErrors, warnings.Mode);
            if (result is null) return 1;
            outputPath ??= Path.ChangeExtension(input, result.HaltAddress < 0 ? platform.BinaryExtension : platform.FileExtension);
            var files = new List<string>();
            void Write(string path, Action<string> write)
            {
                write(path);
                files.Add(path);
            }
            Write(outputPath, p => File.WriteAllBytes(p, result.File));
            Write(Path.ChangeExtension(outputPath, ".lst"), p => File.WriteAllText(p, result.Listing));
            Write(Path.ChangeExtension(outputPath, platform.Labels.Extension), p => File.WriteAllText(p, result.Labels));
            Write(Path.ChangeExtension(outputPath, ".lines"), p => File.WriteAllText(p, LineTable.Format(LineTable.Rows(result, Path.GetFileName(input)))));
            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
            foreach (var overlay in result.Overlays)
                Write(Path.Combine(directory, overlay.Name + platform.BinaryExtension), p => File.WriteAllBytes(p, overlay.File));
            if (result.ApiModule is not null)
                Write(Path.Combine(directory, result.ApiModuleName + ".tern"), p => File.WriteAllText(p, result.ApiModule));

            if (json)
            {
                output.WriteLine(Diagnostic.ToJson(true, files, result.Report, Warnings(result, input)));
                return 0;
            }
            foreach (var f in files.Skip(4)) output.WriteLine($"Wrote {f}");
            output.WriteLine($"Wrote {outputPath}");
            output.Write(result.Report);
            return 0;
        }

        // ---------- tern run ----------

        int RunCommand(string[] arguments)
        {
            bool json = false, screen = false, profile = false;
            int maxErrors = 0;
            var warnings = new WarningSwitches();
            var target = new TargetSwitch();
            string? input = null;
            long cycles = new RunOptions().MaxCycles, irq = 0;
            var dumps = new List<string>();
            var sets = new List<MemorySet>();
            for (int i = 0; i < arguments.Length; i++)
            {
                string a = arguments[i];
                bool hasValue = i + 1 < arguments.Length;
                if (a == "--json") json = true;
                else if (warnings.Parse(a)) { }
                else if (target.Parse(arguments, ref i)) { }
                else if (a == "--set" && hasValue)
                {
                    try { sets.Add(MemorySet.Parse(arguments[++i])); }
                    catch (FormatException e) { return Usage1("--set " + e.Message); }
                }
                else if (a == "--screen") screen = true;
                else if (a == "--profile") profile = true;
                else if (a == "--dump" && hasValue) dumps.Add(arguments[++i]);
                else if (a == "--cycles" && hasValue && long.TryParse(arguments[i + 1], out cycles) && cycles > 0) i++;
                else if (a == "--irq" && hasValue && long.TryParse(arguments[i + 1], out irq) && irq > 0) i++;
                else if (a == "--max-errors" && hasValue && int.TryParse(arguments[i + 1], out maxErrors) && maxErrors > 0) i++;
                else if (input is null && !a.StartsWith('-')) input = a;
                else return Usage1($"Unknown or incomplete argument: {a}");
            }
            if (input is null) return Usage1("No source file given");
            if (warnings.Problem is { } both) return Usage1(both);
            if (target.Platform(out var noTarget) is not { } platform) return Usage1(noTarget);

            var program = CompileFile(input, platform, json, maxErrors, warnings.Mode);
            if (program is null) return 1;
            List<MemoryDump> parsed;
            try { parsed = dumps.Select(d => MemoryDump.Parse(d, program)).ToList(); }
            catch (FormatException e) { return Fail(json, new Diagnostic(input, 0, 0, "error", "--dump " + e.Message, null), "--dump " + e.Message); }
            RunResult run;
            try { run = Runner.Run(program, new RunOptions { MaxCycles = cycles, IrqEvery = irq, MainFile = Path.GetFileName(input), Sets = sets }); }
            catch (InvalidOperationException e) { return Fail(json, new Diagnostic(input, 0, 0, "error", e.Message, null), e.Message); }
            var lines = profile ? LineTable.Rows(program, Path.GetFileName(input), run.Profile) : null;
            output.Write(json ? RunReport.Json(run, parsed, screen, lines, Warnings(program, input)) + Environment.NewLine : RunReport.Text(run, parsed, screen, lines));
            return !run.Returned ? 2 : run.IndexErrors.Count > 0 ? 3 : 0;
        }

        // ---------- tern bench ----------

        int BenchCommand(string[] arguments)
        {
            bool json = false;
            int maxErrors = 0;
            string? function = null;
            long cycles = new RunOptions().MaxCycles, irq = 0;
            var declarations = new List<string>();
            var dumps = new List<string>();
            var variants = new List<(string? Path, string? Code)>();
            var sets = new List<MemorySet>();
            var target = new TargetSwitch();
            for (int i = 0; i < arguments.Length; i++)
            {
                string a = arguments[i];
                bool hasValue = i + 1 < arguments.Length;
                if (a == "--json") json = true;
                else if (target.Parse(arguments, ref i)) { }
                else if (a == "--set" && hasValue)
                {
                    try { sets.Add(MemorySet.Parse(arguments[++i])); }
                    catch (FormatException e) { return Usage1("--set " + e.Message); }
                }
                else if (a == "--code" && hasValue) variants.Add((null, arguments[++i]));
                else if (a == "--decl" && hasValue) declarations.Add(arguments[++i]);
                else if (a == "--fn" && hasValue) function = arguments[++i];
                else if (a == "--dump" && hasValue) dumps.Add(arguments[++i]);
                else if (a == "--cycles" && hasValue && long.TryParse(arguments[i + 1], out cycles) && cycles > 0) i++;
                else if (a == "--irq" && hasValue && long.TryParse(arguments[i + 1], out irq) && irq > 0) i++;
                else if (a == "--max-errors" && hasValue && int.TryParse(arguments[i + 1], out maxErrors) && maxErrors > 0) i++;
                else if (!a.StartsWith('-')) variants.Add((a, null));
                else return Usage1($"Unknown or incomplete argument: {a}");
            }
            if (variants.Count == 0) return Usage1("No variant given: name .tern files or give --code");
            if (target.Platform(out var noTarget) is not { } platform) return Usage1(noTarget);

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
                    catch (IOException e) { return Fail(json, new Diagnostic(path!, 0, 0, "error", $"Cannot read {path}: {e.Message}", null), $"Cannot read {path}: {e.Message}"); }
                }
                results.Add(Bench.Measure(variant, function, dumps, new RunOptions { MaxCycles = cycles, IrqEvery = irq, Sets = sets }, maxErrors > 0 ? maxErrors : null, platform));
            }
            output.Write(json ? Bench.Json(results) + Environment.NewLine : Bench.Text(results));
            return results.All(r => r.Error is null && r.Returned && r.IndexErrors.Count == 0) ? 0 : 1;
        }

        // ---------- shared ----------

        CompileResult? CompileFile(string input, Platform platform, bool json, int maxErrors, WarningMode warnings)
        {
            string source;
            try
            {
                source = File.ReadAllText(input);
            }
            catch (IOException e)
            {
                string text = $"Cannot read {input}: {e.Message}";
                Fail(json, new Diagnostic(input, 0, 0, "error", text, null), text);
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
                if (json) output.WriteLine(Diagnostic.ToJson(false, [], null, e.WithWarnings.Select(x => Diagnostic.From(x, input)).ToList()));
                else Print(e.WithWarnings, e.MoreNotReported);
                return null;
            }

            void Print(IReadOnlyList<CompileError> list, bool more)
            {
                foreach (var x in list)
                {
                    var d = Diagnostic.From(x, input);
                    error.WriteLine($"{d.File}:{d.Line}:{d.Column}: {d.Severity}: {x.Description}");
                }
                error.WriteLine(Diagnostic.Count(list.Count(x => !x.IsWarning), more, maxErrors, list.Count(x => x.IsWarning)));
            }
        }

        static List<Diagnostic> Warnings(CompileResult result, string input) => result.Warnings.Select(w => Diagnostic.From(w, input)).ToList();

        int Fail(bool json, Diagnostic d, string text)
        {
            if (json) output.WriteLine(Diagnostic.ToJson(false, [], null, [d]));
            else error.WriteLine(d.Line == 0 ? text : $"{d.File}:{d.Line}:{d.Column}: error: {text}");
            return 1;
        }

        int Usage1(string problem)
        {
            error.WriteLine($"{problem}. Run 'tern --help' for the options.");
            return 1;
        }
    }
}
