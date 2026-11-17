using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary><c>@ align(n)</c> (spec 4): a global <c>var</c> or <c>data</c> at a multiple of <c>n</c>, wherever the compiler places it.</summary>
public class AlignTests
{
    private static string Message(string source) =>
        Assert.Throws<CompileError>(() => TernCompiler.Compile(source)).Description;

    [Theory]
    [InlineData(2)]
    [InlineData(64)]
    [InlineData(256)]
    [InlineData(2048)]
    public void A_variable_after_the_program_is_at_a_multiple_of_its_alignment(int n)
    {
        // An odd-sized variable before it, so that the next free address is not aligned by chance.
        var r = Run.Program($$"""
            var a: [u8; 3]
            var buf: [u8; 100] @ align({{n}})
            var b: u8
            fn main() {
                a[2] = 7
                for i in 0..100 { buf[i] = i + 1 }
                b = buf[99]
            }
            """, setup: ram => { for (int i = 0x0900; i < 0x2000; i++) ram.Bytes[i] = 0xEE; });
        int at = r.Address("buf");
        Assert.Equal(0, at % n);
        Assert.Equal(1, r.U8("buf"));
        Assert.Equal(100, r.U8("b"));
        Assert.Equal(7, r.U8("a", 2));
        Assert.Equal(0, r.U8("a"));     // still zeroed at startup
        Assert.Contains($"Aligned buf: ${at:X4} (@ align({n})), ", r.Result.Report);
    }

    [Fact]
    public void Several_alignments_in_one_block_all_hold()
    {
        var r = Run.Program("""
            var x: u8
            var sprite: [u8; 63] @ align(64)
            var y: u8
            noinit var page: [u8; 256] @ align(256)
            var table: [u8; 10] @ align(2)
            fn main() {
                x = 1
                y = 2
                sprite[0] = 3
                page[255] = 4
                table[9] = 5
            }
            """);
        Assert.Equal(0, r.Address("sprite") % 64);
        Assert.Equal(0, r.Address("page") % 256);
        Assert.Equal(0, r.Address("table") % 2);
        Assert.Equal(1, r.U8("x"));
        Assert.Equal(2, r.U8("y"));
        Assert.Equal(3, r.U8("sprite"));
        Assert.Equal(4, r.U8("page", 255));
        Assert.Equal(5, r.U8("table", 9));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_long_noinit_buffer_starts_the_block_and_the_zeroed_variables_follow_it()
    {
        var r = Run.Program("""
            var count: [u8; 2]
            var p: ptr u8
            noinit var buffer: [u8; 1024] @ align(256)
            fn main() {
                p = addr(buffer)
                for page in 0..4 {
                    for i in 0..=255 { p[i] = page }
                    p += 256
                }
                p = addr(buffer) + 1023
                count[1] = p[0]
            }
            """, setup: ram => { for (int i = 0x0900; i < 0x3000; i++) ram.Bytes[i] = 0xEE; });
        int at = r.Address("buffer");
        Assert.Equal(0, at % 256);
        Assert.Equal(3, r.U8("count", 1));
        Assert.Equal(0, r.U8("count"));     // zeroed at startup
        // The buffer starts the block and the zeroed variables follow it, so the only padding is before the block.
        Assert.Equal(at + 1024, r.Address("count"));
        var free = System.Text.RegularExpressions.Regex.Match(r.Result.Listing, @"; \$[0-9A-F]{4}: (\d+) byte\(s\) left free so that aligned variables");
        int padding = free.Success ? int.Parse(free.Groups[1].Value) : 0;
        var line = r.Result.Report.Split('\n').Single(l => l.StartsWith("Aligned buffer"));
        Assert.Equal($"Aligned buffer: ${at:X4} (@ align(256)), {padding} bytes of padding before it", line.TrimEnd());
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void The_report_counts_the_padding_before_a_variable()
    {
        // buf comes first in the block, so its padding is what the block leaves free after the code.
        var r = Run.Program("""
            var a: u8
            var b: [u8; 3]
            var buf: [u8; 10] @ align(256)
            fn main() { a = 1; b[0] = 2; buf[0] = 3 }
            """);
        int at = r.Address("buf");
        Assert.Equal(0, at % 256);
        var free = System.Text.RegularExpressions.Regex.Match(r.Result.Listing,
            @"; \$([0-9A-F]{4}): (\d+) byte\(s\) left free so that aligned variables start at a multiple of their alignment");
        Assert.True(free.Success, r.Result.Listing);
        int padding = int.Parse(free.Groups[2].Value);
        Assert.Equal(at, Convert.ToInt32(free.Groups[1].Value, 16) + padding);
        Assert.Contains($"Aligned buf: ${at:X4} (@ align(256)), {padding} bytes of padding before it", r.Result.Report);
        Assert.Equal(1, r.U8("a"));
        Assert.Equal(2, r.U8("b"));
    }

    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData(2)]
    [InlineData(64)]
    [InlineData(256)]
    [InlineData(2048)]
    public void Data_is_at_a_multiple_of_its_alignment_with_zeros_before_it(int n)
    {
        var r = Run.Program($$"""
            data TABLE: [u8; 4] @ align({{n}}) = [10, 20, 30, 40]
            var x: u8
            fn main() { x = TABLE[3] + TABLE[0] }
            """);
        int at = r.Address("TABLE");
        Assert.Equal(0, at % n);
        Assert.Equal(50, r.U8("x"));
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, r.Result.Image[(at - r.Result.LoadAddress)..(at - r.Result.LoadAddress + 4)]);
        var line = r.Result.Report.Split('\n').Single(l => l.StartsWith("Aligned TABLE")).TrimEnd();
        int padding = int.Parse(System.Text.RegularExpressions.Regex.Match(line, @"(\d+) bytes? of padding").Groups[1].Value);
        Assert.True(padding < n);
        Assert.All(r.Result.Image[(at - r.Result.LoadAddress - padding)..(at - r.Result.LoadAddress)], b => Assert.Equal(0, b));
        if (padding > 0) Assert.Contains($"byte(s) of padding: 'TABLE' is placed with '@ align({n})'", r.Result.Listing);
    }

