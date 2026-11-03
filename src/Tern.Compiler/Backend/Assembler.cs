using System.Text;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using Tern.Cpu;
using Encoder = Tern.Cpu.Encoder;

namespace Tern.Compiler.Backend;

/// <summary>A section (a function, a global's initial value, the startup code) at the address it was placed.</summary>
public sealed record PlacedSection(string Name, int Address, int Size, bool IsFixed);

/// <summary>A stretch of a program area that code, data and variables were placed in.</summary>
/// <param name="UsedEnd">The address after the last byte placed in <paramref name="Free"/>.</param>
public sealed record PlacedArea(AddressRange Free, int UsedEnd, List<string> Names);

public sealed class AssembledCode(
    List<(int Address, byte[] Bytes)> segments, string listing, List<PlacedSection> sections, List<PlacedArea> areas, AddressRange? variables)
{
    /// <summary>The bytes of the program file, in address order. Each segment is a run of adjacent sections.</summary>
    public List<(int Address, byte[] Bytes)> Segments { get; } = segments;
    public string Listing { get; } = listing;
    public List<PlacedSection> Sections { get; } = sections;
    public List<PlacedArea> Areas { get; } = areas;
    /// <summary>Where the program's variables (<see cref="Reserve"/>) were placed, or null if there are none.</summary>
    public AddressRange? Variables { get; } = variables;
}

/// <summary>
/// The built-in assembler. Places sections in the program areas (a section never crosses from one
/// area into another), picks zero-page addressing where possible, turns out-of-range branches into an
/// inverted branch over a <c>jmp</c>, and encodes.
/// </summary>
public static class Assembler
{
    private sealed class Slot(AsmItem item)
    {
        public AsmItem Item { get; } = item;
        public AddressingMode Mode;     // final mode for instructions
        public int Address;
        public int Size;
        public bool LongBranch;
    }

    private sealed record Section(int Start, int End, SectionStart Info);

