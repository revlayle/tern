namespace Tern.Compiler.Tests;

/// <summary>
/// V1 fixes 2, gap 31: a 16-bit local used for its bytes is not stored and read back. Each byte is stored
/// where it is used while it is in A, and the high byte tested there (spec 11, <c>var t: u16 = a + b</c>,
/// then <c>x = t as u8</c> and a test of <c>(t &gt;&gt; 8) as u8</c>). The optimizer moves a store of a
/// copied byte up to where the byte was computed (<c>StoreWhereComputed</c>); other passes then remove the
/// reload and the local.
/// </summary>
public class StoreWhereComputedTests
{
    public static TheoryData<int, int> Values()
    {
        var data = new TheoryData<int, int>();
        foreach (int a in new[] { 0, 1, 255, 256, 65280, 65535 })
            foreach (int b in new[] { 0, 1, 255, 256, 65535 })
                data.Add(a, b);
        return data;
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void Gives_each_byte_as_written(int a, int b)
    {
        var r = Run.Program($$"""
            var a: u16 = {{a}}
            var b: u16 = {{b}}
            var g: u16 = {{a}}
            var xs: [u16; 4] = [0, {{a}}, 0, 0]
            var spr: [u8; 4]
            var x: u8
            var y: u8
            var hits: u8
            var i: u8 = 1
            var s: u8 = 2
            fn main() {
                var t: u16 = a + b
                x = t as u8
                if (t >> 8) as u8 != 0 { hits |= 1 }
                var d: u16 = a - b
                y = d.lo
                if d.hi == 0 { hits |= 2 }
                var e: u16 = xs[i] + 20
                spr[s] = e as u8
                if (e >> 8) as u8 != 0 { hits |= 4 }
                var h: u16 = g + b
                g.lo = h.lo
                if h.hi != 0 { hits |= 8 }
            }
            """);
        int t = (a + b) & 0xFFFF, d = (a - b) & 0xFFFF, e = (a + 20) & 0xFFFF;
        Assert.Equal(t & 0xFF, r.U8("x"));
        Assert.Equal(d & 0xFF, r.U8("y"));
        Assert.Equal(e & 0xFF, r.U8("spr", 2));
        Assert.Equal((a & 0xFF00) | (t & 0xFF), r.U16("g"));
        int hits = (t >> 8 != 0 ? 1 : 0) | (d >> 8 == 0 ? 2 : 0) | (e >> 8 != 0 ? 4 : 0) | (t >> 8 != 0 ? 8 : 0);
        Assert.Equal(hits, r.U8("hits"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void The_code_is_the_spec_shape()
    {
        var r = Run.Program("""
            var a: u16
            var b: u16
            var x: u8
            var hits: u8
            var xs: [u16; 4]
            var spr: [u8; 4]
            var i: u8
            fn spec() {
                var t: u16 = a + b
                x = t as u8
                if (t >> 8) as u8 != 0 { hits += 1 }
            }
            fn element(s: u8 using Y) {
                var e: u16 = xs[i] + 20
                spr[s] = e as u8
                if (e >> 8) as u8 != 0 { hits += 1 }
            }
            fn main() {
                spec()
                element(1)
            }
            """);
        Assert.Matches(@"^clc\|lda a\|adc b\|sta x\|lda a\+1\|adc b\+1\|beq spec\.endif\d+\|inc hits\|rts$", Listing.Code(r, "spec"));
        Assert.Matches(@"^clc\|ldx i\|lda xs,x\|adc #\$14\|sta spr,y\|lda xs\+4,x\|adc #\$00\|beq element\.endif\d+\|inc hits\|rts$", Listing.Code(r, "element"));
        Assert.DoesNotContain("spec.t", r.Result.Listing);
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_store_stays_where_its_memory_is_read_in_between_or_it_is_volatile()
    {
        var r = Run.Program("""
            var g: u16
            var b: u16
            volatile var port: u8
            var hits: u8
            fn same_memory() {
                var h: u16 = g + b
                g.lo = h.lo
                if h.hi != 0 { hits += 1 }
            }
            fn hardware() {
                var t: u16 = g + b
                port = t.lo
                if t.hi != 0 { hits += 1 }
            }
            fn main() {
                same_memory()
                hardware()
            }
            """);
        // g+1 is read for the high byte: g is stored after it.
        var same = Listing.Instructions(r, "same_memory");
        Assert.True(same.IndexOf("sta g") > same.IndexOf("lda g+1"), string.Join("|", same));
        var hardware = Listing.Instructions(r, "hardware");
        Assert.True(hardware.IndexOf("sta port") > hardware.IndexOf("adc b+1"), string.Join("|", hardware));
    }
}
