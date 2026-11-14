using Tern.Compiler.Backend;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler;

/// <summary>
/// The Apple II's system file and startup code (spec 12.2): ProDOS loads the file at $2000 and starts
/// it there. The file holds the program's areas one after another, the area with the startup code
/// first; the startup code copies each area that belongs elsewhere to its place, sets up the text
/// screen and the reset vector, then continues as on the other targets.
/// </summary>
public static partial class TernCompiler
{
    private const int Cout = 0xFDED, Home = 0xFC58, SetNorm = 0xFE84, Init = 0xFB2F, SetVid = 0xFE93, SetKbd = 0xFE89;
    private const int Mli = 0xBF00, MliClose = 0xCC, MliQuit = 0x65, MliGetPrefix = 0xC7, MliSetPrefix = 0xC6;
    /// <summary>The path ProDOS started the program with (a length, then the characters), and the input buffer, where the prefix is read.</summary>
    private const int StartPath = 0x0280, InputBuffer = 0x0200;
    /// <summary>The output hook's high byte (CSWH): $C3 while the 80-column firmware prints.</summary>
    private const int Cswh = 0x37;
    /// <summary>The reset vector (SOFTEV) and its power-up byte (PWREDUP), which must be the vector's high byte EOR $A5.</summary>
    private const int SoftEv = 0x03F2, PwrEdUp = 0x03F4;
    /// <summary>Two pointers in free zero page, used only while the areas are copied, before any variable is set.</summary>
    private const int CopyFrom = 0x50, CopyTo = 0x52;

    /// <summary>One copy the startup code makes: <paramref name="Count"/> bytes from the file at <paramref name="From"/> to the area at <paramref name="To"/>.</summary>
    internal readonly record struct AreaMove(int From, int To, int Count);

    /// <summary>
    /// The startup code's first part on the Apple II. The copies run from the file at $2000, before
    /// the code is in its place, so they use only relative branches, then jump to <paramref name="moved"/>.
    /// Then: 40-column text, page 1, full window, the ROM's hooks, the reset vector to <paramref name="quit"/>,
    /// and, when the program imports io (<paramref name="prefix"/> not null), the prefix (<see cref="Prefix"/>).
    /// With <paramref name="exact"/>, each copy has only the loops its count needs (see <see cref="Copy"/>).
    /// </summary>
    private static List<AsmItem> Apple2Start(IReadOnlyList<AreaMove> moves, Label moved, Label quit, Label? prefix, bool exact)
    {
        var items = new List<AsmItem>();
        if (moves.Count > 0)
        {
            items.Add(new Comment("copy the areas from the file at $2000 to their places"));
            for (int i = 0; i < moves.Count; i++) items.AddRange(Copy(moves[i], $"move{i}_", exact));
            items.Add(new Instr(Jmp, Absolute, new LabelOperand(moved)));
            items.Add(new LabelDef(moved));
        }
        // The 80-column firmware, when it prints, is turned off with Ctrl-U.
        var forty = new Label("text40");
        items.Add(new Comment("40-column text, page 1, full window, the ROM's hooks"));
        items.Add(new Instr(Lda, ZeroPage, new AddrOperand(Cswh)));
        items.Add(new Instr(Cmp, Immediate, new Imm(0xC3)));
        items.Add(new Instr(Bne, Relative, new LabelOperand(forty)));
        items.Add(new Instr(Lda, Immediate, new Imm(0x95)));
        items.Add(new Instr(Jsr, Absolute, new AddrOperand(Cout)));
        items.Add(new LabelDef(forty));
        foreach (int routine in new[] { SetNorm, Init, SetVid, SetKbd, Home })
            items.Add(new Instr(Jsr, Absolute, new AddrOperand(routine)));
        items.Add(new Comment("reset closes the files and quits"));
        items.Add(new Instr(Lda, Immediate, new ImmLabelByte(quit, false)));
        items.Add(new Instr(Sta, Absolute, new AddrOperand(SoftEv)));
        items.Add(new Instr(Lda, Immediate, new ImmLabelByte(quit, true)));
        items.Add(new Instr(Sta, Absolute, new AddrOperand(SoftEv + 1)));
        items.Add(new Instr(Eor, Immediate, new Imm(0xA5)));
        items.Add(new Instr(Sta, Absolute, new AddrOperand(PwrEdUp)));
        if (prefix is not null) items.AddRange(Prefix(prefix));
        return items;
    }

