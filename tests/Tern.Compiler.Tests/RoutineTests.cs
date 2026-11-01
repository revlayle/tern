using Tern.Compiler.Syntax;
using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>Routines at a fixed address (spec 6.6): <c>fn name(...) @ $addr keeps X, Y</c>.</summary>
public class RoutineTests
{
    /// <summary>
    /// Stand-in routines placed in RAM before the program runs:
    /// $C000 put: stores A at $C400+X and changes Y (keeps A and X).
    /// $C010 double: A = A * 2.
    /// $C020 is_big: carry set if A >= 100.
    /// $C030 widen: returns A as the low byte and $12 in X.
    /// $C040 log: appends A to $C500 (count at $C5FF), changes A, keeps X and Y.
    /// </summary>
    private static void Stubs(Ram ram)
    {
        ram.Load(0xC000, [0x9D, 0x00, 0xC4, 0xA0, 0xEE, 0x60]);             // sta $C400,x / ldy #$EE / rts
        ram.Load(0xC010, [0x0A, 0x60]);                                     // asl / rts
        ram.Load(0xC020, [0xC9, 0x64, 0x60]);                               // cmp #100 / rts
        ram.Load(0xC030, [0xA2, 0x12, 0x60]);                               // ldx #$12 / rts
        ram.Load(0xC040, [
            0x8C, 0xFE, 0xC5,   // sty $C5FE
            0xAC, 0xFF, 0xC5,   // ldy $C5FF
            0x99, 0x00, 0xC5,   // sta $C500,y
            0xEE, 0xFF, 0xC5,   // inc $C5FF
            0xAC, 0xFE, 0xC5,   // ldy $C5FE
            0xA9, 0xEE,         // lda #$EE
            0x60,               // rts
        ]);
    }

    private const string Declarations = """
        const STUBS = $C000
        fn put(v: u8 using A, at: u8 using X) @ STUBS keeps A, X
        fn double(v: u8 using A) -> u8 @ STUBS + $10
        fn is_big(v: u8 using A) -> bool @ $C020
        fn widen(v: u8 using A) -> u16 @ $C030
        fn log(v: u8 using A) @ $C040 keeps X, Y

        """;

    private static Run RunWithStubs(string source) => Run.Program(Declarations + source, setup: Stubs);

    private static byte[] Logged(Run r) => r.Ram.Bytes.AsSpan(0xC500, r.Ram.Bytes[0xC5FF]).ToArray();

    [Fact]
    public void Calls_are_a_direct_jsr()
    {
        var r = RunWithStubs("""
            var x: u8 = 7
            fn main() {
                log('A')
                log(x)
            }
            """);
        Assert.Equal([0x41, 7], Logged(r));
        Assert.Contains("A9 41     lda #$41", r.Result.Listing);
        Assert.Contains("20 40 C0  jsr $C040", r.Result.Listing);
        Assert.DoesNotContain("\nlog:", r.Result.Listing);
        Assert.Equal(0xC040, r.Address("log"));
        Assert.Contains("al C:c040 .log", r.Result.ViceLabels);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(127)]
    [InlineData(255)]
    public void Return_values_in_a_ax_and_carry(int v)
    {
        var r = RunWithStubs($$"""
            var d: u8
            var w: u16
            var big: bool
            fn main() {
                d = double({{v}})
                w = widen({{v}})
                big = false
                if is_big({{v}}) { big = true }
            }
            """);
        Assert.Equal((v * 2) & 0xFF, r.U8("d"));
        Assert.Equal(0x1200 | v, r.U16("w"));
        Assert.Equal(v >= 100 ? 1 : 0, r.U8("big"));
    }

    [Fact]
    public void Parameters_in_several_registers()
    {
        var r = RunWithStubs("""
            var at: u8 = 3
            fn main() {
                put($AB, at)
                put($CD, at + 2)
            }
            """);
        Assert.Equal([0, 0, 0, 0xAB, 0, 0xCD], r.Ram.Bytes.AsSpan(0xC400, 6).ToArray());
    }

