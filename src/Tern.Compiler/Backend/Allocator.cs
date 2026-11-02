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
}

/// <summary>
/// Assigns memory to variables. Functions that can never run at the same time share memory:
/// each function's frame is placed after the frames of everything that calls it, so frames
/// overlap only between functions on different call paths.
/// </summary>
public static class Allocator
{
    public static RamPlan Allocate(CheckedProgram program, Platform platform)
    {
        var functions = program.Functions;
        var callers = functions.ToDictionary(f => f, _ => new List<FunctionSymbol>());
        foreach (var f in functions)
            foreach (var callee in f.Callees)
                if (callers.ContainsKey(callee)) callers[callee].Add(f);

        foreach (var g in program.Globals)
            if (g.FixedAddress is int a) g.Address = a;

        var placeable = program.Globals.Where(g => g.FixedAddress is null)
            .Concat(functions.SelectMany(f => f.FrameVariables))
            .ToList();
        var inZp = new HashSet<VariableSymbol>();

        int zpCapacity = platform.ZeroPage.Last - platform.ZeroPage.First + 1;

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
                $"Zero page is full: {what} need {ZpNeeded()} bytes, {zpCapacity} are free. Users: {users}");
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
        int zp = platform.ZeroPage.First;
        foreach (var g in program.Globals.Where(inZp.Contains))
        {
            g.Address = zp;
            zp += g.Type.Size;
            plan.ZeroPageGlobals.Add(g);
        }
        var zpOffsets = FrameOffsets(functions, callers, f => f.FrameVariables.Where(inZp.Contains).Sum(v => v.Type.Size));
        foreach (var f in functions)
        {
            int at = zp + zpOffsets[f];
            foreach (var v in f.FrameVariables.Where(inZp.Contains))
            {
                v.Address = at;
                at += v.Type.Size;
            }
        }

        // RAM after the program: zeroed globals, noinit globals, then the overlaid frames.
        int offset = 0;
        var ram = program.Globals.Where(g => g.FixedAddress is null && !inZp.Contains(g)).ToList();
        plan.ImageGlobals.AddRange(ram.Where(g => g.InitialBytes is not null));
        foreach (var g in ram.Where(g => g.InitialBytes is null && !g.NoInit))
        {
            plan.ZeroedGlobals.Add((g, offset));
            offset += g.Type.Size;
        }
        plan.ZeroedSize = offset;
        foreach (var g in ram.Where(g => g.NoInit))
        {
            plan.NoinitGlobals.Add((g, offset));
            offset += g.Type.Size;
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
    /// Each function's frame starts after the frames of all its callers. An interrupt handler can
    /// run at any moment, so its frame starts after every frame placed before it (main's, and
    /// earlier handlers'); the functions it calls follow it.
    /// </summary>
    private static Dictionary<FunctionSymbol, int> FrameOffsets(
        List<FunctionSymbol> callersFirst, Dictionary<FunctionSymbol, List<FunctionSymbol>> callers, Func<FunctionSymbol, int> size)
    {
        var offsets = new Dictionary<FunctionSymbol, int>();
        int end = 0;
        foreach (var f in callersFirst)
        {
            // A routine at a fixed address has no frame; it may be called from main and from handlers.
            offsets[f] = f.IsInterrupt ? end
                : f.FixedAddress is not null || callers[f].Count == 0 ? 0
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
