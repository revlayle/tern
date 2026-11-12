using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Backend;

/// <summary>for loops.</summary>
public sealed partial class CodeGen
{
    // ---------- for loops ----------

    private void GenFor(ForStmt f)
    {
        if (CounterMax(f) is long max) _counterMax[f.Symbol!] = max;
        GenForLoop(f);
        _counterMax.Remove(f.Symbol!);
    }

    /// <summary>
    /// The largest value a u8 counter takes in the body, from the loop header only: the checker's
    /// last value for constant bounds, else a constant end (counting up) or start (counting down),
    /// or a variable u8 end that is excluded (at most 254).
    /// </summary>
    private static long? CounterMax(ForStmt f)
    {
        if (f.Symbol!.Type is not { Size: 1, IsSigned: false }) return null;
        if (f.From.ConstValue is long from && f.To.ConstValue is long last)
            return Checker.LargestLoopValue(from, last, f.Inclusive, f.StepValue);
        if (f.StepValue < 0) return f.From.ConstValue;
        if (f.To.ConstValue is long to) return f.Inclusive ? to : to - 1;
        if (!f.Inclusive && f.To.Type is { Size: 1, IsSigned: false }) return 254;
        return null;
    }

    private void GenForLoop(ForStmt f)
    {
        var c = f.Symbol!;
        var type = c.Type;

        int step = f.StepValue;
        bool up = step > 0;
        int d = Math.Abs(step);
        long? from = f.From.ConstValue, to = f.To.ConstValue;
        bool inclusive = f.Inclusive;
        if (c.Register is null) ChooseCounterRegister(f, c);
        if (c.Register is null) ChooseOuterCounterRegister(f, c);

        var end = NewLabel("fend");
        var cont = NewLabel("fnext");
        var top = NewLabel("for");

        if (from is not null && to is not null && !f.VariableStep)
        {
            long span = up ? to.Value - from.Value : from.Value - to.Value;
            long count = inclusive ? (span < 0 ? 0 : span / d + 1) : (span <= 0 ? 0 : (span + d - 1) / d);
            if (count == 0)
            {
                _out.Add(new Comment("empty loop: no code"));
                return;
            }
            if (count == 1)
            {
                if (f.Variable is not null) SetCounter(c, f.From);
                _loops.Add((end, end));
                GenBlock(f.Body);
                _loops.RemoveAt(_loops.Count - 1);
                Place(end);
                return;
            }
            if (f.Variable is null && d == 1 && count <= (type.Size == 1 ? 256 : 65535))
            {
                GenRepeat(f, c, count, top, cont, end);
                return;
            }
        }

        if (f.VariableStep && from is not null && to is not null && (inclusive ? from > to : from >= to))
        {
            _out.Add(new Comment("empty loop: no code"));
            return;
        }

        // The end bound is evaluated once, before the loop.
        VariableSymbol? boundTemp = null;
        (Operand Lo, Operand Hi) bound;
        if (to is not null)
            bound = (Byte(to.Value), Byte(to.Value >> 8));
        else if (f.To is NameExpr { Symbol: { InMemory: true, AliasOf: null, Kind: VariableKind.Local or VariableKind.Parameter } bv }
                 && bv.Type.Size == type.Size && bv != c && !bv.AddressTaken && !AssignsVar(f.Body, bv))
            bound = (Var(bv), Var(bv, 1));
        else if (type.Size == 1 && f.To is MemberExpr { Target: NameExpr { Symbol: { InMemory: true, AliasOf: null, Kind: VariableKind.Local or VariableKind.Parameter } mv } }
                 && mv != c && !mv.AddressTaken && !AssignsVar(f.Body, mv) && Simple1(f.To) is { } half)
            bound = (half, Byte(0));   // n.lo of a local or parameter the body does not change: cpy n
        else
        {
            boundTemp = AcquireTemp(type.Size);
            if (type.Size == 1) { GenA(f.To); Op(Sta, Var(boundTemp)); }
            else Gen16(f.To, new Loc(boundTemp));
            bound = (Var(boundTemp), Var(boundTemp, 1));
        }

        // A variable step is evaluated once too: used in place if it is a local the body does not change.
        VariableSymbol? stepTemp = null;
        Operand? stepOperand = null;
        if (f.VariableStep)
        {
            if (f.Step is NameExpr { Symbol: { InMemory: true, AliasOf: null, Kind: VariableKind.Local or VariableKind.Parameter } sv }
                && sv.Type.Size == 1 && sv != c && !sv.AddressTaken && !AssignsVar(f.Body, sv))
                stepOperand = Var(sv);
            else
            {
                stepTemp = AcquireTemp(1);
                GenA(f.Step!); Op(Sta, Var(stepTemp));
                stepOperand = Var(stepTemp);
            }
        }

        // A signed loop with a larger step and a variable bound compares the counter with the end
        // flipped (eor #$80), so that cmp gives the signed order (spec 5.1).
        VariableSymbol? flippedTemp = null;
        Operand? flipped = null;
        bool signedTest = type.IsSigned && d > 1 && !(from is not null && to is not null);
        if (signedTest && type.Size == 1)
        {
            if (to is not null) flipped = Byte(to.Value ^ 0x80);
            else
            {
                flippedTemp = AcquireTemp(1);
                Op(Lda, bound.Lo); Op(Eor, Byte(0x80)); Op(Sta, Var(flippedTemp));
                flipped = Var(flippedTemp);
            }
        }

        SetCounter(c, f.From);
        if (c.Register is { } held) _reserved.Add(held);
        // N and Z still hold the start value after a plain ldx / ldy of it.
        bool flagsHoldStart = c.Register is not null && Simple1(f.From) is not null;

        // Skip the loop if the range is empty, unless the bounds prove it is not.
        long max = type.MaxValue;
        bool provablyNonEmpty = (from is not null && to is not null)
            || (inclusive && up && to == max) || (inclusive && !up && to == type.MinValue) || _nonEmpty.Contains(f);

        // A down-count to 0 tests only the start's zero (and sign), which a plain ldx n has set: test it
        // before a variable is loaded into the other index register (spec 11).
        bool downTo0 = !provablyNonEmpty && !up && to == 0;
        int checkFrom = _out.Count;
        if (downTo0 && type.IsSigned)
            SignedEmptyCheck(c, up, inclusive, to, bound, end, flagsHoldStart);
        else if (downTo0 && type.Size == 1)
        {
            // Unsigned n..0 step -1 is empty only when n is 0.
            if (!flagsHoldStart)
            {
                if (c.Register is { } reg) Op(reg == Register.X ? Cpx : Cpy, Byte(0));
                else Op(Lda, Var(c));
            }
            Branch(Beq, end);
        }
        int checkTo = _out.Count;

        var inRegister = HoldInRegister(f.Body, null, c);
        // The early check skips the held variable's load, so it must skip its store after the loop too.
        Label? skip = null;
        if (inRegister is not null && AssignsVar(f.Body, inRegister) && checkTo > checkFrom)
        {
            skip = NewLabel("fskip");
            for (int i = checkFrom; i < checkTo; i++)
                if (_out[i] is Instr { Operand: LabelOperand { Label: var target } } branch && target == end)
                    _out[i] = branch with { Operand = new LabelOperand(skip) };
        }

        if (provablyNonEmpty || checkTo > checkFrom) { }   // no check, or done above
        else if (type.IsSigned)
            SignedEmptyCheck(c, up, inclusive, to, bound, end, flagsHoldStart: false);
        else
        {
            var emptyIf = (up, inclusive) switch
            {
                (true, false) => TokenKind.GreaterEq,
                (true, true) => TokenKind.Greater,
                (false, false) => TokenKind.LessEq,
                _ => TokenKind.Less,
            };
            CounterBranch(c, emptyIf, bound, end);
        }

        Place(top);
        _loops.Add((end, cont));
        _loopDepth++;
        GenBlock(f.Body);
        _loopDepth--;
        _loops.RemoveAt(_loops.Count - 1);
        Place(cont);
        Source(f); // the step and the test belong to the loop's line (source map)

        if (type.IsSigned)
            StepSigned(c, up, d, inclusive, from, to, bound, flipped, top, end);
        else if (type.Size == 1 && d == 1 && !f.VariableStep)
            StepByOne(c, up, inclusive, from, to, bound, top, end);
        else
            StepGeneral(c, up, stepOperand ?? Byte(d), inclusive, to, bound, top, end);

        Place(end);
        ReleaseHeld(inRegister, f.Body);
        if (skip is not null) Place(skip);
        if (c.Register is { } r) _reserved.Remove(r);
        if (flippedTemp is not null) Release(flippedTemp);
        if (stepTemp is not null) Release(stepTemp);
        if (boundTemp is not null) Release(boundTemp);
    }

