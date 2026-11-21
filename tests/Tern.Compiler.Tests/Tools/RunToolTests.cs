using System.Text.Json;
using Tern.Compiler.Tools;

namespace Tern.Compiler.Tests;

/// <summary>M19: <c>tern run</c>, the emulator run tool (<see cref="Runner"/>, <see cref="MemoryDump"/>, <see cref="RunReport"/>). Spec 13 (compiler design: the tools).</summary>
public class RunToolTests
{
    private static (CompileResult Program, RunResult Run) RunSource(string source, RunOptions? options = null)
    {
        var program = TernCompiler.Compile(source);
        return (program, Runner.Run(program, options));
    }

    [Fact]
    public void A_run_ends_when_main_returns_with_the_same_cycles_as_the_test_runner()
    {
        const string source = """
            var total: u16
            fn add(v: u8) { total += v as u16 }
            fn main() {
                for i in 0..=200 { add(i) }
            }
            """;
        var (program, run) = RunSource(source);
        var reference = Run.Of(program);   // the same program, not one compiled for TERN_TARGET
        Assert.True(run.Returned);
        Assert.Equal("main returned", run.Stop);
        Assert.Equal(reference.Cpu.Cycles, run.Cycles);
        Assert.Equal(200 * 201 / 2, run.Ram.Bytes[program.Symbols["total"]] | run.Ram.Bytes[program.Symbols["total"] + 1] << 8);
        Assert.True(run.MainCycles < run.Cycles);
        Assert.Equal(run.Cycles, run.Profile.Total);
    }

    [Fact]
    public void A_run_starts_from_the_seeded_memory_and_registers_with_a_blank_screen()
    {
        // Decision 2026-11-15 (question 171): as the test harnesses, not zero, so a read of memory the program never wrote shows.
        var (program, run) = RunSource("""
            var before: u8 @ $C000
            var seen: u8
            fn main() { seen = before }
            """);
        Assert.True(run.Returned);
        Assert.Equal(Seeded.At(0xC000), run.Ram.Bytes[program.Symbols["seen"]]);
        Assert.NotEqual(0, Seeded.At(0xC000));
        Assert.All(Runner.ScreenText(run), line => Assert.Equal("", line));
    }

    [Fact]
    public void Functions_get_their_calls_and_the_cycles_inside_them()
    {
        var (_, run) = RunSource("""
            var n: u8
            fn inner() { n += 1 }
            fn outer() {
                inner()
                inner()
            }
            fn main() {
                for _ in 0..3 { outer() }
            }
            """);
        var inner = Assert.Single(run.Functions, f => f.Name == "inner");
        var outer = Assert.Single(run.Functions, f => f.Name == "outer");
        var main = Assert.Single(run.Functions, f => f.Name == "main");
        Assert.Equal(6, inner.Calls);
        Assert.Equal(3, outer.Calls);
        Assert.Equal(1, main.Calls);
        // jsr is counted by the caller; inc n / rts by the callee.
        Assert.Equal(6 * (5 + 6), inner.Cycles);
        Assert.True(outer.Cycles > inner.Cycles);
        Assert.Equal(run.MainCycles, main.Cycles);
    }

    [Fact]
    public void Chrout_prints_to_the_output_and_the_screen()
    {
        var (_, run) = RunSource("""
            import c64
            data hello: [u8; 5] = "HELLO"
            fn main() {
                c64.chrout(147)
                c64.print(addr(hello), len(hello))
                c64.chrout(13)
                c64.chrout('1')
                c64.chrout('!')
            }
            """);
        Assert.Equal("HELLO\n1!", run.Output);
        var screen = Runner.ScreenText(run);
        Assert.Equal(25, screen.Count);
        Assert.Equal("HELLO", screen[0]);
        Assert.Equal("1!", screen[1]);
        Assert.Equal("", screen[2]);
    }

