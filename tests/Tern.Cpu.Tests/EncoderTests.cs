using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Cpu.Tests;

public class EncoderTests
{
    private static byte[] Enc(Mnemonic m, AddressingMode mode, int operand = 0, int pc = 0x1000)
    {
        var bytes = new List<byte>();
        Encoder.Encode(InstructionSet.Nmos6502, m, mode, operand, pc, bytes);
        return bytes.ToArray();
    }

    [Fact]
    public void Encodes_each_operand_size()
    {
        Assert.Equal(new byte[] { 0x18 }, Enc(Clc, Implied));
        Assert.Equal(new byte[] { 0xA9, 0x42 }, Enc(Lda, Immediate, 0x42));
        Assert.Equal(new byte[] { 0x8D, 0x20, 0xD0 }, Enc(Sta, Absolute, 0xD020));
        Assert.Equal(new byte[] { 0x91, 0xFB }, Enc(Sta, IndirectIndexed, 0xFB));
    }

    [Fact]
    public void Branch_offsets_are_relative_to_next_instruction()
    {
        Assert.Equal(new byte[] { 0xD0, 0x00 }, Enc(Bne, Relative, 0x1002, pc: 0x1000));
        Assert.Equal(new byte[] { 0xD0, 0xFE }, Enc(Bne, Relative, 0x1000, pc: 0x1000)); // branch to self
        Assert.Equal(new byte[] { 0xF0, 0x7F }, Enc(Beq, Relative, 0x1081, pc: 0x1000)); // +127
        Assert.Equal(new byte[] { 0xF0, 0x80 }, Enc(Beq, Relative, 0x0F82, pc: 0x1000)); // -128
    }

    [Fact]
    public void Rejects_out_of_range_branches_and_operands()
    {
        Assert.Throws<EncodingException>(() => Enc(Beq, Relative, 0x1082, pc: 0x1000));
        Assert.Throws<EncodingException>(() => Enc(Beq, Relative, 0x0F81, pc: 0x1000));
        Assert.Throws<EncodingException>(() => Enc(Lda, ZeroPage, 0x100));
        Assert.Throws<EncodingException>(() => Enc(Lda, Immediate, -1));
        Assert.Throws<EncodingException>(() => Enc(Sta, Immediate, 1));
    }

    [Fact]
    public void Every_opcode_round_trips_through_decode()
    {
        foreach (var info in InstructionSet.Nmos6502.All)
        {
            var bytes = Enc(info.Mnemonic, info.Mode, info.Mode == Relative ? 0x1010 : 0x12, pc: 0x1000);
            Assert.Equal(info.Size, bytes.Length);
            Assert.Equal(info, InstructionSet.Nmos6502.Decode(bytes[0]));
        }
    }
}