    /// <summary>
    /// Puts the counter of an innermost loop in X or Y, as if <c>using</c> were written, when the body
    /// allows it: a u8 counter stepping by one, no calls, asm blocks or loops in the body, its address
    /// not taken, no value returned in that register, and no register already held, so that the other
    /// index register stays free. Y if every pointer access in the body is <c>p[counter]</c> (and
    /// there is one), X otherwise: other pointer accesses compute Y themselves.
    /// </summary>
    private void ChooseCounterRegister(ForStmt f, VariableSymbol c)
    {
        if (c.Type.Size != 1 || Math.Abs(f.StepValue) != 1 || c.AddressTaken
            || Descendants(f.Body).Any(n => n is ForStmt or WhileStmt) || !KeepsIndexRegistersFree(f.Body)) return;
        Register reg;
        if (_reserved.Count == 0)
        {
            var (plain, other) = PointerIndexes(f.Body, c);
            reg = plain > 0 && other == 0 ? Register.Y : Register.X;
        }
        else if (_reserved.Count == 1 && IndexesHeld(f.Body, c, null, null) && !Descendants(f.Body).Any(n => n is MatchStmt || (n is IndexExpr ix && IsPointerIndex(ix))))
        {
            // M18 F13: beside a register already held (an outer counter in Y), the other one, when
            // every index in the body is this counter or already in a register.
            reg = _reserved.Contains(Register.X) ? Register.Y : Register.X;
        }
        else return;
        if (_fn.ReturnRegister is { } ret && ret.Uses(reg) && Descendants(f.Body).Any(n => n is ReturnStmt { Value: not null })) return;
        c.Register = reg;
        _fn.FrameVariables.Remove(c);
    }

