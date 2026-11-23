namespace Tern.Compiler.Tests;

/// <summary>
/// V1 fixes, gap 29: <c>keeps</c> on a function with a body (spec 6.1). Checked on <c>fn</c> and
/// <c>inline fn</c> (the body indexes with the other register; a statement that needs the kept
/// register is an error), a promise on <c>asm fn</c>; a <c>for ... using X</c> / <c>using Y</c> loop may
/// call a function that keeps its register.
/// </summary>
public class KeepsTests
{
    private const string Slots = """
        var ex: [u8; 8] = [0, 10, 0, 40, 0, 200, 70, 0]
        var obj: u8 = 41
        var count: u8
        var far: u8
        """;

    [Fact]
    public void A_using_loop_calls_functions_that_keep_its_register()
    {
        var r = Run.Program(Slots + """

            fn hits(o: u8) -> bool keeps Y {
                var d: u8 = ex[o] - obj
                return d < 40
            }
            inline fn above(o: u8) -> bool keeps Y {
                return ex[o] > obj
            }
            asm fn mark() keeps Y {
                ldx far
                inx
                stx far
            }
            fn main() {
                for s in 0..8 using Y {
                    if ex[s] != 0 {
                        if hits(s) { count += 1 }
                        if above(s) { count += 16 }
                        mark()
                    }
                }
            }
            """);
        int count = 0, far = 0;
        byte[] ex = [0, 10, 0, 40, 0, 200, 70, 0];
        foreach (var e in ex)
        {
            if (e == 0) continue;
            if ((byte)(e - 41) < 40) count += 1;
            if (e > 41) count += 16;
            far++;
        }
        Assert.Equal(count, r.U8("count"));
        Assert.Equal(far, r.U8("far"));
    }

    [Fact]
    public void A_function_that_keeps_X_indexes_with_Y()
    {
        var r = Run.Program(Slots + """

            fn total(o: u8) keeps X {
                count += ex[o]
                for i in 0..2 { far += ex[o + i] }
            }
            fn main() {
                for s in 0..4 using X { total(s << 1) }
            }
            """);
        Assert.Equal(70, r.U8("count"));                            // ex[0] + ex[2] + ex[4] + ex[6]
        Assert.Equal((10 + 40 + 200 + 70) & 0xFF, r.U8("far"));     // every slot
        Assert.DoesNotContain(Listing.Instructions(r, "total"), i => i.StartsWith("ldx") || i.StartsWith("inx") || i.StartsWith("tax"));
    }

    [Theory]
    // Review V1 fixes, finding 1: the optimizer moved the A parameter into the kept register (tax),
    // so each call set the loop counter to 9 and the loop never ended.
    [InlineData("fn f(v: u8 using A) keeps X", "for i in 0..4 using X", "f(9)", "X")]
    [InlineData("fn f(v: u8 using A, w: u8 using X) keeps Y", "for i in 0..4 using Y", "f(9, 1)", "Y")]
    [InlineData("fn f(v: u8 using A) keeps X, Y", "for i in 0..4 using X", "f(9)", "X")]
    [InlineData("fn f(v: u8 using A) keeps X, Y", "for i in 0..4 using Y", "f(9)", "Y")]
    public void The_optimizer_leaves_a_kept_register_alone(string header, string loop, string call, string kept)
    {
        var r = Run.Program($$"""
            var out: u8
            var out2: u8
            var count: u8
            {{header}} {
                out = 3
                out2 = v
            }
            fn main() {
                {{loop}} {
                    {{call}}
                    count += 1
                }
            }
            """);
        Assert.Equal(4, r.U8("count"));
        Assert.Equal(9, r.U8("out2"));
        var write = kept == "X" ? new[] { "tax", "ldx", "inx", "dex" } : ["tay", "ldy", "iny", "dey"];
        Assert.DoesNotContain(Listing.Instructions(r, "f"), i => write.Any(w => i.StartsWith(w)));
    }

