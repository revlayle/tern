namespace Tern.Compiler.Tests;

public class ProgramTests
{
    [Fact]
    public void Assigns_globals()
    {
        var r = Run.Program("""
            var x: u8
            var w: u16 = 1000
            var b: bool
            fn main() {
                x = 42
                w = w + 234
                b = true
            }
            """);
        Assert.Equal(42, r.U8("x"));
        Assert.Equal(1234, r.U16("w"));
        Assert.Equal(1, r.U8("b"));
    }

    [Fact]
    public void Eight_bit_arithmetic_wraps()
    {
        var r = Run.Program("""
            var a: u8 = 200
            var b: u8 = 100
            var sum: u8
            var diff: u8
            var bits: u8
            var shl: u8
            var shr: u8
            var neg: u8
            var inv: u8
            fn main() {
                sum = a + b
                diff = b - a
                bits = (a & $F0) | (b ^ $0F)
                shl = b << 2
                shr = a >> 3
                neg = -b
                inv = ~a
            }
            """);
        Assert.Equal((200 + 100) & 0xFF, r.U8("sum"));
        Assert.Equal((100 - 200) & 0xFF, r.U8("diff"));
        Assert.Equal((200 & 0xF0) | (100 ^ 0x0F), r.U8("bits"));
        Assert.Equal((100 << 2) & 0xFF, r.U8("shl"));
        Assert.Equal(200 >> 3, r.U8("shr"));
        Assert.Equal(-100 & 0xFF, r.U8("neg"));
        Assert.Equal(~200 & 0xFF, r.U8("inv"));
    }

    [Fact]
    public void Signed_shift_right_keeps_sign()
    {
        var r = Run.Program("""
            var a: i8 = -3
            var b: i16 = -1000
            var ra: i8
            var rb: i16
            fn main() {
                ra = a >> 1
                rb = b >> 3
            }
            """);
        Assert.Equal(-2, r.I8("ra"));
        Assert.Equal(-1000 >> 3, r.I16("rb"));
    }

    [Fact]
    public void Sixteen_bit_arithmetic_and_widening()
    {
        var r = Run.Program("""
            var w: u16 = 40000
            var small: u8 = 200
            var s8: i8 = -5
            var sum: u16
            var diff: u16
            var shl: u16
            var shr: u16
            var widened: u16
            var signed: i16
            var lo: u8
            var hi: u8
            fn main() {
                sum = w + small
                diff = small - w
                shl = w << 1
                shr = w >> 4
                widened = small
                signed = s8
                lo = w.lo
                hi = w.hi
                w += 1
            }
            """);
        Assert.Equal(40200, r.U16("sum"));
        Assert.Equal((200 - 40000) & 0xFFFF, r.U16("diff"));
        Assert.Equal((40000 << 1) & 0xFFFF, r.U16("shl"));
        Assert.Equal(40000 >> 4, r.U16("shr"));
        Assert.Equal(200, r.U16("widened"));
        Assert.Equal(-5, r.I16("signed"));
        Assert.Equal(40000 & 0xFF, r.U8("lo"));
        Assert.Equal(40000 >> 8, r.U8("hi"));
        Assert.Equal(40001, r.U16("w"));
    }

    public static TheoryData<string, long, long> ComparisonCases()
    {
        var data = new TheoryData<string, long, long>();
        long[] u8 = [0, 1, 127, 128, 254, 255];
        long[] i8 = [-128, -1, 0, 1, 127];
        long[] u16 = [0, 255, 256, 32767, 32768, 65535];
        long[] i16 = [-32768, -256, -1, 0, 1, 255, 32767];
        foreach (var (type, values) in new[] { ("u8", u8), ("i8", i8), ("u16", u16), ("i16", i16) })
            foreach (var a in values)
                foreach (var b in values)
                    data.Add(type, a, b);
        return data;
    }

