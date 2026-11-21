using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// An inline function's parameters, locals and temporaries live in each caller's frame (spec 6.2, M16),
/// so one inline function with memory can be used from main and from an interrupt handler, and
/// <c>var</c> lines are allowed at the start of an <c>inline asm fn</c>.
/// </summary>
public class InlineLocalsTests
{
    private const string Install = """
        asm {
            sei
            lda #h.lo
            sta $FFFE
            lda #h.hi
            sta $FFFF
            cli
        }
        """;

    private static byte Mix(int x)
    {
        int t = (x ^ 85) & 255;
        t = (t + x) & 255;
        int u = (t << 1) & 255;
        return (byte)(((u ^ t) + 3) & 255);
    }

    [Fact]
    public void One_inline_function_with_locals_is_used_from_main_and_from_a_handler()
    {
        // If main and the handler shared the locals, an interrupt between 't = ...' and 'return' would corrupt a result.
        var r = Run.Program($$"""
            var sum: u8
            var hn: u8
            var hacc: u8
            inline fn mix(x: u8) -> u8 {
                var t: u8 = x ^ 85
                t = t + x
                var u: u8 = t << 1
                return (u ^ t) + 3
            }
            interrupt fn h() {
                hacc += mix(hn)
                hn += 1
            }
            fn main() {
                {{Install}}
                for rep in 0..20 {
                    for i in 0..=255 { sum += mix(i) }
                }
                asm { sei }
            }
            """, maxCycles: 4_000_000, irqEvery: 83);
        int calls = r.Interrupts;
        Assert.True(calls > 100);
        int expectedSum = 0, expectedAcc = 0;
        for (int rep = 0; rep < 20; rep++) for (int i = 0; i < 256; i++) expectedSum += Mix(i);
        for (int k = 0; k < calls; k++) expectedAcc += Mix(k & 255);
        Assert.Equal((byte)expectedSum, r.U8("sum"));
        Assert.Equal((byte)expectedAcc, r.U8("hacc"));
        Assert.Equal((byte)calls, r.U8("hn"));
    }

    [Fact]
    public void Each_caller_has_its_own_copy_of_the_variables()
    {
        var r = Run.Program($$"""
            var a: u8
            var b: u8
            inline fn twice(x: u8) -> u8 {
                var t: u8 = x + x
                return t
            }
            interrupt fn h() { b = twice(b + 1) }
            fn main() {
                {{Install}}
                a = twice(a + 1)
                asm { sei }
            }
            """, irqEvery: 400);
        var s = r.Result.Symbols;
        Assert.DoesNotContain("twice.t", s.Keys);
        Assert.DoesNotContain("twice.x", s.Keys);
        Assert.Contains("main.twice.t", s.Keys);
        Assert.Contains("h.twice.t", s.Keys);
        Assert.Contains("main.twice.x", s.Keys);
        Assert.Contains("h.twice.x", s.Keys);
        Assert.NotEqual(s["main.twice.t"], s["h.twice.t"]);
        Assert.NotEqual(s["main.twice.x"], s["h.twice.x"]);
        Assert.Equal(2, r.U8("a"));
    }

    [Fact]
    public void Copies_in_one_caller_share_the_variables()
    {
        // Three calls need no more memory than one: the report lists each variable once.
        static int Variables(string calls)
        {
            var r = TernCompiler.Compile($$"""
                var out: u8
                inline fn f(x: u8) -> u8 {
                    var t: u8 = x + 1
                    return t + x
                }
                fn main() { {{calls}} }
                """);
            return r.Report.Split('\n').Count(l => l.Contains("main.f."));
        }
        int one = Variables("out = f(1)");
        Assert.True(one > 0);
        Assert.Equal(one, Variables("out = f(1)\nout += f(2)\nout += f(f(3))"));
    }

    [Fact]
    public void Inline_functions_calling_inline_functions_work_from_two_roots()
    {
        var r = Run.Program($$"""
            var sum: u8
            var hacc: u8
            var hn: u8
            inline fn inner(a: u8) -> u8 {
                var s: u8 = a + 1
                return s ^ 7
            }
            inline fn outer(b: u8) -> u8 {
                var q: u8 = inner(b) + inner(b + 1)
                var buf: [u8; 4]
                buf[0] = q
                buf[1] = q + 1
                buf[2] = b
                buf[3] = inner(q)
                return buf[0] + buf[1] + buf[2] + buf[3]
            }
            interrupt fn h() {
                hacc += outer(hn)
                hn += 1
            }
            fn main() {
                {{Install}}
                for rep in 0..4 {
                    for i in 0..=255 { sum += outer(i) }
                }
                asm { sei }
            }
            """, maxCycles: 6_000_000, irqEvery: 701);
        static byte Inner(int a) => (byte)(((a + 1) & 255) ^ 7);
        static byte Outer(int b)
        {
            int q = (Inner(b) + Inner(b + 1)) & 255;
            return (byte)((q + (q + 1) + b + Inner(q)) & 255);
        }
        int calls = r.Interrupts;
        Assert.True(calls > 50);
        int expectedSum = 0, expectedAcc = 0;
        for (int rep = 0; rep < 4; rep++) for (int i = 0; i < 256; i++) expectedSum += Outer(i);
        for (int k = 0; k < calls; k++) expectedAcc += Outer(k & 255);
        Assert.Equal((byte)expectedSum, r.U8("sum"));
        Assert.Equal((byte)expectedAcc, r.U8("hacc"));
        var s = r.Result.Symbols;
        foreach (var root in new[] { "main", "h" })
            foreach (var name in new[] { "inner.s", "outer.q", "outer.buf" })
                Assert.Contains($"{root}.{name}", s.Keys);
    }

