using Tern.Compiler.Syntax;
using Tern.Compiler.Tools;
using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>lib/atari (spec 12.2): text through CIO, interrupts, display lists.</summary>
public class AtariLibraryTests
{
    private static Run RunAtari(string source, Action<Ram>? setup = null) => Run.Program(source, setup: setup, platform: Platform.Atari,
        traps: new Dictionary<int, Action<Run>> { [0xE45C] = Setvbv });

    /// <summary>SETVBV, as the OS does it for A = 6 (immediate) and 7 (deferred): Y low, X high.</summary>
    private static void Setvbv(Run r)
    {
        int vector = r.Cpu.A == 6 ? 0x0222 : 0x0224;
        r.Ram.Bytes[vector] = r.Cpu.Y;
        r.Ram.Bytes[vector + 1] = r.Cpu.X;
    }

    /// <summary>
    /// Raises an NMI after main returned, through a stand-in for the OS's entry placed at $E000, and runs
    /// until the handler returns to where it interrupted. Checks that A, X, Y and the stack are as before.
    /// </summary>
    private static void Nmi(Run r, params byte[] osEntry)
    {
        osEntry.CopyTo(r.Ram.Bytes, 0xE000);
        r.Ram.Bytes[0xFFFA] = 0x00;
        r.Ram.Bytes[0xFFFB] = 0xE0;
        var (a, x, y, sp, pc) = (r.Cpu.A = 0x11, r.Cpu.X = 0x22, r.Cpu.Y = 0x33, r.Cpu.SP, r.Cpu.PC);
        r.Cpu.Nmi();
        for (int i = 0; i < 1000 && r.Cpu.PC != pc; i++) r.Cpu.Step();
        Assert.Equal((a, x, y, sp, pc), (r.Cpu.A, r.Cpu.X, r.Cpu.Y, r.Cpu.SP, r.Cpu.PC));
    }

    /// <summary>The OS's VBI entry: push A, X, Y, then jump through VVBLKI or VVBLKD.</summary>
    private static byte[] VbiEntry(int vector) => [0x48, 0x8A, 0x48, 0x98, 0x48, 0x6C, (byte)vector, (byte)(vector >> 8)];

    [Fact]
    public void Put_print_and_cursor_write_through_the_screen_editor()
    {
        var run = Runner.Run(TernCompiler.Compile("""
            import atari
            data HELLO = "Hello"
            fn main() {
                atari.print(addr(HELLO), len(HELLO))
                atari.put('!')
                atari.put('\n')
                atari.cursor(5, 10)
                atari.put('x')
            }
            """, Platform.Atari));
        Assert.True(run.Returned, run.Stop);
        Assert.Equal("Hello!\nx", run.Output);
        var screen = Runner.ScreenText(run);
        Assert.Equal("  Hello!", screen[0]);
        Assert.Equal(new string(' ', 10) + "x", screen[5]);
    }

    [Fact]
    public void Put_costs_one_call_into_cio()
    {
        var r = TernCompiler.Compile("import atari\nfn main() { atari.put('A') }\n", Platform.Atari);
        var put = r.Listing.Replace("\r", "").Split("atari.put:\n")[1].Split("\n").TakeWhile(l => !l.EndsWith(':')).ToList();
        Assert.Equal(6, put.Count(l => l.StartsWith('$')));
        Assert.Contains(put, l => l.Contains("jmp $E456"));
    }

    [Fact]
    public void Set_dli_installs_the_handler_and_turns_dlis_on()
    {
        var r = RunAtari("""
            import atari
            var hits: u8
            fn main() { atari.set_dli(addr(dli)) }
            interrupt fn dli() {
                hits += 1
                atari.gtia.colbk = hits
            }
            """);
        Assert.Equal(r.Address("dli"), r.Ram.Bytes[0x0200] | r.Ram.Bytes[0x0201] << 8);
        Assert.Equal(0xC0, r.Ram.Bytes[0xD40E]);
        // The OS jumps through VDSLST with only the CPU's push.
        Nmi(r, 0x6C, 0x00, 0x02);
        Nmi(r, 0x6C, 0x00, 0x02);
        Assert.Equal(2, r.U8("hits"));
        Assert.Equal(2, r.Ram.Bytes[0xD01A]);
    }

    [Theory]
    [InlineData("set_vbi", 0x0224)]
    [InlineData("set_vbi_immediate", 0x0222)]
    public void A_vbi_handler_runs_after_the_os_pushed_its_registers(string install, int vector)
    {
        var r = RunAtari($$"""
            import atari
            var frames: u8
            fn main() { atari.{{install}}(addr(frame)) }
            interrupt fn frame() { frames += 1 }
            """);
        Nmi(r, VbiEntry(vector));
        Nmi(r, VbiEntry(vector));
        Assert.Equal(2, r.U8("frames"));
    }

