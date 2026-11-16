using System.Text;
using Tern.Cpu;

namespace Tern.Compiler.Tools;

/// <summary>
/// The Apple II under ProDOS (spec 12.2): the 40 x 24 text screen, page 1 at $0400, rows in the
/// Apple II's order ($0400 + 128 * (row % 8) + 40 * (row / 8)), cleared (spaces, $A0) with the
/// cursor at the top left. COUT ($FDED) prints the byte in A as the ROM's COUT1 does, at CH and CV
/// ($24, $25) through BASL ($28): codes from $A0 are masked with INVFLG ($32, $FF normal, $3F inverse,
/// $7F flashing), codes below $80 are stored as they are, and of $80-$9F only return ($8D), line feed
/// ($8A) and backspace ($88) do anything. Lines wrap at 40 columns and the screen scrolls at the
/// bottom. HOME ($FC58) clears the screen. The startup code's other ROM calls (SETNORM, INIT, SETVID,
/// SETKBD) set the bytes they set. The MLI ($BF00) has stand-ins for QUIT, which ends the run,
/// GET_PREFIX (an empty prefix) and SET_PREFIX, and for reading the build's own overlay files (spec
/// 12.2): GET_FILE_INFO, OPEN, GET_EOF, READ and CLOSE find the one whose file name, in capitals, is
/// the path's last part, upshifted as the MLI does ("LEVEL1.BIN", "level1.bin" or "/DISK/LEVEL1.BIN" is level1.bin), as a BIN file with its
/// load address as the aux type. A name ProDOS cannot store (1-15 letters, digits and dots, starting
/// with a letter) fails with $40, any other with $46, as a file not on the disk. CLOSE with
/// reference 0 closes every file. A call that writes stops the run.
/// </summary>
internal sealed class Apple2Machine(IReadOnlyList<OverlayFile> overlays) : Machine
{
    public const int ScreenAddress = 0x0400, Columns = 40, Rows = 24;
    private const int Wndlft = 0x20, Wndwdth = 0x21, Wndtop = 0x22, Wndbtm = 0x23, Ch = 0x24, Cv = 0x25, Basl = 0x28, Invflg = 0x32, Csw = 0x36, Ksw = 0x38;
    private const int Blank = 0xA0;

    protected override IReadOnlyList<StandIn> Routines =>
    [
        new(0xFDED, "COUT", (cpu, ram, output) => { Print(cpu.A, ram, output); return null; }),
        new(0xFC58, "HOME", (_, ram, _) =>
        {
            for (int row = ram.Bytes[Wndtop]; row < Math.Min((int)ram.Bytes[Wndbtm], Rows); row++) ram.Bytes.AsSpan(Row(row), Columns).Fill(Blank);
            Cursor(ram, ram.Bytes[Wndtop], 0);
            return null;
        }),
        new(0xFE84, "SETNORM", (cpu, ram, _) => { ram.Bytes[Invflg] = 0xFF; cpu.Y = 0xFF; return null; }),
        new(0xFB2F, "INIT", (_, ram, _) => { SetWindow(ram); Cursor(ram, Rows - 1, 0); return null; }),
        new(0xFE93, "SETVID", (_, ram, _) => { (ram.Bytes[Csw], ram.Bytes[Csw + 1]) = (0xF0, 0xFD); return null; }),
        new(0xFE89, "SETKBD", (_, ram, _) => { (ram.Bytes[Ksw], ram.Bytes[Ksw + 1]) = (0x1B, 0xFD); return null; }),
        new(0xBF00, "MLI", Mli, "for QUIT, CLOSE, GET_PREFIX, SET_PREFIX, and for reading the build's overlay files"),
    ];

    private const int BadPath = 0x40, TooManyFiles = 0x42, BadRef = 0x43, NotFound = 0x46, Eof = 0x4C;
    /// <summary>The overlay file open under each reference number, and the next byte to read.</summary>
    private readonly Dictionary<int, (byte[] File, int Pos)> _open = [];

    public override void Start(Ram ram)
    {
        for (int row = 0; row < Rows; row++) ram.Bytes.AsSpan(Row(row), Columns).Fill(Blank);
        SetWindow(ram);
        ram.Bytes[Invflg] = 0xFF;
        Cursor(ram, 0, 0);
        (ram.Bytes[Csw], ram.Bytes[Csw + 1]) = (0xF0, 0xFD);
        (ram.Bytes[Ksw], ram.Bytes[Ksw + 1]) = (0x1B, 0xFD);
    }

