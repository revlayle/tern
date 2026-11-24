using System.Text;
using Tern.Compiler;
using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>
/// Roadmap V1: the validation programs in <c>validation/</c>, each run in the emulator to a known result, the
/// memory it shows after a set number of frames, kept in its <c>expected/</c> folder. Running the tests with
/// <c>TERN_UPDATE_VALIDATION=1</c> rewrites the files; read the diff, since a change of result is a change of
/// behavior. With <c>TERN_REAL=1</c> each also runs on its platform's emulator, whose screenshot is compared
/// with the expected one, allowing for the frame or two by which the emulator's timing varies from run to run.
/// The C64 games run with <see cref="C64Chips"/> (the raster line and its interrupt, and CIA 1 as a joystick) and a
/// KERNAL IRQ entry (<see cref="Kernal"/>), which <c>tern run</c> does not have, so their known results are the test
/// harness's (docs/status.md, question 185). Whole programs: they cover several spec sections.
/// </summary>
public class ValidationTests
{
    private const string Vice = "C:/Vice/bin/x64sc.exe";

    // The share of pixels that may differ from the expected screenshot. Five VICE runs of Galaxy Stars to the
    // same cycle differed by up to 0.29% (sprites a pixel or two apart, the timer a second); its title screen
    // differs from its demo by 5.2%.
    private const double ScreenshotTolerance = 0.01;