    [Theory]
    [MemberData(nameof(ComparisonCases))]
    public void Comparisons_match_math(string type, long a, long b)
    {
        var r = Run.Program($$"""
            var a: {{type}} = {{a}}
            var b: {{type}} = {{b}}
            var results: [u8; 6]
            fn main() {
                if a == b { results[0] = 1 }
                if a != b { results[1] = 1 }
                if a < b { results[2] = 1 }
                if a <= b { results[3] = 1 }
                if a > b { results[4] = 1 }
                if a >= b { results[5] = 1 }
            }
            """);
        bool[] expected = [a == b, a != b, a < b, a <= b, a > b, a >= b];
        for (int i = 0; i < 6; i++)
            Assert.True(expected[i] == (r.U8("results", i) == 1), $"{type} {a} vs {b}, comparison #{i}");
    }

    [Fact]
    public void Logical_operators_short_circuit()
    {
        var r = Run.Program("""
            var calls: u8
            var r1: bool
            var r2: bool
            var r3: bool
            fn count() -> bool {
                calls += 1
                return true
            }
            fn main() {
                r1 = false && count()
                r2 = true || count()
                r3 = !(1 > 2) && count()
            }
            """);
        Assert.Equal(1, r.U8("calls"));
        Assert.Equal(0, r.U8("r1"));
        Assert.Equal(1, r.U8("r2"));
        Assert.Equal(1, r.U8("r3"));
    }

    [Fact]
    public void While_loop_with_break_and_continue()
    {
        var r = Run.Program("""
            var sum: u16
            fn main() {
                var i: u8 = 0
                while true {
                    i += 1
                    if i == 50 { break }
                    if i & 1 == 0 { continue }
                    sum += i
                }
            }
            """);
        Assert.Equal(Enumerable.Range(1, 49).Where(i => i % 2 == 1).Sum(), r.U16("sum"));
    }

    [Theory]
    [InlineData("0..10", 0, 10, false, 1)]
    [InlineData("0..=9", 0, 9, true, 1)]
    [InlineData("9..=0 step -1", 9, 0, true, -1)]
    [InlineData("10..0 step -1", 10, 0, false, -1)]
    [InlineData("0..=255", 0, 255, true, 1)]
    [InlineData("255..=0 step -1", 255, 0, true, -1)]
    [InlineData("200..=0 step -1", 200, 0, true, -1)]
    [InlineData("5..5", 5, 5, false, 1)]
    [InlineData("5..=5", 5, 5, true, 1)]
    [InlineData("0..40 step 8", 0, 40, false, 8)]
    [InlineData("0..=40 step 8", 0, 40, true, 8)]
    [InlineData("250..=255 step 4", 250, 255, true, 4)]
    [InlineData("40..=0 step -8", 40, 0, true, -8)]
    [InlineData("3..=1 step -1", 3, 1, true, -1)]
    [InlineData("0..300", 0, 300, false, 1)]
    [InlineData("1000..=990 step -1", 1000, 990, true, -1)]
    [InlineData("0..1000 step 100", 0, 1000, false, 100)]
    public void Constant_for_loops_visit_the_right_values(string range, int from, int to, bool inclusive, int step)
    {
        var expected = Expected(from, to, inclusive, step);
        var r = Run.Program($$"""
            var count: u16
            var sum: u16
            var last: u16
            fn main() {
                for i in {{range}} {
                    count += 1
                    sum += i
                    last = i
                }
            }
            """);
        AssertLoop(r, expected);
    }

    [Theory]
    [InlineData(0, 10, false, 1)]
    [InlineData(10, 10, false, 1)]
    [InlineData(11, 10, false, 1)]
    [InlineData(3, 7, true, 1)]
    [InlineData(7, 7, true, 1)]
    [InlineData(8, 7, true, 1)]
    [InlineData(250, 255, true, 1)]
    [InlineData(0, 255, true, 1)]
    [InlineData(0, 0, true, 1)]
    [InlineData(1, 200, false, 3)]
    [InlineData(250, 255, true, 4)]
    [InlineData(250, 255, false, 4)]
    public void Variable_bound_loops_counting_up(int from, int to, bool inclusive, int step)
    {
        var expected = Expected(from, to, inclusive, step);
        var r = Run.Program($$"""
            var a: u8 = {{from}}
            var b: u8 = {{to}}
            var count: u16
            var sum: u16
            var last: u16
            fn main() {
                for i in a{{(inclusive ? "..=" : "..")}}b{{(step != 1 ? $" step {step}" : "")}} {
                    count += 1
                    sum += i
                    last = i
                }
            }
            """);
        AssertLoop(r, expected);
    }

