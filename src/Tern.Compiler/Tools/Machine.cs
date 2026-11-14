using System.Text;
using Tern.Cpu;

namespace Tern.Compiler.Tools;

/// <summary>
/// What <c>tern run</c> knows of one platform beyond the CPU and plain RAM: memory as the machine
/// starts, stand-ins for a few ROM routines, and the text screen.
/// </summary>
internal abstract class Machine
{
    /// <summary>The platform's machine, for a run of <paramref name="program"/> (whose overlay files the C64's LOAD finds).</summary>
    public static Machine For(Platform platform, CompileResult? program = null) => platform.Format switch
    {
        OutputFormat.Xex => new AtariMachine(program?.Overlays ?? []),
        OutputFormat.System => new Apple2Machine(),
        _ => new C64Machine(program?.Overlays ?? []),
    };

    /// <summary>The routines with stand-ins, for the message when the run reaches another ROM address.</summary>
    public abstract string StandIns { get; }

    /// <summary>Sets up memory as after the machine starts, before the program is loaded.</summary>
    public abstract void Start(Ram ram);

    /// <summary>Whether <paramref name="pc"/> is a routine with a stand-in.</summary>
    public abstract bool Has(int pc);

    /// <summary>
    /// Does the work of the routine at <paramref name="pc"/> (the caller then returns as rts would). Text
    /// printed goes to <paramref name="output"/>. Returns why the run stops, or null.
    /// </summary>
    public abstract string? Call(int pc, Cpu6502 cpu, Ram ram, StringBuilder output);

    /// <summary>The text screen's address in <paramref name="ram"/>, columns and rows.</summary>
    public abstract (int Address, int Columns, int Rows) Screen(Ram ram);

    /// <summary>A byte of screen memory as text: letters, digits and punctuation as themselves, others as '#'.</summary>
    public abstract char ScreenChar(byte code);

    /// <summary>The address of the screen's row <paramref name="row"/>: rows follow each other, except on the Apple II.</summary>
    public virtual int RowAddress(Ram ram, int row)
    {
        var (address, columns, _) = Screen(ram);
        return address + row * columns;
    }

    /// <summary>The screen's lines as text, without trailing spaces.</summary>
    public IReadOnlyList<string> ScreenText(Ram ram)
    {
        var (_, columns, rows) = Screen(ram);
        return Enumerable.Range(0, rows).Select(r => new string(Enumerable.Range(0, columns)
            .Select(c => ScreenChar(ram.Bytes[(RowAddress(ram, r) + c) & 0xFFFF])).ToArray()).TrimEnd()).ToList();
    }
}

/// <summary>
/// The C64: the screen at $0400 starts cleared (spaces) in light blue. CHROUT ($FFD2) prints to the
/// screen, GETIN ($FFE4) returns 0 (no key), PLOT ($FFF0) sets or reads the cursor. SETNAM ($FFBD),
/// SETLFS ($FFBA) and LOAD ($FFD5) load the build's own overlay files: the one whose module name, in
/// capitals, is the file name (<c>c64.load</c> with "LEVEL1" loads level1.prg); any other name fails
/// with carry set, as for a file not on the disk.
/// </summary>
internal sealed class C64Machine(IReadOnlyList<OverlayFile> overlays) : Machine
{
    public const int ScreenAddress = 0x0400, Columns = 40, Rows = 25;
    private const int Chrout = 0xFFD2, Getin = 0xFFE4, Plot = 0xFFF0, Setnam = 0xFFBD, Setlfs = 0xFFBA, Load = 0xFFD5;

    private (int Row, int Column) _cursor;
    private string _fileName = "";
    private int _secondary;

    public override string StandIns => "CHROUT $FFD2, GETIN $FFE4, PLOT $FFF0, SETNAM $FFBD, SETLFS $FFBA, LOAD $FFD5";

    public override void Start(Ram ram)
    {
        ram.Bytes.AsSpan(ScreenAddress, Columns * Rows).Fill(32);
        ram.Bytes.AsSpan(0xD800, Columns * Rows).Fill(14);
    }

    public override bool Has(int pc) => pc is Chrout or Getin or Plot or Setnam or Setlfs or Load;

    public override (int Address, int Columns, int Rows) Screen(Ram ram) => (ScreenAddress, Columns, Rows);