    [Fact]
    public void An_asm_block_in_a_loop_may_call_a_function_that_keeps_the_register()
    {
        var r = Run.Program("""
            var n: u8
            fn bump() keeps X { n += 1 }
            fn main() {
                for i in 0..5 using X {
                    asm {
                        jsr bump
                    }
                }
            }
            """);
        Assert.Equal(5, r.U8("n"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void The_counter_stays_in_Y_across_the_call()
    {
        var r = Run.Program(Slots + """

            fn hits(o: u8) -> bool keeps Y {
                var d: u8 = ex[o] - obj
                return d < 40
            }
            fn main() {
                for s in 0..8 using Y {
                    if hits(s) { count += 1 }
                }
            }
            """);
        var code = Listing.Instructions(r);
        Assert.Equal("ldy #$00", code[0]);
        Assert.Contains("jsr hits", code);
        Assert.Contains("iny", code);
        Assert.Equal(1, Listing.Count(code, "ldy #$00"));
        Assert.Equal("ldx hits.o", Listing.Instructions(r, "hits")[0]);
        Assert.DoesNotContain(Listing.Instructions(r, "hits"), i => i.Contains('y') && !i.Contains("hits."));
    }

    [Theory]
    [InlineData("var p: ptr u8\nfn f() keeps Y { p[0] = 1 }\nfn main() { f() }", 2, 19, "p[i] needs the Y register, which 'f' keeps. Remove 'keeps Y' from 'f', or read the pointer outside it")]
    [InlineData("fn g() { }\nfn f() keeps X { g() }\nfn main() { f() }", 2, 18, "'g' can change X, which 'f' keeps. Write 'keeps X' on 'g' if it leaves X alone, or remove 'keeps X' from 'f'")]
    [InlineData("fn g() @ $C000\nfn f() keeps X { g() }\nfn main() { f() }", 2, 18, "'g' can change X, which 'f' keeps. Write 'keeps X' on 'g' if it leaves X alone, or remove 'keeps X' from 'f'")]
    [InlineData("var v: u8\nfn f() keeps A { v = 1 }\nfn main() { f() }", 2, 20, "This changes A, which 'f' keeps ('lda'). Change the statement so that it leaves A alone, or remove 'keeps A' from 'f'")]
    [InlineData("fn f() keeps X {\n asm {\n ldx #1\n }\n}\nfn main() { f() }", 2, 6, "This changes X, which 'f' keeps ('ldx'). Change the statement so that it leaves X alone, or remove 'keeps X' from 'f'")]
    [InlineData("var v: u8\nfn f() keeps X {\n for i in 0..3 using X { v += i }\n}\nfn main() { f() }", 3, 2, "This changes X, which 'f' keeps ('ldx'). Change the statement so that it leaves X alone, or remove 'keeps X' from 'f'")]
    [InlineData("var v: u8\ninline fn f() keeps Y { v = 1 }\nfn main() { for i in 0..3 using Y { f() } }", 0, 0, null)]
    [InlineData("fn f() -> u8 keeps A { return 1 }\nfn main() { f() }", 1, 20, "'f' returns a value in A, so it cannot keep A. Remove A from 'keeps'")]
    [InlineData("fn f() -> bool keeps carry { return true }\nfn main() { f() }", 1, 22, "'keeps' lists the registers A, X and Y, not Carry. Flags always count as changed. Remove Carry")]
    [InlineData("fn f() keeps X, X { }\nfn main() { f() }", 1, 17, "X is listed twice after 'keeps'. Remove one")]
    [InlineData("var n: u8\ninterrupt fn h() keeps A { n += 1 }\nfn main() { }", 2, 24, "'h' is an interrupt fn that only the CPU runs, and it saves and restores what it changes, so 'keeps' has no use. Remove 'keeps'")]
    [InlineData("fn f() keeps X @ $C000\nfn main() { }", 1, 16, "For a routine at a fixed address, 'keeps' comes after the address. Write fn f(...) @ $addr keeps X")]
    [InlineData("const fn f() -> int keeps X { return 1 }\nfn main() { }", 1, 27, "A const fn runs at compile time, so it has no registers to keep. Remove 'keeps'")]
    [InlineData("fn f() keeps X { }\nfn main() { for i in 0..3 using Y { f() } }", 2, 13, "The loop body calls a function, which can change Y. Remove 'using Y', or call only routines that keep Y")]
    [InlineData("fn f(v: u8 using Y) keeps Y { }\nfn main() { for i in 0..3 using Y { f(i + 1) } }", 2, 37, "'f' takes 'v' in Y, which holds the loop variable. Remove 'using Y' from the loop")]
    public void Reports_errors(string source, int line, int column, string? expected)
    {
        if (expected is null) Run.Program(source);
        else Expect.Error(source, line, column, expected);
    }
}
