using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Backend;

/// <summary>
/// Generates instructions for each function. Variables are referred to symbolically; their
/// addresses are assigned afterwards by the <see cref="Allocator"/>.
/// </summary>
public sealed partial class CodeGen
{
    /// <summary>A two-byte location in memory: <see cref="Var"/> + <see cref="Offset"/>.</summary>
    private readonly record struct Loc(VariableSymbol Var, int Offset = 0)
    {
        public VarOperand Lo => CodeGen.Var(Var, Offset);
        public VarOperand Hi => CodeGen.Var(Var, Offset + 1);
    }

    private readonly CheckedProgram _program;
    /// <summary>The lines of each source file, by path ("" for source given without one).</summary>
    private readonly Dictionary<string, string[]> _sourceLines = new();
    private readonly string? _mainFile;
    private readonly Dictionary<FunctionSymbol, Label> _functionLabels = new();
    private List<AsmItem> _out = [];
    private FunctionSymbol _fn = null!;
    private int _labelCounter;
    private SourcePos _lastSource;
    private int _loopDepth;
    private readonly List<(Label Break, Label Continue)> _loops = [];
    private readonly HashSet<Register> _reserved = [];
    /// <summary>The largest value each enclosing <c>for</c> counter can have in the body, where its bounds prove one (spec 3.2).</summary>
    private readonly Dictionary<VariableSymbol, long> _counterMax = [];
    private readonly List<VariableSymbol> _freeTemps = [];
    private int _tempCounter;

    /// <summary>False when the program is compiled without the optimizer (<see cref="TernCompiler.Compile"/>).</summary>
    private readonly bool _optimize;

    public CodeGen(CheckedProgram program, bool optimize = true)
    {
        _program = program;
        _optimize = optimize;
        foreach (var m in program.Modules)
            _sourceLines[m.Path ?? ""] = m.Source.Split('\n');
        _mainFile = program.Modules[^1].Path;
        // A routine at a fixed address is never placed: its label already has its address.
        foreach (var f in program.Functions)
            _functionLabels[f] = new Label(f.Name) { Address = f.FixedAddress ?? -1 };
    }

    public Label LabelOf(FunctionSymbol f) => _functionLabels[f];

    /// <summary>The code of each compiled function, after optimization (for the register use of a binary's entries).</summary>
    public Dictionary<FunctionSymbol, List<AsmItem>> FunctionItems { get; } = [];

    /// <summary>The function a label belongs to, or null for a label inside a function.</summary>
    public FunctionSymbol? FunctionOf(Label label) => _functionLabels.FirstOrDefault(p => ReferenceEquals(p.Value, label)).Key;

    public List<AsmItem> GenerateFunctions()
    {
        var all = new List<AsmItem>();
        foreach (var f in _program.Functions.Where(f => f.FixedAddress is null && !f.IsInline))
        {
            GenFunction(f);
            all.Add(new SectionStart(f.Name, Overlay: f.OverlayUnit));
            // An interrupt handler's body is optimized before its saving code is added.
            // main runs once, right after the startup code has cleared memory.
            bool startsProgram = f == _program.Main && !_program.IsBinary && !f.AddressTaken;
            if (_optimize && !f.Declaration!.IsAsm && !(f.IsInterrupt && !f.IsPart)) Optimizer.Run(_out, f, startsProgram);
            FunctionItems[f] = _out.ToList();
            all.AddRange(_out);
        }
        // An inline function has no frame of its own: what it used is in its callers' frames.
        foreach (var f in _program.Functions.Where(f => f.IsInline)) f.FrameVariables.Clear();
        foreach (var ((_, original), copy) in _instances) copy.Weight = original.Weight;
        return all;
    }

    // ---------- Emission helpers ----------

    private void Emit(Mnemonic m, AddressingMode mode = Implied, Operand? op = null) => _out.Add(new Instr(m, mode, op));

    /// <summary>An instruction with a value operand: immediate or memory.</summary>
    private void Op(Mnemonic m, Operand op) => Emit(m, op is Imm or ImmLabelByte or ImmVarByte ? Immediate : Absolute, op);

