using Tern.Compiler.Backend;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Tests;

/// <summary>
/// M18 F10 (review M18, finding 2): Optimizer.RemoveDeadLoads, run on hand-written code so that each
/// rule is reached exactly: a load whose register is loaded again before it is read goes; what
/// rts returns, fixed and volatile memory, branches and calls keep it.
/// </summary>
public class DeadLoadTests
{
    private static readonly VariableSymbol X = new("x", default, TernType.U8, VariableKind.Global, null);
    private static readonly VariableSymbol Reg = new("reg", default, TernType.U8, VariableKind.Global, null) { FixedAddress = 0xC000 };
    private static readonly VariableSymbol Flags = new("flags", default, TernType.U8, VariableKind.Global, null) { DeclaredVolatile = true };

    private static Instr Imm(Mnemonic m, int v) => new(m, Immediate, new Imm(v));
    private static Instr Abs(Mnemonic m, VariableSymbol v) => new(m, Absolute, new VarOperand(v));

    /// <summary>The instructions after optimizing <paramref name="code"/> as the body of a function returning in <paramref name="returns"/>.</summary>
    private static string Optimize(Register? returns, params AsmItem[] code)
    {
        var function = new FunctionSymbol("f", default) { ReturnRegister = returns };
        var list = code.ToList();
        Optimizer.Run(list, function, InstructionSet.Nmos6502);
        return string.Join("|", list.OfType<Instr>().Select(i => $"{i.Mnemonic.ToString().ToLowerInvariant()} {i.Operand}".Trim()));
    }

    [Fact]
    public void A_load_overwritten_before_it_is_read_goes()
    {
        Assert.Equal("lda #$02|sta x|rts", Optimize(null, Imm(Lda, 1), Imm(Lda, 2), Abs(Sta, X), new Instr(Rts)));
        Assert.Equal("ldx #$05|stx x|rts", Optimize(null, Imm(Ldx, 1), Imm(Ldx, 5), Abs(Stx, X), new Instr(Rts)));
        // A load of ordinary memory goes too.
        Assert.Equal("lda #$02|sta x|rts", Optimize(null, Abs(Lda, X), Imm(Lda, 2), Abs(Sta, X), new Instr(Rts)));
    }

    [Theory]
    [InlineData(Register.A, "lda #$05|rts")]
    [InlineData(Register.AX, "ldx #$00|lda #$05|rts")]
    [InlineData(Register.Zero, "ldx #$00|lda #$05|rts")]      // N and Z are the result
    [InlineData(Register.Negative, "ldx #$00|lda #$05|rts")]
    [InlineData(Register.X, "ldx #$00|rts")]
    [InlineData(Register.Carry, "rts")]
    [InlineData(null, "rts")]
    public void Rts_reads_only_the_registers_it_returns(Register? returns, string expected) =>
        Assert.Equal(expected, Optimize(returns, Imm(Ldx, 0), Imm(Lda, 5), new Instr(Rts)));

    [Fact]
    public void Fixed_address_and_volatile_loads_stay()
    {
        Assert.Equal("lda reg|lda #$02|sta x|rts", Optimize(null, Abs(Lda, Reg), Imm(Lda, 2), Abs(Sta, X), new Instr(Rts)));
        Assert.Equal("lda flags|lda #$02|sta x|rts", Optimize(null, Abs(Lda, Flags), Imm(Lda, 2), Abs(Sta, X), new Instr(Rts)));
        Assert.Equal("lda reg|rts", Optimize(null, Abs(Lda, Reg), new Instr(Rts)));
    }

    [Fact]
    public void A_branch_or_a_call_ends_the_search()
    {
        var skip = new Label("skip");
        Assert.Equal("lda #$01|beq skip|lda #$02|sta x|rts",
            Optimize(null, Imm(Lda, 1), new Instr(Beq, Relative, new LabelOperand(skip)), Imm(Lda, 2), new LabelDef(skip), Abs(Sta, X), new Instr(Rts)));
        var g = new Label("g");
        Assert.Equal("lda #$01|jsr g|lda #$02|sta x|rts",
            Optimize(null, Imm(Lda, 1), new Instr(Jsr, Absolute, new LabelOperand(g)), Imm(Lda, 2), Abs(Sta, X), new Instr(Rts)));
    }

    [Fact]
    public void A_load_read_through_an_index_or_by_flags_stays()
    {
        var arr = new VariableSymbol("arr", default, TernType.ArrayOf(TernType.U8, 4), VariableKind.Global, null);
        // ldx #1 is read by lda arr,x before ldx #5.
        Assert.Equal("ldx #$01|lda arr|ldx #$05|stx x|sta arr|rts",
            Optimize(null, Imm(Ldx, 1), new Instr(Lda, AbsoluteX, new VarOperand(arr)), Imm(Ldx, 5), Abs(Stx, X), Abs(Sta, arr), new Instr(Rts)));
    }

    [Fact]
    public void An_indexed_load_of_ordinary_memory_goes_but_not_through_a_pointer_or_from_volatile_memory()
    {
        // M18 F18: the copy's first load (lda values,x) dies once the copy is read from the array.
        var arr = new VariableSymbol("arr", default, TernType.ArrayOf(TernType.U8, 4), VariableKind.Global, null);
        Assert.Equal("lda #$02|sta x|rts", Optimize(null, new Instr(Lda, AbsoluteX, new VarOperand(arr)), Imm(Lda, 2), Abs(Sta, X), new Instr(Rts)));
        var ports = new VariableSymbol("ports", default, TernType.ArrayOf(TernType.U8, 4), VariableKind.Global, null) { DeclaredVolatile = true };
        Assert.Equal("lda ports|lda #$02|sta x|rts", Optimize(null, new Instr(Lda, AbsoluteX, new VarOperand(ports)), Imm(Lda, 2), Abs(Sta, X), new Instr(Rts)));
        var p = new VariableSymbol("p", default, TernType.U16, VariableKind.Global, null);
        Assert.Equal("lda p|lda #$02|sta x|rts", Optimize(null, new Instr(Lda, IndirectIndexed, new VarOperand(p)), Imm(Lda, 2), Abs(Sta, X), new Instr(Rts)));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_function_result_in_A_survives_in_a_program()
    {
        var r = Run.Program("""
            var out: u8
            var t: u8 = 4
            fn f() -> u8 {
                t = 9
                return 7
            }
            fn main() { out = f() + t }
            """);
        Assert.Equal(16, r.U8("out"));
        Assert.Contains("lda #$07", Listing.Instructions(r, "f"));
    }
}