    /// <summary>
    /// True if every index in <paramref name="body"/> needs no index register of its own: a constant,
    /// the counter <paramref name="c"/>, a variable already in a register, or <paramref name="outer"/>
    /// (about to be held in <paramref name="outerReg"/>). A pointer access needs its index in Y.
    /// </summary>
    private bool IndexesHeld(Node body, VariableSymbol c, VariableSymbol? outer, Register? outerReg)
    {
        Register? RegisterOf(Expr e) => e is NameExpr { Symbol: { } s } ? s == outer ? outerReg : s.Register : null;
        foreach (var ix in Descendants(body).OfType<IndexExpr>())
        {
            if (ix.Index.ConstValue is not null) continue;
            if (IsPointerIndex(ix))
            {
                if (!PlainPointerIndex(ix) || RegisterOf(ix.Index) != Register.Y) return false;
                continue;
            }
            var index = FoldIndexOffset(ix.Index, ArrayOf(ix)).Index;
            if (!(index is NameExpr { Symbol: var s } && s == c) && RegisterOf(index) is null) return false;
        }
        return true;
    }

    /// <summary>
    /// M18 F13: the counter of a loop with loops inside goes in a register too, when the body has
    /// one level of <c>for</c> loops and no call, asm block, <c>return</c> or <c>while</c>, and every
    /// use of the counter is one a register serves as well as memory. Y, leaving X to the inner
    /// loops (they take it if every index in them is in a register, see
    /// <see cref="ChooseCounterRegister"/>); X if a pointer access other than <c>p[counter]</c> needs
    /// Y. An inner loop with <c>using</c> must use the other register, and every index in it must
    /// already be in a register.
    /// </summary>
    private void ChooseOuterCounterRegister(ForStmt f, VariableSymbol c)
    {
        if (c.Type.Size != 1 || Math.Abs(f.StepValue) != 1 || c.AddressTaken || _reserved.Count > 0 || f.VariableStep) return;
        var nodes = Descendants(f.Body).ToList();
        var inner = nodes.OfType<ForStmt>().ToList();
        if (inner.Count == 0 || nodes.Any(n => n is WhileStmt or ReturnStmt or AsmStmt or CallExpr { Function: not null })
            || inner.Any(l => Descendants(l.Body).Any(n => n is ForStmt or WhileStmt))) return;
        var parents = new Dictionary<Node, Node>();
        foreach (var n in nodes) foreach (var child in Children(n)) parents[child] = n;
        if (nodes.OfType<NameExpr>().Any(n => n.Symbol == c && !Served(n, parents.GetValueOrDefault(n)))) return;

        bool pointersNeedY = nodes.OfType<IndexExpr>().Any(ix => IsPointerIndex(ix) && !(PlainPointerIndex(ix) && ix.Index is NameExpr { Symbol: var s } && s == c));
        var reg = pointersNeedY || inner.Any(l => l.Symbol!.Register == Register.Y) ? Register.X : Register.Y;
        foreach (var l in inner.Where(l => l.Symbol!.Register is not null))
            if (l.Symbol!.Register == reg || !IndexesHeld(l.Body, l.Symbol, c, reg)) return;
        c.Register = reg;
        _fn.FrameVariables.Remove(c);
    }

