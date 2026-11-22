using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>
/// What a C64 game's run needs from the chips that <c>tern run</c>'s machine leaves as plain RAM: the VIC-II's raster
/// line (<c>$D012</c>, bit 8 in <c>$D011</c>) from the cycle count, 63 cycles a line and 312 lines a frame (PAL), so a
/// wait for a raster line ends; and CIA 1's ports as a joystick in port 2 and no keys. Port A reads the joystick
/// whatever the program wrote there to scan the keyboard (on a C64 the joystick pulls the lines low), so the
/// joystick is held through a keyboard scan (smashy smashy's gap 2).
/// </summary>
/// <param name="joystick">The joystick bits held in a frame (1 = up, 2 = down, 4 = left, 8 = right, 16 = fire).</param>
public sealed class C64Chips(IBus inner, Func<long, byte> joystick) : IBus
{
    public const int FrameCycles = 63 * 312;

    private Cpu6502? _cpu;

    private int Line => (int)(_cpu!.Cycles % FrameCycles / 63);

    public byte Read(ushort address) => address switch
    {
        0xD012 => (byte)Line,
        0xD011 => (byte)(inner.Read(address) & 0x7F | (Line >= 256 ? 0x80 : 0)),
        0xDC00 => (byte)(~joystick(_cpu!.Cycles / FrameCycles) & 0x1F | 0xE0),
        0xDC01 => 0xFF,
        _ => inner.Read(address),
    };

    public void Write(ushort address, byte value) => inner.Write(address, value);

    public void Attach(Cpu6502 cpu)
    {
        _cpu = cpu;
        inner.Attach(cpu);
    }
}