    private void Place(Label l) => _out.Add(new LabelDef(l));
    private void Jmp(Label l) => Emit(Mnemonic.Jmp, Absolute, new LabelOperand(l));
    private void Branch(Mnemonic m, Label l) => Emit(m, Relative, new LabelOperand(l));
    private Label NewLabel(string hint) => new($"{_fn.Name}.{hint}{_labelCounter++}");
    /// <summary>A byte of a variable. A view (a struct field, a field table) is a byte of the variable it is part of.</summary>
    private static VarOperand Var(VariableSymbol v, int offset = 0) =>
        v.AliasOf is { } storage ? new(storage, v.AliasOffset + offset) : new(v, offset);

    /// <summary>
    /// An indexed operand into one table of an array (its low bytes, or the high bytes at
    /// <paramref name="table"/>), with a folded offset <paramref name="shift"/>, indexed by <paramref name="index"/>. It names the table,
    /// so the emulator can report an index outside it (M20, spec 3.2); the code is the same.
    /// </summary>
    private static VarOperand Indexed(VariableSymbol arr, int table, int shift, Expr index)
    {
        var storage = arr.AliasOf ?? arr;
        // Whole records (linear or indexed struct arrays): the index is the record's offset.
        bool records = arr.AliasOf is { Type: { IsArray: true, Element.IsStruct: true } } s && (s.Type.Linear || s.IsIndexed);
        int stride = records ? storage.Type.Element!.Size : arr.Type.Linear ? 2 : 1;
        int length = records ? arr.Type.Length : arr.Type.Linear ? arr.Type.Size : arr.Type.Length;
        string name = storage == arr || records ? storage.QualifiedName : arr.Name;
        return Var(arr, table + shift) with { Check = new IndexCheck(name, shift, length, records ? storage.Type.Length : arr.Type.Length, stride) { At = index.Pos } };
    }

    /// <summary>The check of an indexed match's jump table: values from <paramref name="low"/> to low + span - 1.</summary>
    private static IndexCheck MatchCheck(int span, int low, bool signed) => new("", 0, span, span, MatchLow: low, Signed: signed);

    /// <summary>
    /// The distance from the low-byte table of a split array to its high-byte table; 1 for a linear
    /// array, whose index the checker has already doubled.
    /// </summary>
    private static int HighTable(VariableSymbol arr) => arr.HighTable ?? (arr.Type.Linear ? 1 : arr.Type.Length);
    private static Imm Byte(long v) => new((int)(v & 0xFF));

    /// <param name="zeroPage">The temporary must be in zero page, e.g. to hold a pointer for (zp),y.</param>
    private VariableSymbol AcquireTemp(int size, bool zeroPage = false)
    {
        int i = _freeTemps.FindIndex(t => t.Type.Size == size && t.Dereferenced == zeroPage);
        VariableSymbol temp;
        if (i >= 0)
        {
            temp = _freeTemps[i];
            _freeTemps.RemoveAt(i);
        }
        else
        {
            temp = new VariableSymbol($"_t{_tempCounter++}", _lastSource, size == 1 ? TernType.U8 : TernType.U16, VariableKind.Temp, _fn)
            {
                Dereferenced = zeroPage,
            };
            _fn.FrameVariables.Add(temp);
        }
        temp.Weight += 2 * (long)Math.Pow(8, Math.Min(_loopDepth, 5));
        return temp;
    }

    private void Release(VariableSymbol temp) => _freeTemps.Add(temp);

    /// <summary>True if the last instruction left N and Z matching the value in A.</summary>
    private bool FlagsMatchA()
    {
        if (_out.Count == 0 || _out[^1] is not Instr i) return false;
        return i.Mnemonic is Lda or And or Ora or Eor or Adc or Sbc or Txa or Tya or Pla
            || (i.Mnemonic is Asl or Lsr or Rol or Ror && i.Mode == Accumulator);
    }

    private bool EndsWithJump() => _out.Count > 0 && _out[^1] is Instr { Mnemonic: Rts or Mnemonic.Jmp };

    private void Source(Node node) => Source(node.Pos);

