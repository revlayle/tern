using System.Text.Json;
using Tern.Compiler.Tools;

namespace Tern.Compiler.Tests;

/// <summary>
/// Warnings in the tools (spec 13): <c>warning:</c> in text with the count after the errors' ("1 error,
/// 2 warnings"), <c>"severity": "warning"</c> in JSON with <c>ok</c> true when there are only warnings,
/// <c>--no-warnings</c> and <c>--warnings-as-errors</c> (not both); <c>tern run</c> reports them,
/// <c>tern bench</c> does not.
/// </summary>
public sealed class WarningToolTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tern");

    public void Dispose() => _dir.Delete(recursive: true);

    private static (int Code, string Output, string Error) Tern(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = Cli.Run(Command.C64(args), output, error);
        return (code, output.ToString(), error.ToString());
    }

    private static List<string> Lines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();

    private string Write(string source)
    {
        var path = Path.Combine(_dir.FullName, "main.tern");
        File.WriteAllText(path, source);
        return path;
    }

    /// <summary>Two warnings around a later error: a recursion, found by the call graph after the checks.</summary>
    private const string WithRecursion = """
        fn main() {
            var unused: u8 = 1
            f()
        }
        fn f() { g() }
        fn g() { f() }
        fn spare() { }
        """;

    private const string OneUnused = """
        var out: u8
        fn main() {
            var unused: u8 = 1
            out = 7
        }
        """;

    private const string UnusedMessage = "'unused' is never used. Remove it, or start its name with '_' to keep it";

    [Fact]
    public void Tern_prints_warnings_and_their_number_and_writes_the_program()
    {
        var path = Write("""
            import text
            fn main() { var unused: u8 = 1 }
            """);
        var (code, output, error) = Tern(path);
        Assert.Equal(0, code);
        Assert.Equal([
            $"{path}:1:8: warning: 'text' is imported but not used. Remove the import",
            $"{path}:2:13: warning: {UnusedMessage}",
            "2 warnings",
        ], Lines(error));
        Assert.Contains("Wrote ", output);
        Assert.True(File.Exists(Path.ChangeExtension(path, ".prg")));
    }

    [Fact]
    public void Tern_lists_warnings_with_a_later_error_by_place_and_counts_both()
    {
        var path = Write(WithRecursion);
        var (code, _, error) = Tern(path);
        Assert.Equal(1, code);
        var lines = Lines(error);
        Assert.Equal(4, lines.Count);
        Assert.Equal($"{path}:2:5: warning: {UnusedMessage}", lines[0]);
        Assert.Contains(": error: ", lines[1]);
        Assert.StartsWith($"{path}:7:1: warning: 'spare'", lines[2]);
        Assert.Equal("1 error, 2 warnings", lines[3]);
    }

    [Fact]
    public void Max_errors_counts_errors_only()
    {
        var (_, _, error) = Tern("--max-errors", "1", Write(WithRecursion));
        Assert.Equal(4, Lines(error).Count);
        Assert.Equal("1 error, 2 warnings", Lines(error)[^1]);
    }

    [Fact]
    public void Json_gives_warnings_with_their_severity_and_ok_stays_true()
    {
        var (code, output, _) = Tern("--json", Write(OneUnused));
        Assert.Equal(0, code);
        using (var doc = JsonDocument.Parse(output))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            var d = Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
            Assert.Equal("warning", d.GetProperty("severity").GetString());
            Assert.Equal(3, d.GetProperty("line").GetInt32());
            Assert.Equal("'unused' is never used.", d.GetProperty("message").GetString());
            Assert.Equal("Remove it, or start its name with '_' to keep it", d.GetProperty("suggestion").GetString());
        }

        (code, output, _) = Tern("--json", Write(WithRecursion));
        Assert.Equal(1, code);
        using var failed = JsonDocument.Parse(output);
        Assert.False(failed.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(["warning", "error", "warning"],
            failed.RootElement.GetProperty("diagnostics").EnumerateArray().Select(x => x.GetProperty("severity").GetString()).ToList());
    }

    [Fact]
    public void No_warnings_turns_them_off()
    {
        var (code, _, error) = Tern("--no-warnings", Write(OneUnused));
        Assert.Equal(0, code);
        Assert.Equal("", error);
    }

    [Fact]
    public void Warnings_as_errors_reports_each_as_an_error_and_stops_the_build()
    {
        var path = Write(OneUnused);
        var (code, _, error) = Tern("--warnings-as-errors", path);
        Assert.Equal(1, code);
        Assert.Equal([$"{path}:3:5: error: {UnusedMessage}", "1 error"], Lines(error));
        Assert.False(File.Exists(Path.ChangeExtension(path, ".prg")));
    }

    [Fact]
    public void Both_switches_together_are_an_error()
    {
        var path = Write(OneUnused);
        foreach (var args in new[] { new[] { "--no-warnings", "--warnings-as-errors", path }, ["run", path, "--warnings-as-errors", "--no-warnings"] })
        {
            var (code, _, error) = Tern(args);
            Assert.Equal(1, code);
            Assert.Equal("--no-warnings and --warnings-as-errors cannot be given together. Run 'tern --help' for the options.", error.Trim());
        }
    }

    [Fact]
    public void Tern_run_reports_warnings_and_runs_the_program()
    {
        var path = Write(OneUnused);
        var (code, output, error) = Tern("run", path, "--dump", "out");
        Assert.Equal(0, code);
        Assert.Equal([$"{path}:3:5: warning: {UnusedMessage}", "1 warning"], Lines(error));
        Assert.Contains("out $0002 u8 = 7", output);

        (code, output, _) = Tern("run", "--json", path);
        Assert.Equal(0, code);
        using (var doc = JsonDocument.Parse(output))
            Assert.Equal("warning", Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray()).GetProperty("severity").GetString());

        (code, _, error) = Tern("run", "--warnings-as-errors", path);
        Assert.Equal(1, code);
        Assert.Equal("1 error", Lines(error)[^1]);
    }

    [Fact]
    public void Tern_bench_does_not_report_warnings()
    {
        var (code, output, error) = Tern("bench", "--code", "var unused: u8 = 1", "--code", "var other: u8 = 2");
        Assert.Equal(0, code);
        Assert.Equal("", error);
        Assert.DoesNotContain("warning", output);
    }
}
