namespace Tern.Compiler.Tests;

/// <summary>
/// V1 fixes, gap 27: 16-bit <c>t = a + b</c> and <c>t = a - b</c> straight into t, with no copy of a
/// first, whatever the order of the operands; a constant with a low byte of 0 adds only the high byte (spec 11).
/// </summary>
public class DirectAddTests
{
    public static TheoryData<int, int> Values()
    {
        var data = new TheoryData<int, int>();
        int[] edges = [0, 1, 255, 256, 32767, 32768, 65280, 65535];
        foreach (int a in edges)
            foreach (int b in edges)
                data.Add(a, b);
        return data;
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void Adds_and_subtracts_like_C(int a, int b)
    {
        var r = Run.Program($$"""
            var a: u16 = {{a}}
            var b: u16 = {{b}}
            var x: [u16; 3] = [0, {{b}}, 0]
            var i: u8 = 1
            var sa: i16 = {{(short)a}}
            var sb: i16 = {{(short)b}}
            var r1: u16
            var r2: u16
            var r3: u16
            var r4: u16
            var r5: u16
            var r6: u16
            var r7: u16
            var r8: u16
            var r9: u16
            var r10: i16
            var r11: i16
            var r12: u16
            fn main() {
                r1 = a + b
                r2 = b + a
                r3 = a - b
                r4 = a + $1800
                r5 = $1800 + a
                r6 = a - $1800
                r7 = a + 5
                r8 = x[i] + 20
                r9 = 20 + x[i]
                r10 = sa - sb
                r11 = sa + -256
                r12 = a - x[i]
            }
            """);
        Assert.Equal((ushort)(a + b), r.U16("r1"));
        Assert.Equal((ushort)(a + b), r.U16("r2"));
        Assert.Equal((ushort)(a - b), r.U16("r3"));
        Assert.Equal((ushort)(a + 0x1800), r.U16("r4"));
        Assert.Equal((ushort)(a + 0x1800), r.U16("r5"));
        Assert.Equal((ushort)(a - 0x1800), r.U16("r6"));
        Assert.Equal((ushort)(a + 5), r.U16("r7"));
        Assert.Equal((ushort)(b + 20), r.U16("r8"));
        Assert.Equal((ushort)(b + 20), r.U16("r9"));
        Assert.Equal((short)((short)a - (short)b), r.I16("r10"));
        Assert.Equal((short)((short)a - 256), r.I16("r11"));
        Assert.Equal((ushort)(a - b), r.U16("r12"));
    }

    [Fact]
    public void The_destination_on_either_side_is_still_right()
    {
        var r = Run.Program("""
            var a: u16 = $01FF
            var t: u16 = $0101
            var u: u16 = $0101
            var w: u16 = $1234
            var v: u16 = $00FF
            fn main() {
                t = a - t
                u = a + u
                w = w + $0300
                v = 5 + v
            }
            """);
        Assert.Equal(0x00FE, r.U16("t"));
        Assert.Equal(0x0300, r.U16("u"));
        Assert.Equal(0x1534, r.U16("w"));
        Assert.Equal(0x0104, r.U16("v"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void The_code_is_the_spec_shape()
    {
        var r = Run.Program("""
            const K = $1800
            var a: u16 = 1000
            var b: u16 = 300
            var x: [u16; 4]
            var i: u8
            var t: u16
            fn sub() { t = a - b }
            fn add() { t = a + b }
            fn swapped() { t = b + a }
            fn high() { t = a + K }
            fn high_first() { t = K + a }
            fn high_sub() { t = a - K }
            fn small() { t = a + 5 }
            fn small_first() { t = 5 + a }
            fn element() { t = x[i] + 20 }
            fn element_first() { t = 20 + x[i] }
            fn same() { a = a + 5 }
            fn same_high() { a = a + K }
            fn main() {
                sub()
                add()
                swapped()
                high()
                high_first()
                high_sub()
                small()
                small_first()
                element()
                element_first()
                same()
                same_high()
            }
            """);
        Assert.Equal("sec|lda a|sbc b|sta t|lda a+1|sbc b+1|sta t+1|rts", Listing.Code(r, "sub"));
        Assert.Equal("clc|lda a|adc b|sta t|lda a+1|adc b+1|sta t+1|rts", Listing.Code(r, "add"));
        Assert.Equal("clc|lda b|adc a|sta t|lda b+1|adc a+1|sta t+1|rts", Listing.Code(r, "swapped"));
        Assert.Equal("lda a|sta t|lda a+1|clc|adc #$18|sta t+1|rts", Listing.Code(r, "high"));
        Assert.Equal("lda a|sta t|lda a+1|clc|adc #$18|sta t+1|rts", Listing.Code(r, "high_first"));
        Assert.Equal("lda a|sta t|lda a+1|sec|sbc #$18|sta t+1|rts", Listing.Code(r, "high_sub"));
        Assert.Equal("clc|lda a|adc #$05|sta t|lda a+1|adc #$00|sta t+1|rts", Listing.Code(r, "small"));
        Assert.Equal(Listing.Code(r, "small").Replace("small", "small_first"), Listing.Code(r, "small_first"));
        Assert.Equal("clc|ldx i|lda x,x|adc #$14|sta t|lda x+4,x|adc #$00|sta t+1|rts", Listing.Code(r, "element"));
        Assert.Equal(Listing.Code(r, "element"), Listing.Code(r, "element_first"));
        // The destination on the left keeps the carry into the high byte with bcc / inc, and K adds to a+1 alone.
        Assert.Matches(@"^clc\|lda a\|adc #\$05\|sta a\|bcc same\.c\d+\|inc a\+1\|rts$", Listing.Code(r, "same"));
        Assert.Equal("lda a+1|clc|adc #$18|sta a+1|rts", Listing.Code(r, "same_high"));
    }
}
