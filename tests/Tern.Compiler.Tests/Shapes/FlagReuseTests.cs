namespace Tern.Compiler.Tests;

/// <summary>
/// V1 fixes, gap 28: flags the last instruction set are tested with no reload or compare (spec 11):
/// <c>x -= 1</c> then <c>if x == 0</c> is <c>dec x / bne skip</c>, and a signed <c>d &gt; 0</c> is
/// <c>lda d / beq / bmi</c> (<c>lda d+1 / bmi / ora d / beq</c> for an i16).
/// </summary>
public class FlagReuseTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(128)]
    [InlineData(255)]
    public void Inc_and_dec_then_a_zero_test_are_right(int v)
    {
        var r = Run.Program($$"""
            var m: [u8; 4] = [0, {{v}}, 0, 0]
            var i: u8 = 1
            var x: u8 = {{v}}
            var u: u8 = {{v}}
            var k: u8 = {{v}}
            var hits: u8
            var kept: u8
            fn main() {
                m[i] -= 1
                if m[i] == 0 { hits |= 1 }
                x -= 1
                if x != 0 { hits |= 2 }
                u += 1
                if u == 0 { hits |= 4 }
                k -= 1
                if k != 0 { kept = k }      // A is read after the test: the load stays
            }
            """);
        int dec = (v - 1) & 0xFF, inc = (v + 1) & 0xFF;
        int expected = (dec == 0 ? 1 : 0) | (dec != 0 ? 2 : 0) | (inc == 0 ? 4 : 0);
        Assert.Equal(expected, r.U8("hits"));
        Assert.Equal(dec, r.Ram.Bytes[r.Address("m") + 1]);
        Assert.Equal(dec != 0 ? dec : 0, r.U8("kept"));
    }

    [Theory]
    [InlineData(-128)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    public void A_signed_byte_above_zero(int d)
    {
        var r = Run.Program($$"""
            var d: i8 = {{d}}
            var t: i8 = {{d}}
            var hits: u8
            var n: u8
            fn main() {
                if d > 0 { hits |= 1 }
                if d <= 0 { hits |= 2 }
                if 0 < d { hits |= 4 }
                if 0 >= d { hits |= 8 }
                while t > 0 {
                    t -= 1
                    n += 1
                }
            }
            """);
        int expected = d > 0 ? 1 | 4 : 2 | 8;
        Assert.Equal(expected, r.U8("hits"));
        Assert.Equal(d > 0 ? d : 0, r.U8("n"));
    }

    [Theory]
    [InlineData(-32768)]
    [InlineData(-256)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(32767)]
    public void A_signed_word_above_zero(int w)
    {
        var r = Run.Program($$"""
            var w: i16 = {{w}}
            var a: [i16; 2] = [0, {{w}}]
            var i: u8 = 1
            var hits: u8
            fn main() {
                if w > 0 { hits |= 1 }
                if w <= 0 { hits |= 2 }
                if a[i] + 0 > 0 { hits |= 4 }
                if w - 1 >= 0 { hits |= 8 }
            }
            """);
        int expected = (w > 0 ? 1 : 2) | (w > 0 ? 4 : 0) | ((short)(w - 1) >= 0 ? 8 : 0);
        Assert.Equal(expected, r.U8("hits"));
    }

    [Theory]
    [InlineData(3, 4)]
    [InlineData(4, 3)]
    [InlineData(0, 0)]
    [InlineData(255, 0)]
    public void A_bool_from_the_carry_is_tested_right(int y, int z)
    {
        var r = Run.Program($$"""
            var y: u8 = {{y}}
            var z: u8 = {{z}}
            var hits: u8
            var copy: u8
            fn above() -> bool { return y > z }
            fn main() {
                var b: bool = above()
                if b { hits |= 1 }
                var c: bool = above()
                if !c { hits |= 2 }
                var d: bool = above()
                if d { hits |= 4 }
                copy = d as u8              // d is read after the test: it stays 0 or 1
            }
            """);
        int above = y > z ? 1 : 0;
        Assert.Equal(above * 5 | (1 - above) * 2, r.U8("hits"));
        Assert.Equal(above, r.U8("copy"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_bool_just_returned_in_the_carry_is_tested_from_the_carry()
    {
        var r = Run.Program("""
            var y: u8
            var z: u8
            var hit: u8
            fn above() -> bool { return y > z }
            fn test() {
                var b: bool = above()
                if b { hit = 1 }
            }
            fn main() { test() }
            """);
        Assert.Matches(@"^jsr above\|bcc test\.endif\d+\|lda #\$01\|sta hit\|rts$", Listing.Code(r, "test"));
    }

    [Trait("Shape", "nmos6502")]
    [Theory]
    [Trait("Platform", "c64")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    public void A_fixed_address_outside_the_volatile_areas_reuses_the_flags_and_a_volatile_one_reloads(int v)
    {
        // Galaxy Stars' explode.restore_area: `cover` is a table at $C600 (gap 28). $C000 is RAM on the C64.
        var r = Run.Program($$"""
            var cover: [u8; 4] @ $C600
            volatile var flags: u8 @ $C000
            var hits: u8
            fn table(i: u8) {
                cover[i] -= 1
                if cover[i] == 0 { hits |= 1 }
            }
            fn hardware() {
                flags -= 1
                if flags == 0 { hits |= 2 }
            }
            fn main() {
                cover[1] = {{v}}
                flags = {{v}}
                table(1)
                hardware()
            }
            """);
        int dec = (v - 1) & 0xFF;
        Assert.Equal(dec == 0 ? 3 : 0, r.U8("hits"));
        Assert.Equal(dec, r.Ram.Bytes[0xC601]);
        Assert.Equal(dec, r.Ram.Bytes[0xC000]);
        Assert.Matches(@"^ldx table\.i\|dec cover,x\|bne table\.endif\d+\|", Listing.Code(r, "table"));
        Assert.Matches(@"^dec flags\|lda flags\|bne hardware\.endif\d+\|", Listing.Code(r, "hardware"));
    }

    [Fact]
    public void The_code_is_the_spec_shape()
    {
        var r = Run.Program("""
            var m: [u8; 8]
            var x: u8
            var d: i8
            var w: i16
            var hit: u8
            fn element(i: u8) {
                m[i] -= 1
                if m[i] == 0 { hit = 1 }
            }
            fn memory() {
                x -= 1
                if x == 0 { hit = 2 }
            }
            fn up() {
                x += 1
                if x != 0 { hit = 3 }
            }
            fn byte() { if d > 0 { hit = 4 } }
            fn word() { if w > 0 { hit = 5 } }
            fn main() {
                element(2)
                memory()
                up()
                byte()
                word()
            }
            """);
        Assert.Matches(@"^ldx element\.i\|dec m,x\|bne element\.endif\d+\|lda #\$01\|sta hit\|rts$", Listing.Code(r, "element"));
        Assert.Matches(@"^dec x\|bne memory\.endif\d+\|lda #\$02\|sta hit\|rts$", Listing.Code(r, "memory"));
        Assert.Matches(@"^inc x\|beq up\.endif\d+\|lda #\$03\|sta hit\|rts$", Listing.Code(r, "up"));
        Assert.Matches(@"^lda d\|beq byte\.endif(\d+)\|bmi byte\.endif\1\|lda #\$04\|sta hit\|rts$", Listing.Code(r, "byte"));
        Assert.Matches(@"^lda w\+1\|bmi word\.endif(\d+)\|ora w\|beq word\.endif\1\|lda #\$05\|sta hit\|rts$", Listing.Code(r, "word"));
    }
}
