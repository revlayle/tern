using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// Checks a compile error whole (review code-quality-tests, finding 4): its line, its column and its
/// full message, fix sentence included, as <c>tern --json</c> reports them.
/// </summary>
public static class Expect
{
    /// <summary>
    /// Compiles <paramref name="source"/> for the default target, as it needs it (<see cref="TestTarget.Adapt"/>),
    /// and checks the error it gives, at its place in <paramref name="source"/>.
    /// </summary>
    public static CompileError Error(string source, int line, int column, string message)
    {
        string adapted = TestTarget.Adapt(source, TestTarget.Default, out var moved);
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(adapted, TestTarget.Default));
        // Places on the line whose columns moved, in the position and in the text ("declared at 1:37"), as in the source.
        int Column(int l, int c) => l == moved.Line && c > moved.Columns ? c - moved.Columns : c;
        string text = moved.Line == 0 ? e.Description : System.Text.RegularExpressions.Regex.Replace(e.Description,
            @"\b(\d+):(\d+)\b", m => $"{m.Groups[1].Value}:{Column(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))}");
        Assert.Equal(message, text);
        Assert.Equal((line, column), (e.Pos.Line, Column(e.Pos.Line, e.Pos.Column)));
        return e;
    }

    /// <summary>Runs <paramref name="compile"/>, which must throw a <see cref="CompileError"/>, and checks it.</summary>
    public static CompileError Error(Func<object?> compile, int line, int column, string message)
    {
        var e = Assert.Throws<CompileError>(compile);
        Message(e, line, column, message);
        return e;
    }

    /// <summary>Checks that <paramref name="e"/> is at <paramref name="line"/> and <paramref name="column"/> with <paramref name="message"/>.</summary>
    public static void Message(CompileError e, int line, int column, string message)
    {
        Assert.Equal(message, e.Description);
        Assert.Equal((line, column), (e.Pos.Line, e.Pos.Column));
    }
}
