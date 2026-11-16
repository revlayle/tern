using Tern.Compiler.Semantics;
using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Backend;

/// <summary>Register contents (redundant loads and transfers), and bit test and shift.</summary>
public static partial class Optimizer
{
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

    /// <summary>
    /// An element read or written through an index register (<c>lda e,x</c>, <c>sta e,x</c>): known
    /// while neither the register nor the array changes (F5).
    /// </summary>
    private sealed record Element(VarOperand Base, Reg Index) : Operand;

    /// <summary>The element <paramref name="i"/> reads or writes through X or Y, if it can be remembered: ordinary memory, not at a fixed address.</summary>
    private static Element? ElementOf(Instr i) =>
        i.Operand is VarOperand { Variable: var v } b && !v.IsVolatile && v.FixedAddress is null && v.Storage.FixedAddress is null
            ? i.Mode switch { AbsoluteX or ZeroPageX => new Element(b, Reg.X), AbsoluteY or ZeroPageY => new Element(b, Reg.Y), _ => null }
            : null;

    /// <summary>The value <paramref name="i"/> loads or stores, if it can be remembered.</summary>
    private static Operand? Value(Instr i) => Trackable(i) ? i.Operand : ElementOf(i);

    /// <summary>Forgets the elements read through <paramref name="r"/>, which has changed.</summary>
    private static void ForgetIndex(State s, Reg r) => s.Forget(op => op is Element { Index: var x } && x == r);

    /// <summary>The register <paramref name="s"/> names, if it names exactly one of A, X and Y.</summary>
    private static Reg? RegOf(CpuState? s) => (s & CpuState.Registers) switch { CpuState.A => Reg.A, CpuState.X => Reg.X, CpuState.Y => Reg.Y, _ => null };

    // Loads, stores and transfers, from the instruction table.
    private static Reg? Loaded(Mnemonic m) => RegOf(Instructions.Loaded(m));
    private static Reg? Stored(Mnemonic m) => RegOf(Instructions.Stored(m));
    private static (Reg From, Reg To)? Transfer(Mnemonic m) =>
        Instructions.Transfer(m) is { } t ? (RegOf(t.From)!.Value, RegOf(t.To)!.Value) : null;

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
    /// <param name="offset">The byte written, for a write to a fixed byte (<c>inc v+1</c> leaves what is known of <c>v</c>); null for an indexed write.</param>
    private static void ForgetWrite(State s, VariableSymbol written, FunctionSymbol function, int? offset = null)
    {
        bool callee = written.Owner is { } o && o != function;
        bool fixedAt = written.FixedAddress is not null;
        if (fixedAt && written.Storage.InOverlayArea)
        {
            // RAM that something else shares, e.g. an overlay's file (spec 8.5): like a write through a pointer.
            s.Forget(op => op is VarOperand or Element);
            return;
        }
        bool Shared(VariableSymbol v) => (callee && v.Owner is { } vo && vo != function) || (fixedAt && v.FixedAddress is not null);
        // An element of the variable may be any of its bytes.
        s.Forget(op => op switch
        {
            VarOperand { Variable: var v, Offset: var at } => (v == written && (offset is null || at == offset)) || Shared(v),
            Element { Base.Variable: var v } => v == written || Shared(v),
            _ => false,
        });
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
        if (ChangesAnything(i))
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
            ForgetIndex(s, load);
            if (Value(i) is { } loaded) s.Regs[(int)load].Add(loaded);
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
            ForgetIndex(s, to);
            s.Flags = to;
            return;
        }

        var (_, writes) = Instructions.Effects(m, i.Mode);
        if (i.Mode != Accumulator && (Instructions.WritesMemory(m, i.Mode) || i.Mode is IndirectIndexed or IndexedIndirect))
        {
            if (i.Mode is Absolute or ZeroPage && i.Operand is VarOperand { Variable: var v, Offset: var at })
            {
                ForgetWrite(s, v, function, at);
                if (Stored(m) is Reg st && Trackable(i)) s.Regs[(int)st].Add(i.Operand!);
            }
            else if (i.Mode is AbsoluteX or AbsoluteY or ZeroPageX or ZeroPageY && i.Operand is VarOperand { Variable: var array })
            {
                // An indexed write changes only the array (an index outside it is not supported,
                // provisional); fixed-address arrays may overlap other fixed-address variables.
                ForgetWrite(s, array, function);
                if (Stored(m) is Reg st && ElementOf(i) is { } element) s.Regs[(int)st].Add(element);
            }
            else if (Instructions.WritesMemory(m, i.Mode) || Stored(m) is not null)
            {
                // Through a pointer, or at a fixed numeric address: any memory may change.
                s.Forget(op => op is VarOperand or Element);
            }
        }