    public override (int Address, int Columns, int Rows) Screen(Ram ram) => (ScreenAddress, Columns, Rows);

    public override int RowAddress(Ram ram, int row) => Row(row);

    /// <summary>The address of text page 1's row <paramref name="row"/>.</summary>
    public static int Row(int row) => ScreenAddress + 128 * (row % 8) + 40 * (row / 8);

    /// <summary>The MLI: QUIT, the prefix calls, and reading the build's overlay files.</summary>
    private string? Mli(Cpu6502 cpu, Ram ram, StringBuilder output)
    {
        // jsr $BF00 is followed by the command byte and the parameters' address; the MLI returns after them.
        int sp = 0x100 + (byte)(cpu.SP + 1);
        int at = (ram.Bytes[sp] | ram.Bytes[sp + 1] << 8) + 1;
        int command = ram.Bytes[at & 0xFFFF];
        int list = ram.Bytes[(at + 1) & 0xFFFF] | ram.Bytes[(at + 2) & 0xFFFF] << 8;
        if (command == 0x65) return "the program quit (MLI QUIT)";
        if (FileCall(command, list, ram) is not int error)
            return command is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xCB or 0xD0
                ? $"MLI call ${command:X2}, which writes to the disk; the emulator only reads the build's overlay files"
                : $"MLI call ${command:X2}, which the emulator does not have (stand-ins: QUIT $65, GET_PREFIX $C7, SET_PREFIX $C6, and GET_FILE_INFO $C4, OPEN $C8, GET_EOF $D1, READ $CA and CLOSE $CC for the build's overlay files)";
        int back = at + 2;
        (ram.Bytes[sp], ram.Bytes[sp + 1]) = ((byte)back, (byte)(back >> 8));
        cpu.A = (byte)error;
        cpu.C = error != 0;
        cpu.Z = error == 0;
        cpu.N = error >= 0x80;
        return null;
    }

    /// <summary>COUT1: the character at the cursor, through INVFLG; return, line feed and backspace.</summary>
    private void Print(byte code, Ram ram, StringBuilder output)
    {
        int top = ram.Bytes[Wndtop], bottom = Math.Min((int)ram.Bytes[Wndbtm], Rows), width = Math.Min((int)ram.Bytes[Wndwdth], Columns);
        int row = Math.Min((int)ram.Bytes[Cv], bottom - 1), column = ram.Bytes[Ch];
        if (code >= 0xA0) code &= ram.Bytes[Invflg];
        if (code is >= 0x80 and < 0xA0)
        {
            switch (code)
            {
                case 0x8D: output.Append('\n'); column = 0; Down(); break;
                case 0x8A: Down(); break;
                case 0x88:
                    if (column > 0) column--;
                    else
                    {
                        column = width - 1;
                        if (row > top) row--;
                    }
                    break;
            }
        }
        else
        {
            output.Append(ScreenChar(code));
            int basl = ram.Bytes[Basl] | ram.Bytes[Basl + 1] << 8;
            ram.Bytes[(basl + column) & 0xFFFF] = code;
            if (++column >= width)
            {
                column = 0;
                Down();
            }
        }
        Cursor(ram, row, column);

        void Down()
        {
            if (row < bottom - 1) { row++; return; }
            for (int r = top; r < bottom - 1; r++) ram.Bytes.AsSpan(Row(r + 1), Columns).CopyTo(ram.Bytes.AsSpan(Row(r)));
            ram.Bytes.AsSpan(Row(bottom - 1), Columns).Fill(Blank);
        }
    }