    /// <param name="areas">Where code and data may go, in the order they are filled.</param>
    /// <param name="beforeEncode">
    /// Called once layout is done, before encoding. Variables placed in the <see cref="Reserve"/> block
    /// get their addresses here. Variables without an address during layout are assumed to be
    /// outside zero page.
    /// </param>
    public static AssembledCode Assemble(IReadOnlyList<AsmItem> items, IReadOnlyList<AddressRange> areas, Action? beforeEncode = null)
    {
        var slots = items.Select(i => new Slot(i)).ToList();

        foreach (var s in slots)
            if (s.Item is Instr instr)
                s.Mode = ChooseMode(instr);

        // Sections: the slots from one SectionStart to the next. A Reserve ends the last one.
        var sections = new List<Section>();
        Reserve? reserve = null;
        int reserveSlot = -1;
        int start = 0;
        var info = new SectionStart("code");
        for (int i = 0; i <= slots.Count; i++)
        {
            bool boundary = i == slots.Count || slots[i].Item is SectionStart or Reserve;
            if (!boundary) continue;
            if (i > start) sections.Add(new Section(start, i, info));
            if (i == slots.Count) break;
            if (slots[i].Item is Reserve r)
            {
                reserve = r;
                reserveSlot = i;
                break;
            }
            info = (SectionStart)slots[i].Item;
            start = i;
        }

        int Lay(Section section, int pc)
        {
            for (int i = section.Start; i < section.End; i++)
            {
                var s = slots[i];
                s.Address = pc;
                s.Size = s.Item switch
                {
                    Instr => s.LongBranch ? 5 : InstructionSet.OperandSize(s.Mode) + 1,
                    DataBytes d => d.Bytes.Length,
                    LabelTable t => t.Labels.Count,
                    Padding p => p.SizeAt(pc),
                    _ => 0,
                };
                if (s.Item is LabelDef l) l.Label.Address = pc;
                pc += s.Size;
            }
            return pc;
        }
        int SizeOf(Section section) => slots[section.End - 1].Address + slots[section.End - 1].Size - slots[section.Start].Address;

        List<PlacedArea> placedAreas = [];
        AddressRange? variables = null;
        (int From, int Size) variablePadding = (0, 0);

        // Layout: place the sections, then grow branches that do not reach until nothing changes.
        // Branches only ever grow, so this always finishes.
        bool changed;
        do
        {
            var fixedRanges = new List<AddressRange>();
            foreach (var section in sections.Where(s => s.Info.FixedAddress is not null))
            {
                int at = section.Info.FixedAddress!.Value;
                int end = Lay(section, at);
                if (end > at) fixedRanges.Add(new AddressRange(at, end - 1));
            }
            var free = MemoryLayout.Subtract(areas, fixedRanges);
            if (free.Count == 0)
                throw new CompileError(new SourcePos(1, 1), $"No program area is left: the data placed with '@' fills {MemoryLayout.List(areas)}");
            var used = free.Select(a => a.First).ToArray();
            var names = free.Select(_ => new List<string>()).ToArray();

            int cur = 0, pc = free[0].First;
            foreach (var section in sections.Where(s => s.Info.FixedAddress is null))
            {
                int end = Lay(section, pc);
                while (end > free[cur].Last + 1)
                {
                    if (++cur >= free.Count)
                        throw NoRoom(section.Info.Name, end - slots[section.Start].Address, free, used, names);
                    pc = free[cur].First;
                    end = Lay(section, pc);
                }
                pc = used[cur] = end;
                names[cur].Add(section.Info.Name);
            }

            variables = null;
            variablePadding = (0, 0);
            if (reserve is not null)
            {
                int placedAt = -1;
                for (int a = 0; a < free.Count && placedAt < 0; a++)
                {
                    int at = used[a] + AvoidPageEnd(used[a], reserve.VectorOffsets);
                    if (at + reserve.Size <= free[a].Last + 1)
                    {
                        placedAt = at;
                        variablePadding = (used[a], at - used[a]);
                        used[a] = at + reserve.Size;
                        if (reserve.Size > 0) names[a].Add("variables");
                    }
                }
                if (placedAt < 0)
                    throw NoRoom("the variables", reserve.Size, free, used, names);
                reserve.Label.Address = placedAt;
                slots[reserveSlot].Address = placedAt;
                if (reserve.Size > 0) variables = new AddressRange(placedAt, placedAt + reserve.Size - 1);
            }
            placedAreas = free.Select((a, i) => new PlacedArea(a, used[i], names[i])).ToList();

            changed = false;
            foreach (var s in slots)
            {
                if (s.Item is Instr { Mode: AddressingMode.Relative, Operand: LabelOperand target } i && !s.LongBranch
                    && !Encoder.BranchInRange(Resolve(target.Label) + target.Offset, s.Address))
                {
                    if (i.AsmPos is { } pos)
                        throw new CompileError(pos,
                            $"Branch to {target} is out of range ({Resolve(target.Label) + target.Offset - s.Address - 2} bytes; a branch reaches -128 to 127). Use jmp");
                    s.LongBranch = true;
                    changed = true;
                }
            }
        } while (changed);

        var placed = sections.Select(s => new PlacedSection(s.Info.Name, slots[s.Start].Address, SizeOf(s), s.Info.FixedAddress is not null)).ToList();
        var fixedSections = placed.Where(p => p.IsFixed && p.Size > 0).ToList();
        for (int a = 0; a < fixedSections.Count; a++)
            for (int b = a + 1; b < fixedSections.Count; b++)
            {
                var (x, y) = (fixedSections[a], fixedSections[b]);
                if (x.Address < y.Address + y.Size && y.Address < x.Address + x.Size)
                    throw new CompileError(new SourcePos(1, 1),
                        $"'{x.Name}' ({new AddressRange(x.Address, x.Address + x.Size - 1)}) and '{y.Name}' ({new AddressRange(y.Address, y.Address + y.Size - 1)}) are placed at overlapping addresses");
            }

        beforeEncode?.Invoke();

        var segments = new List<(int Address, byte[] Bytes)>();
        var bytes = new List<byte>();
        int segmentStart = 0;
        void Flush()
        {
            if (bytes.Count > 0) segments.Add((segmentStart, bytes.ToArray()));
            bytes.Clear();
        }
        var listing = new StringBuilder();
        foreach (var s in slots)
        {
            switch (s.Item)
            {
                case SectionStart:
                    Flush();
                    segmentStart = s.Address;
                    break;
                case Instr i:
                {
                    int begin = bytes.Count;
                    if (s.LongBranch)
                    {
                        // bne far  ->  beq +3 / jmp far
                        Encoder.Encode(Invert(i.Mnemonic), AddressingMode.Relative, s.Address + 5, s.Address, bytes);
                        Encoder.Encode(Mnemonic.Jmp, AddressingMode.Absolute, Resolve(((LabelOperand)i.Operand!).Label), s.Address + 2, bytes);
                    }
                    else
                        Encoder.Encode(i.Mnemonic, s.Mode, OperandValue(i, s.Mode), s.Address, bytes);
                    ListInstruction(listing, s, i, bytes.Skip(begin).ToArray());
                    break;
                }
                case DataBytes d:
                    bytes.AddRange(d.Bytes);
                    for (int k = 0; k < d.Bytes.Length; k += 8)
                    {
                        var chunk = d.Bytes.Skip(k).Take(8).ToArray();
                        listing.AppendLine($"${s.Address + k:X4}  {string.Join(" ", chunk.Select(b => b.ToString("X2")))}");
                    }
                    break;
                case LabelTable t:
                {
                    var table = t.Labels.Select(l => (byte)Byte(Resolve(l) + t.Offset, t.High)).ToArray();
                    bytes.AddRange(table);
                    for (int k = 0; k < table.Length; k += 8)
                    {
                        var names = t.Labels.Skip(k).Take(8).Select(l => ImmLabelByte.OffsetText(l.Name, t.Offset));
                        listing.AppendLine($"${s.Address + k:X4}  {string.Join(" ", table.Skip(k).Take(8).Select(b => b.ToString("X2")))}"
                            + $"  ; {(t.High ? "hi" : "lo")}: {string.Join(", ", names)}");
                    }
                    break;
                }
                case Padding p when s.Size > 0:
                    bytes.AddRange(new byte[s.Size]);
                    listing.AppendLine($"${s.Address:X4}  {s.Size} byte(s) of padding: {p.Reason}");
                    break;
                case LabelDef l:
                    listing.AppendLine($"{l.Label.Name}:");
                    break;
                case SourceLine src:
                    listing.AppendLine(src.File is null
                        ? $"                                ; {src.Line,4}: {src.Text.Trim()}"
                        : $"                                ; {src.File}:{src.Line}: {src.Text.Trim()}");
                    break;
                case Comment c:
                    listing.AppendLine($"                                ; {c.Text}");
                    break;
            }
        }
        Flush();
        if (variablePadding.Size > 0)
            listing.AppendLine($"${variablePadding.From:X4}  {variablePadding.Size} byte(s) of padding: no jmp (...) vector may start at $xxFF (before the variables; not in the file)");

        // Adjacent segments are one run of bytes.
        var merged = new List<(int Address, byte[] Bytes)>();
        foreach (var seg in segments.OrderBy(s => s.Address))
        {
            if (merged.Count > 0 && merged[^1].Address + merged[^1].Bytes.Length == seg.Address)
                merged[^1] = (merged[^1].Address, merged[^1].Bytes.Concat(seg.Bytes).ToArray());
            else
                merged.Add(seg);
        }
        return new AssembledCode(merged, listing.ToString(), placed, placedAreas, variables);
    }

