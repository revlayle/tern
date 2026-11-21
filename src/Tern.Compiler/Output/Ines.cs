using Tern.Compiler.Backend;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler;

/// <summary>An iNES 1.0 cartridge image (<see cref="OutputFormat.Ines"/>).</summary>
internal sealed class InesFormat : OutputFormat
{
    public override string Extension => ".nes";

    /// <summary>A cartridge's file is made by its build (<see cref="InesBuild.File"/>); this one is never asked for, since the NES has no overlays or binaries.</summary>
    internal override byte[] FileOf(int first, byte[] image, IReadOnlyList<(int Address, byte[] Bytes)> segments, int? run) =>
        throw new InvalidOperationException("An iNES image is made by its build, from the cartridge's board");

    internal override OutputBuild Begin(OutputBuild.Inputs inputs) => new InesBuild(inputs);
}

/// <summary>
/// A NES cartridge (spec 12.2): code and data in the board's ROM, variables in RAM, initial values
/// copied from ROM. The startup code behind the reset vector, the interrupt vectors, and the iNES file.
/// The ROM's places come from the cartridge's <see cref="Board"/>.
/// </summary>
internal sealed class InesBuild(OutputBuild.Inputs inputs) : OutputBuild(inputs)
{
    private const int PpuCtrl = 0x2000, PpuMask = 0x2001, PpuStatus = 0x2002, DmcFreq = 0x4010, ApuFrame = 0x4017;
    /// <summary>The RAM's mirrors end here: the PPU's registers follow.</summary>
    private const int RamMirrorsEnd = 0x2000;
    private const int NmiVector = 0xFFFA, ResetVector = 0xFFFC, IrqVector = 0xFFFE;
    /// <summary>iNES's units: PRG ROM in 16 KB, CHR ROM in 8 KB.</summary>
    internal const int PrgBank = 0x4000, ChrBank = 0x2000;

    private readonly Cartridge cartridge = inputs.Program.Layout.Cartridge!;
    private readonly Label rti = new("rti"), bankZero = new("bank_zero");
    private (bool Nmi, bool Irq) vectors;
    private int prgSize, prgFirst, bankCount;

    private Board Board => cartridge.Board;
    /// <summary>The fixed ROM, last in the image: the board's first code area ($C000-$FFFF).</summary>
    private AddressRange Fixed => Board.CodeAreas[0];
    /// <summary>The ROM's first address: the lowest of the board's code areas and bank window ($8000).</summary>
    private int RomFirst => Board.CodeAreas.Append(Board.BankArea ?? Fixed).Min(a => a.First);

    public override byte Padding => 0xFF;
    public override AddressRange? RamCleared => new(0, Platform.RamEnd);
    public override bool SetsIrqVector => vectors.Irq;

    public override void Planned(RamPlan plan) => vectors = CheckVectors(plan);

    public override IEnumerable<AsmItem> BeforeStartup() => [new Instr(Sei)];

    /// <summary>
    /// The first part of the startup code (after <c>sei</c> and <c>cld</c>): APU frame IRQ off, the stack,
    /// PPU NMI and rendering off, DMC IRQ off; then two waits for vblank, since the PPU ignores writes for
    /// about two frames after power-on, with all of RAM cleared between them. On a board with banks, bank 0
    /// is selected first (the bank at power-on is undefined), by writing X (0) over a 0 in the fixed bank.
    /// </summary>
    public override IEnumerable<AsmItem> Prelude()
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
        if (Board.BankArea is not null)
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
        for (int page = 1; page <= Platform.RamEnd >> 8; page++) items.Add(new Instr(Sta, AbsoluteX, new AddrOperand(page * 0x100)));
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

    /// <summary>The rti that a vector the program does not set points to, and on a board with banks the 0 that selects bank 0.</summary>
    public override IEnumerable<AsmItem> AfterHalt()
    {
        yield return new Comment("an interrupt vector the program does not set");
        yield return new LabelDef(rti);
        yield return new Instr(Rti);
        if (Board.BankArea is null) yield break;
        // The latch also sees this byte (bus conflicts): writing 0 over a 0 selects bank 0.
        yield return new LabelDef(bankZero);
        yield return new DataBytes([0]);
    }

    /// <summary>The vectors the compiler sets: reset to the startup code, and NMI and IRQ, unless the program sets them, to an rti.</summary>
    public override IEnumerable<AsmItem> AfterCode()
    {
        foreach (var (vector, name, target, own) in new[] { (NmiVector, "nmi vector", rti, vectors.Nmi), (ResetVector, "reset vector", Start, false), (IrqVector, "irq vector", rti, vectors.Irq) })
        {
            if (own) continue;
            yield return new SectionStart(name, vector);
            yield return new AddressByte(target, null, 0, false, target.Name);
            yield return new AddressByte(target, null, 0, true, target.Name);
        }
    }

