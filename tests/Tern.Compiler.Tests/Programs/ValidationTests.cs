using System.Text;
using Tern.Compiler;
using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>
/// Roadmap V1: the validation programs in <c>validation/</c>, each run in the emulator to a known result, the
/// memory it shows after a set number of frames, kept in its <c>expected/</c> folder. Running the tests with
/// <c>TERN_UPDATE_VALIDATION=1</c> rewrites the files; read the diff, since a change of result is a change of
/// behavior. Whole programs: they cover several spec sections.
/// </summary>
public class ValidationTests
{
    private const int PalFrame = 63 * 312;   // cycles per frame on a PAL C64: the raster interrupt's period

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
