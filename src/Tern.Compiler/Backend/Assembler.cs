using System.Text;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using Tern.Cpu;
using Encoder = Tern.Cpu.Encoder;

namespace Tern.Compiler.Backend;

/// <summary>A section (a function, a global's initial value, the startup code) at the address it was placed.</summary>
/// <param name="Overlay">The overlay module the section is part of, or null.</param>
public sealed record PlacedSection(string Name, int Address, int Size, bool IsFixed, string? Overlay = null);

/// <summary>A stretch of a program area that code, data and variables were placed in.</summary>
/// <param name="UsedEnd">The address after the last byte placed in <paramref name="Free"/>.</param>
public sealed record PlacedArea(AddressRange Free, int UsedEnd, List<string> Names);

/// <summary>
/// One instruction of the program and the source line it was generated for: the last source line
/// before it in the listing (null <see cref="Line"/> for code with none, such as the startup code).
/// <see cref="File"/> is null for the main file, else the module's file name. <see cref="Cycles"/> is
/// the base count; <see cref="MoreCycles"/> is set for a branch or a read that can cross a page.
/// </summary>
/// <see cref="Overlay"/> is the overlay or bank its section is in ("levels[1]"), or null.
public sealed record PlacedInstruction(int Address, int Size, string Text, int Cycles, bool MoreCycles, string? File, int? Line, string? Source, string Section,
    CheckedIndex? Index = null, string? Overlay = null)
{
    /// <summary>The bank it is in (spec 8.5), or -1 outside banks.</summary>
    public int Bank => CodePlace.BankOf(Overlay);
    /// <summary>Where it is: its address, told apart from the same address in other banks (<see cref="CodePlace"/>).</summary>
    public int Place => CodePlace.Of(Address, Bank);
}

/// <summary>
/// A place in the program's code: an address, and for code in a bank (spec 8.5) the bank, since banks
/// share their addresses. The emulator's index check, profile and function entries are keyed by place.
/// </summary>
public static class CodePlace
{
    /// <summary>The address, with the bank above its 16 bits; the address alone outside banks (<paramref name="bank"/> -1).</summary>
    public static int Of(int address, int bank) => bank < 0 ? address : (bank + 1) << 16 | address;

    /// <summary>The name of the unit that bank <paramref name="bank"/> of <paramref name="area"/> is ("levels[1]"; "levels[]" for a number that was rejected); <see cref="Bank"/> reads it back.</summary>
    public static string BankUnit(string area, int? bank) => $"{area}[{bank}]";

    /// <summary>The area and number of a bank unit ("levels[1]"), or null for an overlay or none: the one reader of <see cref="BankUnit"/>.</summary>
    public static (string Area, int Number)? Bank(string? unit) =>
        unit is not null && unit.EndsWith(']') && unit.IndexOf('[') is int at and > 0 && int.TryParse(unit[(at + 1)..^1], out int n) ? (unit[..at], n) : null;

    /// <summary>The bank of an overlay or bank unit ("levels[1]" is 1); -1 for an overlay or none.</summary>
    public static int BankOf(string? unit) => Bank(unit)?.Number ?? -1;
}

/// <summary>
/// An array index or <c>indexed match</c> table read that the emulator checks (M20): the address
/// <see cref="Base"/> plus X (or Y, <see cref="UsesY"/>) must lie in <see cref="First"/> to First + Length - 1.
/// </summary>
public sealed record CheckedIndex(int Base, bool UsesY, bool ZeroPage, int First, IndexCheck Check);

public sealed class AssembledCode(
    List<(int Address, byte[] Bytes)> segments, string listing, List<PlacedSection> sections, List<PlacedArea> areas, AddressRange? variables,
    Dictionary<string, List<(int Address, byte[] Bytes)>> overlays, List<PlacedInstruction> instructions)
{
    /// <summary>Every instruction, in address order within each section, with its source line.</summary>
    public List<PlacedInstruction> Instructions { get; } = instructions;
    /// <summary>The bytes of each overlay module's file, by module name.</summary>
    public Dictionary<string, List<(int Address, byte[] Bytes)>> Overlays { get; } = overlays;
    /// <summary>The bytes of the program file, in address order. Each segment is a run of adjacent sections.</summary>
    public List<(int Address, byte[] Bytes)> Segments { get; } = segments;
    public string Listing { get; } = listing;
    public List<PlacedSection> Sections { get; } = sections;
    public List<PlacedArea> Areas { get; } = areas;
    /// <summary>Where the program's variables (<see cref="Reserve"/>) were placed, or null if there are none.</summary>
    public AddressRange? Variables { get; } = variables;
    /// <summary>Where the area's used bytes ended before the variables' block, which starts at <see cref="Variables"/>.</summary>
    public int VariablesAfter { get; init; }
}

