using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Backend;

/// <summary>8-bit expressions, with the result in A, and 16-bit expressions, with the result stored to memory.</summary>
public sealed partial class CodeGen
{
    // ---------- 8-bit expressions: result in A ----------

    private static VariableSymbol ArrayOf(IndexExpr ix) => (VariableSymbol)((NameExpr)ix.Target).Symbol!;

    /// <summary>An operand for a one-byte value that needs no code to reach, or null.</summary>
    private Operand? Simple1(Expr e)
    {
        if (e.ConstValue is long c) return Byte(c);
        switch (e)
        {
            case NameExpr { Symbol: { InMemory: true } v } when v.Type.Size == 1:
                return Var(v);
            case IndexExpr ix when !IsPointerIndex(ix) && ix.Index.ConstValue is long i && ix.Type.Size == 1:
                return Var(ArrayOf(ix), (int)i);
            case MemberExpr m:
                int b = m.Member == "hi" ? 1 : 0;
                if (m.Target is NameExpr { Symbol: { InMemory: true } mv }) return Var(mv, b);
                if (m.Target is IndexExpr { Index.ConstValue: long mi } mix && !IsPointerIndex(mix))
                    return Var(ArrayOf(mix), (b == 1 ? HighTable(ArrayOf(mix)) : 0) + (int)mi);
                if (Simple2(m.Target) is { } pair) return b == 1 ? pair.Hi : pair.Lo;
                return null;
            case CastExpr cast when cast.Type.Size == 1:
                if (cast.Operand.Type.Size == 1) return Simple1(cast.Operand);
                return Simple2(cast.Operand)?.Lo;
            default:
                return null;
        }
    }

    /// <summary>
    /// An element used as the operand of an instruction without a temporary (<c>adc b,x</c>,
    /// <c>cmp (q),y</c>): an array element whose index loads without changing A, or a byte of
    /// <c>p[i]</c> through a zero-page pointer with such an index. The action loads the index
    /// register and emits the instruction on byte 0 or 1 of the element. Null for anything else.
    /// </summary>
    private Action<Mnemonic, int>? IndexedOperand(Expr e, int size = 1)
    {
        // A cast between types of the same size is no code.
        if (e is CastExpr cast && cast.Type.Size == size && cast.Operand.Type.Size == size) return IndexedOperand(cast.Operand, size);
        if (e is not IndexExpr ix || ix.Type.Size != size || ix.Index.ConstValue is not null || ContainsCall(ix.Index)) return null;
        if (IsPointerIndex(ix))
        {
            if (ix.Type.Size != 1 || DirectPointer(ix.Target) is not { } p || !PointerIndexIsCheap(ix)) return null;
            return (m, _) => { LoadPointerIndex(ix, 0); Emit(m, IndirectIndexed, Var(p)); };
        }
        var arr = ArrayOf(ix);
        var (index, shift) = FoldIndexOffset(ix.Index, arr);
        if (!IndexIsCheap(index)) return null;
        // Each byte of a 16-bit element loads the index again: a volatile index must be read once (spec 4).
        if (size == 2 && ReadsVolatile(index)) return null;
        return (m, b) =>
        {
            var reg = LoadIndex(index);
            Emit(m, reg == Register.X ? AbsoluteX : AbsoluteY, Indexed(arr, b == 0 ? 0 : HighTable(arr), shift, index));
        };
    }

    /// <summary>True if evaluating e reads volatile memory.</summary>
    private static bool ReadsVolatile(Expr e) => e switch
    {
        NameExpr { Symbol: VariableSymbol v } => v.IsVolatile,
        UnaryExpr u => ReadsVolatile(u.Operand),
        BinaryExpr b => ReadsVolatile(b.Left) || ReadsVolatile(b.Right),
        IndexExpr ix => ReadsVolatile(ix.Target) || ReadsVolatile(ix.Index),
        MemberExpr m => ReadsVolatile(m.Target),
        CastExpr c => ReadsVolatile(c.Operand),
        _ => false,
    };

    /// <summary>Loading this index into X or Y does not change A.</summary>
    private bool IndexIsCheap(Expr index) =>
        index is NameExpr { Symbol.Register: not null } || Simple1(index) is not null;

