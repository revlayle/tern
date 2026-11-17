using Tern.Compiler.Backend;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Tests;

/// <summary>
/// M18 F3: a store into the function's own local, parameter or temporary that no path reads
/// before it is stored again or the function ends is removed (Optimizer.RemoveDeadStores).
/// </summary>
public class DeadStoreTests
{
    private static readonly FunctionSymbol F = new("f", default);
    private static readonly VariableSymbol T = new("t", default, TernType.U8, VariableKind.Local, F);
    private static readonly VariableSymbol W = new("w", default, TernType.U16, VariableKind.Local, F);
    private static readonly VariableSymbol X = new("x", default, TernType.U8, VariableKind.Global, null);

    private static Instr Imm(Mnemonic m, int v) => new(m, Immediate, new Imm(v));
    private static Instr Abs(Mnemonic m, VariableSymbol v, int offset = 0) => new(m, Absolute, new VarOperand(v, offset));
    private static Instr Rts => new(Mnemonic.Rts);

    private static string Optimize(FunctionSymbol function, params AsmItem[] code)
    {
        var list = code.ToList();
        Optimizer.Run(list, function, InstructionSet.Nmos6502);
        return string.Join("|", list.OfType<Instr>().Select(i => $"{i.Mnemonic.ToString().ToLowerInvariant()} {i.Operand}".Trim()));
    }

    private static string Optimize(params AsmItem[] code) => Optimize(F, code);

    [Fact]
    public void A_store_never_read_goes()
    {
        Assert.Equal("lda #$01|sta x|rts", Optimize(Imm(Lda, 1), Abs(Sta, T), Abs(Sta, X), Rts));
        Assert.Equal("ldx #$01|stx x|rts", Optimize(Imm(Ldx, 1), Abs(Stx, T), Abs(Stx, X), Rts));
        // Stored again before any read (the call keeps the second load of t).
        var g = new Label("g");
        Assert.Equal("lda #$01|sta x|lda #$02|sta f.t|jsr g|ldy f.t|sty x|rts",
            Optimize(Imm(Lda, 1), Abs(Sta, T), Abs(Sta, X), Imm(Lda, 2), Abs(Sta, T), new Instr(Jsr, Absolute, new LabelOperand(g)),
                Abs(Ldy, T), Abs(Sty, X), Rts));
        // The reload becomes unnecessary first (A holds t), then the store is dead.
        Assert.Equal("lda #$07|sta x|rts", Optimize(Imm(Lda, 7), Abs(Sta, T), Abs(Lda, T), Abs(Sta, X), Rts));
    }

    [Fact]
    public void A_store_read_later_stays()
    {
        var g = new Label("g");
        // Read on one path of a branch (the call makes the analysis forget that A held t).
        var skip = new Label("skip");
        Assert.Equal("lda #$01|sta f.t|jsr g|lda x|beq skip|lda f.t|sta x|rts",
            Optimize(Imm(Lda, 1), Abs(Sta, T), new Instr(Jsr, Absolute, new LabelOperand(g)), Abs(Lda, X), new Instr(Beq, Relative, new LabelOperand(skip)),
                Abs(Lda, T), Abs(Sta, X), new LabelDef(skip), Rts));
    }

    [Fact]
    public void Each_byte_is_followed_on_its_own()
    {
        // Only the low byte of w is read.
        var g = new Label("g");
        Assert.Equal("lda #$01|sta f.w|lda #$02|jsr g|lda f.w|sta x|rts",
            Optimize(Imm(Lda, 1), Abs(Sta, W), Imm(Lda, 2), Abs(Sta, W, 1), new Instr(Jsr, Absolute, new LabelOperand(g)), Abs(Lda, W), Abs(Sta, X), Rts));
    }

