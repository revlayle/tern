using Tern.Compiler.Backend;
using Tern.Cpu;

namespace Tern.Compiler.Tools;

/// <summary>
/// One index outside its array, or <c>indexed match</c> value outside its span, seen in a run: the
/// first time an instruction did it (<see cref="Cycle"/>), and how often it did (<see cref="Count"/>).
/// </summary>
public sealed record IndexError(string File, int? Line, string Source, string Message, long Cycle)
{
    public int Count { get; set; } = 1;

    public override string ToString() =>
        $"{File}:{Line}: {Message}" + (Count > 1 ? $" ({Count} times)" : "") + (Source.Length > 0 ? $"\n    {Source}" : "");
}

/// <summary>
/// Checks array indexes and <c>indexed match</c> values while a program runs in the emulator (M20;
/// spec 3.2, 5.2, 14.4), from the instructions the compiler marked (<see cref="CheckedIndex"/>):
/// before each one runs, the address it reads or writes must lie in its table. Pointer accesses and
/// asm blocks are not checked. Nothing is added to the program, so it runs the same either way.
/// </summary>
public sealed class IndexChecker(CompileResult program, string mainFile)
{
    private readonly Dictionary<int, PlacedInstruction> _checks =
        program.Instructions.Where(i => i.Index is not null).GroupBy(i => i.Address).ToDictionary(g => g.Key, g => g.First());
    private readonly Dictionary<int, IndexError> _seen = [];

    /// <summary>The errors seen so far, in the order they first happened.</summary>
    public List<IndexError> Errors { get; } = [];

    /// <summary>
    /// Call before the instruction at the CPU's PC runs. Returns true when the run should stop: an
    /// <c>indexed match</c> value outside its span, whose jump goes to an address no one chose.
    /// </summary>
    public bool Before(Cpu6502 cpu)
    {
        if (!_checks.TryGetValue(cpu.PC, out var instruction)) return false;
        var c = instruction.Index!;
        int register = c.UsesY ? cpu.Y : cpu.X;
        int address = c.ZeroPage ? (c.Base + register) & 0xFF : (c.Base + register) & 0xFFFF;
        int offset = address - c.First;
        if (offset >= 0 && offset < c.Check.Length) return false;

        if (_seen.TryGetValue(cpu.PC, out var seen)) { seen.Count++; return false; }
        var check = c.Check;
        string message;
        if (check.MatchLow is int low)
        {
            int value = (register + low) & 0xFF;
            long shown = check.Signed ? (sbyte)value : value;
            message = $"The 'indexed match' value is {shown}, outside its patterns {low}..{low + check.Length - 1}";
        }
        else
        {
            // As the source wrote it: a u8 index, so a[i - 1] with i = 0 is index 255.
            int index = (offset & 0xFF) / check.Stride;
            message = $"Index {index} is outside '{check.Name}', which has {check.Elements} elements";
        }
        var error = new IndexError(instruction.File ?? mainFile, instruction.Line, instruction.Source ?? "", message, cpu.Cycles);
        _seen[cpu.PC] = error;
        Errors.Add(error);
        return check.MatchLow is not null;
    }
}
