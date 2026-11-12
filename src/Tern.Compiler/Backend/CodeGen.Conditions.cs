using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Backend;

/// <summary>Conditions and comparisons, compiled to jumps.</summary>
public sealed partial class CodeGen
{
    // ---------- Conditions ----------

    /// <summary>Jumps to <paramref name="target"/> if the condition equals <paramref name="jumpIf"/>; otherwise falls through.</summary>
    private void GenCond(Expr e, Label target, bool jumpIf)
    {
        if (e.ConstValue is long c)
        {
            if ((c != 0) == jumpIf) Jmp(target);
            return;
        }
        switch (e)
        {
            case UnaryExpr { Op: TokenKind.Bang } u:
                GenCond(u.Operand, target, !jumpIf);
                return;

            case BinaryExpr { Op: TokenKind.AmpAmp } b:
                if (!jumpIf)
                {
                    GenCond(b.Left, target, false);
                    GenCond(b.Right, target, false);
                }
                else
                {
                    var skip = NewLabel("and");
                    GenCond(b.Left, skip, false);
                    GenCond(b.Right, target, true);
                    Place(skip);
                }
                return;

            case BinaryExpr { Op: TokenKind.PipePipe } b:
                if (jumpIf)
                {
                    GenCond(b.Left, target, true);
                    GenCond(b.Right, target, true);
                }
                else
                {
                    var skip = NewLabel("or");
                    GenCond(b.Left, skip, true);
                    GenCond(b.Right, target, false);
                    Place(skip);
                }
                return;

            case BinaryExpr { Op: TokenKind.EqEq or TokenKind.NotEq or TokenKind.Less or TokenKind.LessEq
                or TokenKind.Greater or TokenKind.GreaterEq } b:
                GenCompare(b, target, jumpIf);
                return;

            case CallExpr call:
                GenCall(call);
                switch (call.Function!.ReturnRegister)
                {
                    case Register.Carry: Branch(jumpIf ? Bcs : Bcc, target); return;
                    case Register.Zero: Branch(jumpIf ? Beq : Bne, target); return;
                    case Register.Negative: Branch(jumpIf ? Bmi : Bpl, target); return;
                    case Register.Overflow: Branch(jumpIf ? Bvs : Bvc, target); return;
                    case Register.X: Op(Cpx, Byte(0)); break;
                    case Register.Y: Op(Cpy, Byte(0)); break;
                    default: Op(Cmp, Byte(0)); break;
                }
                Branch(jumpIf ? Bne : Beq, target);
                return;

            default:
                GenA(e);
                if (!FlagsMatchA()) Op(Cmp, Byte(0));
                Branch(jumpIf ? Bne : Beq, target);
                return;
        }
    }

    private static TokenKind Negate(TokenKind op) => op switch
    {
        TokenKind.EqEq => TokenKind.NotEq, TokenKind.NotEq => TokenKind.EqEq,
        TokenKind.Less => TokenKind.GreaterEq, TokenKind.GreaterEq => TokenKind.Less,
        TokenKind.Greater => TokenKind.LessEq, _ => TokenKind.Greater,
    };

