using Tern.Compiler.CompileTime;
using Tern.Compiler.Syntax;

namespace Tern.Compiler.Semantics;

/// <summary>
/// <c>const fn</c> and the rest of compile-time evaluation (spec 4.1): declaring const fns, running
/// compile-time expressions in the interpreter, and turning their values into constants and table bytes.
/// </summary>
public sealed partial class Checker : ICompileTimeHost
{
    /// <summary>Values of compile-time expressions already evaluated, so a value is computed once.</summary>
    private readonly Dictionary<Expr, CtValue> _ctValues = [];
    /// <summary>Data tables as compile-time arrays, decoded once.</summary>
    private readonly Dictionary<VariableSymbol, CtArray> _dataArrays = [];
    /// <summary>Above 0 while a constant's value is checked: compile-time code may run there, also inside a function.</summary>
    private int _constValues;

    /// <summary>Code that runs on the 6502: a function body, except the values of its local constants.</summary>
    private bool InRuntimeCode => _function is not null && _constValues == 0;

    private void DeclareConstFn(FnDecl decl)
    {
        if (decl.Name is "addr" or "len" or "sizeof" or "offset" or "include_bytes")
            throw new CompileError(decl.Pos, $"'{decl.Name}' is a built-in function; choose another name");
        var fn = new ConstFnSymbol(_module.Qualify(decl.Name), decl.Pos, decl) { Module = _module };
        foreach (var p in decl.Parameters)
        {
            if (p.FixedValue is not null)
                throw new CompileError(p.Pos, "A const fn's parameter is a name and a compile-time type. Write 'n: int'");
            if (p.UsingRegister is not null)
                throw new CompileError(p.Pos, $"A const fn runs at compile time, so '{p.Name}' is in no register. Remove 'using {p.UsingRegister}'");
            if (fn.Parameters.Any(x => x.Name == p.Name))
                throw new CompileError(p.Pos, $"Parameter '{p.Name}' is declared twice");
            fn.Parameters.Add((p.Name, ConstFnType(p.Type)));
        }
        if (decl.ReturnUsing is not null)
            throw new CompileError(decl.ReturnType!.Pos, $"A const fn runs at compile time, so its value is in no register. Remove 'using {decl.ReturnUsing}'");
        if (decl.ReturnType is not null) fn.ReturnType = ConstFnType(decl.ReturnType);
        new ConstFnBodyCheck(fn, TopLevelNames(_module)).Run();
        Declare(fn, decl.Name);
    }

    /// <summary>A parameter or return type: an array's length is written '_', since it is part of the value.</summary>
    private static CtType ConstFnType(TypeSyntax t)
    {
        for (var at = t; at is ArrayTypeSyntax a; at = a.Element)
            if (a.Length is not null)
                throw new CompileError(a.Length.Pos, "A const fn takes and returns arrays of any length. Write '_' for the length, e.g. '[int; _]'");
        return Types.Resolve(t);
    }

    /// <summary>The names declared at the top level of a file, with where, which a const fn's variables may not take (spec 4).</summary>
    private static Dictionary<string, SourcePos> TopLevelNames(Module module)
    {
        var names = new Dictionary<string, SourcePos>();
        var s = module.Syntax;
        foreach (var (name, pos) in s.Consts.Select(c => (c.Name, c.Pos)).Concat(s.Globals.Select(g => (g.Name, g.Pos)))
                     .Concat(s.Functions.Select(f => (f.Name, f.Pos))).Concat(s.ConstFns.Select(f => (f.Name, f.Pos)))
                     .Concat(s.Structs.Select(st => (st.Name, st.Pos))).Concat(s.Header?.Parameters.Select(p => (p.Name, p.Pos)) ?? []))
            names.TryAdd(name, pos);
        return names;
    }

    /// <summary>
    /// What can be checked in a const fn's body before it runs (spec 4.1): the types written, names that
    /// are already declared, statements that cannot run at compile time, and which const fns emit.
    /// </summary>
    private sealed class ConstFnBodyCheck(ConstFnSymbol fn, Dictionary<string, SourcePos> topLevel)
    {
        private readonly List<Dictionary<string, SourcePos>> _scopes = [];
        private int _loops;

        public void Run()
        {
            _scopes.Add(fn.Declaration.Parameters.ToDictionary(p => p.Name, p => p.Pos));
            foreach (var p in fn.Declaration.Parameters) Name(p.Name, p.Pos, isParameter: true);
            Block(fn.Declaration.Body, newScope: false);
        }