    /// <summary>
    /// <c>a[i - c]</c> is addressed as <c>a-c,x</c> with i in the index register, so the subtraction
    /// costs nothing (spec 11). Only where the wrap-around of the u8 index cannot matter: i - c wraps to
    /// 256 - c + i, which is past the end of an array shorter than 257 - c elements.
    /// <c>a[i + c]</c> is <c>a+c,x</c> when i is a <c>for</c> counter whose bounds prove i + c &lt;= 255.
    /// </summary>
    private (Expr Index, int Offset) FoldIndexOffset(Expr index, VariableSymbol array)
    {
        if (index is not BinaryExpr { Type.Kind: TypeKind.U8 } b) return (index, 0);
        if (b.Op == TokenKind.Minus && b.Left.Type is { Size: 1, IsSigned: false } && b.Right.ConstValue is long c && c > 0
            && array.Type.Length + c <= 256)
            return (b.Left, -(int)c);
        if (b.Op == TokenKind.Plus)
        {
            var (counter, add) = b.Right.ConstValue is long r ? (b.Left, r) : b.Left.ConstValue is long l ? (b.Right, l) : (null, 0L);
            if (counter is NameExpr { Symbol: VariableSymbol v } && add > 0 && _counterMax.TryGetValue(v, out long max) && max + add <= 255)
                return (counter, (int)add);
        }
        return (index, 0);
    }

    /// <summary>Puts an array index into X or Y and returns which.</summary>
    private Register LoadIndex(Expr index)
    {
        if (index is NameExpr { Symbol.Register: { } r }) return r;
        // The offset table of an indexed record array: ldx i / ldy table,x (spec 3.3).
        if (index is IndexExpr { Target: NameExpr { Symbol: VariableSymbol { IsOffsetTable: true } table } } tix && !_reserved.Contains(Register.Y))
        {
            if (tix.Index is NameExpr { Symbol.Register: Register.X })
            {
                Emit(Ldy, AbsoluteX, Indexed(table, 0, 0, tix));
                return Register.Y;
            }
            if (!_reserved.Contains(Register.X))
            {
                if (Simple1(tix.Index) is { } inner) Op(Ldx, inner);
                else { GenA(tix.Index); Emit(Tax); }
                Emit(Ldy, AbsoluteX, Indexed(table, 0, 0, tix));
                return Register.Y;
            }
        }
        var reg = FreeIndexRegister(index.Pos);
        if (Simple1(index) is { } op)
            Op(reg == Register.X ? Ldx : Ldy, op);
        else
        {
            GenA(index);
            Emit(reg == Register.X ? Tax : Tay);
        }
        return reg;
    }

    private void LoadArrayByte(IndexExpr ix, int byteIndex)
    {
        var arr = ArrayOf(ix);
        int table = byteIndex == 0 ? 0 : HighTable(arr);
        if (ix.Index.ConstValue is long ci)
        {
            Op(Lda, Var(arr, table + (int)ci));
            return;
        }
        var (index, shift) = FoldIndexOffset(ix.Index, arr);
        var reg = LoadIndex(index);
        Emit(Lda, reg == Register.X ? AbsoluteX : AbsoluteY, Indexed(arr, table, shift, index));
    }