/// <summary>
/// The built-in assembler. Places sections in the program areas (a section never crosses from one
/// area into another), picks zero-page addressing where possible, turns out-of-range branches into an
/// inverted branch over a <c>jmp</c>, and encodes.
/// </summary>
public static partial class Assembler
{
    /// <summary>
    /// Inside the layout: the variables were to start at a place in a page or at an alignment (<see cref="Reserve.Modulus"/>),
    /// but no area has room for that. <see cref="Assemble"/> returns null for it; no caller sees it.
    /// </summary>
    private sealed class NoPageStart() : Exception("The variables do not fit where they were placed in a page");

    private sealed class Slot(AsmItem item)
    {
        public AsmItem Item { get; } = item;
        public AddressingMode Mode;     // final mode for instructions
        public int Address;
        public int Size;
        public bool LongBranch;
    }

    private sealed record Section(int Start, int End, SectionStart Info);

    /// <param name="areas">Where code and data may go, in the order they are filled.</param>
    /// <param name="beforeEncode">
    /// Called once layout is done, before encoding. Variables placed in the <see cref="Reserve"/> block
    /// get their addresses here. Variables without an address during layout are assumed to be
    /// outside zero page.
    /// </param>
    /// <param name="overlayImports">Where each overlay module is imported, for the error when it does not fit.</param>
    /// <param name="overlayAreas">For each overlay module, the area its sections start in (spec 8.5). Each module is laid out from the start of its area.</param>
    /// <param name="variableAreas">
    /// RAM for the variables' block when code is in ROM (the NES): the block goes there, in the first
    /// area with room, instead of after the code in <paramref name="areas"/>. Null or empty: after the code.
    /// </param>
    /// <returns>
    /// The code, or null when the variables were to start at a place in a page or at an alignment (<see cref="Reserve.Modulus"/>)
    /// and no area has room for that, or a <c>jmp (...)</c> vector would then start at $xxFF: the caller places them another way
    /// (review code-quality-backend 15: an outcome, not an exception).
    /// </returns>
    public static AssembledCode? Assemble(IReadOnlyList<AsmItem> items, IReadOnlyList<AddressRange> areas, InstructionSet cpu, Action? beforeEncode = null,
        IReadOnlyDictionary<string, AddressRange>? overlayAreas = null, IReadOnlyDictionary<string, SourcePos>? overlayImports = null,
        IReadOnlyList<AddressRange>? variableAreas = null)
    {
        try { return new Assembly(items, areas, cpu, overlayAreas, overlayImports, variableAreas).Run(beforeEncode); }
        catch (NoPageStart) { return null; }
    }

    /// <summary>
    /// The number of bytes to skip at <paramref name="pc"/> so that no vector at <c>pc + skip + offset</c>
    /// starts at an address ending in $FF. The NMOS 6502 reads the high byte of <c>jmp ($xxFF)</c> from $xx00.
    /// </summary>
    public static int AvoidPageEnd(int pc, IEnumerable<int> offsets)
    {
        var list = offsets.ToList();
        for (int skip = 0; ; skip++)
            if (list.All(o => ((pc + skip + o) & 0xFF) != 0xFF)) return skip;
    }

    /// <summary>The error for a section (null: the variables' block) that fits in no program area, listing the areas and what is in them.</summary>
    private static CompileError NoRoom(string? section, int size, List<AddressRange> free, int[] used, List<string>[] names)
    {
        var areas = free.Select((a, i) =>
        {
            var shown = names[i].Count <= 6 ? string.Join(", ", names[i]) : string.Join(", ", names[i].Take(6)) + $" and {names[i].Count - 6} more";
            return $"{a}: {used[i] - a.First} of {a.Size} bytes used" + (names[i].Count == 0 ? "" : $" ({shown})");
        });
        return new CompileError(new SourcePos(1, 1),
            $"{(section is null ? "The variables need" : $"'{section}' needs")} {size} bytes, and no program area has room for them in one piece " +
            $"(code is never split across areas). Areas: {string.Join("; ", areas)}. Make an area larger, or split the code into smaller functions") { Reason = new AreasFull() };
    }

    /// <summary>Uses a zero-page mode when the operand is a zero-page address and the instruction has one.</summary>
    /// <summary>An error at an instruction a generator emitted also names the generator call (spec 4.1).</summary>
    private static CompileError Generated(Instr i, SourcePos pos, string message) => i.CalledFrom is { } call
        ? new CompileError(pos, CompileTime.Interpreter.WithNote(message, $"generated by the call at {CompileTime.Interpreter.Where(call)}")) { CalledFrom = call }
        : new CompileError(pos, message);

