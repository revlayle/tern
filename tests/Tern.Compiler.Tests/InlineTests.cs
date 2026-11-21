using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary><c>inline fn</c> (spec 6.2): the body is copied into each caller instead of a <c>jsr</c>.</summary>
public class InlineTests
{
    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Calls_are_replaced_by_the_body()
    {
        var r = Run.Program("""
            var x: u8
            var y: u8
            var z: u8
            inline fn clamp(v: u8) -> u8 {
                if v > 100 { return 100 }
                return v
            }
            fn main() {
                x = clamp(42)
                y = clamp(x + 70)
                z = clamp(clamp(250) - 1)
            }
            """);
        Assert.Equal(42, r.U8("x"));
        Assert.Equal(100, r.U8("y"));
        Assert.Equal(99, r.U8("z"));
        Assert.DoesNotContain("jsr", Listing.Code(r));
        Assert.DoesNotContain("clamp", r.Result.Symbols.Keys);
        Assert.Contains("main.clamp.v", r.Result.Symbols.Keys); // its parameter is in main's frame
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_small_inline_function_is_its_body()
    {
        var r = Run.Program("""
            var count: u8
            inline fn tick() { count += 1 }
            fn main() {
                tick()
                tick()
            }
            """);
        Assert.Equal("inc count|inc count|rts", Listing.Code(r));
        Assert.Equal(2, r.U8("count"));
    }

    [Fact]
    public void Register_parameters_and_return_values()
    {
        var r = Run.Program("""
            var a: u8
            var b: u8
            var w: u16
            var n: u8
            inline fn is_zero(v: u8 using A) -> bool { return v == 0 }
            inline fn wide(lo: u8 using X, hi: u8 using Y) -> u16 { return (hi as u16) << 8 | lo }
            inline fn flag(v: u8) -> bool using zero { return v == 7 }
            fn main() {
                if is_zero(0) { a = 1 }
                if is_zero(5) { b = 1 }
                w = wide($34, $12)
                if flag(7) { n += 1 }
                if flag(8) { n += 10 }
            }
            """);
        Assert.Equal(1, r.U8("a"));
        Assert.Equal(0, r.U8("b"));
        Assert.Equal(0x1234, r.U16("w"));
        Assert.Equal(1, r.U8("n"));
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Inline_functions_call_other_functions_and_use_loops_and_match()
    {
        var r = Run.Program("""
            var out: [u8; 8]
            var total: u16
            fn double(v: u8) -> u8 { return v << 1 }
            inline fn name(v: u8) -> u8 {
                match v {
                    0 => return 'Z'
                    1..=3 => return 'S'
                    _ => return 'B'
                }
            }
            inline fn fill(n: u8) {
                for i in 0..8 {
                    if i == n { break }
                    out[i] = double(i) + name(i)
                }
            }
            inline fn sum() -> u16 {
                var s: u16 = 0
                for i in 0..8 { s = s + out[i] }
                return s
            }
            fn main() {
                fill(6)
                total = sum()
            }
            """);
        int expected = 0;
        for (int i = 0; i < 8; i++)
        {
            int v = i < 6 ? (i << 1) + (i == 0 ? 'Z' : i <= 3 ? 'S' : 'B') : 0;
            Assert.Equal(v, r.U8("out", i));
            expected += v;
        }
        Assert.Equal(expected, r.U16("total"));
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void A_return_in_an_inline_copy_inside_a_handler_stays_in_the_handler()
    {
        var r = Run.Program("""
            var ticks: u8
            var odd: u8
            var total: u16
            inline fn count() {
                ticks += 1
                if ticks & 1 == 0 { return }
                odd += 1
            }
            interrupt fn handler() {
                count()
                total = total + 0
            }
            fn main() {
                asm {
                    sei
                    lda #handler.lo
                    sta $FFFE
                    lda #handler.hi
                    sta $FFFF
                    cli
                }
                for i in 0..250 { total = total + (i as u16) }
                asm {
                    sei
                }
            }
            """, irqEvery: 150);
        Assert.True(r.Interrupts > 5);
        Assert.Equal((r.Interrupts + 1) / 2, r.U8("odd"));
        Assert.Equal(249 * 250 / 2, r.U16("total"));
    }

    [Fact]
    public void Temporaries_of_the_caller_survive_an_inline_copy()
    {
        var r = Run.Program("""
            var p: u16 = 1000
            var q: u16 = 3000
            var w: u16
            fn other(v: u16) -> u16 { return v + 1 }
            inline fn mix(a: u16, b: u16) -> u16 { return (a + other(b)) - (b >> 1) }
            fn main() { w = (p + 1) + mix(q, p + q) + (q - 1) }
            """);
        int mix = (3000 + (4000 + 1)) - (4000 >> 1);
        Assert.Equal((1000 + 1 + mix + 3000 - 1) & 0xFFFF, r.U16("w"));
    }

    [Fact]
    public void Inline_function_frames_are_shared_like_others()
    {
        // f is inlined into main, g is a separate call from main: their frames may overlap,
        // since they never run at the same time; h is called inside f's copy, so it follows f.
        var r = Run.Program("""
            var out: u8
            fn h(k: u8) -> u8 { return k + 1 }
            inline fn f(a: u8) -> u8 {
                var t: u8 = a
                return h(t) + t
            }
            fn g(b: u8) -> u8 { return b }
            fn main() { out = f(3) + g(4) }
            """);
        Assert.Equal(3 + 1 + 3 + 4, r.U8("out"));
        var s = r.Result.Symbols;
        Assert.NotEqual(s["main.f.t"], s["h.k"]);
        Assert.NotEqual(s["main.f.a"], s["h.k"]);
    }

    [Fact]
    public void Copies_of_an_inline_function_share_its_temporaries()
    {
        // Review M6, finding 4: three copies need no more temporaries than one.
        static int Temps(string calls)
        {
            var r = TernCompiler.Compile($$"""
                var a: [u8; 4] = [1, 2, 3, 4]
                var out: u8
                fn g(v: u8) -> u8 { return v + 1 }
                inline fn f(i: u8) -> u8 { return a[i] + g(i) + a[i ^ 1] + g(i + 1) }
                fn main() { {{calls}} }
                """);
            return r.Report.Split('\n').Count(l => l.Contains("f._t"));
        }
        int one = Temps("out = f(0)");
        Assert.True(one > 0);
        Assert.Equal(one, Temps("out = f(0)\n out = f(1)\n out = f(2)"));
        var run = Run.Program("""
            var a: [u8; 4] = [1, 2, 3, 4]
            var out: [u8; 3]
            fn g(v: u8) -> u8 { return v + 1 }
            inline fn f(i: u8) -> u8 { return a[i] + g(i) + a[i ^ 1] + g(i + 1) }
            fn main() {
                out[0] = f(0)
                out[1] = f(1)
                out[2] = f(2)
            }
            """);
        int[] a = [1, 2, 3, 4];
        for (int i = 0; i < 3; i++)
            Assert.Equal(a[i] + (i + 1) + a[i ^ 1] + (i + 2), run.U8("out", i));
    }

    [Theory]
    [InlineData("inline var x: u8\nfn main() { }", 1, 1, "'inline' goes before 'fn', e.g. 'inline fn wait_frame() { ... }'; found 'var'. Write 'inline fn' or remove 'inline'")]
    [InlineData("inline interrupt fn f() { }\nfn main() { }", 1, 1, "'inline' goes before 'fn', e.g. 'inline fn wait_frame() { ... }'; found 'interrupt'. Write 'inline fn' or remove 'inline'")]
    [InlineData("interrupt inline fn f() { }\nfn main() { }", 1, 1, "'interrupt' goes before 'fn', e.g. 'interrupt fn raster() { ... }'; found 'inline'. Write 'interrupt fn' or remove 'interrupt'")]
    [InlineData("fn main() { inline fn f() { } }", 1, 13, "'inline fn' must be declared outside functions. Move it to the top level of the file")]
    [InlineData("inline fn main() { }", 1, 8, "'main' cannot be inline: nothing calls it. Remove 'inline'")]
    [InlineData("inline fn f() @ $1000\nfn main() { }", 1, 8, "An inline function needs a body; '@ $addr' declares a routine that is already there. Remove 'inline' or the address")]
    [InlineData("inline fn f() { }\nfn main() { asm {\n jsr f\n } }", 3, 6, "'f' is inline: it is copied into each Tern caller and has no address of its own. Call it from Tern, or remove 'inline'")]
    [InlineData("inline fn f() { g() }\ninline fn g() { f() }\nfn main() { f() }", 1, 8, "Recursion is not allowed: f -> g -> f. Use a loop instead, with an array as the stack if needed")]
    [InlineData("inline asm fn f() {\n var t: u8\n lda t\n var u: u8\n}\nfn main() { f() }", 4, 2, "'var' lines go at the start of an asm fn, before the first instruction. Move this line up")]
    [InlineData("inline asm fn f() {\n var t: u8 = 3\n lda t\n}\nfn main() { f() }", 2, 14, "'t' in an asm fn has no initial value: the assembly sets it. Remove the value and store it in the assembly")]
    [InlineData("fn main() {\n asm {\n var t: u8\n lda t\n }\n}", 3, 2, "'var' is not allowed inside an asm block. Declare the variable before the block, or use 'var' lines at the start of an asm fn")]
    public void Reports_errors(string source, int line, int column, string expected)
    {
        Expect.Error(source, line, column, expected);
    }
}