    /// <summary>
    /// The number of bytes to skip at <paramref name="pc"/> so that no vector at <c>pc + skip + offset</c>
    /// starts at an address ending in $FF. The NMOS 6502 reads the high byte of <c>jmp ($xxFF)</c> from $xx00.
    /// </summary>
    public static int AvoidPageEnd(int pc, IEnumerable<int> offsets)
    {
        var list = offsets.ToList();
        for (int skip = 0; ; skip++)
            if (list.All(o => ((pc + skip + o) & 0xFF) != 0xFF)) return skip;
    }

    /// <summary>The error for a section that fits in no program area, listing the areas and what is in them.</summary>
    private static CompileError NoRoom(string what, int size, List<AddressRange> free, int[] used, List<string>[] names)
    {
        var areas = free.Select((a, i) =>
        {
            var shown = names[i].Count <= 6 ? string.Join(", ", names[i]) : string.Join(", ", names[i].Take(6)) + $" and {names[i].Count - 6} more";
            return $"{a}: {used[i] - a.First} of {a.Size} bytes used" + (names[i].Count == 0 ? "" : $" ({shown})");
        });
        return new CompileError(new SourcePos(1, 1),
            $"{(what == "the variables" ? "The variables need" : $"'{what}' needs")} {size} bytes, and no program area has room for them in one piece " +
            $"(code is never split across areas). Areas: {string.Join("; ", areas)}");
    }