    /// <summary>
    /// M18 F7/F11: a one-byte variable of ordinary memory used in a loop is held in a free index
    /// register for the loop (spec 4: its values may be kept in registers; spec 7: the compiler
    /// chooses registers). It is loaded before the loop and, if the loop assigns it, stored after its
    /// end, where a <c>break</c> goes too. Only for a loop with no call, asm block, <c>return</c> or
    /// loop inside, so nothing else can read the memory or needs the register, and only when every
    /// use is one a register serves at least as well as memory: an array index (also <c>a[v - c]</c>),
    /// a plain read, the left operand of an operator or compare with a simple right one, an
    /// assignment. At least one use must save more than a cycle: an index (no <c>ldx v</c>) or
    /// <c>v += 1</c> / <c>v -= 1</c> (<c>inx</c> for <c>inc v</c>); a plain read only trades <c>lda</c>
    /// for <c>txa</c>, which may lose to what the optimizer does with memory. The variable with the
    /// most such uses, then the most uses; X unless every
    /// pointer access is <c>p[v]</c> (then Y). Beside a counter held in a register, only when every
    /// index in the body is the counter or the variable and nothing else needs an index register.
    /// Returns the variable, now held, or null.
    /// </summary>
    private VariableSymbol? HoldInRegister(Node body, Expr? condition, VariableSymbol? counter)
    {
        var nodes = Descendants(body).Concat(condition is null ? [] : Descendants(condition)).ToList();
        if (nodes.Skip(1).Any(n => n is ForStmt or WhileStmt or ReturnStmt or CallExpr { Function: not null } or AsmStmt)) return null;
        var heldCounter = counter?.Register is not null ? counter : null;
        if (heldCounter is null ? _reserved.Count > 0 : _reserved.Count != 1 || !_reserved.Contains(heldCounter.Register!.Value)) return null;
        if (heldCounter is not null && nodes.Any(n => n is MatchStmt || (n is IndexExpr ix && IsPointerIndex(ix)))) return null;

        var declared = nodes.OfType<VarDecl>().Select(d => d.Symbol).ToHashSet();
        bool Ordinary(VariableSymbol v) =>
            v.Type.Kind == TypeKind.U8 && v.AliasOf is null && v.InMemory && v != counter && !declared.Contains(v)
            && !v.AddressTaken && !v.IsVolatile && v.FixedAddress is null && v.Region is null && !v.IsData && !v.ReadOnly
            && !v.InOverlayArea && v.OverlayArea is null && v.Kind is VariableKind.Global or VariableKind.Local or VariableKind.Parameter;

        // Each use of a candidate, with the node it is part of.
        var parents = new Dictionary<Node, Node>();
        foreach (var n in nodes) foreach (var c in Children(n)) parents[c] = n;
        // Per candidate: uses where the register saves a load or an inc (an index, v += 1 or 2), and all uses.
        var uses = new Dictionary<VariableSymbol, (int Strong, int All)>();
        var bad = new HashSet<VariableSymbol>();
        foreach (var n in nodes.OfType<NameExpr>())
        {
            if (n.Symbol is not { } v || !Ordinary(v) || bad.Contains(v)) continue;
            var parent = parents.GetValueOrDefault(n);
            if (!Served(n, parent)) { bad.Add(v); uses.Remove(v); continue; }
            bool strong = parent is IndexExpr || (parent is AssignStmt { Op: TokenKind.PlusEq or TokenKind.MinusEq, Value.ConstValue: 1 or 2 } a && a.Target == n);
            var (st, all) = uses.GetValueOrDefault(v);
            uses[v] = (st + (strong ? 1 : 0), all + 1);
        }
        foreach (var v in uses.Where(u => u.Value.Strong == 0).Select(u => u.Key).ToList()) uses.Remove(v);
        // Beside a held counter: every index must be the counter or the variable.
        if (heldCounter is not null)
            foreach (var v in uses.Keys.ToList())
                if (!nodes.OfType<IndexExpr>().All(ix => ix.Index.ConstValue is not null
                        || FoldIndexOffset(ix.Index, ArrayOf(ix)).Index is NameExpr { Symbol: var s } && (s == heldCounter || s == v)))
                    uses.Remove(v);
        if (uses.Count == 0) return null;
        var held = uses.MaxBy(u => (u.Value.Strong, u.Value.All)).Key;

        Register reg;
        if (heldCounter is not null) reg = heldCounter.Register == Register.X ? Register.Y : Register.X;
        else
        {
            var (plain, other) = PointerIndexes(body, held);
            reg = plain > 0 && other == 0 ? Register.Y : Register.X;
        }
        Op(reg == Register.X ? Ldx : Ldy, Var(held));
        held.Register = reg;
        _reserved.Add(reg);
        return held;
    }

