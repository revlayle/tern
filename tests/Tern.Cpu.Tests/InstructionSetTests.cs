using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Cpu.Tests;

public class InstructionSetTests
{
    [Fact]
    public void Has_all_151_documented_opcodes()
    {
        Assert.Equal(151, InstructionSet.Nmos6502.All.Count);
        Assert.Equal(151, InstructionSet.Nmos6502.All.Select(o => o.Opcode).Distinct().Count());
        Assert.Equal(56, InstructionSet.Nmos6502.All.Select(o => o.Mnemonic).Distinct().Count());
    }

    /// <summary>
    /// Base cycles of every opcode, row $x0-$xF per line, from the MOS 6500 programming manual's timing
    /// (also the 64doc table); 0 for the undocumented opcodes. Written apart from <see cref="InstructionSet"/>,
    /// whose column the emulator, the listing and tern bench all read (review code-quality-tools 17).
    /// </summary>
    private static readonly int[][] Cycles =
    [
        [7, 6, 0, 0, 0, 3, 5, 0, 3, 2, 2, 0, 0, 4, 6, 0],   // $0x
        [2, 5, 0, 0, 0, 4, 6, 0, 2, 4, 0, 0, 0, 4, 7, 0],   // $1x
        [6, 6, 0, 0, 3, 3, 5, 0, 4, 2, 2, 0, 4, 4, 6, 0],   // $2x
        [2, 5, 0, 0, 0, 4, 6, 0, 2, 4, 0, 0, 0, 4, 7, 0],   // $3x
        [6, 6, 0, 0, 0, 3, 5, 0, 3, 2, 2, 0, 3, 4, 6, 0],   // $4x
        [2, 5, 0, 0, 0, 4, 6, 0, 2, 4, 0, 0, 0, 4, 7, 0],   // $5x
        [6, 6, 0, 0, 0, 3, 5, 0, 4, 2, 2, 0, 5, 4, 6, 0],   // $6x
        [2, 5, 0, 0, 0, 4, 6, 0, 2, 4, 0, 0, 0, 4, 7, 0],   // $7x
        [0, 6, 0, 0, 3, 3, 3, 0, 2, 0, 2, 0, 4, 4, 4, 0],   // $8x
        [2, 6, 0, 0, 4, 4, 4, 0, 2, 5, 2, 0, 0, 5, 0, 0],   // $9x
        [2, 6, 2, 0, 3, 3, 3, 0, 2, 2, 2, 0, 4, 4, 4, 0],   // $Ax
        [2, 5, 0, 0, 4, 4, 4, 0, 2, 4, 2, 0, 4, 4, 4, 0],   // $Bx
        [2, 6, 0, 0, 3, 3, 5, 0, 2, 2, 2, 0, 4, 4, 6, 0],   // $Cx
        [2, 5, 0, 0, 0, 4, 6, 0, 2, 4, 0, 0, 0, 4, 7, 0],   // $Dx
        [2, 6, 0, 0, 3, 3, 5, 0, 2, 2, 2, 0, 4, 4, 6, 0],   // $Ex
        [2, 5, 0, 0, 0, 4, 6, 0, 2, 4, 0, 0, 0, 4, 7, 0],   // $Fx
    ];

    /// <summary>Reads that take one more cycle when indexing crosses a page: (zp),y, abs,y and abs,x loads, compares and arithmetic.</summary>
    private static readonly int[] PageCrossing =
    [
        0x11, 0x19, 0x1D, 0x31, 0x39, 0x3D, 0x51, 0x59, 0x5D, 0x71, 0x79, 0x7D,
        0xB1, 0xB9, 0xBC, 0xBD, 0xBE, 0xD1, 0xD9, 0xDD, 0xF1, 0xF9, 0xFD,
    ];

    [Fact]
    public void Every_opcode_has_the_datasheets_cycles()
    {
        Assert.Equal(151, Cycles.Sum(row => row.Count(c => c > 0)));
        for (int op = 0; op < 256; op++)
        {
            int expected = Cycles[op >> 4][op & 15];
            var info = InstructionSet.Nmos6502.Decode((byte)op);
            if (expected == 0) { Assert.Null(info); continue; }
            Assert.True(info.HasValue, $"${op:X2} is documented");
            Assert.True(expected == info!.Value.Cycles, $"${op:X2} {info.Value.Mnemonic} {info.Value.Mode}: {info.Value.Cycles} cycles, the datasheet gives {expected}");
            Assert.True(PageCrossing.Contains(op) == info.Value.PageCrossPenalty, $"${op:X2} {info.Value.Mnemonic} {info.Value.Mode}: page-crossing cycle");
        }
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
        Assert.True(InstructionSet.Nmos6502.TryGet(m, mode, out var info));
        Assert.Equal(opcode, info.Opcode);
        Assert.Equal(size, info.Size);
        Assert.Equal(cycles, info.Cycles);
    }

    [Fact]
    public void Stores_never_get_page_cross_penalty()
    {
        Assert.All(InstructionSet.Nmos6502.All.Where(o => o.Mnemonic is Sta or Stx or Sty),
            o => Assert.False(o.PageCrossPenalty));
    }
}
