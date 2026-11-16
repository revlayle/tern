using Tern.Cpu;

namespace Tern.Compiler.Tools;

/// <summary>
/// The NES as <c>tern run</c> models it (spec 12.2): PPU memory and OAM, not pixels. CPU RAM ($0000-$07FF,
/// mirrored to $1FFF) is <see cref="Ram"/>'s bytes, so tests read variables as on the other targets; the
/// visible PRG ROM is copied there too, at $8000-$FFFF, and a write to the UxROM latch copies the bank it
/// selects to $8000-$BFFF. Writes through $2006/$2007 go to <see cref="Ppu"/>, 16 KB with the nametable
/// mirroring of the board and the palette's mirrors; OAM DMA ($4014) copies a page to <see cref="Oam"/>.
/// <see cref="Tick"/> starts a frame every 29,781 cycles: it sets the vblank flag that $2002 reports, which
/// a read clears and the end of vblank (<see cref="VblankCycles"/> later) clears too, and with bit 7 of
/// $2000 set raises an NMI; setting that bit while the flag is set raises one at once. $2006 takes the
/// address on its second write (decision 2026-11-15). The controllers read as no buttons, or as
/// <see cref="Buttons"/> gives them for a frame. Not modelled: rendering, sprite 0 hits, sprite overflow,
/// sound, other timing within a frame.
/// </summary>
public sealed class NesBus : IBus
{
    /// <summary>CPU cycles in one NTSC frame.</summary>
    public const int FrameCycles = 29_781;
    /// <summary>CPU cycles from the start of vblank to its end (20 scan lines), when the PPU clears the flag.</summary>
    public const int VblankCycles = 2_273;
    private const int Bank = 0x4000;

    private readonly Ram _ram;
    private readonly byte[] _prg;
    private readonly bool _chrRam;
    private readonly bool _vertical;
    private readonly int _mapper;
    private long _nextFrame = FrameCycles;
    private bool _vblank;
    /// <summary>The cycle at which the current vblank ends.</summary>
    private long _vblankEnd;
    /// <summary>Bit 7 of $2000 was set during vblank: an NMI at the end of the instruction.</summary>
    private bool _nmiNow;
    private bool _latch;
    private int _address;
    /// <summary>The high byte of the first $2006 write, until the second.</summary>
    private int _addressHigh;
    private byte _readBuffer;
    private byte _oamAddress;
    private int _shift1, _shift2;
    private bool _strobe;

    /// <summary>The PPU's 16 KB: pattern tables (CHR ROM or RAM), nametables, palette.</summary>
    public byte[] Ppu { get; } = new byte[0x4000];
    /// <summary>Sprite memory: 64 sprites of y, tile, attributes, x.</summary>
    public byte[] Oam { get; } = new byte[256];
    /// <summary>Frames started so far.</summary>
    public long Frame { get; private set; }
    /// <summary>The NMIs raised.</summary>
    public int Nmis { get; private set; }
    public byte Control { get; private set; }
    public byte Mask { get; private set; }
    public byte ScrollX { get; private set; }
    public byte ScrollY { get; private set; }
    /// <summary>The bank visible at $8000-$BFFF on UxROM.</summary>
    public int SelectedBank { get; private set; }
    /// <summary>The OAM DMA copies made.</summary>
    public int Dmas { get; private set; }
    /// <summary>
    /// The buttons of pad 1 or 2 (the second argument) in a frame, as <c>nes.pad</c> returns them: bit 7 A,
    /// then B, Select, Start, Up, Down, Left, bit 0 Right. Null: no buttons.
    /// </summary>
    public Func<long, int, byte>? Buttons { get; set; }
    /// <summary>The CPU on the bus, for its clock and the cycles an OAM DMA takes; null until one is made with this bus.</summary>
    public Cpu6502? Cpu { get; private set; }

    public void Attach(Cpu6502 cpu) => Cpu = cpu;

    /// <param name="image">The .nes file: the iNES header, PRG ROM, CHR ROM.</param>
    /// <exception cref="RunError">A mapper other than 0 (NROM) or 2 (UxROM), which the model does not have.</exception>
    public NesBus(Ram ram, byte[] image)
    {
        _ram = ram;
        int prgSize = image[4] * Bank, chrSize = image[5] * 0x2000;
        _prg = image[16..(16 + prgSize)];
        _chrRam = chrSize == 0;
        _vertical = (image[6] & 1) != 0;
        _mapper = image[6] >> 4 | (image[7] & 0xF0);
        if (_mapper is not (0 or 2))
            throw new RunError($"The cartridge has mapper {_mapper}, which tern run does not model: it runs mappers 0 (NROM) and 2 (UxROM). Run the program in an emulator such as Mesen");
        image.AsSpan(16 + prgSize, chrSize).CopyTo(Ppu);
        // NROM-128 is seen twice; otherwise the first bank at $8000 and the last at $C000.
        _prg.AsSpan(0, Bank).CopyTo(ram.Bytes.AsSpan(0x8000));
        _prg.AsSpan(prgSize - Bank, Bank).CopyTo(ram.Bytes.AsSpan(0xC000));
    }

