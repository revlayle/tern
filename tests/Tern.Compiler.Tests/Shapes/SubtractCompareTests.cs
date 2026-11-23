namespace Tern.Compiler.Tests;

/// <summary>
/// V1 fixes 2: <c>if a &gt;= b { d = a - b ... }</c> on unsigned bytes subtracts once and branches on the
/// carry; likewise <c>if a &lt; b { ... } else { d = a - b ... }</c> (spec 11, from Galaxy Stars'
/// <c>demo.dist</c> and <c>explode.hits</c>).
/// </summary>
public class SubtractCompareTests
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
    public void Gives_the_difference_where_it_does_not_borrow(int a, int b)
    {
        var r = Run.Program($$"""
            var a: u8 = {{a}}
            var b: u8 = {{b}}
            var arr: [u8; 4] = [0, {{a}}, 0, {{b}}]
            var i: u8 = 1
            var s: i8 = {{(sbyte)a}}
            var t: i8 = {{(sbyte)b}}
            volatile var v: u8 = {{a}}
            var d: [u8; 8]
            var n: [u8; 8]
            fn main() {
                if a >= b { d[0] = a - b
                    n[0] = 1 }
                if a >= b { d[1] = a - b } else { n[1] = 2 }
                if a < b { n[2] = 3 } else { d[2] = a - b
                    n[2] = 4 }
                if arr[i] >= arr[3] { d[3] = arr[i] - arr[3] }
                if a >= 128 { d[4] = a - 128 }
                if v >= b { d[5] = v - b }
                if s >= t { d[6] = (s - t) as u8 }
                if a >= b { d[7] = b - a }
            }
            """);
        byte diff = (byte)(a - b);
        Assert.Equal(a >= b ? diff : 0, r.U8("d", 0));
        Assert.Equal(a >= b ? 1 : 0, r.U8("n", 0));
        Assert.Equal(a >= b ? diff : 0, r.U8("d", 1));
        Assert.Equal(a >= b ? 0 : 2, r.U8("n", 1));
        Assert.Equal(a < b ? 0 : diff, r.U8("d", 2));
        Assert.Equal(a < b ? 3 : 4, r.U8("n", 2));
        Assert.Equal(a >= b ? diff : 0, r.U8("d", 3));
        Assert.Equal(a >= 128 ? a - 128 : 0, r.U8("d", 4));
        Assert.Equal(a >= b ? diff : 0, r.U8("d", 5));
        Assert.Equal((sbyte)a >= (sbyte)b ? (byte)((sbyte)a - (sbyte)b) : 0, r.U8("d", 6));
        Assert.Equal(a >= b ? (byte)(b - a) : 0, r.U8("d", 7));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void The_code_is_the_spec_shape()
    {
        var r = Run.Program("""
            var a: u8
            var b: u8
            var c: u8
            var d: u8
            var n: u8
            volatile var v: u8
            fn at_least() {
                if a >= b {
                    d = a - b
                    n += 1
                }
            }
            fn less_else() {
                if a < b { n = 0 } else { d = a - b }
            }
            fn other_operands() { if a >= b { d = a - c } }
            fn volatile_read() { if v >= b { d = v - b } }
            fn main() {
                at_least()
                less_else()
                other_operands()
                volatile_read()
            }
            """);
        Assert.Matches(@"^lda a\|sec\|sbc b\|bcc at_least\.endif\d+\|sta d\|inc n\|rts$", Listing.Code(r, "at_least"));
        Assert.Matches(@"^lda a\|sec\|sbc b\|bcs less_else\.else\d+\|lda #\$00\|sta n\|jmp less_else\.endif\d+\|less_else\.else\d+:\|sta d\|less_else\.endif\d+:\|rts$",
            Listing.CodeWithLabels(r, "less_else"));
        // Not the same subtract, or a volatile value read twice as written: the compare stays.
        Assert.StartsWith("lda a|cmp b|bcc", Listing.Code(r, "other_operands"));
        Assert.StartsWith("lda v|cmp b|bcc", Listing.Code(r, "volatile_read"));
    }
}
