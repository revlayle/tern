using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>M19: <c>-> bool using overflow</c>, the V flag as a return value (spec 6.1, 7).</summary>
public class OverflowTests
{
    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData(0x00)]
    [InlineData(0x40)]
    [InlineData(0x80)]
    [InlineData(0xBF)]
    [InlineData(0xFF)]
    public void Bit_copies_bit_6_into_the_flag_for_if_not_and_a_stored_value(int value)
    {
        var r = Run.Program($$"""
            var flags: u8 = {{value}}
            var if_yes: u8
            var if_no: u8
            var stored: bool
            inline asm fn bit6() -> bool using overflow {
                bit flags
            }
            fn main() {
                if bit6() { if_yes = 1 }
                if !bit6() { if_no = 1 }
                stored = bit6()
            }
            """);
        bool set = (value & 0x40) != 0;
        Assert.Equal(set ? 1 : 0, r.U8("if_yes"));
        Assert.Equal(set ? 0 : 1, r.U8("if_no"));
        Assert.Equal(set ? 1 : 0, r.U8("stored"));
        Assert.DoesNotContain(Listing.Instructions(r), l => l.StartsWith("jsr"));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x40)]
    [InlineData(0x60)]
    [InlineData(0x7F)]
    [InlineData(0xFF)]
    public void A_while_loop_tests_the_flag_on_each_pass(int value)
    {
        var r = Run.Program($$"""
            var flags: u8 = {{value}}
            var n: u8
            inline asm fn bit6() -> bool using overflow {
                bit flags
            }
            fn main() {
                while bit6() {
                    n += 1
                    flags = flags << 1
                }
            }
            """);
        int expected = 0;
        for (int v = value; (v & 0x40) != 0; v = (v << 1) & 0xFF) expected++;
        Assert.Equal(expected, r.U8("n"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(127, 1)]
    [InlineData(100, 27)]
    [InlineData(100, 28)]
    [InlineData(128, 255)]
    [InlineData(128, 0)]
    [InlineData(255, 255)]
    [InlineData(200, 200)]
    public void An_asm_fn_returns_the_overflow_of_a_signed_add(int a, int b)
    {
        var r = Run.Program($$"""
            var if_yes: u8
            var if_no: u8
            var stored: bool
            asm fn overflows(x: u8, y: u8) -> bool using overflow {
                lda x
                clc
                adc y
            }
            fn main() {
                if overflows({{a}}, {{b}}) { if_yes = 1 }
                if !overflows({{a}}, {{b}}) { if_no = 1 }
                stored = overflows({{a}}, {{b}})
            }
            """);
        int sum = (sbyte)(byte)a + (sbyte)(byte)b;
        bool overflow = sum < -128 || sum > 127;
        Assert.Equal(overflow ? 1 : 0, r.U8("if_yes"));
        Assert.Equal(overflow ? 0 : 1, r.U8("if_no"));
        Assert.Equal(overflow ? 1 : 0, r.U8("stored"));
    }

    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData(0x00)]
    [InlineData(0x40)]
    [InlineData(0xC0)]
    public void A_routine_at_a_fixed_address_returns_the_flag(int value)
    {
        var r = Run.Program("""
            var yes: u8
            fn test_bit() -> bool using overflow @ $C000
            fn main() {
                if test_bit() { yes = 1 }
            }
            """, setup: ram =>
            {
                // bit $C010 / rts
                ram.Bytes[0xC000] = 0x2C; ram.Bytes[0xC001] = 0x10; ram.Bytes[0xC002] = 0xC0; ram.Bytes[0xC003] = 0x60;
                ram.Bytes[0xC010] = (byte)value;
            });
        Assert.Equal((value & 0x40) != 0 ? 1 : 0, r.U8("yes"));
        var main = Listing.Instructions(r);
        int jsr = main.FindIndex(l => l.StartsWith("jsr"));
        Assert.StartsWith("bvc ", main[jsr + 1]);
    }

    [Theory]
    [InlineData(100, 27)]
    [InlineData(100, 28)]
    [InlineData(200, 100)]
    public void An_empty_body_reads_the_flag_the_asm_block_left(int a, int b)
    {
        var r = Run.Program($$"""
            var yes: u8
            var no: u8
            var stored: bool
            var loops: u8
            inline asm fn overflow_set() -> bool using overflow {}
            fn main() {
                asm {
                    lda #{{a}}
                    clc
                    adc #{{b}}
                }
                if overflow_set() { yes = 1 }
                asm {
                    lda #{{a}}
                    clc
                    adc #{{b}}
                }
                if !overflow_set() { no = 1 }
                asm {
                    lda #{{a}}
                    clc
                    adc #{{b}}
                }
                stored = overflow_set()
                asm {
                    lda #{{a}}
                    clc
                    adc #{{b}}
                }
                while overflow_set() {
                    loops += 1
                    asm { clv }
                }
            }
            """);
        int sum = (sbyte)(byte)a + (sbyte)(byte)b;
        bool overflow = sum < -128 || sum > 127;
        Assert.Equal(overflow ? 1 : 0, r.U8("yes"));
        Assert.Equal(overflow ? 0 : 1, r.U8("no"));
        Assert.Equal(overflow ? 1 : 0, r.U8("stored"));
        Assert.Equal(overflow ? 1 : 0, r.U8("loops"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void If_is_the_adc_then_bvc()
    {
        var r = Run.Program("""
            var yes: u8
            inline asm fn overflow_set() -> bool using overflow {}
            fn main() {
                asm {
                    lda #100
                    clc
                    adc #28
                }
                if overflow_set() { yes = 1 }
            }
            """);
        var main = Listing.Instructions(r);
        int adc = main.FindIndex(l => l.StartsWith("adc "));
        Assert.StartsWith("bvc ", main[adc + 1]);
        Assert.Equal(1, r.U8("yes"));
    }

    [Theory]
    [InlineData("fn f() -> bool using overflow { return true }\nfn main() { }", "Only an asm fn, an inline asm fn or a routine at a fixed address")]
    [InlineData("inline fn f() -> bool using overflow { return true }\nfn main() { }", "Only an asm fn, an inline asm fn or a routine at a fixed address")]
    [InlineData("fn f(b: bool using overflow) { }\nfn main() { }", "cannot be passed using Overflow")]
    [InlineData("fn f(true using overflow) @ $C000\nfn main() { }", "A fixed input goes in A, X, Y or the carry, not Overflow")]
    [InlineData("fn f() -> u8 using overflow @ $C000\nfn main() { }", "cannot be passed using Overflow")]
    public void Reports_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
    }
}
