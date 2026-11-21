using Tern.Compiler.Tools;
using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>
/// <c>tern run</c>'s model of the machines, as the code-quality review found it lacking (review
/// code-quality-tools 9-12 and 16; decisions 2026-11-15).
/// Spec 13 (compiler design: the tools).
/// </summary>
public class RunModelTests
{
    private const string Nrom = "import nes\nmemory {\n    cartridge nrom vertical\n}\n";

    /// <summary>A NES bus for an NROM image with no code, and a CPU on it.</summary>
    private static (NesBus Bus, Cpu6502 Cpu) Nes()
    {
        var bus = new NesBus(new Ram(), Image(0));
        var cpu = new Cpu6502(bus, Platform.Nes.Cpu);
        return (bus, cpu);
    }

    /// <summary>An iNES image with one PRG bank and one CHR bank, no code, vertical mirroring and the given mapper.</summary>
    private static byte[] Image(int mapper)
    {
        var image = new byte[16 + 0x4000 + 0x2000];
        "NES\x1A"u8.CopyTo(image);
        image[4] = 1;
        image[5] = 1;
        image[6] = (byte)((mapper & 0x0F) << 4 | 1);
        image[7] = (byte)(mapper & 0xF0);
        return image;
    }

    // ---------- Mappers (tools 18, R7) ----------

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void The_modelled_mappers_run(int mapper) => Assert.NotNull(new NesBus(new Ram(), Image(mapper)));

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(18)]
    public void Another_mapper_is_an_error_not_a_run_as_nrom(int mapper)
    {
        var e = Assert.Throws<RunError>(() => new NesBus(new Ram(), Image(mapper)));
        Assert.Equal($"The cartridge has mapper {mapper}, which tern run does not model: it runs mappers 0 (NROM) and 2 (UxROM). Use an emulator such as Mesen to run it", e.Message);
    }

    // ---------- Each platform names its machine (tools 1) ----------

    /// <summary>A platform with no machine in tern run, as a new one starts: before R6 it ran with the C64's stand-ins and screen.</summary>
    [Fact]
    public void A_platform_with_no_machine_is_an_error_not_a_run_as_a_c64()
    {
        var other = PlatformDataTests.Like(Platform.C64, "c128", machine: false);
        var program = TernCompiler.Compile("var x: u8\nfn main() { x = 1 }", other);
        var e = Assert.Throws<RunError>(() => Runner.Run(program));
        Assert.Equal("tern run has no model of the C128. Use an emulator to run the program", e.Message);
    }

    /// <summary>The screen is read from the machine that ran, not a new one: the NES's from its PPU, the others' from RAM.</summary>
    [Fact]
    public void The_screen_is_the_machine_that_ran()
    {
        var run = Runner.Run(TernCompiler.Compile(Nrom + "encoding tiles { 'A' = 1 }\ndefault encoding tiles\nfn main() {\n    nes.ppu_fill($2000, 'A', 1)\n}", Platform.Nes));
        Assert.Equal("A", Runner.ScreenText(run)[0]);
        Assert.Equal((0x2000, 32, 30), Runner.Screen(run));
    }

    // ---------- OAM DMA through the bus (tools 15) ----------

    [Theory]
    [InlineData(0, 513)]
    [InlineData(1, 514)]
    public void Oam_dma_cycles_are_in_the_instructions_cycles(long start, int dma)
    {
        var (bus, cpu) = Nes();
        Assert.Same(cpu, bus.Cpu);
        bus.Write(0x0200, 0x8D); bus.Write(0x0201, 0x14); bus.Write(0x0202, 0x40);   // sta $4014
        (cpu.PC, cpu.A, cpu.Cycles) = (0x0200, 0x02, start);
        Assert.Equal(4 + dma, cpu.Step());
        Assert.Equal(start + 4 + dma, cpu.Cycles);
        Assert.Equal(1, bus.Dmas);
    }

    [Fact]
    public void A_bus_that_wraps_the_nes_bus_passes_the_cpu_on()
    {
        var (bus, _) = Nes();
        var cpu = new Cpu6502(new BusLog(bus, []), Platform.Nes.Cpu);
        Assert.Same(cpu, bus.Cpu);
    }

    // ---------- The NES's vblank flag, NMI and $2006 (tools 9) ----------

    [Fact]
    public void The_vblank_flag_clears_at_the_end_of_vblank()
    {
        var (bus, cpu) = Nes();
        cpu.Cycles = NesBus.FrameCycles;
        bus.Tick(cpu);
        cpu.Cycles += 100;
        Assert.Equal(0x80, bus.Read(0x2002));
        Assert.Equal(0x00, bus.Read(0x2002));       // a read clears it

        cpu.Cycles = 2 * NesBus.FrameCycles;
        bus.Tick(cpu);
        cpu.Cycles += NesBus.VblankCycles - 1;
        Assert.True(cpu.Cycles < 2 * NesBus.FrameCycles + NesBus.VblankCycles);
        cpu.Cycles += 1;
        Assert.Equal(0x00, bus.Read(0x2002));       // vblank is over, with no read
    }

    [Fact]
    public void A_flag_read_long_after_the_frame_started_is_clear()
    {
        // The review's program: the second read comes about 11,000 cycles after a frame start.
        var r = Runner.Run(TernCompiler.Compile(Nrom + """
            var early: u8
            var late: u8
            var stale: u8
            fn main() {
                asm {
                .w: bit $2002
                    bpl .w
                    ldx #4
                    ldy #0
                .d1:dey
                    bne .d1
                    dex
                    bne .d1
                    lda $2002
                    sta early
                    ldx #28
                    ldy #0
                .d2:dey
                    bne .d2
                    dex
                    bne .d2
                    lda $2002
                    sta stale
                    lda $2002
                    sta late
                }
            }
            """, Platform.Nes));
        Assert.True(r.Returned);
        var program = TernCompiler.Compile(Nrom + "var early: u8\nvar late: u8\nvar stale: u8\nfn main() { }", Platform.Nes);
        Assert.Equal([0, 0, 0], new[] { "early", "stale", "late" }.Select(n => r.Ram.Bytes[program.Symbols[n]]));
    }

    [Fact]
    public void Turning_the_nmi_on_during_vblank_raises_it_at_once()
    {
        var (bus, cpu) = Nes();
        cpu.Cycles = NesBus.FrameCycles;
        bus.Tick(cpu);
        cpu.Cycles += 50;
        bus.Write(0x2000, 0x80);
        bus.Tick(cpu);
        Assert.Equal(1, bus.Nmis);

        // Off and on again outside vblank: no NMI until the next frame.
        bus.Write(0x2000, 0x00);
        cpu.Cycles = NesBus.FrameCycles + NesBus.VblankCycles + 10;
        bus.Write(0x2000, 0x80);
        bus.Tick(cpu);
        Assert.Equal(1, bus.Nmis);
        cpu.Cycles = 2 * NesBus.FrameCycles;
        bus.Tick(cpu);
        Assert.Equal(2, bus.Nmis);
    }

    [Fact]
    public void Ppu_address_changes_on_the_second_write()
    {
        var (bus, _) = Nes();
        bus.Write(0x2006, 0x20);
        bus.Write(0x2006, 0x00);
        bus.Write(0x2006, 0x23);                    // first write only: the address is still $2000
        bus.Write(0x2007, 7);
        Assert.Equal(7, bus.Ppu[bus.Map(0x2000)]);
        Assert.Equal(0, bus.Ppu[bus.Map(0x2300)]);
        bus.Write(0x2006, 0x40);                    // second write: $2340
        bus.Write(0x2007, 9);
        Assert.Equal(9, bus.Ppu[bus.Map(0x2340)]);
    }

    // ---------- brk (tools 12) ----------

    [Fact]
    public void Brk_on_the_nes_runs_the_programs_irq_handler()
    {
        var program = TernCompiler.Compile(Nrom + """
            var breaks: u8
            var out: u8
            interrupt fn on_brk() { breaks += 1 }
            data irq_vector: u16 @ $FFFE = addr(on_brk)
            fn main() {
                asm {
                    brk
                    nop
                    brk
                    nop
                }
                out = 1
            }
            """, Platform.Nes);
        var run = Runner.Run(program);
        Assert.True(run.Returned, run.Stop);
        Assert.Equal((2, 1), (run.Ram.Bytes[program.Symbols["breaks"]], run.Ram.Bytes[program.Symbols["out"]]));
    }

    [Theory]
    [InlineData("nes")]
    [InlineData("c64")]
    public void Brk_without_a_handler_stops_the_run(string target)
    {
        string memory = target == "nes" ? Nrom : "";
        var program = TernCompiler.Compile(memory + "var out: u8\nfn main() {\n    asm { brk }\n    out = 1\n}", Platform.ByName(target));
        var run = Runner.Run(program);
        Assert.False(run.Returned);
        Assert.StartsWith("brk at $", run.Stop);
    }

    // ---------- Stand-ins (tools 11 and 16) ----------

    [Fact]
    public void Chrout_writes_color_0_and_a_missing_load_returns_4()
    {
        // The review's program: black text, then a LOAD of a file that is not on the disk.
        var program = TernCompiler.Compile("""
            fn chrout(ch: u8 using A) @ $FFD2 keeps X, Y
            var color: u8 @ $0286
            var err: u8
            var flag: u8
            data name = "NOFILE"
            fn main() {
                color = 0
                chrout('A')
                color = 1
                chrout('B')
                asm {
                    lda #6
                    ldx #name.lo
                    ldy #name.hi
                    jsr $FFBD
                    lda #1
                    ldx #8
                    ldy #0
                    jsr $FFBA
                    lda #0
                    ldx #0
                    ldy #$C0
                    jsr $FFD5
                    sta err
                    lda #0
                    rol a
                    sta flag
                }
            }
            """);
        var run = Runner.Run(program);
        Assert.True(run.Returned, run.Stop);
        Assert.Equal([0x00, 0x01], run.Ram.Bytes[0xD800..0xD802]);
        Assert.Equal((4, 1), (run.Ram.Bytes[program.Symbols["err"]], run.Ram.Bytes[program.Symbols["flag"]]));
    }

    [Fact]
    public void Chrout_uses_the_kernals_light_blue_until_the_program_sets_a_color()
    {
        var run = Runner.Run(TernCompiler.Compile("fn chrout(ch: u8 using A) @ $FFD2 keeps X, Y\nfn main() { chrout('A') }"));
        Assert.Equal(14, run.Ram.Bytes[0xD800]);
    }

    [Fact]
    public void A_load_past_ffff_stops_the_run()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "level.tern"), "var n: u8\nfn show() { n = 1 }\n");
            string main = Path.Combine(dir.FullName, "main.tern");
            File.WriteAllText(main, """
                import level @ levels
                memory {
                    $0801..=$BFFF  program
                    $C000..=$CFFF  overlay levels
                }
                data name = "LEVEL"
                fn main() {
                    asm {
                        lda #5
                        ldx #name.lo
                        ldy #name.hi
                        jsr $FFBD
                        lda #1
                        ldx #8
                        ldy #0
                        jsr $FFBA
                        lda #0
                        ldx #$FE
                        ldy #$FF
                        jsr $FFD5
                    }
                    level.show()
                }
                """);
            var run = Runner.Run(TernCompiler.Compile(File.ReadAllText(main), sourcePath: main));
            Assert.False(run.Returned);
            Assert.StartsWith("LOAD of 'LEVEL' at $FFFE goes past $FFFF", run.Stop);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void The_atari_screen_scrolls_and_wraps_at_ffff()
    {
        // SAVMSC near the top of memory: the scroll writes on past $FFFF into zero page and the stack, as
        // on the machine, instead of failing with an exception. The program is lost, so the run then stops.
        var program = TernCompiler.Compile("""
            import atari
            fn main() {
                atari.savmsc = $FF00
                for _ in 0..30 { atari.put($41); atari.put($9B) }
            }
            """, Platform.Atari);
        var run = Runner.Run(program);
        Assert.False(string.IsNullOrEmpty(run.Stop));
    }

    [Fact]
    public void The_atari_cursor_wraps_from_the_top_row_to_the_bottom()
    {
        var program = TernCompiler.Compile("""
            import atari
            var row: u8 @ $0054
            var at_top: u8
            var after_up: u8
            var after_down: u8
            fn main() {
                at_top = row
                atari.put($1C)
                after_up = row
                atari.put($1D)
                after_down = row
            }
            """, Platform.Atari);
        var run = Runner.Run(program);
        Assert.True(run.Returned, run.Stop);
        Assert.Equal([0, 23, 0], new[] { "at_top", "after_up", "after_down" }.Select(n => run.Ram.Bytes[program.Symbols[n]]));
    }

    [Theory]
    [InlineData(100, 1, 1)]
    [InlineData(600, 2, 3)]
    public void Get_file_info_gives_a_sapling_for_a_file_over_512_bytes(int size, int storage, int blocks)
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "big.tern"), $"data blob: [u8; {size}] = repeat(1, {size})\nfn first() -> u16 {{ return addr(blob) }}\n");
            string main = Path.Combine(dir.FullName, "main.tern");
            File.WriteAllText(main, """
                import apple2
                import big @ levels
                memory {
                    $2000..=$5FFF  program
                    $6000..=$7FFF  overlay levels
                }
                data path: [u8; 8] = [7, $42, $49, $47, $2E, $42, $49, $4E]
                var list: [u8; 18]
                var never: u8
                var err: u8
                var x: u16
                fn main() {
                    list[0] = 10
                    list[1] = addr(path).lo
                    list[2] = addr(path).hi
                    err = apple2.mli($C4, addr(list))
                    if never == 1 { x = big.first() }
                }
                """);
            var program = TernCompiler.Compile(File.ReadAllText(main), Platform.Apple2, sourcePath: main);
            var run = Runner.Run(program);
            Assert.True(run.Returned, run.Stop);
            int list = program.Symbols["list"];
            Assert.Equal(0, run.Ram.Bytes[program.Symbols["err"]]);
            Assert.Equal(storage, run.Ram.Bytes[list + 7]);
            Assert.Equal(blocks, run.Ram.Bytes[list + 8] | run.Ram.Bytes[list + 9] << 8);
        }
        finally { dir.Delete(recursive: true); }
    }

    // ---------- The profile and interrupts (tools 10) ----------

    [Fact]
    public void The_profile_counts_the_cycles_of_taking_interrupts()
    {
        var program = TernCompiler.Compile("""
            var ticks: u8
            var n: u16
            var vec: u16 @ $FFFE
            interrupt fn tick() { ticks += 1 }
            fn main() {
                vec = addr(tick)
                asm { cli }
                while n < 300 { n += 1 }
                asm { sei }
            }
            """);
        var run = Runner.Run(program, new RunOptions { IrqEvery = 500 });
        Assert.True(run.Interrupts > 0);
        Assert.Equal(7L * run.Interrupts, run.Profile.InterruptCycles);
        Assert.Equal(run.Cycles, run.Profile.Total);
        var rows = LineTable.Rows(program, "irq.tern", run.Profile);
        string text = LineTable.Format(rows, (run.Profile.InterruptCycles, run.Interrupts));
        Assert.Contains($"; total {run.Profile.Total - run.Profile.InterruptCycles} cycles, and {7 * run.Interrupts} taking {run.Interrupts} interrupts", text);
    }

    [Fact]
    public void The_nmis_cycles_are_not_in_the_instruction_before_it()
    {
        var program = TernCompiler.Compile(Nrom + """
            var frames: u8
            interrupt fn frame() { frames += 1 }
            data nmi_vector: u16 @ $FFFA = addr(frame)
            fn main() {
                asm {
                    lda #$80
                    sta $2000
                }
                while frames < 3 { }
            }
            """, Platform.Nes);
        var run = Runner.Run(program);
        Assert.True(run.Returned);
        Assert.Equal(7L * run.Interrupts, run.Profile.InterruptCycles);
        Assert.Equal(run.Cycles, run.Profile.Total);
    }
    // ---------- Idle input (decision 2026-11-21, question 175) ----------

    /// <summary>
    /// tern run's input reads "nothing pressed", not the seeded fill (spec 13): before, the C64's $DC00 read
    /// $97 (joystick right) and the Apple II's $C000 $C6 (a key pressed).
    /// </summary>
    [Theory]
    [InlineData("c64", 0xDC00, 0xFF)]   // port A: no joystick 2 direction or fire, no keyboard column selected
    [InlineData("c64", 0xDC01, 0xFF)]   // port B: no joystick 1, no key
    [InlineData("atari", 0x02FC, 0xFF)] // CH: no key
    [InlineData("atari", 0x0278, 0x0F)] // STICK0-3: centred
    [InlineData("atari", 0x0279, 0x0F)]
    [InlineData("atari", 0x027A, 0x0F)]
    [InlineData("atari", 0x027B, 0x0F)]
    [InlineData("atari", 0x0284, 1)]    // STRIG0-3: not pressed
    [InlineData("atari", 0x0285, 1)]
    [InlineData("atari", 0x0286, 1)]
    [InlineData("atari", 0x0287, 1)]
    [InlineData("atari", 0xD01F, 7)]    // CONSOL: no console key
    [InlineData("apple2", 0xC000, 0)]   // the keyboard: no key (below $80)
    [InlineData("apple2", 0xC061, 0)]   // buttons 0-2 (below $80)
    [InlineData("apple2", 0xC062, 0)]
    [InlineData("apple2", 0xC063, 0)]
    public void Input_reads_idle(string target, int address, int idle)
    {
        var platform = Platform.ByName(target)!;
        var program = TernCompiler.Compile($"var input: u8 @ ${address:X4}\nvar seen: u8\nfn main() {{ seen = input }}", platform);
        var run = Runner.Run(program);
        Assert.True(run.Returned, run.Stop);
        Assert.Equal(idle, run.Ram.Bytes[program.Symbols["seen"]]);
    }

    [Fact]
    public void The_nes_controllers_read_no_button()
    {
        var program = TernCompiler.Compile(Nrom + "var one: u8\nvar two: u8\nfn main() {\n    one = nes.pad(0)\n    two = nes.pad(1)\n}", Platform.Nes);
        var run = Runner.Run(program);
        Assert.True(run.Returned, run.Stop);
        Assert.Equal(0, run.Ram.Bytes[program.Symbols["one"]]);
        Assert.Equal(0, run.Ram.Bytes[program.Symbols["two"]]);
    }
}
