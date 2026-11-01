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

/// <summary>A byte of a variable: its address plus <see cref="Offset"/>.</summary>
public sealed record VarOperand(VariableSymbol Variable, int Offset = 0) : Operand
{
    public override string ToString() => Offset == 0 ? Variable.QualifiedName : $"{Variable.QualifiedName}{Offset:+0;-0}";
}

/// <summary>A fixed numeric address.</summary>
public sealed record AddrOperand(int Address) : Operand
{
    public override string ToString() => Address < 0x100 ? $"${Address:X2}" : $"${Address:X4}";
}

public sealed record LabelOperand(Label Label, int Offset = 0) : Operand
{
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
public sealed record Instr(Mnemonic Mnemonic, AddressingMode Mode = AddressingMode.Implied, Operand? Operand = null, SourcePos? AsmPos = null) : AsmItem;

public sealed record LabelDef(Label Label) : AsmItem;

public sealed record DataBytes(byte[] Bytes) : AsmItem;

/// <summary>A source line, shown in the listing above the code it produced.</summary>
public sealed record SourceLine(int Line, string Text) : AsmItem;

public sealed record Comment(string Text) : AsmItem;
