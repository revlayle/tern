using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// M20 follow-up 3, spec 9: a plain <c>asm fn</c> may start with <c>var</c> lines. They are its locals, in its
/// frame (shared with functions that never run at the same time), with names local to the function.
/// </summary>
public class AsmFnLocalsTests
{
    private const string Program = """
        var dst: [u8; 4]
        var a: u8
        var b: u8
        var kept: u8

        // Two functions with a local of the same name.
        asm fn triple(v: u8 using A) -> u8 {
            var t: u8
            sta t
            asl
            clc
            adc t
        }
        asm fn fill(value: u8 using A) {
            var t: u8
            var p: ptr u8
            sta t
            ldx #dst.lo
            stx p
            ldx #dst.hi
            stx p+1
            ldy #3
            lda t
        .loop:
            sta (p),y
            dey
            bpl .loop
        }
        fn caller() {
            var keep: u8 = 7
            b = triple(keep)
            kept = keep
        }
        fn main() {
            a = triple(5)
            fill(9)
            caller()
        }
        """;

    [Fact]
    public void Var_lines_in_an_asm_fn_are_its_locals()
    {
        var r = Run.Program(Program);
        Assert.Equal(15, r.U8("a"));
        Assert.Equal(21, r.U8("b"));
        Assert.Equal(7, r.U8("kept"));
        Assert.Equal([9, 9, 9, 9], Enumerable.Range(0, 4).Select(i => r.U8("dst", i)));
    }

    [Fact]
    public void They_share_memory_with_functions_that_never_run_at_the_same_time()
    {
        var v = Run.Program(Program).Result.Variables;
        // triple and fill never run at the same time: triple's t shares a byte with one of fill's locals.
        int t = v["triple.t"].Address;
        Assert.True(t == v["fill.t"].Address || t == v["fill.p"].Address || t == v["fill.p"].Address + 1);
        // caller calls triple, so its keep must survive triple's t.
        Assert.NotEqual(v["caller.keep"].Address, v["triple.t"].Address);
    }

    [Fact]
    public void A_pointer_used_as_indirect_y_is_in_zero_page()
    {
        var p = Run.Program(Program).Result.Variables["fill.p"];
        Assert.True(p.Address < 0xFF, $"fill.p at ${p.Address:X4}");
    }

    [Fact]
    public void The_report_lists_them_in_the_frame()
    {
        var report = TernCompiler.Compile(Program).Report;
        Assert.Contains("triple.t", report);
        Assert.Contains("fill.p", report);
    }

    [Theory]
    [InlineData("asm fn f() {\n var t: u8 = 3\n lda t\n}\nfn main() { f() }", 2, 14, "'t' in an asm fn has no initial value: the assembly sets it. Remove the value and store it in the assembly")]
    [InlineData("asm fn f() {\n var t: u8 @ $C000\n lda t\n}\nfn main() { f() }", 2, 2, "Only global variables can have a fixed address. Move the variable out of the function")]
    [InlineData("asm fn f() {\n lda #1\n var t: u8\n}\nfn main() { f() }", 3, 2, "'var' lines go at the start of an asm fn, before the first instruction. Move this line up")]
    [InlineData("var t: u8\nasm fn f() {\n var t: u8\n lda t\n}\nfn main() { f() }", 3, 2, "'t' is already declared at 1:1. Rename one of them")]
    [InlineData("fn main() { asm {\n var t: u8\n } }", 2, 2, "'var' is not allowed inside an asm block. Declare the variable before the block, or use 'var' lines at the start of an asm fn")]
    public void Errors(string source, int line, int column, string expected)
    {
        Expect.Error(source, line, column, expected);
    }
}
