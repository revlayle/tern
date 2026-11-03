using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>
/// A stand-in for the VIC-II: 63 cycles per raster line, 312 lines per frame, the raster compare
/// interrupt, and the sprites. Sprite k starts on the line its y register holds (when enabled), shows
/// 21 lines, and takes its x, color and pointer from the registers at the start of each line.
/// </summary>
public sealed class FakeVic : IBus
{
    public const int CyclesPerLine = 63;
    public const int Lines = 312;

    /// <summary>One sprite line as the VIC would draw it.</summary>
    public readonly record struct Shown(int Slot, int X, int Pointer, int Color, bool Multicolor, bool ExpandX);

    private readonly Ram _ram;
    private readonly int _pointerBase;
    private readonly int[] _regs = new int[0x40];
    private int _compare;
    private int _status;
    private readonly int[] _remaining = new int[8];
    private int _lastLine = -1;

    public Cpu6502 Cpu { get; set; } = null!;

    /// <summary>What each raster line showed, over the whole run (line number counted from 0).</summary>
    public Dictionary<long, List<Shown>> Seen { get; } = [];

    /// <summary>Every write to a VIC register: absolute line, cycle in the line, register, value.</summary>
    public List<(long Line, int Cycle, int Reg, byte Value)> Writes { get; } = [];

    public FakeVic(Ram ram, int pointerBase = 0x07F8)
    {
        _ram = ram;
        _pointerBase = pointerBase;
    }

    public long Line => Cpu.Cycles / CyclesPerLine;
    public int RasterLine => (int)(Line % Lines);

    public byte Read(ushort address)
    {
        if (address >= 0xD000 && address < 0xD040)
        {
            int r = address - 0xD000;
            if (r == 0x12) return (byte)RasterLine;
            if (r == 0x11) return (byte)((_regs[r] & 0x7F) | (RasterLine >> 1 & 0x80));
            if (r == 0x19) return (byte)(_status | 0x70 | ((_status & _regs[0x1A]) != 0 ? 0x80 : 0));
            return (byte)_regs[r];
        }
        return _ram.Read(address);
    }

    public void Write(ushort address, byte value)
    {
        if (address >= 0xD000 && address < 0xD040)
        {
            int r = address - 0xD000;
            Writes.Add((Line, (int)(Cpu.Cycles % CyclesPerLine), r, value));
            if (r == 0x12)
            {
                _compare = _compare & 0x100 | value;
                if (_compare == RasterLine) _status |= 1;   // as in VICE: writing the current line raises the interrupt
            }
            else if (r == 0x11) { _compare = _compare & 0xFF | (value & 0x80) << 1; _regs[r] = value; }
            else if (r == 0x19) _status &= ~value;
            else _regs[r] = value;
            return;
        }
        _ram.Write(address, value);
    }

    public int Reg(int r) => _regs[r];

    /// <summary>Call after each CPU step: new raster lines draw sprites and may raise the interrupt.</summary>
    public void Tick()
    {
        long line = Line;
        if (line == _lastLine) return;
        _lastLine = (int)line;
        int raster = (int)(line % Lines);
        if (raster == _compare) _status |= 1;
        DrawLine(line, raster);
        if (StealCycles)
        {
            // Bad lines take the bus for about 40 cycles; each sprite line fetches 2-3 bytes.
            bool badLine = raster is >= 0x30 and <= 0xF7 && (raster & 7) == (_regs[0x11] & 7) && (_regs[0x11] & 0x10) != 0;
            int stolen = badLine ? BadLineSteal : 0;
            if (Seen.TryGetValue(line, out var shown)) stolen += shown.Count * SpriteSteal;
            Cpu.Cycles += stolen;
        }
    }

    /// <summary>When set, bad lines and sprite fetches take cycles from the CPU, as on a real C64.</summary>
    public bool StealCycles { get; set; }
    public int SpriteSteal { get; set; } = 2;
    public int BadLineSteal { get; set; } = 40;

    /// <summary>True while an enabled interrupt is waiting.</summary>
    public bool IrqPending => (_status & _regs[0x1A]) != 0;

    private void DrawLine(long line, int raster)
    {
        var list = new List<Shown>();
        for (int k = 0; k < 8; k++)
        {
            if (_remaining[k] == 0 && (_regs[0x15] >> k & 1) != 0 && _regs[1 + 2 * k] == raster && raster < 256)
                _remaining[k] = 21;
            if (_remaining[k] > 0)
            {
                _remaining[k]--;
                int x = _regs[2 * k] | (_regs[0x10] >> k & 1) << 8;
                list.Add(new Shown(k, x, _ram.Bytes[_pointerBase + k], _regs[0x27 + k] & 15,
                    (_regs[0x1C] >> k & 1) != 0, (_regs[0x1D] >> k & 1) != 0));
            }
        }
        if (list.Count > 0) Seen[line] = list;
    }
}
