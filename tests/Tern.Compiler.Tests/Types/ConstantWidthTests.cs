namespace Tern.Compiler.Tests;

/// <summary>
/// Spec 3.1 as settled on 2026-11-09 (questions 79-81) and the M20 follow-up review: a typed constant
/// next to a runtime value helps choose the operation's type and is computed at that width; untyped
/// constants are plain numbers; named '~' constants keep their bit pattern; .lo / .hi of typed 16-bit
/// constants.
/// </summary>
public class ConstantWidthTests
{
    [Theory]
    [InlineData(0x1234)]
    [InlineData(0xFFFF)]
    [InlineData(0x000F)]
    public void A_typed_constant_next_to_a_wider_value_is_computed_at_the_operations_width(int start)
    {
        // Question 79: w & ~M with M: u8 is w & $FFF0, as w & ~$0F is, in every spelling.
        var r = Run.Program($$"""
            const M: u8 = $0F
            const BOX: u8 = 13
            var w: u16 = {{start}}
            var a: u16
            var b: u16
            var c: u16
            var d: u16
            fn main() {
                a = w & ~M
                b = w
                b &= ~M
                c = ~M
                d = w + BOX * 30
            }
            """);
        Assert.Equal(start & 0xFFF0, r.U16("a"));
        Assert.Equal(start & 0xFFF0, r.U16("b"));
        Assert.Equal(0xFFF0, r.U16("c"));
        Assert.Equal((start + 390) & 0xFFFF, r.U16("d"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(200, 0x1234)]
    [InlineData(255, 0xFFFF)]
    public void A_compound_assignment_with_a_typed_constant_is_the_operation_it_stands_for(int x, int w)
    {
        string[] ops = ["+", "-", "&", "|", "^"];
        var lines = new List<string>();
        var vars = new List<string>();
        for (int k = 0; k < ops.Length; k++)
        {
            string op = ops[k];
            lines.Add($"x{k}a = x {op} B; x{k}b = x; x{k}b {op}= B");
            lines.Add($"w{k}a = w {op} W; w{k}b = w; w{k}b {op}= W");
            lines.Add($"w{k}c = w {op} ~B; w{k}d = w; w{k}d {op}= ~B");
            vars.Add($"var x{k}a: u8\nvar x{k}b: u8\nvar w{k}a: u16\nvar w{k}b: u16\nvar w{k}c: u16\nvar w{k}d: u16");
        }
        var r = Run.Program($"const B: u8 = $5A\nconst W: u16 = $1357\nvar x: u8 = {x}\nvar w: u16 = {w}\n"
            + string.Join("\n", vars) + "\nfn main() {\n" + string.Join("\n", lines) + "\n}\n");
        for (int k = 0; k < ops.Length; k++)
        {
            Assert.Equal(r.U8($"x{k}a"), r.U8($"x{k}b"));
            Assert.Equal(r.U16($"w{k}a"), r.U16($"w{k}b"));
            Assert.Equal(r.U16($"w{k}c"), r.U16($"w{k}d"));
        }
        Assert.Equal((x + 0x5A) & 0xFF, r.U8("x0a"));
        Assert.Equal((w - 0x1357) & 0xFFFF, r.U16("w1a"));
        Assert.Equal(w & 0xFFA5, r.U16("w2c"));
        Assert.Equal(w ^ 0xFFA5, r.U16("w4c"));
    }

    [Fact]
    public void An_untyped_constant_computed_from_typed_ones_is_a_plain_number()
    {
        // Question 81.
        var r = Run.Program("""
            const BOX: u8 = 13
            const AREA = BOX * 30
            const D = BOX * 2
            var w: u16 = 1000
            var s: i8 = -100
            var v = AREA
            var sum: u16
            var t: i8
            fn main() {
                sum = w + AREA
                t = s + D
            }
            """);
        Assert.Equal(1390, r.U16("sum"));
        Assert.Equal(390, r.U16("v"));
        Assert.Equal("u16", r.Result.Variables["v"].Type);
        Assert.Equal(-74, (sbyte)r.U8("t"));
    }

    [Fact]
    public void Lo_and_hi_of_a_typed_16_bit_constant_below_256()
    {
        // Review finding 1.
        var r = Run.Program("""
            const LINE: u16 = 250
            const ZP_VEC: u16 = $00FB
            const BOX: u8 = 13
            var hi: u8 = 9
            var lo: u8
            var vec: u8
            var big: u8
            var bit8: u8
            fn main() {
                hi = LINE.hi
                lo = LINE.lo
                vec = ZP_VEC.lo
                big = (BOX * 100).hi
                bit8 = (LINE.hi << 7) | 1
            }
            """);
        Assert.Equal(0, r.U8("hi"));
        Assert.Equal(250, r.U8("lo"));
        Assert.Equal(0xFB, r.U8("vec"));
        Assert.Equal(1300 >> 8, r.U8("big"));
        Assert.Equal(1, r.U8("bit8"));
    }

    [Theory]
    [InlineData(0xFF)]
    [InlineData(0x5A)]
    public void A_named_not_constant_keeps_its_bit_pattern(int value)
    {
        // Review finding 2: const N = ~$0F takes the width it is used as, like ~$0F written out.
        var r = Run.Program($$"""
            const N = ~$0F
            const S = ~$0F << 4
            var x: u8 = {{value}}
            var w: u16 = {{value * 257}}
            var xs: u8 = {{value}}
            var ws: u16 = {{value * 257}}
            fn main() {
                x = x & N
                w = w & N
                xs = xs & S
                ws &= S
            }
            """);
        Assert.Equal(value & 0xF0, r.U8("x"));
        Assert.Equal(value * 257 & 0xFFF0, r.U16("w"));
        Assert.Equal(0, r.U8("xs"));
        Assert.Equal(value * 257 & 0xFF00, r.U16("ws"));
    }
}