    private void GenA(Expr e)
    {
        if (e.ConstValue is long c)
        {
            Op(Lda, Byte(c));
            return;
        }
        switch (e)
        {
            case NameExpr { Symbol: { } v }:
                if (v.Register == Register.X) Emit(Txa);
                else if (v.Register == Register.Y) Emit(Tya);
                else Op(Lda, Var(v));
                return;

            case IndexExpr ix when IsPointerIndex(ix):
                PointerByte(ix, 0, Lda);
                return;

            case IndexExpr ix:
                LoadArrayByte(ix, 0);
                return;

            case MemberExpr m:
            {
                int b = m.Member == "hi" ? 1 : 0;
                if (Simple1(m) is { } mop) { Op(Lda, mop); return; }
                if (m.Target is IndexExpr pix && IsPointerIndex(pix)) { PointerByte(pix, b, Lda); return; }
                if (m.Target is IndexExpr mix) { LoadArrayByte(mix, b); return; }
                var temp = AcquireTemp(2);
                Gen16(m.Target, new Loc(temp));
                Op(Lda, Var(temp, b));
                Release(temp);
                return;
            }

            case UnaryExpr { Op: TokenKind.Minus } neg when Simple1(neg.Operand) is not null || IndexedOperand(neg.Operand) is not null:
                // -m as 0 - m: lda #0 / sec / sbc m, shorter than lda m / eor #$FF / clc / adc #1.
                Op(Lda, Byte(0));
                Emit(Sec);
                if (Simple1(neg.Operand) is { } nop) Op(Sbc, nop);
                else IndexedOperand(neg.Operand)!(Sbc, 0);
                return;

            case UnaryExpr u:
                GenA(u.Operand);
                switch (u.Op)
                {
                    case TokenKind.Minus: Op(Eor, Byte(0xFF)); Emit(Clc); Op(Adc, Byte(1)); break;
                    case TokenKind.Tilde: Op(Eor, Byte(0xFF)); break;
                    case TokenKind.Bang: Op(Eor, Byte(1)); break;
                }
                return;

            case BinaryExpr b when b.Type.Kind == TypeKind.Bool:
                GenBoolValue(b);
                return;

            case BinaryExpr b:
                GenBinaryA(b);
                return;

            case CallExpr call:
                GenCall(call);
                switch (call.Function!.ReturnRegister)
                {
                    case Register.X: Emit(Txa); break;
                    case Register.Y: Emit(Tya); break;
                    case Register.Carry: Op(Lda, Byte(0)); Emit(Rol, Accumulator); break;
                    case Register.Zero or Register.Negative or Register.Overflow:
                    {
                        var isFalse = NewLabel("f");
                        var done = NewLabel("b");
                        Branch(call.Function.ReturnRegister switch { Register.Zero => Bne, Register.Negative => Bpl, _ => Bvc }, isFalse);
                        Op(Lda, Byte(1));
                        Branch(Bne, done);
                        Place(isFalse);
                        Op(Lda, Byte(0));
                        Place(done);
                        break;
                    }
                }
                return;

            case CastExpr cast:
                if (cast.Operand.Type.Size == 1)
                    GenA(cast.Operand);
                else if (Simple2(cast.Operand) is { } pair)
                    Op(Lda, pair.Lo);
                else
                {
                    var temp = AcquireTemp(2);
                    Gen16(cast.Operand, new Loc(temp));
                    Op(Lda, Var(temp));
                    Release(temp);
                }
                return;
        }
        throw new InvalidOperationException(e.GetType().Name);
    }

    private static Mnemonic ArithMnemonic(TokenKind op) => op switch
    {
        TokenKind.Plus => Adc, TokenKind.Minus => Sbc, TokenKind.Amp => And, TokenKind.Pipe => Ora, _ => Eor,
    };

    private void CarryFor(TokenKind op)
    {
        if (op == TokenKind.Plus) Emit(Clc);
        else if (op == TokenKind.Minus) Emit(Sec);
    }

    /// <summary>As <see cref="CarryFor(TokenKind)"/>, with no <c>clc</c> when the code for <paramref name="last"/>, just generated, left the carry clear.</summary>
    private void CarryFor(TokenKind op, Expr last)
    {
        if (op == TokenKind.Plus && CarryClearAfter(last)) return;
        CarryFor(op);
    }

    /// <summary>
    /// The largest value of an unsigned one-byte expression, from constants and the enclosing
    /// <c>for</c> counters' bounds (as for <see cref="FoldIndexOffset"/>); 255 when nothing is known.
    /// </summary>
    private long MaxOf(Expr e)
    {
        if (e.ConstValue is long c) return c & 0xFF;
        if (e.Type is not { Size: 1, IsSigned: false }) return 255;
        return e switch
        {
            NameExpr { Symbol: VariableSymbol v } when _counterMax.TryGetValue(v, out long m) => m,
            BinaryExpr { Op: TokenKind.Shl, Right.ConstValue: long k } b => Math.Min(255, MaxOf(b.Left) << (int)k),
            BinaryExpr { Op: TokenKind.Shr, Right.ConstValue: long k } b => MaxOf(b.Left) >> (int)k,
            BinaryExpr { Op: TokenKind.Plus } b => Math.Min(255, MaxOf(b.Left) + MaxOf(b.Right)),
            BinaryExpr { Op: TokenKind.Amp } b => Math.Min(MaxOf(b.Left), MaxOf(b.Right)),
            _ => 255,
        };
    }

