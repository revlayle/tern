namespace Tern.Compiler.Tests;

/// <summary>M9: a[i - c] is addressed as a-c,x where the u8 wrap-around cannot matter.</summary>
public class IndexOffsetTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(8)]
    public void Reads_and_writes_with_a_subtracted_constant_match_C(int c)
    {
        // small has 16 elements, so a[i - c] is folded; i never goes below c.
        var small = Enumerable.Range(0, 16).Select(k => (byte)(k * 7 + 3)).ToArray();
        var words = Enumerable.Range(0, 8).Select(k => (ushort)(k * 300 + 5)).ToArray();
        var r = Run.Program($$"""
            var small: [u8; 16] = [{{string.Join(", ", small)}}]
            var copy: [u8; 16]
            var words: [u16; 8] = [{{string.Join(", ", words)}}]
            var got: [u16; 8]
            var sum: u16
            fn main() {
                var i: u8 = {{c}}
                while i < 16 {
                    copy[i - {{c}}] = small[i - {{c}}] + 1
                    i += 1
                }
                var j: u8 = {{c}}
                while j < 8 {
                    got[j - {{c}}] = words[j - {{c}}] + 1
                    j += 1
                }
                var q: u8 = {{c}}
                small[q - {{c}}] += 100
                sum = small[q - {{c}}] as u16 + words[q - {{c}}]
            }
            """);
        for (int i = c; i < 16; i++) Assert.Equal((byte)(small[i - c] + 1), r.U8("copy", i - c));
        for (int j = c; j < 8; j++) Assert.Equal((ushort)(words[j - c] + 1), r.U16At("got", j - c, 8));
        Assert.Equal((byte)(small[0] + 100), r.U8("small", 0));
        Assert.Equal((ushort)((byte)(small[0] + 100) + words[0]), r.U16("sum"));
    }

    [Fact]
    public void A_subtraction_that_wraps_in_a_256_element_array_is_not_folded()
    {
        var r = Run.Program("""
            var ring: [u8; 256]
            var out: u8
            fn main() {
                for k in 0..=255 { ring[k] = k }
                var head: u8 = 0
                out = ring[head - 1]
            }
            """);
        Assert.Equal(255, r.U8("out"));   // head - 1 wraps to 255
        Assert.DoesNotContain("ring-1", r.Result.Listing);
    }

    [Theory]
    [InlineData(255, 1, true)]
    [InlineData(255, 2, false)]
    [InlineData(250, 6, true)]
    [InlineData(250, 7, false)]
    public void The_fold_stops_where_the_wrap_would_reach_the_array(int length, int c, bool folded)
    {
        var r = Run.Program($$"""
            var a: [u8; {{length}}]
            var out: u8
            fn main() {
                var i: u8 = {{c}}
                a[{{length - 1}}] = 9
                out = a[i - {{c}}] + 1
            }
            """);
        Assert.Equal(1, r.U8("out"));
        Assert.Equal(folded, r.Result.Listing.Contains($"a-{c},x"));
    }

    [Fact]
    public void A_loop_counter_in_x_is_used_directly()
    {
        var r = Run.Program("""
            var a: [u8; 8] = [10, 20, 30, 40, 50, 60, 70, 80]
            var b: [u8; 8]
            fn main() {
                for i in 1..8 using X { b[i - 1] = a[i - 1] + a[i] }
            }
            """);
        for (int i = 1; i < 8; i++) Assert.Equal(20 * i + 10, r.U8("b", i - 1));
        Assert.Contains("a-1,x", r.Result.Listing);
    }
}
