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
    /// when it is volatile: the number of reads of <c>i</c> the CPU makes shows which (review
    /// code-quality-tests, finding 3). A volatile <c>i</c> is read for the destination first (spec 5), into a temporary.
    /// </summary>
    private static int IndexReads(string declaration, string extra = "")
    {
        var r = Run.Program($$"""
            {{declaration}}
            var src: ptr u8 @ zp = $3000
            var dst: ptr u8 @ zp = $3100
            {{extra}}
            fn main() { dst[i] = src[i] }
            """, watch: c => [c.Symbols["i"]]);
        int i = r.Address("i");
        Assert.Equal(r.Ram.Bytes[0x3000 + r.Ram.Bytes[i]], r.Ram.Bytes[0x3100 + r.Ram.Bytes[i]]);
        return r.Log!.Reads(i);
    }

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
        var r = Run.Program("""
            struct S { a: u8, i: u8 }
            volatile var s: S @ $C000
            var src: ptr u8 @ zp = $3000
            var dst: ptr u8 @ zp = $3100
            fn main() { dst[s.i] = src[s.i] }
            """, watch: _ => [0xC000, 0xC001]);
        Assert.Equal(2, r.Log!.Reads(0xC001));
        Assert.Equal(0, r.Log.Reads(0xC000));
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
        // h1 runs once, from an IRQ; main waits for it.
        var r = Run.Program("""
            var i: u8
            var done: bool
            var src: ptr u8 @ zp = $3000
            var dst: ptr u8 @ zp = $3100
            interrupt fn h1() {
                dst[i] = src[i]
                done = true
                asm { sei }
            }
            interrupt fn h2() { i += 1 }
            fn main() {
                asm {
                    lda #h1.lo
                    sta $FFFE
                    lda #h1.hi
                    sta $FFFF
                    cli
                }
                while !done { }
            }
            """, irqEvery: 500, watch: c => [c.Symbols["i"]]);
        Assert.Equal(1, r.Interrupts);
        Assert.Equal(2, r.Log!.Reads(r.Address("i")));
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
    [InlineData("var b: [u8; 16] @ $0810\nfn main() { b[0] = 1 }", 1, 1, "'b' at $0810-$081F overlaps the program and its variables at $0801-$0819. Move 'b', or make the program smaller")]
    [InlineData("var b: u8 @ $0801\nfn main() { b = 1 }", 1, 1, "'b' at $0801 overlaps the program and its variables at $0801-$0819. Move 'b', or make the program smaller")]
    [InlineData("var a: u8\nvar z: u8 @ $02\nfn main() { a = z }", 2, 1, "'z' at $0002 overlaps 'a' at $0002, which the compiler placed in zero page. Move 'z' out of $02-$8F, $FB-$FE")]
    [InlineData("volatile data t: [u8; 2] = [1, 2]\nfn main() { }", 1, 1, "'volatile' goes before 'var', e.g. 'volatile var flags: u8 @ $C000'; found 'data'. Write 'volatile var' or remove 'volatile'")]
    [InlineData("volatile linear data t: [u16; 2] = [1, 2]\nfn main() { }", 1, 1, "'volatile' is for variables; data is read-only, so nothing changes it. Remove 'volatile'")]
    [InlineData("volatile fn f() { }\nfn main() { }", 1, 1, "'volatile' goes before 'var', e.g. 'volatile var flags: u8 @ $C000'; found 'fn'. Write 'volatile var' or remove 'volatile'")]
    [InlineData("volatile volatile var x: u8\nfn main() { }", 1, 1, "'volatile' is written twice. Remove one")]
    [InlineData("noinit noinit var x: u8\nfn main() { }", 1, 1, "'noinit' is written twice. Remove one")]
    [InlineData("volatile noinit volatile var x: u8\nfn main() { }", 1, 1, "'volatile' is written twice. Remove one")]
    [InlineData("fn main() { volatile var x: u8 = 1 }", 1, 13, "'volatile' is for global variables; nothing else can change a local variable. Remove 'volatile'")]
    [InlineData("var volatile: u8\nfn main() { }", 1, 5, "Expected variable name, found 'volatile', which is a keyword. Use another name")]
    public void Reports_errors(string source, int line, int column, string expected)
    {
        Expect.Error(source, line, column, expected);
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
