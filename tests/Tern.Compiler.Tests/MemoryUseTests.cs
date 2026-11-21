using Tern.Compiler.Backend;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Tests;

/// <summary>What an instruction does to memory, the one answer the backend's passes use (review backend 8). Spec 13 (compiler design).</summary>
public class MemoryUseTests
{
    private static readonly FunctionSymbol F = new("f", default);
    private static readonly VarOperand V = new(new VariableSymbol("v", default, TernType.U16, VariableKind.Local, F), 1);

    public static TheoryData<Instr, bool, bool, MemoryTarget> Cases() => new()
    {
        { new Instr(Lda, Absolute, V), true, false, MemoryTarget.Byte },
        { new Instr(Sta, ZeroPage, V), false, true, MemoryTarget.Byte },
        { new Instr(Inc, Absolute, V), true, true, MemoryTarget.Byte },
        { new Instr(Sta, AbsoluteX, V), false, true, MemoryTarget.Element },
        { new Instr(Lda, IndirectIndexed, V), true, false, MemoryTarget.Pointer },
        { new Instr(Sta, IndirectIndexed, V), false, true, MemoryTarget.Pointer },
        { new Instr(Jmp, Indirect, V), false, false, MemoryTarget.Pointer },
        { new Instr(Sta, Absolute, new AddrOperand(0xD020)), false, true, MemoryTarget.Number },
        { new Instr(Sta, Absolute, new LabelOperand(new Label("patch"), 1)), false, true, MemoryTarget.Code },
        { new Instr(Asl, Accumulator), false, false, MemoryTarget.None },
        { new Instr(Lda, Immediate, new Imm(1)), false, false, MemoryTarget.None },
        { new Instr(Jsr, Absolute, new LabelOperand(new Label("g"))), false, false, MemoryTarget.None },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Reads_writes_and_where(Instr i, bool reads, bool writes, MemoryTarget target)
    {
        var use = MemoryUse.Of(i);
        Assert.Equal((reads, writes, target), (use.Reads, use.Writes, use.Target));
    }
}
