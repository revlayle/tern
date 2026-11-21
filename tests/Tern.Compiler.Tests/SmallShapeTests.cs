namespace Tern.Compiler.Tests;

/// <summary>M18 F10: small code shapes, each checked against C# at edge values and in the listing.</summary>
public class SmallShapeTests
{
    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(254)]
    [InlineData(255)]
    public void Adding_two_to_a_byte_in_memory_is_two_inc(int x)
    {
        var r = Run.Program($$"""
            var a: u8 = {{x}}
            var b: u8 = {{x}}
            var s: i8 = {{(sbyte)x}}
            var t: i8 = {{(sbyte)x}}
            volatile var v: u8 = {{x}}
            var w: u16 = {{x}}
            var c: u8 = {{x}}
            var e: u8
            fn main() {
                a += 2
                b -= 2
                s += 2
                t -= 2
                v += 2
                w += 2
                c += 3
                e = a
            }
            """);
        Assert.Equal((byte)(x + 2), r.U8("a"));
        Assert.Equal((byte)(x - 2), r.U8("b"));
        Assert.Equal((sbyte)(x + 2), r.I8("s"));
        Assert.Equal((sbyte)(x - 2), r.I8("t"));
        Assert.Equal((byte)(x + 2), r.U8("v"));
        Assert.Equal((ushort)(x + 2), r.U16("w"));
        Assert.Equal((byte)(x + 3), r.U8("c"));
        Assert.Equal((byte)(x + 2), r.U8("e"));
        // The volatile byte, the u16 and 3 keep the add.
        Assert.StartsWith("inc a|inc a|dec b|dec b|inc s|inc s|dec t|dec t|lda v|clc|adc #$02|sta v|", Listing.Code(r));
        Assert.Contains("adc #$03", Listing.Code(r));
    }

    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(0xFEFF)]
    [InlineData(0xFF00)]
    [InlineData(0xFFFF)]
    public void Adding_a_multiple_of_256_changes_the_high_byte_only(int w)
    {
        var r = Run.Program($$"""
            var a: u16 = {{w}}
            var b: u16 = {{w}}
            var c: u16 = {{w}}
            var d: u16 = {{w}}
            var p: ptr u8 = {{w}}
            fn main() {
                a += 256
                b -= 256
                c += $0300
                d -= $FF00
                p += 256
            }
            """);
        Assert.Equal((ushort)(w + 256), r.U16("a"));
        Assert.Equal((ushort)(w - 256), r.U16("b"));
        Assert.Equal((ushort)(w + 0x300), r.U16("c"));
        Assert.Equal((ushort)(w - 0xFF00), r.U16("d"));
        Assert.Equal((ushort)(w + 256), r.U16("p"));
        Assert.Equal("inc a+1|dec b+1|lda c+1|clc|adc #$03|sta c+1|lda d+1|sec|sbc #$FF|sta d+1|inc p+1|rts", Listing.Code(r));
    }

    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(255)]
    public void An_element_is_incremented_in_place(int v)
    {
        var r = Run.Program($$"""
            var a: [u8; 8] = [{{v}}, {{v}}, {{v}}, {{v}}, {{v}}, {{v}}, {{v}}, {{v}}]
            var i: u8 = 3
            fn main() {
                a[i] += 1
                a[i - 1] -= 1
                for k in 5..7 { a[k + 1] += 1 }
            }
            """);
        byte[] want = [.. Enumerable.Repeat((byte)v, 8)];
        want[3]++; want[2]--; want[6]++; want[7]++;
        for (int k = 0; k < 8; k++) Assert.Equal(want[k], r.U8("a", k));
        Assert.Contains("ldx i|inc a,x|dec a-1,x", Listing.Code(r));
        Assert.Contains("inc a+1,x", Listing.Code(r));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void An_element_indexed_by_y_is_incremented_through_a()
    {
        // inc has no abs,y form: with X held by a loop, a[j] += 1 adds in A.
        var r = Run.Program("""
            var a: [u8; 8]
            var b: [u8; 8]
            fn main() {
                var j: u8 = 2
                for i in 0..4 using X {
                    b[i] = 1
                    a[j] += 1
                }
            }
            """);
        Assert.Equal(4, r.U8("a", 2));
        Assert.DoesNotContain("inc a,y", r.Result.Listing);
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_value_in_X_is_transferred_instead_of_loaded()
    {
        var r = Run.Program("""
            var a: [u8; 4]
            var n: u8 = 2
            var m: u8
            fn main() {
                a[n] = 5
                m = n << 1
            }
            """);
        Assert.Equal(5, r.U8("a", 2));
        Assert.Equal(4, r.U8("m"));
        Assert.Equal("lda #$05|ldx n|sta a,x|txa|asl a|sta m|rts", Listing.Code(r));
    }

    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void A_constant_one_away_from_y_is_iny_or_dey(int v)
    {
        var r = Run.Program($$"""
            var buf: [u8; 8]
            var v: u8 = {{v}}
            fn main() {
                var p: ptr u8 = addr(buf)
                p[1] = v
                p[2] = v
                p[3] = v
                p[2] = v + 1
            }
            """);
        Assert.Equal([0, (byte)v, (byte)(v + 1), (byte)v, 0], new[] { 0, 1, 2, 3, 4 }.Select(k => r.U8("buf", k)));
        var code = Listing.Code(r);
        Assert.Contains("ldy #$01", code);
        Assert.DoesNotContain("ldy #$02", code);
        Assert.DoesNotContain("ldy #$03", code);
        Assert.Contains("dey", code);
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Y_is_known_to_be_zero_after_a_loop_over_256_values()
    {
        var r = Run.Program("""
            var buf: [u8; 256]
            fn main() {
                var p: ptr u8 = addr(buf)
                for i in 0..=255 using Y { p[i] = i }
                p[0] = 99
            }
            """);
        Assert.Equal(99, r.U8("buf", 0));
        Assert.Equal(255, r.U8("buf", 255));
        Assert.Equal(1, Listing.Code(r).Split('|').Count(s => s == "ldy #$00"));
    }

    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void A_is_known_to_be_zero_where_a_test_for_zero_passes(int x)
    {
        var r = Run.Program($$"""
            var x: u8 = {{x}}
            var y: u8 = 7
            fn main() {
                if x == 0 { y = 0 }
            }
            """);
        Assert.Equal(x == 0 ? 0 : 7, r.U8("y"));
        Assert.Equal("lda x|bne main.endif0|sta y|rts", Listing.Code(r));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Negation_subtracts_from_zero()
    {
        int[] values = [0, 1, 2, 127, 128, 129, 254, 255];
        var r = Run.Program($$"""
            var a: [u8; 8] = [{{string.Join(", ", values)}}]
            var n: [u8; 8]
            var s: [i8; 8]
            fn main() {
                for i in 0..8 {
                    var m: u8 = a[i]
                    n[i] = -m
                    s[i] = -(a[i] as i8)
                }
            }
            """);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal((byte)-values[i], r.U8("n", i));
            Assert.Equal((sbyte)-(sbyte)values[i], r.I8("s", i));
        }
        Assert.Contains("lda #$00|sec|sbc a,x", Listing.Code(r));   // m read from a[i] (F18)
        Assert.DoesNotContain("eor #$FF", Listing.Code(r));
    }

    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(65535)]
    public void A_16_bit_test_for_zero_needs_no_cmp(int w)
    {
        var r = Run.Program($$"""
            var w: u16 = {{w}}
            var f: u8
            fn main() {
                if w == 0 { f |= 1 }
                if w != 0 { f |= 2 }
                if 0 == w { f |= 4 }
                if w != $0100 { f |= 8 }
            }
            """);
        Assert.Equal((w == 0 ? 5 : 2) | (w != 256 ? 8 : 0), r.U8("f"));
        Assert.DoesNotContain("cmp #$00", Listing.Code(r));
        Assert.Contains("cmp #$01", Listing.Code(r));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void No_clc_when_the_carry_is_known_clear()
    {
        var r = Run.Program("""
            var a: [u8; 32]
            var b: [u8; 32]
            var c: [u8; 32]
            fn main() {
                for i in 0..16 {
                    a[i] = (i << 3) + i + 10      // i <= 15: no carry out of either shift or the first add
                    b[i] = (i << 4) + i + 250     // 240 + 15 = 255: still no carry before the last add
                }
                for i in 0..17 { c[i] = (i << 4) + 1 }   // 16 << 4 carries: clc stays
            }
            """);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal((byte)((i << 3) + i + 10), r.U8("a", i));
            Assert.Equal((byte)((i << 4) + i + 250), r.U8("b", i));
        }
        for (int i = 0; i < 17; i++) Assert.Equal((byte)((i << 4) + 1), r.U8("c", i));
        Assert.Equal(1, Listing.Code(r).Split('|').Count(s => s == "clc"));
    }

    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(254)]
    public void A_counter_with_a_variable_end_keeps_its_clc(int last)
    {
        var r = Run.Program($$"""
            var a: [u8; 256]
            var n: u8 = {{last + 1}}
            fn main() {
                for i in 0..n { a[i] = (i << 1) + 1 }
            }
            """);
        Assert.Equal((byte)((last << 1) + 1), r.U8("a", last));
        Assert.Contains("clc", Listing.Code(r));
    }

    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData(0x0100)]
    [InlineData(0x0005)]
    [InlineData(0x01FF)]
    public void A_byte_of_a_parameter_is_a_loop_bound_in_place(int n)
    {
        var r = Run.Program($$"""
            var buf: [u8; 256]
            fn fill(n: u16) {
                var p: ptr u8 = addr(buf)
                for i in 0..n.lo using Y { p[i] = 1 }
            }
            fn main() { fill({{n}}) }
            """);
        for (int k = 0; k < 256; k++) Assert.Equal(k < (n & 0xFF) ? 1 : 0, r.U8("buf", k));
        Assert.Contains("cpy fill.n|", Listing.Code(r, "fill"));
        Assert.DoesNotContain("_t0", Listing.Code(r, "fill"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Stores_of_0_at_the_start_of_main_into_cleared_globals_go()
    {
        var r = Run.Program("""
            var a: [u16; 4]
            var b: u8
            var c: u8
            var d: u8 = 7
            noinit var e: u8
            var f: u8
            var g: u8
            fn main() {
                a[0] = 0
                a[1] = 1
                b = 0
                c += 1
                c = 0
                d = 0
                e = 0
                f = 0
                g = f + 1
                b = 0
            }
            """, setup: ram => ram.Bytes[0x0900] = 0);
        Assert.Equal(1, r.U16At("a", 1, 4));
        Assert.Equal(0, r.U8("c"));
        Assert.Equal(0, r.U8("d"));
        Assert.Equal(0, r.U8("e"));
        Assert.Equal(1, r.U8("g"));
        var code = Listing.Code(r);
        Assert.DoesNotContain("sta a|", code);
        Assert.DoesNotContain("sta a+4|", code);
        Assert.DoesNotContain("sta b|", code);   // also the second b = 0: main has not written b
        Assert.DoesNotContain("sta f|", code);
        Assert.Contains("sta c|", code);          // c = 0 after c += 1
        Assert.Contains("sta d|", code);          // initialized: in the file, not cleared
        Assert.Contains("sta e|", code);          // noinit
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Stores_of_0_stay_after_a_label_a_call_or_in_another_function()
    {
        var r = Run.Program("""
            var x: u8
            var y: u8
            var z: u8
            var n: u8 = 3
            fn f() { y = 0 }
            fn main() {
                while n > 0 {
                    x = 0
                    x += 1
                    n -= 1
                }
                f()
                z = 0
            }
            """);
        Assert.Equal(1, r.U8("x"));
        // x is held in X for the loop (lda #0 / tax / inx), then stored (F7).
        Assert.Contains("stx x", Listing.Code(r));
        Assert.Contains("sta z", Listing.Code(r));
        Assert.Contains("sta y", Listing.Code(r, "f"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Constants_stored_together()
    {
        var r = Run.Program("""
            var out: u16
            fn mul(a: u16, b: u16) -> u16 { return a + b }
            fn main() { out = mul(123, 45) }
            """);
        Assert.Equal(168, r.U16("out"));
        Assert.Equal("lda #$7B|sta mul.a|lda #$2D|sta mul.b|lda #$00|sta mul.a+1|sta mul.b+1|jsr mul|sta out|stx out+1|rts", Listing.Code(r));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Volatile_stores_keep_their_order()
    {
        var r = Run.Program("""
            var border: u8 @ $D020
            var x: u8
            var y: u8
            fn main() {
                x = 0
                border = 1
                y = 0
            }
            """, setup: ram => ram.Bytes[0x0900] = 0);
        Assert.Equal("lda #$01|sta border|rts", Listing.Code(r));   // the zeros go: x and y were cleared
        var r2 = Run.Program("""
            var border: u8 @ $D020
            var x: u8
            var y: u8
            fn main() {
                x = 5
                border = 0
                y = 5
            }
            """);
        Assert.Equal("lda #$05|sta x|lda #$00|sta border|lda #$05|sta y|rts", Listing.Code(r2));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Grouped_stores_keep_each_source_line_in_front_of_its_code()
    {
        // Review M18, finding 4.
        var r = Run.Program("""
            var a: u16
            var b: u16
            fn f() {
                a = 123
                b = 45
            }
            fn main() { f() }
            """);
        Assert.Equal(123, r.U16("a"));
        Assert.Equal(45, r.U16("b"));
        var lines = Listing.Annotated(r, "f").Where(l => !l.EndsWith(':')).TakeWhile(l => l != "rts").ToList();
        int A(string s) => lines.FindIndex(l => l.Contains(s));
        Assert.True(A("a = 123") < A("lda #$7B") && A("sta a") < A("b = 45") && A("b = 45") < A("lda #$2D"), string.Join("\n", lines));
        Assert.Equal(1, lines.Count(l => l == "lda #$00"));
    }
}