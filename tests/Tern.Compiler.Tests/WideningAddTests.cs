namespace Tern.Compiler.Tests;

/// <summary>M18 F6: w += b as u16 adds the byte and carries with bcc / inc (spec 11); -= likewise.</summary>
public class WideningAddTests
{
    public static TheoryData<int, int> Values()
    {
        var data = new TheoryData<int, int>();
        foreach (int w in new[] { 0, 1, 255, 256, 1000, 65280, 65535 })
            foreach (int b in new[] { 0, 1, 128, 255 })
                data.Add(w, b);
        return data;
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void Adds_and_subtracts_a_byte_like_C(int w, int b)
    {
        var r = Run.Program($$"""
            var w0: u16 = {{w}}
            var b0: u8 = {{b}}
            var arr: [u8; 4] = [0, 0, {{b}}, 0]
            var r1: u16
            var r2: u16
            var r3: u16
            var r4: u16
            var r5: u16
            var r6: u16
            var r7: i16
            fn main() {
                r1 = w0
                r1 += b0 as u16
                r2 = w0
                r2 = (b0 as u16) + r2
                r3 = w0
                r3 -= b0 as u16
                r4 = w0
                r5 = w0
                for i in 2..3 {
                    r4 += arr[i] as u16
                    r5 -= arr[i] as u16
                }
                r6 = w0
                r6 += (b0 >> 1) as u16
                r7 = w0 as i16
                r7 += b0 as i16
            }
            """);
        Assert.Equal((ushort)(w + b), r.U16("r1"));
        Assert.Equal((ushort)(w + b), r.U16("r2"));
        Assert.Equal((ushort)(w - b), r.U16("r3"));
        Assert.Equal((ushort)(w + b), r.U16("r4"));
        Assert.Equal((ushort)(w - b), r.U16("r5"));
        Assert.Equal((ushort)(w + (b >> 1)), r.U16("r6"));
        Assert.Equal((short)(w + b), r.I16("r7"));
    }

    [Fact]
    public void A_signed_byte_is_still_sign_extended()
    {
        var r = Run.Program("""
            var w: u16 = 1000
            var c: i8 = -1
            fn main() { w += c as u16 }
            """);
        Assert.Equal(999, r.U16("w"));
    }

    [Fact]
    public void The_code_is_the_spec_shape()
    {
        var r = Run.Program("""
            var w: u16 = 300
            var b: u8 = 200
            var x: u16
            fn up() { w += b as u16 }
            fn down() { w -= b as u16 }
            fn main() {
                up()
                x = w
                down()
                w += b as u16
                w -= b as u16
            }
            """);
        Assert.Equal(300, r.U16("w"));
        Assert.Equal(500, r.U16("x"));
        Assert.Matches(@"^lda b\|clc\|adc w\|sta w\|bcc up\.c\d+\|inc w\+1\|rts$", Listing.Code(r, "up"));
        Assert.Matches(@"^lda w\|sec\|sbc b\|sta w\|bcs down\.c\d+\|dec w\+1\|rts$", Listing.Code(r, "down"));
        // A still holds w after the inc w+1 (F17), so the subtraction does not load it; the first
        // store of the low byte is overwritten before anything reads it (F3, F7).
        Assert.Matches(@"lda b\|clc\|adc w\|bcc main\.c\d+\|inc w\+1\|sec\|sbc b\|sta w\|bcs main\.c\d+\|dec w\+1\|rts$", Listing.Code(r));
    }

}
