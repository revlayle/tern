using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// Encodings declared by the program (spec 2.1): <c>encoding tiles { '0'..='9' = $00 }</c> makes <c>tiles</c> a
/// literal prefix; <c>encoding hud: screen { ... }</c> starts from another; <c>default encoding tiles</c> in the
/// main file sets the encoding of the program's own unprefixed literals, never a library module's.
/// </summary>
public sealed class EncodingTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tern");

    public void Dispose() => _dir.Delete(recursive: true);

    private string MainPath => Path.Combine(_dir.FullName, "main.tern");
    private string LibPath => Path.Combine(_dir.FullName, "lib");

    private void Write(params (string Name, string Text)[] modules)
    {
        foreach (var (name, text) in modules) File.WriteAllText(Path.Combine(_dir.FullName, name + ".tern"), text);
    }

    private Run Program(string source, params (string Name, string Text)[] modules)
    {
        Write(modules);
        return Run.Program(source, sourcePath: MainPath);
    }

    private CompileError Error(string source, params (string Name, string Text)[] modules)
    {
        Write(modules);
        return Assert.Throws<CompileError>(() => TernCompiler.Compile(source, sourcePath: MainPath));
    }

    private List<string> Warnings(string source, params (string Name, string Text)[] modules)
    {
        Write(modules);
        return TernCompiler.Compile(source, sourcePath: MainPath).Warnings
            .Select(w => (Path.GetFileName(w.Pos.File) is "main.tern" or null ? "" : Path.GetFileName(w.Pos.File) + ":") + $"{w.Pos.Line}: {w.Description}")
            .ToList();
    }

    private static byte[] Bytes(Run r, string name, int count) => r.Ram.Bytes.AsSpan(r.Address(name), count).ToArray();

    private const string Tiles = """
        encoding tiles {
            '0'..='9' = $00          // a range: consecutive bytes from the one given
            'A'..='Z' = $0A
            ' '       = $24
            '♥'       = $25
        }
        """;

    /// <summary>The bytes <see cref="Tiles"/> gives, worked out here.</summary>
    private static byte TileCode(char c) => c switch
    {
        >= '0' and <= '9' => (byte)(c - '0'),
        >= 'A' and <= 'Z' => (byte)(c - 'A' + 0x0A),
        ' ' => 0x24,
        '♥' => 0x25,
        _ => throw new ArgumentException($"no tile for {c}"),
    };

    // ---------- Bytes ----------

    [Fact]
    public void Strings_and_chars_take_the_bytes_of_a_declared_encoding()
    {
        var r = Program($$"""
            {{Tiles}}
            data SCORE = tiles"SCORE 09 ♥ AZ"
            var out: [u8; 13]
            var digit: u8 = 7
            var c: [u8; 4]
            fn main() {
                for i in 0..len(SCORE) { out[i] = SCORE[i] }
                c[0] = tiles'0' + digit
                c[1] = tiles'Z'
                c[2] = tiles' '
                c[3] = tiles'♥'
            }
            """);
        Assert.Equal("SCORE 09 ♥ AZ".Select(TileCode).ToArray(), Bytes(r, "out", 13));
        Assert.Equal([7, 0x0A + 25, 0x24, 0x25], Bytes(r, "c", 4));
    }

    [Fact]
    public void A_range_may_end_at_ff()
    {
        var r = Program("""
            encoding top { 'A'..='Z' = $E6, 'a' = 0, 'b' = 255 }
            data T = top"AZab"
            var out: [u8; 4]
            fn main() { for i in 0..4 { out[i] = T[i] } }
            """);
        Assert.Equal([0xE6, 0xFF, 0x00, 0xFF], Bytes(r, "out", 4));
    }

    [Fact]
    public void A_base_encoding_gives_the_characters_not_listed()
    {
        var r = Program("""
            encoding hud: screen { '♥' = $53, '0' = $70, 'é' = $1F }
            data T = hud"AZ09 ♥é@"
            var out: [u8; 8]
            fn main() { for i in 0..len(T) { out[i] = T[i] } }
            """);
        static byte Screen(char c) => (byte)CharEncoding.Screen.Encode(c)!.Value;
        Assert.Equal([Screen('A'), Screen('Z'), 0x70, Screen('9'), Screen(' '), 0x53, 0x1F, Screen('@')], Bytes(r, "out", 8));
    }

    [Fact]
    public void A_declared_base_may_come_later_and_have_a_base_of_its_own()
    {
        var r = Program("""
            data T = top"AB0\n"
            encoding top: middle { 'B' = $22 }
            encoding middle: petscii { 'A' = $11, 'B' = $12 }
            var out: [u8; 4]
            fn main() { for i in 0..4 { out[i] = T[i] } }
            """);
        Assert.Equal([0x11, 0x22, 0x30, 0x0D], Bytes(r, "out", 4));
    }

    [Fact]
    public void Two_characters_may_have_the_same_byte()
    {
        var r = Program("""
            encoding t { 'a'..='z' = 1, 'A'..='Z' = 1, '-' = 0 }
            data T = t"aA-zZ"
            var out: [u8; 5]
            fn main() { for i in 0..5 { out[i] = T[i] } }
            """);
        Assert.Equal([1, 1, 0, 26, 26], Bytes(r, "out", 5));
    }

    [Fact]
    public void Escapes_follow_the_rules_of_built_in_encodings()
    {
        var r = Program("""
            encoding t { 'A' = 1, '\n' = $FE, '\'' = 2, '\\' = 3, '"' = 4 }
            data T = t"A\n\'\\\"\0\$80"
            var out: [u8; 7]
            fn main() { for i in 0..7 { out[i] = T[i] } }
            """);
        Assert.Equal([1, 0xFE, 2, 3, 4, 0, 0x80], Bytes(r, "out", 7));
    }

    [Fact]
    public void A_byte_may_be_a_constant_and_a_constant_may_use_the_encoding()
    {
        var r = Program("""
            const LETTERS: u8 = $40
            const HEART = tiles'♥'
            memory {
                $0801..=$9FFF program
            }
            encoding tiles { 'A'..='Z' = LETTERS, '♥' = LETTERS - 1 }
            var out: [u8; 3]
            fn main() {
                out[0] = HEART
                out[1] = tiles'C'
                out[2] = tiles'Z'
            }
            """);
        Assert.Equal([0x3F, 0x42, 0x40 + 25], Bytes(r, "out", 3));
    }

    [Fact]
    public void Encoding_and_default_are_still_names_elsewhere()
    {
        var r = Program("""
            var encoding: u8 = 3
            var default: u8 = 4
            fn main() {
                var e: u8 = encoding
                encoding = default + e
            }
            """);
        Assert.Equal(7, r.U8("encoding"));
    }

    [Fact]
    public void A_const_fn_may_use_a_declared_encoding()
    {
        var r = Program($$"""
            {{Tiles}}
            const fn tile(n: int) -> int { return tiles'0' + n }
            var out: u8
            const T = tile(5)
            fn main() { out = T }
            """);
        Assert.Equal(5, r.U8("out"));
    }

    [Fact]
    public void An_asm_operand_may_use_a_declared_encoding()
    {
        var r = Program($$"""
            {{Tiles}}
            var out: u8
            fn main() {
                asm {
                    lda #tiles'B'
                    sta out
                }
            }
            """);
        Assert.Equal(0x0B, r.U8("out"));
    }

    // ---------- Modules ----------

    private const string Font = """
        encoding tiles { 'A'..='Z' = $80, ' ' = 0 }
        private encoding inner { 'A' = 9 }
        data NAME = inner"A"
        """;

    [Fact]
    public void A_modules_encoding_is_used_with_the_module_name()
    {
        var r = Program("""
            import font
            data T = font.tiles"HI A"
            var out: [u8; 5]
            fn main() {
                for i in 0..4 { out[i] = T[i] }
                out[4] = font.tiles'Z' + font.NAME[0]
            }
            """, ("font", Font));
        Assert.Equal([0x87, 0x88, 0, 0x80, 0x80 + 25 + 9], Bytes(r, "out", 5));
    }

    [Fact]
    public void A_base_may_be_a_modules_encoding()
    {
        var r = Program("""
            import font
            encoding mine: font.tiles { '!' = 1 }
            data T = mine"A!"
            var out: [u8; 2]
            fn main() { for i in 0..2 { out[i] = T[i] } }
            """, ("font", Font));
        Assert.Equal([0x80, 1], Bytes(r, "out", 2));
    }

    [Fact]
    public void The_default_encoding_applies_to_the_main_file_and_program_modules_but_not_library_modules()
    {
        Directory.CreateDirectory(LibPath);
        File.WriteAllText(Path.Combine(LibPath, "libtext.tern"), "data T = \"AB\"\nconst C = 'A'\n");
        Write(("mine", "data T = \"AB\"\nconst C = 'A'\n"));
        var r = Run.Program($$"""
            import libtext
            import mine
            {{Tiles}}
            default encoding tiles
            data T = "AB"
            var out: [u8; 9]
            fn main() {
                out[0] = T[0]
                out[1] = T[1]
                out[2] = 'A'
                out[3] = mine.T[0]
                out[4] = mine.T[1]
                out[5] = mine.C
                out[6] = libtext.T[0]
                out[7] = libtext.T[1]
                out[8] = libtext.C
            }
            """, sourcePath: MainPath, libraryDirectory: LibPath);
        Assert.Equal([0x0A, 0x0B, 0x0A, 0x0A, 0x0B, 0x0A, 0x41, 0x42, 0x41], Bytes(r, "out", 9));
    }

    [Fact]
    public void A_prefix_still_overrides_the_default()
    {
        var r = Program($$"""
            {{Tiles}}
            default encoding tiles
            var out: [u8; 2]
            fn main() {
                out[0] = petscii'A'
                out[1] = screen'A'
            }
            """);
        Assert.Equal([0x41, 0x01], Bytes(r, "out", 2));
    }

    [Fact]
    public void The_default_may_be_a_modules_encoding_or_a_built_in_one()
    {
        var r = Program("""
            import font
            default encoding font.tiles
            var out: u8
            fn main() { out = 'B' }
            """, ("font", Font));
        Assert.Equal(0x81, r.U8("out"));

        r = Program("""
            default encoding screen
            var out: u8
            fn main() { out = 'B' }
            """);
        Assert.Equal(0x02, r.U8("out"));
    }

    [Fact]
    public void The_statusline_example_writes_its_text_in_its_own_character_set()
    {
        var path = AsmTests.FindExample("statusline.tern");
        var program = TernCompiler.Compile(File.ReadAllText(path), sourcePath: path);
        Assert.Empty(program.Warnings);
        // main ends in an endless loop, as on a C64 with its own font.
        var ram = Tools.Runner.Run(program, new Tools.RunOptions { MaxCycles = 300_000 }).Ram.Bytes;
        // hudfont's glyphs: ' ' 0, '0'-'9' 1-10, C E I L O R S V 11-18, '♥' 19.
        byte Glyph(char c) => c switch { ' ' => 0, >= '0' and <= '9' => (byte)(c - '0' + 1), '♥' => 19, _ => (byte)("CEILORSV".IndexOf(c) + 11) };
        Assert.Equal("SCORE 01250".Select(Glyph).ToArray(), ram[(0x0400 + 10 * 40 + 12)..(0x0400 + 10 * 40 + 23)]);
        Assert.Equal("LIVES ♥♥♥".Select(Glyph).ToArray(), ram[(0x0400 + 12 * 40 + 12)..(0x0400 + 12 * 40 + 21)]);
        Assert.All(ram[0x0400..(0x0400 + 10 * 40)], b => Assert.Equal(0, b));
        Assert.Equal([2, 2, 2], ram[(0xD800 + 12 * 40 + 18)..(0xD800 + 12 * 40 + 21)]);   // red hearts
        Assert.Equal(0x18, ram[0xD018]);
        // The glyphs are at $2000: '0' is glyph 1, the heart glyph 19.
        Assert.Equal([0x3C, 0x66, 0x6E, 0x76, 0x66, 0x66, 0x3C, 0x00], ram[0x2008..0x2010]);
        Assert.Equal([0x36, 0x7F, 0x7F, 0x7F, 0x3E, 0x1C, 0x08, 0x00], ram[(0x2000 + 19 * 8)..(0x2000 + 20 * 8)]);
    }

    // ---------- Errors ----------

    [Theory]
    [InlineData("encoding t { 'A' = 1, 'B' = 2, 'A' = 3 }", "'A' is listed twice in the encoding 't', also at line 1. Remove one", 1, 32)]
    [InlineData("encoding t {\n'C' = 1\n'A'..='Z' = 2\n}", "'C' is listed twice in the encoding 't', also at line 2. Remove one", 3, 1)]
    [InlineData("encoding t { 'A'..='Z' = $E7 }", "The range 'A'..='Z' has 26 characters, so from $E7 its bytes pass $FF. Start it at $E6 or lower, or split the range", 1, 14)]
    [InlineData("encoding t { 'A' = 256 }", "256 is not a byte; an encoding gives each character one byte, $00 to $FF. Use a value in that range", 1, 20)]
    [InlineData("encoding t { 'A' = -1 }", "-1 is not a byte", 1, 20)]
    [InlineData("var v: u8\nencoding t { 'A' = v }", "An encoding's byte must be known at compile time. Use a number or a constant", 2, 20)]
    [InlineData("encoding t: scren { }", "Unknown encoding 'scren'. Use one of: petscii, petscii_mixed, screen, screen_mixed, t", 1, 13)]
    [InlineData("encoding a: b { }\nencoding b: a { }", "The encoding 'a' is based on itself, through 'b'. Change the base of one of them, or remove it", 1, 1)]
    [InlineData("encoding a: a { }", "The encoding 'a' is based on itself, through 'a'", 1, 1)]
    [InlineData("encoding screen { 'A' = 1 }", "'screen' is a built-in encoding, so the prefix screen\"...\" would have two meanings. Rename the encoding", 1, 1)]
    [InlineData("var t: u8\nencoding t { }", "'t' is already declared at", 1, 1)]
    [InlineData("encoding t { }\nconst t = 1", "'t' is already declared at", 2, 1)]
    [InlineData("var v: u8\ndata d = v\"A\"", "'v' is a variable, not an encoding. Use an encoding's name before the quote", 2, 10)]
    [InlineData("encoding t { 'A' = 1 }\ndata d = t\"AB\"", "'B' has no code in the t encoding. Write the byte as \\$NN", 2, 10)]
    [InlineData("encoding t { 'A' = 1 }\nvar c: u8 = t'\\n'", "'\\n' has no code in the t encoding. Write the byte as \\$NN", 2, 13)]
    [InlineData("encoding t: screen { 'A' = 1 }\ndata d = t\"A\\n\"", "'\\n' has no code in the t encoding", 2, 10)]
    [InlineData("encoding t { 'A'..'Z' = 1 }", "A range of characters includes its last one. Write '..=', as in 'a'..='z'", 1, 17)]
    [InlineData("encoding t { 'Z'..='A' = 1 }", "The range 'Z'..='A' goes down. Write the lower character first: 'A'..='Z'", 1, 14)]
    [InlineData("encoding t { screen'A' = 1 }", "An encoding lists characters, not bytes of another encoding. Remove 'screen' before the quote", 1, 14)]
    [InlineData("encoding t { '\\$41' = 1 }", "'\\0' and '\\$NN' are bytes, not characters, so an encoding has no code to give them. Write a character, such as 'A' or '\\n'", 1, 14)]
    [InlineData("encoding t { 'A' 1 }", "Expected '= byte' after the character", 1, 18)]
    [InlineData("encoding t { 'A' = 1 'B' = 2 }", "Expected ',' or a line break after the byte", 1, 22)]
    [InlineData("encoding t { 'A' = 1", "Encoding 't' is not closed with '}'", 1, 1)]
    [InlineData("encoding t { A = 1 }", "Expected a character in quotes, such as 'A'", 1, 14)]
    [InlineData("default encoding tiles", "Unknown encoding 'tiles'. Use one of: petscii, petscii_mixed, screen, screen_mixed", 1, 18)]
    [InlineData("encoding t { }\ndefault encoding t\ndefault encoding screen", "The program has one 'default encoding' line, at line 2. Remove one", 3, 1)]
    [InlineData("data d = font.tiles\"A\"", "'font' is not declared. Add 'import font' at the top of the file to use the module font", 1, 10)]
    public void Reports_errors(string source, string expected, int line, int column)
    {
        var e = Error(source + "\nfn main() { }");
        Assert.StartsWith(expected, e.Description);
        Assert.Equal((line, column), (e.Pos.Line, e.Pos.Column));
    }

    [Fact]
    public void An_error_in_an_encoding_is_reported_once()
    {
        var e = Error("""
            encoding t { 'A' = 256 }
            encoding u: t { }
            data d = t"A"
            data e = u"A"
            fn main() { }
            """);
        Assert.Single(e.All);
    }

    [Fact]
    public void A_private_encoding_cannot_be_used_outside_its_module()
    {
        var e = Error("""
            import font
            data d = font.inner"A"
            fn main() { }
            """, ("font", Font));
        Assert.Equal("'font.inner' is private to the module font; only code in font can use it. Remove 'private' there", e.Description);
        Assert.Equal((2, 10), (e.Pos.Line, e.Pos.Column));
    }

    [Fact]
    public void An_unknown_encoding_of_a_module_is_an_error()
    {
        var e = Error("""
            import font
            data d = font.tilez"A"
            fn main() { }
            """, ("font", Font));
        Assert.StartsWith("Unknown encoding 'font.tilez'", e.Description);
    }

    [Fact]
    public void Default_encoding_outside_the_main_file_is_an_error()
    {
        var e = Error("""
            import other
            fn main() { other.f() }
            """, ("other", "default encoding screen\nfn f() { }"));
        Assert.Equal("'default encoding' sets the encoding of the whole program's text, so it goes in the main file; 'other' is a module. " +
            "Move the line to the main file, or write the prefix on each literal (screen\"...\")", e.Description);
        Assert.Equal((1, 1), (e.Pos.Line, e.Pos.Column));
        Assert.EndsWith("other.tern", e.Pos.File);
    }

    // ---------- Warnings ----------

    [Fact]
    public void An_unused_encoding_is_warned()
    {
        // A module's public encodings are its interface, as its other declarations (spec 13).
        Assert.Equal(["3: 'unused' is never used. Remove it, or start its name with '_' to keep it",
                      "font.tern:2: 'hidden' is never used. Remove it, or start its name with '_' to keep it"],
            Warnings("""
                import font
                data D = font.tiles"A"
                encoding unused { 'A' = 1 }
                encoding _kept { 'A' = 1 }
                var out: u8
                fn main() { out = D[0] }
                """, ("font", "encoding tiles { 'A' = 1 }\nprivate encoding hidden { 'A' = 2 }\nencoding open { 'A' = 3 }")));
    }

    [Fact]
    public void An_encoding_used_by_a_literal_a_base_the_default_or_a_const_fn_is_not_warned()
    {
        Assert.Empty(Warnings("""
            import font
            encoding a { 'A' = 1 }
            encoding b: a { 'B' = 2 }
            encoding c { 'C' = 3 }
            encoding d { 'D' = 4 }
            default encoding c
            const fn _f() -> int { return d'D' }
            data T = b"AB"
            var out: u8
            fn main() { out = T[0] + 'C' + font.tiles'A' }
            """, ("font", "encoding tiles { 'A' = 1 }")));
    }

    [Fact]
    public void An_import_used_only_by_the_default_encoding_is_not_warned()
    {
        Assert.Empty(Warnings("""
            import font
            default encoding font.tiles
            var out: u8
            fn main() { out = 'A' }
            """, ("font", "encoding tiles { 'A' = 1 }")));
    }
}
