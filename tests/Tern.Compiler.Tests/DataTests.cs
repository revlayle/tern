using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

public class DataTests
{
    private static byte[] Bytes(Run r, string name, int count) => r.Ram.Bytes.AsSpan(r.Address(name), count).ToArray();

    [Fact]
    public void Data_table_is_readable_by_index()
    {
        var r = Run.Program("""
            data squares: [u8; 8] = [0, 1, 4, 9, 16, 25, 36, 49]
            data big: [u16; 3] = [1000, 2000, 65535]
            var out: [u8; 8]
            var sum: u16
            var last: u16
            fn main() {
                for i in 0..len(squares) { out[i] = squares[i] }
                for j in 0..3 { sum = sum + big[j] }
                last = big[2]
            }
            """);
        Assert.Equal([0, 1, 4, 9, 16, 25, 36, 49], Bytes(r, "out", 8));
        Assert.Equal((1000 + 2000 + 65535) & 0xFFFF, r.U16("sum"));
        Assert.Equal(65535, r.U16("last"));
        // Data lives in the program image; split tables as for arrays.
        int big = r.Address("big");
        Assert.True(big < r.Result.LoadAddress + r.Result.Image.Length);
        Assert.Equal([1000 & 0xFF, 2000 & 0xFF, 0xFF, 1000 >> 8, 2000 >> 8, 0xFF], Bytes(r, "big", 6));
    }

    [Fact]
    public void Char_literals_are_petscii()
    {
        var r = Run.Program("""
            var c: [u8; 8]
            fn main() {
                c[0] = 'A'
                c[1] = 'z'
                c[2] = ' '
                c[3] = '0'
                c[4] = '\n'
                c[5] = '\0'
                c[6] = '\$93'
                c[7] = '\''
            }
            """);
        Assert.Equal([0x41, 0x5A, 0x20, 0x30, 0x0D, 0x00, 0x93, 0x27], Bytes(r, "c", 8));
    }

    [Fact]
    public void Char_literals_compare_with_numbers()
    {
        var r = Run.Program("""
            data msg = "HI!"
            var letters: u8
            fn main() {
                for i in 0..len(msg) {
                    if msg[i] >= 'A' && msg[i] <= 'Z' { letters += 1 }
                }
            }
            """);
        Assert.Equal(2, r.U8("letters"));
    }

    [Fact]
    public void Strings_in_petscii_and_screen_codes()
    {
        var r = Run.Program("""
            data text = "Hello, @[]\n"
            data scr = screen"Hello, @[]"
            data term = "HI\0"
            data raw = screen"A\$80\""
            var lengths: [u8; 4]
            fn main() {
                lengths[0] = len(text)
                lengths[1] = len(scr)
                lengths[2] = len(term)
                lengths[3] = len(raw)
            }
            """);
        Assert.Equal([11, 10, 3, 3], Bytes(r, "lengths", 4));
        Assert.Equal("HELLO, @[]"u8.ToArray().Append((byte)0x0D), Bytes(r, "text", 11));
        Assert.Equal([8, 5, 12, 12, 15, 0x2C, 0x20, 0x00, 0x1B, 0x1D], Bytes(r, "scr", 10));
        Assert.Equal([0x48, 0x49, 0x00], Bytes(r, "term", 3));
        Assert.Equal([0x01, 0x80, 0x22], Bytes(r, "raw", 3));
    }

    [Fact]
    public void Petscii_and_screen_code_tables()
    {
        Assert.Equal(0x5C, Petscii.FromChar('£'));
        Assert.Equal(0x5E, Petscii.FromChar('↑'));
        Assert.Equal(0x5F, Petscii.FromChar('←'));
        Assert.Equal(0xFF, Petscii.FromChar('π'));
        Assert.Equal(0x1C, Petscii.ScreenCode('£'));
        Assert.Equal(0x5E, Petscii.ScreenCode('π'));
        Assert.Equal(0x3F, Petscii.ScreenCode('?'));
        Assert.Null(Petscii.FromChar('{'));
        Assert.Null(Petscii.ScreenCode('\n'));
    }

    [Fact]
    public void Strings_initialize_global_arrays()
    {
        var r = Run.Program("""
            var name: [u8; 4] = "ABCD"
            fn main() { name[0] = 'X' }
            """);
        Assert.Equal("XBCD"u8.ToArray(), Bytes(r, "name", 4));
    }

