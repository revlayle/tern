using Tern.Compiler.Syntax;
using Tern.Compiler.Semantics;
using Tern.Compiler.Tools;

namespace Tern.Compiler.Tests;

/// <summary>The Apple II platform (spec 12.2, M26): memory map, encoding, volatile areas, the system file and its startup code.</summary>
public class Apple2Tests
{
    private static CompileResult Compile(string source) => TernCompiler.Compile(source, Platform.Apple2);

    private static Run RunApple2(string source) => Run.Program(source, platform: Platform.Apple2);

    [Fact]
    public void The_memory_map_is_the_specs()
    {
        var layout = MemoryLayout.Default(Platform.Apple2);
        Assert.Equal([new AddressRange(0x2000, 0xBEFF)], layout.Program);
        Assert.Equal([new AddressRange(0x50, 0xFF), new AddressRange(0x00, 0x1F)], layout.ZeroPage);
        Assert.Same(CharEncoding.Apple2, Platform.Apple2.DefaultEncoding);
        Assert.Same(Platform.Apple2, Platform.ByName("apple2"));
    }

    [Fact]
    public void Zero_page_is_filled_from_50_and_leaves_20_to_4f_alone()
    {
        // 183 bytes: 176 fill $50-$FF, the rest go to $00 on. Filled from $00, the pointer would cross $1F.
        var r = Compile("""
            var a: [u8; 31] @ zp
            var p: ptr u8 @ zp
            var b: [u8; 143] @ zp
            var c: [u8; 7] @ zp
            fn main() {
                a[1] = 1
                b[1] = 2
                c[1] = 3
                p[0] = 4
            }
            """);
        Assert.Equal(0x50, r.Symbols["a"]);
        Assert.Equal(0x6F, r.Symbols["p"]);
        Assert.Equal(0x00, r.Symbols["c"]);
    }

    [Fact]
    public void A_program_is_a_system_file_that_loads_and_starts_at_2000()
    {
        var r = RunApple2("""
            var n: u8
            var big: [u8; 200]
            fn main() {
                n = 5
                big[199] = n
            }
            """);
        Assert.Equal(5, r.U8("n"));
        Assert.Equal(5, r.U8("big", 199));
        Assert.Equal(0x2000, r.Result.Entry);
        Assert.Equal(0x2000, r.Result.LoadAddress);
        // One block, the program's bytes and nothing else.
        Assert.Equal(r.Result.Image, r.Result.File);
        Assert.True(r.Address("big") >= 0x2000 + r.Result.File.Length);
        Assert.Contains("System file: ", r.Result.Report);
        Assert.DoesNotContain("copies", r.Result.Report);
    }

    [Fact]
    public void The_startup_sets_up_40_column_text_and_points_reset_at_a_routine_that_quits()
    {
        var r = RunApple2("fn main() { }");
        var listing = r.Result.Listing;
        // Ctrl-U through COUT when the 80-column firmware prints, then SETNORM, INIT, SETVID, SETKBD, HOME.
        Assert.Contains("lda $37", listing);
        Assert.Contains("cmp #$C3", listing);
        Assert.Contains("lda #$95", listing);
        int[] calls = [0xFDED, 0xFE84, 0xFB2F, 0xFE93, 0xFE89, 0xFC58];
        var order = calls.Select(c => listing.IndexOf($"jsr ${c:X4}", StringComparison.Ordinal)).ToList();
        Assert.All(order, i => Assert.True(i > 0));
        Assert.Equal(order.Order(), order);
        int quit = r.Ram.Bytes[0x3F2] | r.Ram.Bytes[0x3F3] << 8;
        Assert.Equal(r.Ram.Bytes[0x3F3] ^ 0xA5, r.Ram.Bytes[0x3F4]);
        // The routine: MLI CLOSE (all files), the power-up byte made invalid, MLI QUIT.
        Assert.Equal([0x20, 0x00, 0xBF, 0xCC], r.Ram.Bytes[quit..(quit + 4)]);
        int close = r.Ram.Bytes[quit + 4] | r.Ram.Bytes[quit + 5] << 8;
        Assert.Equal([1, 0], r.Ram.Bytes[close..(close + 2)]);
        Assert.Equal([0xEE, 0xF4, 0x03, 0x20, 0x00, 0xBF, 0x65], r.Ram.Bytes[(quit + 6)..(quit + 13)]);
        int parameters = r.Ram.Bytes[quit + 13] | r.Ram.Bytes[quit + 14] << 8;
        Assert.Equal([4, 0, 0, 0, 0, 0, 0], r.Ram.Bytes[parameters..(parameters + 7)]);
    }