    /// <summary>The MLI call <paramref name="command"/> with its parameter list at <paramref name="list"/>: its error code, 0 for success, or null when it has no stand-in.</summary>
    private int? FileCall(int command, int list, Ram ram)
    {
        var b = ram.Bytes;
        int Word(int offset) => b[(list + offset) & 0xFFFF] | b[(list + offset + 1) & 0xFFFF] << 8;
        void Put(int offset, int value, int size = 2)
        {
            for (int i = 0; i < size; i++) b[(list + offset + i) & 0xFFFF] = (byte)(value >> 8 * i);
        }
        switch (command)
        {
            case 0xC7:                                  // GET_PREFIX: empty
                b[Word(1)] = 0;
                return 0;
            case 0xC6:                                  // SET_PREFIX
                return 0;
            case 0xC4:                                  // GET_FILE_INFO: a BIN file, its load address as aux type
            {
                if (Find(Word(1)) is not { } file) return Missing(Word(1));
                Put(3, 0xC3, 1);
                Put(4, 0x06, 1);
                Put(5, file.LoadAddress);
                // A seedling holds up to 512 bytes in one block; a sapling has an index block too.
                bool seedling = file.Image.Length <= 512;
                Put(7, seedling ? 1 : 2, 1);
                Put(8, seedling ? 1 : (file.Image.Length + 511) / 512 + 1);
                for (int i = 10; i < 18; i++) b[(list + i) & 0xFFFF] = 0;
                return 0;
            }
            case 0xC8:                                  // OPEN
            {
                if (Find(Word(1)) is not { } file) return Missing(Word(1));
                if (_open.Count == 8) return TooManyFiles;     // ProDOS has 8 file control blocks
                int reference = Enumerable.Range(1, 8).First(r => !_open.ContainsKey(r));
                _open[reference] = (file.Image, 0);
                Put(5, reference, 1);
                return 0;
            }
            case 0xD1:                                  // GET_EOF
            {
                if (!_open.TryGetValue(b[(list + 1) & 0xFFFF], out var f)) return BadRef;
                Put(2, f.File.Length, 3);
                return 0;
            }
            case 0xCA:                                  // READ
            {
                int reference = b[(list + 1) & 0xFFFF];
                if (!_open.TryGetValue(reference, out var f)) return BadRef;
                int n = Math.Min(Word(4), f.File.Length - f.Pos);
                for (int i = 0; i < n; i++) b[(Word(2) + i) & 0xFFFF] = f.File[f.Pos + i];
                _open[reference] = (f.File, f.Pos + n);
                Put(6, n);
                return n == 0 && Word(4) > 0 ? Eof : 0;
            }
            case 0xCC:                                  // CLOSE; reference 0 closes every file
            {
                int reference = b[(list + 1) & 0xFFFF];
                if (reference == 0) _open.Clear();
                else if (!_open.Remove(reference)) return BadRef;
                return 0;
            }
            default:
                return null;
        }

        // A path: a length byte, then the characters; the file is its last part.
        string Path(int at) => new([.. Enumerable.Range(1, b[at & 0xFFFF]).Select(i => (char)(b[(at + i) & 0xFFFF] & 0x7F))]);
        string Name(int at) => Path(at)[(Path(at).LastIndexOf('/') + 1)..];
        OverlayFile? Find(int at) => overlays.FirstOrDefault(o => (o.Name + ".bin").ToUpperInvariant() == Name(at).ToUpperInvariant());
        int Missing(int at) => System.Text.RegularExpressions.Regex.IsMatch(Name(at), @"^[A-Za-z][A-Za-z0-9.]{0,14}$") ? NotFound : BadPath;
    }

    private static void SetWindow(Ram ram)
    {
        ram.Bytes[Wndlft] = 0;
        ram.Bytes[Wndwdth] = Columns;
        ram.Bytes[Wndtop] = 0;
        ram.Bytes[Wndbtm] = Rows;
    }

    /// <summary>Sets CV and CH, and BASL to the row's address, as VTAB does.</summary>
    private static void Cursor(Ram ram, int row, int column)
    {
        ram.Bytes[Cv] = (byte)row;
        ram.Bytes[Ch] = (byte)column;
        int address = Row(row);
        (ram.Bytes[Basl], ram.Bytes[Basl + 1]) = ((byte)address, (byte)(address >> 8));
    }

    /// <summary>
    /// A screen code: normal text ($80-$FF) as its ASCII character ($E0-$FF as lower case, as the
    /// enhanced IIe shows them); inverse ($00-$3F) and flashing ($40-$7F) as the plain characters.
    /// </summary>
    public override char ScreenChar(byte code)
    {
        int c = code & 0x7F;
        if (code < 0x80) c &= 0x3F;
        return c switch
        {
            < 0x20 => (char)(c + 0x40),
            < 0x7F => (char)c,
            _ => '#',
        };
    }
}
