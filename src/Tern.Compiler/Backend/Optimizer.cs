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
    /// <param name="startsProgram">The function is main, entered once just after the startup code cleared memory.</param>
    public static void Run(List<AsmItem> code, FunctionSymbol function, bool startsProgram = false)
    {
        ThreadJumps(code);
        RemoveUnreachable(code);
        Improve(code, function);
        // After the other changes, which may use what the stores tell them (A holds x after sta x).
        if (startsProgram && RemoveStoresOfStartupZeros(code)) Improve(code, function);
    }

    private static void Improve(List<AsmItem> code, FunctionSymbol function)
    {
        while (RemoveRedundant(code, function) | FoldShiftTests(code, function) | UseOperandInA(code, function)
            | RemoveDeadLoads(code, function) | RemoveDeadStores(code, function) | ReadCopiesFromSource(code, function)
            | GroupConstantStores(code)) { }
    }

    // ---------- Dead stores ----------

    /// <summary>
    /// Bytes of the function's own memory variables that may be read before they are written again.
    /// <see cref="All"/>: anything may be read (code the analysis cannot follow).
    /// </summary>
    private sealed class Live
    {
        public bool All;
        public readonly HashSet<(VariableSymbol, int)> Bytes = [];
        public readonly HashSet<VariableSymbol> Whole = [];   // read with an index

        public bool Has(VariableSymbol v, int offset) => All || Whole.Contains(v) || Bytes.Contains((v, offset));

        /// <summary>Adds what <paramref name="other"/> holds. Returns true if this changed.</summary>
        public bool Union(Live other)
        {
            if (All) return false;
            if (other.All) { All = true; return true; }
            int before = Bytes.Count + Whole.Count;
            Bytes.UnionWith(other.Bytes);
            Whole.UnionWith(other.Whole);
            return Bytes.Count + Whole.Count != before;
        }
    }

    /// <summary>
    /// A variable whose bytes only this function's code reads: a local, parameter or temporary of
    /// its own frame (spec 8.1: no other function runs while it is active and can name it, and it
    /// holds nothing useful on entry), whose address is not taken and that is not volatile.
    /// </summary>
    private static bool OwnMemory(VariableSymbol v, FunctionSymbol function)
    {
        var s = v.Storage;
        return s.Owner == function && s.Kind != VariableKind.Global && !s.AddressTaken && !v.AddressTaken
            && !s.IsVolatile && s.FixedAddress is null && s.Region is null;
    }

    /// <summary>
    /// Removes <c>sta</c>, <c>stx</c> and <c>sty</c> into a byte of the function's own memory
    /// (<see cref="OwnMemory"/>) that no path reads before it is stored again or the function
    /// returns: <c>var b = values[i + 1]</c> whose value is used only from A. A store changes no
    /// register or flag, so nothing else changes. Stores from assembly stay. Returns true if any was removed.
    /// </summary>
    /// <remarks>
    /// A backward analysis over the code. A <c>jsr</c> reads none of these bytes (the callee cannot
    /// name them). An <c>rts</c> or <c>jmp (v)</c> may go to a label named other than by a jump (the
    /// jump table of a <c>match</c> dispatches with <c>rts</c>), so it reads what those labels may read.
    /// The end of the code, and a jump to a label outside it, read nothing: what follows is the
    /// function's exit (an interrupt handler's register restore) or another function.
    /// </remarks>
    private static bool RemoveDeadStores(List<AsmItem> code, FunctionSymbol function)
    {
        var positions = Positions(code);
        // Assembly that patches code, or calls a subroutine of its own block: not followed.
        if (SelfModifying(code, function) || CallsOwnSubroutine(code, positions)) return false;
        var unknown = UnknownEntries(code).Where(positions.ContainsKey).Select(l => positions[l]).ToList();

        (VariableSymbol, int) Byte(VarOperand o) => (o.Variable.Storage, o.Variable.AliasOffset + o.Offset);

        // What item k reads of these bytes, and which byte it overwrites.
        void Transfer(int k, Live after, Live into)
        {
            into.Union(after);
            if (code[k] is not Instr i) return;
            if (i.Mnemonic == Brk) { into.All = true; return; }
            if (i.Operand is ImmVarByte { Variable: var a } && OwnMemory(a, function)) into.Whole.Add(a.Storage);
            if (i.Operand is not VarOperand o || !OwnMemory(o.Variable, function)) return;
            var b = Byte(o);
            switch (i.Mode)
            {
                case Absolute or ZeroPage:
                    if (Stored(i.Mnemonic) is not null) into.Bytes.Remove(b);
                    else if (AsmEffects.ReadsMemory(i.Mnemonic, i.Mode)) into.Bytes.Add(b);
                    break;
                case IndirectIndexed or Indirect:
                    into.Bytes.Add(b);
                    into.Bytes.Add((b.Item1, b.Item2 + 1));
                    break;
                default:
                    // Indexed: any byte of the variable. An indexed store overwrites no known byte.
                    if (AsmEffects.ReadsMemory(i.Mnemonic, i.Mode) || i.Mode == IndexedIndirect) into.Whole.Add(b.Item1);
                    break;
            }
        }

        var liveIn = new Live[code.Count];
        for (int k = 0; k < code.Count; k++) liveIn[k] = new Live();

        // What may be read after item k.
        Live After(int k)
        {
            var after = new Live();
            var item = code[k];
            if (item is Instr i)
            {
                if (i.Mnemonic is Rts || (i.Mnemonic == Mnemonic.Jmp && i.Mode == Indirect))
                    foreach (int u in unknown) after.Union(liveIn[u]);
                if (Target(i) is { } t && positions.TryGetValue(t, out int tp)) after.Union(liveIn[tp]);
            }
            // liveIn keeps the indexes from before any removal below; items before a removal do not move.
            if (FallsThrough(item) && k + 1 < liveIn.Length) after.Union(liveIn[k + 1]);
            return after;
        }

        for (bool changed = true; changed;)
        {
            changed = false;
            for (int k = code.Count - 1; k >= 0; k--)
            {
                var next = new Live();
                Transfer(k, After(k), next);
                changed |= liveIn[k].Union(next);
            }
        }

        bool removed = false;
        for (int k = code.Count - 1; k >= 0; k--)
        {
            if (code[k] is Instr { AsmPos: null, Mode: Absolute or ZeroPage, Operand: VarOperand o } i && Stored(i.Mnemonic) is not null
                && OwnMemory(o.Variable, function))
            {
                var (v, offset) = Byte(o);
                if (!After(k).Has(v, offset)) { code.RemoveAt(k); removed = true; }
            }
        }
        return removed;
    }

    /// <summary>Assembly that calls a subroutine of its own block (<c>jsr .sub</c>): its <c>rts</c> returns into this code.</summary>
    private static bool CallsOwnSubroutine(List<AsmItem> code, Dictionary<Label, int> positions) =>
        code.Any(item => item is Instr { Mnemonic: Jsr, Operand: LabelOperand { Label: var l } } && positions.ContainsKey(l));

    private static CpuState Flag(Reg r) => r switch { Reg.A => CpuState.A, Reg.X => CpuState.X, _ => CpuState.Y };

    /// <summary>
    /// The registers (A, X, Y) that may be read before they are written, before each item. A call
    /// may read all three (arguments); <c>rts</c> reads the return value's registers, or what the
    /// labels of a jump table read; anything the analysis cannot follow reads all three.
    /// </summary>
    private static CpuState[] RegistersLive(List<AsmItem> code, FunctionSymbol function, Dictionary<Label, int> positions)
    {
        const CpuState all = CpuState.A | CpuState.X | CpuState.Y;
        var unknown = UnknownEntries(code).Where(positions.ContainsKey).Select(l => positions[l]).ToList();
        var returned = CpuState.None;
        foreach (var r in new[] { Reg.A, Reg.X, Reg.Y })
            if (function.ReturnRegister is { } ret && ret.Uses(r switch { Reg.A => Register.A, Reg.X => Register.X, _ => Register.Y }))
                returned |= Flag(r);
        var live = new CpuState[code.Count + 1];
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int k = code.Count - 1; k >= 0; k--)
            {
                var item = code[k];
                CpuState after = CpuState.None, result;
                if (FallsThrough(item)) after |= live[k + 1];
                if (item is Instr i)
                {
                    if (Target(i) is { } t) after |= positions.TryGetValue(t, out int tp) ? live[tp] : all;
                    if (i.Mnemonic == Rts) { after |= returned; foreach (int u in unknown) after |= live[u]; }
                    if (i.Mnemonic is Brk or Rti || (i.Mnemonic == Mnemonic.Jmp && Target(i) is null)) after = all;
                    var (reads, writes) = AsmEffects.Of(i.Mnemonic, i.Mode);
                    result = i.Mnemonic == Jsr ? all : ((after & ~writes) | reads) & all;
                }
                else result = after;
                if (result != live[k]) { live[k] = result; changed = true; }
            }
        }
        return live;
    }

    /// <summary>
    /// For each item, whether a fact holds before it on every path, given where it starts
    /// (<paramref name="makes"/>: true after the item) and what ends it. Nothing holds at the start of
    /// the function or at a label entered from where the analysis cannot see.
    /// </summary>
    private static bool[] HoldsBefore(List<AsmItem> code, Dictionary<Label, int> positions, Func<int, bool> makes, Func<int, bool> ends)
    {
        var unknown = UnknownEntries(code);
        var before = new bool?[code.Count + 1];
        before[0] = false;
        foreach (var l in unknown) if (positions.TryGetValue(l, out int p)) before[p] = false;
        var work = new Stack<int>();
        for (int k = 0; k < code.Count; k++) if (before[k] is not null) work.Push(k);
        void Join(int k, bool value)
        {
            if (before[k] is null) { before[k] = value; work.Push(k); }
            else if (before[k] == true && !value) { before[k] = false; work.Push(k); }
        }
        while (work.Count > 0)
        {
            int k = work.Pop();
            if (k >= code.Count) continue;
            bool after = makes(k) || (before[k] == true && !ends(k));
            if (code[k] is Instr i && Target(i) is { } t && positions.TryGetValue(t, out int tp)) Join(tp, after);
            if (FallsThrough(code[k])) Join(k + 1, after);
        }
        return before.Select(b => b == true).ToArray();
    }

    /// <summary>True if item <paramref name="k"/> can run again without passing <paramref name="avoid"/> (it is in a loop that does not contain it).</summary>
    private static bool RepeatsWithout(List<AsmItem> code, Dictionary<Label, int> positions, int k, int avoid)
    {
        var seen = new HashSet<int>();
        var work = new Stack<int>();
        void Next(int j)
        {
            if (code[j] is Instr i && Target(i) is { } t && positions.TryGetValue(t, out int tp)) work.Push(tp);
            if (FallsThrough(code[j]) && j + 1 < code.Count) work.Push(j + 1);
        }
        Next(k);
        while (work.Count > 0)
        {
            int j = work.Pop();
            if (j == k) return true;
            if (j == avoid || !seen.Add(j)) continue;
            Next(j);
        }
        return false;
    }

    /// <summary>
    /// A one-byte local that is a copy of other memory (<c>var a = values[i]</c>: <c>lda values,x / sta a</c>,
    /// its only store) is read from that memory instead while neither the memory nor its index
    /// register has changed (<c>cmp a</c> becomes <c>cmp values,x</c>). Where the memory is overwritten
    /// and the copy is still needed after (a swap), the value is loaded into a free index register
    /// just before the overwriting store and <c>lda a</c> becomes a transfer. The copy's store and the
    /// first load then go (<see cref="RemoveDeadStores"/>, <see cref="RemoveDeadLoads"/>). Only when that
    /// takes no more bytes, and no read moved to the other memory runs more often than the store.
    /// Returns true if a copy was changed.
    /// </summary>
    private static bool ReadCopiesFromSource(List<AsmItem> code, FunctionSymbol function)
    {
        var positions = Positions(code);
        if (SelfModifying(code, function) || CallsOwnSubroutine(code, positions)) return false;
        CpuState[]? live = null;
        for (int s = 0; s < code.Count; s++)
        {
            if (code[s] is not Instr { Mnemonic: Sta, AsmPos: null, Mode: Absolute or ZeroPage, Operand: VarOperand { Offset: 0, Variable: var v } }) continue;
            if (v.AliasOf is not null || v.Type.Size != 1 || !OwnMemory(v, function)) continue;
            int l = PreviousReal(code, s - 1);
            if (l < 0 || code[l] is not Instr { Mnemonic: Lda, AsmPos: null, Operand: VarOperand { Variable: var from } } load
                || load.Mode is not (Absolute or ZeroPage or AbsoluteX or ZeroPageX or AbsoluteY or ZeroPageY)) continue;
            Reg? index = load.Mode is AbsoluteX or ZeroPageX ? Reg.X : load.Mode is AbsoluteY or ZeroPageY ? Reg.Y : null;
            var source = from.Storage;
            if (source == v.Storage || source.IsVolatile || source.FixedAddress is not null || source.InOverlayArea
                || (source.Owner is { } o && o != function)) continue;

            // Every other use of v is a plain read by the compiler's code.
            var reads = new List<int>();
            bool usable = true;
            for (int k = 0; k < code.Count && usable; k++)
            {
                if (k == s || code[k] is not Instr i) continue;
                bool names = (i.Operand is VarOperand w && w.Variable.Storage == v.Storage) || (i.Operand is ImmVarByte b && b.Variable.Storage == v.Storage);
                if (!names) continue;
                usable = !FromAsm(i) && i.Mode is Absolute or ZeroPage && i.Operand is VarOperand { Offset: 0 }
                    && Stored(i.Mnemonic) is null && !AsmEffects.WritesMemory(i.Mnemonic, i.Mode);
                reads.Add(k);
            }
            if (!usable || reads.Count == 0) continue;

            // Where v equals the source memory.
            bool Ends(int k)
            {
                if (code[k] is not Instr i) return false;
                if (i.Mnemonic is Jsr or Brk) return true;
                if (index is Reg r && AsmEffects.Of(i.Mnemonic, i.Mode).Writes.HasFlag(Flag(r))) return true;
                if (i.Mode == Accumulator || !(AsmEffects.WritesMemory(i.Mnemonic, i.Mode) || Stored(i.Mnemonic) is not null)) return false;
                if (i.Mode is IndirectIndexed or IndexedIndirect or Indirect || i.Operand is not VarOperand { Variable: var w }) return true;
                return w.Storage == source || w.Storage == v.Storage || w.Storage.InOverlayArea;
            }
            var same = HoldsBefore(code, positions, k => k == s, Ends);

            var fromSource = new List<int>();
            var fromRegister = new List<int>();
            foreach (int r in reads)
            {
                var i = (Instr)code[r];
                if (same[r] && InstructionSet.TryGet(i.Mnemonic, load.Mode, out _)) fromSource.Add(r);
                else if (i.Mnemonic == Lda) fromRegister.Add(r);
                else { usable = false; break; }
            }
            if (!usable || fromSource.Any(r => RepeatsWithout(code, positions, r, s))) continue;

            // A store into the source memory that ends the copy while it is still needed: load the
            // value into a free register just before it.
            var loads = new List<int>();
            Reg held = Reg.A;
            if (fromRegister.Count > 0)
            {
                var needed = fromRegister.ToHashSet();
                bool StillNeeded(int k)
                {
                    var seen = new HashSet<int>();
                    var work = new Stack<int>();
                    work.Push(k + 1);
                    while (work.Count > 0)
                    {
                        int j = work.Pop();
                        if (j >= code.Count || j == s || !seen.Add(j)) continue;
                        if (needed.Contains(j)) return true;
                        if (code[j] is Instr ji && Target(ji) is { } t && positions.TryGetValue(t, out int tp)) work.Push(tp);
                        if (FallsThrough(code[j])) work.Push(j + 1);
                    }
                    return false;
                }
                for (int k = 0; k < code.Count && usable; k++)
                {
                    if (k == s || !same[k] || !Ends(k) || !StillNeeded(k)) continue;
                    // Only a store into the source memory, not a call or a change of the index.
                    usable = code[k] is Instr { AsmPos: null } i && (Stored(i.Mnemonic) is not null || AsmEffects.WritesMemory(i.Mnemonic, i.Mode))
                        && i.Operand is VarOperand { Variable: var w } && w.Storage == source && i.Mode is not (IndirectIndexed or IndexedIndirect or Indirect)
                        && !(index is Reg r && AsmEffects.Of(i.Mnemonic, i.Mode).Writes.HasFlag(Flag(r)));
                    loads.Add(k);
                }
                if (!usable) continue;
                live ??= RegistersLive(code, function, positions);
                var choices = new[] { Reg.Y, Reg.X }.Where(r => r != index
                    && InstructionSet.TryGet(r == Reg.Y ? Ldy : Ldx, load.Mode, out _)
                    && loads.All(k => (live[k] & Flag(r)) == 0 && !NzLiveAfter(code, k - 1, function, positions))).ToList();
                if (choices.Count == 0) continue;
                held = choices[0];
                // The register still holds v at each lda v.
                var regFlag = Flag(held);
                var loadSet = loads.ToHashSet();
                var holds = HoldsBefore(code, positions, k => loadSet.Contains(k) && !EndsHold(k), EndsHold);
                bool EndsHold(int k) => k == s || (code[k] is Instr i && (i.Mnemonic is Jsr or Brk || AsmEffects.Of(i.Mnemonic, i.Mode).Writes.HasFlag(regFlag)));
                if (!fromRegister.All(r => holds[r])) continue;
            }

            // Bytes: the store (2, a local in zero page at best), each read moved (one more with an
            // absolute address), each load added, each lda v as a transfer, and the first load if it dies.
            int sourceSize = 1 + InstructionSet.OperandSize(load.Mode);
            int next = NextReal(code, s + 1);
            bool firstLoadDies = next < code.Count && code[next] is Instr { AsmPos: null, Mnemonic: Lda or Txa or Tya };
            int delta = -2 + fromSource.Count * (sourceSize - 2) + loads.Count * sourceSize - fromRegister.Count - (firstLoadDies ? sourceSize : 0);
            if (delta > 0) continue;

            foreach (int r in fromSource) code[r] = (Instr)code[r] with { Mode = load.Mode, Operand = load.Operand };
            foreach (int r in fromRegister) code[r] = new Instr(held == Reg.Y ? Tya : Txa);
            foreach (int k in loads.OrderByDescending(k => k)) code.Insert(k, new Instr(held == Reg.Y ? Ldy : Ldx, load.Mode, load.Operand));
            return true;
        }
        return false;
    }

    /// <summary>
    /// At the start of main, before any label, branch, call or assembly, a store of 0 into a global
    /// the startup code cleared, and that main has not written yet, changes nothing (<c>fib[0] = 0</c>).
    /// </summary>
    private static bool RemoveStoresOfStartupZeros(List<AsmItem> code)
    {
        int count = code.Count;
        static bool ClearedAtStartup(VariableSymbol v) =>
            v.Kind == VariableKind.Global && v.InitialBytes is null && !v.NoInit && !v.IsData && v.FixedAddress is null
            && !v.IsOverlayImage && !v.IsVolatile && v.AliasOf is null;
        var known = new Operand?[3];
        var written = new HashSet<VarOperand>();
        var writtenWhole = new HashSet<VariableSymbol>();   // arrays written with an index
        int first = NextReal(code, 0);
        // main's own label, if nothing in main jumps to it.
        if (first < code.Count && code[first] is LabelDef entry && !code.Any(item => item is Instr { Operand: LabelOperand { Label: var l } } && l == entry.Label))
            first++;
        for (int k = first; k < code.Count; k++)
        {
            if (code[k] is Comment or SourceLine) continue;
            if (code[k] is not Instr { AsmPos: null } i || i.Mnemonic is Jsr or Brk or Rts or Rti or Mnemonic.Jmp || IsBranch(i.Mnemonic)) break;
            if (Stored(i.Mnemonic) is Reg st && i.Mode is Absolute or ZeroPage && i.Operand is VarOperand target)
            {
                if (known[(int)st] is Imm { Value: 0 } && ClearedAtStartup(target.Variable) && !written.Contains(target)
                    && !writtenWhole.Contains(target.Variable))
                    code.RemoveAt(k--);
                else
                    written.Add(target);
                continue;
            }
            // Anything else in straight-line code: note what it writes.
            if (AsmEffects.WritesMemory(i.Mnemonic, i.Mode) || Stored(i.Mnemonic) is not null)
            {
                // Through a pointer, or at a number: anything may change.
                if (i.Mode is IndirectIndexed or IndexedIndirect or Indirect || i.Operand is not VarOperand { Variable: var v }) break;
                if (i.Mode is Absolute or ZeroPage) written.Add((VarOperand)i.Operand); else writtenWhole.Add(v);
            }
            var (_, writes) = AsmEffects.Of(i.Mnemonic, i.Mode);
            foreach (var r in new[] { Reg.A, Reg.X, Reg.Y })
                if (writes.HasFlag(r switch { Reg.A => CpuState.A, Reg.X => CpuState.X, _ => CpuState.Y })) known[(int)r] = null;
            if (Loaded(i.Mnemonic) is Reg load && i.Mode == Immediate) known[(int)load] = i.Operand;
        }
        return code.Count != count;
    }

    /// <summary>
    /// Removes a load whose register is loaded again before anything reads it or N and Z, on the
    /// straight-line code after it (<c>lda #0</c> / <c>lda #1</c>). A volatile read, and a read of a
    /// fixed address (it may be hardware), stay. Returns true if any was removed.
    /// </summary>
    private static bool RemoveDeadLoads(List<AsmItem> code, FunctionSymbol function)
    {
        bool removed = false;
        // From the end, so each decision sees the code after it as it will be.
        for (int k = code.Count - 1; k >= 0; k--)
        {
            // An indexed read of ordinary memory has no effect either (not through a pointer: it may reach I/O).
            if (code[k] is not Instr { AsmPos: null } i || Loaded(i.Mnemonic) is not Reg load
                || !(Trackable(i) || (i.Mode is AbsoluteX or AbsoluteY or ZeroPageX or ZeroPageY && i.Operand is VarOperand { Variable.IsVolatile: false }))
                || i.Operand is VarOperand { Variable.FixedAddress: not null } or VarOperand { Variable.Storage.FixedAddress: not null }) continue;
            var reg = load switch { Reg.A => CpuState.A, Reg.X => CpuState.X, _ => CpuState.Y };
            var register = load switch { Reg.A => Register.A, Reg.X => Register.X, _ => Register.Y };
            for (int j = k + 1; j < code.Count; j++)
            {
                if (code[j] is Comment or SourceLine or LabelDef) continue;
                if (code[j] is not Instr n || FromAsm(n)) break;
                // rts reads only the return value's registers (N and Z for a bool in a flag).
                bool deadAtReturn = n.Mnemonic == Rts
                    && function.ReturnRegister is not (Register.Zero or Register.Negative)
                    && !(function.ReturnRegister is { } ret && ret.Uses(register));
                if (!deadAtReturn && (n.Mnemonic is Jsr or Rts or Rti or Brk or Mnemonic.Jmp || IsBranch(n.Mnemonic))) break;
                var (reads, writes) = AsmEffects.Of(n.Mnemonic, n.Mode);
                if ((reads & (reg | CpuState.N | CpuState.Z)) != 0) break;
                if (deadAtReturn || (writes.HasFlag(reg) && writes.HasFlag(CpuState.N) && writes.HasFlag(CpuState.Z)))
                {
                    code.RemoveAt(k);
                    removed = true;
                    break;
                }
            }
        }
        return removed;
    }

    /// <summary>
    /// A run of <c>lda #c</c> / <c>sta v</c> pairs into different bytes of ordinary memory stores each
    /// constant once: <c>lda #0 / sta a+1 / lda #45 / sta b / lda #0 / sta b+1</c> becomes
    /// <c>lda #45 / sta b / lda #0 / sta a+1 / sta b+1</c>. The value loaded last stays last, so A, N
    /// and Z end the same. Bytes that may share memory (frames of different callees, fixed addresses)
    /// keep their order. Returns true if a run was changed.
    /// </summary>
    private static bool GroupConstantStores(List<AsmItem> code)
    {
        bool changed = false;
        for (int k = 0; k < code.Count; k++)
        {
            // The run: from an lda #c, every instruction an lda #c or an sta to a trackable byte.
            if (code[k] is not Instr { Mnemonic: Lda, Mode: Immediate, AsmPos: null }) continue;
            // Each store with the source lines and comments since the store before it.
            var stores = new List<(Operand Value, Instr Store, List<AsmItem> Lines)>();
            var lines = new List<AsmItem>();
            Operand? value = null;
            int end = k;
            for (int j = k; j < code.Count; j++)
            {
                if (code[j] is Comment or SourceLine) { lines.Add(code[j]); continue; }
                if (code[j] is Instr { Mnemonic: Lda, Mode: Immediate, AsmPos: null } l) { value = l.Operand; end = j; continue; }
                if (code[j] is Instr { Mnemonic: Sta, AsmPos: null, Operand: VarOperand } s && Trackable(s)) { stores.Add((value!, s, lines)); lines = []; end = j; continue; }
                break;
            }
            // Lines after the last store belong to what follows the run, unless an lda in the run follows them.
            var trailing = code.GetRange(k, end - k + 1).Where(lines.Contains).ToList();
            var run = code.GetRange(k, end - k + 1);
            // Each value once; the last value loaded (even with no store after it) goes last.
            var values = stores.Select(s => s.Value).Where(v => !v.Equals(value)).Distinct().Append(value!).ToList();
            if (stores.Count == 0 || run.Count(item => item is Instr { Mnemonic: Lda }) <= values.Count) continue;
            var targets = stores.Select(s => (VarOperand)s.Store.Operand!).ToList();
            static bool MayShare(VarOperand a, VarOperand b) =>
                a == b || (a.Variable.FixedAddress is not null && b.Variable.FixedAddress is not null)
                || (a.Variable.Owner is { } oa && b.Variable.Owner is { } ob && oa != ob);
            if (targets.SelectMany((a, x) => targets.Skip(x + 1).Select(b => (a, b))).Any(p => MayShare(p.a, p.b))) continue;

            // Each source line stays in front of the code of its statement: the first store of a
            // value takes its lines in front of the lda, the others in front of the store.
            var grouped = new List<AsmItem>();
            foreach (var v in values)
            {
                var group = stores.Where(s => s.Value.Equals(v)).ToList();
                if (group.Count > 0) grouped.AddRange(group[0].Lines);
                if (v.Equals(values[^1])) grouped.AddRange(trailing);
                grouped.Add(new Instr(Lda, Immediate, v));
                for (int g = 0; g < group.Count; g++)
                {
                    if (g > 0) grouped.AddRange(group[g].Lines);
                    grouped.Add(group[g].Store);
                }
            }
            code.RemoveRange(k, end - k + 1);
            code.InsertRange(k, grouped);
            changed = true;
            k += grouped.Count - 1;
        }
        return changed;
    }

    /// <summary>
    /// <c>lda x</c> / <c>eor y</c> where A already holds y becomes <c>eor x</c>; also <c>and</c>,
    /// <c>ora</c> and <c>adc</c> (with the carry it finds), which are commutative and take every
    /// addressing mode <c>lda</c> does. A, N, Z, C and V end the same; a <c>clc</c> between
    /// <c>lda</c> and <c>adc</c> stays. Returns true if anything changed.
    /// </summary>
    /// <remarks>
    /// Each change leaves every register and flag as it was after the pair, so one analysis serves
    /// all of them; going from the end keeps the positions of the states not yet used.
    /// </remarks>
    private static bool UseOperandInA(List<AsmItem> code, FunctionSymbol function)
    {
        State?[]? before = null;
        bool changed = false;
        for (int k = code.Count - 1; k >= 0; k--)
        {
            if (code[k] is not Instr { Mnemonic: Lda, AsmPos: null } load) continue;
            int o = NextReal(code, k + 1);
            if (o < code.Count && code[o] is Instr { Mnemonic: Clc or Sec, AsmPos: null }) o = NextReal(code, o + 1);
            if (o >= code.Count || code[o] is not Instr { Mnemonic: And or Ora or Eor or Adc, AsmPos: null } op || !Trackable(op)) continue;
            if (op.Mnemonic != Adc && NextReal(code, k + 1) != o) continue;
            before ??= Analyse(code, function);
            if (before[k] is not { } s || !s.Regs[(int)Reg.A].Contains(op.Operand!)) continue;
            code[o] = op with { Mode = load.Mode, Operand = load.Operand };
            code.RemoveAt(k);
            changed = true;
        }
        return changed;
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
        if (fixedAt && written.Storage.InOverlayArea)
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
            && (AsmEffects.WritesMemory(w.Mnemonic, w.Mode) || Stored(w.Mnemonic) is not null));

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
                if (Target(i) is { } t && positions.TryGetValue(t, out int tp)) Join(tp, i.Mnemonic == Beq ? IsZero(s) : s);
                if (i.Mnemonic == Bne) s = IsZero(s);
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
            if (Loaded(i.Mnemonic) is Reg load && Trackable(i))
                redundant = s.Regs[(int)load].Contains(i.Operand!) && FlagsMatch([i.Operand!]);
            else if (Stored(i.Mnemonic) is Reg st && Trackable(i))
                (redundant, setsFlags) = (s.Regs[(int)st].Contains(i.Operand!), false);
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
        if (Loaded(i.Mnemonic) is not Reg load || !Trackable(i)) return null;
        var op = i.Operand!;
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
