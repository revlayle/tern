using System.Text.Json;
using Tern.Compiler.Syntax;
using Tern.Compiler.Tools;

namespace Tern.Compiler.Tests;

/// <summary>M19: diagnostics for tools, the output of <c>tern --json</c>.</summary>
public class DiagnosticTests
{
    private static Diagnostic Error(string source, string file = "game.tern")
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        return Diagnostic.From(e, file);
    }

    [Fact]
    public void An_error_gives_its_place_what_is_wrong_and_the_fix()
    {
        var d = Error("var v: u8\nfn f(n: u8) { v = n }\ninterrupt fn h() { f(1) }\nfn main() { f(2) }");
        Assert.Equal("game.tern", d.File);
        Assert.Equal(2, d.Line);
        Assert.Equal(1, d.Column);
        Assert.Equal("error", d.Severity);
        Assert.StartsWith("'f' is called from main and from the interrupt handler 'h'.", d.Message);
        Assert.EndsWith("shared between main and interrupt handlers.", d.Message);
        Assert.Equal("Use a copy for each", d.Suggestion);
    }

    [Fact]
    public void The_message_and_the_suggestion_are_the_whole_text()
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("var w: u16\nfn main() { match w { 0 => w = 1 } }"));
        var d = Diagnostic.From(e, "x.tern");
        Assert.Equal("'match' on a 16-bit value is not supported yet.", d.Message);
        Assert.Equal("Match on .lo or .hi, or use if / else if", d.Suggestion);
        Assert.Equal(e.Description, d.Message + " " + d.Suggestion);
    }

    [Fact]
    public void A_fix_after_a_semicolon_is_a_suggestion()
    {
        var d = Error("fn addr() { }\nfn main() { }");
        Assert.Equal("'addr' is a built-in function", d.Message);
        Assert.Equal("Choose another name", d.Suggestion);
    }

    [Fact]
    public void An_error_with_no_fix_has_no_suggestion()
    {
        var d = Error("fn main() { x = 1 }");
        Assert.Null(d.Suggestion);
        Assert.Contains("'x'", d.Message);
    }

    [Theory]
    [InlineData("Something is wrong", "Something is wrong", null)]
    [InlineData("A is wrong. It is bad. Write b instead", "A is wrong. It is bad.", "Write b instead")]
    [InlineData("Use 'a.b' here", "Use 'a.b' here", null)]
    [InlineData("x.y is wrong. remove it", "x.y is wrong.", "Remove it")]
    public void Split_finds_the_first_sentence_with_a_fix(string text, string message, string? suggestion)
    {
        var (m, s) = Diagnostic.Split(text);
        Assert.Equal(message, m);
        Assert.Equal(suggestion, s);
    }

    [Fact]
    public void The_json_of_an_error()
    {
        var d = Error("var w: u16\nfn main() { match w { 0 => w = 1 } }");
        using var doc = JsonDocument.Parse(Diagnostic.ToJson(false, [], null, [d]));
        var root = doc.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(0, root.GetProperty("files").GetArrayLength());
        Assert.Equal(0, root.GetProperty("report").GetArrayLength());
        var e = root.GetProperty("diagnostics")[0];
        Assert.Equal("game.tern", e.GetProperty("file").GetString());
        Assert.Equal(2, e.GetProperty("line").GetInt32());
        Assert.Equal(19, e.GetProperty("column").GetInt32());
        Assert.Equal("error", e.GetProperty("severity").GetString());
        Assert.Equal("'match' on a 16-bit value is not supported yet.", e.GetProperty("message").GetString());
        Assert.Equal("Match on .lo or .hi, or use if / else if", e.GetProperty("suggestion").GetString());
    }

    [Fact]
    public void The_json_of_a_compiled_program_lists_the_files_and_the_report()
    {
        var result = TernCompiler.Compile("var x: u8\nfn main() { x = 1 }");
        string json = Diagnostic.ToJson(true, ["game.prg", "game.lst"], result.Report, []);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(["game.prg", "game.lst"], root.GetProperty("files").EnumerateArray().Select(f => f.GetString()));
        Assert.StartsWith("Code and data: $0801-", root.GetProperty("report")[0].GetString());
        Assert.Equal(0, root.GetProperty("diagnostics").GetArrayLength());
    }

    [Fact]
    public void A_missing_suggestion_is_null_in_the_json()
    {
        var d = Error("fn main() { x = 1 }");
        using var doc = JsonDocument.Parse(Diagnostic.ToJson(false, [], null, [d]));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("diagnostics")[0].GetProperty("suggestion").ValueKind);
    }
}
