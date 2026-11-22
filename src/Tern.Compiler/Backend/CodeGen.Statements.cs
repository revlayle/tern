using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Backend;

/// <summary>Statements and assignment.</summary>
public sealed partial class CodeGen
{
    // ---------- Statements ----------

    private void GenBlock(BlockStmt block)
    {
        // A local const has no code, and is not "the statement before" the next one.
        var statements = block.Code;
        for (int k = 0; k < statements.Count; k++)
        {
            _previous = (k > 0 ? statements[k - 1] : null, statements[k]);
            GenStmt(statements[k]);
        }
        _previous = default;
    }

    /// <summary>The statement of a block being generated, and the one before it in the block (null for the first).</summary>
    private (Stmt? Before, Stmt? Current) _previous;

    /// <summary><c>for</c> loops whose range cannot be empty because of where they are (<see cref="GenWhile"/>).</summary>
    private readonly HashSet<ForStmt> _nonEmpty = [];

    private void GenStmt(Stmt stmt)
    {
        Source(stmt);
        int from = _out.Count;
        GenStmtCode(stmt);
        if (_keeping.Count > 0) CheckKeeps(stmt, from);
    }

    private void GenStmtCode(Stmt stmt)
    {
        switch (stmt)
        {
            case VarDecl v:
                if (v.Init is not null) Assign(new NameExpr(v.Pos, v.Name) { Symbol = v.Symbol, Type = v.Symbol!.Type }, v.Init);
                break;
            case AssignStmt a: GenAssign(a); break;
            case ExprStmt e: GenCall((CallExpr)e.Expr); break;
            case BlockStmt b: GenBlock(b); break;
            case IfStmt i: GenIf(i); break;
            case WhileStmt w: GenWhile(w); break;
            case ForStmt f: GenFor(f); break;
            case MatchStmt m: GenMatch(m); break;
            case BreakStmt: Jmp(_loops[^1].Break); break;
            case ContinueStmt: Jmp(_loops[^1].Continue); break;
            case ReturnStmt r: GenReturn(r); break;
            case AsmStmt a: GenAsm(a); break;
            default: throw new InvalidOperationException(stmt.GetType().Name);
        }
    }

    private void GenIf(IfStmt i)
    {
        // A constant condition: only the branch taken exists (the checker did not keep what the other one uses).
        if (i.Condition.ConstValue is long constant)
        {
            if (constant != 0) GenBlock(i.Then);
            else if (i.Else is not null) GenStmt(i.Else);
            return;
        }
        var end = NewLabel("endif");
        if (i.Else is null)
        {
            GenCond(i.Condition, end, false);
            GenBlock(i.Then);
        }
        else
        {
            var otherwise = NewLabel("else");
            GenCond(i.Condition, otherwise, false);
            GenBlock(i.Then);
            if (!EndsWithJump()) Jmp(end);
            Place(otherwise);
            GenStmt(i.Else);
        }
        Place(end);
    }

    private void GenWhile(WhileStmt w)
    {
        if (w.Condition.ConstValue == 0) return;
        var top = NewLabel("while");
        var test = NewLabel("test");
        var end = NewLabel("wend");
        var previous = _previous.Current == w ? _previous.Before : null;
        // An endless loop never reaches the store after it.
        var held = w.Condition.ConstValue == 1 ? null : HoldInRegister(w.Body, w.Condition, null);
        _loopDepth++;
        if (w.Condition.ConstValue == 1)
        {
            Place(top);
            _loops.Add((end, top));
            GenBlock(w.Body);
            _loops.RemoveAt(_loops.Count - 1);
            Source(w);
            Jmp(top);
        }
        else
        {
            // Test at the bottom: one branch per pass instead of a branch and a jump. The first test
            // goes when the statement before the loop makes it true (F9); a loop that ends by counting
            // down to zero tests the Z flag of its decrement, after one test on entry.
            var decremented = DecrementTest(w);
            bool firstTrue = FirstTestTrue(w, previous);
            if (firstTrue) { }
            else if (decremented is not null) GenCond(w.Condition, end, false);
            else Jmp(test);
            if (w.Body.Code.FirstOrDefault() is ForStmt first && NonEmptyInside(w.Condition, first)) _nonEmpty.Add(first);
            Place(top);
            _loops.Add((end, test));
            int bodyStart = _out.Count;
            GenBlock(w.Body);
            CheckHeld(bodyStart, _out.Count, held, w.Body);
            _loops.RemoveAt(_loops.Count - 1);
            // The decrement must be the body's last instruction: dec v, or dex / dey for a held v.
            bool zFromDecrement = decremented is { } d && _out.Count > 0 && _out[^1] switch
            {
                Instr { Mnemonic: Dec, Operand: VarOperand o } => o == d,
                Instr { Mnemonic: Dex } => d.Variable.Register == Register.X && d.Offset == 0,
                Instr { Mnemonic: Dey } => d.Variable.Register == Register.Y && d.Offset == 0,
                _ => false,
            };
            Place(test);
            Source(w);
            if (zFromDecrement) Branch(Bne, top);
            else GenCond(w.Condition, top, true);
        }
        _loopDepth--;
        Place(end);
        ReleaseHeld(held, w.Body);
    }

