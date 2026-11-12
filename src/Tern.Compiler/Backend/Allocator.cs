using System.Text;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;

namespace Tern.Compiler.Backend;

/// <summary>Where variables go after the program: offsets from the end of the program.</summary>
public sealed class RamPlan
{
    /// <summary>Uninitialized globals, zeroed at startup. They come first.</summary>
    public List<(VariableSymbol Var, int Offset)> ZeroedGlobals { get; } = [];
    public int ZeroedSize { get; set; }
    /// <summary>
    /// Arrays among the globals that the code reads through an index register, placed so that no such
    /// read crosses a page when the block starts at <see cref="PageOffset"/> in a page (M18 F12).
    /// </summary>
    public List<VariableSymbol> PageArrays { get; } = [];
    /// <summary>With <see cref="PageArrays"/>: where in its page the block must start (its low byte).</summary>
    public int PageOffset { get; set; }
    /// <summary>Globals declared <c>noinit</c>, after the zeroed ones: not cleared at startup.</summary>
    public List<(VariableSymbol Var, int Offset)> NoinitGlobals { get; } = [];
    /// <summary>Function frames (parameters, locals, temporaries), after the globals.</summary>
    public List<(VariableSymbol Var, int Offset)> FrameVariables { get; } = [];
    public int TotalSize { get; set; }
    /// <summary>Globals with an initial value that live in the program image.</summary>
    public List<VariableSymbol> ImageGlobals { get; } = [];
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

        var placeable = program.Globals.Where(g => g.FixedAddress is null && g.Region is null && !g.IsOverlayImage)
            .Concat(functions.SelectMany(f => f.FrameVariables))
            .ToList();
        var inZp = new HashSet<VariableSymbol>();

        // The free zero-page bytes in address order; a variable must lie in consecutive ones.
        var zeroPage = layout.ZeroPage.OrderBy(r => r.First).SelectMany(r => Enumerable.Range(r.First, r.Size)).ToList();
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
            .Where(v => !v.ForcedZeroPage && !v.Type.IsArray && !v.Type.IsStruct && v.Weight > 0)
            .OrderByDescending(v => v.Weight).ThenBy(v => v.QualifiedName, StringComparer.Ordinal);
        foreach (var v in candidates)
        {
            inZp.Add(v);
            if (ZpNeeded() > zpCapacity) inZp.Remove(v);
        }

        var plan = new RamPlan();

        // Zero-page addresses: globals first, then the overlaid frames.
        int ZeroPageAddress(int index, VariableSymbol v)
        {
            int address = zeroPage[index];
            if (zeroPage[index + v.Type.Size - 1] - address != v.Type.Size - 1)
                throw new CompileError(v.Pos,
                    $"'{v.QualifiedName}' ({v.Type.Size} bytes) would cross the end of a zero-page range at ${zeroPage[index + v.Type.Size - 1]:X2}. " +
                    $"Zero-page ranges: {MemoryLayout.ListZeroPage(layout.ZeroPage)}. Join the ranges or reorder the variables");
            return address;
        }
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

        // Variables and data placed in a region, in the order they are declared: zeroed variables first
        // (one block to clear at startup), then noinit ones, then data.
        foreach (var (name, range) in layout.Regions)
        {
            var inRegion = program.Globals.Where(g => g.Region == name).ToList();
            int at = range.First;
            void PlaceAll(IEnumerable<VariableSymbol> vars)
            {
                foreach (var g in vars)
                {
                    if (at + g.Type.Size - 1 > range.Last)
                        throw new CompileError(g.Pos,
                            $"Region '{name}' ({range}, {range.Size} bytes) is full: '{g.Name}' needs {g.Type.Size} bytes at ${at:X4}, " +
                            $"{Math.Max(0, range.Last + 1 - at)} are left. Place it elsewhere or make the region larger");
                    g.Address = at;
                    at += g.Type.Size;
                }
            }
            var zeroed = inRegion.Where(g => !g.IsData && !g.NoInit).ToList();
            PlaceAll(zeroed);
            if (at > range.First) plan.RegionClears.Add(new AddressRange(range.First, at - 1));
            PlaceAll(inRegion.Where(g => !g.IsData && g.NoInit));
            var data = inRegion.Where(g => g.IsData).ToList();
            PlaceAll(data);
            plan.FixedImageGlobals.AddRange(data);
            if (at > range.First) plan.RegionUse[name] = new AddressRange(range.First, at - 1);
        }
        plan.FixedImageGlobals.AddRange(program.Globals.Where(g => g.FixedAddress is not null && g.IsData));
        plan.OverlayGlobals.AddRange(program.Globals.Where(g => g.IsOverlayImage));

