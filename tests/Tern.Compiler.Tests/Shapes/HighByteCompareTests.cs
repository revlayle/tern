namespace Tern.Compiler.Tests;

/// <summary>
/// V1 fixes 2, gap 30: an unsigned 16-bit compare with a constant whose low byte is 0 (<c>&lt;</c>,
/// <c>&gt;=</c>) or <c>$FF</c> (<c>&lt;=</c>, <c>&gt;</c>) compares only the high byte; against 256 the
/// load of the high byte is the test. Spec 11, <c>if w &lt; K</c>.
/// </summary>
public class HighByteCompareTests
{
    private static readonly int[] Constants = [0x100, 0x200, 0x1FF, 0xFF00, 0x00FF, 0xFEFF];
    private static readonly int[] Values = [0, 1, 255, 256, 257, 511, 512, 0xFEFF, 0xFF00, 0xFFFF];

    public static TheoryData<int> Ks() => [.. Constants];

    [Theory]
    [MemberData(nameof(Ks))]
    public void Gives_each_compare_as_written(int k)
    {
        var tests = new[] { "<", ">=", "<=", ">" };
        string body = string.Concat(Values.Select((v, n) =>
            $"    w = {v}\n" + string.Concat(tests.Select((op, t) => $"    if w {op} {k} {{ r[{n * 4 + t}] = 1 }}\n"))));
        var r = Run.Program($$"""
            var w: u16
            var r: [u8; {{Values.Length * 4}}]
            fn main() {
            {{body}}}
            """);
        for (int n = 0; n < Values.Length; n++)
        {
            int v = Values[n];
            bool[] expected = [v < k, v >= k, v <= k, v > k];
            for (int t = 0; t < 4; t++)
                Assert.True(expected[t] == (r.U8("r", n * 4 + t) == 1), $"{v} {tests[t]} {k}");
        }
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void The_code_is_the_spec_shape()
    {
        var r = Run.Program("""
            var w: u16
            var hits: u8
            fn less_256() { if w < 256 { hits += 1 } }
            fn at_least_512() { if w >= 512 { hits += 1 } }
            fn at_most_511() { if w <= 511 { hits += 1 } }
            fn over_767() { if w > $02FF { hits += 1 } }
            fn low_byte() { if w < 300 { hits += 1 } }
            fn main() {
                less_256()
                at_least_512()
                at_most_511()
                over_767()
                low_byte()
            }
            """);
        Assert.Equal(3, r.U8("hits"));   // w is 0
        Assert.Matches(@"^lda w\+1\|bne less_256\.endif\d+\|inc hits\|rts$", Listing.Code(r, "less_256"));
        Assert.Matches(@"^lda w\+1\|cmp #\$02\|bcc at_least_512\.endif\d+\|inc hits\|rts$", Listing.Code(r, "at_least_512"));
        Assert.Matches(@"^lda w\+1\|cmp #\$02\|bcs at_most_511\.endif\d+\|inc hits\|rts$", Listing.Code(r, "at_most_511"));
        Assert.Matches(@"^lda w\+1\|cmp #\$03\|bcc over_767\.endif\d+\|inc hits\|rts$", Listing.Code(r, "over_767"));
        // A low byte that is not 0 compares both bytes.
        Assert.Matches(@"^lda w\|cmp #\$2C\|lda w\+1\|sbc #\$01\|bcs low_byte\.endif\d+\|inc hits\|rts$", Listing.Code(r, "low_byte"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void An_element_and_a_volatile_value()
    {
        var r = Run.Program("""
            var e: [u16; 4] = [10, 300, 1000, 70]
            volatile var v: u16 = 200
            var i: u8 = 2
            var hits: u8
            fn element() { if e[i] >= 512 { hits += 1 } }
            fn whole() { if v < 256 { hits += 2 } }
            fn main() {
                element()
                whole()
            }
            """);
        Assert.Equal(3, r.U8("hits"));
        Assert.Matches(@"^ldx i\|lda e\+4,x\|cmp #\$02\|bcc element\.endif\d+\|inc hits\|rts$", Listing.Code(r, "element"));
        // A volatile value is read whole, as written.
        Assert.Contains("lda v", Listing.Instructions(r, "whole"));
        Assert.Contains("lda v+1", Listing.Instructions(r, "whole"));
    }
}
