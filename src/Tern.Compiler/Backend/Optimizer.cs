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
public static class Optimizer
{
    public static void Run(List<AsmItem> code, FunctionSymbol function)
    {
        ThreadJumps(code);
        RemoveUnreachable(code);
        while (RemoveRedundant(code, function) | FoldShiftTests(code, function)) { }
    }

    /// <summary>The index of each label's definition.</summary>
    private static Dictionary<Label, int> Positions(List<AsmItem> code)
    {
        var positions = new Dictionary<Label, int>();
        for (int k = 0; k < code.Count; k++)
            if (code[k] is LabelDef d) positions[d.Label] = k;
        return positions;
    }

    private static bool FromAsm(Instr i) => i.AsmPos is not null;

    /// <summary>
    /// A jump or branch to a label whose code is just another <c>jmp</c> goes to that jump's target
    /// instead (<c>jmp endif</c> where <c>endif:</c> is followed by <c>jmp end</c>: an if at the end of a
    /// match arm). The skipped jump may become unreachable and is removed.
    /// </summary>
    private static void ThreadJumps(List<AsmItem> code)
    {
        var positions = Positions(code);

        // The label a jump to this one leads to, if its code is only a jmp.
        Label? Onward(Label label)
        {
            if (!positions.TryGetValue(label, out int p)) return null;
            while (p < code.Count && code[p] is LabelDef or Comment or SourceLine) p++;
            return p < code.Count && code[p] is Instr { Mnemonic: Mnemonic.Jmp, Mode: Absolute, Operand: LabelOperand { Offset: 0 } to } jump && !FromAsm(jump)
                ? to.Label : null;
        }

        for (int k = 0; k < code.Count; k++)
        {
            if (code[k] is not Instr i || FromAsm(i) || Target(i) is not { } target) continue;
            var final = target;
            for (int hops = 0; hops < 8 && Onward(final) is { } next && next != target; hops++) final = next;
            if (final != target) code[k] = i with { Operand = new LabelOperand(final) };
        }
    }

    private static bool IsBranch(Mnemonic m) => m is Bcc or Bcs or Beq or Bne or Bmi or Bpl or Bvc or Bvs;

    /// <summary>A jump or branch to a label in this code: the only way into a label the analysis can follow.</summary>
    private static Label? Target(Instr i) =>
        (IsBranch(i.Mnemonic) || (i.Mnemonic == Mnemonic.Jmp && i.Mode == Absolute)) && i.Operand is LabelOperand { Label: var l, Offset: 0 } ? l : null;

    /// <summary>True if execution can go on to the next item.</summary>
    private static bool FallsThrough(AsmItem item) => item switch
    {
        Instr i => i.Mnemonic is not (Mnemonic.Jmp or Rts or Rti),
        LabelTable or DataBytes => false,
        _ => true,
    };

    /// <summary>
    /// Labels that may be entered from somewhere the analysis cannot see: named other than by a jump
    /// or branch (a jump table, <c>#label.lo</c>, <c>lda table,x</c>).
    /// </summary>
    private static HashSet<Label> UnknownEntries(List<AsmItem> code)
    {
        var entries = new HashSet<Label>();
        foreach (var item in code)
        {
            switch (item)
            {
                case LabelTable t: entries.UnionWith(t.Labels); break;
                case Instr i when Target(i) is null:
                    if (i.Operand is LabelOperand lo) entries.Add(lo.Label);
                    if (i.Operand is ImmLabelByte lb) entries.Add(lb.Label);
                    break;
            }
        }
        return entries;
    }

    /// <summary>Removes instructions that no path reaches, e.g. the <c>rts</c> after an endless loop.</summary>
    private static void RemoveUnreachable(List<AsmItem> code)
    {
        var positions = Positions(code);
        var reached = new bool[code.Count];
        var work = new Stack<int>();
        work.Push(0);
        foreach (var l in UnknownEntries(code))
            if (positions.TryGetValue(l, out int p)) work.Push(p);
        while (work.Count > 0)
        {
            int k = work.Pop();
            if (k >= code.Count || reached[k]) continue;
            reached[k] = true;
            if (code[k] is Instr i && Target(i) is { } target && positions.TryGetValue(target, out int t)) work.Push(t);
            if (FallsThrough(code[k])) work.Push(k + 1);
        }
        for (int k = code.Count - 1; k >= 0; k--)
            if (!reached[k] && code[k] is Instr i && !FromAsm(i)) code.RemoveAt(k);
    }

