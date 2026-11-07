using Tern.Compiler.Syntax;

namespace Tern.Compiler.Semantics;

public sealed class CheckedProgram(
    List<Module> modules, List<VariableSymbol> globals, List<FunctionSymbol> functions, FunctionSymbol main)
{
    /// <summary>The main file and the modules it imports.</summary>
    public List<Module> Modules { get; } = modules;
    /// <summary>The main file's globals, and the globals of modules used by the functions in <see cref="Functions"/>.</summary>
    public List<VariableSymbol> Globals { get; } = globals;
    /// <summary>Functions reachable from <c>main</c>, then from each interrupt handler; callers before callees.</summary>
    public List<FunctionSymbol> Functions { get; } = functions;
    public FunctionSymbol Main { get; } = main;
    /// <summary>The memory the program may use (spec 8.4).</summary>
    public MemoryLayout Layout { get; init; } = null!;
    /// <summary>The exported functions of a binary without <c>main</c> (spec 12.3), in jump table order; empty for a program.</summary>
    public List<FunctionSymbol> Exports { get; init; } = [];
    public bool IsBinary => Exports.Count > 0;
}

/// <summary>
/// Resolves names, assigns types, folds constants and builds the call graph.
/// Reports the first error as a <see cref="CompileError"/>.
/// </summary>
public sealed partial class Checker
{
    private readonly List<Dictionary<string, Symbol>> _scopes = [];
    private readonly List<VariableSymbol> _globals = [];
    /// <summary>The file being checked.</summary>
    private Module _module = null!;
    private FunctionSymbol? _function;
    private int _loopDepth;
    /// <summary>Inside a branch that a constant condition never takes: nothing in it keeps a function or variable in the program.</summary>
    private bool _dead;
    private readonly HashSet<Register> _loopRegisters = [];
    /// <summary>The largest value of each enclosing loop counter whose bounds are constants (for index checks).</summary>
    private readonly Dictionary<VariableSymbol, long> _loopMax = [];
    private int _hiddenCounter;
    private readonly Platform _platform;

    private MemoryLayout _layout;

    private Checker(Platform platform)
    {
        _platform = platform;
        _layout = MemoryLayout.Default(platform);
    }

    /// <param name="modules">The main file and its modules, each module before the files that import it.</param>
    public static CheckedProgram Check(List<Module> modules, Platform platform) => new Checker(platform).Run(modules);

    private CheckedProgram Run(List<Module> modules)
    {
        // Declarations file by file, so that a file can use the constants of the modules it imports.
        foreach (var module in modules)
        {
            _module = module;
            var program = module.Syntax;
            if (!module.IsMain && program.Functions.FirstOrDefault(f => f.Name == "main") is { } wrongMain)
                throw new CompileError(wrongMain.Pos, $"Only the program's main file has 'fn main()'; '{module.Name}' is a module");
            // Struct names first, so that types can name structs declared further down.
            foreach (var s in program.Structs) DeclareStruct(s);
            DeclareParameters(module, modules[^1]);
            foreach (var c in program.Consts) DeclareConst(c);
            if (!module.IsMain && program.Memory is { } wrongMemory)
                throw new CompileError(wrongMemory.Pos, $"Only the program's main file may have a memory block, since it describes the whole program; '{module.Name}' is a module");
            if (!module.IsMain && program.Exports.Count > 0)
                throw new CompileError(program.Exports[0].Pos, $"Only the program's main file may export functions; '{module.Name}' is a module");
            if (module.IsMain && program.Memory is { } memory) _layout = EvaluateMemory(memory);
            foreach (var s in program.Structs) LayOut((StructSymbol)module.Scope[s.Name]);
            foreach (var f in program.Functions) DeclareFunction(f);
            foreach (var g in program.Globals) DeclareGlobal(g);
            // After the globals, so that a variable named as an address gets the right error.
            foreach (var f in program.Functions.Where(f => f.Address is not null)) DeclareRoutine(f, f.Symbol!);
            foreach (var a in program.Asserts) CheckAssert(a, module);
            module.Declared = true;
        }

        // "@ name" means a region or an address, never either depending on other declarations (spec 8.4).
        foreach (var line in modules[^1].Syntax.Memory?.Lines.Where(l => l.Kind == "region") ?? [])
            if (modules[^1].Scope.TryGetValue(line.Name!, out var clash) || modules[^1].Imports.ContainsKey(line.Name!))
                throw new CompileError(line.Pos,
                    $"The region '{line.Name}' has the name of {(clash is null ? "an imported module" : $"a {KindName(clash)} of the main file (line {clash.Pos.Line})")}, so '@ {line.Name}' would be ambiguous. Rename the region");

        foreach (var import in modules[^1].Syntax.Imports.Where(i => i.Area is not null))
        {
            if (_layout.Overlays.ContainsKey(import.Area!)) continue;
            throw new CompileError(import.AreaPos, _layout.Regions.ContainsKey(import.Area!)
                ? $"'{import.Area}' is a region, not an overlay area; '@' on an import names an area declared with 'overlay {import.Area}' in the memory block"
                : $"The memory block has no overlay area '{import.Area}'. Add a line 'first..=last overlay {import.Area}' to it");
        }

        foreach (var g in _globals)
            if (g.FixedAddress is int fixedAt && _layout.IsVolatile(fixedAt, fixedAt + g.Type.Size - 1)) g.InVolatileArea = true;

        var mainModule = modules[^1];
        var exports = new List<FunctionSymbol>();
        FunctionSymbol main;
        if (mainModule.Syntax.Exports.Count > 0)
        {
            // A binary for another program to call (spec 12.3): its entries are one root.
            if (mainModule.Scope.TryGetValue("main", out var hasMain) && hasMain is FunctionSymbol)
                throw new CompileError(mainModule.Syntax.Exports[0].Pos,
                    "A program with 'main' cannot export functions: 'export' builds a binary for another program to call, which has no 'main'. Remove one of them");
            exports = ResolveExports(mainModule);
            main = new FunctionSymbol(EntriesRoot, mainModule.Syntax.Exports[0].Pos);
            foreach (var e in exports) main.Callees.Add(e);
        }
        else
        {
            if (!mainModule.Scope.TryGetValue("main", out var m) || m is not FunctionSymbol realMain)
                throw new CompileError(new SourcePos(1, 1, mainModule.Path),
                    "The program has no 'fn main()'. A binary for another program to call has an 'export' line instead");
            main = realMain;
            if (main.Parameters.Count > 0 || main.ReturnType.Kind != TypeKind.Void)
                throw new CompileError(main.Pos, "'main' must have no parameters and no return value");
            if (main.FixedAddress is not null)
                throw new CompileError(main.Pos, "'main' must have a body");
            if (main.IsInterrupt)
                throw new CompileError(main.Pos, "'main' cannot be an interrupt handler; give the handler another name");
            if (main.IsInline)
                throw new CompileError(main.Pos, "'main' cannot be inline: nothing calls it");
        }

        foreach (var module in modules)
        {
            _module = module;
            foreach (var f in module.Syntax.Functions.Where(f => f.Address is null)) CheckFunctionBody(f);
        }
        CheckInlineCallsBetweenOverlays();
        // Struct fields become plain variables, arrays and pointers for the code generator.
        foreach (var module in modules)
            foreach (var f in module.Syntax.Functions) Lowering.Apply(f.Body);

        // Only what main and the interrupt handlers can reach goes into the program, so unused parts
        // of a module cost nothing. The main file's globals are all kept.
        MarkParts(main, mainModule);
        var reachable = OrderRoots(main, mainModule);
        if (exports.Count > 0) reachable.Remove(main);   // the root of a binary's entries has no code
        CheckPrivateGlobalsPerRoot(modules, reachable);
        MarkGlobalsUsedFromTwoRoots(reachable);
        var used = reachable.SelectMany(f => f.UsedGlobals).Concat(mainModule.Scope.Values.OfType<VariableSymbol>()).ToHashSet();
        return new CheckedProgram(modules, _globals.Where(used.Contains).ToList(), reachable, main) { Layout = _layout, Exports = exports };
    }

    /// <summary>The name of the root that stands for a binary's entries (spec 12.3); it has no code.</summary>
    private const string EntriesRoot = "(entries)";

    /// <summary>The functions named by <c>export</c> lines, in order. Each must be callable with registers alone.</summary>
    private static List<FunctionSymbol> ResolveExports(Module mainModule)
    {
        var result = new List<FunctionSymbol>();
        foreach (var e in mainModule.Syntax.Exports)
        {
            if (!mainModule.Scope.TryGetValue(e.Name, out var symbol) || symbol is not FunctionSymbol f)
                throw new CompileError(e.Pos, mainModule.Scope.ContainsKey(e.Name) ? $"'{e.Name}' is not a function, so it cannot be exported" : $"'{e.Name}' is not declared; 'export' names functions of the main file");
            if (e.Name == "setup")
                throw new CompileError(e.Pos, "'setup' is the name of the binary's startup entry (entry 0 of the jump table). Rename the function");
            if (result.Contains(f))
                throw new CompileError(e.Pos, $"'{e.Name}' is exported twice");
            if (f.IsInterrupt)
                throw new CompileError(e.Pos, $"'{e.Name}' is an interrupt handler, which the CPU calls through a vector; it cannot be an entry of the jump table. Export a plain function that does the work");
            if (f.IsInline)
                throw new CompileError(e.Pos, $"'{e.Name}' is inline: it has no code of its own to put in the jump table. Remove 'inline'");
            if (f.FixedAddress is not null)
                throw new CompileError(e.Pos, $"'{e.Name}' is a routine at a fixed address, not part of this binary");
            if (f.Parameters.FirstOrDefault(p => p.PassedIn is null) is { } memoryParam)
                throw new CompileError(e.Pos,
                    $"'{e.Name}' takes '{memoryParam.Name}' in memory, which the caller of a binary cannot set. Pass it in a register ({memoryParam.Name}: {memoryParam.Type} using A)");
            result.Add(f);
        }
        return result;
    }

