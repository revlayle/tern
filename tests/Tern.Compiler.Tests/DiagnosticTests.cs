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
        var d = Error("var v: u8\nfn main() {\n    v = 300\n}");
        Assert.Equal("game.tern", d.File);
        Assert.Equal(3, d.Line);
        Assert.Equal(9, d.Column);
        Assert.Equal("error", d.Severity);
        Assert.Equal("300 does not fit in u8 (0 to 255).", d.Message);
        Assert.Equal("Use a value in that range, or a wider type", d.Suggestion);
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
    public void A_string_in_code_suggests_declaring_it_as_data()
    {
        var d = Error("import c64\nfn main() { c64.print(\"HI\", 2) }");
        Assert.Equal("A string can only be the value of data or a global array.", d.Message);
        Assert.Equal("Declare it as data, e.g. 'data HI = \"HI\"', and pass addr(HI), len(HI)", d.Suggestion);
        Assert.Equal(2, d.Line);
        Assert.Equal(23, d.Column);
    }

    [Fact]
    public void An_error_with_no_fix_has_no_suggestion()
    {
        var d = Error("var x: u8\nfn main() { x = ) }");   // listed in FixSentenceTests.NoFix
        Assert.Null(d.Suggestion);
        Assert.Contains("')'", d.Message);
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
        var d = Error("var x: u8\nfn main() { x = ) }");   // listed in FixSentenceTests.NoFix
        using var doc = JsonDocument.Parse(Diagnostic.ToJson(false, [], null, [d]));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("diagnostics")[0].GetProperty("suggestion").ValueKind);
    }

    // ---------- The call's location (spec 4.1, decision 2026-11-11, question 102) ----------

    private static JsonElement Json(Diagnostic d)
    {
        using var doc = JsonDocument.Parse(Diagnostic.ToJson(false, [], null, [d]));
        return doc.RootElement.GetProperty("diagnostics")[0].Clone();
    }

    [Fact]
    public void An_error_in_a_const_fn_gives_the_calls_location_as_a_field()
    {
        var d = Error("const fn f(n: int) -> int {\n    return 10 / n\n}\nconst A = 1\nconst K = f(0)\nfn main() { }");
        var e = Json(d);
        Assert.Equal(2, e.GetProperty("line").GetInt32());
        Assert.Equal("Division by zero (called from line 5).", e.GetProperty("message").GetString());
        var call = e.GetProperty("calledFrom");
        Assert.Equal("game.tern", call.GetProperty("file").GetString());
        Assert.Equal(5, call.GetProperty("line").GetInt32());
        Assert.Equal(11, call.GetProperty("column").GetInt32());
    }

    [Fact]
    public void A_failing_assert_in_a_const_fn_gives_the_calls_location_as_a_field()
    {
        var d = Error("const fn f(n: int) -> int {\n    assert n < 10, \"f: n must be below 10\"\n    return n\n}\nconst K = f(12)\nfn main() { }");
        var e = Json(d);
        Assert.Equal(2, e.GetProperty("line").GetInt32());
        Assert.Equal("f: n must be below 10 (called from line 5)", e.GetProperty("message").GetString());
        Assert.Equal(5, e.GetProperty("calledFrom").GetProperty("line").GetInt32());
        Assert.Equal(11, e.GetProperty("calledFrom").GetProperty("column").GetInt32());
    }

    [Fact]
    public void An_error_in_generated_code_gives_the_generator_calls_location()
    {
        // A branch out of range, found by the assembler, and an operand the checker rejects.
        var far = Error("const fn h() {\n    var end = label()\n    emit { beq end }\n    for _ in 0..200 { emit { nop } }\n    emit { end: }\n}\nfn main() {\n    asm {\n        h()\n    }\n}");
        Assert.Equal(3, far.Line);
        Assert.Contains("(generated by the call at line 9)", far.Message);
        Assert.Equal(9, Json(far).GetProperty("calledFrom").GetProperty("line").GetInt32());
        var bad = Error("const fn g() {\n    emit { lda #300 }\n}\nfn main() {\n    asm {\n        nop\n        g()\n    }\n}");
        Assert.Equal(2, bad.Line);
        Assert.Equal(7, Json(bad).GetProperty("calledFrom").GetProperty("line").GetInt32());
        Assert.Contains("(generated by the call at line 7)", bad.Message);
    }

    [Fact]
    public void An_error_in_a_module_const_fn_gives_the_main_files_call()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "tables.tern"), "const fn f() -> int {\n    return 1 / 0\n}\n");
            string main = Path.Combine(dir.FullName, "main.tern");
            var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("import tables\nconst K = tables.f()\nfn main() { }", sourcePath: main));
            var json = Json(Diagnostic.From(e, main));
            Assert.EndsWith("tables.tern", json.GetProperty("file").GetString());
            Assert.Equal(main, json.GetProperty("calledFrom").GetProperty("file").GetString());
            Assert.Equal(2, json.GetProperty("calledFrom").GetProperty("line").GetInt32());
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Other_errors_have_no_call_location()
    {
        Assert.Equal(JsonValueKind.Null, Json(Error("fn main() { x = 1 }")).GetProperty("calledFrom").ValueKind);
        // An error at the top level of a constant is not inside a const fn.
        Assert.Equal(JsonValueKind.Null, Json(Error("const K = 1 / 0.0\nfn main() { }")).GetProperty("calledFrom").ValueKind);
    }
}
