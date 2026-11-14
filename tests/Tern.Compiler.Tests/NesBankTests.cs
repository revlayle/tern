using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// Bank switching on UxROM (spec 8.5, 12.2, M27): bank areas, <c>import m @ area[n]</c>, the image's banks
/// with the fixed bank last, <c>uxrom.select</c>, and the rules between banks.
/// </summary>
public class NesBankTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tern-banks-");

    public void Dispose() => _dir.Delete(recursive: true);

    private const string Memory = "memory {\n    cartridge uxrom vertical\n    $8000..=$BFFF bank levels\n}\n";

    private const string Level1 = """
        data MARK: [u8; 3] = [10, 20, 30]
        var count: u8 = 5
        fn run() -> u8 using A { count += 1; return MARK[1] + count }
        """;
    private const string Level2 = """
        import music
        data MARK: [u8; 2] = [100, 7]
        fn run() -> u8 using A { return MARK[1] + music.tune() }
        """;
    private const string Music = "fn tune() -> u8 using A { return 40 }\n";

    private string Write(string main, string level1 = Level1, string level2 = Level2, string music = Music)
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "level1.tern"), level1);
        File.WriteAllText(Path.Combine(_dir.FullName, "level2.tern"), level2);
        File.WriteAllText(Path.Combine(_dir.FullName, "music.tern"), music);
        var path = Path.Combine(_dir.FullName, "main.tern");
        File.WriteAllText(path, main);
        return path;
    }

    private Run RunMain(string main, string level1 = Level1, string level2 = Level2)
    {
        var path = Write(main, level1, level2);
        return Run.Program(main, sourcePath: path, platform: Platform.Nes);
    }

    private CompileError Error(string main, string level1 = Level1, string level2 = Level2)
    {
        var path = Write(main, level1, level2);
        return Assert.Throws<CompileError>(() => TernCompiler.Compile(main, Platform.Nes, sourcePath: path));
    }

    private const string TwoBanks = """
        import uxrom
        import level1 @ levels[0]
        import level2 @ levels[LEVEL2]
        import music @ levels[LEVEL2]

        """ + Memory + """
        const LEVEL2 = 1
        var a: u8
        var b: u8
        fn main() {
            uxrom.select(0)
            a = level1.run()
            uxrom.select(LEVEL2)
            b = level2.run()
        }
        """;

    [Fact]
    public void Modules_in_two_banks_run_once_their_bank_is_selected()
    {
        var r = RunMain(TwoBanks);
        Assert.Equal(26, r.U8("a"));            // 20 + 6
        Assert.Equal(47, r.U8("b"));            // 7 + 40
        Assert.Equal(1, r.Nes!.SelectedBank);
        Assert.Equal(6, r.U8("level1.count"));
        // Code and data of the banks at $8000-$BFFF; their variables in RAM; the resident program in the fixed bank.
        foreach (var name in new[] { "level1.run", "level1.MARK", "level2.run", "level2.MARK", "music.tune" })
            Assert.InRange(r.Address(name), 0x8000, 0xBFFF);
        Assert.InRange(r.Address("level1.count"), 0x0000, 0x07FF);
        Assert.InRange(r.Address("main"), 0xC000, 0xFFF9);
        Assert.InRange(r.Address("uxrom.BANKS"), 0xC000, 0xFFF9);
        // Both banks start at $8000; modules sharing a bank follow each other.
        Assert.True(r.Address("level1.run") == 0x8000 || r.Address("level1.MARK") == 0x8000);
        Assert.NotEqual(r.Address("level2.run"), r.Address("music.tune"));
    }

    [Fact]
    public void The_image_has_the_banks_in_order_and_the_fixed_bank_last()
    {
        var path = Write(TwoBanks);
        var r = TernCompiler.Compile(TwoBanks, Platform.Nes, sourcePath: path);
        // Banks 0 and 1 and the fixed bank: 3, rounded up to 4 (64 KB, UNROM).
        Assert.Equal(4, r.File[4]);
        Assert.Equal(0, r.File[5]);             // CHR RAM
        Assert.Equal(0x21, r.File[6]);          // mapper 2, vertical mirroring
        Assert.Equal(16 + 4 * 0x4000, r.File.Length);
        byte[] Bank(int n) => r.File[(16 + n * 0x4000)..(16 + (n + 1) * 0x4000)];
        Assert.Equal([10, 20, 30], Bank(0)[(r.Symbols["level1.MARK"] - 0x8000)..(r.Symbols["level1.MARK"] - 0x8000 + 3)]);
        Assert.Equal([100, 7], Bank(1)[(r.Symbols["level2.MARK"] - 0x8000)..(r.Symbols["level2.MARK"] - 0x8000 + 2)]);
        Assert.All(Bank(2), b => Assert.Equal(0xFF, b));
        var fixedBank = Bank(3);
        Assert.Equal(0x78, fixedBank[0]);       // sei: the startup code at $C000
        Assert.Equal([0x00, 0xC0], fixedBank[0x3FFC..0x3FFE]);
        // uxrom.select writes n over a byte holding n (bus conflicts).
        int table = r.Symbols["uxrom.BANKS"] - 0xC000;
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i), fixedBank[table..(table + 16)]);
        Assert.Contains("Cartridge: uxrom (mapper 2), vertical mirroring, 64 KB of PRG ROM in 4 banks of 16 KB, the last fixed at $C000-$FFFF, CHR RAM", r.Report);
        Assert.Contains("Bank area levels $8000-$BFFF:", r.Report);
        Assert.Contains("  bank 0: level1, ", r.Report);
        Assert.Contains("  bank 1: level2, music, ", r.Report);
        // Mesen labels: a bank's code at its offset in PRG ROM, the fixed bank's in the last 16 KB.
        var lines = r.Labels.Split('\n', StringSplitOptions.TrimEntries);
        Assert.Contains($"NesPrgRom:{0x4000 + r.Symbols["level2.run"] - 0x8000:X4}:level2_run", lines);
        Assert.Contains($"NesPrgRom:{3 * 0x4000 + r.Symbols["main"] - 0xC000:X4}:main", lines);
    }

    [Fact]
    public void The_startup_selects_bank_0_by_writing_0_over_a_0()
    {
        var path = Write(TwoBanks);
        var r = TernCompiler.Compile(TwoBanks, Platform.Nes, sourcePath: path);
        var listing = r.Listing;
        int select = listing.IndexOf("stx bank_zero", StringComparison.Ordinal);
        Assert.True(select > 0 && select < listing.IndexOf("vblank1:", StringComparison.Ordinal));
        Assert.Contains("bank_zero:", listing);
        Assert.Matches(@"bank_zero:\r?\n\$[0-9A-F]{4}  00", listing);
    }

    [Fact]
    public void Uxrom_without_banks_is_one_bank()
    {
        const string main = "memory {\n    cartridge uxrom horizontal\n}\nvar x: u8\nfn main() { x = 3 }";
        var r = Run.Program(main, platform: Platform.Nes);
        Assert.Equal(3, r.U8("x"));
        Assert.Equal(1, r.Result.File[4]);
        Assert.Equal(0x20, r.Result.File[6]);
    }

    [Fact]
    public void A_module_of_another_bank_may_use_resident_variables_of_a_bank_module()
    {
        // A bank's variables are in RAM, so they are there whichever bank is selected.
        var r = RunMain(TwoBanks.Replace("b = level2.run()", "b = level2.run() + level1.count"),
            level2: "import level1\n" + Level2.Replace("return MARK[1] + music.tune()", "return MARK[1] + music.tune() + level1.count"));
        Assert.Equal(47 + 6 + 6, r.U8("b"));
    }

    [Fact]
    public void Bank_is_a_word_only_in_the_memory_block()
    {
        var r = Run.Program("var bank: u8\nfn main() { bank = 2 }");
        Assert.Equal(2, r.U8("bank"));
    }

    [Fact]
    public void An_import_that_places_a_module_in_a_bank_is_not_warned()
    {
        // music is only placed by the main file; level2 uses it.
        var path = Write(TwoBanks);
        var r = TernCompiler.Compile(TwoBanks, Platform.Nes, sourcePath: path);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Banks_implement_an_interface_and_resident_code_calls_the_selected_one()
    {
        // spec 8.5: implements works for banks as for overlays; the table is at the same address in each bank.
        File.WriteAllText(Path.Combine(_dir.FullName, "level_api.tern"), "fn value() -> u8 using A @ $8000\n");
        var r = RunMain("""
            import uxrom
            import level_api
            import level1 @ levels[0] implements level_api
            import level2 @ levels[1] implements level_api

            """ + Memory + """
            var a: u8
            var b: u8
            fn main() {
                uxrom.select(0)
                a = level_api.value()
                uxrom.select(1)
                b = level_api.value()
            }
            """,
            level1: "fn value() -> u8 using A { return 11 }\n",
            level2: "fn value() -> u8 using A { return 22 }\n");
        Assert.Equal(11, r.U8("a"));
        Assert.Equal(22, r.U8("b"));
        var file = r.Result.File;
        Assert.Equal(0x4C, file[16]);                   // jmp at $8000 in bank 0
        Assert.Equal(0x4C, file[16 + 0x4000]);          // and in bank 1
    }

    // ---------- Errors ----------

    [Theory]
    // Decision 2026-11-14 (question 169): an interrupt can come with any bank selected.
    [InlineData("$FFFA", "NMI", "interrupt fn on_frame() { }")]
    [InlineData("$FFFE", "IRQ", "asm fn on_frame() { rti }")]
    public void An_interrupt_vector_into_a_bank_is_an_error(string address, string vector, string handler)
    {
        var main = "import level1 @ levels[2]\n" + Memory + $"data v: u16 @ {address} = addr(level1.on_frame)\nfn main() {{ }}\n";
        var e = Error(main, level1: handler + "\n");
        Assert.Equal($"The {vector} vector ({address}) points to 'level1.on_frame' in bank 2 of the bank area 'levels', but an interrupt can come with any bank selected. " +
            "Call it from a resident interrupt fn through an interface its module implements, or import level1 without '@ levels[2]'", e.Description);
        Assert.Equal(6, e.Pos.Line);
    }

    [Fact]
    public void Two_modules_in_one_bank_cannot_implement_the_same_interface()
    {
        // Decision 2026-11-14 (question 166): their tables would be on the same bytes.
        File.WriteAllText(Path.Combine(_dir.FullName, "level_api.tern"), "fn value() -> u8 using A @ $8000\n");
        var main = """
            import level_api
            import level1 @ levels[1] implements level_api
            import level2 @ levels[1] implements level_api

            """ + Memory + "fn main() { _ = level_api.value() }\n";
        var e = Error(main,
            level1: "fn value() -> u8 using A { return 11 }\n",
            level2: "fn value() -> u8 using A { return 22 }\n");
        Assert.Equal("level1 and level2 both implement level_api in bank 1 of the bank area 'levels', so their tables would be on the same bytes. Put them in different banks", e.Description);
        Assert.Equal((3, 38), (e.Pos.Line, e.Pos.Column));
    }

    [Fact]
    public void A_call_and_a_data_use_between_banks_are_errors()
    {
        var call = Error(TwoBanks, level1: "import level2\nfn run() -> u8 using A { return level2.run() }\n");
        Assert.Equal("'level1.run' calls 'level2.run', but the modules level1 and level2 are in banks 0 and 1 of the bank area 'levels', and only one bank is selected at a time. " +
            "Put both modules in the same bank, or call between them from resident code", call.Description);
        var data = Error(TwoBanks, level1: "import level2\nfn run() -> u8 using A { return level2.MARK[0] }\n");
        Assert.Equal("'level2.MARK' belongs to level2, which is in banks 1 and 0 of the bank area 'levels' as level1. They are never loaded together and share their memory. " +
            "Keep shared state in a resident module or the main file", data.Description);
    }

    [Theory]
    [InlineData("@ levels", "'levels' is a bank area, so the import names the bank the module goes in. Write '@ levels[0]', or another bank's number")]
    [InlineData("@ levels[15]", "Bank 15 is the fixed bank when the image has 16 banks, and it holds the resident program at $C000-$FFFF. Use a bank from 0 to 14")]
    [InlineData("@ levels[16]", "The board 'uxrom' has at most 16 banks, the last one fixed, so a module goes in bank 0 to 14, not 16. Use a number in that range")]
    [InlineData("@ levels[n]", "The bank's number must be known at compile time. Use a number or a constant")]
    public void Bank_number_errors(string placement, string message)
    {
        var e = Error($"import level1 {placement}\n" + Memory + "var n: u8\nfn main() { }");
        Assert.Equal(message, e.Description);
    }

    [Theory]
    [InlineData("cartridge nrom vertical\n    $8000..=$BFFF bank levels", "The board 'nrom' has no banks. Remove the line, or name a board with banks, such as 'cartridge uxrom'")]
    [InlineData("cartridge uxrom vertical\n    $8000..=$9FFF bank levels", "The board 'uxrom' switches banks at $8000-$BFFF, not at $8000-$9FFF. Write '$8000..=$BFFF bank levels'")]
    public void Bank_area_errors(string lines, string message)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile($"memory {{\n    {lines}\n}}\nfn main() {{ }}", Platform.Nes));
        Assert.Equal(message, e.Description);
    }

    [Fact]
    public void A_rejected_bank_line_brings_no_error_about_the_area()
    {
        // Review M27, suggestion 9: only the bank line's error, not "no overlay area 'w'".
        var e = Error("import level1 @ w[0]\nmemory {\n    cartridge nrom vertical\n    $8000..=$BFFF bank w\n}\nfn main() { }");
        Assert.Equal("The board 'nrom' has no banks. Remove the line, or name a board with banks, such as 'cartridge uxrom'", e.Description);
        Assert.Single(e.All);
    }

    [Fact]
    public void An_overlay_area_takes_no_bank_number()
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(
            "import level1 @ lv[0]\nmemory {\n $0801..=$5FFF program\n $6000..=$6FFF overlay lv\n}\nfn main() { }", Platform.C64, sourcePath: Write("")));
        Assert.Equal("'lv' is an overlay area, which holds one module at a time, loaded from disk; it has no banks. Remove '[...]'", e.Description);
    }

    [Fact]
    public void The_uxrom_module_on_another_board_is_an_error()
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("import uxrom\nmemory {\n    cartridge nrom vertical\n}\nfn main() { uxrom.select(1) }", Platform.Nes));
        Assert.Equal("'uxrom' selects banks on the board 'uxrom', but the memory block names 'nrom'. Remove the import, or write 'cartridge uxrom ...' in the memory block", e.Description);
        Assert.Equal((1, 8), (e.Pos.Line, e.Pos.Column));
    }

    [Fact]
    public void A_bank_that_does_not_fit_lists_its_modules()
    {
        var e = Error("import level1 @ levels[0]\nimport level2 @ levels[0]\nimport music\n" + Memory + "var w: u16\nfn main() { w = level1.run() + level2.run() }",
            level1: "data BIG: [u8; 10000] = repeat(1, 10000)\nfn run() -> u16 { return addr(BIG) as u16 }\n",
            level2: "data BIG: [u8; 7000] = repeat(2, 7000)\nfn run() -> u16 { return addr(BIG) as u16 }\n");
        Assert.StartsWith("Bank 0 of 'levels' needs 17", e.Description);
        Assert.Contains("but a bank has 16384: ", e.Description);
        Assert.Contains("level1 (100", e.Description);
        Assert.Contains("level2 (70", e.Description);
        Assert.EndsWith("Move a module to another bank, or make one smaller", e.Description);
    }
}