        private void Name(string name, SourcePos pos, bool isParameter = false)
        {
            if (topLevel.TryGetValue(name, out var top))
                throw new CompileError(pos, $"'{name}' is already declared at {top}. Use another name");
            if (isParameter) return;
            foreach (var scope in _scopes)
                if (scope.TryGetValue(name, out var earlier))
                    throw new CompileError(pos, $"'{name}' is already declared at {earlier}. Use another name");
            _scopes[^1][name] = pos;
        }

        private void Block(BlockStmt block, bool newScope = true)
        {
            if (newScope) _scopes.Add([]);
            foreach (var s in block.Statements) Stmt(s);
            if (newScope) _scopes.RemoveAt(_scopes.Count - 1);
        }

        private void Stmt(Stmt s)
        {
            switch (s)
            {
                case VarDecl v:
                    if (v.IsLinear || v.IsIndexed)
                        throw new CompileError(v.Pos, "A const fn's array holds compile-time values, stored nowhere. Remove 'linear' or 'indexed'");
                    if (v.Placement.Kind != PlacementKind.None)
                        throw new CompileError(v.Pos, $"'{v.Name}' exists only at compile time, so it has no address. Remove the '@'");
                    if (v.TypeSyntax is not null) Types.Resolve(v.TypeSyntax);
                    else if (v.Init is null) throw new CompileError(v.Pos, $"'{v.Name}' needs an initial value. Write 'var {v.Name} = ...'");
                    Name(v.Name, v.Pos);
                    break;
                case ConstDecl c:
                    if (c.TypeSyntax is not null) Types.Resolve(c.TypeSyntax);
                    Name(c.Name, c.Pos);
                    break;
                case AssignStmt: break;
                case ExprStmt e:
                    if (e.Expr is not CallExpr)
                        throw new CompileError(e.Pos, "Only a function call can be used as a statement");
                    break;
                case BlockStmt b: Block(b); break;
                case IfStmt i:
                    Block(i.Then);
                    if (i.Else is not null) Stmt(i.Else);
                    break;
                case WhileStmt w:
                    _loops++;
                    Block(w.Body);
                    _loops--;
                    break;
                case ForStmt f:
                    if (f.UsingRegister is { } reg)
                        throw new CompileError(f.Pos, $"A const fn's loop runs at compile time, with its variable in no register. Remove 'using {reg}'");
                    _scopes.Add([]);
                    if (f.Variable is not null) Name(f.Variable, f.Pos);
                    _loops++;
                    Block(f.Body);
                    _loops--;
                    _scopes.RemoveAt(_scopes.Count - 1);
                    break;
                case MatchStmt m:
                    foreach (var arm in m.Arms) Block(arm.Body);
                    break;
                case BreakStmt or ContinueStmt:
                    if (_loops == 0) throw new CompileError(s.Pos, "'break' and 'continue' must be inside a loop");
                    break;
                case ReturnStmt r:
                    if (fn.ReturnType is null && r.Value is not null)
                        throw new CompileError(r.Pos, $"'{fn.Name}' returns no value. Remove the value, or give it a return type, e.g. 'const fn {fn.Declaration.Name}(...) -> int'");
                    if (fn.ReturnType is { } t && r.Value is null)
                        throw new CompileError(r.Pos, $"'{fn.Name}' must return {Interpreter.Describe(t)} value");
                    break;
                case EmitStmt e:
                    if (fn.ReturnType is not null)
                        throw new CompileError(e.Pos, $"'{fn.Name}' returns a value, so it cannot emit assembly: a generator returns nothing. Move the emit into a const fn with no return type");
                    // Checked when the const fn is read, like the rest of the fragment (review M22, finding 6).
                    if (e.Fragment.Lines.OfType<AsmGeneratorCall>().FirstOrDefault() is { } inner)
                        throw new CompileError(inner.Pos, $"A generator is called as a statement of the const fn, not inside 'emit'. Move '{inner.Call.Name}(...)' out of the emit");
                    fn.Emits = true;
                    break;
                case AsmStmt a:
                    throw new CompileError(a.Pos, "A const fn runs at compile time, so it cannot run assembly. Use 'emit { ... }' to generate assembly where the const fn is called");
                default:
                    throw new CompileError(s.Pos, "This statement cannot run at compile time");
            }
        }
    }

    // ---------- Evaluation ----------

    /// <summary>The value of a compile-time expression, computed once (spec 4.1). An error in code that runs on the 6502.</summary>
    private CtValue CompileTimeValue(Expr e)
    {
        if (_ctValues.TryGetValue(e, out var known)) return known;
        if (InRuntimeCode) throw InRuntime(e);
        var value = Interpreter.Evaluate(e, this, e.Pos);
        _ctValues[e] = value;
        return value;
    }

