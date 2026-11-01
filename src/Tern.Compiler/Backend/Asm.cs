using Tern.Compiler.Semantics;
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

/// <summary><c>#&lt;label</c> or <c>#&gt;label</c>: a byte of a label's address.</summary>
public sealed record ImmLabelByte(Label Label, bool High) : Operand
{
    public override string ToString() => $"#{Label}.{(High ? "hi" : "lo")}";
}

/// <summary>A byte of a variable: its address plus <see cref="Offset"/>.</summary>
public sealed record VarOperand(VariableSymbol Variable, int Offset = 0) : Operand
{
    public override string ToString() => Offset == 0 ? Variable.QualifiedName : $"{Variable.QualifiedName}+{Offset}";
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
public sealed record Instr(Mnemonic Mnemonic, AddressingMode Mode = AddressingMode.Implied, Operand? Operand = null) : AsmItem;

public sealed record LabelDef(Label Label) : AsmItem;

public sealed record DataBytes(byte[] Bytes) : AsmItem;

/// <summary>A source line, shown in the listing above the code it produced.</summary>
public sealed record SourceLine(int Line, string Text) : AsmItem;

public sealed record Comment(string Text) : AsmItem;
