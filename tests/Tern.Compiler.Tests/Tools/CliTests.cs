using System.Text.Json;
using Tern.Compiler.Tools;

namespace Tern.Compiler.Tests;

/// <summary>
/// The command line's handling of paths and option values (review code-quality-tools 2, 3, 8 and 13): every
/// problem is a message and a documented exit code, never a stack trace or a lost file.
/// Spec 13 (compiler design: the tools).
/// </summary>
public sealed class CliTests : TempFolderTest
{

    private const string Program = "var out: u8\nfn main() { out = 1 }\n";

    private string Write(string name, string source = Program)
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllText(path, source);
        return path;
    }

    private static (int Code, string Output, string Error) Tern(params string[] args)
    {
        var (code, output, error) = Command.Tern(args);
        return (code, output, error.Trim());
    }

    private static JsonElement OnlyDiagnostic(string output)
    {
        var json = JsonDocument.Parse(output).RootElement;
        Assert.False(json.GetProperty("ok").GetBoolean());
        return Assert.Single(json.GetProperty("diagnostics").EnumerateArray());
    }

    // ---------- Paths (tools 2) ----------

    // Found by the coverage run of R6 (review code-quality-tests, finding 4): no test reached these two.
    [Fact]
    public void A_read_only_output_cannot_be_written()
    {
        string a = Write("a.tern"), prg = Write("a.prg", "old");
        File.SetAttributes(prg, FileAttributes.ReadOnly);
        var (code, _, error) = Tern("--target", "c64", a);
        File.SetAttributes(prg, FileAttributes.Normal);
        Assert.Equal(1, code);
        Assert.Equal($"Cannot write {prg}: access is denied (it may be a folder or read-only)", error);
        Assert.Equal("old", File.ReadAllText(prg));
    }

    [Fact]
    public void Run_with_a_bad_dump_is_an_error()
    {
        var (code, _, error) = Tern("run", "--target", "c64", Write("a.tern"), "--dump", "nosuch");
        Assert.Equal(1, code);
        Assert.Equal("--dump 'nosuch' is not a variable of the program. Write a local as 'function.name'. Run 'tern --help' for the options.", error);
    }

    [Fact]
    public void An_output_in_a_missing_folder_is_an_error_before_compiling()
    {
        string a = Write("a.tern"), missing = Path.Combine(_dir.FullName, "nodir");
        var (code, _, error) = Tern("--target", "c64", a, "-o", Path.Combine(missing, "a.prg"));
        Assert.Equal(1, code);
        Assert.Equal($"Cannot write {Path.Combine(missing, "a.prg")}: the folder {missing} does not exist. Create it, or give -o a path in a folder that exists", error);

        var (jsonCode, output, _) = Tern("--target", "c64", "--json", a, "-o", Path.Combine(missing, "a.prg"));
        Assert.Equal(1, jsonCode);
        Assert.StartsWith("Cannot write", OnlyDiagnostic(output).GetProperty("message").GetString());
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void An_output_that_is_a_folder_is_an_error()
    {
        string a = Write("a.tern"), folder = _dir.CreateSubdirectory("adir").FullName;
        var (code, _, error) = Tern("--target", "c64", a, "-o", folder);
        Assert.Equal(1, code);
        Assert.Equal($"-o {folder} is a folder. Give a file name in it, as in -o {Path.Combine(folder, "game")}", error);
    }

    [Theory]
    [InlineData]
    [InlineData("run")]
    [InlineData("bench")]
    public void A_source_that_is_a_folder_is_an_error(params string[] command)
    {
        string folder = _dir.CreateSubdirectory("src.tern").FullName;
        var (code, _, error) = Tern([.. command, "--target", "c64", folder]);
        Assert.Equal(1, code);
        Assert.Contains($"Cannot read {folder}: it is a folder. Give a .tern file", error);
    }

    [Fact]
    public void A_missing_source_is_an_error_in_json_too()
    {
        string missing = Path.Combine(_dir.FullName, "none.tern");
        var (code, output, _) = Tern("--target", "c64", "--json", missing);
        Assert.Equal(1, code);
        // The fix is the suggestion, as for a compiler error (R6; before, the whole text was the message).
        var d = OnlyDiagnostic(output);
        Assert.Equal($"Cannot read {missing}: the file does not exist.", d.GetProperty("message").GetString());
        Assert.Equal("Check the path", d.GetProperty("suggestion").GetString());
    }

    [Fact]
    public void An_empty_source_name_is_no_source()
    {
        var (code, _, error) = Tern("--target", "c64", "");
        Assert.Equal(1, code);
        Assert.Equal("No source file given. Name a .tern file. Run 'tern --help' for the options.", error);
    }

    [Fact]
    public void Bench_of_a_binary_without_main_is_a_failed_variant()
    {
        string lib = Write("lib1.tern", "var n: u8\nfn bump() { n += 1 }\nexport bump\n");
        var (code, output, _) = Tern("bench", "--target", "c64", "--fn", "bump", lib);
        Assert.Equal(1, code);
        Assert.Contains("error: A binary without main cannot be run on its own. Measure the function in a program that loads and calls it", output);
    }

    [Fact]
    public void Run_of_a_binary_without_main_is_an_error()
    {
        string lib = Write("lib2.tern", "var n: u8\nfn bump() { n += 1 }\nexport bump\n");
        var (code, _, error) = Tern("run", "--target", "c64", lib);
        Assert.Equal(1, code);
        Assert.Contains("A binary without main cannot be run on its own. Call it from a program that loads it, and run that program", error);
    }

    /// <summary>A run error's fix is its suggestion in JSON (R6; review R7 fixes, suggestion 1: before, there was none).</summary>
    [Fact]
    public void A_run_error_has_its_suggestion_in_json()
    {
        string lib = Write("lib3.tern", "var n: u8\nfn bump() { n += 1 }\nexport bump\n");
        var (code, output, _) = Tern("run", "--target", "c64", "--json", lib);
        Assert.Equal(1, code);
        var d = OnlyDiagnostic(output);
        Assert.Equal("A binary without main cannot be run on its own.", d.GetProperty("message").GetString());
        Assert.Equal("Call it from a program that loads it, and run that program", d.GetProperty("suggestion").GetString());
    }

    /// <summary>Throws on its first write, then works: a bug inside a command that the last catch must report.</summary>
    private sealed class FailsOnce : StringWriter
    {
        private bool _failed;
        public override void Write(char value) { Fail(); base.Write(value); }
        public override void Write(string? value) { Fail(); base.Write(value); }
        public override void WriteLine(string? value) { Fail(); base.WriteLine(value); }
        private void Fail()
        {
            if (_failed) return;
            _failed = true;
            throw new InvalidOperationException("broken writer");
        }
    }

    // Review R3, suggestion 3: an exception nothing expected is a message and exit code 4, as JSON with --json.
    [Fact]
    public void An_internal_error_is_a_message_and_exit_code_4()
    {
        string a = Write("a.tern");
        var (output, error) = (new FailsOnce(), new StringWriter());
        Assert.Equal(Cli.InternalError, Cli.Run(["--target", "c64", a], output, error));
        Assert.Equal("Internal error (InvalidOperationException): broken writer. Report it with the program that caused it", error.ToString().Trim());

        var json = new FailsOnce();
        Assert.Equal(4, Cli.Run(["--target", "c64", "--json", a], json, new StringWriter()));
        Assert.StartsWith("Internal error (InvalidOperationException)", OnlyDiagnostic(json.ToString()).GetProperty("message").GetString());
    }

    // ---------- Outputs (tools 3) ----------

    [Fact]
    public void An_output_never_overwrites_the_source()
    {
        string b = Write("b.tern");
        var (code, _, error) = Tern("--target", "c64", b, "-o", b);
        Assert.Equal(1, code);
        Assert.Equal($"{b} would overwrite the source file {b}. Give -o another name", error);
        Assert.Equal(Program, File.ReadAllText(b));
    }

    // Review R3, finding 1: an imported module is source too.
    [Fact]
    public void An_output_never_overwrites_an_imported_module()
    {
        const string util = "var n: u8\nfn bump() { n += 1 }\n";
        string module = Write("util.tern", util);
        string main = Write("main.tern", "import util\nvar out: u8\nfn main() {\n    util.bump()\n    out = 1\n}\n");
        var (code, _, error) = Tern("--target", "c64", main, "-o", module);
        Assert.Equal(1, code);
        Assert.Equal($"{module} would overwrite the source file {module}. Give -o another name", error);
        Assert.Equal(util, File.ReadAllText(module));
        Assert.Equal(0, Tern("--target", "c64", main, "-o", Path.Combine(_dir.FullName, "util.prg")).Code);
        Assert.Equal(util, File.ReadAllText(module));
    }

    [Theory]
    [InlineData(".lst")]
    [InlineData(".vs")]
    [InlineData(".lines")]
    public void Two_outputs_never_share_a_file(string extension)
    {
        string a = Write("a.tern"), o = Path.Combine(_dir.FullName, "c" + extension);
        var (code, _, error) = Tern("--target", "c64", a, "-o", o);
        Assert.Equal(1, code);
        Assert.Equal($"Two outputs would be written to {o}. Give -o another name", error);
        Assert.False(File.Exists(o));
    }

    [Fact]
    public void An_overlay_file_never_overwrites_the_program()
    {
        // The overlay 'level' is written as level.prg next to the output.
        Write("level.tern", "var n: u8\nfn show() { n = 1 }\n");
        string main = Write("main.tern", "import level @ levels\nmemory {\n    $0801..=$5FFF  program\n    $6000..=$6FFF  overlay levels\n}\nfn main() { level.show() }\n");
        string o = Path.Combine(_dir.FullName, "level.prg");
        var (code, _, error) = Tern("--target", "c64", main, "-o", o);
        Assert.Equal(1, code);
        Assert.Equal($"Two outputs would be written to {o}. Give -o another name", error);
        Assert.Equal(0, Tern("--target", "c64", main).Code);
    }

    // ---------- Option values (tools 13) ----------

    [Theory]
    [InlineData(new[] { "run", "--target", "c64", "SRC", "--cycles", "abc" }, "--cycles needs a whole number above 0, found 'abc'. Give a number of 1 or more, in digits")]
    [InlineData(new[] { "run", "--target", "c64", "SRC", "--cycles", "0" }, "--cycles needs a whole number above 0, found '0'. Give a number of 1 or more, in digits")]
    [InlineData(new[] { "run", "--target", "c64", "SRC", "--irq", "-5" }, "--irq needs a whole number above 0, found '-5'. Give a number of 1 or more, in digits")]
    [InlineData(new[] { "run", "--target", "c64", "SRC", "--cycles" }, "--cycles has no value. Give a whole number above 0 after it")]
    [InlineData(new[] { "--target", "c64", "SRC", "--max-errors", "0" }, "--max-errors needs a whole number from 1 to 2147483647, found '0'. Give a number in that range, in digits")]
    [InlineData(new[] { "bench", "--target", "c64", "SRC", "--max-errors", "x" }, "--max-errors needs a whole number from 1 to 2147483647, found 'x'. Give a number in that range, in digits")]
    [InlineData(new[] { "--target", "c64", "SRC", "-o" }, "-o has no value. Give its value after it")]
    [InlineData(new[] { "run", "--target", "c64", "SRC", "--dump" }, "--dump has no value. Give its value after it")]
    [InlineData(new[] { "SRC", "--target" }, "--target has no value. Give its value after it")]
    [InlineData(new[] { "run", "--target", "c64", "SRC", "--fast" }, "Unknown option --fast. Check the spelling")]
    [InlineData(new[] { "--target", "c64", "SRC", "b.tern" }, "b.tern is a second source file, and tern <file.tern> takes one. Remove it")]
    [InlineData(new[] { "run", "--target", "c64", "SRC", "--set", "$DC00" }, "--set '$DC00' is not an address and a byte. Write them as $DC00=$FF")]
    // Review code-quality-tools 5: an option of another command is named as one, wherever it is in the table.
    [InlineData(new[] { "bench", "--target", "c64", "SRC", "-o", "x.prg" }, "-o is an option of tern <file.tern>, not of tern bench. Remove it")]
    [InlineData(new[] { "--target", "c64", "SRC", "--cycles", "100" }, "--cycles is an option of tern run and tern bench, not of tern <file.tern>. Remove it")]
    [InlineData(new[] { "bench", "--target", "c64", "SRC", "--screen" }, "--screen is an option of tern run, not of tern bench. Remove it")]
    [InlineData(new[] { "run", "--target", "c64", "SRC", "--code", "x = 1" }, "--code is an option of tern bench, not of tern run. Remove it")]
    [InlineData(new[] { "bench", "--target", "c64", "SRC", "--no-warnings" }, "--no-warnings is an option of tern <file.tern> and tern run, not of tern bench. Remove it")]
    public void A_bad_option_value_names_the_option_and_the_value(string[] args, string expected)
    {
        string src = Write("a.tern");
        var (code, _, error) = Tern(args.Select(a => a == "SRC" ? src : a).ToArray());
        Assert.Equal(1, code);
        Assert.Equal(expected + ". Run 'tern --help' for the options.", error);
    }

    /// <summary>
    /// A command-line problem with --json is JSON too, with no file, and its fix is the suggestion (review R6,
    /// suggestion 1): the five lines the review ran, and a missing value.
    /// </summary>
    [Theory]
    [InlineData(new[] { "--json", "--target" }, "--target has no value.", "Give its value after it. Run 'tern --help' for the options")]
    [InlineData(new[] { "--target", "zx", "--json", "SRC" }, "Unknown target 'zx'.", "Use --target c64, --target atari, --target apple2 or --target nes. Run 'tern --help' for the options")]
    [InlineData(new[] { "--target", "c64", "--json", "--max-errors", "abc", "SRC" }, "--max-errors needs a whole number from 1 to 2147483647, found 'abc'.", "Give a number in that range, in digits. Run 'tern --help' for the options")]
    [InlineData(new[] { "run", "--target", "c64", "--set", "zz", "--json", "SRC" }, "--set 'zz' is not an address and a byte.", "Write them as $DC00=$FF. Run 'tern --help' for the options")]
    [InlineData(new[] { "--target", "c64", "--json", "--bogus", "SRC" }, "Unknown option --bogus.", "Check the spelling. Run 'tern --help' for the options")]
    [InlineData(new[] { "run", "--target", "c64", "--dump", "out:0", "--json", "SRC" }, "--dump 'out:0' shows no bytes.", "Give a byte count of 1 or more. Run 'tern --help' for the options")]
    public void A_command_line_problem_with_json_is_json_with_a_suggestion_and_no_file(string[] args, string message, string suggestion)
    {
        string src = Write("ok.tern", "var out: u8\nfn main() { out = 1 }\n");
        var (code, output, error) = Tern(args.Select(a => a == "SRC" ? src : a).ToArray());
        Assert.Equal(1, code);
        Assert.Equal("", error);
        var d = OnlyDiagnostic(output);
        Assert.Equal("", d.GetProperty("file").GetString());
        Assert.Equal(message, d.GetProperty("message").GetString());
        Assert.Equal(suggestion, d.GetProperty("suggestion").GetString());
    }

    // ---------- The help (tools 14) ----------

    [Fact]
    public void The_help_names_every_stand_in_and_their_cost()
    {
        var (code, output, _) = Tern("--help");
        Assert.Equal(0, code);
        string help = string.Join(" ", output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
        foreach (var name in new[] { "CHROUT $FFD2", "LOAD $FFD5", "CIOV $E456", "IOCBs 1-7", "SETVBV $E45C", "COUT $FDED", "GET_PREFIX", "CLOSE", "reading the build's overlay files" })
            Assert.Contains(name, help);
        Assert.Contains("which take no cycles", help);
        Assert.Contains("Input reads idle, nothing pressed: the C64's $DC00 and $DC01 are $FF; the Atari's CH ($2FC) is $FF, STICK0-3 $0F, " +
            "STRIG0-3 1 and CONSOL 7; the Apple II's $C000 and $C061-$C063 are 0. Give input with --set.", help);
        Assert.Contains("An undocumented opcode stops the run. A brk does too, unless a machine below says otherwise.", help);
        Assert.Contains("a brk runs the program's IRQ handler when the program sets the IRQ vector.", help);
        // Each machine's own sentence, from Platform.All (review R6, suggestion 3).
        foreach (var p in Platform.All.Where(p => p.NewMachine is not null))
            Assert.Contains(Machine.For(p).Help, help);
        // --target's and --screen's texts too, from each platform and machine (review R8, finding 4).
        foreach (var p in Platform.All)
            Assert.Contains($"{p.Name} ({p.Description})", help);
        foreach (var p in Platform.All.Where(p => p.NewMachine is not null))
            Assert.Contains(Machine.For(p).ScreenHelp, help);
        Assert.Contains("exits with 4 on an internal error", help);
    }

    // Review code-quality-tools 5: the help and the readers come from one table of options.
    [Theory]
    [InlineData("tern <file.tern> compiles", "--target", "-o", "--json", "--max-errors", "--no-warnings", "--warnings-as-errors")]
    [InlineData("tern run <file.tern> compiles", "--target", "--json", "--max-errors", "--no-warnings", "--warnings-as-errors",
        "--cycles", "--irq", "--set", "--dump", "--screen", "--profile")]
    [InlineData("tern bench compiles", "--target", "--json", "--max-errors", "--cycles", "--irq", "--set", "--dump", "--code", "--decl", "--fn")]
    public void The_help_names_each_option_under_each_command_that_takes_it(string section, params string[] options)
    {
        string help = Tern("--help").Output;
        string[] starts = ["tern <file.tern> compiles", "tern run <file.tern> compiles", "tern bench compiles"];
        int start = help.IndexOf(section, StringComparison.Ordinal);
        int end = starts.Select(s => help.IndexOf(s, start + 1, StringComparison.Ordinal)).Where(i => i > start).DefaultIfEmpty(help.Length).Min();
        var named = System.Text.RegularExpressions.Regex.Matches(help[start..end], @"(?<![\w-])(-o|--[a-z-]+)\b").Select(m => m.Value).ToHashSet();
        Assert.Subset(named, options.ToHashSet());
    }

    [Fact]
    public void Every_option_the_help_names_is_read_by_a_command()
    {
        var names = System.Text.RegularExpressions.Regex.Matches(Tern("--help").Output, @"(?<![\w-])(-o|--[a-z-]+)\b").Select(m => m.Value).Distinct().ToList();
        Assert.Equal(15, names.Count);
        foreach (var name in names)
        {
            // Each command either reads the option or names the commands it belongs to; none calls it unknown.
            foreach (var command in new[] { new[] { "--target", "c64" }, ["run", "--target", "c64"], ["bench", "--target", "c64"] })
                Assert.DoesNotContain("Unknown argument", Tern([.. command, name]).Error);
        }
    }

    [Fact]
    public void Bench_keeps_the_order_of_files_and_code_variants()
    {
        string a = Write("first.tern"), b = Write("third.tern");
        var (code, output, _) = Tern("bench", "--target", "c64", a, "--code", "var x: u8 = 1", b);
        Assert.Equal(0, code);
        Assert.True(output.IndexOf("first.tern", StringComparison.Ordinal) < output.IndexOf("code 1", StringComparison.Ordinal));
        Assert.True(output.IndexOf("code 1", StringComparison.Ordinal) < output.IndexOf("third.tern", StringComparison.Ordinal));
    }

    // ---------- Bench's ok and exit code (tools 8) ----------

    [Fact]
    public void Bench_fails_a_variant_that_did_not_return_in_json_and_exit_code()
    {
        var (code, output, _) = Tern("bench", "--target", "c64", "--json", "--cycles", "1000", "--code", "while true { }");
        Assert.Equal(1, code);
        Assert.False(JsonDocument.Parse(output).RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Bench_fails_a_dump_that_names_nothing()
    {
        var (code, output, _) = Tern("bench", "--target", "c64", "--code", "var a: u8 = 1", "--dump", "nosuch");
        Assert.Equal(1, code);
        Assert.Contains("nosuch = ('nosuch' is not a variable of the program", output);
        var (jsonCode, json, _) = Tern("bench", "--target", "c64", "--json", "--code", "var a: u8 = 1", "--dump", "nosuch");
        Assert.Equal(1, jsonCode);
        Assert.False(JsonDocument.Parse(json).RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Bench_of_variants_that_return_is_ok()
    {
        var (code, output, _) = Tern("bench", "--target", "c64", "--json", "--code", "var a: u8 = 1", "--dump", "main.a");
        Assert.Equal(0, code);
        Assert.True(JsonDocument.Parse(output).RootElement.GetProperty("ok").GetBoolean());
    }
}
