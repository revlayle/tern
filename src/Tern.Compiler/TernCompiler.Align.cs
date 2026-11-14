using Tern.Compiler.Backend;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;

namespace Tern.Compiler;

/// <summary><c>@ align(n)</c> (spec 4): padding before aligned data in a file, and the report's count of it.</summary>
public static partial class TernCompiler
{
    /// <summary>
    /// Zero bytes before a global in the program's or an overlay's file, so that it starts at a multiple of
    /// its alignment and no <c>jmp (...)</c> vector in it starts at $xxFF; null when it needs neither.
    /// </summary>
    private static Padding? ImagePadding(VariableSymbol g)
    {
        if (g.Alignment == 1 && g.JumpVectorOffsets.Count == 0) return null;
        int n = g.Alignment;
        string reason = n == 1 ? $"jmp ({g.Name}) must not read a vector at $xxFF"
            : g.JumpVectorOffsets.Count == 0 ? $"'{g.Name}' is placed with '@ align({n})'"
            : $"'{g.Name}' is placed with '@ align({n})', and jmp ({g.Name}) must not read a vector at $xxFF";
        return new Padding(pc =>
        {
            for (int first = -pc & (n - 1), skip = first; skip < first + 512; skip += n)
                if (Assembler.AvoidPageEnd(pc + skip, g.JumpVectorOffsets) == 0) return skip;
            throw new CompileError(g.Pos,
                $"'{g.Name}' is used as jmp ({g.Name}+...), and at every multiple of ${n:X} a vector in it would start at $xxFF, where the 6502 reads it wrongly. Use a smaller alignment, or move the vector in the data");
        }, reason);
    }

    /// <summary>The bytes between an aligned global and whatever ends below it in the same memory: the padding its alignment costs.</summary>
    private static int AlignmentPadding(VariableSymbol g, RamPlan plan, AssembledCode code)
    {
        var block = plan.ZeroedGlobals.Concat(plan.NoinitGlobals).Concat(plan.FrameVariables).Select(e => e.Var).ToList();
        if (block.Contains(g))
            return g.Address - block.Where(v => v != g && v.Address + v.Type.Size <= g.Address)
                .Select(v => v.Address + v.Type.Size).Append(code.VariablesAfter).Max();
        // In a file: its section starts with the padding.
        var section = code.Sections.First(s => s.Name == g.Name && s.Address <= g.Address && g.Address < s.Address + Math.Max(1, s.Size));
        return g.Address - section.Address;
    }
}
