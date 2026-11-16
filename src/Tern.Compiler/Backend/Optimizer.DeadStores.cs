using Tern.Compiler.Semantics;
using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Backend;

/// <summary>Dead stores and loads, stores moved out of loops, copies read from their source, jumps and unreachable code.</summary>
public static partial class Optimizer
{
    // ---------- Dead stores ----------

    /// <summary>
    /// Bytes of tracked variables (<see cref="Tracked"/>) that may be read before they are written again.
    /// <see cref="All"/>: anything may be read (code the analysis cannot follow). <see cref="Globals"/>:
    /// every byte of an ordinary global may be read (a call, the return), except those in <see cref="Dead"/>,
    /// which are stored again first.
    /// </summary>
    private sealed class Live
    {
        public bool All, Globals;
        public readonly HashSet<(VariableSymbol, int)> Bytes = [];
        public readonly HashSet<VariableSymbol> Whole = [];   // read with an index
        public readonly HashSet<(VariableSymbol, int)> Dead = [];

        public bool Has(VariableSymbol v, int offset) => All || Whole.Contains(v) || Bytes.Contains((v, offset))
            || (Globals && v.Kind == VariableKind.Global && !Dead.Contains((v, offset)));

        public void Read((VariableSymbol, int) b) { Bytes.Add(b); Dead.Remove(b); }

        public void Written((VariableSymbol V, int) b)
        {
            Bytes.Remove(b);
            if (Globals && b.V.Kind == VariableKind.Global) Dead.Add(b);
        }

        public void ReadGlobals() { Globals = true; Dead.Clear(); }

        /// <summary>Adds what <paramref name="other"/> holds. Returns true if this changed.</summary>
        public bool Union(Live other)
        {
            if (All) return false;
            if (other.All) { All = true; return true; }
            var before = (Globals, Bytes.Count, Whole.Count, Dead.Count);
            if (other.Globals)
            {
                if (Globals) Dead.IntersectWith(other.Dead);
                else Dead.UnionWith(other.Dead.Where(b => !Bytes.Contains(b)));
                Globals = true;
            }
            Bytes.UnionWith(other.Bytes);
            Whole.UnionWith(other.Whole);
            Dead.ExceptWith(other.Bytes);
            return (Globals, Bytes.Count, Whole.Count, Dead.Count) != before;
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
    /// A global variable of ordinary memory (spec 4): not volatile (so used from one root, spec 8.1),
    /// its address not taken, not at a fixed address, in a region or in an overlay, not data. Other
    /// code may read it only after a call, a return or a jump out of the function's code.
    /// </summary>
    private static bool OrdinaryGlobal(VariableSymbol v)
    {
        var s = v.Storage;
        return s.Kind == VariableKind.Global && !s.AddressTaken && !v.AddressTaken && !s.IsVolatile && s.FixedAddress is null
            && s.Region is null && s.OverlayArea is null && !s.InOverlayArea && !s.IsData;
    }

    /// <summary>The variables whose stores <see cref="RemoveDeadStores"/> follows.</summary>
    private static bool Tracked(VariableSymbol v, FunctionSymbol function) => OwnMemory(v, function) || OrdinaryGlobal(v);

    /// <summary>
    /// Removes <c>sta</c>, <c>stx</c> and <c>sty</c> into a byte of the function's own memory
    /// (<see cref="OwnMemory"/>) or of an ordinary global (<see cref="OrdinaryGlobal"/>) that no path
    /// reads before it is stored again or, for the function's own memory, the function returns:
    /// <c>var b = values[i + 1]</c> whose value is used only from A. A store changes no register or
    /// flag, so nothing else changes. Stores from assembly stay. Returns true if any was removed.
    /// </summary>
    /// <remarks>
    /// A backward analysis over the code. A <c>jsr</c> reads none of the function's own bytes (the
    /// callee cannot name them). An <c>rts</c> or <c>jmp (v)</c> may go to a label named other than by
    /// a jump (the jump table of a <c>match</c> dispatches with <c>rts</c>), so it reads what those
    /// labels may read. The end of the code, and a jump to a label outside it, read none of them:
    /// what follows is the function's exit (an interrupt handler's register restore) or another
    /// function. Globals are read by all of these, by a call, and by a read through a pointer or at a number.
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
            var flow = Instructions.Of(i.Mnemonic).Flow;
            if (flow == Flow.Break) { into.All = true; return; }
            // Other code may read the globals: a callee, the caller, or memory reached through a pointer or at a number.
            var use = MemoryUse.Of(i);
            if (flow is Flow.Call or Flow.Return or Flow.ReturnFromInterrupt || (use.Reads && use.Target is MemoryTarget.Pointer or MemoryTarget.Number))
                into.ReadGlobals();
            if (i.Operand is ImmVarByte { Variable: var a } && Tracked(a, function)) into.Whole.Add(a.Storage);
            if (use.Operand is not { } o || !Tracked(o.Variable, function)) return;
            var b = Byte(o);
            switch (use.Target)
            {
                case MemoryTarget.Byte:
                    if (use.Reads) into.Read(b);
                    else if (use.Writes) into.Written(b);
                    break;
                case MemoryTarget.Pointer when i.Mode != IndexedIndirect:
                    into.Read(b);
                    into.Read((b.Item1, b.Item2 + 1));
                    break;
                default:
                    // Indexed, or a pointer chosen by X: any byte of the variable. An indexed store overwrites no known byte.
                    if (use.Reads || use.Target == MemoryTarget.Pointer) into.Whole.Add(b.Item1);
                    break;
            }
        }

