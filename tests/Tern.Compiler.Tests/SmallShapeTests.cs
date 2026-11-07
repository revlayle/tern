namespace Tern.Compiler.Tests;

/// <summary>M18 F10: small code shapes, each checked against C# at edge values and in the listing.</summary>
public class SmallShapeTests
{
    private static string Code(Run r, string function = "main")
    {
        var listing = r.Result.Listing;
        var start = listing.IndexOf($"\n{function}:", StringComparison.Ordinal);
        var code = new List<string>();
        foreach (var l in listing[(start + 1)..].Split('\n').Skip(1).Select(l => l.TrimEnd('\r')))
        {
            if (l.EndsWith(':') && !l.StartsWith(' ') && !l.StartsWith('$') && !l.StartsWith(';') && !l.StartsWith($"{function}.")) break;
            if (l.StartsWith('$')) code.Add(l[17..].Split(';')[0].Trim());
        }
        return string.Join("|", code);
    }

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
        Assert.Equal("inc a+1|dec b+1|lda c+1|clc|adc #$03|sta c+1|lda d+1|sec|sbc #$FF|sta d+1|inc p+1|rts", Code(r));
    }

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
        Assert.Contains("ldx i|inc a,x|dec a-1,x", Code(r));
        Assert.Contains("inc a+1,x", Code(r));
    }

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
        Assert.Equal("lda #$05|ldx n|sta a,x|txa|asl a|sta m|rts", Code(r));
    }

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
        var code = Code(r);
        Assert.Contains("ldy #$01", code);
        Assert.DoesNotContain("ldy #$02", code);
        Assert.DoesNotContain("ldy #$03", code);
        Assert.Contains("dey", code);
    }

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
        Assert.Equal(1, Code(r).Split('|').Count(s => s == "ldy #$00"));
    }

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
        Assert.Equal("lda x|bne main.endif0|sta y|rts", Code(r));
    }

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
        Assert.Contains("lda #$00|sec|sbc main.m", Code(r));
        Assert.DoesNotContain("eor #$FF", Code(r));
    }

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
        Assert.DoesNotContain("cmp #$00", Code(r));
        Assert.Contains("cmp #$01", Code(r));
    }
}