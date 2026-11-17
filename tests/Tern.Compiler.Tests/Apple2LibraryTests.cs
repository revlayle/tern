using Tern.Compiler.Tools;
using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>lib/apple2 (spec 12.2, M26): text through COUT, hi-res graphics, keyboard, speaker, soft switches, the MLI; numbers in apple2 codes.</summary>
public class Apple2LibraryTests
{
    private static Run RunApple2(string source, Action<Ram>? setup = null) => Run.Program(source, setup: setup, platform: Platform.Apple2);

    private static RunResult RunTool(string source) => Runner.Run(TernCompiler.Compile(source, Platform.Apple2));

    /// <summary>The program below and above the hi-res pages, so that plot and clear draw on free memory.</summary>
    private const string OffTheHiresPages = """
        import apple2
        memory {
            $0800..=$1FFF program
            $6000..=$BEFF program
        }

        """;

    /// <summary>Hi-res row y's offset from the page's start.</summary>
    private static int HiresRow(int y) => 1024 * (y % 8) + 128 * (y / 8 % 8) + 40 * (y / 64);

    [Fact]
    public void Put_print_cursor_and_inverse_write_through_cout()
    {
        var run = RunTool("""
            import apple2
            data HELLO = "Hello"
            fn main() {
                apple2.print(addr(HELLO), len(HELLO))
                apple2.put('!')
                apple2.put('\n')
                apple2.cursor(9, 3)
                apple2.inverse()
                apple2.put('X')
                apple2.flashing()
                apple2.put('Y')
                apple2.normal()
                apple2.put('Z')
            }
            """);
        Assert.True(run.Returned, run.Stop);
        Assert.Equal("Hello!\nXYZ", run.Output);
        var screen = Runner.ScreenText(run);
        Assert.Equal("Hello!", screen[0]);
        Assert.Equal("   XYZ", screen[9]);
        // Row 9 is at $04A8; inverse X is $18, flashing Y $59, normal Z $DA.
        Assert.Equal([0x18, 0x59, 0xDA], run.Ram.Bytes[0x04AB..0x04AE]);
    }

