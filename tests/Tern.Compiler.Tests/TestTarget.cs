namespace Tern.Compiler.Tests;

/// <summary>
/// The target a test runs on when it names none, and the source a platform needs (review code-quality-tests
/// 5 and 6). The default is the C64, or the platform named by <c>TERN_TARGET</c> (<c>atari</c>, <c>apple2</c>,
/// <c>nes</c>), read in this one place. Many tests check C64 addresses or NMOS code shapes, so a whole suite
/// on another target is a way to look for differences, not a run expected to pass.
/// </summary>
public static class TestTarget
{
    public static Platform Default { get; } = Environment.GetEnvironmentVariable("TERN_TARGET") is { Length: > 0 } name
        ? Platform.ByName(name) ?? throw new InvalidOperationException($"TERN_TARGET is '{name}', which is no target: use {Platform.Choices}")
        : Platform.C64;

    /// <summary>
    /// RAM that a test's small program leaves free, for a variable it places with '@' (review code-quality-tests
    /// 6): the C64's $C000, page 6 on the Atari, page 3 on the Apple II, the top page of the NES's RAM. Not a
    /// volatile area, not zero page, not where the compiler puts code or variables.
    /// </summary>
    public static int ScratchRam(Platform platform) => ScratchPages[platform.Name] << 8;

    private static readonly Dictionary<string, int> ScratchPages = new() { ["c64"] = 0xC0, ["atari"] = 0x06, ["apple2"] = 0x03, ["nes"] = 0x07 };

    /// <summary>Every platform's name, for a theory that runs on each.</summary>
    public static TheoryData<string> All => [.. Platform.All.Select(p => p.Name)];

    /// <summary>
    /// <paramref name="source"/> as <paramref name="platform"/> needs it: on a platform where every program is a
    /// cartridge, a source with no memory block gets one naming the first board, after its imports. It goes on a
    /// line the source already has, after ';', so the lines and columns a test checks stay where they were: on a
    /// blank line after the imports, at the end of the last import, or else (no import, or one that ends in a
    /// comment) in front of the first declaration, whose columns then move.
    /// </summary>
    public static string Adapt(string source, Platform platform) => Adapt(source, platform, out _);

    /// <summary>See <see cref="Adapt(string, Platform)"/>; <paramref name="moved"/> is the line (from 1) whose columns moved, and by how much, or (0, 0).</summary>
    public static string Adapt(string source, Platform platform, out (int Line, int Columns) moved)
    {
        moved = (0, 0);
        if (!platform.CartridgeRequired || source.Contains("memory {")) return source;
        string block = $"memory {{ cartridge {platform.Boards[0].Name} vertical }}";
        var lines = source.Split('\n');
        int head = lines.TakeWhile(l => l.TrimStart().StartsWith("import ") || l.Trim().Length == 0 || l.TrimStart().StartsWith("//")).Count();
        int import = head == 0 ? -1 : Array.FindLastIndex(lines, head - 1, head, l => l.TrimStart().StartsWith("import "));
        int blank = Array.FindIndex(lines, import + 1, head - import - 1, l => l.Trim().Length == 0);
        if (blank >= 0) lines[blank] = block;
        else if (import >= 0 && !lines[import].Contains("//")) lines[import] = lines[import].TrimEnd('\r') + "; " + block;
        else if (head < lines.Length)
        {
            lines[head] = block + "; " + lines[head];
            moved = (head + 1, block.Length + 2);
        }
        else return source + "\n" + block;
        return string.Join('\n', lines);
    }
}