    /// <summary>
    /// True if <see cref="GenA"/> of e ends with the carry clear: an <c>asl</c> that shifts out a 0
    /// bit (x &lt;&lt; k with x &lt;&lt; k below 256), or an <c>adc</c> whose sum cannot pass 255.
    /// </summary>
    private bool CarryClearAfter(Expr e) => e.ConstValue is null && e.Type is { Size: 1, IsSigned: false } && e switch
    {
        BinaryExpr { Op: TokenKind.Shl, Right.ConstValue: long k } b => k >= 1 && MaxOf(b.Left) << (int)k <= 255,
        BinaryExpr { Op: TokenKind.Plus } b => MaxOf(b.Left) + MaxOf(b.Right) <= 255,
        _ => false,
    };

    private void GenBinaryA(BinaryExpr b)
    {
        if (InSourceOrder(b.Left, b.Right, l => b.Left = l, () => GenBinaryA(b))) return;
        if (b.Op is TokenKind.Shl or TokenKind.Shr)
        {
            GenA(b.Left);
            if (b.ScalesIndexOf is var (array, elements))
            {
                // The index of a linear array, scaled: checked in A before it can wrap (M20).
                _out.Add(new Instr(Asl, Accumulator) { Check = new IndexCheck(array, 0, elements, elements, InA: true) { At = b.Pos } });
                ShiftA(b.Op, b.Right.ConstValue!.Value - 1, signed: false);
                return;
            }
            ShiftA(b.Op, b.Right.ConstValue!.Value, b.Type.IsSigned);
            return;
        }

        var m = ArithMnemonic(b.Op);
        var right = Simple1(b.Right);
        bool commutative = b.Op != TokenKind.Minus;
        if (right is null && commutative && Simple1(b.Left) is { } left)
        {
            GenA(b.Right);
            CarryFor(b.Op, b.Right);
            Op(m, left);
            return;
        }
        if (right is not null)
        {
            GenA(b.Left);
            CarryFor(b.Op, b.Left);
            Op(m, right);
            return;
        }
        // An element as the operand: lda a,x / adc b,x.
        var indexed = IndexedOperand(b.Right);
        var other = b.Left;
        if (indexed is null && commutative && IndexedOperand(b.Left) is { } li) (indexed, other) = (li, b.Right);
        if (indexed is not null)
        {
            GenA(other);
            CarryFor(b.Op, other);
            indexed(m, 0);
            return;
        }
        var temp = AcquireTemp(1);
        GenA(b.Right);
        Op(Sta, Var(temp));
        GenA(b.Left);
        CarryFor(b.Op, b.Left);
        Op(m, Var(temp));
        Release(temp);
    }

    private void ShiftA(TokenKind op, long count, bool signed)
    {
        for (int i = 0; i < count; i++)
        {
            if (op == TokenKind.Shl) Emit(Asl, Accumulator);
            else if (signed) { Op(Cmp, Byte(0x80)); Emit(Ror, Accumulator); }
            else Emit(Lsr, Accumulator);
        }
    }

    /// <summary>Puts a bool expression's value (0 or 1) in A.</summary>
    private void GenBoolValue(Expr e)
    {
        var isFalse = NewLabel("f");
        var done = NewLabel("b");
        GenCond(e, isFalse, false);
        Op(Lda, Byte(1));
        Branch(Bne, done);
        Place(isFalse);
        Op(Lda, Byte(0));
        Place(done);
    }

    // ---------- 16-bit expressions: result stored to memory ----------

    /// <summary>Operands for the two bytes of a value that needs no code to reach, or null.</summary>
    private (Operand Lo, Operand Hi)? Simple2(Expr e)
    {
        if (e.ConstValue is long c) return (Byte(c), Byte(c >> 8));
        if (e.Type.Size == 1)
            return !e.Type.IsSigned && Simple1(e) is { } op ? (op, Byte(0)) : null;
        switch (e)
        {
            case NameExpr { Symbol: { InMemory: true } v }:
                return (Var(v), Var(v, 1));
            case CallExpr or BinaryExpr when AddressBytes(e, 0) is { } address:
                return address;
            case IndexExpr ix when !IsPointerIndex(ix) && ix.Index.ConstValue is long i:
                var arr = ArrayOf(ix);
                return (Var(arr, (int)i), Var(arr, HighTable(arr) + (int)i));
            case CastExpr cast when cast.Operand.Type.Size == 2:
                return Simple2(cast.Operand);
            default:
                return null;
        }
    }

