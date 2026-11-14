using Tern.Compiler.Backend;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler;

/// <summary>
/// Cartridge images for the NES (spec 12.2): the startup code behind the reset vector, the interrupt
/// vectors, and the iNES file.
/// </summary>
public static partial class TernCompiler
{
    private const int PpuCtrl = 0x2000, PpuMask = 0x2001, PpuStatus = 0x2002, DmcFreq = 0x4010, ApuFrame = 0x4017;
    private const int NmiVector = 0xFFFA, ResetVector = 0xFFFC, IrqVector = 0xFFFE;
    private const int PrgBank = 0x4000, ChrBank = 0x2000;

    /// <summary>
    /// The first part of the startup code (after <c>sei</c> and <c>cld</c>): APU frame IRQ off, the stack,
    /// PPU NMI and rendering off, DMC IRQ off; then two waits for vblank, since the PPU ignores writes for
    /// about two frames after power-on, with all of RAM ($0000-$07FF) cleared between them. On a board
    /// with banks, bank 0 is selected first (the bank at power-on is undefined), by writing X (0) over
    /// <paramref name="bankZero"/>, a 0 in the fixed bank.
    /// </summary>
    private static List<AsmItem> NesStart(Label? bankZero)
    {
        Label wait1 = new("vblank1"), clear = new("clear_ram"), wait2 = new("vblank2");
        var items = new List<AsmItem>
        {
            new Comment("APU frame IRQ off, the stack, NMI and rendering off, DMC IRQ off"),
            new Instr(Ldx, Immediate, new Imm(0x40)),
            new Instr(Stx, Absolute, new AddrOperand(ApuFrame)),
            new Instr(Ldx, Immediate, new Imm(0xFF)),
            new Instr(Txs),
            new Instr(Inx),
            new Instr(Stx, Absolute, new AddrOperand(PpuCtrl)),
            new Instr(Stx, Absolute, new AddrOperand(PpuMask)),
            new Instr(Stx, Absolute, new AddrOperand(DmcFreq)),
        };
        if (bankZero is not null)
        {
            items.Add(new Comment("bank 0"));
            items.Add(new Instr(Stx, Absolute, new LabelOperand(bankZero)));
        }
        items.AddRange(
        [
            new Comment("wait for two vblanks, clearing RAM between them"),
            new Instr(Bit, Absolute, new AddrOperand(PpuStatus)),
            new LabelDef(wait1),
            new Instr(Bit, Absolute, new AddrOperand(PpuStatus)),
            new Instr(Bpl, Relative, new LabelOperand(wait1)),
            new Instr(Txa),
            new LabelDef(clear),
            new Instr(Sta, ZeroPageX, new AddrOperand(0)),
        ]);
        for (int page = 1; page < 8; page++) items.Add(new Instr(Sta, AbsoluteX, new AddrOperand(page * 0x100)));
        items.AddRange(
        [
            new Instr(Inx),
            new Instr(Bne, Relative, new LabelOperand(clear)),
            new LabelDef(wait2),
            new Instr(Bit, Absolute, new AddrOperand(PpuStatus)),
            new Instr(Bpl, Relative, new LabelOperand(wait2)),
        ]);
        return items;
    }

