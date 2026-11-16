using Tern.Compiler.Backend;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Tests;

/// <summary>
/// M18 F5: after <c>sta e,x</c> (or <c>lda e,x</c>) the optimizer knows the register holds the
/// element, until the index register or the array changes.
/// </summary>
public class ElementValueTests
{
    private static readonly VariableSymbol Arr = new("arr", default, TernType.ArrayOf(TernType.U8, 8), VariableKind.Global, null);
    private static readonly VariableSymbol Brr = new("brr", default, TernType.ArrayOf(TernType.U8, 8), VariableKind.Global, null);
    private static readonly VariableSymbol Out = new("out", default, TernType.U8, VariableKind.Global, null);

    private static Instr Imm(Mnemonic m, int v) => new(m, Immediate, new Imm(v));
    private static Instr X(Mnemonic m, VariableSymbol v, int offset = 0) => new(m, AbsoluteX, new VarOperand(v, offset));
    private static Instr Abs(Mnemonic m, VariableSymbol v, int offset = 0) => new(m, Absolute, new VarOperand(v, offset));
    private static Instr I(Mnemonic m) => new(m);

    private static string Optimize(params AsmItem[] code)
    {
        var list = code.ToList();
        Optimizer.Run(list, new FunctionSymbol("f", default), InstructionSet.Nmos6502);
        return string.Join("|", list.OfType<Instr>().Select(i => $"{i.Mnemonic.ToString().ToLowerInvariant()} {i.Operand}".Trim()));
    }

    [Fact]
    public void The_element_just_stored_or_loaded_is_not_loaded_again()
    {
        Assert.Equal("ldx out|lda #$05|sta arr|cmp #$03|sta out|rts",
            Optimize(Abs(Ldx, Out), Imm(Lda, 5), X(Sta, Arr), X(Lda, Arr), Imm(Cmp, 3), X(Lda, Arr), Abs(Sta, Out), I(Rts)));
        // Another array's write keeps it.
        Assert.Equal("ldx out|lda arr|sta brr|sta out|rts",
            Optimize(Abs(Ldx, Out), X(Lda, Arr), X(Sta, Brr), X(Lda, Arr), Abs(Sta, Out), I(Rts)));
        // In Y: lda arr,x becomes tya.
        Assert.Equal("ldx out|ldy arr|lda #$01|sta brr|tya|sta out|rts",
            Optimize(Abs(Ldx, Out), new Instr(Ldy, AbsoluteX, new VarOperand(Arr)), Imm(Lda, 1), X(Sta, Brr), X(Lda, Arr), Abs(Sta, Out), I(Rts)));
    }

    [Fact]
    public void A_change_of_the_index_or_the_array_forgets_it()
    {
        // inx, tax, ldx
        Assert.Equal("ldx out|lda arr|sta brr|inx|lda arr|sta out|rts",
            Optimize(Abs(Ldx, Out), X(Lda, Arr), Abs(Sta, Brr), I(Inx), X(Lda, Arr), Abs(Sta, Out), I(Rts)));
        Assert.Equal("ldx out|lda arr|tax|lda arr|sta out|rts",
            Optimize(Abs(Ldx, Out), X(Lda, Arr), I(Tax), X(Lda, Arr), Abs(Sta, Out), I(Rts)));
        // A write to any byte of the array: absolute, through another index, or through a pointer.
        Assert.Equal("ldx out|lda arr|sta brr|inc arr+3|lda arr|sta out|rts",
            Optimize(Abs(Ldx, Out), X(Lda, Arr), Abs(Sta, Brr), Abs(Inc, Arr, 3), X(Lda, Arr), Abs(Sta, Out), I(Rts)));
        Assert.Equal("ldx out|ldy out|lda arr|sta brr|lda #$00|sta arr|lda arr|sta out|rts",
            Optimize(Abs(Ldx, Out), Abs(Ldy, Out), X(Lda, Arr), Abs(Sta, Brr), Imm(Lda, 0), new Instr(Sta, AbsoluteY, new VarOperand(Arr)), X(Lda, Arr), Abs(Sta, Out), I(Rts)));
        var p = new VariableSymbol("p", default, TernType.U16, VariableKind.Global, null);
        Assert.Equal("ldx out|ldy #$00|lda arr|sta p|lda arr|sta out|rts",
            Optimize(Abs(Ldx, Out), Imm(Ldy, 0), X(Lda, Arr), new Instr(Sta, IndirectIndexed, new VarOperand(p)), X(Lda, Arr), Abs(Sta, Out), I(Rts)));
        // A call.
        var g = new Label("g");
        Assert.Equal("ldx out|lda arr|sta brr|jsr g|lda arr|sta out|rts",
            Optimize(Abs(Ldx, Out), X(Lda, Arr), Abs(Sta, Brr), new Instr(Jsr, Absolute, new LabelOperand(g)), X(Lda, Arr), Abs(Sta, Out), I(Rts)));
    }

    [Fact]
    public void Volatile_and_fixed_arrays_are_read_again()
    {
        var ports = new VariableSymbol("ports", default, TernType.ArrayOf(TernType.U8, 8), VariableKind.Global, null) { DeclaredVolatile = true };
        Assert.Equal("ldx out|lda ports|lda ports|sta out|rts",
            Optimize(Abs(Ldx, Out), X(Lda, ports), X(Lda, ports), Abs(Sta, Out), I(Rts)));
        var screen = new VariableSymbol("screen", default, TernType.ArrayOf(TernType.U8, 8), VariableKind.Global, null) { FixedAddress = 0x0400 };
        Assert.Equal("ldx out|lda #$01|sta screen|lda screen|sta out|rts",
            Optimize(Abs(Ldx, Out), Imm(Lda, 1), X(Sta, screen), X(Lda, screen), Abs(Sta, Out), I(Rts)));
    }

    [Fact]
    public void An_element_updated_in_place_is_tested_from_a()
    {
        byte[] xs = [7, 8, 9, 200, 201, 255, 0, 100];
        sbyte[] ds = [1, -1, -2, 1, -1, 1, -1, 127];
        var r = Run.Program($$"""
            var xs: [u8; 8] = [{{string.Join(", ", xs)}}]
            var ds: [i8; 8] = [{{string.Join(", ", ds)}}]
            var hits: [u8; 8]
            fn main() {
                for _ in 0..3 {
                    for i in 0..8 {
                        xs[i] += ds[i] as u8
                        if xs[i] < 8 || xs[i] > 200 {
                            ds[i] = -ds[i]
                            hits[i] += 1
                        }
                    }
                }
            }
            """);
        var x = xs.ToArray();
        var d = ds.ToArray();
        var hits = new int[8];
        for (int pass = 0; pass < 3; pass++)
            for (int i = 0; i < 8; i++)
            {
                x[i] = (byte)(x[i] + (byte)d[i]);
                if (x[i] < 8 || x[i] > 200)
                {
                    d[i] = (sbyte)-d[i];
                    hits[i]++;
                }
            }
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(x[i], r.U8("xs", i));
            Assert.Equal(d[i], r.I8("ds", i));
            Assert.Equal(hits[i], r.U8("hits", i));
        }
        // The tests read the new x from A: xs[i] is loaded once, before the add.
        var code = Listing.Code(r);
        Assert.Contains("sta xs,x|cmp #$08", code);
        Assert.Single(code.Split('|'), i => i == "lda xs,x");
    }
}
