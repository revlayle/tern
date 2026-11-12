using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Backend;

/// <summary>Pointers, calls and assembly blocks.</summary>
public sealed partial class CodeGen
{
    // ---------- Pointers: p[i] with (zp),y ----------

    private static bool IsPointerIndex(IndexExpr ix) => ix.Target.Type.IsPointer;

    /// <summary>
    /// The pointer variable itself, if (zp),y can use it directly. A pointer field (a view) is not
    /// known to be in zero page, so it is copied to a temporary.
    /// </summary>
    private static VariableSymbol? DirectPointer(Expr target) =>
        target is NameExpr { Symbol: { InMemory: true, AliasOf: null } v } && v.FixedAddress is null or <= 0xFE ? v : null;

    /// <summary>
    /// The zero-page pointer for p[i]: the variable, or a temporary holding a computed pointer
    /// (or one at a fixed address outside zero page). Release the temporary after use.
    /// </summary>
    private (VarOperand Base, VariableSymbol? Temp) PointerBase(Expr target)
    {
        if (DirectPointer(target) is { } v) return (Var(v), null);
        var temp = AcquireTemp(2, zeroPage: true);
        Gen16(target, new Loc(temp));
        return (Var(temp), temp);
    }

    /// <summary>Bytes per index step of p[i]: the element size, or 1 for an array field of a struct.</summary>
    private static int Stride(IndexExpr ix) => ix.Stride != 0 ? ix.Stride : ix.Type.Size;

    /// <summary>Y is just the index: a one-byte element, no field offset.</summary>
    private static bool PlainPointerIndex(IndexExpr ix) => ix.Type.Size == 1 && Stride(ix) == 1 && ix.Offset == 0;

    /// <summary>Loading Y for this element does not change A.</summary>
    private bool PointerIndexIsCheap(IndexExpr ix) =>
        ix.Index.ConstValue is not null
        || (PlainPointerIndex(ix) && (ix.Index is NameExpr { Symbol.Register: Register.Y } || Simple1(ix.Index) is not null));

    /// <summary>
    /// Puts the offset of byte <paramref name="byteIndex"/> of element p[i] in Y:
    /// field offset + i * stride, plus the distance to the second byte.
    /// </summary>
    private void LoadPointerIndex(IndexExpr ix, int byteIndex)
    {
        int stride = Stride(ix);
        int offset = ix.Offset;
        var index = ix.Index;
        // The second byte is one further on (iny), or in the high-byte half of a split array field.
        bool iny = byteIndex > 0 && ix.SecondByte == 1;
        if (byteIndex > 0 && !iny) offset += ix.SecondByte;
        if (PlainPointerIndex(ix) && index is NameExpr { Symbol.Register: Register.Y }) return;
        if (_reserved.Contains(Register.Y))
            throw new CompileError(ix.Pos, "p[i] needs the Y register, which holds a loop variable. Use 'using X' for that loop");
        if (index.ConstValue is long c)
        {
            Op(Ldy, Byte(offset + c * stride + (iny ? 1 : 0)));
            return;
        }
        if (stride == 1 && offset == 0 && Simple1(index) is { } op)
            Op(Ldy, op);
        else
        {
            GenA(index);
            if (stride == 2) Emit(Asl, Accumulator);
            if (offset != 0) { Emit(Clc); Op(Adc, Byte(offset)); }
            Emit(Tay);
        }
        if (iny) Emit(Iny);
    }

    /// <summary>After byte 0 of a 16-bit p[i], puts the offset of byte 1 in Y. A may change.</summary>
    private void NextPointerByte(IndexExpr ix)
    {
        if (ix.SecondByte == 1) Emit(Iny);
        else if (ix.Index.ConstValue is not null) LoadPointerIndex(ix, 1);
        else { Emit(Tya); Emit(Clc); Op(Adc, Byte(ix.SecondByte)); Emit(Tay); }
    }