    [Fact]
    public void A_variable_with_an_initial_value_is_aligned_in_the_file()
    {
        var r = Run.Program("""
            var level: [u8; 3] @ align(256) = [1, 2, 3]
            var x: u8
            fn main() { level[1] += 5; x = level[1] }
            """);
        Assert.Equal(0, r.Address("level") % 256);
        Assert.Equal(7, r.U8("x"));
    }

    [Fact]
    public void Aligned_data_in_a_program_area_of_the_memory_block()
    {
        var r = Run.Program("""
            memory {
                $0801..=$0FFF program
                $4000..=$4FFF program
            }
            data CHARS: [u8; 2000] @ align(2048) = [7 for i in 0..2000]
            var x: u8
            var p: ptr u8
            fn main() { p = addr(CHARS); x = p[0] }
            """);
        Assert.Equal(0x4000, r.Address("CHARS"));
        Assert.Equal(7, r.U8("x"));
    }

    private static T With<T>(IReadOnlyDictionary<string, string> files, Func<string, T> work)
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            foreach (var (name, text) in files) File.WriteAllText(Path.Combine(dir.FullName, name + ".tern"), text);
            return work(Path.Combine(dir.FullName, "main.tern"));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Aligned_data_and_variables_of_an_overlay_are_aligned_in_its_file()
    {
        var level = """
            var hits: u8 = 1
            data TILES: [u8; 3] @ align(64) = [5, 6, 7]
            var map: [u8; 4] @ align(256)
            fn run() -> u8 using A { map[3] = TILES[2]; return map[3] + hits }
            """;
        var main = """
            import level1 @ levels
            memory {
                $0801..=$5FFF  program
                $6001..=$6FFF  overlay levels
            }
            var r: u8
            fn main() { r = level1.run() }
            """;
        var result = With(new Dictionary<string, string> { ["main"] = main, ["level1"] = level },
            path => TernCompiler.Compile(File.ReadAllText(path), sourcePath: path));
        var overlay = result.Overlays.Single();
        int tiles = result.Symbols["level1.TILES"], map = result.Symbols["level1.map"];
        Assert.Equal(0, tiles % 64);
        Assert.Equal(0x6100, map);
        Assert.Equal(new byte[] { 5, 6, 7 }, overlay.Image[(tiles - overlay.LoadAddress)..(tiles - overlay.LoadAddress + 3)]);
        Assert.Equal(new byte[4], overlay.Image[(map - overlay.LoadAddress)..(map - overlay.LoadAddress + 4)]);
        Assert.Contains("Aligned level1.map: $6100 (@ align(256)), 189 bytes of padding before it", result.Report);
    }

    [Fact]
    public void An_aligned_variable_is_never_put_in_zero_page()
    {
        var r = Run.Program("""
            var n: u8 @ align(2)
            fn main() { for i in 0..10 { n += 1 } }
            """);
        Assert.True(r.Address("n") >= 0x100);
        Assert.Equal(0, r.Address("n") % 2);
        Assert.Equal(10, r.U8("n"));
    }

