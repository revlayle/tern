using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// Variable indexes into arrays of structs stored as whole records (spec 3.3): <c>linear var</c>
/// shifts the index, <c>indexed var</c> reads the record's offset from a compile-time table.
/// </summary>
public class RecordIndexTests
{
    /// <summary>The instructions of main, without addresses, bytes and cycles.</summary>
    private static string Code(Run r)
    {
        var listing = r.Result.Listing;
        var start = listing.IndexOf("\nmain:", StringComparison.Ordinal);
        var code = new List<string>();
        foreach (var l in listing[(start + 1)..].Split('\n').Skip(1))
        {
            if (!l.StartsWith('$')) continue;
            code.Add(l[17..].Split(';')[0].Trim());
            if (code[^1] == "rts") break;   // the offset table's bytes follow main
        }
        return string.Join("|", code);
    }

    private const string Header = """
        struct S1 { a: u8 }
        struct S2 { a: u8, b: u8 }
        struct S4 { a: u8, b: u8, c: u8, d: u8 }
        struct S5 { a: u8, b: u8, c: u8, w: u16 }
        struct S8 { a: u8, b: u8, c: u8, w: u16, _: [u8; 3] }
        struct Rec { a: u8, b: i8, w: u16, _: [u8; 4] }       // 8 bytes
        struct Odd { a: u8, w: u16, s: i16 }                   // 5 bytes
        struct Pair { x: u8, y: u8 }
        struct Tri { p: Pair, z: u8 }                          // 3 bytes, a nested struct

        """;

    private static string Shape(string declaration, string statement) =>
        Code(Run.Program($$"""
            {{Header}}
            {{declaration}}
            var i: u8
            var v: u8
            var w: u16
            fn main() { {{statement}} }
            """));

    // ---------- Code shapes ----------

    [Theory]
    [InlineData("linear var list: [S1; 20]", "v = list[i].a", "ldx i|lda list,x|sta v|rts")]
    [InlineData("linear var list: [S2; 20]", "v = list[i].b", "lda i|asl a|tax|lda list+1,x|sta v|rts")]
    [InlineData("linear var list: [S4; 20]", "v = list[i].c", "lda i|asl a|asl a|tax|lda list+2,x|sta v|rts")]
    [InlineData("linear var list: [S8; 20]", "v = list[i].c", "lda i|asl a|asl a|asl a|tax|lda list+2,x|sta v|rts")]
    [InlineData("linear var list: [S8; 20]", "w = list[i].w", "lda i|asl a|asl a|asl a|tax|lda list+3,x|sta w|lda list+4,x|sta w+1|rts")]
    [InlineData("linear var list: [S4; 20]", "list[i].b = v", "lda i|asl a|asl a|tax|lda v|sta list+1,x|rts")]
    public void Linear_index_shape(string declaration, string statement, string expected) =>
        Assert.Equal(expected, Shape(declaration, statement));

    [Theory]
    [InlineData("indexed var list: [S5; 20]", "v = list[i].c", "ldx i|ldy list.offsets,x|lda list+2,y|sta v|rts")]
    [InlineData("indexed var list: [S5; 20]", "list[i].b = v", "ldx i|ldy list.offsets,x|lda v|sta list+1,y|rts")]
    [InlineData("indexed var list: [S5; 20]", "w = list[i].w", "ldx i|ldy list.offsets,x|lda list+3,y|sta w|lda list+4,y|sta w+1|rts")]
    [InlineData("indexed var list: [S4; 20]", "v = list[i].a", "ldx i|ldy list.offsets,x|lda list,y|sta v|rts")]
    public void Indexed_shape(string declaration, string statement, string expected) =>
        Assert.Equal(expected, Shape(declaration, statement));

    [Fact]
    public void A_loop_variable_in_X_is_the_table_index()
    {
        var code = Shape("indexed var list: [S5; 20]", "for k in 0..20 using X { v = list[k].a }");
        Assert.Contains("ldy list.offsets,x|lda list,y", code);
    }

    // ---------- Behaviour: every element and field against C# ----------

