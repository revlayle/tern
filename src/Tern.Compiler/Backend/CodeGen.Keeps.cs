using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using Tern.Cpu;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Backend;

/// <summary>
/// <c>keeps</c> on a compiled <c>fn</c> or <c>inline fn</c> (spec 6.1): the body's code leaves the
/// registers alone. A kept X or Y is reserved for the body, as for a <c>for ... using X</c> counter,
/// so the body indexes with the other register; after each statement its code is checked, so a
/// statement that needs the register, or calls a function that does not keep it, is an error at
/// that statement. Nothing is saved and restored.
/// </summary>
public sealed partial class CodeGen
{
    /// <summary>Starts checking the registers <paramref name="f"/> keeps; returns how many, for <see cref="EndKeeping"/>.</summary>
    private int StartKeeping(FunctionSymbol f)
    {
        // A is not an index register: nothing chooses it, so it is not reserved; the check finds its uses.
        // X or Y may be reserved already, by the loop around an inline call.
        foreach (var reg in f.Keeps.Order())
            _keeping.Add((f, reg, reg != Register.A && _reserved.Add(reg)));
        return f.Keeps.Count;
    }

    private void EndKeeping(int count)
    {
        for (int k = 0; k < count; k++)
        {
            var (_, reg, reserved) = _keeping[^1];
            _keeping.RemoveAt(_keeping.Count - 1);
            if (reserved) _reserved.Remove(reg);
        }
    }

    /// <summary>
    /// The code of <paramref name="stmt"/>, emitted from <paramref name="from"/>, must not change a
    /// register a function being generated keeps, nor call a function that may change it.
    /// </summary>
    private void CheckKeeps(Stmt stmt, int from)
    {
        for (int i = from; i < _out.Count; i++)
        {
            if (_out[i] is not Instr ins) continue;
            foreach (var (f, reg, _) in _keeping)
            {
                if (ins.Mnemonic == Jsr)
                {
                    var callee = Callee(ins);
                    if (callee?.Keeps.Contains(reg) == true) continue;
                    throw new CompileError(stmt.Pos, callee is null
                        ? $"This calls a routine that can change {reg}, which '{f.Name}' keeps. Remove 'keeps {reg}' from '{f.Name}', or leave out the call"
                        : $"'{callee.Name}' can change {reg}, which '{f.Name}' keeps. Write 'keeps {reg}' on '{callee.Name}' if it leaves {reg} alone, or remove 'keeps {reg}' from '{f.Name}'");
                }
                if ((Instructions.Effects(ins.Mnemonic, ins.Mode).Writes & State(reg)) != 0)
                    throw new CompileError(stmt.Pos,
                        $"This changes {reg}, which '{f.Name}' keeps ('{ins.Mnemonic.ToString().ToLowerInvariant()}'). Change the statement so that it leaves {reg} alone, or remove 'keeps {reg}' from '{f.Name}'");
            }
        }
    }

    /// <summary>The function being generated that keeps <paramref name="reg"/>, or null (then a loop variable holds it).</summary>
    private FunctionSymbol? KeptBy(Register reg) => _keeping.FirstOrDefault(k => k.Register == reg).Function;

    private static CpuState State(Register reg) => reg switch { Register.A => CpuState.A, Register.X => CpuState.X, _ => CpuState.Y };

    /// <summary>The function a <c>jsr</c> calls: one of the program's, or a routine at a fixed address. Null if it is neither.</summary>
    private FunctionSymbol? Callee(Instr jsr) => jsr.Operand switch
    {
        LabelOperand l => FunctionOf(l.Label),
        AddrOperand a => _program.Functions.FirstOrDefault(f => f.FixedAddress == a.Address),
        _ => null,
    };
}