    [Theory]
    [InlineData(10, 0, false, -1)]
    [InlineData(0, 0, false, -1)]
    [InlineData(10, 0, true, -1)]
    [InlineData(0, 0, true, -1)]
    [InlineData(255, 0, true, -1)]
    [InlineData(10, 3, true, -1)]
    [InlineData(3, 3, true, -1)]
    [InlineData(2, 3, true, -1)]
    [InlineData(10, 3, false, -1)]
    [InlineData(100, 0, true, -7)]
    [InlineData(3, 0, true, -5)]
    public void Variable_bound_loops_counting_down(int from, int to, bool inclusive, int step)
    {
        var expected = Expected(from, to, inclusive, step);
        var r = Run.Program($$"""
            var a: u8 = {{from}}
            var b: u8 = {{to}}
            var count: u16
            var sum: u16
            var last: u16
            fn main() {
                for i in a{{(inclusive ? "..=" : "..")}}b step {{step}} {
                    count += 1
                    sum += i
                    last = i
                }
            }
            """);
        AssertLoop(r, expected);
    }

    [Theory]
    [InlineData("X")]
    [InlineData("Y")]
    public void Register_loop_variable(string reg)
    {
        var r = Run.Program($$"""
            var values: [u8; 20]
            var n: u8 = 20
            fn main() {
                for i in 0..n using {{reg}} {
                    values[i] = i + 100
                }
                for i in 19..=0 step -1 using {{reg}} {
                    values[i] += 1
                }
            }
            """);
        for (int i = 0; i < 20; i++) Assert.Equal(i + 101, r.U8("values", i));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(256)]
    [InlineData(1000)]
    public void Repeat_loops(int times)
    {
        var r = Run.Program($$"""
            var count: u16
            fn main() {
                for _ in 0..{{times}} { count += 1 }
            }
            """);
        Assert.Equal(times, r.U16("count"));
    }

    [Fact]
    public void Arrays_of_u8_and_split_u16()
    {
        var r = Run.Program("""
            var bytes: [u8; 4] = [10, 20, 30, 40]
            var words: [u16; 3] = [1000, 2000, 3000]
            var total: u16
            var copy: [u16; 3]
            fn main() {
                for i in 0..4 { total += bytes[i] }
                for i in 0..3 {
                    total += words[i]
                    copy[i] = words[i] + 1
                }
                words[1].hi = 0
            }
            """);
        Assert.Equal(10 + 20 + 30 + 40 + 1000 + 2000 + 3000, r.U16("total"));
        for (int i = 0; i < 3; i++) Assert.Equal((i + 1) * 1000 + 1, r.U16At("copy", i, 3));
        Assert.Equal(2000 & 0xFF, r.U16At("words", 1, 3));
    }

    [Fact]
    public void Functions_with_memory_and_register_parameters()
    {
        var r = Run.Program("""
            var r1: u8
            var r2: u16
            var r3: u16
            var r4: u8
            fn add(a: u8, b: u8) -> u8 { return a + b }
            fn plot(x: u8 using X, y: u8 using Y) -> u8 using Y { return x + y }
            fn wide(a: u16 using AX, b: u16) -> u16 { return a + b }
            fn twice(v: u8) -> u8 { return add(v, v) }
            fn main() {
                r1 = add(3, twice(add(1, 2)))
                r2 = wide(1000, 2345)
                r3 = wide(wide(1, 2), wide(3, 4))
                r4 = plot(40, 2)
            }
            """);
        Assert.Equal(3 + 2 * (1 + 2), r.U8("r1"));
        Assert.Equal(3345, r.U16("r2"));
        Assert.Equal(10, r.U16("r3"));
        Assert.Equal(42, r.U8("r4"));
    }

