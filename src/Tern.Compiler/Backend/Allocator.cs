using System.Text;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;

namespace Tern.Compiler.Backend;

/// <summary>Where variables go after the program: offsets from the end of the program.</summary>
public sealed class RamPlan
{
    /// <summary>Uninitialized globals, zeroed at startup: <see cref="ZeroedSize"/> bytes from <see cref="ZeroedStart"/>, first in the block but for aligned noinit globals.</summary>
    public List<(VariableSymbol Var, int Offset)> ZeroedGlobals { get; } = [];
    public int ZeroedStart { get; set; }
    public int ZeroedSize { get; set; }
    /// <summary>
    /// Arrays among the globals that the code reads through an index register, placed so that no such
    /// read crosses a page when the block starts at <see cref="StartResidue"/> in a page (M18 F12).
    /// </summary>
    public List<VariableSymbol> PageArrays { get; } = [];
    /// <summary>
    /// With <see cref="PageArrays"/> or aligned variables: the block must start at an address that is
    /// <see cref="StartResidue"/> more than a multiple of <see cref="StartModulus"/> (1 when it may start anywhere).
    /// </summary>
    public int StartModulus { get; set; } = 1;
    public int StartResidue { get; set; }
    /// <summary>Globals declared <c>noinit</c>, after the zeroed ones (aligned ones before them): not cleared at startup.</summary>
    public List<(VariableSymbol Var, int Offset)> NoinitGlobals { get; } = [];
    /// <summary>Function frames (parameters, locals, temporaries), after the globals.</summary>
    public List<(VariableSymbol Var, int Offset)> FrameVariables { get; } = [];
    public int TotalSize { get; set; }
    /// <summary>Globals with an initial value that live in the program image (on a cartridge, only <c>data</c>).</summary>
    public List<VariableSymbol> ImageGlobals { get; } = [];
    /// <summary>
    /// On a cartridge (code in ROM): variables with an initial value, in the block from <see cref="CopiedStart"/>,
    /// <see cref="CopiedSize"/> bytes. Their initial values are a table in ROM that the startup code copies (spec 12.2).
    /// </summary>
    public List<(VariableSymbol Var, int Offset)> CopiedGlobals { get; } = [];
    public int CopiedStart { get; set; }
    public int CopiedSize { get; set; }
    /// <summary>Globals in zero page, initialized by the startup code.</summary>
    public List<VariableSymbol> ZeroPageGlobals { get; } = [];
    public string ZeroPageMap { get; set; } = "";
    /// <summary>Data placed at a fixed address or in a region: part of the program file, at that address.</summary>
    public List<VariableSymbol> FixedImageGlobals { get; } = [];
    /// <summary>Variables and data of overlay modules: part of the overlay's file, with initial values (spec 8.5).</summary>
    public List<VariableSymbol> OverlayGlobals { get; } = [];
    /// <summary>Blocks of zeroed variables in regions: cleared at startup.</summary>
    public List<AddressRange> RegionClears { get; } = [];
    /// <summary>Where the first and last variable in each region are, for the report.</summary>
    public Dictionary<string, AddressRange> RegionUse { get; } = [];
    /// <summary>Tile data in the chr areas (NES), at PPU addresses: bytes of the image's CHR ROM.</summary>
    public List<VariableSymbol> ChrGlobals { get; } = [];
    /// <summary>The bytes of each chr area that its data covers, for the report.</summary>
    public Dictionary<string, int> ChrUse { get; } = [];
}

