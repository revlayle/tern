using System.Text;
using Tern.Compiler.Backend;
using Tern.Compiler.Semantics;

namespace Tern.Compiler.Tools;

/// <summary>
/// Bytes and cycles per source line, from the source map (<see cref="CompileResult.Instructions"/>):
/// the <c>.lines</c> file, and with the cycles measured in a run, the profile of <c>tern run</c> and
/// <c>tern bench</c>.
/// </summary>
public static class LineTable
{
    /// <summary>
    /// One source line, or one section's code with no source line (<see cref="Line"/> null, such as
    /// the startup code). <see cref="Cycles"/> adds up the base cycles of every instruction once, both
    /// sides of an <c>if</c> included; <see cref="MoreCycles"/> says a branch or a page crossing can
    /// add to it. <see cref="Executed"/> is the cycles the line's instructions took in a run, and
    /// <see cref="Runs"/> how often its first instruction ran.
    /// </summary>
    public sealed record Row(string File, int? Line, string Source, int Bytes, int Instructions, int Cycles, bool MoreCycles,
        IReadOnlyList<AddressRange> Addresses, long? Executed = null, long? Runs = null);

    /// <param name="mainFile">The name shown for the main file's lines.</param>
    /// <param name="executed">Cycles taken by the instruction at each address in a run (<see cref="Profile"/>), or null.</param>
    public static List<Row> Rows(CompileResult result, string mainFile, Profile? executed = null)
    {
        var rows = new List<Row>();
        var groups = result.Instructions
            .GroupBy(i => (File: i.File ?? (i.Line is null ? null : mainFile), i.Line, Section: i.Line is null ? i.Section : null))
            .OrderBy(g => g.Key.Line is null)
            .ThenBy(g => g.Key.File != mainFile)
            .ThenBy(g => g.Key.File, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Line)
            .ThenBy(g => g.Min(i => i.Address));
        foreach (var g in groups)
        {
            var list = g.OrderBy(i => i.Address).ToList();
            var ranges = new List<AddressRange>();
            foreach (var i in list)
            {
                if (ranges.Count > 0 && ranges[^1].Last + 1 == i.Address) ranges[^1] = ranges[^1] with { Last = i.Address + i.Size - 1 };
                else ranges.Add(new AddressRange(i.Address, i.Address + i.Size - 1));
            }
            string source = g.Key.Line is null ? $"({g.Key.Section})" : list.First(i => i.Source is not null).Source!;
            rows.Add(new Row(g.Key.File ?? "", g.Key.Line, source, list.Sum(i => i.Size), list.Count, list.Sum(i => i.Cycles), list.Any(i => i.MoreCycles), ranges,
                executed?.CyclesOf(list), executed?.RunsOf(list)));
        }
        return rows;
    }

    /// <summary>The table as text: one row per line, in source order.</summary>
    /// <param name="interrupts">For a run: the cycles of taking interrupts and how many were taken, for the total line.</param>
    public static string Format(IReadOnlyList<Row> rows, (long Cycles, int Count)? interrupts = null)
    {
        bool run = rows.Any(r => r.Executed is not null);
        bool modules = rows.Select(r => r.File).Where(f => f.Length > 0).Distinct().Count() > 1;
        var sb = new StringBuilder();
        sb.AppendLine(run
            ? "; Per source line: bytes, the cycles of its instructions once each (+: a branch or page crossing adds more), how often its first instruction ran, and the cycles they took in the run."
            : "; Per source line: bytes, and the cycles of its instructions once each (+: a branch or page crossing adds more).");
        int fileWidth = Math.Max(4, rows.Count == 0 ? 0 : rows.Max(r => r.File.Length));
        string fileHeader = modules ? "file".PadRight(fileWidth) + " " : "";
        static string Addresses(Row r) => string.Join(" ", r.Addresses.Select(a => a.Size == 1 ? $"${a.First:X4}" : $"${a.First:X4}-${a.Last:X4}"));
        int width = Math.Max(9, rows.Count == 0 ? 0 : rows.Max(r => Addresses(r).Length));
        sb.AppendLine(run
            ? $"; {fileHeader}{"line",5} {"bytes",5} {"cycles",7} {"runs",8} {"run cycles",10}  {"addresses".PadRight(width)}  source"
            : $"; {fileHeader}{"line",5} {"bytes",5} {"cycles",7}  {"addresses".PadRight(width)}  source");
        foreach (var r in rows)
        {
            string file = modules ? (r.Line is null ? "" : r.File).PadRight(fileWidth) + " " : "";
            string line = r.Line?.ToString() ?? "-";
            string cycles = r.Cycles + (r.MoreCycles ? "+" : "");
            string addresses = Addresses(r).PadRight(width);
            sb.AppendLine(run
                ? $"  {file}{line,5} {r.Bytes,5} {cycles,7} {r.Runs,8} {r.Executed,10}  {addresses}  {r.Source}"
                : $"  {file}{line,5} {r.Bytes,5} {cycles,7}  {addresses}  {r.Source}");
        }
        if (run)
            sb.AppendLine($"; total {rows.Sum(r => r.Executed ?? 0)} cycles"
                + (interrupts is { Count: > 0 } i ? $", and {i.Cycles} taking {i.Count} interrupt{(i.Count == 1 ? "" : "s")}" : ""));
        return sb.ToString();
    }
}

/// <summary>How often the instruction at each place (<see cref="CodePlace"/>: an address, with its bank) ran, and the cycles it took, in a run of the emulator.</summary>
public sealed class Profile
{
    private readonly Dictionary<int, long> _runs = [];
    private readonly Dictionary<int, long> _cycles = [];

    public void Add(int place, long cycles)
    {
        _runs[place] = _runs.GetValueOrDefault(place) + 1;
        _cycles[place] = _cycles.GetValueOrDefault(place) + cycles;
    }

    /// <summary>The cycles of taking interrupts (7 each), which belong to no instruction (review code-quality-tools 10).</summary>
    public long InterruptCycles { get; set; }

    /// <summary>Every cycle of the run: the instructions' and <see cref="InterruptCycles"/>.</summary>
    public long Total => _cycles.Values.Sum() + InterruptCycles;

    /// <summary>The cycles of the instructions, including the jmp of a long branch, which starts inside its entry.</summary>
    public long CyclesOf(IEnumerable<PlacedInstruction> instructions) =>
        instructions.Sum(i => Enumerable.Range(i.Address, i.Size).Sum(a => _cycles.GetValueOrDefault(CodePlace.Of(a, i.Bank))));

    /// <summary>How often the first of the instructions (by address) ran.</summary>
    public long RunsOf(IEnumerable<PlacedInstruction> instructions) => _runs.GetValueOrDefault(instructions.MinBy(i => i.Address)!.Place);
}