    [Trait("Platform", "c64")]
    [Fact]
    public void Galaxy_stars_plays_its_demo()
    {
        // Idle input: the title, HOW TO PLAY, SCORING, then the demo, which plays the game; frame 3000 is in the demo.
        // Frames are counted by the raster interrupt, from when the game enables it.
        string dir = FindValidation("c64", "galaxy-stars");
        string path = Path.Combine(dir, "src", "main.tern");
        var result = TernCompiler.Compile(File.ReadAllText(path), Platform.C64, sourcePath: path);
        const int frames = 3000;
        var r = Run.Of(result, maxCycles: 70_000_000, setup: Kernal, standIns: true,
            until: run => run.Interrupts >= frames, bus: b => new C64Chips(b, _ => 0));
        Expect(Path.Combine(dir, "expected", $"frame-{frames}.txt"), $"Galaxy Stars after {frames} frames in the emulator",
            ("screen $C000", Rows(r.Ram, 0xC000, 1000, 40, 0xFF)),
            ("color RAM $D800", Rows(r.Ram, 0xD800, 1000, 40, 0x0F)),
            ("VIC $D000-$D02E", Rows(r.Ram, 0xD000, 0x2F, 0x2F, 0xFF)));
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void C64Chips_raises_the_raster_interrupt_at_the_compare_line_once_a_frame()
    {
        // Review V1 batch 2, finding 1: the IRQ on C64Chips' raster clock, at the line the program sets, held
        // until acknowledged. The handler notes the line it sees (a few cycles in: the same line or the next).
        var result = Run.Compile("""
            import c64
            var count: u8
            var line: [u8; 8]
            interrupt fn tick() {
                line[count] = c64.vic.raster
                c64.vic.irq_status = $FF
                count += 1
            }
            fn main() {
                c64.interrupts_off()
                c64.vic.control1 &= $7F
                c64.vic.raster = 100
                c64.vic.irq_status = $FF
                c64.vic.irq_enable = 1
                c64.set_kernal_irq(addr(tick))
                c64.interrupts_on()
                while count != 8 { }
                c64.vic.irq_enable = 0
            }
            """, platform: Platform.C64);
        var r = Run.Of(result, maxCycles: 9 * C64Chips.FrameCycles, setup: Kernal, standIns: true,
            bus: b => new C64Chips(b, _ => 0));
        Assert.Equal(8, r.U8("count"));
        Assert.Equal(8, r.Interrupts);
        for (int i = 0; i < 8; i++) Assert.InRange(r.U8("line", i), 100, 101);
        Assert.InRange(r.Cpu.Cycles, 7 * C64Chips.FrameCycles + 100 * 63, 8 * C64Chips.FrameCycles + 100 * 63);
    }

    [Fact]
    public void AtariChips_raises_the_vertical_blank_and_the_display_lists_interrupts()
    {
        // V1 batch 3, Atari step 4: the VBI once a frame through the OS stub's SYSVBV and the deferred vector, and a
        // DLI at the last line of each display-list row with bit 7. The list is 24 blank lines from line 8, then
        // rows of 8 lines from line 32: row 0 ends on line 39 (VCOUNT 19), row 5 on line 79 (VCOUNT 39).
        var result = Run.Compile("""
            import atari
            const SCREEN = $4000
            const LIST = $3C00
            memory { SCREEN..=SCREEN + 959 reserved }
            data list: [u8; _] @ LIST = atari.display_list(atari.MODE_TEXT, 24, SCREEN, LIST, 1 | (1 << 5))
            var seen: [u8; 8]
            var n: u8
            var frames: u8
            interrupt fn dli() {
                if n < 8 {
                    seen[n] = atari.antic.vcount
                    n += 1
                }
            }
            interrupt fn frame() {
                frames += 1
            }
            fn main() {
                atari.set_vbi(addr(frame))
                atari.set_display_list(addr(list))
                atari.set_dli(addr(dli))
                while frames < 3 { }
            }
            """, platform: Platform.Atari);
        AtariChips? chips = null;
        var r = Run.Of(result, maxCycles: 4 * AtariChips.FrameCycles, setup: AtariChips.Os, standIns: true,
            bus: b => chips = new AtariChips(b, _ => 0));
        Assert.Equal(3, r.U8("frames"));
        Assert.Equal(3, chips!.Frames);
        Assert.Equal(6, r.U8("n"));
        for (int i = 0; i < 6; i += 2)
        {
            Assert.Equal(19, r.U8("seen", i));
            Assert.Equal(39, r.U8("seen", i + 1));
        }
        Assert.InRange(r.Cpu.Cycles, 2 * AtariChips.FrameCycles + 248 * 114, 3 * AtariChips.FrameCycles);
    }

    [Fact]
    public void AtariChips_gives_the_joystick_from_the_chips_and_the_os_copies_and_no_key()
    {
        // The script's bits, per frame, as the PIA's PORTA (port 1 in bits 0-3, 0 = pressed; port 2 idle) and
        // GTIA's TRIG0, and as the OS's STICK0 and STRIG0; SKSTAT with no key down.
        var result = Run.Compile("""
            import atari
            var stick: u8
            var trig: u8
            var porta: u8
            var trig0: u8
            var skstat: u8
            fn main() {
                stick = atari.stick[0]
                trig = atari.strig[0]
                porta = atari.pia.porta
                trig0 = atari.gtia_in.trig[0]
                skstat = atari.pokey_in.skstat
            }
            """, platform: Platform.Atari);
        var r = Run.Of(result, setup: AtariChips.Os, standIns: true, bus: b => new AtariChips(b, _ => 0b1_0101));
        Assert.Equal(0x0A, r.U8("stick"));      // up and left held
        Assert.Equal(0, r.U8("trig"));          // fire held
        Assert.Equal(0xFA, r.U8("porta"));
        Assert.Equal(0, r.U8("trig0"));
        Assert.Equal(0xFF, r.U8("skstat"));
    }

    [Trait("Platform", "c64")]
    [FactWhen("TERN_REAL", "it runs Galaxy Stars in VICE and compares the screenshot")]
    public void Galaxy_stars_plays_its_demo_in_vice()
    {
        // 60 million cycles: about 3050 frames, in the demo, as the emulator's run above.
        string dir = FindValidation("c64", "galaxy-stars");
        string path = Path.Combine(dir, "src", "main.tern");
        var result = TernCompiler.Compile(File.ReadAllText(path), Platform.C64, sourcePath: path);
        string work = Directory.CreateTempSubdirectory("tern-real-").FullName;
        string prg = Path.Combine(work, "galaxy-stars.prg"), shot = Path.Combine(work, "vice.png");
        File.WriteAllBytes(prg, result.File);
        RunVice(prg, 60_000_000, shot);
        ExpectScreenshot(shot, Path.Combine(dir, "expected", "vice-60M-cycles.png"));
        Directory.Delete(work, recursive: true);   // kept when the check fails, to look at
    }

    // Smashy smashy's joystick: (frames, bits) runs. The title for 3 s, fire, READY?, then the play pattern over
    // and over: run right and left, jumps, fast-falls (down in the air) onto platforms.
    private const byte Down = 2, Left = 4, Right = 8, Fire = 16;
    private static readonly (int Frames, byte Bits)[] SmashyStart = [(150, 0), (3, Fire), (10, 0)];
    private static readonly (int Frames, byte Bits)[] SmashyPlay =
    [
        (40, Right), (25, Right | Fire), (6, 0), (3, Down), (30, 0), (50, Left), (20, Left | Fire), (8, 0),
        (2, Down), (30, 0), (12, Fire), (4, 0), (3, Down), (40, 0), (70, Right), (15, Fire), (20, Left),
        (6, Down), (35, 0),
    ];

    /// <summary>The joystick bits of frame f of smashy smashy's script (frames of C64Chips' clock, from the run's start).</summary>
    private static byte SmashyJoystick(long f)
    {
        foreach (var (n, bits) in SmashyStart)
        {
            if (f < n) return bits;
            f -= n;
        }
        int cycle = SmashyPlay.Sum(r => r.Frames);
        f %= cycle;
        foreach (var (n, bits) in SmashyPlay)
        {
            if (f < n) return bits;
            f -= n;
        }
        return 0;
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Smashy_smashy_plays_a_scripted_game()
    {
        // Through the title to a game: the levels it generates, the player running, jumping and smashing under the
        // script, the enemies and their AI, all in the emulator with a raster line and the joystick (C64Chips).
        string dir = FindValidation("c64", "smashy-smashy");
        string path = Path.Combine(dir, "src", "main.tern");
        var result = TernCompiler.Compile(File.ReadAllText(path), Platform.C64, sourcePath: path);
        const int frames = 2500;     // in the second game's play (the first ended with 500 points)
        var r = Run.Of(result, maxCycles: 70_000_000, setup: Kernal, standIns: true,
            until: run => run.Interrupts >= frames, bus: b => new C64Chips(b, SmashyJoystick));
        string state = $"score {r.U16("score.points"):X4}00, time {r.U16("score.time"):X3}, level {r.U8("score.level")}, "
            + $"treasures left {r.U8("score.treasures_left")}, kills {r.U8("enemies.kills")}, high score {r.U16("score.high"):X4}00";
        Expect(Path.Combine(dir, "expected", $"frame-{frames}.txt"), $"Smashy smashy after {frames} frames in the emulator",
            ("state", [state]),
            ("screen $C000", Rows(r.Ram, 0xC000, 1000, 40, 0xFF)),
            ("color RAM $D800", Rows(r.Ram, 0xD800, 1000, 40, 0x0F)),
            ("VIC $D000-$D02E", Rows(r.Ram, 0xD000, 0x2F, 0x2F, 0xFF)));
    }

    [Fact]
    public void Smashy_smashy_plays_a_scripted_game_on_the_atari()
    {
        // V1 batch 3, Atari step 4: the shared logic with the Atari front end, through the title to a game under the
        // C64 test's joystick script, with the VBI, the DLIs, VCOUNT and the joystick from AtariChips and the OS's NMI
        // handling from its stub. The rebuild budget and the frame's timing are the harness's (no ANTIC DMA), so the
        // run is this harness's known result, not a real Atari's frame for frame (question 189: each machine its own).
        string dir = FindValidation("c64", "smashy-smashy");
        string path = Path.Combine(dir, "src", "main.tern");
        var result = TernCompiler.Compile(File.ReadAllText(path), Platform.Atari, sourcePath: path);
        const int frames = 2500;
        AtariChips? chips = null;
        var r = Run.Of(result, maxCycles: (frames + 10) * (long)AtariChips.FrameCycles, setup: AtariChips.Os, standIns: true,
            until: _ => chips!.Frames >= frames, bus: b => chips = new AtariChips(b, SmashyJoystick));
        string state = $"score {r.U16("score.points"):X4}00, time {r.U16("score.time"):X3}, level {r.U8("score.level")}, "
            + $"treasures left {r.U8("score.treasures_left")}, kills {r.U8("enemies.kills")}, high score {r.U16("score.high"):X4}00";
        string sprites = "shown " + Convert.ToString(r.U8("sprites.ena"), 2).PadLeft(8, '0') + "; "
            + string.Join(", ", Enumerable.Range(0, 8).Select(n => $"{n}: x {r.U8("sprites.hpos", n)} y {r.U8("sprites.y", n)} colour ${r.U8("sprites.color", n):X2}"));
        Expect(Path.Combine(dir, "expected", $"atari-frame-{frames}.txt"), $"Smashy smashy on the Atari after {frames} frames in the emulator",
            ("state", [state]),
            ("sprites (shadows)", [sprites]),
            ("screen (video.screen, ANTIC's codes)", Rows(r.Ram, r.Address("video.screen"), 1000, 40, 0xFF)));
    }

    [FactWhen("TERN_REAL", "it runs smashy smashy in Altirra and compares the screenshot")]
    public void Smashy_smashy_shows_its_scoring_page_in_altirra()
    {
        // An 800 with 48 KB, PAL, the OS Altirra has (AltirraOS: no OS ROM images are installed). Altirra runs by the
        // clock, not by cycles, so the shot is of the page that stands still longest: SCORING, from 20 s to 32 s
        // after the program starts (the title's colours roll every 2 frames), taken at 26 s. Altirra's start takes
        // a varying time (one run's shot at 15 s still showed the title), so a run that misses is tried once more.
        // tests/tools/altirra-shot.ps1 captures the screen.
        string dir = FindValidation("c64", "smashy-smashy");
        string path = Path.Combine(dir, "src", "main.tern");
        var result = TernCompiler.Compile(File.ReadAllText(path), Platform.Atari, sourcePath: path);
        string work = Directory.CreateTempSubdirectory("tern-real-").FullName;
        string xex = Path.Combine(work, "smashy-smashy.xex"), shot = Path.Combine(work, "altirra.png");
        File.WriteAllBytes(xex, result.File);
        string expected = Path.Combine(dir, "expected", "altirra-800-pal-scoring.png");
        RunAltirra(xex, 26, shot, "/hardware:800", "/memsize:48K", "/pal");
        if (File.Exists(expected) && Png.Difference(shot, expected) > ScreenshotTolerance)
            RunAltirra(xex, 26, shot, "/hardware:800", "/memsize:48K", "/pal");
        ExpectScreenshot(shot, expected);
        Directory.Delete(work, recursive: true);   // kept when the check fails, to look at
    }

    [Fact]
    public void Smashy_smashy_on_the_atari_shows_no_sprites_on_the_title_after_a_game()
    {
        // The developer saw an enemy on the title after a game over: the title paces itself by VCOUNT and never
        // calls wait_frame(), so render() did not run and the VBI went on showing the game's last sprites. Run the
        // scripted game until the title follows a game, 10 frames into it: the copy of
        // player-missile memory on show is empty and no slot has an object.
        string dir = FindValidation("c64", "smashy-smashy");
        string path = Path.Combine(dir, "src", "main.tern");
        var result = TernCompiler.Compile(File.ReadAllText(path), Platform.Atari, sourcePath: path);
        AtariChips? chips = null;
        int titleFrames = -1;
        bool played = false;        // a game was under way (not the title, well after the startup cleared memory)
        var r = Run.Of(result, maxCycles: 4000 * (long)AtariChips.FrameCycles, setup: AtariChips.Os, standIns: true,
            until: run =>
            {
                if (chips!.Frames > 100 && run.U8("video.title_mode") == 0) played = true;
                if (titleFrames < 0 && played && run.U8("video.title_mode") != 0) titleFrames = chips.Frames;
                return titleFrames >= 0 && chips!.Frames >= titleFrames + 10;
            }, bus: b => chips = new AtariChips(b, SmashyJoystick));
        Assert.True(titleFrames >= 0, "the scripted game never came back to the title");
        int shown = r.U8("sprites.shown");
        int copy = r.Address("sprites.pm") + shown * 2048;
        Assert.All(Enumerable.Range(copy + 0x300, 0x500), a => Assert.Equal(0, r.Ram.Bytes[a]));
        Assert.All(Enumerable.Range(0, 5), s => Assert.Equal(0, r.U8("sprites.init_hpos", shown * 5 + s)));
    }

    [Fact]
    public void Smashy_smashy_on_the_atari_ignores_dlis_with_no_sprite_group()
    {
        // The developer's black screen after fire on the title: leaving the title installed the game's DLI while
        // every row's DLI bit was still on, the DLI ran past the frame's groups into a long loop over stale
        // entries, and nested DLIs filled the stack. Here every row's bit is turned on in each frame of play:
        // the game goes on (the DLI ignores a DLI with no group) and the stack stays shallow.
        string dir = FindValidation("c64", "smashy-smashy");
        string path = Path.Combine(dir, "src", "main.tern");
        var result = TernCompiler.Compile(File.ReadAllText(path), Platform.Atari, sourcePath: path);
        AtariChips? chips = null;
        int dl = result.Symbols["video.dlist"];
        int lowestStack = 0xFF, lastFrame = -1;
        var r = Run.Of(result, maxCycles: 1300 * (long)AtariChips.FrameCycles, setup: AtariChips.Os, standIns: true,
            until: run =>
            {
                if (chips!.Frames != lastFrame && chips.Frames > 300 && run.U8("video.title_mode") == 0)
                {
                    lastFrame = chips.Frames;
                    run.Ram.Bytes[dl + 8] |= 0x80;                          // playfield rows 0-22
                    for (int row = 11; row < 11 + 22; row++) run.Ram.Bytes[dl + row] |= 0x80;
                }
                if (chips.Frames > 300) lowestStack = Math.Min(lowestStack, run.Cpu.SP);
                return chips.Frames >= 1200;
            }, bus: b => chips = new AtariChips(b, SmashyJoystick));
        Assert.Equal(1200, chips!.Frames);
        Assert.True(lowestStack > 0xC0, $"the stack went down to ${lowestStack:X2}");
        Assert.NotEqual(0, r.U8("video.frames"));
    }

    [Fact]
    public void Smashy_smashy_on_the_atari_shows_only_the_player_during_ready()
    {
        // The developer's screenshot: fire on HOW TO PLAY, and the page's four sprites stayed on screen through
        // READY? (the C64's frame IRQ copies the shadows every frame; here sprites are drawn only when the game
        // draws, and READY? waits in a plain loop). Fire at frame 600 (the title shows 500 frames, then HOW TO
        // PLAY), then 60 frames into READY?: on show, the player only.
        string dir = FindValidation("c64", "smashy-smashy");
        string path = Path.Combine(dir, "src", "main.tern");
        var result = TernCompiler.Compile(File.ReadAllText(path), Platform.Atari, sourcePath: path);
        AtariChips? chips = null;
        int fireAt = -1;
        var r = Run.Of(result, maxCycles: 900 * (long)AtariChips.FrameCycles, setup: AtariChips.Os, standIns: true,
            until: run =>
            {
                if (fireAt < 0 && chips!.Frames > 600 && run.U8("video.title_mode") == 0 && run.U16("score.time") == 0x60) fireAt = chips.Frames;
                return fireAt >= 0 && chips!.Frames >= fireAt + 60;
            }, bus: b => chips = new AtariChips(b, f => (byte)(f >= 600 && f < 604 ? 16 : 0)));
        Assert.True(fireAt >= 0, "no game started after fire on HOW TO PLAY");
        int shown = r.U8("sprites.shown");
        Assert.NotEqual(0, r.U8("sprites.drawn_page", shown * 10));                  // part 0: the player
        Assert.All(Enumerable.Range(1, 9), part => Assert.Equal(0, r.U8("sprites.drawn_page", shown * 10 + part)));
    }

    [Fact]
    public void Smashy_smashy_builds_for_the_atari()
    {
        // V1 batch 3, step 1: the shared logic with the Atari front end (src/atari/) builds with no warning, and
        // the front end's aligned screen memory is where ANTIC can read it: the screen inside one 4 KB block,
        // the character set on a 1 KB boundary, the display list inside one 1 KB block, the player-missile memory on
        // 2 KB (review finding 4). Played in Altirra.
        string dir = FindValidation("c64", "smashy-smashy");
        string path = Path.Combine(dir, "src", "main.tern");
        var result = TernCompiler.Compile(File.ReadAllText(path), Platform.Atari, sourcePath: path);
        Assert.Empty(result.Warnings);
        int screen = result.Symbols["video.screen"], charset = result.Symbols["video.charset"], dlist = result.Symbols["video.dlist"];
        int pm = result.Symbols["sprites.pm"];   // two copies of single-line player-missile memory, each on 2 KB (PMBASE)
        Assert.Equal(0, pm % 2048);
        Assert.Equal(screen / 4096, (screen + 1039) / 4096);
        Assert.Equal(0, charset % 1024);
        Assert.Equal(dlist / 1024, (dlist + 47) / 1024);
    }

    [Trait("Platform", "c64")]
    [FactWhen("TERN_REAL", "it runs smashy smashy in VICE and compares the screenshot")]
    public void Smashy_smashy_shows_its_scoring_page_in_vice()
    {
        // 25 million cycles with no input: the title, HOW TO PLAY, then SCORING, a still page (its chain values
        // need the raster split to hi-res below row 15).
        string dir = FindValidation("c64", "smashy-smashy");
        string path = Path.Combine(dir, "src", "main.tern");
        var result = TernCompiler.Compile(File.ReadAllText(path), Platform.C64, sourcePath: path);
        string work = Directory.CreateTempSubdirectory("tern-real-").FullName;
        string prg = Path.Combine(work, "smashy-smashy.prg"), shot = Path.Combine(work, "vice.png");
        File.WriteAllBytes(prg, result.File);
        RunVice(prg, 25_000_000, shot);
        ExpectScreenshot(shot, Path.Combine(dir, "expected", "vice-25M-cycles.png"));
        Directory.Delete(work, recursive: true);   // kept when the check fails, to look at
    }

    /// <summary>Runs a C64 program in VICE for a number of cycles, from the start of the machine, and saves the screen.</summary>
    private static void RunVice(string prg, long cycles, string screenshot)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Vice) { UseShellExecute = false };
        foreach (string a in new[] { "-default", "-silent", "-sounddev", "dummy", "-warp", "-autostartprgmode", "1",
            "-autostart", prg, "-limitcycles", cycles.ToString(), "-exitscreenshot", screenshot })
            start.ArgumentList.Add(a);
        using var vice = System.Diagnostics.Process.Start(start)!;
        if (!vice.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            vice.Kill();
            Assert.Fail($"VICE did not stop within 2 minutes running {prg}");
        }
        Assert.True(File.Exists(screenshot), $"VICE wrote no screenshot running {prg}");
    }