    /// <summary>
    /// The interrupt vectors a program sets with <c>data</c> (spec 12.2): reset is the compiler's, and NMI
    /// and IRQ must be the address of an <c>interrupt fn</c> or an <c>asm fn</c>, which return with rti.
    /// Returns which of NMI and IRQ the program sets. Data outside ROM is an error: the cartridge cannot
    /// put bytes in RAM.
    /// </summary>
    private static (bool Nmi, bool Irq) CheckVectors(RamPlan plan)
    {
        bool nmi = false, irq = false;
        foreach (var g in plan.FixedImageGlobals)
        {
            var range = new AddressRange(g.Address, g.Address + g.Type.Size - 1);
            if (range.First < 0x8000)
                throw new CompileError(g.Pos,
                    $"'{g.Name}' at {range} is outside the cartridge's ROM ($8000-$FFFF), and the image cannot put bytes in RAM. Make it a 'var' with an initial value and no fixed address, which the startup code copies to RAM, or place it in ROM");
            if (range.Overlaps(new AddressRange(ResetVector, ResetVector + 1)))
                throw new CompileError(g.Pos,
                    $"'{g.Name}' at {range} covers the reset vector ($FFFC), which belongs to the startup code: reset sets up the machine and then calls main. Remove the data, and start the program in main");
            foreach (var (vector, what) in new[] { (NmiVector, "NMI"), (IrqVector, "IRQ") })
            {
                if (!range.Overlaps(new AddressRange(vector, vector + 1))) continue;
                int at = vector - g.Address;
                var fill = g.AddressFills.FirstOrDefault(f => f.Part == AddressPart.Word && LayoutIndex(g, f.Index) == at && LayoutIndex(g, f.Index + 1) == at + 1);
                var f = fill?.Function;
                if (f is null || fill!.Offset != 0 || !(f.IsInterrupt && !f.IsPart || f.Declaration?.IsAsm == true))
                    throw new CompileError(g.Pos,
                        $"The {what} vector (${vector:X4}) must be the address of an interrupt fn or an asm fn, which return with rti" +
                        (f is null || fill!.Offset != 0 ? "" : f.IsPart ? $"; '{f.Name}' is called by another interrupt fn, so it is compiled as a part, which returns with rts"
                            : $"; '{f.Name}' is a plain fn, which returns with rts") +
                        $". {(f is not null && !f.IsPart && fill!.Offset == 0 ? $"Declare it 'interrupt fn {f.Name}'" : "Write addr() of an interrupt fn")}");
                if (vector == NmiVector) nmi = true; else irq = true;
            }
        }
        return (nmi, irq);
    }

    /// <summary>Areas that touch joined into one, in address order: NROM's $8000-$BFFF and $C000-$FFFF as $8000-$FFFF.</summary>
    private static List<AddressRange> Joined(IEnumerable<AddressRange> areas)
    {
        var joined = new List<AddressRange>();
        foreach (var a in areas.OrderBy(a => a.First))
            if (joined.Count > 0 && joined[^1].Last + 1 == a.First) joined[^1] = joined[^1] with { Last = a.Last };
            else joined.Add(a);
        return joined;
    }

    /// <summary>The vectors the compiler sets: reset to the startup code, and NMI and IRQ, unless the program sets them, to an rti.</summary>
    private static IEnumerable<AsmItem> NesVectors(Label start, Label rti, (bool Nmi, bool Irq) set)
    {
        foreach (var (vector, name, target, own) in new[] { (NmiVector, "nmi vector", rti, set.Nmi), (ResetVector, "reset vector", start, false), (IrqVector, "irq vector", rti, set.Irq) })
        {
            if (own) continue;
            yield return new SectionStart(name, vector);
            yield return new AddressByte(target, null, 0, false, target.Name);
            yield return new AddressByte(target, null, 0, true, target.Name);
        }
    }

    /// <summary>
    /// The iNES 1.0 file (spec 12.2): the header (mapper, PRG ROM in 16 KB units, CHR ROM in 8 KB units,
    /// mirroring), PRG ROM, then CHR ROM. NROM is 16 KB (at $C000, mirrored at $8000) when nothing lies
    /// below $C000, else 32 KB. Bytes no code or data covers are $FF. CHR ROM holds the data of the chr
    /// area at its PPU addresses, zeros elsewhere.
    /// </summary>
    /// <param name="banks">Each bank's unit ("levels[1]") and number; <paramref name="placed"/> has their bytes.</param>
    private static (byte[] File, int PrgSize, int PrgFirst, int Banks) NesFile(Cartridge cartridge, IReadOnlyList<(int Address, byte[] Bytes)> segments, RamPlan plan,
        IReadOnlyDictionary<string, int> banks, IReadOnlyDictionary<string, List<(int Address, byte[] Bytes)>> placed)
    {
        var rom = segments.Where(s => s.Address >= 0x8000).ToList();
        byte[] prg;
        int prgFirst, count = 0;
        if (cartridge.Board.BankArea is { } window)
        {
            // UxROM: the banks by number, then the fixed bank; as many as the highest number needs, a power of two (spec 12.2).
            count = 1;
            while (count < (banks.Count == 0 ? 1 : banks.Values.Max() + 2)) count *= 2;
            prg = new byte[count * PrgBank];
            Array.Fill(prg, (byte)0xFF);
            prgFirst = 0xC000;
            foreach (var (address, bytes) in rom) bytes.CopyTo(prg, (count - 1) * PrgBank + address - 0xC000);
            foreach (var (unit, n) in banks)
                if (placed.TryGetValue(unit, out var bankSegments))
                    foreach (var (address, bytes) in bankSegments) bytes.CopyTo(prg, n * PrgBank + address - window.First);
        }
        else
        {
            prgFirst = rom.Count > 0 && rom.Min(s => s.Address) < 0xC000 ? 0x8000 : 0xC000;
            prg = new byte[0x10000 - prgFirst];
            Array.Fill(prg, (byte)0xFF);
            foreach (var (address, bytes) in rom) bytes.CopyTo(prg, address - prgFirst);
        }
        var chr = cartridge.Board.ChrRom ? new byte[ChrBank] : [];
        foreach (var g in plan.ChrGlobals) ByteLayout(g, g.InitialBytes ?? new byte[g.Type.Size]).CopyTo(chr, g.Address);
        var board = cartridge.Board;
        var file = new List<byte>
        {
            (byte)'N', (byte)'E', (byte)'S', 0x1A,
            (byte)(prg.Length / PrgBank), (byte)(chr.Length / ChrBank),
            (byte)((board.Mapper & 0x0F) << 4 | (cartridge.VerticalMirroring ? 1 : 0)),
            (byte)(board.Mapper & 0xF0),
            0, 0, 0, 0, 0, 0, 0, 0,
        };
        file.AddRange(prg);
        file.AddRange(chr);
        return (file.ToArray(), prg.Length, prgFirst, count);
    }

