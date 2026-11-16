using Tern.Cpu;

namespace Tern.Compiler.Tools;

/// <summary>
/// The NES for <c>tern run</c>: no ROM routines (the cartridge is the program); its chips are modelled
/// by <see cref="NesBus"/>. The screen is nametable 0 in PPU memory, shown as text through the
/// program's encoding (<see cref="Text"/>).
/// </summary>
internal sealed class NesMachine : Machine
{
    /// <summary>None: the NES has no ROM routines.</summary>
    protected override IReadOnlyList<StandIn> Routines => [];

    public override void Start(Ram ram) { }

    /// <summary>Not in CPU memory: <see cref="Runner.Screen"/> gives the nametable's place in PPU memory.</summary>
    public override (int Address, int Columns, int Rows) Screen(Ram ram) => (0x2000, 32, 30);

    public override char ScreenChar(byte code) => code == 0 ? ' ' : '#';

    /// <summary>
    /// Nametable 0 as 30 lines of 32 tiles, without trailing spaces: each tile as the character that
    /// <paramref name="encoding"/> gives that tile number (the program's 'default encoding'), else a
    /// space for tile 0 and '#' for the others.
    /// </summary>
    public static IReadOnlyList<string> Text(NesBus nes, CharEncoding? encoding)
    {
        var chars = new Dictionary<int, char>();
        if (encoding is not null)
            for (char c = '~'; c >= ' '; c--)
                if (encoding.Encode(c) is int b) chars[b] = c;
        return nes.Nametable(0).Select(row => new string(row.Select(b => chars.TryGetValue(b, out var c) ? c : b == 0 ? ' ' : '#').ToArray()).TrimEnd()).ToList();
    }
}