    /// <summary>
    /// For <c>while v &gt; 0</c> or <c>while v != 0</c> (v a one-byte variable, or the high byte of a
    /// 16-bit one) whose body ends with <c>v -= 1</c> (<c>w -= 256</c>) and has no <c>continue</c>:
    /// the byte the decrement leaves Z for. Null otherwise.
    /// </summary>
    private static VarOperand? DecrementTest(WhileStmt w)
    {
        if (w.Condition is not BinaryExpr { Op: TokenKind.Greater or TokenKind.NotEq, Right.ConstValue: 0 } c
            || c.Left.Type is not { Size: 1, IsSigned: false } || ContinuesLoop(w.Body)) return null;
        (VariableSymbol? v, int offset, long step) = c.Left switch
        {
            NameExpr { Symbol: { Type.Size: 1 } s } => (s, 0, 1L),
            MemberExpr { Member: "hi", Target: NameExpr { Symbol: { Type.Size: 2 } s } } => (s, 1, 256L),
            _ => (null, 0, 0L),
        };
        if (v is null || v.IsVolatile || v.AliasOf is not null) return null;
        return w.Body.Code.LastOrDefault() is AssignStmt { Op: TokenKind.MinusEq, Target: NameExpr { Symbol: var t } } a
            && t == v && a.Value.ConstValue == step ? new VarOperand(v, offset) : null;
    }

    /// <summary>True if a <c>continue</c> in the code goes to this loop's test (not one inside a loop of its own).</summary>
    private static bool ContinuesLoop(Node node) => node switch
    {
        ContinueStmt => true,
        WhileStmt or ForStmt => false,
        _ => node.Children.Any(ContinuesLoop),
    };

    /// <summary>
    /// True if the statement before a <c>while</c> sets its variable to a constant that makes the
    /// first test true: <c>var n: u8 = 2</c> before <c>while n &lt; 16</c>.
    /// </summary>
    private static bool FirstTestTrue(WhileStmt w, Stmt? previous)
    {
        if (w.Condition is not BinaryExpr { Left: NameExpr { Symbol: { Type: { Size: 1, IsSigned: false }, IsVolatile: false } v }, Right.ConstValue: long r } c) return false;
        long? value = previous switch
        {
            VarDecl { Symbol: var s, Init.ConstValue: long i } when s == v => i,
            AssignStmt { Op: TokenKind.Eq, Target: NameExpr { Symbol: var s }, Value.ConstValue: long i } when s == v => i,
            _ => null,
        };
        if (value is not long l) return false;
        l &= 0xFF;
        return c.Op switch
        {
            TokenKind.Less => l < r, TokenKind.LessEq => l <= r, TokenKind.Greater => l > r, TokenKind.GreaterEq => l >= r,
            TokenKind.EqEq => l == r, TokenKind.NotEq => l != r, _ => false,
        };
    }

    /// <summary>
    /// True if <c>for i in 0..v</c>, the first statement of a body entered only when the loop's
    /// condition <c>v &gt; 0</c> (or <c>v != 0</c>) holds, cannot be empty.
    /// </summary>
    private static bool NonEmptyInside(Expr condition, ForStmt f) =>
        condition is BinaryExpr { Op: TokenKind.Greater or TokenKind.NotEq, Left: NameExpr { Symbol: { Type: { Size: 1, IsSigned: false }, IsVolatile: false } v }, Right.ConstValue: 0 }
        && f.From.ConstValue == 0 && !f.Inclusive && f.StepValue == 1 && !f.VariableStep && f.To is NameExpr { Symbol: var to } && to == v;

