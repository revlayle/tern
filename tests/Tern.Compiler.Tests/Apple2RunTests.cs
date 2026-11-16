using Tern.Compiler.Tools;

namespace Tern.Compiler.Tests;

/// <summary><c>tern run</c> on the Apple II (spec 12.2): the stand-ins for COUT, HOME and the MLI, and the screen's row order.</summary>
public class Apple2RunTests
{
    private const string Rom = """
        fn cout(ch: u8 using A) @ $FDED
        fn home() @ $FC58
        var ch: u8 @ $24
        var cv: u8 @ $25
        var invflg: u8 @ $32
        """;

    private static RunResult RunApple2(string source) => Runner.Run(TernCompiler.Compile(source, Platform.Apple2));

    [Fact]
    public void Cout_prints_at_the_cursor_and_the_screen_rows_are_in_the_apple_iis_order()
    {
        var run = RunApple2(Rom + """

            data LINES = "0\n1\n2\n3\n4\n5\n6\n7\n8\n"
            fn main() {
                for i in 0..len(LINES) {
                    cout(LINES[i])
                }
            }
            """);
        Assert.True(run.Returned, run.Stop);
        Assert.Equal("0\n1\n2\n3\n4\n5\n6\n7\n8\n", run.Output);
        Assert.Equal(["0", "1", "2", "3", "4", "5", "6", "7", "8", ""], Runner.ScreenText(run).Take(10));
        Assert.Equal((0x0400, 40, 24), Runner.Screen(run));
        Assert.Equal(0xB0, run.Ram.Bytes[0x0400]);   // row 0
        Assert.Equal(0xB1, run.Ram.Bytes[0x0480]);   // row 1
        Assert.Equal(0xB8, run.Ram.Bytes[0x0428]);   // row 8
        Assert.Equal(0xA0, run.Ram.Bytes[0x0401]);   // a blank is a space with the high bit set
        Assert.Equal(9, run.Ram.Bytes[0x25]);        // CV
        Assert.Equal(0, run.Ram.Bytes[0x24]);        // CH
        Assert.Equal(0xA8, run.Ram.Bytes[0x28]);     // BASL: row 9 at $0400 + $80 + $28 = $04A8
        Assert.Equal(0x04, run.Ram.Bytes[0x29]);
    }

    [Fact]
    public void A_full_line_wraps_and_the_bottom_line_scrolls()
    {
        var run = RunApple2(Rom + """

            data LINE = "0123456789012345678901234567890123456789"
            fn main() {
                for _ in 0..23 {
                    cout('\n')
                }
                for i in 0..len(LINE) {
                    cout(LINE[i])
                }
                cout('X')
            }
            """);
        Assert.True(run.Returned, run.Stop);
        var screen = Runner.ScreenText(run);
        Assert.Equal("0123456789012345678901234567890123456789", screen[22]);
        Assert.Equal("X", screen[23]);
    }

    [Fact]
    public void Invflg_makes_text_inverse_and_control_codes_below_80_are_stored_as_they_are()
    {
        var run = RunApple2(Rom + """

            fn main() {
                invflg = $3F
                cout('A')
                invflg = $FF
                cout('B')
                cout($01)
                cout($87)    // the bell does nothing
            }
            """);
        Assert.True(run.Returned, run.Stop);
        Assert.Equal([0x01, 0xC2, 0x01, 0xA0], run.Ram.Bytes[0x0400..0x0404]);
        Assert.Equal("ABA", run.Output);
        Assert.Equal("ABA", Runner.ScreenText(run)[0]);
    }

    [Fact]
    public void Backspace_and_line_feed_move_the_cursor()
    {
        var run = RunApple2(Rom + """

            fn main() {
                cout('A')
                cout('B')
                cout($88)
                cout('C')
                cout($8A)
                cout('D')
            }
            """);
        Assert.True(run.Returned, run.Stop);
        Assert.Equal(["AC", "  D"], Runner.ScreenText(run).Take(2));
    }

    [Fact]
    public void Home_clears_the_screen_and_puts_the_cursor_at_the_top_left()
    {
        var run = RunApple2(Rom + """

            fn main() {
                cout('A')
                cout('\n')
                cout('B')
                home()
                cout('C')
            }
            """);
        Assert.True(run.Returned, run.Stop);
        Assert.Equal(["C", ""], Runner.ScreenText(run).Take(2));
    }

    /// <summary>Code in page 3 that calls the MLI: jsr $BF00, the command, the parameters' address, then lda #7, sta $0310, rts.</summary>
    private static string MliCall(int command) => $$"""
        memory { $0300..=$033F reserved }
        data CALL: [u8; 13] @ $0300 = [$20, $00, $BF, ${{command:X2}}, $20, $03, $A9, $07, $8D, $10, $03, $60, $00]
        data PARAMETERS: [u8; 2] @ $0320 = [1, 0]
        fn call() @ $0300
        var after: u8 @ $0310
        var done: u8
        fn main() {
            call()
            done = 1
        }
        """;

    [Fact]
    public void Mli_close_returns_after_its_parameters()
    {
        var run = RunApple2(MliCall(0xCC));
        Assert.True(run.Returned, run.Stop);
        Assert.Equal(7, run.Ram.Bytes[0x0310]);
        Assert.False(run.Cpu.C);
    }

    [Fact]
    public void Mli_quit_ends_the_run()
    {
        var run = RunApple2(MliCall(0x65));
        Assert.False(run.Returned);
        Assert.Equal("the program quit (MLI QUIT)", run.Stop);
        Assert.Equal(Seeded.At(0x0310), run.Ram.Bytes[0x0310]);
    }

    [Fact]
    public void An_mli_call_without_a_stand_in_stops_the_run()
    {
        var run = RunApple2(MliCall(0xC5));
        Assert.False(run.Returned);
        Assert.Equal("MLI call $C5, which the emulator does not have (stand-ins: QUIT $65, GET_PREFIX $C7, SET_PREFIX $C6, and GET_FILE_INFO $C4, OPEN $C8, GET_EOF $D1, READ $CA and CLOSE $CC for the build's overlay files)", run.Stop);
    }

    [Theory]
    [InlineData(0xC0)]      // CREATE
    [InlineData(0xCB)]      // WRITE
    [InlineData(0xD0)]      // SET_EOF
    public void An_mli_call_that_writes_stops_the_run(int command)
    {
        var run = RunApple2(MliCall(command));
        Assert.False(run.Returned);
        Assert.Equal($"MLI call ${command:X2}, which writes to the disk; the emulator only reads the build's overlay files", run.Stop);
    }

    [Fact]
    public void A_program_whose_areas_move_runs_from_its_system_file()
    {
        var run = RunApple2("""
            memory {
                $0800..=$08FF program
                $6000..=$BEFF program
            }
            """ + "\n" + Rom + """

            data TEXT: [u8; 200] = repeat(apple2'*', 200)
            fn main() {
                for i in 0..40 {
                    cout(TEXT[i + 150])
                }
            }
            """);
        Assert.True(run.Returned, run.Stop);
        Assert.Equal(new string('*', 40), Runner.ScreenText(run)[0]);
    }
}