    public override string? Call(int pc, Cpu6502 cpu, Ram ram, StringBuilder output)
    {
        switch (pc)
        {
            case Chrout: Print(cpu.A); cpu.C = false; break;
            case Getin: cpu.A = 0; cpu.Z = true; cpu.N = false; cpu.C = false; break;
            case Setnam:
                _fileName = string.Concat(Enumerable.Range(0, cpu.A).Select(i => (char)ram.Bytes[(cpu.X | cpu.Y << 8) + i & 0xFFFF]));
                return null;
            case Setlfs: _secondary = cpu.Y; return null;
            case Load:
            {
                // The build's overlays are the disk. Secondary address 0 loads to X/Y; X/Y return the end.
                var file = overlays.FirstOrDefault(o => o.Name.ToUpperInvariant() == _fileName);
                cpu.C = file is null;
                if (file is null) return null;
                int at = _secondary == 0 ? cpu.X | cpu.Y << 8 : file.LoadAddress;
                ram.Load((ushort)at, file.Image);
                int end = at + file.Image.Length;
                (cpu.X, cpu.Y) = ((byte)end, (byte)(end >> 8));
                return null;
            }
            default:
                if (cpu.C) { cpu.X = (byte)_cursor.Row; cpu.Y = (byte)_cursor.Column; }
                else _cursor = (Math.Min((int)cpu.X, Rows - 1), Math.Min((int)cpu.Y, Columns - 1));
                break;
        }
        ram.Bytes[0xD6] = (byte)_cursor.Row;
        ram.Bytes[0xD3] = (byte)_cursor.Column;
        return null;

        // CHROUT in the upper-case character set: letters, digits and punctuation, return, clear,
        // home and the four cursor moves; other control codes are ignored.
        void Print(byte ch)
        {
            switch (ch)
            {
                case 13: output.Append('\n'); NewLine(); return;
                case 147:
                    ram.Bytes.AsSpan(ScreenAddress, Columns * Rows).Fill(32);
                    _cursor = (0, 0);
                    return;
                case 19: _cursor = (0, 0); return;
                case 17: if (_cursor.Row < Rows - 1) _cursor.Row++; return;
                case 145: if (_cursor.Row > 0) _cursor.Row--; return;
                case 29: if (_cursor.Column < Columns - 1) _cursor.Column++; return;
                case 157: if (_cursor.Column > 0) _cursor.Column--; return;
            }
            int code = ch switch
            {
                >= 32 and < 64 => ch,
                >= 64 and < 96 => ch - 64,
                >= 96 and < 128 => ch - 32,
                >= 160 and < 192 => ch - 64,
                >= 192 and < 255 => ch - 128,
                255 => 94,
                _ => -1,
            };
            if (code < 0) return;
            output.Append(ScreenChar((byte)code));
            ram.Bytes[ScreenAddress + _cursor.Row * Columns + _cursor.Column] = (byte)code;
            ram.Bytes[0xD800 + _cursor.Row * Columns + _cursor.Column] = ram.Bytes[0x0286] == 0 ? (byte)14 : ram.Bytes[0x0286];
            if (++_cursor.Column == Columns) NewLine();
        }

        void NewLine()
        {
            _cursor.Column = 0;
            if (_cursor.Row < Rows - 1) { _cursor.Row++; return; }
            ram.Bytes.AsSpan(ScreenAddress + Columns, Columns * (Rows - 1)).CopyTo(ram.Bytes.AsSpan(ScreenAddress));
            ram.Bytes.AsSpan(ScreenAddress + Columns * (Rows - 1), Columns).Fill(32);
        }
    }

    /// <summary>A screen code in the upper-case character set; reversed characters as the plain ones.</summary>
    public override char ScreenChar(byte code)
    {
        code &= 0x7F;
        return code switch
        {
            0 => '@',
            >= 1 and <= 26 => (char)('A' + code - 1),
            27 => '[',
            28 => '\\',
            29 => ']',
            30 => '^',
            31 => '<',
            >= 32 and < 64 => (char)code,
            _ => '#',
        };
    }
}