    [Fact]
    public void Indexed_and_indirect_reads_keep_the_stores()
    {
        var buf = new VariableSymbol("buf", default, TernType.ArrayOf(TernType.U8, 4), VariableKind.Local, F);
        Assert.Equal("lda #$01|sta f.buf+2|ldx x|lda f.buf|sta x|rts",
            Optimize(Imm(Lda, 1), Abs(Sta, buf, 2), Abs(Ldx, X), new Instr(Lda, AbsoluteX, new VarOperand(buf)), Abs(Sta, X), Rts));
        var p = new VariableSymbol("p", default, TernType.U16, VariableKind.Local, F);
        Assert.Equal("lda #$00|sta f.p|lda #$C0|sta f.p+1|ldy #$00|lda f.p|sta x|rts",
            Optimize(Imm(Lda, 0), Abs(Sta, p), Imm(Lda, 0xC0), Abs(Sta, p, 1), Imm(Ldy, 0),
                new Instr(Lda, IndirectIndexed, new VarOperand(p)), Abs(Sta, X), Rts));
    }

    [Fact]
    public void Only_the_functions_own_ordinary_memory()
    {
        var g = new FunctionSymbol("g", default);
        var parameter = new VariableSymbol("n", default, TernType.U8, VariableKind.Parameter, g);
        var taken = new VariableSymbol("a", default, TernType.U8, VariableKind.Local, F) { AddressTaken = true };
        var shaky = new VariableSymbol("v", default, TernType.U8, VariableKind.Local, F) { DeclaredVolatile = true };
        Assert.Equal("lda #$01|sta x|rts", Optimize(Imm(Lda, 1), Abs(Sta, X), Rts));                   // a global
        Assert.Equal("lda #$01|sta g.n|rts", Optimize(Imm(Lda, 1), Abs(Sta, parameter), Rts));       // a callee's parameter
        Assert.Equal("lda #$01|sta f.a|rts", Optimize(Imm(Lda, 1), Abs(Sta, taken), Rts));             // address taken
        Assert.Equal("lda #$01|sta f.v|rts", Optimize(Imm(Lda, 1), Abs(Sta, shaky), Rts));             // volatile
        // The function's own parameter is its memory.
        var own = new VariableSymbol("k", default, TernType.U8, VariableKind.Parameter, F);
        Assert.Equal("lda #$01|sta x|rts", Optimize(Imm(Lda, 1), Abs(Sta, own), Abs(Sta, X), Rts));
    }

    [Fact]
    public void A_store_from_assembly_stays()
    {
        var pos = new SourcePos(1, 1);
        Assert.Equal("lda #$01|sta f.t|rts", Optimize(Imm(Lda, 1), new Instr(Sta, Absolute, new VarOperand(T), pos), Rts));
    }

    [Fact]
    public void A_call_reads_none_of_the_functions_memory()
    {
        var g = new Label("g");
        Assert.Equal("lda #$01|jsr g|rts", Optimize(Imm(Lda, 1), Abs(Sta, T), new Instr(Jsr, Absolute, new LabelOperand(g)), Rts));
        // Read after the call: stays.
        Assert.Equal("lda #$01|sta f.t|jsr g|lda f.t|sta x|rts",
            Optimize(Imm(Lda, 1), Abs(Sta, T), new Instr(Jsr, Absolute, new LabelOperand(g)), Abs(Lda, T), Abs(Sta, X), Rts));
    }

    [Fact]
    public void Rts_and_an_indirect_jump_may_go_to_a_label_of_a_jump_table()
    {
        // A match dispatches with rts to a label named in a table.
        var arm = new Label("arm");
        var table = new Label("table");
        Assert.Equal("lda #$01|sta f.t|lda table|pha|lda table|pha|rts|lda f.t|sta x|rts",
            Optimize(Imm(Lda, 1), Abs(Sta, T), new Instr(Lda, Absolute, new LabelOperand(table)), new Instr(Pha),
                new Instr(Lda, Absolute, new LabelOperand(table)), new Instr(Pha), Rts,
                new LabelDef(arm), Abs(Lda, T), Abs(Sta, X), Rts,
                new LabelDef(table), new LabelTable([arm], false)));
        var vec = new VariableSymbol("vec", default, TernType.U16, VariableKind.Global, null);
        Assert.Equal("lda #$01|sta f.t|jmp vec|lda f.t|sta x|rts",
            Optimize(Imm(Lda, 1), Abs(Sta, T), new Instr(Mnemonic.Jmp, Indirect, new VarOperand(vec)),
                new LabelDef(arm), Abs(Lda, T), Abs(Sta, X), Rts,
                new LabelDef(table), new LabelTable([arm], false)));
    }

