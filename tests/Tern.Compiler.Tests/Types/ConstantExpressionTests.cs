using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// M20 follow-up, spec 3.1 and 3.4: an expression of literals and constants, typed or not, is computed
/// exactly and takes its type from where it is used, like a literal; only the final value is checked.
/// A typed constant mixed with a runtime value keeps its type.
/// </summary>
public class ConstantExpressionTests
{
    private static string Error(string source) => Assert.Throws<CompileError>(() => TernCompiler.Compile(source)).Description;

    [Fact]
    public void Constants_are_computed_exactly_and_checked_where_they_are_used()
    {
        var r = Run.Program("""
            const BOX: u8 = 13
            const FRAMES: u16 = 400
            const MAX_X: u16 = 320 - BOX          // 307, not an error about 320 in a u8
            const SECS: u8 = FRAMES / 50          // 8, with no 'as'
            const LOW: u8 = (BOX + 250) as u8     // 263 keeps its low byte on purpose
            const TWICE = BOX * 2
            const BIG: u16 = BOX * 100            // 1300, not 1300 & $FF
            const NEG: i8 = BOX - 141             // -128
            var a: u16
            var b: u8
            var c: u8
            var d: u8
            var e: u16
            var f: i8
            var g: i16
            fn main() {
                a = MAX_X
                b = SECS
                c = LOW
                d = TWICE
                e = BIG
                f = NEG
                g = BOX - 1000                     // a typed constant takes the type it is used as
            }
            """);
        Assert.Equal(307, r.U16("a"));
        Assert.Equal(8, r.U8("b"));
        Assert.Equal(7, r.U8("c"));
        Assert.Equal(26, r.U8("d"));
        Assert.Equal(1300, r.U16("e"));
        Assert.Equal(-128, (sbyte)r.U8("f"));
        Assert.Equal(-987, r.I16("g"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(229)]
    [InlineData(230)]
    [InlineData(255)]
    public void A_typed_constant_with_a_runtime_value_keeps_its_type(int x)
    {
        var r = Run.Program($$"""
            const BOX: u8 = 13
            var x: u8 = {{x}}
            var w: u16 = {{x * 200}}
            var sum: u8
            var more: u8
            var wide: u16
            fn main() {
                sum = x + BOX               // u8: wraps
                more = x + BOX * 2          // only the constant part is exact: adds 26
                wide = w + BOX              // the u8 constant widens to u16
            }
            """);
        Assert.Equal((x + 13) & 0xFF, r.U8("sum"));
        Assert.Equal((x + 26) & 0xFF, r.U8("more"));
        Assert.Equal((x * 200 + 13) & 0xFFFF, r.U16("wide"));
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void A_constant_expression_converts_to_a_pointer()
    {
        var r = Run.Program("""
            import mem
            const CHARSET = $C000
            const FIRST: u8 = 64
            data glyph: [u8; 4] = [1, 2, 3, 4]
            var p: ptr u8 = CHARSET + FIRST * 8
            var q: ptr u8
            fn set(target: ptr u8) { q = target }
            fn main() {
                set(CHARSET + FIRST * 8 + 1)
                mem.copy(CHARSET + FIRST * 8, addr(glyph), len(glyph))
            }
            """);
        Assert.Equal(0xC200, r.U16("p"));
        Assert.Equal(0xC201, r.U16("q"));
        Assert.Equal([1, 2, 3, 4], r.Ram.Bytes.AsSpan(0xC200, 4).ToArray());
    }

    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData(0xFF)]
    [InlineData(0x3C)]
    [InlineData(0x00)]
    public void Not_of_a_constant_takes_the_width_of_the_value_it_is_used_with(int value)
    {
        var r = Run.Program($$"""
            const MASK = $0F
            var x: u8 = {{value}}
            var w: u16 = {{value * 257}}
            var s: i8 = {{(sbyte)value}}
            var c: u8
            fn main() {
                x = x & ~$0F
                w = w & ~MASK
                s = s & ~$0F
                c = ~MASK & $FF
            }
            """);
        Assert.Equal(value & 0xF0, r.U8("x"));
        Assert.Equal(value * 257 & 0xFFF0, r.U16("w"));
        Assert.Equal((sbyte)value & ~0x0F, (sbyte)r.U8("s"));
        Assert.Equal(0xF0, r.U8("c"));
        Assert.Contains("and #$F0", Listing.Instructions(r));
    }

    [Fact]
    public void A_typed_constant_bound_gives_the_loop_counter_its_type()
    {
        // With an i8 counter, 's = i' needs no conversion.
        var r = Run.Program("""
            const N: i8 = 5
            var s: i8
            var n: u8
            fn main() {
                for i in 0..N {
                    s = i
                    n += 1
                }
            }
            """);
        Assert.Equal(4, r.U8("s"));
        Assert.Equal(5, r.U8("n"));
    }

    /// <summary>Review code-quality-semantics 6: with nothing to give a type, a typed constant narrows only when the value fits.</summary>
    [Fact]
    public void A_typed_constant_where_nothing_gives_a_type_does_not_narrow_a_value_too_large()
    {
        var r = Run.Program("""
            const BOX: u8 = 13
            var out: u16
            var n: u16
            fn main() {
                var v = BOX * 30
                out = v
                for i in 0..BOX * 30 { n += 1 }
            }
            """);
        Assert.Equal(390, r.U16("out"));
        Assert.Equal(390, r.U16("n"));
    }

    /// <summary>Review code-quality-semantics 7: 'as' keeps the low bits of a constant of any size (spec 3.5).</summary>
    [Fact]
    public void As_keeps_the_low_bits_of_a_constant_wider_than_16_bits()
    {
        var r = Run.Program("""
            const BIG = 70000
            const NEG = -70000
            var w: u16
            var b: u8
            var s: i16
            fn main() {
                w = BIG as u16
                b = BIG as u8
                s = NEG as i16
            }
            """);
        Assert.Equal(70000 & 0xFFFF, r.U16("w"));
        Assert.Equal(70000 & 0xFF, r.U8("b"));
        Assert.Equal(unchecked((short)-70000), (short)r.U16("s"));
    }

    /// <summary>Review code-quality-semantics 8: '.lo' and '.hi' of an untyped constant do not depend on its size.</summary>
    [Theory]
    [InlineData("$C0", 0xC0, 0x00)]
    [InlineData("$C000", 0x00, 0xC0)]
    [InlineData("0", 0x00, 0x00)]
    [InlineData("-1", 0xFF, 0xFF)]
    [InlineData("-300", 0xD4, 0xFE)]
    public void Lo_and_hi_of_an_untyped_constant_take_both_bytes(string value, int lo, int hi)
    {
        var r = Run.Program($$"""
            const BASE = {{value}}
            var l: u8
            var h: u8
            fn main() {
                l = BASE.lo
                h = BASE.hi
            }
            """);
        Assert.Equal(lo, r.U8("l"));
        Assert.Equal(hi, r.U8("h"));
    }

    [Theory]
    [InlineData("const BOX: u8 = 13\nconst B: u8 = BOX + 250\nfn main() { }", "263 does not fit in u8 (0 to 255)")]
    [InlineData("const BOX: u8 = 13\nvar v: u8 = BOX * 20\nfn main() { }", "260 does not fit in u8 (0 to 255)")]
    [InlineData("var x: u8\nfn main() { x = -1 }", "-1 does not fit in u8 (0 to 255)")]
    [InlineData("const A: u16 = 40000\nconst B: u16 = A * 2\nfn main() { }", "80000 does not fit in u16 (0 to 65535)")]
    [InlineData("var x: u8\nfn main() { x = x & ~$1FF }", "'~' gives -512 here, a pattern wider than u8: the value before '~' has bits above u8. Use a value that fits in u8 before '~'")]
    [InlineData("const M = ~$1FF\nvar x: u8\nfn main() { x = x & M }", "'~' gives -512 here, a pattern wider than u8")]
    [InlineData("var x: u8\nvar w: u16\nfn main() { w = x + 300 }",
        "300 does not fit in u8 (0 to 255), the type of 'x', which it is combined with. Convert 'x' with 'as u16'")]
    [InlineData("var hp: [u8; 4]\nvar w: u16\nfn main() { w = hp[1] + 1000 }", "the type of 'hp[1]', which it is combined with. Convert 'hp[1]' with 'as u16'")]
    [InlineData("var s: i8\nfn main() { s = s - 200 }", "200 does not fit in i8 (-128 to 127), the type of 's', which it is combined with. Convert 's' with 'as i16'")]
    [InlineData("const BOX: u8 = 13\nvar x: u8\nfn main() { x = x + BOX * 30 }",
        "390 does not fit in u8 (0 to 255), the type of 'x', which it is combined with. Convert 'x' with 'as u16'")]
    [InlineData("const BOX: u8 = 13\nconst AREA = BOX * 30\nvar x: u8\nfn main() { x = x + AREA }",
        "390 does not fit in u8 (0 to 255), the type of 'x', which it is combined with. Convert 'x' with 'as u16'")]
    [InlineData("const BIG: u16 = 100\nvar x: u8\nvar w: u16\nfn main() { w = x + BIG * 1000 }",
        "100000 does not fit in u16 (0 to 65535), the type of the constant 'BIG' in it, which sets the operation's type. Use a smaller value")]
    [InlineData("const BIG: u16 = 100\nvar x: u8\nfn main() { x += BIG }", "Expected u8, found u16: 'BIG' is u16. Convert with 'as u8'")]
    [InlineData("const BOX: u8 = 13\nvar s: i8\nfn main() { s = s + BOX }", "Cannot mix signed and unsigned (i8 and u8)")]
    [InlineData("const BOX: u8 = 13\nvar s: i8\nfn main() { s += BOX }", "Cannot mix signed and unsigned (i8 and u8)")]
    // Review 2, suggestion 2: a compound assignment names its target too, with a fix.
    [InlineData("const BOX: u8 = 13\nvar x: u8\nfn main() { x += BOX * 30 }",
        "390 does not fit in u8 (0 to 255), the type of 'x', which it is combined with. Declare 'x' as u16, or use a smaller value")]
    [InlineData("var x: u8\nfn main() { x += 300 }",
        "300 does not fit in u8 (0 to 255), the type of 'x', which it is combined with. Declare 'x' as u16, or use a smaller value")]
    [InlineData("var s: i8\nfn main() { s -= 200 }",
        "200 does not fit in i8 (-128 to 127), the type of 's', which it is combined with. Declare 's' as i16, or use a smaller value")]
    [InlineData("var hp: [u8; 4]\nfn main() { hp[1] += 1000 }",
        "1000 does not fit in u8 (0 to 255), the type of 'hp[1]', which it is combined with. Use a smaller value")]
    // Review 3, suggestion 2: a negative value on an unsigned target is the other operator.
    [InlineData("var x: u8\nfn main() { x += -1 }",
        "-1 does not fit in u8 (0 to 255), the type of 'x', which it is combined with. Write 'x -= 1'")]
    [InlineData("var hp: [u8; 4]\nfn main() { hp[2] -= -5 }",
        "-5 does not fit in u8 (0 to 255), the type of 'hp[2]', which it is combined with. Write 'hp[2] += 5'")]
    [InlineData("var w: u16\nfn main() { w += -300 }",
        "-300 does not fit in u16 (0 to 65535), the type of 'w', which it is combined with. Write 'w -= 300'")]
    [InlineData("var x: u8\nfn main() { x += -300 }",
        "-300 does not fit in u8 (0 to 255), the type of 'x', which it is combined with. Use a smaller value")]
    [InlineData("var w: u16\nfn main() { w += 70000 }",
        "70000 does not fit in u16 (0 to 65535), the type of 'w', which it is combined with. Use a smaller value")]
    [InlineData("const C: u8 = 5\nvar x: u8\nfn main() { x = C.hi }", ".hi needs a 16-bit value, not u8")]
    public void Reports_errors(string source, string expected) => Assert.Contains(expected, Error(source));
}
