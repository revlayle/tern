using Tern.Compiler.Syntax;
using Tern.Compiler.Semantics;

namespace Tern.Compiler.Tests;

/// <summary>
/// <see cref="TernCompiler.Check"/>: the checks without a build, which return their errors instead of
/// throwing them, for a language server (review code-quality-syntax 7, tools 7; roadmap R5).
/// Spec 13 (compiler design).
/// </summary>
public sealed class CheckTests : TempFolderTest
{

    private string MainPath => Path.Combine(_dir.FullName, "main.tern");

    private static string Line(CompileError e) => $"{e.Pos.Line}:{e.Pos.Column}: {e.Description}";

    [Fact]
    public void A_correct_program_has_a_checked_program_and_no_diagnostics()
    {
        var result = TernCompiler.Check("var border: u8 @ $D020\nfn helper() -> u8 { return 1 }\nfn main() { var x = helper() + 1; border = x }\n");
        Assert.True(result.Ok);
        Assert.NotNull(result.Program);
        Assert.Empty(result.All);
        Assert.Contains(result.Program!.Functions, f => f.Name == "helper");
    }

    [Fact]
    public void Errors_are_returned_with_the_files_as_far_as_they_were_understood()
    {
        const string source = """
            fn helper() -> u8 { return 1 }
            fn main() {
                x = 1
                var y: u8 = true
            }
            """;
        var result = TernCompiler.Check(source);
        Assert.False(result.Ok);
        Assert.Null(result.Program);
        Assert.Equal(2, result.Errors.Count);
        // The same errors as a compile reports, in the same order.
        var thrown = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Equal(thrown.All.Select(Line), result.Errors.Select(Line));
        // What was declared is there, for completion and go-to-definition.
        var main = result.Modules[^1];
        Assert.IsType<FunctionSymbol>(main.Scope["helper"]);
        Assert.Equal(2, main.Syntax.Functions.Count);
    }

    [Fact]
    public void A_syntax_error_is_returned_too()
    {
        var result = TernCompiler.Check("fn main() {\n    var x: u8 = (1 +\n}\n");
        Assert.False(result.Ok);
        Assert.NotEmpty(result.Errors);
        Assert.Single(result.Modules);
    }

    [Fact]
    public void Warnings_are_returned_and_do_not_stop_the_check()
    {
        var result = TernCompiler.Check("fn main() {\n    var temp: u8 = 3\n}\n");
        Assert.True(result.Ok);
        Assert.Empty(result.Errors);
        Assert.Equal(["2:5: 'temp' is never used. Remove it, or start its name with '_' to keep it"], result.Warnings.Select(Line));

        var strict = TernCompiler.Check("fn main() {\n    var temp: u8 = 3\n}\n", warnings: WarningMode.AsErrors);
        Assert.False(strict.Ok);
        Assert.Single(strict.Errors);
        Assert.Empty(TernCompiler.Check("fn main() {\n    var temp: u8 = 3\n}\n", warnings: WarningMode.Off).All);
    }

    [Fact]
    public void Max_errors_is_kept()
    {
        var result = TernCompiler.Check("fn main() {\n    a = 1\n    b = 2\n    c = 3\n}\n", maxErrors: 2);
        Assert.Equal(2, result.Errors.Count);
        Assert.True(result.MoreNotReported);
    }

    [Fact]
    public void The_check_does_not_build()
    {
        // The overlap with the program is found by the layout, which a check does not run.
        const string source = "var first: u8 @ $0801\nfn main() { first = 1 }\n";
        Assert.True(TernCompiler.Check(source).Ok);
        Expect.Message(Assert.Throws<CompileError>(() => TernCompiler.Compile(source)), 1, 1, "'first' at $0801 overlaps the program and its variables at $0801-$0819. Move 'first', or make the program smaller");
        // Nor does it allocate (review R5, finding 2).
        const string full = "var a: [u8; 200] @ zp\nfn main() { a[1] = a[0] }\n";
        Assert.True(TernCompiler.Check(full).Ok);
        Expect.Message(Assert.Throws<CompileError>(() => TernCompiler.Compile(full)), 1, 1, "Zero page is full: '@ zp' variables need 200 bytes, 146 are free. Users: a (200). Remove '@ zp' from some of them");
    }

    [Fact]
    public void A_module_open_in_an_editor_is_read_from_its_text()
    {
        var modulePath = Path.Combine(_dir.FullName, "shapes.tern");
        File.WriteAllText(modulePath, "fn area() -> u8 { return 1 }\n");
        // The editor's text, not saved: another function, and an error on line 2.
        const string open = "fn size() -> u8 { return 2 }\nfn broken() { nothing = 1 }\n";
        string? OpenFile(string path) => string.Equals(path, modulePath, StringComparison.OrdinalIgnoreCase) ? open : null;

        var result = TernCompiler.Check("import shapes\nvar border: u8 @ $D020\nfn main() { border = shapes.size() }\n", sourcePath: MainPath, openFile: OpenFile);
        var error = Assert.Single(result.Errors);
        Assert.Equal(modulePath, error.Pos.File, ignoreCase: true);
        Assert.Equal(2, error.Pos.Line);
        Assert.Equal(open, result.Modules[0].Source);
    }

    [Fact]
    public void A_module_only_open_in_an_editor_is_found()
    {
        var modulePath = Path.Combine(_dir.FullName, "fresh.tern");
        string? OpenFile(string path) => string.Equals(path, modulePath, StringComparison.OrdinalIgnoreCase) ? "fn one() -> u8 { return 1 }\n" : null;
        var result = TernCompiler.Check("import fresh\nvar border: u8 @ $D020\nfn main() { border = fresh.one() }\n", sourcePath: MainPath, openFile: OpenFile);
        Assert.True(result.Ok, string.Join("\n", result.All.Select(Line)));
    }

    [Fact]
    public void Diagnostics_split_the_suggestion_from_the_message()
    {
        var d = Assert.Single(TernCompiler.Check("fn main() {\n    var temp: u8 = 3\n}\n").Diagnostics("main.tern"));
        Assert.Equal(("main.tern", 2, Tools.Severity.Warning, "'temp' is never used."), (d.File, d.Line, d.Severity, d.Message));
        Assert.Equal("Remove it, or start its name with '_' to keep it", d.Suggestion);
    }
}