    [Fact]
    public void Literals_without_a_prefix_are_apple2_codes()
    {
        var r = RunApple2("""
            data t = "Hi~ {}\n"
            var c: u8
            var d: u8
            fn main() {
                c = 'a'
                d = apple2' '
            }
            """);
        Assert.Equal([0xC8, 0xE9, 0xFE, 0xA0, 0xFB, 0xFD, 0x8D], r.Ram.Bytes[r.Address("t")..(r.Address("t") + 7)]);
        Assert.Equal(0xE1, r.U8("c"));
        Assert.Equal(0xA0, r.U8("d"));
    }

    [Fact]
    public void A_character_outside_ascii_has_no_apple2_code()
    {
        var e = Assert.Throws<CompileError>(() => Compile("data t = \"£\"\nfn main() { }\n"));
        Assert.Contains("apple2", e.Description);
        Assert.Contains("\\$NN", e.Description);
    }

    [Theory]
    [InlineData(0xC000)]   // keyboard
    [InlineData(0xC030)]   // speaker
    [InlineData(0xC054)]   // page 1
    [InlineData(0xCFFF)]   // slot ROMs off
    public void Soft_switches_and_io_are_volatile(int address)
    {
        var r = Compile($"var v: u8 @ ${address:X4}\nfn main() {{\n    v = 1\n    v = 2\n}}\n");
        Assert.Equal(2, r.Listing.Split('\n').Count(l => l.Contains("sta v ")));
    }

    [Theory]
    [InlineData(0x0024, false)]   // CH, changed only inside COUT
    [InlineData(0x0400, false)]   // the text screen
    [InlineData(0x03F2, false)]   // the reset vector
    [InlineData(0xC000, true)]    // the keyboard
    public void Memory_that_changes_only_inside_a_call_is_not_volatile(int address, bool isVolatile)
    {
        // A volatile variable is read again after it is written.
        var r = Compile($"var v: u8 @ ${address:X4}\nvar n: u8\nfn main() {{\n    v = 1\n    n = v\n}}\n");
        Assert.Equal(isVolatile ? 1 : 0, r.Listing.Split('\n').Count(l => l.Contains("lda v ")));
    }

    /// <summary>Areas below and above the hi-res pages, the first with the startup code, and data in both.</summary>
    private const string Split = """
        memory {
            $0800..=$08FF program
            $6000..=$BEFF program
        }
        data A: [u8; 200] = repeat(7, 200)
        data B: [u8; 200] = repeat(9, 200)
        data C: [u8; 200] = repeat(11, 200)
        var sum: u16
        var last: u8
        fn main() {
            for i in 0..200 {
                sum += A[i] as u16 + B[i] as u16 + C[i] as u16
            }
            last = C[199]
        }
        """;

    [Fact]
    public void Areas_elsewhere_are_copied_into_place_from_the_file()
    {
        var r = RunApple2(Split);
        Assert.Equal(200 * 27, r.U16("sum"));
        Assert.Equal(11, r.U8("last"));
        // The file: the area with the startup code, then the other, with no gap; it starts at $2000.
        var areas = r.Result.Segments;
        Assert.Equal([0x0800, 0x6000], areas.Select(s => s.Address));
        Assert.Equal(areas[0].Bytes.Concat(areas[1].Bytes), r.Result.File);
        Assert.Equal(0x2000, r.Result.Entry);
        int first = areas[0].Bytes.Length;
        Assert.Contains($"System file: {r.Result.File.Length} bytes, loaded at $2000-${0x2000 + r.Result.File.Length - 1:X4}; " +
            $"the startup code copies 2 areas into place: $2000 to $0800, {first} bytes, ${0x2000 + first:X4} to $6000, {areas[1].Bytes.Length} bytes", r.Result.Report);
    }

    [Fact]
    public void An_area_that_overlaps_its_place_in_the_file_is_copied_from_its_end()
    {
        // The second area holds 2,000 bytes and moves up by less than that: copied from its first byte up,
        // it would overwrite bytes not copied yet.
        var tables = string.Concat(Enumerable.Range(0, 10).Select(k => $"data T{k}: [u8; 200] = [{string.Join(", ", Enumerable.Range(0, 200).Select(i => (i * 7 + k) & 0xFF))}]\n"));
        var sums = string.Concat(Enumerable.Range(0, 10).Select(k => $"        sum += T{k}[i] as u16\n"));
        var r = RunApple2($$"""
            memory {
                $0800..=$08FF program
                $2400..=$BEFF program
            }
            {{tables}}
            var sum: u16
            fn main() {
                for i in 0..200 {
            {{sums}}
                }
            }
            """);
        int expected = Enumerable.Range(0, 10).Sum(k => Enumerable.Range(0, 200).Sum(i => (i * 7 + k) & 0xFF));
        Assert.Equal(expected & 0xFFFF, r.U16("sum"));
        var second = r.Result.Segments[1];
        int from = 0x2000 + r.Result.Segments[0].Bytes.Length;
        Assert.True(second.Bytes.Length > second.Address - from, "the area must overlap its place in the file");
        Assert.Contains("dec $51", r.Result.Listing);
    }

