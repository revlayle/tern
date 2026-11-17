namespace Tern.Compiler.Tests;

/// <summary>
/// lib/math (spec 10.1): 8-bit routines tested for every input, 16-bit routines on edge values.
/// Cycle counts include the jsr and rts; best and worst cases are recorded here and in docs/status.md.
/// </summary>
public class MathLibraryTests
{
    private static readonly Library Math = new("import math", """
                math.mul8(0, 0)
                math.mul8_fast(0, 0)
                math.mul16(0, 0)
                math.div8(0, 1)
                math.div16(0, 1)
                math.shl8(0, 0)
                math.shr8(0, 0)
                math.shl16(0, 0)
                math.shr16(0, 0)
        """);

    /// <summary>16-bit edge values, and a fixed set of others.</summary>
    private static readonly int[] Edges16 =
        new[] { 0, 1, 2, 3, 7, 10, 127, 128, 255, 256, 257, 1000, 4095, 12345, 0x7FFF, 0x8000, 0x8001, 0xFF00, 0xFFFE, 0xFFFF }
            .Concat(Enumerable.Range(0, 30).Select(i => new Random(i).Next(0x10000))).ToArray();

    private static (long Min, long Max) Range(IEnumerable<long> cycles) => (cycles.Min(), cycles.Max());

    [Fact]
    public void Mul8_for_every_input()
    {
        var cycles = new List<long>();
        lock (Math)
            for (int a = 0; a < 256; a++)
            for (int b = 0; b < 256; b++)
            {
                cycles.Add(Math.Call("math.mul8", a, b));
                if (Math.AX != a * b) Assert.Fail($"mul8({a}, {b}) = {Math.AX}, expected {a * b}");
            }
        Assert.Equal((151, 183), Range(cycles));
    }

    [Fact]
    public void Mul8_fast_for_every_input()
    {
        var cycles = new List<long>();
        lock (Math)
            for (int a = 0; a < 256; a++)
            for (int b = 0; b < 256; b++)
            {
                cycles.Add(Math.Call("math.mul8_fast", a, b));
                if (Math.AX != a * b) Assert.Fail($"mul8_fast({a}, {b}) = {Math.AX}, expected {a * b}");
            }
        Assert.Equal((65, 73), Range(cycles));
    }

    [Fact]
    public void Div8_for_every_input()
    {
        var cycles = new List<long>();
        lock (Math)
            for (int a = 0; a < 256; a++)
            for (int b = 1; b < 256; b++)
            {
                cycles.Add(Math.Call("math.div8", a, b));
                if (Math.Cpu.A != a / b || Math.U8("math.remainder8") != a % b)
                    Assert.Fail($"div8({a}, {b}) = {Math.Cpu.A} rest {Math.U8("math.remainder8")}, expected {a / b} rest {a % b}");
            }
        Assert.Equal((187, 243), Range(cycles));
    }

    [Fact]
    public void Shifts8_for_every_input()
    {
        var left = new List<long>();
        var right = new List<long>();
        lock (Math)
            for (int x = 0; x < 256; x++)
            foreach (int n in Enumerable.Range(0, 17).Append(255))
            {
                left.Add(Math.Call("math.shl8", x, n));
                if (Math.Cpu.A != (n > 7 ? 0 : (x << n) & 0xFF)) Assert.Fail($"shl8({x}, {n}) = {Math.Cpu.A}");
                right.Add(Math.Call("math.shr8", x, n));
                if (Math.Cpu.A != (n > 7 ? 0 : x >> n)) Assert.Fail($"shr8({x}, {n}) = {Math.Cpu.A}");
            }
        Assert.Equal((17, 1800), Range(left));   // 17 for n = 0, then 7 per step
        Assert.Equal((17, 1800), Range(right));
    }

    [Fact]
    public void Mul16_on_edge_values()
    {
        var cycles = new List<long>();
        lock (Math)
            foreach (int m in Edges16)
            foreach (int n in Edges16)
            {
                Math.Set16("math.mul16.m", m);
                Math.Set16("math.mul16.n", n);
                cycles.Add(Math.Call("math.mul16"));
                int expected = (int)((long)m * n & 0xFFFF);
                if (Math.AX != expected) Assert.Fail($"mul16({m}, {n}) = {Math.AX}, expected {expected}");
            }
        Assert.Equal((38, 870), Range(cycles));
    }

