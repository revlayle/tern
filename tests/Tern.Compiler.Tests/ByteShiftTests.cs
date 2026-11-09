namespace Tern.Compiler.Tests;

/// <summary>
/// M20 follow-up 2, spec 11: a 16-bit shift by 8 to 15 moves a byte first, then shifts the moved byte
/// k - 8 times, instead of shifting both bytes k times.
/// </summary>
public class ByteShiftTests
{
    private static string Code(Run r, string function)
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

    public static TheoryData<int, int> Cases()
    {
        var data = new TheoryData<int, int>();
        int[] values = [0, 1, 0x7F, 0x80, 0xFF, 0x100, 0x1234, 0x7FFF, 0x8000, 0x8001, 0xFFFE, 0xFFFF];
        for (int k = 8; k <= 15; k++)
            foreach (int v in values) data.Add(k, v);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_count_from_8_to_15_gives_the_same_result_as_csharp(int k, int value)
    {
        short s = (short)value;
        byte b = (byte)value;
        var r = Run.Program($$"""
            var v: u16 = {{value}}
            var s: i16 = {{s}}
            var b: u8 = {{b}}
            var x: u16 = 1000
            var r1: u16
            var r2: u16
            var r3: i16
            var r4: i16
            var r5: u16
            var r6: u16
            var r7: i16
            var r8: i16
            var r9: u16
            var r10: u16
            var r11: u16
            var r12: u16
            fn main() {
                r1 = v << {{k}}
                r2 = v >> {{k}}
                r3 = s << {{k}}
                r4 = s >> {{k}}
                r5 = v
                r5 <<= {{k}}
                r6 = v
                r6 >>= {{k}}
                r7 = s
                r7 >>= {{k}}
                r8 = s
                r8 <<= {{k}}
                r9 = (b as u16) << {{k}}
                r10 = x + (v >> {{k}})
                r11 = x - (v << {{k}})
                r12 = (v >> {{k}}) - x
            }
            """);
        ushort v = (ushort)value;
        Assert.Equal((ushort)(v << k), r.U16("r1"));
        Assert.Equal((ushort)(v >> k), r.U16("r2"));
        Assert.Equal((short)(s << k), r.I16("r3"));
        Assert.Equal((short)(s >> k), r.I16("r4"));
        Assert.Equal((ushort)(v << k), r.U16("r5"));
        Assert.Equal((ushort)(v >> k), r.U16("r6"));
        Assert.Equal((short)(s >> k), r.I16("r7"));
        Assert.Equal((short)(s << k), r.I16("r8"));
        Assert.Equal((ushort)(b << k), r.U16("r9"));
        Assert.Equal((ushort)(1000 + (v >> k)), r.U16("r10"));
        Assert.Equal((ushort)(1000 - (v << k)), r.U16("r11"));
        Assert.Equal((ushort)((v >> k) - 1000), r.U16("r12"));
    }

    [Fact]
    public void The_code_moves_a_byte_as_spec_11_shows()
    {
        var r = Run.Program("""
            var v: u16 = $1234
            var s: i16 = -2
            var w: u16
            var t: i16
            fn left8() { w = v << 8 }
            fn left11() { w = v << 11 }
            fn right8() { w = v >> 8 }
            fn right9() { w = v >> 9 }
            fn signed9() { t = s >> 9 }
            fn in_place() { w <<= 8 }
            fn main() { left8(); left11(); right8(); right9(); signed9(); in_place() }
            """);
        Assert.Equal("lda v|sta w+1|lda #$00|sta w|rts", Code(r, "left8"));
        Assert.Equal("lda v|asl a|asl a|asl a|sta w+1|lda #$00|sta w|rts", Code(r, "left11"));
        Assert.Equal("lda v+1|sta w|lda #$00|sta w+1|rts", Code(r, "right8"));
        Assert.Equal("lda v+1|lsr a|sta w|lda #$00|sta w+1|rts", Code(r, "right9"));
        Assert.Matches(@"^lda s\+1\|cmp #\$80\|ror a\|sta t\|ora #\$7F\|bmi [^|]+\|lda #\$00\|sta t\+1\|rts$", Code(r, "signed9"));
        Assert.Equal("lda w|sta w+1|lda #$00|sta w|rts", Code(r, "in_place"));
        Assert.Equal(-1, r.I16("t"));
    }
}
