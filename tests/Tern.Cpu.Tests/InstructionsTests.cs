using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Cpu.Tests;

/// <summary>
/// The table of what each instruction does (<see cref="Instructions"/>), from which the checker and
/// the backend take their loads, stores, transfers and branches (review code-quality, theme 1).
/// </summary>
public class InstructionsTests
{
    [Fact]
    public void Every_instruction_of_every_variant_has_a_row()
    {
        foreach (var variant in Enum.GetValues<CpuVariant>())
            foreach (var info in InstructionSet.For(variant).All)
                Assert.Equal(info.Mnemonic, Instructions.Of(info.Mnemonic).Mnemonic);
    }

    [Fact]
    public void Loads_stores_and_transfers_come_from_the_rows()
    {
        var all = Enum.GetValues<Mnemonic>();
        Assert.Equal([(Lda, CpuState.A), (Ldx, CpuState.X), (Ldy, CpuState.Y)],
            all.Where(m => Instructions.Loaded(m) is not null).Select(m => (m, Instructions.Loaded(m)!.Value)));
        Assert.Equal([(Sta, CpuState.A), (Stx, CpuState.X), (Sty, CpuState.Y)],
            all.Where(m => Instructions.Stored(m) is not null).Select(m => (m, Instructions.Stored(m)!.Value)));
        Assert.Equal([(Tax, CpuState.A, CpuState.X), (Tay, CpuState.A, CpuState.Y), (Txa, CpuState.X, CpuState.A), (Tya, CpuState.Y, CpuState.A)],
            all.Where(m => Instructions.Transfer(m) is not null).Select(m => (m, Instructions.Transfer(m)!.Value.From, Instructions.Transfer(m)!.Value.To)));
    }

    [Fact]
    public void Branches_are_the_relative_instructions_and_invert_in_pairs()
    {
        var relative = InstructionSet.Nmos6502.All.Where(o => o.Mode == Relative).Select(o => o.Mnemonic).Order();
        Assert.Equal(relative, Enum.GetValues<Mnemonic>().Where(Instructions.IsBranch).Order());
        foreach (var b in relative)
        {
            Assert.NotEqual(b, Instructions.Inverse(b));
            Assert.Equal(b, Instructions.Inverse(Instructions.Inverse(b)));
        }
        Assert.Equal(Bcs, Instructions.Inverse(Bcc));
        Assert.Equal(Bne, Instructions.Inverse(Beq));
        Assert.Throws<ArgumentException>(() => Instructions.Inverse(Jmp));
    }

    [Theory]
    [InlineData(Asl, Accumulator, false, false, CpuState.A, CpuState.A | CpuState.C | CpuState.N | CpuState.Z)]
    [InlineData(Asl, Absolute, true, true, CpuState.None, CpuState.C | CpuState.N | CpuState.Z)]
    [InlineData(Inc, AbsoluteX, true, true, CpuState.X, CpuState.N | CpuState.Z)]
    [InlineData(Sta, IndirectIndexed, true, false, CpuState.A | CpuState.Y, CpuState.None)]
    [InlineData(Lda, Immediate, false, false, CpuState.None, CpuState.A | CpuState.N | CpuState.Z)]
    [InlineData(Inx, Implied, false, false, CpuState.X, CpuState.X | CpuState.N | CpuState.Z)]
    [InlineData(Jmp, Indirect, true, false, CpuState.None, CpuState.None)]
    [InlineData(Stx, ZeroPageY, false, true, CpuState.X | CpuState.Y, CpuState.None)]
    public void Effects_depend_on_the_mode(Mnemonic m, AddressingMode mode, bool readsMemory, bool writesMemory, CpuState reads, CpuState writes)
    {
        Assert.Equal(readsMemory, Instructions.ReadsMemory(m, mode));
        Assert.Equal(writesMemory, Instructions.WritesMemory(m, mode));
        Assert.Equal((reads, writes), Instructions.Effects(m, mode));
    }

    [Fact]
    public void A_store_through_a_pointer_writes_what_it_points_to()
    {
        Assert.True(Instructions.WritesThroughPointer(Sta, IndirectIndexed));
        Assert.False(Instructions.WritesThroughPointer(Lda, IndirectIndexed));
        Assert.False(Instructions.WritesThroughPointer(Sta, Absolute));
    }

    [Fact]
    public void Each_variant_has_its_own_set()
    {
        Assert.Same(InstructionSet.Nmos6502, InstructionSet.For(CpuVariant.Nmos6502));
        Assert.Same(InstructionSet.Ricoh2A03, InstructionSet.For(CpuVariant.Ricoh2A03));
        Assert.True(InstructionSet.Nmos6502.DecimalMode);
        Assert.False(InstructionSet.Ricoh2A03.DecimalMode);
        Assert.Equal(InstructionSet.Nmos6502.All, InstructionSet.Ricoh2A03.All);
        Assert.Same(InstructionSet.Ricoh2A03, new Cpu6502(new Ram(), CpuVariant.Ricoh2A03).Instructions);
    }
}
