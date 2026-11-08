using Tern.Compiler.Backend;
using Tern.Compiler.Semantics;
using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Tests;

/// <summary>
/// M18 part 3, F7 and F15: a loop that stores a variable but never reads its memory keeps it in
/// the register and stores it once at each exit (Optimizer.SinkStoresFromLoops); a jump to the
/// label that follows it goes (Optimizer.RemoveJumpsToNext).
/// </summary>
public class LoopStoreTests
{
    private static string Code(Run r, string function = "main")
    {
        var listing = r.Result.Listing;
        var start = listing.IndexOf($"\n{function}:", StringComparison.Ordinal);
        var code = new List<string>();
        foreach (var l in listing[(start + 1)..].Split('\n').Skip(1).Select(l => l.TrimEnd('\r')))
        {
            if (l.EndsWith(':') && !l.StartsWith(' ') && !l.StartsWith('$') && !l.StartsWith(';') && !l.StartsWith($"{function}.")) break;
            if (l.StartsWith('$') && l.Length > 17) code.Add(l[17..].Split(';')[0].Trim());
        }
        return string.Join("|", code);
    }

    private static byte Crc(IEnumerable<byte> data, byte poly)
    {
        byte crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ poly : crc << 1);
        }
        return crc;
    }

    [Theory]
    [InlineData(0x07)]
    [InlineData(0x00)]
    [InlineData(0xFF)]
    [InlineData(0x80)]
    public void A_crc_keeps_its_value_in_a_for_the_bit_loop(int poly)
    {
        byte[] data = [0, 255, 1, 128, 127, 0x5A, 0xA5, 0, 0];
        var r = Run.Program($$"""
            var bytes: [u8; 9] = [{{string.Join(", ", data)}}]
            var crc: u8
            fn main() {
                for i in 0..9 {
                    crc ^= bytes[i]
                    for _ in 0..8 {
                        if crc & $80 != 0 { crc = (crc << 1) ^ {{poly}} } else { crc = crc << 1 }
                    }
                }
            }
            """);
        Assert.Equal(Crc(data, (byte)poly), r.U8("crc"));
        // One store per byte, after the bit loop; the jmp over the empty else is gone.
        var code = Code(r);
        Assert.Equal(1, code.Split("sta crc").Length - 1);
        Assert.DoesNotContain("jmp", code);
        Assert.Contains("asl a|bcc main.else", code);
        Assert.Matches(@"dex\|bne main\.for\d+\|sta crc\|", code);
    }

    [Fact]
    public void A_break_and_the_end_of_the_loop_share_one_store()
    {
        byte[] values = [3, 9, 0, 200, 255, 7, 1, 2];
        for (byte stop = 0; stop <= 2; stop++)
        {
            var r = Run.Program($$"""
                var values: [u8; 8] = [{{string.Join(", ", values)}}]
                var last: u8
                var stop: u8 = {{(stop == 0 ? 0 : stop == 1 ? 200 : 77)}}
                fn main() {
                    for i in 0..8 {
                        last = values[i] + 1
                        if last == stop { break }
                    }
                }
                """);
            byte target = stop == 0 ? (byte)0 : stop == 1 ? (byte)200 : (byte)77, last = 0;
            foreach (var v in values) { last = (byte)(v + 1); if (last == target) break; }
            Assert.Equal(last, r.U8("last"));
            Assert.Equal(1, Code(r).Split("sta last").Length - 1);
        }
    }

    [Fact]
    public void A_local_in_a_while_loop_with_continue()
    {
        // m is a local read after the loop; continue goes to the while's test.
        var r = Run.Program("""
            var out: [u8; 4]
            fn main() {
                for k in 0..4 {
                    var m: u8 = k
                    var n: u8 = 0
                    while n < 10 {
                        m = m + k + 1
                        n += 1
                        if m > 20 {
                            out[k] = m
                            continue
                        }
                    }
                    out[k] = m ^ 255
                }
            }
            """);
        for (int k = 0; k < 4; k++)
        {
            int m = k, n = 0;
            while (n < 10)
            {
                m = (m + k + 1) & 0xFF;
                n++;
            }
            Assert.Equal(m ^ 255, r.U8("out", k));
        }
    }

    [Fact]
    public void A_loop_that_reads_the_memory_or_calls_keeps_its_stores()
    {
        var r = Run.Program("""
            var total: u8
            var seen: u8
            var arr: [u8; 4] = [1, 2, 3, 4]
            fn look() { seen = total }
            fn main() {
                for i in 0..4 {
                    total = arr[i] + 5
                    look()
                }
                for i in 0..4 {
                    seen += arr[i]
                }
            }
            """);
        Assert.Equal(19, r.U8("seen"));
        Assert.Equal(9, r.U8("total"));
        Assert.Contains("sta total|jsr look", Code(r));
        Assert.Contains("adc seen|sta seen|inx", Code(r));
    }

    [Fact]
    public void A_global_used_by_main_and_a_handler_is_stored_in_the_loop()
    {
        var r = Run.Program("""
            var shared: u8
            var copy: u8
            interrupt fn h() { copy = shared }
            fn main() {
                asm {
                    sei
                    lda #h.lo
                    sta $FFFE
                    lda #h.hi
                    sta $FFFF
                    cli
                }
                for i in 0..200 {
                    shared = i
                }
                asm { sei }
            }
            """, irqEvery: 50);
        Assert.Equal(199, r.U8("shared"));
        Assert.True(r.Interrupts > 5);
        Assert.Matches(@"stx shared\|inx|sta shared", Code(r));
    }

    [Fact]
    public void A_global_read_through_its_address_in_the_loop_keeps_its_stores()
    {
        var r = Run.Program("""
            var last: u8
            var sum: u8
            fn main() {
                var p: ptr u8 = addr(last) as ptr u8
                for i in 0..4 {
                    last = i + 1
                    sum += p[0]
                }
            }
            """);
        Assert.Equal(10, r.U8("sum"));
        Assert.Contains("sta last", Code(r).Split("bne")[0]);
    }

    [Fact]
    public void No_register_holds_the_value_at_the_exit()
    {
        // A is overwritten after the store, so the store stays.
        var r = Run.Program("""
            var arr: [u8; 6] = [5, 1, 250, 0, 9, 8]
            var a: u8
            var b: u8
            fn main() {
                for i in 0..6 {
                    a = arr[i] << 1
                    b = arr[i] ^ 3
                }
            }
            """);
        Assert.Equal(16, r.U8("a"));
        Assert.Equal(11, r.U8("b"));
        // b's store is sunk (A holds it at the exit), a's stays in the loop.
        Assert.Matches(@"sta a\|.*bne main\.for\d+\|sta b", Code(r));
    }

    // ---------- Hand-written code ----------

    private static readonly FunctionSymbol F = new("f", default);
    private static readonly VariableSymbol V = new("v", default, TernType.U8, VariableKind.Global, null);

    private static string Optimize(params AsmItem[] code)
    {
        var list = code.ToList();
        Optimizer.Run(list, F);
        return string.Join("|", list.Select(i => i switch
        {
            Instr n => $"{n.Mnemonic.ToString().ToLowerInvariant()} {n.Operand}".Trim(),
            LabelDef d => d.Label + ":",
            _ => "",
        }).Where(s => s != ""));
    }

    private static Instr I(Mnemonic m, int v) => new(m, Immediate, new Imm(v));
    private static Instr To(Mnemonic m, Label l) => new(m, m == Mnemonic.Jmp || m == Jsr ? Absolute : Relative, new LabelOperand(l));

    [Fact]
    public void The_store_goes_after_the_labels_only_the_loop_jumps_to()
    {
        var top = new Label("top");
        var brk = new Label("brk");
        var next = new Label("next");
        var g = new Label("g");
        // next is also reached from outside the loop, so the store goes between brk and next.
        Assert.Equal("ldx #$03|lda #$00|beq next|top:|clc|adc #$01|cmp #$05|beq brk|dex|bne top|brk:|sta v|next:|jsr g|rts",
            Optimize(I(Ldx, 3), I(Lda, 0), To(Beq, next),
                new LabelDef(top), new Instr(Clc), I(Adc, 1), new Instr(Sta, Absolute, new VarOperand(V)), I(Cmp, 5), To(Beq, brk),
                new Instr(Dex), To(Bne, top),
                new LabelDef(brk), new LabelDef(next), To(Jsr, g), new Instr(Rts)));
    }

    [Fact]
    public void A_jump_to_a_label_outside_gets_its_own_store()
    {
        var top = new Label("top");
        var away = new Label("away");
        var g = new Label("g");
        Assert.Equal("ldx #$03|top:|txa|cmp #$01|beq away|dex|bne top|stx v|jsr g|rts|away:|sta v|jsr g|rts",
            Optimize(I(Ldx, 3), new LabelDef(top), new Instr(Txa), new Instr(Sta, Absolute, new VarOperand(V)), I(Cmp, 1), To(Beq, away),
                new Instr(Dex), new Instr(Stx, Absolute, new VarOperand(V)), To(Bne, top), To(Jsr, g), new Instr(Rts),
                new LabelDef(away), To(Jsr, g), new Instr(Rts)));
    }

    [Fact]
    public void A_read_through_a_pointer_or_a_call_in_the_loop_keeps_a_globals_stores()
    {
        var top = new Label("top");
        var g = new Label("g");
        var p = new VariableSymbol("p", default, TernType.U16, VariableKind.Global, null);
        Assert.Equal("ldx #$03|top:|stx v|lda p|dex|bne top|jsr g|rts",
            Optimize(I(Ldx, 3), new LabelDef(top), new Instr(Stx, Absolute, new VarOperand(V)), new Instr(Lda, IndirectIndexed, new VarOperand(p)),
                new Instr(Dex), To(Bne, top), To(Jsr, g), new Instr(Rts)));
        Assert.Equal("ldx #$03|top:|stx v|jsr g|dex|bne top|rts",
            Optimize(I(Ldx, 3), new LabelDef(top), new Instr(Stx, Absolute, new VarOperand(V)), To(Jsr, g),
                new Instr(Dex), To(Bne, top), new Instr(Rts)));
    }

    [Fact]
    public void A_jump_to_the_next_label_goes()
    {
        var a = new Label("a");
        var b = new Label("b");
        Assert.Equal("lda #$01|a:|b:|sta v|rts",
            Optimize(new Instr(Lda, Immediate, new Imm(1)), new Instr(Mnemonic.Jmp, Absolute, new LabelOperand(b)), new LabelDef(a), new LabelDef(b),
                new Instr(Sta, Absolute, new VarOperand(V)), new Instr(Rts)));
        Assert.Equal("lda #$01|a:|sta v|rts",
            Optimize(new Instr(Lda, Immediate, new Imm(1)), new Instr(Bne, Relative, new LabelOperand(a)), new LabelDef(a),
                new Instr(Sta, Absolute, new VarOperand(V)), new Instr(Rts)));
    }
}