    // ---------- Register contents ----------

    private enum Reg { A, X, Y }

    /// <summary>
    /// What is known before an instruction: for each register, the operands it equals (immediates
    /// and bytes of ordinary memory), and which register N and Z were last set from.
    /// </summary>
    private sealed class State
    {
        public readonly HashSet<Operand>[] Regs = [[], [], []];
        public Reg? Flags;

        public State Copy()
        {
            var s = new State { Flags = Flags };
            for (int r = 0; r < 3; r++) s.Regs[r].UnionWith(Regs[r]);
            return s;
        }

        /// <summary>Keeps only what both paths know. Returns true if this state changed.</summary>
        public bool Meet(State other)
        {
            bool changed = false;
            for (int r = 0; r < 3; r++)
            {
                int before = Regs[r].Count;
                Regs[r].IntersectWith(other.Regs[r]);
                changed |= Regs[r].Count != before;
            }
            if (Flags != other.Flags && Flags is not null) { Flags = null; changed = true; }
            return changed;
        }

        public void Forget(Func<Operand, bool> which)
        {
            foreach (var set in Regs) set.RemoveWhere(o => which(o));
        }

        public void Clear()
        {
            foreach (var set in Regs) set.Clear();
            Flags = null;
        }
    }

    /// <summary>
    /// "These two registers hold the same value", as left by a transfer, whatever that value is. It stays
    /// in both registers' sets until one of them is changed (which clears its set), so a transfer back
    /// can be removed, also around a loop. It is only ever in the sets of those two registers.
    /// </summary>
    private sealed record Same(Reg First, Reg Second) : Operand;

    private static Reg? Loaded(Mnemonic m) => m switch { Lda => Reg.A, Ldx => Reg.X, Ldy => Reg.Y, _ => null };
    private static Reg? Stored(Mnemonic m) => m switch { Sta => Reg.A, Stx => Reg.X, Sty => Reg.Y, _ => null };
    private static (Reg From, Reg To)? Transfer(Mnemonic m) => m switch
    {
        Tax => (Reg.A, Reg.X), Tay => (Reg.A, Reg.Y), Txa => (Reg.X, Reg.A), Tya => (Reg.Y, Reg.A), _ => null,
    };

    /// <summary>An operand whose value can be remembered: an immediate, or a byte of ordinary memory.</summary>
    private static bool Trackable(Instr i) => i.Mode switch
    {
        Immediate => i.Operand is Imm or ImmVarByte or ImmLabelByte,
        Absolute or ZeroPage => i.Operand is VarOperand { Variable.IsVolatile: false },
        _ => false,
    };

    /// <summary>
    /// Forgets what registers know about memory that a write to <paramref name="written"/> may change.
    /// Variables placed by the compiler never overlap within a function's view, except frames of the
    /// functions it calls (they may share memory with each other) and fixed-address variables (which
    /// may overlap each other).
    /// </summary>
    private static void ForgetWrite(State s, VariableSymbol written, FunctionSymbol function)
    {
        bool callee = written.Owner is { } o && o != function;
        bool fixedAt = written.FixedAddress is not null;
        if (fixedAt && written.Storage.DeclaredVolatile && !written.Storage.InVolatileArea)
        {
            // RAM that something else shares, e.g. an overlay's file (spec 8.5): like a write through a pointer.
            s.Forget(op => op is VarOperand);
            return;
        }
        s.Forget(op => op is VarOperand { Variable: var v } && (v == written
            || (callee && v.Owner is { } vo && vo != function)
            || (fixedAt && v.FixedAddress is not null)));
    }