/// <summary>
/// Assigns memory to variables. Functions that can never run at the same time share memory:
/// each function's frame is placed after the frames of everything that calls it, so frames
/// overlap only between functions on different call paths.
/// </summary>
public static class Allocator
{
    /// <param name="byPage">Place indexed arrays so their reads do not cross a page (<see cref="RamPlan.PageArrays"/>); else one after another.</param>
    /// <param name="indexed">For each variable, the offsets of the code's indexed operands into it.</param>
    public static RamPlan Allocate(CheckedProgram program, Platform platform, bool byPage = true,
        IReadOnlyDictionary<VariableSymbol, List<int>>? indexed = null)
    {
        var layout = program.Layout;
        var functions = program.Functions;
        var callers = functions.ToDictionary(f => f, _ => new List<FunctionSymbol>());
        foreach (var f in functions)
            foreach (var callee in f.Callees)
                if (callers.ContainsKey(callee)) callers[callee].Add(f);

        foreach (var g in program.Globals)
            if (g.FixedAddress is int a) g.Address = a;

        // An overlay's variables are part of its file, in the area with its code; loading the file sets them.
        // A zero-page variable cannot be set that way.
        foreach (var g in program.Globals.Where(g => g.IsOverlayImage))
            if (g.RequireZeroPage || g.ForcedZeroPage)
                throw new CompileError(g.Pos,
                    $"'{g.Name}' must be in zero page, but it belongs to the overlay {g.Module!.Name}, whose variables are set by loading its file, and zero page is not part of the file. " +
                    "Use a local variable in the function that needs it, name a zero-page address (var p: ptr u8 @ $FB), or move the variable to the main file");

        foreach (var g in program.Globals.Where(g => g.Alignment > 1 && g.ForcedZeroPage && !g.RequireZeroPage))
            throw new CompileError(g.Pos,
                $"'{g.Name}' must be in zero page, since it is {(g.Dereferenced ? "dereferenced as a pointer" : "a zero-page operand of assembly code")}, so it cannot be placed with '@ align({g.Alignment})'. Remove '@ align({g.Alignment})'");

        var placeable = program.Globals.Where(g => g.FixedAddress is null && g.Region is null && !g.IsOverlayImage)
            .Concat(functions.SelectMany(f => f.FrameVariables))
            .ToList();
        var inZp = new HashSet<VariableSymbol>();

        // The free zero-page bytes in the order they are filled; a variable must lie in consecutive ones.
        var zeroPage = layout.ZeroPage.SelectMany(r => Enumerable.Range(r.First, r.Size)).ToList();
        int zpCapacity = zeroPage.Count;

        int ZpNeeded()
        {
            int globals = inZp.Where(v => v.Owner is null).Sum(v => v.Type.Size);
            var offsets = FrameOffsets(functions, callers, f => f.FrameVariables.Where(inZp.Contains).Sum(v => v.Type.Size));
            int frames = functions.Count == 0 ? 0 : functions.Max(f => offsets[f] + f.FrameVariables.Where(inZp.Contains).Sum(v => v.Type.Size));
            return globals + frames;
        }

        // 1. Required: variables marked "@ zp", then pointers that are dereferenced with (zp),y.
        void Require(IEnumerable<VariableSymbol> vars, string what)
        {
            var added = vars.ToList();
            foreach (var v in added) inZp.Add(v);
            if (ZpNeeded() <= zpCapacity) return;
            var users = string.Join(", ", inZp.Select(v => $"{v.QualifiedName} ({v.Type.Size})"));
            throw new CompileError(added.First().Pos,
                $"Zero page is full: {what} need {ZpNeeded()} bytes, {zpCapacity} are free. Users: {users}. Remove '@ zp' from some of them");
        }
        Require(placeable.Where(v => v.RequireZeroPage), "'@ zp' variables");
        Require(placeable.Where(v => v.ForcedZeroPage && !v.RequireZeroPage),
            "'@ zp' variables, dereferenced pointers and zero-page assembly operands");

        // 2. Best effort: the most used small variables.
        var candidates = placeable
            .Where(v => !v.ForcedZeroPage && !v.Type.IsArray && !v.Type.IsStruct && v.Weight > 0 && v.Alignment == 1)
            .OrderByDescending(v => v.Weight).ThenBy(v => v.QualifiedName, StringComparer.Ordinal);
        foreach (var v in candidates)
        {
            inZp.Add(v);
            if (ZpNeeded() > zpCapacity) inZp.Remove(v);
        }

        var plan = new RamPlan();

        // Zero-page addresses: globals first, then the overlaid frames. A variable the compiler chose
        // (step 2) that would cross the end of a range goes to RAM instead, and the rest are placed again.
        // A variable the program requires there that would cross makes room the same way: the least used
        // variable the compiler chose and placed before it goes to RAM. Only when there is none is it an
        // error (review M26, findings 1 and 8).
        VariableSymbol? crossing = null;
        (VariableSymbol Var, int Index)? required = null;
        int ZeroPageAddress(int index, VariableSymbol v)
        {
            int address = zeroPage[index];
            if (zeroPage[index + v.Type.Size - 1] - address != v.Type.Size - 1)
            {
                if (!v.ForcedZeroPage) crossing ??= v;
                else required ??= (v, index);
            }
            return address;
        }
        while (true)
        {
            crossing = null;
            required = null;
            plan.ZeroPageGlobals.Clear();
            int zp = 0;
            foreach (var g in program.Globals.Where(inZp.Contains))
            {
                g.Address = ZeroPageAddress(zp, g);
                zp += g.Type.Size;
                plan.ZeroPageGlobals.Add(g);
            }
            var zpOffsets = FrameOffsets(functions, callers, f => f.FrameVariables.Where(inZp.Contains).Sum(v => v.Type.Size));
            foreach (var f in functions)
            {
                int at = zp + zpOffsets[f];
                foreach (var v in f.FrameVariables.Where(inZp.Contains))
                {
                    v.Address = ZeroPageAddress(at, v);
                    at += v.Type.Size;
                }
            }
            var leave = crossing;
            if (leave is null && required is var (needed, index))
            {
                leave = inZp.Where(v => !v.ForcedZeroPage && zeroPage.IndexOf(v.Address) < index)
                    .OrderBy(v => v.Weight).ThenBy(v => v.QualifiedName, StringComparer.Ordinal).FirstOrDefault();
                if (leave is null)
                    throw new CompileError(needed.Pos,
                        $"'{needed.QualifiedName}' ({needed.Type.Size} bytes) would cross the end of a zero-page range at ${zeroPage[index + needed.Type.Size - 1]:X2}. " +
                        $"Zero-page ranges: {MemoryLayout.ListZeroPage(layout.ZeroPage)}. Join the ranges or reorder the variables");
            }
            if (leave is null) break;
            inZp.Remove(leave);
            leave.Address = -1;     // not placed yet: the assembler must not size its instructions for zero page
        }

        // Variables and data placed in a region, in the order they are declared: zeroed variables first
        // (one block to clear at startup), then noinit ones, then data. One placed at an offset
        // ('@ region + offset') is at that offset, and may not overlap the others (spec 4). A chr area
        // holds only data, in the PPU's memory (spec 12.2).
        foreach (var (name, range, chr) in layout.Regions.Select(r => (r.Key, r.Value, false)).Concat(layout.Chr.Select(c => (c.Key, c.Value, true))))
        {
            var inRegion = program.Globals.Where(g => g.Region == name).ToList();
            var placed = new List<VariableSymbol>();
            void Overlaps(VariableSymbol g)
            {
                foreach (var other in placed)
                    if (g.Address < other.Address + other.Type.Size && other.Address < g.Address + g.Type.Size)
                    {
                        var (first, second) = other.Pos.Line <= g.Pos.Line ? (other, g) : (g, other);
                        throw new CompileError(second.Pos,
                            $"'{second.Name}' at {new AddressRange(second.Address, second.Address + second.Type.Size - 1)} overlaps '{first.Name}' at {new AddressRange(first.Address, first.Address + first.Type.Size - 1)} in '{name}'. Move one of them, with '@ {name} + offset'");
                    }
                placed.Add(g);
            }
            foreach (var g in inRegion.Where(g => g.RegionOffset is not null))
            {
                g.Address = range.First + g.RegionOffset!.Value;
                Overlaps(g);
            }
            int at = range.First;
            void PlaceAll(IEnumerable<VariableSymbol> vars)
            {
                foreach (var g in vars.Where(g => g.RegionOffset is null))
                {
                    if (at + g.Type.Size - 1 > range.Last)
                        throw new CompileError(g.Pos,
                            $"{(chr ? "The chr area" : "Region")} '{name}' ({range}, {range.Size} bytes) is full: '{g.Name}' needs {g.Type.Size} bytes at ${at:X4}, " +
                            $"{Math.Max(0, range.Last + 1 - at)} are left. Place it elsewhere or make the {(chr ? "area" : "region")} larger");
                    g.Address = at;
                    at += g.Type.Size;
                    Overlaps(g);
                }
            }
            var zeroed = inRegion.Where(g => !g.IsData && !g.NoInit).ToList();
            PlaceAll(zeroed);
            if (at > range.First) plan.RegionClears.Add(new AddressRange(range.First, at - 1));
            foreach (var g in zeroed.Where(g => g.RegionOffset is not null))
                plan.RegionClears.Add(new AddressRange(g.Address, g.Address + g.Type.Size - 1));
            PlaceAll(inRegion.Where(g => !g.IsData && g.NoInit));
            var data = inRegion.Where(g => g.IsData).ToList();
            PlaceAll(data);
            (chr ? plan.ChrGlobals : plan.FixedImageGlobals).AddRange(data);
            if (placed.Count == 0) continue;
            if (chr) plan.ChrUse[name] = placed.Sum(g => g.Type.Size);
            else plan.RegionUse[name] = new AddressRange(placed.Min(g => g.Address), placed.Max(g => g.Address + g.Type.Size) - 1);
        }
        plan.FixedImageGlobals.AddRange(program.Globals.Where(g => g.FixedAddress is not null && g.IsData));
        plan.OverlayGlobals.AddRange(program.Globals.Where(g => g.IsOverlayImage));

        // RAM after the program: aligned noinit globals, zeroed globals, the other noinit globals, then
        // the overlaid frames, so that the block starts with the aligned ones and has no gap. Arrays read
        // through an index register are placed so that no such read crosses a page, and aligned
        // variables at a multiple of their alignment (spec 4), counting the offsets from a block that
        // starts at a multiple of the largest alignment (256 with such arrays, F12).
        // On a cartridge the initial values are in ROM and the variables in RAM: the variables are in the
        // block, after the aligned noinit ones, and the startup code copies their values (spec 12.2).
        var ram = program.Globals.Where(g => g.FixedAddress is null && g.Region is null && !g.IsOverlayImage && !inZp.Contains(g)).ToList();
        bool rom = layout.Variables.Count > 0;
        bool InBlock(VariableSymbol g) => g.InitialBytes is null || (rom && !g.IsData);
        plan.ImageGlobals.AddRange(ram.Where(g => !InBlock(g)));
        var spans = new Dictionary<VariableSymbol, List<(int From, int To)>>();
        if (byPage && indexed is not null)
            foreach (var g in ram.Where(InBlock))
                if (Spans(g, indexed) is { } list) spans[g] = list;
        var noinit = ram.Where(g => g.NoInit).ToList();
        int offset = PlaceByPage(noinit.Where(g => g.Alignment > 1).ToList(), 0, plan.NoinitGlobals, spans);
        // On a cartridge the startup code clears all of RAM, so the zeroed variables need not be one
        // block: the aligned ones go first too, with no gap before them.
        offset = PlaceByPage(ram.Where(g => rom && g.InitialBytes is null && !g.NoInit && g.Alignment > 1).ToList(), offset, plan.ZeroedGlobals, spans);
        offset = PlaceByPage(ram.Where(g => rom && !g.IsData && g.InitialBytes is not null).ToList(), offset, plan.CopiedGlobals, spans);
        plan.ZeroedStart = offset;
        offset = PlaceByPage(ram.Where(g => g.InitialBytes is null && !g.NoInit && !(rom && g.Alignment > 1)).ToList(), offset, plan.ZeroedGlobals, spans);
        int zeroedEnd = offset;
        offset = PlaceByPage(noinit.Where(g => g.Alignment == 1).ToList(), offset, plan.NoinitGlobals, spans);
        var globals = plan.ZeroedGlobals.Concat(plan.NoinitGlobals).Concat(plan.CopiedGlobals).ToList();
        plan.PageArrays.AddRange(spans.Keys.OrderBy(v => globals.First(e => e.Var == v).Offset));
        // Only the block's own variables count: data and initialized variables are aligned in the file (review M26 follow-up, finding 1).
        int modulus = Math.Max(spans.Count > 0 ? 256 : 1, globals.Select(e => e.Var.Alignment).DefaultIfEmpty(1).Max());
        // Space before the first variable is not needed: the block starts that far past a multiple of the modulus instead.
        if (modulus > 1 && globals.Count > 0 && globals.Min(e => e.Offset) is int lead and > 0)
        {
            foreach (var list in new[] { plan.ZeroedGlobals, plan.NoinitGlobals, plan.CopiedGlobals })
                for (int k = 0; k < list.Count; k++) list[k] = (list[k].Var, list[k].Offset - lead);
            plan.ZeroedStart = Math.Max(0, plan.ZeroedStart - lead);
            zeroedEnd -= lead;
            offset -= lead;
        }
        plan.ZeroedSize = zeroedEnd - plan.ZeroedStart;
        if (plan.CopiedGlobals.Count > 0)
        {
            plan.CopiedStart = plan.CopiedGlobals.Min(e => e.Offset);
            plan.CopiedSize = plan.CopiedGlobals.Max(e => e.Offset + e.Var.Type.Size) - plan.CopiedStart;
        }
        if (modulus > 1)
        {
            plan.StartModulus = modulus;
            plan.StartResidue = globals.Count == 0 ? 0 : globals.Min(e => e.Offset) % modulus;
        }

        var ramOffsets = FrameOffsets(functions, callers, f => f.FrameVariables.Where(v => !inZp.Contains(v)).Sum(v => v.Type.Size));
        int framesEnd = offset;
        foreach (var f in functions)
        {
            int at = offset + ramOffsets[f];
            foreach (var v in f.FrameVariables.Where(v => !inZp.Contains(v)))
            {
                plan.FrameVariables.Add((v, at));
                at += v.Type.Size;
            }
            framesEnd = Math.Max(framesEnd, at);
        }
        plan.TotalSize = framesEnd;
        plan.ZeroPageMap = DescribeZeroPage(program, functions, inZp);
        return plan;
    }

