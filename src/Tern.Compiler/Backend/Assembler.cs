using System.Text;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using Tern.Cpu;
using Encoder = Tern.Cpu.Encoder;

namespace Tern.Compiler.Backend;

/// <summary>A section (a function, a global's initial value, the startup code) at the address it was placed.</summary>
/// <param name="Overlay">The overlay module the section is part of, or null.</param>
public sealed record PlacedSection(string Name, int Address, int Size, bool IsFixed, string? Overlay = null);

/// <summary>A stretch of a program area that code, data and variables were placed in.</summary>
/// <param name="UsedEnd">The address after the last byte placed in <paramref name="Free"/>.</param>
public sealed record PlacedArea(AddressRange Free, int UsedEnd, List<string> Names);

/// <summary>
/// One instruction of the program and the source line it was generated for: the last source line
/// before it in the listing (null <see cref="Line"/> for code with none, such as the startup code).
/// <see cref="File"/> is null for the main file, else the module's file name. <see cref="Cycles"/> is
/// the base count; <see cref="MoreCycles"/> is set for a branch or a read that can cross a page.
/// </summary>
/// <see cref="Overlay"/> is the overlay or bank its section is in ("levels[1]"), or null.
public sealed record PlacedInstruction(int Address, int Size, string Text, int Cycles, bool MoreCycles, string? File, int? Line, string? Source, string Section,
    CheckedIndex? Index = null, string? Overlay = null)
{
    /// <summary>The bank it is in (spec 8.5), or -1 outside banks.</summary>
    public int Bank => CodePlace.BankOf(Overlay);
    /// <summary>Where it is: its address, told apart from the same address in other banks (<see cref="CodePlace"/>).</summary>
    public int Place => CodePlace.Of(Address, Bank);
}

/// <summary>
/// A place in the program's code: an address, and for code in a bank (spec 8.5) the bank, since banks
/// share their addresses. The emulator's index check, profile and function entries are keyed by place.
/// </summary>
public static class CodePlace
{
    /// <summary>The address, with the bank above its 16 bits; the address alone outside banks (<paramref name="bank"/> -1).</summary>
    public static int Of(int address, int bank) => bank < 0 ? address : (bank + 1) << 16 | address;

    /// <summary>The bank of an overlay or bank unit ("levels[1]" is 1); -1 for an overlay or none.</summary>
    public static int BankOf(string? unit) =>
        unit is not null && unit.EndsWith(']') && int.TryParse(unit[(unit.IndexOf('[') + 1)..^1], out int n) ? n : -1;
}

/// <summary>
/// An array index or <c>indexed match</c> table read that the emulator checks (M20): the address
/// <see cref="Base"/> plus X (or Y, <see cref="UsesY"/>) must lie in <see cref="First"/> to First + Length - 1.
/// </summary>
public sealed record CheckedIndex(int Base, bool UsesY, bool ZeroPage, int First, IndexCheck Check);

public sealed class AssembledCode(
    List<(int Address, byte[] Bytes)> segments, string listing, List<PlacedSection> sections, List<PlacedArea> areas, AddressRange? variables,
    Dictionary<string, List<(int Address, byte[] Bytes)>> overlays, List<PlacedInstruction> instructions)
{
    /// <summary>Every instruction, in address order within each section, with its source line.</summary>
    public List<PlacedInstruction> Instructions { get; } = instructions;
    /// <summary>The bytes of each overlay module's file, by module name.</summary>
    public Dictionary<string, List<(int Address, byte[] Bytes)>> Overlays { get; } = overlays;
    /// <summary>The bytes of the program file, in address order. Each segment is a run of adjacent sections.</summary>
    public List<(int Address, byte[] Bytes)> Segments { get; } = segments;
    public string Listing { get; } = listing;
    public List<PlacedSection> Sections { get; } = sections;
    public List<PlacedArea> Areas { get; } = areas;
    /// <summary>Where the program's variables (<see cref="Reserve"/>) were placed, or null if there are none.</summary>
    public AddressRange? Variables { get; } = variables;
    /// <summary>Where the area's used bytes ended before the variables' block, which starts at <see cref="Variables"/>.</summary>
    public int VariablesAfter { get; init; }
}