    /// <summary>A use of a variable that a register serves at least as well as memory (<see cref="HoldInRegister"/>).</summary>
    private bool Served(NameExpr use, Node? parent) => parent switch
    {
        IndexExpr ix => ix.Index == use && (!IsPointerIndex(ix) || PlainPointerIndex(ix)),
        // txa (or tya) where memory needs lda; the operator's own instruction takes the right operand.
        BinaryExpr b => b.Left == use && b.Type.Size == 1 && Simple1(b.Right) is not null
            && b.Op is TokenKind.Plus or TokenKind.Minus or TokenKind.Amp or TokenKind.Pipe or TokenKind.Caret
                or TokenKind.EqEq or TokenKind.NotEq or TokenKind.Less or TokenKind.LessEq or TokenKind.Greater or TokenKind.GreaterEq,
        AssignStmt a => a.Target == use ? a.Op == TokenKind.Eq || Simple1(a.Value) is not null || a.Value.ConstValue is not null : a.Value == use,
        VarDecl d => d.Init == use,
        MatchStmt m => m.Subject == use,
        _ => false,
    };
    private void GenRepeat(ForStmt f, VariableSymbol c, long count, Label top, Label cont, Label end)
    {
        _out.Add(new Comment($"repeat {count} times"));
        if (c.Type.Size == 1)
        {
            LoadCounterConst(c, count & 0xFF);
            if (c.Register is { } held) _reserved.Add(held);
            Place(top);
            _loops.Add((end, cont));
            _loopDepth++;
            GenBlock(f.Body);
            _loopDepth--;
            _loops.RemoveAt(_loops.Count - 1);
            Place(cont);
            Source(f);
            DecCounter(c);
            Branch(Bne, top);
            if (c.Register is { } r) _reserved.Remove(r);
        }
        else
        {
            Op(Lda, Byte(count)); Op(Sta, Var(c));
            Op(Lda, Byte(count >> 8)); Op(Sta, Var(c, 1));
            Place(top);
            _loops.Add((end, cont));
            _loopDepth++;
            GenBlock(f.Body);
            _loopDepth--;
            _loops.RemoveAt(_loops.Count - 1);
            Place(cont);
            Source(f);
            var noBorrow = NewLabel("nb");
            Op(Lda, Var(c)); Branch(Bne, noBorrow); Op(Dec, Var(c, 1));
            Place(noBorrow);
            Op(Dec, Var(c));
            Op(Lda, Var(c)); Op(Ora, Var(c, 1));
            Branch(Bne, top);
        }
        Place(end);
    }

    private void SetCounter(VariableSymbol c, Expr from)
    {
        if (c.Type.Size == 2)
        {
            Gen16(from, new Loc(c));
            return;
        }
        if (c.Register is { } reg)
        {
            if (Simple1(from) is { } op)
                Op(reg == Register.X ? Ldx : Ldy, op);
            else
            {
                GenA(from);
                Emit(reg == Register.X ? Tax : Tay);
            }
            return;
        }
        GenA(from);
        Op(Sta, Var(c));
    }

    private void LoadCounterConst(VariableSymbol c, long value)
    {
        if (c.Register == Register.X) Op(Ldx, Byte(value));
        else if (c.Register == Register.Y) Op(Ldy, Byte(value));
        else { Op(Lda, Byte(value)); Op(Sta, Var(c)); }
    }

    private void IncCounter(VariableSymbol c)
    {
        if (c.Register == Register.X) Emit(Inx);
        else if (c.Register == Register.Y) Emit(Iny);
        else Op(Inc, Var(c));
    }

    private void DecCounter(VariableSymbol c)
    {
        if (c.Register == Register.X) Emit(Dex);
        else if (c.Register == Register.Y) Emit(Dey);
        else Op(Dec, Var(c));
    }