    [Fact]
    public void Linear_records_of_8_bytes_hold_every_element_and_field()
    {
        var r = Run.Program(Header + """
            linear var list: [Rec; 20]
            var back: [u16; 20]
            var sum: i16
            fn main() {
                for i in 0..20 {
                    list[i].a = i << 1
                    list[i].b = 0 - (i as i8)
                    list[i].w = (i as u16) << 4
                }
                for i in 0..20 { back[i] = list[i].w }
                for i in 0..20 { list[i].a += 1 }
                for i in 0..20 { sum = sum + list[i].b }
            }
            """);
        int list = r.Address("list");
        int expectedSum = 0;
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal((i * 2 + 1) & 0xFF, r.Ram.Bytes[list + i * 8]);
            Assert.Equal((sbyte)-i, (sbyte)r.Ram.Bytes[list + i * 8 + 1]);
            Assert.Equal((i << 4) & 0xFFFF, r.Ram.Bytes[list + i * 8 + 2] | r.Ram.Bytes[list + i * 8 + 3] << 8);
            Assert.Equal((i << 4) & 0xFFFF, r.U16At("back", i, 20));
            expectedSum -= i;
        }
        Assert.Equal(expectedSum, r.I16("sum"));
    }

    [Fact]
    public void Indexed_records_of_5_bytes_hold_every_element_and_field()
    {
        var r = Run.Program(Header + """
            indexed var list: [Odd; 51]                    // 255 bytes: the largest that fits
            var back: [u16; 51]
            var neg: i16
            fn main() {
                for i in 0..51 {
                    list[i].a = i + 200
                    list[i].w = (i as u16) << 8
                    list[i].s = 0 - ((i as i16) << 2)
                }
                for i in 0..51 { back[i] = list[i].w }
                neg = list[50].s
            }
            """);
        int list = r.Address("list");
        for (int i = 0; i < 51; i++)
        {
            Assert.Equal((i + 200) & 0xFF, r.Ram.Bytes[list + i * 5]);
            Assert.Equal(i << 8, r.Ram.Bytes[list + i * 5 + 1] | r.Ram.Bytes[list + i * 5 + 2] << 8);
            Assert.Equal((short)(-i * 4), (short)(r.Ram.Bytes[list + i * 5 + 3] | r.Ram.Bytes[list + i * 5 + 4] << 8));
            Assert.Equal(i << 8, r.U16At("back", i, 51));
        }
        Assert.Equal(-200, r.I16("neg"));
    }

    [Fact]
    public void The_offset_table_holds_i_times_the_struct_size()
    {
        var r = Run.Program(Header + """
            indexed var list: [Odd; 10]
            fn main() { var k: u8 = 3; list[k].a = 1 }
            """);
        var table = r.Address("list.offsets");
        for (int i = 0; i < 10; i++) Assert.Equal(i * 5, r.Ram.Bytes[table + i]);
        Assert.Equal(1, r.Ram.Bytes[r.Address("list") + 15]);
    }

    [Fact]
    public void Index_forms_in_loops_and_expressions()
    {
        var r = Run.Program(Header + """
            linear var q: [Pair; 8]
            indexed var t: [Tri; 8]
            var j: u8 = 6
            var got: u8
            var total: u8
            fn main() {
                for i in 0..8 using X { q[i].x = i; q[i].y = i + 100 }
                for i in 0..8 using Y { t[i].p.x = i + 1; t[i].p.y = i + 50; t[i].z = i + 90 }
                got = q[j - 1].y + t[j].z
                q[j + 1].x = q[j].x + 7
                for i in 0..8 using X { total += t[i].p.y }
                t[j].z -= 1
                t[j].z <<= 1
            }
            """);
        int q = r.Address("q"), t = r.Address("t");
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(i == 7 ? 13 : i, r.Ram.Bytes[q + i * 2]);
            Assert.Equal(i + 100, r.Ram.Bytes[q + i * 2 + 1]);
            Assert.Equal(i + 1, r.Ram.Bytes[t + i * 3]);
            Assert.Equal(i + 50, r.Ram.Bytes[t + i * 3 + 1]);
        }
        Assert.Equal(((6 + 90 - 1) << 1) & 0xFF, r.Ram.Bytes[t + 6 * 3 + 2]);
        Assert.Equal(105 + 96, r.U8("got"));
        Assert.Equal(Enumerable.Range(0, 8).Sum(i => i + 50) & 0xFF, r.U8("total"));
    }

    [Fact]
    public void Constant_indexes_are_fixed_addresses_and_work_on_any_struct_size()
    {
        var r = Run.Program(Header + """
            linear var list: [Odd; 60]                     // 300 bytes, 5-byte records: no variable index needed
            var v: u8
            fn main() {
                list[59].a = 7
                list[2].s = -2
                v = list[59].a
            }
            """);
        Assert.Equal("lda #$07|sta list+295|lda #$FE|sta list+13|lda #$FF|sta list+14|lda list+295|sta v|rts", Code(r));
        Assert.Equal(7, r.U8("v"));
        Assert.Equal(-2, (short)(r.Ram.Bytes[r.Address("list") + 13] | r.Ram.Bytes[r.Address("list") + 14] << 8));
    }

    [Fact]
    public void An_indexed_array_in_a_function_and_a_pointer_to_it_see_the_same_records()
    {
        var r = Run.Program(Header + """
            indexed var list: [Odd; 6]
            var seen: u8
            fn main() {
                indexed var local: [Pair; 5]
                var k: u8 = 4
                list[k].a = 9
                local[k].y = 21
                var p: ptr Odd @ zp = addr(list)
                p = p + sizeof(Odd) * 4
                seen = p.a + local[k].y
            }
            """);
        Assert.Equal(30, r.U8("seen"));
    }

    [Fact]
    public void A_padding_field_takes_space_and_has_no_name()
    {
        var r = Run.Program("""
            struct Rec { a: u8, _: u8, b: u8, _: [u8; 2] @ 5, c: u8 @ 7 }
            var one: Rec
            var n: u8
            fn main() { n = sizeof(Rec); one.b = 4; one.c = 5 }
            """);
        Assert.Equal(8, r.U8("n"));
        Assert.Equal(4, r.Ram.Bytes[r.Address("one") + 2]);
        Assert.Equal(5, r.Ram.Bytes[r.Address("one") + 7]);
    }

    [Fact]
    public void Padding_to_a_power_of_two_makes_a_struct_indexable()
    {
        var r = Run.Program("""
            struct Rec { a: u8, b: u8, c: u8, _: u8, w: u16, _: u16 }
            linear var list: [Rec; 4]
            fn main() { var i: u8 = 3; list[i].w = $1234 }
            """);
        Assert.Equal(0x34, r.Ram.Bytes[r.Address("list") + 3 * 8 + 4]);
        Assert.Equal(0x12, r.Ram.Bytes[r.Address("list") + 3 * 8 + 5]);
    }

    [Fact]
    public void Indexed_is_still_a_usable_name()
    {
        var r = Run.Program("""
            var indexed: u8
            fn main() { indexed = 5; indexed += 1 }
            """);
        Assert.Equal(6, r.U8("indexed"));
    }

    [Fact]
    public void Indexed_may_follow_noinit_and_volatile_and_private()
    {
        var r = Run.Program(Header + """
            noinit indexed var a: [Odd; 4]
            volatile indexed var b: [Odd; 4]
            private indexed var c: [Odd; 4]
            fn main() { var i: u8 = 2; a[i].a = 1; b[i].a = 2; c[i].a = 3 }
            """);
        Assert.Equal(1, r.Ram.Bytes[r.Address("a") + 10]);
        Assert.Equal(2, r.Ram.Bytes[r.Address("b") + 10]);
        Assert.Equal(3, r.Ram.Bytes[r.Address("c") + 10]);
    }

    [Fact]
    public void The_table_is_left_out_when_no_variable_index_uses_it()
    {
        var r = Run.Program(Header + """
            indexed var list: [Odd; 6]
            fn main() { list[2].a = 9 }
            """);
        Assert.DoesNotContain("list.offsets", r.Result.Listing);
        var used = Run.Program(Header + """
            indexed var list: [Odd; 6]
            fn main() { var i: u8 = 2; list[i].a = 9 }
            """);
        Assert.Contains("list.offsets", used.Result.Listing);
        Assert.Contains("Offset table list.offsets: 6 bytes", used.Result.Report);
        Assert.DoesNotContain("Offset table", r.Result.Report);
    }

    [Fact]
    public void A_value_changed_by_the_index_call_is_read_before_the_call()
    {
        var r = Run.Program("""
            var arr: [u8; 10]
            var v: u8 = 1
            fn h() -> u8 { v = 7; return 2 }
            fn main() { arr[h()] = v }
            """);
        Assert.Equal(1, r.U8("arr", 2));
    }

    [Fact]
    public void A_user_name_cannot_clash_with_the_offset_table()
    {
        var r = Run.Program(Header + """
            indexed var list: [Odd; 6]
            var list_offsets: u8 = 3
            fn main() { var i: u8 = 2; list[i].a = list_offsets }
            """);
        Assert.Equal(3, r.Ram.Bytes[r.Address("list") + 10]);
        Assert.Contains("list.offsets:", r.Result.Listing);
    }

    // ---------- Errors ----------

    [Theory]
    [InlineData("linear var list: [Odd; 4]\nvar i: u8\nfn main() { i = list[i].a }",
        "'list' has records of 5 bytes, and a variable index into a linear array needs 1, 2, 4 or 8. Pad Odd with '_' fields to 8 bytes (e.g. _: [u8; 3]), or declare the array 'indexed var list' to use a table of 4 bytes")]
    [InlineData("linear var list: [Pair; 130]\nvar i: u8\nfn main() { i = list[i].x }", "more than the 256 an 8-bit offset reaches")]
    [InlineData("linear var list: [Pair; 130]\nvar i: u8\nfn main() { i = list[i].x }", "Step through it with a pointer: p = p + sizeof(Pair)")]
    [InlineData("linear var list: [Pair; 4]\nvar i: u8\nfn main() { i = list[4].x }", "Index 4 is past the end of 'list'")]
    [InlineData("indexed var list: [Pair; 130]\nfn main() { }", "An indexed array is at most 256 bytes")]
    [InlineData("indexed var list: [u8; 10]\nfn main() { }", "'indexed' is for arrays of structs")]
    [InlineData("indexed var n: u8\nfn main() { }", "'indexed' is for arrays of structs")]
    [InlineData("indexed linear var list: [Pair; 4]\nfn main() { }", "'indexed' replaces 'linear'")]
    [InlineData("linear indexed var list: [Pair; 4]\nfn main() { }", "'indexed' replaces 'linear'")]
    [InlineData("indexed data list: [u8; 4] = [1, 2, 3, 4]\nfn main() { }", "'indexed' is for arrays of structs, which cannot be data")]
    [InlineData("indexed var list: [Pair; 4]\nvar i: u8\nfn main() { var p: ptr u8 = addr(list[i].x) }", "variable index, so its address is not known")]
    [InlineData("struct A { x: u8, _: u8 }\nvar a: A\nfn main() { a._ = 1 }", "'_' is padding in struct 'A'")]
    [InlineData("struct A { x: u8, _: u8 }\nvar a: A\nvar n: u8\nfn main() { n = a._ }", "'_' is padding in struct 'A'")]
    [InlineData("struct A { x: u8, _: u8 @ 0 }\nfn main() { }", "overlaps")]
    [InlineData("struct A { n: [u8; 3], w: u8 }\nindexed var list: [A; 4]\nvar i: u8\nvar j: u8\nfn main() { j = list[i].n[j] }",
        "needs a constant index, since the record's offset is already in the index register")]
    public void Reports_errors(string body, string expected)
    {
        var e = Assert.Throws<CompileError>(() => Run.Program(Header + body));
        Assert.Contains(expected, e.Message);
    }
}
