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
    /// <param name="map">
    /// Turns an address into the value the line names (Mesen: an offset into PRG ROM or RAM, see
    /// <see cref="Mesen"/>), or -1 to leave the label out. Null: the address itself.
    /// </param>
    public string Write(IEnumerable<KeyValuePair<string, int>> labels, Func<int, int>? map = null)
    {
        var text = new StringBuilder();
        foreach (var line in Header) text.AppendLine(line);
        foreach (var (name, address) in labels.OrderBy(s => s.Value))
        {
            int value = map is null ? address : map(address);
            if (value >= 0) text.AppendLine(Line(value, name));
        }
        return text.ToString();
    }

    /// <summary>In a value for <see cref="Mesen"/>: the label is in PRG ROM, at the offset below this bit.</summary>
    public const int PrgRom = 0x100000;

    /// <summary>VICE's monitor commands: "al C:0810 .main".</summary>
    public static readonly LabelFormat Vice = new() { Extension = ".vs", Line = (address, name) => $"al C:{address:x4} .{name}" };

    /// <summary>MADS's label table, which Altirra loads by itself with the program of the same name.</summary>
    public static readonly LabelFormat Mads = new()
    {
        Extension = ".lbl",
        Header = ["mads label table, written by Tern", "Label table:"],
        Line = (address, name) => $"00\t{address:X4}\t{name}",
    };

    /// <summary>
    /// AppleWin's symbol tables: "ADDRESS NAME", ';' starts a comment. Loaded in AppleWin's debugger with
    /// symuser load "..\path\game.sym": the path in quotes, relative to AppleWin's folder (checked in 1.32).
    /// </summary>
    public static readonly LabelFormat AppleWin = new()
    {
        Extension = ".sym",
        Header = ["; AppleWin symbols, written by Tern"],
        Line = (address, name) => $"{address:X4} {name}",
    };

    /// <summary>
    /// Mesen's label files (.mlb): "NesPrgRom:offset:name" for an offset into PRG ROM (the value has
    /// <see cref="PrgRom"/> set), "NesInternalRam:address:name" for RAM. Mesen's labels have no dots, so
    /// a dot is written as '_'.
    /// </summary>
    public static readonly LabelFormat Mesen = new()
    {
        Extension = ".mlb",
        Line = (value, name) => (value & PrgRom) != 0
            ? $"NesPrgRom:{value & ~PrgRom:X4}:{name.Replace('.', '_')}"
            : $"NesInternalRam:{value:X4}:{name.Replace('.', '_')}",
    };
}
