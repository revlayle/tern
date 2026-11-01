namespace Tern.Compiler.Tests;

public class PointerTests
{
    [Fact]
    public void Reads_and_writes_through_a_pointer()
    {
        var r = Run.Program("""
            var buf: [u8; 4] = [10, 20, 30, 40]
            var i: u8 = 2
            var first: u8
            var third: u8
            fn main() {
                var p: ptr u8 = addr(buf)
                first = p[0]
                third = p[i]
                p[3] = 99
                p[i] += 5
            }
            """);
        Assert.Equal(10, r.U8("first"));
        Assert.Equal(30, r.U8("third"));
        Assert.Equal(99, r.U8("buf", 3));
        Assert.Equal(35, r.U8("buf", 2));
        Assert.True(r.Address("main.p") < 0x100, "a dereferenced pointer must be in zero page");
    }

    [Fact]
    public void Store_through_pointer_has_the_spec_code_shape()
    {
        var r = Run.Program("""
            var p: ptr u8
            var i: u8
            var v: u8
            fn main() { p[i] = v }
            """);
        var main = r.Result.Listing[r.Result.Listing.IndexOf("main:")..];
        Assert.Matches(@"lda v\s.*\n.*ldy i\s.*\n.*sta \(p\),y", main);
    }

    [Theory]
    [InlineData(0x10FF, 0)]
    [InlineData(0x10FF, 1)]     // crosses into the next page
    [InlineData(0x20F0, 0x20)]
    [InlineData(0x2000, 255)]
    [InlineData(0x2001, 255)]
    public void Index_crosses_pages(int pointer, int index)
    {
        var r = Run.Program($$"""
            var p: ptr u8 = {{pointer}}
            var i: u8 = {{index}}
            var got: u8
            fn main() {
                p[i] = 77
                got = p[i]
            }
            """);
        Assert.Equal(77, r.Ram.Bytes[pointer + index]);
        Assert.Equal(77, r.U8("got"));
    }

    [Theory]
    [InlineData(0x10FF, 1)]
    [InlineData(0x10F0, 200)]
    [InlineData(0x1000, 0)]
    [InlineData(0xFFFF, 1)]     // wraps around
    [InlineData(0x1234, 256)]
    [InlineData(0x1234, 0xFFFF)]
    public void Pointer_arithmetic_in_bytes(int start, int n)
    {
        var r = Run.Program($$"""
            var p: ptr u8 = {{start}}
            var n: u16 = {{n}}
            var small: u8 = {{n & 0xFF}}
            var plus: ptr u8
            var plus8: ptr u8
            var before: ptr u8
            var minus: ptr u8
            var inc: ptr u8
            var dec: ptr u8
            var stepped: ptr u8
            fn main() {
                plus = p + n
                plus8 = small + p
                minus = p - n
                inc = p
                inc += 1
                dec = p
                dec -= 1
                stepped = p
                stepped += n
            }
            """);
        Assert.Equal((start + n) & 0xFFFF, r.U16("plus"));
        Assert.Equal((start + (n & 0xFF)) & 0xFFFF, r.U16("plus8"));
        Assert.Equal((start - n) & 0xFFFF, r.U16("minus"));
        Assert.Equal((start + 1) & 0xFFFF, r.U16("inc"));
        Assert.Equal((start - 1) & 0xFFFF, r.U16("dec"));
        Assert.Equal((start + n) & 0xFFFF, r.U16("stepped"));
    }

    [Fact]
    public void Walks_a_buffer_across_a_page_boundary()
    {
        // 300 bytes starting at $30F0: the pointer steps over $3100 and $3200.
        var r = Run.Program("""
            var p: ptr u8 = $30F0
            var end: ptr u8 = $30F0 + 300
            fn main() {
                var v: u8 = 0
                while p != end {
                    p[0] = v
                    v += 1
                    p += 1
                }
            }
            """);
        for (int i = 0; i < 300; i++)
            Assert.Equal((byte)i, r.Ram.Bytes[0x30F0 + i]);
        Assert.Equal(0x30F0 + 300, r.U16("p"));
    }

    [Fact]
    public void Sixteen_bit_elements()
    {
        var r = Run.Program("""
            var words: [u16; 2]
            var raw: [u8; 8] = [$34, $12, $78, $56, 0, 0, $CD, $AB]
            var i: u8 = 1
            var a: u16
            var b: u16
            var last: u16
            var lo: u8
            var hi: u8
            fn main() {
                var p: ptr u16 = addr(raw) as ptr u16
                a = p[0]
                b = p[i]
                last = p[3]
                p[2] = $BEEF
                p[i] = p[i] + 1
                lo = p[i].lo
                hi = p[3].hi
                p[0].hi = $99
            }
            """);
        Assert.Equal(0x1234, r.U16("a"));
        Assert.Equal(0x5678, r.U16("b"));
        Assert.Equal(0xABCD, r.U16("last"));
        Assert.Equal(0xEF, r.U8("raw", 4));
        Assert.Equal(0xBE, r.U8("raw", 5));
        Assert.Equal(0x79, r.U8("raw", 2));
        Assert.Equal(0x79, r.U8("lo"));
        Assert.Equal(0xAB, r.U8("hi"));
        Assert.Equal(0x99, r.U8("raw", 1));
    }