    /// <summary>Ends <see cref="HoldInRegister"/>: stores the variable if the loop assigns it, and frees the register.</summary>
    private void ReleaseHeld(VariableSymbol? held, Stmt body)
    {
        if (held?.Register is not { } reg) return;
        _reserved.Remove(reg);
        held.Register = null;
        if (AssignsVar(body, held)) Op(reg == Register.X ? Stx : Sty, Var(held));
    }

    /// <summary>The jump tables of <c>indexed match</c> statements, for the report.</summary>
    public List<(FunctionSymbol Function, SourcePos Pos, Label High, Label Low, int Span)> MatchTables { get; } = [];

    /// <summary>
    /// <c>match</c>: the value is loaded into A once. A plain match is a chain of <c>cmp</c> and
    /// branches (for an <c>i8</c>, after <c>eor #$80</c>, so that signed order becomes unsigned
    /// order); <c>indexed match</c> is a jump table with no range check (spec 5.2). Arm bodies follow,
    /// each ending with a jump to the end; the default arm (or the end) is where values that match
    /// nothing go.
    /// </summary>
    private void GenMatch(MatchStmt m)
    {
        var arms = m.Arms.Where(a => !a.IsDefault).ToList();
        var fallback = m.Arms.FirstOrDefault(a => a.IsDefault);
        var end = NewLabel("mend");
        if (arms.Count == 0)
        {
            GenA(m.Subject); // it may call a function or read a volatile register
            if (fallback is not null) GenBlock(fallback.Body);
            return;
        }
        var labels = arms.ToDictionary(a => a, _ => NewLabel("case"));
        var otherwise = fallback is null ? end : NewLabel("default");

        GenA(m.Subject);

        // Pattern values are biased by 128 for an i8, so that signed order is unsigned order.
        int bias = m.Subject.Type.IsSigned ? 128 : 0;
        var patterns = arms.SelectMany(a => a.Patterns.Select(p => (Arm: a, p.From, p.To))).ToList();
        int low = patterns.Min(p => p.From), high = patterns.Max(p => p.To);
        int span = high - low + 1;
        List<MatchArm> order;
        if (m.IsIndexed)
        {
            // Jump table: push the target address minus 1, high byte first, and rts to it. The value
            // minus the lowest pattern value indexes the tables (for an i8, v - low is the same byte
            // with or without the bias). A value outside the span is not checked (spec 5.2).
            int offset = (low - bias) & 0xFF;
            if (offset != 0) { Emit(Sec); Op(Sbc, Byte(offset)); }
            if (_reserved.Contains(Register.X) && _reserved.Contains(Register.Y))
                throw new CompileError(m.Pos, "An 'indexed match' needs X or Y to index its table, but both hold loop variables here. Use a plain 'match', or free a register");
            var reg = FreeIndexRegister(m.Pos);
            Emit(reg == Register.X ? Tax : Tay);
            var targets = new Label[span];
            for (int v = 0; v < span; v++)
                targets[v] = patterns.FirstOrDefault(p => p.From <= v + low && v + low <= p.To).Arm is { } arm ? labels[arm] : otherwise;
            var hiTable = NewLabel("jumphi");
            var loTable = NewLabel("jumplo");
            var mode = reg == Register.X ? AbsoluteX : AbsoluteY;
            Emit(Lda, mode, new LabelOperand(hiTable) { Check = MatchCheck(span, low - bias, bias != 0) }); Emit(Pha);
            Emit(Lda, mode, new LabelOperand(loTable) { Check = MatchCheck(span, low - bias, bias != 0) }); Emit(Pha);
            Emit(Rts);
            Place(hiTable); _out.Add(new LabelTable(targets, High: true, Offset: -1));
            Place(loTable); _out.Add(new LabelTable(targets, High: false, Offset: -1));
            MatchTables.Add((_ctx.Function, m.Pos, hiTable, loTable, span));
            order = arms;
        }
        else
        {
            if (bias != 0) Op(Eor, Byte(0x80));
            // Compare chain, in source order. The last test is inverted to go to the default, so the
            // last arm's body follows it directly.
            for (int i = 0; i < patterns.Count; i++)
            {
                var (arm, from, to) = patterns[i];
                if (i < patterns.Count - 1) Test(from, to, labels[arm]);
                else TestInverted(from, to, otherwise);
            }
            order = [arms[^1], .. arms.Take(arms.Count - 1)];
        }

        var bodies = order.Select(a => (labels[a], a.Body)).ToList();
        if (fallback is not null) bodies.Add((otherwise, fallback.Body));
        for (int i = 0; i < bodies.Count; i++)
        {
            Place(bodies[i].Item1);
            GenBlock(bodies[i].Body);
            if (i < bodies.Count - 1 && !EndsWithJump()) Jmp(end);
        }
        Place(end);

        // Branches to target when A (unsigned) is in from..to.
        void Test(int from, int to, Label target)
        {
            if (from == to) { Op(Cmp, Byte(from)); Branch(Beq, target); }
            else if (from == 0 && to == 255) Jmp(target);
            else if (from == 0) { Op(Cmp, Byte(to + 1)); Branch(Bcc, target); }
            else if (to == 255) { Op(Cmp, Byte(from)); Branch(Bcs, target); }
            else
            {
                var skip = NewLabel("next");
                Op(Cmp, Byte(from)); Branch(Bcc, skip);
                Op(Cmp, Byte(to + 1)); Branch(Bcc, target);
                Place(skip);
            }
        }

        // Branches to target when A is not in from..to, and falls through when it is.
        void TestInverted(int from, int to, Label target)
        {
            if (from == to) { Op(Cmp, Byte(from)); Branch(Bne, target); }
            else if (from == 0 && to == 255) { }
            else if (from == 0) { Op(Cmp, Byte(to + 1)); Branch(Bcs, target); }
            else if (to == 255) { Op(Cmp, Byte(from)); Branch(Bcc, target); }
            else
            {
                Op(Cmp, Byte(from)); Branch(Bcc, target);
                Op(Cmp, Byte(to + 1)); Branch(Bcs, target);
            }
        }
    }

