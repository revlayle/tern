using Tern.Compiler.Backend;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>The memory block (spec 8.4): program areas, reserved memory, regions, zero page, volatile ranges, data at a fixed address.</summary>
public class MemoryTests
{
    /// <summary>Six small functions called from main, so that a small first area overflows into the second.</summary>
    private const string Functions = """
        var a: u8
        fn f1() { a += 1; a += 2; a += 3 }
        fn f2() { a += 4; a += 5; a += 6 }
        fn f3() { a += 7; a += 8; a += 9 }
        fn f4() { a += 10; a += 11; a += 12 }
        fn f5() { a += 13; a += 14; a += 15 }
        fn f6() { a += 16; a += 17; a += 18 }
        fn main() { f1(); f2(); f3(); f4(); f5(); f6() }
        """;

    private static bool Inside(PlacedSection s, AddressRange area) => s.Address >= area.First && s.Address + s.Size - 1 <= area.Last;

    private static bool Overlaps(PlacedSection s, AddressRange area) => s.Size > 0 && s.Address <= area.Last && s.Address + s.Size - 1 >= area.First;

    // ---------- Program areas ----------

    [Fact]
    public void Functions_are_placed_whole_in_the_program_areas_and_never_in_reserved_memory()
    {
        var r = Run.Program($$"""
            memory {
                $0801..=$0850  program
                $2000..=$2FFF  reserved
                $4000..=$9FFF  program
            }
            {{Functions}}
            """);
        Assert.Equal(171, r.U8("a"));       // 1 + 2 + ... + 18
        var areas = new[] { new AddressRange(0x0801, 0x0850), new AddressRange(0x4000, 0x9FFF) };
        foreach (var s in r.Result.Sections.Where(s => s.Size > 0))
            Assert.True(areas.Any(a => Inside(s, a)), $"{s.Name} at ${s.Address:X4} (+{s.Size}) is not inside one program area");
        Assert.Contains(r.Result.Sections, s => s.Address >= 0x4000 && s.Name.StartsWith('f'));
        Assert.Contains(r.Result.Sections, s => s.Address < 0x0851 && s.Name == "startup");
        Assert.Equal(0x0801, r.Result.LoadAddress);
        Assert.Contains("Program areas:", r.Result.Report);
    }

    [Fact]
    public void Reserved_memory_alone_keeps_the_platforms_areas_around_it()
    {
        var r = Run.Program($$"""
            memory { $0900..=$0FFF reserved }
            {{Functions}}
            """);
        Assert.Equal(171, r.U8("a"));
        Assert.Equal(new[] { new AddressRange(0x0801, 0x08FF), new AddressRange(0x1000, 0x9FFF) }, r.Result.Areas.Select(a => a.Free));
    }

    [Fact]
    public void Constants_may_be_used_in_ranges_even_when_declared_below_the_block()
    {
        var r = Run.Program("""
            memory { SPRITES..=SPRITES + $3FF reserved }
            const SPRITES = $2000
            data s: [u8; 2] @ SPRITES = [1, 2]
            var a: u8
            fn main() { a = s[1] }
            """);
        Assert.Equal(2, r.U8("a"));
        Assert.Equal(0x2000, r.Address("s"));
    }

    [Fact]
    public void Constants_may_come_before_the_block_and_ranges_may_use_them()
    {
        var r = Run.Program("""
            const SPRITES = $2000
            const SIZE = $3FF
            memory { SPRITES..=SPRITES + SIZE reserved }
            const OTHER = 5
            data s: [u8; 2] @ SPRITES = [1, 2]
            var a: u8
            fn main() { a = s[1] + OTHER }
            """);
        Assert.Equal(7, r.U8("a"));
        Assert.Equal(0x2000, r.Address("s"));
    }

