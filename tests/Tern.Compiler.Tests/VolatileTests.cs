using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// Volatile memory (spec 4): the platform's volatile areas, <c>volatile var</c>, globals used from
/// two roots. And the error for a fixed-address variable that overlaps the program.
/// </summary>
public class VolatileTests
{
    /// <summary>
    /// <c>dst[i] = src[i]</c> loads Y once when <c>i</c> is ordinary memory, and reads <c>i</c> twice
    /// when it is volatile: the number of <c>ldy i</c> shows which.
    /// </summary>
    private static int IndexReads(string declaration, string extra = "")
    {
        var r = TernCompiler.Compile($$"""
            {{declaration}}
            var src: ptr u8 @ zp
            var dst: ptr u8 @ zp
            {{extra}}
            fn main() { dst[i] = src[i] }
            """);
        var main = r.Listing[r.Listing.IndexOf("\nmain:", StringComparison.Ordinal)..];
        return Count(main, "ldy i ");
    }

    private static int Count(string listing, string instruction) =>
        listing.Split('\n').Count(l => l.StartsWith('$') && l.Contains(instruction));

    [Theory]
    [InlineData("var i: u8", 1)]
    [InlineData("var i: u8 @ $C000", 1)]                  // fixed-address RAM is ordinary memory
    [InlineData("var i: u8 @ $D020", 2)]                  // I/O area
    [InlineData("var i: u8 @ $DFFF", 2)]                  // last byte of the I/O area
    [InlineData("var i: u8 @ $CFFF", 1)]
    [InlineData("var i: u8 @ $01", 2)]                    // CPU port
    [InlineData("var i: u8 @ $A2", 2)]                    // jiffy clock
    [InlineData("var i: u8 @ $0277", 2)]                  // keyboard buffer
    [InlineData("volatile var i: u8 @ $C000", 2)]
    [InlineData("volatile var i: u8", 2)]
    [InlineData("volatile var i: u8 @ $D020", 2)]         // already volatile: allowed
    [InlineData("volatile noinit var i: u8", 2)]
    [InlineData("noinit volatile var i: u8", 2)]
    [InlineData("private volatile var i: u8", 2)]
    public void Volatile_memory_is_read_every_time(string declaration, int reads)
    {
        Assert.Equal(reads, IndexReads(declaration));
    }

    [Fact]
    public void A_volatile_struct_makes_every_field_volatile()
    {
        var r = TernCompiler.Compile("""
            struct S { a: u8, i: u8 }
            volatile var s: S @ $C000
            var src: ptr u8 @ zp
            var dst: ptr u8 @ zp
            fn main() { dst[s.i] = src[s.i] }
            """);
        Assert.Equal(2, Count(r.Listing, "ldy s+1 "));
    }

    [Fact]
    public void A_global_used_by_main_and_a_handler_is_volatile()
    {
        Assert.Equal(2, IndexReads("var i: u8", "interrupt fn h() { i += 1 }"));
        Assert.Equal(1, IndexReads("var i: u8", "var j: u8\ninterrupt fn h() { j += 1 }"));
    }

    [Fact]
    public void A_global_used_by_two_handlers_is_volatile()
    {
        var r = TernCompiler.Compile("""
            var i: u8
            var src: ptr u8 @ zp
            var dst: ptr u8 @ zp
            interrupt fn h1() { dst[i] = src[i] }
            interrupt fn h2() { i += 1 }
            fn main() { }
            """);
        var h1 = r.Listing[r.Listing.IndexOf("\nh1:", StringComparison.Ordinal)..r.Listing.IndexOf("\nh2:", StringComparison.Ordinal)];
        Assert.Equal(2, Count(h1, "ldy i "));
    }

    [Fact]
    public void A_global_used_through_an_address_and_by_main_is_volatile()
    {
        // f may be called from anywhere, e.g. from a handler through a vector.
        Assert.Equal(2, IndexReads("var i: u8", "fn f() { i += 1 }\nvar w: u16 = addr(f)"));
        Assert.Equal(1, IndexReads("var i: u8", "var j: u8\nfn f() { j += 1 }\nvar w: u16 = addr(f)"));
    }

    [Fact]
    public void A_main_loop_sees_a_flag_set_by_a_handler()
    {
        var r = Run.Program("""
            var flag: u8
            var waited: u8
            interrupt fn handler() { flag = 1 }
            fn main() {
                asm {
                    lda #handler.lo
                    sta $FFFE
                    lda #handler.hi
                    sta $FFFF
                    cli
                }
                while flag == 0 { waited += 1 }
            }
            """, irqEvery: 2000);
        Assert.Equal(1, r.U8("flag"));
        Assert.True(r.U8("waited") > 0);
    }

    [Theory]
    [InlineData("var b: [u8; 16] @ $0810\nfn main() { b[0] = 1 }", "'b' at $0810-$081F overlaps the program and its variables at $0801-$")]
    [InlineData("var b: u8 @ $0801\nfn main() { b = 1 }", "'b' at $0801 overlaps the program and its variables")]
    [InlineData("var a: u8\nvar z: u8 @ $02\nfn main() { a = z }", "'z' at $0002 overlaps 'a' at $0002, which the compiler placed in zero page. Move 'z' out of $02-$8F")]
    [InlineData("volatile data t: [u8; 2] = [1, 2]\nfn main() { }", "'volatile' goes before 'var'")]
    [InlineData("volatile linear data t: [u16; 2] = [1, 2]\nfn main() { }", "'volatile' is for variables; data is read-only, so nothing changes it")]
    [InlineData("volatile fn f() { }\nfn main() { }", "'volatile' goes before 'var'")]
    [InlineData("volatile volatile var x: u8\nfn main() { }", "'volatile' is written twice")]
    [InlineData("noinit noinit var x: u8\nfn main() { }", "'noinit' is written twice")]
    [InlineData("volatile noinit volatile var x: u8\nfn main() { }", "'volatile' is written twice")]
    [InlineData("fn main() { volatile var x: u8 = 1 }", "'volatile' is for global variables; nothing else can change a local variable")]
    [InlineData("var volatile: u8\nfn main() { }", "Expected variable name, found 'volatile'")]
    public void Reports_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
    }

    [Fact]
    public void Fixed_address_variables_may_overlap_each_other_and_sit_after_the_program()
    {
        var r = Run.Program("""
            var screen: [u8; 40] @ $0400
            var first: u8 @ $0400
            var buf: [u8; 4] @ $C000
            fn main() {
                screen[0] = 7
                buf[3] = first
            }
            """);
        Assert.Equal(7, r.Ram.Bytes[0xC003]);
    }
}