    /// <summary>
    /// A label's place for Mesen (<see cref="LabelFormat.Mesen"/>): RAM as itself, ROM as its offset into
    /// PRG ROM, registers left out. With <paramref name="bankCount"/> banks, the fixed bank is the last,
    /// and a label of a module in <paramref name="moduleBanks"/> is in that module's bank.
    /// </summary>
    private static Func<string, int, int> NesLabelMap(int prgFirst, int bankCount, IReadOnlyDictionary<string, int> moduleBanks) => (name, address) =>
    {
        if (address < 0x2000) return address & 0x07FF;
        if (address < 0x8000) return -1;
        if (bankCount == 0) return address >= prgFirst ? LabelFormat.PrgRom | (address - prgFirst) : -1;
        if (address >= 0xC000) return LabelFormat.PrgRom | ((bankCount - 1) * PrgBank + address - 0xC000);
        int dot = name.IndexOf('.');
        return dot > 0 && moduleBanks.TryGetValue(name[..dot], out int bank) ? LabelFormat.PrgRom | (bank * PrgBank + address - 0x8000) : -1;
    };

    /// <summary>
    /// Copies <paramref name="size"/> bytes from <paramref name="from"/> to <paramref name="to"/>, 256 at a time,
    /// as <see cref="ClearBlock"/> clears: the startup code's copy of the initial values from ROM to RAM.
    /// </summary>
    private static void CopyBlock(List<AsmItem> items, int size, Func<int, Operand> from, Func<int, Operand> to, string name)
    {
        for (int p = 0; p < size / 256; p++)
        {
            var loop = new Label($"{name}{p}");
            items.Add(new Instr(Ldx, Immediate, new Imm(0)));
            items.Add(new LabelDef(loop));
            items.Add(new Instr(Lda, AbsoluteX, from(p * 256)));
            items.Add(new Instr(Sta, AbsoluteX, to(p * 256)));
            items.Add(new Instr(Inx));
            items.Add(new Instr(Bne, Relative, new LabelOperand(loop)));
        }
        int rest = size % 256;
        if (rest > 0)
        {
            var loop = new Label(name);
            items.Add(new Instr(Ldx, Immediate, new Imm(rest)));
            items.Add(new LabelDef(loop));
            items.Add(new Instr(Lda, AbsoluteX, from(size / 256 * 256 - 1)));
            items.Add(new Instr(Sta, AbsoluteX, to(size / 256 * 256 - 1)));
            items.Add(new Instr(Dex));
            items.Add(new Instr(Bne, Relative, new LabelOperand(loop)));
        }
    }

    /// <summary>The initial values of the variables the startup code copies to RAM: their bytes in the block's order, gaps as zeros.</summary>
    private static List<AsmItem> InitialValues(RamPlan plan, Label initial, Func<FunctionSymbol, Label> labelOf)
    {
        var items = new List<AsmItem> { new SectionStart("initial values"), new LabelDef(initial) };
        int at = plan.CopiedStart;
        foreach (var (g, offset) in plan.CopiedGlobals.OrderBy(e => e.Offset))
        {
            if (offset > at) items.Add(new DataBytes(new byte[offset - at]));
            items.AddRange(ImageBytes(g, labelOf));
            at = offset + g.Type.Size;
        }
        return items;
    }
}
