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
/// SETKBD) set the bytes they set. The MLI ($BF00) has stand-ins for CLOSE, which succeeds, and QUIT,
/// which ends the run.
/// </summary>
internal sealed class Apple2Machine : Machine
{
    public const int ScreenAddress = 0x0400, Columns = 40, Rows = 24;
    private const int Cout = 0xFDED, Home = 0xFC58, SetNorm = 0xFE84, Init = 0xFB2F, SetVid = 0xFE93, SetKbd = 0xFE89, Mli = 0xBF00;
    private const int Wndlft = 0x20, Wndwdth = 0x21, Wndtop = 0x22, Wndbtm = 0x23, Ch = 0x24, Cv = 0x25, Basl = 0x28, Invflg = 0x32, Csw = 0x36, Ksw = 0x38;
    private const int Blank = 0xA0;

    public override string StandIns => "COUT $FDED, HOME $FC58, SETNORM $FE84, INIT $FB2F, SETVID $FE93, SETKBD $FE89, and the MLI at $BF00 for CLOSE and QUIT";

    public override void Start(Ram ram)
    {
        for (int row = 0; row < Rows; row++) ram.Bytes.AsSpan(Row(row), Columns).Fill(Blank);
        SetWindow(ram);
        ram.Bytes[Invflg] = 0xFF;
        Cursor(ram, 0, 0);
        (ram.Bytes[Csw], ram.Bytes[Csw + 1]) = (0xF0, 0xFD);
        (ram.Bytes[Ksw], ram.Bytes[Ksw + 1]) = (0x1B, 0xFD);
    }

    public override bool Has(int pc) => pc is Cout or Home or SetNorm or Init or SetVid or SetKbd or Mli;

    public override (int Address, int Columns, int Rows) Screen(Ram ram) => (ScreenAddress, Columns, Rows);

    public override int RowAddress(Ram ram, int row) => Row(row);

    /// <summary>The address of text page 1's row <paramref name="row"/>.</summary>
    public static int Row(int row) => ScreenAddress + 128 * (row % 8) + 40 * (row / 8);

    public override string? Call(int pc, Cpu6502 cpu, Ram ram, StringBuilder output)
    {
        switch (pc)
        {
            case Cout: Print(cpu.A); break;
            case Home:
                for (int row = ram.Bytes[Wndtop]; row < Math.Min((int)ram.Bytes[Wndbtm], Rows); row++) ram.Bytes.AsSpan(Row(row), Columns).Fill(Blank);
                Cursor(ram, ram.Bytes[Wndtop], 0);
                break;
            case SetNorm:
                ram.Bytes[Invflg] = 0xFF;
                cpu.Y = 0xFF;
                break;
            case Init:
                SetWindow(ram);
                Cursor(ram, Rows - 1, 0);
                break;
            case SetVid: (ram.Bytes[Csw], ram.Bytes[Csw + 1]) = (0xF0, 0xFD); break;
            case SetKbd: (ram.Bytes[Ksw], ram.Bytes[Ksw + 1]) = (0x1B, 0xFD); break;
            default:
            {
                // jsr $BF00 is followed by the command byte and the parameters' address; the MLI returns after them.
                int sp = 0x100 + (byte)(cpu.SP + 1);
                int at = (ram.Bytes[sp] | ram.Bytes[sp + 1] << 8) + 1;
                int command = ram.Bytes[at & 0xFFFF];
                if (command == 0x65) return "the program quit (MLI QUIT)";
                if (command != 0xCC)
                    return $"MLI call ${command:X2}, which the emulator does not have (stand-ins: CLOSE $CC, QUIT $65)";
                int back = at + 2;
                (ram.Bytes[sp], ram.Bytes[sp + 1]) = ((byte)back, (byte)(back >> 8));
                cpu.A = 0;
                cpu.C = false;
                cpu.Z = true;
                cpu.N = false;
                break;
            }
        }
        return null;

        // COUT1: the character at the cursor, through INVFLG; return, line feed and backspace.
        void Print(byte code)
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
