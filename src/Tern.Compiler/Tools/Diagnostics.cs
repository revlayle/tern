using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tools;

/// <summary>
/// One compiler message for tools (<c>tern --json</c>): where it is, what is wrong, and how to fix it.
/// <see cref="Message"/> followed by <see cref="Suggestion"/> is the whole text of the error.
/// </summary>
public sealed record Diagnostic(string File, int Line, int Column, string Severity, string Message, string? Suggestion)
{
    /// <summary>
    /// Words that start the fix in an error's text: the first sentence (after ". " or "; ") that starts
    /// with one of them begins the suggestion.
    /// </summary>
    private static readonly HashSet<string> FixVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Add", "Assign", "Call", "Change", "Check", "Choose", "Compare", "Convert", "Declare", "Export", "Fill", "Free", "Give",
        "Import", "Install", "Join", "Keep", "Leave", "Make", "Mark", "Match", "Move", "Pass", "Place", "Prefer", "Put",
        "Read", "Remove", "Rename", "Replace", "Return", "Set", "Split", "Store", "Take", "Use", "Write",
    };

    public static Diagnostic From(CompileError e, string defaultFile)
    {
        var (message, suggestion) = Split(e.Description);
        return new Diagnostic(e.Pos.File ?? defaultFile, e.Pos.Line, e.Pos.Column, "error", message, suggestion);
    }

    /// <summary>Splits an error's text before the first sentence that tells how to fix it.</summary>
    public static (string Message, string? Suggestion) Split(string text)
    {
        for (int i = 0; i + 2 < text.Length; i++)
        {
            if (text[i] is not ('.' or ';') || text[i + 1] != ' ') continue;
            int start = i + 2, end = start;
            while (end < text.Length && char.IsLetter(text[end])) end++;
            if (end > start && FixVerbs.Contains(text[start..end]))
            {
                string head = text[..i].TrimEnd();
                string tail = text[start..];
                return (text[i] == '.' ? head + "." : head, char.ToUpperInvariant(tail[0]) + tail[1..]);
            }
        }
        return (text, null);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// The output of <c>tern --json</c>: <c>ok</c>, the files written, the report's lines and the
    /// diagnostics (an empty list when the program compiled).
    /// </summary>
    public static string ToJson(bool ok, IReadOnlyList<string> files, string? report, IReadOnlyList<Diagnostic> diagnostics) =>
        JsonSerializer.Serialize(new
        {
            ok,
            files,
            report = report?.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray() ?? [],
            diagnostics,
        }, Options);
}
