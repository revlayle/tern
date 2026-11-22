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
/// Whole programs: they cover several spec sections.
/// </summary>
public class ValidationTests
{
    private const int PalFrame = 63 * 312;   // cycles per frame on a PAL C64: the raster interrupt's period
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
        string dir = FindValidation("c64", "galaxy-stars");
        string path = Path.Combine(dir, "src", "main.tern");
        var result = TernCompiler.Compile(File.ReadAllText(path), Platform.C64, sourcePath: path);
        const int frames = 3000;
        var r = Run.Of(result, maxCycles: 70_000_000, setup: Kernal, irqEvery: PalFrame, standIns: true,
            until: run => run.Interrupts >= frames);
        Expect(Path.Combine(dir, "expected", $"frame-{frames}.txt"), $"Galaxy Stars after {frames} frames in the emulator",
            ("screen $C000", Rows(r.Ram, 0xC000, 1000, 40, 0xFF)),
            ("color RAM $D800", Rows(r.Ram, 0xD800, 1000, 40, 0x0F)),
            ("VIC $D000-$D02E", Rows(r.Ram, 0xD000, 0x2F, 0x2F, 0xFF)));
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

    /// <summary>The joystick bits of frame f of smashy smashy's script.</summary>
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
        const int frames = 3000;
        var r = Run.Of(result, maxCycles: 70_000_000, setup: ram => KernalAt(ram, 0xFFC0), irqEvery: PalFrame,
            standIns: true, until: run => run.Interrupts >= frames, bus: b => new C64Chips(b, SmashyJoystick));
        string state = $"score {r.U16("score.points"):X4}00, time {r.U16("score.time"):X3}, level {r.U8("score.level")}, "
            + $"treasures left {r.U8("score.treasures_left")}, kills {r.U8("enemies.kills")}, high score {r.U16("score.high"):X4}00";
        Expect(Path.Combine(dir, "expected", $"frame-{frames}.txt"), $"Smashy smashy after {frames} frames in the emulator",
            ("state", [state]),
            ("screen $C000", Rows(r.Ram, 0xC000, 1000, 40, 0xFF)),
            ("color RAM $D800", Rows(r.Ram, 0xD800, 1000, 40, 0x0F)),
            ("VIC $D000-$D02E", Rows(r.Ram, 0xD000, 0x2F, 0x2F, 0xFF)));
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
        var start = new System.Diagnostics.ProcessStartInfo(Vice) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-default", "-silent", "-sounddev", "dummy", "-warp", "-autostartprgmode", "1",
            "-autostart", prg, "-limitcycles", cycles.ToString(), "-exitscreenshot", screenshot })
            start.ArgumentList.Add(a);
        using var vice = System.Diagnostics.Process.Start(start)!;
        vice.StandardOutput.ReadToEndAsync();
        vice.StandardError.ReadToEndAsync();
        if (!vice.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            vice.Kill();
            Assert.Fail($"VICE did not stop within 2 minutes running {prg}");
        }
        Assert.True(File.Exists(screenshot), $"VICE wrote no screenshot running {prg}");
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
    /// The KERNAL's IRQ entry (<see cref="C64LibraryTests.KernalIrqEntry"/>), and for the IRQs before the program
    /// installs its handler, the KERNAL's own handler reduced to its exit (pull Y, X and A, rti) at $FF5B, after the
    /// entry: a program may use the RAM under the ROM at $EA31, which the emulator's flat memory does not keep apart.
    /// </summary>
    internal static void Kernal(Ram ram)
    {
        C64LibraryTests.KernalIrqEntry(ram);
        byte[] exit = [0x68, 0xA8, 0x68, 0xAA, 0x68, 0x40];
        exit.CopyTo(ram.Bytes, 0xFF5B);
        ram.Bytes[0x0314] = 0x5B;
        ram.Bytes[0x0315] = 0xFF;
    }

    /// <summary>
    /// A KERNAL IRQ entry at <paramref name="entry"/> (push A, X and Y, then jmp ($0314), as the KERNAL's at $FF48 without
    /// its brk test), $FFFE pointing to it, and the KERNAL's own handler reduced to its exit (pull Y, X and A, rti) just
    /// after it, for the IRQs before the program installs its handler. For a program that uses the RAM under $FF48.
    /// </summary>
    internal static void KernalAt(Ram ram, int entry)
    {
        byte[] stub = [0x48, 0x8A, 0x48, 0x98, 0x48, 0x6C, 0x14, 0x03, 0x68, 0xA8, 0x68, 0xAA, 0x68, 0x40];
        stub.CopyTo(ram.Bytes, entry);
        ram.Bytes[0xFFFE] = (byte)entry;
        ram.Bytes[0xFFFF] = (byte)(entry >> 8);
        ram.Bytes[0x0314] = (byte)(entry + 8);
        ram.Bytes[0x0315] = (byte)((entry + 8) >> 8);
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
