using System.Globalization;

namespace Tern.Compiler.Tools;

public static partial class Cli
{
    /// <summary>The three commands, in the order the help describes them.</summary>
    private enum Command { Compile, Run, Bench }

    private static string NameOf(Command command) => command switch
    {
        Command.Compile => "tern <file.tern>",
        Command.Run => "tern run",
        _ => "tern bench",
    };

    /// <summary>What an option takes after it.</summary>
    private enum Takes { Nothing, Text, Count }

    /// <summary>
    /// One command-line option: the reader of every command and the help are made from <see cref="Options"/>,
    /// so an option is written once (review code-quality-tools 5). A <see cref="Takes.Count"/> is a whole
    /// number from 1 to <see cref="Max"/>; <see cref="Check"/> says what is wrong with a text value, or null.
    /// </summary>
    private sealed record Option(string Name, string Value, Takes Takes, Command[] Commands, string Help)
    {
        public bool Many { get; init; }
        public long Max { get; init; } = long.MaxValue;
        public Func<string, string?>? Check { get; init; }

        /// <summary>The option as the help writes it: "--cycles &lt;n&gt;".</summary>
        public string Synopsis => Value == "" ? Name : $"{Name} {Value}";
    }

    private static readonly Command[] EveryCommand = [Command.Compile, Command.Run, Command.Bench];
    private static readonly Command[] Running = [Command.Run, Command.Bench];

    /// <summary>Every option, in the order the help lists them; each is described under the first command that takes it.</summary>
    private static readonly Option[] Options =
    [
        new("--target", "<machine>", Takes.Text, EveryCommand,
            "the machine to build for, which every command needs: c64 (Commodore 64), atari (Atari 8-bit " +
            "with 48 KB or more), apple2 (Apple II with 64 KB under ProDOS 8) or nes (NES; the memory block names the board)"),
        new("-o", "<output>", Takes.Text, [Command.Compile],
            "the program's file; the other files take its name with their own extension. Without it, the source's name"),
        new("--json", "", Takes.Nothing, EveryCommand,
            "print the result as JSON: ok, the files written, the report's lines, and diagnostics (file, line, " +
            "column, severity, message, suggestion)"),
        new("--max-errors", "<n>", Takes.Count, EveryCommand, "report the first n errors; without it, every error found is reported")
            { Max = int.MaxValue },
        new("--no-warnings", "", Takes.Nothing, [Command.Compile, Command.Run], "do not report warnings"),
        new("--warnings-as-errors", "", Takes.Nothing, [Command.Compile, Command.Run],
            "report each warning as an error, which stops the build"),
        new("--cycles", "<n>", Takes.Count, Running, $"stop after n cycles (default {new RunOptions().MaxCycles})"),
        new("--irq", "<n>", Takes.Count, Running, "raise an IRQ every n cycles, through the vector at $FFFE"),
        new("--set", "<a>=<v>", Takes.Text, Running,
            "write the byte v at address a before the run, e.g. a joystick value: --set $DC00=$7B")
            { Many = true, Check = SetProblem },
        new("--dump", "<what>", Takes.Text, Running,
            "show memory after the run: a variable (score, main.count), a variable with a byte count (table:4), " +
            "$C000-$C0FF or $C000:16; bench shows it after each run, to check the variants agree")
            { Many = true },
        new("--screen", "", Takes.Nothing, [Command.Run],
            "show the text screen ($0400 on the C64 and the Apple II, SAVMSC on the Atari; on the NES nametable 0, " +
            "each tile as the character the program's default encoding gives it)"),
        new("--profile", "", Takes.Nothing, [Command.Run], "show the cycles of each function and each source line"),
        new("--code", "<stmts>", Takes.Text, [Command.Bench],
            "a variant: statements run as the body of main (';' or newlines between statements)") { Many = true },
        new("--decl", "<decls>", Takes.Text, [Command.Bench],
            "declarations before main for every --code variant (globals, imports, functions)") { Many = true },
        new("--fn", "<name>", Takes.Text, [Command.Bench], "measure this function instead of main"),
    ];