        // RAM after the program: zeroed globals, noinit globals, then the overlaid frames. Arrays read
        // through an index register are placed so that no such read crosses a page, counting the
        // offsets from where the block starts in its page (F12).
        var ram = program.Globals.Where(g => g.FixedAddress is null && g.Region is null && !g.IsOverlayImage && !inZp.Contains(g)).ToList();
        plan.ImageGlobals.AddRange(ram.Where(g => g.InitialBytes is not null));
        var spans = new Dictionary<VariableSymbol, List<(int From, int To)>>();
        if (byPage && indexed is not null)
            foreach (var g in ram.Where(g => g.InitialBytes is null))
                if (Spans(g, indexed) is { } list) spans[g] = list;
        int offset = PlaceByPage(ram.Where(g => g.InitialBytes is null && !g.NoInit).ToList(), 0, plan.ZeroedGlobals, spans);
        plan.ZeroedSize = offset;
        offset = PlaceByPage(ram.Where(g => g.NoInit).ToList(), offset, plan.NoinitGlobals, spans);
        plan.PageArrays.AddRange(spans.Keys.OrderBy(v => plan.ZeroedGlobals.Concat(plan.NoinitGlobals).First(e => e.Var == v).Offset));
        // Space before the first variable is not needed: the block starts that far into a page instead.
        var globals = plan.ZeroedGlobals.Concat(plan.NoinitGlobals).ToList();
        if (spans.Count > 0 && globals.Min(e => e.Offset) is int lead and > 0)
        {
            foreach (var list in new[] { plan.ZeroedGlobals, plan.NoinitGlobals })
                for (int k = 0; k < list.Count; k++) list[k] = (list[k].Var, list[k].Offset - lead);
            plan.ZeroedSize -= lead;
            offset -= lead;
        }
        if (spans.Count > 0) plan.PageOffset = globals.Count == 0 ? 0 : globals.Min(e => e.Offset) & 0xFF;

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
    /// a page. Arrays with spans (<see cref="Spans"/>) first, in order, each at the first offset where
    /// fewest of its spans cross a page; then the other variables in order, each at the top of the
    /// first gap left that holds it, or at the end. Without spans, one after another. Returns the end.
    /// </summary>
    private static int PlaceByPage(List<VariableSymbol> vars, int start, List<(VariableSymbol Var, int Offset)> placed,
        Dictionary<VariableSymbol, List<(int From, int To)>> spans)
    {
        int end = start;
        var gaps = new List<(int From, int To)>();
        foreach (var v in vars.Where(spans.ContainsKey))
        {
            var span = spans[v];
            // Room for the reads below the array's start (a-2,x) in the same page.
            int first = end - Math.Min(0, span.Min(s => s.From));
            int best = first;
            for (int at = first; at < first + 256 && Crossings(span, best) > 0; at++)
                if (Crossings(span, at) < Crossings(span, best)) best = at;
            if (best > end) gaps.Add((end, best));
            placed.Add((v, best));
            end = best + v.Type.Size;
        }
        foreach (var v in vars.Where(v => !spans.ContainsKey(v)))
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
    /// earlier handlers'); the functions it calls follow it. Functions reached from an address-taken
    /// function may be called from anywhere, so their frames come last and overlap nothing.
    /// </summary>
    private static Dictionary<FunctionSymbol, int> FrameOffsets(
        List<FunctionSymbol> callersFirst, Dictionary<FunctionSymbol, List<FunctionSymbol>> callers, Func<FunctionSymbol, int> size)
    {
        var offsets = new Dictionary<FunctionSymbol, int>();
        int end = 0;
        foreach (var f in callersFirst.Where(f => !f.Isolated).Concat(callersFirst.Where(f => f.Isolated)))
        {
            // A routine at a fixed address has no frame; it may be called from main and from handlers.
            // So may a function with no memory (checked by Checker.CheckSharedFunctions), whose callers
            // in a later root are not placed yet.
            offsets[f] = (f.IsInterrupt && !f.IsPart) || f.Isolated ? end
                : f.FixedAddress is not null || f.Roots.Count > 1 || callers[f].Count == 0 ? 0
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