    private void GenReturn(ReturnStmt r)
    {
        if (r.Value is null)
        {
            ReturnJump();
            return;
        }
        var value = r.Value;
        switch (_ctx.Function.ReturnRegister)
        {
            case Register.A: GenA(value); break;
            case Register.X: GenA(value); Emit(Tax); break;
            case Register.Y: GenA(value); Emit(Tay); break;
            case Register.AX or Register.AY or Register.XY:
            {
                var pair = Simple2(value);
                VariableSymbol? temp = null;
                if (pair is null)
                {
                    temp = AcquireTemp(2);
                    Gen16(value, new Loc(temp));
                    pair = (Var(temp), Var(temp, 1));
                }
                var (lo, hi) = pair.Value;
                switch (_ctx.Function.ReturnRegister)
                {
                    case Register.AX: Op(Ldx, hi); Op(Lda, lo); break;
                    case Register.AY: Op(Ldy, hi); Op(Lda, lo); break;
                    default: Op(Ldx, lo); Op(Ldy, hi); break;
                }
                if (temp is not null) Release(temp);
                break;
            }
            case Register.Carry or Register.Zero or Register.Negative:
            {
                Mnemonic setTrue, setFalse;
                Operand? trueOp = null, falseOp = null;
                if (_ctx.Function.ReturnRegister == Register.Carry) { setTrue = Sec; setFalse = Clc; }
                else if (_ctx.Function.ReturnRegister == Register.Zero) { setTrue = Lda; trueOp = Byte(0); setFalse = Lda; falseOp = Byte(1); }
                else { setTrue = Lda; trueOp = Byte(0x80); setFalse = Lda; falseOp = Byte(0); }

                void Set(Mnemonic m, Operand? op) { if (op is null) Emit(m); else Op(m, op); }

                if (value.ConstValue is long c)
                {
                    if (c != 0) Set(setTrue, trueOp); else Set(setFalse, falseOp);
                }
                else if (_ctx.Function.ReturnRegister == Register.Carry && CompareIntoCarry(value))
                {
                    // The compare left the result in the carry.
                }
                else
                {
                    var isTrue = NewLabel("rtrue");
                    GenCond(value, isTrue, true);
                    Set(setFalse, falseOp);
                    ReturnJump();
                    Place(isTrue);
                    Set(setTrue, trueOp);
                }
                break;
            }
        }
        ReturnJump();
    }