    /// <summary>
    /// NROM fills $C000-$FFFF first, so a small program is NROM-128; one that does not fit there is laid out
    /// again in one area, $8000-$FFFF, so that a function or table may be larger than 16 KB (review M27, finding 4).
    /// </summary>
    public override IReadOnlyList<AddressRange>? Rejoined(IReadOnlyList<AddressRange> areas)
    {
        if (Board.BankArea is not null) return null;
        var joined = new List<AddressRange>();
        foreach (var a in areas.OrderBy(a => a.First))
            if (joined.Count > 0 && joined[^1].Last + 1 == a.First) joined[^1] = joined[^1] with { Last = a.Last };
            else joined.Add(a);
        return joined.Count < areas.Count ? joined : null;
    }

    /// <summary>
    /// The interrupt vectors a program sets with <c>data</c> (spec 12.2): reset is the compiler's, and NMI
    /// and IRQ must be the address of an <c>interrupt fn</c> or an <c>asm fn</c>, which return with rti.
    /// Returns which of NMI and IRQ the program sets. Data outside ROM is an error: the cartridge cannot
    /// put bytes in RAM.
    /// </summary>
    private (bool Nmi, bool Irq) CheckVectors(RamPlan plan)
    {
        bool nmi = false, irq = false;
        var rom = new AddressRange(RomFirst, 0xFFFF);
        foreach (var g in plan.FixedImageGlobals)
        {
            var range = new AddressRange(g.Address, g.Address + g.Type.Size - 1);
            if (range.First < rom.First)
                throw new CompileError(g.Pos,
                    $"'{g.Name}' at {range} is outside the cartridge's ROM ({rom}), and the image cannot put bytes in RAM. Make it a 'var' with an initial value and no fixed address, which the startup code copies to RAM, or place it in ROM");
            if (range.Overlaps(new AddressRange(ResetVector, ResetVector + 1)))
                throw new CompileError(g.Pos,
                    $"'{g.Name}' at {range} covers the reset vector (${ResetVector:X4}), which belongs to the startup code: reset sets up the machine and then calls main. Remove the data, and start the program in main");
            foreach (var (vector, what) in new[] { (NmiVector, "NMI"), (IrqVector, "IRQ") })
            {
                if (!range.Overlaps(new AddressRange(vector, vector + 1))) continue;
                int at = vector - g.Address;
                var fill = g.AddressFills.FirstOrDefault(f => f.Part == AddressPart.Word && TernCompiler.LayoutIndex(g, f.Index) == at && TernCompiler.LayoutIndex(g, f.Index + 1) == at + 1);
                var f = fill?.Function;
                if (f is null || fill!.Offset != 0 || !(f.IsInterrupt && !f.IsPart || f.Declaration?.IsAsm == true))
                    throw new CompileError(g.Pos,
                        $"The {what} vector (${vector:X4}) must be the address of an interrupt fn or an asm fn, which return with rti" +
                        (f is null || fill!.Offset != 0 ? "" : f.IsPart ? $"; '{f.Name}' is called by another interrupt fn, so it is compiled as a part, which returns with rts"
                            : $"; '{f.Name}' is a plain fn, which returns with rts") +
                        $". {(f is not null && !f.IsPart && fill!.Offset == 0 ? $"Declare it 'interrupt fn {f.Name}'" : "Write addr() of an interrupt fn")}");
                // Decision 2026-11-14 (question 169): an interrupt can come with any bank selected.
                if (f.Module is { InBank: true } m)
                    throw new CompileError(g.Pos,
                        $"The {what} vector (${vector:X4}) points to '{f.Name}' in bank {m.Bank} of the bank area '{m.OverlayArea}', but an interrupt can come with any bank selected. " +
                        $"Call it from a resident interrupt fn through an interface its module implements, or import {m.Name} without '@ {m.OverlayArea}[{m.Bank}]'");
                if (vector == NmiVector) nmi = true; else irq = true;
            }
        }
        return (nmi, irq);
    }

