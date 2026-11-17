using Tern.Compiler.Syntax;
using Tern.Compiler.Semantics;

namespace Tern.Compiler.Tests;

/// <summary>
/// The NES platform (spec 12.2, M27): the cartridge line, the memory map, the startup code, the
/// interrupt vectors and the iNES image, on NROM.
/// </summary>
public class NesTests
{
    private const string Nrom = "memory {\n    cartridge nrom vertical\n}\n";

    private static CompileResult Compile(string source) => TernCompiler.Compile(source, Platform.Nes);

    private static Run RunNes(string source, Func<long, int, byte>? buttons = null) => Run.Program(source, platform: Platform.Nes, buttons: buttons);

    private static CompileError Error(string source) => Assert.Throws<CompileError>(() => Compile(source));

    [Fact]
    public void The_memory_map_is_the_specs()
    {
        var layout = MemoryLayout.Default(Platform.Nes);
        Assert.Equal([new AddressRange(0xC000, 0xFFFF), new AddressRange(0x8000, 0xBFFF)], layout.Program);
        Assert.Equal([new AddressRange(0x0200, 0x07FF)], layout.Variables);
        Assert.Equal([new AddressRange(0x00, 0xFF)], layout.ZeroPage);
        Assert.Null(Platform.Nes.DefaultEncoding);
        Assert.Equal(Tern.Cpu.CpuVariant.Ricoh2A03, Platform.Nes.Cpu);
        Assert.Same(Platform.Nes, Platform.ByName("nes"));
        Assert.True(Platform.Nes.IsVolatile(0x2002, 0x2002));
        Assert.True(Platform.Nes.IsVolatile(0x4016, 0x4016));
        Assert.False(Platform.Nes.IsVolatile(0x4020, 0x4020));
    }

    [Fact]
    public void A_program_runs_with_its_variables_in_ram_and_initial_values_copied_from_rom()
    {
        var r = RunNes(Nrom + """
            var count: u8 = 7
            var table: [u8; 4] = [1, 2, 3, 4]
            var big: [u8; 256]
            var words: [u16; 2] = [1000, 2000]
            fn main() {
                count += table[3]
                big[255] = count
                words[1] += 1
            }
            """);
        Assert.Equal(11, r.U8("count"));
        Assert.Equal([1, 2, 3, 4], Enumerable.Range(0, 4).Select(i => (int)r.U8("table", i)));
        Assert.Equal(11, r.U8("big", 255));
        Assert.Equal(2001, r.U16At("words", 1, 2));
        Assert.Equal(1000, r.U16At("words", 0, 2));
        // Variables are in RAM, code in ROM.
        foreach (var name in new[] { "table", "big", "words" }) Assert.InRange(r.Address(name), 0x0200, 0x07FF);
        Assert.InRange(r.Address("main"), 0xC000, 0xFFF9);
        Assert.Contains("the startup code copies 8 bytes of initial values to RAM", r.Result.Report);
        Assert.Contains("Variables in RAM: ", r.Result.Report);
    }

