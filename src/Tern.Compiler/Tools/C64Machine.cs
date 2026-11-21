using System.Text;
using Tern.Cpu;

namespace Tern.Compiler.Tools;

/// <summary>
/// The C64: the screen at $0400 starts cleared (spaces) in light blue. CHROUT ($FFD2) prints to the
/// screen, GETIN ($FFE4) returns 0 (no key), PLOT ($FFF0) sets or reads the cursor. SETNAM ($FFBD),
/// SETLFS ($FFBA) and LOAD ($FFD5) load the build's own overlay files: the one whose module name, in
/// capitals, is the file name (<c>c64.load</c> with "LEVEL1" loads level1.prg); any other name fails
/// with carry set, as for a file not on the disk.
/// </summary>
internal sealed class C64Machine(OverlayDisk disk) : Machine
{
    public const int ScreenAddress = 0x0400, Columns = 40, Rows = 25;
    private const int ColorRam = 0xD800;
    /// <summary>The KERNAL's cursor row and column (TBLX, PNTR).</summary>
    private const int CursorRow = 0xD6, CursorColumn = 0xD3;
    /// <summary>The color CHROUT writes into color RAM (COLOR).</summary>
    private const int TextColor = 0x0286;
    private const byte Space = 32, LightBlue = 14;
    /// <summary>The KERNAL's "file not found".</summary>
    private const byte FileNotFound = 4;

    private (int Row, int Column) _cursor;
    private string _fileName = "";
    private int _secondary;

    protected override IReadOnlyList<StandIn> Routines =>
    [
        new(0xFFD2, "CHROUT", (cpu, ram, output) => { Print(cpu.A, ram, output); cpu.C = false; return CursorBytes(ram); }),
        new(0xFFE4, "GETIN", (cpu, ram, _) => { cpu.A = 0; cpu.Z = true; cpu.N = false; cpu.C = false; return CursorBytes(ram); }),
        new(0xFFF0, "PLOT", Plot),
        new(0xFFBD, "SETNAM", (cpu, ram, _) =>
        {
            _fileName = string.Concat(Enumerable.Range(0, cpu.A).Select(i => (char)ram.Bytes[(cpu.X | cpu.Y << 8) + i & 0xFFFF]));
            return null;
        }),
        new(0xFFBA, "SETLFS", (cpu, _, _) => { _secondary = cpu.Y; return null; }),
        new(0xFFD5, "LOAD", (cpu, ram, _) => Load(cpu, ram)),
    ];

    public override void Start(Ram ram)
    {
        Text(ram).Clear();
        ram.Bytes.AsSpan(ColorRam, Columns * Rows).Fill(LightBlue);
        ram.Bytes[TextColor] = LightBlue;     // the KERNAL's light blue
        ram.Bytes[CursorRow] = 0;
        ram.Bytes[CursorColumn] = 0;
        // Idle input (decision 2026-11-21, question 175): CIA 1's ports read no joystick and no key.
        ram.Bytes[0xDC00] = 0xFF;
        ram.Bytes[0xDC01] = 0xFF;
    }

    public override (int Address, int Columns, int Rows) Screen(Ram ram) => (ScreenAddress, Columns, Rows);

    /// <summary>The screen with the cursor, which the KERNAL keeps here and copies to $D6 and $D3 (<see cref="CursorBytes"/>).</summary>
    private static TextScreen Text(Ram ram, int row = 0, int column = 0) =>
        new(ram, r => ScreenAddress + r * Columns, Columns, Rows, Space) { Row = row, Column = column };

    /// <summary>PLOT: reads the cursor with carry set, sets it with carry clear.</summary>
    private string? Plot(Cpu6502 cpu, Ram ram, StringBuilder output)
    {
        if (cpu.C) { cpu.X = (byte)_cursor.Row; cpu.Y = (byte)_cursor.Column; }
        else _cursor = (Math.Min((int)cpu.X, Rows - 1), Math.Min((int)cpu.Y, Columns - 1));
        return CursorBytes(ram);
    }

    /// <summary>The KERNAL's cursor row and column, as CHROUT, GETIN and PLOT leave them.</summary>
    private string? CursorBytes(Ram ram)
    {
        ram.Bytes[CursorRow] = (byte)_cursor.Row;
        ram.Bytes[CursorColumn] = (byte)_cursor.Column;
        return null;
    }

    /// <summary>The build's overlays are the disk. Secondary address 0 loads to X/Y; X/Y return the end.</summary>
    private string? Load(Cpu6502 cpu, Ram ram)
    {
        var file = disk.Find(_fileName);
        cpu.C = file is null;
        if (file is null)
        {
            cpu.A = FileNotFound;
            return null;
        }
        int at = _secondary == 0 ? cpu.X | cpu.Y << 8 : file.LoadAddress;
        if (at + file.Image.Length > 0x10000)
            return $"LOAD of '{_fileName}' at ${at:X4} goes past $FFFF ({file.Image.Length} bytes)";
        ram.Load((ushort)at, file.Image);
        int end = at + file.Image.Length;
        (cpu.X, cpu.Y) = ((byte)end, (byte)(end >> 8));
        return null;
    }

    /// <summary>
    /// CHROUT in the upper-case character set: letters, digits and punctuation, return, clear, home
    /// and the four cursor moves; other control codes are ignored.
    /// </summary>
    private void Print(byte ch, Ram ram, StringBuilder output)
    {
        var screen = Text(ram, _cursor.Row, _cursor.Column);
        switch (ch)
        {
            case 13: output.Append('\n'); screen.NewLine(); break;
            case 147: screen.Clear(); (screen.Row, screen.Column) = (0, 0); break;
            case 19: (screen.Row, screen.Column) = (0, 0); break;
            case 17: if (screen.Row < Rows - 1) screen.Row++; break;
            case 145: if (screen.Row > 0) screen.Row--; break;
            case 29: if (screen.Column < Columns - 1) screen.Column++; break;
            case 157: if (screen.Column > 0) screen.Column--; break;
            default:
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
                ram.Bytes[ColorRam + screen.Row * Columns + screen.Column] = ram.Bytes[TextColor];
                screen.Put((byte)code);
                break;
        }
        _cursor = (screen.Row, screen.Column);
    }

    /// <summary>A screen code in the upper-case character set; reversed characters as the plain ones.</summary>
    protected override char ScreenChar(byte code)
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