    /// <summary>The state after <paramref name="i"/>, given the state before it.</summary>
    private static void Step(State s, Instr i, FunctionSymbol function, bool selfModifying)
    {
        if (selfModifying && FromAsm(i))
        {
            // Assembly that patches its own code (sta .label+1): a later instruction may not be what it says.
            s.Clear();
            return;
        }
        if (i.Mnemonic is Jsr or Brk)
        {
            // A call may change any register and any ordinary memory. Other instructions of an asm
            // block are followed like compiler-generated ones: they change what AsmEffects says.
            s.Clear();
            return;
        }
        var m = i.Mnemonic;
        if (Loaded(m) is Reg load)
        {
            s.Regs[(int)load].Clear();
            if (Trackable(i)) s.Regs[(int)load].Add(i.Operand!);
            s.Flags = load;
            return;
        }
        if (Transfer(m) is var (from, to))
        {
            // The registers now hold the same value, known or not.
            var same = new Same((Reg)Math.Min((int)from, (int)to), (Reg)Math.Max((int)from, (int)to));
            s.Regs[(int)from].Add(same);
            s.Regs[(int)to].Clear();
            // Other markers are not copied: "A and X are the same" says nothing about the register copied to.
            s.Regs[(int)to].UnionWith(s.Regs[(int)from].Where(o => o is not Same || o == same));
            s.Flags = to;
            return;
        }

        var (_, writes) = AsmEffects.Of(m, i.Mode);
        if (i.Mode != Accumulator && (AsmEffects.WritesMemory(m, i.Mode) || i.Mode is IndirectIndexed or IndexedIndirect))
        {
            if (i.Mode is Absolute or ZeroPage && i.Operand is VarOperand { Variable: var v })
            {
                ForgetWrite(s, v, function);
                if (Stored(m) is Reg st && Trackable(i)) s.Regs[(int)st].Add(i.Operand!);
            }
            else if (i.Mode is AbsoluteX or AbsoluteY or ZeroPageX or ZeroPageY && i.Operand is VarOperand { Variable: var array })
            {
                // An indexed write changes only the array (an index outside it is not supported,
                // provisional); fixed-address arrays may overlap other fixed-address variables.
                ForgetWrite(s, array, function);
            }
            else if (AsmEffects.WritesMemory(m, i.Mode) || Stored(m) is not null)
            {
                // Through a pointer, or at a fixed numeric address: any memory may change.
                s.Forget(op => op is VarOperand);
            }
        }

        foreach (var (reg, flag) in new[] { (Reg.A, CpuState.A), (Reg.X, CpuState.X), (Reg.Y, CpuState.Y) })
        {
            if (!writes.HasFlag(flag)) continue;
            // inx / dex / iny / dey keep a known immediate known.
            int delta = m is Inx or Iny ? 1 : m is Dex or Dey ? -1 : 0;
            var known = delta != 0 ? s.Regs[(int)reg].OfType<Imm>().FirstOrDefault() : null;
            s.Regs[(int)reg].Clear();
            if (known is not null) s.Regs[(int)reg].Add(new Imm((known.Value + delta) & 0xFF));
        }
        if (writes.HasFlag(CpuState.Z))
            s.Flags = m is Inx or Dex ? Reg.X : m is Iny or Dey ? Reg.Y
                : writes.HasFlag(CpuState.A) && m is not (Cmp or Bit) ? Reg.A : null;
        if (m is Plp or Rti) s.Flags = null;
    }

    /// <summary>The state before each item, from the start of the function and every jump into a label.</summary>
    private static State?[] Analyse(List<AsmItem> code, FunctionSymbol function)
    {
        // An asm instruction that writes to a code label changes the code itself.
        bool selfModifying = function.CodePatched || code.Any(item => item is Instr { AsmPos: not null, Operand: LabelOperand } w
            && (AsmEffects.WritesMemory(w.Mnemonic, w.Mode) || Stored(w.Mnemonic) is not null));
        var positions = Positions(code);
        var unknown = UnknownEntries(code);
        var before = new State?[code.Count + 1];
        before[0] = new State();
        foreach (var l in unknown)
            if (positions.TryGetValue(l, out int p)) before[p] = new State();

        var work = new Stack<int>();
        void Join(int k, State s)
        {
            if (before[k] is null) { before[k] = s.Copy(); work.Push(k); }
            else if (before[k]!.Meet(s)) work.Push(k);
        }
        for (int k = 0; k < code.Count; k++) if (before[k] is not null) work.Push(k);
        while (work.Count > 0)
        {
            int k = work.Pop();
            if (k >= code.Count) continue;
            var s = before[k]!.Copy();
            if (code[k] is LabelDef d && unknown.Contains(d.Label)) s.Clear();
            if (code[k] is Instr i)
            {
                Step(s, i, function, selfModifying);
                if (Target(i) is { } t && positions.TryGetValue(t, out int tp)) Join(tp, s);
            }
            if (FallsThrough(code[k])) Join(k + 1, s);
        }
        return before;
    }