        var liveIn = new Live[code.Count];
        for (int k = 0; k < code.Count; k++) liveIn[k] = new Live();

        // What may be read after item k.
        Live After(int k)
        {
            var after = new Live();
            var exits = ExitsOf(code, k, positions);
            if (exits.Entries) foreach (int u in unknown) after.Union(liveIn[u]);
            if (exits.Inside is int tp) after.Union(liveIn[tp]);
            // A jump out of the code leaves the function: its globals may be read there.
            if (exits.Elsewhere) after.ReadGlobals();
            // liveIn keeps the indexes from before any removal below; items before a removal do not move.
            if (exits.Next && k + 1 < liveIn.Length) after.Union(liveIn[k + 1]);
            // The end of the code: the function's exit (an interrupt handler's register restore).
            else if (exits.Next) after.ReadGlobals();
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
                && Tracked(o.Variable, function))
            {
                var (v, offset) = Byte(o);
                if (!After(k).Has(v, offset)) { code.RemoveAt(k); removed = true; }
            }
        }
        return removed;
    }

    /// <summary>
    /// A loop that stores a variable (<see cref="Tracked"/>) but never reads its memory keeps it only in
    /// the register: the stores in the loop go, and the variable is stored once at each exit, from a
    /// register that holds it on every path there (<c>crc</c> in A for the eight steps of a CRC byte:
    /// spec 4, ordinary memory may be kept in registers). Only when that takes no more stores than it
    /// removes. Returns true if a loop was changed.
    /// </summary>
    /// <remarks>
    /// A loop is the code from a label to a branch or jump back to it. Inside it no instruction reads
    /// the variable, and nothing leaves it except by a jump, a branch or falling out at its end (no
    /// call, return or jump table), so the memory is out of date only where nothing reads it. A
    /// register the analysis shows equal to the memory before the change holds the right value after
    /// it, since removing stores changes no register.
    /// </remarks>
    private static bool SinkStoresFromLoops(List<AsmItem> code, FunctionSymbol function)
    {
        var positions = Positions(code);
        if (SelfModifying(code, function) || CallsOwnSubroutine(code, positions)) return false;
        var unknown = UnknownEntries(code);
        State?[]? before = null;
        for (int b = 0; b < code.Count; b++)
        {
            if (code[b] is not Instr back || Target(back) is not { } head || !positions.TryGetValue(head, out int h) || h > b) continue;
            bool InLoop(int k) => k >= h && k <= b;
            bool JumpedFromOutside(Label l) =>
                code.Where((item, k) => !InLoop(k) && item is Instr i && Target(i) == l).Any();

            // Nothing leaves the loop except by a jump or branch to a label of this code, or at its end.
            bool followed = true, readsAnywhere = false;
            for (int k = h; k <= b && followed; k++)
            {
                switch (code[k])
                {
                    case LabelTable or DataBytes or AddressByte:
                    case LabelDef d when unknown.Contains(d.Label):
                        followed = false;
                        break;
                    case Instr i:
                        var exits = ExitsOf(code, k, positions);
                        followed = Instructions.Of(i.Mnemonic).Flow != Flow.Call && !exits.Elsewhere && !exits.Entries && !exits.Returns;
                        readsAnywhere |= MemoryUse.Of(i) is { Reads: true, Target: MemoryTarget.Pointer or MemoryTarget.Number };
                        break;
                }
            }
            if (!followed) continue;

            // Each variable byte the compiler stores in the loop, and the stores.
            var stores = new Dictionary<VarOperand, List<int>>();
            for (int k = h; k <= b; k++)
                if (code[k] is Instr { AsmPos: null, Mode: Absolute or ZeroPage, Operand: VarOperand o } i && Stored(i.Mnemonic) is not null
                    && Tracked(o.Variable, function) && !(readsAnywhere && OrdinaryGlobal(o.Variable)))
                    (stores.TryGetValue(o, out var list) ? list : stores[o] = []).Add(k);

            foreach (var (o, at) in stores)
            {
                var storage = o.Variable.Storage;
                // No other instruction in the loop names the variable.
                bool alone = true;
                for (int k = h; k <= b && alone; k++)
                    if (code[k] is Instr i && !at.Contains(k))
                        alone = !(i.Operand is VarOperand w && w.Variable.Storage == storage) && !(i.Operand is ImmVarByte a && a.Variable.Storage == storage);
                if (!alone) continue;

                // The exits: falling out at the end, inserted after the labels that follow it if only
                // the loop jumps to them (a break), and jumps to other labels outside, inserted just
                // after the label (every path into it has a register holding the value).
                before ??= Analyse(code, function);
                var exits = new List<(int At, State? State)>();
                int fall = -1;
                if (FallsThrough(back))
                {
                    fall = b + 1;
                    while (fall < code.Count && code[fall] is LabelDef or Comment or SourceLine
                           && !(code[fall] is LabelDef d && (unknown.Contains(d.Label) || JumpedFromOutside(d.Label)))) fall++;
                    State? s = null;
                    if (fall > b + 1) s = before[fall - 1];
                    else if (before[b] is { } last) { s = last.Copy(); Step(s, back, function, false); }
                    exits.Add((fall, s));
                }
                for (int k = h; k <= b; k++)
                    if (code[k] is Instr i && Target(i) is { } t && positions[t] is int tp && !InLoop(tp)
                        && !(tp > b && tp < fall) && exits.All(e => e.At != tp + 1))
                        exits.Add((tp + 1, before[tp]));
                if (exits.Count > at.Count) continue;

                var inserts = new List<(int At, Instr Store)>();
                foreach (var (pos, s) in exits)
                {
                    if (s is null) break;
                    Reg? held = new[] { Reg.A, Reg.X, Reg.Y }.Cast<Reg?>().FirstOrDefault(r => s.Regs[(int)r!].Contains(o));
                    if (held is not Reg r) break;
                    inserts.Add((pos, new Instr(r == Reg.A ? Sta : r == Reg.X ? Stx : Sty, Absolute, o)));
                }
                if (inserts.Count != exits.Count) continue;

                // Positions after the loop first, then the stores in it, from the end, so indexes stay right.
                var changes = inserts.Select(x => (x.At, (Instr?)x.Store)).Concat(at.Select(k => (At: k, (Instr?)null)))
                    .OrderByDescending(x => x.At).ThenBy(x => x.Item2 is null ? 0 : 1);
                foreach (var (pos, store) in changes)
                {
                    if (store is null) code.RemoveAt(pos);
                    else code.Insert(pos, store);
                }
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Removes a <c>jmp</c> or branch to the label that follows it (only labels, comments and source
    /// lines between): an <c>if</c> whose <c>else</c> became empty when its store moved out of a loop
    /// (F15). Neither changes a register or flag. Returns true if any was removed.
    /// </summary>
    private static bool RemoveJumpsToNext(List<AsmItem> code)
    {
        bool removed = false;
        for (int k = code.Count - 1; k >= 0; k--)
        {
            if (code[k] is not Instr i || FromAsm(i) || Target(i) is not { } t) continue;
            for (int j = k + 1; j < code.Count && code[j] is LabelDef or Comment or SourceLine; j++)
                if (code[j] is LabelDef d && d.Label == t) { code.RemoveAt(k); removed = true; break; }
        }
        return removed;
    }

    /// <summary>Assembly that calls a subroutine of its own block (<c>jsr .sub</c>): its <c>rts</c> returns into this code.</summary>
    private static bool CallsOwnSubroutine(List<AsmItem> code, Dictionary<Label, int> positions) =>
        code.Any(item => item is Instr { Operand: LabelOperand { Label: var l } } i && Instructions.Of(i.Mnemonic).Flow == Flow.Call && positions.ContainsKey(l));

    private static CpuState Flag(Reg r) => r switch { Reg.A => CpuState.A, Reg.X => CpuState.X, _ => CpuState.Y };

    /// <summary>The registers (A, X, Y) that hold the function's return value, which its <c>rts</c> reads.</summary>
    private static CpuState Returned(FunctionSymbol function)
    {
        var returned = CpuState.None;
        foreach (var r in new[] { Reg.A, Reg.X, Reg.Y })
            if (function.ReturnRegister is { } ret && ret.Uses(r switch { Reg.A => Register.A, Reg.X => Register.X, _ => Register.Y }))
                returned |= Flag(r);
        return returned;
    }

    /// <summary>
    /// The registers (A, X, Y) that may be read before they are written, before each item. A call
    /// may read all three (arguments); <c>rts</c> reads the return value's registers, or what the
    /// labels of a jump table read; anything the analysis cannot follow reads all three.
    /// </summary>
    private static CpuState[] RegistersLive(List<AsmItem> code, FunctionSymbol function, Dictionary<Label, int> positions)
    {
        const CpuState all = CpuState.A | CpuState.X | CpuState.Y;
        var unknown = UnknownEntries(code).Where(positions.ContainsKey).Select(l => positions[l]).ToList();
        var returned = Returned(function);
        var live = new CpuState[code.Count + 1];
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int k = code.Count - 1; k >= 0; k--)
            {
                var item = code[k];
                CpuState after = CpuState.None, result;
                var exits = ExitsOf(code, k, positions);
                if (exits.Next) after |= live[k + 1];
                if (item is Instr i)
                {
                    if (exits.Inside is int tp) after |= live[tp];
                    if (exits.Returns) after |= returned;
                    if (exits.Entries) foreach (int u in unknown) after |= live[u];
                    if (exits.Elsewhere) after = all;
                    var (reads, writes) = Instructions.Effects(i.Mnemonic, i.Mode);
                    result = Instructions.Of(i.Mnemonic).Flow == Flow.Call ? all : ((after & ~writes) | reads) & all;
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
            var exits = ExitsOf(code, k, positions);
            if (exits.Inside is int tp) Join(tp, after);
            if (exits.Next) Join(k + 1, after);
        }
        return before.Select(b => b == true).ToArray();
    }

    /// <summary>
    /// True if a path from item <paramref name="from"/> (through jumps, branches and fall-through)
    /// reaches an item in <paramref name="targets"/> without passing <paramref name="stop"/>.
    /// </summary>
    private static bool Reaches(List<AsmItem> code, Dictionary<Label, int> positions, int from, Func<int, bool> targets, int stop)
    {
        var seen = new HashSet<int>();
        var work = new Stack<int>();
        void Next(int j)
        {
            var exits = ExitsOf(code, j, positions);
            if (exits.Inside is int tp) work.Push(tp);
            if (exits.Next && j + 1 < code.Count) work.Push(j + 1);
        }
        Next(from);
        while (work.Count > 0)
        {
            int j = work.Pop();
            if (targets(j)) return true;
            if (j == stop || !seen.Add(j)) continue;
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
    private static bool ReadCopiesFromSource(List<AsmItem> code, FunctionSymbol function, InstructionSet instructions)
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
                usable = !FromAsm(i) && MemoryUse.Of(i) is { Target: MemoryTarget.Byte, Writes: false, Operand.Offset: 0 };
                reads.Add(k);
            }
            if (!usable || reads.Count == 0) continue;

            // Where v equals the source memory.
            bool Ends(int k)
            {
                if (code[k] is not Instr i) return false;
                if (ChangesAnything(i)) return true;
                if (index is Reg r && Instructions.Effects(i.Mnemonic, i.Mode).Writes.HasFlag(Flag(r))) return true;
                var use = MemoryUse.Of(i);
                if (!use.Writes) return false;
                if (use.Target is not (MemoryTarget.Byte or MemoryTarget.Element)) return true;
                var w = use.Operand!.Variable;
                return w.Storage == source || w.Storage == v.Storage || w.Storage.InOverlayArea;
            }
            var same = HoldsBefore(code, positions, k => k == s, Ends);

            var fromSource = new List<int>();
            var fromRegister = new List<int>();
            foreach (int r in reads)
            {
                var i = (Instr)code[r];
                if (same[r] && instructions.TryGet(i.Mnemonic, load.Mode, out _)) fromSource.Add(r);
                else if (i.Mnemonic == Lda) fromRegister.Add(r);
                else { usable = false; break; }
            }
            // A read moved to the source must not run more often than the store: in a loop without it.
            if (!usable || fromSource.Any(r => Reaches(code, positions, r, j => j == r, s))) continue;

            // A store into the source memory that ends the copy while it is still needed: load the
            // value into a free register just before it.
            var loads = new List<int>();
            Reg held = Reg.A;
            if (fromRegister.Count > 0)
            {
                var needed = fromRegister.ToHashSet();
                for (int k = 0; k < code.Count && usable; k++)
                {
                    // Still needed: a read from the register can follow before the next store of v.
                    if (k == s || !same[k] || !Ends(k) || !Reaches(code, positions, k, needed.Contains, s)) continue;
                    // Only a store into the source memory, not a call or a change of the index.
                    usable = code[k] is Instr { AsmPos: null } i
                        && MemoryUse.Of(i) is { Writes: true, Target: MemoryTarget.Byte or MemoryTarget.Element, Operand.Variable: var w } && w.Storage == source
                        && !(index is Reg r && Instructions.Effects(i.Mnemonic, i.Mode).Writes.HasFlag(Flag(r)));
                    loads.Add(k);
                }
                if (!usable) continue;
                live ??= RegistersLive(code, function, positions);
                var choices = new[] { Reg.Y, Reg.X }.Where(r => r != index
                    && instructions.TryGet(r == Reg.Y ? Ldy : Ldx, load.Mode, out _)
                    && loads.All(k => (live[k] & Flag(r)) == 0 && !NzLiveAfter(code, k - 1, function, positions))).ToList();
                if (choices.Count == 0) continue;
                held = choices[0];
                // The register still holds v at each lda v.
                var regFlag = Flag(held);
                var loadSet = loads.ToHashSet();
                var holds = HoldsBefore(code, positions, k => loadSet.Contains(k) && !EndsHold(k), EndsHold);
                bool EndsHold(int k) => k == s || (code[k] is Instr i && (ChangesAnything(i) || Instructions.Effects(i.Mnemonic, i.Mode).Writes.HasFlag(regFlag)));
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
            if (code[k] is not Instr { AsmPos: null } i || !Straight(i)) break;
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
            var use = MemoryUse.Of(i);
            if (use.Writes)
            {
                // Through a pointer, at a number or into code: anything may change.
                if (use.Target == MemoryTarget.Byte) written.Add(use.Operand!);
                else if (use.Target == MemoryTarget.Element) writtenWhole.Add(use.Operand!.Variable);
                else break;
            }
            var (_, writes) = Instructions.Effects(i.Mnemonic, i.Mode);
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
        bool entries = UnknownEntries(code).Count > 0;
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
                // rts reads the return value's registers (N and Z for a bool in a flag), and, if it may
                // be an indexed match's dispatch, what the arms it enters read.
                bool deadAtReturn = Instructions.Of(n.Mnemonic).Flow == Flow.Return && DeadAtReturn(code, j, reg, function, entries);
                if (!deadAtReturn && !Straight(n)) break;
                var (reads, writes) = Instructions.Effects(n.Mnemonic, n.Mode);
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
    /// True if neither <paramref name="reg"/> nor N and Z are read after the compiler's <c>rts</c> at
    /// <paramref name="j"/>: by the caller (the return value), or by the labels a jump table's
    /// dispatch may enter (<see cref="RegistersLive"/>, the same liveness as the other analyses).
    /// </summary>
    private static bool DeadAtReturn(List<AsmItem> code, int j, CpuState reg, FunctionSymbol function, bool entries)
    {
        var positions = Positions(code);
        var live = entries ? RegistersLive(code, function, positions)[j] : Returned(function);
        return !live.HasFlag(reg) && !NzLiveAfter(code, j - 1, function, positions);
    }

    /// <summary>A call or <c>brk</c>: may change any register and any ordinary memory.</summary>
    private static bool ChangesAnything(Instr i) => Instructions.Of(i.Mnemonic).Flow is Flow.Call or Flow.Break;

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

    /// <summary>A jump or branch to a label: the only way into a label the analysis can follow.</summary>
    private static Label? Target(Instr i) =>
        Instructions.Of(i.Mnemonic).Flow is Flow.Branch or Flow.Jump && i.Mode != Indirect && i.Operand is LabelOperand { Label: var l, Offset: 0 } ? l : null;

    /// <summary>True if execution can go on to the next item.</summary>
    private static bool FallsThrough(AsmItem item) => item switch
    {
        Instr i => Instructions.FallsThrough(i.Mnemonic),
        LabelTable or DataBytes or AddressByte => false,
        _ => true,
    };

    /// <summary>
    /// Where control may go after an item (review backend 9): the one description of control flow
    /// that every analysis of the optimizer follows.
    /// </summary>
    /// <param name="Inside">The position of the label a jump or branch goes to, in this code.</param>
    /// <param name="Next">Goes on to the next item (at the last item: the end of the code).</param>
    /// <param name="Elsewhere">
    /// May go where the analysis cannot follow: a jump or branch to an address or to a label outside
    /// this code, <c>jmp (v)</c>, <c>brk</c>, <c>rti</c>.
    /// </param>
    /// <param name="Entries">
    /// May go to the labels entered from where the analysis cannot see (<see cref="UnknownEntries"/>):
    /// an <c>rts</c> may be a jump table's dispatch, a <c>jmp (v)</c> may jump to one.
    /// </param>
    /// <param name="Returns"><c>rts</c>: may return to the caller.</param>
    private readonly record struct Exits(int? Inside, bool Next, bool Elsewhere, bool Entries, bool Returns);

    private static Exits ExitsOf(List<AsmItem> code, int k, Dictionary<Label, int> positions)
    {
        if (code[k] is not Instr i) return new(null, FallsThrough(code[k]), false, false, false);
        var flow = Instructions.Of(i.Mnemonic).Flow;
        int? inside = Target(i) is { } t && positions.TryGetValue(t, out int p) ? p : null;
        bool indirect = flow == Flow.Jump && i.Mode == Indirect;
        return new(inside, FallsThrough(i),
            Elsewhere: (flow is Flow.Branch or Flow.Jump && inside is null) || flow is Flow.Break or Flow.ReturnFromInterrupt,
            Entries: flow == Flow.Return || indirect,
            Returns: flow == Flow.Return);
    }

    /// <summary>True if control goes on only to the next instruction: not a jump, branch, call, return or brk.</summary>
    private static bool Straight(Instr i) => Instructions.Of(i.Mnemonic).Flow == Flow.Next;

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
            var exits = ExitsOf(code, k, positions);
            if (exits.Inside is int t) work.Push(t);
            if (exits.Next) work.Push(k + 1);
        }
        for (int k = code.Count - 1; k >= 0; k--)
            if (!reached[k] && code[k] is Instr i && !FromAsm(i)) code.RemoveAt(k);
    }
}
