using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// <c>const fn</c> and compile-time evaluation (spec 4.1): int, real, bool and arrays, lists built with
/// <c>for</c>, what a const fn reads, the step limit, and the errors.
/// </summary>
public sealed class ConstFnTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tern");

    public void Dispose() => _dir.Delete(recursive: true);

    private string MainPath => Path.Combine(_dir.FullName, "main.tern");

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_dir.FullName, name), text);

    private Run RunMain(string source) => Run.Program(source, sourcePath: MainPath);

    private static byte[] Bytes(Run r, string name, int count) => r.Ram.Bytes.AsSpan(r.Address(name), count).ToArray();

    private static CompileError Error(string source) => Assert.Throws<CompileError>(() => TernCompiler.Compile(source));

    private CompileError ErrorInMain(string source) => Assert.Throws<CompileError>(() => TernCompiler.Compile(source, sourcePath: MainPath));

    /// <summary>C#'s rounding for round(): halves away from zero.</summary>
    private static long Round(double x) => (long)Math.Round(x, MidpointRounding.AwayFromZero);

    // ---------- Done when (roadmap M22, part 1) ----------

    [Fact]
    public void A_sine_table_and_a_row_address_table_built_by_const_fns_from_a_module_match_CSharp()
    {
        Write("tables.tern", """
            const fn sine(n: int) -> [int; _] {
                var t = repeat(0, n)
                for i in 0..n {
                    t[i] = round(128 + 127 * sin(i * TAU / n))
                }
                return t
            }

            const fn row_addr(row: int) -> int {
                return $0400 + row * 40
            }
            """);
        var r = RunMain("""
            import tables
            data sine: [u8; _] = tables.sine(256)
            data rows: [u16; 25] = [tables.row_addr(i) for i in 0..25]
            var a: u8
            var b: u16
            fn main() {
                a = sine[64]
                b = rows[24]
            }
            """);
        var expected = Enumerable.Range(0, 256).Select(i => (byte)Round(128 + 127 * Math.Sin(i * Math.Tau / 256))).ToArray();
        Assert.Equal(expected, Bytes(r, "sine", 256));
        for (int i = 0; i < 25; i++) Assert.Equal(0x0400 + i * 40, r.U16At("rows", i, 25));
        Assert.Equal(expected[64], r.U8("a"));
        Assert.Equal(0x0400 + 24 * 40, r.U16("b"));
    }

    // ---------- Lists built with for ----------

    [Theory]
    [InlineData("[i for i in 0..5]", new[] { 0, 1, 2, 3, 4 })]
    [InlineData("[i for i in 0..=5]", new[] { 0, 1, 2, 3, 4, 5 })]
    [InlineData("[i for i in 5..=0 step -1]", new[] { 5, 4, 3, 2, 1, 0 })]
    [InlineData("[i for i in 5..0 step -1]", new[] { 5, 4, 3, 2, 1 })]
    [InlineData("[i + 2 for i in -2..=0]", new[] { 0, 1, 2 })]
    [InlineData("[i for i in 0..10 step 3]", new[] { 0, 3, 6, 9 })]
    [InlineData("[i for i in 0..=9 step 3]", new[] { 0, 3, 6, 9 })]
    [InlineData("[i for i in 10..=0 step -4]", new[] { 10, 6, 2 })]
    [InlineData("[i * i for i in 0..6]", new[] { 0, 1, 4, 9, 16, 25 })]
    [InlineData("[round(i * 1.5) for i in 0..5]", new[] { 0, 2, 3, 5, 6 })]
    [InlineData("repeat(7, 3)", new[] { 7, 7, 7 })]
    [InlineData("[N - i for i in 0..N]", new[] { 4, 3, 2, 1 })]
    [InlineData("[1 for _ in 0..2]", new[] { 1, 1 })]
    public void A_list_built_with_for_fills_a_table(string list, int[] expected)
    {
        var r = Run.Program($"const N = 4\ndata t: [u8; _] = {list}\nfn main() {{ }}");
        Assert.Equal(expected.Select(v => (byte)v).ToArray(), Bytes(r, "t", expected.Length));
    }

    [Fact]
    public void A_list_fills_a_var_global_and_a_signed_table_and_gives_len()
    {
        var r = Run.Program("""
            var v: [u8; 4] = [i + 10 for i in 0..4]
            data s: [i8; _] = [i - 2 for i in 0..4]
            data w: [i16; _] = [i * -1000 for i in 0..3]
            const N = len([0 for _ in 0..9])
            var n: u8
            fn main() { n = N + v[3] }
            """);
        Assert.Equal([10, 11, 12, 13], Bytes(r, "v", 4));
        Assert.Equal([0xFE, 0xFF, 0, 1], Bytes(r, "s", 4));
        Assert.Equal(-2000, (short)r.U16At("w", 2, 3));
        Assert.Equal(9 + 13, r.U8("n"));
    }

    [Theory]
    [InlineData("data t: [u8; 4] = [i for i in 0..5]", "Expected 4 values, found 5")]
    [InlineData("data t: [u8; _] = [i * 100 for i in 0..4]", "Element 3 is 300, which does not fit in u8 (0 to 255)")]
    [InlineData("data t: [i8; _] = [i - 200 for i in 0..4]", "Element 0 is -200, which does not fit in i8 (-128 to 127)")]
    [InlineData("data t: [u8; _] = [i * 0.5 for i in 0..4]", "Element 0 is the real number 0.0, which needs rounding to fit in u8. Use round(x), floor(x), ceil(x) or trunc(x)")]
    [InlineData("data t: [u8; _] = [i < 2 for i in 0..4]", "Element 0 is a bool, but the table holds u8")]
    [InlineData("data t = [i for i in 0..4]", "Data arrays need a written type. Write data t: [u8; _] = [...]")]
    [InlineData("data t: [u8; _] = [i for i in 0..4 for j in 0..2]", "A list built with 'for' has one 'for'. Write a const fn with nested loops for more")]
    [InlineData("data t: [u8; _] = [i for i in 0..4 if i > 1]", "A list built with 'for' has no filter. Write a const fn with a loop and an 'if' for that")]
    [InlineData("data t: [u8; _] = [i for i in 0..10 step 0]", "The step is 0, so the loop would never end. Use a step other than 0")]
    [InlineData("data t: [u16; _] = [i for i in 0..1000]", "An array of u16 has at most 256 elements")]
    [InlineData("const N = [i for i in 0..4]", "'N' is an array, which a const cannot hold. Use data, e.g. 'data N: [u8; _] = ...'")]
    [InlineData("data t: [u8; _] = [1, 2.5]", "This value is the real number 2.5, but an int is expected here")]
    [InlineData("data t: [u8; _] = [i for i in 0..4]\nfn main2() { t[0] = 1 }", "'t' is data, which is read-only")]
    public void List_errors(string source, string expected)
    {
        var e = Error(source + "\nfn main() { }");
        Assert.Contains(expected, e.Description);
    }

    [Fact]
    public void A_list_built_with_for_in_runtime_code_is_an_error()
    {
        var e = Error("fn main() {\n    var a: [u8; 4] = [i for i in 0..4]\n}");
        Assert.Equal("A list built with 'for' is computed at compile time. Use a 'for' loop to fill the array at runtime", e.Description);
        Assert.Equal(2, e.Pos.Line);
        var d = Tern.Compiler.Tools.Diagnostic.From(e, "main.tern");
        Assert.Equal("Use a 'for' loop to fill the array at runtime", d.Suggestion);
    }

    [Fact]
    public void A_long_byte_table_may_come_from_a_const_fn()
    {
        var r = Run.Program("""
            const fn ramp(n: int) -> [int; _] {
                var r = repeat(0, n)
                for i in 0..n { r[i] = i & $FF }
                return r
            }
            data t: [u8; _] = ramp(1000)
            fn main() { }
            """);
        Assert.Equal(Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray(), Bytes(r, "t", 1000));
    }

    // ---------- int and real ----------

    [Theory]
    [InlineData("round(2.5)", 3)]
    [InlineData("round(-2.5)", -3)]
    [InlineData("round(2.4999)", 2)]
    [InlineData("floor(-1.5)", -2)]
    [InlineData("ceil(-1.5)", -1)]
    [InlineData("ceil(1.01)", 2)]
    [InlineData("trunc(-1.7)", -1)]
    [InlineData("trunc(1.7)", 1)]
    [InlineData("round(7)", 7)]
    [InlineData("7 / 2", 3)]
    [InlineData("-7 / 2", -3)]
    [InlineData("-7 % 2", -1)]
    [InlineData("round(7 / 2.0)", 4)]
    [InlineData("round(2e3)", 2000)]
    [InlineData("round(1_000.5)", 1001)]
    [InlineData("round(25e-1)", 3)]
    [InlineData("floor(PI * 1000)", 3141)]
    [InlineData("floor(TAU * 1000)", 6283)]
    [InlineData("round(1.5) + 1", 3)]
    [InlineData("abs(-5)", 5)]
    [InlineData("round(abs(-2.5) * 2)", 5)]
    [InlineData("min(4, 2, 9)", 2)]
    [InlineData("max(4, 2, 9)", 9)]
    [InlineData("round(max(1, 2.5) * 2)", 5)]
    [InlineData("($1234).hi", 0x12)]
    [InlineData("sizeof(P)", 3)]
    public void Compile_time_math_in_a_constant(string value, long expected)
    {
        var r = Run.Program($"struct P {{ x: u8, y: u16 }}\nconst K = {value}\nvar v: i16\nfn main() {{ v = K }}");
        Assert.Equal(expected, r.I16("v"));
    }

    [Fact]
    public void The_math_functions_match_CSharp()
    {
        // Each function at several values, scaled by 1000 and rounded into an i16 table.
        (string Tern, Func<double, double> CSharp)[] functions =
        [
            ("sin(x)", Math.Sin), ("cos(x)", Math.Cos), ("tan(x)", Math.Tan), ("atan2(x, 2)", x => Math.Atan2(x, 2)),
            ("sqrt(x)", Math.Sqrt), ("pow(x, 1.5)", x => Math.Pow(x, 1.5)), ("exp(x)", Math.Exp), ("log(x)", Math.Log),
            ("log2(x)", Math.Log2), ("abs(x - 2)", x => Math.Abs(x - 2)), ("min(x, 1.25)", x => Math.Min(x, 1.25)), ("max(x, 1.25)", x => Math.Max(x, 1.25)),
        ];
        double[] xs = [0.25, 0.5, 1, 1.5, 2.75];
        foreach (var (tern, csharp) in functions)
        {
            var r = Run.Program($$"""
                const fn f(x: real) -> real { return {{tern}} }
                data t: [i16; _] = [round(1000 * f(XS[i] / 100.0)) for i in 0..5]
                data XS: [u16; _] = [25, 50, 100, 150, 275]
                fn main() { }
                """);
            for (int i = 0; i < xs.Length; i++)
                Assert.True(Round(1000 * csharp(xs[i])) == (short)r.U16At("t", i, 5), $"{tern} at {xs[i]}");
        }
    }

    [Fact]
    public void Real_literals_do_not_change_ranges_and_member_access()
    {
        var r = Run.Program("""
            data t: [u8; 3] = [1, 2, 3]
            var n: u8
            var h: u8
            fn main() {
                for i in 0..3 { n += t[i] }
                h = ($1234).lo
            }
            """);
        Assert.Equal(6, r.U8("n"));
        Assert.Equal(0x34, r.U8("h"));
    }

    [Theory]
    [InlineData("const K: u8 = 1.5 * 2", "'K' is the real number 3.0, which has no runtime type. Remove the type to keep a real constant, or use round(x), floor(x), ceil(x) or trunc(x)")]
    [InlineData("const K = [1, 2]", "'K' is an array, which a const cannot hold. Use data")]
    [InlineData("const K = 1 / 0.0", "Division by zero")]
    [InlineData("const K = round(1.0 / 0)", "Division by zero")]
    [InlineData("const K = round(sqrt(-1))", "sqrt() of a negative number (-1) has no real value. Use a number of 0 or more")]
    [InlineData("const K = round(log(0))", "log() of 0 has no real value. Use a number above 0")]
    [InlineData("const K = round(log2(-2.5))", "log2() of -2.5 has no real value. Use a number above 0")]
    [InlineData("const K = round(pow(0, -1))", "pow() gives a real too large for 64 bits. Use smaller values")]
    [InlineData("const K = round(1e300 * 1e300)", "'*' gives a real too large for 64 bits")]
    [InlineData("const K = round(1e30)", "round() gives 1E+30, outside the signed 64-bit range of int. Use a smaller value")]
    [InlineData("const K = round(5.5 % 2)", "'%' works on ints, not reals")]
    [InlineData("const K = round(1.5 & 1)", "'&' works on ints, not reals")]
    [InlineData("const K = round(true)", "round() needs a number, not a bool")]
    [InlineData("const K = sin(1, 2)", "sin() takes 1 argument, not 2")]
    [InlineData("const K = round(min(1.5))", "min() takes two or more numbers")]
    [InlineData("const K = round(2e400)", "The real number 2e400 is too large. Use a value below 1.7e308")]
    [InlineData("const K = label()", "A label() value is only for emit in a generator")]
    [InlineData("const K = round(\"a\")", "A string is not a compile-time value")]
    public void Real_errors(string source, string expected)
    {
        var e = Error(source + "\nfn main() { }");
        Assert.Contains(expected, e.Description);
    }

    // ---------- Real constants (decision 2026-11-11, question 94) ----------

    [Fact]
    public void A_real_constant_is_used_in_a_const_fn_a_list_and_another_constant()
    {
        Write("waves.tern", """
            const AMP = 100.5
            """);
        var r = RunMain("""
            import waves
            const SCALE = 1.5
            const K = PI / 128
            const DOUBLE = SCALE * 2
            const fn scaled(n: int) -> int {
                return round(n * SCALE)
            }
            data t: [u8; _] = [round(i * DOUBLE) for i in 0..4]
            data s: [u8; _] = [round(waves.AMP * sin(i * K * 64)) for i in 0..3]
            const N = scaled(7)
            var a: u8
            var b: u8
            fn main() {
                const HALF = 0.5
                const H = round(HALF * 9)
                a = N
                b = H
            }
            """);
        Assert.Equal([0, 3, 6, 9], Bytes(r, "t", 4));
        Assert.Equal([0, (byte)Round(100.5), 0], Bytes(r, "s", 3));
        Assert.Equal(11, r.U8("a"));
        Assert.Equal(5, r.U8("b"));
    }

    [Theory]
    [InlineData("x = SCALE", "'SCALE' is a real number, which exists only at compile time. Use it in a constant or a const fn, e.g. 'const SCALE_40 = round(SCALE * 40)'")]
    [InlineData("x = x * SCALE", "'SCALE' is a real number, which exists only at compile time")]
    [InlineData("asm { lda #SCALE }", "'SCALE' is a real number, which exists only at compile time")]
    [InlineData("const L = 0.25\n    x = L", "'L' is a real number, which exists only at compile time")]
    public void A_real_constant_in_runtime_code_is_an_error(string statement, string expected)
    {
        var e = Error($"const SCALE = 1.5\nfn main() {{\n    var x: u8 = 1\n    {statement}\n}}");
        Assert.StartsWith(expected, e.Description);
    }

    [Fact]
    public void A_real_constant_in_a_global_initial_value_needs_rounding()
    {
        var e = Error("const SCALE = 1.5\nvar x: u8 = SCALE\nfn main() { }");
        Assert.StartsWith("This value is the real number 1.5, but an int is expected here. Use round(x)", e.Description);
        Assert.Equal(3, Run.Program("const SCALE = 1.5\nvar x: u8 = round(SCALE * 2)\nfn main() { }").U8("x"));
    }

    [Theory]
    [InlineData("var x: u8 = 1.5", "1.5 is a real number, which exists only at compile time. Use a constant or a const fn to compute the value, e.g. 'const K = round(1.5 * 40)'")]
    [InlineData("var x: u8 = round(2.5)", "round() works only at compile time. Use it in a constant, in data, in a global's initial value or in a const fn")]
    [InlineData("var x: u8 = 3\n    x = x * PI", "'PI' is a real number, which exists only at compile time")]
    [InlineData("var x: u8 = f(2)", "'f' is a const fn, which runs only at compile time. Use it in a constant, in data or in a global's initial value")]
    [InlineData("var a: [u8; 3] = repeat(1, 3)", "'repeat' builds an array at compile time. Use a 'for' loop to fill the array at runtime")]
    [InlineData("var x: u8 = len(repeat(1, 3))", "'repeat' builds an array at compile time")]
    [InlineData("asm { lda #round(1.5) }", "round() works only at compile time")]
    public void Compile_time_code_in_a_function_body_is_an_error(string statement, string expected)
    {
        var e = Error($"const fn f(n: int) -> int {{ return n }}\nfn main() {{\n    {statement}\n}}");
        Assert.StartsWith(expected, e.Description);
    }

    // ---------- repeat(value, count) (decision 2026-11-11, question 97) ----------

    [Fact]
    public void Repeat_fills_data_a_global_and_a_const_fn_array()
    {
        var r = RunMain("""
            const N = 3
            const fn ramp(n: int) -> [int; _] {
                var t = repeat(0, n)
                var none = repeat(7, 0)
                for i in 0..n { t[i] = i + len(none) }
                return t
            }
            const fn count(t: [int; _]) -> int { return len(t) }
            data spaces: [u8; 4] = repeat($20, 4)
            data flags: [bool; _] = repeat(true, N)
            var words: [u16; _] = repeat(1000, 2)
            data r: [u8; _] = ramp(4)
            const EMPTY = count(repeat(1, 0))
            var e: u8 = EMPTY
            fn main() { }
            """);
        Assert.Equal([0x20, 0x20, 0x20, 0x20], Bytes(r, "spaces", 4));
        Assert.Equal([1, 1, 1], Bytes(r, "flags", 3));
        Assert.Equal(1000, r.U16At("words", 0, 2));
        Assert.Equal(1000, r.U16At("words", 1, 2));
        Assert.Equal([0, 1, 2, 3], Bytes(r, "r", 4));
        Assert.Equal(0, r.U8("e"));
    }

    [Theory]
    [InlineData("data t: [u8; 4] = [7; 4]", "'[7; 4]' is not an array in Tern, where [T; n] is only an array type. Write repeat(7, 4)")]
    [InlineData("data t: [u8; _] = [N + 1; f(N)]", "'[N + 1; f(N)]' is not an array in Tern, where [T; n] is only an array type. Write repeat(N + 1, f(N))")]
    [InlineData("data t: [u8; _] = ['A'; 4]", "'['A'; 4]' is not an array in Tern, where [T; n] is only an array type. Write repeat('A', 4)")]
    [InlineData("const fn g() -> [int; _] { return [0; -1] }", "'[0; -1]' is not an array in Tern")]
    [InlineData("data t: [u8; _] = repeat(1, 0)", "Array length must be 1 to 256, not 0")]
    [InlineData("data t: [u8; 4] = repeat(1, 5)", "Expected 4 values, found 5")]
    [InlineData("data t: [u8; _] = repeat(300, 2)", "Element 0 is 300, which does not fit in u8")]
    [InlineData("data t: [u8; _] = repeat(1)", "repeat() takes 2 arguments, not 1")]
    [InlineData("data t: [u8; _] = repeat(1, 1.5)", "The count of repeat() is a real here, but needs an int")]
    [InlineData("data t: [u8; _] = repeat(1, 70000)", "An array has 0 to 65536 elements, not 70000. Use a smaller count")]
    public void Repeat_errors(string source, string expected)
    {
        var e = Error("const N = 2\nconst fn f(n: int) -> int { return n }\n" + source + "\nfn main() { }");
        Assert.Contains(expected, e.Description);
    }

    [Fact]
    public void A_local_constant_may_use_compile_time_code()
    {
        var r = Run.Program("""
            const fn twice(n: int) -> int { return n * 2 }
            var v: u8
            fn main() {
                const K = round(2.5 * twice(4))
                v = K
            }
            """);
        Assert.Equal(20, r.U8("v"));
    }

    // ---------- Statements and values in a const fn ----------

    [Fact]
    public void A_const_fn_runs_all_statements()
    {
        var r = Run.Program("""
            const fn collatz(start: int) -> int {
                var n = start
                var steps = 0
                while n != 1 {
                    if n % 2 == 0 { n = n / 2 } else { n = 3 * n + 1 }
                    steps += 1
                }
                return steps
            }
            const fn primes(count: int) -> [int; _] {
                var out = repeat(0, count)
                var found = 0
                var k = 2
                while true {
                    var prime = true
                    for d in 2..k {
                        if d * d > k { break }
                        if k % d == 0 {
                            prime = false
                            break
                        }
                    }
                    if prime {
                        out[found] = k
                        found += 1
                        if found == count { return out }
                    }
                    k += 1
                }
                return out
            }
            const fn kind(n: int) -> int {
                match n {
                    0 => return 10
                    1, 2 => return 20
                    3..=7 => return 30
                    _ => { }
                }
                return 40
            }
            const fn odd_sum(n: int) -> int {
                var s = 0
                for i in 0..n {
                    if i % 2 == 0 { continue }
                    s += i
                }
                return s
            }
            const fn bits(n: int) -> int {
                var x = n
                x <<= 4
                x |= 3
                x ^= 1
                x &= $FF
                x >>= 1
                x -= 1
                return x
            }
            const fn grid() -> int {
                var g: [[int; _]; _] = repeat(repeat(0, 3), 2)
                g[1][2] = 5
                var z: [int; 4]
                z[3] = len(g) + len(g[0])
                return g[1][2] * 10 + z[3]
            }
            const fn halves(n: int) -> real {
                const HALF = 0.5
                var r: real = n
                r = r * HALF
                return r
            }
            const fn is_even(n: int) -> bool { return n % 2 == 0 }
            data chain: [u8; 1] = [collatz(27)]
            data p: [u8; _] = primes(8)
            data kinds: [u8; _] = [kind(i) for i in 0..9]
            const ODD = odd_sum(10)
            const BITS = bits(13)
            const GRID = grid()
            const HALVES = round(halves(5))
            const EVEN = is_even(4) && !is_even(3)
            assert EVEN, "is_even"
            var v: [u8; 5]
            fn main() {
                v[0] = ODD
                v[1] = BITS
                v[2] = GRID
                v[3] = HALVES
                if EVEN { v[4] = 1 }
            }
            """);
        Assert.Equal(111, r.U8("chain"));
        Assert.Equal([2, 3, 5, 7, 11, 13, 17, 19], Bytes(r, "p", 8));
        Assert.Equal([10, 20, 20, 30, 30, 30, 30, 30, 40], Bytes(r, "kinds", 9));
        Assert.Equal(1 + 3 + 5 + 7 + 9, r.U8("v"));
        Assert.Equal((((13 << 4) | 3) ^ 1 & 0xFF) / 2 - 1, r.U8("v", 1));
        Assert.Equal(55, r.U8("v", 2));
        Assert.Equal(3, r.U8("v", 3));
        Assert.Equal(1, r.U8("v", 4));
    }

    [Fact]
    public void A_list_of_int_arrays_and_real_arrays_is_a_list_of_real_arrays()
    {
        var r = Run.Program("""
            const fn f() -> int {
                var g = [[1, 2], [1.5]]
                return round(g[0][1] * 10 + g[1][0] * 100)
            }
            const K = f()
            var v: u8
            fn main() { v = K }
            """);
        Assert.Equal(170, r.U8("v"));
    }

    [Fact]
    public void An_error_the_checker_raises_in_a_module_const_fn_names_the_main_files_line()
    {
        Write("tables.tern", "const fn width() -> int {\n    return limit\n}\nvar limit: u8 = 3\n");
        var e = ErrorInMain("import tables\n\nconst K = tables.width()\nfn main() { }");
        Assert.Equal(Path.Combine(_dir.FullName, "tables.tern"), e.Pos.File);
        Assert.Equal("'limit' is a variable, which a const fn cannot read (called from main.tern:3). Use a const or data", e.Description);
    }

    [Fact]
    public void Arrays_are_values_and_ints_never_wrap()
    {
        var r = Run.Program("""
            const fn changed(t: [int; _]) -> int {
                var copy = t
                copy[0] = 99
                t[1] = 98
                return t[0] * 1000 + copy[0]
            }
            const fn big() -> int {
                var x = $FFFF
                x = x * $10000
                return x >> 24
            }
            const A = changed([1, 2])
            const B = big()
            var a: u16
            var b: u8
            fn main() {
                a = A
                b = B
            }
            """);
        Assert.Equal(1099, r.U16("a"));
        Assert.Equal(0xFF, r.U8("b"));
    }

    [Fact]
    public void A_const_fn_calls_const_fns_and_recursion_works()
    {
        var r = Run.Program("""
            const fn fact(n: int) -> int {
                if n <= 1 { return 1 }
                return n * fact(n - 1)
            }
            const fn square(n: int) -> int { return n * n }
            const fn sum_squares(n: int) -> int {
                var s = 0
                for i in 1..=n { s += square(i) }
                return s
            }
            var a: u16
            var b: u16
            fn main() {
                a = fact(8) / 10
                b = sum_squares(10)
            }
            """.Replace("a = fact(8) / 10", "a = FACT").Replace("b = sum_squares(10)", "b = SUMS") + "\nconst FACT = fact(8) / 10\nconst SUMS = sum_squares(10)");
        Assert.Equal(4032, r.U16("a"));
        Assert.Equal(385, r.U16("b"));
    }

    // ---------- What a const fn reads ----------

    [Fact]
    public void A_const_fn_reads_constants_parameters_sizeof_and_data()
    {
        Write("level.bin", "\u0001\u0002\u0003\u0004");
        Write("settings.tern", """
            module settings(COUNT: u8 = 4)
            const fn count() -> int { return COUNT }
            """);
        var r = RunMain("""
            import settings(COUNT = 6)
            struct Enemy { x: u8, y: u8, hp: u16 }
            const WIDTH: u8 = 40
            const ON = true
            data bytes: [u8; 3] = [1, 200, 3]
            data signed: [i8; 2] = [-1, -128]
            data words: [u16; 2] = [1000, 65535]
            data flags: [bool; 2] = [false, true]
            data file = include_bytes("level.bin")
            const fn total() -> int {
                var s = 0
                for i in 0..len(bytes) { s += bytes[i] }
                for i in 0..len(file) { s += file[i] * 1000 }
                return s
            }
            const fn mix() -> int {
                var n = WIDTH * sizeof(Enemy) + settings.count() * 1000 + signed[0] + signed[1] * 10
                if ON && flags[1] && !flags[0] { n += words[1] * 100000 + words[0] * 10000000000 }
                return n
            }
            data v: [i16; 1] = [0]
            const TOTAL = total()
            const MIX = mix()
            var t: u16
            var m: u16
            fn main() {
                t = TOTAL
                m = MIX % 65536
            }
            """);
        Assert.Equal(204 + 10 * 1000, r.U16("t"));
        long mix = 40 * 4 + 6 * 1000 - 1 - 1280 + 65535 * 100000L + 1000 * 10000000000L;
        Assert.Equal(mix % 65536, r.U16("m"));
    }

    [Theory]
    [InlineData("var score: u8 = 3\nconst fn f() -> int { return score }\nconst K = f()", "'score' is a variable, which a const fn cannot read (called from line 3). Use a const or data")]
    [InlineData("var score: u8 = 3\nconst K = round(score * 1.5)", "'score' is a variable, whose value is not known at compile time. Use a const or data")]
    [InlineData("data t: [u16; 1] = [addr(t)]\nconst fn f() -> int { return t[0] }\nconst K = f()", "'t' holds addresses, which are decided after compile-time evaluation (called from line 3). Read a table without addr()")]
    [InlineData("var x: u8\nconst fn f() -> int { return addr(x) }\nconst K = f()", "The value 'f' returns is a label, but an int is expected")]
    [InlineData("const fn f() -> label { return addr(x) }\nvar x: u8\nconst K = f()", "This value is a label, an address decided when the program is placed, so a constant cannot hold it. Use addr() in the initial value of a var or data, or pass the label to a generator")]
    [InlineData("fn g() { }\nconst fn f() -> label { return addr(g) }\nconst K = f()", "addr(g) is a label, decided when the program is placed, so it cannot be part of a constant (called from line 3). Use it in a generator's emit")]
    [InlineData("struct P { x: u8 }\ndata p = P { x = 1 }\nconst fn f() -> int { return p[0] }\nconst K = f()", "'p' holds structs, which are not compile-time values (called from line 4). Read a table of numbers")]
    [InlineData("fn g() -> u8 { return 1 }\nconst fn f() -> int { return g() }\nconst K = f()", "'g' runs on the 6502, so compile-time code cannot call it (called from line 3). Use a const fn")]
    [InlineData("const fn f() -> int { return K }\nconst K = f()", "'K' depends on itself: K -> K")]
    [InlineData("data t: [u8; _] = f()\nconst fn f() -> [int; _] { return [t[0]] }", "'t' depends on itself: t -> t")]
    [InlineData("const fn f() -> int { return nope }\nconst K = f()", "'nope' is not declared")]
    [InlineData("const fn f() -> int { return nope() }\nconst K = f()", "'nope' is not declared")]
    [InlineData("const fn f() -> int { return f }\nconst K = f()", "'f' is a const fn; call it with ()")]
    [InlineData("const fn f() -> int { var x = 1\n return x() }\nconst K = f()", "'x' is a variable, not a function")]
    [InlineData("const fn f() -> int { return [1, 2][5] }\nconst K = f()", "Index 5 is outside the array (length 2) (called from line 2). Use an index from 0 to 1")]
    [InlineData("const fn f() -> int { return 1 / (2 - 2) }\nconst K = f()", "Division by zero (called from line 2)")]
    [InlineData("const fn f() -> int { return 1 << 64 }\nconst K = f()", "The shift count is 64 (called from line 2). Use a count from 0 to 63")]
    [InlineData("const fn f() -> int { return $7FFF_FFFF_FFFF_FFFF + 1 }\nconst K = f()", "goes outside the signed 64-bit range")]
    [InlineData("const fn f() -> int { return 1 as u8 }\nconst K = f()", "'as' converts runtime types, which a const fn does not have")]
    [InlineData("const fn f(n: int) -> int { return n }\nconst K = f(1.5)", "The argument 'n' of 'f' is a real, but an int is expected. Use round(x), floor(x), ceil(x) or trunc(x)")]
    [InlineData("const fn f(n: int) -> int { return n }\nconst K = f(1, 2)", "'f' takes 1 argument, not 2. Pass 1 argument")]
    [InlineData("const fn f(n: int) -> int { return n }\nconst K = f(true)", "The argument 'n' of 'f' is a bool, but an int is expected")]
    [InlineData("const fn f() -> int { var x = 1\n x = 1.5\n return x }\nconst K = f()", "The value of 'x' is a real, but an int is expected")]
    [InlineData("const fn f() -> int { for i in 0..3 { i = 2 }\n return 0 }\nconst K = f()", "'i' is the loop variable, which cannot be changed (called from line 3). Declare a variable with 'var' for a value that changes")]
    [InlineData("const fn f() -> int { WIDTH = 2\n return 0 }\nconst WIDTH = 1\nconst K = f()", "'WIDTH' is not a variable of this const fn, which changes only its own variables")]
    [InlineData("const fn f() -> int { if 1 { return 1 }\n return 0 }\nconst K = f()", "An 'if' condition needs a bool, not an int (called from line 3). Compare instead, e.g. 'n != 0'")]
    [InlineData("const fn f(n: int) -> int { if n > 0 { return 1 } }\nconst K = f(0)", "'f' ends without returning a value (called from line 2). Add a 'return' at its end")]
    [InlineData("const fn f() { }\nconst K = f()", "'f' returns no value. Give it a return type, or call it on a line of its own")]
    [InlineData("const fn f(n: int) -> int { return f(n + 1) }\nconst K = f(0)", "Const fn calls are nested more than 200 deep here, through 'f'")]
    [InlineData("const fn f() -> real { return 1.5 }\nconst K: i8 = f()", "'K' is the real number 1.5, which has no runtime type")]
    [InlineData("const fn f() -> [int; _] { return [1] }\nconst T = f()", "'T' is an array, which a const cannot hold. Use data")]
    [InlineData("const fn f() -> int { return [1, true][0] }\nconst K = f()", "This element is a bool, but the list's first one is an int (called from line 2). Use one kind of value in a list")]
    [InlineData("struct P { x: u8 }\nconst fn f() -> int { var p = P { x = 1 }\n return 0 }\nconst K = f()", "A struct literal is not a compile-time value")]
    [InlineData("const fn f() -> int { return round(!1.5) }\nconst K = f()", "'!' needs a bool, not a real")]
    [InlineData("const fn f() -> int { return ~1.5 }\nconst K = f()", "'~' needs an int, not a real")]
    [InlineData("const fn f() -> int { return -true }\nconst K = f()", "'-' needs a number, not a bool")]
    [InlineData("const fn f() -> int { return 1 + true }\nconst K = f()", "'+' needs numbers, not a bool")]
    [InlineData("const fn f() -> bool { return true & false }\nconst K = f()", "'&' works on the bits of ints (called from line 2). Use '&&' for bools")]
    [InlineData("const fn f() -> int { return 5.x }\nconst K = f()", "Unknown member '.x'")]
    [InlineData("data d: [u8; 1] = [1]\nconst fn f() -> int { return len(1) }\nconst K = f()", "len() needs an array")]
    [InlineData("const fn f() -> int { return sizeof(1) }\nconst K = f()", "sizeof() takes one type name")]
    [InlineData("const fn f() -> int { return len(include_bytes(\"x.bin\")) }\nconst K = f()", "include_bytes() can only be the value of data")]
    [InlineData("const fn f() -> int { return len(repeat(0, -1)) }\nconst K = f()", "An array has 0 to 65536 elements, not -1")]
    [InlineData("const fn f() -> int { var t: [int; 70000]\n return 0 }\nconst K = f()", "An array has 0 to 65536 elements, not 70000")]
    [InlineData("const fn f() -> int { return len([0 for i in 0..70000]) }\nconst K = f()", "An array has at most 65536 elements (called from line 2). Use a shorter range")]
    [InlineData("const fn f() -> int { const C = 1\n C = 2\n return C }\nconst K = f()", "'C' is a constant, which cannot be changed")]
    [InlineData("const fn g() -> [int; _] { return [1] }\nconst fn f() -> int { g()[0] = 1\n return 0 }\nconst K = f()", "A const fn assigns only its own variables and their elements")]
    [InlineData("const fn f() -> int { match 1.5 { _ => { } }\n return 0 }\nconst K = f()", "A match value is a real here, but needs an int")]
    [InlineData("struct P { x: u8 }\nconst fn f() -> int { return P }\nconst K = f()", "'P' is a struct type, not a value")]
    [InlineData("fn g() { }\nconst fn f() -> int { return g }\nconst K = f()", "'g' is a function, not a value")]
    [InlineData("const C = 1\nconst fn f() -> int { return C() }\nconst K = f()", "'C' is a constant, not a function")]
    [InlineData("const fn f(b: bool) -> int { return 1 }\nconst K = f(1)", "The argument 'b' of 'f' is an int, but a bool is expected")]
    [InlineData("const fn f(t: [int; _]) -> int { return t[0] }\nconst K = f([1.5])", "An element of the argument 't' of 'f' is a real, but an int is expected")]
    [InlineData("const fn f(t: [int; _]) -> int { return t[0] }\nconst K = f(2)", "The argument 't' of 'f' is an int, but an array is expected")]
    [InlineData("const fn f() -> int { return round(pow(-8, 1 / 3.0)) }\nconst K = f()", "pow() has no real value for these numbers (called from line 2). Use other values")]
    [InlineData("const fn f() -> real { return 1 }\nconst K: u8 = f()", "'K' is the real number 1.0, which has no runtime type")]
    [InlineData("const fn f(a: int, b: int) -> int { return a }\nconst K = f(1)", "'f' takes 2 arguments, not 1. Pass 2 arguments")]
    [InlineData("const fn f() -> int { return -$7FFF_FFFF_FFFF_FFFF - 1 + abs(-$7FFF_FFFF_FFFF_FFFF - 1) }\nconst K = f()", "(called from line 2). Use smaller values")]
    public void Errors_while_running(string source, string expected)
    {
        var e = Error(source + "\nfn main() { }");
        Assert.Contains(expected, e.Description);
    }

    [Theory]
    [InlineData("const fn f(n: u8) -> int { return n }", "'u8' is a runtime type. Use int; the value is checked when it leaves the const fn")]
    [InlineData("const fn f() -> u16 { return 1 }", "'u16' is a runtime type. Use int; the value is checked when it leaves the const fn")]
    [InlineData("const fn f() -> int { var x: i8 = 1\n return x }", "'i8' is a runtime type")]
    [InlineData("const fn f(p: ptr u8) -> int { return 1 }", "A pointer is a runtime type. Use int for an address")]
    [InlineData("struct E { x: u8 }\nconst fn f(e: E) -> int { return 1 }", "'E' is not a compile-time type. Use int, real, bool, label or an array of them")]
    [InlineData("const fn f(t: [int; 4]) -> int { return 1 }", "A const fn takes and returns arrays of any length. Write '_' for the length, e.g. '[int; _]'")]
    [InlineData("const fn f(n: int using A) -> int { return n }", "A const fn runs at compile time, so 'n' is in no register. Remove 'using A'")]
    [InlineData("const fn f() -> int using A { return 1 }", "Remove 'using A'")]
    [InlineData("const fn f() -> int @ $1000", "A const fn runs at compile time and has no address")]
    [InlineData("const fn f() -> int { var x: int @ $1000 = 1\n return x }", "'x' exists only at compile time, so it has no address. Remove the '@'")]
    [InlineData("const fn f() -> int { asm { nop }\n return 1 }", "A const fn runs at compile time, so it cannot run assembly. Use 'emit { ... }' to generate assembly where the const fn is called")]
    [InlineData("const fn f() -> int { emit { nop }\n return 1 }", "'f' returns a value, so it cannot emit assembly")]
    [InlineData("const fn f() { return 1 }", "'f' returns no value. Remove the value, or give it a return type, e.g. 'const fn f(...) -> int'")]
    [InlineData("const fn f() -> int { return }", "'f' must return an int value")]
    [InlineData("const fn f() -> int { break }", "'break' and 'continue' must be inside a loop")]
    [InlineData("const fn f() -> int { for i in 0..3 using X { }\n return 1 }", "Remove 'using X'")]
    [InlineData("const fn f(n: int) -> int { var n = 1\n return n }", "'n' is already declared at")]
    [InlineData("const W = 1\nconst fn f() -> int { var W = 1\n return W }", "'W' is already declared at")]
    [InlineData("const fn f() -> int { 1 + 2\n return 1 }", "Only a function call can be used as a statement")]
    [InlineData("const fn f(n: int, n: int) -> int { return n }", "Parameter 'n' is declared twice")]
    [InlineData("const fn len(n: int) -> int { return n }", "'len' is a built-in function; choose another name")]
    [InlineData("const fn f() -> int { return 1 }\nfn f() { }", "'f' is already declared at")]
    [InlineData("fn g() { const fn f() -> int { return 1 } }", "'const fn' must be declared outside functions")]
    [InlineData("const fn f(true using carry) -> int { return 1 }", "A const fn's parameter is a name and a compile-time type. Write 'n: int'")]
    [InlineData("const fn f() -> int { linear var t: [int; 2]\n return 1 }", "Remove 'linear' or 'indexed'")]
    [InlineData("const fn f() -> int { var x\n return 1 }", "'x' needs an initial value. Write 'var x = ...'")]
    [InlineData("const fn f() -> int { var x: int\n return x }\nconst K = f()", "'x' needs an initial value (called from line 3). Write 'var x: int = ...'")]
    public void Errors_in_the_declaration(string source, string expected)
    {
        var e = Error(source + "\nfn main() { }");
        Assert.Contains(expected, e.Description);
    }

    [Fact]
    public void An_error_in_a_module_const_fn_names_its_file_and_the_caller()
    {
        Write("tables.tern", "const fn inverse(n: int) -> int {\n    return 1000 / n\n}\n");
        var e = ErrorInMain("import tables\n\nconst K = tables.inverse(0)\nfn main() { }");
        Assert.Equal(Path.Combine(_dir.FullName, "tables.tern"), e.Pos.File);
        Assert.Equal(2, e.Pos.Line);
        Assert.Equal("Division by zero (called from main.tern:3). Check the divisor", e.Description);
    }

    [Fact]
    public void A_private_const_fn_is_not_visible_outside_its_module()
    {
        Write("tables.tern", "private const fn secret() -> int { return 1 }\nconst fn open() -> int { return secret() + 1 }\n");
        Assert.Equal(2, RunMain("import tables\nconst K = tables.open()\nvar v: u8\nfn main() { v = K }").U8("v"));
        var e = ErrorInMain("import tables\nconst K = tables.secret()\nfn main() { }");
        Assert.Contains("'tables.secret' is private to the module tables", e.Description);
    }

    [Fact]
    public void The_step_limit_stops_a_loop_that_never_ends()
    {
        var e = ErrorInMain("const fn forever() -> int {\n    var n = 0\n    while true { n += 1 }\n    return n\n}\n\nconst K = forever()\nfn main() { }");
        Assert.Equal("'forever' did not finish after 10,000,000 steps (called from main.tern:7). Check its loops for one that never ends", e.Description);
        Assert.Equal(1, e.Pos.Line);
        var d = Tern.Compiler.Tools.Diagnostic.From(e, "main.tern");
        Assert.Equal("Check its loops for one that never ends", d.Suggestion);
    }

    [Fact]
    public void The_step_limit_counts_each_top_level_evaluation()
    {
        // Two evaluations of 6,000,000 steps each: neither reaches the limit alone.
        var r = Run.Program("""
            const fn spin(n: int) -> int {
                var k = 0
                while k < n { k += 1 }
                return k & $FF
            }
            const A = spin(2_000_000)
            const B = spin(2_000_000)
            var v: u8
            fn main() { v = A + B - $80 }
            """);
        Assert.Equal(0x80, r.U8("v"));
    }

    [Fact]
    public void Emit_is_contextual_and_a_function_may_still_be_called_emit()
    {
        var r = Run.Program("var n: u8\nfn emit() { n = 3 }\nfn main() {\n    var emit2: u8 = 1\n    emit()\n    n += emit2\n}");
        Assert.Equal(4, r.U8("n"));
    }

    // ---------- assert in a const fn (spec 4.1, decision 2026-11-13, question 128) ----------

    [Fact]
    public void An_assert_that_holds_lets_the_const_fn_go_on()
    {
        var r = Run.Program("""
            const fn half(n: int) -> int {
                assert n % 2 == 0, "half: n must be even"
                return n / 2
            }
            const K = half(200)
            var v: u8
            fn main() { v = K }
            """);
        Assert.Equal(100, r.U8("v"));
    }

    [Fact]
    public void A_failing_assert_gives_its_message_at_its_line_with_the_call()
    {
        var e = Error("const fn half(n: int) -> int {\n    assert n % 2 == 0, \"half: n must be even\"\n    return n / 2\n}\nconst K = half(7)\nfn main() { }");
        Assert.Equal(2, e.Pos.Line);
        Assert.Equal("half: n must be even (called from line 5)", e.Description);
        Assert.Equal(5, e.CalledFrom!.Value.Line);
    }

    [Fact]
    public void A_message_with_a_fix_keeps_the_call_after_its_first_sentence()
    {
        var e = Error("const fn f(n: int) -> int {\n    assert n <= 255, \"n is too large. Use a value up to 255\"\n    return n\n}\nconst K = f(256)\nfn main() { }");
        Assert.Equal("n is too large (called from line 5). Use a value up to 255", e.Description);
    }

    [Fact]
    public void An_assert_in_a_const_fn_called_by_another_names_the_first_call()
    {
        Write("tables.tern", """
            const fn check(n: int) {
                assert n < 10, "tables: n must be below 10"
            }

            const fn table(n: int) -> [int; _] {
                check(n)
                return [i for i in 0..n]
            }
            """);
        var ok = RunMain("import tables\ndata t: [u8; _] = tables.table(9)\nvar v: u8\nfn main() { v = t[8] }");
        Assert.Equal(8, ok.U8("v"));
        var e = ErrorInMain("import tables\n\ndata t: [u8; _] = tables.table(12)\nfn main() { }");
        Assert.Equal(Path.Combine(_dir.FullName, "tables.tern"), e.Pos.File);
        Assert.Equal(2, e.Pos.Line);
        Assert.Equal("tables: n must be below 10 (called from main.tern:3)", e.Description);
    }

    [Fact]
    public void An_assert_may_use_locals_reals_and_loops()
    {
        var src = """
            const fn sum(n: int) -> int {
                var s = 0
                for i in 0..n {
                    s += i
                    assert s * 1.5 < 300.0, "sum: too large"
                }
                return s
            }
            const K = sum(LIMIT)
            var v: u8
            fn main() { v = K }
            """;
        Assert.Equal(190, Run.Program(src.Replace("LIMIT", "20")).U8("v"));
        var e = Error(src.Replace("LIMIT", "21"));
        Assert.Equal(5, e.Pos.Line);
        Assert.Equal("sum: too large (called from line 9)", e.Description);
    }

    [Theory]
    [InlineData("const fn f(n: int) -> int {\n    assert n, \"m\"\n    return n\n}\nconst K = f(1)\nfn main() { }", 2,
        "An assert condition is a bool (called from line 5). Write a comparison, e.g. COUNT <= 64")]
    [InlineData("const fn f(n: int) -> int {\n    assert n > 0\n    return n\n}\nconst K = f(1)\nfn main() { }", 2,
        "Expected ',' and a message string after the condition")]
    [InlineData("const fn f(n: int) -> int {\n    assert n > 0, n\n    return n\n}\nconst K = f(1)\nfn main() { }", 2,
        "Expected a message string")]
    [InlineData("fn main() {\n    assert 1 < 2, \"m\"\n}", 2,
        "An assert is checked at compile time. Write it at the top level or in a const fn")]
    [InlineData("fn main() {\n    if true {\n        assert 1 < 2, \"m\"\n    }\n}", 3,
        "An assert is checked at compile time. Write it at the top level or in a const fn")]
    public void Assert_errors(string source, int line, string message)
    {
        var e = Error(source);
        Assert.Equal(line, e.Pos.Line);
        Assert.StartsWith(message, e.Description);
    }
}
