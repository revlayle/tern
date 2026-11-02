using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary><c>inline fn</c> (spec 6.2): the body is copied into each caller instead of a <c>jsr</c>.</summary>
public class InlineTests
{
    /// <summary>The instructions of a function, without addresses, bytes and cycles.</summary>
    private static string Code(Run r, string function = "main")
    {
        var listing = r.Result.Listing;
        var start = listing.IndexOf($"\n{function}:", StringComparison.Ordinal);
        var code = new List<string>();
        foreach (var l in listing[(start + 1)..].Split('\n').Skip(1).Select(l => l.TrimEnd('\r')))
        {
            if (l.EndsWith(':') && !l.StartsWith(' ') && !l.StartsWith('$') && !l.StartsWith(';') && !l.Contains('.')) break;
            if (l.StartsWith('$')) code.Add(l[17..].Split(';')[0].Trim());
        }
        return string.Join("|", code);
    }

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
        Assert.DoesNotContain("jsr", Code(r));
        Assert.DoesNotContain("clamp", r.Result.Symbols.Keys);
        Assert.Contains("clamp.v", r.Result.Symbols.Keys); // its frame is still there
    }

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
        Assert.Equal("inc count|inc count|rts", Code(r));
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
        Assert.NotEqual(s["f.t"], s["h.k"]);
        Assert.NotEqual(s["f.a"], s["h.k"]);
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
    [InlineData("inline asm fn f() {\n rts\n }\nfn main() { }", "'inline asm fn' is not supported yet")]
    [InlineData("inline var x: u8\nfn main() { }", "'inline' goes before 'fn'")]
    [InlineData("inline interrupt fn f() { }\nfn main() { }", "'inline' goes before 'fn'")]
    [InlineData("interrupt inline fn f() { }\nfn main() { }", "'interrupt' goes before 'fn'")]
    [InlineData("fn main() { inline fn f() { } }", "'inline fn' must be declared outside functions")]
    [InlineData("inline fn main() { }", "'main' cannot be inline")]
    [InlineData("inline fn f() @ $1000\nfn main() { }", "An inline function needs a body")]
    [InlineData("inline fn f() { }\nfn main() { asm {\n jsr f\n } }", "'f' is inline: it is copied into each Tern caller and has no address of its own")]
    [InlineData("inline fn f() { g() }\ninline fn g() { f() }\nfn main() { f() }", "Recursion is not allowed: f -> g -> f")]
    [InlineData("var v: u8\ninline fn f() { v += 1 }\ninterrupt fn h() { f() }\nfn main() { f() }", "'f' is called from main and from the interrupt handler 'h'")]
    public void Reports_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
    }
}
