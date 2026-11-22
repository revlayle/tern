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
        // After the other changes, which may use what the stores tell them (A holds x after sta x).
        if (startsProgram && RemoveStoresOfStartupZeros(code)) Improve(code, function, instructions);
    }

    private static void Improve(List<AsmItem> code, FunctionSymbol function, InstructionSet instructions)
    {
        while (RemoveRedundant(code, function) | FoldShiftTests(code, function) | UseOperandInA(code, function)
            | RemoveDeadLoads(code, function) | ReuseIncDecFlags(code, function) | RemoveDeadStores(code, function) | ReadCopiesFromSource(code, function, instructions)
            | SinkStoresFromLoops(code, function) | RemoveJumpsToNext(code) | GroupConstantStores(code)) { }
    }
}
