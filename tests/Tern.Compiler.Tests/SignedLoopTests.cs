using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>M20: <c>for</c> loops with i8 and i16 counters (spec 5.1, 11), against a C# model.</summary>
public class SignedLoopTests
{
    /// <summary>The values a counter takes: from <paramref name="from"/> by <paramref name="step"/>, stopping at the end or the type's limit.</summary>
    private static List<long> Expected(long from, long to, bool inclusive, int step, long min, long max)
    {
        var values = new List<long>();
        for (long v = from; step > 0 ? (inclusive ? v <= to : v < to) : (inclusive ? v >= to : v > to); v += step)
        {
            values.Add(v);
            if (v + step > max || v + step < min) break;
        }
        return values;
    }

    /// <summary>
    /// Runs <c>for i in header</c> with the bounds a and b set, recording each value of i. With
    /// <paramref name="call"/> the body calls a function, so the counter stays in memory.
    /// </summary>
    private static List<long?> Values(string type, string header, long a, long b, bool call)
    {
        string body = call ? "record(i)" : type == "i8" ? "lo[n] = i as u8\n        n += 1\n        total += 1" : "lo[n] = (i as u16).lo\n        hi[n] = (i as u16).hi\n        n += 1\n        total += 1";
        var r = Run.Program($$"""
            var a: {{type}} = {{a}}
            var b: {{type}} = {{b}}
            var lo: [u8; 256]
            var hi: [u8; 256]
            var n: u8
            var total: u16
            fn record(v: {{type}}) {
                lo[n] = (v as u16).lo
                hi[n] = (v as u16).hi
                n += 1
                total += 1
            }
            fn main() {
                for i in {{header}} {
                    {{body}}
                }
            }
            """, maxCycles: 20_000_000);
        // The log keeps the last 256 values; the earlier ones read as null.
        int total = r.U16("total");
        var values = new List<long?>();
        for (int k = 0; k < total; k++)
        {
            int raw = r.U8("lo", k & 0xFF) | r.U8("hi", k & 0xFF) << 8;
            values.Add(k < total - 256 ? null : type == "i8" ? (sbyte)raw : (short)raw);
        }
        return values;
    }

    private static List<long?> Logged(List<long> expected) =>
        expected.Select((v, k) => k < expected.Count - 256 ? null : (long?)v).ToList();

    [Theory]
    // Constant bounds, step ±1.
    [InlineData("-10..10", 0, 0, -10, 10, false, 1)]
    [InlineData("-128..=127", 0, 0, -128, 127, true, 1)]
    [InlineData("127..=-128 step -1", 0, 0, 127, -128, true, -1)]
    [InlineData("5..=-5 step -1", 0, 0, 5, -5, true, -1)]
    [InlineData("-1..=-128 step -1", 0, 0, -1, -128, true, -1)]
    [InlineData("-128..-1", 0, 0, -128, -1, false, 1)]
    [InlineData("10..-10 step -1", 0, 0, 10, -10, false, -1)]
    // Constant bounds, larger steps, also past 127 and -128.
    [InlineData("-5..5 step 3", 0, 0, -5, 5, false, 3)]
    [InlineData("-128..=127 step 2", 0, 0, -128, 127, true, 2)]
    [InlineData("100..=-100 step -7", 0, 0, 100, -100, true, -7)]
    [InlineData("-120..=127 step 100", 0, 0, -120, 127, true, 100)]
    [InlineData("120..=-128 step -100", 0, 0, 120, -128, true, -100)]
    // Variable bounds.
    [InlineData("a..b", -3, 4, -3, 4, false, 1)]
    [InlineData("a..b", 4, -3, 4, -3, false, 1)]
    [InlineData("a..b", -128, 127, -128, 127, false, 1)]
    [InlineData("a..b", 5, 5, 5, 5, false, 1)]
    [InlineData("a..=b", -128, 127, -128, 127, true, 1)]
    [InlineData("a..=b", 127, 127, 127, 127, true, 1)]
    [InlineData("a..=b", 0, -1, 0, -1, true, 1)]
    [InlineData("a..=b", -1, 0, -1, 0, true, 1)]
    [InlineData("a..=b", 100, -100, 100, -100, true, 1)]
    [InlineData("a..=b step -1", 3, -3, 3, -3, true, -1)]
    [InlineData("a..=b step -1", 127, -128, 127, -128, true, -1)]
    [InlineData("a..=b step -1", -3, 3, -3, 3, true, -1)]
    [InlineData("a..b step -1", 3, -3, 3, -3, false, -1)]
    [InlineData("a..=127", -128, 0, -128, 127, true, 1)]
    [InlineData("a..=127", 127, 0, 127, 127, true, 1)]
    [InlineData("-128..=b", 0, -128, -128, -128, true, 1)]
    [InlineData("-5..=b", 0, 3, -5, 3, true, 1)]
    [InlineData("-5..=b", 0, -6, -5, -6, true, 1)]
    [InlineData("a..=0 step -1", 5, 0, 5, 0, true, -1)]
    [InlineData("a..=0 step -1", 0, 0, 0, 0, true, -1)]
    [InlineData("a..=0 step -1", -1, 0, -1, 0, true, -1)]
    [InlineData("a..=0 step -1", 127, 0, 127, 0, true, -1)]
    [InlineData("a..=0 step -1", -128, 0, -128, 0, true, -1)]
    [InlineData("a..0 step -1", 5, 0, 5, 0, false, -1)]
    [InlineData("a..0 step -1", 0, 0, 0, 0, false, -1)]
    [InlineData("a..0 step -1", -1, 0, -1, 0, false, -1)]
    [InlineData("a..0 step -1", 127, 0, 127, 0, false, -1)]
    // Variable bounds, larger steps: the V flag ends the loop past 127 or -128.
    [InlineData("a..b step 3", -5, 5, -5, 5, false, 3)]
    [InlineData("a..b step 3", 5, -5, 5, -5, false, 3)]
    [InlineData("a..=b step 3", -128, 127, -128, 127, true, 3)]
    [InlineData("a..=b step 3", 120, 127, 120, 127, true, 3)]
    [InlineData("a..=b step 3", 124, 126, 124, 126, true, 3)]
    [InlineData("a..b step 100", -100, 127, -100, 127, false, 100)]
    [InlineData("a..=b step -3", 127, -128, 127, -128, true, -3)]
    [InlineData("a..=b step -3", -120, -128, -120, -128, true, -3)]
    [InlineData("a..b step -3", 5, -5, 5, -5, false, -3)]
    [InlineData("a..b step -3", 5, 5, 5, 5, false, -3)]
    [InlineData("a..=0 step -2", 7, 0, 7, 0, true, -2)]
    [InlineData("a..=0 step -2", -7, 0, -7, 0, true, -2)]
    [InlineData("a..=100 step 50", -128, 0, -128, 100, true, 50)]
    public void An_i8_counter_takes_the_values_of_its_range(string header, int a, int b, int from, int to, bool inclusive, int step)
    {
        var expected = Logged(Expected(from, to, inclusive, step, -128, 127));
        Assert.Equal(expected, Values("i8", header, a, b, call: false));
        Assert.Equal(expected, Values("i8", header, a, b, call: true));
    }