    /// <summary>Runs an Atari program in Altirra for some seconds and saves its screen (tests/tools/altirra-shot.ps1).</summary>
    private static void RunAltirra(string xex, int seconds, string screenshot, params string[] machine)
    {
        string script = Path.Combine(FindRepositoryFolder("tests"), "tools", "altirra-shot.ps1");
        var start = new System.Diagnostics.ProcessStartInfo("pwsh") { UseShellExecute = false };
        foreach (string a in new[] { "-NoProfile", "-File", script, xex, screenshot, seconds.ToString(), "/skipsetup", "/kernel:default", "/nobasic" }.Concat(machine))
            start.ArgumentList.Add(a);
        using var run = System.Diagnostics.Process.Start(start)!;
        if (!run.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            run.Kill(entireProcessTree: true);
            Assert.Fail($"Altirra did not stop within 2 minutes running {xex}");
        }
        Assert.True(File.Exists(screenshot), $"Altirra gave no screenshot running {xex}");
        Assert.Equal(0, run.ExitCode);
    }

    /// <summary>A folder of the repository, found above the test's binaries.</summary>
    private static string FindRepositoryFolder(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, name);
            if (Directory.Exists(path) && File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) return path;
        }
        throw new DirectoryNotFoundException($"{name} not found above {AppContext.BaseDirectory}");
    }

    /// <summary>Compares a screenshot with the expected one within the tolerance, or copies it there with TERN_UPDATE_VALIDATION=1.</summary>
    private static void ExpectScreenshot(string actual, string expected)
    {
        if (Environment.GetEnvironmentVariable("TERN_UPDATE_VALIDATION") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
            File.Copy(actual, expected, overwrite: true);
            return;
        }
        Assert.True(File.Exists(expected), $"{expected} is missing: run the tests with TERN_UPDATE_VALIDATION=1 to write it, and look at it");
        double difference = Png.Difference(actual, expected);
        Assert.True(difference <= ScreenshotTolerance,
            $"{difference:P2} of the pixels of {actual} differ from {expected} (at most {ScreenshotTolerance:P0} may)");
    }

    /// <summary>Memory as lines of hex, <paramref name="perLine"/> bytes each, each byte and-ed with <paramref name="mask"/>.</summary>
    private static IEnumerable<string> Rows(Ram ram, int start, int length, int perLine, byte mask)
    {
        for (int i = 0; i < length; i += perLine)
            yield return Convert.ToHexString([.. ram.Bytes.AsSpan(start + i, Math.Min(perLine, length - i)).ToArray().Select(b => (byte)(b & mask))]);
    }

    /// <summary>Compares the parts with the expected file, or writes it with TERN_UPDATE_VALIDATION=1.</summary>
    private static void Expect(string file, string title, params (string Name, IEnumerable<string> Lines)[] parts)
    {
        var text = new StringBuilder($"# {title}. Rewritten by running the tests with TERN_UPDATE_VALIDATION=1.\n");
        foreach (var (name, lines) in parts)
        {
            text.Append($"\n{name}\n");
            foreach (var line in lines) text.Append(line).Append('\n');
        }
        if (Environment.GetEnvironmentVariable("TERN_UPDATE_VALIDATION") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text.ToString());
            return;
        }
        Assert.True(File.Exists(file), $"{file} is missing: run the tests with TERN_UPDATE_VALIDATION=1 to write it");
        string[] expected = File.ReadAllText(file).ReplaceLineEndings("\n").Split('\n');
        string[] actual = text.ToString().Split('\n');
        var differences = Enumerable.Range(0, Math.Max(expected.Length, actual.Length))
            .Where(i => i >= expected.Length || i >= actual.Length || expected[i] != actual[i])
            .Select(i => $"line {i + 1}:\n  expected {(i < expected.Length ? expected[i] : "(none)")}\n  actual   {(i < actual.Length ? actual[i] : "(none)")}")
            .ToList();
        Assert.True(differences.Count == 0, $"{title} differs from {file}:\n" + string.Join("\n", differences.Take(10)));
    }

    /// <summary>
    /// A KERNAL IRQ entry at $FFC0 (push A, X and Y, then jmp ($0314), as the KERNAL's at $FF48 without its brk test),
    /// $FFFE pointing to it, and $0314 to the KERNAL's own handler reduced to its exit (pull Y, X and A, rti), just
    /// after it, until the program installs its handler. Not at $FF48: the emulator's memory is flat, and smashy
    /// smashy's popups use the RAM under the ROM at $FF40-$FFBF (Galaxy Stars its sprites at $E800 and up).
    /// </summary>
    internal static void Kernal(Ram ram)
    {
        const int entry = 0xFFC0;
        byte[] stub = [0x48, 0x8A, 0x48, 0x98, 0x48, 0x6C, 0x14, 0x03, 0x68, 0xA8, 0x68, 0xAA, 0x68, 0x40];
        stub.CopyTo(ram.Bytes, entry);
        ram.Bytes[0xFFFE] = entry & 0xFF;
        ram.Bytes[0xFFFF] = entry >> 8;
        ram.Bytes[0x0314] = (entry + 8) & 0xFF;
        ram.Bytes[0x0315] = (entry + 8) >> 8;
    }

    /// <summary>A validation program's folder: validation/&lt;platform&gt;/name.</summary>
    internal static string FindValidation(string platform, string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "validation", platform, name);
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException($"validation/{platform}/{name} not found above {AppContext.BaseDirectory}");
    }
}
