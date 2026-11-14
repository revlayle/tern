using Tern.Cpu;

namespace Tern.Compiler.Tools;

/// <summary>
/// The NES as <c>tern run</c> models it (spec 12.2): PPU memory and OAM, not pixels. CPU RAM ($0000-$07FF,
/// mirrored to $1FFF) is <see cref="Ram"/>'s bytes, so tests read variables as on the other targets; the
/// visible PRG ROM is copied there too, at $8000-$FFFF, and a write to the UxROM latch copies the bank it
/// selects to $8000-$BFFF. Writes through $2006/$2007 go to <see cref="Ppu"/>, 16 KB with the nametable
/// mirroring of the board and the palette's mirrors; OAM DMA ($4014) copies a page to <see cref="Oam"/>.
/// <see cref="Tick"/> starts a frame every 29,781 cycles: it sets the vblank flag that $2002 reports (a
/// read clears it) and, with bit 7 of $2000 set, raises an NMI. The controllers read as no buttons, or as
/// <see cref="Buttons"/> gives them for a frame. Not modelled: rendering, sprite 0 hits, sprite overflow,
/// sound, timing within a frame.
/// </summary>
public sealed class NesBus : IBus
{
    /// <summary>CPU cycles in one NTSC frame.</summary>
    public const int FrameCycles = 29_781;
    private const int Bank = 0x4000;

    private readonly Ram _ram;
    private readonly byte[] _prg;
    private readonly bool _chrRam;
    private readonly bool _vertical;
    private readonly int _mapper;
    private long _nextFrame = FrameCycles;
    private bool _vblank;
    private bool _latch;
    private int _address;
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
    /// <summary>The CPU, for the cycles an OAM DMA takes.</summary>
    public Cpu6502? Cpu { get; set; }

    /// <param name="image">The .nes file: the iNES header, PRG ROM, CHR ROM.</param>
    public NesBus(Ram ram, byte[] image)
    {
        _ram = ram;
        int prgSize = image[4] * Bank, chrSize = image[5] * 0x2000;
        _prg = image[16..(16 + prgSize)];
        _chrRam = chrSize == 0;
        _vertical = (image[6] & 1) != 0;
        _mapper = image[6] >> 4 | (image[7] & 0xF0);
        image.AsSpan(16 + prgSize, chrSize).CopyTo(Ppu);
        // NROM-128 is seen twice; otherwise the first bank at $8000 and the last at $C000.
        _prg.AsSpan(0, Bank).CopyTo(ram.Bytes.AsSpan(0x8000));
        _prg.AsSpan(prgSize - Bank, Bank).CopyTo(ram.Bytes.AsSpan(0xC000));
    }

    /// <summary>Starts the frames that are due by the CPU's cycle count: vblank, and the NMI if it is on.</summary>
    public void Tick(Cpu6502 cpu)
    {
        while (cpu.Cycles >= _nextFrame)
        {
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

    public byte Read(ushort address)
    {
        if (address < 0x2000) return _ram.Bytes[address & 0x07FF];
        if (address < 0x4000)
        {
            switch (address & 7)
            {
                case 2:
                    byte status = (byte)(_vblank ? 0x80 : 0);
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
        if (address < 0x4020) return 0;
        return _ram.Bytes[address];
    }

    public void Write(ushort address, byte value)
    {
        if (address < 0x2000) { _ram.Bytes[address & 0x07FF] = value; return; }
        if (address < 0x4000)
        {
            switch (address & 7)
            {
                case 0: Control = value; break;
                case 1: Mask = value; break;
                case 3: _oamAddress = value; break;
                case 4: Oam[_oamAddress++] = value; break;
                case 5:
                    if (!_latch) ScrollX = value; else ScrollY = value;
                    _latch = !_latch;
                    break;
                case 6:
                    _address = !_latch ? (value & 0x3F) << 8 | (_address & 0xFF) : (_address & 0x3F00) | value;
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
            if (Cpu is not null) Cpu.Cycles += 513 + (Cpu.Cycles & 1);
            return;
        }
        if (address == 0x4016)
        {
            _strobe = (value & 1) != 0;
            if (_strobe) Latch();
            return;
        }
        if (address < 0x8000) { if (address >= 0x4020) _ram.Bytes[address] = value; return; }
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
