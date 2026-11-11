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
    /// <summary>For <c>export level_api</c>: each export's routine in the interface module, which gives its address.</summary>
    public Dictionary<FunctionSymbol, FunctionSymbol> ExportedAs { get; init; } = [];
    /// <summary>The address of a program's export table (<c>export f, g @ $0900</c>), or null.</summary>
    public int? ExportTableAddress { get; init; }
    /// <summary>A binary without <c>main</c>, which starts with its jump table.</summary>
    public bool IsBinary => Exports.Count > 0 && ExportTableAddress is null;
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
        // Const fns first: declaring one needs no other declaration, and constants of any file may call them (spec 4.1).
        foreach (var module in modules)
        {
            _module = module;
            foreach (var f in module.Syntax.ConstFns) DeclareConstFn(f);
        }

        // Struct names and module parameters of every file first, so that a constant can be worked out
        // when it is first used, in any module, also across an import ring (spec 2.2, 4).
        foreach (var module in modules)
        {
            _module = module;
            var program = module.Syntax;
            if (!module.IsMain && program.Functions.FirstOrDefault(f => f.Name == "main") is { } wrongMain)
                throw new CompileError(wrongMain.Pos, $"Only the program's main file has 'fn main()'; '{module.Name}' is a module");
            foreach (var s in program.Structs) DeclareStruct(s);
            DeclareParameters(module, modules[^1]);
        }

        // Declarations file by file. A constant (or, for len(), a global) that another declaration
        // uses before its turn is declared then (Demand).
        foreach (var module in modules)
        {
            _module = module;
            var program = module.Syntax;
            foreach (var c in program.Consts) DeclareConst(c);
            if (!module.IsMain && program.Memory is { } wrongMemory)
                throw new CompileError(wrongMemory.Pos, $"Only the program's main file may have a memory block, since it describes the whole program; '{module.Name}' is a module");
            if (!module.IsMain && program.Exports.Count > 0)
                throw new CompileError(program.Exports[0].Pos, $"Only the program's main file may export functions; '{module.Name}' is a module");
            if (module.IsMain) EnsureMemoryLayout();
            foreach (var s in program.Structs) LayOut((StructSymbol)module.Scope[s.Name]);
            foreach (var f in program.Functions) DeclareFunction(f);
            foreach (var g in program.Globals.Where(g => g.Symbol is null)) DeclareGlobal(g);
            // Initial values with addresses after all of the file's globals, so they can name globals declared below.
            // Only this file's: a global declared early for another module's constant waits for its own
            // module's turn, when the functions and globals it names are declared.
            foreach (var (decl, v, _) in _addressInits.Where(a => a.Module == module).ToList()) v.InitialBytes = ConstantBytes(decl.Init!, v.Type, v);
            _addressInits.RemoveAll(a => a.Module == module);
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
        {
            if (g.FixedAddress is not int fixedAt) continue;
            var span = new AddressRange(fixedAt, fixedAt + g.Type.Size - 1);
            if (_layout.IsVolatile(fixedAt, span.Last)) g.InVolatileArea = true;
            if (_layout.Overlays.Values.Any(a => a.Overlaps(span))) g.InOverlayArea = true;
        }

        var mainModule = modules[^1];
        var exports = new List<FunctionSymbol>();
        var exportedAs = new Dictionary<FunctionSymbol, FunctionSymbol>();
        int? exportTable = null;
        FunctionSymbol main;
        bool hasMain = mainModule.Scope.TryGetValue("main", out var mainSymbol) && mainSymbol is FunctionSymbol;
        if (mainModule.Syntax.ExportTable is { } table)
        {
            // export f, g @ $0900 in a program with main (spec 12.3).
            if (!hasMain)
                throw new CompileError(table.Pos,
                    "A binary's jump table is at the start of its first program area, so its 'export' line has no address. Remove the '@' and the address, or add 'fn main()' for a program whose table plug-ins call");
            exportTable = ExportTableAddress(table, mainModule);
        }
        if (mainModule.Syntax.Exports.Count > 0 && exportTable is null)
        {
            // A binary for another program to call (spec 12.3): its entries are one root.
            if (hasMain)
                throw new CompileError(mainModule.Syntax.Exports[0].Pos,
                    "A program with 'main' cannot export functions without an address: 'export' alone builds a binary for another program to call, which has no 'main'. Give the table an address for plug-ins to call ('export f @ $0900'), or remove 'main'");
            if (ExportedModule(mainModule) is { } api)
            {
                exportedAs = MatchInterface(mainModule, api);
                exports = exportedAs.Keys.OrderBy(f => exportedAs[f].FixedAddress).ToList();
            }
            else
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
            if (exportTable is not null)
            {
                if (ExportedModule(mainModule) is { } api)
                    throw new CompileError(mainModule.Syntax.Exports[0].Pos,
                        $"'export {api.Name}' builds a binary that plugs into {api.Name}'s jump table, which a program with 'main' does not. Write the program's own functions instead: 'export f, g @ address'");
                // Plug-ins call the entries, so their callers are unknown, as for functions whose address is taken (6.5).
                exports = ResolveExports(mainModule, inProgram: true);
                foreach (var e in exports)
                {
                    e.AddressTaken = true;
                    main.UsedAddresses.Add(e);
                }
            }
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
        if (exports.Count > 0 && exportTable is null) reachable.Remove(main);   // the root of a binary's entries has no code
        CheckPrivateGlobalsPerRoot(modules, reachable);
        MarkGlobalsUsedFromTwoRoots(reachable);
        var used = reachable.SelectMany(f => f.UsedGlobals).Concat(mainModule.Scope.Values.OfType<VariableSymbol>()).ToHashSet();
        // A kept global keeps the globals whose addresses are in its initial value.
        foreach (var g in used.ToList()) used.UnionWith(GlobalsNamedBy(g.Storage));
        return new CheckedProgram(modules, _globals.Where(used.Contains).ToList(), reachable, main) { Layout = _layout, Exports = exports, ExportedAs = exportedAs, ExportTableAddress = exportTable };
    }

    /// <summary>A global and the globals whose addresses are in its initial value, and in theirs (spec 4).</summary>
    private static List<VariableSymbol> GlobalsNamedBy(VariableSymbol g)
    {
        var all = new List<VariableSymbol> { g };
        for (int i = 0; i < all.Count; i++)
            foreach (var fill in all[i].AddressFills)
                if (fill.Variable is { } v && !all.Contains(v)) all.Add(v);
        return all;
    }

    /// <summary>The name of the root that stands for a binary's entries (spec 12.3); it has no code.</summary>
    private const string EntriesRoot = "(entries)";

    /// <summary>The functions named by <c>export</c> lines, in order. Each must be callable with registers alone.</summary>
    private static List<FunctionSymbol> ResolveExports(Module mainModule, bool inProgram = false)
    {
        var result = new List<FunctionSymbol>();
        foreach (var e in mainModule.Syntax.Exports)
        {
            if (!mainModule.Scope.TryGetValue(e.Name, out var symbol) || symbol is not FunctionSymbol f)
                throw new CompileError(e.Pos, mainModule.Scope.ContainsKey(e.Name) ? $"'{e.Name}' is not a function, so it cannot be exported" : $"'{e.Name}' is not declared; 'export' names functions of the main file");
            if (e.Name == "setup" && !inProgram)
                throw new CompileError(e.Pos, "'setup' is the name of the binary's startup entry (entry 0 of the jump table). Rename the function");
            if (e.Name == "main")
                throw new CompileError(e.Pos, "'main' is where the program starts; plug-ins cannot call it. Remove it from the export line");
            if (result.Contains(f))
                throw new CompileError(e.Pos, $"'{e.Name}' is exported twice");
            CheckExportable(f, e.Name, e.Pos, inProgram ? "a plug-in" : "the caller of a binary");
            result.Add(f);
        }
        return result;
    }

    /// <summary>An entry of a jump table is called with registers alone, and has code of its own (spec 12.3).</summary>
    private static void CheckExportable(FunctionSymbol f, string name, SourcePos pos, string caller)
    {
        if (f.IsInterrupt)
            throw new CompileError(pos, $"'{name}' is an interrupt handler, which the CPU calls through a vector; it cannot be an entry of the jump table. Export a plain function that does the work");
        if (f.IsInline)
            throw new CompileError(pos, $"'{name}' is inline: it has no code of its own to put in the jump table. Remove 'inline'");
        if (f.FixedAddress is not null)
            throw new CompileError(pos, $"'{name}' is a routine at a fixed address, not part of this binary");
        if (f.Parameters.FirstOrDefault(p => p.PassedIn is null) is { } memoryParam)
            throw new CompileError(pos,
                $"'{name}' takes '{memoryParam.Name}' in memory, which {caller} cannot set. Pass it in a register ({memoryParam.Name}: {memoryParam.Type} using A)");
    }

    /// <summary>The address of a program's export table, a constant (spec 12.3).</summary>
    private int ExportTableAddress(ExportTable table, Module mainModule)
    {
        _module = mainModule;
        CheckExpr(table.Address);
        if (table.Address.ConstValue is not long a)
            throw new CompileError(table.Address.Pos, "The address of the export table must be known at compile time. Use a constant, such as $0900");
        int size = 3 * mainModule.Syntax.Exports.Count;
        if (a < 0 || a + size - 1 > 0xFFFF)
            throw new CompileError(table.Address.Pos, $"The export table ({size} bytes) at ${a:X} does not fit in memory. Use a lower address");
        return (int)a;
    }

    /// <summary><c>export level_api</c>: a name on the export line that is a module the main file imports (spec 12.3).</summary>
    private static Module? ExportedModule(Module mainModule)
    {
        var names = mainModule.Syntax.Exports;
        var module = names.Select(e => mainModule.Scope.ContainsKey(e.Name) ? null : mainModule.Imports.GetValueOrDefault(e.Name)).FirstOrDefault(m => m is not null);
        if (module is null) return null;
        if (names.Count > 1)
        {
            var other = names.Skip(names.FindIndex(e => e.Name == module.Name) == 0 ? 1 : 0).First();
            throw new CompileError(other.Pos,
                $"'export {module.Name}' takes the entries and their addresses from {module.Name}, so the export line names nothing else. " +
                (other.Name == module.Name ? $"Remove the second '{module.Name}'" : $"Declare a routine for '{other.Name}' in {module.Name} instead"));
        }
        return module;
    }

    /// <summary>
    /// <c>export level_api</c> (spec 12.3): one function of the main file for each routine of the interface
    /// module, by name, with the same registers; <c>setup</c> at entry 0; each fixed variable of the module
    /// defined as <c>data</c> of the same name, type and address. Returns each function's routine.
    /// </summary>
    private Dictionary<FunctionSymbol, FunctionSymbol> MatchInterface(Module mainModule, Module api)
    {
        var pos = mainModule.Syntax.Exports[0].Pos;
        int start = _layout.Program[0].First;
        // By the names they have inside the module: the symbols' names are qualified (level_api.enter).
        var routines = api.Scope.Where(e => e.Value is FunctionSymbol { FixedAddress: not null })
            .Select(e => (Name: e.Key, Routine: (FunctionSymbol)e.Value)).OrderBy(e => e.Routine.FixedAddress).ToList();

        if (routines.FirstOrDefault(r => r.Name == "setup").Routine is not { } setup)
            throw new CompileError(pos,
                $"{api.Name} has no 'setup' routine. The host calls setup first, at entry 0 of the jump table: declare 'fn setup() @ ${start:X4}' in {api.Name}");
        if (setup.FixedAddress != start)
            throw new CompileError(pos,
                $"'{api.Name}.setup' is at ${setup.FixedAddress:X4}, but setup is entry 0 of the jump table, at the start of the binary's first program area (${start:X4}). " +
                $"Move setup to ${start:X4} in {api.Name}, or start the first program area at ${setup.FixedAddress:X4}");
        if (setup.Parameters.Count > 0 || setup.FixedInputs.Count > 0 || setup.ReturnType.Kind != TypeKind.Void)
            throw new CompileError(setup.Pos, $"'{api.Name}.setup' must take nothing and return nothing: it is the binary's startup code. Remove its parameters and return value");
        if (mainModule.Scope.TryGetValue("setup", out var ownSetup))
            throw new CompileError(ownSetup.Pos, "'setup' is the name of the binary's startup entry (entry 0 of the jump table). Rename the function");

        var result = new Dictionary<FunctionSymbol, FunctionSymbol>();
        int next = start + 3;
        foreach (var (name, r) in routines.Where(r => r.Routine != setup))
        {
            if (r.FixedAddress < next)
                throw new CompileError(r.Pos,
                    $"'{api.Name}.{name}' at ${r.FixedAddress:X4} overlaps the entry before it: each entry of the jump table is a 3-byte jmp, from ${start:X4}. Move it to ${next:X4} or later");
            next = r.FixedAddress!.Value + 3;
            if (next - 1 > _layout.Program[0].Last)
                throw new CompileError(r.Pos,
                    $"'{api.Name}.{name}' at ${r.FixedAddress:X4} is outside the binary's first program area {_layout.Program[0]}, where the jump table is. Move it in {api.Name} to the table, after the entry before it");
            if (r.FixedInputs.Count > 0)
                throw new CompileError(r.Pos,
                    $"'{api.Name}.{name}' has fixed inputs, which a function of the binary cannot receive. Declare them as parameters in registers");
            if (!mainModule.Scope.TryGetValue(name, out var symbol) || symbol is not FunctionSymbol f)
                throw new CompileError(pos,
                    $"'{api.Name}.{name}' has no function in this binary. Write 'fn {name}({Signature.Parameters(r)}){Signature.Return(r)}' in the main file");
            CheckExportable(f, name, f.Pos, "the caller of a binary");
            if (!Signature.Matches(f, r))
                throw new CompileError(f.Pos,
                    $"'{name}' does not match '{api.Name}.{name}': it is 'fn {name}({Signature.Parameters(f)}){Signature.Return(f)}', the interface says 'fn {name}({Signature.Parameters(r)}){Signature.Return(r)}'. Change the function to match");
            result[f] = r;
        }

        // Data every binary provides at a fixed address, such as a level's header: the interface's
        // volatile variables. A plain fixed variable is memory of the host, which levels may use.
        foreach (var (name, v) in api.Scope.Where(e => e.Value is VariableSymbol { FixedAddress: not null, IsData: false, DeclaredVolatile: true })
                     .Select(e => (e.Key, (VariableSymbol)e.Value)).OrderBy(e => e.Item2.FixedAddress))
        {
            string want = $"data {name}: {v.Type} @ ${v.FixedAddress:X4}";
            if (!mainModule.Scope.TryGetValue(name, out var symbol) || symbol is not VariableSymbol d)
                throw new CompileError(pos, $"'{api.Name}.{name}' is data every binary of {api.Name} provides. Declare '{want} = ...' in the main file");
            if (!d.IsData || !d.Type.SameAs(v.Type) || d.FixedAddress != v.FixedAddress)
                throw new CompileError(d.Pos, $"'{name}' does not match '{api.Name}.{name}'. Declare it as '{want} = ...'");
            _binaryData[v] = name;
        }
        return result;
    }

    /// <summary>For <c>export level_api</c>: the interface's volatile variables, each with the name of the binary's own data at its address.</summary>
    private readonly Dictionary<VariableSymbol, string> _binaryData = [];

    /// <summary>The binary names its own data, not the interface's variable at the same address (spec 12.3, question 92).</summary>
    private void CheckNotBinaryData(VariableSymbol v, SourcePos pos)
    {
        if (_binaryData.TryGetValue(v, out var own))
            throw new CompileError(pos, $"'{v.QualifiedName}' is this binary's data '{own}'. Use '{own}'");
    }

    // ---------- Declarations ----------

    /// <summary>0: the main file's memory block is not evaluated yet, 1: being evaluated, 2: done.</summary>
    private int _memoryState;

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
        if (_module.Syntax.Memory is { } memory) _layout = EvaluateMemory(memory);
        _memoryState = 2;
    }

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
        if (Lookup(name, demand: false) is { } existing)
            throw new CompileError(symbol.Pos, $"'{name}' is already declared at {existing.Pos}");
        scope[name] = symbol;
    }

    /// <summary>
    /// Finds a name in the enclosing scopes and the current file, or <c>module.name</c> in an imported module.
    /// A <c>private</c> name of another module is not found.
    /// </summary>
    /// <param name="demand">Declare a constant that is not declared yet (<see cref="Demand"/>).</param>
    private Symbol? Lookup(string name, bool demand = true)
    {
        int dot = name.IndexOf('.');
        if (dot > 0)
        {
            if (!_module.Imports.TryGetValue(name[..dot], out var module)) return null;
            string member = name[(dot + 1)..];
            if (module.Syntax.PrivateNames.Contains(member)) return null;
            return module.Scope.GetValueOrDefault(member) ?? (demand ? Demand(module, member) : null);
        }
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out var s)) return s;
        return _module.Scope.GetValueOrDefault(name) ?? (demand ? Demand(_module, name) : null);
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
        ConstFnSymbol => "const fn",
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
            // A constant has a type only when its declaration writes one (spec 3.1).
            IsBitPattern = type.Kind == TypeKind.IntLiteral && decl.Value.IsBitPattern,
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
            if (fn.ReturnRegister == Register.Overflow && !decl.IsAsm && decl.Address is null)
                throw new CompileError(decl.ReturnType.Pos,
                    "Only an asm fn, an inline asm fn or a routine at a fixed address can return a bool using overflow: no instruction sets the V flag, so a fn cannot 'return true' in it. Return it using carry, zero or negative");
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
            throw new CompileError(decl.Pos, "Data must be an array, a string or a struct. For a single value, use 'const'");
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
        if (decl.IsIndexed)
        {
            var t = decl.TypeSyntax is null ? null : ResolveType(decl.TypeSyntax);
            if (t is null || !t.IsStructArray)
                throw new CompileError(decl.Pos,
                    "'indexed' is for arrays of structs, e.g. 'indexed var list: [Enemy; 20]'. Arrays of numbers are indexed directly");
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
                    "'linear' is for arrays of 16-bit values or structs, e.g. 'linear var pts: [u16; 32]' or 'linear var list: [Enemy; 8]'");
            if (t.Element!.Size == 1 && !t.Element.IsStruct)
                throw new CompileError(decl.Pos, $"'linear' is only for arrays of 16-bit values or structs: the elements of a {t} are one byte each, so they are already stored in order");
            if (!t.Element.IsStruct && t.Length > 128)
                throw new CompileError(decl.Pos,
                    $"A linear array of 16-bit values has at most 128 elements, so that the doubled index fits in a register; '{decl.Name}' has {t.Length}. Leave out 'linear' to store it split");
            return TernType.ArrayOf(t.Element, t.Length, linear: true);
        }
        if (decl.TypeSyntax is not null) return ResolveType(decl.TypeSyntax);
        if (decl.Init is null) throw new CompileError(decl.Pos, $"'{decl.Name}' needs a type or an initial value");
        if (decl.Init is ArrayLiteralExpr || IsCompileTimeArray(decl.Init))
            throw new CompileError(decl.Pos, decl.IsData
                ? $"Data arrays need a written type, e.g. data {decl.Name}: [u8; _] = [...], where '_' takes the length from the list"
                : $"Array variables need a written type, e.g. var {decl.Name}: [u8; _] = [...], where '_' takes the length from the list");
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
                if (!allowAddress) throw new CompileError(decl.Pos, "Only global variables can have a fixed address");
                var addr = decl.Placement.Address!;
                // Only a region name needs the memory block; a constant address does not.
                if (addr is NameExpr placed && _module.Syntax.Memory?.Lines.Any(l => l.Kind == "region" && l.Name == placed.Name) == true)
                    EnsureMemoryLayout();
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
        if (type.IsStruct) return StructBytes(init, type, target, at);
        if (type.IsArray)
        {
            if (init is not ArrayLiteralExpr list)
            {
                if (IsCompileTimeArray(init)) return ArrayBytes(CompileTimeArray(init), type, init.Pos);
                throw new CompileError(init.Pos, "An array must be initialized with [ ... ]");
            }
            if (list.Elements.Count != type.Length)
                throw new CompileError(init.Pos, $"Expected {type.Length} values, found {list.Elements.Count}");
            var bytes = new List<byte>();
            foreach (var e in list.Elements) bytes.AddRange(ConstantBytes(e, type.Element!, target, at + bytes.Count));
            return bytes.ToArray();
        }

        if (init is StructLiteralExpr lit) throw LiteralForScalar(lit, type, null);
        if (UsesAddr(init)) return AddressBytes(init, type, target, at);
        CheckExpr(init);
        Coerce(init, type);
        if (init.ConstValue is not long value)
            throw new CompileError(init.Pos, "A global's initial value must be known at compile time");
        return type.Size == 1 ? [(byte)value] : [(byte)value, (byte)(value >> 8)];
    }

    /// <summary>A struct literal where a number, bool or pointer is expected; <paramref name="field"/> names the field, if any.</summary>
    private static CompileError LiteralForScalar(StructLiteralExpr lit, TernType type, string? field)
    {
        if (!type.IsPointer) return new CompileError(lit.Pos, $"Expected {type}, found a {lit.StructName} literal");
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
            throw new CompileError(lit.Pos, $"'{lit.StructName}' is not a struct, so '{lit.StructName} {{ ... }}' is not a struct literal");
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
                throw new CompileError(value.Pos, $"A string is a [u8; {text.Length}] array, but the field '{field.Name}' is {type}");
            if (text.Length != n)
                throw new CompileError(value.Pos, $"The field '{field.Name}' is {type}: expected {n} bytes, found {text.Length}");
            text.CopyTo(record, offset);
            return;
        }
        if (value is not ArrayLiteralExpr list)
            throw new CompileError(value.Pos, $"The field '{field.Name}' is {type}, so its value is a list. Write [ ... ] with {n} values");
        if (list.Elements.Count != n)
            throw new CompileError(value.Pos, $"The field '{field.Name}' is {type}: expected {n} values, found {list.Elements.Count}");
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
                    len = a.Length.ConstValue ?? throw new CompileError(a.Length.Pos, "Array length must be known at compile time");
                }
                if (len < 1 || len > 256 && !(_allowLongBytes && element.Size == 1 && element.IsInteger))
                    throw new CompileError(a.Length?.Pos ?? a.Pos, len > 256
                        ? $"An array of {element} has at most 256 elements. Only a global var or data of u8 or i8 may be longer (spec 3.2), and it is used through a pointer"
                        : $"Array length must be 1 to 256, not {len}");
                if (len > 0x10000)
                    throw new CompileError(a.Length?.Pos ?? a.Pos, $"An array of {element} has at most 65536 elements, all of memory, not {len}. Use a smaller length");
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

        // The var lines of an asm fn: locals in its frame, or in each caller's for an inline asm fn (spec 9).
        if (decl.IsAsm && ((AsmStmt)decl.Body.Statements[0]).Locals is { Count: > 0 } asmLocals)
            foreach (var local in asmLocals) CheckLocal(local, inAsmFn: true);

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
            case ConstDecl c: DeclareConst(c); break;
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

    private void CheckLocal(VarDecl decl, bool inAsmFn = false)
    {
        var type = DeclaredType(decl);
        var v = new VariableSymbol(decl.Name, decl.Pos, type, VariableKind.Local, _function) { IsIndexed = decl.IsIndexed };
        decl.Symbol = v;
        ApplyPlacement(decl, v, allowAddress: false);

        if (inAsmFn && decl.Init is not null)
            throw new CompileError(decl.Init.Pos, $"'{decl.Name}' in an asm fn has no initial value: the assembly sets it. Remove the value and store it in the assembly");
        if (decl.Init is null)
        {
            if (!type.IsArray && !type.IsStruct && !inAsmFn)
                throw new CompileError(decl.Pos, $"Local variable '{decl.Name}' needs an initial value");
        }
        else
        {
            if (decl.Init is StructLiteralExpr lit) throw StructLiteralInCode(lit, decl.Name);
            if (type.IsStruct || type.IsStructArray)
                throw new CompileError(decl.Init.Pos, "A struct cannot have an initial value. Assign its fields");
            if (type.IsArray && decl.Init is ListForExpr or ArrayRepeatExpr) CheckExpr(decl.Init);
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
        if (a.Value is StructLiteralExpr lit) throw StructLiteralInCode(lit, Describe(a.Target));
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
        {
            // Checked as the operation it stands for (spec 3.1): x op= e is x = x op e.
            if (a.Value is { Type.Kind: TypeKind.IntLiteral, ConstType: { } typed })
            {
                var operation = TypedOperationType(target.Type, typed, a.Pos);
                if (!operation.SameAs(target.Type))
                    throw new CompileError(a.Value.Pos, $"Expected {target.Type}, found {operation}: '{a.Value.ConstTypeFrom}' is {typed}. Convert with 'as {target.Type}'");
            }
            // Inside an operator a pointer still needs 'as' (spec 3.5), as in w = w + p.
            if (a.Value.Type.IsPointer && target.Type.Kind == TypeKind.U16)
                throw new CompileError(a.Value.Pos, $"Expected {target.Type}, found {a.Value.Type}. Convert with 'as u16'");
            try { Coerce(a.Value, target.Type); }
            catch (CompileError error) when (a.Value.ConstValue is long v && error.Description.Contains(" does not fit in "))
            {
                var wider = WiderFor(v, target.Type);
                var written = Describe(target);
                var name = written is not null ? $"'{written}'" : "the left side";
                // x += -1 on an unsigned x is x -= 1.
                var fix = v < 0 && !target.Type.IsSigned && -v <= target.Type.MaxValue && written is not null && a.Op is TokenKind.PlusEq or TokenKind.MinusEq
                    ? $"Write '{written} {(a.Op == TokenKind.PlusEq ? "-=" : "+=")} {-v}'"
                    : wider is not null && Unwrap(target) is NameExpr ? $"Declare {name} as {wider}, or use a smaller value" : "Use a smaller value";
                throw new CompileError(a.Value.Pos,
                    $"{v} does not fit in {target.Type} ({target.Type.MinValue} to {target.Type.MaxValue}), the type of {name}, which it is combined with. {fix}");
            }
        }
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
        {
            if (!(bound.Type.IsInteger || bound.Type.Kind == TypeKind.IntLiteral))
                throw new CompileError(bound.Pos, "Loop bounds must be numbers");
            // A typed constant gives the counter its type, as a variable of that type would.
            if (bound is { Type.Kind: TypeKind.IntLiteral, ConstType: { } typed }) Coerce(bound, typed);
        }

        // The loop variable's type: the wider of the bounds, or u8/u16 for literals. A negative
        // literal or a signed bound makes it i8 if the bounds fit in -128..127, else i16 (spec 5.1).
        TernType type;
        bool fromLiteral = f.From.Type.Kind == TypeKind.IntLiteral, toLiteral = f.To.Type.Kind == TypeKind.IntLiteral;
        if (fromLiteral && toLiteral)
        {
            long a = f.From.ConstValue!.Value, b = f.To.ConstValue!.Value;
            if (a < 0 || b < 0)
                type = Math.Min(a, b) >= -128 && Math.Max(a, b) <= 127 ? TernType.I8 : TernType.I16;
            else
                type = DefaultType(f.From).Size >= DefaultType(f.To).Size ? DefaultType(f.From) : DefaultType(f.To);
        }
        else if (fromLiteral != toLiteral)
        {
            var (literal, other) = fromLiteral ? (f.From, f.To) : (f.To, f.From);
            long v = literal.ConstValue!.Value;
            if (v < 0 && other.Type.IsInteger && !other.Type.IsSigned)
                throw new CompileError(literal.Pos,
                    $"A loop from a negative bound counts with a signed variable, but the other bound is {other.Type}. Convert it with 'as {(v >= -128 && other.Type.Size == 1 ? "i8" : "i16")}'");
            type = other.Type.IsSigned && other.Type.Size == 1 && (v < -128 || v > 127) ? TernType.I16 : Unify(f.From, f.To, f.Pos);
        }
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
                throw new CompileError(f.Pos,
                    $"{(type.IsSigned ? "An" : "A")} {type} loop variable does not fit in {reg}. Remove 'using {reg}', or use bounds that fit in {(type.IsSigned ? "an i8" : "a u8")}");
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
            IsLoopCounter = true,
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

    /// <summary>The largest value a loop with constant bounds reaches, or null if it runs no passes. Also used by the code generator (spec 3.2's fold).</summary>
    internal static long? LargestLoopValue(long from, long to, bool inclusive, int step)
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
        if (m.IsIndexed && m.Arms.All(a => a.IsDefault))
            throw new CompileError(m.Pos, "An 'indexed match' needs at least one pattern besides '_': its table covers the values from the lowest pattern to the highest");
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
        BlockStmt { Code: [.., var last] } => AlwaysReturns(last),
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
        // A whole expression with a compile-time part is computed by the interpreter (spec 4.1).
        if (!InRuntimeCode && e is BinaryExpr or UnaryExpr or MemberExpr or IndexExpr && NeedsInterpreter(e))
        {
            EvaluateConstant(e);
            return;
        }
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

            case StructLiteralExpr lit:
                throw StructLiteralInCode(lit, null);

            case RealExpr or ListForExpr or ArrayRepeatExpr:
                EvaluateConstant(e);
                break;

            case StringExpr:
                throw new CompileError(e.Pos, "A string can only be the value of data or a global array. "
                    + "Declare it as data, e.g. 'data HI = \"HI\"', and pass addr(HI), len(HI)");

            case NameExpr n:
                switch (Lookup(n.Name))
                {
                    case null when n.Name is "PI" or "TAU":
                        EvaluateConstant(e);
                        break;
                    case null: throw NotDeclared(n.Name, n.Pos);
                    case ConstFnSymbol:
                        throw new CompileError(n.Pos, $"'{n.Name}' is a const fn; call it with ()");
                    case ConstSymbol c:
                        // An integer constant, typed or not, is computed exactly in constant expressions (spec 3.1).
                        e.ConstValue = c.Value;
                        if (c.Type.IsInteger) { e.Type = TernType.IntLiteral; e.ConstType = c.Type; e.ConstTypeFrom = c.Name; }
                        else { e.Type = c.Type; e.IsBitPattern = c.IsBitPattern; }
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
                // addr(x).lo / .hi are the bytes of the address, even for a struct with a field named lo (spec 3.4).
                if (m.Member is "lo" or "hi" && m.Target is CallExpr { Name: "addr" } address)
                    m.Target = new CastExpr(address.Pos, address, new NamedTypeSyntax(address.Pos, "u16"));
                CheckMemberTarget(m);
                CheckExpr(m.Target);
                // So are those of addr(x) plus or minus a constant, the forms an initial value allows (question 84).
                if (m.Member is "lo" or "hi" && m.Target is BinaryExpr { Type.IsPointer: true } && Placed(m.Target) is not null)
                    m.Target = new CastExpr(m.Target.Pos, m.Target, new NamedTypeSyntax(m.Target.Pos, "u16")) { Type = TernType.U16 };
                if (m.Target.Type.IsStruct || m.Target.Type is { IsPointer: true, Element.IsStruct: true })
                {
                    CheckField(m);
                    break;
                }
                if (m.Member is not ("lo" or "hi"))
                    throw new CompileError(m.Pos, $"Unknown member '.{m.Member}'");
                // A typed 16-bit constant keeps its size (LINE.hi with LINE: u16 = 250 is 0); otherwise the size of the value.
                if (m.Target.Type.Kind == TypeKind.IntLiteral) Coerce(m.Target, m.Target.ConstType is { Size: 2 } wide ? wide : LiteralType(m.Target));
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
                    Coerce(c.Operand, c.Operand.IsBitPattern && c.Operand.ConstValue < 0 ? target : LiteralType(c.Operand));
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
                    u.ConstValue = u.Op == TokenKind.Minus ? ConstNegate(u.Pos, v!.Value) : v;
                    KeepConstType(u, u.Operand);
                    u.IsBitPattern = u.Op == TokenKind.Plus && u.Operand.IsBitPattern;
                    return;
                }
                if (!t.IsInteger) throw new CompileError(u.Pos, $"Unary '{(u.Op == TokenKind.Minus ? "-" : "+")}' needs a number");
                u.Type = t;
                if (v is not null) u.ConstValue = Normalize(u.Op == TokenKind.Minus ? -v.Value : v.Value, t);
                return;
            case TokenKind.Tilde:
                if (t.Kind == TypeKind.IntLiteral)
                {
                    // Exact, and negative: it takes the width of the type it is used as (x & ~$0F on a u8 is and #$F0).
                    u.Type = t;
                    u.ConstValue = ~v!.Value;
                    KeepConstType(u, u.Operand);
                    u.IsBitPattern = true;
                    return;
                }
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
                    long shifted = t.Kind == TypeKind.IntLiteral
                        ? ConstArithmetic(b.Pos, b.Op, l.Value, r!.Value)
                        : b.Op == TokenKind.Shl ? l.Value << (int)r!.Value : l.Value >> (int)r!.Value;
                    b.ConstValue = t.Kind == TypeKind.IntLiteral ? shifted : Normalize(shifted, t);
                    if (t.Kind == TypeKind.IntLiteral) { KeepConstType(b, b.Left); b.IsBitPattern = b.Left.IsBitPattern; }
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
                long value = ConstArithmetic(b.Pos, b.Op, l.Value, r.Value);
                b.Type = t;
                b.ConstValue = t.Kind == TypeKind.IntLiteral ? value : Normalize(value, t);
                if (t.Kind == TypeKind.IntLiteral) KeepConstType(b, b.Left, b.Right);
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
                    long value = ConstArithmetic(b.Pos, b.Op, l.Value, r.Value);
                    b.ConstValue = t.Kind == TypeKind.IntLiteral ? value : Normalize(value, t);
                    if (t.Kind == TypeKind.IntLiteral)
                    {
                        KeepConstType(b, b.Left, b.Right);
                        b.IsBitPattern = b.Op is TokenKind.Amp or TokenKind.Pipe or TokenKind.Caret && (b.Left.IsBitPattern || b.Right.IsBitPattern);
                    }
                }
                return;
            }
        }
    }

    /// <summary>
    /// The type a constant expression takes from its typed constants (spec 3.1): the wider of the two
    /// operands' types. With signed and unsigned ones mixed it has none, and is a plain number.
    /// </summary>
    private static void KeepConstType(Expr result, Expr left, Expr? right = null)
    {
        var (a, b) = (left.ConstType, right?.ConstType);
        if (a is null || b is null || a.IsSigned == b.IsSigned)
        {
            bool fromLeft = b is null || (a is not null && a.Size >= b.Size);
            result.ConstType = fromLeft ? a : b;
            result.ConstTypeFrom = fromLeft ? left.ConstTypeFrom : right!.ConstTypeFrom;
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
        // w + p would be a pointer while w + (p as u16) is a number: only a constant may come first (spec 3.5).
        if (!leftPtr && b.Left.ConstValue is null)
            throw new CompileError(b.Pos,
                $"A number before a pointer is not pointer arithmetic. Write '{Describe(b.Right) ?? "p"} + {Describe(b.Left) ?? "n"}' to move the pointer, or convert the pointer with 'as u16' for a number");
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
        int bits = shifted.Kind == TypeKind.IntLiteral ? 64 : shifted.Size * 8;
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
            : new BinaryExpr(ix.Index.Pos, TokenKind.Shl, ix.Index, new NumberExpr(ix.Index.Pos, 1) { Type = u8, ConstValue = 1 })
            {
                Type = u8,
                ScalesIndexOf = ix.Target is NameExpr { Symbol: VariableSymbol v } ? (v.QualifiedName, v.Type.Length) : null,
            };
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
    /// <summary>An overlay may not take the address of a function of another overlay in its area (spec 8.5).</summary>
    private void CheckSiblingAddress(FunctionSymbol f, SourcePos pos)
    {
        if (_function?.OverlayArea is { } area && f.OverlayArea == area && f.Module != _function.Module)
            throw new CompileError(pos,
                $"addr({f.Name}) is in {f.Module!.Name}, which is in the same overlay area '{area}' as {_function.Module!.Name}. They are never loaded together, so the address would point at the wrong code. " +
                "Take the address in resident code, or put one of them in another area");
    }

    private void TakeFunctionAddress(CallExpr c, FunctionSymbol f, SourcePos pos)
    {
        if (f.IsInline)
            throw new CompileError(pos, $"'{f.Name}' is inline: it is copied into each caller and has no address of its own. Remove 'inline'");
        if (f.Parameters.FirstOrDefault(p => p.PassedIn is null) is { } memoryParam)
            throw new CompileError(pos,
                $"'{f.Name}' takes '{memoryParam.Name}' in memory, which a call through an address cannot set. " +
                $"Pass it in a register ({memoryParam.Name}: {memoryParam.Type} using A)");
        CheckSiblingAddress(f, pos);
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
            IndexExpr { Target: NameExpr { Symbol.AliasOf.Type.Linear: true } } => new StructPlace(PlaceKind.Records, 0),
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
            case PlaceKind.Records:
                throw new CompileError(field.Pos,
                    $"'{name}' has a variable index, so its address is not known at compile time. Use a constant index, or add the record's offset to the array's address");
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
                if (IsCompileTimeArray(c.Args[0]))
                {
                    c.Type = TernType.IntLiteral;
                    c.ConstValue = CompileTimeArray(c.Args[0]).Items.Length;
                    return;
                }
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
        if (symbol is ConstFnSymbol || (symbol is null && CompileTime.Interpreter.Builtins.Contains(c.Name)))
        {
            EvaluateConstant(c);
            return;
        }
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
                foreach (var g in inline.UsedGlobals.Where(g => g.OverlayArea == area && g.Module != caller.Module && (g.FixedAddress is null || g.IsData) && g.Region is null))
                    throw new CompileError(pos,
                        $"'{caller.Name}' calls the inline function '{callee.Name}', which is copied into it and uses '{g.Name}', {(g.IsData ? "data" : "a variable")} of {g.Module!.Name}. " +
                        $"{caller.Module!.Name} and {g.Module.Name} are both in the overlay area '{area}', never loaded together, and share their memory. Keep shared state in a resident module or the main file");
                foreach (var f in inline.UsedAddresses.Where(f => f.OverlayArea == area && f.Module != caller.Module))
                    throw new CompileError(pos,
                        $"'{caller.Name}' calls the inline function '{callee.Name}', which is copied into it and takes addr({f.Name}). " +
                        $"{caller.Module!.Name} and {f.Module!.Name} are both in the overlay area '{area}', never loaded together, so the address would point at the wrong code. Take the address in resident code");
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

    /// <summary>
    /// Type of a constant expression with no context: the type of its typed constants, or for one of
    /// literals and untyped constants the smallest type that holds it.
    /// </summary>
    private static TernType DefaultType(Expr e)
    {
        if (e.Type.Kind == TypeKind.IntLiteral && e.ConstType is { } typed) return typed;
        return LiteralType(e);
    }

    /// <summary>The smallest type that holds a constant's value, whatever its constants' types (for 'as', '.lo' and '.hi').</summary>
    private static TernType LiteralType(Expr e)
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
        if (left.Type.Kind == TypeKind.IntLiteral && right.Type.Kind == TypeKind.IntLiteral) return left.Type;
        if (left.Type.Kind == TypeKind.IntLiteral || right.Type.Kind == TypeKind.IntLiteral)
        {
            // A constant next to a runtime value: the operation's type is chosen as if the constant were a
            // variable of its written type, then the constant is computed exactly at that width (spec 3.1).
            var (constant, value) = left.Type.Kind == TypeKind.IntLiteral ? (left, right) : (right, left);
            var operation = value.Type;
            bool constantSetIt = false;
            if (constant.ConstType is { } typed && value.Type.IsInteger)
            {
                operation = TypedOperationType(value.Type, typed, pos);
                constantSetIt = !operation.SameAs(value.Type);
            }
            CoerceNamed(constant, operation, constantSetIt ? constant.ConstTypeFrom : Describe(value), constantSetIt);
            return operation;
        }
        return TypedOperationType(left.Type, right.Type, pos);
    }

    /// <summary>The type of an operation on two runtime types: the wider one; signed and unsigned do not mix.</summary>
    private static TernType TypedOperationType(TernType lt, TernType rt, SourcePos pos)
    {
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
            // ~$0F is the bit pattern ...1111_0000, as wide as the type it is used as.
            if (e.IsBitPattern && v < 0 && !target.IsSigned && v >= -(1L << (8 * target.Size)))
                e.ConstValue = v = Normalize(v, target);
            if (e.IsBitPattern && v < 0 && !target.IsSigned)
                throw new CompileError(e.Pos, $"'~' gives {v} here, a pattern wider than {target}: the value before '~' has bits above {target}. Use a value that fits in {target} before '~'");
            if (v < target.MinValue || v > target.MaxValue)
                throw new CompileError(e.Pos, $"{v} does not fit in {target} ({target.MinValue} to {target.MaxValue})");
            e.Type = target;
            return;
        }
        if (source.SameAs(target)) return;
        // The address as a number, where a u16 is expected (spec 3.5): no code.
        if (source.IsPointer && target.Kind == TypeKind.U16) return;
        if (target.IsPointer && source.IsPointer)
            throw new CompileError(e.Pos, $"Expected {target}, found {source}. Convert with 'as {target}'");
        if (source.IsInteger && target.IsInteger && source.IsSigned == target.IsSigned && source.Size < target.Size)
            return; // automatic widening; the code generator extends the value
        bool convertible = (source.IsInteger || source.IsPointer) && (target.IsInteger || target.IsPointer);
        throw new CompileError(e.Pos, $"Expected {target}, found {source}" + (convertible ? $". Convert with 'as {target}'" : ""));
    }

    /// <summary>
    /// Coerce for a constant combined with a runtime value: when it does not fit, the error names what
    /// set the type, the other operand or the typed constant (spec 3.1).
    /// </summary>
    private static void CoerceNamed(Expr e, TernType target, string? setBy, bool isConstant)
    {
        try { Coerce(e, target); }
        catch (CompileError error) when (setBy is not null && target.IsInteger && error.Description.Contains(" does not fit in "))
        {
            long v = e.ConstValue!.Value;
            var wider = WiderFor(v, target);
            throw new CompileError(e.Pos, isConstant
                ? $"{v} does not fit in {target} ({target.MinValue} to {target.MaxValue}), the type of the constant '{setBy}' in it, which sets the operation's type. " +
                  (wider is null ? "Use a smaller value" : $"Give '{setBy}' the type {wider}")
                : $"{v} does not fit in {target} ({target.MinValue} to {target.MaxValue}), the type of '{setBy}', which it is combined with. " +
                  (wider is null ? "Use a smaller value" : $"Convert '{setBy}' with 'as {wider}'"));
        }
    }

    /// <summary>A type of the same signedness as <paramref name="t"/> that holds <paramref name="v"/>, if one is wider.</summary>
    private static TernType? WiderFor(long v, TernType t)
    {
        var wide = t.IsSigned ? TernType.I16 : TernType.U16;
        return t.Size < 2 && v >= wide.MinValue && v <= wide.MaxValue ? wide : null;
    }

    /// <summary>Source text for a simple operand, to name it in an error; null for anything else.</summary>
    private static string? Describe(Expr e) => e switch
    {
        NameExpr n => n.Name,
        MemberExpr m when Describe(m.Target) is { } t => $"{t}.{m.Member}",
        IndexExpr ix when Describe(ix.Target) is { } t => $"{t}[{Describe(ix.Index) ?? "..."}]",
        CallExpr c => $"{c.Name}({string.Join(", ", c.Args.Select(a => Describe(a) ?? "..."))})",
        NumberExpr n => n.Value.ToString(),
        BinaryExpr b when Describe(b.Left) is { } l && Describe(b.Right) is { } r => $"{l} {OpText(b.Op)} {r}",
        _ => null,
    };

    /// <summary>
    /// One step of constant arithmetic, in signed 64 bits (spec 3.1): a result outside that range is
    /// an error. <c>/</c> rounds toward zero, <c>%</c> has the left operand's sign, <c>&gt;&gt;</c>
    /// rounds down. The shift count is already checked (0 to 63).
    /// </summary>
    internal static long ConstArithmetic(SourcePos pos, TokenKind op, long l, long r)
    {
        try
        {
            return op switch
            {
                TokenKind.Plus => checked(l + r),
                TokenKind.Minus => checked(l - r),
                TokenKind.Star => checked(l * r),
                TokenKind.Slash => checked(l / r),
                TokenKind.Percent => r == -1 ? 0 : l % r,
                TokenKind.Amp => l & r,
                TokenKind.Pipe => l | r,
                TokenKind.Caret => l ^ r,
                TokenKind.Shl => (l << (int)r) >> (int)r == l ? l << (int)r : throw new OverflowException(),
                TokenKind.Shr => l >> (int)r,
                _ => throw new InvalidOperationException(),
            };
        }
        catch (OverflowException)
        {
            throw OutOfConstantRange(pos);
        }
    }

    /// <summary>Negates a constant, in signed 64 bits (spec 3.1).</summary>
    internal static long ConstNegate(SourcePos pos, long v) => v == long.MinValue ? throw OutOfConstantRange(pos) : -v;

    private static CompileError OutOfConstantRange(SourcePos pos) => new(pos,
        "This constant expression goes outside the signed 64-bit range of constant arithmetic "
        + "(-9223372036854775808 to 9223372036854775807). Use smaller values");

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
            foreach (var f in GlobalsNamedBy(g).SelectMany(t => t.FunctionAddresses)) Named(f);

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
            foreach (var f in GlobalsNamedBy(g).SelectMany(t => t.FunctionAddresses)) Named(f);
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
            string memory = v.InlineOf is { } inlined
                ? $"the {(v.Kind == VariableKind.Temp ? "hidden temporary" : $"variable '{v.Name[(inlined.Name.Length + 1)..]}'")} of the inline function '{inlined.Name}', which is copied into it"
                : v.Kind switch
            {
                VariableKind.Temp => $"a hidden temporary for the code at line {v.Pos.Line} (e.g. for 16-bit arithmetic or an argument)",
                VariableKind.Parameter when v.PassedIn is { } reg => $"the parameter '{v.Name}', which it keeps in memory (it arrives in {reg})",
                VariableKind.Parameter => $"the parameter '{v.Name}' in memory",
                _ when v.IsLoopCounter => v.Name.StartsWith("_loop") ? $"the counter of the loop at line {v.Pos.Line}" : $"the counter '{v.Name}' of the loop at line {v.Pos.Line}",
                _ => $"the local variable '{v.Name}'",
            };
            // Only 'using X' / 'using Y' guarantees a counter is not memory (spec 8.1).
            string fix = v.IsLoopCounter && v.InlineOf is null
                ? $"Write 'using X' or 'using Y' on the loop at line {v.Pos.Line}, which keeps its counter in a register, or use a copy for each"
                : "Use a copy for each";
            throw new CompileError(f.Pos,
                $"'{f.Name}' is called from {DescribeRoot(roots[0])} and from {DescribeRoot(roots[1])}. An interrupt can stop it halfway and run it again, " +
                $"which would overwrite its memory: {memory}. Only a function with no memory of its own can be shared between main and interrupt handlers. {fix}");
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
