using Tern.Compiler.Semantics;
using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Backend;

/// <summary>
/// Improves the generated code of one function without changing what it does (roadmap M7):
/// removes unreachable instructions, and loads and stores of values a register already holds.
/// Instructions from assembly blocks are never changed, but what they change is tracked like the
/// compiler's own (a call, or a write to a fixed address, forgets memory); volatile memory (spec 4) is never
/// cached. Labels in a function's code are only used inside it (assembly labels are local to their
/// block), so the whole function can be analysed at once.
/// </summary>
public static partial class Optimizer
{
    /// <param name="instructions">The target CPU's instructions: a change uses only modes it has.</param>
    /// <param name="startsProgram">The function is main, entered once just after the startup code cleared memory.</param>
    public static void Run(List<AsmItem> code, FunctionSymbol function, InstructionSet instructions, bool startsProgram = false)
    {
        ThreadJumps(code);
        RemoveUnreachable(code);
        Improve(code, function, instructions);
        MoveParameterToIndex(code, function, instructions);
        // After the other changes, which may use what the stores tell them (A holds x after sta x).
        if (startsProgram && RemoveStoresOfStartupZeros(code)) Improve(code, function, instructions);
    }

    /// <summary>
    /// A jump can land on a jmp once the code between them is removed (V1 fixes): it goes straight on,
    /// and what that leaves unreachable goes. True if a jump changed.
    /// </summary>
    private static bool ThreadJumpsAgain(List<AsmItem> code)
    {
        if (!ThreadJumps(code)) return false;
        RemoveUnreachable(code);
        return true;
    }

    /// <summary>
    /// A one-byte parameter passed in A is stored on entry (<c>sta v</c>). Where A is needed before it is
    /// read, a copy in X or Y (<c>tax</c> after the store) lets the other passes read it from there:
    /// <c>lda v</c> becomes <c>txa</c>, <c>ldx v</c> goes, and then the store too when nothing reads v's
    /// memory any more (V1 fixes: a register parameter used once is not stored to memory). The copy is
    /// kept only when no instruction uses v's memory then (so <c>tax</c> replaced the store and each read
    /// got shorter), and the code is smaller, or as small and faster, counting each instruction once.
    /// </summary>
    private static void MoveParameterToIndex(List<AsmItem> code, FunctionSymbol function, InstructionSet instructions)
    {
        int k = code.FindIndex(i => i is Instr);
        if (k < 0 || code[k] is not Instr { Mnemonic: Sta, AsmPos: null, Operand: VarOperand { Offset: 0, Variable: var v } }
            || v.Kind != VariableKind.Parameter || v.PassedIn != Register.A || v.Type.Size != 1 || !function.Parameters.Contains(v))
            return;
        var live = RegistersLive(code, function, Positions(code))[k + 1];
        var best = code;
        foreach (var (reg, transfer) in new[] { (CpuState.X, Tax), (CpuState.Y, Tay) })
        {
            // live counts a register the function keeps as read at rts (Returned), so it is never chosen.
            if ((live & reg) != 0) continue;
            var trial = code.ToList();
            trial.Insert(k + 1, new Instr(transfer));
            Improve(trial, function, instructions);
            if (trial.Any(i => i is Instr { Operand: VarOperand { Variable: var u } } && u == v)) continue;
            if (Cost(trial, instructions).CompareTo(Cost(best, instructions)) < 0) best = trial;
        }
        if (best == code) return;
        code.Clear();
        code.AddRange(best);
    }

    /// <summary>Bytes, then cycles, of the instructions, each counted once (no branch taken, no page crossed).</summary>
    private static (int Bytes, int Cycles) Cost(List<AsmItem> code, InstructionSet instructions)
    {
        int bytes = 0, cycles = 0;
        foreach (var i in code.OfType<Instr>())
            if (instructions.TryGet(i.Mnemonic, i.Mode, out var info)) { bytes += info.Size; cycles += info.Cycles; }
        return (bytes, cycles);
    }

    private static void Improve(List<AsmItem> code, FunctionSymbol function, InstructionSet instructions)
    {
        while (RemoveRedundant(code, function) | FoldShiftTests(code, function) | UseOperandInA(code, function)
            | RemoveDeadLoads(code, function) | RemoveDeadTransfers(code, function) | ReuseIncDecFlags(code, function) | TestCarryDirectly(code, function) | RemoveDeadStores(code, function) | ReadCopiesFromSource(code, function, instructions)
            | SinkStoresFromLoops(code, function) | RemoveJumpsToNext(code) | GroupConstantStores(code)
            | ThreadJumpsAgain(code)) { }
    }
}
