using Tern.Cpu;

namespace Tern.Compiler.Tools;

/// <summary>
/// The NES for <c>tern run</c>: no ROM routines (the cartridge is the program); its chips are modelled
/// by <see cref="NesBus"/>, the bus the program runs on, with its frames and NMIs after each
/// instruction and its UxROM banks. The screen is nametable 0 in PPU memory, shown as text through the
/// program's encoding (<see cref="ScreenText"/>).
/// </summary>
internal sealed class NesMachine : Machine
{
    /// <summary>None: the NES has no ROM routines.</summary>
    protected override IReadOnlyList<StandIn> Routines => [];

    public override string Help =>
        "On the NES: PPU memory and sprite memory (not pixels); the vblank flag, set at the start of vblank and cleared by a read of $2002 or at the end of vblank; " +
        "an NMI every frame when bit 7 of $2000 is set, and at once when it is set during vblank; the UxROM bank latch; the controllers read as no buttons; " +
        "a brk runs the program's IRQ handler when the program sets the IRQ vector.";

    public override string ScreenHelp => "on the NES nametable 0, each tile as the character the program's default encoding gives it";

    /// <summary>The PPU, APU, controllers and banks; null until a program is loaded.</summary>
    public NesBus? Bus { get; private set; }

    public override void Start(Ram ram) { }

    /// <summary>The .nes file: PRG ROM into the CPU's view, CHR ROM into the PPU's (<see cref="NesBus"/>).</summary>
    protected override IBus LoadFile(CompileResult program, Ram ram, Func<long, int, byte>? buttons) =>
        Bus = new NesBus(ram, program.File) { Buttons = buttons };

    public override bool AfterStep(Cpu6502 cpu)
    {
        int nmis = Bus!.Nmis;
        Bus.Tick(cpu);
        return Bus.Nmis > nmis;
    }

    /// <summary>By the bank selected now: code in two banks may share an address (spec 8.5).</summary>
    public override int PlaceOf(int pc) => Program!.PlaceOf(pc, Bus!.SelectedBank);

    /// <summary>The RAM is mirrored up to $1FFF.</summary>
    public override byte Peek(Ram ram, int address) => ram.Bytes[address < 0x2000 ? address & 0x07FF : address];

    public override bool RunsBrk => Program!.SetsIrqVector;

    /// <summary>Nametable 0, in PPU memory.</summary>
    public override (int Address, int Columns, int Rows) Screen(Ram ram) => (0x2000, 32, 30);

    protected override char ScreenChar(byte code) => code == 0 ? ' ' : '#';

    /// <summary>
    /// Nametable 0 as 30 lines of 32 tiles, without trailing spaces: each tile as the character that the
    /// program's 'default encoding' gives that tile number, else a space for tile 0 and '#' for the others.
    /// </summary>
    public override IReadOnlyList<string> ScreenText(Ram ram)
    {
        var chars = new Dictionary<int, char>();
        if (Program?.TextEncoding is { } encoding)
            for (char c = '~'; c >= ' '; c--)
                if (encoding.Encode(c) is int b) chars[b] = c;
        return Bus!.Nametable(0).Select(row => new string(row.Select(b => chars.TryGetValue(b, out var c) ? c : ScreenChar(b)).ToArray()).TrimEnd()).ToList();
    }
}