    [Theory]
    [InlineData("-1000..1000 step 7", 0, 0, -1000, 1000, false, 7)]
    [InlineData("-200..=100", 0, 0, -200, 100, true, 1)]
    [InlineData("200..=-50 step -1", 0, 0, 200, -50, true, -1)]
    [InlineData("-32700..=-32768 step -1", 0, 0, -32700, -32768, true, -1)]
    [InlineData("-32768..-32700", 0, 0, -32768, -32700, false, 1)]
    [InlineData("a..b", -100, 100, -100, 100, false, 1)]
    [InlineData("a..b", 100, -100, 100, -100, false, 1)]
    [InlineData("a..=b", -1, 1, -1, 1, true, 1)]
    [InlineData("a..=b", 32700, 32767, 32700, 32767, true, 1)]
    [InlineData("a..=32767", 32600, 0, 32600, 32767, true, 1)]
    [InlineData("a..=b step -1", 50, -50, 50, -50, true, -1)]
    [InlineData("a..=b step -1", -32700, -32768, -32700, -32768, true, -1)]
    [InlineData("a..=0 step -1", 100, 0, 100, 0, true, -1)]
    [InlineData("a..=0 step -1", -100, 0, -100, 0, true, -1)]
    [InlineData("a..=0 step -1", 256, 0, 256, 0, true, -1)]
    [InlineData("a..0 step -1", 256, 0, 256, 0, false, -1)]
    [InlineData("a..0 step -1", 0, 0, 0, 0, false, -1)]
    [InlineData("a..0 step -1", -256, 0, -256, 0, false, -1)]
    [InlineData("a..b step 100", -32768, 32767, -32768, 32767, false, 100)]
    [InlineData("a..=b step 127", 32000, 32767, 32000, 32767, true, 127)]
    [InlineData("a..=b step -127", -32000, -32768, -32000, -32768, true, -127)]
    [InlineData("a..b step 3", 10, -10, 10, -10, false, 3)]
    public void An_i16_counter_takes_the_values_of_its_range(string header, int a, int b, int from, int to, bool inclusive, int step)
    {
        var expected = Logged(Expected(from, to, inclusive, step, -32768, 32767));
        Assert.Equal(expected, Values("i16", header, a, b, call: false));
        Assert.Equal(expected, Values("i16", header, a, b, call: true));
    }

