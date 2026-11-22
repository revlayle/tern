namespace Tern.Compiler.Tests;

/// <summary>
/// V1 fixes, gap 26: the constant parts of a 16-bit or pointer sum are added together at compile
/// time, wherever they are written; a sum with a low byte of 0 goes into the high-byte add of
/// <c>q + e</c> (spec 11, <c>p = q + e + K</c>).
/// </summary>
public class ConstantSumTests
{
    public static TheoryData<int, int> Values()
    {
        var data = new TheoryData<int, int>();
        foreach (int q in new[] { 0, 1, 255, 256, 32768, 65280, 65535 })
            foreach (int c in new[] { 0, 1, 127, 128, 255 })
                data.Add(q, c);
        return data;
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void Gives_the_sum_as_written(int q, int c)
    {
        var r = Run.Program($$"""
            const SCREEN = $0400
            const COLOR_RAM = $D800
            var rows: [u16; 2] = [0, {{q}}]
            var i: u8 = 1
            var q: u16 = {{q}}
            var c: u8 = {{c}}
            var s: i16 = {{(short)q}}
            var p1: ptr u8
            var p2: ptr u8
            var p3: ptr u8
            var r1: u16
            var r2: u16
            var r3: u16
            var r4: u16
            var r5: u16
            var r6: i16
            fn main() {
                p1 = (rows[i] + (c << 1) + (COLOR_RAM - SCREEN)) as ptr u8
                p2 = (rows[i] + (COLOR_RAM - SCREEN) + (c << 1)) as ptr u8
                p3 = (q + c + $0100) as ptr u8
                r1 = q + 5 - 3
                r2 = 2 + q + 3
                r3 = q + c as u16 + 3
                r4 = q - $0100 + c as u16 - $0200
                r5 = 7 - q + 2
                r6 = s - 300 + 44
            }
            """);
        Assert.Equal((ushort)(q + (byte)(c << 1) + 0xD400), r.U16("p1"));
        Assert.Equal((ushort)(q + (byte)(c << 1) + 0xD400), r.U16("p2"));
        Assert.Equal((ushort)(q + c + 0x100), r.U16("p3"));
        Assert.Equal((ushort)(q + 2), r.U16("r1"));
        Assert.Equal((ushort)(q + 5), r.U16("r2"));
        Assert.Equal((ushort)(q + c + 3), r.U16("r3"));
        Assert.Equal((ushort)(q + c - 0x300), r.U16("r4"));
        Assert.Equal((ushort)(9 - q), r.U16("r5"));
        Assert.Equal((short)((short)q - 256), r.I16("r6"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void The_code_is_the_spec_shape()
    {
        var r = Run.Program("""
            const SCREEN = $0400
            const COLOR_RAM = $D800
            var rows: [u16; 25]
            var r: u8
            var c: u8
            var q: u16
            var p: ptr u8
            var t: u16
            fn last() { p = (rows[r] + (c << 1) + (COLOR_RAM - SCREEN)) as ptr u8 }
            fn middle() { p = (rows[r] + (COLOR_RAM - SCREEN) + (c << 1)) as ptr u8 }
            fn first() { p = ((COLOR_RAM - SCREEN) + rows[r] + (c << 1)) as ptr u8 }
            fn folded() { t = 2 + q + 3 }
            fn low_byte() { t = q + c as u16 + 3 }
            fn main() {
                last()
                middle()
                first()
                folded()
                low_byte()
            }
            """);
        const string shape = "lda c|asl a|clc|ldx r|adc rows,x|sta p|lda rows+25,x|adc #$D4|sta p+1|rts";
        Assert.Equal(shape, Listing.Code(r, "last"));
        Assert.Equal(shape, Listing.Code(r, "middle"));
        Assert.Equal(shape, Listing.Code(r, "first"));
        Assert.Equal("clc|lda q|adc #$05|sta t|lda q+1|adc #$00|sta t+1|rts", Listing.Code(r, "folded"));
        // K with a low byte that is not 0 is added once, at the end.
        Assert.Matches(@"^lda c\|clc\|adc q\|sta t\|lda q\+1\|adc #\$00\|sta t\+1\|clc\|lda t\|adc #\$03\|sta t\|bcc low_byte\.c\d+\|inc t\+1\|rts$",
            Listing.Code(r, "low_byte"));
    }
}
