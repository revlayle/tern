using Tern.Compiler.Semantics;
using Tern.Cpu;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Backend;

/// <summary>
/// Which of A, X and Y a function may change, counting everything it calls. Used for the
/// <c>keeps</c> list of a binary's interface module (spec 12.3), so it errs towards "changed":
/// an instruction or call the analysis does not understand changes all three.
/// </summary>
public sealed class RegisterUse(CodeGen gen, IEnumerable<FunctionSymbol> functions)
{
    private const CpuState Registers = CpuState.A | CpuState.X | CpuState.Y;
    private readonly Dictionary<FunctionSymbol, CpuState> _known = [];
    private readonly Dictionary<int, FunctionSymbol> _routines =
        functions.Where(f => f.FixedAddress is not null).ToDictionary(f => f.FixedAddress!.Value);

    public CpuState Changed(FunctionSymbol f)
    {
        if (_known.TryGetValue(f, out var known)) return known;
        // A routine at a fixed address: the compiler cannot see inside; it changes what it does not promise to keep.
        if (f.FixedAddress is not null)
            return _known[f] = Registers & ~Kept(f);
        var changed = CpuState.None;
        foreach (var item in gen.FunctionItems[f])
        {
            if (item is not Instr i) continue;
            if (i.Mnemonic is Jsr or Jmp)
            {
                changed |= Target(i, f);
                continue;
            }
            changed |= Instructions.Effects(i.Mnemonic, i.Mode).Writes & Registers;
        }
        return _known[f] = changed;
    }

    private static CpuState Kept(FunctionSymbol routine) =>
        (routine.Keeps.Contains(Register.A) ? CpuState.A : 0)
        | (routine.Keeps.Contains(Register.X) ? CpuState.X : 0)
        | (routine.Keeps.Contains(Register.Y) ? CpuState.Y : 0);

    /// <summary>What a jsr or jmp does to the registers: what its target changes. A jump inside the function changes nothing.</summary>
    private CpuState Target(Instr i, FunctionSymbol caller)
    {
        switch (i.Operand)
        {
            case LabelOperand l:
                var callee = gen.FunctionOf(l.Label);
                if (callee is null) return CpuState.None;     // a label inside the function
                return callee == caller ? CpuState.None : Changed(callee);
            case AddrOperand a:
                return _routines.TryGetValue(a.Address, out var routine) ? Changed(routine) : Registers;
            default:
                return Registers;                              // jmp (vector), or an address we cannot name
        }
    }
}