    [Fact]
    public void A_negative_constant_makes_the_counter_i8_or_i16()
    {
        var r = Run.Program("""
            var n: i8 = -3
            var w: i16 = -300
            var s1: i16
            var s2: i16
            var s3: i16
            fn main() {
                for i in -10..10 { s1 += i }
                for i in -200..n { s2 += i }
                for i in w..=-299 { s3 += i }
            }
            """);
        Assert.Equal(Enumerable.Range(-10, 20).Sum(), r.I16("s1"));
        Assert.Equal(Enumerable.Range(-200, 197).Sum(), r.I16("s2"));
        Assert.Equal(-599, r.I16("s3"));
    }

    [Fact]
    public void Loops_with_unnamed_signed_counters_repeat()
    {
        var r = Run.Program("""
            var a: i8 = -100
            var b: i8 = 100
            var c1: u8
            var c2: u8
            var c3: u16
            fn main() {
                for _ in -10..10 { c1 += 1 }
                for _ in a..=b { c2 += 1 }
                for _ in -300..300 { c3 += 1 }
            }
            """);
        Assert.Equal((20, 201, 600), (r.U8("c1"), r.U8("c2"), r.U16("c3")));
    }

    // Spec 11 rows.
    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_constant_signed_range_is_the_unsigned_code()
    {
        var r = Run.Program("""
            volatile var out: i8
            fn main() {
                for i in -10..10 using X { out = i }
            }
            """);
        Assert.Equal("ldx #$F6|BODY|inx|cpx #$0A|bne|rts", Loop(Listing.Code(r)));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Down_to_zero_tests_the_sign_of_the_start()
    {
        var r = Run.Program("""
            var n: i8 = 3
            volatile var out: i8
            fn main() {
                for i in n..=0 step -1 using X { out = i }
                for i in n..0 step -1 using X { out = i }
            }
            """);
        Assert.Equal("ldx n|bmi|BODY|dex|bpl|ldx n|beq|bmi|BODY|dex|bne|rts", Loop(Listing.Code(r)));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Variable_bounds_are_checked_once_with_a_signed_compare()
    {
        var r = Run.Program("""
            fn f(a: i8, b: i8) {
                for i in a..b using X { out = i }
            }
            volatile var out: i8
            fn main() { f(-1, 1) }
            """);
        Assert.Equal("ldx f.a|txa|sec|sbc f.b|bvc|eor #$80|bpl|BODY|inx|cpx f.b|bne|rts", Loop(Listing.Code(r, "f")));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_larger_step_with_a_variable_end_stops_on_overflow()
    {
        var r = Run.Program("""
            fn f(a: i8, b: i8) {
                for i in a..b step 3 using X { out = i }
            }
            volatile var out: i8
            fn main() { f(-1, 1) }
            """);
        var code = string.Join("|", Loop(Listing.Code(r, "f")).Split('|').Select(i => i.StartsWith("cmp ") || i.StartsWith("sta ") ? i[..3] : i));
        Assert.StartsWith("lda f.b|eor #$80|sta|", code);
        Assert.EndsWith("|BODY|clc|adc #$03|bvs|tax|eor #$80|cmp|bcc|rts", code);
    }

    /// <summary>The loop's code with branches shown without labels, and the body (out = i, as txa / sta out) as BODY.</summary>
    private static string Loop(string code) => string.Join("|", code.Split('|').Select(Mnemonic)).Replace("txa|sta out", "BODY");

    /// <summary>A branch without its label, so the tests do not depend on label numbers.</summary>
    private static string Mnemonic(string instruction) =>
        instruction.Split(' ')[0] is "bcc" or "bcs" or "beq" or "bne" or "bmi" or "bpl" or "bvc" or "bvs" or "jmp" ? instruction.Split(' ')[0] : instruction;

    [Theory]
    [InlineData("var n: u8\nfn main() { for i in -5..n { } }", "A loop from a negative bound counts with a signed variable, but the other bound is u8. Convert it with 'as i8'")]
    [InlineData("var n: u16\nfn main() { for i in n..=-5 { } }", "Convert it with 'as i16'")]
    [InlineData("var s: i8\nvar n: u8\nfn main() { for i in s..n { } }", "Cannot mix signed and unsigned")]
    [InlineData("var s: i8\nvar k: u8\nfn main() { for i in s..10 step k { } }", "A variable step needs a u8 loop counter, but the bounds are i8")]
    [InlineData("fn main() { for i in -300..300 using X { } }", "An i16 loop variable does not fit in X. Remove 'using X', or use bounds that fit in an i8")]
    [InlineData("fn main() { for i in 0..300 using Y { } }", "A u16 loop variable does not fit in Y. Remove 'using Y', or use bounds that fit in a u8")]
    public void Reports_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
    }

    [Fact]
    public void An_i8_counter_may_use_y()
    {
        var r = Run.Program("""
            var s: i16
            fn main() {
                for i in -3..=3 using Y { s += i }
            }
            """);
        Assert.Equal(0, r.I16("s"));
    }
}