    /// <summary>Compares an 8-bit counter with an operand: C = counter &gt;= op, Z = counter == op.</summary>
    private void CompareCounter(VariableSymbol c, Operand op)
    {
        if (c.Register == Register.X) Op(Cpx, op);
        else if (c.Register == Register.Y) Op(Cpy, op);
        else { Op(Lda, Var(c)); Op(Cmp, op); }
    }

    /// <summary>Branches to <paramref name="target"/> if <c>counter rel bound</c> (unsigned).</summary>
    private void CounterBranch(VariableSymbol c, TokenKind rel, (Operand Lo, Operand Hi) bound, Label target)
    {
        if (c.Type.Size == 1)
        {
            CompareCounter(c, bound.Lo);
            switch (rel)
            {
                case TokenKind.EqEq: Branch(Beq, target); break;
                case TokenKind.NotEq: Branch(Bne, target); break;
                case TokenKind.Less: Branch(Bcc, target); break;
                case TokenKind.GreaterEq: Branch(Bcs, target); break;
                case TokenKind.LessEq: Branch(Bcc, target); Branch(Beq, target); break;
                case TokenKind.Greater:
                {
                    var skip = NewLabel("eq");
                    Branch(Beq, skip);
                    Branch(Bcs, target);
                    Place(skip);
                    break;
                }
            }
            return;
        }

        (Operand Lo, Operand Hi) counter = (Var(c), Var(c, 1));
        var (l, r) = rel is TokenKind.Greater or TokenKind.LessEq ? (bound, counter) : (counter, bound);
        rel = rel switch { TokenKind.Greater => TokenKind.Less, TokenKind.LessEq => TokenKind.GreaterEq, _ => rel };
        switch (rel)
        {
            case TokenKind.EqEq:
            {
                var skip = NewLabel("ne");
                Op(Lda, l.Lo); Op(Cmp, r.Lo); Branch(Bne, skip);
                Op(Lda, l.Hi); Op(Cmp, r.Hi); Branch(Beq, target);
                Place(skip);
                break;
            }
            case TokenKind.NotEq:
                Op(Lda, l.Lo); Op(Cmp, r.Lo); Branch(Bne, target);
                Op(Lda, l.Hi); Op(Cmp, r.Hi); Branch(Bne, target);
                break;
            default:
                Op(Lda, l.Lo); Op(Cmp, r.Lo);
                Op(Lda, l.Hi); Op(Sbc, r.Hi);
                Branch(rel == TokenKind.Less ? Bcc : Bcs, target);
                break;
        }
    }

    /// <summary>The end of an 8-bit loop with step +1 or -1, using the shortest exit test.</summary>
    private void StepByOne(VariableSymbol c, bool up, bool inclusive, long? from, long? to,
        (Operand Lo, Operand Hi) bound, Label top, Label end)
    {
        if (up)
        {
            if (!inclusive)
            {
                IncCounter(c);
                CompareCounter(c, bound.Lo);
                Branch(Bne, top);
            }
            else if (to == 255)
            {
                IncCounter(c);
                Branch(Bne, top);
            }
            else if (to is not null)
            {
                IncCounter(c);
                CompareCounter(c, Byte(to.Value + 1));
                Branch(Bne, top);
            }
            else
            {
                // Test before stepping, so a bound of 255 cannot wrap around.
                CompareCounter(c, bound.Lo);
                Branch(Beq, end);
                IncCounter(c);
                Branch(Bne, top); // never zero here, so always taken
            }
            return;
        }

        if (!inclusive)
        {
            DecCounter(c);
            if (to != 0) CompareCounter(c, bound.Lo);
            Branch(Bne, top);
        }
        else if (to == 0 && from is <= 127)
        {
            DecCounter(c);
            Branch(Bpl, top);
        }
        else if (to == 0 && from is not null && from != 255)
        {
            DecCounter(c);
            CompareCounter(c, Byte(0xFF));
            Branch(Bne, top);
        }
        else if (to is > 0)
        {
            DecCounter(c);
            CompareCounter(c, Byte(to.Value - 1));
            Branch(Bne, top);
        }
        else
        {
            CompareCounter(c, bound.Lo);
            Branch(Beq, end);
            DecCounter(c);
            Jmp(top);
        }
    }