    /// <summary>The error for compile-time code in a function body (spec 4.1).</summary>
    private CompileError InRuntime(Expr e) => e switch
    {
        RealExpr r => new CompileError(e.Pos,
            $"{r.Text} is a real number, which exists only at compile time. Use a constant or a const fn to compute the value, e.g. 'const K = round({r.Text} * 40)'"),
        ListForExpr => new CompileError(e.Pos, "A list built with 'for' is computed at compile time. Use a 'for' loop to fill the array at runtime"),
        CallExpr { Name: "repeat" } when Lookup("repeat") is null =>
            new CompileError(e.Pos, "'repeat' builds an array at compile time. Use a 'for' loop to fill the array at runtime"),
        CallExpr c when Lookup(c.Name) is ConstFnSymbol { Emits: true } => new CompileError(e.Pos,
            $"'{c.Name}' is a generator, which emits assembly. Call it on a line of its own in an asm block"),
        CallExpr c when Lookup(c.Name) is ConstFnSymbol => new CompileError(e.Pos,
            $"'{c.Name}' is a const fn, which runs only at compile time. Use it in a constant, in data or in a global's initial value"),
        CallExpr c => new CompileError(e.Pos,
            $"{c.Name}() works only at compile time. Use it in a constant, in data, in a global's initial value or in a const fn"),
        NameExpr n => new CompileError(e.Pos,
            $"'{n.Name}' is a real number, which exists only at compile time. Use it in a constant or a const fn, e.g. 'const K = round({n.Name} * 40)'"),
        _ => new CompileError(e.Pos, "This value exists only at compile time"),
    };

    /// <summary>
    /// Whether an expression outside runtime code has a part that only the interpreter can compute: a
    /// real, a list, PI or TAU, a const fn or a compile-time built-in. Then the whole expression is
    /// computed there, so that <c>1 / 2.0</c> is a real and not an int and a real. Not through 'as',
    /// whose operand is checked on its own.
    /// </summary>
    private bool NeedsInterpreter(Expr e) => e switch
    {
        RealExpr or ListForExpr => true,
        NameExpr n => Lookup(n.Name) switch
        {
            null => n.Name is "PI" or "TAU",
            ConstSymbol { Real: not null } => true,
            _ => false,
        },
        CallExpr { Name: "addr" or "len" or "sizeof" or "offset" or "include_bytes" } => false,
        CallExpr c => Lookup(c.Name) switch
        {
            ConstFnSymbol => true,
            null => Interpreter.Builtins.Contains(c.Name),
            _ => false,
        },
        UnaryExpr u => NeedsInterpreter(u.Operand),
        BinaryExpr b => NeedsInterpreter(b.Left) || NeedsInterpreter(b.Right),
        MemberExpr { Member: "lo" or "hi" } m => NeedsInterpreter(m.Target),
        IndexExpr ix => NeedsInterpreter(ix.Index),
        _ => false,
    };

    /// <summary>
    /// A constant whose value comes from compile-time code (spec 4.1): declares it when the value is a real,
    /// which a const may hold; an array is an error. False for an int or bool, which the caller declares.
    /// </summary>
    private bool DeclaredRealConst(ConstDecl decl)
    {
        if (!NeedsInterpreter(decl.Value) && decl.Value is not ArrayLiteralExpr) return false;
        switch (CompileTimeValue(decl.Value))
        {
            case CtArray:
                throw new CompileError(decl.Value.Pos, $"'{decl.Name}' is an array, which a const cannot hold. Use data, e.g. 'data {decl.Name}: [u8; _] = ...'");
            case CtReal r when decl.TypeSyntax is not null:
                throw new CompileError(decl.TypeSyntax.Pos,
                    $"'{decl.Name}' is the real number {r}, which has no runtime type. Remove the type to keep a real constant, or use round(x), floor(x), ceil(x) or trunc(x)");
            case CtReal r:
                var names = new HashSet<string>();
                CollectNames(decl.Value, names);
                Declare(new ConstSymbol(decl.Name, decl.Pos, TernType.IntLiteral, 0)
                {
                    Real = r.Value,
                    UsedParameters = names.SelectMany(n => _module.Scope.GetValueOrDefault(n) is ConstSymbol c ? c.UsedParameters : []).ToHashSet(),
                });
                return true;
            default:
                return false;
        }
    }

