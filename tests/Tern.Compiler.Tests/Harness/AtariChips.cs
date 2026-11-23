using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>A bus that raises NMIs itself: <see cref="Run"/> asks it before each instruction.</summary>
public interface INmiSource
{
    /// <summary>True once for each NMI: the CPU takes it at once.</summary>
    bool NmiPending();
}

/// <summary>
/// What an Atari game's run needs from the chips that <c>tern run</c>'s machine leaves as plain RAM, on one clock, the
/// CPU's cycles (PAL: 114 cycles a line, 312 lines a frame, frame 0 starting at cycle 0; ANTIC's DMA is not
/// modelled, so every line gives the CPU all 114):
/// <list type="bullet">
/// <item>ANTIC's line counter, <c>VCOUNT</c> (<c>$D40B</c>, the line / 2), and <c>WSYNC</c> (<c>$D40A</c>): a write
/// holds the CPU to the end of the line.</item>
/// <item>The NMIs, with <c>NMIST</c> (<c>$D40F</c>) saying which: the vertical blank at line 248 while <c>NMIEN</c>
/// (<c>$D40E</c>) bit 6 is set (it is after the OS's reset), and a display-list interrupt at the last line of each
/// display list instruction with bit 7 set while <c>NMIEN</c> bit 7 is set. The display list is read at line 8 of
/// each frame from <c>SDLST</c> (<c>$0230</c>; the OS copies it to ANTIC in each vertical blank), with each
/// mode's lines and the vertical scroll (<c>VSCROL</c>, <c>$D405</c>, as the program last wrote it) of rows with
/// bit 5 set: the first such row shows lines VSCROL to the end, the row after the last one lines 0 to VSCROL.</item>
/// <item>The joystick in port 1 through the OS's copies, <c>STICK0</c> (<c>$0278</c>, 0 = pressed, bits 0-3 up, down,
/// left, right) and <c>STRIG0</c> (<c>$0284</c>), which the OS reads in each vertical blank; no key held
/// (<c>SKSTAT</c> <c>$D20F</c> reads <c>$FF</c>).</item>
/// <item>A PAL machine (GTIA's <c>PAL</c>, <c>$D014</c>, reads 1), and POKEY's <c>RANDOM</c> (<c>$D20A</c>) the next
/// byte of a fixed sequence on each read, so a run repeats exactly and a seed read from it does not change when
/// the program's timing does (on the machine it depends on the cycle).</item>
/// </list>
/// The OS's NMI handling is <see cref="Os"/>'s stub, in the RAM where the OS ROM would be.
/// </summary>
/// <param name="joystick">The joystick bits held in a frame (1 = up, 2 = down, 4 = left, 8 = right, 16 = fire).</param>
public sealed class AtariChips(IBus inner, Func<long, byte> joystick) : IBus, INmiSource
{
    public const int LineCycles = 114, Lines = 312, FrameCycles = LineCycles * Lines, VbiLine = 248;
    private const int FirstDisplayLine = 8;
    private const ushort Vcount = 0xD40B, Wsync = 0xD40A, Nmien = 0xD40E, Nmist = 0xD40F, Vscrol = 0xD405;
    private const ushort Sdlst = 0x0230, Stick0 = 0x0278, Strig0 = 0x0284, Skstat = 0xD20F, Random = 0xD20A, Pal = 0xD014;
    /// <summary>The scan lines of each ANTIC mode's rows (modes 2-15).</summary>
    private static readonly int[] ModeLines = [0, 0, 8, 10, 8, 16, 8, 16, 8, 4, 4, 2, 1, 2, 1, 1];

    private Cpu6502? _cpu;
    private byte _nmien = 0x40;             // the OS's reset leaves the vertical blank on
    private byte _nmist;
    private long _checkedLine = -1;         // the last line (counted from cycle 0) looked at for NMIs
    private readonly HashSet<int> _dliLines = [];
    private ulong _random = 0x9E3779B97F4A7C15UL;

    /// <summary>The vertical blanks raised so far: the frames.</summary>
    public int Frames { get; private set; }

    private long Cycles => _cpu!.Cycles;
    private long Frame => Cycles / FrameCycles;

    public byte Read(ushort address) => address switch
    {
        Vcount => (byte)(Cycles % FrameCycles / LineCycles / 2),
        Nmist => _nmist,
        Stick0 => (byte)(~joystick(Frame) & 0x0F),
        Strig0 => (byte)((joystick(Frame) & 0x10) != 0 ? 0 : 1),
        Skstat => 0xFF,
        Random => NextRandom(),
        Pal => 1,
        _ => inner.Read(address),
    };

    private byte NextRandom()
    {
        _random ^= _random << 13;
        _random ^= _random >> 7;
        _random ^= _random << 17;
        return (byte)(_random >> 56);
    }