    private void Source(SourcePos pos)
    {
        if ((pos.Line == _lastSource.Line && pos.File == _lastSource.File)
            || !_sourceLines.TryGetValue(pos.File ?? "", out var lines) || pos.Line <= 0 || pos.Line > lines.Length) return;
        _lastSource = pos;
        // Lines from a module show its file name.
        var file = pos.File == _mainFile ? null : Path.GetFileName(pos.File);
        _out.Add(new SourceLine(pos.Line, lines[pos.Line - 1].TrimEnd('\r'), file));
    }

    private Register FreeIndexRegister(SourcePos pos) =>
        !_reserved.Contains(Register.X) ? Register.X
        : !_reserved.Contains(Register.Y) ? Register.Y
        : throw new CompileError(pos, "No free index register: X and Y are both used by loop variables. Remove 'using' from one of the loops");

    // ---------- Functions ----------

    private void GenFunction(FunctionSymbol f)
    {
        _fn = f;
        _frameOwner = f;
        _out = [];
        _freeTemps.Clear();
        _lastSource = default;

        Source(f.Declaration!);
        Place(_functionLabels[f]);

        // An asm fn: register parameters stay where they arrive. An rts is added unless the
        // block ends with rts, rti or jmp.
        if (f.Declaration!.IsAsm)
        {
            var block = (AsmStmt)f.Declaration.Body.Statements[0];
            GenAsm(block);
            if (block.Lines.LastOrDefault() is not AsmInstruction { Mnemonic: Rts or Rti or Mnemonic.Jmp }) Emit(Rts);
            return;
        }

        bool handler = f.IsInterrupt && !f.IsPart;
        var kept = handler ? null : KeptRegister(f);
        StoreRegisterParameters(f);

        if (handler)
        {
            GenInterrupt(f);
            return;
        }
        if (kept is { } k) _reserved.Add(k);
        var inA = f.Parameters.FirstOrDefault(p => p.InMemory && p.PassedIn is Register.A or Register.AX);
        _aParameter = inA is null ? null : (inA, _out.Count);
        GenBlock(f.Declaration!.Body);
        _aParameter = null;
        if (kept is { } r) _reserved.Remove(r);
        if (!EndsWithJump())
        {
            Source(f.Declaration!); // the closing rts belongs to the function's line (source map)
            Emit(Rts);
        }
    }

    /// <summary>The <c>using A</c> or <c>using AX</c> parameter of the function being generated, and where its body's code starts.</summary>
    private (VariableSymbol Parameter, int From)? _aParameter;

    /// <summary>
    /// True if its registers (A, or A and X) hold <paramref name="e"/> here: it is the <c>using A</c>
    /// or <c>using AX</c> parameter, and the body's code so far is straight-line code (no label) that
    /// changes neither those registers nor the parameter.
    /// </summary>
    private bool AHolds(Expr e) =>
        _aParameter is var (p, from) && e is NameExpr { Symbol: VariableSymbol v } && v == p
        && _out.Skip(from).All(item => item is Comment or SourceLine
            || (item is Instr { AsmPos: null } i && i.Mnemonic is not (Jsr or Brk)
                && (AsmEffects.Of(i.Mnemonic, i.Mode).Writes & (p.PassedIn == Register.AX ? CpuState.A | CpuState.X : CpuState.A)) == 0
                && !(i.Operand is VarOperand { Variable: var w } && w.Storage == p && AsmEffects.WritesMemory(i.Mnemonic, i.Mode))));

    /// <summary>Functions already decided by <see cref="KeptRegister"/>, with the register kept (or null).</summary>
    private readonly Dictionary<FunctionSymbol, Register?> _keptRegisters = new();

