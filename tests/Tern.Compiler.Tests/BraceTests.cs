using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>A '{' may go on the line after a block header (spec 2); anywhere else a line break before it is an error.</summary>
public class BraceTests
{
    [Fact]
    public void Every_block_header_accepts_a_brace_on_the_next_line()
    {
        var r = Run.Program("""
            memory
            {
                $0801..=$9FFF program
            }
            struct Pair
            {
                a: u8
                b: u8
            }
            var p: Pair
            var total: u8
            var m: u8
            var f: u8
            var w: u8
            var asmv: u8
            interrupt fn irq()
            {
                f += 1
            }
            asm fn bump()
            {
                inc asmv
            }
            fn main()
            {
                p.a = 1
                for i in 0..3
                {
                    total += 2
                }
                w = 0
                while w < 4
                {
                    w += 1
                }
                if w == 4
                {
                    total += 10
                }
                else if w == 5
                {
                    total += 20
                }
                else
                {
                    total += 30
                }
                match total
                {
                    16 => m = 7
                    _ => m = 9
                }
                asm
                {
                    inc asmv
                }
                bump()
            }
            """);
        Assert.Equal(16, r.U8("total"));
        Assert.Equal(7, r.U8("m"));
        Assert.Equal(2, r.U8("asmv"));
    }

    [Theory]
    [InlineData("var x: u8\nfn main()\n\n  x = 1\n}", 2, 10, "Expected '{', found end of line")]
    [InlineData("var x: u8\nfn main() { x = 1 }\n{ x = 2 }", 3, 1, "Expected 'const', 'var', 'data', 'struct' or 'fn', found '{'. Check that statements are inside a function, and that each '{' has its '}'")]
    [InlineData("var x: u8 = 1\n{\nfn main() { }", 2, 1, "Expected 'const', 'var', 'data', 'struct' or 'fn', found '{'. Check that statements are inside a function, and that each '{' has its '}'")]
    [InlineData("var x: u8\nfn f() @ $E000\n{ }\nfn main() { }", 3, 1, "Expected 'const', 'var', 'data', 'struct' or 'fn', found '{'. Check that statements are inside a function, and that each '{' has its '}'")]    // a routine at a fixed address has no body
    public void A_brace_after_a_line_break_where_no_block_is_expected_is_an_error(string source, int line, int column, string message)
    {
        Expect.Error(() => Run.Program(source), line, column, message);
    }
}
