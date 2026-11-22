using System.Diagnostics;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using Tern.Cpu;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Backend;

/// <summary>
/// Debug-build checks of the generator's predictions against the code it emitted (review
/// code-quality-backend 10, decision 2026-11-16). A prediction such as <see cref="UsesY"/> restates on
/// the syntax tree what the generator will emit; each check compares it with the emitted instructions,
/// read through the instruction table, so a disagreement fails the test suite instead of producing
/// wrong code. Release builds leave the checks out.
/// </summary>
public sealed partial class CodeGen
{
    /// <summary>
    /// Fails if an instruction emitted from <paramref name="from"/> on (up to <paramref name="to"/>, when
    /// given) changes <paramref name="state"/>, which <paramref name="prediction"/> said would stay.
    /// </summary>
    [Conditional("DEBUG")]
    private void CheckUnchanged(int from, CpuState state, string prediction, int to = -1)
    {
        for (int i = from; i < (to < 0 ? _out.Count : to); i++)
            if (_out[i] is Instr ins && (Instructions.Effects(ins.Mnemonic, ins.Mode).Writes & state) != 0)
                throw new InvalidOperationException(
                    $"CodeGen predicted that {prediction}, but emitted '{ins.Mnemonic.ToString().ToLowerInvariant()}', which changes {Instructions.Describe(state)}");
    }

    /// <summary>Fails if the generator chose <paramref name="chosen"/> where <paramref name="prediction"/> said <paramref name="predicted"/>.</summary>
    [Conditional("DEBUG")]
    private static void CheckRegister(Register chosen, Register predicted, string prediction)
    {
        if (chosen != predicted)
            throw new InvalidOperationException($"CodeGen predicted that {prediction}, but it loaded into {chosen}");
    }

    /// <summary>
    /// <see cref="CarryClearAfter"/> said the code just emitted for a value ends with the carry clear: the
    /// last instruction that sets the carry must be the <c>asl</c> or <c>adc</c> the prediction is about.
    /// </summary>
    [Conditional("DEBUG")]
    private void CheckCarryClear(Expr e)
    {
        var expected = e is BinaryExpr { Op: TokenKind.Shl } ? Asl : Adc;
        var last = _out.OfType<Instr>().LastOrDefault(i => (Instructions.Effects(i.Mnemonic, i.Mode).Writes & CpuState.C) != 0);
        if (last?.Mnemonic != expected)
            throw new InvalidOperationException(
                $"CodeGen predicted the carry clear after '{expected.ToString().ToLowerInvariant()}', but the carry was last set by '{last?.Mnemonic.ToString().ToLowerInvariant() ?? "nothing"}'");
    }

    /// <summary>
    /// A variable the compiler chose to hold in a register (a loop counter, a variable held for a loop,
    /// a kept parameter) through the code of <paramref name="body"/>, emitted from <paramref name="from"/>
    /// to <paramref name="to"/>: <see cref="KeepsIndexRegistersFree"/>, <see cref="IndexesHeld"/> and
    /// <see cref="Served"/> said the body needs no call and leaves the register to the variable, so the
    /// code calls nothing, and when the body never assigns the variable, nothing in it changes the register.
    /// Nothing to check when <paramref name="v"/> is in memory.
    /// </summary>
    [Conditional("DEBUG")]
    private void CheckHeld(int from, int to, VariableSymbol? v, Stmt body)
    {
        if (v?.Register is not { } reg) return;
        for (int i = from; i < to; i++)
            if (_out[i] is Instr { Mnemonic: Jsr })
                throw new InvalidOperationException($"CodeGen held '{v.Name}' in {reg} for code that has no call, but emitted 'jsr'");
        if (!AssignsVar(body, v))
            CheckUnchanged(from, reg == Register.X ? CpuState.X : CpuState.Y, $"nothing changes {reg}, which holds '{v.Name}'", to);
    }

    /// <summary>
    /// A compiled function's <c>keeps</c> (spec 6.1) was checked on the generated code, statement by
    /// statement (<see cref="CheckKeeps"/>); the optimizer must not add a write to a kept register.
    /// Unlike the checks above, this one runs in every build (review V1 fixes, suggestion 2): it costs
    /// one pass over a function with <c>keeps</c>, and the callers' loops rely on the promise, so a
    /// broken one is an internal error, never wrong code.
    /// </summary>
    private void CheckKeptAfterOptimizer(FunctionSymbol f)
    {
        foreach (var reg in f.Keeps)
            foreach (var i in _out.OfType<Instr>())
                if (i.Mnemonic != Jsr && (Instructions.Effects(i.Mnemonic, i.Mode).Writes & State(reg)) != 0)
                    throw new InvalidOperationException($"The optimizer changed '{f.Name}', which keeps {reg}, so that '{i.Mnemonic.ToString().ToLowerInvariant()}' changes it");
    }
}
