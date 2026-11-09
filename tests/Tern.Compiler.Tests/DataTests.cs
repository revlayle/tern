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
        var petscii = CharEncoding.Petscii;
        var screen = CharEncoding.Screen;
        Assert.Equal(0x5C, petscii.Encode('£'));
        Assert.Equal(0x5E, petscii.Encode('↑'));
        Assert.Equal(0x5F, petscii.Encode('←'));
        Assert.Equal(0xFF, petscii.Encode('π'));
        Assert.Equal(0x1C, screen.Encode('£'));
        Assert.Equal(0x5E, screen.Encode('π'));
        Assert.Equal(0x3F, screen.Encode('?'));
        Assert.Null(petscii.Encode('{'));
        Assert.Null(screen.Encode('\n'));
    }

    [Fact]
    public void Mixed_case_encoding_tables()
    {
        var petscii = CharEncoding.PetsciiMixed;
        var screen = CharEncoding.ScreenMixed;
        Assert.Equal(0x41, petscii.Encode('a'));
        Assert.Equal(0x5A, petscii.Encode('z'));
        Assert.Equal(0xC1, petscii.Encode('A'));
        Assert.Equal(0xDA, petscii.Encode('Z'));
        Assert.Equal(0x40, petscii.Encode('@'));
        Assert.Equal(0x0D, petscii.Encode('\n'));
        Assert.Null(petscii.Encode('π'));
        Assert.Equal(0x00, screen.Encode('@'));
        Assert.Equal(0x01, screen.Encode('a'));
        Assert.Equal(0x1A, screen.Encode('z'));
        Assert.Equal(0x41, screen.Encode('A'));
        Assert.Equal(0x5A, screen.Encode('Z'));
        Assert.Equal(0x30, screen.Encode('0'));
        Assert.Equal(0x1C, screen.Encode('£'));
        Assert.Null(screen.Encode('\n'));
    }

    [Fact]
    public void Encoding_prefixes_on_strings_and_chars()
    {
        var r = Run.Program("""
            data p = petscii"Ab"
            data pm = petscii_mixed"Ab"
            data s = screen"Ab"
            data sm = screen_mixed"Ab"
            var c: [u8; 6]
            fn main() {
                c[0] = 'a'
                c[1] = petscii'a'
                c[2] = petscii_mixed'A'
                c[3] = screen'A'
                c[4] = screen_mixed'a'
                c[5] = screen'\$80'
            }
            """);
        Assert.Equal([0x41, 0x42], Bytes(r, "p", 2));
        Assert.Equal([0xC1, 0x42], Bytes(r, "pm", 2));
        Assert.Equal([0x01, 0x02], Bytes(r, "s", 2));
        Assert.Equal([0x41, 0x02], Bytes(r, "sm", 2));
        Assert.Equal([0x41, 0x41, 0xC1, 0x01, 0x01, 0x80], Bytes(r, "c", 6));
    }

    [Fact]
    public void A_keyword_before_a_quote_is_not_an_encoding_prefix()
    {
        var r = Run.Program("""
            var c: u8
            fn f() -> u8 { return'A' }
            fn main() { c = f() }
            """);
        Assert.Equal(0x41, r.U8("c"));
    }

    [Fact]
    public void Prefixed_char_literals_are_constants()
    {
        var r = Run.Program("""
            const A = screen'A'
            var c: [u8; 2] = [screen'Z', A + 1]
            fn main() { }
            """);
        Assert.Equal([26, 2], Bytes(r, "c", 2));
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

    [Fact]
    public void Tables_and_arguments_may_span_lines()
    {
        var r = Run.Program("""
            data table: [u8; 6] = [
                1, 2, 3,
                4, 5, 6
            ]
            var sum: u8
            fn add(a: u8, b: u8) -> u8 { return a + b }
            fn main() {
                sum = add(
                    table[1],
                    table[5]
                )
            }
            """);
        Assert.Equal(8, r.U8("sum"));
        Assert.Equal([1, 2, 3, 4, 5, 6], Bytes(r, "table", 6));
    }

    [Theory]
    [InlineData("data d: [u8; 2] = [1, 2]\nfn main() { d[0] = 5 }", "'d' is data, which is read-only")]
    [InlineData("data d: [u16; 2] = [1, 2]\nfn main() { d[0].lo = 5 }", "'d' is data, which is read-only")]
    [InlineData("data d = 5\nfn main() { }", "Data must be an array, a string or a struct")]
    [InlineData("data d: [u8; 2]\nfn main() { }", "Expected '=' and the data's value")]
    [InlineData("data d @ $C000 = \"A\"\nfn main() { }", "'d' at $C000 is not in a program or reserved area")]
    [InlineData("fn main() { data d = \"A\" }", "'data' must be declared outside functions")]
    [InlineData("data d: [u8; 3] = \"AB\"\nfn main() { }", "Expected 3 bytes, found 2")]
    [InlineData("data d: [u16; 2] = \"AB\"\nfn main() { }", "is a [u8; 2] array, but 'd' is [u16; 2]")]
    [InlineData("data d = \"\"\nfn main() { }", "at least one character")]
    [InlineData("data d = \"A{\"\nfn main() { }", "'{' has no code in the petscii encoding")]
    [InlineData("data d = screen\"A\\n\"\nfn main() { }", "'\\n' has no code in the screen encoding")]
    [InlineData("var c: u8 = petscii_mixed'π'\nfn main() { }", "'π' has no code in the petscii_mixed encoding")]
    [InlineData("data d = ascii\"A\"\nfn main() { }", "Unknown encoding 'ascii'. Known encodings: petscii, petscii_mixed, screen, screen_mixed")]
    [InlineData("var c: u8\nfn main() { c = scren'A' }", "Unknown encoding 'scren'")]
    [InlineData("data d = include_bytes(screen\"x\")\nfn main() { }", "takes a file name in quotes")]
    [InlineData("data d = \"A\\q\"\nfn main() { }", "Unknown escape '\\q'")]
    [InlineData("data d = \"AB\nfn main() { }", "String is not closed")]
    [InlineData("var c: u8 = 'AB'\nfn main() { }", "exactly one character")]
    [InlineData("var c: u8 = ''\nfn main() { }", "exactly one character")]
    [InlineData("var c: u8 = 'A\nfn main() { }", "Character literal is not closed")]
    [InlineData("data d = [1, 2, 3]\nfn main() { }", "Data arrays need a written type")]
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

    [Fact]
    public void Escape_errors_point_at_the_escape()
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("data d = \"AB\\q\"\nfn main() { }"));
        Assert.Equal(new SourcePos(1, 13), e.Pos);
    }

    [Fact]
    public void Empty_include_file_is_an_error()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            File.WriteAllBytes(Path.Combine(dir.FullName, "empty.bin"), []);
            var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(
                "data d = include_bytes(\"empty.bin\")\nfn main() { }", sourcePath: Path.Combine(dir.FullName, "x.tern")));
            Assert.Contains("'empty.bin' is empty", e.Description);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void An_array_length_of_underscore_is_taken_from_the_list()
    {
        // M20 follow-up, spec 4: [T; _] on data and on global vars with a list.
        var r = Run.Program("""
            data script: [u8; _] = [3, 1, 4, 1, 5]
            data words: [u16; _] = [1000, 65535, 0]
            linear data pairs: [u16; _] = [$1234, $5678]
            data signed: [i8; _] = [-128, 127]
            var table: [u8; _] = [9, 8, 7]
            var n: u8
            var w: u8
            var lin: u8
            var sum: u16
            var last: i8
            fn main() {
                n = len(script)
                w = len(words)
                lin = len(pairs)
                for i in 0..len(words) { sum += words[i] }
                table[2] += script[4]
                last = signed[1]
            }
            """);
        Assert.Equal(5, r.U8("n"));
        Assert.Equal(3, r.U8("w"));
        Assert.Equal(2, r.U8("lin"));
        Assert.Equal((1000 + 65535) & 0xFFFF, r.U16("sum"));
        Assert.Equal([9, 8, 12], Bytes(r, "table", 3));
        Assert.Equal(127, (sbyte)r.U8("last"));
        Assert.Equal([3, 1, 4, 1, 5], Bytes(r, "script", 5));
        Assert.Equal([0x34, 0x12, 0x78, 0x56], Bytes(r, "pairs", 4));
        Assert.Equal("[u8; 5]", r.Result.Variables["script"].Type);
    }

    [Fact]
    public void A_long_byte_list_with_underscore_is_long_data()
    {
        string list = string.Join(", ", Enumerable.Range(0, 300).Select(i => i & 0xFF));
        var r = Run.Program($$"""
            data big: [u8; _] = [{{list}}]
            var n: u16
            var p: ptr u8
            var at: u8
            fn main() {
                n = len(big)
                p = addr(big) + 256
                at = p[43]
            }
            """);
        Assert.Equal(300, r.U16("n"));
        Assert.Equal(299 & 0xFF, r.U8("at"));
    }

    [Theory]
    [InlineData("data d: [u8; _] = \"AB\"\nfn main() { }", "The length '_' is taken from a list initial value, so it works only on a global var or data with a list, as in 'data t: [u8; _] = [1, 2, 3]'. Write the length here")]
    [InlineData("var d: [u8; _]\nfn main() { }", "The length '_' is taken from a list initial value")]
    [InlineData("fn main() { var d: [u8; _] }", "The length '_' is taken from a list initial value")]
    [InlineData("fn f(p: ptr [u8; _]) { }\nfn main() { }", "The length '_' is taken from a list initial value")]
    [InlineData("struct S { a: [u8; _] }\nvar s: S\nfn main() { }", "The length '_' is taken from a list initial value")]
    [InlineData("data d: [u8; _] = []\nfn main() { }", "Array length must be 1 to 256, not 0")]
    [InlineData("var d: [u8; _] = [1, 2, 300]\nfn main() { }", "300 does not fit in u8")]
    [InlineData("data d = [1, 2, 3]\nfn main() { }", "Data arrays need a written type, e.g. data d: [u8; _] = [...], where '_' takes the length from the list")]
    public void Underscore_length_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
    }

    [Theory]
    [InlineData("var d: [u8; _]", 257, "'d' is a long byte array (257 bytes)")]
    [InlineData("data d: [u16; _]", 257, "An array of u16 has at most 256 elements")]
    [InlineData("var d: [u8; _]", 0, "Array length must be 1 to 256, not 0")]
    public void A_list_length_is_checked_like_a_written_one(string declaration, int count, string expected)
    {
        string source = $"{declaration} = [{string.Join(", ", Enumerable.Repeat("0", count))}]\nfn main() {{ }}";
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
    }
}