    [Fact]
    public void Fixed_data_in_a_gap_is_copied_and_the_first_area_stays_in_place()
    {
        var r = RunApple2("""
            memory { $9000..=$90FF reserved }
            data table: [u8; 3] @ $9000 = [$11, $22, $33]
            var n: u8
            fn main() { n = table[2] }
            """);
        Assert.Equal(0x33, r.U8("n"));
        Assert.Equal(0x2000, r.Result.Segments[0].Address);
        Assert.Equal(r.Result.Segments[0].Bytes.Length + 3, r.Result.File.Length);
        Assert.Equal([0x11, 0x22, 0x33], r.Result.File[^3..]);
        Assert.Contains("copies 1 area into place", r.Result.Report);
    }

    [Fact]
    public void An_area_over_the_running_copier_is_an_error()
    {
        var e = Assert.Throws<CompileError>(() => Compile("""
            memory {
                $0800..=$08FF program
                $2000..=$BEFF program
            }
            data A: [u8; 200] = repeat(7, 200)
            var n: u8
            fn main() { n = A[5] }
            """));
        Assert.Contains("would overwrite the startup code that copies the areas into place, which runs at $2000-", e.Description);
        Assert.Contains("Start the first program area at $2000", e.Description);
    }

    [Fact]
    public void A_system_file_past_beff_is_an_error()
    {
        var tables = string.Concat(Enumerable.Range(0, 160).Select(k => $"data T{k}: [u8; 256] = repeat({k}, 256)\n"));
        var uses = string.Concat(Enumerable.Range(0, 160).Select(k => $"    n += T{k}[1]\n"));
        var e = Assert.Throws<CompileError>(() => Compile($$"""
            memory {
                $0800..=$1FFF program
                $2000..=$BEFF program
            }
            {{tables}}
            var n: u8
            fn main() {
            {{uses}}
            }
            """));
        Assert.Matches(@"^The system file is \d+ bytes, but ProDOS loads it at \$2000 and it may reach only \$BEFF \(40704 bytes\)\. Make the program smaller", e.Description);
    }

    [Fact]
    public void Tern_writes_a_system_file_and_an_applewin_symbol_file()
    {
        var dir = Directory.CreateTempSubdirectory("tern-apple2-");
        try
        {
            var path = Path.Combine(dir.FullName, "game.tern");
            File.WriteAllText(path, "var n: u8\nfn main() { n = 1 }\n");
            var output = new StringWriter();
            Assert.Equal(0, Cli.Run(["--target", "apple2", path], output, output));
            var file = File.ReadAllBytes(Path.ChangeExtension(path, ".system"));
            Assert.Equal(0xD8, file[0]);   // cld, at $2000
            // AppleWin's symbol tables: "ADDRESS NAME", ';' for comments (question 138).
            var labels = File.ReadAllLines(Path.ChangeExtension(path, ".sym"));
            Assert.StartsWith(";", labels[0]);
            var main = TernCompiler.Compile(File.ReadAllText(path), Platform.Apple2).Symbols["main"];
            Assert.Contains($"{main:X4} main", labels);
            Assert.All(labels[1..], l => Assert.Matches(@"^[0-9A-F]{4} [a-z_.0-9]+$", l));
            Assert.True(File.Exists(Path.ChangeExtension(path, ".lst")));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Overlays_and_binaries_are_plain_binary_files()
    {
        var dir = Directory.CreateTempSubdirectory("tern-apple2-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "level1.tern"), "var hits: u8\nfn start() { hits = 1 }\n");
            var main = Path.Combine(dir.FullName, "main.tern");
            File.WriteAllText(main, "import level1 @ lv\nmemory { $6000..=$6FFF overlay lv }\nfn main() { level1.start() }\n");
            var r = TernCompiler.Compile(File.ReadAllText(main), Platform.Apple2, sourcePath: main);
            var overlay = Assert.Single(r.Overlays);
            Assert.Equal(overlay.Image, overlay.File);
            Assert.Contains("level1.bin:", r.Report);

            var binary = TernCompiler.Compile("""
                memory { $9000..=$90FF program }
                var count: u8
                fn add(n: u8 using A) { count += n }
                export add
                """, Platform.Apple2, sourcePath: Path.Combine(dir.FullName, "counter.tern"));
            Assert.Equal(binary.Image, binary.File);
            Assert.Equal(0x9000, binary.LoadAddress);
            Assert.Contains("counter.bin", binary.ApiModule);
        }
        finally { dir.Delete(recursive: true); }
    }
}