/// <summary>
/// The Atari with 48 KB, as the OS leaves it for a program: the 40 x 24 text screen at $BC40 (SAVMSC
/// points to it), cleared, with the cursor at the left margin (column 2) of the top row. CIOV ($E456)
/// has a stand-in for IOCB 0, the screen editor, which the OS opens at startup: put bytes ($0B) prints
/// ICBLL/ICBLH bytes from ICBAL/ICBAH, or the byte in A when the length is 0. On IOCBs 1-7 it reads the
/// build's own overlay files: open ($03) for reading finds the one whose name, in capitals, follows
/// the device ("D1:LEVEL1.XEX" opens level1.xex, on any device). A name Atari DOS 2 cannot store
/// (1-8 letters and digits starting with a letter, an extension of up to 3) fails with $A5, DOS's file
/// name error, so an overlay named level_1 fails here as on a disk; any other name fails with $AA, as
/// a file not on the disk. Get bytes ($07) and close ($0C) work as in CIO. CIO returns the status in Y
/// and ICSTA, negative on an error. SETVBV ($E45C) sets the immediate (A = 6) or deferred (A = 7) VBI
/// vector to Y (low), X (high).
/// </summary>
internal sealed class AtariMachine(IReadOnlyList<OverlayFile> overlays) : Machine
{
    public const int ScreenAddress = 0xBC40, Columns = 40, Rows = 24;
    private const int Ciov = 0xE456, Setvbv = 0xE45C;
    private const int Savmsc = 0x58, Lmargn = 0x52, Rmargn = 0x53, Rowcrs = 0x54, Colcrs = 0x55;
    private const int Iccom = 0x0342, Icsta = 0x0343, Icbal = 0x0344, Icbll = 0x0348, Icax1 = 0x034A;
    private const int Ok = 1, Eof = 0x88, NotFound = 0xAA, AlreadyOpen = 0x81, NotOpen = 0x85, BadName = 0xA5;

    /// <summary>The overlay file open on each IOCB (1-7) and the next byte to read.</summary>
    private readonly Dictionary<int, (byte[] File, int Pos)> _open = [];

    public override string StandIns => "CIOV $E456 for put bytes on IOCB 0 (the screen editor) and for reading the build's overlay files on IOCBs 1-7, SETVBV $E45C";

    public override void Start(Ram ram)
    {
        ram.Bytes.AsSpan(ScreenAddress, Columns * Rows).Clear();
        ram.Bytes[Savmsc] = ScreenAddress & 0xFF;
        ram.Bytes[Savmsc + 1] = ScreenAddress >> 8;
        ram.Bytes[Lmargn] = 2;
        ram.Bytes[Rmargn] = Columns - 1;
        ram.Bytes[Rowcrs] = 0;
        ram.Bytes[Colcrs] = 2;
        ram.Bytes[Colcrs + 1] = 0;
    }

    public override bool Has(int pc) => pc is Ciov or Setvbv;

    public override (int Address, int Columns, int Rows) Screen(Ram ram) => (ram.Bytes[Savmsc] | ram.Bytes[Savmsc + 1] << 8, Columns, Rows);

