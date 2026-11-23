namespace Tern.Compiler.Tests;

/// <summary>
/// V1 fixes 2, gap 30: a sum of two unsigned bytes widened to 16 bits is a byte add with the carry into the
/// high byte, with no 16-bit temporary (spec 11, <c>s = (a as u16) + e</c>).
/// </summary>
public class WidenedSumTests
{
    public static TheoryData<int, int> Values()
    {
        var data = new TheoryData<int, int>();
        foreach (int a in new[] { 0, 1, 127, 128, 255 })
            foreach (int b in new[] { 0, 1, 128, 255 })
                data.Add(a, b);
        return data;
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void Gives_the_sum_as_written(int a, int b)
    {
        var r = Run.Program($$"""
            var a: u8 = {{a}}
            var b: u8 = {{b}}
            var arr: [u8; 4] = [0, {{a}}, {{b}}, 0]
            var i: u8 = 1
            var calls: u8
            var r1: u16
            var r2: u16
            var r3: u16
            var r4: u16
            var r5: u16
            var r6: u16
            fn f() -> u8 {
                calls += 1
                return b
            }
            fn main() {
                r1 = (a as u16) + (b >> 1)
                r2 = (a as u16) + (b as u16)
                r3 = a as u16 + b
                r4 = (arr[i] as u16) + arr[i + 1]
                r5 = (a as u16) + f()
                r6 = ((a ^ b) as u16) + (b << 1)
            }
            """);
        Assert.Equal(a + (b >> 1), r.U16("r1"));
        Assert.Equal(a + b, r.U16("r2"));
        Assert.Equal(a + b, r.U16("r3"));
        Assert.Equal(a + b, r.U16("r4"));
        Assert.Equal(a + b, r.U16("r5"));
        Assert.Equal((a ^ b) + (byte)(b << 1), r.U16("r6"));
        Assert.Equal(1, r.U8("calls"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void The_code_is_the_spec_shape()
    {
        var r = Run.Program("""
            var d_ax: u8
            var d_ay: u8
            var arr: [u8; 4]
            var i: u8
            var j: u8
            var s: u16
            fn shifted() { s = (d_ay as u16) + (d_ax >> 1) }
            fn elements() { s = (arr[i] as u16) + arr[j] }
            fn both_need_code() { s = ((d_ax ^ d_ay) as u16) + (d_ax << 1) }
            fn main() {
                shifted()
                elements()
                both_need_code()
            }
            """);
        Assert.Equal("lda d_ax|lsr a|clc|adc d_ay|sta s|lda #$00|adc #$00|sta s+1|rts", Listing.Code(r, "shifted"));
        Assert.Equal("ldx i|lda arr,x|clc|ldx j|adc arr,x|sta s|lda #$00|adc #$00|sta s+1|rts", Listing.Code(r, "elements"));
        // Both need code: one through a byte temporary, never a 16-bit one.
        Assert.Matches(@"^lda d_ax\|asl a\|sta both_need_code\._t\d+\|lda d_ax\|eor d_ay\|clc\|adc both_need_code\._t\d+\|sta s\|lda #\$00\|adc #\$00\|sta s\+1\|rts$",
            Listing.Code(r, "both_need_code"));
    }
}