    /// <summary>
    /// When ProDOS started the program with an empty prefix, the prefix becomes the folder of the path
    /// at $0280 (up to its last '/'), so that io's partial names find files next to the program (spec
    /// 12.2). Only in a program that imports io, which counts its bytes (<see cref="PrefixSize"/>;
    /// question 150). The prefix is read into the input buffer at $0200; a partial path at $0280 leaves it empty.
    /// Bit 7 of the path's characters is cleared: Bitsy Bye leaves it set (AppleWin, M26 follow-up).
    /// <paramref name="parameters"/> is the list for GET_PREFIX and SET_PREFIX (<see cref="Apple2Quit"/>).
    /// </summary>
    private static List<AsmItem> Prefix(Label parameters)
    {
        Label find = new("prefix_find"), found = new("prefix_found"), copy = new("prefix_copy"), done = new("prefix_done");
        var items = new List<AsmItem> { new Comment("an empty prefix becomes the program's folder") };
        items.AddRange(MliCall(MliGetPrefix, parameters));
        items.Add(new Instr(Lda, Absolute, new AddrOperand(InputBuffer)));
        items.Add(new Instr(Bne, Relative, new LabelOperand(done)));
        items.Add(new Instr(Ldx, Absolute, new AddrOperand(StartPath)));
        items.Add(new Instr(Beq, Relative, new LabelOperand(done)));
        items.Add(new LabelDef(find));
        items.Add(new Instr(Lda, AbsoluteX, new AddrOperand(StartPath)));
        items.Add(new Instr(And, Immediate, new Imm(0x7F)));
        items.Add(new Instr(Cmp, Immediate, new Imm('/')));
        items.Add(new Instr(Beq, Relative, new LabelOperand(found)));
        items.Add(new Instr(Dex));
        items.Add(new Instr(Bne, Relative, new LabelOperand(find)));
        items.Add(new Instr(Beq, Relative, new LabelOperand(done)));
        items.Add(new LabelDef(found));
        items.Add(new Instr(Stx, Absolute, new AddrOperand(InputBuffer)));
        items.Add(new LabelDef(copy));
        items.Add(new Instr(Lda, AbsoluteX, new AddrOperand(StartPath)));
        items.Add(new Instr(And, Immediate, new Imm(0x7F)));
        items.Add(new Instr(Sta, AbsoluteX, new AddrOperand(InputBuffer)));
        items.Add(new Instr(Dex));
        items.Add(new Instr(Bne, Relative, new LabelOperand(copy)));
        items.AddRange(MliCall(MliSetPrefix, parameters));
        items.Add(new LabelDef(done));
        return items;
    }

    /// <summary>The bytes of the prefix code and its parameter list, which the report counts under io.</summary>
    private static int PrefixSize => Prefix(new Label("size")).Sum(item => item switch
    {
        Instr i => 1 + Tern.Cpu.InstructionSet.OperandSize(i.Mode),
        DataBytes d => d.Bytes.Length,
        AddressByte => 1,
        _ => 0,
    }) + PrefixParameters.Length;

    /// <summary>GET_PREFIX's and SET_PREFIX's parameter list: one parameter, the input buffer.</summary>
    private static readonly byte[] PrefixParameters = [1, InputBuffer & 0xFF, InputBuffer >> 8];

    /// <summary><c>jsr $BF00</c>, then the command and the parameter list's address, where the MLI returns past them.</summary>
    private static IEnumerable<AsmItem> MliCall(int command, Label parameters) =>
    [
        new Instr(Jsr, Absolute, new AddrOperand(Mli)),
        new DataBytes([(byte)command]),
        new AddressByte(parameters, null, 0, false, parameters.Name),
        new AddressByte(parameters, null, 0, true, parameters.Name),
    ];