    [Fact]
    public void Set_display_list_writes_sdlst_and_leaves_sdmctl_as_it_was()
    {
        var r = RunAtari("""
            import atari
            const LIST = $3000
            data list: [u8; _] @ LIST = atari.display_list(atari.MODE_TEXT, 24, $3400, LIST, 0)
            fn main() { atari.set_display_list(addr(list)) }
            """, ram => ram.Bytes[0x022F] = 0x22);
        Assert.Equal(0x3000, r.Ram.Bytes[0x0230] | r.Ram.Bytes[0x0231] << 8);
        Assert.Equal(0x22, r.Ram.Bytes[0x022F]);
    }

    [Fact]
    public void Wait_frame_returns_when_the_frame_counter_changes()
    {
        var result = TernCompiler.Compile("import atari\nvar done: u8\nfn main() {\n    atari.wait_frame()\n    done = 1\n}\n", Platform.Atari);
        var ram = new Ram();
        ram.Load((ushort)result.LoadAddress, result.Image);
        var cpu = new Cpu6502(ram) { PC = (ushort)result.Entry, SP = 0xFF };
        for (int i = 0; i < 2000; i++) cpu.Step();
        Assert.NotEqual(result.HaltAddress, cpu.PC);
        ram.Bytes[0x14]++;
        for (int i = 0; i < 100 && cpu.PC != result.HaltAddress; i++) cpu.Step();
        Assert.Equal(result.HaltAddress, cpu.PC);
        Assert.Equal(1, ram.Bytes[result.Symbols["done"]]);
    }

    [Fact]
    public void Exit_jumps_through_dosvec()
    {
        var result = TernCompiler.Compile("import atari\nfn main() { atari.exit() }\n", Platform.Atari);
        // DOS's address is the halt loop, so the run ends as if main had returned.
        var run = Runner.Run(result, new RunOptions { Sets = [new(0x0A, (byte)result.HaltAddress), new(0x0B, (byte)(result.HaltAddress >> 8))] });
        Assert.True(run.Returned, run.Stop);
        Assert.Contains("jmp (atari.dosvec)", result.Listing);
    }

    [Fact]
    public void A_text_display_list()
    {
        var r = TernCompiler.Compile("""
            import atari
            const LIST = $3000
            data list: [u8; _] @ LIST = atari.display_list(atari.MODE_TEXT, 24, $3400, LIST, (1 << 0) | (1 << 23))
            var n: u8
            fn main() { n = list[0] }
            """, Platform.Atari);
        var list = r.Segments.Single(s => s.Address == 0x3000).Bytes;
        byte[] expected = [0x70, 0x70, 0x70, 0xC2, 0x00, 0x34, .. Enumerable.Repeat((byte)0x02, 22), 0x82, 0x41, 0x00, 0x30];
        Assert.Equal(expected, list);
    }

    [Theory]
    [InlineData(0x0F, 102, 0x4000, 0x3000, 3 + 3 + 101 + 3)]    // 102 rows of 40 bytes: up to $4FEF
    [InlineData(0x07, 12, 0x4000, 0x3000, 3 + 3 + 11 + 3)]
    public void Other_modes_and_sizes(int mode, int rows, int screen, int at, int size)
    {
        var r = TernCompiler.Compile($$"""
            import atari
            data list: [u8; _] @ {{at}} = atari.display_list({{mode}}, {{rows}}, {{screen}}, {{at}}, 0)
            var n: u8
            fn main() { n = list[0] }
            """, Platform.Atari);
        Assert.Equal(size, r.Segments.Single(s => s.Address == at).Bytes.Length);
    }

    [Theory]
    [InlineData("atari.display_list(2, 24, $3F00, $3000, 0)", "atari.display_list: the screen crosses a 4 KB boundary. Move it, or split it with a second LMS")]
    [InlineData("atari.display_list(2, 24, $4000, $33F0, 0)", "atari.display_list: the list crosses a 1 KB boundary. Place it at another address")]
    [InlineData("atari.display_list(1, 24, $4000, $3000, 0)", "atari.row_bytes: an ANTIC mode is 2 to 15")]
    public void Display_list_errors(string call, string message)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("import atari\ndata list: [u8; _] = " + call + "\nvar n: u8\nfn main() { n = list[0] }\n", Platform.Atari));
        // The call's place follows the first sentence.
        int dot = message.IndexOf(". ");
        Assert.Equal(dot < 0 ? message + " (called from line 2)" : message[..dot] + " (called from line 2)" + message[dot..], e.Description);
    }
}