    [Fact]
    public void Print_of_0_characters_prints_nothing()
    {
        var run = RunTool("""
            import apple2
            data HELLO = "Hello"
            fn main() {
                apple2.print(addr(HELLO), 0)
            }
            """);
        Assert.True(run.Returned, run.Stop);
        Assert.Equal("", run.Output);
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Put_is_cout_itself()
    {
        var r = TernCompiler.Compile("import apple2\nfn main() {\n    apple2.put('A')\n    apple2.put('B')\n}\n", Platform.Apple2);
        // COUT keeps A and Y, so nothing is saved around the calls.
        // One more is the startup's, which turns off the 80-column firmware.
        Assert.Equal(3, r.Listing.Split('\n').Count(l => l.Contains("jsr $FDED")));
    }

    [Theory]
    [InlineData(0x0000)]
    [InlineData(0x09AF)]
    [InlineData(0xABCD)]
    [InlineData(0xFFFF)]
    public void Numbers_are_written_in_apple2_codes(int value)
    {
        var r = RunApple2($$"""
            import text
            import wide
            import bcd
            var h8: [u8; 2]
            var h16: [u8; 4]
            var h32: [u8; 8]
            var d: [u8; 6]
            var n: u8
            var w: wide.U32
            var dw: [u8; 10]
            var nw: u8
            var score: [u8; 2]
            var sd: [u8; 4]
            var ns: u8
            fn main() {
                text.hex8(addr(h8), {{value & 0xFF}})
                text.hex16(addr(h16), {{value}})
                w.lo = {{value}}
                w.hi = {{value ^ 0xFFFF}}
                wide.hex32(addr(h32), addr(w))
                n = text.dec16s(addr(d), {{(short)value}})
                nw = wide.dec32(addr(dw), addr(w))
                score[0] = $34
                score[1] = $02
                ns = bcd.text(addr(sd), addr(score), 2)
            }
            """);
        // ASCII with the high bit set.
        string Text(string name, int n) => new(r.Ram.Bytes.AsSpan(r.Address(name), n).ToArray().Select(c => c >= 0x80 ? (char)(c & 0x7F) : '?').ToArray());
        Assert.Equal((value & 0xFF).ToString("X2"), Text("h8", 2));
        Assert.Equal(value.ToString("X4"), Text("h16", 4));
        Assert.Equal((value ^ 0xFFFF).ToString("X4") + value.ToString("X4"), Text("h32", 8));
        Assert.Equal(((short)value).ToString(), Text("d", r.U8("n")));
        Assert.Equal(((uint)(value ^ 0xFFFF) << 16 | (uint)value).ToString(), Text("dw", r.U8("nw")));
        Assert.Equal("234", Text("sd", r.U8("ns")));
    }

    [Fact]
    public void Float_text_has_an_apple2_sign_and_point()
    {
        var r = RunApple2("""
            import float
            var x: float.F32 = float.F32 { bytes = float.of(-1.875) }
            var t: [u8; 12]
            var n: u8
            fn main() {
                n = float.dec(addr(t), addr(x), 3)
            }
            """);
        Assert.Equal([0xAD, 0xB1, 0xAE, 0xB8, 0xB7, 0xB5], r.Ram.Bytes[r.Address("t")..(r.Address("t") + r.U8("n"))]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(6, 0)]
    [InlineData(7, 1)]
    [InlineData(255, 63)]
    [InlineData(256, 64)]
    [InlineData(262, 100)]
    [InlineData(263, 191)]
    [InlineData(279, 191)]
    public void Plot_sets_the_pixels_bit_on_the_draw_page(int x, int y)
    {
        foreach (int page in new[] { 0x20, 0x40 })
        {
            var r = RunApple2(OffTheHiresPages + $$"""
                fn main() {
                    apple2.draw_page = ${{page:X2}}
                    apple2.clear()
                    apple2.plot({{x}}, {{y}})
                    apple2.plot({{x}}, {{y}})
                }
                """);
            int at = page << 8 | HiresRow(y) + x / 7;
            Assert.Equal(1 << (x % 7), r.Ram.Bytes[at]);
            // The rest of the page is clear. (The system file stays at $2000 after the startup copied it, so a program clears page 1 before drawing.)
            Assert.Equal(1, r.Ram.Bytes.AsSpan(page << 8, 0x2000).ToArray().Count(b => b != 0));
        }
    }

    [Fact]
    public void Plot_leaves_the_other_pixels_of_the_byte()
    {
        var r = RunApple2(OffTheHiresPages + """
            fn main() {
                apple2.clear()
                apple2.plot(14, 5)
                apple2.plot(20, 5)
                apple2.plot(17, 5)
            }
            """);
        Assert.Equal(0b1001001, r.Ram.Bytes[0x2000 + HiresRow(5) + 2]);
    }

    [Fact]
    public void Clear_zeroes_the_draw_page_and_nothing_else()
    {
        var r = RunApple2(OffTheHiresPages + """
            fn main() {
                apple2.draw_page = $40
                apple2.clear()
            }
            """, ram => ram.Bytes.AsSpan(0x3F00, 0x2100).Fill(0xFF));
        Assert.All(r.Ram.Bytes[0x4000..0x6000], b => Assert.Equal(0, b));
        Assert.All(r.Ram.Bytes[0x3F00..0x4000], b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void The_hires_row_table_is_the_apple_iis_order()
    {
        var r = RunApple2("import apple2\nvar a: u16\nfn main() { a = apple2.HIRES_ROWS[0] }\n");
        int table = r.Address("apple2.HIRES_ROWS");
        for (int y = 0; y < 192; y++)
            Assert.Equal(HiresRow(y), r.Ram.Bytes[table + y] | r.Ram.Bytes[table + 192 + y] << 8);
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Soft_switches_are_written()
    {
        var r = TernCompiler.Compile("""
            import apple2
            fn main() {
                apple2.show_graphics()
                apple2.show_text()
                apple2.show_full()
                apple2.show_mixed()
                apple2.show_page1()
                apple2.show_page2()
                apple2.show_lores()
                apple2.show_hires()
            }
            """, Platform.Apple2);
        for (int a = 0xC050; a <= 0xC057; a++)
            Assert.Contains($"sta ${a:X4}", r.Listing.Replace("apple2.sw_graphics", "$C050").Replace("apple2.sw_text", "$C051")
                .Replace("apple2.sw_full", "$C052").Replace("apple2.sw_mixed", "$C053").Replace("apple2.sw_page1", "$C054")
                .Replace("apple2.sw_page2", "$C055").Replace("apple2.sw_lores", "$C056").Replace("apple2.sw_hires", "$C057"));
    }

    [Theory]
    [InlineData(0xC1, 0xC1)]
    [InlineData(0x41, 0)]
    public void Key_returns_the_waiting_key_and_takes_it(int kbd, int expected)
    {
        var r = RunApple2("""
            import apple2
            var k: u8
            fn main() {
                k = apple2.key()
            }
            """, ram => { ram.Bytes[0xC000] = (byte)kbd; ram.Bytes[0xC010] = 0x55; });
        Assert.Equal(expected, r.U8("k"));
        Assert.Equal(expected == 0 ? 0x55 : 0, r.Ram.Bytes[0xC010]);
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Click_and_tone_read_the_speaker()
    {
        var r = RunApple2("""
            import apple2
            fn main() {
                apple2.click()
                apple2.tone(10, 3)
            }
            """);
        Assert.Equal(2, r.Result.Listing.Split('\n').Count(l => l.Contains("2C 30 C0")));
    }

    [Fact]
    public void Mli_writes_the_command_and_the_parameters_after_its_call()
    {
        var run = RunTool("""
            import apple2
            data CLOSE: [u8; 2] = [1, 0]
            var result: u8 = $FF
            fn main() {
                result = apple2.mli(apple2.MLI_CLOSE, addr(CLOSE))
            }
            """);
        Assert.True(run.Returned, run.Stop);
        var compiled = TernCompiler.Compile("import apple2\ndata CLOSE: [u8; 2] = [1, 0]\nvar result: u8 = $FF\nfn main() { result = apple2.mli(apple2.MLI_CLOSE, addr(CLOSE)) }\n", Platform.Apple2);
        Assert.Equal(0, run.Ram.Bytes[compiled.Symbols["result"]]);
        int call = compiled.Symbols["apple2.mli"] + 9;
        Assert.Equal([0x20, 0x00, 0xBF, 0xCC], run.Ram.Bytes[call..(call + 4)]);
        Assert.Equal(compiled.Symbols["CLOSE"], run.Ram.Bytes[call + 4] | run.Ram.Bytes[call + 5] << 8);
    }

    [Fact]
    public void Exit_closes_the_files_and_quits()
    {
        var run = RunTool("""
            import apple2
            var after: u8
            fn main() {
                apple2.exit()
                after = 1
            }
            """);
        Assert.False(run.Returned);
        Assert.Equal("the program quit (MLI QUIT)", run.Stop);
        // The power-up byte no longer matches the reset vector, so reset does not come back.
        Assert.NotEqual(run.Ram.Bytes[0x3F3] ^ 0xA5, run.Ram.Bytes[0x3F4]);
    }

    /// <summary>Every module that is not platform-specific (spec 10.1), used once; on the C64 and the Apple II the variables must agree, apart from text.</summary>
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
        var digits: [u8; 8]
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
    public void Library_modules_that_are_not_platform_specific_work_unchanged()
    {
        var c64 = Run.Program(Modules);
        var apple = RunApple2(Modules);
        foreach (var v in c64.Result.Variables.Values.Where(v => v.Function is null && !v.IsData && !v.Name.Contains('.') && v.Name is not ("digits" or "ftext")))
            Assert.True(c64.Ram.Bytes.AsSpan(v.Address, v.Size).SequenceEqual(apple.Ram.Bytes.AsSpan(apple.Address(v.Name), v.Size)), v.Name);
        Assert.Equal(90000, apple.U16("a") | apple.U8("a", 2) << 16);
        // The same text, with the high bit set.
        Assert.Equal(c64.Ram.Bytes[c64.Address("digits")..(c64.Address("digits") + 5)].Select(c => (byte)(c | 0x80)), apple.Ram.Bytes[apple.Address("digits")..(apple.Address("digits") + 5)]);
        Assert.Equal("-1.875", string.Concat(apple.Ram.Bytes.AsSpan(apple.Address("ftext"), 6).ToArray().Select(c => (char)(c & 0x7F))));
    }

    [Fact]
    public void Hello_example_prints_squares_and_inverse_text()
    {
        var path = AsmTests.FindExample("hello.tern", "apple2");
        var run = Runner.Run(TernCompiler.Compile(File.ReadAllText(path), Platform.Apple2, sourcePath: path));
        Assert.True(run.Returned, run.Stop);
        Assert.Equal(["HELLO FROM TERN!", "SQUARES: 1 4 9 16 25 36 49 64", "", "", "INVERSE"], Runner.ScreenText(run).Take(5));
        Assert.Equal(0x09, run.Ram.Bytes[0x0600]);   // row 4: inverse 'I'
    }

    [Fact]
    public void Hires_example_runs_above_the_hires_pages_and_draws_its_frame()
    {
        var path = AsmTests.FindExample("hires.tern", "apple2");
        var result = TernCompiler.Compile(File.ReadAllText(path), Platform.Apple2, sourcePath: path);
        // Done when (roadmap M26): the code is above the hi-res pages, placed there by the memory block.
        Assert.All(result.Segments, s => Assert.True(s.Address >= 0x6000, $"${s.Address:X4}"));
        var run = Runner.Run(result);
        Assert.True(run.Returned, run.Stop);
        // The frame's corners: (20, 8), (259, 8), (20, 151), (259, 151).
        foreach (var (x, y) in new[] { (20, 8), (259, 8), (20, 151), (259, 151) })
            Assert.NotEqual(0, run.Ram.Bytes[0x2000 + HiresRow(y) + x / 7] & (1 << (x % 7)));
        Assert.Equal(0, run.Ram.Bytes[0x2000 + HiresRow(160)]);
        var screen = Runner.ScreenText(run);
        Assert.Equal(" TERN ON THE APPLE II: HI-RES AND TEXT", screen[21]);
        Assert.StartsWith(" PIXELS PLOTTED: ", screen[22]);
    }
}
