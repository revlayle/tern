using System.Text;
using Tern.Cpu;

namespace Tern.Compiler.Tools;

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
internal sealed class AtariMachine(OverlayDisk disk) : Machine
{
    public const int ScreenAddress = 0xBC40, Columns = 40, Rows = 24;
    private const int Savmsc = 0x58, Lmargn = 0x52, Rmargn = 0x53, Rowcrs = 0x54, Colcrs = 0x55;
    private const int Iccom = 0x0342, Icsta = 0x0343, Icbal = 0x0344, Icbll = 0x0348, Icax1 = 0x034A;
    private const int Ok = 1, Eof = 0x88, NotFound = 0xAA, AlreadyOpen = 0x81, NotOpen = 0x85, BadName = 0xA5;
    /// <summary>CIO's commands (ICCOM) that have stand-ins.</summary>
    private const int Open = 0x03, GetBytes = 0x07, PutBytes = 0x0B, Close = 0x0C;
    /// <summary>The open mode (ICAX1) for reading.</summary>
    private const int ForReading = 4;
    /// <summary>The immediate and deferred VBI vectors (VVBLKI, VVBLKD), which SETVBV sets for A = 6 and A = 7.</summary>
    private const int Vvblki = 0x0222, Vvblkd = 0x0224;
    /// <summary>ATASCII's end of line, and the screen editor's control codes that have stand-ins.</summary>
    private const byte EndOfLine = 0x9B, ClearScreen = 0x7D, CursorUp = 0x1C, CursorDown = 0x1D, CursorLeft = 0x1E, CursorRight = 0x1F, Backspace = 0x7E;

    public override string Help => $"On the Atari: {StandIns}.";

    public override string ScreenHelp => "SAVMSC on the Atari";

    public override string UnmodelledChips => "ANTIC, GTIA, POKEY or PIA";

    public override string IdleInput => "the Atari's CH ($2FC) is $FF, STICK0-3 $0F, STRIG0-3 1 and CONSOL 7";

