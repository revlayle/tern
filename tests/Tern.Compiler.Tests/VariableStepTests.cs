using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>M9: a u8 loop counting up with a variable u8 step (spec 5.1).</summary>
public class VariableStepTests
{
    /// <summary>What the loop does: the counter goes on while adding the step does not pass 255 and stays in range.</summary>
    private static (int Count, int Sum, int Last) Expected(int from, int to, bool inclusive, int step)
    {
        int count = 0, sum = 0, last = 0;
        for (int i = from; inclusive ? i <= to : i < to; i += step)
        {
            count++; sum += i; last = i;
            if (i + step > 255) break;
            if (count > 1000) break;   // a step of 0 never ends; the tests do not use one
        }
        return (count, sum & 0xFFFF, last);
    }

    [Theory]
    [InlineData(0, 255, true, 1)]
    [InlineData(0, 255, true, 128)]
    [InlineData(0, 255, true, 255)]
    [InlineData(255, 255, true, 1)]
    [InlineData(255, 255, true, 255)]
    [InlineData(250, 255, true, 4)]
    [InlineData(250, 255, true, 5)]
    [InlineData(4, 255, true, 7)]
    [InlineData(2, 200, true, 3)]
    [InlineData(2, 200, false, 3)]
    [InlineData(2, 200, false, 198)]
    [InlineData(2, 200, true, 198)]
    [InlineData(10, 10, true, 5)]
    [InlineData(10, 10, false, 5)]
    [InlineData(0, 255, false, 128)]
    [InlineData(0, 254, true, 127)]
    public void Variable_step_visits_the_right_values(int from, int to, bool inclusive, int step)
    {
        var expected = Expected(from, to, inclusive, step);
        // The constant end gives the bound check at compile time ('..=255' needs no compare); the variable one the general shape.
        foreach (var end in new[] { to.ToString(), "e" })
        foreach (var counter in new[] { "", " using X", " using Y" })
        {
            var r = Run.Program($$"""
                var s: u8 = {{step}}
                var e: u8 = {{to}}
                var count: u16
                var sum: u16
                var last: u16
                fn main() {
                    var n: u8 = s
                    for i in {{from}}{{(inclusive ? "..=" : "..")}}{{end}} step n{{counter}} {
                        count += 1
                        sum += i
                        last = i
                    }
                }
                """);
            Assert.Equal(expected.Count, r.U16("count"));
            Assert.Equal(expected.Sum, r.U16("sum"));
            Assert.Equal(expected.Last, r.U16("last"));
        }
    }

    [Fact]
    public void The_step_is_evaluated_once_even_if_the_body_changes_it()
    {
        var r = Run.Program("""
            var n: u8 = 50
            var count: u8
            fn main() {
                for i in 0..=255 step n {
                    n = 1
                    count += 1
                }
            }
            """);
        Assert.Equal(6, r.U8("count"));   // 0, 50, 100, 150, 200, 250
    }

    [Fact]
    public void The_step_may_be_an_expression_and_a_start_may_use_it_too()
    {
        var r = Run.Program("""
            var n: u8 = 5
            var count: u8
            fn main() {
                for m in (n << 1)..=255 step n + 1 {
                    count += 1
                }
            }
            """);
        Assert.Equal(1 + (255 - 10) / 6, r.U8("count"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void An_inclusive_255_loop_ends_on_the_carry_with_no_compare()
    {
        var r = Run.Program("""
            var n: u8 = 3
            var a: [u8; 256]
            fn main() {
                for m in 3..=255 step n using Y {
                    a[m] = 1
                }
            }
            """);
        Assert.Contains("adc", r.Result.Listing);
        var lines = r.Result.Listing.Split('\n').Select(l => l.Trim()).ToList();
        int adc = lines.FindIndex(l => l.Contains(" adc "));
        Assert.Contains(" tya", lines[adc - 2]);
        Assert.Contains(" clc", lines[adc - 1]);
        Assert.Contains(" tay", lines[adc + 1]);
        Assert.Contains(" bcc ", lines[adc + 2]);
    }

    [Theory]
    [InlineData("var n: i8 = 2\nfor i in 0..=255 step n { }", "cannot be negative")]
    [InlineData("var n: u16 = 2\nfor i in 0..=255 step n { }", "must be a u8")]
    [InlineData("var n: u8 = 2\nfor i in 0..=1000 step n { }", "needs a u8 loop counter")]
    [InlineData("var n: u8 = 2\nfor i in 255..=0 step n { }", "'255..=0' counts up, so it never runs. Use a constant negative step to count down")]
    [InlineData("var n: u8 = 2\nvar a: u8 = 9\nfor i in a..0 step n { }", "'a..0' counts up, so it never runs")]
    public void Variable_step_errors(string body, string message)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile($"fn main() {{\n{body}\n}}"));
        Assert.Contains(message, e.Message);
    }
}