    [Fact]
    public void Bool_returns_in_flags()
    {
        var r = Run.Program("""
            var hits: u8
            var stored: bool
            fn is_big(v: u8 using A) -> bool { return v >= 100 }
            fn is_zero(v: u8) -> bool using zero { return v == 0 }
            fn is_neg(v: i8) -> bool using negative { return v < 0 }
            fn main() {
                if is_big(150) { hits += 1 }
                if is_big(50) { hits += 10 }
                if is_zero(0) { hits += 2 }
                if !is_zero(5) { hits += 4 }
                if is_neg(-5) { hits += 8 }
                stored = is_big(200)
            }
            """);
        Assert.Equal(1 + 2 + 4 + 8, r.U8("hits"));
        Assert.Equal(1, r.U8("stored"));
    }

    [Fact]
    public void Fixed_address_and_zero_page_variables()
    {
        var r = Run.Program("""
            var border: u8 @ $D020
            var fast: u16 @ zp
            fn main() {
                border = 6
                fast = $1234
            }
            """);
        Assert.Equal(0xD020, r.Address("border"));
        Assert.Equal(6, r.Ram.Bytes[0xD020]);
        Assert.True(r.Address("fast") < 0x100);
        Assert.Equal(0x1234, r.U16("fast"));
    }

    [Fact]
    public void Functions_that_never_run_together_share_memory()
    {
        var r = Run.Program("""
            var out1: u8
            var out2: u8
            fn first(a: u8) -> u8 { var t: u8 = a + 1; return t }
            fn second(b: u8) -> u8 { var u: u8 = b + 2; return u }
            fn outer(c: u8) -> u8 { return first(c) + second(c) }
            fn main() {
                out1 = outer(10)
                out2 = first(1)
            }
            """);
        Assert.Equal(11 + 12, r.U8("out1"));
        Assert.Equal(2, r.U8("out2"));
        // first and second are siblings: they may share. outer calls both: it must not share with either.
        Assert.Equal(r.Address("first.a"), r.Address("second.b"));
        Assert.NotEqual(r.Address("outer.c"), r.Address("first.a"));
        Assert.NotEqual(r.Address("outer.c"), r.Address("second.b"));
    }

    [Fact]
    public void Constants_fold_at_compile_time()
    {
        var r = Run.Program("""
            const W = 40
            const H = 25
            const SIZE = W * H
            const MASK: u8 = $F0
            var size: u16
            var mask: u8
            fn main() {
                size = SIZE
                mask = MASK >> 4
            }
            """);
        Assert.Equal(1000, r.U16("size"));
        Assert.Equal(0x0F, r.U8("mask"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Branches_longer_than_127_bytes_are_rewritten()
    {
        var body = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"        w = w + {i + 1}"));
        var r = Run.Program($$"""
            var w: u16
            var flag: u8 = 1
            var after: u8
            fn main() {
                if flag == 1 {
            {{body}}
                }
                after = 7
            }
            """);
        Assert.Equal(Enumerable.Range(1, 40).Sum(), r.U16("w"));
        Assert.Equal(7, r.U8("after"));
        Assert.Contains("jmp main.endif", r.Result.Listing);
    }

    private static (int Count, int Sum, int Last) Expected(int from, int to, bool inclusive, int step)
    {
        int count = 0, sum = 0, last = 0;
        for (long i = from; step > 0 ? (inclusive ? i <= to : i < to) : (inclusive ? i >= to : i > to); i += step)
        {
            count++;
            sum += (int)i;
            last = (int)i;
        }
        return (count, sum & 0xFFFF, last);
    }

    private static void AssertLoop(Run r, (int Count, int Sum, int Last) expected)
    {
        Assert.Equal(expected.Count, r.U16("count"));
        Assert.Equal(expected.Sum, r.U16("sum"));
        Assert.Equal(expected.Last, r.U16("last"));
    }
}
