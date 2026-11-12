using Tern.Compiler.Syntax;

namespace Tern.Compiler.Semantics;

/// <summary>Declarations: names and scopes, constants, functions, global variables and their initial bytes, and types.</summary>
public sealed partial class Checker
{
    // ---------- Declarations ----------

    /// <summary>0: the main file's memory block is not evaluated yet, 1: being evaluated, 2: done.</summary>
    private int _memoryState;
    /// <summary>The memory block had an error (reported): placing a variable in one of its regions adds none.</summary>
    private bool _memoryFailed;

    /// <summary>
    /// Evaluates the main file's memory block, once: at the main file's turn, or before, when a constant
    /// worked out early names a main-file global placed in a region (<c>len(map)</c>, spec 4).
    /// </summary>
    private void EnsureMemoryLayout()
    {
        if (_memoryState == 2 || !_module.IsMain) return;
        if (_memoryState == 1)
            throw new CompileError(_module.Syntax.Memory!.Pos, "The memory block depends on itself: a constant it uses needs a variable placed in one of its regions. Give that constant a value that does not use the variable");
        _memoryState = 1;
        // After an error the default layout is kept, and the block is not evaluated again (spec 13).
        try { if (_module.Syntax.Memory is { } memory) _layout = EvaluateMemory(memory); }
        catch (Exception e) when (e is CompileError or FollowOnError) { _memoryFailed = true; throw; }
        finally { _memoryState = 2; }
    }

