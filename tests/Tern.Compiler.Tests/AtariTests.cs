using Tern.Compiler.Semantics;
using Tern.Compiler.Tools;

namespace Tern.Compiler.Tests;

/// <summary>The Atari platform (spec 12.2, M25): memory map, encoding, volatile areas and the .xex file.</summary>
public class AtariTests
{
    private static CompileResult Compile(string source) => TernCompiler.Compile(source, Platform.Atari);

    /// <summary>A .xex file's load segments: first address and bytes.</summary>
    internal static List<(int First, byte[] Bytes)> Segments(byte[] xex)
    {
        Assert.Equal([0xFF, 0xFF], xex[..2]);
        var list = new List<(int, byte[])>();
        for (int at = 2; at < xex.Length;)
        {
            int first = xex[at] | xex[at + 1] << 8, last = xex[at + 2] | xex[at + 3] << 8;
            list.Add((first, xex[(at + 4)..(at + 5 + last - first)]));
            at += 5 + last - first;
        }
        return list;
    }

    [Fact]
    public void The_memory_map_is_the_specs()
    {
        var layout = MemoryLayout.Default(Platform.Atari);
        Assert.Equal([new AddressRange(0x2000, 0xBC1F)], layout.Program);
        Assert.Equal([new AddressRange(0x80, 0xFF)], layout.ZeroPage);
        Assert.Same(CharEncoding.Atascii, Platform.Atari.DefaultEncoding);
    }

    [Fact]
    public void A_program_is_a_xex_that_loads_at_2000_and_runs_its_startup_code()
    {
        var r = Run.Program("""
            var n: u8
            var big: [u8; 200]
            fn main() {
                n = 5
                big[199] = n
            }
            """, platform: Platform.Atari);
        Assert.Equal(5, r.U8("n"));
        Assert.Equal(0x2000, r.Result.Entry);
        Assert.Equal(0x2000, r.Result.LoadAddress);
        var segments = Segments(r.Result.File);
        Assert.Equal(2, segments.Count);
        Assert.Equal(0x2000, segments[0].First);
        Assert.Equal(r.Result.Image, segments[0].Bytes);
        // The last segment writes the run address to RUNAD.
        Assert.Equal(0x02E0, segments[1].First);
        Assert.Equal([0x00, 0x20], segments[1].Bytes);
        // Zeroed variables are not in the file; the startup code clears them.
        Assert.True(r.Address("big") >= 0x2000 + r.Result.Image.Length);
        Assert.InRange(r.Address("n"), 0x80, 0xFF);
    }

    [Fact]
    public void Data_at_a_fixed_address_is_a_segment_of_its_own_with_no_padding()
    {
        var r = Compile("""
            data dl: [u8; 3] @ $9000 = [$70, $70, $41]
            var n: u8
            fn main() { n = dl[2] }
            """);
        var segments = Segments(r.File);
        Assert.Equal([0x2000, 0x9000, 0x02E0], segments.Select(s => s.First));
        Assert.Equal([0x70, 0x70, 0x41], segments[1].Bytes);
        Assert.Equal(2 + segments.Sum(s => 4 + s.Bytes.Length), r.File.Length);
        Assert.Equal(r.Segments.Select(s => s.Address), segments.Take(2).Select(s => s.First));
        Assert.Equal(r.Segments[1].Bytes, segments[1].Bytes);
        // Review M25, finding 5: the report counts the segments' bytes, not the gap between them.
        int code = segments[0].Bytes.Length;
        Assert.StartsWith($"Code and data: $2000-${0x2000 + code - 1:X4}, $9000-$9002 ({code + 3} bytes)", r.Report);
    }

    [Fact]
    public void The_first_program_area_may_start_anywhere()
    {
        var r = Run.Program("""
            memory { $3000..=$7FFF program }
            var n: u8
            fn main() { n = 9 }
            """, platform: Platform.Atari);
        Assert.Equal(9, r.U8("n"));
        Assert.Equal(0x3000, r.Result.Entry);
        Assert.Equal([0x00, 0x30], Segments(r.Result.File)[^1].Bytes);
    }

    [Fact]
    public void Literals_without_a_prefix_are_atascii()
    {
        var r = Run.Program("""
            data t = "Hi\n"
            var c: u8
            fn main() { c = 'a' }
            """, platform: Platform.Atari);
        Assert.Equal([0x48, 0x69, 0x9B], r.Ram.Bytes[r.Address("t")..(r.Address("t") + 3)]);
        Assert.Equal(0x61, r.U8("c"));
    }

    [Theory]
    [InlineData(0x0012)]   // RTCLOK
    [InlineData(0x004D)]   // ATRACT
    [InlineData(0x02C8)]   // COLOR4, a shadow register
    [InlineData(0xD01A)]   // COLBK
    [InlineData(0xD40A)]   // WSYNC
    public void Os_and_chip_addresses_are_volatile(int address)
    {
        var r = Run.Program($"var v: u8 @ ${address:X4}\nfn main() {{\n    v = 1\n    v = 2\n}}\n", platform: Platform.Atari,
            watch: _ => [address]);
        Assert.Equal([1, 2], r.Log!.Writes(address));
    }

    [Fact]
    public void A_binary_has_no_run_address_and_its_interface_names_the_xex()
    {
        var r = TernCompiler.Compile("""
            memory { $9000..=$90FF program }
            var count: u8
            fn add(n: u8 using A) { count += n }
            export add
            """, Platform.Atari, sourcePath: "counter.tern");
        var segments = Segments(r.File);
        Assert.Single(segments);
        Assert.Equal(0x9000, segments[0].First);
        Assert.Contains("counter.xex", r.ApiModule);
    }