    /// <summary>
    /// A parameter passed in X or Y stays there when the body allows it, like a <c>for ... using X</c>
    /// counter: the body calls nothing, has no asm block and no loop held in a register, never assigns
    /// the parameter or takes its address, and does not return a value in that register. One such
    /// parameter per function, so that the other index register stays free. Returns its register.
    /// </summary>
    private Register? KeptRegister(FunctionSymbol f)
    {
        if (_keptRegisters.TryGetValue(f, out var decided)) return decided;
        var body = f.Declaration!.Body;
        var p = f.Parameters.FirstOrDefault(p => p.PassedIn is Register.X or Register.Y && p.Type.Size == 1
            && !p.AddressTaken && !AssignsVar(body, p) && !(f.ReturnRegister is { } ret && ret.Uses(p.PassedIn!.Value)));
        Register? kept = null;
        // In Y, every pointer access must be exactly p[param]; others need to load Y.
        if (p is not null && KeepsIndexRegistersFree(body) && (p.PassedIn != Register.Y || PointerIndexes(body, p).Other == 0))
        {
            kept = p.PassedIn!.Value;
            p.Register = kept;
            f.FrameVariables.Remove(p);
        }
        _keptRegisters[f] = kept;
        return kept;
    }

    /// <summary>
    /// True if the code leaves X and Y alone apart from its own use of them: it calls nothing and has
    /// no asm block or loop held in a register. A parameter or counter may then stay in X or Y.
    /// </summary>
    private static bool KeepsIndexRegistersFree(Node node) => !Descendants(node).Any(n => n
        is CallExpr { Function: not null } or AsmStmt or ForStmt { UsingRegister: not null });

    /// <summary>The nodes directly inside a statement or expression.</summary>
    private static IEnumerable<Node> Children(Node node)
    {
        switch (node)
        {
            case BlockStmt b: return b.Statements;
            case IfStmt i: return i.Else is null ? [i.Condition, i.Then] : [i.Condition, i.Then, i.Else];
            case WhileStmt w: return [w.Condition, w.Body];
            case ForStmt f: return f.Step is null ? [f.From, f.To, f.Body] : [f.From, f.To, f.Step, f.Body];
            case MatchStmt m: return m.Arms.Select(a => (Node)a.Body).Prepend(m.Subject);
            case ReturnStmt { Value: { } v }: return [v];
            case AssignStmt a: return [a.Target, a.Value];
            case ExprStmt e: return [e.Expr];
            case VarDecl { Init: { } init }: return [init];
            case UnaryExpr u: return [u.Operand];
            case BinaryExpr b: return [b.Left, b.Right];
            case IndexExpr ix: return [ix.Target, ix.Index];
            case MemberExpr m: return [m.Target];
            case CastExpr c: return [c.Operand];
            case CallExpr c: return c.Args;
            case ArrayLiteralExpr l: return l.Elements;
            default: return [];
        }
    }

    /// <summary>A node and everything inside it.</summary>
    private static IEnumerable<Node> Descendants(Node node) => Children(node).SelectMany(Descendants).Prepend(node);

    /// <summary>
    /// Pointer accesses in the code: those that are exactly <c>p[v]</c> (Y = v, no ldy needed) and
    /// the others, which load Y themselves.
    /// </summary>
    private static (int Plain, int Other) PointerIndexes(Node node, VariableSymbol v)
    {
        var accesses = Descendants(node).OfType<IndexExpr>().Where(IsPointerIndex).ToList();
        int plain = accesses.Count(ix => PlainPointerIndex(ix) && ix.Index is NameExpr { Symbol: var s } && s == v);
        return (plain, accesses.Count - plain);
    }

    /// <summary>
    /// Register parameters are stored into the frame, so the body can use A, X and Y freely, except
    /// one that stays in its register (<see cref="KeptRegister"/>).
    /// </summary>
    private void StoreRegisterParameters(FunctionSymbol f)
    {
        foreach (var p in f.Parameters.Where(p => p.InMemory))
        {
            switch (p.PassedIn)
            {
                case Register.A: Op(Sta, Var(p)); break;
                case Register.X: Op(Stx, Var(p)); break;
                case Register.Y: Op(Sty, Var(p)); break;
                case Register.AX: Op(Sta, Var(p)); Op(Stx, Var(p, 1)); break;
                case Register.AY: Op(Sta, Var(p)); Op(Sty, Var(p, 1)); break;
                case Register.XY: Op(Stx, Var(p)); Op(Sty, Var(p, 1)); break;
            }
        }
    }