    private void GenCompare(BinaryExpr b, Label target, bool jumpIf)
    {
        if (InSourceOrder(b.Left, b.Right, l => b.Left = l, () => GenCompare(b, target, jumpIf))) return;
        var op = jumpIf ? b.Op : Negate(b.Op);
        Expr left = b.Left, right = b.Right;
        var type = left.Type.Size >= right.Type.Size ? left.Type : right.Type;
        bool signed = type.IsSigned;

        // A one-byte value against a constant keeps the value in A: c < x is x > c, and x > c is
        // x >= c + 1 (cmp #c+1), x <= c is x < c + 1. Not when c + 1 does not fit (x > 255).
        // A signed 0 > w is w < 0 at any size, the sign test below.
        if ((type.Size == 1 || (signed && left.ConstValue == 0)) && left.ConstValue is not null && right.ConstValue is null)
        {
            (left, right) = (right, left);
            op = op switch
            {
                TokenKind.Less => TokenKind.Greater, TokenKind.Greater => TokenKind.Less,
                TokenKind.LessEq => TokenKind.GreaterEq, TokenKind.GreaterEq => TokenKind.LessEq, _ => op,
            };
        }
        // Unsigned, x > 0 is x != 0 and x <= 0 is x == 0: the load sets Z, no cmp needed (F9).
        if (type.Size == 1 && !signed && right.ConstValue == 0 && op is TokenKind.Greater or TokenKind.LessEq)
            op = op == TokenKind.Greater ? TokenKind.NotEq : TokenKind.EqEq;
        if (type.Size == 1 && op is TokenKind.Greater or TokenKind.LessEq && right.ConstValue is long c && c < type.MaxValue)
        {
            right = new NumberExpr(right.Pos, c + 1) { Type = right.Type, ConstValue = c + 1 };
            op = op == TokenKind.Greater ? TokenKind.GreaterEq : TokenKind.Less;
        }

        // A signed s < 0 or s >= 0 is the sign bit (spec 11): lda s / bmi, lda s+1 / bpl for an i16.
        if (signed && right.ConstValue == 0 && op is TokenKind.Less or TokenKind.GreaterEq)
        {
            if (type.Size == 1)
            {
                GenA(left);
                if (!FlagsMatchA()) Op(Cmp, Byte(0));
            }
            else if (Simple2(left) is { } pair)
                Op(Lda, pair.Hi);
            else
            {
                var temp = AcquireTemp(2);
                Gen16(left, new Loc(temp));
                Op(Lda, Var(temp, 1));
                Release(temp);
            }
            Branch(op == TokenKind.Less ? Bmi : Bpl, target);
            return;
        }

        // The left side already in A (a using A parameter, F16): compare in place with two branches
        // instead of loading the right side, so the parameter need not be read from memory.
        // A using AX parameter (F16): the high bytes with cpx, then the low bytes with cmp, so A and X
        // keep the parameter.
        if (type.Size == 2 && !signed && left.Type.Size == 2 && AHolds(left) && Simple2(right) is var (lo, hi))
        {
            CompareInAX(op, lo, hi, target);
            return;
        }
        if (type.Size == 1 && !signed && op is TokenKind.Greater or TokenKind.LessEq && AHolds(left) && Simple1(right) is { } r)
        {
            Op(Cmp, r);
            if (op == TokenKind.LessEq)
            {
                Branch(Bcc, target);
                Branch(Beq, target);
            }
            else
            {
                var equal = NewLabel("eq");
                Branch(Beq, equal);
                Branch(Bcs, target);
                Place(equal);
            }
            return;
        }

        // Only ==, !=, < and >= remain: a > b is b < a, and a <= b is b >= a.
        if (op is TokenKind.Greater or TokenKind.LessEq)
        {
            (left, right) = (right, left);
            op = op == TokenKind.Greater ? TokenKind.Less : TokenKind.GreaterEq;
        }

        if (type.Size == 1) Compare8(left, right, op, signed, target);
        else Compare16(left, right, op, signed, target);
    }

    /// <summary>
    /// Jumps to <paramref name="target"/> when the unsigned value in A (low) and X (high) relates to
    /// (<paramref name="lo"/>, <paramref name="hi"/>) by <paramref name="op"/>, without changing A or X.
    /// </summary>
    private void CompareInAX(TokenKind op, Operand lo, Operand hi, Label target)
    {
        var skip = NewLabel("cmp");
        if (op is TokenKind.EqEq or TokenKind.NotEq)
        {
            Op(Cmp, lo);
            Branch(Bne, op == TokenKind.EqEq ? skip : target);
            Op(Cpx, hi);
            Branch(op == TokenKind.EqEq ? Beq : Bne, target);
            Place(skip);
            return;
        }
        bool less = op is TokenKind.Less or TokenKind.LessEq;
        // The high bytes decide unless they are equal.
        Op(Cpx, hi);
        Branch(Bcc, less ? target : skip);
        Branch(Bne, less ? skip : target);
        Op(Cmp, lo);
        switch (op)
        {
            case TokenKind.Less: Branch(Bcc, target); break;
            case TokenKind.GreaterEq: Branch(Bcs, target); break;
            case TokenKind.Greater: Branch(Beq, skip); Branch(Bcs, target); break;
            default: Branch(Bcc, target); Branch(Beq, target); break;
        }
        Place(skip);
    }

