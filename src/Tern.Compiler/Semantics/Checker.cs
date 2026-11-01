using Tern.Compiler.Syntax;

namespace Tern.Compiler.Semantics;

public sealed class CheckedProgram(
    ProgramSyntax syntax, List<VariableSymbol> globals, List<FunctionSymbol> functions, FunctionSymbol main)
{
    public ProgramSyntax Syntax { get; } = syntax;
    public List<VariableSymbol> Globals { get; } = globals;
    /// <summary>Functions reachable from <c>main</c>, callers before callees.</summary>
    public List<FunctionSymbol> Functions { get; } = functions;
    public FunctionSymbol Main { get; } = main;
}

/// <summary>
/// Resolves names, assigns types, folds constants and builds the call graph.
/// Reports the first error as a <see cref="CompileError"/>.
/// </summary>
public sealed class Checker
{
    private readonly Dictionary<string, Symbol> _globalScope = new();
    private readonly List<Dictionary<string, Symbol>> _scopes = [];
    private readonly List<VariableSymbol> _globals = [];
    private FunctionSymbol? _function;
    private int _loopDepth;
    private readonly HashSet<Register> _loopRegisters = [];
    private int _hiddenCounter;
    private readonly Platform _platform;
    private readonly string _baseDirectory;

    private Checker(Platform platform, string baseDirectory)
    {
        _platform = platform;
        _baseDirectory = baseDirectory;
    }

    /// <param name="baseDirectory">Where <c>include_bytes</c> looks for files: the source file's directory.</param>
    public static CheckedProgram Check(ProgramSyntax program, Platform platform, string baseDirectory) =>
        new Checker(platform, baseDirectory).Run(program);

    private CheckedProgram Run(ProgramSyntax program)
    {
        foreach (var c in program.Consts) DeclareConst(c);
        foreach (var f in program.Functions) DeclareFunction(f);
        foreach (var g in program.Globals) DeclareGlobal(g);

        if (!_globalScope.TryGetValue("main", out var m) || m is not FunctionSymbol main)
            throw new CompileError(new SourcePos(1, 1), "The program has no 'fn main()'");
        if (main.Parameters.Count > 0 || main.ReturnType.Kind != TypeKind.Void)
            throw new CompileError(main.Pos, "'main' must have no parameters and no return value");

        foreach (var f in program.Functions) CheckFunctionBody(f);

        var reachable = OrderReachable(main);
        return new CheckedProgram(program, _globals, reachable, main);
    }

    // ---------- Declarations ----------

    private void Declare(Symbol symbol)
    {
        var scope = _scopes.Count > 0 ? _scopes[^1] : _globalScope;
        if (Lookup(symbol.Name) is { } existing)
            throw new CompileError(symbol.Pos, $"'{symbol.Name}' is already declared at {existing.Pos}");
        scope[symbol.Name] = symbol;
    }