    /// <summary>
    /// An unsigned one-byte <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c> or <c>&gt;=</c> computed straight into the
    /// carry, with no branches: <c>l &gt;= r</c> is <c>lda l / cmp r</c>, <c>l &gt; r</c> is
    /// <c>lda l / clc / sbc r</c> (C is set when l - r - 1 does not borrow), or <c>cmp #c+1</c> for a
    /// constant; <c>&lt;</c> and <c>&lt;=</c> swap the operands. False (nothing emitted) for anything else.
    /// </summary>
    private bool CompareIntoCarry(Expr value)
    {
        if (value is not BinaryExpr { Op: TokenKind.Less or TokenKind.LessEq or TokenKind.Greater or TokenKind.GreaterEq } b
            || b.Left.Type is not { Size: 1, IsSigned: false } || b.Right.Type is not { Size: 1, IsSigned: false })
            return false;
        if (InSourceOrder(b.Left, b.Right, e => b.Left = e, () => CompareIntoCarry(value))) return true;
        Expr l = b.Left, r = b.Right;
        var op = b.Op;
        if (op is TokenKind.Less or TokenKind.LessEq)
        {
            (l, r) = (r, l);
            op = op == TokenKind.Less ? TokenKind.Greater : TokenKind.GreaterEq;
        }
        if (op == TokenKind.Greater && r.ConstValue is long c && c < 255)
        {
            r = new NumberExpr(r.Pos, c + 1) { Type = r.Type, ConstValue = c + 1 };
            op = TokenKind.GreaterEq;
        }

        VariableSymbol? temp = null;
        Action<Mnemonic> use;
        if (Simple1(r) is { } rop) use = m => Op(m, rop);
        else if (IndexedOperand(r) is { } element) use = m => element(m, 0);
        else
        {
            temp = AcquireTemp(1);
            GenA(r);
            Op(Sta, Var(temp));
            use = m => Op(m, Var(temp));
        }
        if (op == TokenKind.GreaterEq && l is NameExpr { Symbol.Register: { } reg } && Simple1(r) is { } simple)
            Op(reg == Register.X ? Cpx : Cpy, simple);
        else
        {
            GenA(l);
            if (op == TokenKind.GreaterEq) use(Cmp);
            else { Emit(Clc); use(Sbc); }
        }
        if (temp is not null) Release(temp);
        return true;
    }

    // ---------- Assignment ----------

    private void GenAssign(AssignStmt a)
    {
        if (a.Op == TokenKind.Eq)
        {
            Assign(a.Target, a.Value);
            return;
        }
        // The shorter forms, in order (spec 11).
        if (StepHeldRegister(a) || StepMemory(a) || StepByteTwice(a) || StepHighByte(a) || StepElementInX(a) || ShiftInPlace(a)) return;

        var target = a.Target;
        if (ContainsCall(target))
            throw new CompileError(a.Pos, "Compound assignment to an element whose index calls a function is not supported. Store the index in a variable first");

        if (CompoundThroughPointer(a) || CompoundAtExpensiveIndex(a)) return;

        var op = a.Op switch
        {
            TokenKind.PlusEq => TokenKind.Plus, TokenKind.MinusEq => TokenKind.Minus, TokenKind.AmpEq => TokenKind.Amp,
            TokenKind.PipeEq => TokenKind.Pipe, TokenKind.CaretEq => TokenKind.Caret, TokenKind.ShlEq => TokenKind.Shl,
            _ => TokenKind.Shr,
        };
        Assign(target, new BinaryExpr(a.Pos, op, target, a.Value) { Type = target.Type });
    }

    /// <summary>x += 1 / x -= 1 on a variable held in a register for a loop: inx / dex / iny / dey; x += 2 / x -= 2 twice (spec 11). False when it does not apply.</summary>
    private bool StepHeldRegister(AssignStmt a)
    {
        var target = a.Target;
        if (!(a.Op is TokenKind.PlusEq or TokenKind.MinusEq && a.Value.ConstValue is 1 or 2
            && target is NameExpr { Symbol.Register: Register.X or Register.Y } held))
            return false;
        bool x = held.Symbol!.Register == Register.X;
        for (long k = 0; k < a.Value.ConstValue; k++) Emit(a.Op == TokenKind.PlusEq ? (x ? Inx : Iny) : (x ? Dex : Dey));
        return true;
    }

    /// <summary>x += 1 / x -= 1 on memory: inc / dec. False when it does not apply.</summary>
    private bool StepMemory(AssignStmt a)
    {
        var target = a.Target;
        bool byOne = a.Value.ConstValue == 1 && a.Op is TokenKind.PlusEq or TokenKind.MinusEq;
        if (!(byOne && target is NameExpr { Symbol: { InMemory: true } v }))
            return false;
        if (v.Type.Size == 1)
        {
            Op(a.Op == TokenKind.PlusEq ? Inc : Dec, Var(v));
        }
        else if (v.IsVolatile)
        {
            // inc / bne / inc would write the high byte only on a carry, and lda / bne / dec / dec would read
            // the low byte twice: every byte is read and written once (spec 4), so the add or subtraction instead.
            Assign(target, new BinaryExpr(a.Pos, a.Op == TokenKind.PlusEq ? TokenKind.Plus : TokenKind.Minus, target, a.Value) { Type = target.Type });
        }
        else if (a.Op == TokenKind.PlusEq)
        {
            var done = NewLabel("inc");
            Op(Inc, Var(v));
            Branch(Bne, done);
            Op(Inc, Var(v, 1));
            Place(done);
        }
        else
        {
            var done = NewLabel("dec");
            Op(Lda, Var(v));
            Branch(Bne, done);
            Op(Dec, Var(v, 1));
            Place(done);
            Op(Dec, Var(v));
        }
        return true;
    }