    [Fact]
    public void Div16_on_edge_values()
    {
        var cycles = new List<long>();
        lock (Math)
            foreach (int n in Edges16)
            foreach (int d in Edges16.Where(d => d != 0))
            {
                Math.Set16("math.div16.n", n);
                Math.Set16("math.div16.d", d);
                cycles.Add(Math.Call("math.div16"));
                if (Math.AX != n / d || Math.U16("math.remainder16") != n % d)
                    Assert.Fail($"div16({n}, {d}) = {Math.AX} rest {Math.U16("math.remainder16")}, expected {n / d} rest {n % d}");
            }
        Assert.Equal((699, 1051), Range(cycles));
    }

    [Fact]
    public void Shifts16_on_edge_values()
    {
        var left = new List<long>();
        var right = new List<long>();
        lock (Math)
            foreach (int x in Edges16)
            foreach (int n in Enumerable.Range(0, 18).Append(255))
            {
                left.Add(Math.Call("math.shl16", x & 0xFF, x >> 8, n));
                if (Math.AX != (n > 15 ? 0 : (x << n) & 0xFFFF)) Assert.Fail($"shl16({x}, {n}) = {Math.AX}");
                right.Add(Math.Call("math.shr16", x & 0xFF, x >> 8, n));
                if (Math.AX != (n > 15 ? 0 : x >> n)) Assert.Fail($"shr16({x}, {n}) = {Math.AX}");
            }
        Assert.Equal((17, 3081), Range(left));   // 17 for n = 0, then 12 per step
        Assert.Equal((17, 3081), Range(right));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Calls_compile_to_the_spec_shape()
    {
        // Spec 11: math.mul8(a, b) is lda a / ldx b / jsr math.mul8, with the result in A/X.
        // A is loaded last (M3), which costs the same.
        var r = Run.Program("""
            import math
            var a: u8 = 200
            var b: u8 = 3
            var p: u16
            var q: u8
            var rest: u8
            fn main() {
                p = math.mul8(a, b)
                q = math.div8(a, b)
                rest = math.remainder8
            }
            """);
        Assert.Equal(600, r.U16("p"));
        Assert.Equal(66, r.U8("q"));
        Assert.Equal(2, r.U8("rest"));
        Assert.Matches(@"ldx b\s+;.*\n.*lda a\s+;.*\n.*jsr math\.mul8", r.Result.Listing);
    }

    [Fact]
    public void Only_the_routines_used_are_in_the_program()
    {
        var r = TernCompiler.Compile("import math\nvar p: u16\nfn main() { p = math.mul8(2, 3) }");
        Assert.Contains("math.mul8", r.Symbols.Keys);
        Assert.DoesNotContain("math.mul8_fast", r.Symbols.Keys);
        Assert.DoesNotContain("math.SQUARE_LO", r.Symbols.Keys);
        Assert.DoesNotContain("math.remainder16", r.Symbols.Keys);
        Assert.True(r.Image.Length < 100, $"{r.Image.Length} bytes");
    }

    [Theory]
    [InlineData("import math\nvar x: u8\nfn main() { x = math.w0 }", "'math.w0' is private to the module math")]
    [InlineData("import math\nvar x: u8\nfn main() { x = math.SQUARE_LO[3] }", "'math.SQUARE_LO' is private to the module math")]
    [InlineData("import text\nvar x: u8\nfn main() { x = text.POWERS[3].lo }", "'text.POWERS' is private to the module text")]
    [InlineData("import text\nvar x: u16\nfn main() { x = text.POWERS[0] }", "'text.POWERS' is private to the module text")]
    public void Library_work_variables_and_tables_are_private(string source, string expected)
    {
        var e = Assert.Throws<Syntax.CompileError>(() => TernCompiler.Compile(source));
        Assert.StartsWith(expected, e.Description);
    }
}