    /// <summary>
    /// The byte ranges of <paramref name="v"/> (offsets from its start) that one indexed read may
    /// cover, from the code's indexed operands (<paramref name="indexed"/>: their offsets into the
    /// variable). A split or struct array has one table per byte of the element, n bytes each; an
    /// operand belongs to the table whose start is nearest, and one below that start (<c>a-1,x</c>)
    /// widens the range down to it. A linear or indexed array is one table. Null if nothing reads the
    /// variable through an index, or a table is longer than a page.
    /// </summary>
    private static List<(int From, int To)>? Spans(VariableSymbol v, IReadOnlyDictionary<VariableSymbol, List<int>> indexed)
    {
        if (!v.Type.IsArray || v.Type.Size <= 1 || !indexed.TryGetValue(v, out var offsets)) return null;
        var (count, length) = v.Type.Linear || v.IsIndexed ? (1, v.Type.Size) : (v.Type.Size / v.Type.Length, v.Type.Length);
        if (length > 256) return null;
        var spans = new List<(int From, int To)>();
        for (int t = 0; t < count; t++) spans.Add((t * length, t * length + length - 1));
        foreach (int o in offsets)
        {
            int t = Math.Clamp((int)Math.Round((double)o / length), 0, count - 1);
            if (o < spans[t].From) spans[t] = (o, spans[t].To);
        }
        return spans;
    }

