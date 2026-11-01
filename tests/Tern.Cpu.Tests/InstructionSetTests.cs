using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Cpu.Tests;

public class InstructionSetTests
{
    [Fact]
    public void Has_all_151_documented_opcodes()
    {
        Assert.Equal(151, InstructionSet.All.Count);
        Assert.Equal(151, InstructionSet.All.Select(o => o.Opcode).Distinct().Count());
        Assert.Equal(56, InstructionSet.All.Select(o => o.Mnemonic).Distinct().Count());
    }

    [Theory]
    [InlineData(Lda, Immediate, 0xA9, 2, 2)]
    [InlineData(Lda, IndirectIndexed, 0xB1, 2, 5)]
    [InlineData(Sta, AbsoluteX, 0x9D, 3, 5)]
    [InlineData(Jmp, Indirect, 0x6C, 3, 5)]
    [InlineData(Jsr, Absolute, 0x20, 3, 6)]
    [InlineData(Inc, AbsoluteX, 0xFE, 3, 7)]
    [InlineData(Ldx, ZeroPageY, 0xB6, 2, 4)]
    [InlineData(Asl, Accumulator, 0x0A, 1, 2)]
    [InlineData(Bne, Relative, 0xD0, 2, 2)]
    [InlineData(Brk, Implied, 0x00, 1, 7)]
    public void Known_opcodes(Mnemonic m, AddressingMode mode, int opcode, int size, int cycles)
    {
        Assert.True(InstructionSet.TryGet(m, mode, out var info));
        Assert.Equal(opcode, info.Opcode);
        Assert.Equal(size, info.Size);
        Assert.Equal(cycles, info.Cycles);
    }

    [Fact]
    public void Stores_never_get_page_cross_penalty()
    {
        Assert.All(InstructionSet.All.Where(o => o.Mnemonic is Sta or Stx or Sty),
            o => Assert.False(o.PageCrossPenalty));
    }
}
