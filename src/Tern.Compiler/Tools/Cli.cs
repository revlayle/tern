namespace Tern.Compiler.Tools;

/// <summary>
/// The <c>tern</c> command line: argument parsing and printing for compiling, <c>tern run</c> and
/// <c>tern bench</c>. In the compiler library so that tests can run the exact commands the skill shows.
/// Paths are relative to the current directory.
/// </summary>
public static partial class Cli
{
    /// <summary>The help, with each command's options from <see cref="Options"/>.</summary>
    public static string Usage => Help();

    /// <summary>Runs one <c>tern</c> command; returns the process exit code.</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length < 1 || args[0] is "-h" or "--help")
        {
            output.WriteLine(Usage);
            return args.Length < 1 ? 1 : 0;
        }
        bool jsonOut = args.Contains("--json");
        var commands = new Commands(output, error, jsonOut);
        try
        {
            return args[0] switch
            {
                "run" => commands.Run(args[1..]),
                "bench" => commands.Bench(args[1..]),
                _ => commands.Compile(args),
            };
        }
        catch (Exception e)
        {
            // A bug in the compiler or the emulator: a message and a fixed exit code, never a stack trace (review code-quality-tools 2).
            string text = $"Internal error ({e.GetType().Name}): {e.Message}. Report it with the program that caused it";
            if (jsonOut) output.WriteLine(Diagnostic.ToJson(false, [], null, [Diagnostic.Error("", text)]));
            else error.WriteLine(text);
            return InternalError;
        }
    }

    private static string Help()
    {
        string target = OptionNamed("--target").Synopsis;
        var text = new System.Text.StringBuilder($"""
            Usage:
              tern {target} [options] <file.tern>
              tern run {target} [options] <file.tern>
              tern bench {target} [options] <file.tern>... | {OptionNamed("--code").Synopsis}...

            tern <file.tern> compiles a Tern program. Writes:
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


            """);
        text.Append(OptionHelp(Command.Compile));
        text.Append("""

            Errors are listed by place (the main file first, then modules in the order first
            imported; by line and column), then their number. Errors of the later stages
            (memory allocation, code generation, layout) come only when there are no others.
            Warnings are for code that does nothing: an unused local, declaration or import,
            and a statement that is never reached. A name starting with '_' is never warned
            as unused. Warnings come only when the checks found no error, listed by place with
            the errors; they do not stop the build.


            """);
        text.AppendLine(RunModelHelp());
        text.Append("""
            The emulator reports an array index outside its array and an 'indexed match' value
            outside its span, with the source line (a match value outside stops the run). Exit
            code: 0 when main returned, 2 when it did not, 3 when it did but an index was outside,
            1 when the program did not compile. Every command exits with 4 on an internal error.

            """);
        text.Append(OptionHelp(Command.Run));
        text.Append("""

            tern bench compiles and runs each variant (as tern run does) and compares them:
            program bytes, the measured function's bytes, its calls and cycles (with what it
            calls), and the memory chosen for its variables and the program's globals. A variant
            whose run finds an array index outside its array fails, and the errors are printed.
            Warnings are not reported, since compared snippets often leave values unused. Exit
            code: 0 when every variant compiled, returned and passed the index check, and each
            --dump named a variable or memory; 1 otherwise.
              <file.tern>      a variant: a whole program

            """);
        text.Append(OptionHelp(Command.Bench));
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// The option lines of one command's help: each option the command is the first to take, then one line
    /// naming the rest by the command that describes them.
    /// </summary>
    private static string OptionHelp(Command command)
    {
        const string indent = "                   ";
        var lines = new List<string>();
        foreach (var option in Options.Where(o => o.Commands[0] == command))
        {
            string help = option.Help + (option.Many ? "; may be given more than once" : "");
            string left = "  " + option.Synopsis;
            if (left.Length >= indent.Length) { lines.Add(left); lines.AddRange(Wrap(help, indent, indent)); }
            else lines.AddRange(Wrap(help, left.PadRight(indent.Length), indent));
        }
        foreach (var earlier in Options.Where(o => o.Commands.Contains(command) && o.Commands[0] != command).GroupBy(o => o.Commands[0]))
            lines.AddRange(Wrap($"{string.Join(", ", earlier.Select(o => o.Synopsis))}: as for {NameOf(earlier.Key)}", "  ", indent));
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>
    /// The words of <paramref name="text"/> in lines of at most 86 characters, the first line starting with
    /// <paramref name="first"/> and the others with <paramref name="rest"/>.
    /// </summary>
    private static List<string> Wrap(string text, string first, string rest)
    {
        var lines = new List<string>();
        var line = new System.Text.StringBuilder(first);
        int start = first.Length;
        foreach (var word in text.Split(' '))
        {
            if (line.Length > start && line.Length + 1 + word.Length > 86)
            {
                lines.Add(line.ToString());
                line.Clear().Append(rest);
                start = rest.Length;
            }
            if (line.Length > start) line.Append(' ');
            line.Append(word);
        }
        lines.Add(line.ToString());
        return lines;
    }

    /// <summary>
    /// The help's paragraph on what <c>tern run</c> models. Each machine gives its own part (<see cref="Machine.Help"/>,
    /// with its stand-ins from its own list, review code-quality-tools 14), listed from <see cref="Platform.All"/>,
    /// so a new machine is in the help without an edit here (review R6, suggestion 3).
    /// </summary>
    private static string RunModelHelp()
    {
        var machines = Platform.All.Where(p => p.NewMachine is not null).Select(p => Machine.For(p)).ToList();
        string text =
            "tern run <file.tern> compiles the program and runs it in the built-in emulator until main returns, then prints the cycles taken and the registers. " +
            $"Memory is plain RAM (no {string.Join("; no ", machines.Select(m => m.UnmodelledChips).OfType<string>())}); a few ROM routines have stand-ins, which take no cycles, and other ROM calls stop the run. " +
            "Memory the program does not load starts with fixed pseudo-random bytes and A, X and Y are not zero, as on a machine after its ROM ran, so a read of memory the program never wrote shows; the text screen starts blank. " +
            $"Input reads idle, nothing pressed: {string.Join("; ", machines.Select(m => m.IdleInput).OfType<string>())}. Give input with --set. " +
            "An undocumented opcode stops the run. A brk does too, unless a machine below says otherwise. " +
            string.Join(" ", machines.Select(m => m.Help));
        return string.Join(Environment.NewLine, Wrap(text, "", ""));
    }

    /// <summary>The exit code when the compiler or the emulator fails with an exception it did not expect.</summary>
    public const int InternalError = 4;

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