    /// <summary>
    /// True if N or Z may be read after item <paramref name="k"/> before something sets both. Follows
    /// jumps and both ways of a branch; gives up (true) on anything it cannot follow, and after a
    /// fixed number of steps.
    /// </summary>
    private static bool NzLiveAfter(List<AsmItem> code, int k, FunctionSymbol function, Dictionary<Label, int> positions)
    {
        // A bool returned in Z or N (-> bool using zero) is read after rts.
        bool returnsInNz = function.ReturnRegister is Register.Zero or Register.Negative;
        var visited = new HashSet<int>();
        var paths = new Stack<int>();
        paths.Push(k + 1);
        int steps = 0;
        while (paths.Count > 0)
        {
            for (int j = paths.Pop(); j < code.Count; j++)
            {
                if (!visited.Add(j)) break;
                if (++steps > 200) return true;
                if (code[j] is LabelTable or DataBytes) return true;
                if (code[j] is not Instr i) continue;
                if (i.Mnemonic is Jsr or Rti) break;
                if (i.Mnemonic == Rts)
                {
                    if (FromAsm(i) || returnsInNz) return true;
                    break;
                }
                var (reads, writes) = AsmEffects.Of(i.Mnemonic, i.Mode);
                if ((reads & (CpuState.N | CpuState.Z)) != 0) return true;
                if (writes.HasFlag(CpuState.N) && writes.HasFlag(CpuState.Z)) break;
                if (IsBranch(i.Mnemonic) || i.Mnemonic == Mnemonic.Jmp)
                {
                    if (Target(i) is not { } target || !positions.TryGetValue(target, out int at)) return true;
                    if (i.Mnemonic == Mnemonic.Jmp) { j = at - 1; continue; }
                    paths.Push(at);
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Removes loads, stores and transfers that change nothing: the register (or the memory) already
    /// holds the value. N and Z must already match the value, or not be needed. Returns true if
    /// anything was removed; the caller analyses again until nothing is.
    /// </summary>
    /// <remarks>
    /// One analysis serves many removals: a removed instruction changed no register or memory value,
    /// so the register contents stay right. Only N and Z can differ, after a removed load or transfer,
    /// so from then on in this pass "N and Z already match" is not trusted, while "N and Z are not
    /// needed" is still checked on the code itself. (Review M7, finding 2: one removal per analysis
    /// made the time grow with the cube of the function's size.)
    /// </remarks>
    private static bool RemoveRedundant(List<AsmItem> code, FunctionSymbol function)
    {
        var positions = Positions(code);
        var before = Analyse(code, function);
        var kept = new List<AsmItem>(code.Count);
        bool flagsChanged = false;
        for (int k = 0; k < code.Count; k++)
        {
            if (code[k] is not Instr i || FromAsm(i) || before[k] is not { } s)
            {
                kept.Add(code[k]);
                continue;
            }
            bool FlagsMatch(HashSet<Operand> value) =>
                (!flagsChanged && s.Flags is Reg f && s.Regs[(int)f].Overlaps(value)) || !NzLiveAfter(code, k, function, positions);
            bool redundant = false, setsFlags = true;
            if (Loaded(i.Mnemonic) is Reg load && Trackable(i))
                redundant = s.Regs[(int)load].Contains(i.Operand!) && FlagsMatch([i.Operand!]);
            else if (Stored(i.Mnemonic) is Reg st && Trackable(i))
                (redundant, setsFlags) = (s.Regs[(int)st].Contains(i.Operand!), false);
            else if (Transfer(i.Mnemonic) is var (from, to))
                redundant = s.Regs[(int)from].Overlaps(s.Regs[(int)to]) && FlagsMatch(s.Regs[(int)from]);
            if (redundant) flagsChanged |= setsFlags;
            else kept.Add(i);
        }
        if (kept.Count == code.Count) return false;
        code.Clear();
        code.AddRange(kept);
        return true;
    }

    // ---------- Bit test and shift ----------

    /// <summary>The index of the next instruction or label at or after <paramref name="k"/>, skipping comments and source lines.</summary>
    private static int NextReal(List<AsmItem> code, int k)
    {
        while (k < code.Count && code[k] is Comment or SourceLine) k++;
        return k;
    }

    private static int PreviousReal(List<AsmItem> code, int k)
    {
        while (k >= 0 && code[k] is Comment or SourceLine) k--;
        return k;
    }

    /// <summary>
    /// <c>and #$80</c> / <c>beq else</c> followed on both paths by <c>lda x</c> / <c>asl a</c>, where A
    /// already holds x: the shift tests bit 7 itself. The two shifts become one, before the branch,
    /// and the branch tests the carry (<c>asl a</c> / <c>bcc else</c>). Every register and flag has the
    /// value it had after the shifts it replaces. For <c>if x &amp; $80 != 0 { x = x &lt;&lt; 1 ... } else { x = x &lt;&lt; 1 }</c> (CRC loops).
    /// </summary>
    private static bool FoldShiftTests(List<AsmItem> code, FunctionSymbol function)
    {
        var positions = new Dictionary<Label, int>();
        var references = new Dictionary<Label, int>();
        for (int k = 0; k < code.Count; k++)
        {
            if (code[k] is LabelDef d) positions[d.Label] = k;
            if (code[k] is Instr i && Target(i) is { } t) references[t] = references.GetValueOrDefault(t) + 1;
        }
        var unknown = UnknownEntries(code);
        State?[]? before = null;

        for (int k = 0; k < code.Count; k++)
        {
            if (code[k] is not Instr { Mnemonic: And, Mode: Immediate, Operand: Imm { Value: 0x80 }, AsmPos: null }) continue;
            int b = NextReal(code, k + 1);
            if (b >= code.Count || code[b] is not Instr { Mnemonic: Beq or Bne } branch || FromAsm(branch)) continue;
            if (Target(branch) is not { } label || references[label] != 1 || unknown.Contains(label) || !positions.TryGetValue(label, out int p)) continue;

            int l1 = NextReal(code, b + 1);
            if (l1 >= code.Count || code[l1] is not Instr { Mnemonic: Lda, Mode: Absolute or ZeroPage, Operand: VarOperand { Variable.IsVolatile: false } x } load1 || FromAsm(load1)) continue;
            int a1 = NextReal(code, l1 + 1);
            if (a1 >= code.Count || code[a1] is not Instr { Mnemonic: Asl, Mode: Accumulator, AsmPos: null }) continue;

            int l2 = NextReal(code, p + 1);
            if (l2 >= code.Count || code[l2] is not Instr { Mnemonic: Lda, AsmPos: null } load2 || load2.Operand != x || load2.Mode != load1.Mode) continue;
            int a2 = NextReal(code, l2 + 1);
            if (a2 >= code.Count || code[a2] is not Instr { Mnemonic: Asl, Mode: Accumulator, AsmPos: null }) continue;

            // The first path must not run on into the label, or the second shift would be needed there.
            int before2 = PreviousReal(code, p - 1);
            if (before2 < 0 || code[before2] is not Instr last || FallsThrough(last)) continue;
            // The label's code must not be entered from anywhere else.
            if (code.GetRange(p + 1, l2 - p - 1).Any(item => item is LabelDef)) continue;

            before ??= Analyse(code, function);
            if (before[k] is not { } state || !state.Regs[(int)Reg.A].Contains(x)) continue;

            code[k] = new Instr(Asl, Accumulator);
            code[b] = new Instr(branch.Mnemonic == Beq ? Bcc : Bcs, branch.Mode, branch.Operand);
            foreach (int remove in new[] { a2, l2, a1, l1 }.OrderByDescending(r => r)) code.RemoveAt(remove);
            return true;
        }
        return false;
    }
}