    [Fact]
    public void Loop_counters_and_pointer_parameters_of_an_inline_function_are_per_caller()
    {
        var r = Run.Program($$"""
            var data_bytes: [u8; 4] = [9, 7, 5, 3]
            var sum: u8
            var hacc: u8
            var hn: u8
            inline fn total(n: u8, p: ptr u8) -> u8 {
                var s: u8 = 0
                for i in 0..n {
                    for j in 0..2 { s += p[i] }
                }
                return s
            }
            interrupt fn h() {
                hacc += total(hn & 3, addr(data_bytes))
                hn += 1
            }
            fn main() {
                {{Install}}
                for rep in 0..40 {
                    sum += total(4, addr(data_bytes))
                }
                asm { sei }
            }
            """, maxCycles: 6_000_000, irqEvery: 151);
        int calls = r.Interrupts;
        Assert.True(calls > 20);
        int[] d = [9, 7, 5, 3];
        static int Total(int n, int[] d) { int s = 0; for (int i = 0; i < n; i++) s += 2 * d[i]; return s; }
        int expectedAcc = 0;
        for (int k = 0; k < calls; k++) expectedAcc += Total(k & 3, d);
        Assert.Equal((byte)(40 * Total(4, d)), r.U8("sum"));
        Assert.Equal((byte)expectedAcc, r.U8("hacc"));
        var s = r.Result.Symbols;
        Assert.True(s["main.total.p"] < 0x100, "a dereferenced pointer parameter is in zero page");
        Assert.True(s["h.total.p"] < 0x100);
        Assert.NotEqual(s["main.total.p"], s["h.total.p"]);
    }

    [Fact]
    public void An_inline_function_called_by_a_function_and_by_main_has_a_copy_in_each()
    {
        var r = Run.Program("""
            var a: u8
            var b: u8
            inline fn mix(x: u8) -> u8 {
                var t: u8 = x ^ 85
                t = t + x
                return t
            }
            fn helper(v: u8) -> u8 { return mix(v) + mix(v + 1) }
            fn main() {
                a = helper(10) + mix(3)
                b = mix(a)
            }
            """);
        int mix(int x) => ((x ^ 85) + x) & 255;
        int a = (mix(10) + mix(11) + mix(3)) & 255;
        Assert.Equal(a, r.U8("a"));
        Assert.Equal(mix(a), r.U8("b"));
        var s = r.Result.Symbols;
        Assert.NotEqual(s["main.mix.t"], s["helper.mix.t"]);
    }

    [Fact]
    public void An_inline_asm_fn_with_var_lines_works_from_main_and_a_handler()
    {
        var r = Run.Program($$"""
            var sum: u8
            var hacc: u8
            var hn: u8
            inline asm fn swap_nibbles(x: u8 using A) -> u8 {
                var t: u8
                sta t
                asl t
                asl t
                asl t
                asl t
                lsr a
                lsr a
                lsr a
                lsr a
                ora t
            }
            interrupt fn h() {
                hacc += swap_nibbles(hn)
                hn += 1
            }
            fn main() {
                {{Install}}
                for rep in 0..20 {
                    for i in 0..=255 { sum += swap_nibbles(i) }
                }
                asm { sei }
            }
            """, maxCycles: 4_000_000, irqEvery: 97);
        static byte Swap(int x) => (byte)(((x << 4) | (x >> 4)) & 255);
        int calls = r.Interrupts;
        Assert.True(calls > 100);
        int expectedSum = 0, expectedAcc = 0;
        for (int rep = 0; rep < 20; rep++) for (int i = 0; i < 256; i++) expectedSum += Swap(i);
        for (int k = 0; k < calls; k++) expectedAcc += Swap(k & 255);
        Assert.Equal((byte)expectedSum, r.U8("sum"));
        Assert.Equal((byte)expectedAcc, r.U8("hacc"));
        var s = r.Result.Symbols;
        Assert.NotEqual(s["main.swap_nibbles.t"], s["h.swap_nibbles.t"]);
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void An_inline_asm_fn_with_a_local_compiles_to_its_instructions_only()
    {
        var r = Run.Program("""
            var a: u8
            inline asm fn bump(x: u8 using A) -> u8 {
                var t: u8
                sta t
                inc t
                lda t
            }
            fn main() { a = bump(41) }
            """);
        Assert.Equal(42, r.U8("a"));
        Assert.DoesNotContain("jsr bump", r.Result.Listing);
        Assert.Contains("main.bump.t", r.Result.Symbols.Keys);
    }

    [Fact]
    public void A_function_with_memory_called_by_an_inline_function_is_still_shared_between_roots()
    {
        // The inline function's own variables are per caller, but what it calls is one function, with
        // one frame that overlaps no frame of main or the handler (spec 8.1).
        InterruptTests.AssertFrameOverlapsNoOther(TernCompiler.Compile("""
            var v: u8
            fn g(k: u8) { v = k }
            inline fn f() { g(1) }
            interrupt fn h() {
                var a: u8 = v
                f()
                v = a
            }
            fn main() {
                var b: u8 = v
                f()
                v = b
            }
            """), "g");
    }

    [Fact]
    public void An_inline_function_with_a_local_in_a_function_shared_by_two_roots_is_in_the_shared_frame()
    {
        // The inline function's variable is in g's frame, which overlaps no frame of main or the handler.
        InterruptTests.AssertFrameOverlapsNoOther(TernCompiler.Compile("""
            var v: u8
            inline fn f() {
                var t = v
                v = t + 1
            }
            fn g() { f() }
            interrupt fn h() {
                var a: u8 = v
                g()
                v = a
            }
            fn main() {
                var b: u8 = v
                g()
                v = b
            }
            """), "g");
    }
}