    [Fact]
    public void An_overlay_file_is_a_xex_without_a_run_address()
    {
        var dir = Directory.CreateTempSubdirectory("tern-atari-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "level1.tern"), "var hits: u8\nfn start() { hits = 1 }\n");
            var main = Path.Combine(dir.FullName, "main.tern");
            File.WriteAllText(main, "import level1 @ lv\nmemory { $6000..=$6FFF overlay lv }\nfn main() { level1.start() }\n");
            var r = TernCompiler.Compile(File.ReadAllText(main), Platform.Atari, sourcePath: main);
            var overlay = Assert.Single(r.Overlays);
            var segments = Segments(overlay.File);
            var segment = Assert.Single(segments);
            Assert.Equal(0x6000, segment.First);
            Assert.Equal(overlay.Image, segment.Bytes);
            Assert.Contains("level1.xex:", r.Report);
        }
        finally { dir.Delete(recursive: true); }
    }


    /// <summary>Every module that is not C64-only (spec 10.1), used once; run on both machines, the variables must agree.</summary>
    private const string Modules = """
        import math
        import mem
        import text
        import wide
        import bcd
        import random
        import cpu
        import float
        var product: u16
        var quotient: u16
        var buf: [u8; 16]
        var copy: [u8; 16]
        var digits: [u8; 8]
        var n: u8
        var a: wide.U24
        var b: wide.U24
        var score: [u8; 3]
        var add: [u8; 3]
        var r: u8
        var carry: bool
        var x: float.F32 = float.F32 { bytes = float.of(2.5) }
        var y: float.F32 = float.F32 { bytes = float.of(-0.75) }
        var f: u16
        var ftext: [u8; 12]
        fn main() {
            product = math.mul8(200, 123)
            quotient = math.div16(60000, 7)
            mem.fill(addr(buf), $AA, 16)
            mem.copy(addr(copy), addr(buf), 16)
            n = text.dec16(addr(digits), 65535)
            wide.set24(addr(a), 50000)
            wide.set24(addr(b), 40000)
            wide.add24(addr(a), addr(b))
            add[2] = $99
            bcd.add(addr(score), addr(add), 3)
            random.seed(1234)
            r = random.next8()
            asm { sec }
            carry = cpu.carry()
            float.mul(addr(x), addr(y))
            f = float.to_u16(addr(x))
            n += float.dec(addr(ftext), addr(x), 3)
        }
        """;

    [Fact]
    public void Library_modules_that_are_not_c64_only_work_unchanged()
    {
        var c64 = Run.Program(Modules);
        var atari = Run.Program(Modules, platform: Platform.Atari);
        foreach (var v in c64.Result.Variables.Values.Where(v => v.Function is null && !v.IsData && !v.Name.Contains('.')))
            Assert.True(c64.Ram.Bytes.AsSpan(v.Address, v.Size).SequenceEqual(atari.Ram.Bytes.AsSpan(atari.Address(v.Name), v.Size)), v.Name);
        Assert.Equal(200 * 123, atari.U16("product"));
        Assert.Equal(90000, atari.U16("a") | atari.U8("a", 2) << 16);
        Assert.Equal("-1.875", string.Concat(atari.Ram.Bytes.AsSpan(atari.Address("ftext"), 6).ToArray().Select(c => (char)c)));
    }

    [Fact]
    public void Tern_writes_a_xex_and_an_altirra_label_file()
    {
        var dir = Directory.CreateTempSubdirectory("tern-atari-");
        try
        {
            var path = Path.Combine(dir.FullName, "game.tern");
            File.WriteAllText(path, "var n: u8\nfn main() { n = 1 }\n");
            var output = new StringWriter();
            Assert.Equal(0, Cli.Run(["--target", "atari", path], output, output));
            var xex = File.ReadAllBytes(Path.ChangeExtension(path, ".xex"));
            Assert.Equal(0x2000, Segments(xex)[0].First);
            // MADS's label table, which Altirra reads (question 136); VICE's format loads with no labels.
            var labels = File.ReadAllLines(Path.ChangeExtension(path, ".lbl"));
            Assert.Equal(["mads label table, written by Tern", "Label table:"], labels[..2]);
            var main = TernCompiler.Compile(File.ReadAllText(path), Platform.Atari).Symbols["main"];
            Assert.Contains($"00\t{main:X4}\tmain", labels);
            Assert.All(labels[2..], l => Assert.Matches(@"^00\t[0-9A-F]{4}\t[a-z_.0-9]+$", l));
            Assert.True(File.Exists(Path.ChangeExtension(path, ".lst")));
            Assert.True(File.Exists(Path.ChangeExtension(path, ".lines")));
            Assert.False(File.Exists(Path.ChangeExtension(path, ".prg")));
        }
        finally { dir.Delete(recursive: true); }
    }

    // Errors the coverage run of R6 found no test for (review code-quality-tests, finding 4).
    [Theory]
    [InlineData("var w: [u8; 126] @ zp\nvar t: [u8; 2] @ zp\nfn main() { w[0] = 1; asm { jmp (t+1) } }", 2, 1, "'t' is used as jmp (t+1) and was placed so that the vector starts at $FF, where the 6502 reads it wrongly. Place it at a fixed address whose low byte is not $FF")]
    public void Reports_errors_no_other_test_reached(string source, int line, int column, string expected)
    {
        Expect.Error(() => TernCompiler.Compile(source, Platform.ByName("atari")), line, column, expected);
    }
}