    /// <summary>
    /// Before a signed loop with variable bounds: leaves if the range is empty (spec 5.1). <c>n..=0</c>
    /// and <c>n..0</c> test the sign (and zero) of the start, which a plain <c>ldx n</c> has already set
    /// when <paramref name="flagsHoldStart"/>; other ranges compare signed, once.
    /// </summary>
    private void SignedEmptyCheck(VariableSymbol c, bool up, bool inclusive, long? to, (Operand Lo, Operand Hi) bound, Label end, bool flagsHoldStart)
    {
        if (!up && to == 0)
        {
            if (c.Type.Size == 2)
            {
                Op(Lda, Var(c, 1));
                Branch(Bmi, end);
                if (!inclusive) { Op(Ora, Var(c)); Branch(Beq, end); }
                return;
            }
            if (!flagsHoldStart)
            {
                if (c.Register is { } reg) Op(reg == Register.X ? Cpx : Cpy, Byte(0));
                else Op(Lda, Var(c));
            }
            if (!inclusive) Branch(Beq, end);
            Branch(Bmi, end);
            return;
        }
        var emptyIf = (up, inclusive) switch
        {
            (true, false) => TokenKind.GreaterEq,
            (true, true) => TokenKind.Greater,
            (false, false) => TokenKind.LessEq,
            _ => TokenKind.Less,
        };
        SignedCounterBranch(c, emptyIf, bound, end);
    }

    /// <summary>Branches to <paramref name="target"/> if <c>counter rel bound</c>, in signed order: the subtraction's N, corrected by V.</summary>
    private void SignedCounterBranch(VariableSymbol c, TokenKind rel, (Operand Lo, Operand Hi) bound, Label target)
    {
        var sign = NewLabel("sgn");
        if (c.Type.Size == 1)
        {
            var skip = NewLabel("eq");
            if (c.Register is { } reg) Emit(reg == Register.X ? Txa : Tya);
            else Op(Lda, Var(c));
            Emit(Sec);
            Op(Sbc, bound.Lo);
            if (rel == TokenKind.LessEq) Branch(Beq, target);
            if (rel == TokenKind.Greater) Branch(Beq, skip);
            Branch(Bvc, sign);
            Op(Eor, Byte(0x80));
            Place(sign);
            Branch(rel is TokenKind.Less or TokenKind.LessEq ? Bmi : Bpl, target);
            Place(skip);
            return;
        }
        (Operand Lo, Operand Hi) counter = (Var(c), Var(c, 1));
        var (l, r) = rel is TokenKind.Greater or TokenKind.LessEq ? (bound, counter) : (counter, bound);
        bool less = rel is TokenKind.Less or TokenKind.Greater;   // l < r
        Op(Lda, l.Lo); Op(Cmp, r.Lo);
        Op(Lda, l.Hi); Op(Sbc, r.Hi);
        Branch(Bvc, sign);
        Op(Eor, Byte(0x80));
        Place(sign);
        Branch(less ? Bmi : Bpl, target);
    }

    /// <summary>
    /// The end of a loop with an i8 or i16 counter (spec 5.1). A step of ±1 is the unsigned code:
    /// tested for equality. A larger step with constant bounds compares with the value after the
    /// last; with a variable bound it leaves when the step overflows (V), then compares signed.
    /// </summary>
    private void StepSigned(VariableSymbol c, bool up, int d, bool inclusive, long? from, long? to,
        (Operand Lo, Operand Hi) bound, Operand? flipped, Label top, Label end)
    {
        bool wide = c.Type.Size == 2;
        void BranchNotEq(long v, Label target) => CounterBranch(c, TokenKind.NotEq, (Byte(v), Byte(v >> 8)), target);

        if (d == 1)
        {
            if (inclusive && to is null)
            {
                // Test before stepping, so an end at the type's limit cannot wrap around.
                CounterBranch(c, TokenKind.EqEq, bound, end);
                StepByOneSigned(c, up);
                Jmp(top);
                return;
            }
            StepByOneSigned(c, up);
            if (!inclusive && !up && to == 0 && !wide) Branch(Bne, top);   // dec sets Z
            else if (!inclusive) CounterBranch(c, TokenKind.NotEq, bound, top);
            else if (!up && to == 0)
            {
                if (wide) Op(Lda, Var(c, 1));
                Branch(Bpl, top);
            }
            else BranchNotEq(up ? to!.Value + 1 : to!.Value - 1, top);
            return;
        }

        if (from is not null && to is not null)
        {
            // Constant bounds: the counter steps through distinct values, so the value after the last ends it.
            long last = up
                ? Checker.LargestLoopValue(from.Value, to.Value, inclusive, d)!.Value
                : from.Value - (from.Value - (inclusive ? to.Value : to.Value + 1)) / d * d;
            AddStep(c, up, d);
            BranchNotEq(up ? last + d : last - d, top);
            return;
        }

        var continueIf = (up, inclusive) switch
        {
            (true, false) => TokenKind.Less,
            (true, true) => TokenKind.LessEq,
            (false, false) => TokenKind.Greater,
            _ => TokenKind.GreaterEq,
        };
        if (wide)
        {
            AddStep(c, up, d);
            Branch(Bvs, end);
            SignedCounterBranch(c, continueIf, bound, top);
            return;
        }
        // txa / clc / adc #d / bvs end / tax / eor #$80 / cmp e / bcc top
        if (c.Register is { } reg) Emit(reg == Register.X ? Txa : Tya);
        else Op(Lda, Var(c));
        if (up) { Emit(Clc); Op(Adc, Byte(d)); } else { Emit(Sec); Op(Sbc, Byte(d)); }
        Branch(Bvs, end);
        if (c.Register is { } r) Emit(r == Register.X ? Tax : Tay);
        else Op(Sta, Var(c));
        Op(Eor, Byte(0x80));
        Op(Cmp, flipped!);
        switch (continueIf)
        {
            case TokenKind.Less: Branch(Bcc, top); break;
            case TokenKind.LessEq: Branch(Bcc, top); Branch(Beq, top); break;
            case TokenKind.Greater: Branch(Beq, end); Branch(Bcs, top); break;
            default: Branch(Bcs, top); break;
        }
    }

