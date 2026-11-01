using Tern.Compiler.Syntax;
using Tern.Cpu;
using static Tern.Cpu.AddressingMode;

namespace Tern.Compiler.Semantics;

/// <summary>Assembly blocks: operands, addressing modes, and what each block reads and changes.</summary>
public sealed partial class Checker
{
    private void CheckAsm(AsmStmt block)
    {
        var labels = new Dictionary<string, AsmLabel>();
        foreach (var l in block.Lines.OfType<AsmLabel>())
            if (!labels.TryAdd(l.Name, l))
                throw new CompileError(l.Pos, $"Label '{l.Name}' is defined twice in this block");

        var effects = new AsmEffects();
        foreach (var ins in block.Lines.OfType<AsmInstruction>())
        {
            ins.Value = ins.Operand is null ? null : AsmOperand(ins.Operand, labels);
            ins.Mode = AsmMode(ins, ins.Value);
            AddEffects(ins, effects);
        }
        block.Effects = effects;
    }

    /// <summary>Evaluates an operand: a number, or an address (variable, function, label) plus a constant.</summary>
    private AsmValue AsmOperand(Expr e, Dictionary<string, AsmLabel> labels)
    {
        switch (e)
        {
            case AsmLabelExpr l:
                return labels.TryGetValue(l.Name, out var label)
                    ? new AsmValue(0, Label: label)
                    : throw new CompileError(l.Pos, $"Label '{l.Name}' is not defined in this asm block");

            case NameExpr n:
                switch (Lookup(n.Name))
                {
                    case null: throw new CompileError(n.Pos, $"'{n.Name}' is not declared");
                    case ConstSymbol c: return new AsmValue(c.Value);
                    case FunctionSymbol f: return new AsmValue(0, Function: f);
                    case VariableSymbol v:
                        if (!v.InMemory)
                            throw new CompileError(n.Pos, v.Kind == VariableKind.Parameter
                                ? $"'{n.Name}' is passed in register {v.Register}; use the register directly"
                                : $"'{n.Name}' lives in register {v.Register}; use the register directly");
                        n.Symbol = v;
                        v.Weight += Weight();
                        return new AsmValue(0, Variable: v);
                }
                throw new InvalidOperationException();

            case MemberExpr m:
            {
                if (m.Member is not ("lo" or "hi"))
                    throw new CompileError(m.Pos, $"Unknown member '.{m.Member}'; assembly only has .lo and .hi");
                var inner = AsmOperand(m.Target, labels);
                bool high = m.Member == "hi";
                if (inner.High is not null) throw new CompileError(m.Pos, $".{m.Member} of a value that is already one byte");
                if (inner.IsNumber) return new AsmValue(high ? (inner.Number >> 8) & 0xFF : inner.Number & 0xFF);
                return inner with { High = high };
            }

            case BinaryExpr b:
            {
                var l = AsmOperand(b.Left, labels);
                var r = AsmOperand(b.Right, labels);
                if (l.IsNumber && r.IsNumber) return new AsmValue(FoldAsm(b, l.Number, r.Number));
                if (!l.IsNumber && l.High is null && r.IsNumber && b.Op is TokenKind.Plus or TokenKind.Minus)
                    return l with { Number = l.Number + (b.Op == TokenKind.Plus ? r.Number : -r.Number) };
                if (!r.IsNumber && r.High is null && l.IsNumber && b.Op == TokenKind.Plus)
                    return r with { Number = r.Number + l.Number };
                throw new CompileError(b.Pos, "In assembly, an address can only have a constant added or subtracted, e.g. name+1 or (name+1).hi");
            }

            case UnaryExpr { Op: TokenKind.Minus or TokenKind.Plus or TokenKind.Tilde } u:
            {
                var inner = AsmOperand(u.Operand, labels);
                if (!inner.IsNumber) throw new CompileError(u.Pos, "Only numbers can be negated or inverted");
                return new AsmValue(u.Op switch { TokenKind.Minus => -inner.Number, TokenKind.Tilde => ~inner.Number, _ => inner.Number });
            }
        }

        // Literals, len(), sizeof(), casts of constants.
        CheckExpr(e);
        if (e.ConstValue is long value) return new AsmValue(value);
        throw new CompileError(e.Pos, "An assembly operand must be a constant, or the name of a variable, function or .label");
    }

    private static long FoldAsm(BinaryExpr b, long l, long r) => b.Op switch
    {
        TokenKind.Plus => l + r,
        TokenKind.Minus => l - r,
        TokenKind.Star => l * r,
        TokenKind.Slash or TokenKind.Percent when r == 0 => throw new CompileError(b.Pos, "Division by zero"),
        TokenKind.Slash => l / r,
        TokenKind.Percent => l % r,
        TokenKind.Amp => l & r,
        TokenKind.Pipe => l | r,
        TokenKind.Caret => l ^ r,
        TokenKind.Shl when r is >= 0 and < 32 => l << (int)r,
        TokenKind.Shr when r is >= 0 and < 32 => l >> (int)r,
        _ => throw new CompileError(b.Pos, "Assembly operands can only use + - * / % & | ^ << >>"),
    };

