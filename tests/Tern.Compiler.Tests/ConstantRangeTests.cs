using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// M20 follow-up 2, spec 3.1 and 4.1: constant arithmetic is done in signed 64 bits, and a value outside
/// that range at any step is an error. <c>/</c> rounds toward zero, <c>%</c> has the left operand's sign,
/// <c>&gt;&gt;</c> rounds down. A shift count in a constant expression is 0 to 63.
/// </summary>
public class ConstantRangeTests
{
    private static string Error(string source) => Assert.Throws<CompileError>(() => TernCompiler.Compile(source)).Description;

    private const string Edges = """
        const MAX = 9223372036854775807
        const MIN = -9223372036854775807 - 1
        """;

    [Fact]
    public void Values_up_to_the_edges_of_64_bits_are_exact()
    {
        var r = Run.Program(Edges + """

            var a: u16
            var b: i8
            var c: i8
            var d: u8
            var e: u8
            var f: u8
            var g: u16
            var h: i8
            fn main() {
                a = MAX >> 48                         // 32767
                b = MIN >> 56                         // -128
                c = MIN / (1 << 56)                   // -128
                d = MIN % -1                          // 0
                e = (MAX - 1 + 1) / (MAX / 3)         // 3
                f = 4000000000 * 2000000000 / 1000000000 / 1000000000 // 8, with 8e18 on the way
                g = (-1 << 63) / -(1 << 50)           // 8192
                h = -1 >> 63                          // -1
            }
            """);
        Assert.Equal(0x7FFF, r.U16("a"));
        Assert.Equal(-128, (sbyte)r.U8("b"));
        Assert.Equal(-128, (sbyte)r.U8("c"));
        Assert.Equal(0, r.U8("d"));
        Assert.Equal(3, r.U8("e"));
        Assert.Equal(8, r.U8("f"));
        Assert.Equal(8192, r.U16("g"));
        Assert.Equal(-1, (sbyte)r.U8("h"));
    }

    [Theory]
    [InlineData("-7 / 2", -3)]
    [InlineData("7 / -2", -3)]
    [InlineData("-7 / -2", 3)]
    [InlineData("-7 % 2", -1)]
    [InlineData("7 % -2", 1)]
    [InlineData("-7 % -2", -1)]
    [InlineData("-7 >> 1", -4)]
    [InlineData("-1 >> 1", -1)]
    [InlineData("-3 >> 1", -2)]
    [InlineData("-128 >> 7", -1)]
    [InlineData("-129 >> 7", -2)]
    [InlineData("(1 << 40) >> 38", 4)]
    [InlineData("-(1 << 40) >> 39", -2)]
    public void Division_remainder_and_shift_round_as_the_spec_says(string expression, int expected)
    {
        var r = Run.Program($$"""
            const V = {{expression}}
            var v: i8
            fn main() { v = V }
            """);
        Assert.Equal(expected, (sbyte)r.U8("v"));
    }

    [Fact]
    public void The_roadmap_example_is_an_error_not_eight()
    {
        // 4000000000 * 4000000000 is 1.6e19, past 2^63: it used to wrap and give 8 at the end.
        string e = Error("const V = 4000000000 * 4000000000 * 4 / 1000000000 / 1000000000\nvar v: u8 = V\nfn main() { }");
        Assert.Contains("goes outside the signed 64-bit range of constant arithmetic", e);
        Assert.EndsWith("Use smaller values", e);
    }

