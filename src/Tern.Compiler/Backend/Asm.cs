using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using Tern.Cpu;

namespace Tern.Compiler.Backend;

/// <summary>A code address, known after layout.</summary>
public sealed class Label(string name)
{
    public string Name { get; } = name;
    public int Address { get; set; } = -1;
    public override string ToString() => Name;
}

/// <summary>An instruction operand. Variable addresses are known after allocation, labels after layout.</summary>
public abstract record Operand;

/// <summary><c>#value</c></summary>
public sealed record Imm(int Value) : Operand
{
    public override string ToString() => $"#${Value & 0xFF:X2}";
}

/// <summary><c>#label.lo</c> or <c>#label.hi</c>: a byte of a label's address plus <see cref="Offset"/>.</summary>
public sealed record ImmLabelByte(Label Label, bool High, int Offset = 0) : Operand
{
    public override string ToString() => $"#{OffsetText(Label.Name, Offset)}.{(High ? "hi" : "lo")}";

    /// <summary><c>name</c>, or <c>(name+1)</c> with an offset.</summary>
    internal static string OffsetText(string name, int offset) => offset == 0 ? name : $"({name}{offset:+0;-0})";
}

/// <summary><c>#addr.lo</c> or <c>#addr.hi</c>: a byte of a variable's address plus <see cref="Offset"/>.</summary>
public sealed record ImmVarByte(VariableSymbol Variable, bool High, int Offset = 0) : Operand
{
    public override string ToString() => $"#{ImmLabelByte.OffsetText(Variable.QualifiedName, Offset)}.{(High ? "hi" : "lo")}";
}

/// <summary>
/// The table an indexed instruction must stay in, for the emulator's index check (M20; spec 3.2, 5.2).
/// The table starts <see cref="Shift"/> bytes before the operand's address (a folded offset such as
/// <c>a-1,x</c>) and holds <see cref="Length"/> bytes: <see cref="Elements"/> elements, <see cref="Stride"/> bytes apart. For an
/// <c>indexed match</c>, <see cref="MatchLow"/> is its lowest pattern value.
/// </summary>
/// With <see cref="InA"/> the check is on A before an instruction (the index before it is scaled), not on an address.
public sealed record IndexCheck(string Name, int Shift, int Length, int Elements, int Stride = 1, int? MatchLow = null, bool Signed = false, bool InA = false)
{
    /// <summary>The index expression's position: the checks of one access (both bytes, the scaled index) share it.</summary>
    public SourcePos? At { get; init; }
}

/// <summary>A byte of a variable: its address plus <see cref="Offset"/>.</summary>
public sealed record VarOperand(VariableSymbol Variable, int Offset = 0) : Operand
{
    /// <summary>Set on an array index the emulator checks; not part of the operand's identity.</summary>
    public IndexCheck? Check { get; init; }
    public bool Equals(VarOperand? other) => other is not null && Variable == other.Variable && Offset == other.Offset;
    public override int GetHashCode() => HashCode.Combine(Variable, Offset);
    public override string ToString() => Offset == 0 ? Variable.QualifiedName : $"{Variable.QualifiedName}{Offset:+0;-0}";
}

/// <summary>A fixed numeric address.</summary>
public sealed record AddrOperand(int Address) : Operand
{
    public override string ToString() => Address < 0x100 ? $"${Address:X2}" : $"${Address:X4}";
}

public sealed record LabelOperand(Label Label, int Offset = 0) : Operand
{
    /// <summary>Set on a jump table read the emulator checks; not part of the operand's identity.</summary>
    public IndexCheck? Check { get; init; }
    public bool Equals(LabelOperand? other) => other is not null && Label == other.Label && Offset == other.Offset;
    public override int GetHashCode() => HashCode.Combine(Label, Offset);
    public override string ToString() => Offset == 0 ? Label.Name : $"{Label.Name}{Offset:+0;-0}";
}

public abstract record AsmItem;

/// <summary>
/// An instruction. Use the absolute modes for memory operands; the assembler switches to
/// zero-page modes when the address is below $100.
/// </summary>
/// <param name="AsmPos">
/// Set for instructions written in an asm block. The assembler never replaces them (an out-of-range
/// branch is an error at this position instead of a branch over a <c>jmp</c>).
/// </param>
public sealed record Instr(Mnemonic Mnemonic, AddressingMode Mode = AddressingMode.Implied, Operand? Operand = null, SourcePos? AsmPos = null) : AsmItem
{
    /// <summary>Set on the shift that scales a linear array's index: A must be below the element count (M20). Not part of the identity.</summary>
    public IndexCheck? Check { get; init; }
    public bool Equals(Instr? other) => other is not null && Mnemonic == other.Mnemonic && Mode == other.Mode && Equals(Operand, other.Operand) && AsmPos == other.AsmPos;
    public override int GetHashCode() => HashCode.Combine(Mnemonic, Mode, Operand, AsmPos);
}

public sealed record LabelDef(Label Label) : AsmItem;

public sealed record DataBytes(byte[] Bytes) : AsmItem;

/// <summary>One byte per label: the low or high byte of its address plus <see cref="Offset"/> (a jump table).</summary>
public sealed record LabelTable(IReadOnlyList<Label> Labels, bool High, int Offset = 0) : AsmItem;

/// <summary>Zero bytes whose count depends on the address where they start, worked out during layout.</summary>
public sealed record Padding(Func<int, int> SizeAt, string Reason) : AsmItem;

/// <summary>A source line, shown in the listing above the code it produced.</summary>
/// <param name="File">The file name for lines from a module; null for the main file.</param>
public sealed record SourceLine(int Line, string Text, string? File = null) : AsmItem;

public sealed record Comment(string Text) : AsmItem;

/// <summary>
/// Starts a section: the items up to the next <see cref="SectionStart"/> (or <see cref="Reserve"/>) are
/// placed together in one program area, so a function is never split (spec 8.4). A section with
/// <paramref name="FixedAddress"/> goes there instead (<c>data x @ $2000</c>).
/// </summary>
/// <param name="Overlay">The module whose overlay file the section goes into (spec 8.5); it is placed in that module's area.</param>
public sealed record SectionStart(string Name, int? FixedAddress = null, string? Overlay = null) : AsmItem;

/// <summary>
/// Room for the variables the compiler places in the program's areas. The assembler picks the first
/// area with enough space after its code and sets <paramref name="Label"/> to its start; nothing is
/// written to the program file. Must be the last item.
/// </summary>
/// <param name="VectorOffsets">Offsets of <c>jmp (...)</c> vectors in the block; none may start at an address ending in $FF.</param>
/// <param name="PageOffset">Start the block at an address with this low byte: its indexed arrays are placed for that (M18 F12).</param>
public sealed record Reserve(Label Label, int Size, IReadOnlyList<int> VectorOffsets, int? PageOffset = null) : AsmItem;