    /// <summary>Reads (lda) or writes (sta) one byte of p[i]. For a write, the value is in A.</summary>
    private void PointerByte(IndexExpr ix, int byteIndex, Mnemonic m)
    {
        VariableSymbol? saved = null;
        if (m == Sta && (DirectPointer(ix.Target) is null || !PointerIndexIsCheap(ix)))
        {
            saved = AcquireTemp(1);
            Op(Sta, Var(saved));
        }
        var (p, temp) = PointerBase(ix.Target);
        LoadPointerIndex(ix, byteIndex);
        if (saved is not null)
        {
            Op(Lda, Var(saved));
            Release(saved);
        }
        Emit(m, IndirectIndexed, p);
        if (temp is not null) Release(temp);
    }

    /// <summary>
    /// <c>p[i] = v</c> in the spec 11 shape: Y first, then the value, so A never needs saving.
    /// Returns false if computing the value would change Y.
    /// </summary>
    private bool StorePointerByteIndexFirst(IndexExpr ix, Expr value)
    {
        if (ix.Type.Size != 1) return false;
        // dst[i] = src[i]: one Y load for both.
        var source = value is IndexExpr vix && IsPointerIndex(vix) && vix.Type.Size == 1 && SameIndex(vix.Index, ix.Index)
            && vix.Offset == ix.Offset && Stride(vix) == Stride(ix)
            ? DirectPointer(vix.Target) : null;
        if (source is null && UsesY(value)) return false;

        var (p, temp) = PointerBase(ix.Target);
        LoadPointerIndex(ix, 0);
        if (source is not null) Emit(Lda, IndirectIndexed, Var(source));
        else GenA(value);
        Emit(Sta, IndirectIndexed, p);
        if (temp is not null) Release(temp);
        return true;
    }

    private static bool SameIndex(Expr a, Expr b) =>
        (a.ConstValue is long ca && b.ConstValue == ca)
        || (a is NameExpr { Symbol: { } va } && b is NameExpr { Symbol: { } vb } && va == vb && !va.IsVolatile);

    /// <summary>True if computing <paramref name="e"/> may change Y (conservative).</summary>
    private bool UsesY(Expr e)
    {
        if (e.ConstValue is not null) return false;
        return e switch
        {
            NameExpr => false,
            IndexExpr ix when IsPointerIndex(ix) => true,
            // Arrays are indexed with X unless a loop holds X.
            IndexExpr ix => ix.Index.ConstValue is null && (_reserved.Contains(Register.X) || UsesY(ix.Index)),
            CallExpr call => call.AddressOf is null && call.FunctionAddress is null,
            UnaryExpr u => UsesY(u.Operand),
            BinaryExpr b => UsesY(b.Left) || UsesY(b.Right),
            MemberExpr m => UsesY(m.Target),
            CastExpr c => UsesY(c.Operand),
            _ => true,
        };
    }

    /// <summary>Reads a two-byte element p[i] into <paramref name="d"/>.</summary>
    private void LoadPointerWord(IndexExpr ix, Loc d)
    {
        if (ReadsVar(ix.Target, d.Var))
        {
            // Writing the low byte would change the pointer before the high byte is read.
            var copy = AcquireTemp(2);
            LoadPointerWord(ix, new Loc(copy));
            Op(Lda, Var(copy)); Op(Sta, d.Lo);
            Op(Lda, Var(copy, 1)); Op(Sta, d.Hi);
            Release(copy);
            return;
        }
        var (p, temp) = PointerBase(ix.Target);
        LoadPointerIndex(ix, 0);
        Emit(Lda, IndirectIndexed, p); Op(Sta, d.Lo);
        NextPointerByte(ix);
        Emit(Lda, IndirectIndexed, p); Op(Sta, d.Hi);
        if (temp is not null) Release(temp);
    }