    /// <summary>Where a <c>return</c> in an interrupt handler goes: the code that restores the registers.</summary>
    private Label? _interruptExit;
    /// <summary>Where a <c>return</c> goes in the copy of an inline function being generated: its end.</summary>
    private Label? _inlineExit;
    /// <summary>Free temporaries of each inline function, kept between its copies, which never run at the same time.</summary>
    private readonly Dictionary<FunctionSymbol, List<VariableSymbol>> _inlineTemps = new();
    /// <summary>The function whose code is being generated: the one whose frame holds the variables of the inline copies in it.</summary>
    private FunctionSymbol _frameOwner = null!;
    /// <summary>The copy of an inline function's variable in a caller's frame (spec 6.2), for each caller.</summary>
    private readonly Dictionary<(FunctionSymbol Owner, VariableSymbol Original), VariableSymbol> _instances = new();

    /// <summary>
    /// Moves the variables of the inline function <paramref name="f"/>, in the code from
    /// <paramref name="start"/> on (the arguments and the copy of the body), into the frame of the
    /// function being generated. All copies of <paramref name="f"/> in one caller share these
    /// variables, as the calls of an ordinary function share its frame; two callers have two sets,
    /// so an inline function may be used from main and from an interrupt handler.
    /// </summary>
    private void MoveInlineFrame(FunctionSymbol f, int start)
    {
        VariableSymbol InstanceOf(VariableSymbol original)
        {
            if (_instances.TryGetValue((_frameOwner, original), out var copy)) return copy;
            copy = new VariableSymbol($"{f.Name}.{original.Name}", original.Pos, original.Type, original.Kind, _frameOwner)
            {
                InlineOf = f,
                IsIndexed = original.IsIndexed,
                PassedIn = original.PassedIn,
                ReadOnly = original.ReadOnly,
                RequireZeroPage = original.RequireZeroPage,
                Dereferenced = original.Dereferenced,
                AddressTaken = original.AddressTaken,
                AsmZeroPage = original.AsmZeroPage,
                OffsetTable = original.OffsetTable,
            };
            copy.JumpVectorOffsets.UnionWith(original.JumpVectorOffsets);
            _instances[(_frameOwner, original)] = copy;
            _frameOwner.FrameVariables.Add(copy);
            return copy;
        }
        for (int i = start; i < _out.Count; i++)
        {
            if (_out[i] is not Instr { Operand: { } operand } instr) continue;
            Operand? moved = operand switch
            {
                VarOperand v when v.Variable.Owner == f => v with { Variable = InstanceOf(v.Variable) },
                ImmVarByte v when v.Variable.Owner == f => v with { Variable = InstanceOf(v.Variable) },
                _ => null,
            };
            if (moved is not null) _out[i] = instr with { Operand = moved };
        }
    }

    /// <summary>Leaves the function: <c>rts</c>, or a jump to the end of an inline copy or of an interrupt handler.</summary>
    private void ReturnJump()
    {
        if (_inlineExit is { } end) Jmp(end);
        else if (_interruptExit is { } exit) Jmp(exit);
        else Emit(Rts);
    }

