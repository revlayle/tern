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
    private const int Mli = 0xBF00, MliClose = 0xCC, MliQuit = 0x65;
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
    /// Then: 40-column text, page 1, full window, the ROM's hooks, and the reset vector to <paramref name="quit"/>.
    /// </summary>
    private static List<AsmItem> Apple2Start(IReadOnlyList<AreaMove> moves, Label moved, Label quit)
    {
        var items = new List<AsmItem>();
        if (moves.Count > 0)
        {
            items.Add(new Comment("copy the areas from the file at $2000 to their places"));
            for (int i = 0; i < moves.Count; i++) items.AddRange(Copy(moves[i], $"move{i}_"));
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
        return items;
    }

    /// <summary>
    /// Copies one area: upwards in memory from its last byte down, otherwise from its first byte up,
    /// so that an area may overlap its own place in the file.
    /// </summary>
    private static List<AsmItem> Copy(AreaMove m, string name)
    {
        var items = new List<AsmItem>();
        bool down = m.To > m.From;
        int pages = m.Count >> 8, rest = m.Count & 0xFF;
        // Copying down starts at the last whole page's end, which the rest of the area follows.
        int from = down ? m.From + (pages << 8) : m.From, to = down ? m.To + (pages << 8) : m.To;
        foreach (var (address, value) in new[] { (CopyFrom, from), (CopyTo, to) })
        {
            items.Add(new Instr(Lda, Immediate, new Imm(value & 0xFF)));
            items.Add(new Instr(Sta, ZeroPage, new AddrOperand(address)));
            items.Add(new Instr(Lda, Immediate, new Imm(value >> 8)));
            items.Add(new Instr(Sta, ZeroPage, new AddrOperand(address + 1)));
        }
        Label page = new(name + "page"), part = new(name + "part"), restLoop = new(name + "rest"), done = new(name + "done");
        var copyByte = new AsmItem[]
        {
            new Instr(Lda, IndirectIndexed, new AddrOperand(CopyFrom)),
            new Instr(Sta, IndirectIndexed, new AddrOperand(CopyTo)),
        };
        if (!down)
        {
            items.Add(new Instr(Ldy, Immediate, new Imm(0)));
            items.Add(new Instr(Ldx, Immediate, new Imm(pages)));
            items.Add(new Instr(Beq, Relative, new LabelOperand(part)));
            items.Add(new LabelDef(page));
            items.AddRange(copyByte);
            items.Add(new Instr(Iny));
            items.Add(new Instr(Bne, Relative, new LabelOperand(page)));
            items.Add(new Instr(Inc, ZeroPage, new AddrOperand(CopyFrom + 1)));
            items.Add(new Instr(Inc, ZeroPage, new AddrOperand(CopyTo + 1)));
            items.Add(new Instr(Dex));
            items.Add(new Instr(Bne, Relative, new LabelOperand(page)));
            items.Add(new LabelDef(part));
            items.Add(new Instr(Ldx, Immediate, new Imm(rest)));
            items.Add(new Instr(Beq, Relative, new LabelOperand(done)));
            items.Add(new LabelDef(restLoop));
            items.AddRange(copyByte);
            items.Add(new Instr(Iny));
            items.Add(new Instr(Dex));
            items.Add(new Instr(Bne, Relative, new LabelOperand(restLoop)));
        }
        else
        {
            // The rest above the whole pages first, then each page, every one from its top byte down.
            items.Add(new Instr(Ldy, Immediate, new Imm(rest)));
            items.Add(new Instr(Beq, Relative, new LabelOperand(part)));
            items.Add(new LabelDef(restLoop));
            items.Add(new Instr(Dey));
            items.AddRange(copyByte);
            items.Add(new Instr(Tya));
            items.Add(new Instr(Bne, Relative, new LabelOperand(restLoop)));
            items.Add(new LabelDef(part));
            items.Add(new Instr(Ldx, Immediate, new Imm(pages)));
            items.Add(new Instr(Beq, Relative, new LabelOperand(done)));
            items.Add(new LabelDef(page));
            items.Add(new Instr(Dec, ZeroPage, new AddrOperand(CopyFrom + 1)));
            items.Add(new Instr(Dec, ZeroPage, new AddrOperand(CopyTo + 1)));
            var bytes = new Label(name + "bytes");
            items.Add(new LabelDef(bytes));
            items.Add(new Instr(Dey));
            items.AddRange(copyByte);
            items.Add(new Instr(Tya));
            items.Add(new Instr(Bne, Relative, new LabelOperand(bytes)));
            items.Add(new Instr(Dex));
            items.Add(new Instr(Bne, Relative, new LabelOperand(page)));
        }
        items.Add(new LabelDef(done));
        return items;
    }

    /// <summary>
    /// Where reset goes, after the startup code: close all files (MLI CLOSE with reference 0), make the
    /// power-up byte invalid so that a second reset does not come back here, and quit (MLI QUIT).
    /// </summary>
    private static List<AsmItem> Apple2Quit(Label quit)
    {
        var closeParameters = new Label("close_parameters");
        var quitParameters = new Label("quit_parameters");
        var items = new List<AsmItem> { new Comment("reset: close the files and quit"), new LabelDef(quit) };
        void Call(int command, Label parameters)
        {
            items.Add(new Instr(Jsr, Absolute, new AddrOperand(Mli)));
            items.Add(new DataBytes([(byte)command]));
            items.Add(new AddressByte(parameters, null, 0, false, parameters.Name));
            items.Add(new AddressByte(parameters, null, 0, true, parameters.Name));
        }
        Call(MliClose, closeParameters);
        items.Add(new Instr(Inc, Absolute, new AddrOperand(PwrEdUp)));
        Call(MliQuit, quitParameters);
        items.Add(new LabelDef(closeParameters));
        items.Add(new DataBytes([1, 0]));
        items.Add(new LabelDef(quitParameters));
        items.Add(new DataBytes([4, 0, 0, 0, 0, 0, 0]));
        return items;
    }

    /// <summary>
    /// The system file: the run of bytes that starts at <paramref name="start"/> first, since ProDOS
    /// starts the file at its first byte, then the others by address, with no gaps. Returns the copies
    /// the startup code makes, in an order where none overwrites the startup code that copies (the
    /// file's first <paramref name="copierSize"/> bytes) or bytes of the file not copied yet.
    /// </summary>
    private static (byte[] File, List<AreaMove> Moves) SystemFile(Platform platform, IReadOnlyList<(int Address, byte[] Bytes)> segments,
        int start, int copierSize, SourcePos pos)
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
                throw new CompileError(pos, Free(blocked)
                    ? $"The startup code copies the program's areas from the system file, which ProDOS loads at ${platform.LoadAddress:X4}, to their places, but every order of the copies overwrites bytes of the file before they are copied (the area at {target} first). Start the first program area at ${platform.LoadAddress:X4}, or place the areas further from ${platform.LoadAddress:X4}-${at - 1:X4}"
                    : $"The area at {target} would overwrite the startup code that copies the areas into place, which runs at {copier} in the system file. Start the first program area at ${platform.LoadAddress:X4}, or leave {copier} out of the program areas");
            }
            ordered.Add(pending[i]);
            pending.RemoveAt(i);
        }
        return (file, ordered);
    }
}