    /// <summary>Writes a two-byte value to element p[i].</summary>
    private void StorePointerWord(IndexExpr ix, Expr value)
    {
        VariableSymbol? valueTemp = null;
        var pair = Simple2(value);
        if (pair is null)
        {
            valueTemp = AcquireTemp(2);
            Gen16(value, new Loc(valueTemp));
            pair = (Var(valueTemp), Var(valueTemp, 1));
        }
        var (p, temp) = PointerBase(ix.Target);
        LoadPointerIndex(ix, 0);
        Op(Lda, pair.Value.Lo); Emit(Sta, IndirectIndexed, p);
        NextPointerByte(ix);
        Op(Lda, pair.Value.Hi); Emit(Sta, IndirectIndexed, p);
        if (temp is not null) Release(temp);
        if (valueTemp is not null) Release(valueTemp);
    }

    // ---------- Calls ----------

    private void GenCall(CallExpr call)
    {
        var fn = call.Function!;
        int start = _out.Count;
        var temps = new List<VariableSymbol>();
        var early = new Dictionary<int, VariableSymbol>();

        // Calls inside arguments run before any parameter is written: the callee of the inner
        // call may share memory with this call's parameters. An argument with a call or a volatile
        // read that a later argument also has goes early too, so they run left to right (spec 5).
        var isEarly = new bool[call.Args.Count];
        bool effectAfter = false;
        for (int i = call.Args.Count - 1; i >= 0; i--)
        {
            bool effect = HasEffect(call.Args[i]);
            isEarly[i] = ContainsCall(call.Args[i]) || (effect && effectAfter);
            effectAfter |= effect;
        }
        for (int i = 0; i < call.Args.Count; i++)
        {
            if (!isEarly[i]) continue;
            var t = AcquireTemp(fn.Parameters[i].Type.Size);
            temps.Add(t);
            if (t.Type.Size == 1) { GenA(call.Args[i]); Op(Sta, Var(t)); }
            else Gen16(call.Args[i], new Loc(t));
            early[i] = t;
        }

        // Memory parameters: store straight into the callee's frame.
        for (int i = 0; i < call.Args.Count; i++)
        {
            var p = fn.Parameters[i];
            if (p.PassedIn is not null) continue;
            if (early.TryGetValue(i, out var t))
            {
                Op(Lda, Var(t)); Op(Sta, Var(p));
                if (p.Type.Size == 2) { Op(Lda, Var(t, 1)); Op(Sta, Var(p, 1)); }
            }
            else if (p.Type.Size == 1)
            {
                GenA(call.Args[i]);
                Op(Sta, Var(p));
            }
            else
                Gen16(call.Args[i], new Loc(p));
        }

        // Register parameters: compute what needs code first, then load the registers last.
        // A one-byte value for A is computed straight into A after the temporaries, since the
        // ldx/ldy that follow do not change A.
        var loads = new List<(Register Reg, Operand Op)>();
        Expr? intoA = null;
        for (int i = 0; i < call.Args.Count; i++)
        {
            var p = fn.Parameters[i];
            if (p.PassedIn is not { } reg) continue;
            var arg = call.Args[i];
            if (p.Type.Size == 1)
            {
                Operand op;
                if (early.TryGetValue(i, out var t)) op = Var(t);
                else if (Simple1(arg) is { } s) op = s;
                else if (reg == Register.A)
                {
                    intoA = arg;
                    continue;
                }
                else
                {
                    var nt = AcquireTemp(1);
                    temps.Add(nt);
                    GenA(arg);
                    Op(Sta, Var(nt));
                    op = Var(nt);
                }
                loads.Add((reg, op));
            }
            else
            {
                (Operand Lo, Operand Hi) pair;
                if (early.TryGetValue(i, out var t)) pair = (Var(t), Var(t, 1));
                else if (Simple2(arg) is { } s) pair = s;
                else
                {
                    var nt = AcquireTemp(2);
                    temps.Add(nt);
                    Gen16(arg, new Loc(nt));
                    pair = (Var(nt), Var(nt, 1));
                }
                var (loReg, hiReg) = reg switch
                {
                    Register.AX => (Register.A, Register.X),
                    Register.AY => (Register.A, Register.Y),
                    _ => (Register.X, Register.Y),
                };
                loads.Add((loReg, pair.Lo));
                loads.Add((hiReg, pair.Hi));
            }
        }
        foreach (var (reg, value) in fn.FixedInputs)
            if (reg != Register.Carry) loads.Add((reg, Byte(value)));
        if (intoA is not null) GenA(intoA);
        foreach (var (reg, op) in loads.OrderBy(l => l.Reg == Register.A ? 1 : 0))
            Op(reg switch { Register.A => Lda, Register.X => Ldx, _ => Ldy }, op);
        // A fixed carry goes right before the jsr, after everything that could change it.
        foreach (var (reg, value) in fn.FixedInputs)
            if (reg == Register.Carry) Emit(value != 0 ? Sec : Clc);

        if (fn.IsInline)
        {
            GenInline(fn);
            MoveInlineFrame(fn, start);
        }
        else Emit(Jsr, Absolute, fn.FixedAddress is int at ? new AddrOperand(at) : new LabelOperand(_functionLabels[fn]));
        foreach (var t in temps) Release(t);
    }