    private Symbol? Lookup(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out var s)) return s;
        return _globalScope.GetValueOrDefault(name);
    }

    private void DeclareConst(ConstDecl decl)
    {
        CheckExpr(decl.Value);
        if (decl.Value.ConstValue is not long value)
            throw new CompileError(decl.Value.Pos, $"The value of '{decl.Name}' is not known at compile time");
        var type = decl.Value.Type;
        if (decl.TypeSyntax is not null)
        {
            type = ResolveType(decl.TypeSyntax);
            if (!type.IsScalar) throw new CompileError(decl.Pos, "Constants must be numbers or bool");
            Coerce(decl.Value, type);
        }
        Declare(new ConstSymbol(decl.Name, decl.Pos, type, decl.Value.ConstValue!.Value));
    }

    private void DeclareFunction(FnDecl decl)
    {
        var fn = new FunctionSymbol(decl.Name, decl.Pos) { Declaration = decl };
        decl.Symbol = fn;

        var usedRegisters = new HashSet<char>();
        foreach (var p in decl.Parameters)
        {
            var type = ResolveType(p.Type);
            if (!type.IsScalar) throw new CompileError(p.Pos, "Parameters must be numbers or bool");
            var param = new VariableSymbol(p.Name, p.Pos, type, VariableKind.Parameter, fn);
            if (fn.Parameters.Any(x => x.Name == p.Name))
                throw new CompileError(p.Pos, $"Parameter '{p.Name}' is declared twice");
            if (p.UsingRegister is { } reg)
            {
                CheckRegisterFits(reg, type, p.Pos, allowFlags: false);
                foreach (char r in reg.ToString().Where(char.IsUpper))
                    if (!usedRegisters.Add(r))
                        throw new CompileError(p.Pos, $"Register {r} is used by more than one parameter");
                param.PassedIn = reg;
            }
            fn.Parameters.Add(param);
            fn.FrameVariables.Add(param);
        }

        if (decl.ReturnType is not null)
        {
            fn.ReturnType = ResolveType(decl.ReturnType);
            if (!fn.ReturnType.IsScalar) throw new CompileError(decl.ReturnType.Pos, "Functions can only return numbers or bool");
            fn.ReturnRegister = decl.ReturnUsing ?? fn.ReturnType.Kind switch
            {
                TypeKind.Bool => Register.Carry,
                TypeKind.U16 or TypeKind.I16 or TypeKind.Ptr => Register.AX,
                _ => Register.A,
            };
            CheckRegisterFits(fn.ReturnRegister.Value, fn.ReturnType, decl.ReturnType.Pos, allowFlags: true);
        }
        else if (decl.ReturnUsing is not null)
            throw new CompileError(decl.Pos, "'using' needs a return type");

        Declare(fn);
    }

    private static void CheckRegisterFits(Register reg, TernType type, SourcePos pos, bool allowFlags)
    {
        bool ok = reg switch
        {
            Register.A or Register.X or Register.Y => type.Size == 1,
            Register.AX or Register.AY or Register.XY => type.Size == 2,
            _ => allowFlags && type.Kind == TypeKind.Bool,
        };
        if (!ok) throw new CompileError(pos, $"A {type} value cannot be passed using {reg}");
    }

    private void DeclareGlobal(VarDecl decl)
    {
        // A string or include_bytes() gives a [u8; N] array, which may be longer than 256 bytes.
        var bytes = decl.Init is null ? null : ByteSource(decl.Init);
        var type = bytes is not null && decl.TypeSyntax is null ? TernType.ArrayOf(TernType.U8, bytes.Length) : DeclaredType(decl);
        var v = new VariableSymbol(decl.Name, decl.Pos, type, VariableKind.Global, null) { IsData = decl.IsData };
        decl.Symbol = v;
        ApplyPlacement(decl, v, allowAddress: true);

        if (decl.IsData && !type.IsArray)
            throw new CompileError(decl.Pos, "Data must be an array or a string. For a single value, use 'const'");
        if (decl.Init is not null)
        {
            if (v.FixedAddress is not null)
                throw new CompileError(decl.Init.Pos, "A variable at a fixed address cannot have an initial value");
            if (bytes is not null)
            {
                if (!type.IsArray || type.Element!.Kind != TypeKind.U8)
                    throw new CompileError(decl.Init.Pos, $"This value is a [u8; {bytes.Length}] array, but '{decl.Name}' is {type}");
                if (type.Length != bytes.Length)
                    throw new CompileError(decl.Init.Pos, $"Expected {type.Length} bytes, found {bytes.Length}");
                v.InitialBytes = bytes;
            }
            else
                v.InitialBytes = ConstantBytes(decl.Init, type);
        }
        Declare(v);
        _globals.Add(v);
    }

    /// <summary>The bytes of a string literal or <c>include_bytes()</c>, or null for other initializers.</summary>
    private byte[]? ByteSource(Expr init)
    {
        switch (init)
        {
            case StringExpr s:
                if (s.Text.Length == 0) throw new CompileError(s.Pos, "A string must have at least one character");
                return s.Text.Select(c => (byte)Encode(c, s.Screen, s.Pos)).ToArray();
            case CallExpr { Name: "include_bytes" } call:
                return IncludeBytes(call);
            default:
                return null;
        }
    }

    private int Encode(char c, bool screen, SourcePos pos)
    {
        if (Lexer.IsRawByte(c)) return c - Lexer.RawByteBase;
        int? code = screen ? _platform.ScreenEncoding(c) : _platform.TextEncoding(c);
        if (code is int value) return value;
        string shown = c == '\n' ? "\\n" : c.ToString();
        throw new CompileError(pos, screen
            ? $"'{shown}' has no screen code. Write the byte as \\$NN"
            : $"'{shown}' has no {_platform.TextEncodingName} code. Write the byte as \\$NN");
    }

    private byte[] IncludeBytes(CallExpr call)
    {
        if (call.Args is not [StringExpr { Screen: false } file])
            throw new CompileError(call.Pos, "include_bytes() takes a file name in quotes");
        var path = Path.Combine(_baseDirectory, file.Text);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new CompileError(file.Pos, $"Cannot read '{file.Text}' ({path}): {e.Message}");
        }
        if (bytes.Length == 0) throw new CompileError(file.Pos, $"'{file.Text}' is empty");
        return bytes;
    }

    /// <summary>The variable's type: written out, or taken from the initializer.</summary>
    private TernType DeclaredType(VarDecl decl)
    {
        if (decl.TypeSyntax is not null) return ResolveType(decl.TypeSyntax);
        if (decl.Init is null) throw new CompileError(decl.Pos, $"'{decl.Name}' needs a type or an initial value");
        if (decl.Init is ArrayLiteralExpr) throw new CompileError(decl.Pos, "Array variables need a written type, e.g. [u8; 4]");
        CheckExpr(decl.Init);
        return DefaultType(decl.Init);
    }

    private void ApplyPlacement(VarDecl decl, VariableSymbol v, bool allowAddress)
    {
        switch (decl.Placement.Kind)
        {
            case PlacementKind.ZeroPage:
                v.RequireZeroPage = true;
                break;
            case PlacementKind.Address:
                if (!allowAddress) throw new CompileError(decl.Pos, "Only global variables can have a fixed address");
                var addr = decl.Placement.Address!;
                CheckExpr(addr);
                if (addr.ConstValue is not long a)
                    throw new CompileError(addr.Pos, "The address must be known at compile time");
                if (a < 0 || a + v.Type.Size > 0x10000)
                    throw new CompileError(addr.Pos, $"Address ${a:X} is outside memory");
                v.FixedAddress = (int)a;
                break;
        }
    }

    private byte[] ConstantBytes(Expr init, TernType type)
    {
        if (type.IsArray)
        {
            if (init is not ArrayLiteralExpr list)
                throw new CompileError(init.Pos, "An array must be initialized with [ ... ]");
            if (list.Elements.Count != type.Length)
                throw new CompileError(init.Pos, $"Expected {type.Length} values, found {list.Elements.Count}");
            var bytes = new List<byte>();
            foreach (var e in list.Elements) bytes.AddRange(ConstantBytes(e, type.Element!));
            return bytes.ToArray();
        }

        CheckExpr(init);
        Coerce(init, type);
        if (init.ConstValue is not long value)
            throw new CompileError(init.Pos, "A global's initial value must be known at compile time");
        return type.Size == 1 ? [(byte)value] : [(byte)value, (byte)(value >> 8)];
    }

    private TernType ResolveType(TypeSyntax syntax)
    {
        switch (syntax)
        {
            case NamedTypeSyntax n:
                return n.Name switch
                {
                    "u8" => TernType.U8, "i8" => TernType.I8, "u16" => TernType.U16, "i16" => TernType.I16,
                    "bool" => TernType.Bool,
                    "ptr" => throw new CompileError(n.Pos, "'ptr' needs the type it points to, e.g. 'ptr u8'"),
                    _ => throw new CompileError(n.Pos, $"Unknown type '{n.Name}'"),
                };
            case PointerTypeSyntax p:
                var target = ResolveType(p.Target);
                if (!target.IsScalar) throw new CompileError(p.Pos, "A pointer must point to a number, bool or pointer");
                return TernType.PointerTo(target);
            case ArrayTypeSyntax a:
                var element = ResolveType(a.Element);
                if (!element.IsScalar) throw new CompileError(a.Pos, "Array elements must be numbers or bool");
                CheckExpr(a.Length);
                if (a.Length.ConstValue is not long len)
                    throw new CompileError(a.Length.Pos, "Array length must be known at compile time");
                if (len < 1 || len > 256)
                    throw new CompileError(a.Length.Pos, $"Array length must be 1 to 256, not {len}");
                return TernType.ArrayOf(element, (int)len);
            default:
                throw new InvalidOperationException();
        }
    }

    // ---------- Function bodies ----------

    private void CheckFunctionBody(FnDecl decl)
    {
        _function = decl.Symbol!;
        _scopes.Add(new Dictionary<string, Symbol>());
        foreach (var p in _function.Parameters) Declare(p);

        CheckBlock(decl.Body, newScope: false);

        if (_function.ReturnType.Kind != TypeKind.Void && !AlwaysReturns(decl.Body))
            throw new CompileError(decl.Pos, $"'{decl.Name}' can reach its end without returning a value");

        _scopes.Clear();
        _function = null;
    }

    private void CheckBlock(BlockStmt block, bool newScope = true)
    {
        if (newScope) _scopes.Add(new Dictionary<string, Symbol>());
        foreach (var s in block.Statements) CheckStmt(s);
        if (newScope) _scopes.RemoveAt(_scopes.Count - 1);
    }

    private void CheckStmt(Stmt stmt)
    {
        switch (stmt)
        {
            case VarDecl v: CheckLocal(v); break;
            case AssignStmt a: CheckAssign(a); break;
            case ExprStmt e:
                if (e.Expr is not CallExpr)
                    throw new CompileError(e.Pos, "Only a function call can be used as a statement");
                CheckExpr(e.Expr);
                break;
            case BlockStmt b: CheckBlock(b); break;
            case IfStmt i:
                CheckCondition(i.Condition);
                CheckBlock(i.Then);
                if (i.Else is not null) CheckStmt(i.Else);
                break;
            case WhileStmt w:
                CheckCondition(w.Condition);
                _loopDepth++;
                CheckBlock(w.Body);
                _loopDepth--;
                break;
            case ForStmt f: CheckFor(f); break;
            case BreakStmt or ContinueStmt:
                if (_loopDepth == 0) throw new CompileError(stmt.Pos, "'break' and 'continue' must be inside a loop");
                break;
            case ReturnStmt r:
                var ret = _function!.ReturnType;
                if (ret.Kind == TypeKind.Void)
                {
                    if (r.Value is not null) throw new CompileError(r.Pos, $"'{_function.Name}' does not return a value");
                }
                else
                {
                    if (r.Value is null) throw new CompileError(r.Pos, $"'{_function.Name}' must return a {ret} value");
                    CheckExpr(r.Value);
                    Coerce(r.Value, ret);
                }
                break;
            default:
                throw new InvalidOperationException(stmt.GetType().Name);
        }
    }

    private void CheckLocal(VarDecl decl)
    {
        var type = DeclaredType(decl);
        var v = new VariableSymbol(decl.Name, decl.Pos, type, VariableKind.Local, _function);
        decl.Symbol = v;
        ApplyPlacement(decl, v, allowAddress: false);

        if (decl.Init is null)
        {
            if (!type.IsArray)
                throw new CompileError(decl.Pos, $"Local variable '{decl.Name}' needs an initial value");
        }
        else
        {
            if (type.IsArray) throw new CompileError(decl.Init.Pos, "Local arrays cannot have initial values yet");
            CheckExpr(decl.Init);
            Coerce(decl.Init, type);
            v.Weight += Weight();
        }
        Declare(v);
        _function!.FrameVariables.Add(v);
    }

    private void CheckAssign(AssignStmt a)
    {
        CheckExpr(a.Target);
        var target = a.Target;
        if (!IsAssignable(target))
            throw new CompileError(target.Pos, "The left side of an assignment must be a variable, an array element, or .lo/.hi of one");
        if (!target.Type.IsScalar)
            throw new CompileError(target.Pos, "Whole arrays cannot be assigned");

        CheckExpr(a.Value);
        if (a.Op == TokenKind.Eq)
        {
            Coerce(a.Value, target.Type);
            return;
        }
        if (target.Type.IsPointer)
        {
            if (a.Op is not (TokenKind.PlusEq or TokenKind.MinusEq))
                throw new CompileError(a.Pos, $"'{OpText(a.Op)}' does not work on pointers; only '+=' and '-=' do");
            CheckPointerOffset(a.Value);
            return;
        }
        if (!target.Type.IsInteger)
            throw new CompileError(a.Pos, $"'{OpText(a.Op)}' needs a number on the left");
        if (a.Op is TokenKind.ShlEq or TokenKind.ShrEq)
            CheckShiftCount(a.Value, target.Type);
        else
            Coerce(a.Value, target.Type);
    }

    private bool IsAssignable(Expr e)
    {
        switch (e)
        {
            case NameExpr { Symbol: VariableSymbol v }:
                if (v.IsData) throw new CompileError(e.Pos, $"'{v.Name}' is data, which is read-only");
                if (v.ReadOnly) throw new CompileError(e.Pos, $"Loop variable '{v.Name}' cannot be changed inside the loop");
                return true;
            case IndexExpr ix:
                if (ix.Target is NameExpr { Symbol: { IsData: true } d })
                    throw new CompileError(e.Pos, $"'{d.Name}' is data, which is read-only");
                return true;
            case MemberExpr m:
                return m.Target is NameExpr or IndexExpr && IsAssignable(m.Target);
            default:
                return false;
        }
    }

    private void CheckFor(ForStmt f)
    {
        CheckExpr(f.From);
        CheckExpr(f.To);
        foreach (var bound in new[] { f.From, f.To })
            if (!(bound.Type.IsInteger || bound.Type.Kind == TypeKind.IntLiteral))
                throw new CompileError(bound.Pos, "Loop bounds must be numbers");

        // The loop variable's type: the wider of the bounds, or u8/u16 for literals.
        TernType type;
        if (f.From.Type.Kind == TypeKind.IntLiteral && f.To.Type.Kind == TypeKind.IntLiteral)
            type = DefaultType(f.From).Size >= DefaultType(f.To).Size ? DefaultType(f.From) : DefaultType(f.To);
        else
            type = Unify(f.From, f.To, f.Pos);
        Coerce(f.From, type);
        Coerce(f.To, type);

        long? from = f.From.ConstValue, to = f.To.ConstValue;
        if (f.Step is not null)
        {
            CheckExpr(f.Step);
            if (f.Step.ConstValue is not long step || step == 0)
                throw new CompileError(f.Step.Pos, "'step' must be a non-zero constant");
            if (Math.Abs(step) > 127)
                throw new CompileError(f.Step.Pos, "'step' must be between -127 and 127");
            if (from is not null && to is not null && from != to && (to > from) != (step > 0))
                throw new CompileError(f.Step.Pos, $"A step of {step} goes the wrong way from {from} to {to}");
            f.StepValue = (int)step;
        }
        else if (from is not null && to is not null)
            f.StepValue = to >= from ? 1 : -1;
        else
            f.StepValue = to == 0 ? -1 : 1;

        if (f.UsingRegister is { } reg)
        {
            if (reg is not (Register.X or Register.Y))
                throw new CompileError(f.Pos, "A loop variable can only use X or Y");
            if (type.Size != 1)
                throw new CompileError(f.Pos, $"A {type} loop variable does not fit in {reg}");
            if (!_loopRegisters.Add(reg))
                throw new CompileError(f.Pos, $"{reg} is already used by an enclosing loop");
            if (ContainsCall(f.Body))
                throw new CompileError(f.Pos, $"The loop body calls a function, which can change {reg}. Remove 'using {reg}'");
        }

        var counter = new VariableSymbol(f.Variable ?? $"_loop{_hiddenCounter++}", f.Pos, type, VariableKind.Local, _function)
        {
            ReadOnly = true,
            Register = f.UsingRegister,
        };
        f.Symbol = counter;
        if (counter.InMemory) _function!.FrameVariables.Add(counter);

        _scopes.Add(new Dictionary<string, Symbol>());
        if (f.Variable is not null) Declare(counter);
        _loopDepth++;
        counter.Weight += Weight() * 3;   // load, step and compare on every pass
        CheckBlock(f.Body, newScope: false);
        _loopDepth--;
        _scopes.RemoveAt(_scopes.Count - 1);

        if (f.UsingRegister is { } r) _loopRegisters.Remove(r);
    }

    private static bool ContainsCall(Node node) => node switch
    {
        CallExpr c => c.Name is not ("addr" or "len" or "sizeof") || c.Args.Any(ContainsCall),
        BlockStmt b => b.Statements.Any(ContainsCall),
        IfStmt i => ContainsCall(i.Condition) || ContainsCall(i.Then) || (i.Else is not null && ContainsCall(i.Else)),
        WhileStmt w => ContainsCall(w.Condition) || ContainsCall(w.Body),
        ForStmt f => ContainsCall(f.From) || ContainsCall(f.To) || ContainsCall(f.Body),
        ReturnStmt r => r.Value is not null && ContainsCall(r.Value),
        AssignStmt a => ContainsCall(a.Target) || ContainsCall(a.Value),
        ExprStmt e => ContainsCall(e.Expr),
        VarDecl v => v.Init is not null && ContainsCall(v.Init),
        UnaryExpr u => ContainsCall(u.Operand),
        BinaryExpr b => ContainsCall(b.Left) || ContainsCall(b.Right),
        IndexExpr ix => ContainsCall(ix.Target) || ContainsCall(ix.Index),
        MemberExpr m => ContainsCall(m.Target),
        CastExpr c => ContainsCall(c.Operand),
        _ => false,
    };

    private static bool AlwaysReturns(Stmt stmt) => stmt switch
    {
        ReturnStmt => true,
        BlockStmt b => b.Statements.Count > 0 && AlwaysReturns(b.Statements[^1]),
        IfStmt i => i.Else is not null && AlwaysReturns(i.Then) && AlwaysReturns(i.Else),
        WhileStmt w => w.Condition.ConstValue == 1 && !ContainsBreak(w.Body),
        _ => false,
    };

    /// <summary>True if a break leaves this loop body (breaks inside nested loops do not count).</summary>
    private static bool ContainsBreak(Stmt stmt) => stmt switch
    {
        BreakStmt => true,
        BlockStmt b => b.Statements.Any(ContainsBreak),
        IfStmt i => ContainsBreak(i.Then) || (i.Else is not null && ContainsBreak(i.Else)),
        _ => false,
    };

    private void CheckCondition(Expr cond)
    {
        CheckExpr(cond);
        if (cond.Type.Kind != TypeKind.Bool)
            throw new CompileError(cond.Pos, $"A condition must be bool, not {cond.Type}. Compare it, e.g. 'x != 0'");
    }

    // ---------- Expressions ----------

    private long Weight() => (long)Math.Pow(8, Math.Min(_loopDepth, 5));

    private void CheckExpr(Expr e)
    {
        switch (e)
        {
            case NumberExpr n:
                e.Type = TernType.IntLiteral;
                e.ConstValue = n.Value;
                break;

            case BoolExpr b:
                e.Type = TernType.Bool;
                e.ConstValue = b.Value ? 1 : 0;
                break;

            case CharExpr ch:
                e.Type = TernType.IntLiteral;
                e.ConstValue = Encode(ch.Value, screen: false, ch.Pos);
                break;

            case StringExpr:
                throw new CompileError(e.Pos, "A string can only be the value of data or a global array");

            case NameExpr n:
                switch (Lookup(n.Name))
                {
                    case null: throw new CompileError(n.Pos, $"'{n.Name}' is not declared");
                    case ConstSymbol c:
                        e.Type = c.Type;
                        e.ConstValue = c.Value;
                        break;
                    case VariableSymbol v:
                        n.Symbol = v;
                        e.Type = v.Type;
                        v.Weight += Weight();
                        break;
                    case FunctionSymbol:
                        throw new CompileError(n.Pos, $"'{n.Name}' is a function; call it with ()");
                }
                break;

            case UnaryExpr u: CheckUnary(u); break;
            case BinaryExpr b: CheckBinary(b); break;
            case CallExpr c: CheckCall(c); break;

            case IndexExpr ix:
                CheckExpr(ix.Target);
                CheckExpr(ix.Index);
                Coerce(ix.Index, TernType.U8);
                if (ix.Target.Type.IsPointer)
                {
                    var element = ix.Target.Type.Element!;
                    if (element.Size == 2 && ix.Index.ConstValue is > 127)
                        throw new CompileError(ix.Index.Pos,
                            $"Index {ix.Index.ConstValue} is too large: p[i] reaches 256 bytes, which is 128 {element} values");
                    if (ix.Target is NameExpr { Symbol: { } pv }) pv.Dereferenced = true;
                    e.Type = element;
                    break;
                }
                if (ix.Target is not NameExpr { Symbol: VariableSymbol arr } || !arr.Type.IsArray)
                    throw new CompileError(ix.Pos, "Only arrays and pointers can be indexed");
                if (arr.Type.Length > 256)
                    throw new CompileError(ix.Pos,
                        $"'{arr.Name}' has {arr.Type.Length} elements, too many for a u8 index. Read it through a pointer: addr({arr.Name})");
                if (ix.Index.ConstValue is long idx && idx >= arr.Type.Length)
                    throw new CompileError(ix.Index.Pos, $"Index {idx} is past the end of '{arr.Name}' (length {arr.Type.Length})");
                e.Type = arr.Type.Element!;
                break;

            case MemberExpr m:
                CheckExpr(m.Target);
                if (m.Member is not ("lo" or "hi"))
                    throw new CompileError(m.Pos, $"Unknown member '.{m.Member}'");
                if (m.Target.Type.Kind == TypeKind.IntLiteral) Coerce(m.Target, DefaultType(m.Target));
                if (m.Target.Type.IsPointer)
                    throw new CompileError(m.Pos, $".{m.Member} needs a number; convert the pointer first: (p as u16).{m.Member}");
                if (m.Target.Type.Size != 2 || !m.Target.Type.IsInteger)
                    throw new CompileError(m.Pos, $".{m.Member} needs a 16-bit value, not {m.Target.Type}");
                e.Type = TernType.U8;
                if (m.Target.ConstValue is long mv)
                    e.ConstValue = m.Member == "lo" ? mv & 0xFF : (mv >> 8) & 0xFF;
                break;

            case CastExpr c:
                CheckExpr(c.Operand);
                var target = ResolveType(c.Target);
                var source = c.Operand.Type;
                if (!target.IsScalar) throw new CompileError(c.Pos, $"Cannot convert to {target}");
                if (target.Kind == TypeKind.Bool)
                    throw new CompileError(c.Pos, "Numbers cannot be converted to bool; compare instead, e.g. 'x != 0'");
                if (target.IsPointer || source.IsPointer)
                {
                    // Pointers convert to other pointers and to and from u16, with no code.
                    if (source.Kind == TypeKind.IntLiteral) Coerce(c.Operand, TernType.U16);
                    else if (!(source.IsPointer || source.Kind == TypeKind.U16))
                        throw new CompileError(c.Pos, $"Cannot convert {source} to {target}; only u16 and pointers convert to a pointer");
                    if (!(target.IsPointer || target.Kind == TypeKind.U16))
                        throw new CompileError(c.Pos, $"Cannot convert {source} to {target}; a pointer converts only to u16 or another pointer");
                }
                else if (source.Kind == TypeKind.IntLiteral)
                    Coerce(c.Operand, DefaultType(c.Operand));
                else if (!source.IsScalar)
                    throw new CompileError(c.Pos, $"Cannot convert {source} to {target}");
                e.Type = target;
                if (c.Operand.ConstValue is long cv) e.ConstValue = Normalize(cv, target);
                break;

            case ArrayLiteralExpr:
                throw new CompileError(e.Pos, "An array literal can only be used to initialize a global array");

            default:
                throw new InvalidOperationException(e.GetType().Name);
        }
    }

    private void CheckUnary(UnaryExpr u)
    {
        CheckExpr(u.Operand);
        var t = u.Operand.Type;
        long? v = u.Operand.ConstValue;
        switch (u.Op)
        {
            case TokenKind.Bang:
                if (t.Kind != TypeKind.Bool) throw new CompileError(u.Pos, "'!' needs a bool; use '~' to flip bits");
                u.Type = TernType.Bool;
                if (v is not null) u.ConstValue = v == 0 ? 1 : 0;
                return;
            case TokenKind.Minus or TokenKind.Plus:
                if (t.Kind == TypeKind.IntLiteral)
                {
                    u.Type = t;
                    u.ConstValue = u.Op == TokenKind.Minus ? -v : v;
                    return;
                }
                if (!t.IsInteger) throw new CompileError(u.Pos, $"Unary '{(u.Op == TokenKind.Minus ? "-" : "+")}' needs a number");
                u.Type = t;
                if (v is not null) u.ConstValue = Normalize(u.Op == TokenKind.Minus ? -v.Value : v.Value, t);
                return;
            case TokenKind.Tilde:
                if (t.Kind == TypeKind.IntLiteral) { Coerce(u.Operand, DefaultType(u.Operand)); t = u.Operand.Type; }
                if (!t.IsInteger) throw new CompileError(u.Pos, "'~' needs a number; use '!' for bool");
                u.Type = t;
                if (v is not null) u.ConstValue = Normalize(~v.Value, t);
                return;
        }
        throw new InvalidOperationException();
    }

    private void CheckBinary(BinaryExpr b)
    {
        CheckExpr(b.Left);
        CheckExpr(b.Right);
        long? l = b.Left.ConstValue, r = b.Right.ConstValue;

        switch (b.Op)
        {
            case TokenKind.AmpAmp or TokenKind.PipePipe:
                if (b.Left.Type.Kind != TypeKind.Bool || b.Right.Type.Kind != TypeKind.Bool)
                    throw new CompileError(b.Pos, $"'{OpText(b.Op)}' needs bool values; use '{(b.Op == TokenKind.AmpAmp ? "&" : "|")}' for bits");
                b.Type = TernType.Bool;
                if (l is not null && r is not null)
                    b.ConstValue = (b.Op == TokenKind.AmpAmp ? l != 0 && r != 0 : l != 0 || r != 0) ? 1 : 0;
                return;

            case TokenKind.EqEq or TokenKind.NotEq or TokenKind.Less or TokenKind.LessEq or TokenKind.Greater or TokenKind.GreaterEq:
            {
                if (b.Left.Type.IsPointer || b.Right.Type.IsPointer)
                {
                    if (b.Left.Type.Kind == TypeKind.IntLiteral) Coerce(b.Left, b.Right.Type);
                    else if (b.Right.Type.Kind == TypeKind.IntLiteral) Coerce(b.Right, b.Left.Type);
                    else if (!b.Left.Type.SameAs(b.Right.Type))
                        throw new CompileError(b.Pos, $"Cannot compare {b.Left.Type} with {b.Right.Type}; convert one side with 'as'");
                }
                else if (b.Left.Type.Kind == TypeKind.Bool || b.Right.Type.Kind == TypeKind.Bool)
                {
                    if (b.Left.Type.Kind != b.Right.Type.Kind || b.Op is not (TokenKind.EqEq or TokenKind.NotEq))
                        throw new CompileError(b.Pos, "bool values can only be compared with == and !=");
                }
                else
                {
                    var t = Unify(b.Left, b.Right, b.Pos);
                    if (t.Kind == TypeKind.IntLiteral) { Coerce(b.Left, DefaultType(b.Left)); Coerce(b.Right, b.Left.Type); }
                }
                b.Type = TernType.Bool;
                if (l is not null && r is not null)
                    b.ConstValue = b.Op switch
                    {
                        TokenKind.EqEq => l == r, TokenKind.NotEq => l != r, TokenKind.Less => l < r,
                        TokenKind.LessEq => l <= r, TokenKind.Greater => l > r, _ => l >= r,
                    } ? 1 : 0;
                return;
            }

            case TokenKind.Shl or TokenKind.Shr:
            {
                var t = b.Left.Type;
                if (!(t.IsInteger || t.Kind == TypeKind.IntLiteral))
                    throw new CompileError(b.Pos, "Only numbers can be shifted");
                CheckShiftCount(b.Right, t);
                b.Type = t;
                if (l is not null)
                {
                    long shifted = b.Op == TokenKind.Shl ? l.Value << (int)r!.Value : l.Value >> (int)r!.Value;
                    b.ConstValue = t.Kind == TypeKind.IntLiteral ? shifted : Normalize(shifted, t);
                }
                return;
            }

            case TokenKind.Star or TokenKind.Slash or TokenKind.Percent:
            {
                if (l is null || r is null)
                    throw new CompileError(b.Pos,
                        $"'{OpText(b.Op)}' only works on constants. At runtime use a library function (mul8, div8, ...), or shifts for powers of two");
                var t = Unify(b.Left, b.Right, b.Pos);
                if (b.Op != TokenKind.Star && r == 0) throw new CompileError(b.Pos, "Division by zero");
                long value = b.Op switch { TokenKind.Star => l.Value * r.Value, TokenKind.Slash => l.Value / r.Value, _ => l.Value % r.Value };
                b.Type = t;
                b.ConstValue = t.Kind == TypeKind.IntLiteral ? value : Normalize(value, t);
                return;
            }

            default: // + - & | ^
            {
                if (b.Left.Type.IsPointer || b.Right.Type.IsPointer)
                {
                    CheckPointerArithmetic(b);
                    return;
                }
                var t = Unify(b.Left, b.Right, b.Pos);
                if (!(t.IsInteger || t.Kind == TypeKind.IntLiteral))
                    throw new CompileError(b.Pos, $"'{OpText(b.Op)}' needs numbers");
                b.Type = t;
                if (l is not null && r is not null)
                {
                    long value = b.Op switch
                    {
                        TokenKind.Plus => l.Value + r.Value, TokenKind.Minus => l.Value - r.Value,
                        TokenKind.Amp => l.Value & r.Value, TokenKind.Pipe => l.Value | r.Value, _ => l.Value ^ r.Value,
                    };
                    b.ConstValue = t.Kind == TypeKind.IntLiteral ? value : Normalize(value, t);
                }
                return;
            }
        }
    }

    /// <summary><c>p + n</c>, <c>n + p</c> and <c>p - n</c>: a pointer moved by a number of bytes.</summary>
    private static void CheckPointerArithmetic(BinaryExpr b)
    {
        bool leftPtr = b.Left.Type.IsPointer;
        if (b.Op is not (TokenKind.Plus or TokenKind.Minus))
            throw new CompileError(b.Pos, $"'{OpText(b.Op)}' does not work on pointers. Convert to u16 first: (p as u16)");
        if (leftPtr && b.Right.Type.IsPointer)
            throw new CompileError(b.Pos,
                $"Two pointers cannot be {(b.Op == TokenKind.Plus ? "added" : "subtracted")}. Convert them to u16 first: (p as u16)");
        if (!leftPtr && b.Op == TokenKind.Minus)
            throw new CompileError(b.Pos, "A pointer cannot be subtracted from a number");
        CheckPointerOffset(leftPtr ? b.Right : b.Left);
        b.Type = leftPtr ? b.Left.Type : b.Right.Type;
    }

    /// <summary>The number of bytes a pointer moves by: an unsigned number.</summary>
    private static void CheckPointerOffset(Expr offset)
    {
        if (offset.Type.Kind == TypeKind.IntLiteral)
        {
            if (offset.ConstValue < 0)
                throw new CompileError(offset.Pos, "A pointer moves by an unsigned number; use '-' to move back");
            Coerce(offset, offset.ConstValue <= 255 ? TernType.U8 : TernType.U16);
            return;
        }
        if (!offset.Type.IsInteger || offset.Type.IsSigned)
            throw new CompileError(offset.Pos, $"A pointer moves by a u8 or u16, not {offset.Type}");
    }

    private void CheckShiftCount(Expr count, TernType shifted)
    {
        if (count.ConstValue is not long n)
            throw new CompileError(count.Pos, "The shift count must be a constant. Use shl8/shr8 for a variable count");
        int bits = shifted.Kind == TypeKind.IntLiteral ? 32 : shifted.Size * 8;
        if (n < 0 || n >= bits)
            throw new CompileError(count.Pos, $"Shift count must be 0 to {bits - 1}");
        if (count.Type.Kind == TypeKind.IntLiteral) Coerce(count, TernType.U8);
    }

    private void CheckCall(CallExpr c)
    {
        switch (c.Name)
        {
            case "len":
            {
                if (c.Args.Count != 1) throw new CompileError(c.Pos, "len() takes one array");
                CheckExpr(c.Args[0]);
                if (!c.Args[0].Type.IsArray) throw new CompileError(c.Pos, "len() needs an array");
                c.Type = TernType.IntLiteral;
                c.ConstValue = c.Args[0].Type.Length;
                return;
            }
            case "include_bytes":
                throw new CompileError(c.Pos, "include_bytes() can only be the value of data or a global array");
            case "addr":
            {
                if (c.Args.Count != 1 || c.Args[0] is not NameExpr name)
                    throw new CompileError(c.Pos, "addr() takes the name of a variable, array or data");
                switch (Lookup(name.Name))
                {
                    case null: throw new CompileError(name.Pos, $"'{name.Name}' is not declared");
                    case ConstSymbol: throw new CompileError(name.Pos, $"'{name.Name}' is a constant, which has no address");
                    case FunctionSymbol: throw new CompileError(name.Pos, "addr() of a function is not supported yet");
                    case VariableSymbol v:
                        if (!v.InMemory) throw new CompileError(name.Pos, $"'{name.Name}' lives in a register and has no address");
                        name.Symbol = v;
                        name.Type = v.Type;
                        v.AddressTaken = true;
                        c.AddressOf = v;
                        // An array gives a pointer to its first element. A split array of 16-bit
                        // values gives a pointer to its table of low bytes.
                        c.Type = !v.Type.IsArray ? TernType.PointerTo(v.Type)
                            : v.Type.Element!.Size == 1 ? TernType.PointerTo(v.Type.Element!)
                            : TernType.PointerTo(TernType.U8);
                        return;
                }
                throw new InvalidOperationException();
            }
            case "sizeof":
            {
                if (c.Args.Count != 1 || c.Args[0] is not NameExpr typeName)
                    throw new CompileError(c.Pos, "sizeof() takes one type name");
                c.Type = TernType.IntLiteral;
                c.ConstValue = ResolveType(new NamedTypeSyntax(typeName.Pos, typeName.Name)).Size;
                return;
            }
        }

        if (Lookup(c.Name) is not FunctionSymbol fn)
            throw new CompileError(c.Pos, $"'{c.Name}' is not a function");
        if (c.Args.Count != fn.Parameters.Count)
            throw new CompileError(c.Pos, $"'{fn.Name}' takes {fn.Parameters.Count} argument(s), not {c.Args.Count}");
        for (int i = 0; i < c.Args.Count; i++)
        {
            CheckExpr(c.Args[i]);
            Coerce(c.Args[i], fn.Parameters[i].Type);
        }
        c.Function = fn;
        c.Type = fn.ReturnType;
        _function?.Callees.Add(fn);
    }

    // ---------- Type rules ----------

    /// <summary>Type of a number literal with no context: the smallest type that holds it.</summary>
    private static TernType DefaultType(Expr e)
    {
        if (e.Type.Kind != TypeKind.IntLiteral) return e.Type;
        long v = e.ConstValue!.Value;
        if (v >= 0 && v <= 255) return TernType.U8;
        if (v >= 0 && v <= 65535) return TernType.U16;
        if (v >= -128 && v < 0) return TernType.I8;
        if (v >= -32768 && v < 0) return TernType.I16;
        throw new CompileError(e.Pos, $"{v} does not fit in 16 bits");
    }

    /// <summary>Makes two operands the same type, applying the automatic conversions.</summary>
    private TernType Unify(Expr left, Expr right, SourcePos pos)
    {
        var lt = left.Type;
        var rt = right.Type;
        if (lt.Kind == TypeKind.IntLiteral && rt.Kind == TypeKind.IntLiteral) return lt;
        if (lt.Kind == TypeKind.IntLiteral) { Coerce(left, rt); return rt; }
        if (rt.Kind == TypeKind.IntLiteral) { Coerce(right, lt); return lt; }
        if (!lt.IsInteger || !rt.IsInteger)
            throw new CompileError(pos, $"Cannot combine {lt} and {rt}");
        if (lt.IsSigned != rt.IsSigned)
            throw new CompileError(pos, $"Cannot mix signed and unsigned ({lt} and {rt}); convert one side with 'as'");
        return lt.Size >= rt.Size ? lt : rt;
    }

    /// <summary>Checks that <paramref name="e"/> can be used where <paramref name="target"/> is expected.</summary>
    private static void Coerce(Expr e, TernType target)
    {
        var source = e.Type;
        if (source.Kind == TypeKind.Error) return;
        if (source.Kind == TypeKind.Void)
            throw new CompileError(e.Pos, "This function call does not return a value");

        if (source.Kind == TypeKind.IntLiteral)
        {
            if (!target.IsInteger && !target.IsPointer)
                throw new CompileError(e.Pos, $"Expected {target}, found a number");
            long v = e.ConstValue!.Value;
            if (v < target.MinValue || v > target.MaxValue)
                throw new CompileError(e.Pos, $"{v} does not fit in {target} ({target.MinValue} to {target.MaxValue})");
            e.Type = target;
            return;
        }
        if (source.SameAs(target)) return;
        if (target.IsPointer && source.IsPointer)
            throw new CompileError(e.Pos, $"Expected {target}, found {source}. Convert with 'as {target}'");
        if (source.IsInteger && target.IsInteger && source.IsSigned == target.IsSigned && source.Size < target.Size)
            return; // automatic widening; the code generator extends the value
        bool convertible = (source.IsInteger || source.IsPointer) && (target.IsInteger || target.IsPointer);
        throw new CompileError(e.Pos, $"Expected {target}, found {source}" + (convertible ? $". Convert with 'as {target}'" : ""));
    }

    /// <summary>Wraps a value to the range of <paramref name="t"/>, as the CPU would.</summary>
    public static long Normalize(long v, TernType t) => t.Kind switch
    {
        TypeKind.U8 => v & 0xFF,
        TypeKind.I8 => (sbyte)(v & 0xFF),
        TypeKind.U16 or TypeKind.Ptr => v & 0xFFFF,
        TypeKind.I16 => (short)(v & 0xFFFF),
        TypeKind.Bool => v != 0 ? 1 : 0,
        _ => v,
    };

    private static string OpText(TokenKind op) => op switch
    {
        TokenKind.AmpAmp => "&&", TokenKind.PipePipe => "||", TokenKind.Star => "*", TokenKind.Slash => "/",
        TokenKind.Percent => "%", TokenKind.PlusEq => "+=", TokenKind.MinusEq => "-=", TokenKind.AmpEq => "&=",
        TokenKind.PipeEq => "|=", TokenKind.CaretEq => "^=", TokenKind.ShlEq => "<<=", TokenKind.ShrEq => ">>=",
        TokenKind.Plus => "+", TokenKind.Minus => "-", TokenKind.Amp => "&", TokenKind.Pipe => "|", TokenKind.Caret => "^",
        _ => op.ToString(),
    };

    // ---------- Call graph ----------

    /// <summary>Functions reachable from main, callers before callees. Reports recursion.</summary>
    private static List<FunctionSymbol> OrderReachable(FunctionSymbol main)
    {
        var state = new Dictionary<FunctionSymbol, int>(); // 1 = visiting, 2 = done
        var postOrder = new List<FunctionSymbol>();
        var path = new Stack<FunctionSymbol>();

        void Visit(FunctionSymbol f)
        {
            state[f] = 1;
            path.Push(f);
            foreach (var callee in f.Callees)
            {
                if (state.GetValueOrDefault(callee) == 1)
                {
                    var cycle = path.Reverse().SkipWhile(x => x != callee).Select(x => x.Name).Append(callee.Name);
                    throw new CompileError(callee.Pos, $"Recursion is not allowed: {string.Join(" -> ", cycle)}");
                }
                if (state.GetValueOrDefault(callee) == 0) Visit(callee);
            }
            path.Pop();
            state[f] = 2;
            postOrder.Add(f);
        }

        Visit(main);
        postOrder.Reverse();
        return postOrder;
    }
}