    /// <summary>
    /// Picks the addressing mode from how the operand is written. Memory operands get the absolute
    /// modes; the assembler uses the zero-page form when the address turns out to be below $100.
    /// </summary>
    private static AddressingMode AsmMode(AsmInstruction ins, AsmValue? v)
    {
        var m = ins.Mnemonic;
        string name = m.ToString().ToLowerInvariant();
        bool Has(AddressingMode mode) => InstructionSet.TryGet(m, mode, out _);

        switch (ins.Form)
        {
            case AsmForm.None:
                if (Has(Implied)) return Implied;
                if (Has(Accumulator)) return Accumulator;
                throw new CompileError(ins.Pos, $"'{name}' needs an operand");
            case AsmForm.Accumulator:
                return Accumulator;
            case AsmForm.Immediate:
                if (!Has(Immediate)) throw new CompileError(ins.Pos, $"'{name}' has no immediate mode (#)");
                if (v!.IsNumber && v.Number is < -128 or > 255)
                    throw new CompileError(ins.Operand!.Pos, $"#{v.Number} does not fit in a byte");
                if (!v.IsNumber && v.High is null)
                    throw new CompileError(ins.Operand!.Pos, "An address has two bytes: write #name.lo or #name.hi");
                return Immediate;
        }

        if (Has(Relative))
        {
            if (ins.Form != AsmForm.Direct || ins.Index is not null || v!.Label is null && v.Function is null || v.High is not null)
                throw new CompileError(ins.Pos, $"'{name}' needs a label, e.g. {name} .loop");
            return Relative;
        }
        if (v!.High is not null)
            throw new CompileError(ins.Operand!.Pos, ".lo and .hi give a byte value, not an address. Use it as an immediate: #name.lo");
        if (v.IsNumber && v.Number is < 0 or > 0xFFFF)
            throw new CompileError(ins.Operand!.Pos, $"Address {v.Number} is outside memory");

        switch (ins.Form)
        {
            case AsmForm.Direct when ins.Index is null:
                if (Has(Absolute)) return Absolute;
                throw new CompileError(ins.Pos, $"'{name}' takes no operand");
            case AsmForm.Direct:
            {
                var (abs, zp) = ins.Index == Register.X ? (AbsoluteX, ZeroPageX) : (AbsoluteY, ZeroPageY);
                string form = $"address,{ins.Index.ToString()!.ToLowerInvariant()}";
                if (Has(abs)) return abs;
                if (Has(zp))
                {
                    RequireZeroPage(ins, v, $"'{name} {form}'");
                    return zp;
                }
                throw new CompileError(ins.Pos, $"'{name}' has no {form} mode");
            }
            case AsmForm.Indirect:
                if (Has(Indirect)) return Indirect;
                throw new CompileError(ins.Pos, $"'{name}' has no (address) mode. To read through a pointer, write (pointer),y");
            default:
            {
                var mode = ins.Form == AsmForm.IndexedIndirect ? IndexedIndirect : IndirectIndexed;
                string form = mode == IndexedIndirect ? "(address,x)" : "(address),y";
                if (!Has(mode)) throw new CompileError(ins.Pos, $"'{name}' has no {form} mode");
                RequireZeroPage(ins, v, $"'{name} {form}'");
                return mode;
            }
        }
    }

    /// <summary>The operand of a zero-page-only mode must be a zero-page address. Variables are moved there.</summary>
    private static void RequireZeroPage(AsmInstruction ins, AsmValue v, string what)
    {
        var pos = ins.Operand!.Pos;
        if (v.IsNumber)
        {
            if (v.Number > 0xFF)
                throw new CompileError(pos, $"{what} needs a zero-page address ($00-$FF), not ${v.Number:X4}");
            return;
        }
        if (v.Variable is not { } var)
            throw new CompileError(pos, $"{what} needs a zero-page address, not a {(v.Function is not null ? "function" : "label")}");
        if (var.FixedAddress is int a && a + v.Number > 0xFF)
            throw new CompileError(pos, $"{what} needs a zero-page address, but '{var.Name}' is at ${a:X4}");
        var.AsmZeroPage = true;
    }

    private void AddEffects(AsmInstruction ins, AsmEffects effects)
    {
        var (reads, writes) = AsmEffects.Of(ins.Mnemonic, ins.Mode);
        var routine = ins.Mnemonic == Mnemonic.Jsr && ins.Value?.Function is { FixedAddress: not null } r ? r : null;
        if (routine is not null)
            foreach (var kept in routine.Keeps)
                writes &= ~(kept switch { Register.A => CpuState.A, Register.X => CpuState.X, _ => CpuState.Y });
        effects.Reads |= reads;
        effects.Writes |= writes;
        if (ins.Mnemonic == Mnemonic.Jsr) effects.Calls = true;

        // A register that holds a loop variable must survive the block.
        foreach (var reg in _loopRegisters)
        {
            if (ins.Mnemonic == Mnemonic.Jsr && routine?.Keeps.Contains(reg) != true)
                throw new CompileError(ins.Pos,
                    $"'jsr' calls a subroutine, which can change {reg}, the register of the loop variable. Remove 'using {reg}' from the loop, or call a routine that keeps {reg}");
            if (writes.HasFlag(reg == Register.X ? CpuState.X : CpuState.Y))
                throw new CompileError(ins.Pos,
                    $"This changes {reg}, which holds the loop variable. Remove 'using {reg}' from the loop, or leave {reg} unchanged");
        }

        var v = ins.Value;
        if (v?.Function is { } f && (f != _function || ins.Mnemonic == Mnemonic.Jsr))
            _function!.Callees.Add(f);
        if (v?.Variable is not { } variable) return;
        if (v.High is not null)
        {
            // #name.lo / #name.hi: the address is used, perhaps as a pointer.
            variable.AddressTaken = true;
            return;
        }
        if (AsmEffects.ReadsMemory(ins.Mnemonic, ins.Mode)) effects.VariablesRead.Add(variable);
        if (AsmEffects.WritesMemory(ins.Mnemonic, ins.Mode))
        {
            if (variable.IsData) throw new CompileError(ins.Operand!.Pos, $"'{variable.Name}' is data, which is read-only");
            if (variable.ReadOnly)
                throw new CompileError(ins.Operand!.Pos, $"Loop variable '{variable.Name}' cannot be changed inside the loop");
            effects.VariablesWritten.Add(variable);
        }
    }
}