    [Theory]
    [InlineData("MAX + 1")]
    [InlineData("MIN - 1")]
    [InlineData("1 - MIN")]
    [InlineData("-MIN")]
    [InlineData("MIN / -1")]
    [InlineData("MAX * 2")]
    [InlineData("MIN * -1")]
    [InlineData("1 << 63")]
    [InlineData("3 << 62")]
    [InlineData("-3 << 62")]
    [InlineData("(MAX + 1) - 1")] // out of range at a step, back in range at the end
    public void A_value_outside_64_bits_at_any_step_is_an_error(string expression)
    {
        string e = Error(Edges + $"\nconst V = {expression}\nvar v: u8 = (V & 1) as u8\nfn main() {{ }}");
        Assert.Contains("goes outside the signed 64-bit range", e);
    }

    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("$7FFFFFFFFFFFFFFF")]
    [InlineData("$7FFF_FFFF_FFFF_FFFF")]
    [InlineData("0b0111111111111111111111111111111111111111111111111111111111111111")]
    [InlineData("(1 << 62) - 1 + (1 << 62)")]
    public void The_largest_number_literal_is_2_to_the_63_minus_1(string literal)
    {
        // Spec 2.1. Shifted down so the result fits a u16: 2^63 - 1 >> 48 is $7FFF.
        var r = Run.Program($"const V = {literal}\nvar a: u16\nvar b: u8\nfn main() {{ a = V >> 48; b = (V & $FF) as u8 }}");
        Assert.Equal(0x7FFF, r.U16("a"));
        Assert.Equal(0xFF, r.U8("b"));
    }

    [Fact]
    public void The_lowest_value_is_written_with_a_subtraction()
    {
        var r = Run.Program("const V = -9223372036854775807 - 1\nvar a: i8\nvar b: u8\nfn main() { a = V >> 56; b = (V & $FF) as u8 }");
        Assert.Equal(-128, (sbyte)r.U8("a"));
        Assert.Equal(0, r.U8("b"));
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("$8000000000000000")]
    [InlineData("0b1000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("99999999999999999999")]
    [InlineData("$FFFFFFFFFFFFFFFFFF")]
    [InlineData("-9223372036854775808")] // the literal has no sign (spec 2.1)
    public void A_number_literal_above_2_to_the_63_minus_1_is_an_error(string literal)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile($"const V = {literal}\nfn main() {{ }}"));
        Assert.Equal("Number is too large. Use a value up to $7FFFFFFFFFFFFFFF (9223372036854775807)", e.Description);
        Assert.Equal(new SourcePos(1, literal.StartsWith('-') ? 12 : 11), e.Pos);
    }

    [Fact]
    public void The_error_points_at_the_step_that_leaves_the_range()
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("const V = 1 + 4000000000 * 4000000000\nfn main() { }"));
        Assert.Equal(new SourcePos(1, 26), e.Pos);
    }

    [Theory]
    [InlineData("const V = 1 << 64\nfn main() { }", "Shift count must be 0 to 63")]
    [InlineData("const V = 1 >> 64\nfn main() { }", "Shift count must be 0 to 63")]
    [InlineData("const V = 1 << -1\nfn main() { }", "Shift count must be 0 to 63")]
    [InlineData("var x: u8\nfn main() { x = x << 8 }", "Shift count must be 0 to 7")]
    [InlineData("fn main() { asm { lda #1 << 64 } }", "Shift count must be 0 to 63")]
    [InlineData("fn main() { asm { lda #1 >> -1 } }", "Shift count must be 0 to 63")]
    [InlineData("var w: u16\nfn main() { w = w >> 16 }", "Shift count must be 0 to 15")]
    public void Shift_counts(string source, string expected) => Assert.Contains(expected, Error(source));

    [Fact]
    public void A_shift_by_32_to_63_is_allowed_in_a_constant()
    {
        var r = Run.Program("""
            const V = (1 << 33) >> 32
            const W = (5 << 60) >> 59
            var v: u8
            var w: u8
            fn main() { v = V; w = W }
            """);
        Assert.Equal(2, r.U8("v"));
        Assert.Equal(10, r.U8("w"));
    }

    [Fact]
    public void Assembly_operands_use_the_same_arithmetic()
    {
        var r = Run.Program("""
            var v: u8
            var w: u8
            fn main() {
                asm {
                    lda #((1 << 40) >> 38)
                    sta v
                    lda #(-7 / 2) & $FF
                    sta w
                }
            }
            """);
        Assert.Equal(4, r.U8("v"));
        Assert.Equal(0xFD, r.U8("w"));
        Assert.Contains("goes outside the signed 64-bit range",
            Error("fn main() { asm { lda #(4000000000 * 4000000000) & $FF } }"));
    }
}