    [Fact]
    public void Assembly_calling_its_own_subroutine_is_not_followed()
    {
        var pos = new SourcePos(1, 1);
        var sub = new Label(".sub");
        Assert.Equal("lda #$01|sta f.t|jsr .sub|rts|lda f.t|sta x|rts",
            Optimize(Imm(Lda, 1), Abs(Sta, T), new Instr(Jsr, Absolute, new LabelOperand(sub), pos), new Instr(Mnemonic.Rts, Implied, null, pos),
                new LabelDef(sub), new Instr(Lda, Absolute, new VarOperand(T), pos), new Instr(Sta, Absolute, new VarOperand(X), pos), new Instr(Mnemonic.Rts, Implied, null, pos)));
    }

    // M18 part 3, F7: ordinary globals.

    [Fact]
    public void A_store_into_a_global_stored_again_before_any_read_goes()
    {
        var y = new VariableSymbol("y", default, TernType.U8, VariableKind.Global, null);
        Assert.Equal("lda #$02|sta x|rts", Optimize(Imm(Lda, 1), Abs(Sta, X), Imm(Lda, 2), Abs(Sta, X), Rts));
        // Another global in between changes nothing; on both paths of a branch.
        var skip = new Label("skip");
        Assert.Equal("ldx #$01|lda y|beq skip|ldx #$03|stx y|lda #$02|sta x|rts",
            Optimize(Imm(Ldx, 1), Abs(Stx, X), Abs(Lda, y), new Instr(Beq, Relative, new LabelOperand(skip)), Imm(Ldx, 3), Abs(Stx, X),
                new LabelDef(skip), Abs(Stx, y), Imm(Lda, 2), Abs(Sta, X), Rts));
    }

    [Fact]
    public void A_global_is_read_by_a_call_a_return_a_jump_out_and_the_end_of_the_code()
    {
        var g = new Label("g");
        Assert.Equal("lda #$01|sta x|jsr g|lda #$02|sta x|rts",
            Optimize(Imm(Lda, 1), Abs(Sta, X), new Instr(Jsr, Absolute, new LabelOperand(g)), Imm(Lda, 2), Abs(Sta, X), Rts));
        Assert.Equal("lda #$01|sta x|rts", Optimize(Imm(Lda, 1), Abs(Sta, X), Rts));
        Assert.Equal("lda #$01|sta x|jmp g", Optimize(Imm(Lda, 1), Abs(Sta, X), new Instr(Mnemonic.Jmp, Absolute, new LabelOperand(g))));
        Assert.Equal("lda #$01|sta x|beq g|lda #$02|sta x|rts",
            Optimize(Imm(Lda, 1), Abs(Sta, X), new Instr(Beq, Relative, new LabelOperand(g)), Imm(Lda, 2), Abs(Sta, X), Rts));
        Assert.Equal("lda #$01|sta x", Optimize(Imm(Lda, 1), Abs(Sta, X)));
    }