    [Fact]
    public void Sixteen_bit_element_at_largest_index()
    {
        var r = Run.Program("""
            var p: ptr u16 = $3000
            var i: u8 = 127
            var got: u16
            fn main() {
                p[i] = $1234
                got = p[127]
            }
            """);
        Assert.Equal(0x34, r.Ram.Bytes[0x3000 + 254]);
        Assert.Equal(0x12, r.Ram.Bytes[0x3000 + 255]);
        Assert.Equal(0x1234, r.U16("got"));
    }

    [Fact]
    public void Pointers_to_pointers()
    {
        // A linked list: each node is a pointer to the next one, the last points to 0.
        var r = Run.Program("""
            var count: u8
            var mem: [u8; 6]
            fn main() {
                var node: ptr ptr u8 = addr(mem) as ptr ptr u8
                node[0] = (addr(mem) + 2) as ptr u8
                node[1] = (addr(mem) + 4) as ptr u8
                node[2] = 0
                var n: ptr ptr u8 = node
                while n != 0 {
                    count += 1
                    n = n[0] as ptr ptr u8
                }
            }
            """);
        Assert.Equal(3, r.U8("count"));
    }

    [Fact]
    public void Indexing_a_computed_pointer_uses_a_zero_page_temporary()
    {
        var r = Run.Program("""
            var rows: [ptr u8; 2] = [$3000, $3100]
            var j: u8 = 1
            var i: u8 = 5
            var got: u8
            fn main() {
                rows[j][i] = 42
                got = (rows[j] + 1)[i]
                (rows[0] + 300)[0] = 7
            }
            """);
        Assert.Equal(42, r.Ram.Bytes[0x3105]);
        Assert.Equal(0, r.U8("got"));
        Assert.Equal(7, r.Ram.Bytes[0x3000 + 300]);
    }

    [Fact]
    public void Fixed_address_pointers()
    {
        var r = Run.Program("""
            var near: ptr u8 @ $FB
            var far: ptr u8 @ $C000
            fn main() {
                near = $3000
                far = $3100
                near[1] = 11
                far[2] = 22
            }
            """);
        Assert.Equal(11, r.Ram.Bytes[0x3001]);
        Assert.Equal(22, r.Ram.Bytes[0x3102]);
        Assert.Contains("91 FB     sta (near),y", r.Result.Listing);
    }

    [Fact]
    public void Pointer_index_in_loop_registers()
    {
        var r = Run.Program("""
            var buf: [u8; 8]
            var p: ptr u8
            fn main() {
                p = addr(buf)
                for i in 0..4 using Y { p[i] = i }
                for i in 4..8 using X { p[i] = i + 10 }
            }
            """);
        Assert.Equal([0, 1, 2, 3, 14, 15, 16, 17], r.Ram.Bytes.AsSpan(r.Address("buf"), 8).ToArray());
    }

    [Fact]
    public void Conversions_and_comparisons()
    {
        var r = Run.Program("""
            var w: u16 = $1234
            var p: ptr u8
            var q: ptr i8
            var back: u16
            var hi: u8
            var results: [u8; 6]
            fn main() {
                p = w as ptr u8
                q = p as ptr i8
                back = (q as u16) + 1
                hi = (p as u16).hi
                if p == $1234 { results[0] = 1 }
                if p != 0 { results[1] = 1 }
                if p < $1235 as ptr u8 { results[2] = 1 }
                if p >= $1300 { results[3] = 1 }
                if q as ptr u8 == p { results[4] = 1 }
                if p + 1 > p { results[5] = 1 }
            }
            """);
        Assert.Equal(0x1234, r.U16("p"));
        Assert.Equal(0x1234, r.U16("q"));
        Assert.Equal(0x1235, r.U16("back"));
        Assert.Equal(0x12, r.U8("hi"));
        Assert.Equal([1, 1, 1, 0, 1, 1], r.Ram.Bytes.AsSpan(r.Address("results"), 6).ToArray());
    }

    [Fact]
    public void Functions_take_and_return_pointers()
    {
        var r = Run.Program("""
            var buf: [u8; 4]
            fn second(p: ptr u8) -> ptr u8 { return p + 1 }
            fn put(p: ptr u8 using AX, v: u8) { p[0] = v }
            fn main() {
                put(second(addr(buf)), 9)
                put(addr(buf) + 3, 4)
            }
            """);
        Assert.Equal([0, 9, 0, 4], r.Ram.Bytes.AsSpan(r.Address("buf"), 4).ToArray());
    }

    [Fact]
    public void Addresses_of_variables()
    {
        var r = Run.Program("""
            var g: u8
            var w: u16
            var table: [u16; 3]
            var pg: ptr u8
            var pw: ptr u16
            var pt: ptr u8
            fn f(x: u8) { var p: ptr u8 = addr(x); p[0] += 1; g = x }
            fn main() {
                pg = addr(g)
                pw = addr(w)
                pt = addr(table)
                pw[0] = $4321
                f(41)
            }
            """);
        Assert.Equal(r.Address("g"), r.U16("pg"));
        Assert.Equal(r.Address("w"), r.U16("pw"));
        Assert.Equal(r.Address("table"), r.U16("pt"));
        Assert.Equal(0x4321, r.U16("w"));
        Assert.Equal(42, r.U8("g"));
    }

    [Fact]
    public void Loop_bound_changed_through_a_pointer_is_read_once()
    {
        var r = Run.Program("""
            var count: u8
            fn main() {
                var n: u8 = 5
                var p: ptr u8 = addr(n)
                for i in 0..n {
                    p[0] = 2
                    count += 1
                }
            }
            """);
        Assert.Equal(5, r.U8("count"));
    }
}