    [Fact]
    public void Initial_values_with_addresses_are_copied_too()
    {
        var r = RunNes(Nrom + """
            var a: [u8; 3]
            var p: ptr u8 = addr(a) + 1
            fn main() { p[0] = 9 }
            """);
        Assert.Equal(9, r.U8("a", 1));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void The_startup_code_is_the_specs()
    {
        var r = RunNes(Nrom + "var x: u8\nfn main() { x = 1 }");
        Assert.Equal(1, r.U8("x"));
        var listing = r.Result.Listing;
        string[] steps =
        [
            "sei", "cld", "ldx #$40", "stx $4017", "ldx #$FF", "txs", "inx", "stx $2000", "stx $2001", "stx $4010",
            "bit $2002", "vblank1:", "bit $2002", "bpl vblank1", "txa", "clear_ram:", "sta $00,x", "sta $0100,x", "sta $0700,x",
            "inx", "bne clear_ram", "vblank2:", "bit $2002", "bpl vblank2", "jsr main", "halt:", "jmp halt",
        ];
        int at = 0;
        foreach (var step in steps)
        {
            int found = listing.IndexOf(step, at, StringComparison.Ordinal);
            Assert.True(found >= 0, $"'{step}' after offset {at} in\n{listing}");
            at = found + step.Length;
        }
        // The program starts at $C000, and RAM is cleared once: no zeroing of the variables after it.
        Assert.Equal(0xC000, r.Result.Entry);
        Assert.DoesNotContain("clear0", listing);
        Assert.DoesNotContain("\nclear:", listing);
        // Two vblanks: the startup waits about two frames.
        Assert.True(r.Nes!.Frame >= 1);
    }

    [Fact]
    public void The_image_is_ines_with_the_mapper_sizes_and_mirroring()
    {
        var r = Compile(Nrom + "fn main() { }");
        Assert.Equal("NES\u001A"u8.ToArray(), r.File[..4]);
        Assert.Equal(1, r.File[4]);     // 16 KB of PRG ROM: NROM-128
        Assert.Equal(1, r.File[5]);     // 8 KB of CHR ROM
        Assert.Equal(0x01, r.File[6]);  // mapper 0, vertical mirroring
        Assert.Equal(0x00, r.File[7]);
        Assert.Equal(new byte[8], r.File[8..16]);
        Assert.Equal(16 + 0x4000 + 0x2000, r.File.Length);
        // PRG ROM is $C000-$FFFF: the startup at its start, the vectors at its end.
        Assert.Equal(0x78, r.File[16]);
        Assert.Contains("16 KB of PRG ROM at $C000-$FFFF, 8 KB of CHR ROM", r.Report);

        var h = Compile("memory {\n    cartridge nrom horizontal\n}\nfn main() { }");
        Assert.Equal(0x00, h.File[6]);
    }

    [Fact]
    public void A_program_larger_than_16_kb_is_nrom_256()
    {
        const string source = Nrom + """
            data a: [u8; 10000] = repeat(7, 10000)
            data b: [u8; 10000] = repeat(8, 10000)
            var x: u8
            fn main() {
                var p: ptr u8 = addr(a) + 9990
                var q: ptr u8 = addr(b) + 9990
                x = p[9] + q[9]
            }
            """;
        var r = Compile(source);
        Assert.Equal(2, r.File[4]);
        Assert.Equal(16 + 0x8000 + 0x2000, r.File.Length);
        Assert.Contains("32 KB of PRG ROM at $8000-$FFFF", r.Report);
        // The startup code stays at $C000, at offset $4000 of PRG ROM; reset points to it.
        Assert.Equal(0x78, r.File[16 + 0x4000]);
        Assert.Equal([0x00, 0xC0], r.File[(16 + 0x7FFC)..(16 + 0x7FFE)]);
        var run = RunNes(source);
        Assert.Equal(15, run.U8("x"));
        Assert.True(run.Address("a") < 0xC000 || run.Address("b") < 0xC000);
    }

    [Fact]
    public void Reset_goes_to_the_startup_code_and_unset_vectors_to_an_rti()
    {
        var r = Compile(Nrom + "fn main() { }");
        var prg = r.File[16..(16 + 0x4000)];
        int Vector(int at) => prg[at - 0xC000] | prg[at - 0xC000 + 1] << 8;
        Assert.Equal(0xC000, Vector(0xFFFC));
        int rti = Vector(0xFFFA);
        Assert.Equal(rti, Vector(0xFFFE));
        Assert.Equal(0x40, prg[rti - 0xC000]);
    }

    [Fact]
    public void The_program_sets_the_nmi_and_irq_vectors_with_data()
    {
        var r = RunNes(Nrom + """
            var frames: u8
            var irqs: u8
            interrupt fn frame() { frames += 1 }
            asm fn on_irq() {
                rti
            }
            data nmi_vector: u16 @ $FFFA = addr(frame)
            data irq_vector: u16 @ $FFFE = addr(on_irq)
            fn main() {
                asm {
                    lda #$80
                    sta $2000
                }
                while frames < 3 { }
            }
            """);
        Assert.Equal(3, r.U8("frames"));
        var prg = r.Result.File[16..(16 + 0x4000)];
        Assert.Equal(r.Address("frame"), prg[0x3FFA] | prg[0x3FFB] << 8);
        Assert.Equal(r.Address("on_irq"), prg[0x3FFE] | prg[0x3FFF] << 8);
        Assert.Equal(0xC000, prg[0x3FFC] | prg[0x3FFD] << 8);
        Assert.True(r.Nes!.Nmis >= 3);
    }

    [Fact]
    public void Placed_data_in_a_module_is_kept_with_the_handler_it_names()
    {
        var dir = Directory.CreateTempSubdirectory();
        File.WriteAllText(Path.Combine(dir.FullName, "vectors.tern"), """
            var frames: u8
            interrupt fn frame() { frames += 1 }
            data nmi_vector: u16 @ $FFFA = addr(frame)
            """);
        var path = Path.Combine(dir.FullName, "main.tern");
        var r = Run.Program("import vectors\n" + Nrom + "fn main() {\n    asm {\n        lda #$80\n        sta $2000\n    }\n    while vectors.frames < 2 { }\n}", sourcePath: path, platform: Platform.Nes);
        Assert.Equal(2, r.U8("vectors.frames"));
    }

    // ---------- The cartridge line ----------

    [Fact]
    public void A_nes_program_needs_a_cartridge_line()
    {
        var e = Error("fn main() { }");
        Assert.Equal("A NES program is a cartridge image, so its memory block names the board. Write 'memory { cartridge nrom vertical }' (boards: nrom, uxrom; mirroring: vertical or horizontal)", e.Description);
        Assert.Equal((1, 1), (e.Pos.Line, e.Pos.Column));
        var e2 = Error("memory {\n    $0300..=$03FF reserved\n}\nfn main() { }");
        Assert.StartsWith("A NES program is a cartridge image", e2.Description);
        Assert.Equal(1, e2.Pos.Line);
    }

    [Theory]
    [InlineData("cartridge mmc1 vertical", "The NES has no board 'mmc1'. Use one of: nrom, uxrom", 2, 15)]
    [InlineData("cartridge nrom diagonal", "The board 'nrom' has no option 'diagonal'. Use one of: vertical, horizontal", 2, 20)]
    [InlineData("cartridge nrom", "The board 'nrom' is wired for one mirroring, which the line must name. Write 'cartridge nrom vertical' or 'cartridge nrom horizontal'", 2, 15)]
    [InlineData("cartridge nrom vertical horizontal", "The board has one mirroring, and the line names 2. Remove 'horizontal'", 2, 29)]
    [InlineData("cartridge nrom vertical\n    cartridge nrom vertical", "The memory block has a second 'cartridge' line. Remove one: a build is an image for one board", 3, 5)]
    [InlineData("$0300..=$03FF reserved\n    cartridge nrom vertical", "The 'cartridge' line comes before the ranges of the memory block. Move it to the top of the block", 3, 5)]
    [InlineData("cartridge nrom vertical\n    $8000..=$BFFF program", "A 'program' line has no meaning on the NES: the board 'nrom' gives the code areas ($C000-$FFFF, $8000-$BFFF), and the variables are in RAM, $0000-$07FF. Remove the line, or use 'reserved' or 'region' to take a range out of ROM or RAM", 3, 19)]
    [InlineData("cartridge uxrom vertical\n    $0300..=$03FF program", "A 'program' line has no meaning on the NES: the board 'uxrom' gives the code areas ($C000-$FFFF), and the variables are in RAM, $0000-$07FF. Remove the line, or use 'reserved' or 'region' to take a range out of ROM or RAM", 3, 19)]
    [InlineData("cartridge nrom vertical\n    $0200..=$07FF reserved", "No RAM is left for variables: the block's ranges cover all of $0200-$07FF. Leave room for the variables", 1, 1)]
    public void Cartridge_line_errors(string lines, string message, int line, int column)
    {
        var e = Error($"memory {{\n    {lines}\n}}\nfn main() {{ }}");
        Assert.Equal(message, e.Description);
        Assert.Equal((line, column), (e.Pos.Line, e.Pos.Column));
    }

    [Theory]
    // Review M27, finding 1: a cartridge has no disk and no RAM to load an overlay into.
    [InlineData("cartridge nrom vertical\n    $6000..=$7FFF overlay lv",
        "A cartridge has no disk to load an overlay from, and the board 'nrom' no RAM at $6000-$7FFF to load it into. Remove the line, or use a board with banks ('cartridge uxrom') and place modules in a bank area", 3, 19)]
    [InlineData("cartridge uxrom vertical\n    $6000..=$7FFF overlay lv",
        "A cartridge has no disk to load an overlay from, and the board 'uxrom' no RAM at $6000-$7FFF to load it into. Remove the line, and place modules in banks: '$8000..=$BFFF bank lv' and 'import m @ lv[0]'", 3, 19)]
    // Finding 2: a region only in RAM; $0800-$1FFF repeats it.
    [InlineData("cartridge nrom vertical\n    $0800..=$08FF region mirror",
        "A region must lie in RAM, $0000-$07FF; $0800-$08FF does not ($0800-$1FFF repeats the RAM, and the board 'nrom' has no RAM elsewhere). Use addresses in that range", 3, 19)]
    [InlineData("cartridge nrom vertical\n    $6000..=$60FF region extra",
        "A region must lie in RAM, $0000-$07FF; $6000-$60FF does not ($0800-$1FFF repeats the RAM, and the board 'nrom' has no RAM elsewhere). Use addresses in that range", 3, 19)]
    [InlineData("cartridge nrom vertical\n    $6000..=$60FF reserved",
        "A reserved range must lie in RAM, $0000-$07FF, or in the board's ROM, $8000-$FFFF; $6000-$60FF does not. Use addresses in one of them", 3, 19)]
    // Suggestion 6: the board's ROM, not $8000-$07FF.
    [InlineData("cartridge nrom vertical\n    $8000..=$FFFF reserved",
        "No program area is left: the reserved ranges cover all of the board 'nrom's ROM for the program, $C000-$FFFF, $8000-$BFFF. Leave room for the program", 3, 19)]
    public void Memory_a_cartridge_does_not_have(string lines, string message, int line, int column)
    {
        var e = Error($"memory {{\n    {lines}\n}}\nfn main() {{ }}");
        Assert.Equal(message, e.Description);
        Assert.Equal((line, column), (e.Pos.Line, e.Pos.Column));
    }

    [Fact]
    public void A_region_and_reserved_ranges_in_ram_and_rom_work()
    {
        var r = RunNes("memory {\n    cartridge nrom vertical\n    $0700..=$07FF region buffer\n    $E000..=$E0FF reserved\n}\nvar m: [u8; 4] @ buffer\nvar score: u8 @ zp\nfn main() {\n    score = 7\n    m[0] = 99\n}");
        Assert.Equal(7, r.U8("score"));
        Assert.Equal(99, r.U8("m"));
        Assert.Equal(0x0700, r.Address("m"));
    }

    [Fact]
    public void Nrom_256_holds_a_table_larger_than_16_kb()
    {
        // Review M27, finding 4: the program is laid out again in one area, $8000-$FFFF.
        var r = RunNes(Nrom + """
            data big: [u8; 17000] = [i % 251 for i in 0..17000]
            var x: u8
            var y: u8
            fn main() {
                var p: ptr u8 = addr(big) + 16990
                x = p[9]
                p = addr(big) + 200
                y = p[51]
            }
            """);
        Assert.Equal(16999 % 251, r.U8("x"));
        Assert.Equal(251 % 251, r.U8("y"));
        Assert.Equal(2, r.Result.File[4]);
        Assert.Contains("32 KB of PRG ROM at $8000-$FFFF", r.Result.Report);
        // The startup code then starts the ROM, and reset points to it.
        Assert.Equal(0x8000, r.Result.Entry);
        Assert.Equal([0x00, 0x80], r.Result.File[(16 + 0x7FFC)..(16 + 0x7FFE)]);
    }

    [Fact]
    public void Running_out_of_ram_names_the_ram()
    {
        // Review M27, suggestion 5.
        var e = Error(Nrom + "var a: [u8; 256]\nvar b: [u8; 256]\nvar c: [u8; 256]\nvar d: [u8; 256]\nvar f: [u8; 256]\nvar g: [u8; 256]\nvar h: [u8; 256]\n" +
            "fn main() { a[1] = 1; b[1] = 1; c[1] = 1; d[1] = 1; f[1] = 1; g[1] = 1; h[1] = 1 }");
        Assert.Equal("The variables need 1792 bytes, but the RAM for variables, $0200-$07FF (1536 bytes), has room for 1536. " +
            "Make arrays smaller, use 'data' for tables that never change (they stay in ROM), or put small variables in zero page with '@ zp'", e.Description);
    }

    [Fact]
    public void The_report_lists_ram_apart_from_the_program_areas()
    {
        // Review M27, suggestion 7.
        var r = Compile(Nrom + "var big: [u8; 100]\nfn main() { big[1] = 2 }");
        Assert.Contains("Variables in RAM: 100 bytes", r.Report);
        Assert.DoesNotContain("$0200-$07FF:", r.Report);
    }

    [Fact]
    public void Tern_run_has_no_ram_at_4020_to_7fff()
    {
        // Review M27, suggestion 10: NROM and UxROM have no PRG RAM.
        var r = RunNes(Nrom + "volatile var cart: u8 @ $6000\nvar seen: u8\nfn main() {\n    cart = 5\n    seen = cart\n}");
        Assert.Equal(0, r.U8("seen"));
    }

    [Fact]
    public void Other_targets_have_no_cartridges_yet()
    {
        var c64 = Assert.Throws<CompileError>(() => TernCompiler.Compile("memory {\n    cartridge nrom vertical\n}\nfn main() { }", Platform.C64));
        Assert.Equal("'cartridge' is not supported yet for the c64: cartridge images are built only for the NES so far. Remove the 'cartridge' line", c64.Description);
        var apple = Assert.Throws<CompileError>(() => TernCompiler.Compile("memory {\n    cartridge nrom vertical\n}\nfn main() { }", Platform.Apple2));
        Assert.Equal("The Apple II has no cartridges. Remove the 'cartridge' line", apple.Description);
        var chr = Assert.Throws<CompileError>(() => TernCompiler.Compile("memory {\n    $0000..=$1FFF chr tiles\n}\nfn main() { }", Platform.C64));
        Assert.Equal("A 'chr' area is part of a cartridge board, which the c64 does not have. Remove the line", chr.Description);
    }

    [Fact]
    public void Cartridge_is_a_word_only_in_the_memory_block()
    {
        var r = RunNes(Nrom + "var cartridge: u8\nfn main() { cartridge = 3 }");
        Assert.Equal(3, r.U8("cartridge"));
    }

    // ---------- Vectors and data ----------

    [Fact]
    public void Data_at_the_reset_vector_is_an_error()
    {
        var e = Error(Nrom + "data reset: u16 @ $FFFC = 0\nfn main() { }");
        Assert.Equal("'reset' at $FFFC-$FFFD covers the reset vector ($FFFC), which belongs to the startup code: reset sets up the machine and then calls main. Remove the data, and start the program in main", e.Description);
        var e2 = Error(Nrom + "data v: [u16; 3] @ $FFFA = [0, 0, 0]\nfn main() { }");
        Assert.StartsWith("'v' at $FFFA-$FFFF covers the reset vector", e2.Description);
    }

    [Fact]
    public void A_vector_must_be_an_interrupt_fn_or_an_asm_fn()
    {
        var plain = Error(Nrom + "fn frame() { }\ndata nmi: u16 @ $FFFA = addr(frame)\nfn main() { }");
        Assert.Equal("The NMI vector ($FFFA) must be the address of an interrupt fn or an asm fn, which return with rti; 'frame' is a plain fn, which returns with rts. Declare it 'interrupt fn frame'", plain.Description);
        var number = Error(Nrom + "data irq: u16 @ $FFFE = $C000\nfn main() { }");
        Assert.Equal("The IRQ vector ($FFFE) must be the address of an interrupt fn or an asm fn, which return with rti. Write addr() of an interrupt fn", number.Description);
        var offset = Error(Nrom + "interrupt fn frame() { }\ndata nmi: u16 @ $FFFA = addr(frame) + 1\nfn main() { }");
        Assert.StartsWith("The NMI vector ($FFFA) must be the address of an interrupt fn or an asm fn", offset.Description);
    }

    [Fact]
    public void Data_outside_rom_is_an_error()
    {
        var e = Error("memory {\n    cartridge nrom vertical\n    $0300..=$03FF reserved\n}\ndata d: [u8; 2] @ $0300 = [1, 2]\nfn main() { }");
        Assert.Equal("'d' at $0300-$0301 is outside the cartridge's ROM ($8000-$FFFF), and the image cannot put bytes in RAM. Make it a 'var' with an initial value and no fixed address, which the startup code copies to RAM, or place it in ROM", e.Description);
    }

    [Fact]
    public void A_single_value_of_data_needs_a_placement()
    {
        var e = Error(Nrom + "data d: u16 = 5\nfn main() { }");
        Assert.Equal("Data must be an array, a string or a struct, unless it is placed with '@'. Use 'const' for a single value", e.Description);
        var r = RunNes(Nrom + "data d: u16 @ $E000 = 1234\nvar x: u16\nfn main() { x = d }");
        Assert.Equal(1234, r.U16("x"));
    }

    // ---------- Tile data: chr areas ----------

    private const string Tiles = "memory {\n    cartridge nrom vertical\n    $0000..=$1FFF chr tiles\n}\n";

    [Fact]
    public void Data_in_a_chr_area_is_in_chr_rom_at_its_ppu_address()
    {
        var r = RunNes(Tiles + """
            data bg: [u8; _] @ tiles = [1, 2, 3]
            data font: [u8; _] @ tiles + $1000 = [i * 2 for i in 0..16]
            data more: [u8; 2] @ tiles = [7, 8]
            var at: u16
            var n: u8
            fn main() {
                at = addr(font)
                n = len(font)
            }
            """);
        var chr = r.Result.File[(16 + 0x4000)..];
        Assert.Equal(0x2000, chr.Length);
        Assert.Equal([1, 2, 3, 7, 8, 0], chr[..6]);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)(i * 2)), chr[0x1000..0x1010]);
        Assert.Equal(0, chr[0x1010]);
        Assert.Equal(0x1000, r.U16("at"));
        Assert.Equal(16, r.U8("n"));
        Assert.Equal(0x0003, r.Result.Symbols["more"]);
        // The PPU sees it: tern run loads CHR ROM into the PPU's memory.
        Assert.Equal(2, r.Nes!.Ppu[0x1001]);
        Assert.Contains("Chr area tiles $0000-$1FFF: 21 of 8192 bytes used", r.Result.Report);
        // Tile data takes no CPU memory and is not in PRG ROM.
        Assert.DoesNotContain(r.Result.Sections, s => s.Name is "bg" or "font");
    }

    /// <summary>Decision 2026-11-15 (review code-quality-semantics 3): a font module places its tiles in the main file's chr area.</summary>
    [Fact]
    public void A_module_places_tile_data_in_a_chr_area_of_the_main_file()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "font.tern"), "data glyphs: [u8; 4] @ tiles + $10 = [1, 2, 3, 4]\nfn base() -> u16 { return addr(glyphs) }\n");
            var r = Run.Program("import font\n" + Tiles + "var at: u16\nfn main() { at = font.base() }",
                sourcePath: Path.Combine(dir.FullName, "main.tern"), platform: Platform.Nes);
            Assert.Equal(0x0010, r.U16("at"));
            Assert.Equal([1, 2, 3, 4], r.Result.File[(16 + 0x4000 + 0x10)..(16 + 0x4000 + 0x14)]);
            Assert.Equal(3, r.Nes!.Ppu[0x12]);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void A_chr_area_may_be_part_of_the_pattern_tables()
    {
        var r = Compile("memory {\n    cartridge nrom vertical\n    $1000..=$1FFF chr sprites\n}\ndata ship: [u8; 2] @ sprites = [5, 6]\nfn main() { }");
        Assert.Equal(0x1000, r.Symbols["ship"]);
        Assert.Equal(5, r.File[16 + 0x4000 + 0x1000]);
    }

    [Theory]
    [InlineData("fn main() { var x: u8 = font[1] }", "'font' is tile data in the chr area 'tiles', in the PPU's memory, which the CPU cannot read. Use addr(font) for its PPU address, or len(font)", 6, 25)]
    [InlineData("var buffer: [u8; 4] @ tiles\nfn main() { }", "'buffer' is a variable, but the chr area 'tiles' is ROM the PPU reads, so it holds only tile data. Declare it with 'data'", 6, 1)]
    [InlineData("data big: [u8; 2] @ tiles + $1FFF = [1, 2]\nfn main() { }", "'big' (2 bytes) at offset $1FFF does not fit in 'tiles' ($0000-$1FFF, 8192 bytes). Use an offset up to $1FFE", 6, 29)]
    [InlineData("data a: [u8; 2] @ tiles + $FF2 = [1, 2]\nfn main() { }", "'a' at $0FF2-$0FF3 overlaps 'font' at $0FF0-$0FF3 in 'tiles'. Move one of them, with '@ tiles + offset'", 6, 1)]
    public void Chr_data_errors(string rest, string message, int line, int column)
    {
        var e = Error(Tiles + "data font: [u8; 4] @ tiles + $FF0 = [1, 2, 3, 4]\n" + rest);
        Assert.Equal(message, e.Description);
        Assert.Equal((line, column), (e.Pos.Line, e.Pos.Column));
    }

    [Theory]
    [InlineData("cartridge uxrom vertical\n    $0000..=$1FFF chr tiles", "The board 'uxrom' has CHR RAM, empty at power-on, not CHR ROM. Remove the line, make the tiles ordinary data and copy them to the PPU with nes.ppu_copy", 3, 19)]
    [InlineData("cartridge nrom vertical\n    $0000..=$2FFF chr tiles", "A chr area lies in the PPU's pattern tables, $0000-$1FFF; $0000-$2FFF does not. Use addresses in that range", 3, 19)]
    [InlineData("cartridge nrom vertical\n    $0000..=$0FFF chr a\n    $1000..=$1FFF chr b", "The board 'nrom' has one CHR ROM, and the block already declares it as 'a'. Remove the line, or join the two", 4, 19)]
    public void Chr_area_errors(string lines, string message, int line, int column)
    {
        var e = Error($"memory {{\n    {lines}\n}}\nfn main() {{ }}");
        Assert.Equal(message, e.Description);
        Assert.Equal((line, column), (e.Pos.Line, e.Pos.Column));
    }

    [Fact]
    public void The_address_of_tile_data_is_a_u16_not_a_pointer()
    {
        var e = Error(Tiles + "data font: [u8; 4] @ tiles = [1, 2, 3, 4]\nvar p: ptr u8\nfn main() { p = addr(font) }");
        Assert.StartsWith("Expected ptr u8, found u16", e.Description);
    }

    [Fact]
    public void A_chr_area_takes_no_cpu_memory()
    {
        // $0000-$1FFF in the PPU's addresses: zero page and RAM stay the program's.
        var r = RunNes(Tiles + "var x: u8\nvar big: [u8; 200]\nfn main() { x = 5; big[199] = 6 }");
        Assert.Equal(5, r.U8("x"));
        Assert.Equal(6, r.U8("big", 199));
        Assert.Equal(0x00, r.Address("x"));
    }

    // ---------- Encoding ----------

    [Fact]
    public void The_nes_has_no_default_encoding()
    {
        var e = Error(Nrom + "data t = \"HI\"\nfn main() { }");
        Assert.Equal("The NES has no default encoding: its text is tile numbers, which each game chooses (spec 2.1). Write a 'default encoding' line in the main file, or write an encoding's prefix before the quote", e.Description);
        var r = RunNes(Nrom + "encoding tiles { 'A'..='Z' = 10 }\ndefault encoding tiles\ndata t = \"HI\"\nvar x: u8\nfn main() { x = t[1] }");
        Assert.Equal(10 + 'I' - 'A', r.U8("x"));
    }

    // ---------- Labels ----------

    [Fact]
    public void Labels_are_in_mesens_format()
    {
        var r = Compile(Nrom + "var x: u8\nvar big: [u8; 4] = [1, 2, 3, 4]\nfn main() { x = big[1] }");
        var lines = r.Labels.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains($"NesInternalRam:{r.Symbols["x"]:X4}:x", lines);
        Assert.Contains($"NesInternalRam:{r.Symbols["big"]:X4}:big", lines);
        Assert.Contains($"NesPrgRom:{r.Symbols["main"] - 0xC000:X4}:main", lines);
        Assert.Equal(".mlb", Platform.Nes.Labels.Extension);
    }

    // Review code-quality-syntax 17: padding before aligned data in PRG ROM is $FF, erased ROM (spec 12.2).
    [Fact]
    public void Alignment_padding_in_rom_is_ff()
    {
        var r = Compile(Nrom + "data a: [u8; 3] = [1, 2, 3]\ndata b: [u8; 4] @ align(256) = [9, 9, 9, 9]\nvar out: u8\nfn main() { out = a[0] + b[0] }");
        int aEnd = r.Symbols["a"] + 3, b = r.Symbols["b"];
        Assert.Equal(0, b & 0xFF);
        Assert.True(b - aEnd > 0);
        Assert.All(r.File[(16 + aEnd - 0xC000)..(16 + b - 0xC000)], x => Assert.Equal(0xFF, x));
        Assert.Equal([9, 9, 9, 9], r.File[(16 + b - 0xC000)..(16 + b - 0xC000 + 4)]);
    }

    // Review code-quality-syntax 5: tile data is labelled in CHR ROM, not as RAM, and --dump does not read CPU memory for it.
    [Fact]
    public void Tile_data_is_labelled_in_chr_rom()
    {
        var r = Compile(Tiles + "data bg: [u8; 16] @ tiles + $20 = repeat($11, 16)\nvar out: u8\nfn main() { out = 1 }");
        var lines = r.Labels.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("NesChrRom:0020:bg", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("NesInternalRam:") && l.EndsWith(":bg"));
        Assert.Contains($"NesInternalRam:{r.Symbols["out"]:X4}:out", lines);
        Assert.True(r.Variables["bg"].InChr);
        var e = Assert.Throws<FormatException>(() => Tools.MemoryDump.Parse("bg", r));
        Assert.Equal("'bg' is tile data in a chr area, at $0020 in the PPU's memory, and --dump shows the CPU's memory", e.Message);
    }
}
