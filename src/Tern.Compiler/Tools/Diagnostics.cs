using System.Text.Json.Serialization;
using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tools;

/// <summary>Whether a <see cref="Diagnostic"/> stops the build. JSON writes it as "error" or "warning".</summary>
public enum Severity { Error, Warning }

/// <summary>
/// One compiler message for tools (<c>tern --json</c>): where it is, what is wrong, and how to fix it.
/// <see cref="Message"/> followed by <see cref="Suggestion"/> is the whole text of the error.
/// </summary>
public sealed record Diagnostic(string File, int Line, int Column, Severity Severity, string Message, string? Suggestion)
{
    /// <summary>
    /// An error of the tool, not at a place in the source (a path, an option, a run): its text split into the
    /// message and the suggestion as a compiler error's is (<see cref="Split"/>).
    /// </summary>
    public static Diagnostic Error(string file, string text)
    {
        var (message, suggestion) = Split(text);
        return new Diagnostic(file, 0, 0, Severity.Error, message, suggestion);
    }

    /// <summary>The errors of a failed compile and the warnings found before it, sorted by place (spec 13): the one conversion for every tool.</summary>
    public static List<Diagnostic> All(CompileError e, string defaultFile) => e.WithWarnings.Select(x => From(x, defaultFile)).ToList();

    /// <summary>"error" or "warning", as the text output and JSON write it.</summary>
    [JsonIgnore] public string SeverityWord => Severity == Severity.Error ? "error" : "warning";

    /// <summary>
    /// For an error in a const fn or in generated code: the line that called it (spec 4.1), also named
    /// in the message. Null for other errors.
    /// </summary>
    public Location? CalledFrom { get; init; }

    /// <summary>A place in a source file.</summary>
    public sealed record Location(string File, int Line, int Column);

    /// <summary>
    /// Words that start the fix in an error's text: the first sentence (after ". " or "; ") that starts
    /// with one of them begins the suggestion.
    /// </summary>
    private static readonly HashSet<string> FixVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Add", "Assign", "Call", "Change", "Check", "Choose", "Compare", "Convert", "Declare", "Define", "Export", "Fill", "Free", "Give",
        "Import", "Install", "Join", "Keep", "Leave", "Make", "Mark", "Match", "Move", "Name", "Pass", "Place", "Prefer", "Put",
        "Read", "Remove", "Rename", "Replace", "Return", "Separate", "Set", "Split", "Start", "Store", "Take", "Use", "Write",
    };

    public static Diagnostic From(CompileError e, string defaultFile)
    {
        var (message, suggestion) = Split(e.Description);
        return new Diagnostic(e.Pos.File ?? defaultFile, e.Pos.Line, e.Pos.Column, e.IsWarning ? Severity.Warning : Severity.Error, message, suggestion)
        {
            CalledFrom = e.CalledFrom is { } call ? new Location(call.File ?? defaultFile, call.Line, call.Column) : null,
        };
    }

    /// <summary>The line that ends the text output of a compile with errors or warnings: "3 errors", "1 error, 2 warnings" (spec 13).</summary>
    /// <param name="more">--max-errors stopped the compile before every error was found.</param>
    public static string Count(int count, bool more, int maxErrors, int warnings = 0) =>
        string.Join(", ", new[] { Plural(count, "error"), Plural(warnings, "warning") }.Where(p => p.Length > 0))
        + (more ? $"; more errors were not reported (--max-errors {maxErrors})" : "");

    private static string Plural(int n, string what) => n switch { 0 => "", 1 => $"1 {what}", _ => $"{n} {what}s" };

    /// <summary>Splits an error's text before the first sentence that tells how to fix it.</summary>
    public static (string Message, string? Suggestion) Split(string text)
    {
        int i = FixStart(text);
        if (i < 0) return (text, null);
        string head = text[..i].TrimEnd();
        string tail = text[(i + 2)..];
        return (text[i] == '.' ? head + "." : head, char.ToUpperInvariant(tail[0]) + tail[1..]);
    }

    /// <summary>
    /// Where the fix begins in an error's text: the index of the ". " or "; " before the first sentence that
    /// starts with one of <see cref="FixVerbs"/>, or -1 when no sentence tells how to fix it.
    /// </summary>
    public static int FixStart(string text)
    {
        for (int i = 0; i + 2 < text.Length; i++)
        {
            if (text[i] is not ('.' or ';') || text[i + 1] != ' ') continue;
            int start = i + 2, end = start;
            while (end < text.Length && char.IsLetter(text[end])) end++;
            if (end > start && FixVerbs.Contains(text[start..end])) return i;
        }
        return -1;
    }

    /// <summary>
    /// The output of <c>tern --json</c>: <c>ok</c>, the files written, the report's lines and the
    /// diagnostics (an empty list when the program compiled).
    /// </summary>
    public static string ToJson(bool ok, IReadOnlyList<string> files, string? report, IReadOnlyList<Diagnostic> diagnostics) =>
        JsonOutput.Write(new CompileOutput(ok, files, report?.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray() ?? [], diagnostics));
}