    /// <summary>How many spans cross a page with the variable at <paramref name="at"/> from a page start.</summary>
    private static int Crossings(List<(int From, int To)> spans, int at) =>
        spans.Count(s => (at + s.From) >> 8 != (at + s.To) >> 8);

    /// <summary>
    /// Gives each variable an offset from <paramref name="start"/>, counted as if the block began on
    /// a page (or a multiple of the largest alignment). Arrays with spans (<see cref="Spans"/>) and
    /// aligned variables first, in order, each at the first offset that is a multiple of its alignment
    /// where fewest of its spans cross a page; then the other variables in order, each at the top of the
    /// first gap left that holds it, or at the end. Without either, one after another. Returns the end.
    /// </summary>
    private static int PlaceByPage(List<VariableSymbol> vars, int start, List<(VariableSymbol Var, int Offset)> placed,
        Dictionary<VariableSymbol, List<(int From, int To)>> spans)
    {
        int end = start;
        var gaps = new List<(int From, int To)>();
        bool First(VariableSymbol v) => spans.ContainsKey(v) || v.Alignment > 1;
        foreach (var v in vars.Where(First))
        {
            var span = spans.GetValueOrDefault(v) ?? [];
            int step = v.Alignment;
            // Room for the reads below the array's start (a-2,x) in the same page.
            int first = end - Math.Min(0, span.Select(s => s.From).DefaultIfEmpty(0).Min());
            first = (first + step - 1) & -step;
            int best = first;
            for (int at = first; at < first + 256 && Crossings(span, best) > 0; at += step)
                if (Crossings(span, at) < Crossings(span, best)) best = at;
            if (best > end) gaps.Add((end, best));
            placed.Add((v, best));
            end = best + v.Type.Size;
        }
        foreach (var v in vars.Where(v => !First(v)))
        {
            int gap = gaps.FindIndex(g => g.To - g.From >= v.Type.Size);
            if (gap < 0)
            {
                placed.Add((v, end));
                end += v.Type.Size;
                continue;
            }
            placed.Add((v, gaps[gap].To - v.Type.Size));
            gaps[gap] = (gaps[gap].From, gaps[gap].To - v.Type.Size);
        }
        placed.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return end;
    }

