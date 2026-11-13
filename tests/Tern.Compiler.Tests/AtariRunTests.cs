using Tern.Compiler.Tools;

namespace Tern.Compiler.Tests;

/// <summary><c>tern run</c> on the Atari (spec 12.2): ROM, and the stand-ins for CIO's screen output and SETVBV.</summary>
public class AtariRunTests
{
    /// <summary>IOCB 0's command, buffer and length, and CIOV with X = 0.</summary>
    private const string Cio = """
        var iccom: u8 @ $0342
        var icbuf: u16 @ $0344
        var iclen: u16 @ $0348
        fn ciov(ch: u8 using A, 0 using X) @ $E456
        """;

    private static RunResult RunAtari(string source, RunOptions? options = null) =>
        Runner.Run(TernCompiler.Compile(source, Platform.Atari), options);

    [Fact]
    public void Put_bytes_on_the_screen_editor_prints_a_buffer_or_the_byte_in_a()
    {
        var run = RunAtari(Cio + """

            data HELLO = "Hello, Atari\n"
            fn main() {
                iccom = $0B
                icbuf = addr(HELLO)
                iclen = len(HELLO)
                ciov(0)
                iclen = 0
                ciov('!')
            }
            """);
        Assert.True(run.Returned, run.Stop);
        Assert.Equal("Hello, Atari\n!", run.Output);
        // The left margin is column 2, as the OS sets it.
        Assert.Equal(["  Hello, Atari", "  !"], Runner.ScreenText(run).Take(2));
        Assert.Equal((0xBC40, 40, 24), Runner.Screen(run));
        Assert.Equal(0x28, run.Ram.Bytes[0xBC42]);   // 'H' as an internal code
        Assert.Equal(1, run.Cpu.Y);                  // success
    }

    [Fact]
    public void Lines_wrap_at_the_right_margin()
    {
        var run = RunAtari(Cio + """

            data LINE = "0123456789012345678901234567890123456789\n"
            fn main() {
                iccom = $0B
                icbuf = addr(LINE)
                iclen = len(LINE)
                ciov(0)
            }
            """);
        Assert.True(run.Returned, run.Stop);
        // Columns 2 to 39 hold 38 characters; the 39th and 40th go to the next line.
        Assert.Equal(["  01234567890123456789012345678901234567", "  89", ""], Runner.ScreenText(run).Take(3));
    }

    [Fact]
    public void The_screen_scrolls_at_the_bottom()
    {
        var run = RunAtari(Cio + """

            data LINE = "ab\n"
            fn main() {
                iccom = $0B
                for i in 0..30 {
                    icbuf = addr(LINE)
                    iclen = len(LINE)
                    ciov(0)
                    iclen = 0
                    ciov('A' + i)
                }
            }
            """);
        Assert.True(run.Returned, run.Stop);
        var screen = Runner.ScreenText(run);
        // Line 0 had "ab", then each line starts with the letter printed after the previous line ended.
        Assert.Equal("  Gab", screen[0]);
        Assert.Equal("  ]ab", screen[^2]);
        Assert.Equal("  ^", screen[^1]);
    }

    [Fact]
    public void Clear_and_cursor_moves()
    {
        var run = RunAtari(Cio + """

            fn main() {
                iccom = $0B
                iclen = 0
                ciov('A')
                ciov($7D)       // clear
                ciov('B')
                ciov($1D)       // down
                ciov($1F)       // right
                ciov('C')
                ciov($1E)       // left, onto the C
                ciov('D')
                ciov('E')
                ciov($7E)       // backspace: clears the E
            }
            """);
        Assert.True(run.Returned, run.Stop);
        Assert.Equal(["  B", "    D", ""], Runner.ScreenText(run).Take(3));
        Assert.Equal("ABCDE", run.Output);
    }

    [Fact]
    public void Other_cio_commands_stop_the_run()
    {
        var run = RunAtari(Cio + """

            fn main() {
                iccom = $03     // open
                ciov(0)
            }
            """);
        Assert.False(run.Returned);
        Assert.Equal("CIO command $03 on IOCB 0, which the emulator does not have (stand-in: put bytes, $0B, on IOCB 0, the screen editor)", run.Stop);
    }

    [Fact]
    public void Another_os_routine_stops_the_run()
    {
        var run = RunAtari("fn siov() @ $E459\nfn main() { siov() }\n");
        Assert.False(run.Returned);
        Assert.StartsWith("reached $E459 in ROM, which the emulator does not have (stand-ins: CIOV $E456", run.Stop);
    }

    [Fact]
    public void Ram_up_to_bfff_is_not_rom()
    {
        var run = RunAtari("""
            memory { $A000..=$AFFF program }
            var n: u8
            fn main() { n = 3 }
            """);
        Assert.True(run.Returned, run.Stop);
    }

    [Theory]
    [InlineData(6, 0x0222)]
    [InlineData(7, 0x0224)]
    public void Setvbv_sets_a_vbi_vector(int which, int vector)
    {
        var run = RunAtari($$"""
            fn setvbv({{which}} using A, high: u8 using X, low: u8 using Y) @ $E45C
            fn main() { setvbv($12, $34) }
            """);
        Assert.True(run.Returned, run.Stop);
        Assert.Equal([0x34, 0x12], run.Ram.Bytes[vector..(vector + 2)]);
    }

    [Fact]
    public void Tern_run_shows_the_atari_screen()
    {
        var dir = Directory.CreateTempSubdirectory("tern-atari-");
        try
        {
            var path = Path.Combine(dir.FullName, "hi.tern");
            File.WriteAllText(path, Cio + "\ndata HI = \"HI\"\nfn main() {\n    iccom = $0B\n    icbuf = addr(HI)\n    iclen = 2\n    ciov(0)\n}\n");
            var output = new StringWriter();
            Assert.Equal(0, Cli.Run(["run", "--target", "atari", path, "--screen"], output, output));
            var text = output.ToString().Replace("\r", "");
            Assert.Contains($"Screen ($BC40, 40 x 24):\n  |{"  HI",-40}|\n", text);
        }
        finally { dir.Delete(recursive: true); }
    }
}
