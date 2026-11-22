using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>A bus that raises IRQs itself: <see cref="Run"/> asks it before each instruction.</summary>
public interface IIrqSource
{
    /// <summary>True while the IRQ line is held; the CPU takes it when interrupts are allowed.</summary>
    bool IrqPending();
}

/// <summary>
/// What a C64 game's run needs from the chips that <c>tern run</c>'s machine leaves as plain RAM, on one clock, the
/// CPU's cycles (PAL: 63 cycles a line, 312 lines a frame, frame 0 starting at cycle 0):
/// <list type="bullet">
/// <item>The VIC-II's raster line, <c>$D012</c> and bit 8 in <c>$D011</c>, so a wait for a raster line ends.</item>
/// <item>Its raster interrupt: when the line reaches the compare line the program wrote (<c>$D012</c>, bit 8 from
/// <c>$D011</c>) the interrupt is latched, and while the raster interrupt is enabled (<c>$D01A</c> bit 0) the IRQ line
/// is held until the program acknowledges it by writing bit 0 of <c>$D019</c>. The enable starts clear, as after the
/// KERNAL's reset (the run's memory starts with seeded bytes), so no IRQ comes before the program enables it (the
/// KERNAL's CIA timer interrupt is not modelled).</item>
/// <item>CIA 1's ports as a joystick in port 2 and no keys. Port A reads what the program drives (its keyboard
/// row selection) ANDed with the joystick, which pulls its lines low, so the joystick is held through a keyboard
/// scan (smashy smashy's gap 2); port B reads no keys, which a joystick in port 2 does not touch.</item>
/// </list>
/// </summary>
/// <param name="joystick">The joystick bits held in a frame (1 = up, 2 = down, 4 = left, 8 = right, 16 = fire).</param>
public sealed class C64Chips(IBus inner, Func<long, byte> joystick) : IBus, IIrqSource
{
    public const int FrameCycles = 63 * 312;

    private Cpu6502? _cpu;
    private long _checked;              // the cycle up to which the compare line was looked for
    private bool _latched;              // the raster interrupt happened and was not acknowledged
    private bool _enabled;              // $D01A bit 0, as the program last wrote it

    private long Cycles => _cpu!.Cycles;
    private int Line => (int)(Cycles % FrameCycles / 63);

    public byte Read(ushort address) => address switch
    {
        0xD012 => (byte)Line,
        0xD011 => (byte)(inner.Read(address) & 0x7F | (Line >= 256 ? 0x80 : 0)),
        0xDC00 => (byte)(inner.Read(address) & (~joystick(Cycles / FrameCycles) & 0x1F | 0xE0)),
        0xDC01 => 0xFF,
        _ => inner.Read(address),
    };

    public void Write(ushort address, byte value)
    {
        if (address == 0xD019 && (value & 1) != 0) _latched = false;
        if (address == 0xD01A) _enabled = (value & 1) != 0;
        inner.Write(address, value);
    }

    public bool IrqPending()
    {
        // The compare line as written: $D012 and bit 7 of $D011 (the reads above give the raster instead).
        int compare = inner.Read(0xD012) | (inner.Read(0xD011) & 0x80) << 1;
        long at = compare * 63L;        // its first cycle within a frame
        long now = Cycles;
        if (FrameIndex(now - at) > FrameIndex(_checked - at)) _latched = true;
        _checked = now;
        return _latched && _enabled;
    }

    private static long FrameIndex(long cycle) => cycle < 0 ? -1 : cycle / FrameCycles;

    public void Attach(Cpu6502 cpu)
    {
        _cpu = cpu;
        inner.Attach(cpu);
    }
}
