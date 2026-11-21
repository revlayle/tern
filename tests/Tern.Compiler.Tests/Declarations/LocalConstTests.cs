using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// M20 follow-up 2, spec 4: a <c>const</c> inside a function follows the rules of a top-level one, takes
/// no memory and no code, and can be used from its declaration to the end of its block.
/// </summary>
public class LocalConstTests
{
    private static string Error(string source) => Assert.Throws<CompileError>(() => TernCompiler.Compile(source)).Description;

    [Fact]
    public void A_local_const_is_used_like_a_top_level_one()
    {
        var r = Run.Program("""
            const BASE: u8 = 10
            var a: u8
            var b: u16
            var c: i8
            fn main() {
                const STEP = 3
                const TOTAL: u16 = BASE * STEP * 100  // 3000, exact
                const NEG: i8 = -STEP
                a = BASE + STEP
                b = TOTAL
                c = NEG
            }
            """);
        Assert.Equal(13, r.U8("a"));
        Assert.Equal(3000, r.U16("b"));
        Assert.Equal(-3, r.I8("c"));
    }

    [Fact]
    public void A_local_const_has_no_code()
    {
        var with = TernCompiler.Compile("var a: u8\nfn main() {\n    const N = 7\n    a = N\n}");
        var without = TernCompiler.Compile("var a: u8\nfn main() {\n    a = 7\n}");
        Assert.Equal(without.File, with.File);
    }

    [Fact]
    public void A_local_const_is_a_for_bound_and_an_array_length()
    {
        var r = Run.Program("""
            var sum: u8
            var last: u8
            fn main() {
                const LEN = 5
                var buf: [u8; LEN]
                for i in 0..LEN { buf[i] = i << 1 }
                for i in 0..LEN { sum += buf[i] }
                last = buf[LEN - 1]
            }
            """);
        Assert.Equal(20, r.U8("sum"));
        Assert.Equal(8, r.U8("last"));
    }

    [Fact]
    public void Blocks_that_do_not_contain_each_other_may_reuse_the_name()
    {
        var r = Run.Program("""
            var a: u8
            var b: u8
            fn f() -> u8 {
                const K = 40
                return K
            }
            fn main() {
                if a == 0 {
                    const K = 1
                    a = K
                } else {
                    const K = 2
                    a = K
                }
                b = f()
            }
            """);
        Assert.Equal(1, r.U8("a"));
        Assert.Equal(40, r.U8("b"));
    }

    [Fact]
    public void A_local_const_works_in_an_inline_function_and_in_assembly()
    {
        var r = Run.Program("""
            var a: u8
            var b: u8
            inline fn plus_four(x: u8) -> u8 {
                const FOUR = 4
                return x + FOUR
            }
            fn main() {
                const MASK = $0F
                a = plus_four(6)
                asm {
                    lda #$FF
                    and #MASK
                    sta b
                }
            }
            """);
        Assert.Equal(10, r.U8("a"));
        Assert.Equal(0x0F, r.U8("b"));
    }

    [Fact]
    public void A_const_at_the_end_of_a_function_does_not_hide_its_return()
    {
        var r = Run.Program("""
            var a: u8
            fn f() -> u8 {
                if a == 0 { return 5 }
                return 6
                const UNUSED = 1
            }
            fn main() { a = f() }
            """);
        Assert.Equal(5, r.U8("a"));
    }

    [Theory]
    [InlineData("var a: u8\nfn main() {\n    { const N = 1 }\n    a = N\n}", "'N' is not declared")]
    [InlineData("var a: u8\nfn main() {\n    a = N\n    const N = 1\n}", "'N' is not declared")]
    [InlineData("const N = 1\nfn main() { const N = 2 }", "'N' is already declared at 1:1")]
    [InlineData("var N: u8\nfn main() { const N = 2 }", "'N' is already declared at 1:1")]
    [InlineData("fn f(n: u8) { const n = 2 }\nfn main() { f(1) }", "'n' is already declared")]
    [InlineData("fn main() {\n    var n: u8 = 0\n    if n == 0 { const n = 2 }\n}", "'n' is already declared at 2:5")]
    [InlineData("fn main() {\n    const n = 1\n    var n: u8 = 0\n}", "'n' is already declared at 2:5")]
    [InlineData("fn main() {\n    const n = 1\n    for n in 0..3 { }\n}", "'n' is already declared at 2:5")]
    [InlineData("fn main() { const N: u8 = 300 }", "300 does not fit in u8")]
    [InlineData("var v: u8\nfn main() { const N = v }", "The value of 'N' is not known at compile time")]
    [InlineData("import c64\nfn main() { const c64 = 1 }", "'c64' is the name of an imported module")]
    public void Reports_errors(string source, string expected) => Assert.Contains(expected, Error(source));
}