    /// <summary>Checks a compile-time expression where a constant is expected: an int or a bool.</summary>
    private void EvaluateConstant(Expr e)
    {
        switch (CompileTimeValue(e))
        {
            case CtInt i:
                e.Type = TernType.IntLiteral;
                e.ConstValue = i.Value;
                break;
            case CtBool b:
                e.Type = TernType.Bool;
                e.ConstValue = b.Value ? 1 : 0;
                break;
            case CtReal r:
                throw new CompileError(e.Pos,
                    $"This value is the real number {r}, but an int is expected here. Use round(x), floor(x), ceil(x) or trunc(x) to make it an int");
            case CtArray a:
                throw new CompileError(e.Pos,
                    $"This value is an array of {a.Items.Length}, which only data or a global array can hold. Use it as their initial value, e.g. 'data t: [u8; _] = ...'");
            default:
                throw new CompileError(e.Pos, "A label() value is only for emit in a generator. Use it in a const fn that emits assembly");
        }
    }

    /// <summary>A list built with <c>for</c>, <c>repeat(value, count)</c>, or a call of a const fn that returns an array.</summary>
    private bool IsCompileTimeArray(Expr e) =>
        e is ListForExpr || e is CallExpr c && Lookup(c.Name) switch
        {
            ConstFnSymbol { ReturnType.Kind: CtKind.Array } => true,
            null => c.Name == "repeat",
            _ => false,
        };

    private CtArray CompileTimeArray(Expr e) => CompileTimeValue(e) as CtArray
        ?? throw new CompileError(e.Pos, "This value is not an array");

    /// <summary>
    /// A compile-time array as the bytes of a table (spec 4.1): the length is checked, and each element
    /// against the table's type.
    /// </summary>
    private static byte[] ArrayBytes(CtArray array, TernType type, SourcePos pos)
    {
        if (array.Items.Length != type.Length)
            throw new CompileError(pos, $"Expected {type.Length} values, found {array.Items.Length}");
        var element = type.Element!;
        if (!element.IsScalar)
            throw new CompileError(pos, $"A compile-time array holds numbers or bools, so it cannot fill a table of {element}. Use a table of numbers");
        var bytes = new List<byte>(type.Size);
        for (int k = 0; k < array.Items.Length; k++)
        {
            long value;
            var item = array.Items[k];
            if (element.Kind == TypeKind.Bool)
                value = item is CtBool b ? (b.Value ? 1 : 0)
                    : throw new CompileError(pos, $"Element {k} is {Interpreter.Describe(item.Type)}, but the table holds bool");
            else
            {
                value = item switch
                {
                    CtInt i => i.Value,
                    CtReal r => throw new CompileError(pos,
                        $"Element {k} is the real number {r}, which needs rounding to fit in {element}. Use round(x), floor(x), ceil(x) or trunc(x)"),
                    _ => throw new CompileError(pos, $"Element {k} is {Interpreter.Describe(item.Type)}, but the table holds {element}"),
                };
                if (value < element.MinValue || value > element.MaxValue)
                    throw new CompileError(pos, $"Element {k} is {value}, which does not fit in {element} ({element.MinValue} to {element.MaxValue})");
            }
            bytes.Add((byte)value);
            if (element.Size == 2) bytes.Add((byte)(value >> 8));
        }
        return bytes.ToArray();
    }

    // ---------- The interpreter's view of the program ----------

    /// <summary>A name in <paramref name="module"/>, or where the evaluation started when it is null.</summary>
    private Symbol? LookupIn(Module? module, string name)
    {
        if (module is null) return Lookup(name);
        Symbol? found = null;
        InModule(module, () => found = Lookup(name));
        return found;
    }

    CtValue? ICompileTimeHost.Read(Module? module, string name, SourcePos pos, bool inConstFn)
    {
        switch (LookupIn(module, name))
        {
            case null when FunctionDeclaredIn(module ?? _module, name):
                throw new CompileError(pos, $"'{name}' is a function, not a value");
            case null: return null;
            case ConstSymbol { Real: { } real }: return new CtReal(real);
            case ConstSymbol c: return c.Type.Kind == TypeKind.Bool ? CtBool.Of(c.Value != 0) : new CtInt(c.Value);
            case VariableSymbol { IsData: true, Kind: VariableKind.Global } v: return DataArray(v, name, pos);
            case VariableSymbol:
                throw new CompileError(pos, inConstFn
                    ? $"'{name}' is a variable, which a const fn cannot read. Use a const or data"
                    : $"'{name}' is a variable, whose value is not known at compile time. Use a const or data");
            case ConstFnSymbol:
                throw new CompileError(pos, $"'{name}' is a const fn; call it with ()");
            case FunctionSymbol:
                throw new CompileError(pos, $"'{name}' is a function, not a value");
            default:
                throw new CompileError(pos, $"'{name}' is a struct type, not a value");
        }
    }