    /// <summary>
    /// The body of an inline function in place of <c>jsr</c>; the arguments are already in its
    /// parameters or registers, as for a call. Its parameters, locals and temporaries stay in its
    /// own frame, so the call graph and memory sharing are the same as for a call. Its temporaries
    /// are kept apart from the caller's, since they are in its frame.
    /// </summary>
    private void GenInline(FunctionSymbol f)
    {
        var (caller, callerExit, interruptExit, callerTemps) = (_fn, _inlineExit, _interruptExit, _freeTemps.ToList());
        var callerSource = _lastSource;
        _fn = f;
        _interruptExit = null;
        _freeTemps.Clear();
        if (_inlineTemps.TryGetValue(f, out var pool)) _freeTemps.AddRange(pool);
        var end = NewLabel("inline_end");
        _inlineExit = end;
        _out.Add(new Comment($"inline {f.Name}"));
        // An inline asm fn keeps its register parameters where they arrive, as an asm fn does.
        bool isAsm = f.Declaration!.IsAsm;
        var kept = isAsm ? null : KeptRegister(f);
        if (!isAsm) StoreRegisterParameters(f);
        if (kept is { } k) _reserved.Add(k);
        GenBlock(f.Declaration!.Body);
        if (kept is { } r) _reserved.Remove(r);
        if (_out[^1] is Instr { Mnemonic: Mnemonic.Jmp, Operand: LabelOperand { Label: var l } } && l == end)
            _out.RemoveAt(_out.Count - 1);
        Place(end);
        (_fn, _inlineExit, _interruptExit) = (caller, callerExit, interruptExit);
        _inlineTemps[f] = _freeTemps.ToList();
        _freeTemps.Clear();
        _freeTemps.AddRange(callerTemps);
        // The rest of the caller's statement belongs to the caller's line again (source map).
        if (_lastSource != callerSource)
        {
            _lastSource = default;
            Source(callerSource);
        }
    }

    /// <summary>
    /// An interrupt handler: the body, between code that saves and restores the registers it
    /// changes, then <c>rti</c> (the CPU saved the flags). A call may change every register. Saving
    /// X or Y goes through A, so A is saved whenever X or Y is.
    /// </summary>
    private void GenInterrupt(FunctionSymbol f)
    {
        int start = _out.Count;
        _interruptExit = NewLabel("exit");
        GenBlock(f.Declaration!.Body);
        if (_out[^1] is Instr { Mnemonic: Mnemonic.Jmp, Operand: LabelOperand { Label: var l } } && l == _interruptExit)
            _out.RemoveAt(_out.Count - 1);
        var body = _out.GetRange(start, _out.Count - start);
        if (_optimize) Optimizer.Run(body, f);
        _out.RemoveRange(start, _out.Count - start);
        _out.AddRange(body);

        var changed = CpuState.None;
        foreach (var i in _out.Skip(start).OfType<Instr>())
            changed |= AsmEffects.Of(i.Mnemonic, i.Mode).Writes;
        bool x = changed.HasFlag(CpuState.X), y = changed.HasFlag(CpuState.Y);
        bool a = x || y || changed.HasFlag(CpuState.A);

        var save = new List<AsmItem>();
        if (a) save.Add(new Instr(Pha));
        if (x) { save.Add(new Instr(Txa)); save.Add(new Instr(Pha)); }
        if (y) { save.Add(new Instr(Tya)); save.Add(new Instr(Pha)); }
        if (save.Count > 0) save.Insert(0, new Comment($"save {string.Join(", ", new[] { a ? "A" : null, x ? "X" : null, y ? "Y" : null }.OfType<string>())}"));
        // Main may be in decimal mode when the interrupt arrives, and only adc and sbc care (spec 6.3).
        if (_out.Skip(start).OfType<Instr>().Any(i => i.Mnemonic is Mnemonic.Adc or Mnemonic.Sbc or Mnemonic.Jsr))
            save.Insert(0, new Instr(Mnemonic.Cld));
        _out.InsertRange(start, save);

        Place(_interruptExit);
        if (y) { Emit(Pla); Emit(Tay); }
        if (x) { Emit(Pla); Emit(Tax); }
        if (a) Emit(Pla);
        Emit(Rti);
        _interruptExit = null;
    }

    // ---------- Tree queries ----------

    private static bool ContainsCall(Expr e) => e switch
    {
        CallExpr call => call.Function is not null,
        UnaryExpr u => ContainsCall(u.Operand),
        BinaryExpr b => ContainsCall(b.Left) || ContainsCall(b.Right),
        IndexExpr ix => ContainsCall(ix.Target) || ContainsCall(ix.Index),
        MemberExpr m => ContainsCall(m.Target),
        CastExpr c => ContainsCall(c.Operand),
        _ => false,
    };