    [Theory]
    [InlineData("3")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-2")]
    public void The_alignment_must_be_a_power_of_two_from_2_to_32768(string n)
    {
        var message = Message($"var buf: [u8; 4] @ align({n})\nfn main() {{ buf[0] = 1 }}");
        Assert.StartsWith("The alignment must be a power of two from 2 to 32768", message);
        Assert.Contains("Use one such as 64, 256 or 2048", message);
    }

    [Fact]
    public void The_alignment_may_be_a_constant()
    {
        var r = Run.Program("""
            const SPRITE: u16 = 64
            var s: [u8; 63] @ align(SPRITE)
            fn main() { s[0] = 1 }
            """);
        Assert.Equal(0, r.Address("s") % 64);
    }

    [Fact]
    public void The_alignment_must_be_known_at_compile_time()
    {
        var message = Message("var k: u8\nvar buf: [u8; 4] @ align(k)\nfn main() { buf[0] = k }");
        Assert.StartsWith("The alignment must be known at compile time", message);
    }

    [Fact]
    public void A_local_cannot_be_aligned()
    {
        var message = Message("fn main() {\n    var buf: [u8; 4] @ align(4)\n    buf[0] = 1\n}");
        Assert.StartsWith("Only global variables and data can be aligned", message);
        Assert.Contains("Move the variable out of the function", message);
    }

    [Fact]
    public void A_dereferenced_pointer_cannot_be_aligned()
    {
        var message = Message("var buf: [u8; 4]\nvar p: ptr u8 @ align(2)\nfn main() { p = addr(buf); p[0] = 1 }");
        Assert.Equal("'p' must be in zero page, since it is dereferenced as a pointer, so it cannot be placed with '@ align(2)'. Remove '@ align(2)'", message);
    }

    [Fact]
    public void Align_needs_parentheses()
    {
        var message = Message("var buf: [u8; 4] @ align 256\nfn main() { buf[0] = 1 }");
        Assert.StartsWith("Expected '(' and the alignment, as in '@ align(256)'", message);
    }

    [Fact]
    public void Variables_that_fit_nowhere_at_their_alignment_are_an_error()
    {
        // 1500 bytes fit in the 2 KB area, but not from a multiple of $800 after the program's code.
        var message = Message("""
            memory { $0801..=$0FFF program }
            noinit var buf: [u8; 1500] @ align(2048)
            var p: ptr u8
            fn main() { p = addr(buf); p[0] = 1 }
            """);
        Assert.StartsWith("The variables after the program (", message);
        Assert.Contains("must start where 'buf' is at a multiple of $800, and no program area has room for them there", message);
        Assert.EndsWith("Make a program area larger, or use a smaller alignment", message);
    }

    [Fact]
    public void Aligned_data_that_fits_nowhere_is_an_error()
    {
        var message = Message("""
            memory { $0801..=$0FFF program }
            data BIG: [u8; 1500] @ align(1024) = [0 for i in 0..1500]
            fn main() { }
            """);
        Assert.Contains("BIG", message);
    }

    [Fact]
    public void Aligned_data_does_not_move_the_variables_after_the_program()
    {
        // Review M26 follow-up, finding 1: font is aligned in the file; buf, a page array, follows it.
        var r = Run.Program("""
            data font: [u8; 8] @ align(2048) = [1, 2, 3, 4, 5, 6, 7, 8]
            var x: u8
            var buf: [u8; 200]
            var out: u8 @ $C000
            fn main() {
                x = font[out]
                buf[x] = 3
                out = buf[out]
            }
            """, setup: ram => ram.Bytes[0xC000] = 0);   // a fixed-address variable is never zeroed (spec 4)
        int font = r.Address("font"), buf = r.Address("buf");
        Assert.Equal(0, font % 2048);
        Assert.InRange(buf, font + 8, font + 0x100);
        Assert.Equal(buf >> 8, (buf + 199) >> 8);     // still placed so that indexed reads do not cross a page
        Assert.Equal(3, r.U8("buf", 1));
        Assert.Equal(0, r.Ram.Bytes[0xC000]);     // buf[0]
    }

    [Fact]
    public void Aligned_data_with_only_frames_after_the_program_compiles()
    {
        // Review M26 follow-up, finding 2: this crashed the compiler.
        var r = Run.Program("""
            data font: [u8; 8] @ align(32768) = [1, 2, 3, 4, 5, 6, 7, 8]
            var out: u8 @ $C000
            fn main() {
                var y: u8 = font[out]
                out = y
            }
            """, setup: ram => ram.Bytes[0xC000] = 0);
        Assert.Equal(0x8000, r.Address("font"));
        Assert.Equal(1, r.Ram.Bytes[0xC000]);
    }

    [Theory]
    [InlineData("var x: u8 @ zp align(2)")]
    [InlineData("var x: u8 @ $C000 align(2)")]
    [InlineData("memory { $C000..=$CFFF region buffers }\nvar x: u8 @ buffers align(2)")]
    [InlineData("var x: u8 @ align(2) zp")]
    [InlineData("var x: u8 @ align(2) buffers")]
    public void Align_with_another_location_is_an_error(string declaration)
    {
        var message = Message($"{declaration}\nfn main() {{ x = 1 }}");
        Assert.Equal("'@' takes one location (an address, a region, 'zp' or 'align(n)'), and this has two. Remove 'align(n)', or the other location", message);
    }

    [Fact]
    public void A_struct_field_cannot_be_aligned()
    {
        var message = Message("struct S { a: u8, b: u8 @ align(2) }\nvar s: S\nfn main() { s.b = 1 }");
        Assert.Equal("A struct field's '@' is its offset from the start of the struct, so it cannot be aligned. Write the offset, as in '@ 2'", message);
    }

    [Fact]
    public void An_aligned_global_that_is_not_used_is_warned()
    {
        var result = TernCompiler.Compile("var buf: [u8; 4] @ align(4)\nfn main() { }");
        Assert.Contains(result.Warnings, w => w.Description.Contains("'buf'"));
    }
}
