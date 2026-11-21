namespace Tern.Compiler.Tests;

/// <summary>
/// M20 follow-up 3, spec 5: calls and reads of <c>volatile</c> memory in one statement happen left to
/// right: operands, arguments, and a call in an index before the value. <c>f(k)</c> logs k and returns it.
/// </summary>
public class EvaluationOrderTests
{
    private const string Logger = """
        var log: [u8; 16]
        var n: u8
        volatile var vol: u8
        var r: u8
        var w: u16
        var a: [u8; 8]
        var aw: [u16; 8]
        var buf: [u8; 8]
        var p: ptr u8

        fn f(k: u8) -> u8 {
            log[n] = k
            n += 1
            vol = 99
            return k
        }
        fn g(k: u8) -> u16 {
            log[n] = k
            n += 1
            return (k as u16) << 8 | k
        }
        fn add(x: u8, y: u8) -> u8 { return x + y }
        fn radd(x: u8 using A, y: u8 using X) -> u8 {
            var t: u8 = x
            return t + y
        }
        fn diff(x: u8, y: u8) -> u8 { return f(x) - f(y) }
        fn ge(x: u8, y: u8) -> bool using carry { return f(x) >= f(y) }
        fn lt(x: u8, y: u8) -> bool { return f(x) < f(y) }
        """;

    private static Run Program(string body) => Run.Program(Logger + "\nfn main() {\n    vol = 10\n    p = addr(buf)\n" + body + "\n}");

    private static string Log(Run run) => string.Join(",", Enumerable.Range(0, run.U8("n")).Select(i => run.U8("log", i)));

    [Theory]
    [InlineData("r = f(1) | f(2)", "1,2", 3)]
    [InlineData("r = f(1) + f(2)", "1,2", 3)]
    [InlineData("r = f(5) - f(2)", "5,2", 3)]
    [InlineData("r = f(2) - f(5)", "2,5", 253)]
    [InlineData("r = f(6) & f(3)", "6,3", 2)]
    [InlineData("r = f(6) ^ f(3)", "6,3", 5)]
    [InlineData("r = (f(1) + 1) << 2 | f(2)", "1,2", 10)]            // the port's speck kinds
    [InlineData("r = f(1) + f(2) + f(3)", "1,2,3", 6)]
    [InlineData("r = f(1) - (f(2) + f(3))", "1,2,3", 252)]
    [InlineData("r = f(f(1) + f(2))", "1,2,3", 3)]
    [InlineData("r = add(f(1), f(2))", "1,2", 3)]
    [InlineData("r = add(f(1), add(f(2), f(3)))", "1,2,3", 6)]
    [InlineData("r = radd(f(1), f(2))", "1,2", 3)]
    [InlineData("r = diff(7, 3)", "7,3", 4)]
    [InlineData("r = (g(1) - g(2)).hi", "1,2", 254)]
    [InlineData("w = g(1) + g(2)\n r = w.lo", "1,2", 3)]
    [InlineData("w = g(3) - g(1)\n r = w.hi", "3,1", 2)]
    [InlineData("w = g(1) | (f(2) as u16)\n r = w.lo", "1,2", 3)]
    public void Operands_and_arguments_run_left_to_right(string body, string log, int result)
    {
        var run = Program(body);
        Assert.Equal(log, Log(run));
        Assert.Equal(result, run.U8("r"));
    }

    [Theory]
    [InlineData("f(1) < f(2)", true)]
    [InlineData("f(1) > f(2)", false)]
    [InlineData("f(1) <= f(2)", true)]
    [InlineData("f(2) >= f(1)", true)]
    [InlineData("f(1) == f(2)", false)]
    [InlineData("f(1) != f(2)", true)]
    [InlineData("(f(1) as i8) > (f(255) as i8)", true)]                // 1 > -1
    [InlineData("(f(255) as i8) < (f(1) as i8)", true)]
    [InlineData("g(1) < g(2)", true)]
    [InlineData("g(2) > g(1)", true)]
    [InlineData("g(1) == g(1)", true)]
    public void Both_sides_of_a_compare_run_left_to_right(string condition, bool holds)
    {
        var run = Program($"if {condition} {{ r = 1 }} else {{ r = 2 }}");
        var calls = System.Text.RegularExpressions.Regex.Matches(condition, @"[fg]\((\d+)\)").Select(m => m.Groups[1].Value);
        Assert.Equal(string.Join(",", calls), Log(run));
        Assert.Equal(holds ? 1 : 2, run.U8("r"));
    }