    /// <summary>x += 2 / x -= 2 on a byte in memory: two inc / dec, shorter than the add at the same cycles (spec 11). Not on a volatile byte, where the value in between would be written to the hardware. False when it does not apply.</summary>
    private bool StepByteTwice(AssignStmt a)
    {
        var target = a.Target;
        if (!(a.Op is TokenKind.PlusEq or TokenKind.MinusEq && a.Value.ConstValue == 2
            && target is NameExpr { Symbol: { InMemory: true, Type.Size: 1, IsVolatile: false } v2 }))
            return false;
        Op(a.Op == TokenKind.PlusEq ? Inc : Dec, Var(v2));
        Op(a.Op == TokenKind.PlusEq ? Inc : Dec, Var(v2));
        return true;
    }

    /// <summary>w += 256 / w -= 256 on a 16-bit variable: inc w+1 / dec w+1; another multiple of 256 adds to the high byte only. Not on a volatile variable, whose low byte is read and written too (spec 4). False when it does not apply.</summary>
    private bool StepHighByte(AssignStmt a)
    {
        var target = a.Target;
        if (!(a.Op is TokenKind.PlusEq or TokenKind.MinusEq && target is NameExpr { Symbol: { InMemory: true, Type.Size: 2, IsVolatile: false } wv }
            && a.Value.ConstValue is long whole && (whole & 0xFF) == 0 && whole is > 0 and <= 0xFF00))
            return false;
        long k = whole >> 8;
        if (k == 1) Op(a.Op == TokenKind.PlusEq ? Inc : Dec, Var(wv, 1));
        else
        {
            Op(Lda, Var(wv, 1));
            CarryFor(a.Op == TokenKind.PlusEq ? TokenKind.Plus : TokenKind.Minus);
            Op(a.Op == TokenKind.PlusEq ? Adc : Sbc, Byte(k));
            Op(Sta, Var(wv, 1));
        }
        return true;
    }

    /// <summary>a[i] += 1 / a[i] -= 1 with the index in X: inc a,x / dec a,x (there is no inc a,y). False when it does not apply.</summary>
    private bool StepElementInX(AssignStmt a)
    {
        var target = a.Target;
        bool byOne = a.Value.ConstValue == 1 && a.Op is TokenKind.PlusEq or TokenKind.MinusEq;
        if (!(byOne && target is IndexExpr { Type.Size: 1 } incx && !IsPointerIndex(incx) && incx.Index.ConstValue is null
            && FoldIndexOffset(incx.Index, ArrayOf(incx)) is var (incIndex, incShift) && IndexIsCheap(incIndex)
            && (incIndex is NameExpr { Symbol.Register: Register.X } || (incIndex is not NameExpr { Symbol.Register: not null } && !_reserved.Contains(Register.X)))))
            return false;
        var incReg = LoadIndex(incIndex);
        CheckRegister(incReg, Register.X, "the index of inc a,x loads into X");
        Emit(a.Op == TokenKind.PlusEq ? Inc : Dec, AbsoluteX, Indexed(ArrayOf(incx), 0, incShift, incIndex));
        return true;
    }

    /// <summary>Shifts of a memory byte: shift in place. A volatile byte only by one, so no value in between is written. False when it does not apply.</summary>
    private bool ShiftInPlace(AssignStmt a)
    {
        var target = a.Target;
        if (!(a.Op is TokenKind.ShlEq or TokenKind.ShrEq && target is NameExpr { Symbol: { InMemory: true } sv }
            && sv.Type.Size == 1 && !(a.Op == TokenKind.ShrEq && sv.Type.IsSigned) && !(sv.IsVolatile && a.Value.ConstValue != 1)))
            return false;
        for (int i = 0; i < a.Value.ConstValue; i++) Op(a.Op == TokenKind.ShlEq ? Asl : Lsr, Var(sv));
        return true;
    }