    public void Write(ushort address, byte value)
    {
        if (address == Nmien) _nmien = value;
        if (address == Wsync) _cpu!.Cycles = (Cycles / LineCycles + 1) * LineCycles;
        inner.Write(address, value);
    }

    public bool NmiPending()
    {
        long line = Cycles / LineCycles;
        bool pending = false;
        for (long l = _checkedLine + 1; l <= line; l++)
        {
            int inFrame = (int)(l % Lines);
            if (inFrame == FirstDisplayLine) ReadDisplayList();
            if (inFrame == VbiLine && (_nmien & 0x40) != 0)
            {
                _nmist = 0x40;
                Frames++;
                pending = true;
            }
            else if (_dliLines.Contains(inFrame) && (_nmien & 0x80) != 0)
            {
                _nmist = 0x80;
                pending = true;
            }
        }
        _checkedLine = line;
        return pending;
    }

    /// <summary>The lines this frame's display-list interrupts come on: the last line of each instruction with bit 7.</summary>
    private void ReadDisplayList()
    {
        _dliLines.Clear();
        int at = inner.Read(Sdlst) | inner.Read((ushort)(Sdlst + 1)) << 8;
        int vscrol = inner.Read(Vscrol) & 15;
        int line = FirstDisplayLine;
        bool scrolling = false;
        for (int guard = 0; guard < 256 && line < VbiLine; guard++)
        {
            byte op = inner.Read((ushort)at++);
            int mode = op & 0x0F, lines;
            if (mode == 1)
            {
                int target = inner.Read((ushort)at) | inner.Read((ushort)(at + 1)) << 8;
                if ((op & 0x40) != 0) break;            // JVB: wait for the vertical blank
                at = target;
                continue;
            }
            if (mode == 0)
                lines = (op >> 4 & 7) + 1;
            else
            {
                if ((op & 0x40) != 0) at += 2;          // LMS: the screen address follows
                lines = ModeLines[mode];
                if ((op & 0x20) != 0)
                {
                    if (!scrolling) lines -= vscrol;    // the first scrolled row starts at line VSCROL
                    scrolling = true;
                }
                else if (scrolling)
                {
                    lines = vscrol + 1;                 // the row after the scrolled ones ends at line VSCROL
                    scrolling = false;
                }
            }
            line += lines;
            if ((op & 0x80) != 0) _dliLines.Add(line - 1);
        }
    }

    public void Attach(Cpu6502 cpu)
    {
        _cpu = cpu;
        inner.Attach(cpu);
    }

    /// <summary>
    /// The OS's NMI handling, written into the RAM where the OS ROM would be: the NMI vector to an entry that
    /// jumps through <c>VDSLST</c> for a display-list interrupt, and for the vertical blank pushes A, X and Y and
    /// jumps through <c>VVBLKI</c>; the immediate vertical blank's end, <c>SYSVBV</c> (<c>$E45F</c>), jumps through
    /// <c>VVBLKD</c> (the OS's frame work between them is not modelled: no shadow registers are copied); the
    /// deferred one's, <c>XITVBV</c> (<c>$E462</c>), pulls Y, X and A and returns. The vectors start as the OS sets
    /// them. Not at the ROM font (<c>$E000-$E3FF</c>), which a program may read. <c>SETVBV</c> is <c>tern run</c>'s
    /// stand-in.
    /// </summary>
    internal static void Os(Ram ram)
    {
        const int Entry = 0xE7E0, Rti = 0xE7FF;
        byte[] sysvbv = [0x6C, 0x24, 0x02];                                 // jmp (VVBLKD)
        byte[] xitvbv = [0x68, 0xA8, 0x68, 0xAA, 0x68, 0x40];               // pla, tay, pla, tax, pla, rti
        byte[] entry =
        [
            0x2C, 0x0F, 0xD4,               // bit NMIST
            0x10, 0x03,                     // bpl vbi
            0x6C, 0x00, 0x02,               // jmp (VDSLST)
            0x48, 0x8A, 0x48, 0x98, 0x48,   // vbi: pha, txa, pha, tya, pha
            0x6C, 0x22, 0x02,               // jmp (VVBLKI)
        ];
        sysvbv.CopyTo(ram.Bytes, 0xE45F);
        xitvbv.CopyTo(ram.Bytes, 0xE462);
        entry.CopyTo(ram.Bytes, Entry);
        ram.Bytes[Rti] = 0x40;
        SetWord(ram, 0xFFFA, Entry);
        SetWord(ram, 0x0200, Rti);          // VDSLST
        SetWord(ram, 0x0222, 0xE45F);       // VVBLKI: SYSVBV
        SetWord(ram, 0x0224, 0xE462);       // VVBLKD: XITVBV
    }

    private static void SetWord(Ram ram, int at, int value)
    {
        ram.Bytes[at] = (byte)value;
        ram.Bytes[at + 1] = (byte)(value >> 8);
    }
}