        foreach (var (reg, flag) in new[] { (Reg.A, CpuState.A), (Reg.X, CpuState.X), (Reg.Y, CpuState.Y) })
        {
            if (!writes.HasFlag(flag)) continue;
            // inx / dex / iny / dey keep a known immediate known.
            int delta = m is Inx or Iny ? 1 : m is Dex or Dey ? -1 : 0;
            var known = delta != 0 ? s.Regs[(int)reg].OfType<Imm>().FirstOrDefault() : null;
            s.Regs[(int)reg].Clear();
            ForgetIndex(s, reg);
            if (known is not null) s.Regs[(int)reg].Add(new Imm((known.Value + delta) & 0xFF));
        }
        // N and Z come from the one register the instruction writes (none: cmp, bit, plp, a shift in memory).
        if (writes.HasFlag(CpuState.Z)) s.Flags = RegOf(writes);
    }

    /// <summary>
    /// The state where Z is set: the register N and Z were set from is 0 (after <c>iny / bne loop</c>
    /// falls through, Y is 0).
    /// </summary>
    private static State IsZero(State s)
    {
        if (s.Flags is not Reg f) return s;
        var z = s.Copy();
        z.Regs[(int)f].Add(new Imm(0));
        return z;
    }

    /// <summary>An asm instruction writes to a code label, so it changes the code itself (or another function patches this one).</summary>
    private static bool SelfModifying(List<AsmItem> code, FunctionSymbol function) =>
        function.CodePatched || code.Any(item => item is Instr { AsmPos: not null, Operand: LabelOperand } w
            && (Instructions.WritesMemory(w.Mnemonic, w.Mode) || Stored(w.Mnemonic) is not null));

    /// <summary>The state before each item, from the start of the function and every jump into a label.</summary>
    private static State?[] Analyse(List<AsmItem> code, FunctionSymbol function)
    {
        bool selfModifying = SelfModifying(code, function);
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
                // A branch on Z: where it goes with Z set, the register N and Z came from is 0.
                var row = Instructions.Of(i.Mnemonic);
                bool onZero = row is { Flow: Flow.Branch, Condition: CpuState.Z };
                if (ExitsOf(code, k, positions).Inside is int tp) Join(tp, onZero && row.WhenSet ? IsZero(s) : s);
                if (onZero && !row.WhenSet) s = IsZero(s);
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
                // A call and rti set N and Z (the callee, the flags pulled).
                var (reads, writes) = Instructions.Effects(i.Mnemonic, i.Mode);
                if ((reads & (CpuState.N | CpuState.Z)) != 0) return true;
                if (writes.HasFlag(CpuState.N) && writes.HasFlag(CpuState.Z)) break;
                var exits = ExitsOf(code, j, positions);
                if (exits.Elsewhere) return true;
                if (exits.Returns)
                {
                    // The caller reads N and Z of a bool returned in them; a jump table's dispatch enters an arm.
                    if (FromAsm(i) || returnsInNz) return true;
                    foreach (var l in UnknownEntries(code)) if (positions.TryGetValue(l, out int u)) paths.Push(u);
                    break;
                }
                if (exits.Inside is int at)
                {
                    if (!exits.Next) { j = at - 1; continue; }
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
        bool flagsChanged = false, replaced = false;
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
            if (Loaded(i.Mnemonic) is Reg load && Value(i) is { } loaded)
                redundant = s.Regs[(int)load].Contains(loaded) && FlagsMatch([loaded]);
            else if (Stored(i.Mnemonic) is Reg st && Value(i) is { } stored)
                (redundant, setsFlags) = (s.Regs[(int)st].Contains(stored), false);
            else if (Transfer(i.Mnemonic) is var (from, to))
                redundant = s.Regs[(int)from].Overlaps(s.Regs[(int)to]) && FlagsMatch(s.Regs[(int)from]);
            if (redundant) flagsChanged |= setsFlags;
            else if (Shorter(i, s) is { } shorter) { kept.Add(shorter); replaced = true; }
            else kept.Add(i);
        }
        if (kept.Count == code.Count && !replaced) return false;
        code.Clear();
        code.AddRange(kept);
        return true;
    }

    /// <summary>
    /// A one-byte instruction that loads the same value and sets N and Z the same way, or null:
    /// <c>lda v</c> as <c>txa</c> when X holds v (also <c>tya</c>, <c>tax</c>, <c>tay</c>), and
    /// <c>ldy #c</c> as <c>iny</c> when Y holds c - 1 (<c>dey</c> for c + 1; also X).
    /// </summary>
    private static Instr? Shorter(Instr i, State s)
    {
        if (Loaded(i.Mnemonic) is not Reg load || Value(i) is not { } op) return null;
        Mnemonic? transfer = load switch
        {
            Reg.A when s.Regs[(int)Reg.X].Contains(op) => Txa,
            Reg.A when s.Regs[(int)Reg.Y].Contains(op) => Tya,
            Reg.X when s.Regs[(int)Reg.A].Contains(op) => Tax,
            Reg.Y when s.Regs[(int)Reg.A].Contains(op) => Tay,
            _ => null,
        };
        if (transfer is { } t) return new Instr(t);
        if (load != Reg.A && op is Imm { Value: var c })
        {
            var held = s.Regs[(int)load];
            if (held.Contains(new Imm((c - 1) & 0xFF))) return new Instr(load == Reg.X ? Inx : Iny);
            if (held.Contains(new Imm((c + 1) & 0xFF))) return new Instr(load == Reg.X ? Dex : Dey);
        }
        return null;
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
