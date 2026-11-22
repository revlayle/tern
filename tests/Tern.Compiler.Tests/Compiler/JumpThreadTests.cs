using Tern.Compiler.Backend;
using Tern.Compiler.Semantics;
using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Tests;

/// <summary>
/// V1 fixes, small shapes: a jump that lands on a <c>jmp</c> goes straight to its target, also when
/// the code between them is only removed by a later pass; a ring of <c>jmp</c>s is left alone.
/// Run on hand-written code, as <see cref="DeadLoadTests"/>. Spec 13 (compiler design).
/// </summary>
public class JumpThreadTests
{
    private static readonly VariableSymbol X = new("x", default, TernType.U8, VariableKind.Global, null);

    private static Instr Jmp(Label l) => new(Mnemonic.Jmp, Absolute, new LabelOperand(l));

    private static string Optimize(params AsmItem[] code)
    {
        var list = code.ToList();
        Optimizer.Run(list, new FunctionSymbol("f", default), InstructionSet.Nmos6502);
        return string.Join("|", list.Select(i => i switch
        {
            Instr n => $"{n.Mnemonic.ToString().ToLowerInvariant()} {n.Operand}".Trim(),
            LabelDef d => d.Label.Name + ":",
            _ => "",
        }).Where(s => s != ""));
    }

    [Fact]
    public void A_jmp_left_in_front_of_a_jmp_by_another_pass_is_threaded()
    {
        var one = new Label("one");
        var two = new Label("two");
        // At "one", A already holds 1: the load goes, and "jmp one" then lands on "jmp two", which is
        // the next line, so it goes too; the "jmp two" after "one:" can no longer be reached.
        var code = Optimize(
            new Instr(Lda, Immediate, new Imm(1)), Jmp(one),
            new LabelDef(two), new Instr(Sta, Absolute, new VarOperand(X)), new Instr(Rts),
            new LabelDef(one), new Instr(Lda, Immediate, new Imm(1)), Jmp(two));
        Assert.Equal("lda #$01|two:|sta x|rts|one:", code);
    }

    [Fact]
    public void A_ring_of_jmps_is_entered_where_it_starts()
    {
        var a = new Label("a");
        var b = new Label("b");
        var c = new Label("c");
        // a -> b -> c -> b: "jmp a" goes to b, where the ring starts, and another pass keeps it there,
        // so the loop of passes ends. The jumps that then go to the next line are removed, and what is
        // left is the same endless loop: c, a and b are one place, and "jmp c" jumps to it.
        var code = Optimize(
            Jmp(a),
            new LabelDef(c), Jmp(b),
            new LabelDef(a), Jmp(b),
            new LabelDef(b), Jmp(c));
        Assert.Equal("c:|a:|b:|jmp c", code);
    }
}