    /// <summary>Adds or subtracts a constant step to a signed counter; for a 16-bit one V is the high byte's overflow.</summary>
    private void AddStep(VariableSymbol c, bool up, int d)
    {
        if (c.Register is { } reg)
        {
            Emit(reg == Register.X ? Txa : Tya);
            if (up) { Emit(Clc); Op(Adc, Byte(d)); } else { Emit(Sec); Op(Sbc, Byte(d)); }
            Emit(reg == Register.X ? Tax : Tay);
            return;
        }
        Op(Lda, Var(c));
        if (up) { Emit(Clc); Op(Adc, Byte(d)); } else { Emit(Sec); Op(Sbc, Byte(d)); }
        Op(Sta, Var(c));
        if (c.Type.Size == 2)
        {
            Op(Lda, Var(c, 1));
            Op(up ? Adc : Sbc, Byte(0));
            Op(Sta, Var(c, 1));
        }
    }

    /// <summary>Steps a signed counter by one: inx / inc c, or the 16-bit inc / bne / inc and lda / bne / dec / dec.</summary>
    private void StepByOneSigned(VariableSymbol c, bool up)
    {
        if (c.Type.Size == 1)
        {
            if (up) IncCounter(c); else DecCounter(c);
            return;
        }
        var done = NewLabel(up ? "inc" : "dec");
        if (up)
        {
            Op(Inc, Var(c)); Branch(Bne, done); Op(Inc, Var(c, 1));
            Place(done);
            return;
        }
        Op(Lda, Var(c)); Branch(Bne, done); Op(Dec, Var(c, 1));
        Place(done);
        Op(Dec, Var(c));
    }

    /// <summary>The end of a loop with a larger step or a 16-bit counter.</summary>
    private void StepGeneral(VariableSymbol c, bool up, Operand step, bool inclusive, long? to, (Operand Lo, Operand Hi) bound, Label top, Label end)
    {
        if (c.Register is { } reg)
        {
            Emit(reg == Register.X ? Txa : Tya);
            if (up) { Emit(Clc); Op(Adc, step); } else { Emit(Sec); Op(Sbc, step); }
            Emit(reg == Register.X ? Tax : Tay);
        }
        else
        {
            Op(Lda, Var(c));
            if (up) { Emit(Clc); Op(Adc, step); } else { Emit(Sec); Op(Sbc, step); }
            Op(Sta, Var(c));
            if (c.Type.Size == 2)
            {
                Op(Lda, Var(c, 1));
                Op(up ? Adc : Sbc, Byte(0));
                Op(Sta, Var(c, 1));
            }
        }
        // Up to 255 inclusive: the carry is the whole test, so the loop goes on while there is none.
        if (up && inclusive && c.Type.Size == 1 && to == 255)
        {
            Branch(Bcc, top);
            return;
        }

        // Leave if the counter went past the end of its type.
        Branch(up ? Bcs : Bcc, end);

        var continueIf = (up, inclusive) switch
        {
            (true, false) => TokenKind.Less,
            (true, true) => TokenKind.LessEq,
            (false, false) => TokenKind.Greater,
            _ => TokenKind.GreaterEq,
        };
        CounterBranch(c, continueIf, bound, top);
    }
}