    [Fact]
    public void Copies_a_string_to_the_screen_through_a_pointer()
    {
        var r = Run.Program("""
            data msg = screen"HELLO WORLD"
            fn print(dst: ptr u8, src: ptr u8, n: u8) {
                for i in 0..n { dst[i] = src[i] }
            }
            fn main() {
                print($0400 + 40 * 12 + 14, addr(msg), len(msg))
            }
            """);
        Assert.Equal([8, 5, 12, 12, 15, 0x20, 23, 15, 18, 12, 4], r.Ram.Bytes.AsSpan(0x0400 + 40 * 12 + 14, 11).ToArray());
    }

    [Fact]
    public void Include_bytes_loads_a_file_relative_to_the_source()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "gfx"));
            var charset = Enumerable.Range(0, 600).Select(i => (byte)(i * 7)).ToArray();
            File.WriteAllBytes(Path.Combine(dir.FullName, "gfx", "font.bin"), charset);
            File.WriteAllBytes(Path.Combine(dir.FullName, "ship.bin"), [1, 2, 3]);
            var r = Run.Program("""
                data ship: [u8; 3] = include_bytes("ship.bin")
                data font = include_bytes("gfx/font.bin")
                var size: u16
                var s: u8
                var f: u8
                fn main() {
                    size = len(font)
                    s = ship[2]
                    var p: ptr u8 = addr(font) + 599
                    f = p[0]
                }
                """, sourcePath: Path.Combine(dir.FullName, "game.tern"));
            Assert.Equal(600, r.U16("size"));
            Assert.Equal(3, r.U8("s"));
            Assert.Equal(charset[599], r.U8("f"));
            Assert.Equal(charset, Bytes(r, "font", 600));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("data d: [u8; 2] = [1, 2]\nfn main() { d[0] = 5 }", "'d' is data, which is read-only")]
    [InlineData("data d: [u16; 2] = [1, 2]\nfn main() { d[0].lo = 5 }", "'d' is data, which is read-only")]
    [InlineData("data d = 5\nfn main() { }", "Data must be an array or a string")]
    [InlineData("data d: [u8; 2]\nfn main() { }", "Expected '=' and the data's value")]
    [InlineData("data d @ $C000 = \"A\"\nfn main() { }", "'@' on data is not supported yet")]
    [InlineData("fn main() { data d = \"A\" }", "'data' must be declared outside functions")]
    [InlineData("data d: [u8; 3] = \"AB\"\nfn main() { }", "Expected 3 bytes, found 2")]
    [InlineData("data d: [u16; 2] = \"AB\"\nfn main() { }", "is a [u8; 2] array, but 'd' is [u16; 2]")]
    [InlineData("data d = \"\"\nfn main() { }", "at least one character")]
    [InlineData("data d = \"A{\"\nfn main() { }", "'{' has no PETSCII code")]
    [InlineData("data d = screen\"A\\n\"\nfn main() { }", "'\\n' has no screen code")]
    [InlineData("data d = \"A\\q\"\nfn main() { }", "Unknown escape '\\q'")]
    [InlineData("data d = \"AB\nfn main() { }", "String is not closed")]
    [InlineData("var c: u8 = 'AB'\nfn main() { }", "exactly one character")]
    [InlineData("var c: u8 = ''\nfn main() { }", "exactly one character")]
    [InlineData("var c: u8\nfn main() { c = \"A\" }", "A string can only be the value of data or a global array")]
    [InlineData("data d = include_bytes(\"no-such-file.bin\")\nfn main() { }", "Cannot read 'no-such-file.bin'")]
    [InlineData("data d = include_bytes(5)\nfn main() { }", "takes a file name in quotes")]
    [InlineData("var c: u8\nfn main() { c = include_bytes(\"x\")[0] }", "include_bytes() can only be the value")]
    [InlineData("data d = \"" + "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"
        + "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"
        + "0123456789012345678901234567890123456789012345678901234567890123456789\"\nvar c: u8\nfn main() { c = d[0] }",
        "'d' has 270 elements, too many for a u8 index")]
    public void Reports_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
    }
}