    /// <summary>
    /// Copies one area: upwards in memory from its last byte down, otherwise from its first byte up,
    /// so that an area may overlap its own place in the file. With <paramref name="exact"/>, only the
    /// loops the count needs: one over the whole pages, one over the rest. Otherwise both, each skipped
    /// at run time when its count is 0, so that the code's size does not depend on the count; the
    /// startup code uses that form when the copies do not settle in a few passes (the copying code's
    /// size changes the counts).
    /// </summary>
    private static List<AsmItem> Copy(AreaMove m, string name, bool exact)
    {
        var items = new List<AsmItem>();
        bool down = m.To > m.From;
        int pages = m.Count >> 8, rest = m.Count & 0xFF;
        bool pageLoop = !exact || pages > 0, restLoop = !exact || rest > 0;
        // Copying down starts at the last whole page's end, which the rest of the area follows.
        int from = down ? m.From + (pages << 8) : m.From, to = down ? m.To + (pages << 8) : m.To;
        foreach (var (address, value) in new[] { (CopyFrom, from), (CopyTo, to) })
        {
            items.Add(new Instr(Lda, Immediate, new Imm(value & 0xFF)));
            items.Add(new Instr(Sta, ZeroPage, new AddrOperand(address)));
            items.Add(new Instr(Lda, Immediate, new Imm(value >> 8)));
            items.Add(new Instr(Sta, ZeroPage, new AddrOperand(address + 1)));
        }
        Label page = new(name + "page"), bytes = new(name + "bytes"), part = new(name + "part"), restBytes = new(name + "rest"), done = new(name + "done");
        var copyByte = new AsmItem[]
        {
            new Instr(Lda, IndirectIndexed, new AddrOperand(CopyFrom)),
            new Instr(Sta, IndirectIndexed, new AddrOperand(CopyTo)),
        };
        // A count in X; when the count is not known to be above 0, a branch past the loop.
        void Count(Tern.Cpu.Mnemonic load, int value, Label skip)
        {
            items.Add(new Instr(load, Immediate, new Imm(value)));
            if (!exact) items.Add(new Instr(Beq, Relative, new LabelOperand(skip)));
        }
        if (!down)
        {
            items.Add(new Instr(Ldy, Immediate, new Imm(0)));
            if (pageLoop)
            {
                Count(Ldx, pages, part);
                items.Add(new LabelDef(page));
                items.AddRange(copyByte);
                items.Add(new Instr(Iny));
                items.Add(new Instr(Bne, Relative, new LabelOperand(page)));
                items.Add(new Instr(Inc, ZeroPage, new AddrOperand(CopyFrom + 1)));
                items.Add(new Instr(Inc, ZeroPage, new AddrOperand(CopyTo + 1)));
                items.Add(new Instr(Dex));
                items.Add(new Instr(Bne, Relative, new LabelOperand(page)));
                items.Add(new LabelDef(part));
            }
            if (restLoop)
            {
                Count(Ldx, rest, done);
                items.Add(new LabelDef(restBytes));
                items.AddRange(copyByte);
                items.Add(new Instr(Iny));
                items.Add(new Instr(Dex));
                items.Add(new Instr(Bne, Relative, new LabelOperand(restBytes)));
            }
        }
        else
        {
            // The rest above the whole pages first, then each page, every one from its top byte down.
            // Each loop ends with Y = 0, where the page loop starts.
            if (restLoop)
            {
                Count(Ldy, rest, part);
                items.Add(new LabelDef(restBytes));
                items.Add(new Instr(Dey));
                items.AddRange(copyByte);
                items.Add(new Instr(Tya));
                items.Add(new Instr(Bne, Relative, new LabelOperand(restBytes)));
                items.Add(new LabelDef(part));
            }
            else items.Add(new Instr(Ldy, Immediate, new Imm(0)));
            if (pageLoop)
            {
                Count(Ldx, pages, done);
                items.Add(new LabelDef(page));
                items.Add(new Instr(Dec, ZeroPage, new AddrOperand(CopyFrom + 1)));
                items.Add(new Instr(Dec, ZeroPage, new AddrOperand(CopyTo + 1)));
                items.Add(new LabelDef(bytes));
                items.Add(new Instr(Dey));
                items.AddRange(copyByte);
                items.Add(new Instr(Tya));
                items.Add(new Instr(Bne, Relative, new LabelOperand(bytes)));
                items.Add(new Instr(Dex));
                items.Add(new Instr(Bne, Relative, new LabelOperand(page)));
            }
        }
        items.Add(new LabelDef(done));
        return items;
    }

