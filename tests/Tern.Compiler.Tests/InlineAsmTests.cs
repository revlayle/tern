using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>M9: <c>inline asm fn</c> (spec 6.2, 7).</summary>
public class InlineAsmTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(255)]
    public void The_assembly_is_copied_with_no_jsr_and_no_rts(int x)
    {
        var r = Run.Program($$"""
            var out: u8
            inline asm fn twice_plus_one(v: u8 using A) -> u8 using A {
                asl
                ora #1
            }
            fn main() {
                var t: u8 = {{x}}
                out = twice_plus_one(t)
            }
            """);
        Assert.Equal(((x << 1) | 1) & 0xFF, r.U8("out"));
        var main = string.Join("\n", Listing.Lines(r, "main"));
        Assert.DoesNotContain("jsr", main);
        Assert.DoesNotContain("twice_plus_one:", r.Result.Listing);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(4, 5)]
    [InlineData(5, 5)]
    [InlineData(6, 5)]
    [InlineData(255, 255)]
    [InlineData(0, 0)]
    public void A_carry_return_gives_a_branch_a_negation_and_a_stored_value(int x, int limit)
    {
        var r = Run.Program($$"""
            var if_yes: u8
            var if_no: u8
            var stored: bool
            var limit: u8
            inline asm fn at_least(v: u8 using X) -> bool using carry {
                cpx limit
            }
            fn main() {
                var v: u8 = {{x}}
                limit = {{limit}}
                if at_least(v) { if_yes = 1 }
                if !at_least(v) { if_no = 1 }
                stored = at_least(v)
            }
            """);
        bool expected = x >= limit;
        Assert.Equal(expected ? 1 : 0, r.U8("if_yes"));
        Assert.Equal(expected ? 0 : 1, r.U8("if_no"));
        Assert.Equal(expected ? 1 : 0, r.U8("stored"));
        Assert.DoesNotContain("jsr", string.Join("\n", Listing.Lines(r, "main")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x10)]
    [InlineData(0xEF)]
    [InlineData(0xFF)]
    public void A_zero_return_tests_the_flag_of_the_assembly(int x)
    {
        var r = Run.Program($$"""
            var bit_clear: u8
            var bit_set: u8
            var stored: bool
            inline asm fn bit4_clear(v: u8 using A) -> bool using zero {
                and #$10
            }
            fn main() {
                var v: u8 = {{x}}
                if bit4_clear(v) { bit_clear = 1 }
                if !bit4_clear(v) { bit_set = 1 }
                stored = bit4_clear(v)
            }
            """);
        bool clear = (x & 0x10) == 0;
        Assert.Equal(clear ? 1 : 0, r.U8("bit_clear"));
        Assert.Equal(clear ? 0 : 1, r.U8("bit_set"));
        Assert.Equal(clear ? 1 : 0, r.U8("stored"));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x7F)]
    [InlineData(0x80)]
    [InlineData(0xFF)]
    public void A_negative_return_works(int x)
    {
        var r = Run.Program($$"""
            var out: u8
            inline asm fn high_bit(v: u8 using A) -> bool using negative {
                cmp #0
            }
            fn main() {
                var v: u8 = {{x}}
                if high_bit(v) { out = 1 }
            }
            """);
        Assert.Equal(x >= 0x80 ? 1 : 0, r.U8("out"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(200, 100)]
    [InlineData(200, 200)]
    [InlineData(255, 1)]
    [InlineData(255, 255)]
    public void An_empty_body_reads_the_flag_the_asm_block_left(int a, int b)
    {
        var r = Run.Program($$"""
            var yes: u8
            var no: u8
            var stored: bool
            inline asm fn carry_set() -> bool using carry {}
            fn main() {
                asm {
                    lda #{{a}}
                    cmp #{{b}}
                }
                if carry_set() { yes = 1 }
                asm {
                    lda #{{a}}
                    cmp #{{b}}
                }
                if !carry_set() { no = 1 }
                asm {
                    lda #{{a}}
                    cmp #{{b}}
                }
                stored = carry_set()
            }
            """);
        bool ge = a >= b;
        Assert.Equal(ge ? 1 : 0, r.U8("yes"));
        Assert.Equal(ge ? 0 : 1, r.U8("no"));
        Assert.Equal(ge ? 1 : 0, r.U8("stored"));
    }

    [Theory]
    [InlineData(5, 3, 1)]
    [InlineData(3, 3, 1)]
    [InlineData(2, 3, 0)]
    public void An_empty_body_works_for_the_first_test_of_a_while(int a, int b, int runs)
    {
        var r = Run.Program($$"""
            var n: u8
            inline asm fn carry_set() -> bool using carry {}
            fn main() {
                asm {
                    lda #{{a}}
                    cmp #{{b}}
                }
                while carry_set() {
                    n += 1
                    break
                }
            }
            """);
        Assert.Equal(runs, r.U8("n"));
    }

    [Fact]
    public void An_empty_body_costs_only_the_branch()
    {
        var r = Run.Program("""
            var yes: u8
            inline asm fn carry_set() -> bool using carry {}
            fn main() {
                asm {
                    lda #5
                    cmp #3
                }
                if carry_set() { yes = 1 }
            }
            """);
        var main = Listing.Lines(r, "main").Select(l => l.Trim()).Where(l => l.StartsWith('$')).ToList();
        int cmp = main.FindIndex(l => l.Contains(" cmp "));
        Assert.Contains(" bcc ", main[cmp + 1]);
        Assert.Equal(1, r.U8("yes"));
    }

    [Fact]
    public void The_library_interrupt_switches_are_inline_asm()
    {
        var r = Run.Program("""
            import c64
            fn main() {
                c64.interrupts_off()
                c64.interrupts_on()
            }
            """); 
        var main = Listing.Lines(r, "main").Select(l => l.Trim()).ToList();
        Assert.DoesNotContain(main, l => l.Contains("jsr"));
        Assert.Contains(main, l => l.EndsWith(" sei") || l.Contains(" sei"));
        Assert.Contains(main, l => l.Contains(" cli"));
    }

    [Theory]
    [InlineData("inline asm fn f() {\n rts\n}\nfn main() { }", "would end the function that calls it")]
    [InlineData("inline asm fn f() {\n nop\n rti\n}\nfn main() { }", "would end the function that calls it")]
    [InlineData("inline asm fn f(x: u8) {\n nop\n}\nfn main() { }", "must be passed in a register")]
    [InlineData("inline asm fn f(true using carry) {\n nop\n}\nfn main() { }", "Fixed inputs are only for routines at a fixed address")]
    [InlineData("inline asm fn f() @ $1000\nfn main() { }", "not supported yet")]
    [InlineData("inline asm fn f() { nop }\nfn main() { asm {\n jsr f\n } }", "is inline")]
    public void Reports_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
    }
}
