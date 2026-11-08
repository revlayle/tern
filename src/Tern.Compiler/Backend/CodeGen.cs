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
public sealed class CodeGen
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

    public CodeGen(CheckedProgram program)
    {
        _program = program;
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
            all.Add(new SectionStart(f.Name, Overlay: f.OverlayArea is null ? null : f.Module!.Name));
            // An interrupt handler's body is optimized before its saving code is added.
            // main runs once, right after the startup code has cleared memory.
            bool startsProgram = f == _program.Main && !_program.IsBinary && !f.AddressTaken;
            if (!f.Declaration!.IsAsm && !(f.IsInterrupt && !f.IsPart)) Optimizer.Run(_out, f, startsProgram);
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
        : throw new CompileError(pos, "No free index register: X and Y are both used by loop variables");

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
        Optimizer.Run(body, f);
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

    // ---------- Statements ----------

    private void GenBlock(BlockStmt block)
    {
        for (int k = 0; k < block.Statements.Count; k++)
        {
            _previous = (k > 0 ? block.Statements[k - 1] : null, block.Statements[k]);
            GenStmt(block.Statements[k]);
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
            if (w.Body.Statements.FirstOrDefault() is ForStmt first && NonEmptyInside(w.Condition, first)) _nonEmpty.Add(first);
            Place(top);
            _loops.Add((end, test));
            GenBlock(w.Body);
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
        return w.Body.Statements.LastOrDefault() is AssignStmt { Op: TokenKind.MinusEq, Target: NameExpr { Symbol: var t } } a
            && t == v && a.Value.ConstValue == step ? new VarOperand(v, offset) : null;
    }

    /// <summary>True if a <c>continue</c> in the code goes to this loop's test (not one inside a loop of its own).</summary>
    private static bool ContinuesLoop(Node node) => node switch
    {
        ContinueStmt => true,
        WhileStmt or ForStmt => false,
        _ => Children(node).Any(ContinuesLoop),
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
            Emit(Lda, mode, new LabelOperand(hiTable)); Emit(Pha);
            Emit(Lda, mode, new LabelOperand(loTable)); Emit(Pha);
            Emit(Rts);
            Place(hiTable); _out.Add(new LabelTable(targets, High: true, Offset: -1));
            Place(loTable); _out.Add(new LabelTable(targets, High: false, Offset: -1));
            MatchTables.Add((_fn, m.Pos, hiTable, loTable, span));
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
        switch (_fn.ReturnRegister)
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
                switch (_fn.ReturnRegister)
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
                if (_fn.ReturnRegister == Register.Carry) { setTrue = Sec; setFalse = Clc; }
                else if (_fn.ReturnRegister == Register.Zero) { setTrue = Lda; trueOp = Byte(0); setFalse = Lda; falseOp = Byte(1); }
                else { setTrue = Lda; trueOp = Byte(0x80); setFalse = Lda; falseOp = Byte(0); }

                void Set(Mnemonic m, Operand? op) { if (op is null) Emit(m); else Op(m, op); }

                if (value.ConstValue is long c)
                {
                    if (c != 0) Set(setTrue, trueOp); else Set(setFalse, falseOp);
                }
                else if (_fn.ReturnRegister == Register.Carry && CompareIntoCarry(value))
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

        var target = a.Target;
        bool byOne = a.Value.ConstValue == 1 && a.Op is TokenKind.PlusEq or TokenKind.MinusEq;

        // x += 1 / x -= 1 on a variable held in a register for a loop: inx / dex / iny / dey.
        if (byOne && target is NameExpr { Symbol.Register: Register.X or Register.Y } held)
        {
            bool x = held.Symbol!.Register == Register.X;
            Emit(a.Op == TokenKind.PlusEq ? (x ? Inx : Iny) : (x ? Dex : Dey));
            return;
        }

        // x += 1 / x -= 1 on memory: inc / dec.
        if (byOne && target is NameExpr { Symbol: { InMemory: true } v })
        {
            if (v.Type.Size == 1)
            {
                Op(a.Op == TokenKind.PlusEq ? Inc : Dec, Var(v));
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
            return;
        }

        // x += 2 / x -= 2 on a byte in memory: two inc / dec, shorter than the add at the same cycles (spec 11).
        // Not on a volatile byte, where the value in between would be written to the hardware.
        if (a.Op is TokenKind.PlusEq or TokenKind.MinusEq && a.Value.ConstValue == 2
            && target is NameExpr { Symbol: { InMemory: true, Type.Size: 1, IsVolatile: false } v2 })
        {
            Op(a.Op == TokenKind.PlusEq ? Inc : Dec, Var(v2));
            Op(a.Op == TokenKind.PlusEq ? Inc : Dec, Var(v2));
            return;
        }

        // w += 256 / w -= 256 on a 16-bit variable: inc w+1 / dec w+1; another multiple of 256 adds to the high byte only.
        if (a.Op is TokenKind.PlusEq or TokenKind.MinusEq && target is NameExpr { Symbol: { InMemory: true, Type.Size: 2 } wv }
            && a.Value.ConstValue is long whole && (whole & 0xFF) == 0 && whole is > 0 and <= 0xFF00)
        {
            long k = whole >> 8;
            if (k == 1) Op(a.Op == TokenKind.PlusEq ? Inc : Dec, Var(wv, 1));
            else
            {
                Op(Lda, Var(wv, 1));
                CarryFor(a.Op == TokenKind.PlusEq ? TokenKind.Plus : TokenKind.Minus);
                Op(a.Op == TokenKind.PlusEq ? Adc : Sbc, Byte(k));
                Op(Sta, Var(wv, 1));
            }
            return;
        }

        // a[i] += 1 / a[i] -= 1 with the index in X: inc a,x / dec a,x (there is no inc a,y).
        if (byOne && target is IndexExpr { Type.Size: 1 } incx && !IsPointerIndex(incx) && incx.Index.ConstValue is null
            && FoldIndexOffset(incx.Index, ArrayOf(incx)) is var (incIndex, incShift) && IndexIsCheap(incIndex)
            && (incIndex is NameExpr { Symbol.Register: Register.X } || (incIndex is not NameExpr { Symbol.Register: not null } && !_reserved.Contains(Register.X))))
        {
            LoadIndex(incIndex);   // X, by the condition above
            Emit(a.Op == TokenKind.PlusEq ? Inc : Dec, AbsoluteX, Var(ArrayOf(incx), incShift));
            return;
        }

        // Shifts of a memory byte: shift in place.
        if (a.Op is TokenKind.ShlEq or TokenKind.ShrEq && target is NameExpr { Symbol: { InMemory: true } sv }
            && sv.Type.Size == 1 && !(a.Op == TokenKind.ShrEq && sv.Type.IsSigned))
        {
            for (int i = 0; i < a.Value.ConstValue; i++) Op(a.Op == TokenKind.ShlEq ? Asl : Lsr, Var(sv));
            return;
        }

        if (ContainsCall(target))
            throw new CompileError(a.Pos, "Compound assignment to an element whose index calls a function is not supported");

        // p[i] op= v: read, change and write the element with one Y load.
        if (target is IndexExpr pix && IsPointerIndex(pix) && pix.Type.Size == 1
            && (a.Op is TokenKind.ShlEq or TokenKind.ShrEq || Simple1(a.Value) is not null))
        {
            var (p, temp) = PointerBase(pix.Target);
            LoadPointerIndex(pix, 0);
            Emit(Lda, IndirectIndexed, p);
            ApplyCompound(a, pix.Type);
            Emit(Sta, IndirectIndexed, p);
            if (temp is not null) Release(temp);
            return;
        }

        // arr[expensive index] op= v: compute the index once (a shift or an offset table), read, change and write.
        if (target is IndexExpr aix && !IsPointerIndex(aix) && aix.Type.Size == 1 && aix.Index.ConstValue is null
            && (a.Op is TokenKind.ShlEq or TokenKind.ShrEq || Simple1(a.Value) is not null)
            && FoldIndexOffset(aix.Index, ArrayOf(aix)) is var (index, shift) && !IndexIsCheap(index))
        {
            var reg = LoadIndex(index);
            var mode = reg == Register.X ? AbsoluteX : AbsoluteY;
            Emit(Lda, mode, Var(ArrayOf(aix), shift));
            ApplyCompound(a, aix.Type);
            Emit(Sta, mode, Var(ArrayOf(aix), shift));
            return;
        }

        var op = a.Op switch
        {
            TokenKind.PlusEq => TokenKind.Plus, TokenKind.MinusEq => TokenKind.Minus, TokenKind.AmpEq => TokenKind.Amp,
            TokenKind.PipeEq => TokenKind.Pipe, TokenKind.CaretEq => TokenKind.Caret, TokenKind.ShlEq => TokenKind.Shl,
            _ => TokenKind.Shr,
        };
        Assign(target, new BinaryExpr(a.Pos, op, target, a.Value) { Type = target.Type });
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
        if (target.Type.Size == 1)
        {
            if (target is IndexExpr pix && IsPointerIndex(pix) && StorePointerByteIndexFirst(pix, value)) return;
            // arr[expensive index] = v: load the index, then the value, so nothing has to be saved.
            if (target is IndexExpr aix && !IsPointerIndex(aix) && aix.Index.ConstValue is null && !ContainsCall(aix.Index) && Simple1(value) is { } simple
                && FoldIndexOffset(aix.Index, ArrayOf(aix)) is var (index, shift) && !IndexIsCheap(index))
            {
                var reg = LoadIndex(index);
                Op(Lda, simple);
                Emit(Sta, reg == Register.X ? AbsoluteX : AbsoluteY, Var(ArrayOf(aix), shift));
                return;
            }
            GenA(value);
            StoreA(target);
            return;
        }

        switch (target)
        {
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
                if (value is BinaryExpr bv && Simple2(value) is null && IndexedOperand(ix, 2) is { } element
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
                    Op(Lda, lo); Emit(Sta, mode, Var(arr, shift));
                    Op(Lda, hi); Emit(Sta, mode, Var(arr, HighTable(arr) + shift));
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
        var reg = LoadIndex(index);
        if (saved is not null)
        {
            Op(Lda, Var(saved));
            Release(saved);
        }
        Emit(Sta, reg == Register.X ? AbsoluteX : AbsoluteY, Var(arr, table + shift));
    }

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
            Emit(m, reg == Register.X ? AbsoluteX : AbsoluteY, Var(arr, (b == 0 ? 0 : HighTable(arr)) + shift));
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
                Emit(Ldy, AbsoluteX, Var(table));
                return Register.Y;
            }
            if (!_reserved.Contains(Register.X))
            {
                if (Simple1(tix.Index) is { } inner) Op(Ldx, inner);
                else { GenA(tix.Index); Emit(Tax); }
                Emit(Ldy, AbsoluteX, Var(table));
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
        Emit(Lda, reg == Register.X ? AbsoluteX : AbsoluteY, Var(arr, table + shift));
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
        if (b.Op is TokenKind.Shl or TokenKind.Shr)
        {
            GenA(b.Left);
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
            case CallExpr { FunctionAddress: { } f }:
                return (new ImmLabelByte(_functionLabels[f], false), new ImmLabelByte(_functionLabels[f], true));
            case CallExpr { AddressOf: { } av }:
            {
                // A field of a struct variable is a view: its storage plus an offset.
                var (v, offset) = av.AliasOf is { } storage ? (storage, av.AliasOffset) : (av, 0);
                return (new ImmVarByte(v, false, offset), new ImmVarByte(v, true, offset));
            }
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
        Op(Lda, y.Hi); Op(Sta, Var(t)); Op(Lda, y.Lo);
        for (int i = 0; i < s.Right.ConstValue; i++)
        {
            if (s.Op == TokenKind.Shl) { Emit(Asl, Accumulator); Op(Rol, Var(t)); }
            else { Op(Lsr, Var(t)); Emit(Ror, Accumulator); }
        }
        var m = ArithMnemonic(b.Op);
        CarryFor(b.Op);
        x(m, 0); Op(Sta, d.Lo);
        Op(Lda, Var(t)); x(m, 1); Op(Sta, d.Hi);
        Release(t);
        return true;
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
                Emit(Lda, mode, Var(arr, shift)); Op(Sta, d.Lo);
                Emit(Lda, mode, Var(arr, HighTable(arr) + shift)); Op(Sta, d.Hi);
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
                if (right is null)
                {
                    temp = AcquireTemp(2);
                    Gen16(b.Right, new Loc(temp));
                    right = (Var(temp), Var(temp, 1));
                }
                Gen16(b.Left, d);

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
        var op = jumpIf ? b.Op : Negate(b.Op);
        Expr left = b.Left, right = b.Right;
        var type = left.Type.Size >= right.Type.Size ? left.Type : right.Type;
        bool signed = type.IsSigned;

        // A one-byte value against a constant keeps the value in A: c < x is x > c, and x > c is
        // x >= c + 1 (cmp #c+1), x <= c is x < c + 1. Not when c + 1 does not fit (x > 255).
        if (type.Size == 1 && left.ConstValue is not null && right.ConstValue is null)
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

    // ---------- Pointers: p[i] with (zp),y ----------

    private static bool IsPointerIndex(IndexExpr ix) => ix.Target.Type.IsPointer;

    /// <summary>
    /// The pointer variable itself, if (zp),y can use it directly. A pointer field (a view) is not
    /// known to be in zero page, so it is copied to a temporary.
    /// </summary>
    private static VariableSymbol? DirectPointer(Expr target) =>
        target is NameExpr { Symbol: { InMemory: true, AliasOf: null } v } && v.FixedAddress is null or <= 0xFE ? v : null;

    /// <summary>
    /// The zero-page pointer for p[i]: the variable, or a temporary holding a computed pointer
    /// (or one at a fixed address outside zero page). Release the temporary after use.
    /// </summary>
    private (VarOperand Base, VariableSymbol? Temp) PointerBase(Expr target)
    {
        if (DirectPointer(target) is { } v) return (Var(v), null);
        var temp = AcquireTemp(2, zeroPage: true);
        Gen16(target, new Loc(temp));
        return (Var(temp), temp);
    }

    /// <summary>Bytes per index step of p[i]: the element size, or 1 for an array field of a struct.</summary>
    private static int Stride(IndexExpr ix) => ix.Stride != 0 ? ix.Stride : ix.Type.Size;

    /// <summary>Y is just the index: a one-byte element, no field offset.</summary>
    private static bool PlainPointerIndex(IndexExpr ix) => ix.Type.Size == 1 && Stride(ix) == 1 && ix.Offset == 0;

    /// <summary>Loading Y for this element does not change A.</summary>
    private bool PointerIndexIsCheap(IndexExpr ix) =>
        ix.Index.ConstValue is not null
        || (PlainPointerIndex(ix) && (ix.Index is NameExpr { Symbol.Register: Register.Y } || Simple1(ix.Index) is not null));

    /// <summary>
    /// Puts the offset of byte <paramref name="byteIndex"/> of element p[i] in Y:
    /// field offset + i * stride, plus the distance to the second byte.
    /// </summary>
    private void LoadPointerIndex(IndexExpr ix, int byteIndex)
    {
        int stride = Stride(ix);
        int offset = ix.Offset;
        var index = ix.Index;
        // The second byte is one further on (iny), or in the high-byte half of a split array field.
        bool iny = byteIndex > 0 && ix.SecondByte == 1;
        if (byteIndex > 0 && !iny) offset += ix.SecondByte;
        if (PlainPointerIndex(ix) && index is NameExpr { Symbol.Register: Register.Y }) return;
        if (_reserved.Contains(Register.Y))
            throw new CompileError(ix.Pos, "p[i] needs the Y register, which holds a loop variable. Use 'using X' for that loop");
        if (index.ConstValue is long c)
        {
            Op(Ldy, Byte(offset + c * stride + (iny ? 1 : 0)));
            return;
        }
        if (stride == 1 && offset == 0 && Simple1(index) is { } op)
            Op(Ldy, op);
        else
        {
            GenA(index);
            if (stride == 2) Emit(Asl, Accumulator);
            if (offset != 0) { Emit(Clc); Op(Adc, Byte(offset)); }
            Emit(Tay);
        }
        if (iny) Emit(Iny);
    }

    /// <summary>After byte 0 of a 16-bit p[i], puts the offset of byte 1 in Y. A may change.</summary>
    private void NextPointerByte(IndexExpr ix)
    {
        if (ix.SecondByte == 1) Emit(Iny);
        else if (ix.Index.ConstValue is not null) LoadPointerIndex(ix, 1);
        else { Emit(Tya); Emit(Clc); Op(Adc, Byte(ix.SecondByte)); Emit(Tay); }
    }

    /// <summary>Reads (lda) or writes (sta) one byte of p[i]. For a write, the value is in A.</summary>
    private void PointerByte(IndexExpr ix, int byteIndex, Mnemonic m)
    {
        VariableSymbol? saved = null;
        if (m == Sta && (DirectPointer(ix.Target) is null || !PointerIndexIsCheap(ix)))
        {
            saved = AcquireTemp(1);
            Op(Sta, Var(saved));
        }
        var (p, temp) = PointerBase(ix.Target);
        LoadPointerIndex(ix, byteIndex);
        if (saved is not null)
        {
            Op(Lda, Var(saved));
            Release(saved);
        }
        Emit(m, IndirectIndexed, p);
        if (temp is not null) Release(temp);
    }

    /// <summary>
    /// <c>p[i] = v</c> in the spec 11 shape: Y first, then the value, so A never needs saving.
    /// Returns false if computing the value would change Y.
    /// </summary>
    private bool StorePointerByteIndexFirst(IndexExpr ix, Expr value)
    {
        if (ix.Type.Size != 1) return false;
        // dst[i] = src[i]: one Y load for both.
        var source = value is IndexExpr vix && IsPointerIndex(vix) && vix.Type.Size == 1 && SameIndex(vix.Index, ix.Index)
            && vix.Offset == ix.Offset && Stride(vix) == Stride(ix)
            ? DirectPointer(vix.Target) : null;
        if (source is null && UsesY(value)) return false;

        var (p, temp) = PointerBase(ix.Target);
        LoadPointerIndex(ix, 0);
        if (source is not null) Emit(Lda, IndirectIndexed, Var(source));
        else GenA(value);
        Emit(Sta, IndirectIndexed, p);
        if (temp is not null) Release(temp);
        return true;
    }

    private static bool SameIndex(Expr a, Expr b) =>
        (a.ConstValue is long ca && b.ConstValue == ca)
        || (a is NameExpr { Symbol: { } va } && b is NameExpr { Symbol: { } vb } && va == vb && !va.IsVolatile);

    /// <summary>True if computing <paramref name="e"/> may change Y (conservative).</summary>
    private bool UsesY(Expr e)
    {
        if (e.ConstValue is not null) return false;
        return e switch
        {
            NameExpr => false,
            IndexExpr ix when IsPointerIndex(ix) => true,
            // Arrays are indexed with X unless a loop holds X.
            IndexExpr ix => ix.Index.ConstValue is null && (_reserved.Contains(Register.X) || UsesY(ix.Index)),
            CallExpr call => call.AddressOf is null && call.FunctionAddress is null,
            UnaryExpr u => UsesY(u.Operand),
            BinaryExpr b => UsesY(b.Left) || UsesY(b.Right),
            MemberExpr m => UsesY(m.Target),
            CastExpr c => UsesY(c.Operand),
            _ => true,
        };
    }

    /// <summary>Reads a two-byte element p[i] into <paramref name="d"/>.</summary>
    private void LoadPointerWord(IndexExpr ix, Loc d)
    {
        if (ReadsVar(ix.Target, d.Var))
        {
            // Writing the low byte would change the pointer before the high byte is read.
            var copy = AcquireTemp(2);
            LoadPointerWord(ix, new Loc(copy));
            Op(Lda, Var(copy)); Op(Sta, d.Lo);
            Op(Lda, Var(copy, 1)); Op(Sta, d.Hi);
            Release(copy);
            return;
        }
        var (p, temp) = PointerBase(ix.Target);
        LoadPointerIndex(ix, 0);
        Emit(Lda, IndirectIndexed, p); Op(Sta, d.Lo);
        NextPointerByte(ix);
        Emit(Lda, IndirectIndexed, p); Op(Sta, d.Hi);
        if (temp is not null) Release(temp);
    }

    /// <summary>Writes a two-byte value to element p[i].</summary>
    private void StorePointerWord(IndexExpr ix, Expr value)
    {
        VariableSymbol? valueTemp = null;
        var pair = Simple2(value);
        if (pair is null)
        {
            valueTemp = AcquireTemp(2);
            Gen16(value, new Loc(valueTemp));
            pair = (Var(valueTemp), Var(valueTemp, 1));
        }
        var (p, temp) = PointerBase(ix.Target);
        LoadPointerIndex(ix, 0);
        Op(Lda, pair.Value.Lo); Emit(Sta, IndirectIndexed, p);
        NextPointerByte(ix);
        Op(Lda, pair.Value.Hi); Emit(Sta, IndirectIndexed, p);
        if (temp is not null) Release(temp);
        if (valueTemp is not null) Release(valueTemp);
    }

    // ---------- Calls ----------

    private void GenCall(CallExpr call)
    {
        var fn = call.Function!;
        int start = _out.Count;
        var temps = new List<VariableSymbol>();
        var early = new Dictionary<int, VariableSymbol>();

        // Calls inside arguments run before any parameter is written: the callee of the inner
        // call may share memory with this call's parameters.
        for (int i = 0; i < call.Args.Count; i++)
        {
            if (!ContainsCall(call.Args[i])) continue;
            var t = AcquireTemp(fn.Parameters[i].Type.Size);
            temps.Add(t);
            if (t.Type.Size == 1) { GenA(call.Args[i]); Op(Sta, Var(t)); }
            else Gen16(call.Args[i], new Loc(t));
            early[i] = t;
        }

        // Memory parameters: store straight into the callee's frame.
        for (int i = 0; i < call.Args.Count; i++)
        {
            var p = fn.Parameters[i];
            if (p.PassedIn is not null) continue;
            if (early.TryGetValue(i, out var t))
            {
                Op(Lda, Var(t)); Op(Sta, Var(p));
                if (p.Type.Size == 2) { Op(Lda, Var(t, 1)); Op(Sta, Var(p, 1)); }
            }
            else if (p.Type.Size == 1)
            {
                GenA(call.Args[i]);
                Op(Sta, Var(p));
            }
            else
                Gen16(call.Args[i], new Loc(p));
        }

        // Register parameters: compute what needs code first, then load the registers last.
        // A one-byte value for A is computed straight into A after the temporaries, since the
        // ldx/ldy that follow do not change A.
        var loads = new List<(Register Reg, Operand Op)>();
        Expr? intoA = null;
        for (int i = 0; i < call.Args.Count; i++)
        {
            var p = fn.Parameters[i];
            if (p.PassedIn is not { } reg) continue;
            var arg = call.Args[i];
            if (p.Type.Size == 1)
            {
                Operand op;
                if (early.TryGetValue(i, out var t)) op = Var(t);
                else if (Simple1(arg) is { } s) op = s;
                else if (reg == Register.A)
                {
                    intoA = arg;
                    continue;
                }
                else
                {
                    var nt = AcquireTemp(1);
                    temps.Add(nt);
                    GenA(arg);
                    Op(Sta, Var(nt));
                    op = Var(nt);
                }
                loads.Add((reg, op));
            }
            else
            {
                (Operand Lo, Operand Hi) pair;
                if (early.TryGetValue(i, out var t)) pair = (Var(t), Var(t, 1));
                else if (Simple2(arg) is { } s) pair = s;
                else
                {
                    var nt = AcquireTemp(2);
                    temps.Add(nt);
                    Gen16(arg, new Loc(nt));
                    pair = (Var(nt), Var(nt, 1));
                }
                var (loReg, hiReg) = reg switch
                {
                    Register.AX => (Register.A, Register.X),
                    Register.AY => (Register.A, Register.Y),
                    _ => (Register.X, Register.Y),
                };
                loads.Add((loReg, pair.Lo));
                loads.Add((hiReg, pair.Hi));
            }
        }
        foreach (var (reg, value) in fn.FixedInputs)
            if (reg != Register.Carry) loads.Add((reg, Byte(value)));
        if (intoA is not null) GenA(intoA);
        foreach (var (reg, op) in loads.OrderBy(l => l.Reg == Register.A ? 1 : 0))
            Op(reg switch { Register.A => Lda, Register.X => Ldx, _ => Ldy }, op);
        // A fixed carry goes right before the jsr, after everything that could change it.
        foreach (var (reg, value) in fn.FixedInputs)
            if (reg == Register.Carry) Emit(value != 0 ? Sec : Clc);

        if (fn.IsInline)
        {
            GenInline(fn);
            MoveInlineFrame(fn, start);
        }
        else Emit(Jsr, Absolute, fn.FixedAddress is int at ? new AddrOperand(at) : new LabelOperand(_functionLabels[fn]));
        foreach (var t in temps) Release(t);
    }

    // ---------- Assembly ----------

    /// <summary>Emits an asm block as written. The checker has made sure it leaves loop registers alone.</summary>
    private void GenAsm(AsmStmt block)
    {
        var fx = block.Effects!;
        string Names(IEnumerable<VariableSymbol> vs) => string.Join(" ", vs.Select(v => v.QualifiedName).Order(StringComparer.Ordinal));
        var summary = $"asm: reads {AsmEffects.Describe(fx.Reads)}, changes {AsmEffects.Describe(fx.Writes)}";
        if (fx.VariablesRead.Count > 0) summary += $", reads {Names(fx.VariablesRead)}";
        if (fx.VariablesWritten.Count > 0) summary += $", writes {Names(fx.VariablesWritten)}";
        if (fx.Calls) summary += ", calls subroutines";
        _out.Add(new Comment(summary));

        var labels = block.Lines.OfType<AsmLabel>().ToDictionary(l => l, l => NewLabel(l.Name[1..]));
        foreach (var line in block.Lines)
        {
            Source(line);
            switch (line)
            {
                case AsmLabel l:
                    Place(labels[l]);
                    break;
                case AsmInstruction ins:
                    _out.Add(new Instr(ins.Mnemonic, ins.Mode, AsmOperandOf(ins, labels), ins.Pos));
                    break;
            }
        }
    }

    private Operand? AsmOperandOf(AsmInstruction ins, Dictionary<AsmLabel, Label> labels)
    {
        if (ins.Value is not { } v) return null;
        int offset = (int)v.Number;
        Label LabelOf() => v.Function is { } f ? _functionLabels[f] : labels[v.Label!];

        if (v.High is bool high)
            return v.Variable is { } hv ? new ImmVarByte(hv, high, offset) : new ImmLabelByte(LabelOf(), high, offset);
        if (v.IsNumber) return ins.Mode == Immediate ? new Imm(offset) : new AddrOperand(offset);
        if (v.Variable is { } variable) return new VarOperand(variable, offset);
        return new LabelOperand(LabelOf(), offset);
    }

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
        if (type.IsSigned)
            throw new CompileError(f.Pos, "Signed loop variables are not supported yet");

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

        SetCounter(c, f.From);
        if (c.Register is { } held) _reserved.Add(held);
        var inRegister = HoldInRegister(f.Body, null, c);

        // Skip the loop if the range is empty, unless the bounds prove it is not.
        long max = type.MaxValue;
        bool provablyNonEmpty = (from is not null && to is not null)
            || (inclusive && up && to == max) || (inclusive && !up && to == 0) || _nonEmpty.Contains(f);
        if (!provablyNonEmpty)
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

        if (type.Size == 1 && d == 1 && !f.VariableStep)
            StepByOne(c, up, inclusive, from, to, bound, top, end);
        else
            StepGeneral(c, up, stepOperand ?? Byte(d), inclusive, to, bound, top, end);

        Place(end);
        ReleaseHeld(inRegister, f.Body);
        if (c.Register is { } r) _reserved.Remove(r);
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
        // Per candidate: uses where the register saves a load or an inc (an index, v += 1), and all uses.
        var uses = new Dictionary<VariableSymbol, (int Strong, int All)>();
        var bad = new HashSet<VariableSymbol>();
        foreach (var n in nodes.OfType<NameExpr>())
        {
            if (n.Symbol is not { } v || !Ordinary(v) || bad.Contains(v)) continue;
            var parent = parents.GetValueOrDefault(n);
            if (!Served(n, parent)) { bad.Add(v); uses.Remove(v); continue; }
            bool strong = parent is IndexExpr || (parent is AssignStmt { Op: TokenKind.PlusEq or TokenKind.MinusEq, Value.ConstValue: 1 } a && a.Target == n);
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