    protected override IReadOnlyList<StandIn> Routines =>
    [
        new(0xE456, "CIOV", Ciov, "for put bytes on IOCB 0 (the screen editor) and for reading the build's overlay files on IOCBs 1-7"),
        new(0xE45C, "SETVBV", (cpu, ram, _) =>
        {
            if (cpu.A is not (6 or 7)) return $"SETVBV with A = {cpu.A}; the stand-in sets the VBI vectors only (A = 6 or 7)";
            int vector = cpu.A == 6 ? Vvblki : Vvblkd;
            ram.Bytes[vector] = cpu.Y;
            ram.Bytes[vector + 1] = cpu.X;
            return null;
        }),
    ];

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
        // Idle input (decision 2026-11-21, question 175): no key (CH), sticks centred (STICK0-3),
        // triggers up (STRIG0-3), no console key (CONSOL).
        ram.Bytes[0x02FC] = 0xFF;
        ram.Bytes.AsSpan(0x0278, 4).Fill(0x0F);
        ram.Bytes.AsSpan(0x0284, 4).Fill(1);
        ram.Bytes[0xD01F] = 7;
    }

    public override (int Address, int Columns, int Rows) Screen(Ram ram) => (ram.Bytes[Savmsc] | ram.Bytes[Savmsc + 1] << 8, Columns, Rows);

    /// <summary>CIOV: put bytes on IOCB 0; open, get bytes and close the build's overlay files on IOCBs 1-7.</summary>
    private string? Ciov(Cpu6502 cpu, Ram ram, StringBuilder output)
    {
        int x = cpu.X, command = ram.Bytes[Iccom + x];
        int buffer = ram.Bytes[Icbal + x] | ram.Bytes[Icbal + x + 1] << 8;
        int length = ram.Bytes[Icbll + x] | ram.Bytes[Icbll + x + 1] << 8;
        int status = Ok;
        if (x == 0 && command == PutBytes)
        {
            if (length == 0) Put(cpu.A, ram, output);
            else
                for (int i = 0; i < length; i++) Put(ram.Bytes[(buffer + i) & 0xFFFF], ram, output);
        }
        else if (x is > 0 and < 0x80 && (x & 0x0F) == 0 && command is Open or GetBytes or Close)
        {
            if (File() is { } why) return why;
        }
        else
            return $"CIO command ${command:X2} on IOCB {x >> 4}, which the emulator does not have (stand-ins: put bytes, ${PutBytes:X2}, on IOCB 0, the screen editor; open for reading, get bytes and close on IOCBs 1-7, for the build's overlay files)";
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
                case Open:
                {
                    int mode = ram.Bytes[Icax1 + x];
                    if (mode != ForReading)
                        return $"CIO open with mode {mode} on IOCB {x >> 4}; the emulator only reads the build's overlay files (mode {ForReading})";
                    if (disk.IsOpen(x)) { status = AlreadyOpen; break; }
                    var name = new StringBuilder();
                    for (int i = 0; i < 128 && ram.Bytes[(buffer + i) & 0xFFFF] != EndOfLine; i++) name.Append((char)ram.Bytes[(buffer + i) & 0xFFFF]);
                    var text = name.ToString();
                    string fileName = text[(text.IndexOf(':') + 1)..];
                    bool dosName = text.Contains(':') && System.Text.RegularExpressions.Regex.IsMatch(fileName, @"^[A-Z][A-Z0-9]{0,7}(\.[A-Z0-9]{0,3})?$");
                    var file = dosName ? disk.Find(fileName) : null;
                    disk.Open(x, file?.File ?? []);     // as in CIO, the IOCB stays taken after a failed open
                    if (file is null) status = text.Contains(':') && !dosName ? BadName : NotFound;
                    break;
                }
                case GetBytes:
                {
                    // Length 0 reads one byte, into A.
                    if (disk.Read(x, Math.Max(length, 1)) is not { } bytes) { status = NotOpen; break; }
                    if (length == 0)
                    {
                        if (bytes.Length == 0) status = Eof;
                        else cpu.A = bytes[0];
                        break;
                    }
                    for (int i = 0; i < bytes.Length; i++) ram.Bytes[(buffer + i) & 0xFFFF] = bytes[i];
                    ram.Bytes[Icbll + x] = (byte)bytes.Length;
                    ram.Bytes[Icbll + x + 1] = (byte)(bytes.Length >> 8);
                    if (bytes.Length < length) status = Eof;
                    break;
                }
                case Close:
                    disk.Close(x);
                    break;
            }
            return null;
        }
    }

    /// <summary>
    /// The screen editor: the end-of-line code, clear, the four cursor moves and backspace; other control
    /// codes are ignored. Lines go from the left to the right margin and scroll at the bottom.
    /// </summary>
    private void Put(byte ch, Ram ram, StringBuilder output)
    {
        var (address, _, _) = Screen(ram);
        int left = ram.Bytes[Lmargn], right = Math.Min((int)ram.Bytes[Rmargn], Columns - 1);
        var screen = new TextScreen(ram, r => address + r * Columns, Columns, Rows, 0)
        {
            Left = left,
            Right = right,
            Row = Math.Min((int)ram.Bytes[Rowcrs], Rows - 1),
            Column = Math.Min((int)ram.Bytes[Colcrs], right),
        };
        switch (ch)
        {
            case EndOfLine: output.Append('\n'); screen.NewLine(); break;
            case ClearScreen: screen.Clear(); (screen.Row, screen.Column) = (0, left); break;
            // Up and down wrap from the top row to the bottom one and back, as in the screen editor.
            case CursorUp: screen.Row = screen.Row > 0 ? screen.Row - 1 : Rows - 1; break;
            case CursorDown: screen.Row = screen.Row < Rows - 1 ? screen.Row + 1 : 0; break;
            case CursorLeft: if (screen.Column > left) screen.Column--; break;
            case CursorRight: if (screen.Column < right) screen.Column++; break;
            case Backspace:
                if (screen.Column > left) screen.Column--;
                ram.Bytes[(screen.RowAddress(screen.Row) + screen.Column) & 0xFFFF] = 0;
                break;
            case 0x7F or (>= 0x9C and <= 0x9F) or (>= 0xFD): break;
            default:
                int a = ch & 0x7F;
                int code = (a < 0x20 ? a + 0x40 : a < 0x60 ? a - 0x20 : a) | (ch & 0x80);
                output.Append(ScreenChar((byte)code));
                screen.Put((byte)code);
                break;
        }
        ram.Bytes[Rowcrs] = (byte)screen.Row;
        ram.Bytes[Colcrs] = (byte)screen.Column;
        ram.Bytes[Colcrs + 1] = 0;
    }

    /// <summary>An internal screen code; inverse characters as the plain ones.</summary>
    protected override char ScreenChar(byte code)
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
