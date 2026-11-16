using System.Text.Json;
using Tern.Compiler.Tools;

namespace Tern.Compiler.Tests;

/// <summary>
/// The command line's handling of paths and option values (review code-quality-tools 2, 3, 8 and 13): every
/// problem is a message and a documented exit code, never a stack trace or a lost file.
/// </summary>
public sealed class CliTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tern-cli-");

    public void Dispose() => _dir.Delete(recursive: true);

    private const string Program = "var out: u8\nfn main() { out = 1 }\n";

    private string Write(string name, string source = Program)
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllText(path, source);
        return path;
    }

    private static (int Code, string Output, string Error) Tern(params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        int code = Cli.Run(args, output, error);
        return (code, output.ToString(), error.ToString().Trim());
    }

    private static JsonElement OnlyDiagnostic(string output)
    {
        var json = JsonDocument.Parse(output).RootElement;
        Assert.False(json.GetProperty("ok").GetBoolean());
        return Assert.Single(json.GetProperty("diagnostics").EnumerateArray());
    }

    // ---------- Paths (tools 2) ----------

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
        Assert.Equal($"Cannot read {missing}: the file does not exist. Check the path", OnlyDiagnostic(output).GetProperty("message").GetString());
    }

    [Fact]
    public void An_empty_source_name_is_no_source()
    {
        var (code, _, error) = Tern("--target", "c64", "");
        Assert.Equal(1, code);
        Assert.Equal("No source file given. Run 'tern --help' for the options.", error);
    }

    [Fact]
    public void Bench_of_a_binary_without_main_is_a_failed_variant()
    {
        string lib = Write("lib1.tern", "var n: u8\nfn bump() { n += 1 }\nexport bump\n");
        var (code, output, _) = Tern("bench", "--target", "c64", "--fn", "bump", lib);
        Assert.Equal(1, code);
        Assert.Contains("error: A binary without main cannot be run on its own. Measure the function in a program that loads and calls it", output);
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
    [InlineData(new[] { "run", "--target", "c64", "SRC", "--cycles", "abc" }, "--cycles needs a whole number above 0, found 'abc'")]
    [InlineData(new[] { "run", "--target", "c64", "SRC", "--cycles", "0" }, "--cycles needs a whole number above 0, found '0'")]
    [InlineData(new[] { "run", "--target", "c64", "SRC", "--irq", "-5" }, "--irq needs a whole number above 0, found '-5'")]
    [InlineData(new[] { "run", "--target", "c64", "SRC", "--cycles" }, "--cycles needs a whole number above 0 after it")]
    [InlineData(new[] { "--target", "c64", "SRC", "--max-errors", "0" }, "--max-errors needs a whole number from 1 to 2147483647, found '0'")]
    [InlineData(new[] { "bench", "--target", "c64", "SRC", "--max-errors", "x" }, "--max-errors needs a whole number from 1 to 2147483647, found 'x'")]
    [InlineData(new[] { "--target", "c64", "SRC", "-o" }, "-o needs a value after it")]
    [InlineData(new[] { "run", "--target", "c64", "SRC", "--dump" }, "--dump needs a value after it")]
    [InlineData(new[] { "SRC", "--target" }, "--target needs a value after it")]
    [InlineData(new[] { "run", "--target", "c64", "SRC", "--fast" }, "Unknown argument: --fast")]
    public void A_bad_option_value_names_the_option_and_the_value(string[] args, string expected)
    {
        string src = Write("a.tern");
        var (code, _, error) = Tern(args.Select(a => a == "SRC" ? src : a).ToArray());
        Assert.Equal(1, code);
        Assert.Equal(expected + ". Run 'tern --help' for the options.", error);
    }

    [Fact]
    public void A_command_line_problem_with_json_is_json()
    {
        var (code, output, error) = Tern("--json", "--target");
        Assert.Equal(1, code);
        Assert.Equal("", error);
        var d = OnlyDiagnostic(output);
        Assert.Equal("--target needs a value after it. Run 'tern --help' for the options", d.GetProperty("message").GetString() + (d.GetProperty("suggestion").GetString() is { } s ? ". " + s : ""));
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
        Assert.Contains("A brk stops the run, except on the NES when the program sets the IRQ vector", help);
        Assert.Contains("exits with 4 on an internal error", help);
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
        Assert.Contains("nosuch = (No variable 'nosuch'", output);
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
