namespace Tern.Compiler.Tests;

/// <summary>
/// M18 part 3, F9: a <c>while</c> whose first test is known true has no entry jump; one that counts
/// down to zero tests the Z flag of its decrement; a <c>for i in 0..v</c> at the start of a
/// <c>while v &gt; 0</c> body has no empty-range test; unsigned <c>x &gt; 0</c> needs no <c>cmp</c>.
/// </summary>
public class LoopControlTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(200)]
    [InlineData(255)]
    public void A_loop_counting_down_to_zero_tests_its_decrement(int start)
    {
        var r = Run.Program($$"""
            var n: u8 = {{start}}
            var sum: u16
            var passes: u8
            fn main() {
                while n > 0 {
                    sum += n as u16
                    passes += 1
                    n -= 1
                }
            }
            """);
        Assert.Equal(start * (start + 1) / 2, r.U16("sum"));
        Assert.Equal(start, r.U8("passes"));
        Assert.Equal(0, r.U8("n"));
        var code = Listing.Code(r);
        Assert.Contains("dec n|bne main.while", code);
        Assert.DoesNotContain("jmp", code);
        Assert.Contains("lda n|beq main.wend", code);   // the entry test: n is not known
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(1000)]
    [InlineData(65535)]
    public void A_16_bit_count_of_pages_tests_its_high_byte(int start)
    {
        var r = Run.Program($$"""
            var n: u16 = {{start}}
            var pages: u8
            fn main() {
                while n.hi != 0 {
                    pages += 1
                    n -= 256
                }
            }
            """);
        Assert.Equal(start >> 8, r.U8("pages"));
        Assert.Equal(start & 0xFF, r.U16("n"));
        Assert.Contains("dec n+1|bne main.while", Listing.Code(r));
    }

    [Fact]
    public void A_held_counter_tests_its_dex()
    {
        var r = Run.Program("""
            var out: [u8; 10]
            var k: u8 = 9
            fn main() {
                while k != 0 {
                    out[k] = k ^ 15
                    k -= 1
                }
            }
            """);
        for (int i = 0; i < 10; i++) Assert.Equal(i == 0 ? 0 : i ^ 15, r.U8("out", i));
        Assert.Equal(0, r.U8("k"));
        Assert.Contains("dex|bne main.while", Listing.Code(r));
    }

    [Fact]
    public void Continue_needs_the_full_test()
    {
        var r = Run.Program("""
            var n: u8 = 10
            var odd: u8
            fn main() {
                while n > 0 {
                    if n & 1 == 0 {
                        n -= 1
                        continue
                    }
                    odd += 1
                    n -= 1
                }
            }
            """);
        Assert.Equal(5, r.U8("odd"));
        Assert.Equal(0, r.U8("n"));
        Assert.DoesNotContain("dec n|bne", Listing.Code(r));
    }

    [Theory]
    [InlineData(2, 16, true)]
    [InlineData(15, 16, true)]
    [InlineData(16, 16, false)]
    [InlineData(200, 16, false)]
    public void A_loop_after_a_constant_that_makes_its_test_true_has_no_entry_jump(int start, int bound, bool known)
    {
        var r = Run.Program($$"""
            var passes: u8
            fn main() {
                var n: u8 = {{start}}
                while n < {{bound}} {
                    passes += 1
                    n += 1
                }
            }
            """);
        Assert.Equal(Math.Max(0, bound - start), r.U8("passes"));
        Assert.Equal(!known, Listing.Code(r).Contains("jmp"));
    }

    [Theory]
    [InlineData(new byte[] { 5, 3, 9, 0, 255, 128, 1, 1 })]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 2, 1 })]
    public void An_inner_loop_up_to_the_outer_condition_has_no_empty_test(byte[] input)
    {
        var r = Run.Program($$"""
            var values: [u8; {{input.Length}}] = [{{string.Join(", ", input)}}]
            fn main() {
                var last: u8 = {{input.Length - 1}}
                while last > 0 {
                    for i in 0..last {
                        if values[i] > values[i + 1] {
                            var t: u8 = values[i]
                            values[i] = values[i + 1]
                            values[i + 1] = t
                        }
                    }
                    last -= 1
                }
            }
            """);
        var sorted = input.OrderBy(b => b).ToArray();
        for (int k = 0; k < input.Length; k++) Assert.Equal(sorted[k], r.U8("values", k));
        var code = Listing.Code(r);
        Assert.DoesNotContain("bcs main.fend", code);
        Assert.Contains("dec main.last|bne main.while", code);
    }

    [Fact]
    public void An_inner_loop_after_another_statement_keeps_its_empty_test()
    {
        var r = Run.Program("""
            var values: [u8; 4] = [1, 2, 3, 4]
            var total: u8
            fn main() {
                var last: u8 = 3
                while last > 0 {
                    last -= 1
                    for i in 0..last { total += values[i] }
                }
            }
            """);
        Assert.Equal(1 + 2 + 1, r.U8("total"));
        Assert.Contains("bcs main.fend", Listing.Code(r));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    public void Unsigned_greater_than_zero_needs_no_compare(int v)
    {
        var r = Run.Program($$"""
            var v: u8 = {{v}}
            var big: u8
            var small: u8
            fn main() {
                if v > 0 { big = 1 }
                if v <= 0 { small = 1 }
            }
            """);
        Assert.Equal(v > 0 ? 1 : 0, r.U8("big"));
        Assert.Equal(v <= 0 ? 1 : 0, r.U8("small"));
        Assert.DoesNotContain("cmp", Listing.Code(r));
    }
}