    /// <summary>
    /// Where reset goes, after the startup code: close all files (MLI CLOSE with reference 0), make the
    /// power-up byte invalid so that a second reset does not come back here, and quit (MLI QUIT). The
    /// parameter lists follow, with the prefix's when the program imports io (<paramref name="prefix"/>: the input buffer).
    /// </summary>
    private static List<AsmItem> Apple2Quit(Label quit, Label? prefix)
    {
        var closeParameters = new Label("close_parameters");
        var quitParameters = new Label("quit_parameters");
        var items = new List<AsmItem> { new Comment("reset: close the files and quit"), new LabelDef(quit) };
        items.AddRange(MliCall(MliClose, closeParameters));
        items.Add(new Instr(Inc, Absolute, new AddrOperand(PwrEdUp)));
        items.AddRange(MliCall(MliQuit, quitParameters));
        items.Add(new LabelDef(closeParameters));
        items.Add(new DataBytes([1, 0]));
        items.Add(new LabelDef(quitParameters));
        items.Add(new DataBytes([4, 0, 0, 0, 0, 0, 0]));
        if (prefix is null) return items;
        items.Add(new LabelDef(prefix));
        items.Add(new DataBytes(PrefixParameters));
        return items;
    }

    /// <summary>
    /// The system file: the run of bytes that starts at <paramref name="start"/> first, since ProDOS
    /// starts the file at its first byte, then the others by address, with no gaps. Returns the copies
    /// the startup code makes, in an order where none overwrites the startup code that copies (the
    /// file's first <paramref name="copierSize"/> bytes) or bytes of the file not copied yet. Copies that
    /// cannot be made are reported at <paramref name="layoutPos"/> (the memory block that placed the
    /// areas), a file that is too large at <paramref name="pos"/>.
    /// </summary>
    private static (byte[] File, List<AreaMove> Moves) SystemFile(Platform platform, IReadOnlyList<(int Address, byte[] Bytes)> segments,
        int start, int copierSize, SourcePos pos, SourcePos layoutPos)
    {
        var runs = new List<(int Address, byte[] Bytes)>();
        foreach (var (address, bytes) in segments)
        {
            if (start > address && start < address + bytes.Length)
            {
                runs.Add((address, bytes[..(start - address)]));
                runs.Add((start, bytes[(start - address)..]));
            }
            else runs.Add((address, bytes));
        }
        var order = runs.Where(r => r.Address == start).Concat(runs.Where(r => r.Address != start).OrderBy(r => r.Address)).ToList();
        int at = platform.LoadAddress;
        var copies = new List<AreaMove>();
        foreach (var (address, bytes) in order)
        {
            if (at != address) copies.Add(new AreaMove(at, address, bytes.Length));
            at += bytes.Length;
        }
        if (at - 1 > platform.RamEnd)
            throw new CompileError(pos,
                $"The system file is {at - platform.LoadAddress} bytes, but ProDOS loads it at ${platform.LoadAddress:X4} and it may reach only ${platform.RamEnd:X4} " +
                $"({platform.RamEnd + 1 - platform.LoadAddress} bytes). Make the program smaller, or move data out of it");
        var file = order.SelectMany(r => r.Bytes).ToArray();

        static AddressRange Bytes(int first, int count) => new(first, first + count - 1);
        var copier = copierSize > 0 ? Bytes(platform.LoadAddress, copierSize) : (AddressRange?)null;
        var pending = copies.ToList();
        var ordered = new List<AreaMove>();
        while (pending.Count > 0)
        {
            bool Free(AreaMove m)
            {
                var to = Bytes(m.To, m.Count);
                return copier is not { } c || !to.Overlaps(c);
            }
            bool Clear(AreaMove m) => pending.All(o => o == m || !Bytes(m.To, m.Count).Overlaps(Bytes(o.From, o.Count)));
            int i = pending.FindIndex(m => Free(m) && Clear(m));
            if (i < 0)
            {
                var blocked = pending[0];
                var target = Bytes(blocked.To, blocked.Count);
                throw new CompileError(layoutPos, Free(blocked)
                    ? $"The startup code copies the program's areas from the system file, which ProDOS loads at ${platform.LoadAddress:X4}, to their places, but every order of the copies overwrites bytes of the file before they are copied (the area at {target} first). Start the first program area at ${platform.LoadAddress:X4}, or place the areas further from ${platform.LoadAddress:X4}-${at - 1:X4}"
                    : $"The area at {target} would overwrite the startup code that copies the areas into place, which runs at {copier} in the system file. Start the first program area at ${platform.LoadAddress:X4}, or leave {copier} out of the program areas");
            }
            ordered.Add(pending[i]);
            pending.RemoveAt(i);
        }
        return (file, ordered);
    }
}
