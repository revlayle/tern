using Tern.Compiler.Semantics;
using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Backend;

/// <summary>
/// Improves the generated code of one function without changing what it does (roadmap M7):
/// removes unreachable instructions, and loads and stores of values a register already holds.
/// Instructions from assembly blocks are never changed, and volatile memory (spec 4) is never
/// cached. Labels in a function's code are only used inside it (assembly labels are local to their
/// block), so the whole function can be analysed at once.
/// </summary>
public static class Optimizer
{
    public static void Run(List<AsmItem> code, FunctionSymbol function)
    {
        RemoveUnreachable(code);
        while (RemoveOneRedundant(code, function)) { }
    }

    private static bool FromAsm(Instr i) => i.AsmPos is not null;

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
        var positions = new Dictionary<Label, int>();
        for (int k = 0; k < code.Count; k++)
            if (code[k] is LabelDef d) positions[d.Label] = k;
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
        s.Forget(op => op is VarOperand { Variable: var v } && (v == written
            || (callee && v.Owner is { } vo && vo != function)
            || (fixedAt && v.FixedAddress is not null)));
    }

    /// <summary>The state after <paramref name="i"/>, given the state before it.</summary>
    private static void Step(State s, Instr i, FunctionSymbol function)
    {
        if (FromAsm(i) || i.Mnemonic == Jsr)
        {
            // An asm block or a call may change any register and any ordinary memory.
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
            s.Regs[(int)to].Clear();
            s.Regs[(int)to].UnionWith(s.Regs[(int)from]);
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
        var positions = new Dictionary<Label, int>();
        for (int k = 0; k < code.Count; k++)
            if (code[k] is LabelDef d) positions[d.Label] = k;
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
                Step(s, i, function);
                if (Target(i) is { } t && positions.TryGetValue(t, out int tp)) Join(tp, s);
            }
            if (FallsThrough(code[k])) Join(k + 1, s);
        }
        return before;
    }

    /// <summary>True if N or Z may be read after item <paramref name="k"/> before something sets both.</summary>
    private static bool NzLiveAfter(List<AsmItem> code, int k, FunctionSymbol function)
    {
        // A bool returned in Z or N (-> bool using zero) is read after rts.
        bool returnsInNz = function.ReturnRegister is Register.Zero or Register.Negative;
        for (int j = k + 1; j < code.Count; j++)
        {
            switch (code[j])
            {
                case Instr i:
                    if (i.Mnemonic is Jsr or Rti) return false;
                    if (i.Mnemonic == Rts && !FromAsm(i)) return returnsInNz;
                    if (i.Mnemonic is Rts or Mnemonic.Jmp || IsBranch(i.Mnemonic)) return true;
                    var (reads, writes) = AsmEffects.Of(i.Mnemonic, i.Mode);
                    if ((reads & (CpuState.N | CpuState.Z)) != 0) return true;
                    if (writes.HasFlag(CpuState.N) && writes.HasFlag(CpuState.Z)) return false;
                    break;
                case LabelTable or DataBytes:
                    return true;
            }
        }
        return true;
    }

    /// <summary>
    /// Removes the first load, store or transfer that changes nothing: the register (or the memory)
    /// already holds the value. N and Z must already match the value, or not be needed.
    /// </summary>
    private static bool RemoveOneRedundant(List<AsmItem> code, FunctionSymbol function)
    {
        var before = Analyse(code, function);
        for (int k = 0; k < code.Count; k++)
        {
            if (code[k] is not Instr i || FromAsm(i) || before[k] is not { } s) continue;
            bool FlagsMatch(HashSet<Operand> value) =>
                s.Flags is Reg f && s.Regs[(int)f].Overlaps(value) || !NzLiveAfter(code, k, function);
            bool redundant = false;
            if (Loaded(i.Mnemonic) is Reg load && Trackable(i))
                redundant = s.Regs[(int)load].Contains(i.Operand!) && FlagsMatch([i.Operand!]);
            else if (Stored(i.Mnemonic) is Reg st && Trackable(i))
                redundant = s.Regs[(int)st].Contains(i.Operand!);
            else if (Transfer(i.Mnemonic) is var (from, to))
                redundant = s.Regs[(int)from].Overlaps(s.Regs[(int)to]) && FlagsMatch(s.Regs[(int)from]);
            if (!redundant) continue;
            code.RemoveAt(k);
            return true;
        }
        return false;
    }
}