    [Fact]
    public void Printing_past_the_last_line_scrolls_the_screen()
    {
        var (_, run) = RunSource("""
            import c64
            fn main() {
                for i in 0..30 {
                    c64.chrout('A' + i)
                    c64.chrout(13)
                }
            }
            """);
        var screen = Runner.ScreenText(run);
        // 30 lines and a return after the last: the screen shows the last 24 and an empty line.
        Assert.Equal(((char)('A' + 6)).ToString(), screen[0]);
        Assert.Equal(((char)('A' + 29)).ToString(), screen[23]);
        Assert.Equal("", screen[24]);
    }

    [Fact]
    public void Plot_sets_and_reads_the_cursor_and_getin_returns_no_key()
    {
        var (program, run) = RunSource("""
            import c64
            var key: u8 = 99
            var at: u16
            fn main() {
                c64.cursor(3, 7)
                c64.chrout('X')
                at = c64.get_cursor()
                key = c64.getin()
            }
            """);
        Assert.Equal(new string(' ', 7) + "X", Runner.ScreenText(run)[3]);
        Assert.Equal(3, run.Ram.Bytes[program.Symbols["at"]]);
        Assert.Equal(8, run.Ram.Bytes[program.Symbols["at"] + 1]);
        Assert.Equal(0, run.Ram.Bytes[program.Symbols["key"]]);
    }

    [Fact]
    public void A_loop_that_never_ends_stops_at_the_cycle_limit()
    {
        var (_, run) = RunSource("var n: u8\nfn main() { while true { n += 1 } }", new RunOptions { MaxCycles = 10_000 });
        Assert.False(run.Returned);
        Assert.Equal("stopped after 10000 cycles; main had not returned", run.Stop);
        Assert.InRange(run.Cycles, 10_000, 10_010);
        // main is still running: its cycles so far count.
        Assert.Equal(1, Assert.Single(run.Functions, f => f.Name == "main").Calls);
    }

    [Fact]
    public void A_rom_routine_with_no_stand_in_stops_the_run()
    {
        var (_, run) = RunSource("import io\nfn main() { io.clrchn() }");
        Assert.False(run.Returned);
        Assert.Equal("reached $FFCC in ROM, which the emulator does not have (stand-ins: CHROUT $FFD2, GETIN $FFE4, PLOT $FFF0, SETNAM $FFBD, SETLFS $FFBA, LOAD $FFD5)", run.Stop);
    }