    // ---------- Assembly ----------

    /// <summary>Emits an asm block as written. The checker has made sure it leaves loop registers alone.</summary>
    private void GenAsm(AsmStmt block)
    {
        var fx = block.Effects!;
        string Names(IEnumerable<VariableSymbol> vs) => string.Join(" ", vs.Select(v => v.QualifiedName).Order(StringComparer.Ordinal));
        var summary = $"asm: reads {AsmEffects.Describe(fx.Reads)}, changes {AsmEffects.Describe(fx.Writes)}";
        if (fx.VariablesRead.Count > 0) summary += $", reads {Names(fx.VariablesRead)}";
        if (fx.VariablesWritten.Count > 0) summary += $", writes {Names(fx.VariablesWritten)}";
        if (fx.Calls) summary += ", calls subroutines";
        _out.Add(new Comment(summary));

        // Labels a generator emitted have a '#' in their name, to keep them apart from the block's own.
        var labels = block.Lines.OfType<AsmLabel>().ToDictionary(l => l, l => NewLabel(l.Name[1..].Replace('#', '_')));
        SourcePos? call = null;
        foreach (var line in block.Lines)
        {
            if (line.CalledFrom is { } from)
            {
                // A generated line: the generator call once, then the emit line of each instruction (spec 4.1).
                if (from != call) SourceAgain(from);
                SourceAgain(line.Pos, from.File == _mainFile ? $"generated by line {from.Line}" : $"generated by {CompileTime.Interpreter.Where(from)}");
            }
            else Source(line);
            call = line.CalledFrom;
            switch (line)
            {
                case AsmLabel l:
                    Place(labels[l]);
                    break;
                case AsmInstruction ins:
                    _out.Add(new Instr(ins.Mnemonic, ins.Mode, AsmOperandOf(ins, labels), ins.Pos) { CalledFrom = ins.CalledFrom });
                    break;
            }
        }
    }

    /// <summary>A source line in the listing even when the line before showed it too; <paramref name="via"/> follows it.</summary>
    private void SourceAgain(SourcePos pos, string? via = null)
    {
        _lastSource = default;
        Source(pos);
        if (via is not null && _out.Count > 0 && _out[^1] is SourceLine shown) _out[^1] = shown with { Via = via };
    }

    private Operand? AsmOperandOf(AsmInstruction ins, Dictionary<AsmLabel, Label> labels)
    {
        if (ins.Value is not { } v) return null;
        int offset = (int)v.Number;
        Label LabelOf() => v.Function is { } f ? _functionLabels[f] : labels[v.Label!];

        if (v.High is bool high)
            return v.Variable is { } hv ? new ImmVarByte(hv, high, offset) : new ImmLabelByte(LabelOf(), high, offset);
        if (v.IsNumber) return ins.Mode == Immediate ? new Imm(offset) : new AddrOperand(offset);
        if (v.Variable is { } variable) return new VarOperand(variable, offset);
        return new LabelOperand(LabelOf(), offset);
    }
}
