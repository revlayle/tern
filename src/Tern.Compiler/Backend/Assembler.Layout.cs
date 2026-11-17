using System.Text;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using Tern.Cpu;
using Encoder = Tern.Cpu.Encoder;

namespace Tern.Compiler.Backend;

/// <summary>One assembly (<see cref="Assembly"/>): layout, the variables' place, and encoding with the listing.</summary>
public static partial class Assembler
{
    /// <summary>
    /// One call of <see cref="Assemble"/>. The fields are what its steps share: the slots and sections, and
    /// where the last layout pass put the program areas and the variables (review code-quality-backend 12).
    /// </summary>
    private sealed class Assembly
    {
        private readonly List<Slot> slots;
        private readonly IReadOnlyList<AddressRange> areas;
        private readonly InstructionSet cpu;
        private readonly IReadOnlyDictionary<string, AddressRange>? overlayAreas;
        private readonly IReadOnlyDictionary<string, SourcePos>? overlayImports;
        private readonly IReadOnlyList<AddressRange>? variableAreas;

        /// <summary>The slots from one <see cref="SectionStart"/> to the next; a <see cref="Reserve"/> ends the last one.</summary>
        private readonly List<Section> sections = [];
        private readonly Reserve? reserve;
        private readonly int reserveSlot = -1;

        // Set by each layout pass.
        private List<PlacedArea> placedAreas = [];
        private AddressRange? variables;
        private (int From, int Size) variablePadding = (0, 0);
        private bool variablesOnPage;

        public Assembly(IReadOnlyList<AsmItem> items, IReadOnlyList<AddressRange> areas, InstructionSet cpu,
            IReadOnlyDictionary<string, AddressRange>? overlayAreas, IReadOnlyDictionary<string, SourcePos>? overlayImports, IReadOnlyList<AddressRange>? variableAreas)
        {
            (this.areas, this.cpu, this.overlayAreas, this.overlayImports, this.variableAreas) = (areas, cpu, overlayAreas, overlayImports, variableAreas);
            slots = items.Select(i => new Slot(i)).ToList();
            foreach (var s in slots)
                if (s.Item is Instr instr)
                    s.Mode = ChooseMode(instr, cpu);

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
        }

        public AssembledCode Run(Action? beforeEncode)
        {
            // Layout: place the sections, then grow branches that do not reach until nothing changes.
            // Branches only ever grow, so this always finishes.
            do LayOut(); while (GrowBranches());

            var placed = sections.Select(s => new PlacedSection(s.Info.Name, slots[s.Start].Address, SizeOf(s), s.Info.FixedAddress is not null, s.Info.Overlay)).ToList();
            CheckFixedOverlaps(placed);
            beforeEncode?.Invoke();
            return Encode(placed);
        }