    /// <summary>
    /// The two bytes of an address the assembler fills in: <c>addr(x)</c> plus or minus constants,
    /// through casts between two-byte types (<c>#&lt;(map+299)</c>). Null for anything else.
    /// </summary>
    private (Operand Lo, Operand Hi)? AddressBytes(Expr e, int offset)
    {
        switch (e)
        {
            case CallExpr { FunctionAddress: { } f }:
                return (new ImmLabelByte(_functionLabels[f], false, offset), new ImmLabelByte(_functionLabels[f], true, offset));
            case CallExpr { AddressOf: { } av }:
            {
                // A field of a struct variable is a view: its storage plus an offset.
                var (v, field) = av.AliasOf is { } storage ? (storage, av.AliasOffset) : (av, 0);
                return (new ImmVarByte(v, false, field + offset), new ImmVarByte(v, true, field + offset));
            }
            case CastExpr { Type.Size: 2, Operand.Type.Size: 2 } cast:
                return AddressBytes(cast.Operand, offset);
            case BinaryExpr { Op: TokenKind.Plus, Type.Size: 2, Right.ConstValue: long r } b:
                return AddressBytes(b.Left, offset + (int)r);
            case BinaryExpr { Op: TokenKind.Plus, Type.Size: 2, Left.ConstValue: long l } b:
                return AddressBytes(b.Right, offset + (int)l);
            case BinaryExpr { Op: TokenKind.Minus, Type.Size: 2, Right.ConstValue: long r } b:
                return AddressBytes(b.Left, offset - (int)r);
            default:
                return null;
        }
    }

    /// <summary>
    /// One byte of a 16-bit operand used in place: a simple operand, or an element of a split array
    /// whose index loads without changing A (<c>adc w,x</c>). Neither may read <paramref name="written"/>
    /// (the index, for an element), which the low byte of the result changes before the high byte is
    /// read; the variable itself is fine, since each byte is read before it is written. Null otherwise.
    /// </summary>
    private Action<Mnemonic, int>? InPlace16(Expr e, VariableSymbol written)
    {
        if (Simple2(e) is { } p)
            return e is NameExpr { Symbol: { } s } && s == written || !ReadsVar(e, written) ? (m, b) => Op(m, b == 0 ? p.Lo : p.Hi) : null;
        return e is IndexExpr ix && !ReadsVar(ix.Index, written) ? IndexedOperand(e, 2) : null;
    }

    /// <summary>
    /// <c>l op r</c> (+, -, &amp;, |, ^) one byte at a time with both operands in place:
    /// <c>lda l / clc / adc r / sta d</c>, then the high bytes. False if an operand needs code.
    /// </summary>
    private bool Binary16InPlace(BinaryExpr b, Action<Mnemonic, int> store, VariableSymbol written)
    {
        if (b.Op is TokenKind.Shl or TokenKind.Shr) return false;
        var l = InPlace16(b.Left, written);
        var r = InPlace16(b.Right, written);
        if (l is null || r is null) return false;
        var m = ArithMnemonic(b.Op);
        CarryFor(b.Op);
        l(Lda, 0); r(m, 0); store(Sta, 0);
        l(Lda, 1); r(m, 1); store(Sta, 1);
        return true;
    }

    /// <summary>
    /// The one-byte unsigned operand of <c>d + b</c>, <c>b + d</c> or <c>d - b</c> (b a u8, or a u8 as u16),
    /// where d is the destination itself; for <c>-</c>, b must need no code. Null otherwise.
    /// </summary>
    private Expr? AddsByteTo(BinaryExpr b, Loc d)
    {
        static Expr? Narrow(Expr e) =>
            e.Type is { Size: 1, IsSigned: false } ? e
            : e is CastExpr { Operand.Type: { Size: 1, IsSigned: false } } c ? c.Operand : null;
        static bool IsDest(Expr e, Loc d) => e is NameExpr { Symbol: { } v } && v == d.Var && d.Offset == 0;
        if (b.Op == TokenKind.Plus)
        {
            // A constant keeps clc / lda w / adc #c, the same cost.
            if (IsDest(b.Left, d) && Narrow(b.Right) is { ConstValue: null } r) return r;
            if (IsDest(b.Right, d) && Narrow(b.Left) is { ConstValue: null } l) return l;
            return null;
        }
        return IsDest(b.Left, d) && Narrow(b.Right) is { } s && (Simple1(s) is not null || IndexedOperand(s) is not null) ? s : null;
    }

