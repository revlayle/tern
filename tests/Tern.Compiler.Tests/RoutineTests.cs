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
    /// $C060 cursor, like KERNAL PLOT: carry set loads X and Y from $C5F0/$C5F1; carry clear stores them there.
    /// $C080 mode: stores X at $C5F2 and A at $C5F3.
    /// </summary>
    private static void Stubs(Ram ram)
    {
        ram.Bytes.AsSpan(0xC400, 0x200).Clear();   // the put and log buffers, and the log count, start empty
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
        ram.Load(0xC060, [
            0x90, 0x07,         // bcc set
            0xAE, 0xF0, 0xC5,   // ldx $C5F0
            0xAC, 0xF1, 0xC5,   // ldy $C5F1
            0x60,               // rts
            0x8E, 0xF0, 0xC5,   // set: stx $C5F0
            0x8C, 0xF1, 0xC5,   // sty $C5F1
            0x60,               // rts
        ]);
        ram.Load(0xC080, [0x8E, 0xF2, 0xC5, 0x8D, 0xF3, 0xC5, 0x60]);    // stx $C5F2 / sta $C5F3 / rts
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

    [Trait("Shape", "nmos6502")]
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
        Assert.Contains("A9 41     lda #$41", Listing.Text(r));
        Assert.Contains("20 40 C0  jsr $C040", Listing.Text(r));
        Assert.DoesNotContain("log", Listing.Sections(r));
        Assert.Equal(0xC040, r.Address("log"));
        Assert.Contains("al C:c040 .log", r.Result.Labels);
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

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_one_byte_argument_for_A_is_computed_straight_into_A()
    {
        var r = RunWithStubs("""
            data msg: [u8; 3] = [5, 6, 7]
            var i: u8 = 2
            fn main() { log(msg[i] + 1) }
            """);
        Assert.Equal([8], Logged(r));
        var main = Listing.Code(r);
        Assert.DoesNotContain("_t", main);
        Assert.Matches(@"lda msg,[xy]\|clc\|adc #\$01\|jsr \$C040", main);
    }

    [Trait("Shape", "nmos6502")]
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
        Assert.Matches(@"BD [0-9A-F]{2} [0-9A-F]{2} +lda msg,x", Listing.Text(r));
    }

    [Trait("Shape", "nmos6502")]
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
        Assert.Contains("jsr log", Listing.Instructions(r));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Fixed_inputs_in_the_carry()
    {
        var r = RunWithStubs("""
            fn get_cursor(true using carry) -> u16 using XY @ $C060
            fn set_cursor(false using carry, row: u8 using X, col: u8 using Y) @ $C060
            var pos: u16
            var row: u8 = 5
            fn main() {
                set_cursor(row, 10)
                pos = get_cursor()
            }
            """);
        Assert.Equal(5, r.Ram.Bytes[0xC5F0]);
        Assert.Equal(10, r.Ram.Bytes[0xC5F1]);
        Assert.Equal(0x0A05, r.U16("pos"));
        // Spec 6.6 and 11: the carry is set or cleared right before the jsr.
        Assert.Equal(["ldx row", "ldy #$0A", "clc", "jsr $C060", "sec", "jsr $C060", "stx pos", "sty pos+1", "rts"], Listing.Instructions(r));
    }

    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void Fixed_inputs_in_registers(int v)
    {
        var r = RunWithStubs($$"""
            const MODE = 3
            fn mode(MODE using X, v: u8 using A) @ $C080
            fn mode7(v: u8 using A, 7 using X) @ $C080
            fn both(200 using A, $FF using X) @ $C080
            var seen: [u8; 6]
            var x: u8 = {{v}}
            fn main() {
                mode(x)
                asm {
                    lda $C5F2
                    sta seen
                    lda $C5F3
                    sta seen+1
                }
                mode7(x)
                asm {
                    lda $C5F2
                    sta seen+2
                    lda $C5F3
                    sta seen+3
                }
                both()
                asm {
                    lda $C5F2
                    sta seen+4
                    lda $C5F3
                    sta seen+5
                }
            }
            """);
        Assert.Equal([3, (byte)v, 7, (byte)v, 0xFF, 200], r.Ram.Bytes.AsSpan(r.Address("seen"), 6).ToArray());
        Assert.Contains("ldx #$03", Listing.Instructions(r));
    }

    [Fact]
    public void Unused_routines_add_nothing()
    {
        var with = TernCompiler.Compile(Declarations + "fn main() { }");
        var without = TernCompiler.Compile("fn main() { }");
        Assert.Equal(without.Image.Length, with.Image.Length);
    }

    [Theory]
    [InlineData("fn f(v: u8) @ $C000\nfn main() { }", 1, 6, "Parameter 'v' must be passed in a register (using A, X, Y, AX, AY or XY): a routine at a fixed address has no memory for parameters. Add 'using A', 'using X' or 'using Y'")]
    [InlineData("fn f() @ $C000 { }\nfn main() { }", 1, 8, "A function with a body at a fixed address is not supported yet. Without a body, it declares a routine that is already there. Remove the address or the body")]
    [InlineData("asm fn f() @ $C000 { }\nfn main() { }", 1, 12, "An asm fn at a fixed address is not supported yet. Remove 'asm' and the body to declare a routine that is already there, or remove the address")]
    [InlineData("fn f() @ $C000 keeps carry\nfn main() { }", 1, 22, "'keeps' lists the registers A, X and Y, not Carry. Flags always count as changed. Remove Carry")]
    [InlineData("fn f() @ $C000 keeps AX\nfn main() { }", 1, 22, "'keeps' lists the registers A, X and Y, not AX. Flags always count as changed. Remove AX")]
    [InlineData("fn f() @ $C000 keeps X, X\nfn main() { }", 1, 25, "X is listed twice after 'keeps'. Remove one")]
    [InlineData("fn f() -> u16 @ $C000 keeps X\nfn main() { }", 1, 29, "'f' returns a value in X, so it cannot keep X. Remove X from 'keeps'")]
    [InlineData("fn f() -> u8 @ $C000 keeps A\nfn main() { }", 1, 28, "'f' returns a value in A, so it cannot keep A. Remove A from 'keeps'")]
    [InlineData("var a: u16\nfn f() @ a\nfn main() { }", 2, 10, "The address must be known at compile time. Use a number or a constant")]
    [InlineData("fn f() @ $10000\nfn main() { }", 1, 10, "Address $10000 is outside memory. Use an address from $0000 to $FFFF")]
    [InlineData("fn main() @ $1000", 1, 1, "'main' must have a body. Remove the address and write the body")]
    [InlineData("fn f() keeps X { }\nfn main() { }", 1, 8, "'keeps' is only for routines at a fixed address: fn f(...) @ $addr keeps X. Remove 'keeps' here")]
    [InlineData("fn f() -> u8 keeps X { return 1 }\nfn main() { }", 1, 14, "'keeps' is only for routines at a fixed address: fn f(...) @ $addr keeps X. Remove 'keeps' here")]
    [InlineData("fn f() @ $C000 keeps\nfn main() { }", 2, 1, "Expected a register or flag (A, X, Y, AX, AY, XY, carry, zero, negative, overflow), found 'fn'")]
    [InlineData("fn f(v: u8 using A) @ $C000 keeps Y\nfn main() { for i in 0..3 using X { f(i) } }", 2, 13, "The loop body calls a function, which can change X. Remove 'using X', or call only routines that keep X")]
    [InlineData("fn f(v: u8 using X) @ $C000 keeps X\nfn main() { for i in 0..3 using X { f(1) } }", 2, 37, "'f' takes 'v' in X, which holds the loop variable. Remove 'using X' from the loop")]
    // Review code-quality-semantics 11: a misspelled call is reported as such, not as a call that changes X.
    [InlineData("var out: u8\nfn helper(v: u8) -> u8 { return v }\nfn main() { for i in 0..3 using X { out += helpr(i) } }", 3, 44, "'helpr' is not a function. Check the spelling, or declare it")]
    [InlineData("fn f(v: u16 using XY) @ $C000 keeps X, Y\nfn main() { for i in 0..3 using Y { f(1) } }", 2, 37, "'f' takes 'v' in Y, which holds the loop variable. Remove 'using Y' from the loop")]
    [InlineData("fn f() @ $C000 keeps Y\nfn main() { for i in 0..3 using X { asm {\n jsr f\n } } }", 3, 2, "'jsr' calls a subroutine, which can change X, the register of the loop variable. Remove 'using X' from the loop, or call a routine that keeps X")]
    [InlineData("fn f(true using carry) { }\nfn main() { }", 1, 6, "Fixed inputs are only for routines at a fixed address (fn name(...) @ $addr). Give the parameter a name and a type: 'name: type'")]
    [InlineData("asm fn f(1 using X) { }\nfn main() { }", 1, 10, "Fixed inputs are only for routines at a fixed address. An asm fn sets up its inputs in its own code. Remove it")]
    [InlineData("var m: u8\nfn f(m using X) @ $C000\nfn main() { }", 2, 6, "A fixed input must be a constant. Use a number, true, false or a const")]
    [InlineData("fn f(1 using carry) @ $C000\nfn main() { }", 1, 6, "The carry takes true or false. Write 'true using carry' or 'false using carry'")]
    [InlineData("fn f(true using X) @ $C000\nfn main() { }", 1, 6, "Expected u8, found bool. Use 'if' to choose a number, as in 'if b { x = 1 }'")]
    [InlineData("fn f(256 using X) @ $C000\nfn main() { }", 1, 6, "256 does not fit in u8 (0 to 255). Use a value in that range, or a wider type")]
    [InlineData("fn f(1 using AX) @ $C000\nfn main() { }", 1, 6, "A fixed input goes in A, X, Y or the carry, not AX. Use one of those")]
    [InlineData("fn f(true using zero) @ $C000\nfn main() { }", 1, 6, "A fixed input goes in A, X, Y or the carry, not Zero. Use one of those")]
    [InlineData("fn f(1 using X, v: u8 using X) @ $C000\nfn main() { }", 1, 6, "Register X is used by more than one parameter. Use another register for one of them")]
    [InlineData("fn f(true using carry, false using carry) @ $C000\nfn main() { }", 1, 24, "The carry is set more than once. Remove one")]
    [InlineData("fn f(1) @ $C000\nfn main() { }", 1, 7, "Expected a parameter 'name: type', or a fixed input such as 'true using carry'; found ')'. Write the parameter as 'name: type'")]
    [InlineData("fn f(2 using X) @ $C000 keeps X\nfn main() { for i in 0..3 using X { f() } }", 2, 37, "'f' takes a fixed input in X, which holds the loop variable. Remove 'using X' from the loop")]
    [InlineData("fn f(true using carry) @ $C000\nfn main() { f(true) }", 2, 13, "'f' takes 0 argument(s), not 1. Pass 0: f()")]
    public void Reports_errors(string source, int line, int column, string expected)
    {
        Expect.Error(source, line, column, expected);
    }
}
