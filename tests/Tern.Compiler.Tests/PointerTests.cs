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

    [Theory]
    // Spec 11: p[i] = v is ldy i / lda v / sta (p),y.
    [InlineData("p[i] = v", "ldy i|lda v|sta (p),y|rts")]
    [InlineData("for k in 0..4 using X { p[k] = v }",
        "ldx #$00|txa|tay|lda v|sta (p),y|inx|cpx #$04|bne main.for2|rts")]
    // dst[i] = src[i] loads Y once.
    [InlineData("for k in 0..4 using X { p[k] = q[k] }",
        "ldx #$00|txa|tay|lda (q),y|sta (p),y|inx|cpx #$04|bne main.for2|rts")]
    [InlineData("p[i] = q[i]", "ldy i|lda (q),y|sta (p),y|rts")]
    // p[i] op= v changes the element in place.
    [InlineData("for k in 0..4 using X { p[k] += 1 }",
        "ldx #$00|txa|tay|lda (p),y|clc|adc #$01|sta (p),y|inx|cpx #$04|bne main.for2|rts")]
    [InlineData("p[i] <<= 2", "ldy i|lda (p),y|asl a|asl a|sta (p),y|rts")]
    // A 16-bit store into a split array needs no temporary when the value needs no code.
    [InlineData("rows[1] = addr(v)", "lda #v.lo|sta rows+1|lda #v.hi|sta rows+3|rts")]
    public void Pointer_code_shapes(string statement, string expected)
    {
        var r = Run.Program($$"""
            var p: ptr u8
            var q: ptr u8
            var i: u8
            var v: u8
            var rows: [ptr u8; 2]
            fn main() { {{statement}} }
            """);
        Assert.Equal(expected, Listing.Code(r));
    }

    [Fact]
    public void Stores_with_the_index_in_a_register_give_the_right_values()
    {
        var r = Run.Program("""
            var src: [u8; 4] = [5, 6, 7, 8]
            var dst: [u8; 4]
            var other: [u8; 4]
            var v: u8 = 9
            var arr: [u8; 4] = [1, 2, 3, 4]
            fn ten() -> u8 { return 10 }
            fn main() {
                var s: ptr u8 = addr(src)
                var d: ptr u8 = addr(dst)
                var o: ptr u8 = addr(other)
                for i in 0..4 using X { d[i] = s[i] }
                for i in 0..4 using X { s[i] += v }
                for i in 0..2 using X { o[i] = s[3 - i] }      // value uses Y with another index
                for i in 0..2 { o[i + 2] = ten() + arr[i] }      // value calls a function
                for i in 0..4 using Y { d[i] = d[i] ^ arr[i] }   // arrays use X while Y is held
            }
            """);
        Assert.Equal([5 ^ 1, 6 ^ 2, 7 ^ 3, 8 ^ 4], r.Ram.Bytes.AsSpan(r.Address("dst"), 4).ToArray());
        Assert.Equal([14, 15, 16, 17], r.Ram.Bytes.AsSpan(r.Address("src"), 4).ToArray());
        Assert.Equal([17, 16, 11, 12], r.Ram.Bytes.AsSpan(r.Address("other"), 4).ToArray());
    }

    [Fact]
    public void Compound_assignment_through_pointers()
    {
        var r = Run.Program("""
            var a: [u8; 6] = [10, 10, $F0, $0F, 3, 200]
            var s: [i8; 1] = [-8]
            var n: u8 = 4
            fn main() {
                var p: ptr u8 = addr(a)
                var q: ptr i8 = addr(s)
                p[0] += n
                p[1] -= 11
                p[2] &= $3C
                p[3] |= $80
                p[4] ^= 1
                p[5] >>= 3
                q[0] >>= 1
                (p + 1)[3] <<= 1
            }
            """);
        Assert.Equal([14, 255, 0x30, 0x8F, 4, 25], r.Ram.Bytes.AsSpan(r.Address("a"), 6).ToArray());
        Assert.Equal(-4, r.I8("s"));
    }

    [Fact]
    public void Sixteen_bit_stores_into_split_arrays()
    {
        var r = Run.Program("""
            var rows: [ptr u8; 3]
            var ws: [u16; 3]
            var w: u16 = $1234
            var j: u8 = 2
            var buf: [u8; 2]
            fn main() {
                rows[0] = addr(buf)
                rows[j] = $C000
                ws[1] = w
                ws[j] = w + 1
            }
            """);
        Assert.Equal(r.Address("buf"), r.U16At("rows", 0, 3));
        Assert.Equal(0xC000, r.U16At("rows", 2, 3));
        Assert.Equal(0x1234, r.U16At("ws", 1, 3));
        Assert.Equal(0x1235, r.U16At("ws", 2, 3));
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
                plus8 = p + small
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

    [Theory]
    [InlineData(0x1000, 0x1000)]
    [InlineData(0x10F0, 0x1010)]
    [InlineData(0x1010, 0x10F0)]
    [InlineData(0x0000, 0xFFFF)]
    [InlineData(0xFFFF, 0x0000)]
    public void Pointer_difference_is_a_u16_byte_distance(int p, int q)
    {
        var r = Run.Program($$"""
            var p: ptr u16 = {{p}}
            var q: ptr u16 = {{q}}
            var d: u16
            var hi: u8
            var near: bool
            fn main() {
                d = p - q
                hi = (p - q).hi
                near = p - q < 256
            }
            """);
        int expected = (p - q) & 0xFFFF;
        Assert.Equal(expected, r.U16("d"));
        Assert.Equal(expected >> 8, r.U8("hi"));
        Assert.Equal(expected < 256 ? 1 : 0, r.U8("near"));
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
            """, setup: ram => ram.Bytes[0x3106] = 99);
        Assert.Equal(42, r.Ram.Bytes[0x3105]);
        Assert.Equal(99, r.U8("got"));
        Assert.Equal(7, r.Ram.Bytes[0x3000 + 300]);
    }

    [Trait("Shape", "nmos6502")]
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

    /// <summary>The pointer errors (spec 3.4), with the part of the message each must have; moved from ErrorTests (review code-quality-tests 14).</summary>
    [Theory]
    [InlineData("var p: ptr\nfn main() { }", 1, 8, "'ptr' needs the type it points to. Write e.g. 'ptr u8'")]
    [InlineData("var p: ptr u8\nvar q: ptr u8\nvar r: ptr u8\nfn main() { r = p + q }", 4, 19, "Two pointers cannot be added. Convert them to u16 first: (p as u16)")]
    [InlineData("var p: ptr u8\nvar q: ptr u16\nvar w: u16\nfn main() { w = p - q }", 4, 19, "Cannot subtract ptr u16 from ptr u8; convert one side with 'as'")]
    [InlineData("var p: ptr u8\nvar q: ptr u8\nfn main() { q = p - q }", 3, 19, "Expected ptr u8, found u16. Convert with 'as ptr u8'")]
    [InlineData("var p: ptr u8\nfn main() { p = p & 7 }", 2, 19, "'&' does not work on pointers. Convert to u16 first: (p as u16)")]
    [InlineData("var p: ptr u8\nfn main() { p <<= 1 }", 2, 15, "'<<=' does not work on pointers; only '+=' and '-=' do. Convert the pointer with 'as u16' for other operators")]
    [InlineData("var p: ptr u8\nvar s: i8\nfn main() { p = p + s }", 3, 21, "A pointer moves by a u8 or u16, not i8. Convert the offset with 'as u16'")]
    [InlineData("var p: ptr u8\nfn main() { p = p + -1 }", 2, 21, "A pointer moves by an unsigned number; use '-' to move back")]
    [InlineData("var p: ptr u8\nvar q: ptr u8\nfn main() { q = 1 - p }", 3, 19, "A pointer cannot be subtracted from a number. Convert the pointer with 'as u16'")]
    [InlineData("var p: ptr u8\nvar w: u16\nfn main() { p = w }", 3, 17, "Expected ptr u8, found u16. Convert with 'as ptr u8'")]
    // Review code-quality-semantics 12: every type mismatch says how to fix it, with a fix that compiles.
    [InlineData("var b: bool = 1\nfn main() { }", 1, 15, "Expected bool, found a number. Write true or false, or compare, as in 'x != 0'")]
    [InlineData("var b: bool\nvar x: u8\nfn main() { b = x }", 3, 17, "Expected bool, found u8. Compare instead, as in 'x != 0'")]
    [InlineData("var b: bool\nvar x: u8\nfn main() { x = b }", 3, 17, "Expected u8, found bool. Use 'if' to choose a number, as in 'if b { x = 1 }'")]
    [InlineData("var p: ptr u8\nvar x: u8\nfn main() { p = x }", 3, 17, "Expected ptr u8, found u8. Convert with 'as u16' first, then 'as ptr u8'")]
    [InlineData("var p: ptr u8\nvar x: u8\nfn main() { x = p }", 3, 17, "Expected u8, found ptr u8. Convert with 'as u16' first, then take .lo or .hi")]
    [InlineData("var p: ptr u8\nvar s: i16\nfn main() { s = p }", 3, 17, "Expected i16, found ptr u8. Convert with 'as u16' first, then 'as i16'")]
    [InlineData("fn main() { var a: [u8; 2] = [1, 2] }", 1, 30, "An initial value for a local array is not supported yet. Assign the elements, or use a global or data")]
    [InlineData("var p: ptr u8\nvar w: u16\nfn main() { w = w + p }", 3, 19, "A number before a pointer is not pointer arithmetic. Write 'p + w' to move the pointer, or convert the pointer with 'as u16' for a number")]
    [InlineData("var p: ptr u8\nvar q: ptr u16\nfn main() { q = p }", 3, 17, "Expected ptr u16, found ptr u8. Convert with 'as ptr u16'")]
    [InlineData("var p: ptr u8\nvar b: u8\nfn main() { b = p as u8 }", 3, 19, "Cannot convert ptr u8 to u8; a pointer converts only to u16 or another pointer. Convert to u16 first, then take .lo or .hi")]
    [InlineData("var p: ptr u8\nvar b: u8\nfn main() { p = b as ptr u8 }", 3, 19, "Cannot convert u8 to ptr u8; only u16 and pointers convert to a pointer. Convert to u16 first")]
    [InlineData("var p: ptr u8\nvar q: ptr u16\nfn main() { if p == q { } }", 3, 18, "Cannot compare ptr u8 with ptr u16; convert one side with 'as'")]
    [InlineData("var p: ptr u8\nvar w: u16\nfn main() { if p == w { } }", 3, 18, "Cannot compare ptr u8 with u16; convert one side with 'as'")]
    [InlineData("var p: ptr u8\nvar b: u8\nfn main() { b = p.lo }", 3, 18, ".lo needs a number; convert the pointer first: (p as u16).lo")]
    [InlineData("var p: ptr u16\nvar w: u16\nfn main() { w = p[128] }", 3, 19, "Index 128 is too large: p[i] reaches 256 bytes, which is 128 u16 values. Move the pointer forward first: p + 256")]
    [InlineData("var p: ptr u8\nfn main() { for i in 0..4 using Y { p[0] = i } }", 2, 38, "p[i] needs the Y register, which holds a loop variable. Use 'using X' for that loop")]
    [InlineData("const C = 5\nvar p: ptr u8\nfn main() { p = addr(C) }", 3, 22, "'C' is a constant, which has no address. Use data for a value in memory")]
    [InlineData("var p: ptr u8\nfn main() { p = addr(nothing) }", 2, 22, "'nothing' is not declared. Check the spelling, or declare it")]
    [InlineData("var p: ptr u8\nvar a: [u8; 2]\nfn main() { p = addr(a[1]) }", 3, 17, "addr() takes the name of a variable, array or data, or a field of a struct variable. Write addr(name)")]
    [InlineData("var p: ptr u8\nfn main() { for i in 0..4 using X { p = addr(i) } }", 2, 46, "'i' lives in a register and has no address. Remove 'using' to keep it in memory")]
    [InlineData("var p: ptr [u8; 4]\nfn main() { }", 1, 8, "A pointer must point to a number, bool, pointer or struct. Use the array's element type: ptr u8")]
    public void Reports_pointer_errors(string source, int line, int column, string expected)
    {
        Expect.Error(source, line, column, expected);
    }
}
