using System.Text;

namespace Tern.Compiler;

/// <summary>
/// A label file's format: the one the platform's main emulator loads (spec 13, decision 2026-11-13,
/// question 138). The platform description names the one it uses.
/// </summary>
public sealed class LabelFormat
{
    /// <summary>The label file's extension, such as ".vs".</summary>
    public required string Extension { get; init; }
    /// <summary>Lines before the labels.</summary>
    public string[] Header { get; init; } = [];
    /// <summary>One label's line.</summary>
    public required Func<int, string, string> Line { get; init; }

    /// <summary>The label file for <paramref name="labels"/>, in address order.</summary>
    public string Write(IEnumerable<KeyValuePair<string, int>> labels)
    {
        var text = new StringBuilder();
        foreach (var line in Header) text.AppendLine(line);
        foreach (var (name, address) in labels.OrderBy(s => s.Value)) text.AppendLine(Line(address, name));
        return text.ToString();
    }

    /// <summary>VICE's monitor commands: "al C:0810 .main".</summary>
    public static readonly LabelFormat Vice = new() { Extension = ".vs", Line = (address, name) => $"al C:{address:x4} .{name}" };

    /// <summary>MADS's label table, which Altirra loads by itself with the program of the same name.</summary>
    public static readonly LabelFormat Mads = new()
    {
        Extension = ".lbl",
        Header = ["mads label table, written by Tern", "Label table:"],
        Line = (address, name) => $"00\t{address:X4}\t{name}",
    };

    /// <summary>AppleWin's symbol tables: "ADDRESS NAME", ';' starts a comment. Loaded in the debugger with "symuser load".</summary>
    public static readonly LabelFormat AppleWin = new()
    {
        Extension = ".sym",
        Header = ["; AppleWin symbols, written by Tern"],
        Line = (address, name) => $"{address:X4} {name}",
    };
}