/// <summary>
/// The built-in assembler. Places sections in the program areas (a section never crosses from one
/// area into another), picks zero-page addressing where possible, turns out-of-range branches into an
/// inverted branch over a <c>jmp</c>, and encodes.
/// </summary>
public static class Assembler
{
    /// <summary>
    /// The variables were to start at a place in a page or at an alignment (<see cref="Reserve.Modulus"/>), but no area has room
    /// for that, or a <c>jmp (...)</c> vector would then start at $xxFF. The caller places them another way.
    /// </summary>
    public sealed class NoPageStart() : Exception("The variables do not fit where they were placed in a page");

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
    /// <param name="overlayImports">Where each overlay module is imported, for the error when it does not fit.</param>
    /// <param name="overlayAreas">For each overlay module, the area its sections start in (spec 8.5). Each module is laid out from the start of its area.</param>
    /// <param name="variableAreas">
    /// RAM for the variables' block when code is in ROM (the NES): the block goes there, in the first
    /// area with room, instead of after the code in <paramref name="areas"/>. Null or empty: after the code.
    /// </param>
    public static AssembledCode Assemble(IReadOnlyList<AsmItem> items, IReadOnlyList<AddressRange> areas, Action? beforeEncode = null,
        IReadOnlyDictionary<string, AddressRange>? overlayAreas = null, IReadOnlyDictionary<string, SourcePos>? overlayImports = null,
        IReadOnlyList<AddressRange>? variableAreas = null)
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
                    AddressByte => 1,
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
        bool variablesOnPage = false;

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
                if (end > at && section.Info.Overlay is null) fixedRanges.Add(new AddressRange(at, end - 1));
            }
            // The first section (the BASIC line and startup code, or a binary's jump table) must be at the
            // start of the first area: data placed there with '@' would push it away.
            var firstSection = sections.FirstOrDefault(s => s.Info.FixedAddress is null && s.Info.Overlay is null);
            if (firstSection is not null && areas.Count > 0)
            {
                int firstEnd = Lay(firstSection, areas[0].First);
                if (firstEnd > areas[0].First)
                {
                    var head = new AddressRange(areas[0].First, firstEnd - 1);
                    string what = firstSection.Info.Name == "jump table" ? "jump table" : "BASIC line and startup code";
                    if (firstEnd > areas[0].Last + 1)
                        throw new CompileError(new SourcePos(1, 1),
                            $"The {what} ({firstEnd - areas[0].First} bytes) must be at the start of the first program area, but it does not fit in {areas[0]}. Make the first area larger");
                    foreach (var section in sections.Where(s => s.Info.FixedAddress is not null && s.Info.Overlay is null))
                    {
                        int at = section.Info.FixedAddress!.Value;
                        int end = Lay(section, at);
                        if (end > at && head.Overlaps(new AddressRange(at, end - 1)))
                            throw new CompileError(new SourcePos(1, 1),
                                $"'{section.Info.Name}' at {new AddressRange(at, end - 1)} overlaps the {(firstSection.Info.Name == "jump table" ? "jump table" : "BASIC line and startup code")} at {head}, " +
                                "which must be at the start of the first program area. " +
                                (section.Info.Name == "export table" ? $"Give the export table an address after ${head.Last:X4}" : "Move the data, or start the program area lower"));
                    }
                }
            }
            var free = MemoryLayout.Subtract(areas, fixedRanges);
            if (free.Count == 0)
                throw new CompileError(new SourcePos(1, 1), $"No program area is left: the data placed with '@' fills {MemoryLayout.List(areas)}. Move some of the data, or make an area larger");
            var used = free.Select(a => a.First).ToArray();
            var names = free.Select(_ => new List<string>()).ToArray();

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

            // Overlay modules: each one from the start of its area, so overlays in one area overlap.
            foreach (var module in sections.Where(s => s.Info.Overlay is not null).GroupBy(s => s.Info.Overlay!))
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
                if (!fits && module.Key.EndsWith(']'))
                {
                    var sizes = module.GroupBy(s => s.Info.Name.Contains('.') ? s.Info.Name[..s.Info.Name.IndexOf('.')] : s.Info.Name)
                        .Select(g => $"{g.Key} ({g.Sum(s => SizeOf(s))} bytes)");
                    int at = module.Key.IndexOf('[');
                    throw new CompileError(overlayImports?.GetValueOrDefault(module.Key) ?? new SourcePos(1, 1),
                        $"Bank {module.Key[(at + 1)..^1]} of '{module.Key[..at]}' needs {size} bytes, but a bank has {area.Size}: {string.Join(", ", sizes)}. Move a module to another bank, or make one smaller");
                }
                if (!fits)
                    throw new CompileError(overlayImports?.GetValueOrDefault(module.Key) ?? new SourcePos(1, 1),
                        $"The overlay '{module.Key}' needs {size} bytes" + (fixedHere.Count > 0 ? $" of code, data and variables, plus {fixedHere.Sum(r => r.Size)} bytes of fixed data," : "") +
                        $" but its area {area} has {area.Size}. Make the area larger, or split the module");
            }

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
                            "Make arrays smaller, use 'data' for tables that never change (they stay in ROM), or put small variables in zero page with '@ zp'");
                    }
                    throw NoRoom("the variables", reserve.Size, free, used, names);
                }
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
                        throw Generated(i, pos,
                            $"Branch to {target} is out of range ({Resolve(target.Label) + target.Offset - s.Address - 2} bytes; a branch reaches -128 to 127). Use jmp");
                    s.LongBranch = true;
                    changed = true;
                }
            }
        } while (changed);

        var placed = sections.Select(s => new PlacedSection(s.Info.Name, slots[s.Start].Address, SizeOf(s), s.Info.FixedAddress is not null, s.Info.Overlay)).ToList();
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

        beforeEncode?.Invoke();

        var segments = new List<(int Address, byte[] Bytes)>();
        var overlaySegments = new Dictionary<string, List<(int Address, byte[] Bytes)>>();
        var bytes = new List<byte>();
        int segmentStart = 0;
        string? overlay = null;
        void Flush()
        {
            if (bytes.Count > 0)
            {
                if (overlay is null) segments.Add((segmentStart, bytes.ToArray()));
                else
                {
                    if (!overlaySegments.TryGetValue(overlay, out var list)) overlaySegments[overlay] = list = [];
                    list.Add((segmentStart, bytes.ToArray()));
                }
            }
            bytes.Clear();
        }
        var listing = new StringBuilder();
        var instructions = new List<PlacedInstruction>();
        SourceLine? source = null;
        string sectionName = "";
        foreach (var s in slots)
        {
            switch (s.Item)
            {
                case SectionStart begin:
                    Flush();
                    segmentStart = s.Address;
                    overlay = begin.Overlay;
                    source = null;
                    sectionName = begin.Name;
                    break;
                case Instr i:
                {
                    int begin = bytes.Count;
                    // An asm operand 'name + offset' must stay in memory; the encoder would keep 16 bits of it.
                    if (i.AsmPos is { } at && s.Mode != AddressingMode.Relative
                        && i.Operand switch { LabelOperand l => Resolve(l.Label) + l.Offset, VarOperand v => v.Variable.Address + v.Offset, _ => (int?)null }
                            is int sum and (< 0 or > 0xFFFF))
                        throw Generated(i, at, $"The address {i.Operand} is {(sum < 0 ? "-" : "")}${Math.Abs(sum):X}, outside memory. Use a smaller offset");
                    if (s.LongBranch)
                    {
                        // bne far  ->  beq +3 / jmp far
                        Encoder.Encode(Invert(i.Mnemonic), AddressingMode.Relative, s.Address + 5, s.Address, bytes);
                        var far = (LabelOperand)i.Operand!;
                        Encoder.Encode(Mnemonic.Jmp, AddressingMode.Absolute, Resolve(far.Label) + far.Offset, s.Address + 2, bytes);
                    }
                    else
                        Encoder.Encode(i.Mnemonic, s.Mode, OperandValue(i, s.Mode), s.Address, bytes);
                    var (text, cycles, more) = ListInstruction(listing, s, i, bytes.Skip(begin).ToArray());
                    instructions.Add(new PlacedInstruction(s.Address, bytes.Count - begin, text, cycles, more, source?.File, source?.Line, source?.Text.Trim(), sectionName,
                        Checked(i, s.Mode), overlay));
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
                case AddressByte a:
                {
                    int address = a.Label is { } label ? Resolve(label) : Address(new VarOperand(a.Variable!, 0));
                    byte value = (byte)Byte(address + a.Offset, a.High);
                    bytes.Add(value);
                    listing.AppendLine($"${s.Address:X4}  {value:X2}  ; {(a.High ? "hi" : "lo")}: {ImmLabelByte.OffsetText(a.Name, a.Offset)}");
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
        Flush();
        if (variablePadding.Size > 0 && variablesOnPage)
            listing.AppendLine($"; ${variablePadding.From:X4}: {variablePadding.Size} byte(s) left free so that {reserve!.Reason} (not in the file)");
        else if (variablePadding.Size > 0)
            listing.AppendLine($"${variablePadding.From:X4}  {variablePadding.Size} byte(s) of padding: no jmp (...) vector may start at $xxFF (before the variables; not in the file)");

        // Adjacent segments are one run of bytes.
        static List<(int Address, byte[] Bytes)> Merge(List<(int Address, byte[] Bytes)> segments)
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
        return new AssembledCode(Merge(segments), listing.ToString(), placed, placedAreas, variables,
            overlaySegments.ToDictionary(p => p.Key, p => Merge(p.Value)), instructions) { VariablesAfter = variablePadding.From };
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
            $"(code is never split across areas). Areas: {string.Join("; ", areas)}. Make an area larger, or split the code into smaller functions");
    }

    /// <summary>Uses a zero-page mode when the operand is a zero-page address and the instruction has one.</summary>
    /// <summary>An error at an instruction a generator emitted also names the generator call (spec 4.1).</summary>
    private static CompileError Generated(Instr i, SourcePos pos, string message) => i.CalledFrom is { } call
        ? new CompileError(pos, CompileTime.Interpreter.WithNote(message, $"generated by the call at {CompileTime.Interpreter.Where(call)}")) { CalledFrom = call }
        : new CompileError(pos, message);

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

    /// <summary>The index check of a compiler-generated indexed instruction (never one from an asm block), or null.</summary>
    private static CheckedIndex? Checked(Instr i, AddressingMode mode)
    {
        if (i.Check is { InA: true } inA && i.AsmPos is null) return new CheckedIndex(0, false, false, 0, inA);
        var check = i.Operand switch { VarOperand v => v.Check, LabelOperand l => l.Check, _ => null };
        if (check is null || i.AsmPos is not null
            || mode is not (AddressingMode.AbsoluteX or AddressingMode.AbsoluteY or AddressingMode.ZeroPageX or AddressingMode.ZeroPageY)) return null;
        int at = OperandValue(i, mode);
        return new CheckedIndex(at, mode is AddressingMode.AbsoluteY or AddressingMode.ZeroPageY,
            mode is AddressingMode.ZeroPageX or AddressingMode.ZeroPageY, (at - check.Shift) & 0xFFFF, check);
    }

    private static int Byte(int address, bool high) => high ? (address >> 8) & 0xFF : address & 0xFF;

    private static Mnemonic Invert(Mnemonic branch) => branch switch
    {
        Mnemonic.Bcc => Mnemonic.Bcs, Mnemonic.Bcs => Mnemonic.Bcc,
        Mnemonic.Beq => Mnemonic.Bne, Mnemonic.Bne => Mnemonic.Beq,
        Mnemonic.Bmi => Mnemonic.Bpl, Mnemonic.Bpl => Mnemonic.Bmi,
        Mnemonic.Bvc => Mnemonic.Bvs, Mnemonic.Bvs => Mnemonic.Bvc,
        _ => throw new InvalidOperationException($"{branch} is not a branch"),
    };

    /// <summary>Lists one instruction; returns its text and its cycles (base, and whether it can take more).</summary>
    private static (string Text, int Cycles, bool More) ListInstruction(StringBuilder listing, Slot s, Instr i, byte[] encoded)
    {
        string hex = string.Join(" ", encoded.Take(3).Select(b => b.ToString("X2")));
        string text = s.LongBranch
            ? $"{Invert(i.Mnemonic).ToString().ToLowerInvariant()} *+5 / jmp {i.Operand}"
            : $"{i.Mnemonic.ToString().ToLowerInvariant()} {FormatOperand(i.Operand, s.Mode)}".TrimEnd();
        InstructionSet.TryGet(i.Mnemonic, s.Mode, out var info);
        string cycles = s.LongBranch ? "2+3" : info.Mode == AddressingMode.Relative ? "2+" : info.PageCrossPenalty ? $"{info.Cycles}+" : $"{info.Cycles}";
        listing.AppendLine($"${s.Address:X4}  {hex,-9} {text,-22}; {cycles}");
        return (text, s.LongBranch ? 2 : info.Cycles, s.LongBranch || info.Mode == AddressingMode.Relative || info.PageCrossPenalty);
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