    /// <summary>
    /// True if <paramref name="e"/> calls a function or reads <c>volatile</c> memory: the parts whose
    /// order can be seen, which run left to right (spec 5).
    /// </summary>
    private static bool HasEffect(Expr e) => e.ConstValue is null && e switch
    {
        CallExpr call => call.Function is not null,
        NameExpr { Symbol: { } v } => v.IsVolatile,
        UnaryExpr u => HasEffect(u.Operand),
        BinaryExpr b => HasEffect(b.Left) || HasEffect(b.Right),
        IndexExpr ix => HasEffect(ix.Target) || HasEffect(ix.Index),
        MemberExpr m => HasEffect(m.Target),
        CastExpr c => HasEffect(c.Operand),
        _ => false,
    };

    /// <summary>
    /// Spec 5: when <paramref name="first"/> and a part after it, <paramref name="later"/>, both call a
    /// function or read volatile memory, <paramref name="first"/> is computed into a temporary first,
    /// and <paramref name="gen"/> generates the code with the temporary in its place (<paramref name="put"/>
    /// swaps it in and back). The code generators may then order the parts as they like. False, with no
    /// code, otherwise.
    /// </summary>
    private bool InSourceOrder(Expr first, Expr later, Action<Expr> put, Action gen)
    {
        if (!HasEffect(first) || !HasEffect(later)) return false;
        var t = AcquireTemp(first.Type.Size);
        if (first.Type.Size == 1) { GenA(first); Op(Sta, Var(t)); }
        else Gen16(first, new Loc(t));
        put(new NameExpr(first.Pos, t.Name) { Symbol = t, Type = first.Type });
        try { gen(); }
        finally { put(first); }
        Release(t);
        return true;
    }

    /// <summary>True if <paramref name="e"/> reads any byte of <paramref name="v"/>'s memory.</summary>
    private static bool ReadsVar(Expr e, VariableSymbol v) => e switch
    {
        NameExpr { Symbol: { } s } => Overlap(s, v),
        UnaryExpr u => ReadsVar(u.Operand, v),
        BinaryExpr b => ReadsVar(b.Left, v) || ReadsVar(b.Right, v),
        IndexExpr ix => ReadsVar(ix.Target, v) || ReadsVar(ix.Index, v),
        MemberExpr m => ReadsVar(m.Target, v),
        CastExpr c => ReadsVar(c.Operand, v),
        CallExpr call => call.Args.Any(a => ReadsVar(a, v)),
        _ => false,
    };

    /// <summary>True if two variables share a byte. Different fields of one struct (views) do not.</summary>
    private static bool Overlap(VariableSymbol a, VariableSymbol b)
    {
        if (a.Storage != b.Storage) return false;
        if (a.AliasOf is null || b.AliasOf is null) return true;
        return ByteRanges(a).Any(x => ByteRanges(b).Any(y => x.Start < y.End && y.Start < x.End));
    }

    /// <summary>The bytes of a view inside its storage: one range, or the low and high tables of a split array.</summary>
    private static IEnumerable<(int Start, int End)> ByteRanges(VariableSymbol view)
    {
        int at = view.AliasOffset;
        if (view.Type.IsArray && view.Type.Element!.Size == 2)
        {
            int n = view.Type.Length;
            yield return (at, at + n);
            yield return (at + HighTable(view), at + HighTable(view) + n);
        }
        else
            yield return (at, at + view.Type.Size);
    }

    private static bool AssignsVar(Stmt s, VariableSymbol v) => s switch
    {
        AssignStmt a => BaseVar(a.Target) == v,
        BlockStmt b => b.Statements.Any(x => AssignsVar(x, v)),
        IfStmt i => AssignsVar(i.Then, v) || (i.Else is not null && AssignsVar(i.Else, v)),
        WhileStmt w => AssignsVar(w.Body, v),
        ForStmt f => AssignsVar(f.Body, v),
        MatchStmt m => m.Arms.Any(a => AssignsVar(a.Body, v)),
        AsmStmt asm => asm.Effects!.VariablesWritten.Contains(v),
        _ => false,
    };

    private static VariableSymbol? BaseVar(Expr e) => e switch
    {
        NameExpr n => n.Symbol,
        IndexExpr ix => BaseVar(ix.Target),
        MemberExpr m => BaseVar(m.Target),
        _ => null,
    };
}