    [Fact]
    public void A_global_may_be_read_through_a_pointer_or_at_a_number()
    {
        var p = new VariableSymbol("p", default, TernType.U16, VariableKind.Global, null);
        Assert.Equal("lda #$01|sta x|ldy #$00|lda p|lda #$02|sta x|rts",
            Optimize(Imm(Lda, 1), Abs(Sta, X), Imm(Ldy, 0), new Instr(Lda, IndirectIndexed, new VarOperand(p)), Imm(Lda, 2), Abs(Sta, X), Rts));
        Assert.Equal("lda #$01|sta x|lda $0900|lda #$02|sta x|rts",
            Optimize(Imm(Lda, 1), Abs(Sta, X), new Instr(Lda, Absolute, new AddrOperand(0x900)), Imm(Lda, 2), Abs(Sta, X), Rts));
        // A write through a pointer reads nothing.
        Assert.Equal("lda #$01|ldy #$00|sta p|lda #$02|sta x|rts",
            Optimize(Imm(Lda, 1), Abs(Sta, X), Imm(Ldy, 0), new Instr(Sta, IndirectIndexed, new VarOperand(p)), Imm(Lda, 2), Abs(Sta, X), Rts));
    }

    [Fact]
    public void Only_ordinary_globals()
    {
        var shaky = new VariableSymbol("v", default, TernType.U8, VariableKind.Global, null) { DeclaredVolatile = true };
        var taken = new VariableSymbol("a", default, TernType.U8, VariableKind.Global, null) { AddressTaken = true };
        var at = new VariableSymbol("h", default, TernType.U8, VariableKind.Global, null) { FixedAddress = 0xC000 };
        var shared = new VariableSymbol("s", default, TernType.U8, VariableKind.Global, null) { UsedFromTwoRoots = true };
        foreach (var v in new[] { shaky, taken, at, shared })
            Assert.Equal($"lda #$01|sta {v.Name}|lda #$02|sta {v.Name}|rts", Optimize(Imm(Lda, 1), Abs(Sta, v), Imm(Lda, 2), Abs(Sta, v), Rts));
    }