    ConstFnSymbol? ICompileTimeHost.ConstFn(Module? module, string name, SourcePos pos) => LookupIn(module, name) switch
    {
        // Functions are declared after constants, so one may not be in the scope yet.
        null when FunctionDeclaredIn(module ?? _module, name) => throw new CompileError(pos, $"'{name}' runs on the 6502, so compile-time code cannot call it. Use a const fn"),
        null => null,
        ConstFnSymbol f => f,
        FunctionSymbol => throw new CompileError(pos, $"'{name}' runs on the 6502, so compile-time code cannot call it. Use a const fn"),
        var other => throw new CompileError(pos, $"'{name}' is a {KindName(other)}, not a function"),
    };

    /// <summary>Whether <paramref name="name"/> (perhaps <c>module.name</c>) is a function of the program, declared or not yet.</summary>
    private static bool FunctionDeclaredIn(Module module, string name)
    {
        int dot = name.IndexOf('.');
        if (dot > 0 && module.Imports.TryGetValue(name[..dot], out var other)) (module, name) = (other, name[(dot + 1)..]);
        return module.Syntax.Functions.Any(f => f.Name == name);
    }

    Symbol? ICompileTimeHost.Symbol(Module? module, string name) => LookupIn(module, name);

    Symbol ICompileTimeHost.AddressOf(Module? module, string name, SourcePos pos) => LookupIn(module, name) switch
    {
        VariableSymbol { Kind: VariableKind.Global } v => v,
        FunctionSymbol f => f,
        // Functions are declared after constants: a constant cannot hold a label anyway.
        null when FunctionDeclaredIn(module ?? _module, name) => throw new CompileError(pos,
            $"addr({name}) is a label, decided when the program is placed, so it cannot be part of a constant. Use it in a generator's emit"),
        null => throw NotDeclared(name, pos),
        ConstSymbol => throw new CompileError(pos, $"'{name}' is a constant, which has no address. Use the constant itself"),
        var other => throw new CompileError(pos, $"'{name}' is a {KindName(other)}, which has no address. Use addr() with a global, data or a function"),
    };

    long ICompileTimeHost.SizeOf(Module? module, string type, SourcePos pos)
    {
        long size = 0;
        void Work() => size = ResolveType(new NamedTypeSyntax(pos, type)).Size;
        if (module is null) Work();
        else InModule(module, Work);
        return size;
    }

    long ICompileTimeHost.OffsetOf(Module? module, CallExpr c)
    {
        long offset = 0;
        void Work() => offset = OffsetOf(c);
        if (module is null) Work();
        else InModule(module, Work);
        return offset;
    }

    long ICompileTimeHost.Encode(Module? module, CharExpr c) => Encode(c.Value, EncodingOf(c.Encoding, c.Pos), c.Pos);

    /// <summary>A data table as a compile-time array, element by element (spec 4.1).</summary>
    private CtArray DataArray(VariableSymbol v, string name, SourcePos pos)
    {
        if (_dataArrays.TryGetValue(v, out var known)) return known;
        if (v.Type.IsStruct || v.Type.IsStructArray)
            throw new CompileError(pos, $"'{name}' holds structs, which are not compile-time values. Read a table of numbers");
        if (v.AddressFills.Count > 0 || _addressInits.Any(a => a.Variable == v))
            throw new CompileError(pos, $"'{name}' holds addresses, which are decided after compile-time evaluation. Read a table without addr()");
        if (v.InitialBytes is not { } bytes)
            throw new CompileError(pos, $"'{name}' is read while its own initial value is computed, so it depends on itself. Give it a value that does not read '{name}'");
        var element = v.Type.Element!;
        var items = new CtValue[v.Type.Length];
        for (int k = 0; k < items.Length; k++)
            items[k] = element.Kind switch
            {
                TypeKind.Bool => CtBool.Of(bytes[k] != 0),
                TypeKind.I8 => new CtInt((sbyte)bytes[k]),
                TypeKind.U16 or TypeKind.Ptr => new CtInt(bytes[2 * k] | bytes[2 * k + 1] << 8),
                TypeKind.I16 => new CtInt((short)(bytes[2 * k] | bytes[2 * k + 1] << 8)),
                _ => new CtInt(bytes[k]),
            };
        var array = new CtArray(items, element.Kind == TypeKind.Bool ? CtType.Bool : CtType.Int);
        _dataArrays[v] = array;
        return array;
    }
}