    /// <summary>Starts the frames that are due by the CPU's cycle count: vblank, and the NMI if it is on.</summary>
    public void Tick(Cpu6502 cpu)
    {
        if (_nmiNow)
        {
            _nmiNow = false;
            Nmis++;
            cpu.Nmi();
        }
        while (cpu.Cycles >= _nextFrame)
        {
            _vblankEnd = _nextFrame + VblankCycles;
            _nextFrame += FrameCycles;
            Frame++;
            _vblank = true;
            if ((Control & 0x80) != 0)
            {
                Nmis++;
                cpu.Nmi();
            }
        }
    }

    /// <summary>The vblank flag as $2002 reports it: set at the start of vblank, clear after its end or a read.</summary>
    private bool InVblank => _vblank && (Cpu is null || Cpu.Cycles < _vblankEnd);

    public byte Read(ushort address)
    {
        if (address < 0x2000) return _ram.Bytes[address & 0x07FF];
        if (address < 0x4000)
        {
            switch (address & 7)
            {
                case 2:
                    byte status = (byte)(InVblank ? 0x80 : 0);
                    _vblank = false;
                    _latch = false;
                    return status;
                case 4: return Oam[_oamAddress];
                case 7:
                    int at = _address & 0x3FFF;
                    byte value = _readBuffer;
                    _readBuffer = Ppu[Map(at)];
                    // The palette is read at once; the buffer then holds the nametable byte under it.
                    if (at >= 0x3F00) { value = _readBuffer; _readBuffer = Ppu[Map(at - 0x1000)]; }
                    Step();
                    return value;
                default: return 0;
            }
        }
        if (address == 0x4016) return Serial(ref _shift1);
        if (address == 0x4017) return Serial(ref _shift2);
        // NROM and UxROM have no PRG RAM at $4020-$7FFF: nothing answers there.
        if (address < 0x8000) return 0;
        return _ram.Bytes[address];
    }

    public void Write(ushort address, byte value)
    {
        if (address < 0x2000) { _ram.Bytes[address & 0x07FF] = value; return; }
        if (address < 0x4000)
        {
            switch (address & 7)
            {
                case 0:
                    if ((Control & 0x80) == 0 && (value & 0x80) != 0 && InVblank) _nmiNow = true;
                    Control = value;
                    break;
                case 1: Mask = value; break;
                case 3: _oamAddress = value; break;
                case 4: Oam[_oamAddress++] = value; break;
                case 5:
                    if (!_latch) ScrollX = value; else ScrollY = value;
                    _latch = !_latch;
                    break;
                case 6:
                    if (!_latch) _addressHigh = value & 0x3F;
                    else _address = _addressHigh << 8 | value;
                    _latch = !_latch;
                    break;
                case 7:
                    int at = Map(_address & 0x3FFF);
                    if (at >= 0x2000 || _chrRam) Ppu[at] = value;
                    Step();
                    break;
            }
            return;
        }
        if (address == 0x4014)
        {
            for (int i = 0; i < 256; i++) Oam[(byte)(_oamAddress + i)] = Read((ushort)(value << 8 | i));
            Dmas++;
            // 513 cycles, and one more on an odd cycle (counted from the start of the instruction).
            Cpu?.Stall(513 + (int)(Cpu.Cycles & 1));
            return;
        }
        if (address == 0x4016)
        {
            _strobe = (value & 1) != 0;
            if (_strobe) Latch();
            return;
        }
        if (address < 0x8000) return;
        if (_mapper == 2)
        {
            // UxROM's latch sees the ROM's byte at the address as well as the CPU's (bus conflicts).
            int bank = (value & _ram.Bytes[address]) % (_prg.Length / Bank);
            SelectedBank = bank;
            _prg.AsSpan(bank * Bank, Bank).CopyTo(_ram.Bytes.AsSpan(0x8000));
        }
    }

    /// <summary>The nametable at <paramref name="table"/> (0-3) as 30 rows of 32 tile numbers.</summary>
    public byte[][] Nametable(int table) =>
        Enumerable.Range(0, 30).Select(r => Enumerable.Range(0, 32).Select(c => Ppu[Map(0x2000 + table * 0x400 + r * 32 + c)]).ToArray()).ToArray();

    /// <summary>Where a PPU address is stored: the nametables with the board's mirroring, the palette with its mirrors.</summary>
    public int Map(int at)
    {
        at &= 0x3FFF;
        if (at >= 0x3F00)
        {
            at &= 0x3F1F;
            if ((at & 0x13) == 0x10) at -= 0x10;
            return at;
        }
        if (at >= 0x3000) at -= 0x1000;
        if (at >= 0x2000) at = _vertical ? at & ~0x0800 : (at & ~0x0C00) | (at & 0x0800) >> 1;
        return at;
    }

    private void Step() => _address = (_address + ((Control & 0x04) != 0 ? 32 : 1)) & 0x3FFF;

    private void Latch()
    {
        _shift1 = Buttons?.Invoke(Frame, 1) ?? 0;
        _shift2 = Buttons?.Invoke(Frame, 2) ?? 0;
    }

    /// <summary>The next button of a pad, A first; 1 after the eighth.</summary>
    private byte Serial(ref int shift)
    {
        if (_strobe) Latch();
        byte bit = (byte)((shift & 0x80) != 0 ? 1 : 0);
        shift = (shift << 1 | 1) & 0xFF;
        return bit;
    }
}