    // ---------- Programs ----------

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Globals_overwritten_before_they_are_read_give_the_same_results()
    {
        byte[] input = [0, 1, 127, 128, 255, 254, 3, 0];
        var r = Run.Program($$"""
            var values: [u8; 8] = [{{string.Join(", ", input)}}]
            var last: u8
            var big: u8
            var seen: u8
            fn note() { seen += last }
            fn main() {
                for i in 0..8 {
                    last = values[i]
                    if last > 100 { big += 1 }
                    last = last + 1
                    note()
                    last = 0
                }
                last = values[7] ^ 255
            }
            """);
        int seen = 0, big = 0;
        foreach (var v in input) { if (v > 100) big++; seen += (byte)(v + 1); }
        Assert.Equal(input[7] ^ 255, r.U8("last"));
        Assert.Equal(big, r.U8("big"));
        Assert.Equal(seen & 0xFF, r.U8("seen"));
        // last = values[i] is overwritten before the call reads it; last = 0 in the next pass or after the loop.
        var main = r.Result.Listing.Split("\nmain:")[1].Split("\nnote:")[0];
        Assert.Equal(2, main.Split("sta last").Length - 1);
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_local_used_only_from_a_is_not_stored()
    {
        byte[] input = [0, 1, 97, 98, 252, 253, 255, 128, 127, 0];
        var r = Run.Program($$"""
            var values: [u8; 10] = [{{string.Join(", ", input)}}]
            var out: [u8; 10]
            var big: u8
            fn main() {
                for i in 0..10 {
                    var t: u8 = values[i] + 3
                    if t > 100 { big += 1 }
                    out[i] = t
                }
            }
            """);
        for (int k = 0; k < input.Length; k++) Assert.Equal((byte)(input[k] + 3), r.U8("out", k));
        Assert.Equal(input.Count(v => (byte)(v + 3) > 100), r.U8("big"));
        Assert.DoesNotContain("sta main.t", r.Result.Listing);
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_value_read_in_the_next_pass_of_a_loop_stays()
    {
        byte[] input = [0, 255, 1, 128, 127, 0, 200, 56];
        var r = Run.Program($$"""
            var values: [u8; 8] = [{{string.Join(", ", input)}}]
            var diffs: [u8; 8]
            fn main() {
                var prev: u8 = 0
                for i in 0..8 {
                    var v: u8 = values[i]
                    diffs[i] = v - prev
                    prev = v
                }
            }
            """);
        byte prev = 0;
        for (int k = 0; k < input.Length; k++)
        {
            Assert.Equal((byte)(input[k] - prev), r.U8("diffs", k));
            prev = input[k];
        }
        Assert.Contains("sta main.prev", r.Result.Listing);
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_local_read_in_a_match_arm_reached_through_the_jump_table_stays()
    {
        var r = Run.Program("""
            var out: [u8; 10]
            fn f(k: u8) -> u8 {
                var t: u8 = k + 100
                indexed match k {
                    0 => { return t }
                    1 => { return t + 1 }
                    2 => { return t + 2 }
                    3 => { return t + 3 }
                    4 => { return t + 4 }
                    5 => { return t + 5 }
                    6 => { return t + 6 }
                    7 => { return t + 7 }
                    9 => { return 0 }
                    _ => { return 0 }
                }
            }
            fn main() {
                for i in 0..10 { out[i] = f(i) }
            }
            """);
        for (int k = 0; k < 10; k++) Assert.Equal(k < 8 ? 100 + 2 * k : 0, r.U8("out", k));
        Assert.Contains("pha", r.Result.Listing);   // the jump table
    }

    [Fact]
    public void A_local_read_by_an_asm_block_stays()
    {
        var r = Run.Program("""
            var out: u8
            fn main() {
                var t: u8 = 42
                asm {
                    lda t
                    sta out
                }
            }
            """);
        Assert.Equal(42, r.U8("out"));
    }

    [Fact]
    public void A_local_read_through_its_address_stays()
    {
        var r = Run.Program("""
            var out: u8
            fn main() {
                var t: u8 = 42
                var p: ptr u8 = addr(t)
                out = p[0]
            }
            """);
        Assert.Equal(42, r.U8("out"));
    }

    // Review M18 part 2, suggestion 3.

    [Fact]
    public void The_end_of_the_code_and_a_jump_out_of_it_read_nothing()
    {
        var other = new Label("other");
        Assert.Equal("lda #$01|jmp other", Optimize(Imm(Lda, 1), Abs(Sta, T), new Instr(Mnemonic.Jmp, Absolute, new LabelOperand(other))));
        // An interrupt handler's body falls into the register restore, which is added after optimizing.
        Assert.Equal("lda #$01", Optimize(Imm(Lda, 1), Abs(Sta, T)));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void An_interrupt_handler_with_locals_runs_beside_main()
    {
        var r = Run.Program("""
            var hn: u8
            var hacc: u8
            var last: u8
            var sum: u16
            interrupt fn h() {
                var t: u8 = hn
                hn += 1
                hacc += t
                var u: u8 = hacc ^ 85
                last = u
            }
            fn main() {
                asm {
                    sei
                    lda #h.lo
                    sta $FFFE
                    lda #h.hi
                    sta $FFFF
                    cli
                }
                for rep in 0..40 {
                    for i in 0..=255 {
                        var v: u8 = i ^ 170
                        sum += v as u16
                    }
                }
                asm { sei }
            }
            """, maxCycles: 4_000_000, irqEvery: 97);
        int calls = r.Interrupts;
        Assert.True(calls > 100);
        int sum = 0;
        for (int rep = 0; rep < 40; rep++) for (int i = 0; i < 256; i++) sum += i ^ 170;
        Assert.Equal(sum & 0xFFFF, r.U16("sum"));
        int acc = 0;
        for (int n = 0; n < calls; n++) acc += n;
        Assert.Equal(calls & 0xFF, r.U8("hn"));
        Assert.Equal(acc & 0xFF, r.U8("hacc"));
        Assert.Equal((acc & 0xFF) ^ 85, r.U8("last"));
        Assert.DoesNotContain("sta h.u", r.Result.Listing);   // u is used only from A
    }
}
