using Tern.Compiler.Backend;
using Tern.Compiler.Semantics;
using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>The assembler on hand-built items, for cases no source program reaches.</summary>
public class AssemblerTests
{
    [Fact]
    public void A_long_branch_keeps_the_label_offset()
    {
        // Review backend 6: 'bne far+3' out of range becomes 'beq +3 / jmp far+3', not 'jmp far'.
        var far = new Label("far");
        var code = Assembler.Assemble(
            [
                new SectionStart("code"),
                new Instr(Mnemonic.Bne, AddressingMode.Relative, new LabelOperand(far, 3)),
                new DataBytes(new byte[200]),
                new LabelDef(far),
                new DataBytes(new byte[4]),
            ],
            [new AddressRange(0x1000, 0x1FFF)], InstructionSet.Nmos6502);
        var (address, bytes) = Assert.Single(code.Segments);
        Assert.Equal(0x1000, address);
        int target = 0x1000 + 5 + 200 + 3;
        Assert.Equal(new byte[] { 0xF0, 0x03, 0x4C, (byte)target, (byte)(target >> 8) }, bytes[..5]);
    }
}