    /// <summary>
    /// Each function's frame starts after the frames of all its callers. An interrupt handler can
    /// run at any moment, so its frame starts after every frame placed before it (main's, and
    /// earlier handlers'); the functions it calls follow it. Functions reached from two roots (main and
    /// a handler, or two handlers; spec 8.1) can be active while any function of those roots is, and
    /// functions reached from an address-taken function may be called from anywhere, so their frames
    /// come last and overlap nothing.
    /// </summary>
    private static Dictionary<FunctionSymbol, int> FrameOffsets(
        List<FunctionSymbol> callersFirst, Dictionary<FunctionSymbol, List<FunctionSymbol>> callers, Func<FunctionSymbol, int> size)
    {
        var offsets = new Dictionary<FunctionSymbol, int>();
        int end = 0;
        // A shared function's callees are reached from its roots too, so they are shared and follow it.
        bool PlacedLast(FunctionSymbol f) => f.Isolated || f.Roots.Count > 1;

        // A routine at a fixed address has no frame; it may be called from main and from handlers.
        // One with implementations (spec 8.5) follows its callers, so that the implementations do too;
        // a caller of another root comes later, but then the implementations have two roots and are placed last.
        int RoutineOffset(FunctionSymbol routine)
        {
            if (routine.Callees.Count == 0) return 0;
            var placed = callers[routine].Where(offsets.ContainsKey).ToList();
            return placed.Count == 0 ? 0 : placed.Max(c => offsets[c] + size(c));
        }

        foreach (var f in callersFirst.Where(f => !PlacedLast(f)).Concat(callersFirst.Where(PlacedLast)))
        {
            offsets[f] = (f.IsInterrupt && !f.IsPart) || PlacedLast(f) ? end
                : f.FixedAddress is not null ? RoutineOffset(f)
                : callers[f].Count == 0 ? 0
                : callers[f].Max(c => offsets[c] + size(c));
            end = Math.Max(end, offsets[f] + size(f));
        }
        return offsets;
    }

    private static string DescribeZeroPage(CheckedProgram program, List<FunctionSymbol> functions, HashSet<VariableSymbol> inZp)
    {
        var sb = new StringBuilder();
        foreach (var v in program.Globals.Concat(functions.SelectMany(f => f.FrameVariables))
                     .Where(inZp.Contains).OrderBy(v => v.Address).ThenBy(v => v.QualifiedName, StringComparer.Ordinal))
            sb.AppendLine($"${v.Address:X2}  {v.Type.Size}  {v.QualifiedName}");
        return sb.ToString();
    }
}