    /// <summary>Uses a zero-page mode when the operand is a zero-page address and the instruction has one.</summary>
    private static AddressingMode ChooseMode(Instr i)
    {
        var mode = i.Mode;
        if (i.Operand is LabelOperand) return mode;
        if (mode is AddressingMode.Absolute or AddressingMode.AbsoluteX or AddressingMode.AbsoluteY
            && AddressOf(i.Operand!) is < 0x100)
        {
            var zp = mode switch
            {
                AddressingMode.Absolute => AddressingMode.ZeroPage,
                AddressingMode.AbsoluteX => AddressingMode.ZeroPageX,
                _ => AddressingMode.ZeroPageY,
            };
            if (InstructionSet.TryGet(i.Mnemonic, zp, out _)) return zp;
        }
        if (!InstructionSet.TryGet(i.Mnemonic, mode, out _))
            throw new InvalidOperationException($"Code generator produced {i.Mnemonic} {mode}");
        return mode;
    }

    private static int? AddressOf(Operand op) => op switch
    {
        VarOperand v => v.Variable.Address < 0 ? null : Address(v),
        AddrOperand a => a.Address,
        _ => null,
    };

    private static int Address(VarOperand v)
    {
        if (v.Variable.Address < 0)
            throw new InvalidOperationException($"Variable {v.Variable.QualifiedName} has no address");
        return (v.Variable.Address + v.Offset) & 0xFFFF;   // an array folded with 'i - c' may start below its variable
    }

    private static int Resolve(Label label) =>
        label.Address >= 0 ? label.Address : throw new InvalidOperationException($"Label {label.Name} is not defined");

    private static int OperandValue(Instr i, AddressingMode mode) => i.Operand switch
    {
        null => 0,
        Imm imm => imm.Value & 0xFF,
        ImmLabelByte b => Byte(Resolve(b.Label) + b.Offset, b.High),
        ImmVarByte v => Byte(Address(new VarOperand(v.Variable, v.Offset)), v.High),
        VarOperand v => Address(v),
        AddrOperand a => a.Address,
        LabelOperand l => Resolve(l.Label) + l.Offset,
        _ => throw new InvalidOperationException(),
    };

    private static int Byte(int address, bool high) => high ? (address >> 8) & 0xFF : address & 0xFF;

    private static Mnemonic Invert(Mnemonic branch) => branch switch
    {
        Mnemonic.Bcc => Mnemonic.Bcs, Mnemonic.Bcs => Mnemonic.Bcc,
        Mnemonic.Beq => Mnemonic.Bne, Mnemonic.Bne => Mnemonic.Beq,
        Mnemonic.Bmi => Mnemonic.Bpl, Mnemonic.Bpl => Mnemonic.Bmi,
        Mnemonic.Bvc => Mnemonic.Bvs, Mnemonic.Bvs => Mnemonic.Bvc,
        _ => throw new InvalidOperationException($"{branch} is not a branch"),
    };

    private static void ListInstruction(StringBuilder listing, Slot s, Instr i, byte[] encoded)
    {
        string hex = string.Join(" ", encoded.Take(3).Select(b => b.ToString("X2")));
        string text = s.LongBranch
            ? $"{Invert(i.Mnemonic).ToString().ToLowerInvariant()} *+5 / jmp {i.Operand}"
            : $"{i.Mnemonic.ToString().ToLowerInvariant()} {FormatOperand(i.Operand, s.Mode)}".TrimEnd();
        InstructionSet.TryGet(i.Mnemonic, s.Mode, out var info);
        string cycles = s.LongBranch ? "2+3" : info.Mode == AddressingMode.Relative ? "2+" : info.PageCrossPenalty ? $"{info.Cycles}+" : $"{info.Cycles}";
        listing.AppendLine($"${s.Address:X4}  {hex,-9} {text,-22}; {cycles}");
    }

    private static string FormatOperand(Operand? op, AddressingMode mode)
    {
        string o = op?.ToString() ?? "";
        return mode switch
        {
            AddressingMode.Accumulator => "a",
            AddressingMode.AbsoluteX or AddressingMode.ZeroPageX => $"{o},x",
            AddressingMode.AbsoluteY or AddressingMode.ZeroPageY => $"{o},y",
            AddressingMode.IndirectIndexed => $"({o}),y",
            AddressingMode.IndexedIndirect => $"({o},x)",
            AddressingMode.Indirect => $"({o})",
            _ => o,
        };
    }
}
