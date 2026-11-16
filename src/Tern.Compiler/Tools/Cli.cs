using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tools;

/// <summary>
/// The <c>tern</c> command line: argument parsing and printing for compiling, <c>tern run</c> and
/// <c>tern bench</c>. In the compiler library so that tests can run the exact commands the skill shows.
/// Paths are relative to the current directory.
/// </summary>
public static class Cli
{
    public static readonly string Usage = $"""
        Usage:
          tern --target <machine> [--json] [--max-errors <n>] [--no-warnings | --warnings-as-errors] <file.tern> [-o <output>]
          tern run --target <machine> <file.tern> [options]
          tern bench --target <machine> [options] <file.tern>... | --code <statements>...

        Every command names the machine it builds for: --target c64 (Commodore 64),
        --target atari (Atari 8-bit with 48 KB or more), --target apple2 (Apple II with
        64 KB under ProDOS 8) or --target nes (NES; the memory block names the board).

        tern --target <machine> <file.tern> compiles a Tern program. Writes:
          <output>.prg   the program
          <output>.lst   listing with addresses, bytes and cycles
          <output>.vs    VICE label file (load in the monitor with: ll "<output>.vs")
          <output>.lines bytes, cycles and addresses of each source line
        On the Atari the program is <output>.xex and the labels <output>.lbl (Altirra loads
        them with the program); on the Apple II the program is <output>.system, a ProDOS
        system file, and the labels <output>.sym (in AppleWin's debugger: symuser load
        "<path>", the path in quotes and relative to AppleWin's folder); on the NES the
        program is <output>.nes, an iNES cartridge image, and the labels <output>.mlb (Mesen
        loads them with the image).

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

        {RunModelHelp()}
        The emulator reports an array index outside its array and an 'indexed match' value
        outside its span, with the source line (a match value outside stops the run). Exit
        code: 0 when main returned, 2 when it did not, 3 when it did but an index was outside,
        1 when the program did not compile. Every command exits with 4 on an internal error.
          --cycles <n>    stop after n cycles (default 20000000)
          --irq <n>        raise an IRQ every n cycles, through the vector at $FFFE
          --set <a>=<v>    write the byte v at address a before the run, e.g. a joystick
                           value: --set $DC00=$7B; may be given more than once
          --dump <what>    show memory after the run: a variable (score, main.count), a
                           variable with a byte count (table:4), $C000-$C0FF or $C000:16;
                           may be given more than once
          --screen         show the text screen ($0400 on the C64 and the Apple II, SAVMSC
                           on the Atari; on the NES nametable 0, each tile as the character
                           the program's default encoding gives it)
          --profile        show the cycles of each function and each source line
          --max-errors <n>, --no-warnings, --warnings-as-errors  as for tern <file.tern>
          --json           print the result as JSON

        tern bench compiles and runs each variant (as tern run does) and compares them:
        program bytes, the measured function's bytes, its calls and cycles (with what it
        calls), and the memory chosen for its variables and the program's globals. A variant
        whose run finds an array index outside its array fails, and the errors are printed.
        Warnings are not reported, since compared snippets often leave values unused. Exit
        code: 0 when every variant compiled, returned and passed the index check, and each
        --dump named a variable or memory; 1 otherwise.
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
        bool jsonOut = args.Contains("--json");
        try
        {
            return args[0] switch
            {
                "run" => RunCommand(args[1..]),
                "bench" => BenchCommand(args[1..]),
                _ => CompileCommand(args),
            };
        }
        catch (Exception e)
        {
            // A bug in the compiler or the emulator: a message and a fixed exit code, never a stack trace (review code-quality-tools 2).
            string text = $"Internal error ({e.GetType().Name}): {e.Message}. Report it with the program that caused it";
            if (jsonOut) output.WriteLine(Diagnostic.ToJson(false, [], null, [new Diagnostic("", 0, 0, "error", text, null)]));
            else error.WriteLine(text);
            return InternalError;
        }

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
                else if (arguments[i] == "--max-errors")
                {
                    if (Count(arguments, ref i, int.MaxValue, out long n) is { } bad) return Usage1(bad);
                    maxErrors = (int)n;
                }
                else if (input is null && !arguments[i].StartsWith('-')) input = arguments[i];
                else return Usage1(Unknown(arguments[i]));
            }
            if (string.IsNullOrWhiteSpace(input)) return Usage1("No source file given");
            if (warnings.Problem is { } both) return Usage1(both);
            if (target.Platform(out var noTarget) is not { } platform) return Usage1(noTarget);
            if (outputPath is not null && OutputProblem(outputPath) is { } badOutput) return Fail(json, new Diagnostic(input, 0, 0, "error", badOutput, null), badOutput);
            var result = CompileFile(input, platform, json, maxErrors, warnings.Mode);
            if (result is null) return 1;
            outputPath ??= Path.ChangeExtension(input, result.HaltAddress < 0 ? platform.BinaryExtension : platform.FileExtension);

            // Every path first, so that no output overwrites the source or another output (review code-quality-tools 3).
            var writes = new List<(string Path, Action<string> Write)>
            {
                (outputPath, p => File.WriteAllBytes(p, result.File)),
                (Path.ChangeExtension(outputPath, ".lst"), p => File.WriteAllText(p, result.Listing)),
                (Path.ChangeExtension(outputPath, platform.Labels.Extension), p => File.WriteAllText(p, result.Labels)),
                (Path.ChangeExtension(outputPath, ".lines"), p => File.WriteAllText(p, LineTable.Format(LineTable.Rows(result, Path.GetFileName(input))))),
            };
            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
            foreach (var overlay in result.Overlays)
                writes.Add((Path.Combine(directory, overlay.Name + platform.BinaryExtension), p => File.WriteAllBytes(p, overlay.File)));
            if (result.ApiModule is not null)
                writes.Add((Path.Combine(directory, result.ApiModuleName + ".tern"), p => File.WriteAllText(p, result.ApiModule)));
            if (Clash(result.SourceFiles.Append(input), writes.Select(w => w.Path).ToList()) is { } clash) return Fail(json, new Diagnostic(input, 0, 0, "error", clash, null), clash);
            var files = new List<string>();
            foreach (var (path, write) in writes)
            {
                try { write(path); }
                catch (Exception e) when (FileProblem(e) is { } why)
                {
                    string text = $"Cannot write {path}: {why}";
                    return Fail(json, new Diagnostic(input, 0, 0, "error", text, null), text);
                }
                files.Add(path);
            }

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
                else if (RunCount(arguments, ref i, ref cycles, ref irq, ref maxErrors, out var bad)) { if (bad is not null) return Usage1(bad); }
                else if (input is null && !a.StartsWith('-')) input = a;
                else return Usage1(Unknown(a));
            }
            if (string.IsNullOrWhiteSpace(input)) return Usage1("No source file given");
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
                else if (RunCount(arguments, ref i, ref cycles, ref irq, ref maxErrors, out var bad)) { if (bad is not null) return Usage1(bad); }
                else if (!a.StartsWith('-')) variants.Add((a, null));
                else return Usage1(Unknown(a));
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
                    catch (Exception e) when (FileProblem(e) is { } why)
                    {
                        string text = ReadProblem(path!, why);
                        return Fail(json, new Diagnostic(path!, 0, 0, "error", text, null), text);
                    }
                }
                results.Add(Bench.Measure(variant, function, dumps, new RunOptions { MaxCycles = cycles, IrqEvery = irq, Sets = sets }, maxErrors > 0 ? maxErrors : null, platform));
            }
            output.Write(json ? Bench.Json(results) + Environment.NewLine : Bench.Text(results));
            return results.Any(r => r.Failed) ? 1 : 0;
        }

        // ---------- shared ----------

        CompileResult? CompileFile(string input, Platform platform, bool json, int maxErrors, WarningMode warnings)
        {
            string source;
            try
            {
                source = File.ReadAllText(input);
            }
            catch (Exception e) when (FileProblem(e) is { } why)
            {
                string text = ReadProblem(input, why);
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
            string text = $"{problem}. Run 'tern --help' for the options";
            // With --json, a command-line problem is JSON too, with no file (review code-quality-tools 13).
            if (jsonOut) output.WriteLine(Diagnostic.ToJson(false, [], null, [new Diagnostic("", 0, 0, "error", text, null)]));
            else error.WriteLine(text + ".");
            return 1;
        }
    }

    /// <summary>
    /// The help's paragraph on what <c>tern run</c> models, with each machine's stand-ins from its own list
    /// (<see cref="Machine.StandIns"/>), so the help cannot fall behind them (review code-quality-tools 14).
    /// </summary>
    private static string RunModelHelp()
    {
        string StandIns(Platform p) => Machine.For(p).StandIns;
        string text =
            "tern run <file.tern> compiles the program and runs it in the built-in emulator until main returns, then prints the cycles taken and the registers. " +
            "Memory is plain RAM (no VIC-II, SID or CIA; no ANTIC, GTIA, POKEY or PIA); a few ROM routines have stand-ins, which take no cycles, and other ROM calls stop the run. " +
            "Memory the program does not load starts with fixed pseudo-random bytes and A, X and Y are not zero, as on a machine after its ROM ran, so a read of memory the program never wrote shows; the text screen starts blank. " +
            $"On the C64: {StandIns(Platform.C64)}; LOAD loads the program's own overlay files (\"LEVEL1\" is level1.prg). " +
            $"On the Atari: {StandIns(Platform.Atari)}. On the Apple II: {StandIns(Platform.Apple2)}. " +
            "On the NES: PPU memory and sprite memory (not pixels); the vblank flag, set at the start of vblank and cleared by a read of $2002 or at the end of vblank; " +
            "an NMI every frame when bit 7 of $2000 is set, and at once when it is set during vblank; the UxROM bank latch; the controllers read as no buttons. " +
            "A brk stops the run, except on the NES when the program sets the IRQ vector, which then runs its handler; an undocumented opcode stops the run too.";
        var lines = new List<string>();
        var line = new System.Text.StringBuilder();
        foreach (var word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > 86) { lines.Add(line.ToString()); line.Clear(); }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        lines.Add(line.ToString());
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The exit code when the compiler or the emulator fails with an exception it did not expect.</summary>
    public const int InternalError = 4;

    /// <summary>Options that take a value after them.</summary>
    private static readonly HashSet<string> ValueOptions = ["-o", "--target", "--set", "--dump", "--code", "--decl", "--fn", "--cycles", "--irq", "--max-errors"];

    private static string Unknown(string argument) =>
        ValueOptions.Contains(argument) ? $"{argument} needs a value after it" : $"Unknown argument: {argument}";

    /// <summary>
    /// The whole number from 1 to <paramref name="max"/> after the option at <paramref name="i"/>, or what is
    /// wrong with it: "--cycles needs a whole number above 0, found 'abc'" (review code-quality-tools 13).
    /// </summary>
    private static string? Count(string[] arguments, ref int i, long max, out long value)
    {
        string option = arguments[i];
        value = 0;
        if (i + 1 >= arguments.Length) return $"{option} needs a whole number above 0 after it";
        string text = arguments[++i];
        if (!long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value) || value < 1 || value > max)
            return max == long.MaxValue ? $"{option} needs a whole number above 0, found '{text}'" : $"{option} needs a whole number from 1 to {max}, found '{text}'";
        return null;
    }

    /// <summary><c>--cycles</c>, <c>--irq</c> and <c>--max-errors</c> of <c>tern run</c> and <c>tern bench</c>: true when the argument is one, with any problem in <paramref name="problem"/>.</summary>
    private static bool RunCount(string[] arguments, ref int i, ref long cycles, ref long irq, ref int maxErrors, out string? problem)
    {
        problem = null;
        long n;
        switch (arguments[i])
        {
            case "--cycles": problem = Count(arguments, ref i, long.MaxValue, out n); if (problem is null) cycles = n; return true;
            case "--irq": problem = Count(arguments, ref i, long.MaxValue, out n); if (problem is null) irq = n; return true;
            case "--max-errors": problem = Count(arguments, ref i, int.MaxValue, out n); if (problem is null) maxErrors = (int)n; return true;
            default: return false;
        }
    }

    /// <summary>Why a path cannot be read or written, for the exceptions a path can give; null for any other exception.</summary>
    private static string? FileProblem(Exception e) => e switch
    {
        DirectoryNotFoundException => "the folder does not exist",
        FileNotFoundException => "the file does not exist",
        UnauthorizedAccessException => "access is denied (it may be a folder or read-only)",
        IOException or ArgumentException or NotSupportedException => e.Message,
        _ => null,
    };

    private static string ReadProblem(string path, string why) =>
        Directory.Exists(path) ? $"Cannot read {path}: it is a folder. Give a .tern file" : $"Cannot read {path}: {why}. Check the path";

    /// <summary>What is wrong with <c>-o</c> before compiling: a folder, or a path in a folder that does not exist.</summary>
    private static string? OutputProblem(string outputPath)
    {
        if (Directory.Exists(outputPath))
            return $"-o {outputPath} is a folder. Give a file name in it, as in -o {Path.Combine(outputPath, "game")}";
        string? folder = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        return folder is not null && !Directory.Exists(folder)
            ? $"Cannot write {outputPath}: the folder {folder} does not exist. Create it, or give -o a path in a folder that exists"
            : null;
    }

    /// <summary>An output that would overwrite a source file or another output, or null (review code-quality-tools 3).</summary>
    private static string? Clash(IEnumerable<string> sources, IReadOnlyList<string> outputs)
    {
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        // Every file the compile read: the main file and the modules it imports (review R3, finding 1).
        var source = new Dictionary<string, string>(comparer);
        foreach (var s in sources) source.TryAdd(Path.GetFullPath(s), s);
        var seen = new Dictionary<string, string>(comparer);
        foreach (var path in outputs)
        {
            string full = Path.GetFullPath(path);
            if (source.TryGetValue(full, out var file))
                return $"{path} would overwrite the source file {file}. Give -o another name";
            if (!seen.TryAdd(full, path))
                return $"Two outputs would be written to {path}. Give -o another name";
        }
        return null;
    }
}
