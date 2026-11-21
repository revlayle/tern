using Tern.Compiler.Backend;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Tests;

/// <summary>
/// M18 F17: a write to one byte of a variable (<c>inc v+1</c>) leaves what the optimizer knows
/// about its other bytes, so A still holds <c>v</c> after <c>sta v / bcc / inc v+1</c>.
/// Spec 11 (code shapes).
/// </summary>
public class ByteWriteTests
{
    private static readonly VariableSymbol V = new("v", default, TernType.U16, VariableKind.Global, null);
    private static readonly VariableSymbol X = new("x", default, TernType.U8, VariableKind.Global, null);

    private static Instr Abs(Mnemonic m, VariableSymbol v, int offset = 0) => new(m, Absolute, new VarOperand(v, offset));

    private static string Optimize(params AsmItem[] code)
    {
        var list = code.ToList();
        Optimizer.Run(list, new FunctionSymbol("f", default), InstructionSet.Nmos6502);
        return string.Join("|", list.OfType<Instr>().Select(i => $"{i.Mnemonic.ToString().ToLowerInvariant()} {i.Operand}".Trim()));
    }

    [Fact]
    public void A_write_to_the_high_byte_keeps_the_low_byte_known()
    {
        var skip = new Label("skip");
        Assert.Equal("lda #$05|sta v|bcc skip|inc v+1|sta x|rts",
            Optimize(new Instr(Lda, Immediate, new Imm(5)), Abs(Sta, V), new Instr(Bcc, Relative, new LabelOperand(skip)),
                Abs(Inc, V, 1), new LabelDef(skip), Abs(Lda, V), Abs(Sta, X), new Instr(Rts)));
    }

    [Fact]
    public void A_write_to_the_same_byte_forgets_it()
    {
        Assert.Equal("lda #$05|sta v|inc v|lda v|sta x|rts",
            Optimize(new Instr(Lda, Immediate, new Imm(5)), Abs(Sta, V), Abs(Inc, V), Abs(Lda, V), Abs(Sta, X), new Instr(Rts)));
        // An indexed write may reach any byte of the variable.
        Assert.Equal("lda #$05|sta v|ldx #$01|inc v|lda v|sta x|rts",
            Optimize(new Instr(Lda, Immediate, new Imm(5)), Abs(Sta, V), new Instr(Ldx, Immediate, new Imm(1)),
                new Instr(Inc, AbsoluteX, new VarOperand(V)), Abs(Lda, V), Abs(Sta, X), new Instr(Rts)));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_sum_with_carries_reads_the_low_byte_from_a()
    {
        byte[] input = [255, 1, 0, 254, 2, 128, 128, 255, 255, 3];
        var r = Run.Program($$"""
            var values: [u8; 10] = [{{string.Join(", ", input)}}]
            var lows: [u8; 10]
            var sum: u16 = 65000
            fn main() {
                for i in 0..10 {
                    sum += values[i] as u16
                    lows[i] = sum.lo
                }
            }
            """);
        int sum = 65000;
        for (int k = 0; k < input.Length; k++)
        {
            sum = (sum + input[k]) & 0xFFFF;
            Assert.Equal(sum & 0xFF, r.U8("lows", k));
        }
        Assert.Equal(sum, r.U16("sum"));
        // inc sum+1, then lows[i] = sum.lo stores A as it is.
        var main = Listing.Instructions(r);
        Assert.DoesNotContain("lda sum", main);
        Assert.Contains("inc sum+1", main);
    }
}