    /// <summary>
    /// For <c>q + e</c> (either order) into d, with e an unsigned byte that needs code and q a 16-bit
    /// operand used in place that does not read d: the two bytes of q and e. Null otherwise, also for a
    /// constant e, which keeps <c>clc / lda q / adc #c</c>.
    /// </summary>
    private (Action<Mnemonic, int> Wide, Expr Small)? ByteAddend(BinaryExpr b, Loc d)
    {
        static Expr? Narrow(Expr e) =>
            e.Type is { Size: 1, IsSigned: false } ? e
            : e is CastExpr { Operand.Type: { Size: 1, IsSigned: false } } c ? c.Operand : null;
        foreach (var (q, e) in new[] { (b.Left, b.Right), (b.Right, b.Left) })
        {
            if (q.Type.Size != 2 || Narrow(e) is not { ConstValue: null } small || ReadsVar(q, d.Var)) continue;
            if (InPlace16(q, d.Var) is { } wide) return (wide, small);
        }
        return null;
    }

    /// <summary>
    /// <c>x op (y &gt;&gt; k)</c> (or <c>&lt;&lt;</c>, either side unless op is <c>-</c>) with y and x
    /// needing no code: the shifted low byte stays in A and only its high byte goes to a temporary,
    /// <c>lda y+1 / sta t / lda y / lsr t / ror a ... / clc / adc x / sta d / lda t / adc x+1 / sta d+1</c>.
    /// Not for a signed <c>&gt;&gt;</c>. False if the expression is not of this form.
    /// </summary>
    private bool ShiftedOperand16(BinaryExpr b, Loc d)
    {
        if (b.Op is not (TokenKind.Plus or TokenKind.Minus or TokenKind.Amp or TokenKind.Pipe or TokenKind.Caret) || d.Offset != 0)
            return false;
        static bool IsShift(Expr e) =>
            e is BinaryExpr { Op: TokenKind.Shl or TokenKind.Shr, Type.Size: 2, Right.ConstValue: not null } s
            && !(s.Op == TokenKind.Shr && s.Type.IsSigned);
        Expr shift, other;
        if (IsShift(b.Right) && b.Op != TokenKind.Minus) (shift, other) = (b.Right, b.Left);
        else if (IsShift(b.Left)) (shift, other) = (b.Left, b.Right);
        else return false;
        var s = (BinaryExpr)shift;
        if (Simple2(s.Left) is not { } y || InPlace16(other, d.Var) is not { } x) return false;

        var t = AcquireTemp(1);
        int k = (int)s.Right.ConstValue!.Value;
        if (k >= 8)
        {
            // A byte moves first: << puts the shifted low byte in t and 0 in A; >> 0 in t and the shifted high byte in A.
            if (s.Op == TokenKind.Shr) { Op(Lda, Byte(0)); Op(Sta, Var(t)); }
            Op(Lda, s.Op == TokenKind.Shl ? y.Lo : y.Hi);
            for (int i = 8; i < k; i++) Emit(s.Op == TokenKind.Shl ? Asl : Lsr, Accumulator);
            if (s.Op == TokenKind.Shl) { Op(Sta, Var(t)); Op(Lda, Byte(0)); }
        }
        else
        {
            Op(Lda, y.Hi); Op(Sta, Var(t)); Op(Lda, y.Lo);
            for (int i = 0; i < k; i++)
            {
                if (s.Op == TokenKind.Shl) { Emit(Asl, Accumulator); Op(Rol, Var(t)); }
                else { Op(Lsr, Var(t)); Emit(Ror, Accumulator); }
            }
        }
        var m = ArithMnemonic(b.Op);
        CarryFor(b.Op);
        x(m, 0); Op(Sta, d.Lo);
        Op(Lda, Var(t)); x(m, 1); Op(Sta, d.Hi);
        Release(t);
        return true;
    }