    /// <summary>A program with one overlay, level1, at $6000, built in a temporary folder and run.</summary>
    private static (CompileResult Program, RunResult Run) RunWithOverlay(string main)
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "level1.tern"), "data MARK: [u8; 3] = [7, 8, 9]\nfn first() -> u8 using A { return MARK[0] }");
            var path = Path.Combine(dir.FullName, "main.tern");
            var program = TernCompiler.Compile(main, sourcePath: path);
            return (program, Runner.Run(program));
        }
        finally { dir.Delete(recursive: true); }
    }

    private const string OverlayHeader = """
        import c64
        import io
        import level1 @ levels
        memory {
            $0801..=$5FFF  program
            $6000..=$6FFF  overlay levels
        }
        """;

    [Fact]
    public void Load_reads_the_overlay_whose_name_in_capitals_is_the_file_name()
    {
        var (program, run) = RunWithOverlay($$"""
            {{OverlayHeader}}
            data NAME = "LEVEL1"
            var failed: bool = true
            var got: u8
            fn main() {
                failed = c64.load(addr(NAME), len(NAME), 8)
                got = level1.first()
            }
            """);
        Assert.True(run.Returned);
        Assert.Equal(0, run.Ram.Bytes[program.Symbols["failed"]]);
        Assert.Equal(7, run.Ram.Bytes[program.Symbols["got"]]);
    }

    [Fact]
    public void Load_of_a_name_that_is_not_an_overlay_fails_with_carry_set()
    {
        var (program, run) = RunWithOverlay($$"""
            {{OverlayHeader}}
            data NAME = "LEVEL9"
            var failed: bool
            fn main() { failed = c64.load(addr(NAME), len(NAME), 8) }
            """);
        Assert.True(run.Returned);
        Assert.Equal(1, run.Ram.Bytes[program.Symbols["failed"]]);
        Assert.Equal(Seeded.At(0x6000), run.Ram.Bytes[0x6000]);
    }

    [Fact]
    public void Load_with_secondary_address_0_puts_the_file_at_the_address_given()
    {
        var (program, run) = RunWithOverlay($$"""
            {{OverlayHeader}}
            data NAME = "LEVEL1"
            var failed: bool = true
            var keep: u16 = addr(level1.first)      // keeps level1 in the build without calling it at $6000
            fn main() { failed = io.load(addr(NAME), len(NAME), 8, $7000 as ptr u8) }
            """);
        Assert.True(run.Returned);
        Assert.Equal(0, run.Ram.Bytes[program.Symbols["failed"]]);
        var file = program.Overlays.Single();
        Assert.Equal(file.Image, run.Ram.Bytes[0x7000..(0x7000 + file.Image.Length)]);
        Assert.Equal(Seeded.At(0x6000), run.Ram.Bytes[0x6000]);
    }

    [Fact]
    public void A_jump_into_empty_memory_stops_at_the_brk()
    {
        var (_, run) = RunSource("fn main() {\n asm {\n jmp $9000\n }\n }", new RunOptions { Sets = [new MemorySet(0x9000, 0)] });
        Assert.False(run.Returned);
        Assert.Equal("brk at $9000 (often a jump into empty memory)", run.Stop);
    }

    [Fact]
    public void Interrupts_go_through_the_vector()
    {
        var (program, run) = RunSource("""
            var ticks: u8
            var done: u8
            interrupt fn handler() { ticks += 1 }
            fn main() {
                asm {
                    sei
                    lda #handler.lo
                    sta $FFFE
                    lda #handler.hi
                    sta $FFFF
                    cli
                }
                for _ in 0..200 { done += 1 }
                asm {
                    sei
                }
            }
            """, new RunOptions { IrqEvery = 300 });
        Assert.True(run.Returned);
        Assert.True(run.Interrupts > 0);
        Assert.Equal(run.Interrupts, run.Ram.Bytes[program.Symbols["ticks"]]);
        Assert.Equal(run.Interrupts, Assert.Single(run.Functions, f => f.Name == "handler").Calls);
    }

    [Fact]
    public void A_binary_without_main_cannot_be_run()
    {
        var program = TernCompiler.Compile("var n: u8\nexport bump\nfn bump() { n += 1 }");
        // A RunError of its own (tools 18, R7): Cli reports only these as the user's problem.
        var e = Assert.Throws<RunError>(() => Runner.Run(program));
        Assert.Contains("A binary without main cannot be run on its own", e.Message);
    }

    private const string Values = """
        var b: u8 = 200
        var s: i8 = -5
        var w: u16 = 54321
        var sw: i16 = -1234
        var flag: bool = true
        var table: [u8; 6] = [1, 2, 3, 4, 5, 6]
        var high: u8 @ $FFF0
        fn main() {
            var local: u8 = 7
            b += local
        }
        """;

    [Theory]
    [InlineData("b", 1, 207L)]
    [InlineData("s", 1, -5L)]
    [InlineData("w", 2, 54321L)]
    [InlineData("sw", 2, -1234L)]
    [InlineData("flag", 1, 1L)]
    [InlineData("table", 6, null)]
    [InlineData("w:1", 1, null)]
    public void A_variable_dump_has_its_size_and_value(string what, int size, long? value)
    {
        var (program, run) = RunSource(Values);
        var d = MemoryDump.Parse(what, program);
        Assert.Equal(size, d.Size);
        Assert.Equal(program.Symbols[what.Split(':')[0]], d.Address);
        Assert.Equal(value, d.Value(run.Ram.Bytes));
    }

    [Theory]
    [InlineData("$C000", 0xC000, 1)]
    [InlineData("$C000-$C00F", 0xC000, 16)]
    [InlineData("$c000:4", 0xC000, 4)]
    [InlineData("49152:2", 0xC000, 2)]
    [InlineData("$FFFF", 0xFFFF, 1)]
    public void An_address_dump(string what, int address, int size)
    {
        var d = MemoryDump.Parse(what, TernCompiler.Compile(Values));
        Assert.Equal(address, d.Address);
        Assert.Equal(size, d.Size);
        Assert.Null(d.Variable);
    }

    [Theory]
    [InlineData("nope", "'nope' is not a variable of the program. Write a local as 'function.name'")]
    [InlineData("local", "'local' is not a variable of the program. Check the spelling: did you mean 'main.local'?")]
    [InlineData("$C010-$C000", "'$C010-$C000' ends before it starts. Write the lower address first")]
    [InlineData("$FFFF:2", "goes past $FFFF. Give a range that ends at $FFFF or below")]
    [InlineData("$G000", "is not an address or count. Write $C000 or 49152")]
    // Review M19, finding 3: no exception other than FormatException.
    [InlineData("", "'' is not a variable or an address. Write one, as in score, main.count, $C000-$C0FF or $C000:16")]
    [InlineData(" ", "is not a variable or an address. Write one, as in score")]
    [InlineData(":4", "':4' is not a variable or an address. Write one")]
    [InlineData("b:", "'' is not an address or count")]
    [InlineData("$C000:0", "'$C000:0' shows no bytes. Give a byte count of 1 or more")]
    [InlineData("b:0", "'b:0' shows no bytes. Give a byte count of 1 or more")]
    [InlineData("high:100", "'high:100' goes past $FFFF ('high' is at $FFF0). Give a smaller byte count")]
    public void A_bad_dump_says_what_is_wrong(string what, string expected)
    {
        var e = Assert.Throws<FormatException>(() => MemoryDump.Parse(what, TernCompiler.Compile(Values)));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void The_text_report()
    {
        var (program, run) = RunSource(Values);
        var dumps = new[] { "b", "s", "flag", "table", "$C000:2" }.Select(d => MemoryDump.Parse(d, program)).ToList();
        var lines = RunReport.Text(run, dumps, screen: true, profile: null).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Matches(@"^Main returned: \d+ cycles \(\d+ in main\)$", lines[0]);
        Assert.Matches(@"^A=\$[0-9A-F]{2} X=\$[0-9A-F]{2} Y=\$[0-9A-F]{2} SP=\$FF PC=\$[0-9A-F]{4}$", lines[1]);
        Assert.Equal($"b ${program.Symbols["b"]:X4} u8 = 207 ($CF)", lines[2]);
        Assert.Equal($"s ${program.Symbols["s"]:X4} i8 = -5 ($FB)", lines[3]);
        Assert.Equal($"flag ${program.Symbols["flag"]:X4} bool = 1 (true)", lines[4]);
        Assert.Equal($"table ${program.Symbols["table"]:X4} [u8; 6] (6 bytes):", lines[5]);
        Assert.Equal($"  ${program.Symbols["table"]:X4}  01 02 03 04 05 06", lines[6]);
        Assert.Equal("$C000 (2 bytes):", lines[7]);
        Assert.Equal($"  $C000  {Seeded.At(0xC000):X2} {Seeded.At(0xC001):X2}", lines[8]);
        Assert.Equal("Screen ($0400, 40 x 25):", lines[9]);
        Assert.Equal("  |" + new string(' ', 40) + "|", lines[10]);
        Assert.Equal(10 + 25, lines.Count(l => l.Length > 0));
    }

    [Fact]
    public void The_profile_in_the_text_report()
    {
        var (program, run) = RunSource("var n: u8\nfn bump() { n += 1 }\nfn main() {\n    bump()\n}\n");
        var text = RunReport.Text(run, [], screen: false, profile: LineTable.Rows(program, "game.tern", run.Profile));
        Assert.Contains("Functions (cycles include the functions they call):", text);
        Assert.Matches(@"  bump +1 calls +11 cycles", text);
        Assert.Contains("run cycles", text);
        Assert.Contains($"; total {run.Cycles} cycles", text);
    }

    [Fact]
    public void The_json_report()
    {
        var (program, run) = RunSource(Values);
        var dumps = new[] { "w", "table" }.Select(d => MemoryDump.Parse(d, program)).ToList();
        using var doc = JsonDocument.Parse(RunReport.Json(run, dumps, screen: true, profile: LineTable.Rows(program, "game.tern", run.Profile)));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.True(root.GetProperty("returned").GetBoolean());
        Assert.Equal("main returned", root.GetProperty("stop").GetString());
        Assert.Equal(run.Cycles, root.GetProperty("cycles").GetInt64());
        Assert.Equal(run.MainCycles, root.GetProperty("mainCycles").GetInt64());
        Assert.Equal(0xFF, root.GetProperty("registers").GetProperty("sp").GetInt32());
        var w = root.GetProperty("memory")[0];
        Assert.Equal("w", w.GetProperty("what").GetString());
        Assert.Equal("u16", w.GetProperty("type").GetString());
        Assert.Equal(54321, w.GetProperty("value").GetInt32());
        Assert.Equal([0x31, 0xD4], w.GetProperty("bytes").EnumerateArray().Select(b => b.GetInt32()));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("memory")[1].GetProperty("value").ValueKind);
        Assert.Equal(25, root.GetProperty("screen").GetArrayLength());
        Assert.Contains(root.GetProperty("functions").EnumerateArray(), f => f.GetProperty("name").GetString() == "main");
        var line = root.GetProperty("lines").EnumerateArray().First(l => l.GetProperty("source").GetString() == "b += local");
        Assert.Equal(10, line.GetProperty("line").GetInt32());
        Assert.Equal(1, line.GetProperty("runs").GetInt64());
    }

    [Fact]
    public void Set_writes_bytes_before_the_run()
    {
        // M20 follow-up: a joystick value for a game loop.
        const string source = """
            var joy: u8 @ $DC00
            var seen: u8
            var other: u8
            fn main() { seen = joy; other = joy ^ $FF }
            """;
        var (program, run) = RunSource(source, new RunOptions { Sets = [MemorySet.Parse("$DC00=$7B")] });
        Assert.Equal(0x7B, run.Ram.Bytes[program.Symbols["seen"]]);
        Assert.Equal(0x84, run.Ram.Bytes[program.Symbols["other"]]);
        Assert.Equal(new MemorySet(56320, 255), MemorySet.Parse("56320=255"));
        Assert.Equal(new MemorySet(0xC000, 1), MemorySet.Parse(" $C000 = 1 "));
    }

    [Theory]
    [InlineData("$DC00", "'$DC00' is not an address and a byte. Write them as $DC00=$FF")]
    [InlineData("$DC00=256", "'256' is not a byte (0 to 255)")]
    [InlineData("$10000=1", "'$10000' is not an address")]
    [InlineData("joy=1", "'joy' is not an address")]
    [InlineData("$DC00=", "'' is not a byte")]
    public void A_bad_set_says_what_is_wrong(string text, string message)
    {
        var e = Assert.Throws<FormatException>(() => MemorySet.Parse(text));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void Set_on_the_command_line()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tern-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "joy.tern");
            File.WriteAllText(file, "var joy: u8 @ $DC00\nvar seen: u8\nfn main() { seen = joy }\n");
            var output = new StringWriter();
            Assert.Equal(0, Cli.Run(["run", "--target", "c64", file, "--set", "$DC00=$7B", "--set", "$C000=1", "--dump", "seen", "--dump", "$C000"], output, output));
            Assert.Contains("seen $", output.ToString());
            Assert.Contains("u8 = 123 ($7B)", output.ToString());
            Assert.Contains("$C000 (1 byte):\n  $C000  01", output.ToString().Replace("\r", ""));
            var error = new StringWriter();
            Assert.Equal(1, Cli.Run(["run", "--target", "c64", file, "--set", "$DC00"], output, error));
            Assert.Equal("--set '$DC00' is not an address and a byte. Write them as $DC00=$FF. Run 'tern --help' for the options.", error.ToString().Trim());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