    [Fact]
    public void A_one_byte_argument_for_A_is_computed_straight_into_A()
    {
        var r = RunWithStubs("""
            data msg: [u8; 3] = [5, 6, 7]
            var i: u8 = 2
            fn main() { log(msg[i] + 1) }
            """);
        Assert.Equal([8], Logged(r));
        var main = r.Result.Listing[r.Result.Listing.IndexOf("\nmain:")..];
        Assert.DoesNotContain("_t", main);
    }

    [Fact]
    public void Loops_using_x_or_y_may_call_routines_that_keep_the_register()
    {
        var r = RunWithStubs("""
            data msg = "HELLO"
            fn main() {
                for i in 0..len(msg) using X { log(msg[i]) }
                for j in 0..3 using Y { log(j) }
            }
            """);
        Assert.Equal("HELLO"u8.ToArray().Concat(new byte[] { 0, 1, 2 }), Logged(r));
        Assert.Contains("BD", r.Result.Listing);    // lda msg,x
    }

    [Fact]
    public void Routines_in_assembly()
    {
        var r = RunWithStubs("""
            var lo: u8
            var hi: u8
            fn main() {
                for i in 0..3 using X {
                    asm {
                        txa
                        jsr log         // log keeps X
                    }
                }
                asm {
                    lda #log.lo
                    sta lo
                    lda #log.hi
                    sta hi
                }
            }
            """);
        Assert.Equal([0, 1, 2], Logged(r));
        Assert.Equal(0xC040, r.U8("lo") | (r.U8("hi") << 8));
        Assert.Contains("jsr log", r.Result.Listing);
    }

    [Fact]
    public void Unused_routines_add_nothing()
    {
        var with = TernCompiler.Compile(Declarations + "fn main() { }");
        var without = TernCompiler.Compile("fn main() { }");
        Assert.Equal(without.Image.Length, with.Image.Length);
    }

    [Theory]
    [InlineData("fn f(v: u8) @ $C000\nfn main() { }", "Parameter 'v' must be passed in a register")]
    [InlineData("fn f() @ $C000 { }\nfn main() { }", "A function with a body at a fixed address is not supported yet")]
    [InlineData("asm fn f() @ $C000 { }\nfn main() { }", "An asm fn at a fixed address is not supported yet")]
    [InlineData("fn f() @ $C000 keeps carry\nfn main() { }", "'keeps' lists the registers A, X and Y, not Carry")]
    [InlineData("fn f() @ $C000 keeps AX\nfn main() { }", "'keeps' lists the registers A, X and Y, not AX")]
    [InlineData("fn f() @ $C000 keeps X, X\nfn main() { }", "X is listed twice after 'keeps'")]
    [InlineData("fn f() -> u16 @ $C000 keeps X\nfn main() { }", "'f' returns a value in X, so it cannot keep X")]
    [InlineData("fn f() -> u8 @ $C000 keeps A\nfn main() { }", "'f' returns a value in A, so it cannot keep A")]
    [InlineData("var a: u16\nfn f() @ a\nfn main() { }", "The address must be known at compile time")]
    [InlineData("fn f() @ $10000\nfn main() { }", "Address $10000 is outside memory")]
    [InlineData("fn main() @ $1000", "'main' must have a body")]
    [InlineData("fn f() keeps X { }\nfn main() { }", "Expected '{'")]
    [InlineData("fn f() @ $C000 keeps\nfn main() { }", "Expected a register or flag")]
    [InlineData("fn f(v: u8 using A) @ $C000 keeps Y\nfn main() { for i in 0..3 using X { f(i) } }", "The loop body calls a function, which can change X. Remove 'using X', or call only routines that keep X")]
    [InlineData("fn f(v: u8 using X) @ $C000 keeps X\nfn main() { for i in 0..3 using X { f(1) } }", "'f' takes 'v' in X, which holds the loop variable")]
    [InlineData("fn f(v: u16 using XY) @ $C000 keeps X, Y\nfn main() { for i in 0..3 using Y { f(1) } }", "'f' takes 'v' in Y, which holds the loop variable")]
    [InlineData("fn f() @ $C000 keeps Y\nfn main() { for i in 0..3 using X { asm {\n jsr f\n } } }", "'jsr' calls a subroutine, which can change X")]
    [InlineData("fn f() @ $C000\nvar p: ptr u8\nfn main() { p = addr(f) }", "addr() of a function is not supported yet")]
    public void Reports_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
    }
}