    private static Option OptionNamed(string name) => Options.First(o => o.Name == name);

    private static string? SetProblem(string text)
    {
        try { MemorySet.Parse(text); return null; }
        catch (FormatException e) { return "--set " + e.Message; }
    }

    /// <summary>A command's arguments after reading: each option with its value, and the files, in the order given.</summary>
    private sealed class Arguments
    {
        public readonly List<(Option? Option, string Text)> Items = [];

        public bool Has(string name) => Items.Any(x => x.Option?.Name == name);

        public IEnumerable<string> All(string name) => Items.Where(x => x.Option?.Name == name).Select(x => x.Text);

        /// <summary>The value given last, or null.</summary>
        public string? Value(string name) => All(name).LastOrDefault();

        public long Count(string name, long otherwise) => Value(name) is { } text ? long.Parse(text, CultureInfo.InvariantCulture) : otherwise;

        public IEnumerable<string> Files => Items.Where(x => x.Option is null).Select(x => x.Text);

        public WarningMode Warnings =>
            Has("--no-warnings") ? WarningMode.Off : Has("--warnings-as-errors") ? WarningMode.AsErrors : WarningMode.Report;

        /// <summary>What is wrong with the warning options, or null (spec 13).</summary>
        public string? WarningProblem =>
            Has("--no-warnings") && Has("--warnings-as-errors") ? "--no-warnings and --warnings-as-errors cannot be given together" : null;

        /// <summary>The platform of <c>--target</c> (spec 12.2), or null with <paramref name="problem"/> saying what is wrong.</summary>
        public Platform? Platform(out string problem)
        {
            var names = All("--target").ToList();
            problem = names.Count > 1 ? "--target is given more than once: give one target"
                : names.Count == 0 ? $"No target given: write {Compiler.Platform.Choices}"
                : $"Unknown target '{names[0]}': write {Compiler.Platform.Choices}";
            return names.Count == 1 ? Compiler.Platform.ByName(names[0]) : null;
        }
    }

    /// <summary>
    /// Reads <paramref name="arguments"/> for <paramref name="command"/> by <see cref="Options"/>, or gives the first
    /// problem: an unknown option, an option of another command, a missing or bad value (review code-quality-tools 13).
    /// </summary>
    private static Arguments? Read(Command command, string[] arguments, out string? problem)
    {
        var read = new Arguments();
        problem = null;
        for (int i = 0; i < arguments.Length; i++)
        {
            string a = arguments[i];
            var option = Options.FirstOrDefault(o => o.Name == a);
            if (option is null)
            {
                // Bench compares any number of files; the other commands take one.
                if (a.StartsWith('-') || (command != Command.Bench && read.Files.Any())) problem = $"Unknown argument: {a}";
                else read.Items.Add((null, a));
            }
            else if (!option.Commands.Contains(command))
                problem = $"{a} is an option of {string.Join(" and ", option.Commands.Select(NameOf))}, not of {NameOf(command)}";
            else if (option.Takes == Takes.Nothing) read.Items.Add((option, ""));
            else if (i + 1 >= arguments.Length)
                problem = option.Takes == Takes.Count ? $"{a} needs a whole number above 0 after it" : $"{a} needs a value after it";
            else
            {
                string text = arguments[++i];
                problem = option.Takes == Takes.Count ? CountProblem(option, text) : option.Check?.Invoke(text);
                read.Items.Add((option, text));
            }
            if (problem is not null) return null;
        }
        return read;
    }

    /// <summary>What is wrong with a count's value: "--cycles needs a whole number above 0, found 'abc'", or null.</summary>
    private static string? CountProblem(Option option, string text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value >= 1 && value <= option.Max ? null
        : option.Max == long.MaxValue ? $"{option.Name} needs a whole number above 0, found '{text}'"
        : $"{option.Name} needs a whole number from 1 to {option.Max}, found '{text}'";
}