    [Fact]
    public void Ranges_may_use_constants_from_imports()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "layout.tern"), "const SPRITES = $2000\nconst SIZE = $3FF\n");
            var r = Run.Program("""
                import layout
                memory { layout.SPRITES..=layout.SPRITES + layout.SIZE reserved }
                data s: [u8; 1] @ layout.SPRITES = [7]
                var a: u8
                fn main() { a = s[0] }
                """, sourcePath: Path.Combine(dir.FullName, "main.tern"));
            Assert.Equal(7, r.U8("a"));
            Assert.Equal(0x2000, r.Address("s"));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Program_lines_replace_the_platforms_areas()
    {
        var r = Run.Program($$"""
            memory { $0801..=$0FFF program }
            {{Functions}}
            """);
        Assert.Equal(new[] { new AddressRange(0x0801, 0x0FFF) }, r.Result.Areas.Select(a => a.Free));
        Assert.Equal(171, r.U8("a"));
    }

    [Fact]
    public void Variables_go_in_the_first_area_with_room_after_its_code()
    {
        // The code fills the small first area, so the variables go after the code in the second.
        var r = Run.Program($$"""
            memory {
                $0801..=$0850  program
                $4000..=$9FFF  program
            }
            var big: [u8; 100]
            {{Functions}}
            """);
        Assert.InRange(r.Address("big"), 0x4000, 0x9FFF);
    }

    // ---------- Data at a fixed address ----------

    [Fact]
    public void Data_is_placed_at_its_address_and_gaps_in_the_file_are_zeros()
    {
        var r = Run.Program("""
            memory { $2000..=$2FFF reserved }
            data sprite: [u8; 4] @ $2000 = [1, 2, 3, 4]
            var a: u8
            fn main() { a = sprite[3] }
            """);
        Assert.Equal(4, r.U8("a"));
        Assert.Equal(0x2000, r.Address("sprite"));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, r.Ram.Bytes[0x2000..0x2004]);
        Assert.Equal(0x2004 - 0x0801, r.Result.Image.Length);
        int codeEnd = r.Result.Sections.Where(s => !s.IsFixed).Max(s => s.Address + s.Size);
        Assert.All(r.Result.Image[(codeEnd - 0x0801)..(0x2000 - 0x0801)], b => Assert.Equal(0, b));
        Assert.Contains($"({r.Result.Image.Length} bytes)", r.Result.Report);
    }

    [Fact]
    public void Data_below_the_BASIC_line_moves_the_load_address_down()
    {
        var r = Run.Program("""
            memory { $0400..=$07FF reserved }
            data screen: [u8; 2] @ $0400 = [5, 6]
            var a: u8
            fn main() { a = screen[1] }
            """);
        Assert.Equal(6, r.U8("a"));
        Assert.Equal(0x0400, r.Result.LoadAddress);
        Assert.Equal(0x080D, r.Result.Entry);
        Assert.Equal(new byte[] { 0x00, 0x04 }, r.Result.File[..2]);
    }

    [Fact]
    public void Data_inside_a_program_area_is_a_hole_the_code_goes_around()
    {
        var r = Run.Program($$"""
            memory { $0801..=$0FFF program }
            data table: [u8; 4] @ $0830 = [1, 2, 3, 4]
            {{Functions}}
            """);
        Assert.Equal(171, r.U8("a"));
        var hole = new AddressRange(0x0830, 0x0833);
        foreach (var s in r.Result.Sections.Where(s => !s.IsFixed))
            Assert.False(Overlaps(s, hole), $"{s.Name} at ${s.Address:X4} (+{s.Size}) is on top of the data");
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, r.Ram.Bytes[0x0830..0x0834]);
    }

    [Fact]
    public void Data_with_function_addresses_can_sit_at_a_fixed_address()
    {
        var r = Run.Program("""
            memory { $2000..=$20FF reserved }
            var a: u8
            fn f() { a = 9 }
            data vectors: [u16; 1] @ $2000 = [addr(f)]
            fn main() { asm { jmp (vectors) } }
            """);
        // main ends in jmp (vectors) -> f -> rts back to the startup code
        Assert.Equal(9, r.U8("a"));
        Assert.Equal(r.Address("f"), r.Ram.Bytes[0x2000] | (r.Ram.Bytes[0x2001] << 8));
    }

    // ---------- Regions ----------

    [Fact]
    public void Variables_and_data_in_a_region_are_placed_there_and_variables_are_zeroed()
    {
        var r = Run.Program("""
            memory { $C000..=$C0FF region buffers }
            var map: [u8; 200] @ buffers
            var count: u16 @ buffers
            noinit var keep: u8 @ buffers
            data table: [u8; 3] @ buffers = [9, 8, 7]
            var seen: u8
            var seen_map: u8
            var seen_keep: u8
            fn main() {
                seen = table[1]
                seen_map = map[199]
                seen_keep = keep
                count += 300
            }
            """, setup: ram =>
        {
            for (int i = 0xC000; i < 0xC0C8 + 2; i++) ram.Bytes[i] = 0xEE;   // map and count
            ram.Bytes[0xC0CA] = 0x55;                                         // keep
        });
        Assert.Equal(8, r.U8("seen"));
        Assert.Equal(0, r.U8("seen_map"));          // cleared at startup
        Assert.Equal(0x55, r.U8("seen_keep"));      // noinit: not cleared
        Assert.Equal(0xC000, r.Address("map"));
        Assert.Equal(0xC0C8, r.Address("count"));
        Assert.Equal(300, r.U16("count"));
        Assert.Equal(0xC0CA, r.Address("keep"));
        Assert.Equal(0xC0CB, r.Address("table"));
        Assert.Contains("region buffers $C000-$C0FF: 206 of 256 bytes used", r.Result.Report);
    }

    [Fact]
    public void A_large_region_is_cleared_page_by_page()
    {
        var r = Run.Program("""
            memory { $C000..=$C3FF region big }
            var buf1: [u8; 200] @ big
            var buf2: [u8; 200] @ big
            var buf3: [u8; 200] @ big
            var a: u8
            var b: u8
            fn main() { a = buf1[0]; b = buf3[199] }
            """, setup: ram =>
        {
            for (int i = 0xC000; i <= 0xC000 + 600; i++) ram.Bytes[i] = 0xEE;
        });
        Assert.Equal(0, r.U8("a"));
        Assert.Equal(0, r.U8("b"));
        Assert.Equal(0xEE, r.Ram.Bytes[0xC000 + 600]);     // one byte past the variable: untouched
    }

    [Fact]
    public void A_region_is_not_used_by_the_program_areas()
    {
        var r = Run.Program($$"""
            memory { $0900..=$0FFF region buffers }
            {{Functions}}
            """);
        Assert.Equal(new[] { new AddressRange(0x0801, 0x08FF), new AddressRange(0x1000, 0x9FFF) }, r.Result.Areas.Select(a => a.Free));
    }

    // ---------- Zero page ----------

    [Fact]
    public void Zeropage_lines_replace_the_platforms_free_zero_page()
    {
        var r = Run.Program("""
            memory { $FB..=$FE zeropage }
            var a: u8 @ zp
            var b: u16 @ zp
            var c: u8
            fn main() { a = 1; b = 2; c = 3 }
            """);
        Assert.Equal(0xFB, r.Address("a"));
        Assert.Equal(0xFC, r.Address("b"));
        Assert.True(r.Address("c") == 0xFE || r.Address("c") > 0x100);   // best effort: the last byte, or normal RAM
        Assert.Equal(3, r.U8("c"));
    }

    [Fact]
    public void Zero_page_can_be_several_ranges()
    {
        var r = Run.Program("""
            memory {
                $80..=$81 zeropage
                $FB..=$FE zeropage
            }
            var a: u16 @ zp
            var b: u16 @ zp
            var c: u16 @ zp
            fn main() { a = 1; b = 2; c = 3 }
            """);
        Assert.Equal(new[] { 0x80, 0xFB, 0xFD }, new[] { r.Address("a"), r.Address("b"), r.Address("c") });
        Assert.Equal(3, r.U16("c"));
    }

    [Fact]
    public void Reserved_memory_is_taken_out_of_the_platforms_zero_page()
    {
        var r = Run.Program("""
            memory { $02..=$8E reserved }
            var a: u8 @ zp
            fn main() { a = 1 }
            """);
        Assert.Equal(0x8F, r.Address("a"));
    }

    // ---------- Volatile ----------

    private static int IndexReads(string memory, string declaration)
    {
        var r = TernCompiler.Compile($$"""
            {{memory}}
            {{declaration}}
            var src: ptr u8 @ zp
            var dst: ptr u8 @ zp
            fn main() { dst[i] = src[i] }
            """);
        var main = r.Listing[r.Listing.IndexOf("\nmain:", StringComparison.Ordinal)..];
        return main.Split('\n').Count(l => l.StartsWith('$') && (l.Contains("ldy i ") || l.Contains("lda i ")));
    }

    [Theory]
    [InlineData("", "var i: u8 @ $C010", 1)]
    [InlineData("memory { $C000..=$C0FF volatile }", "var i: u8 @ $C010", 2)]
    [InlineData("memory { $C000..=$C0FF volatile }", "var i: u8 @ $C100", 1)]
    [InlineData("memory { $C000..=$C0FF region shared\n $C000..=$C0FF volatile }", "var i: u8 @ shared", 2)]   // a region inside a volatile range
    [InlineData("memory { $C000..=$C0FF region shared }", "var i: u8 @ shared", 1)]
    [InlineData("memory { $D000..=$D0FF region io }", "var i: u8 @ io", 2)]                                  // a region in the platform's volatile area
    [InlineData("memory { $C000..=$C0FF volatile\n $C000..=$C0FF region r }", "var i: u8 @ $C010", 2)]   // a volatile range may lie in another kind
    public void A_volatile_line_makes_fixed_address_variables_in_it_volatile(string memory, string declaration, int reads)
    {
        Assert.Equal(reads, IndexReads(memory, declaration));
    }

    // ---------- Errors ----------

    [Theory]
    [InlineData("memory { $2000..=$1000 reserved }\nfn main() { }", "starts at $2000 and ends at $1000, before its start")]
    [InlineData("memory { $2000..$2FFF reserved }\nfn main() { }", "A memory range includes its last address. Write first..=last")]
    [InlineData("memory { $2000..=$2FFF reserved\n$2800..=$37FF reserved }\nfn main() { }", "$2800-$37FF (reserved) overlaps $2000-$2FFF (reserved, line 1)")]
    [InlineData("memory { $0801..=$4FFF program\n$4000..=$9FFF program }\nfn main() { }", "overlaps $0801-$4FFF (program, line 1)")]
    [InlineData("memory { $2000..=$2FFF region a\n$2000..=$2FFF reserved }\nfn main() { }", "(reserved) overlaps $2000-$2FFF (region a, line 1)")]
    [InlineData("memory { $100..=$1FF zeropage }\nfn main() { }", "A zeropage range must lie in $00-$FF")]
    [InlineData("memory { $2000..=$20FF region r\n$3000..=$30FF region r }\nfn main() { }", "The region 'r' is declared twice")]
    [InlineData("memory { $2000..=$2FFF weird }\nfn main() { }", "Unknown kind of memory 'weird'")]
    [InlineData("memory { $2000..=$2FFF region }\nfn main() { }", "Expected a name after 'region'")]
    [InlineData("memory { $2000..=$2FFF reserved", "The memory block is not closed with '}'")]
    [InlineData("memory { $2000..=$10000 reserved }\nfn main() { }", "is outside memory")]
    [InlineData("memory { NOPE..=$2FFF reserved }\nfn main() { }", "'NOPE' is not declared")]
    [InlineData("var x: u8\nmemory { $2000..=$2FFF reserved }\nfn main() { }", "The memory block goes after the imports and constants")]
    [InlineData("memory { $2000..=$2FFF reserved }\nmemory { $3000..=$3FFF reserved }\nfn main() { }", "The memory block goes after the imports and constants")]
    [InlineData("memory { $0900..=$9FFF program }\nfn main() { }", "A program with 'main' starts at $0801")]
    [InlineData("memory { $0800..=$08FF reserved }\nfn main() { }", "A program with 'main' starts at $0801")]
    [InlineData("memory { $0800..=$9FFF reserved }\nfn main() { }", "No program area is left")]
    [InlineData("memory { $0801..=$0830 program }\n" + Functions, "no program area has room for them in one piece")]
    [InlineData("memory { $0801..=$0830 program }\n" + Functions, "$0801-$0830:")]
    [InlineData("memory { $0801..=$0805 program\n$1000..=$1FFF program }\nfn main() { }", "The BASIC line and startup code (")]
    [InlineData("memory { $0801..=$0805 program\n$1000..=$1FFF program }\nfn main() { }", "does not fit in $0801-$0805")]
    [InlineData("data first: [u8; 3] @ $0801 = [1, 2, 3]\nfn main() { }", "'first' at $0801-$0803 overlaps the BASIC line and startup code at $0801-")]
    [InlineData("memory { $C000..=$C0FF region buf }\nvar a: [u8; 16] @ buf\nvar b: u8 @ $C004\nfn main() { a[4] = 1; b = 2 }", "'b' at $C004 overlaps the variables the compiler placed in region 'buf' at $C000-$C00F")]
    [InlineData("memory { $0002..=$7FFF program }\nvar p: ptr u8 = 0\nfn main() { p[0] = 1 }", "Zero page is full")]
    [InlineData("memory { $0801..=$0830 program }\n" + Functions, "(startup")]   // what is in the area
    [InlineData("memory { $0801..=$09FF program }\nvar a: [u8; 200]\nvar b: [u8; 200]\nvar c: [u8; 200]\nvar d: [u8; 200]\nfn main() { a[0] = 1 }", "The variables need 800 bytes")]
    [InlineData("memory { $0801..=$0810 program }\ndata d: [u8; 16] @ $0801 = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]\nfn main() { }", "does not fit in $0801-$0810")]
    [InlineData("memory { $C000..=$C00F region small }\nvar big: [u8; 32] @ small\nfn main() { }", "Region 'small' ($C000-$C00F, 16 bytes) is full: 'big' needs 32 bytes")]
    [InlineData("memory { $C000..=$C00F region small }\nvar v: u8 @ small = 5\nfn main() { }", "A variable in a region cannot have an initial value")]
    [InlineData("var v: u8 @ nowhere\nfn main() { }", "'nowhere' is not declared")]
    [InlineData("memory { $C000..=$C0FF region BUF }\nconst BUF = $C100\nvar x: u8 @ BUF\nfn main() { x = 1 }", "The region 'BUF' has the name of a constant of the main file (line 2)")]
    [InlineData("memory { $C000..=$C0FF region x }\nvar x: u8\nfn main() { x = 1 }", "The region 'x' has the name of a variable of the main file")]
    [InlineData("memory { $C000..=$C0FF region main }\nfn main() { }", "The region 'main' has the name of a function of the main file")]
    [InlineData("import c64\nmemory { $C000..=$C0FF region c64 }\nfn main() { }", "The region 'c64' has the name of an imported module")]
    [InlineData("memory { $C000..=$C00F region r }\nfn main() { var local: u8 @ r = 0 }", "Only global variables can have a fixed address")]
    [InlineData("data a: [u8; 2] @ $C000 = [1, 2]\nfn main() { }", "'a' at $C000-$C001 is not in a program or reserved area")]
    [InlineData("memory { $2000..=$2FFF reserved }\ndata a: [u8; 2] @ $2000 = [1, 2]\ndata b: [u8; 1] @ $2001 = [3]\nfn main() { }", "'a' ($2000-$2001) and 'b' ($2001) are placed at overlapping addresses")]
    [InlineData("memory { $2000..=$2FFF reserved }\ndata a: [u8; 2] @ $2000 = [1, 2]\nvar v: u8 @ $2001\nfn main() { }", "'v' at $2001 overlaps the data 'a' at $2000-$2001")]
    [InlineData("memory { $2000..=$2FFF reserved }\ndata a: [u8; 2] @ $2FFF = [1, 2]\nfn main() { }", "'a' at $2FFF-$3000 is not in a program or reserved area")]
    [InlineData("memory { $2000..=$2FFF reserved }\ndata a @ zp = [1, 2]\nfn main() { }", "'@ zp' is for variables")]
    [InlineData("memory { $FB..=$FE zeropage }\nvar a: [u8; 5] @ zp\nfn main() { a[0] = 1 }", "Zero page is full")]
    [InlineData("memory { $80..=$80 zeropage\n$FB..=$FE zeropage }\nvar a: u16 @ zp\nfn main() { a = 1 }", "would cross the end of a zero-page range")]
    public void Reports_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
    }

    [Fact]
    public void A_module_cannot_have_a_memory_block_or_name_a_region()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            string main = Path.Combine(dir.FullName, "main.tern");
            File.WriteAllText(Path.Combine(dir.FullName, "m.tern"), "memory { $2000..=$2FFF reserved }\n");
            var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("import m\nfn main() { }", sourcePath: main));
            Assert.Contains("Only the program's main file may have a memory block", e.Description);

            File.WriteAllText(Path.Combine(dir.FullName, "m.tern"), "var v: u8 @ buffers\nfn touch() { v = 1 }\n");
            e = Assert.Throws<CompileError>(() => TernCompiler.Compile(
                "import m\nmemory { $C000..=$C0FF region buffers }\nfn main() { m.touch() }", sourcePath: main));
            Assert.Contains("'buffers' is not declared", e.Description);
        }
        finally { dir.Delete(recursive: true); }
    }
}