    /// <summary>The places review code-quality-tests finding 1 names: the bounds and step of a for, a compare returned as a flag.</summary>
    [Theory]
    [InlineData("for _ in 2..f(5) { r += 1 }", "5", 3)]
    [InlineData("for _ in 0..20 step f(3) { r += 1 }", "3", 7)]
    [InlineData("if lt(1, 2) { r = 1 } else { r = 2 }", "1,2", 1)]
    [InlineData("if lt(2, 1) { r = 1 } else { r = 2 }", "2,1", 2)]
    [InlineData("for _ in f(1)..f(5) { r += 1 }", "1,5", 4)]
    [InlineData("for _ in f(1)..=f(4) { r += 1 }", "1,4", 4)]
    [InlineData("for _ in f(1)..f(9) step f(2) { r += 1 }", "1,9,2", 4)]
    [InlineData("for _ in f(1)..20 step f(5) { r += 1 }", "1,5", 4)]
    [InlineData("if ge(1, 2) { r = 1 } else { r = 2 }", "1,2", 2)]
    [InlineData("if ge(2, 1) { r = 1 } else { r = 2 }", "2,1", 1)]
    public void Calls_in_loop_bounds_and_returned_compares_run_left_to_right(string body, string log, int result)
    {
        var run = Program(body);
        Assert.Equal(log, Log(run));
        Assert.Equal(result, run.U8("r"));
    }

    [Fact]
    public void A_call_in_an_index_runs_before_the_value()
    {
        var run = Program("a[f(1)] = f(2)");
        Assert.Equal("1,2", Log(run));
        Assert.Equal(2, run.U8("a", 1));

        run = Program("a[f(3)] = f(5) - f(1)");
        Assert.Equal("3,5,1", Log(run));
        Assert.Equal(4, run.U8("a", 3));

        run = Program("aw[f(2)] = g(7)");
        Assert.Equal("2,7", Log(run));
        Assert.Equal(0x0707, run.U16At("aw", 2, 8));

        run = Program("p[f(4)] = f(6)");
        Assert.Equal("4,6", Log(run));
        Assert.Equal(6, run.U8("buf", 4));

        run = Program("aw[f(2)].hi = f(9)");
        Assert.Equal("2,9", Log(run));
    }

    [Fact]
    public void A_compound_assignment_with_a_call_keeps_its_result()
    {
        var run = Program("a[3] = 40\n var i: u8 = 3\n a[i] += f(2)\n a[i] -= f(5)\n r = a[3]");
        Assert.Equal("2,5", Log(run));
        Assert.Equal(37, run.U8("r"));
    }

    /// <summary>f sets <c>vol</c> to 99, so the value read shows whether the read came before the call.</summary>
    [Theory]
    [InlineData("r = vol + f(2)", 12)]
    [InlineData("r = f(2) + vol", 101)]
    [InlineData("r = vol - f(2)", 8)]
    [InlineData("r = add(vol, f(2))", 12)]
    [InlineData("r = add(f(2), vol)", 101)]
    [InlineData("r = radd(vol, f(2))", 12)]
    [InlineData("r = radd(f(2), vol)", 101)]
    [InlineData("w = (vol as u16) + g(2)\n r = w.lo", 12)]
    [InlineData("a[vol & 7] = f(1)\n r = a[2]", 1)]                  // index 10 & 7 = 2, read before f
    [InlineData("if vol < f(50) { r = 1 }", 1)]
    [InlineData("if f(50) < vol { r = 1 }", 1)]
    public void A_volatile_read_and_a_call_run_left_to_right(string body, int result)
    {
        Assert.Equal(result, Program(body).U8("r"));
    }

    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData("r = v1 - (v2 & 3)")]
    [InlineData("r = (v1 & 3) | (v2 & 3)")]
    [InlineData("if v1 > v2 { r = 1 }")]
    [InlineData("w = (v1 as u16) - ((v2 as u16) << 1)")]
    public void Two_volatile_reads_run_left_to_right(string body)
    {
        var result = TernCompiler.Compile($$"""
            volatile var v1: u8 @ $D012
            volatile var v2: u8 @ $D013
            var r: u8
            var w: u16
            fn main() { {{body}} }
            """);
        var main = Listing.Instructions(result);
        int first = main.FindIndex(i => i.EndsWith(" v1"));
        int second = main.FindIndex(i => i.EndsWith(" v2"));
        Assert.True(first >= 0 && second > first, Listing.Code(result));
    }

    [Fact]
    public void Two_calls_cost_the_same_as_a_local_for_the_first()
    {
        // The left call's result goes to a temporary, as it would to a local written by hand.
        static int Bytes(string body) => TernCompiler.Compile(Logger + "\nfn main() {\n" + body + "\n}").File.Length;
        Assert.Equal(Bytes("var t: u8 = f(1)\n r = t + f(2)"), Bytes("r = f(1) + f(2)"));
        Assert.Equal(Bytes("var t: u8 = f(1)\n r = t | f(2)"), Bytes("r = f(1) | f(2)"));
        Assert.Equal(Bytes("var t: u8 = f(1)\n r = t - f(2)"), Bytes("r = f(1) - f(2)"));
    }
}