        /// <summary>Gives the slots of <paramref name="section"/> their addresses from <paramref name="pc"/>; returns the address after it.</summary>
        private int Lay(Section section, int pc)
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
                    AddressByte => 1,
                    Padding p => p.SizeAt(pc),
                    _ => 0,
                };
                if (s.Item is LabelDef l) l.Label.Address = pc;
                pc += s.Size;
            }
            return pc;
        }

        private int SizeOf(Section section) => slots[section.End - 1].Address + slots[section.End - 1].Size - slots[section.Start].Address;

        /// <summary>One layout pass: fixed sections, the first section, the program areas, the overlays, then the variables.</summary>
        private void LayOut()
        {
            var fixedRanges = LayFixedSections();
            LayFirstSection();
            var free = MemoryLayout.Subtract(areas, fixedRanges);
            if (free.Count == 0)
                throw new CompileError(new SourcePos(1, 1), $"No program area is left: the data placed with '@' fills {MemoryLayout.List(areas)}. Move some of the data, or make an area larger");
            var used = free.Select(a => a.First).ToArray();
            var names = free.Select(_ => new List<string>()).ToArray();
            LayInAreas(free, used, names);
            foreach (var module in sections.Where(s => s.Info.Overlay is not null).GroupBy(s => s.Info.Overlay!))
                LayOverlay(module);
            PlaceVariables(free, used, names);
        }

        /// <summary>Sections at a fixed address; returns the ranges the resident ones take from the program areas.</summary>
        private List<AddressRange> LayFixedSections()
        {
            var fixedRanges = new List<AddressRange>();
            foreach (var section in sections.Where(s => s.Info.FixedAddress is not null))
            {
                int at = section.Info.FixedAddress!.Value;
                int end = Lay(section, at);
                if (end > at && section.Info.Overlay is null) fixedRanges.Add(new AddressRange(at, end - 1));
            }
            return fixedRanges;
        }

        /// <summary>
        /// The first section (the BASIC line and startup code, or a binary's jump table) must be at the
        /// start of the first area: data placed there with '@' would push it away.
        /// </summary>
        private void LayFirstSection()
        {
            var firstSection = sections.FirstOrDefault(s => s.Info.FixedAddress is null && s.Info.Overlay is null);
            if (firstSection is null || areas.Count == 0) return;
            int firstEnd = Lay(firstSection, areas[0].First);
            if (firstEnd <= areas[0].First) return;
            var head = new AddressRange(areas[0].First, firstEnd - 1);
            string what = firstSection.Info.Title ?? firstSection.Info.Name;
            if (firstEnd > areas[0].Last + 1)
                throw new CompileError(new SourcePos(1, 1),
                    $"The {what} ({firstEnd - areas[0].First} bytes) must be at the start of the first program area, but it does not fit in {areas[0]}. Make the first area larger");
            foreach (var section in sections.Where(s => s.Info.FixedAddress is not null && s.Info.Overlay is null))
            {
                int at = section.Info.FixedAddress!.Value;
                int end = Lay(section, at);
                if (end > at && head.Overlaps(new AddressRange(at, end - 1)))
                    throw new CompileError(new SourcePos(1, 1),
                        $"'{section.Info.Name}' at {new AddressRange(at, end - 1)} overlaps the {what} at {head}, " +
                        "which must be at the start of the first program area. " +
                        (section.Info.Kind == SectionKind.ExportTable ? $"Give the export table an address after ${head.Last:X4}" : "Move the data, or start the program area lower"));
            }
        }

        /// <summary>The resident sections without an address, first fit in the program areas; a section never crosses from one area into the next.</summary>
        private void LayInAreas(List<AddressRange> free, int[] used, List<string>[] names)
        {
            int cur = 0, pc = free[0].First;
            foreach (var section in sections.Where(s => s.Info.FixedAddress is null && s.Info.Overlay is null))
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
        }

        /// <summary>An overlay module or a bank: from the start of its area, so overlays in one area overlap.</summary>
        private void LayOverlay(IGrouping<string, Section> module)
        {
            var area = overlayAreas![module.Key];
            // Fixed data (spec 8.5) goes at its address; the rest goes around it, first fit, as in a program area.
            var fixedHere = new List<AddressRange>();
            foreach (var section in module.Where(s => s.Info.FixedAddress is not null))
            {
                int at0 = section.Info.FixedAddress!.Value;
                int end0 = Lay(section, at0);
                if (end0 > at0) fixedHere.Add(new AddressRange(at0, end0 - 1));
            }
            var room = MemoryLayout.Subtract([area], fixedHere);
            int size = 0, slot = 0;
            int pos = room.Count > 0 ? room[0].First : area.First;
            bool fits = true;
            foreach (var section in module.Where(s => s.Info.FixedAddress is null))
            {
                int end = Lay(section, pos);
                while (room.Count > 0 && end > room[slot].Last + 1 && slot + 1 < room.Count)
                {
                    pos = room[++slot].First;
                    end = Lay(section, pos);
                }
                if (room.Count == 0 || end > room[slot].Last + 1) fits = false;
                size += end - slots[section.Start].Address;
                pos = end;
            }
            // A bank ("levels[1]") holds the modules placed in it; its sections are named module.name.
            if (!fits && CodePlace.Bank(module.Key) is (string bankArea, int bankNumber))
            {
                var sizes = module.GroupBy(s => s.Info.Module ?? s.Info.Name)
                    .Select(g => $"{g.Key} ({g.Sum(s => SizeOf(s))} bytes)");
                throw new CompileError(overlayImports?.GetValueOrDefault(module.Key) ?? new SourcePos(1, 1),
                    $"Bank {bankNumber} of '{bankArea}' needs {size} bytes, but a bank has {area.Size}: {string.Join(", ", sizes)}. Move a module to another bank, or make one smaller");
            }
            if (!fits)
                throw new CompileError(overlayImports?.GetValueOrDefault(module.Key) ?? new SourcePos(1, 1),
                    $"The overlay '{module.Key}' needs {size} bytes" + (fixedHere.Count > 0 ? $" of code, data and variables, plus {fixedHere.Sum(r => r.Size)} bytes of fixed data," : "") +
                    $" but its area {area} has {area.Size}. Make the area larger, or split the module");
        }

        /// <summary>
        /// The variables' block (<see cref="Reserve"/>): after the code, or in the RAM given for it, at the place in a page
        /// asked for, else first fit. Sets <see cref="placedAreas"/> and the variables' place.
        /// </summary>
        private void PlaceVariables(List<AddressRange> free, int[] used, List<string>[] names)
        {
            variables = null;
            variablePadding = (0, 0);
            variablesOnPage = false;
            int codeAreas = free.Count;
            if (variableAreas is { Count: > 0 })
            {
                free = [.. free, .. variableAreas];
                used = [.. used, .. variableAreas.Select(a => a.First)];
                names = [.. names, .. variableAreas.Select(_ => new List<string>())];
            }
            // The areas the variables' block may go in: after the code, or the RAM given for it.
            int firstForVariables = variableAreas is { Count: > 0 } ? codeAreas : 0;
            if (reserve is not null)
            {
                int placedAt = -1;
                // At the place in a page that was asked for (the first area with room, where no vector then ends a page), else first fit.
                int? Aligned(int a)
                {
                    if (reserve.Modulus == 1) return null;
                    int at = used[a] - (used[a] - reserve.Residue) % reserve.Modulus;
                    if (at < used[a]) at += reserve.Modulus;
                    return AvoidPageEnd(at, reserve.VectorOffsets) == 0 && at + reserve.Size <= free[a].Last + 1 ? at : null;
                }
                int alignedArea = Enumerable.Range(firstForVariables, free.Count - firstForVariables).FirstOrDefault(a => Aligned(a) is not null, -1);
                if (reserve.Modulus > 1 && alignedArea < 0) throw new NoPageStart();
                for (int a = firstForVariables; a < free.Count && placedAt < 0; a++)
                {
                    if (alignedArea >= 0 && a != alignedArea) continue;
                    int at = a == alignedArea ? Aligned(a)!.Value : used[a] + AvoidPageEnd(used[a], reserve.VectorOffsets);
                    if (at + reserve.Size <= free[a].Last + 1)
                    {
                        placedAt = at;
                        variablePadding = (used[a], at - used[a]);
                        variablesOnPage = a == alignedArea;
                        used[a] = at + reserve.Size;
                        if (reserve.Size > 0) names[a].Add("variables");
                    }
                }
                if (placedAt < 0)
                {
                    // In RAM of its own (the NES): name only that RAM (review M27, suggestion 5).
                    if (firstForVariables > 0)
                    {
                        var ram = free.Skip(firstForVariables).ToList();
                        int room = ram.Select((a, i) => a.Last + 1 - used[firstForVariables + i]).Max();
                        throw new CompileError(new SourcePos(1, 1),
                            $"The variables need {reserve.Size} bytes, but the RAM for variables, {MemoryLayout.List(ram)} ({ram.Sum(a => a.Size)} bytes), has room for {room}. " +
                            "Make arrays smaller, use 'data' for tables that never change (they stay in ROM), or put small variables in zero page with '@ zp'") { Reason = new AreasFull() };
                    }
                    throw NoRoom(null, reserve.Size, free, used, names);
                }
                reserve.Label.Address = placedAt;
                slots[reserveSlot].Address = placedAt;
                if (reserve.Size > 0) variables = new AddressRange(placedAt, placedAt + reserve.Size - 1);
            }
            placedAreas = free.Select((a, i) => new PlacedArea(a, used[i], names[i])).ToList();
        }

        /// <summary>Turns each branch that does not reach into a long one (an inverted branch over a <c>jmp</c>); true if any changed.</summary>
        private bool GrowBranches()
        {
            bool changed = false;
            foreach (var s in slots)
            {
                if (s.Item is Instr { Mode: AddressingMode.Relative, Operand: LabelOperand target } i && !s.LongBranch
                    && !Encoder.BranchInRange(Resolve(target.Label) + target.Offset, s.Address))
                {
                    if (i.AsmPos is { } pos)
                        throw Generated(i, pos,
                            $"Branch to {target} is out of range ({Resolve(target.Label) + target.Offset - s.Address - 2} bytes; a branch reaches -128 to 127). Use jmp");
                    s.LongBranch = true;
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>Two sections at fixed addresses may not overlap, unless they are in two overlays, which are never loaded together.</summary>
        private static void CheckFixedOverlaps(List<PlacedSection> placed)
        {
            var fixedSections = placed.Where(p => p.IsFixed && p.Size > 0).ToList();
            for (int a = 0; a < fixedSections.Count; a++)
                for (int b = a + 1; b < fixedSections.Count; b++)
                {
                    var (x, y) = (fixedSections[a], fixedSections[b]);
                    // Fixed data of two overlays may share an address: they are never loaded together.
                    if (x.Overlay is not null && y.Overlay is not null && x.Overlay != y.Overlay) continue;
                    if (x.Address < y.Address + y.Size && y.Address < x.Address + x.Size)
                        throw new CompileError(new SourcePos(1, 1),
                            $"'{x.Name}' ({new AddressRange(x.Address, x.Address + x.Size - 1)}) and '{y.Name}' ({new AddressRange(y.Address, y.Address + y.Size - 1)}) are placed at overlapping addresses. Move one of them");
                }
        }

        /// <summary>The bytes of the program and of each overlay, the listing and the placed instructions.</summary>
        private AssembledCode Encode(List<PlacedSection> placed)
        {
            var output = new Output();
            var listing = new StringBuilder();
            var instructions = new List<PlacedInstruction>();
            SourceLine? source = null;
            string sectionName = "";
            foreach (var s in slots)
            {
                switch (s.Item)
                {
                    case SectionStart begin:
                        output.Flush();
                        output.Start(s.Address, begin.Overlay);
                        source = null;
                        sectionName = begin.Name;
                        break;
                    case Instr i:
                        instructions.Add(EncodeInstruction(s, i, output, listing, source, sectionName));
                        break;
                    default:
                        EncodeData(s, output.Bytes, listing, ref source);
                        break;
                }
            }
            output.Flush();
            if (variablePadding.Size > 0 && variablesOnPage)
                listing.AppendLine($"; ${variablePadding.From:X4}: {variablePadding.Size} byte(s) left free so that {reserve!.Reason} (not in the file)");
            else if (variablePadding.Size > 0)
                listing.AppendLine($"${variablePadding.From:X4}  {variablePadding.Size} byte(s) of padding: no jmp (...) vector may start at $xxFF (before the variables; not in the file)");
            return new AssembledCode(Merge(output.Segments), listing.ToString(), placed, placedAreas, variables,
                output.OverlaySegments.ToDictionary(p => p.Key, p => Merge(p.Value)), instructions) { VariablesAfter = variablePadding.From };
        }

        private PlacedInstruction EncodeInstruction(Slot s, Instr i, Output output, StringBuilder listing, SourceLine? source, string sectionName)
        {
            var bytes = output.Bytes;
            int begin = bytes.Count;
            // An asm operand 'name + offset' must stay in memory; the encoder would keep 16 bits of it.
            if (i.AsmPos is { } at && s.Mode != AddressingMode.Relative
                && i.Operand switch { LabelOperand l => Resolve(l.Label) + l.Offset, VarOperand v => v.Variable.Address + v.Offset, _ => (int?)null }
                    is int sum and (< 0 or > 0xFFFF))
                throw Generated(i, at, $"The address {i.Operand} is {(sum < 0 ? "-" : "")}${Math.Abs(sum):X}, outside memory. Use a smaller offset");
            if (s.LongBranch)
            {
                // bne far  ->  beq +3 / jmp far
                Encoder.Encode(cpu, Instructions.Inverse(i.Mnemonic), AddressingMode.Relative, s.Address + 5, s.Address, bytes);
                var far = (LabelOperand)i.Operand!;
                Encoder.Encode(cpu, Mnemonic.Jmp, AddressingMode.Absolute, Resolve(far.Label) + far.Offset, s.Address + 2, bytes);
            }
            else
                Encoder.Encode(cpu, i.Mnemonic, s.Mode, OperandValue(i, s.Mode), s.Address, bytes);
            var (text, cycles, more) = ListInstruction(listing, s, i, bytes.Skip(begin).ToArray(), cpu);
            return new PlacedInstruction(s.Address, bytes.Count - begin, text, cycles, more, source?.File, source?.Line, source?.Text.Trim(), sectionName,
                Checked(i, s.Mode), output.Overlay);
        }

        /// <summary>A slot that is not an instruction: data, a table, an address byte, padding, or a line of the listing only.</summary>
        private static void EncodeData(Slot s, List<byte> bytes, StringBuilder listing, ref SourceLine? source)
        {
            switch (s.Item)
            {
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
                case AddressByte a:
                {
                    int address = a.Label is { } label ? Resolve(label) : Address(new VarOperand(a.Variable!, 0));
                    byte value = (byte)Byte(address + a.Offset, a.High);
                    bytes.Add(value);
                    listing.AppendLine($"${s.Address:X4}  {value:X2}  ; {(a.High ? "hi" : "lo")}: {ImmLabelByte.OffsetText(a.Name, a.Offset)}");
                    break;
                }
                case Padding p when s.Size > 0:
                    bytes.AddRange(Enumerable.Repeat(p.Fill, s.Size));
                    listing.AppendLine($"${s.Address:X4}  {s.Size} byte(s) of padding: {p.Reason}");
                    break;
                case LabelDef l:
                    listing.AppendLine($"{l.Label.Name}:");
                    break;
                case SourceLine src:
                    source = src;
                    listing.AppendLine((src.File is null
                        ? $"                                ; {src.Line,4}: {src.Text.Trim()}"
                        : $"                                ; {src.File}:{src.Line}: {src.Text.Trim()}")
                        + (src.Via is null ? "" : $"   ({src.Via})"));
                    break;
                case Comment c:
                    listing.AppendLine($"                                ; {c.Text}");
                    break;
            }
        }

        /// <summary>The runs of bytes being encoded: the program's, and each overlay's.</summary>
        private sealed class Output
        {
            public readonly List<(int Address, byte[] Bytes)> Segments = [];
            public readonly Dictionary<string, List<(int Address, byte[] Bytes)>> OverlaySegments = [];
            /// <summary>The bytes of the current section.</summary>
            public readonly List<byte> Bytes = [];
            private int _start;
            /// <summary>The overlay the current section is in, or null.</summary>
            public string? Overlay { get; private set; }

            public void Start(int address, string? overlay) => (_start, Overlay) = (address, overlay);

            /// <summary>Ends the current section's run of bytes.</summary>
            public void Flush()
            {
                if (Bytes.Count > 0)
                {
                    if (Overlay is null) Segments.Add((_start, Bytes.ToArray()));
                    else
                    {
                        if (!OverlaySegments.TryGetValue(Overlay, out var list)) OverlaySegments[Overlay] = list = [];
                        list.Add((_start, Bytes.ToArray()));
                    }
                }
                Bytes.Clear();
            }
        }

        /// <summary>Adjacent segments are one run of bytes.</summary>
        private static List<(int Address, byte[] Bytes)> Merge(List<(int Address, byte[] Bytes)> segments)
        {
            var merged = new List<(int Address, byte[] Bytes)>();
            foreach (var seg in segments.OrderBy(s => s.Address))
            {
                if (merged.Count > 0 && merged[^1].Address + merged[^1].Bytes.Length == seg.Address)
                    merged[^1] = (merged[^1].Address, merged[^1].Bytes.Concat(seg.Bytes).ToArray());
                else
                    merged.Add(seg);
            }
            return merged;
        }
    }
}
