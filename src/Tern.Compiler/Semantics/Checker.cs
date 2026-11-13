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
    /// <summary>The entries of the overlays' interface tables (<c>implements</c>, spec 8.5).</summary>
    public List<Implementation> Implementations { get; init; } = [];
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

    private Checker(Platform platform, ErrorList? errors)
    {
        _platform = platform;
        _errors = errors;
        _layout = MemoryLayout.Default(platform);
    }

    /// <summary>Takes the warnings (spec 13), found once the checks found no error; null: none are worked out.</summary>
    private List<CompileError>? _warnings;
    private bool _warnLibrary;

    /// <param name="modules">The main file and its modules, each module before the files that import it.</param>
    /// <param name="errors">
    /// Takes every error found by the checks, which go on at the next statement or declaration (spec 13);
    /// if there is any, the first is thrown, carrying all, before the call graph is worked out. Null: the first error is thrown.
    /// </param>
    /// <param name="warnings">
    /// Takes the warnings (spec 13), in the order found, before the call graph is worked out, so they are there
    /// also when it throws an error. Null: none are worked out.
    /// </param>
    /// <param name="warnLibrary">Warn about library modules too, which programs never are (for the repository's test of its library).</param>
    public static CheckedProgram Check(List<Module> modules, Platform platform, ErrorList? errors = null, List<CompileError>? warnings = null, bool warnLibrary = false) =>
        new Checker(platform, errors) { _warnings = warnings, _warnLibrary = warnLibrary }.Run(modules);

    private CheckedProgram Run(List<Module> modules)
    {
        // Names whose declaration (or import) had a syntax error: their uses add no errors (spec 13).
        foreach (var module in modules)
            foreach (var (name, pos) in module.Syntax.BrokenNames)
                module.Scope.TryAdd(name, new ErrorSymbol(name, pos));

        // Const fns and encodings first: declaring one needs no other declaration, and constants of any
        // file may call them or use them in a literal (spec 4.1, 2.1).
        _mainModule = modules[^1];
        foreach (var module in modules)
        {
            _module = module;
            foreach (var f in module.Syntax.ConstFns) Declaring(f.Name, f.Pos, () => DeclareConstFn(f));
            foreach (var e in module.Syntax.Encodings) Declaring(e.Name, e.Pos, () => DeclareEncoding(e));
        }

        // Struct names and module parameters of every file first, so that a constant can be worked out
        // when it is first used, in any module, also across an import ring (spec 2.2, 4).
        foreach (var module in modules)
        {
            _module = module;
            var program = module.Syntax;
            if (!module.IsMain && program.Functions.FirstOrDefault(f => f.Name == "main") is { } wrongMain)
                Recover(() => throw new CompileError(wrongMain.Pos, $"Only the program's main file has 'fn main()'; '{module.Name}' is a module. Rename the function"));
            foreach (var s in program.Structs) Declaring(s.Name, s.Pos, () => DeclareStruct(s));
            // After an error, the parameters not declared yet accept every use.
            if (!Recover(() => DeclareParameters(module, modules[^1])))
                foreach (var p in program.Header?.Parameters ?? []) module.Scope.TryAdd(p.Name, new ErrorSymbol(p.Name, p.Pos));
        }

        // Declarations file by file. A constant (or, for len(), a global) that another declaration
        // uses before its turn is declared then (Demand).
        foreach (var module in modules)
        {
            _module = module;
            var program = module.Syntax;
            foreach (var c in program.Consts) Declaring(c.Name, c.Pos, () => DeclareConst(c));
            if (!module.IsMain && program.Memory is { } wrongMemory)
                Recover(() => throw new CompileError(wrongMemory.Pos, $"Only the program's main file may have a memory block, since it describes the whole program; '{module.Name}' is a module. Move the block to the main file"));
            if (!module.IsMain && program.Exports.Count > 0)
                Recover(() => throw new CompileError(program.Exports[0].Pos, $"Only the program's main file may export functions; '{module.Name}' is a module. Move the 'export' line to the main file"));
            if (module.IsMain) Recover(EnsureMemoryLayout);
            foreach (var s in program.Structs)
                if (module.Scope.GetValueOrDefault(s.Name) is StructSymbol st && st.Declaration == s)
                    Declaring(s.Name, s.Pos, () => LayOut(st));
            foreach (var f in program.Functions)
                if (!Declaring(f.Name, f.Pos, () => DeclareFunction(f))) _brokenFunctions.Add(f);
            foreach (var g in program.Globals.Where(g => g.Symbol is null)) Declaring(g.Name, g.Pos, () => DeclareGlobal(g));
            // Initial values with addresses after all of the file's globals, so they can name globals declared below.
            // Only this file's: a global declared early for another module's constant waits for its own
            // module's turn, when the functions and globals it names are declared.
            foreach (var (decl, v, _) in _addressInits.Where(a => a.Module == module).ToList())
                Declaring(decl.Name, decl.Pos, () => v.InitialBytes = ConstantBytes(decl.Init!, v.Type, v));
            _addressInits.RemoveAll(a => a.Module == module);
            // After the globals, so that a variable named as an address gets the right error.
            foreach (var f in program.Functions.Where(f => f.Address is not null && !_brokenFunctions.Contains(f)))
                if (!Declaring(f.Name, f.Pos, () => DeclareRoutine(f, f.Symbol!))) _brokenFunctions.Add(f);
            foreach (var a in program.Asserts) Recover(() => CheckAssert(a, module));
            // Encodings no literal used yet, so that their errors are reported too.
            foreach (var e in program.Encodings)
                if (module.Scope.GetValueOrDefault(e.Name) is EncodingSymbol es && es.Declaration == e)
                    Declaring(e.Name, e.Pos, () => Resolve(es));
            CheckDefaultEncodingLines(module);
            module.Declared = true;
        }

        // "@ name" means a region or an address, never either depending on other declarations (spec 8.4).
        foreach (var line in modules[^1].Syntax.Memory?.Lines.Where(l => l.Kind == "region") ?? [])
            if (modules[^1].Scope.TryGetValue(line.Name!, out var clash) && clash is not ErrorSymbol || modules[^1].Imports.ContainsKey(line.Name!))
                Recover(() => throw new CompileError(line.Pos,
                    $"The region '{line.Name}' has the name of {(clash is null ? "an imported module" : $"a {KindName(clash)} of the main file (line {clash.Pos.Line})")}, so '@ {line.Name}' would be ambiguous. Rename the region"));

        foreach (var import in modules[^1].Syntax.Imports.Where(i => i.Area is not null))
        {
            if (_layout.Overlays.ContainsKey(import.Area!)) continue;
            Recover(() => throw new CompileError(import.AreaPos, _layout.Regions.ContainsKey(import.Area!)
                ? $"'{import.Area}' is a region, not an overlay area; '@' on an import names an area declared with 'overlay {import.Area}' in the memory block. Use the name of an overlay area"
                : $"The memory block has no overlay area '{import.Area}'. Add a line 'first..=last overlay {import.Area}' to it"));
        }

        var implementations = CheckImplements(modules);

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
        FunctionSymbol main = null!;
        Recover(() =>
        {
            // A main whose declaration had an error: the checks below would report it missing.
            if (mainModule.Scope.GetValueOrDefault("main") is ErrorSymbol) throw new FollowOnError();
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
                {
                    // main may be in a declaration the parser dropped.
                    if (mainModule.Syntax.HasSyntaxErrors) throw new FollowOnError();
                    throw new CompileError(new SourcePos(1, 1, mainModule.Path),
                        "The program has no 'fn main()'. Add 'fn main() { }', or an 'export' line for a binary that another program calls");
                }
                main = realMain;
                if (main.Parameters.Count > 0 || main.ReturnType.Kind != TypeKind.Void)
                    throw new CompileError(main.Pos, "'main' must have no parameters and no return value. Remove them");
                if (main.FixedAddress is not null)
                    throw new CompileError(main.Pos, "'main' must have a body. Remove the address and write the body");
                if (main.IsInterrupt)
                    throw new CompileError(main.Pos, "'main' cannot be an interrupt handler; give the handler another name");
                if (main.IsInline)
                    throw new CompileError(main.Pos, "'main' cannot be inline: nothing calls it. Remove 'inline'");
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
        });

        foreach (var module in modules)
        {
            _module = module;
            foreach (var f in module.Syntax.Functions.Where(f => f.Address is null && !_brokenFunctions.Contains(f))) Recover(() => CheckFunctionBody(f));
        }
        Recover(CheckInlineCallsBetweenOverlays);
        // The call graph and what follows need a program without errors (spec 13).
        _errors?.ThrowIfAny();
        _warnings?.AddRange(FindWarnings(modules, _warnLibrary, exports));

        // Struct fields become plain variables, arrays and pointers for the code generator.
        foreach (var module in modules)
            foreach (var f in module.Syntax.Functions) Lowering.Apply(f.Body);

        // Only what main and the interrupt handlers can reach goes into the program, so unused parts
        // of a module cost nothing. The main file's globals are all kept.
        // An overlay's interface table points to its functions, so they are compiled even if nothing calls the interface.
        var tableTargets = implementations.Select(i => i.Function).ToList();
        MarkParts(main, mainModule, tableTargets);
        var reachable = OrderRoots(main, mainModule, tableTargets);
        if (exports.Count > 0 && exportTable is null) reachable.Remove(main);   // the root of a binary's entries has no code
        CheckPrivateGlobalsPerRoot(modules, reachable);
        MarkGlobalsUsedFromTwoRoots(reachable);
        var used = reachable.SelectMany(f => f.UsedGlobals).Concat(mainModule.Scope.Values.OfType<VariableSymbol>()).Concat(_tableData).ToHashSet();
        // A kept global keeps the globals whose addresses are in its initial value.
        foreach (var g in used.ToList()) used.UnionWith(GlobalsNamedBy(g.Storage));
        return new CheckedProgram(modules, _globals.Where(used.Contains).ToList(), reachable, main) { Layout = _layout, Exports = exports, ExportedAs = exportedAs, ExportTableAddress = exportTable, Implementations = implementations };
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
            if (mainModule.Scope.GetValueOrDefault(e.Name) is ErrorSymbol) throw new FollowOnError();
            if (!mainModule.Scope.TryGetValue(e.Name, out var symbol) || symbol is not FunctionSymbol f)
                throw new CompileError(e.Pos, mainModule.Scope.ContainsKey(e.Name) ? $"'{e.Name}' is not a function, so it cannot be exported. Remove it from the export line" : $"'{e.Name}' is not declared; 'export' names functions of the main file. Declare it there, or remove it from the export line");
            if (e.Name == "setup" && !inProgram)
                throw new CompileError(e.Pos, "'setup' is the name of the binary's startup entry (entry 0 of the jump table). Rename the function");
            if (e.Name == "main")
                throw new CompileError(e.Pos, "'main' is where the program starts; plug-ins cannot call it. Remove it from the export line");
            if (result.Contains(f))
                throw new CompileError(e.Pos, $"'{e.Name}' is exported twice. Remove one");
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
            throw new CompileError(pos, $"'{name}' is a routine at a fixed address, not part of this binary. Remove it from the export line");
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
                $"{api.Name} has no 'setup' routine. The host calls setup first, at entry 0 of the jump table. Declare 'fn setup() @ ${start:X4}' in {api.Name}");
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

    /// <summary>
    /// The binary names its own data, not the interface's variable at the same address (spec 12.3, question 92);
    /// so does a module that implements the interface, inside that module (spec 8.5, decision 120).
    /// </summary>
    private void CheckNotBinaryData(VariableSymbol v, SourcePos pos)
    {
        if (_binaryData.TryGetValue(v, out var own))
            throw new CompileError(pos, $"'{v.QualifiedName}' is this binary's data '{own}'. Use '{own}'");
        if (_moduleData.TryGetValue((_module, v), out var mine))
            throw new CompileError(pos, $"'{v.QualifiedName}' is this module's data '{mine}'. Use '{mine}'");
    }

    // ---------- Going on after an error (spec 13) ----------

    /// <summary>Where errors go; null: the first error is thrown and checking stops.</summary>
    private readonly ErrorList? _errors;
    /// <summary>Functions whose declaration had an error: their bodies are not checked.</summary>
    private readonly HashSet<FnDecl> _brokenFunctions = [];

    /// <summary>
    /// Runs the check of one declaration or statement. On an error, reports it and returns false, with
    /// the checker's state as before; a use of a broken name (<see cref="FollowOnError"/>) is not reported.
    /// A name that is not declared stays so: each use is reported (spec 13, decision 107).
    /// </summary>
    private bool Recover(Action check)
    {
        if (_errors is null)
        {
            check();
            return true;
        }
        var (module, function, loopDepth, dead, scopes, longBytes, listLength) = (_module, _function, _loopDepth, _dead, _scopes.Count, _allowLongBytes, _listLength);
        var registers = _loopRegisters.ToList();
        var loopMax = _loopMax.ToList();
        try
        {
            check();
            return true;
        }
        catch (CompileError e)
        {
            _errors.Add(e);
        }
        catch (FollowOnError) { }
        (_module, _function, _loopDepth, _dead, _allowLongBytes, _listLength) = (module, function, loopDepth, dead, longBytes, listLength);
        if (_scopes.Count > scopes) _scopes.RemoveRange(scopes, _scopes.Count - scopes);
        _loopRegisters.Clear();
        _loopRegisters.UnionWith(registers);
        _loopMax.Clear();
        foreach (var (v, max) in loopMax) _loopMax[v] = max;
        return false;
    }

    /// <summary>
    /// Runs the check of a top-level declaration of <paramref name="name"/>. After an error, the name
    /// is broken: its uses add no errors. Not when the error was that another declaration has the name.
    /// </summary>
    private bool Declaring(string name, SourcePos pos, Action declare)
    {
        var module = _module;
        if (Recover(declare)) return true;
        if (!module.Scope.TryGetValue(name, out var s) || s.Pos == pos) module.Scope[name] = new ErrorSymbol(name, pos);
        return false;
    }
}