    private void Compare8(Expr left, Expr right, TokenKind op, bool signed, Label target)
    {
        VariableSymbol? temp = null;
        bool equality = op is TokenKind.EqEq or TokenKind.NotEq;
        var rop = Simple1(right);
        if (rop is null && equality && Simple1(left) is { } lop)
        {
            (left, right) = (right, left);
            rop = lop;
        }
        // An element compared from A: lda (p),y / cmp (q),y. cpx and cpy have no indexed forms.
        bool inRegister = !signed && left is NameExpr { Symbol.Register: not null };
        var indexed = rop is null && !inRegister ? IndexedOperand(right) : null;
        if (rop is null && indexed is null && equality && !inRegister && IndexedOperand(left) is { } li)
        {
            (left, right) = (right, left);
            indexed = li;
        }
        if (rop is null && indexed is null)
        {
            temp = AcquireTemp(1);
            GenA(right);
            Op(Sta, Var(temp));
            rop = Var(temp);
        }
        void Use(Mnemonic m)
        {
            if (indexed is not null) indexed(m, 0);
            else Op(m, rop!);
        }

        if (inRegister && left is NameExpr { Symbol.Register: { } reg })
        {
            Op(reg == Register.X ? Cpx : Cpy, rop!);
        }
        else
        {
            GenA(left);
            if (signed && op is TokenKind.Less or TokenKind.GreaterEq)
            {
                // N = (left < right) after correcting for overflow.
                var noOverflow = NewLabel("v");
                Emit(Sec);
                Use(Sbc);
                Branch(Bvc, noOverflow);
                Op(Eor, Byte(0x80));
                Place(noOverflow);
                Branch(op == TokenKind.Less ? Bmi : Bpl, target);
                if (temp is not null) Release(temp);
                return;
            }
            if (!(rop is Imm { Value: 0 } && equality && FlagsMatchA()))
                Use(Cmp);
        }
        Branch(op switch
        {
            TokenKind.EqEq => Beq, TokenKind.NotEq => Bne, TokenKind.Less => Bcc, _ => Bcs,
        }, target);
        if (temp is not null) Release(temp);
    }

    private void Compare16(Expr left, Expr right, TokenKind op, bool signed, Label target)
    {
        var temps = new List<VariableSymbol>();
        (Operand Lo, Operand Hi) Operands(Expr e)
        {
            if (Simple2(e) is { } p) return p;
            var t = AcquireTemp(2);
            temps.Add(t);
            Gen16(e, new Loc(t));
            return (Var(t), Var(t, 1));
        }
        // An element on either side is used in place (lda u,x / cmp v,x / lda u+n,x / sbc v+n,x).
        Action<Mnemonic, int> Side(Expr e)
        {
            if (Simple2(e) is null && IndexedOperand(e, 2) is { } element) return element;
            var p = Operands(e);
            return (m, b) => Op(m, b == 0 ? p.Lo : p.Hi);
        }
        if (op is TokenKind.EqEq or TokenKind.NotEq && left.ConstValue is not null && right.ConstValue is null)
            (left, right) = (right, left);
        var l = Side(left);
        var r = Side(right);
        // One byte loaded and compared for == and !=; lda sets Z, so a zero byte needs no cmp #0.
        void LoadAndCompare(int b)
        {
            l(Lda, b);
            if (right.ConstValue is not long c || ((c >> (8 * b)) & 0xFF) != 0) r(Cmp, b);
        }

        switch (op)
        {
            case TokenKind.EqEq:
            {
                var skip = NewLabel("ne");
                LoadAndCompare(0); Branch(Bne, skip);
                LoadAndCompare(1); Branch(Beq, target);
                Place(skip);
                break;
            }
            case TokenKind.NotEq:
                LoadAndCompare(0); Branch(Bne, target);
                LoadAndCompare(1); Branch(Bne, target);
                break;
            default: // < or >=
                l(Lda, 0); r(Cmp, 0);
                l(Lda, 1); r(Sbc, 1);
                if (signed)
                {
                    var noOverflow = NewLabel("v");
                    Branch(Bvc, noOverflow);
                    Op(Eor, Byte(0x80));
                    Place(noOverflow);
                    Branch(op == TokenKind.Less ? Bmi : Bpl, target);
                }
                else
                    Branch(op == TokenKind.Less ? Bcc : Bcs, target);
                break;
        }
        foreach (var t in temps) Release(t);
    }
}