    private MemoryLayout EvaluateMemory(MemoryDecl memory)
    {
        long Value(Expr e)
        {
            CheckExpr(e);
            return e.ConstValue ?? throw new CompileError(e.Pos, "A memory range must be known at compile time. Use numbers and constants");
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
        // A name reported as not declared, used before this declaration, is now declared (spec 13).
        if (Lookup(name, demand: false) is { } existing and not ErrorSymbol)
            throw new CompileError(symbol.Pos, $"'{name}' is already declared at {existing.Pos}. Rename one of them");
        scope[name] = symbol;
    }

    /// <summary>
    /// Finds a name in the enclosing scopes and the current file, or <c>module.name</c> in an imported module.
    /// A <c>private</c> name of another module is not found.
    /// </summary>
    /// <param name="demand">Declare a constant that is not declared yet (<see cref="Demand"/>).</param>
    private Symbol? Lookup(string name, bool demand = true)
    {
        var found = Find();
        // A name whose declaration had an error, or an import that did: the error was reported (spec 13).
        if (demand && found is ErrorSymbol) throw new FollowOnError();
        return found;

        Symbol? Find()
        {
            int dot = name.IndexOf('.');
            if (dot > 0)
            {
                if (!_module.Imports.TryGetValue(name[..dot], out var module)) return _module.Scope.GetValueOrDefault(name[..dot]) as ErrorSymbol;
                string member = name[(dot + 1)..];
                if (module.Syntax.PrivateNames.Contains(member)) return null;
                return module.Scope.GetValueOrDefault(member) ?? (demand ? Demand(module, member) : null);
            }
            for (int i = _scopes.Count - 1; i >= 0; i--)
                if (_scopes[i].TryGetValue(name, out var s)) return s;
            return _module.Scope.GetValueOrDefault(name) ?? (demand ? Demand(_module, name) : null);
        }
    }

    /// <summary>The constants and globals being declared, innermost last, to find a value that depends on itself.</summary>
    private readonly List<(Module Module, string Name, Node Decl)> _declaring = [];
    private readonly HashSet<ConstDecl> _constsDone = [];

    /// <summary>
    /// A top-level name of <paramref name="module"/> used before its declaration's turn (spec 2.2, 4):
    /// a constant is declared now, in its own module. So is a global used while a constant or a global's
    /// type is worked out (<c>len(t)</c>); it is returned as soon as its type is known. Null if there is
    /// no such declaration.
    /// </summary>
    private Symbol? Demand(Module module, string name)
    {
        // A constant of the main file that a parameter value cannot use was tried already (ParameterScope).
        if (module == _parameterScope && _unusableConstants.ContainsKey(name)) return null;
        if (module.Syntax.Consts.FirstOrDefault(c => c.Name == name) is { } c)
        {
            InModule(module, () => DeclareConst(c));
            return module.Scope.GetValueOrDefault(name);
        }
        // Not in the copy of the main file that parameter values are checked in: its globals are not the program's.
        if (_declaring.Count > 0 && module != _parameterScope && module.Syntax.Globals.FirstOrDefault(g => g.Name == name) is { } g)
        {
            if (g.Symbol is { } partly) return partly;
            InModule(module, () => DeclareGlobal(g));
            return g.Symbol;
        }
        return null;
    }

    /// <summary>Runs <paramref name="work"/> as a top-level declaration of <paramref name="module"/>, then restores the state.</summary>
    private void InModule(Module module, Action work)
    {
        var (saved, function, longBytes, listLength) = (_module, _function, _allowLongBytes, _listLength);
        var scopes = _scopes.ToList();
        _scopes.Clear();
        (_module, _function, _allowLongBytes, _listLength) = (module, null, false, null);
        try { work(); }
        finally
        {
            (_module, _function, _allowLongBytes, _listLength) = (saved, function, longBytes, listLength);
            _scopes.Clear();
            _scopes.AddRange(scopes);
        }
    }

    /// <summary>Marks a constant or global as being declared; a second start is a value that depends on itself.</summary>
    private void StartDeclaring(string name, Node decl)
    {
        int first = _declaring.FindIndex(d => d.Decl == decl);
        if (first >= 0)
        {
            var chain = _declaring.Skip(first).Select(d => d.Module.Qualify(d.Name)).Append(_module.Qualify(name));
            throw new CompileError(decl.Pos,
                $"'{name}' depends on itself: {string.Join(" -> ", chain)}. Give one of them a value that does not use the others");
        }
        _declaring.Add((_module, name, decl));
    }

    /// <summary>The error for a name that is not declared. A module name on its own gets a hint.</summary>
    private CompileError NotDeclared(string name, SourcePos pos)
    {
        if (_module.Imports.ContainsKey(name))
            return new CompileError(pos, $"'{name}' is a module; use the names in it as {name}.name");
        int dot = name.IndexOf('.');
        if (dot <= 0) return new CompileError(pos, $"'{name}' is not declared. Check the spelling, or declare it") { Undeclared = name };
        // s.a() where s is declared: a field or member, not a function in a module.
        switch (Lookup(name[..dot]))
        {
            case VariableSymbol { Type: var t } when t.IsStruct || t is { IsPointer: true, Element.IsStruct: true }:
                return new CompileError(pos, $"'{name}' is a field, not a function; only functions can be called. Remove the ()");
            case { } s:
                return new CompileError(pos, $"'{name}' is not a function: '{name[..dot]}' is a {KindName(s)}, not a module. Check the spelling of the module name");
        }
        if (!_module.Imports.TryGetValue(name[..dot], out var other))
            return MissingImport(name[..dot], pos);
        if (other.Syntax.PrivateNames.Contains(name[(dot + 1)..]))
            return new CompileError(pos, $"'{name}' is private to the module {other.Name}; only code in {other.Name} can use it. Remove 'private' there, or use another of its functions");
        // Modules that import each other are declared one after the other, so a declaration
        // in the first one cannot use the second one's names yet.
        if (!other.Declared)
        {
            string here = _module.IsMain ? "the main file" : _module.Name;
            return new CompileError(pos,
                $"'{name}' cannot be used in a declaration of {here}: {other.Name} and {here} import each other, and {other.Name} is declared after {here}. " +
                $"Move {name[(dot + 1)..]} to {here}, or to a module both import");
        }
        return new CompileError(pos, $"'{name}' is not declared. Check the spelling, or declare it in {other.Name}");
    }

    private static string KindName(Symbol s) => s switch
    {
        VariableSymbol => "variable",
        ConstSymbol => "constant",
        FunctionSymbol => "function",
        ConstFnSymbol => "const fn",
        _ => "struct type",
    };

    /// <summary><c>text.put</c> where <c>text</c> is not declared: most likely a missing import.</summary>
    private static CompileError MissingImport(string module, SourcePos pos) =>
        new(pos, $"'{module}' is not declared. Add 'import {module}' at the top of the file to use the module {module}") { Undeclared = module };

    /// <summary>Reports <c>name.member</c> where <c>name</c> is not declared as a missing import.</summary>
    private void CheckMemberTarget(MemberExpr m)
    {
        if (m.Member is not ("lo" or "hi") && m.Target is NameExpr { Bound: null } n && !n.Name.Contains('.') && Lookup(n.Name) is null && !_module.Imports.ContainsKey(n.Name))
            throw MissingImport(n.Name, n.Pos);
    }

    /// <summary>Records that the function being checked uses a global, so it is kept in the program.</summary>
    private void UseGlobal(VariableSymbol v, SourcePos pos)
    {
        CheckNotBinaryData(v, pos);
        if (v.Kind != VariableKind.Global || _function is null) return;
        // Overlays in one area share their memory: another one's variables are not there while this one runs.
        if (_function.OverlayArea is { } area && v.OverlayArea == area && v.Module != _function.Module && (v.FixedAddress is null || v.IsData) && v.Region is null)
            throw new CompileError(pos,
                $"'{v.Name}' belongs to {v.Module!.Name}, which is in the same overlay area '{area}' as {_function.Module!.Name}. They are never loaded together and share their memory. " +
                "Keep shared state in a resident module or the main file");
        if (_dead) return;
        _function.UsedGlobals.Add(v);
        // A table of function addresses keeps those functions in the program, also through a table it names.
        foreach (var f in GlobalsNamedBy(v.Storage).SelectMany(g => g.FunctionAddresses)) _function.UsedAddresses.Add(f);
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
            throw new CompileError(header.Pos, "The main file has no module header; 'module' is only for modules that have parameters. Remove the header");
        if (header is not null && header.Name != module.Name)
            throw new CompileError(header.Pos, $"The module header says '{header.Name}' but the file is {module.Name}.tern; the name must match the file. Rename one of them");
        var parameters = header?.Parameters ?? [];
        if (import is not null)
        {
            var seen = new HashSet<string>();
            foreach (var arg in import.Args)
            {
                if (parameters.All(p => p.Name != arg.Name))
                    throw new CompileError(arg.Pos, header is null
                        ? $"Module '{module.Name}' has no parameters (it has no 'module {module.Name}(...)' header). Remove the arguments from the import"
                        : $"Module '{module.Name}' has no parameter '{arg.Name}'; its parameters are {string.Join(", ", parameters.Select(p => p.Name))}. Use one of those names");
                if (!seen.Add(arg.Name))
                    throw new CompileError(arg.Pos, $"Parameter '{arg.Name}' is set twice. Remove one");
            }
        }
        var saved = _module;
        foreach (var p in parameters)
        {
            var type = ResolveType(p.Type);
            if (!(type.IsInteger || type.Kind == TypeKind.Bool))
                throw new CompileError(p.Type.Pos, "A module parameter is an integer type or bool. Use u8, i8, u16, i16 or bool");
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
                        throw new CompileError(e.Pos, $"'{m.Groups[1].Value}' cannot be used in a parameter value, since it does not check without the rest of the program: {why}. Write the value as a number");
                    throw new CompileError(e.Pos, e.Description + ". A parameter value uses literals and the main file's constants that do not depend on imported modules. Write the value as a number");
                }
                finally { _module = saved; }
            }
            else
                CheckExpr(value);
            if (value.ConstValue is not long v)
                throw new CompileError(value.Pos, $"The value of parameter '{p.Name}' is not known at compile time. Use numbers and constants");
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
        _parameterScope = scope;
        var saved = _module;
        _module = scope;
        // Structs first, so that sizeof works. One that needs an import is left out.
        foreach (var st in scope.Syntax.Structs)
        {
            try { DeclareStruct(st); LayOut((StructSymbol)scope.Scope[st.Name]); }
            catch (CompileError) { scope.Scope.Remove(st.Name); }
        }
        foreach (var f in scope.Syntax.ConstFns)
        {
            try { DeclareConstFn(f); }
            catch (CompileError) { }
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
            throw new CompileError(a.Condition.Pos, "An assert condition is a bool. Write a comparison, e.g. COUNT <= 64");
        if (a.Condition.ConstValue is not long v)
            throw new CompileError(a.Condition.Pos, "An assert condition must be known at compile time. Use constants, sizeof and len");
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
        if (_constsDone.Contains(decl)) return;
        StartDeclaring(decl.Name, decl);
        _constValues++;
        try { DeclareConstValue(decl); }
        finally { _declaring.RemoveAt(_declaring.Count - 1); _constValues--; }
        _constsDone.Add(decl);
    }

    private void DeclareConstValue(ConstDecl decl)
    {
        if (decl.Value is StructLiteralExpr lit)
            throw new CompileError(lit.Pos, $"A constant cannot be a struct. Declare it as data: 'data {decl.Name} = {lit.StructName} {{ ... }}'");
        if (DeclaredRealConst(decl)) return;
        CheckExpr(decl.Value);
        if (decl.Value.ConstValue is not long value)
            throw new CompileError(decl.Value.Pos, $"The value of '{decl.Name}' is not known at compile time. Use numbers and constants, or a var for a value computed at run time");
        var type = decl.Value.Type;
        if (decl.TypeSyntax is not null)
        {
            type = ResolveType(decl.TypeSyntax);
            if (!type.IsScalar) throw new CompileError(decl.Pos, "Constants must be numbers or bool. Use data for an array or a string");
            Coerce(decl.Value, type);
        }
        var names = new HashSet<string>();
        CollectNames(decl.Value, names);
        Declare(new ConstSymbol(decl.Name, decl.Pos, type, decl.Value.ConstValue!.Value)
        {
            // A constant has a type only when its declaration writes one (spec 3.1).
            IsBitPattern = type.Kind == TypeKind.IntLiteral && decl.Value.IsBitPattern,
            UsedParameters = names.SelectMany(n => _module.Scope.GetValueOrDefault(n) is ConstSymbol c ? c.UsedParameters : []).ToHashSet(),
        });
    }

    private void DeclareFunction(FnDecl decl)
    {
        if (decl.Name is "addr" or "len" or "sizeof" or "offset" or "include_bytes")
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
                        ? "Fixed inputs are only for routines at a fixed address. An asm fn sets up its inputs in its own code. Remove it"
                        : "Fixed inputs are only for routines at a fixed address (fn name(...) @ $addr). Give the parameter a name and a type: 'name: type'");
                continue;
            }
            var type = ResolveType(p.Type);
            if (type.IsStruct)
                throw new CompileError(p.Pos, $"A struct cannot be passed to a function, since structs are never copied. Pass a pointer (ptr {type}) or an index");
            if (!type.IsScalar) throw new CompileError(p.Pos, "Parameters must be numbers or bool. Pass a pointer to an array: ptr u8");
            var param = new VariableSymbol(p.Name, p.Pos, type, VariableKind.Parameter, fn);
            if (fn.Parameters.Any(x => x.Name == p.Name))
                throw new CompileError(p.Pos, $"Parameter '{p.Name}' is declared twice. Rename one of them");
            if (p.UsingRegister is { } reg)
            {
                CheckRegisterFits(reg, type, p.Pos, allowFlags: false);
                foreach (char r in reg.ToString().Where(char.IsUpper))
                    if (!usedRegisters.Add(r))
                        throw new CompileError(p.Pos, $"Register {r} is used by more than one parameter. Use another register for one of them");
                param.PassedIn = reg;
                // An asm fn or a fixed-address routine uses register parameters where they arrive; they have no memory.
                if (decl.IsAsm || decl.Address is not null) param.Register = reg;
            }
            else if (decl.IsAsm && decl.IsInline)
                throw new CompileError(p.Pos,
                    $"Parameter '{p.Name}' of an inline asm fn must be passed in a register (using A, X, Y, AX, AY or XY): it is copied into each caller, which has no memory for it. Add 'using A', 'using X' or 'using Y'");
            else if (decl.Address is not null)
                throw new CompileError(p.Pos,
                    $"Parameter '{p.Name}' must be passed in a register (using A, X, Y, AX, AY or XY): a routine at a fixed address has no memory for parameters. Add 'using A', 'using X' or 'using Y'");
            fn.Parameters.Add(param);
            if (param.InMemory) fn.FrameVariables.Add(param);
        }

        if (decl.ReturnType is not null)
        {
            fn.ReturnType = ResolveType(decl.ReturnType);
            if (fn.ReturnType.IsStruct)
                throw new CompileError(decl.ReturnType.Pos, $"A function cannot return a struct, since structs are never copied. Return a pointer (ptr {fn.ReturnType}) or an index");
            if (!fn.ReturnType.IsScalar) throw new CompileError(decl.ReturnType.Pos, "Functions can only return numbers or bool. Return a pointer to an array: ptr u8");
            fn.ReturnRegister = decl.ReturnUsing ?? fn.ReturnType.Kind switch
            {
                TypeKind.Bool => Register.Carry,
                TypeKind.U16 or TypeKind.I16 or TypeKind.Ptr => Register.AX,
                _ => Register.A,
            };
            CheckRegisterFits(fn.ReturnRegister.Value, fn.ReturnType, decl.ReturnType.Pos, allowFlags: true);
            if (fn.ReturnRegister == Register.Overflow && !decl.IsAsm && decl.Address is null)
                throw new CompileError(decl.ReturnType.Pos,
                    "Only an asm fn, an inline asm fn or a routine at a fixed address can return a bool using overflow: no instruction sets the V flag, so a fn cannot 'return true' in it. Return it using carry, zero or negative");
        }
        else if (decl.ReturnUsing is not null)
            throw new CompileError(decl.Pos, "'using' needs a return type. Write it before 'using': -> u8 using A");

        Declare(fn, decl.Name);
    }

    /// <summary>A constant passed on every call of a routine at a fixed address: <c>true using carry</c>, <c>3 using X</c>.</summary>
    private void DeclareFixedInput(FunctionSymbol fn, ParamSyntax p, HashSet<char> usedRegisters)
    {
        var reg = p.UsingRegister!.Value;
        var value = p.FixedValue!;
        CheckExpr(value);
        if (value.ConstValue is not long v)
            throw new CompileError(value.Pos, "A fixed input must be a constant. Use a number, true, false or a const");
        switch (reg)
        {
            case Register.Carry:
                if (value.Type.Kind != TypeKind.Bool)
                    throw new CompileError(value.Pos, "The carry takes true or false. Write 'true using carry' or 'false using carry'");
                break;
            case Register.A or Register.X or Register.Y:
                Coerce(value, TernType.U8);
                break;
            default:
                throw new CompileError(p.Pos, $"A fixed input goes in A, X, Y or the carry, not {reg}. Use one of those");
        }
        char key = reg == Register.Carry ? 'c' : reg.ToString()[0];
        if (!usedRegisters.Add(key))
            throw new CompileError(p.Pos, reg == Register.Carry ? "The carry is set more than once. Remove one" : $"Register {reg} is used by more than one parameter. Use another register for one of them");
        fn.FixedInputs.Add((reg, v));
    }

    /// <summary>A routine that already exists at a fixed address: the address and the registers it keeps.</summary>
    private void DeclareRoutine(FnDecl decl, FunctionSymbol fn)
    {
        CheckExpr(decl.Address!);
        if (decl.Address!.ConstValue is not long a)
            throw new CompileError(decl.Address.Pos, "The address must be known at compile time. Use a number or a constant");
        if (a < 0 || a > 0xFFFF)
            throw new CompileError(decl.Address.Pos, $"Address ${a:X} is outside memory. Use an address from $0000 to $FFFF");
        fn.FixedAddress = (int)a;

        var usedRegisters = fn.Parameters.SelectMany(p => p.PassedIn!.Value.ToString().Where(char.IsUpper)).ToHashSet();
        foreach (var p in decl.Parameters.Where(p => p.FixedValue is not null))
            DeclareFixedInput(fn, p, usedRegisters);

        foreach (var (reg, pos) in decl.Keeps)
        {
            if (reg is not (Register.A or Register.X or Register.Y))
                throw new CompileError(pos, $"'keeps' lists the registers A, X and Y, not {reg}. Flags always count as changed. Remove {reg}");
            if (!fn.Keeps.Add(reg))
                throw new CompileError(pos, $"{reg} is listed twice after 'keeps'. Remove one");
            if (fn.ReturnRegister is { } ret && ret.Uses(reg))
                throw new CompileError(pos, $"'{fn.Name}' returns a value in {reg}, so it cannot keep {reg}. Remove {reg} from 'keeps'");
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
        if (!ok) throw new CompileError(pos, $"A {type} value cannot be passed using {reg}. Use {(type.Size == 2 ? "AX, AY or XY" : "A, X or Y")}");
    }

    /// <summary>Set while a global's type is resolved: a byte array there may be longer than 256 (spec 3.2).</summary>
    private bool _allowLongBytes;
    /// <summary>Set while a global's type is resolved: the length of its list initial value, for <c>[T; _]</c> (spec 4).</summary>
    private int? _listLength;
    /// <summary>Globals of the file being declared whose initial values hold addresses (spec 4), worked out after its last global.</summary>
    private readonly List<(VarDecl Decl, VariableSymbol Variable, Module Module)> _addressInits = [];

    private void DeclareGlobal(VarDecl decl)
    {
        StartDeclaring(decl.Name, decl);
        try { DeclareGlobalValue(decl); }
        catch
        {
            decl.Symbol = null;   // not declared: tried again at its module's turn, to break its name (spec 13)
            throw;
        }
        finally { _declaring.RemoveAt(_declaring.Count - 1); }
    }

    private void DeclareGlobalValue(VarDecl decl)
    {
        // A string or include_bytes() gives a [u8; N] array, which may be longer than 256 bytes.
        var bytes = decl.Init is null ? null : ByteSource(decl.Init);
        _allowLongBytes = !decl.IsLinear && !decl.IsIndexed;
        // [T; _] takes its length from a list or a string (spec 4).
        _listLength = decl is { TypeSyntax: ArrayTypeSyntax { Length: null } } ? decl.Init switch
        {
            ArrayLiteralExpr list => list.Elements.Count,
            StringExpr => bytes!.Length,
            { } other when IsCompileTimeArray(other) => CompileTimeArray(other).Items.Length,
            _ => null,
        } : null;
        TernType type;
        try { type = bytes is not null && decl.TypeSyntax is null ? TernType.ArrayOf(TernType.U8, bytes.Length) : DeclaredType(decl); }
        finally { _allowLongBytes = false; _listLength = null; }
        if (!decl.IsData && decl.TypeSyntax is not null && decl.Init is not null && type.IsArray && type.Length > 256)
            throw new CompileError(decl.Init.Pos,
                $"'{decl.Name}' is a long byte array ({type.Length} bytes), which is zeroed at startup and has no initial value. Fill it at run time, or declare it with 'data'");
        var v = new VariableSymbol(_module.Qualify(decl.Name), decl.Pos, type, VariableKind.Global, null)
        {
            IsData = decl.IsData, NoInit = decl.IsNoinit, DeclaredVolatile = decl.IsVolatile, IsIndexed = decl.IsIndexed,
        };
        decl.Symbol = v;
        v.Module = _module;
        ApplyPlacement(decl, v, allowAddress: true);
        if (decl.IsNoinit && decl.Init is not null)
            throw new CompileError(decl.Init.Pos, $"'{decl.Name}' is noinit, so it cannot have an initial value. Leave out one or the other");
        if (decl.IsNoinit && v.FixedAddress is not null)
            throw new CompileError(decl.Pos, $"'{decl.Name}' is at a fixed address, which is never zeroed; leave out 'noinit'");

        if (decl.IsData && !type.IsArray && !type.IsStruct)
            throw new CompileError(decl.Pos, "Data must be an array, a string or a struct. Use 'const' for a single value");
        if (decl.Init is not null)
        {
            if (v.FixedAddress is not null && !decl.IsData)
                throw new CompileError(decl.Init.Pos, "A variable at a fixed address cannot have an initial value. Assign it at the start of main, or use data");
            if (v.Region is not null && !decl.IsData)
                throw new CompileError(decl.Init.Pos, "A variable in a region cannot have an initial value: the region is not part of the program file. Assign it at the start of main");
            if (bytes is not null)
            {
                if (!type.IsArray || type.Element!.Kind != TypeKind.U8)
                    throw new CompileError(decl.Init.Pos, $"This value is a [u8; {bytes.Length}] array, but '{decl.Name}' is {type}. Change the type to [u8; {bytes.Length}]");
                if (type.Length != bytes.Length)
                    throw new CompileError(decl.Init.Pos, $"Expected {type.Length} bytes, found {bytes.Length}. Change the length to {bytes.Length}, or write '_' to take it from the value");
                v.InitialBytes = bytes;
            }
            else if (UsesAddr(decl.Init)) _addressInits.Add((decl, v, _module));
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
                if (s.Text.Length == 0) throw new CompileError(s.Pos, "A string must have at least one character. Write at least one");
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
        throw new CompileError(pos, $"Unknown encoding '{prefix}'. Use one of: {string.Join(", ", CharEncoding.ByName.Keys)}");
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
            throw new CompileError(call.Pos, "include_bytes() takes a file name in quotes. Write include_bytes(\"file.bin\")");
        var path = Path.Combine(_module.Directory, file.Text);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new CompileError(file.Pos, $"Cannot read '{file.Text}' ({path}): {e.Message}. Check the file name, relative to the source file");
        }
        if (bytes.Length == 0) throw new CompileError(file.Pos, $"'{file.Text}' is empty. Use a file with at least one byte");
        return bytes;
    }

    /// <summary>The variable's type: written out, or taken from the initializer.</summary>
    private TernType DeclaredType(VarDecl decl)
    {
        if (decl.IsIndexed)
        {
            var t = decl.TypeSyntax is null ? null : ResolveType(decl.TypeSyntax);
            if (t is null || !t.IsStructArray)
                throw new CompileError(decl.Pos,
                    "'indexed' is for arrays of structs, e.g. 'indexed var list: [Enemy; 20]'. Arrays of numbers are indexed directly. Remove 'indexed'");
            if (t.Size > 256)
                throw new CompileError(decl.Pos,
                    $"An indexed array is at most 256 bytes, so that every byte is reachable with an 8-bit offset; '{decl.Name}' is {t.Size} ({t.Length} records of {t.Element!.Size}). " +
                    "Use fewer records, or step through it with a pointer");
            return TernType.ArrayOf(t.Element!, t.Length, linear: true);
        }
        if (decl.IsLinear)
        {
            var t = decl.TypeSyntax is null ? null : ResolveType(decl.TypeSyntax);
            if (t is null || !t.IsArray)
                throw new CompileError(decl.Pos,
                    "'linear' is for arrays of 16-bit values or structs, e.g. 'linear var pts: [u16; 32]' or 'linear var list: [Enemy; 8]'. Remove 'linear'");
            if (t.Element!.Size == 1 && !t.Element.IsStruct)
                throw new CompileError(decl.Pos, $"'linear' is only for arrays of 16-bit values or structs: the elements of a {t} are one byte each, so they are already stored in order. Remove 'linear'");
            if (!t.Element.IsStruct && t.Length > 128)
                throw new CompileError(decl.Pos,
                    $"A linear array of 16-bit values has at most 128 elements, so that the doubled index fits in a register; '{decl.Name}' has {t.Length}. Leave out 'linear' to store it split");
            return TernType.ArrayOf(t.Element, t.Length, linear: true);
        }
        if (decl.TypeSyntax is not null) return ResolveType(decl.TypeSyntax);
        if (decl.Init is null) throw new CompileError(decl.Pos, $"'{decl.Name}' needs a type or an initial value. Write one: var {decl.Name}: u8");
        if (decl.Init is ArrayLiteralExpr || IsCompileTimeArray(decl.Init))
            throw new CompileError(decl.Pos, decl.IsData
                ? $"Data arrays need a written type. Write data {decl.Name}: [u8; _] = [...], where '_' takes the length from the list"
                : $"Array variables need a written type. Write var {decl.Name}: [u8; _] = [...], where '_' takes the length from the list");
        if (decl.Init is StructLiteralExpr lit) return ResolveType(new NamedTypeSyntax(lit.Pos, lit.StructName));
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
                if (!allowAddress) throw new CompileError(decl.Pos, "Only global variables can have a fixed address. Move the variable out of the function");
                var addr = decl.Placement.Address!;
                // Only a region name needs the memory block; a constant address does not.
                if (addr is NameExpr placed && _module.Syntax.Memory?.Lines.Any(l => l.Kind == "region" && l.Name == placed.Name) == true)
{
                    EnsureMemoryLayout();
                    if (_memoryFailed) throw new FollowOnError();
                }
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
                    throw new CompileError(addr.Pos, "The address must be known at compile time. Use a number or a constant");
                if (a < 0 || a + v.Type.Size > 0x10000)
                    throw new CompileError(addr.Pos, $"Address ${a:X} is outside memory. Use an address up to $FFFF, less the variable's size");
                v.FixedAddress = (int)a;
                v.InVolatileArea = _platform.IsVolatile((int)a, (int)a + v.Type.Size - 1);
                break;
        }
    }

    /// <param name="target">Collects the function addresses (<c>addr(f)</c>) in the value.</param>
    private byte[] ConstantBytes(Expr init, TernType type, VariableSymbol target, int at = 0)
    {
        if (type.IsStruct) return StructBytes(init, type, target, at);
        if (type.IsArray)
        {
            if (init is not ArrayLiteralExpr list)
            {
                if (IsCompileTimeArray(init)) return ArrayBytes(CompileTimeArray(init), type, init.Pos);
                throw new CompileError(init.Pos, "An array must be initialized with [ ... ]. Write the elements in brackets");
            }
            if (list.Elements.Count != type.Length)
                throw new CompileError(init.Pos, $"Expected {type.Length} values, found {list.Elements.Count}. Change the length to {list.Elements.Count}, or write '_' to take it from the list");
            var bytes = new List<byte>();
            foreach (var e in list.Elements) bytes.AddRange(ConstantBytes(e, type.Element!, target, at + bytes.Count));
            return bytes.ToArray();
        }

        if (init is StructLiteralExpr lit) throw LiteralForScalar(lit, type, null);
        if (UsesAddr(init)) return AddressBytes(init, type, target, at);
        CheckExpr(init);
        Coerce(init, type);
        if (init.ConstValue is not long value)
            throw new CompileError(init.Pos, "A global's initial value must be known at compile time. Assign it at the start of main instead");
        return type.Size == 1 ? [(byte)value] : [(byte)value, (byte)(value >> 8)];
    }

    /// <summary>A struct literal where a number, bool or pointer is expected; <paramref name="field"/> names the field, if any.</summary>
    private static CompileError LiteralForScalar(StructLiteralExpr lit, TernType type, string? field)
    {
        if (!type.IsPointer) return new CompileError(lit.Pos, $"Expected {type}, found a {lit.StructName} literal. Use a struct literal only for a struct");
        string what = field is null ? "This value" : $"'{field}'";
        string write = field is null ? "addr(name)" : $"{field} = addr(name)";
        return new CompileError(lit.Pos,
            $"{what} is {type}, an address, not a {lit.StructName}. Declare the record as data and write '{write}'");
    }

    /// <summary>
    /// The bytes of a struct literal (spec 3.3), in the record's memory layout; fields not listed are 0.
    /// <paramref name="at"/> is the record's first byte in the global's value, for addresses filled in later.
    /// </summary>
    private byte[] StructBytes(Expr init, TernType type, VariableSymbol target, int at)
    {
        var s = type.Struct!;
        var named = s.Fields.Where(f => f.Name != "_").Select(f => f.Name).ToList();
        if (init is not StructLiteralExpr lit)
            throw new CompileError(init.Pos,
                $"A {s.Name} is initialized with a struct literal. Write '{s.Name} {{ {named.FirstOrDefault() ?? "field"} = ... }}'");
        var written = ResolveType(new NamedTypeSyntax(lit.Pos, lit.StructName));
        if (!written.IsStruct)
            throw new CompileError(lit.Pos, $"'{lit.StructName}' is not a struct, so '{lit.StructName} {{ ... }}' is not a struct literal. Check the spelling of the struct name");
        if (written.Struct != s)
            throw new CompileError(lit.Pos, $"This is a {written.Struct!.Name} literal, but the value here is a {s.Name}. Write '{s.Name} {{ ... }}'");
        var bytes = new byte[s.Size];
        var given = new HashSet<string>();
        foreach (var f in lit.Fields)
        {
            if (f.Name == "_")
                throw new CompileError(f.Pos, $"'_' is padding, which has no value; fields not listed are 0. Remove '_ = ...'");
            var field = s.Field(f.Name)
                ?? throw new CompileError(f.Pos, $"'{s.Name}' has no field '{f.Name}'. Use one of its fields: {string.Join(", ", named)}");
            if (!given.Add(f.Name))
                throw new CompileError(f.Pos, $"'{f.Name}' is given twice in this {s.Name} literal. Remove one");
            FieldBytes(f.Value, field, target, bytes, at);
        }
        return bytes;
    }

    /// <summary>
    /// One field's value into <paramref name="record"/>: a nested literal, a list or string for an array
    /// field (an array of 16-bit values split into low and high bytes inside the record, spec 3.3), or a
    /// value as any initial value of the field's type allows.
    /// </summary>
    private void FieldBytes(Expr value, StructField field, VariableSymbol target, byte[] record, int at)
    {
        var type = field.Type;
        int offset = field.Offset;
        if (type.IsStruct)
        {
            StructBytes(value, type, target, at + offset).CopyTo(record, offset);
            return;
        }
        if (!type.IsArray)
        {
            if (value is StructLiteralExpr lit) throw LiteralForScalar(lit, type, field.Name);
            ScalarInto(value, type, target, record, offset, type.Size == 2 ? offset + 1 : null, at);
            return;
        }
        int n = type.Length;
        var element = type.Element!;
        if (ByteSource(value) is { } text)
        {
            if (element.Size != 1 || !element.IsInteger)
                throw new CompileError(value.Pos, $"A string is a [u8; {text.Length}] array, but the field '{field.Name}' is {type}. Change the field to [u8; {text.Length}]");
            if (text.Length != n)
                throw new CompileError(value.Pos, $"The field '{field.Name}' is {type}: expected {n} bytes, found {text.Length}. Write a string of {n} characters");
            text.CopyTo(record, offset);
            return;
        }
        if (value is not ArrayLiteralExpr list)
            throw new CompileError(value.Pos, $"The field '{field.Name}' is {type}, so its value is a list. Write [ ... ] with {n} values");
        if (list.Elements.Count != n)
            throw new CompileError(value.Pos, $"The field '{field.Name}' is {type}: expected {n} values, found {list.Elements.Count}. Write {n} values");
        for (int i = 0; i < n; i++)
            ScalarInto(list.Elements[i], element, target, record, offset + i, element.Size == 2 ? offset + n + i : null, at);
    }

    /// <summary>A number, bool or pointer into <paramref name="record"/>: the low byte at <paramref name="lo"/>, the high byte (if any) at <paramref name="hi"/>.</summary>
    private void ScalarInto(Expr e, TernType type, VariableSymbol target, byte[] record, int lo, int? hi, int at)
    {
        if (e is StructLiteralExpr lit) throw LiteralForScalar(lit, type, null);
        if (UsesAddr(e))
        {
            AddressBytes(e, type, target, at + lo, hi is { } h ? at + h : null);
            return;
        }
        var bytes = ConstantBytes(e, type, target);
        record[lo] = bytes[0];
        if (hi is { } high) record[high] = bytes[1];
    }

    /// <summary>The error for a struct literal outside an initial value of a global or data (spec 3.3).</summary>
    private static CompileError StructLiteralInCode(StructLiteralExpr lit, string? name)
    {
        string example = lit.Fields.FirstOrDefault() is { } f
            ? $"{name ?? "s"}.{f.Name} = {Describe(f.Value) ?? "..."}"
            : $"{name ?? "s"}.field = ...";
        return new CompileError(lit.Pos,
            "A struct literal is only the initial value of a global or of data: structs are never copied, so here it would be stores at run time. "
            + $"Assign the fields one by one, e.g. '{example}'");
    }

    /// <summary>
    /// An address in an initial value (spec 4): <c>addr()</c> of a function, global, data or field of a
    /// struct global, alone, plus or minus a constant, or its <c>.lo</c> / <c>.hi</c>. Filled in when the
    /// program is placed; the bytes are zeros until then.
    /// </summary>
    /// <param name="highAt">Where the high byte of a whole address goes, when not right after the low byte (a 16-bit array field, split inside its record).</param>
    private byte[] AddressBytes(Expr init, TernType type, VariableSymbol target, int at, int? highAt = null)
    {
        var part = AddressPart.Word;
        var e = init;
        if (init is MemberExpr { Member: "lo" or "hi" } half)
        {
            part = half.Member == "lo" ? AddressPart.Low : AddressPart.High;
            e = half.Target;
            if (type.Kind != TypeKind.U8)
                throw new CompileError(init.Pos, $"'.{half.Member}' of an address is one byte, a u8, but this value is {type}. Use a [u8; n] table, or the whole address");
        }
        CheckExpr(e);
        if (part == AddressPart.Word) Coerce(e, type);
        if (Placed(e) is not var (function, variable, offset))
            throw new CompileError(init.Pos,
                "An address in an initial value is filled in when the program is placed, so it can only be used alone, plus or minus a constant, or as .lo / .hi. Assign other values at the start of main");
        // Overlays in one area share their memory: the other one's code or data is not there while this one is loaded (spec 8.5).
        var (otherArea, otherModule, name) = function is not null ? (function.OverlayArea, function.Module, function.Name) : (variable!.OverlayArea, variable.Module, variable.QualifiedName);
        if (target.OverlayArea is { } area && otherArea == area && otherModule != target.Module && (variable is null || variable.FixedAddress is null || variable.IsData))
            throw new CompileError(init.Pos,
                $"'{name}' is in {otherModule!.Name}, which is in the same overlay area '{area}' as {target.Module!.Name}. They are never loaded together, so the address would point at the wrong memory. " +
                "Take the address in resident code, or put one of them in another area");
        var (fillVariable, fillOffset) = (variable?.AliasOf ?? variable, (int)offset + (variable?.AliasOffset ?? 0));
        if (part == AddressPart.Word && highAt is { } h && h != at + 1)
        {
            target.AddressFills.Add(new AddressFill(at, function, fillVariable, fillOffset, AddressPart.Low));
            target.AddressFills.Add(new AddressFill(h, function, fillVariable, fillOffset, AddressPart.High));
        }
        else target.AddressFills.Add(new AddressFill(at, function, fillVariable, fillOffset, part));
        return new byte[part == AddressPart.Word ? 2 : 1];
    }

    /// <summary>What an address in an initial value names, with its constant offset; null for any other use of an address.</summary>
    private static (FunctionSymbol? Function, VariableSymbol? Variable, long Offset)? Placed(Expr e)
    {
        switch (e)
        {
            case CallExpr { FunctionAddress: { } f }: return (f, null, 0);
            case CallExpr { AddressOf: { } v }: return (null, v, 0);
            case CastExpr { Type.Size: 2 } c: return Placed(c.Operand);
            case BinaryExpr { Op: TokenKind.Plus } b when b.Right.ConstValue is long r && Placed(b.Left) is var (f1, v1, o1): return (f1, v1, o1 + r);
            case BinaryExpr { Op: TokenKind.Plus } b when b.Left.ConstValue is long l && Placed(b.Right) is var (f2, v2, o2): return (f2, v2, o2 + l);
            case BinaryExpr { Op: TokenKind.Minus } b when b.Right.ConstValue is long r && Placed(b.Left) is var (f3, v3, o3): return (f3, v3, o3 - r);
            default: return null;
        }
    }

    /// <summary>Whether the expression has an <c>addr()</c> in it, before it is checked.</summary>
    private static bool UsesAddr(Expr e) => e switch
    {
        CallExpr c => c.Name == "addr" || c.Args.Any(UsesAddr),
        UnaryExpr u => UsesAddr(u.Operand),
        BinaryExpr b => UsesAddr(b.Left) || UsesAddr(b.Right),
        CastExpr c => UsesAddr(c.Operand),
        MemberExpr m => UsesAddr(m.Target),
        ArrayLiteralExpr a => a.Elements.Any(UsesAddr),
        StructLiteralExpr s => s.Fields.Any(f => UsesAddr(f.Value)),
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
                    case "ptr": throw new CompileError(n.Pos, "'ptr' needs the type it points to. Write e.g. 'ptr u8'");
                }
                switch (Lookup(n.Name))
                {
                    case StructSymbol s:
                        if (layOut) LayOut(s);
                        return s.Type;
                    case null when n.Name.Contains('.'):
                        throw NotDeclared(n.Name, n.Pos);
                    case null:
                        throw new CompileError(n.Pos, $"Unknown type '{n.Name}'. Check the spelling, or declare a struct of that name");
                    default:
                        throw new CompileError(n.Pos, $"'{n.Name}' is not a type. Use a number type, bool, ptr, an array or a struct");
                }
            case PointerTypeSyntax p:
                var target = ResolveType(p.Target, layOut: false);
                if (!target.IsScalar && !target.IsStruct) throw new CompileError(p.Pos, "A pointer must point to a number, bool, pointer or struct. Use the array's element type: ptr u8");
                return TernType.PointerTo(target);
            case ArrayTypeSyntax a:
                var element = ResolveType(a.Element);
                if (!element.IsScalar && !element.IsStruct) throw new CompileError(a.Pos, "Array elements must be numbers, bool, pointers or structs. Use one array per row, or a longer array");
                long len;
                if (a.Length is null)
                {
                    len = _listLength ?? throw new CompileError(a.Pos,
                        "The length '_' is taken from a list or string initial value, a list built with 'for' or a const fn's array, so it works only on a global var or data with one, as in 'data t: [u8; _] = [1, 2, 3]'. Write the length here");
                    _listLength = null;
                }
                else
                {
                    CheckExpr(a.Length);
                    len = a.Length.ConstValue ?? throw new CompileError(a.Length.Pos, "Array length must be known at compile time. Use a number or a constant");
                }
                if (len < 1 || len > 256 && !(_allowLongBytes && element.Size == 1 && element.IsInteger))
                    throw new CompileError(a.Length?.Pos ?? a.Pos, len > 256
                        ? $"An array of {element} has at most 256 elements. Only a global var or data of u8 or i8 may be longer (spec 3.2), and it is used through a pointer. Split it into arrays of at most 256"
                        : $"Array length must be 1 to 256, not {len}. Use a length in that range");
                if (len > 0x10000)
                    throw new CompileError(a.Length?.Pos ?? a.Pos, $"An array of {element} has at most 65536 elements, all of memory, not {len}. Use a smaller length");
                return TernType.ArrayOf(element, (int)len);
            default:
                throw new InvalidOperationException();
        }
    }
}