    /// <summary>p[i] op= v: read, change and write the element with one Y load. False when it does not apply.</summary>
    private bool CompoundThroughPointer(AssignStmt a)
    {
        var target = a.Target;
        if (!(target is IndexExpr pix && IsPointerIndex(pix) && pix.Type.Size == 1
            && (a.Op is TokenKind.ShlEq or TokenKind.ShrEq || Simple1(a.Value) is not null)))
            return false;
        var (p, temp) = PointerBase(pix.Target);
        LoadPointerIndex(pix, 0);
        Emit(Lda, IndirectIndexed, p);
        ApplyCompound(a, pix.Type);
        Emit(Sta, IndirectIndexed, p);
        if (temp is not null) Release(temp);
        return true;
    }

    /// <summary>arr[expensive index] op= v: compute the index once (a shift or an offset table), read, change and write. False when it does not apply.</summary>
    private bool CompoundAtExpensiveIndex(AssignStmt a)
    {
        var target = a.Target;
        if (!(target is IndexExpr aix && !IsPointerIndex(aix) && aix.Type.Size == 1 && aix.Index.ConstValue is null
            && (a.Op is TokenKind.ShlEq or TokenKind.ShrEq || Simple1(a.Value) is not null)
            && FoldIndexOffset(aix.Index, ArrayOf(aix)) is var (index, shift) && !IndexIsCheap(index)))
            return false;
        var reg = LoadIndex(index);
        var mode = reg == Register.X ? AbsoluteX : AbsoluteY;
        Emit(Lda, mode, Indexed(ArrayOf(aix), 0, shift, index));
        ApplyCompound(a, aix.Type);
        Emit(Sta, mode, Indexed(ArrayOf(aix), 0, shift, index));
        return true;
    }

    /// <summary>
    /// A computed 16-bit value into volatile memory: each byte is written once, with no value in
    /// between, and read no more often than the source reads it (spec 4). <c>v = v + a</c> and
    /// <c>v = a + b</c> go one byte at a time (<c>lda / adc / sta</c>); anything else is computed into
    /// a temporary and copied, since <see cref="Gen16"/> uses its destination as scratch memory.
    /// </summary>
    private void AssignVolatile16(VariableSymbol v, Expr value)
    {
        var d = new Loc(v);
        if (value is BinaryExpr b && Binary16InPlace(b, (m, k) => Op(m, k == 0 ? d.Lo : d.Hi), v)) return;
        var t = AcquireTemp(2);
        Gen16(value, new Loc(t));
        Op(Lda, Var(t)); Op(Sta, d.Lo);
        Op(Lda, Var(t, 1)); Op(Sta, d.Hi);
        Release(t);
    }

    /// <summary>The operation of <c>x op= v</c> on the element in A: a shift by a constant, or an arithmetic or logic instruction with a simple operand.</summary>
    private void ApplyCompound(AssignStmt a, TernType type)
    {
        if (a.Op is TokenKind.ShlEq or TokenKind.ShrEq)
        {
            ShiftA(a.Op == TokenKind.ShlEq ? TokenKind.Shl : TokenKind.Shr, a.Value.ConstValue!.Value, type.IsSigned);
            return;
        }
        var binary = a.Op switch
        {
            TokenKind.PlusEq => TokenKind.Plus, TokenKind.MinusEq => TokenKind.Minus, TokenKind.AmpEq => TokenKind.Amp,
            TokenKind.PipeEq => TokenKind.Pipe, _ => TokenKind.Caret,
        };
        CarryFor(binary);
        Op(ArithMnemonic(binary), Simple1(a.Value)!);
    }