    /// <summary>
    /// A 16-bit shift by k = 8 to 15 moves a byte first (spec 11): <c>w = v &lt;&lt; 8</c> is
    /// <c>lda v / sta w+1 / lda #0 / sta w</c>, <c>w = v &gt;&gt; 8</c> is <c>lda v+1 / sta w / lda #0 / sta w+1</c>,
    /// with k - 8 more shifts of the moved byte in A before its store. A signed <c>&gt;&gt;</c> shifts with
    /// <c>cmp #$80 / ror</c> and fills the high byte from the sign.
    /// </summary>
    private void ShiftByBytes(BinaryExpr b, int k, Loc d)
    {
        Operand lo, hi;
        if (Simple2(b.Left) is { } s) (lo, hi) = s;
        else { Gen16(b.Left, d); (lo, hi) = (d.Lo, d.Hi); }
        if (b.Op == TokenKind.Shl)
        {
            Op(Lda, lo);
            for (int i = 8; i < k; i++) Emit(Asl, Accumulator);
            Op(Sta, d.Hi);
            Op(Lda, Byte(0)); Op(Sta, d.Lo);
            return;
        }
        Op(Lda, hi);
        for (int i = 8; i < k; i++)
        {
            if (b.Type.IsSigned) { Op(Cmp, Byte(0x80)); Emit(Ror, Accumulator); }
            else Emit(Lsr, Accumulator);
        }
        Op(Sta, d.Lo);
        if (b.Type.IsSigned)
        {
            var positive = NewLabel("sx");
            Op(Ora, Byte(0x7F));
            Branch(Bmi, positive);
            Op(Lda, Byte(0));
            Place(positive);
        }
        else
            Op(Lda, Byte(0));
        Op(Sta, d.Hi);
    }