    /// <summary>
    /// The iNES 1.0 file (spec 12.2): the header (mapper, PRG ROM in 16 KB units, CHR ROM in 8 KB units,
    /// mirroring), PRG ROM, then CHR ROM. NROM is 16 KB (the fixed area, mirrored below it) when nothing lies
    /// below it, else 32 KB. Bytes no code or data covers are $FF. CHR ROM holds the data of the chr
    /// area at its PPU addresses, zeros elsewhere.
    /// </summary>
    public override byte[] File(AssembledCode code, RamPlan plan, int first, byte[] image, IReadOnlyList<Module> banks)
    {
        var rom = code.Segments.Where(s => s.Address >= RomFirst).ToList();
        byte[] prg;
        if (Board.BankArea is { } window)
        {
            // UxROM: the banks by number, then the fixed bank; as many as the highest number needs, a power of two (spec 12.2).
            var units = banks.GroupBy(m => m.Unit!).ToDictionary(g => g.Key, g => g.First().Bank!.Value);
            bankCount = 1;
            while (bankCount < (units.Count == 0 ? 1 : units.Values.Max() + 2)) bankCount *= 2;
            prg = new byte[bankCount * PrgBank];
            Array.Fill(prg, (byte)0xFF);
            prgFirst = Fixed.First;
            foreach (var (address, bytes) in rom) bytes.CopyTo(prg, (bankCount - 1) * PrgBank + address - Fixed.First);
            foreach (var (unit, n) in units)
                if (code.Overlays.TryGetValue(unit, out var bankSegments))
                    foreach (var (address, bytes) in bankSegments) bytes.CopyTo(prg, n * PrgBank + address - window.First);
        }
        else
        {
            prgFirst = rom.Count > 0 && rom.Min(s => s.Address) < Fixed.First ? RomFirst : Fixed.First;
            prg = new byte[Fixed.Last + 1 - prgFirst];
            Array.Fill(prg, (byte)0xFF);
            foreach (var (address, bytes) in rom) bytes.CopyTo(prg, address - prgFirst);
        }
        prgSize = prg.Length;
        var chr = Board.ChrRom ? new byte[ChrBank] : [];
        foreach (var g in plan.ChrGlobals) TernCompiler.ByteLayout(g, g.InitialBytes ?? new byte[g.Type.Size]).CopyTo(chr, g.Address);
        var file = new List<byte>
        {
            (byte)'N', (byte)'E', (byte)'S', 0x1A,
            (byte)(prg.Length / PrgBank), (byte)(chr.Length / ChrBank),
            (byte)((Board.Mapper & 0x0F) << 4 | (cartridge.VerticalMirroring ? 1 : 0)),
            (byte)(Board.Mapper & 0xF0),
            0, 0, 0, 0, 0, 0, 0, 0,
        };
        file.AddRange(prg);
        file.AddRange(chr);
        return file.ToArray();
    }

    /// <summary>
    /// A label's place for Mesen (<see cref="LabelFormat.Mesen"/>): RAM as itself, ROM as its offset into
    /// PRG ROM, registers left out. With banks, the fixed bank is the last, and a label of a module in a
    /// bank is in that module's bank. Tile data (in a chr area) has a PPU address, which is its offset into CHR ROM.
    /// </summary>
    public override Func<string, int, int>? LabelPlaces(IReadOnlyList<Module> banks)
    {
        var moduleBanks = banks.ToDictionary(m => m.Name, m => m.Bank!.Value);
        var chr = Program.Globals.Where(g => g.InChr).Select(g => g.Name).ToHashSet();
        var window = Board.BankArea;
        return (name, address) =>
        {
            if (chr.Contains(name)) return LabelFormat.ChrRom | address;
            if (address < RamMirrorsEnd) return address & Platform.RamEnd;
            if (address < RomFirst) return -1;
            if (bankCount == 0) return address >= prgFirst ? LabelFormat.PrgRom | (address - prgFirst) : -1;
            if (address >= Fixed.First) return LabelFormat.PrgRom | ((bankCount - 1) * PrgBank + address - Fixed.First);
            int dot = name.IndexOf('.');
            return dot > 0 && moduleBanks.TryGetValue(name[..dot], out int bank) ? LabelFormat.PrgRom | (bank * PrgBank + address - window!.Value.First) : -1;
        };
    }

    public override IEnumerable<string> ReportLines(RamPlan plan)
    {
        yield return $"Cartridge: {Board.Name} (mapper {Board.Mapper}), {(cartridge.VerticalMirroring ? "vertical" : "horizontal")} mirroring, " +
            (Board.BankArea is null ? $"{prgSize / 1024} KB of PRG ROM at ${prgFirst:X4}-${Fixed.Last:X4}, "
                : $"{prgSize / 1024} KB of PRG ROM in {TernCompiler.Plural(bankCount, "bank", "banks")} of {PrgBank / 1024} KB, the last fixed at {Fixed}, ") +
            (Board.ChrRom ? $"{ChrBank / 1024} KB of CHR ROM" : "CHR RAM") +
            (plan.CopiedSize > 0 ? $"; the startup code copies {TernCompiler.Plural(plan.CopiedSize, "byte", "bytes")} of initial values to RAM" : "");
    }
}