    private void Assign(Expr target, Expr value)
    {
        // Spec 5: the index's calls and volatile reads before the value's, then the value's parts in order.
        var tix = target as IndexExpr ?? (target as MemberExpr)?.Target as IndexExpr;
        if (tix is not null && InSourceOrder(tix.Index, value, i => tix.Index = i, () => Assign(target, value))) return;
        if (value is BinaryExpr vb && InSourceOrder(vb.Left, vb.Right, l => vb.Left = l, () => Assign(target, value))) return;
        if (target.Type.Size == 1)
        {
            if (target is IndexExpr pix && IsPointerIndex(pix) && StorePointerByteIndexFirst(pix, value)) return;
            // arr[expensive index] = v: load the index, then the value, so nothing has to be saved.
            if (target is IndexExpr aix && !IsPointerIndex(aix) && aix.Index.ConstValue is null && !ContainsCall(aix.Index) && Simple1(value) is { } simple
                && FoldIndexOffset(aix.Index, ArrayOf(aix)) is var (index, shift) && !IndexIsCheap(index))
            {
                var reg = LoadIndex(index);
                Op(Lda, simple);
                Emit(Sta, reg == Register.X ? AbsoluteX : AbsoluteY, Indexed(ArrayOf(aix), 0, shift, index));
                return;
            }
            GenA(value);
            StoreA(target);
            return;
        }

        switch (target)
        {
            case NameExpr { Symbol: { IsVolatile: true } v } when Simple2(value) is null:
                AssignVolatile16(v, value);
                break;
            case NameExpr { Symbol: { } v }:
                Gen16(value, new Loc(v));
                break;
            case IndexExpr ix when IsPointerIndex(ix):
                StorePointerWord(ix, value);
                break;
            case IndexExpr ix:
            {
                var arr = ArrayOf(ix);
                // a[i] = b[j] + c[k]: each byte straight into the element (lda b,x / adc c,x / sta a,x).
                // Not when i reads a: the element's index is loaded for each byte, after the low store.
                if (value is BinaryExpr bv && Simple2(value) is null && !ReadsVar(ix.Index, arr) && IndexedOperand(ix, 2) is { } element
                    && Binary16InPlace(bv, element, arr))
                    break;
                VariableSymbol? temp = null;
                var pair = Simple2(value);
                if (pair is null)
                {
                    temp = AcquireTemp(2);
                    Gen16(value, new Loc(temp));
                    pair = (Var(temp), Var(temp, 1));
                }
                var (lo, hi) = pair.Value;
                if (ix.Index.ConstValue is long ci)
                {
                    Op(Lda, lo); Op(Sta, Var(arr, (int)ci));
                    Op(Lda, hi); Op(Sta, Var(arr, HighTable(arr) + (int)ci));
                }
                else
                {
                    var (index, shift) = FoldIndexOffset(ix.Index, arr);
                    var reg = LoadIndex(index);
                    var mode = reg == Register.X ? AbsoluteX : AbsoluteY;
                    Op(Lda, lo); Emit(Sta, mode, Indexed(arr, 0, shift, index));
                    Op(Lda, hi); Emit(Sta, mode, Indexed(arr, HighTable(arr), shift, index));
                }
                if (temp is not null) Release(temp);
                break;
            }
            default:
                throw new InvalidOperationException();
        }
    }

    private void StoreA(Expr target)
    {
        switch (target)
        {
            case NameExpr { Symbol.Register: Register.X }:
                Emit(Tax);
                break;
            case NameExpr { Symbol.Register: Register.Y }:
                Emit(Tay);
                break;
            case NameExpr { Symbol: { } v }:
                Op(Sta, Var(v));
                break;
            case IndexExpr ix when IsPointerIndex(ix):
                PointerByte(ix, 0, Sta);
                break;
            case IndexExpr ix:
                StoreArrayByte(ix, 0);
                break;
            case MemberExpr m:
                int b = m.Member == "hi" ? 1 : 0;
                if (m.Target is NameExpr { Symbol: { } mv }) Op(Sta, Var(mv, b));
                else if (m.Target is IndexExpr pix && IsPointerIndex(pix)) PointerByte(pix, b, Sta);
                else StoreArrayByte((IndexExpr)m.Target, b);
                break;
            default:
                throw new InvalidOperationException();
        }
    }

    private void StoreArrayByte(IndexExpr ix, int byteIndex)
    {
        var arr = ArrayOf(ix);
        int table = byteIndex == 0 ? 0 : HighTable(arr);
        if (ix.Index.ConstValue is long ci)
        {
            Op(Sta, Var(arr, table + (int)ci));
            return;
        }
        var (index, shift) = FoldIndexOffset(ix.Index, arr);
        VariableSymbol? saved = null;
        if (!IndexIsCheap(index))
        {
            saved = AcquireTemp(1);
            Op(Sta, Var(saved));
        }
        int start = _out.Count;
        var reg = LoadIndex(index);
        if (saved is not null)
        {
            Op(Lda, Var(saved));
            Release(saved);
        }
        else CheckUnchanged(start, CpuState.A, "loading the index keeps A (IndexIsCheap)");
        Emit(Sta, reg == Register.X ? AbsoluteX : AbsoluteY, Indexed(arr, table, shift, index));
    }
}
