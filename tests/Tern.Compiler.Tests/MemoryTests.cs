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
    public void A_declaration_at_an_offset_from_a_regions_start_is_there_and_the_others_go_in_order()
    {
        // '@ region + offset' (spec 4, decision 2026-11-14): the offset follows the region when it moves.
        var r = Run.Program("""
            memory { $C000..=$C3FF region buffers }
            const ROW = $100
            var first: [u8; 16] @ buffers
            var col: [u8; 32] @ buffers + ROW
            data table: [u8; 3] @ buffers + $200 = [9, 8, 7]
            noinit var keep: u8 @ buffers + $3FF
            var second: u8 @ buffers
            var seen: u8
            fn main() {
                col[31] += 1
                seen = table[2] + keep
            }
            """, setup: ram =>
        {
            for (int i = 0xC000; i < 0xC200; i++) ram.Bytes[i] = 0xEE;    // not table, loaded with the program
            ram.Bytes[0xC3FF] = 1;
        });
        Assert.Equal(0xC000, r.Address("first"));
        Assert.Equal(0xC010, r.Address("second"));
        Assert.Equal(0xC100, r.Address("col"));
        Assert.Equal(0xC200, r.Address("table"));
        Assert.Equal(0xC3FF, r.Address("keep"));
        Assert.Equal(1, r.U8("col", 31));           // zeroed at startup, then incremented
        Assert.Equal(0, r.U8("col", 0));
        Assert.Equal(0, r.U8("second"));
        Assert.Equal(0xEE, r.Ram.Bytes[0xC011]);    // between the variables: untouched
        Assert.Equal(8, r.U8("seen"));              // 7 + 1, keep not cleared
    }

    [Theory]
    [InlineData("var a: [u8; 32] @ buffers + $F0", "'a' (32 bytes) at offset $F0 does not fit in 'buffers' ($C000-$C0FF, 256 bytes). Use an offset up to $E0", 2, 29)]
    [InlineData("var a: u8 @ buffers + n", "The offset must be known at compile time. Use a number or a constant", 2, 23)]
    [InlineData("var a: [u8; 16] @ buffers\nvar b: [u8; 4] @ buffers + 8", "'b' at $C008-$C00B overlaps 'a' at $C000-$C00F in 'buffers'. Move one of them, with '@ buffers + offset'", 3, 1)]
    [InlineData("var b: [u8; 4] @ buffers + 8\nvar a: [u8; 4] @ buffers + 10", "'a' at $C00A-$C00D overlaps 'b' at $C008-$C00B in 'buffers'. Move one of them, with '@ buffers + offset'", 3, 1)]
    public void Region_offset_errors(string declaration, string message, int line, int column)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile($"memory {{ $C000..=$C0FF region buffers }}\n{declaration}\nvar n: u8\nfn main() {{ }}"));
        Assert.Equal(message, e.Description);
        Assert.Equal((line, column), (e.Pos.Line, e.Pos.Column));
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
        return Listing.Instructions(r).Count(i => i is "ldy i" or "lda i");
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
    [InlineData("memory { $2000..=$1000 reserved }\nfn main() { }", 1, 24, "The range starts at $2000 and ends at $1000, before its start. Write the lower address first")]
    [InlineData("memory { $2000..$2FFF reserved }\nfn main() { }", 1, 15, "A memory range includes its last address. Write first..=last")]
    [InlineData("memory { $2000..=$2FFF reserved\n$2800..=$37FF reserved }\nfn main() { }", 2, 15, "$2800-$37FF (reserved) overlaps $2000-$2FFF (reserved, line 1). Ranges may not overlap, except volatile ones. Move one of them")]
    [InlineData("memory { $0801..=$4FFF program\n$4000..=$9FFF program }\nfn main() { }", 2, 15, "$4000-$9FFF (program) overlaps $0801-$4FFF (program, line 1). Ranges may not overlap, except volatile ones. Move one of them")]
    [InlineData("memory { $2000..=$2FFF region a\n$2000..=$2FFF reserved }\nfn main() { }", 2, 15, "$2000-$2FFF (reserved) overlaps $2000-$2FFF (region a, line 1). Ranges may not overlap, except volatile ones. Move one of them")]
    [InlineData("memory { $100..=$1FF zeropage }\nfn main() { }", 1, 22, "A zeropage range must lie in $00-$FF; $0100-$01FF does not. Use addresses below $100")]
    [InlineData("memory { $2000..=$20FF region r\n$3000..=$30FF region r }\nfn main() { }", 2, 15, "The region 'r' is declared twice. Give each region its own name")]
    [InlineData("memory { $2000..=$2FFF weird }\nfn main() { }", 1, 24, "Unknown kind of memory 'weird'. Use program, zeropage, reserved, region, volatile, overlay, bank or chr")]
    [InlineData("memory { $2000..=$2FFF region }\nfn main() { }", 1, 31, "Expected a name after 'region', found '}'")]
    [InlineData("memory { $2000..=$2FFF reserved", 1, 1, "The memory block is not closed with '}'. Add '}' after its last range")]
    [InlineData("memory { $2000..=$10000 reserved }\nfn main() { }", 1, 25, "The range $2000..=$10000 is outside memory ($0000-$FFFF). Use addresses in that range")]
    [InlineData("memory { NOPE..=$2FFF reserved }\nfn main() { }", 1, 10, "'NOPE' is not declared. Check the spelling, or declare it")]
    [InlineData("var x: u8\nmemory { $2000..=$2FFF reserved }\nfn main() { }", 2, 1, "The memory block goes after the imports and constants, before other declarations, and there is only one. Move it there, or join the two blocks")]
    [InlineData("memory { $2000..=$2FFF reserved }\nmemory { $3000..=$3FFF reserved }\nfn main() { }", 2, 1, "The memory block goes after the imports and constants, before other declarations, and there is only one. Move it there, or join the two blocks")]
    [InlineData("memory { $0900..=$9FFF program }\nfn main() { }", 2, 1, "A program with 'main' starts at $0801, where the C64 BASIC line goes, but its first program area begins at $0900. Start the first 'program' line of the memory block at $0801")]
    [InlineData("memory { $0800..=$08FF reserved }\nfn main() { }", 2, 1, "A program with 'main' starts at $0801, where the C64 BASIC line goes, but its first program area begins at $0900. Move the 'reserved' or 'region' range that covers it")]
    [InlineData("memory { $0800..=$9FFF reserved }\nfn main() { }", 1, 24, "No program area is left: the reserved and region ranges cover all of $0801-$9FFF. Leave room for the program")]
    [InlineData("memory { $0801..=$0830 program }\n" + Functions, 1, 1, "'f6' needs 14 bytes, and no program area has room for them in one piece (code is never split across areas). Areas: $0801-$0830: 42 of 48 bytes used (startup, main). Make an area larger, or split the code into smaller functions")]
    [InlineData("memory { $0801..=$0805 program\n$1000..=$1FFF program }\nfn main() { }", 1, 1, "The BASIC line and startup code (19 bytes) must be at the start of the first program area, but it does not fit in $0801-$0805. Make the first area larger")]
    [InlineData("data first: [u8; 3] @ $0801 = [1, 2, 3]\nfn main() { }", 1, 1, "'first' at $0801-$0803 overlaps the BASIC line and startup code at $0801-$0813, which must be at the start of the first program area. Move the data, or start the program area lower")]
    [InlineData("memory { $C000..=$C0FF region buf }\nvar a: [u8; 16] @ buf\nvar b: u8 @ $C004\nfn main() { a[4] = 1; b = 2 }", 3, 1, "'b' at $C004 overlaps the variables the compiler placed in region 'buf' at $C000-$C00F. Move 'b', or place it with the region")]
    [InlineData("memory { $0002..=$7FFF program }\nvar p: ptr u8 = 0\nfn main() { p[0] = 1 }", 2, 1, "Zero page is full: '@ zp' variables, dereferenced pointers and zero-page assembly operands need 2 bytes, 0 are free. Users: p (2). Remove '@ zp' from some of them")]
    [InlineData("memory { $0801..=$09FF program }\nvar a: [u8; 200]\nvar b: [u8; 200]\nvar c: [u8; 200]\nvar d: [u8; 200]\nfn main() { a[0] = 1 }", 1, 1, "The variables need 800 bytes, and no program area has room for them in one piece (code is never split across areas). Areas: $0801-$09FF: 59 of 511 bytes used (startup, main). Make an area larger, or split the code into smaller functions")]
    [InlineData("memory { $0801..=$0810 program }\ndata d: [u8; 16] @ $0801 = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]\nfn main() { }", 1, 1, "The BASIC line and startup code (19 bytes) must be at the start of the first program area, but it does not fit in $0801-$0810. Make the first area larger")]
    [InlineData("memory { $C000..=$C00F region small }\nvar big: [u8; 32] @ small\nfn main() { }", 2, 1, "Region 'small' ($C000-$C00F, 16 bytes) is full: 'big' needs 32 bytes at $C000, 16 are left. Place it elsewhere or make the region larger")]
    [InlineData("memory { $C000..=$C00F region small }\nvar v: u8 @ small = 5\nfn main() { }", 2, 21, "A variable in a region cannot have an initial value: the region is not part of the program file. Assign it at the start of main")]
    [InlineData("var v: u8 @ nowhere\nfn main() { }", 1, 13, "'nowhere' is not declared. Check the spelling, or declare it")]
    [InlineData("memory { $C000..=$C0FF region BUF }\nconst BUF = $C100\nvar x: u8 @ BUF\nfn main() { x = 1 }", 1, 24, "The region 'BUF' has the name of a constant of the main file (line 2), so '@ BUF' would be ambiguous. Rename the region")]
    [InlineData("memory { $C000..=$C0FF region x }\nvar x: u8\nfn main() { x = 1 }", 1, 24, "The region 'x' has the name of a variable of the main file (line 2), so '@ x' would be ambiguous. Rename the region")]
    [InlineData("memory { $C000..=$C0FF region main }\nfn main() { }", 1, 24, "The region 'main' has the name of a function of the main file (line 2), so '@ main' would be ambiguous. Rename the region")]
    [InlineData("import c64\nmemory { $C000..=$C0FF region c64 }\nfn main() { }", 2, 24, "The region 'c64' has the name of an imported module, so '@ c64' would be ambiguous. Rename the region")]
    [InlineData("memory { $C000..=$C00F region r }\nfn main() { var local: u8 @ r = 0 }", 2, 13, "Only global variables can have a fixed address. Move the variable out of the function")]
    [InlineData("data a: [u8; 2] @ $C000 = [1, 2]\nfn main() { }", 1, 1, "'a' at $C000-$C001 is not in a program or reserved area. Program areas: $0801-$9FFF; reserved: none. Add a 'reserved' line for it to the memory block")]
    [InlineData("memory { $2000..=$2FFF reserved }\ndata a: [u8; 2] @ $2000 = [1, 2]\ndata b: [u8; 1] @ $2001 = [3]\nfn main() { }", 1, 1, "'a' ($2000-$2001) and 'b' ($2001) are placed at overlapping addresses. Move one of them")]
    [InlineData("memory { $2000..=$2FFF reserved }\ndata a: [u8; 2] @ $2000 = [1, 2]\nvar v: u8 @ $2001\nfn main() { }", 3, 1, "'v' at $2001 overlaps the data 'a' at $2000-$2001. Move one of them")]
    [InlineData("memory { $2000..=$2FFF reserved }\ndata a: [u8; 2] @ $2FFF = [1, 2]\nfn main() { }", 2, 1, "'a' at $2FFF-$3000 is not in a program or reserved area. Program areas: $0801-$1FFF, $3000-$9FFF; reserved: $2000-$2FFF. Add a 'reserved' line for it to the memory block")]
    [InlineData("memory { $2000..=$2FFF reserved }\ndata a @ zp = [1, 2]\nfn main() { }", 2, 10, "Data is read-only and lives in the program; '@ zp' is for variables. Remove '@ zp', or make it a var")]
    [InlineData("memory { $FB..=$FE zeropage }\nvar a: [u8; 5] @ zp\nfn main() { a[0] = 1 }", 2, 1, "Zero page is full: '@ zp' variables need 5 bytes, 4 are free. Users: a (5). Remove '@ zp' from some of them")]
    [InlineData("memory { $80..=$80 zeropage\n$FB..=$FE zeropage }\nvar a: u16 @ zp\nfn main() { a = 1 }", 3, 1, "'a' (2 bytes) would cross the end of a zero-page range at $FB. Zero-page ranges: $80, $FB-$FE. Join the ranges or reorder the variables")]
    public void Reports_errors(string source, int line, int column, string expected)
    {
        Expect.Error(source, line, column, expected);
    }

    [Fact]
    public void A_module_cannot_have_a_memory_block()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            string main = Path.Combine(dir.FullName, "main.tern");
            File.WriteAllText(Path.Combine(dir.FullName, "m.tern"), "memory { $2000..=$2FFF reserved }\n");
            Expect.Error(() => TernCompiler.Compile("import m\nfn main() { }", sourcePath: main), 1, 1, "Only the program's main file may have a memory block, since it describes the whole program; 'm' is a module. Move the block to the main file");
        }
        finally { dir.Delete(recursive: true); }
    }

    /// <summary>Review code-quality-semantics 3, decision 2026-11-15: a module places declarations in the main file's regions by name.</summary>
    [Fact]
    public void A_module_places_declarations_in_a_region_of_the_main_file()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "m.tern"), """
                noinit var buf: [u8; 16] @ buffers
                var count: u8 @ buffers + 16
                data table: [u8; 3] @ buffers + 32 = [9, 8, 7]
                fn fill() {
                    buf[0] = 7
                    count = table[1]
                }
                """);
            var r = Run.Program("""
                import m
                memory {
                    $C000..=$CFFF  region buffers
                }
                var out: u8
                var n: u8
                fn main() {
                    m.fill()
                    out = m.buf[0]
                    n = m.count
                }
                """, sourcePath: Path.Combine(dir.FullName, "main.tern"));
            Assert.Equal(7, r.U8("out"));
            Assert.Equal(8, r.U8("n"));
            Assert.Equal(0xC000, r.Address("m.buf"));
            Assert.Equal(0xC010, r.Address("m.count"));
            Assert.Equal(0xC020, r.Address("m.table"));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("const buffers = $C000\nvar v: u8 @ buffers\nfn touch() { v = 1 }", "constant of this module (line 1)")]
    [InlineData("fn buffers() { }\nvar v: u8 @ buffers\nfn touch() { v = 1 }", "function of this module (line 1)")]
    [InlineData("var v: u8 @ buffers\nvar buffers: u8\nfn touch() { v = 1 }", "variable of this module (line 2)")]
    public void A_region_name_a_module_uses_cannot_be_a_symbol_of_the_module(string module, string expected)
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "m.tern"), module + "\n");
            var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(
                "import m\nmemory { $C000..=$C0FF region buffers }\nfn main() { m.touch() }", sourcePath: Path.Combine(dir.FullName, "main.tern")));
            Assert.StartsWith("'buffers' is a region of the main file and a ", e.Description);
            Assert.Contains(expected + ", so '@ buffers' would be ambiguous. Rename the ", e.Description);
            Assert.EndsWith("m.tern", e.Pos.File);
        }
        finally { dir.Delete(recursive: true); }
    }
}
