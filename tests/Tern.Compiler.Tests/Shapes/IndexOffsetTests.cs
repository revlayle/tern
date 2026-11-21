namespace Tern.Compiler.Tests;

/// <summary>M9: a[i - c] is addressed as a-c,x where the u8 wrap-around cannot matter. Spec 11 (code shapes).</summary>
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

    [Trait("Shape", "nmos6502")]
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
        Assert.DoesNotContain("ring-1", Listing.Code(r));
        Assert.Matches(@"\blda ring,[xy]\b", Listing.Code(r));
    }

    [Trait("Shape", "nmos6502")]
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
        Assert.Equal(folded, Listing.Code(r).Contains($"a-{c},x"));
        Assert.Equal(!folded, System.Text.RegularExpressions.Regex.IsMatch(Listing.Code(r), @"\blda a,[xy]\b"));
    }

    [Trait("Shape", "nmos6502")]
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
        Assert.Contains("a-1,x", Listing.Code(r));
    }

    // M18 F2: a[i + c] folded when the for counter's bounds prove i + c does not wrap (spec 3.2).

    private static readonly byte[] Ring = Enumerable.Range(0, 256).Select(k => (byte)(k * 37 + 11)).ToArray();
    private static readonly ushort[] Words = Enumerable.Range(0, 128).Select(k => (ushort)(k * 515 + 3)).ToArray();

    private static string Arrays => $$"""
        var a: [u8; 256] = [{{string.Join(", ", Ring)}}]
        var w: [u16; 128] = [{{string.Join(", ", Words)}}]
        var b: [u8; 256]
        var v: [u16; 128]
        """;

    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData("0..99", 1, 0, 98, false)]
    [InlineData("0..=254", 1, 0, 254, false)]
    [InlineData("3..=250", 5, 3, 250, false)]
    [InlineData("200..0 step -1", 55, 1, 200, true)]     // counts down, from a constant
    [InlineData("9..=0 step -1", 3, 0, 9, true)]
    public void A_constant_offset_folds_under_constant_bounds(string range, int c, int first, int last, bool down)
    {
        var r = Run.Program($$"""
            {{Arrays}}
            fn main() {
                for i in {{range}} {
                    b[i] = a[i + {{c}}]
                    b[i + {{c}}] += 1
                }
            }
            """);
        // In the loop's direction: an element written by b[i] may be incremented before or after.
        var want = new byte[256];
        var order = Enumerable.Range(first, last - first + 1);
        if (down) order = order.Reverse();
        foreach (int i in order) { want[i] = Ring[i + c]; want[i + c] = (byte)(want[i + c] + 1); }
        for (int k = 0; k < 256; k++) Assert.Equal(want[k], r.U8("b", k));
        Assert.Contains($"a+{c},x", Listing.Code(r));
        Assert.Contains($"b+{c},x", Listing.Code(r));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_step_folds_from_the_last_value_the_loop_reaches()
    {
        // Review M18 suggestion 5: 0..=250 step 7 ends at 245, so i + 10 <= 255.
        var r = Run.Program($$"""
            {{Arrays}}
            fn main() {
                for i in 0..=250 step 7 { b[i] = a[i + 10] }
            }
            """);
        for (int i = 0; i <= 250; i += 7) Assert.Equal(Ring[i + 10], r.U8("b", i));
        Assert.Contains("a+10,x", Listing.Code(r));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_constant_offset_folds_on_16_bit_arrays_and_with_the_constant_first()
    {
        var r = Run.Program($$"""
            {{Arrays}}
            fn main() {
                for i in 0..127 { v[i] = w[1 + i] + w[i] }
            }
            """);
        for (int i = 0; i < 127; i++) Assert.Equal((ushort)(Words[i + 1] + Words[i]), r.U16At("v", i, 128));
        Assert.Contains("w+1,x", Listing.Code(r));
    }

    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(254)]
    [InlineData(255)]
    public void A_variable_end_of_the_counters_type_proves_i_plus_1(int last)
    {
        var r = Run.Program($$"""
            {{Arrays}}
            var last: u8 = {{last}}
            fn main() {
                var n: u8 = last
                for i in 0..n { b[i] = a[i + 1] }
            }
            """);
        for (int k = 0; k < 256; k++) Assert.Equal(k < last ? Ring[k + 1] : 0, r.U8("b", k));
        Assert.Contains("a+1,x", Listing.Code(r));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void I_plus_2_under_a_variable_end_is_not_folded()
    {
        var r = Run.Program($$"""
            {{Arrays}}
            fn main() {
                var n: u8 = 255
                for i in 0..n { b[i] = a[i + 2] }
            }
            """);
        for (int k = 0; k < 255; k++) Assert.Equal(Ring[(k + 2) & 0xFF], r.U8("b", k));   // b[254] = a[0]
        Assert.DoesNotContain("a+2,x", Listing.Code(r));
        Assert.Matches(@"\blda a,[xy]\b", Listing.Code(r));
    }

    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData("0..=n", 255)]        // inclusive variable end: i can be 255
    [InlineData("0..=255", 255)]
    [InlineData("0..=254", 254)]      // i + 2 reaches 256 at i = 254
    [InlineData("n..0 step -1", 255)]         // counts down from a variable start
    public void A_sum_that_can_wrap_is_not_folded(string range, int top)
    {
        int c = range == "0..=254" ? 2 : 1;
        var r = Run.Program($$"""
            {{Arrays}}
            var out: u8
            fn main() {
                var n: u8 = 255
                for i in {{range}} {
                    if i == {{top}} { out = a[i + {{c}}] }
                }
            }
            """);
        Assert.Equal(Ring[(top + c) & 0xFF], r.U8("out"));
        Assert.DoesNotContain($"a+{c},x", Listing.Code(r));
        Assert.Matches(@"\blda a,[xy]\b", Listing.Code(r));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_ring_buffer_index_outside_a_loop_wraps()
    {
        var r = Run.Program($$"""
            {{Arrays}}
            var out: u8
            fn main() {
                var head: u8 = 255
                out = a[head + 1]
            }
            """);
        Assert.Equal(Ring[0], r.U8("out"));
        Assert.DoesNotContain("a+1,x", Listing.Code(r));
        Assert.Matches(@"\blda a,[xy]\b", Listing.Code(r));
    }

    [Fact]
    public void The_proof_ends_with_the_loop()
    {
        var r = Run.Program($$"""
            {{Arrays}}
            var out: u8
            var head: u8 = 255
            fn main() {
                for i in 0..10 { b[i] = a[i + 1] }
                for i in 0..=255 { if i == head { out = a[i + 1] } }
            }
            """);
        Assert.Equal(Ring[0], r.U8("out"));
        Assert.Equal(Ring[1], r.U8("b", 0));
    }
}