    private static AddressingMode ChooseMode(Instr i, InstructionSet instructions)
    {
        var mode = i.Mode;
        if (i.Operand is LabelOperand) return mode;
        if (mode is AddressingMode.Absolute or AddressingMode.AbsoluteX or AddressingMode.AbsoluteY
            && AddressOf(i.Operand!) is < 0x100)
        {
            var zp = mode switch
            {
                AddressingMode.Absolute => AddressingMode.ZeroPage,
                AddressingMode.AbsoluteX => AddressingMode.ZeroPageX,
                _ => AddressingMode.ZeroPageY,
            };
            if (instructions.TryGet(i.Mnemonic, zp, out _)) return zp;
        }
        if (!instructions.TryGet(i.Mnemonic, mode, out _))
            throw new InvalidOperationException($"Code generator produced {i.Mnemonic} {mode}");
        return mode;
    }

    private static int? AddressOf(Operand op) => op switch
    {
        VarOperand v => v.Variable.Address < 0 ? null : Address(v),
        AddrOperand a => a.Address,
        _ => null,
    };

    private static int Address(VarOperand v)
    {
        if (v.Variable.Address < 0)
            throw new InvalidOperationException($"Variable {v.Variable.QualifiedName} has no address");
        return (v.Variable.Address + v.Offset) & 0xFFFF;   // an array folded with 'i - c' may start below its variable
    }

    private static int Resolve(Label label) =>
        label.Address >= 0 ? label.Address : throw new InvalidOperationException($"Label {label.Name} is not defined");

    private static int OperandValue(Instr i, AddressingMode mode) => i.Operand switch
    {
        null => 0,
        Imm imm => imm.Value & 0xFF,
        ImmLabelByte b => Byte(Resolve(b.Label) + b.Offset, b.High),
        ImmVarByte v => Byte(Address(new VarOperand(v.Variable, v.Offset)), v.High),
        VarOperand v => Address(v),
        AddrOperand a => a.Address,
        LabelOperand l => Resolve(l.Label) + l.Offset,
        _ => throw new InvalidOperationException(),
    };

    /// <summary>The index check of a compiler-generated indexed instruction (never one from an asm block), or null.</summary>
    private static CheckedIndex? Checked(Instr i, AddressingMode mode)
    {
        if (i.Check is { InA: true } inA && i.AsmPos is null) return new CheckedIndex(0, false, false, 0, inA);
        var check = i.Operand switch { VarOperand v => v.Check, LabelOperand l => l.Check, _ => null };
        if (check is null || i.AsmPos is not null
            || mode is not (AddressingMode.AbsoluteX or AddressingMode.AbsoluteY or AddressingMode.ZeroPageX or AddressingMode.ZeroPageY)) return null;
        int at = OperandValue(i, mode);
        return new CheckedIndex(at, mode is AddressingMode.AbsoluteY or AddressingMode.ZeroPageY,
            mode is AddressingMode.ZeroPageX or AddressingMode.ZeroPageY, (at - check.Shift) & 0xFFFF, check);
    }

    private static int Byte(int address, bool high) => high ? (address >> 8) & 0xFF : address & 0xFF;

    /// <summary>Lists one instruction; returns its text and its cycles (base, and whether it can take more).</summary>
    private static (string Text, int Cycles, bool More) ListInstruction(StringBuilder listing, Slot s, Instr i, byte[] encoded, InstructionSet instructions)
    {
        string hex = string.Join(" ", encoded.Take(3).Select(b => b.ToString("X2")));
        string text = s.LongBranch
            ? $"{Instructions.Inverse(i.Mnemonic).ToString().ToLowerInvariant()} *+5 / jmp {i.Operand}"
            : $"{i.Mnemonic.ToString().ToLowerInvariant()} {FormatOperand(i.Operand, s.Mode)}".TrimEnd();
        instructions.TryGet(i.Mnemonic, s.Mode, out var info);
        string cycles = s.LongBranch ? "2+3" : info.Mode == AddressingMode.Relative ? "2+" : info.PageCrossPenalty ? $"{info.Cycles}+" : $"{info.Cycles}";
        listing.AppendLine($"${s.Address:X4}  {hex,-9} {text,-22}; {cycles}");
        return (text, s.LongBranch ? 2 : info.Cycles, s.LongBranch || info.Mode == AddressingMode.Relative || info.PageCrossPenalty);
    }

    private static string FormatOperand(Operand? op, AddressingMode mode)
    {
        string o = op?.ToString() ?? "";
        return mode switch
        {
            AddressingMode.Accumulator => "a",
            AddressingMode.AbsoluteX or AddressingMode.ZeroPageX => $"{o},x",
            AddressingMode.AbsoluteY or AddressingMode.ZeroPageY => $"{o},y",
            AddressingMode.IndirectIndexed => $"({o}),y",
            AddressingMode.IndexedIndirect => $"({o},x)",
            AddressingMode.Indirect => $"({o})",
            _ => o,
        };
    }
}