    private void Gen16(Expr e, Loc d)
    {
        if (e.ConstValue is long c)
        {
            Op(Lda, Byte(c)); Op(Sta, d.Lo);
            Op(Lda, Byte(c >> 8)); Op(Sta, d.Hi);
            return;
        }

        // Widening a one-byte value.
        if (e.Type.Size == 1)
        {
            GenA(e);
            Op(Sta, d.Lo);
            if (e.Type.IsSigned)
            {
                var positive = NewLabel("sx");
                Op(Ora, Byte(0x7F));
                Branch(Bmi, positive);
                Op(Lda, Byte(0));
                Place(positive);
            }
            else
                Op(Lda, Byte(0));
            Op(Sta, d.Hi);
            return;
        }

        if (e is BinaryExpr eb && InSourceOrder(eb.Left, eb.Right, l => eb.Left = l, () => Gen16(eb, d))) return;
        switch (e)
        {
            case NameExpr { Symbol: { } v }:
                if (v == d.Var && d.Offset == 0) return;
                Op(Lda, Var(v)); Op(Sta, d.Lo);
                Op(Lda, Var(v, 1)); Op(Sta, d.Hi);
                return;

            case IndexExpr ix when IsPointerIndex(ix):
                LoadPointerWord(ix, d);
                return;

            case IndexExpr ix:
            {
                var arr = ArrayOf(ix);
                if (ix.Index.ConstValue is long ci)
                {
                    Op(Lda, Var(arr, (int)ci)); Op(Sta, d.Lo);
                    Op(Lda, Var(arr, HighTable(arr) + (int)ci)); Op(Sta, d.Hi);
                    return;
                }
                var (index, shift) = FoldIndexOffset(ix.Index, arr);
                var reg = LoadIndex(index);
                var mode = reg == Register.X ? AbsoluteX : AbsoluteY;
                Emit(Lda, mode, Indexed(arr, 0, shift, index)); Op(Sta, d.Lo);
                Emit(Lda, mode, Indexed(arr, HighTable(arr), shift, index)); Op(Sta, d.Hi);
                return;
            }

            case CallExpr { AddressOf: not null } or CallExpr { FunctionAddress: not null }:
            {
                var (lo, hi) = Simple2(e)!.Value;
                Op(Lda, lo); Op(Sta, d.Lo);
                Op(Lda, hi); Op(Sta, d.Hi);
                return;
            }

            case CallExpr call:
                GenCall(call);
                switch (call.Function!.ReturnRegister)
                {
                    case Register.AX: Op(Sta, d.Lo); Op(Stx, d.Hi); break;
                    case Register.AY: Op(Sta, d.Lo); Op(Sty, d.Hi); break;
                    default: Op(Stx, d.Lo); Op(Sty, d.Hi); break;
                }
                return;

            case CastExpr cast:
                Gen16(cast.Operand, d);
                return;

            case UnaryExpr u:
                Gen16(u.Operand, d);
                if (u.Op == TokenKind.Minus)
                {
                    Op(Lda, Byte(0)); Emit(Sec); Op(Sbc, d.Lo); Op(Sta, d.Lo);
                    Op(Lda, Byte(0)); Op(Sbc, d.Hi); Op(Sta, d.Hi);
                }
                else if (u.Op == TokenKind.Tilde)
                {
                    Op(Lda, d.Lo); Op(Eor, Byte(0xFF)); Op(Sta, d.Lo);
                    Op(Lda, d.Hi); Op(Eor, Byte(0xFF)); Op(Sta, d.Hi);
                }
                return;

            case BinaryExpr b when b.Op is TokenKind.Shl or TokenKind.Shr && b.Right.ConstValue >= 8:
                ShiftByBytes(b, (int)b.Right.ConstValue!.Value, d);
                return;

            case BinaryExpr b when b.Op is TokenKind.Shl or TokenKind.Shr:
                Gen16(b.Left, d);
                for (int i = 0; i < b.Right.ConstValue; i++)
                {
                    if (b.Op == TokenKind.Shl) { Op(Asl, d.Lo); Op(Rol, d.Hi); }
                    else if (b.Type.IsSigned) { Op(Lda, d.Hi); Op(Cmp, Byte(0x80)); Op(Ror, d.Hi); Op(Ror, d.Lo); }
                    else { Op(Lsr, d.Hi); Op(Ror, d.Lo); }
                }
                return;

            case BinaryExpr b when b.Op is TokenKind.Plus or TokenKind.Minus && AddsByteTo(b, d) is { } small:
            {
                // w += b as u16: lda b / clc / adc w / sta w / bcc + / inc w+1 (spec 11);
                // w -= b as u16: lda w / sec / sbc b / sta w / bcs + / dec w+1.
                var done = NewLabel("c");
                if (b.Op == TokenKind.Plus)
                {
                    GenA(small);
                    Emit(Clc); Op(Adc, d.Lo); Op(Sta, d.Lo);
                    Branch(Bcc, done);
                    Op(Inc, d.Hi);
                }
                else
                {
                    Op(Lda, d.Lo); Emit(Sec);
                    if (Simple1(small) is { } op) Op(Sbc, op);
                    else IndexedOperand(small)!(Sbc, 0);
                    Op(Sta, d.Lo);
                    Branch(Bcs, done);
                    Op(Dec, d.Hi);
                }
                Place(done);
                return;
            }

            case BinaryExpr b when b.Op == TokenKind.Plus && ByteAddend(b, d) is var (wide, small):
            {
                // p = q + e with e an unsigned byte (spec 11): e in A / clc / adc q / sta p / lda q+1 / adc #0 / sta p+1.
                GenA(small);
                CarryFor(TokenKind.Plus, small);
                wide(Adc, 0); Op(Sta, d.Lo);
                wide(Lda, 1); Op(Adc, Byte(0)); Op(Sta, d.Hi);
                return;
            }

            case BinaryExpr b:
            {
                if (ShiftedOperand16(b, d)) return;
                var right = Simple2(b.Right);
                // An element on either side: lda a,x / adc b,x / sta d. Not for w + (u8 operand), which adds with bcc / inc.
                if (d.Offset == 0 && !(b.Op == TokenKind.Plus && right is { Hi: Imm { Value: 0 } })
                    && (right is null || Simple2(b.Left) is null)
                    && Binary16InPlace(b, (m, k) => Op(m, k == 0 ? d.Lo : d.Hi), d.Var))
                    return;
                if (right is not null && ReadsVar(b.Right, d.Var)) right = null;
                VariableSymbol? temp = null;
                if (right is null && b.Op != TokenKind.Minus && Simple2(b.Left) is { } left && !ReadsVar(b.Left, d.Var))
                {
                    // x + f(): the right side into the destination, then x as the operand, with no temporary.
                    Gen16(b.Right, d);
                    right = left;
                }
                else
                {
                    if (right is null)
                    {
                        temp = AcquireTemp(2);
                        Gen16(b.Right, new Loc(temp));
                        right = (Var(temp), Var(temp, 1));
                    }
                    Gen16(b.Left, d);
                }

                var m = ArithMnemonic(b.Op);
                CarryFor(b.Op);
                Op(Lda, d.Lo); Op(m, right.Value.Lo); Op(Sta, d.Lo);
                if (b.Op == TokenKind.Plus && right.Value.Hi is Imm { Value: 0 })
                {
                    var done = NewLabel("c");
                    Branch(Bcc, done);
                    Op(Inc, d.Hi);
                    Place(done);
                }
                else
                {
                    Op(Lda, d.Hi); Op(m, right.Value.Hi); Op(Sta, d.Hi);
                }
                if (temp is not null) Release(temp);
                return;
            }
        }
        throw new InvalidOperationException(e.GetType().Name);
    }
}
