namespace Tern.Compiler.Tests;

/// <summary>
/// M18 part 3, F12: arrays the code reads through an index register are placed so that no such read
/// crosses a page, when the program area has room (Allocator.PlaceByPage, Reserve.PageOffset);
/// otherwise the variables follow each other as before.
/// </summary>
public class PagePlacementTests
{
    private static bool OnePage(int first, int last) => first >> 8 == last >> 8;

    [Fact]
    public void A_256_byte_array_starts_a_page()
    {
        var r = Run.Program("""
            var count: u8
            var marks: [u8; 256]
            fn main() {
                for i in 0..=255 { marks[i] = i & 1 }
                for i in 0..=255 { if marks[i] == 0 { count += 1 } }
            }
            """);
        Assert.Equal(128, r.U8("count"));
        Assert.Equal(0, r.Address("marks") & 0xFF);
        Assert.Contains("placed so that indexed reads do not cross a page: marks $", r.Result.Report);
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Each_table_of_a_split_array_stays_in_its_page()
    {
        var r = Run.Program("""
            var total: u16
            var big: u16
            var values: [u16; 200]
            fn main() {
                for i in 0..200 { values[i] = ((i as u16) << 2) + 1000 }
                for i in 0..200 {
                    total += values[i]
                    if values[i] > big { big = values[i] }
                }
            }
            """);
        int a = r.Address("values");
        Assert.True(OnePage(a, a + 199));
        Assert.True(OnePage(a + 200, a + 399));
        int sum = 0;
        for (int i = 0; i < 200; i++) sum += (i << 2) + 1000;
        Assert.Equal(sum & 0xFFFF, r.U16("total"));
        Assert.Equal((199 << 2) + 1000, r.U16("big"));
        // The only space left free is the gap from the code up to the array's page.
        var free = System.Text.RegularExpressions.Regex.Matches(Listing.Outside(r), @"; \$([0-9A-F]{4}): (\d+) byte\(s\) left free");
        var gap = Assert.Single(free);
        Assert.Equal(a, Convert.ToInt32(gap.Groups[1].Value, 16) + int.Parse(gap.Groups[2].Value));
    }

    [Fact]
    public void A_read_below_the_start_stays_in_the_same_page()
    {
        var r = Run.Program("""
            var stack: [u8; 200]
            var out: u8
            fn main() {
                for i in 1..=199 { stack[i] = i }
                for i in 2..=199 { out += stack[i - 2] ^ stack[i - 1] }
            }
            """);
        int a = r.Address("stack");
        Assert.True(OnePage(a - 2, a + 199));
        int expected = 0;
        for (int i = 2; i <= 199; i++) expected += (i - 2) ^ (i - 1);
        Assert.Equal(expected & 0xFF, r.U8("out"));
    }

    [Fact]
    public void Struct_tables_and_noinit_arrays_are_placed_too()
    {
        var r = Run.Program("""
            struct E { x: u8, y: u8, hp: u16 }
            var pad: [u8; 3]
            var es: [E; 100]
            noinit var buf: [u8; 180]
            var sum: u16
            fn main() {
                for i in 0..100 {
                    es[i].x = i
                    es[i].hp = (i as u16) << 2
                }
                for i in 0..180 { buf[i] = i }
                for i in 0..100 { sum += es[i].hp + (es[i].x as u16) + (buf[i] as u16) }
            }
            """);
        int e = r.Address("es");
        for (int t = 0; t < 4; t++) Assert.True(OnePage(e + t * 100, e + t * 100 + 99), $"table {t} of es at ${e:X4}");
        int b = r.Address("buf");
        Assert.True(OnePage(b, b + 179));
        int sum = 0;
        for (int i = 0; i < 100; i++) sum += (i << 2) + i + i;
        Assert.Equal(sum & 0xFFFF, r.U16("sum"));
    }

    [Fact]
    public void An_array_read_only_at_constant_indexes_is_not_placed()
    {
        var r = Run.Program("""
            var a: [u8; 200]
            var b: [u8; 200]
            fn main() {
                a[3] = 1
                b[199] = a[3] + 1
            }
            """);
        Assert.Equal(2, r.U8("b", 199));
        Assert.DoesNotContain("placed so that", r.Result.Report);
        Assert.Equal(r.Address("a") + 200, r.Address("b"));
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Without_room_the_variables_follow_each_other()
    {
        // 600 bytes of arrays fit one after another, but not with their tables placed in pages.
        var r = Run.Program("""
            memory { $0801..=$0AFF program }
            var a: [u8; 200]
            var b: [u8; 200]
            var c: [u8; 200]
            fn main() {
                for i in 0..200 {
                    a[i] = i
                    b[i] = a[i] + 1
                    c[i] = b[i] + 1
                }
            }
            """);
        for (int i = 0; i < 200; i += 37) Assert.Equal((i + 2) & 0xFF, r.U8("c", i));
        Assert.DoesNotContain("placed so that", r.Result.Report);
        Assert.Equal(r.Address("a") + 200, r.Address("b"));
        Assert.Equal(r.Address("b") + 200, r.Address("c"));
    }

    [Trait("Platform", "c64")]
    // Review M18 part 3, finding 1.
    [Fact]
    public void A_fixed_address_variable_where_the_placed_block_would_go_keeps_the_variables_together()
    {
        var r = Run.Program("""
            var arr: [u8; 250]
            var gfx: [u8; 16] @ $0980
            var total: u8
            fn main() {
                gfx[0] = 1
                for i in 0..250 { total += arr[i] }
            }
            """, setup: ram => { for (int k = 0; k < 250; k++) ram.Bytes[0x0900 + k] = 0xEE; });
        Assert.Equal(0, r.U8("total"));   // arr was cleared at startup
        Assert.Equal(1, r.U8("gfx"));
        Assert.DoesNotContain("placed so that", r.Result.Report);
        int a = r.Address("arr");
        Assert.True(a + 250 <= 0x0980, $"arr at ${a:X4}");
    }

    [Trait("Platform", "c64")]
    // Review M18 part 3, suggestion 2.
    [Trait("Shape", "nmos6502")]
    [Fact]
    public void An_array_only_stored_through_an_index_is_not_placed()
    {
        var r = Run.Program("""
            noinit var buf: [u8; 250]
            var out: u8 @ $C000
            fn main() {
                for i in 0..250 { buf[i] = i }
                out = buf[249]
            }
            """);
        Assert.Equal(249, r.U8("out"));
        Assert.DoesNotContain("placed so that", r.Result.Report);
        Assert.DoesNotContain("left free", Listing.Outside(r));
        // buf is stored through an index and read at a constant one, with no table of its own.
        Assert.Equal([Listing.Startup, "main"], Listing.Sections(r));
        Assert.Contains("sta buf,x", Listing.Instructions(r));
        Assert.Contains("lda buf+249", Listing.Instructions(r));
    }
}