    // ---------- Declarations ----------

    private MemoryLayout EvaluateMemory(MemoryDecl memory)
    {
        long Value(Expr e)
        {
            CheckExpr(e);
            return e.ConstValue ?? throw new CompileError(e.Pos, "A memory range must be known at compile time: use numbers and constants");
        }
        return MemoryLayout.Build(_platform, memory.Lines.Select(l => (l, Value(l.First), Value(l.Last))).ToList());
    }

    /// <param name="name">The name in this file's scope; for top-level declarations of a module, without the module name.</param>
    private void Declare(Symbol symbol, string? name = null)
    {
        name ??= symbol.Name;
        var scope = _scopes.Count > 0 ? _scopes[^1] : _module.Scope;
        if (_module.Imports.ContainsKey(name))
            throw new CompileError(symbol.Pos, $"'{name}' is the name of an imported module; choose another name");
        if (Lookup(name) is { } existing)
            throw new CompileError(symbol.Pos, $"'{name}' is already declared at {existing.Pos}");
        scope[name] = symbol;
    }

    /// <summary>
    /// Finds a name in the enclosing scopes and the current file, or <c>module.name</c> in an imported module.
    /// A <c>private</c> name of another module is not found.
    /// </summary>
    private Symbol? Lookup(string name)
    {
        int dot = name.IndexOf('.');
        if (dot > 0)
        {
            if (!_module.Imports.TryGetValue(name[..dot], out var module)) return null;
            string member = name[(dot + 1)..];
            return module.Syntax.PrivateNames.Contains(member) ? null : module.Scope.GetValueOrDefault(member);
        }
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out var s)) return s;
        return _module.Scope.GetValueOrDefault(name);
    }

    /// <summary>The error for a name that is not declared. A module name on its own gets a hint.</summary>
    private CompileError NotDeclared(string name, SourcePos pos)
    {
        if (_module.Imports.ContainsKey(name))
            return new CompileError(pos, $"'{name}' is a module; use the names in it as {name}.name");
        int dot = name.IndexOf('.');
        if (dot <= 0) return new CompileError(pos, $"'{name}' is not declared");
        // s.a() where s is declared: a field or member, not a function in a module.
        switch (Lookup(name[..dot]))
        {
            case VariableSymbol { Type: var t } when t.IsStruct || t is { IsPointer: true, Element.IsStruct: true }:
                return new CompileError(pos, $"'{name}' is a field, not a function; only functions can be called");
            case { } s:
                return new CompileError(pos, $"'{name}' is not a function: '{name[..dot]}' is a {KindName(s)}, not a module");
        }
        if (!_module.Imports.TryGetValue(name[..dot], out var other))
            return MissingImport(name[..dot], pos);
        if (other.Syntax.PrivateNames.Contains(name[(dot + 1)..]))
            return new CompileError(pos, $"'{name}' is private to the module {other.Name}; only code in {other.Name} can use it");
        // Modules that import each other are declared one after the other, so a declaration
        // in the first one cannot use the second one's names yet.
        if (!other.Declared)
        {
            string here = _module.IsMain ? "the main file" : _module.Name;
            return new CompileError(pos,
                $"'{name}' cannot be used in a declaration of {here}: {other.Name} and {here} import each other, and {other.Name} is declared after {here}. " +
                $"Move {name[(dot + 1)..]} to {here}, or to a module both import");
        }
        return new CompileError(pos, $"'{name}' is not declared");
    }

    private static string KindName(Symbol s) => s switch
    {
        VariableSymbol => "variable",
        ConstSymbol => "constant",
        FunctionSymbol => "function",
        _ => "struct type",
    };

    /// <summary><c>text.put</c> where <c>text</c> is not declared: most likely a missing import.</summary>
    private static CompileError MissingImport(string module, SourcePos pos) =>
        new(pos, $"'{module}' is not declared. To use the module {module}, add 'import {module}' at the top of the file");

    /// <summary>Reports <c>name.member</c> where <c>name</c> is not declared as a missing import.</summary>
    private void CheckMemberTarget(MemberExpr m)
    {
        if (m.Member is not ("lo" or "hi") && m.Target is NameExpr n && !n.Name.Contains('.') && Lookup(n.Name) is null && !_module.Imports.ContainsKey(n.Name))
            throw MissingImport(n.Name, n.Pos);
    }

    /// <summary>Records that the function being checked uses a global, so it is kept in the program.</summary>
    private void UseGlobal(VariableSymbol v, SourcePos pos)
    {
        if (v.Kind != VariableKind.Global || _function is null) return;
        // Overlays in one area share their memory: another one's variables are not there while this one runs.
        if (_function.OverlayArea is { } area && v.OverlayArea == area && v.Module != _function.Module && (v.FixedAddress is null || v.IsData) && v.Region is null)
            throw new CompileError(pos,
                $"'{v.Name}' belongs to {v.Module!.Name}, which is in the same overlay area '{area}' as {_function.Module!.Name}. They are never loaded together and share their memory. " +
                "Keep shared state in a resident module or the main file");
        if (_dead) return;
        _function.UsedGlobals.Add(v);
        // A table of function addresses keeps those functions in the program.
        foreach (var (_, f) in v.Storage.FunctionAddresses) _function.UsedAddresses.Add(f);
    }

    /// <summary>The main file with the constants it can declare without any import, for evaluating module parameter values.</summary>
    private Module? _parameterScope;
    /// <summary>Constants of the main file that a parameter value cannot use, with the reason.</summary>
    private readonly Dictionary<string, string> _unusableConstants = [];

    /// <summary>Declares a module's parameters as constants, using the values from the main file's import or the defaults.</summary>
    private void DeclareParameters(Module module, Module main)
    {
        var header = module.Syntax.Header;
        var import = module.ImportedBy;
        if (header is not null && module.IsMain)
            throw new CompileError(header.Pos, "The main file has no module header; 'module' is only for modules that have parameters");
        if (header is not null && header.Name != module.Name)
            throw new CompileError(header.Pos, $"The module header says '{header.Name}' but the file is {module.Name}.tern; the name must match the file");
        var parameters = header?.Parameters ?? [];
        if (import is not null)
        {
            var seen = new HashSet<string>();
            foreach (var arg in import.Args)
            {
                if (parameters.All(p => p.Name != arg.Name))
                    throw new CompileError(arg.Pos, header is null
                        ? $"Module '{module.Name}' has no parameters (it has no 'module {module.Name}(...)' header)"
                        : $"Module '{module.Name}' has no parameter '{arg.Name}'; its parameters are {string.Join(", ", parameters.Select(p => p.Name))}");
                if (!seen.Add(arg.Name))
                    throw new CompileError(arg.Pos, $"Parameter '{arg.Name}' is set twice");
            }
        }
        var saved = _module;
        foreach (var p in parameters)
        {
            var type = ResolveType(p.Type);
            if (!(type.IsInteger || type.Kind == TypeKind.Bool))
                throw new CompileError(p.Type.Pos, "A module parameter is an integer type or bool");
            var arg = import?.Args.FirstOrDefault(a => a.Name == p.Name);
            Expr value = arg?.Value ?? p.Default;
            if (arg is not null)
            {
                // The value may use literals and the main file's constants that need no import.
                _parameterScope ??= ParameterScope(main);
                _module = _parameterScope;
                try { CheckExpr(value); }
                catch (CompileError e)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(e.Description, "^'([^']+)' is not declared");
                    if (m.Success && _unusableConstants.TryGetValue(m.Groups[1].Value, out var why))
                        throw new CompileError(e.Pos, $"'{m.Groups[1].Value}' cannot be used in a parameter value, since it does not check without the rest of the program: {why}");
                    throw new CompileError(e.Pos, e.Description + ". A parameter value uses literals and the main file's constants that do not depend on imported modules");
                }
                finally { _module = saved; }
            }
            else
                CheckExpr(value);
            if (value.ConstValue is not long v)
                throw new CompileError(value.Pos, $"The value of parameter '{p.Name}' is not known at compile time");
            Coerce(value, type);
            Declare(new ConstSymbol(p.Name, p.Pos, type, v) { UsedParameters = [p.Name] });
        }
    }

    private Module ParameterScope(Module main)
    {
        // A fresh copy of the main file, so that checking its constants here changes nothing in the real syntax tree.
        var scope = new Module
        {
            Name = "", Path = main.Path, Directory = main.Directory, Source = main.Source,
            Syntax = Parser.Parse(main.Source, main.Path),
        };
        var saved = _module;
        _module = scope;
        // Structs first, so that sizeof works. One that needs an import is left out.
        foreach (var st in scope.Syntax.Structs)
        {
            try { DeclareStruct(st); LayOut((StructSymbol)scope.Scope[st.Name]); }
            catch (CompileError) { scope.Scope.Remove(st.Name); }
        }
        foreach (var c in scope.Syntax.Consts)
        {
            try { DeclareConst(c); }
            catch (CompileError e) { _unusableConstants[c.Name] = e.Description; } // needs an import, data or a later constant
        }
        _module = saved;
        return scope;
    }

    private void CheckAssert(AssertDecl a, Module module)
    {
        CheckExpr(a.Condition);
        if (a.Condition.Type.Kind != TypeKind.Bool)
            throw new CompileError(a.Condition.Pos, "An assert condition is a bool, e.g. COUNT <= 64");
        if (a.Condition.ConstValue is not long v)
            throw new CompileError(a.Condition.Pos, "An assert condition must be known at compile time: use constants, sizeof and len");
        if (v != 0) return;
        // A wrong parameter is reported where the main file set it, also when the condition only
        // uses constants computed from it.
        var import = module.ImportedBy;
        var used = new HashSet<string>();
        CollectNames(a.Condition, used);
        var parameters = used.SelectMany(n => module.Scope.GetValueOrDefault(n) is ConstSymbol c ? c.UsedParameters : []).ToHashSet();
        if (import is not null && import.Args.Any(arg => parameters.Contains(arg.Name)))
            throw new CompileError(import.Pos, $"{a.Message} (assert at {a.Pos})");
        throw new CompileError(a.Pos, a.Message);
    }

    private static void CollectNames(Expr e, HashSet<string> names)
    {
        switch (e)
        {
            case NameExpr n: names.Add(n.Name); break;
            case UnaryExpr u: CollectNames(u.Operand, names); break;
            case BinaryExpr b: CollectNames(b.Left, names); CollectNames(b.Right, names); break;
            case CallExpr c: foreach (var x in c.Args) CollectNames(x, names); break;
            case CastExpr c: CollectNames(c.Operand, names); break;
            case IndexExpr i: CollectNames(i.Target, names); CollectNames(i.Index, names); break;
            case MemberExpr m: CollectNames(m.Target, names); break;
        }
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
        var names = new HashSet<string>();
        CollectNames(decl.Value, names);
        Declare(new ConstSymbol(decl.Name, decl.Pos, type, decl.Value.ConstValue!.Value)
        {
            UsedParameters = names.SelectMany(n => _module.Scope.GetValueOrDefault(n) is ConstSymbol c ? c.UsedParameters : []).ToHashSet(),
        });
    }

    private void DeclareFunction(FnDecl decl)
    {
        if (decl.Name is "addr" or "len" or "sizeof" or "include_bytes")
            throw new CompileError(decl.Pos, $"'{decl.Name}' is a built-in function; choose another name");
        var fn = new FunctionSymbol(_module.Qualify(decl.Name), decl.Pos) { Declaration = decl, IsInterrupt = decl.IsInterrupt, IsInline = decl.IsInline };
        decl.Symbol = fn;
        fn.Module = _module;

        var usedRegisters = new HashSet<char>();
        foreach (var p in decl.Parameters)
        {
            if (p.FixedValue is not null)
            {
                // Checked with the routine's address, after the globals are declared.
                if (decl.Address is null)
                    throw new CompileError(p.Pos, decl.IsAsm
                        ? "Fixed inputs are only for routines at a fixed address. An asm fn sets up its inputs in its own code"
                        : "Fixed inputs are only for routines at a fixed address (fn name(...) @ $addr). Give the parameter a name and a type: 'name: type'");
                continue;
            }
            var type = ResolveType(p.Type);
            if (type.IsStruct)
                throw new CompileError(p.Pos, $"A struct cannot be passed to a function, since structs are never copied. Pass a pointer (ptr {type}) or an index");
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
                // An asm fn or a fixed-address routine uses register parameters where they arrive; they have no memory.
                if (decl.IsAsm || decl.Address is not null) param.Register = reg;
            }
            else if (decl.IsAsm && decl.IsInline)
                throw new CompileError(p.Pos,
                    $"Parameter '{p.Name}' of an inline asm fn must be passed in a register (using A, X, Y, AX, AY or XY): it is copied into each caller, which has no memory for it");
            else if (decl.Address is not null)
                throw new CompileError(p.Pos,
                    $"Parameter '{p.Name}' must be passed in a register (using A, X, Y, AX, AY or XY): a routine at a fixed address has no memory for parameters");
            fn.Parameters.Add(param);
            if (param.InMemory) fn.FrameVariables.Add(param);
        }

        if (decl.ReturnType is not null)
        {
            fn.ReturnType = ResolveType(decl.ReturnType);
            if (fn.ReturnType.IsStruct)
                throw new CompileError(decl.ReturnType.Pos, $"A function cannot return a struct, since structs are never copied. Return a pointer (ptr {fn.ReturnType}) or an index");
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

        Declare(fn, decl.Name);
    }

    /// <summary>A constant passed on every call of a routine at a fixed address: <c>true using carry</c>, <c>3 using X</c>.</summary>
    private void DeclareFixedInput(FunctionSymbol fn, ParamSyntax p, HashSet<char> usedRegisters)
    {
        var reg = p.UsingRegister!.Value;
        var value = p.FixedValue!;
        CheckExpr(value);
        if (value.ConstValue is not long v)
            throw new CompileError(value.Pos, "A fixed input must be a constant");
        switch (reg)
        {
            case Register.Carry:
                if (value.Type.Kind != TypeKind.Bool)
                    throw new CompileError(value.Pos, "The carry takes true or false");
                break;
            case Register.A or Register.X or Register.Y:
                Coerce(value, TernType.U8);
                break;
            default:
                throw new CompileError(p.Pos, $"A fixed input goes in A, X, Y or the carry, not {reg}");
        }
        char key = reg == Register.Carry ? 'c' : reg.ToString()[0];
        if (!usedRegisters.Add(key))
            throw new CompileError(p.Pos, reg == Register.Carry ? "The carry is set more than once" : $"Register {reg} is used by more than one parameter");
        fn.FixedInputs.Add((reg, v));
    }

    /// <summary>A routine that already exists at a fixed address: the address and the registers it keeps.</summary>
    private void DeclareRoutine(FnDecl decl, FunctionSymbol fn)
    {
        CheckExpr(decl.Address!);
        if (decl.Address!.ConstValue is not long a)
            throw new CompileError(decl.Address.Pos, "The address must be known at compile time");
        if (a < 0 || a > 0xFFFF)
            throw new CompileError(decl.Address.Pos, $"Address ${a:X} is outside memory");
        fn.FixedAddress = (int)a;

        var usedRegisters = fn.Parameters.SelectMany(p => p.PassedIn!.Value.ToString().Where(char.IsUpper)).ToHashSet();
        foreach (var p in decl.Parameters.Where(p => p.FixedValue is not null))
            DeclareFixedInput(fn, p, usedRegisters);

        foreach (var (reg, pos) in decl.Keeps)
        {
            if (reg is not (Register.A or Register.X or Register.Y))
                throw new CompileError(pos, $"'keeps' lists the registers A, X and Y, not {reg}. Flags always count as changed");
            if (!fn.Keeps.Add(reg))
                throw new CompileError(pos, $"{reg} is listed twice after 'keeps'");
            if (fn.ReturnRegister is { } ret && ret.Uses(reg))
                throw new CompileError(pos, $"'{fn.Name}' returns a value in {reg}, so it cannot keep {reg}");
        }
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
        var v = new VariableSymbol(_module.Qualify(decl.Name), decl.Pos, type, VariableKind.Global, null)
        {
            IsData = decl.IsData, NoInit = decl.IsNoinit, DeclaredVolatile = decl.IsVolatile,
        };
        decl.Symbol = v;
        v.Module = _module;
        ApplyPlacement(decl, v, allowAddress: true);
        if (decl.IsNoinit && decl.Init is not null)
            throw new CompileError(decl.Init.Pos, $"'{decl.Name}' is noinit, so it cannot have an initial value. Leave out one or the other");
        if (decl.IsNoinit && v.FixedAddress is not null)
            throw new CompileError(decl.Pos, $"'{decl.Name}' is at a fixed address, which is never zeroed; leave out 'noinit'");

        if (decl.IsData && (type.IsStruct || type.IsStructArray))
            throw new CompileError(decl.Pos, "Data cannot hold structs yet. Use a var, or data arrays of numbers");
        if (decl.IsData && !type.IsArray)
            throw new CompileError(decl.Pos, "Data must be an array or a string. For a single value, use 'const'");
        if (decl.Init is not null && (type.IsStruct || type.IsStructArray))
            throw new CompileError(decl.Init.Pos, "A struct cannot have an initial value. Assign its fields at the start of main");
        if (decl.Init is not null)
        {
            if (v.FixedAddress is not null && !decl.IsData)
                throw new CompileError(decl.Init.Pos, "A variable at a fixed address cannot have an initial value");
            if (v.Region is not null && !decl.IsData)
                throw new CompileError(decl.Init.Pos, "A variable in a region cannot have an initial value: the region is not part of the program file. Assign it at the start of main");
            if (bytes is not null)
            {
                if (!type.IsArray || type.Element!.Kind != TypeKind.U8)
                    throw new CompileError(decl.Init.Pos, $"This value is a [u8; {bytes.Length}] array, but '{decl.Name}' is {type}");
                if (type.Length != bytes.Length)
                    throw new CompileError(decl.Init.Pos, $"Expected {type.Length} bytes, found {bytes.Length}");
                v.InitialBytes = bytes;
            }
            else
                v.InitialBytes = ConstantBytes(decl.Init, type, v);
        }
        Declare(v, decl.Name);
        _globals.Add(v);
    }

    /// <summary>The bytes of a string literal or <c>include_bytes()</c>, or null for other initializers.</summary>
    private byte[]? ByteSource(Expr init)
    {
        switch (init)
        {
            case StringExpr s:
                if (s.Text.Length == 0) throw new CompileError(s.Pos, "A string must have at least one character");
                var encoding = EncodingOf(s.Encoding, s.Pos);
                return s.Text.Select(c => (byte)Encode(c, encoding, s.Pos)).ToArray();
            case CallExpr { Name: "include_bytes" } call:
                return IncludeBytes(call);
            default:
                return null;
        }
    }

    /// <summary>The encoding named by a literal's prefix, or the target's default.</summary>
    private CharEncoding EncodingOf(string? prefix, SourcePos pos)
    {
        if (prefix is null) return _platform.DefaultEncoding;
        if (CharEncoding.ByName.TryGetValue(prefix, out var encoding)) return encoding;
        throw new CompileError(pos, $"Unknown encoding '{prefix}'. Known encodings: {string.Join(", ", CharEncoding.ByName.Keys)}");
    }

    private static int Encode(char c, CharEncoding encoding, SourcePos pos)
    {
        if (Lexer.IsRawByte(c)) return c - Lexer.RawByteBase;
        if (encoding.Encode(c) is int value) return value;
        string shown = c == '\n' ? "\\n" : c.ToString();
        throw new CompileError(pos, $"'{shown}' has no code in the {encoding.Name} encoding. Write the byte as \\$NN");
    }

    private byte[] IncludeBytes(CallExpr call)
    {
        if (call.Args is not [StringExpr { Encoding: null } file])
            throw new CompileError(call.Pos, "include_bytes() takes a file name in quotes");
        var path = Path.Combine(_module.Directory, file.Text);
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
        if (decl.IsLinear)
        {
            var t = decl.TypeSyntax is null ? null : ResolveType(decl.TypeSyntax);
            if (t is null || !t.IsArray)
                throw new CompileError(decl.Pos,
                    "'linear' is for arrays of 16-bit values or structs, e.g. 'linear var pts: [u16; 32]' or 'linear var list: [Enemy; 8]'");
            if (t.Element!.Size == 1)
                throw new CompileError(decl.Pos, $"'linear' is only for arrays of 16-bit values or structs: the elements of a {t} are one byte each, so they are already stored in order");
            if (!t.Element.IsStruct && t.Length > 128)
                throw new CompileError(decl.Pos,
                    $"A linear array of 16-bit values has at most 128 elements, so that the doubled index fits in a register; '{decl.Name}' has {t.Length}. Leave out 'linear' to store it split");
            return TernType.ArrayOf(t.Element, t.Length, linear: true);
        }
        if (decl.TypeSyntax is not null) return ResolveType(decl.TypeSyntax);
        if (decl.Init is null) throw new CompileError(decl.Pos, $"'{decl.Name}' needs a type or an initial value");
        if (decl.Init is ArrayLiteralExpr)
            throw new CompileError(decl.Pos, decl.IsData
                ? $"Data arrays need a written type, e.g. data {decl.Name}: [u8; 4] = [...]"
                : "Array variables need a written type, e.g. [u8; 4]");
        CheckExpr(decl.Init);
        if (decl.Init.Type.IsStruct) Coerce(decl.Init, decl.Init.Type); // "A struct cannot be used as a value"
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
                if (addr is NameExpr region && _layout.Regions.ContainsKey(region.Name) && Lookup(region.Name) is null)
                {
                    v.Region = region.Name;
                    // A region that touches volatile memory is volatile as a whole: its addresses are not known yet.
                    var span = _layout.Regions[region.Name];
                    v.InVolatileArea = _layout.IsVolatile(span.First, span.Last) || _platform.IsVolatile(span.First, span.Last);
                    break;
                }
                CheckExpr(addr);
                if (addr.ConstValue is not long a)
                    throw new CompileError(addr.Pos, "The address must be known at compile time");
                if (a < 0 || a + v.Type.Size > 0x10000)
                    throw new CompileError(addr.Pos, $"Address ${a:X} is outside memory");
                v.FixedAddress = (int)a;
                v.InVolatileArea = _platform.IsVolatile((int)a, (int)a + v.Type.Size - 1);
                break;
        }
    }

    /// <param name="target">Collects the function addresses (<c>addr(f)</c>) in the value.</param>
    private byte[] ConstantBytes(Expr init, TernType type, VariableSymbol target, int at = 0)
    {
        if (type.IsArray)
        {
            if (init is not ArrayLiteralExpr list)
                throw new CompileError(init.Pos, "An array must be initialized with [ ... ]");
            if (list.Elements.Count != type.Length)
                throw new CompileError(init.Pos, $"Expected {type.Length} values, found {list.Elements.Count}");
            var bytes = new List<byte>();
            foreach (var e in list.Elements) bytes.AddRange(ConstantBytes(e, type.Element!, target, at + bytes.Count));
            return bytes.ToArray();
        }

        CheckExpr(init);
        Coerce(init, type);
        if (init is CallExpr { FunctionAddress: { } f })
        {
            // Filled in when the program is placed.
            target.FunctionAddresses.Add((at, f));
            return [0, 0];
        }
        if (init.ConstValue is not long value)
            throw new CompileError(init.Pos, UsesFunctionAddress(init)
                ? "addr() of a function can be an initial value only on its own, not in a calculation"
                : UsesAddr(init)
                ? "addr() cannot be used in an initial value, since addresses are known only after compiling. Assign it at the start of main"
                : "A global's initial value must be known at compile time");
        return type.Size == 1 ? [(byte)value] : [(byte)value, (byte)(value >> 8)];
    }

    private static bool UsesAddr(Expr e) => e switch
    {
        CallExpr c => c.AddressOf is not null,
        UnaryExpr u => UsesAddr(u.Operand),
        BinaryExpr b => UsesAddr(b.Left) || UsesAddr(b.Right),
        CastExpr c => UsesAddr(c.Operand),
        MemberExpr m => UsesAddr(m.Target),
        _ => false,
    };

    private static bool UsesFunctionAddress(Expr e) => e switch
    {
        CallExpr c => c.FunctionAddress is not null,
        UnaryExpr u => UsesFunctionAddress(u.Operand),
        BinaryExpr b => UsesFunctionAddress(b.Left) || UsesFunctionAddress(b.Right),
        CastExpr c => UsesFunctionAddress(c.Operand),
        MemberExpr m => UsesFunctionAddress(m.Target),
        _ => false,
    };

    /// <param name="layOut">False for the target of a pointer, which may be a struct still being laid out (<c>next: ptr Enemy</c>).</param>
    private TernType ResolveType(TypeSyntax syntax, bool layOut = true)
    {
        switch (syntax)
        {
            case NamedTypeSyntax n:
                switch (n.Name)
                {
                    case "u8": return TernType.U8;
                    case "i8": return TernType.I8;
                    case "u16": return TernType.U16;
                    case "i16": return TernType.I16;
                    case "bool": return TernType.Bool;
                    case "ptr": throw new CompileError(n.Pos, "'ptr' needs the type it points to, e.g. 'ptr u8'");
                }
                switch (Lookup(n.Name))
                {
                    case StructSymbol s:
                        if (layOut) LayOut(s);
                        return s.Type;
                    case null when n.Name.Contains('.'):
                        throw NotDeclared(n.Name, n.Pos);
                    case null:
                        throw new CompileError(n.Pos, $"Unknown type '{n.Name}'");
                    default:
                        throw new CompileError(n.Pos, $"'{n.Name}' is not a type");
                }
            case PointerTypeSyntax p:
                var target = ResolveType(p.Target, layOut: false);
                if (!target.IsScalar && !target.IsStruct) throw new CompileError(p.Pos, "A pointer must point to a number, bool, pointer or struct");
                return TernType.PointerTo(target);
            case ArrayTypeSyntax a:
                var element = ResolveType(a.Element);
                if (!element.IsScalar && !element.IsStruct) throw new CompileError(a.Pos, "Array elements must be numbers, bool, pointers or structs");
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

        // Copied into the caller, an rts or rti would end the caller too.
        if (decl is { IsAsm: true, IsInline: true })
            foreach (var ins in ((AsmStmt)decl.Body.Statements[0]).Lines.OfType<AsmInstruction>())
                if (ins.Mnemonic is Tern.Cpu.Mnemonic.Rts or Tern.Cpu.Mnemonic.Rti)
                    throw new CompileError(ins.Pos,
                        $"'{ins.Mnemonic.ToString().ToLowerInvariant()}' in an inline asm fn would end the function that calls it, since the code is copied there. Remove it: the code simply runs on after the copy");

        if (_function.ReturnType.Kind != TypeKind.Void && !decl.IsAsm && !AlwaysReturns(decl.Body))
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
            {
                // A constant condition compiles only the branch taken: calls, variables and addresses in the
                // other one are checked but not recorded, so what only it uses is left out of the program (spec 2.2).
                CheckCondition(i.Condition);
                bool wasDead = _dead;
                _dead = wasDead || i.Condition.ConstValue == 0;
                CheckBlock(i.Then);
                _dead = wasDead || i.Condition.ConstValue is not null and not 0;
                if (i.Else is not null) CheckStmt(i.Else);
                _dead = wasDead;
                break;
            }
            case WhileStmt w:
            {
                CheckCondition(w.Condition);
                bool wasDead = _dead;
                _dead = wasDead || w.Condition.ConstValue == 0;
                _loopDepth++;
                CheckBlock(w.Body);
                _loopDepth--;
                _dead = wasDead;
                break;
            }
            case ForStmt f: CheckFor(f); break;
            case MatchStmt m: CheckMatch(m); break;
            case AsmStmt a: CheckAsm(a); break;
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
            if (!type.IsArray && !type.IsStruct)
                throw new CompileError(decl.Pos, $"Local variable '{decl.Name}' needs an initial value");
        }
        else
        {
            if (type.IsStruct || type.IsStructArray)
                throw new CompileError(decl.Init.Pos, "A struct cannot have an initial value. Assign its fields");
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
        if (target.Type.IsStruct)
            throw new CompileError(target.Pos, "Structs are never copied, so a struct cannot be assigned. Assign its fields one by one, or copy the memory with mem.copy");
        if (!IsAssignable(target))
            throw new CompileError(target.Pos, "The left side of an assignment must be a variable, an array element, a field, or .lo/.hi of one");
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
        switch (Unwrap(e))
        {
            case NameExpr { Symbol: VariableSymbol v }:
                if (v.IsData) throw new CompileError(e.Pos, $"'{v.Name}' is data, which is read-only");
                if (v.ReadOnly) throw new CompileError(e.Pos, $"Loop variable '{v.Name}' cannot be changed inside the loop");
                return true;
            case IndexExpr ix:
                if (Unwrap(ix.Target) is NameExpr { Symbol: { IsData: true } d })
                    throw new CompileError(e.Pos, $"'{d.Storage.Name}' is data, which is read-only");
                return true;
            case MemberExpr m:
                return Unwrap(m.Target) is NameExpr or IndexExpr && IsAssignable(m.Target);
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
        if (f.Step is not null) CheckExpr(f.Step);
        if (f.Step is { ConstValue: null, Type.IsInteger: true })
            CheckVariableStep(f, type, from, to);
        else if (f.Step is not null)
        {
            if (f.Step.ConstValue is not long step || step == 0)
                throw new CompileError(f.Step.Pos, "'step' must be a non-zero constant, or a u8 variable in a loop that counts up with a u8 counter");
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
            if (ContainsCall(f.Body, reg))
                throw new CompileError(f.Pos,
                    $"The loop body calls a function, which can change {reg}. Remove 'using {reg}', or call only routines that keep {reg}");
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
        if (from is not null && to is not null && LargestLoopValue(from.Value, to.Value, f.Inclusive, f.StepValue) is long largest)
            _loopMax[counter] = largest;
        CheckBlock(f.Body, newScope: false);
        _loopMax.Remove(counter);
        _loopDepth--;
        _scopes.RemoveAt(_scopes.Count - 1);

        if (f.UsingRegister is { } r) _loopRegisters.Remove(r);
    }

    /// <summary>A step that is not a constant (spec 5.1): a u8 value, in a loop that counts up with a u8 counter.</summary>
    private static void CheckVariableStep(ForStmt f, TernType counter, long? from, long? to)
    {
        var step = f.Step!;
        if (step.Type.IsSigned)
            throw new CompileError(step.Pos, $"A variable step must be a u8, but this is {step.Type}: a variable step cannot be negative");
        if (step.Type.Size != 1)
            throw new CompileError(step.Pos, $"A variable step must be a u8, but this is {step.Type}. Convert with 'as u8' if it always fits");
        if (counter.Size != 1 || counter.IsSigned)
            throw new CompileError(step.Pos, $"A variable step needs a u8 loop counter, but the bounds are {counter}. Use a constant step, or u8 bounds");
        if ((from is not null && to is not null && to < from) || (from is null && to == 0))
            throw new CompileError(step.Pos, "A variable step cannot be used in a loop that counts down. Count up, or use a constant negative step");
        f.VariableStep = true;
        f.StepValue = 1;
    }

    /// <summary>An index that is the counter of a constant-bounds loop must stay inside the array (spec 3.2).</summary>
    private void CheckLoopIndex(Expr index, string name, int length)
    {
        if (Unwrap(index) is NameExpr { Symbol: VariableSymbol counter } && _loopMax.TryGetValue(counter, out var last) && last >= length)
            throw new CompileError(index.Pos,
                $"The loop reaches {last}, past the end of '{name}' (length {length}). Make the loop stop before {length}");
    }

    /// <summary>The largest value a loop with constant bounds reaches, or null if it runs no passes.</summary>
    private static long? LargestLoopValue(long from, long to, bool inclusive, int step)
    {
        if (step > 0)
        {
            long limit = inclusive ? to : to - 1;
            return from > limit ? null : from + (limit - from) / step * step;
        }
        long floor = inclusive ? to : to + 1;
        return from < floor ? null : from;
    }

    private void CheckMatch(MatchStmt m)
    {
        CheckExpr(m.Subject);
        if (m.Subject.Type.Kind == TypeKind.IntLiteral) Coerce(m.Subject, DefaultType(m.Subject));
        var type = m.Subject.Type;
        if (type.Kind is TypeKind.U16 or TypeKind.I16)
            throw new CompileError(m.Subject.Pos, "'match' on a 16-bit value is not supported yet. Match on .lo or .hi, or use if / else if");
        if (type.Kind is not (TypeKind.U8 or TypeKind.I8))
            throw new CompileError(m.Subject.Pos, $"'match' needs a u8 or i8 value, not {type}");
        int bias = type.IsSigned ? 128 : 0;

        // Which arm matches each value, to find values matched twice.
        var owner = new MatchPattern?[256];
        for (int a = 0; a < m.Arms.Count; a++)
        {
            var arm = m.Arms[a];
            if (arm.IsDefault && a != m.Arms.Count - 1)
                throw new CompileError(m.Arms[a + 1].Pos, "This arm can never run: '_' above it matches every value. Put '_' last");
            foreach (var p in arm.Patterns)
            {
                long low = PatternValue(p.Low, type), high = p.High is null ? low : PatternValue(p.High, type);
                if (high < low)
                    throw new CompileError(p.Pos, $"The range {low}..={high} is empty; write the smaller value first");
                p.From = (int)low + bias;
                p.To = (int)high + bias;
                for (int v = p.From; v <= p.To; v++)
                {
                    if (owner[v] is { } first)
                        throw new CompileError(p.Pos, $"{v - bias} is already matched at {first.Pos}; each value may appear in one pattern only");
                    owner[v] = p;
                }
            }
            CheckBlock(arm.Body);
        }
    }

    /// <summary>A constant in a match pattern, as a value of the subject's type.</summary>
    private long PatternValue(Expr e, TernType type)
    {
        CheckExpr(e);
        if (e.ConstValue is not long v)
            throw new CompileError(e.Pos, "A match pattern must be a constant: a number, a character or a const");
        Coerce(e, type);
        return v;
    }

    /// <summary>True if the code calls a function that may change <paramref name="reg"/>: any function except a routine that keeps it.</summary>
    private bool ContainsCall(Node node, Register reg) => node switch
    {
        CallExpr c => (c.Name is not ("addr" or "len" or "sizeof") && !(Lookup(c.Name) is FunctionSymbol f && f.Keeps.Contains(reg)))
            || c.Args.Any(a => ContainsCall(a, reg)),
        BlockStmt b => b.Statements.Any(x => ContainsCall(x, reg)),
        IfStmt i => ContainsCall(i.Condition, reg) || ContainsCall(i.Then, reg) || (i.Else is not null && ContainsCall(i.Else, reg)),
        WhileStmt w => ContainsCall(w.Condition, reg) || ContainsCall(w.Body, reg),
        ForStmt f => ContainsCall(f.From, reg) || ContainsCall(f.To, reg) || ContainsCall(f.Body, reg),
        MatchStmt m => ContainsCall(m.Subject, reg) || m.Arms.Any(a => ContainsCall(a.Body, reg)),
        ReturnStmt r => r.Value is not null && ContainsCall(r.Value, reg),
        AssignStmt a => ContainsCall(a.Target, reg) || ContainsCall(a.Value, reg),
        ExprStmt e => ContainsCall(e.Expr, reg),
        VarDecl v => v.Init is not null && ContainsCall(v.Init, reg),
        UnaryExpr u => ContainsCall(u.Operand, reg),
        BinaryExpr b => ContainsCall(b.Left, reg) || ContainsCall(b.Right, reg),
        IndexExpr ix => ContainsCall(ix.Target, reg) || ContainsCall(ix.Index, reg),
        MemberExpr m => ContainsCall(m.Target, reg),
        CastExpr c => ContainsCall(c.Operand, reg),
        _ => false,
    };

    private static bool AlwaysReturns(Stmt stmt) => stmt switch
    {
        ReturnStmt => true,
        BlockStmt b => b.Statements.Count > 0 && AlwaysReturns(b.Statements[^1]),
        IfStmt i => i.Else is not null && AlwaysReturns(i.Then) && AlwaysReturns(i.Else),
        WhileStmt w => w.Condition.ConstValue == 1 && !ContainsBreak(w.Body),
        MatchStmt m => m.Arms.Count > 0 && m.Arms[^1].IsDefault && m.Arms.All(a => AlwaysReturns(a.Body)),
        _ => false,
    };

    /// <summary>True if a break leaves this loop body (breaks inside nested loops do not count).</summary>
    private static bool ContainsBreak(Stmt stmt) => stmt switch
    {
        BreakStmt => true,
        BlockStmt b => b.Statements.Any(ContainsBreak),
        IfStmt i => ContainsBreak(i.Then) || (i.Else is not null && ContainsBreak(i.Else)),
        MatchStmt m => m.Arms.Any(a => ContainsBreak(a.Body)),
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
                e.ConstValue = Encode(ch.Value, EncodingOf(ch.Encoding, ch.Pos), ch.Pos);
                break;

            case StringExpr:
                throw new CompileError(e.Pos, "A string can only be the value of data or a global array");

            case NameExpr n:
                switch (Lookup(n.Name))
                {
                    case null: throw NotDeclared(n.Name, n.Pos);
                    case ConstSymbol c:
                        e.Type = c.Type;
                        e.ConstValue = c.Value;
                        break;
                    case VariableSymbol v:
                        n.Symbol = v;
                        UseGlobal(v, n.Pos);
                        e.Type = v.Type;
                        v.Weight += Weight();
                        if (v.Type.IsStruct) e.Place = new StructPlace(PlaceKind.Memory, 0, Base: v);
                        break;
                    case FunctionSymbol:
                        throw new CompileError(n.Pos, $"'{n.Name}' is a function; call it with ()");
                    case StructSymbol:
                        throw new CompileError(n.Pos, $"'{n.Name}' is a struct type, not a value");
                }
                break;

            case UnaryExpr u: CheckUnary(u); break;
            case BinaryExpr b: CheckBinary(b); break;
            case CallExpr c: CheckCall(c); break;

            case IndexExpr ix:
            {
                CheckExpr(ix.Target);
                CheckExpr(ix.Index);
                Coerce(ix.Index, TernType.U8);
                if (CheckArrayFieldIndex(ix)) break;
                var indexed = Unwrap(ix.Target);
                if (ix.Target.Type.IsPointer)
                {
                    var element = ix.Target.Type.Element!;
                    if (element.IsStruct)
                        throw new CompileError(ix.Pos,
                            $"A pointer to a struct cannot be indexed: p[i] would need a multiply by the struct size. Step the pointer instead: p = p + sizeof({element})");
                    if (element.Size == 2 && ix.Index.ConstValue is > 127)
                        throw new CompileError(ix.Index.Pos,
                            $"Index {ix.Index.ConstValue} is too large: p[i] reaches 256 bytes, which is 128 {element} values");
                    if (indexed is NameExpr { Symbol: { AliasOf: null } pv }) pv.Dereferenced = true;
                    e.Type = element;
                    break;
                }
                if (indexed is not NameExpr { Symbol: VariableSymbol arr } || !arr.Type.IsArray)
                    throw new CompileError(ix.Pos, "Only arrays and pointers can be indexed");
                if (arr.Type.IsStructArray)
                {
                    CheckRecordIndex(ix, arr);
                    break;
                }
                if (arr.Type.Length > 256)
                    throw new CompileError(ix.Pos,
                        $"'{arr.Name}' has {arr.Type.Length} elements, too many for a u8 index. Read it through a pointer: addr({arr.Name})");
                if (ix.Index.ConstValue is long idx && idx >= arr.Type.Length)
                    throw new CompileError(ix.Index.Pos, $"Index {idx} is past the end of '{arr.Name}' (length {arr.Type.Length})");
                CheckLoopIndex(ix.Index, arr.Name, arr.Type.Length);
                e.Type = arr.Type.Element!;
                if (arr.Type.Linear) ix.Lowered = LinearIndex(ix);
                break;
            }

            case MemberExpr m:
                CheckMemberTarget(m);
                CheckExpr(m.Target);
                if (m.Target.Type.IsStruct || m.Target.Type is { IsPointer: true, Element.IsStruct: true })
                {
                    CheckField(m);
                    break;
                }
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
        if ((b.Left.Type.IsStruct || b.Right.Type.IsStruct)
            && b.Op is not (TokenKind.EqEq or TokenKind.NotEq or TokenKind.Less or TokenKind.LessEq or TokenKind.Greater or TokenKind.GreaterEq))
            throw new CompileError(b.Pos, $"A struct is not a number; use one of its fields, e.g. s.x {OpText(b.Op)} 1");

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
                if (b.Left.Type.IsStruct || b.Right.Type.IsStruct)
                    throw new CompileError(b.Pos, "Structs cannot be compared; compare their fields, e.g. a.x == b.x");
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
                    // Two constants are compared as plain numbers: sizeof(T) * 32 <= 256 must not wrap to u8.
                    if (t.Kind == TypeKind.IntLiteral && !(l is not null && r is not null)) { Coerce(b.Left, DefaultType(b.Left)); Coerce(b.Right, b.Left.Type); }
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

    /// <summary>
    /// <c>p + n</c>, <c>n + p</c> and <c>p - n</c>: a pointer moved by a number of bytes.
    /// <c>p - q</c>: the distance between two pointers in bytes, a <c>u16</c>.
    /// </summary>
    private static void CheckPointerArithmetic(BinaryExpr b)
    {
        bool leftPtr = b.Left.Type.IsPointer;
        if (b.Op is not (TokenKind.Plus or TokenKind.Minus))
            throw new CompileError(b.Pos, $"'{OpText(b.Op)}' does not work on pointers. Convert to u16 first: (p as u16)");
        if (leftPtr && b.Right.Type.IsPointer)
        {
            if (b.Op == TokenKind.Plus)
                throw new CompileError(b.Pos, "Two pointers cannot be added. Convert them to u16 first: (p as u16)");
            if (!b.Left.Type.SameAs(b.Right.Type))
                throw new CompileError(b.Pos, $"Cannot subtract {b.Right.Type} from {b.Left.Type}; convert one side with 'as'");
            b.Type = TernType.U16;
            return;
        }
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

    /// <summary>
    /// <c>pts[i]</c> on a linear array of 16-bit values: the index counts elements, so it is doubled.
    /// A variable index is <c>i &lt;&lt; 1</c> (one <c>asl</c>), a constant one a fixed offset.
    /// </summary>
    private static IndexExpr LinearIndex(IndexExpr ix)
    {
        var u8 = TernType.U8;
        Expr index = ix.Index.ConstValue is long c
            ? new NumberExpr(ix.Index.Pos, 2 * c) { Type = u8, ConstValue = 2 * c }
            : new BinaryExpr(ix.Index.Pos, TokenKind.Shl, ix.Index, new NumberExpr(ix.Index.Pos, 1) { Type = u8, ConstValue = 1 }) { Type = u8 };
        return new IndexExpr(ix.Pos, ix.Target, index) { Type = ix.Type };
    }

    /// <summary>Makes <c>addr(...)</c> a constant address: the variable's, or a view's (its storage plus an offset).</summary>
    private static void TakeAddress(CallExpr c, VariableSymbol v)
    {
        v.AddressTaken = true;
        if (v.AliasOf is { } storage) storage.AddressTaken = true;
        c.AddressOf = v;
        // An array gives a pointer to its first element. A split array of 16-bit
        // values gives a pointer to its table of low bytes; a linear one, a pointer to its first value.
        c.Type = !v.Type.IsArray ? TernType.PointerTo(v.Type)
            : v.Type.Element!.IsStruct ? TernType.PointerTo(v.Type.Element)
            : v.Type.Element!.Size == 1 || v.Type.Linear ? TernType.PointerTo(v.Type.Element!)
            : TernType.PointerTo(TernType.U8);
    }

    /// <summary><c>addr(f)</c>: a <c>u16</c> known when the program is placed (spec 6.5).</summary>
    private void TakeFunctionAddress(CallExpr c, FunctionSymbol f, SourcePos pos)
    {
        if (f.IsInline)
            throw new CompileError(pos, $"'{f.Name}' is inline: it is copied into each caller and has no address of its own. Remove 'inline'");
        if (f.Parameters.FirstOrDefault(p => p.PassedIn is null) is { } memoryParam)
            throw new CompileError(pos,
                $"'{f.Name}' takes '{memoryParam.Name}' in memory, which a call through an address cannot set. " +
                $"Pass it in a register ({memoryParam.Name}: {memoryParam.Type} using A)");
        if (_function?.OverlayArea is { } area && f.OverlayArea == area && f.Module != _function.Module)
            throw new CompileError(pos,
                $"addr({f.Name}) is in {f.Module!.Name}, which is in the same overlay area '{area}' as {_function.Module!.Name}. They are never loaded together, so the address would point at the wrong code. " +
                "Take the address in resident code, or put one of them in another area");
        if (!_dead) f.AddressTaken = true;
        c.FunctionAddress = f;
        c.Type = TernType.U16;
        if (!_dead) _function?.UsedAddresses.Add(f);
    }

    /// <summary><c>addr(player.name)</c>: a field of a struct variable or of a struct at a fixed address.</summary>
    private void CheckAddrOfField(CallExpr c, MemberExpr field)
    {
        CheckExpr(field);
        string name = DescribePlace(field);
        var place = field.Place ?? Unwrap(field) switch
        {
            NameExpr { Symbol: { AliasOf: not null } } => null,
            IndexExpr { Target.Type.IsArray: true } => new StructPlace(PlaceKind.Table, 0),
            _ => new StructPlace(PlaceKind.Pointer, 0),
        };
        switch (place?.Kind)
        {
            case PlaceKind.Pointer:
                throw new CompileError(field.Pos,
                    $"'{name}' is reached through a pointer, so it has no fixed address. addr() works on fields of struct variables; through a pointer, add the field's offset to the pointer");
            case PlaceKind.Table:
                throw new CompileError(field.Pos,
                    $"'{name}' is in an array of structs, which is stored as one table per field, so the record has no address. addr() works on fields of struct variables");
        }
        var view = place is { Kind: PlaceKind.Memory } at
            ? View(at.Base!, at.Offset, field.Type, name)
            : ((NameExpr)Unwrap(field)).Symbol!;
        TakeAddress(c, (VariableSymbol)view);
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
                if (c.Args.Count == 1 && c.Args[0] is MemberExpr { Member: not ("lo" or "hi") } field)
                {
                    CheckAddrOfField(c, field);
                    return;
                }
                if (c.Args.Count != 1 || c.Args[0] is not NameExpr name)
                    throw new CompileError(c.Pos, "addr() takes the name of a variable, array or data, or a field of a struct variable");
                switch (Lookup(name.Name))
                {
                    // Constants are declared before the file's functions.
                    case null when _module.Syntax.Functions.Any(f => f.Name == name.Name):
                        throw new CompileError(name.Pos,
                            $"addr({name.Name}) is known only when the program is placed, so it cannot be part of a constant. Use it in a var, data or code");
                    case null: throw NotDeclared(name.Name, name.Pos);
                    case ConstSymbol: throw new CompileError(name.Pos, $"'{name.Name}' is a constant, which has no address");
                    case FunctionSymbol f:
                        TakeFunctionAddress(c, f, name.Pos);
                        return;
                    case VariableSymbol v:
                        if (!v.InMemory) throw new CompileError(name.Pos, $"'{name.Name}' lives in a register and has no address");
                        name.Symbol = v;
                        name.Type = v.Type;
                        UseGlobal(v, name.Pos);
                        if (v.Type.IsStructArray && !v.Type.Linear)
                            throw new CompileError(name.Pos,
                                $"'{name.Name}' is stored as one table per field, so there is no record to point to. Declare it 'linear var' to store whole records");
                        TakeAddress(c, v);
                        return;
                }
                throw new InvalidOperationException();
            }
            case "sizeof":
            {
                if (c.Args.Count != 1 || c.Args[0] is not NameExpr typeName)
                    throw new CompileError(c.Pos, "sizeof() takes one type name, e.g. sizeof(Enemy)");
                c.Type = TernType.IntLiteral;
                c.ConstValue = ResolveType(new NamedTypeSyntax(typeName.Pos, typeName.Name)).Size;
                return;
            }
        }

        var symbol = Lookup(c.Name);
        if (symbol is null && c.Name.Contains('.')) throw NotDeclared(c.Name, c.Pos);
        if (symbol is not FunctionSymbol fn)
            throw new CompileError(c.Pos, $"'{c.Name}' is not a function");
        if (fn.IsInterrupt)
        {
            if (_function is not { IsInterrupt: true })
                throw new CompileError(c.Pos,
                    $"'{c.Name}' is an interrupt fn, which only the CPU or another interrupt fn calls. Install its address in the interrupt vector, or call it from an interrupt fn");
        }
        if (c.Args.Count != fn.Parameters.Count)
            throw new CompileError(c.Pos, $"'{fn.Name}' takes {fn.Parameters.Count} argument(s), not {c.Args.Count}");
        for (int i = 0; i < c.Args.Count; i++)
        {
            CheckExpr(c.Args[i]);
            Coerce(c.Args[i], fn.Parameters[i].Type);
        }
        foreach (var p in fn.Parameters)
            foreach (var held in _loopRegisters)
                if (p.PassedIn is { } pr && pr.Uses(held))
                    throw new CompileError(c.Pos, $"'{fn.Name}' takes '{p.Name}' in {held}, which holds the loop variable. Remove 'using {held}' from the loop");
        foreach (var (reg, _) in fn.FixedInputs)
            if (_loopRegisters.Contains(reg))
                throw new CompileError(c.Pos, $"'{fn.Name}' takes a fixed input in {reg}, which holds the loop variable. Remove 'using {reg}' from the loop");
        c.Function = fn;
        c.Type = fn.ReturnType;
        if (_function is not null) AddCall(_function, fn, c.Pos);
    }

    /// <summary>Calls from an overlay's function to an inline function of another module, for <see cref="CheckInlineCallsBetweenOverlays"/>.</summary>
    private readonly List<(FunctionSymbol Caller, FunctionSymbol Callee, SourcePos Pos)> _inlineCalls = [];

    /// <summary>
    /// An inline function is copied into its caller, so what its body uses must be there when the caller
    /// runs: an inline function of another overlay in the same area may not use that overlay's variables
    /// or call its functions (spec 8.5).
    /// </summary>
    private void CheckInlineCallsBetweenOverlays()
    {
        foreach (var (caller, callee, pos) in _inlineCalls)
        {
            var area = caller.OverlayArea;
            if (callee.OverlayArea != area) continue;
            var seen = new HashSet<FunctionSymbol>();
            void Visit(FunctionSymbol inline)
            {
                if (!seen.Add(inline)) return;
                foreach (var g in inline.UsedGlobals.Where(g => g.OverlayArea == area && g.Module != caller.Module && g.FixedAddress is null && g.Region is null))
                    throw new CompileError(pos,
                        $"'{caller.Name}' calls the inline function '{callee.Name}', which is copied into it and uses '{g.Name}', a variable of {g.Module!.Name}. " +
                        $"{caller.Module!.Name} and {g.Module.Name} are both in the overlay area '{area}', never loaded together, and share their memory. Keep shared state in a resident module or the main file");
                foreach (var next in inline.Callees)
                    if (next.IsInline) Visit(next);
                    else if (next.OverlayArea == area && next.Module != caller.Module)
                        throw new CompileError(pos,
                            $"'{caller.Name}' calls the inline function '{callee.Name}', which is copied into it and calls '{next.Name}'. " +
                            $"{caller.Module!.Name} and {next.Module!.Name} are both in the overlay area '{area}', so they are never loaded together. Call between them from resident code");
            }
            Visit(callee);
        }
    }

    /// <summary>
    /// Records a call. Two overlays in the same area are never loaded together, so one cannot call
    /// the other (spec 8.5).
    /// </summary>
    private void AddCall(FunctionSymbol caller, FunctionSymbol callee, SourcePos pos)
    {
        if (!_dead) caller.Callees.Add(callee);
        if (!_dead && callee.IsInline && caller.OverlayArea is not null && callee.Module != caller.Module) _inlineCalls.Add((caller, callee, pos));
        if (caller.OverlayArea is { } area && callee.OverlayArea == area && callee.Module != caller.Module && !callee.IsInline)
            throw new CompileError(pos,
                $"'{caller.Name}' calls '{callee.Name}', but the modules {caller.Module!.Name} and {callee.Module!.Name} are both in the overlay area '{area}', so they are never loaded together. " +
                "Call between them from resident code, or put one of them in another area");
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
        if (source.IsStruct)
            throw new CompileError(e.Pos, $"A struct cannot be used as a value, since structs are never copied. Use one of its fields, or a pointer: addr(...) gives a ptr {source}");

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
        TokenKind.Shl => "<<", TokenKind.Shr => ">>",
        _ => op.ToString(),
    };

    // ---------- Call graph ----------

    /// <summary>
    /// The functions reachable from main, then from each interrupt handler, then from each function
    /// whose address is taken; callers first. Handlers are roots: those of the main file, and those
    /// a compiled function names (to install them). Each function records the roots it is reached
    /// from (<see cref="FunctionSymbol.Roots"/>); whether it may be shared between them is checked
    /// after code generation, when its temporaries are known (<see cref="CheckSharedFunctions"/>).
    /// </summary>
    private static List<FunctionSymbol> OrderRoots(FunctionSymbol main, Module mainModule)
    {
        var all = new List<FunctionSymbol>();
        var seen = new HashSet<FunctionSymbol>();
        var roots = new List<FunctionSymbol> { main };
        roots.AddRange(mainModule.Scope.Values.OfType<FunctionSymbol>().Where(f => f.IsInterrupt && !f.IsPart).OrderBy(f => f.Pos.Line));
        foreach (var h in roots.Skip(1)) CheckInstallable(h);
        var addressTaken = new List<FunctionSymbol>();

        void Named(FunctionSymbol f)
        {
            if (f.IsInterrupt)
            {
                if (f.IsPart)
                    throw new CompileError(f.Pos,
                        $"'{f.Name}' is called by another interrupt fn (so it is compiled as a part, ending with rts) and its address is also used to install it (which needs rti). Use it one way, or write a second interrupt fn for the other");
                CheckInstallable(f);
                if (!roots.Contains(f)) roots.Add(f);
            }
            else if (!addressTaken.Contains(f)) addressTaken.Add(f);
        }
        // The main file's globals are all kept, so the functions in their initial values are too.
        foreach (var g in mainModule.Scope.Values.OfType<VariableSymbol>())
            foreach (var (_, f) in g.FunctionAddresses) Named(f);

        void Add(FunctionSymbol root, bool isRoot)
        {
            foreach (var f in OrderReachable(root))
            {
                if (seen.Add(f)) all.Add(f);
                if (f.FixedAddress is not null) continue; // no frame; may be called from anywhere
                if (isRoot) f.Roots.Add(root);
                else f.Isolated = true;
                foreach (var h in f.UsedInterrupts) Named(h);
                foreach (var a in f.UsedAddresses) Named(a);
            }
        }
        // Main and the handlers first, then the address-taken functions, whose callers are unknown.
        for (int r = 0, a = 0; r < roots.Count || a < addressTaken.Count;)
        {
            if (r < roots.Count) Add(roots[r++], isRoot: true);
            else Add(addressTaken[a++], isRoot: false);
        }
        return all;
    }

    /// <summary>
    /// An interrupt fn is a part if a function that is compiled calls it (spec 6.3). Callers that are
    /// never compiled, such as a module's handler that nothing installs, do not count.
    /// </summary>
    private static void MarkParts(FunctionSymbol main, Module mainModule)
    {
        var visited = new HashSet<FunctionSymbol>();
        var compiled = new HashSet<FunctionSymbol>();
        void Visit(FunctionSymbol f)
        {
            if (!visited.Add(f)) return;
            foreach (var c in f.Callees)
            {
                if (c.IsInterrupt) { c.IsPart = true; Compiled(c); }
                else Visit(c);
            }
            foreach (var h in f.UsedInterrupts) Compiled(h);
            foreach (var a in f.UsedAddresses) Named(a);
        }
        void Compiled(FunctionSymbol f) { if (compiled.Add(f)) Visit(f); }
        void Named(FunctionSymbol f) { if (f.IsInterrupt) Compiled(f); else Visit(f); }
        Visit(main);
        foreach (var f in mainModule.Scope.Values.OfType<FunctionSymbol>().Where(f => f.IsInterrupt)) Compiled(f);
        foreach (var g in mainModule.Scope.Values.OfType<VariableSymbol>())
            foreach (var (_, f) in g.FunctionAddresses) Named(f);
    }

    /// <summary>A handler is entered by the CPU, with nothing to pass: only a part may have parameters or a return value.</summary>
    private static void CheckInstallable(FunctionSymbol f)
    {
        if (f.Parameters.Count > 0 || f.ReturnType.Kind != TypeKind.Void)
            throw new CompileError(f.Pos,
                $"'{f.Name}' has {(f.Parameters.Count > 0 ? "parameters" : "a return value")}, so it can only be called as a part of another interrupt fn. " +
                "An interrupt handler takes no parameters and returns no value: the CPU calls it, not your code");
    }

    private static string DescribeRoot(FunctionSymbol root) =>
        root.IsInterrupt ? $"the interrupt handler '{root.Name}'" : root.Name == EntriesRoot ? "the binary's entries" : "main";

    /// <summary>
    /// A function reached from two roots (main and a handler, or two handlers) may run again while
    /// it is stopped halfway by an interrupt, so it must have no memory of its own: no memory
    /// parameters, locals or temporaries (spec 8.1). Its callees are reached from both roots too,
    /// so they are checked the same way. Runs after code generation, which creates the temporaries.
    /// </summary>
    public static void CheckSharedFunctions(CheckedProgram program)
    {
        foreach (var f in program.Functions)
        {
            if (f.Roots.Count < 2 || f.FrameVariables.Count == 0) continue;
            var roots = f.Roots.OrderBy(r => r.IsInterrupt).ThenBy(r => r.Pos.File).ThenBy(r => r.Pos.Line).ToList();
            var v = f.FrameVariables[0];
            string memory = v.Kind switch
            {
                VariableKind.Temp => $"a hidden temporary for the code at line {v.Pos.Line} (e.g. for 16-bit arithmetic or an argument)",
                VariableKind.Parameter when v.PassedIn is { } reg => $"the parameter '{v.Name}', which it keeps in memory (it arrives in {reg})",
                VariableKind.Parameter => $"the parameter '{v.Name}' in memory",
                _ when v.Name.StartsWith("_loop") => $"the counter of the loop at line {v.Pos.Line}",
                _ => $"the local variable '{v.Name}'",
            };
            throw new CompileError(f.Pos,
                $"'{f.Name}' is called from {DescribeRoot(roots[0])} and from {DescribeRoot(roots[1])}. An interrupt can stop it halfway and run it again, " +
                $"which would overwrite its memory: {memory}. Only a function with no memory of its own can be shared between main and interrupt handlers. Use a copy for each");
        }
    }

    /// <summary>
    /// A module's private global used from two roots (main and a handler, or two handlers) is an
    /// error: an interrupt could change it while the other root is using it, and the caller cannot
    /// see it (review M6, finding 1). The library's work bytes are such globals. Public globals are
    /// how main and handlers talk to each other, and data is read-only, so both stay allowed.
    /// </summary>
    private static void CheckPrivateGlobalsPerRoot(List<Module> modules, List<FunctionSymbol> ordered)
    {
        var moduleOf = new Dictionary<VariableSymbol, Module>();
        foreach (var m in modules.Where(m => !m.IsMain))
            foreach (var v in m.Syntax.PrivateNames.Select(n => m.Scope.GetValueOrDefault(n)).OfType<VariableSymbol>())
                if (!v.IsData) // read-only tables are safe to share
                    moduleOf[v] = m;
        var privateGlobals = moduleOf.Keys.ToHashSet();
        if (privateGlobals.Count == 0) return;

        // A module's own handler may share the module's private variables (spec 8.1): its author wrote both sides.
        bool OwnHandler(FunctionSymbol root, VariableSymbol g) =>
            root.IsInterrupt && moduleOf[g].Scope.Values.Contains(root);
        // Or through an interrupt fn of the module that is called as a part (decision 34).
        // A function of the module that only the module's own interrupt fns call is on that side too (spec 8.1).
        var handlerSide = HandlerSideFunctions(ordered);
        bool OwnPart(FunctionSymbol f, VariableSymbol g) =>
            (f.IsInterrupt || handlerSide.Contains(f)) && moduleOf[g].Scope.Values.Contains(f);

        var firstUser = new Dictionary<VariableSymbol, (FunctionSymbol Function, FunctionSymbol Root)>();
        foreach (var f in ordered.Where(f => f.FixedAddress is null))
            foreach (var root in f.Roots.OrderBy(r => r.IsInterrupt))
                foreach (var used in f.UsedGlobals)
                {
                    var g = used.AliasOf ?? used;
                    if (!privateGlobals.Contains(g) || OwnPart(f, g) || (OwnHandler(root, g) && handlerSide.Contains(f))) continue;
                    if (!firstUser.TryGetValue(g, out var first))
                        firstUser[g] = (f, root);
                    else if (first.Root != root)
                    {
                        // Reported at the handler, which is in the program's own code.
                        var handler = root.IsInterrupt ? root : first.Root;
                        throw new CompileError(handler.Pos,
                            $"'{first.Function.Name}' (called from {DescribeRoot(first.Root)}) and '{f.Name}' (called from {DescribeRoot(root)}) both use '{g.Name}', " +
                            $"a private variable of its module. An interrupt could change it while the other is using it. " +
                            $"Call functions of this module from only main or only one interrupt handler");
                    }
                }
    }

    /// <summary>
    /// The functions that only interrupt fns of their own module call, directly or through other such
    /// functions: they run on the handler side of their module (spec 8.1). A function whose address is taken
    /// may be called from anywhere, so it is not one of them.
    /// </summary>
    private static HashSet<FunctionSymbol> HandlerSideFunctions(List<FunctionSymbol> ordered)
    {
        var callers = ordered.ToDictionary(f => f, _ => new List<FunctionSymbol>());
        foreach (var f in ordered)
            foreach (var callee in f.Callees)
                if (callers.TryGetValue(callee, out var list)) list.Add(f);
        var side = new HashSet<FunctionSymbol>();
        bool changed;
        do
        {
            changed = false;
            foreach (var f in ordered)
            {
                if (f.IsInterrupt || f.FixedAddress is not null || f.AddressTaken || f.Module is null || side.Contains(f)) continue;
                var from = callers[f];
                if (from.Count == 0 || !from.All(c => c.Module == f.Module && (c.IsInterrupt || side.Contains(c)))) continue;
                side.Add(f);
                changed = true;
            }
        } while (changed);
        return side;
    }

    /// <summary>
    /// A global used from two roots is volatile (spec 4, 8.1), so that a main loop waiting for a flag
    /// set by a handler reads it on every pass. A function reached from an address-taken function
    /// may be called from any root, so it also counts as reached from an unknown one.
    /// </summary>
    private static void MarkGlobalsUsedFromTwoRoots(List<FunctionSymbol> ordered)
    {
        var unknown = new FunctionSymbol("(through an address)", default);
        var rootOf = new Dictionary<VariableSymbol, FunctionSymbol>();
        foreach (var f in ordered.Where(f => f.FixedAddress is null))
            foreach (var root in f.Isolated ? f.Roots.Append(unknown) : f.Roots)
                foreach (var used in f.UsedGlobals)
                {
                    var g = used.Storage;
                    if (rootOf.TryGetValue(g, out var other) && other != root) g.UsedFromTwoRoots = true;
                    else rootOf[g] = root;
                }
    }

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
