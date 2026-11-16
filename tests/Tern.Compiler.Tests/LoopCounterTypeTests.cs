using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// With constant bounds, a loop counter's type comes from the values it takes, not from the bounds as
/// written (spec 5.1, decision 2026-11-15; review code-quality-semantics 13): <c>0..256</c> counts with a u8.
/// </summary>
public class LoopCounterTypeTests
{
    /// <summary>The values of <c>for i in from..to step step</c> (or <c>..=</c>), in C#.</summary>
    private static List<long> Values(long from, long to, bool inclusive, long step)
    {
        var values = new List<long>();
        for (long i = from; step > 0 ? (inclusive ? i <= to : i < to) : (inclusive ? i >= to : i > to); i += step)
            values.Add(i);
        return values;
    }

    [Theory]
    [InlineData(0, 256, false, 1, 1)]
    [InlineData(0, 255, true, 1, 1)]
    [InlineData(0, 256, false, 2, 1)]
    [InlineData(0, 256, false, 3, 1)]
    [InlineData(1, 256, false, 5, 1)]
    [InlineData(0, 300, true, 127, 1)]
    [InlineData(200, 256, false, 50, 1)]
    [InlineData(255, 0, true, -1, 1)]
    [InlineData(1, 257, false, 1, 2)]
    [InlineData(250, 260, false, 3, 2)]
    [InlineData(0, 256, true, 1, 2)]
    [InlineData(300, 0, true, -100, 2)]
    public void The_counter_has_the_type_of_the_values_it_takes(long from, long to, bool inclusive, long step, int size)
    {
        var values = Values(from, to, inclusive, step);
        string range = $"{from}..{(inclusive ? "=" : "")}{to}{(step != 1 ? $" step {step}" : "")}";
        foreach (var reg in size == 1 ? new[] { "", " using X", " using Y" } : [""])
        {
            var r = Run.Program($$"""
                var n: u16
                var sum: u16
                var last: u16
                var wide: bool
                fn main() {
                    for i in {{range}}{{reg}} {
                        n += 1
                        sum += i as u16
                        last = i as u16
                    }
                }
                """);
            Assert.Equal(values.Count, r.U16("n"));
            Assert.Equal((int)(values.Sum() & 0xFFFF), r.U16("sum"));
            Assert.Equal((int)values[^1], r.U16("last"));
        }
        // The counter's type: a u8 counter fits a u8 variable with no 'as'.
        var assign = $"var b: u8\nfn main() {{\n    for i in {range} {{ b = i }}\n}}";
        if (size == 1) TernCompiler.Compile(assign);
        else Assert.Contains("Expected u8, found u16", Assert.Throws<CompileError>(() => TernCompiler.Compile(assign)).Description);
    }

    [Fact]
    public void A_whole_page_is_walked_with_a_u8_in_x()
    {
        var r = Run.Program("""
            var buf: [u8; 256]
            fn main() {
                for i in 0..len(buf) using X { buf[i] = i + 1 }
            }
            """);
        for (int k = 0; k < 256; k++) Assert.Equal((k + 1) & 0xFF, r.U8("buf", k));
        var lines = Listing.Lines(r);
        Assert.Contains(lines, l => l.Contains(" inx "));
        Assert.Contains(lines, l => l.Contains(" bne "));
        Assert.DoesNotContain(lines, l => l.Contains(" cpx "));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(200, 1)]
    [InlineData(255, 1)]
    [InlineData(0, 3)]
    [InlineData(250, 2)]
    [InlineData(251, 2)]
    [InlineData(100, 127)]
    public void A_u8_start_to_256_counts_with_a_u8_to_the_end_of_the_page(int start, int step)
    {
        // Decision 2026-11-15 (question 172): start..256 and start..len(page) with a u8 start.
        var values = Values(start, 256, false, step);
        foreach (var end in new[] { "256", "len(page)" })
            foreach (var reg in new[] { "", " using X", " using Y" })
            {
                var r = Run.Program($$"""
                    var page: [u8; 256]
                    var start: u8 = {{start}}
                    var n: u16
                    var sum: u16
                    var last: u8
                    fn main() {
                        for i in start..{{end}}{{(step != 1 ? $" step {step}" : "")}}{{reg}} {
                            n += 1
                            sum += i as u16
                            last = i
                        }
                    }
                    """);
                Assert.Equal(values.Count, r.U16("n"));
                Assert.Equal((int)(values.Sum() & 0xFFFF), r.U16("sum"));
                Assert.Equal((int)values[^1], r.U8("last"));
            }
    }

    [Fact]
    public void A_u8_start_to_256_ends_with_inx_and_bne()
    {
        var r = Run.Program("""
            var page: [u8; 256]
            var start: u8 = 3
            fn main() {
                for i in start..256 using X { page[i] = 1 }
            }
            """);
        for (int k = 0; k < 256; k++) Assert.Equal(k >= 3 ? 1 : 0, r.U8("page", k));
        var lines = Listing.Lines(r);
        Assert.Contains(lines, l => l.Contains(" inx "));
        Assert.Contains(lines, l => l.Contains(" bne "));
        Assert.DoesNotContain(lines, l => l.Contains(" cpx "));
    }

    [Theory]
    [InlineData("start..=256")]
    [InlineData("start..257")]
    public void Other_ends_past_255_keep_the_type_of_the_variable_bound(string range)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile($$"""
            var start: u8
            fn main() {
                for i in {{range}} { }
            }
            """));
        Assert.Contains("does not fit in", e.Description);
    }

    [Fact]
    public void A_typed_constant_bound_too_large_for_its_type_gives_a_wider_counter()
    {
        var r = Run.Program("""
            const BOX: u8 = 13
            var n: u16
            fn main() {
                for i in 0..BOX * 30 { n += 1 }
            }
            """);
        Assert.Equal(390, r.U16("n"));
    }
}
