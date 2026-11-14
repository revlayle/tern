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
    [InlineData("cartridge nrom vertical\n    $8000..=$BFFF program", "'program' lines are not supported yet on the NES: the board 'nrom' gives the program's ROM ($C000-$FFFF, $8000-$BFFF). Remove the line, or use 'reserved' for memory the program keeps for itself", 3, 19)]
    [InlineData("cartridge nrom vertical\n    $0200..=$07FF reserved", "No RAM is left for variables: the block's ranges cover all of $0200-$07FF. Leave room for the variables", 1, 1)]
    public void Cartridge_line_errors(string lines, string message, int line, int column)
    {
        var e = Error($"memory {{\n    {lines}\n}}\nfn main() {{ }}");
        Assert.Equal(message, e.Description);
        Assert.Equal((line, column), (e.Pos.Line, e.Pos.Column));
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
        Assert.Equal("'d' at $0300-$0301 is outside the cartridge's ROM ($8000-$FFFF), and the image cannot put bytes in RAM. Make it a 'var' with an initial value, which the startup code copies to RAM, or place it in ROM", e.Description);
    }

    [Fact]
    public void A_single_value_of_data_needs_a_placement()
    {
        var e = Error(Nrom + "data d: u16 = 5\nfn main() { }");
        Assert.Equal("Data must be an array, a string or a struct, unless it is placed with '@'. Use 'const' for a single value", e.Description);
        var r = RunNes(Nrom + "data d: u16 @ $E000 = 1234\nvar x: u16\nfn main() { x = d }");
        Assert.Equal(1234, r.U16("x"));
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
}