    public override string? Call(int pc, Cpu6502 cpu, Ram ram, StringBuilder output)
    {
        if (pc == Setvbv)
        {
            if (cpu.A is not (6 or 7)) return $"SETVBV with A = {cpu.A}; the stand-in sets the VBI vectors only (A = 6 or 7)";
            int vector = cpu.A == 6 ? 0x0222 : 0x0224;
            ram.Bytes[vector] = cpu.Y;
            ram.Bytes[vector + 1] = cpu.X;
            return null;
        }
        int x = cpu.X, command = ram.Bytes[Iccom + x];
        int buffer = ram.Bytes[Icbal + x] | ram.Bytes[Icbal + x + 1] << 8;
        int length = ram.Bytes[Icbll + x] | ram.Bytes[Icbll + x + 1] << 8;
        int status = Ok;
        if (x == 0 && command == 0x0B)
        {
            if (length == 0) Put(cpu.A);
            else
                for (int i = 0; i < length; i++) Put(ram.Bytes[(buffer + i) & 0xFFFF]);
        }
        else if (x is > 0 and < 0x80 && (x & 0x0F) == 0 && command is 0x03 or 0x07 or 0x0C)
        {
            if (File() is { } why) return why;
        }
        else
            return $"CIO command ${command:X2} on IOCB {x >> 4}, which the emulator does not have (stand-ins: put bytes, $0B, on IOCB 0, the screen editor; open for reading, get bytes and close on IOCBs 1-7, for the build's overlay files)";
        ram.Bytes[Icsta + x] = (byte)status;
        cpu.Y = (byte)status;
        cpu.N = status >= 0x80;
        cpu.Z = false;
        return null;

        // The build's overlay files are the disk, for reading.
        string? File()
        {
            switch (command)
            {
                case 0x03:
                {
                    int mode = ram.Bytes[Icax1 + x];
                    if (mode != 4)
                        return $"CIO open with mode {mode} on IOCB {x >> 4}; the emulator only reads the build's overlay files (mode 4)";
                    if (_open.ContainsKey(x)) { status = AlreadyOpen; break; }
                    var name = new StringBuilder();
                    for (int i = 0; i < 128 && ram.Bytes[(buffer + i) & 0xFFFF] != 0x9B; i++) name.Append((char)ram.Bytes[(buffer + i) & 0xFFFF]);
                    var text = name.ToString();
                    string fileName = text[(text.IndexOf(':') + 1)..];
                    bool dosName = text.Contains(':') && System.Text.RegularExpressions.Regex.IsMatch(fileName, @"^[A-Z][A-Z0-9]{0,7}(\.[A-Z0-9]{0,3})?$");
                    var file = dosName ? overlays.FirstOrDefault(o => (o.Name + ".xex").ToUpperInvariant() == fileName) : null;
                    _open[x] = (file?.File ?? [], 0);     // as in CIO, the IOCB stays taken after a failed open
                    if (file is null) status = text.Contains(':') && !dosName ? BadName : NotFound;
                    break;
                }
                case 0x07:
                {
                    if (!_open.TryGetValue(x, out var f)) { status = NotOpen; break; }
                    if (length == 0)
                    {
                        // One byte, into A.
                        if (f.Pos == f.File.Length) { status = Eof; break; }
                        cpu.A = f.File[f.Pos];
                        _open[x] = (f.File, f.Pos + 1);
                        break;
                    }
                    int n = Math.Min(length, f.File.Length - f.Pos);
                    for (int i = 0; i < n; i++) ram.Bytes[(buffer + i) & 0xFFFF] = f.File[f.Pos + i];
                    ram.Bytes[Icbll + x] = (byte)n;
                    ram.Bytes[Icbll + x + 1] = (byte)(n >> 8);
                    _open[x] = (f.File, f.Pos + n);
                    if (n < length) status = Eof;
                    break;
                }
                default:
                    _open.Remove(x);
                    break;
            }
            return null;
        }

        // The screen editor: the end-of-line code, clear, the four cursor moves and backspace; other
        // control codes are ignored. Lines go from the left to the right margin and scroll at the bottom.
        void Put(byte ch)
        {
            var (screen, _, _) = Screen(ram);
            int left = ram.Bytes[Lmargn], right = Math.Min((int)ram.Bytes[Rmargn], Columns - 1);
            int row = Math.Min((int)ram.Bytes[Rowcrs], Rows - 1), column = Math.Min((int)ram.Bytes[Colcrs], right);
            switch (ch)
            {
                case 0x9B: output.Append('\n'); NewLine(); break;
                case 0x7D:
                    Clear(0, Rows);
                    (row, column) = (0, left);
                    break;
                case 0x1C: if (row > 0) row--; break;
                case 0x1D: if (row < Rows - 1) row++; break;
                case 0x1E: if (column > left) column--; break;
                case 0x1F: if (column < right) column++; break;
                case 0x7E:
                    if (column > left) column--;
                    ram.Bytes[(screen + row * Columns + column) & 0xFFFF] = 0;
                    break;
                case 0x7F or (>= 0x9C and <= 0x9F) or (>= 0xFD): break;
                default:
                    int a = ch & 0x7F;
                    int code = (a < 0x20 ? a + 0x40 : a < 0x60 ? a - 0x20 : a) | (ch & 0x80);
                    output.Append(ScreenChar((byte)code));
                    ram.Bytes[(screen + row * Columns + column) & 0xFFFF] = (byte)code;
                    if (++column > right) NewLine();
                    break;
            }
            ram.Bytes[Rowcrs] = (byte)row;
            ram.Bytes[Colcrs] = (byte)column;
            ram.Bytes[Colcrs + 1] = 0;

            void NewLine()
            {
                column = left;
                if (row < Rows - 1) { row++; return; }
                ram.Bytes.AsSpan(screen + Columns, Columns * (Rows - 1)).CopyTo(ram.Bytes.AsSpan(screen));
                Clear(Rows - 1, 1);
            }

            void Clear(int first, int rows) => ram.Bytes.AsSpan(screen + first * Columns, rows * Columns).Clear();
        }
    }

    /// <summary>An internal screen code; inverse characters as the plain ones.</summary>
    public override char ScreenChar(byte code)
    {
        code &= 0x7F;
        return code switch
        {
            < 0x40 => (char)(code + 0x20),
            >= 0x61 and <= 0x7A => (char)code,
            0x7C => '|',
            _ => '#',
        };
    }
}
