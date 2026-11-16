using Tern.Compiler.Syntax;

namespace Tern.Compiler.Semantics;

/// <summary>
/// One entry of an overlay's interface table (<c>import level1 @ levels implements level_api</c>, spec 8.5):
/// a <c>jmp</c> at <paramref name="Routine"/>'s address, in <paramref name="Module"/>'s file, to <paramref name="Function"/>.
/// </summary>
public sealed record Implementation(Module Module, Module Interface, string Name, FunctionSymbol Routine, FunctionSymbol Function);

public sealed partial class Checker
{
    /// <summary>
    /// The <c>implements</c> lists of the imports (spec 8.5): for each interface, one function of the module
    /// per routine at a fixed address, matched as for <c>export level_api</c> (12.3), and <c>data</c> for each
    /// <c>volatile</c> fixed variable. A call to a routine counts as a call to each of its implementations.
    /// </summary>
    private List<Implementation> CheckImplements(List<Module> modules)
    {
        var result = new List<Implementation>();
        var mainModule = modules[^1];
        // The module implementing each interface in each bank: two in one bank would put two tables on the same bytes (spec 8.5).
        var inBank = new Dictionary<(Module, string), Module>();
        foreach (var importer in modules)
            foreach (var import in importer.Syntax.Imports.Where(i => i.Implements.Count > 0))
                Recover(() =>
                {
                    _module = importer;
                    var (firstName, firstPos) = import.Implements[0];
                    // An area on a module's import is already an error (only the main file places modules).
                    if (import.Area is null)
                        throw new CompileError(firstPos,
                            $"'implements' puts a jump table in an overlay's file, but '{import.Name}' is not placed in an overlay area. " +
                            $"Place it with 'import {import.Name} @ area implements {firstName}'; a resident program offers entries with 'export f, g @ address'");
                    if (!importer.IsMain || _layout.PlacedArea(import.Area) is not { } area) throw new FollowOnError();
                    if (!importer.Imports.TryGetValue(import.Name, out var module)) throw new FollowOnError();
                    var tables = new List<(string Interface, AddressRange Range)>();
                    var named = new HashSet<string>();
                    foreach (var (name, pos) in import.Implements)
                    {
                        if (!named.Add(name))
                            throw new CompileError(pos, $"'{name}' is named twice after 'implements'. Remove one");
                        if (!mainModule.Imports.TryGetValue(name, out var api))
                        {
                            if (mainModule.Syntax.BrokenNames.Any(b => b.Name == name)) throw new FollowOnError();
                            throw new CompileError(pos, $"'{name}' is not a module the main file imports; 'implements' names interface modules. Add 'import {name}' to the main file");
                        }
                        // Decision 123: its helpers would go into an overlay file the levels cannot call.
                        if (api.OverlayArea is { } apiArea)
                            throw new CompileError(pos, $"'{name}' is placed in the overlay area '{apiArea}'; an interface must be resident. Remove '@ {apiArea}' from its import");
                        if (module.InBank && module.Unit is { } unit && !inBank.TryAdd((api, unit), module))
                            throw new CompileError(pos,
                                $"{inBank[(api, unit)].Name} and {module.Name} both implement {name} in bank {module.Bank} of the bank area '{import.Area}', so their tables would be on the same bytes. " +
                                "Put them in different banks");
                        // Both imports are used, by the table (spec 13).
                        _usedImports.Add((mainModule, name));
                        _usedImports.Add((importer, import.Name));
                        foreach (var entry in Implement(module, api, area, import.Area, pos))
                        {
                            var range = new AddressRange(entry.Routine.FixedAddress!.Value, entry.Routine.FixedAddress.Value + 2);
                            if (tables.FirstOrDefault(t => t.Range.Overlaps(range)) is { Interface: not null } clash)
                                throw new CompileError(pos,
                                    $"The tables of {clash.Interface} and {name} overlap in {module.Name}: '{name}.{entry.Name}' at {range} and an entry of {clash.Interface} at {clash.Range}. " +
                                    "Move the routines of one interface so that each one's 3-byte jmp has its own addresses");
                            result.Add(entry);
                        }
                        foreach (var entry in result.Where(e => e.Module == module && e.Interface == api))
                            tables.Add((name, new AddressRange(entry.Routine.FixedAddress!.Value, entry.Routine.FixedAddress.Value + 2)));
                    }
                });
        // Checked in full first, so a failed import adds no edges.
        foreach (var e in result) e.Routine.Callees.Add(e.Function);
        return result;
    }

    /// <summary>The data each implementing module provides for its interfaces' volatile variables: always in the overlay's file.</summary>
    private readonly List<VariableSymbol> _tableData = [];

    /// <summary>Each implementing module's data for an interface's volatile variable, by module and variable: the module names its own (decision 120).</summary>
    private readonly Dictionary<(Module, VariableSymbol), string> _moduleData = [];

    /// <summary>
    /// The module that implements an interface (spec 12.3): a binary's main file (<c>export level_api</c>) or an
    /// overlay (<c>implements level_api</c>), and how messages name it: "has no function in <paramref name="In"/>",
    /// "write ... in <paramref name="File"/>", "data <paramref name="Providers"/> provides", and who calls its
    /// functions, for <see cref="CheckExportable"/>. Both are matched by <see cref="ImplementingFunction"/> and
    /// <see cref="ImplementingData"/> (review code-quality-semantics 20).
    /// </summary>
    private sealed record Implementer(Module Module, string In, string File, string Providers, string Caller);

    /// <summary>The interface's routines, its functions at a fixed address, by address, with the names they have inside it (the symbols' are qualified: level_api.enter).</summary>
    private static List<(string Name, FunctionSymbol Routine)> InterfaceRoutines(Module api) =>
        api.Scope.Where(e => e.Value is FunctionSymbol { FixedAddress: not null })
            .Select(e => (Name: e.Key, Routine: (FunctionSymbol)e.Value)).OrderBy(e => e.Routine.FixedAddress).ToList();

    /// <summary>The data every implementer provides at a fixed address, such as a level's header: the interface's volatile variables, by address.</summary>
    private static List<(string Name, VariableSymbol Variable)> InterfaceData(Module api) =>
        api.Scope.Where(e => e.Value is VariableSymbol { FixedAddress: not null, IsData: false, DeclaredVolatile: true })
            .Select(e => (e.Key, (VariableSymbol)e.Value)).OrderBy(e => e.Item2.FixedAddress).ToList();

    /// <summary>The function of <paramref name="who"/> that implements routine <paramref name="r"/>: the same name and registers, with a body.</summary>
    private FunctionSymbol ImplementingFunction(Module api, string name, FunctionSymbol r, Implementer who, SourcePos pos)
    {
        if (!who.Module.Scope.TryGetValue(name, out var symbol) || symbol is not FunctionSymbol f || f.FixedAddress is not null)
        {
            if (symbol is ErrorSymbol) throw new FollowOnError();
            throw new CompileError(pos,
                $"'{api.Name}.{name}' has no function in {who.In}. Write 'fn {name}({Signature.Parameters(r)}){Signature.Return(r)}' in {who.File}");
        }
        CheckExportable(f, name, f.Pos, who.Caller);
        if (!Signature.Matches(f, r))
            throw new CompileError(f.Pos,
                $"'{name}' does not match '{api.Name}.{name}': it is 'fn {name}({Signature.Parameters(f)}){Signature.Return(f)}', the interface says 'fn {name}({Signature.Parameters(r)}){Signature.Return(r)}'. Change the function to match");
        return f;
    }

    /// <summary>The data of <paramref name="who"/> for the interface's volatile variable <paramref name="v"/>: the same name, type and address.</summary>
    private VariableSymbol ImplementingData(Module api, string name, VariableSymbol v, Implementer who, SourcePos pos)
    {
        string want = $"data {name}: {v.Type} @ ${v.FixedAddress:X4}";
        if (!who.Module.Scope.TryGetValue(name, out var symbol) || symbol is not VariableSymbol d)
        {
            if (symbol is ErrorSymbol) throw new FollowOnError();
            throw new CompileError(pos, $"'{api.Name}.{name}' is data {who.Providers} provides. Declare '{want} = ...' in {who.File}");
        }
        if (!d.IsData || !d.Type.SameAs(v.Type) || d.FixedAddress != v.FixedAddress)
            throw new CompileError(d.Pos, $"'{name}' does not match '{api.Name}.{name}'. Declare it as '{want} = ...'");
        return d;
    }

    /// <summary>The entries of one interface in one module, checked.</summary>
    private List<Implementation> Implement(Module module, Module api, AddressRange area, string areaName, SourcePos pos)
    {
        var routines = InterfaceRoutines(api);
        var overlay = new Implementer(module, module.Name, module.Name, $"every module implementing {api.Name}", $"a call through {api.Name}");
        if (routines.Count == 0)
            throw new CompileError(pos, $"'{api.Name}' has no routines at a fixed address to implement. Remove it from the 'implements' list, or declare its entries as 'fn name() @ address'");

        var result = new List<Implementation>();
        int next = -1;
        foreach (var (name, r) in routines)
        {
            int at = r.FixedAddress!.Value;
            if (at < next)
                throw new CompileError(pos,
                    $"'{api.Name}.{name}' at ${at:X4} overlaps the entry before it: each entry of the table is a 3-byte jmp. Move it to ${next:X4} or later in {api.Name}");
            next = at + 3;
            if (!area.Contains(new AddressRange(at, at + 2)))
                throw new CompileError(pos,
                    $"'{api.Name}.{name}' at ${at:X4} is outside the overlay area '{areaName}' {area}, where {module.Name}'s jmp to it would go. Move the routine into the area in {api.Name}");
            if (r.FixedInputs.Count > 0)
                throw new CompileError(pos,
                    $"'{api.Name}.{name}' has fixed inputs, which a function of {module.Name} cannot receive. Declare them as parameters in registers");
            var f = ImplementingFunction(api, name, r, overlay, pos);
            _named.Add(f);
            result.Add(new Implementation(module, api, name, r, f));
        }

        // Data every implementing module provides at a fixed address, such as a level's header.
        foreach (var (name, v) in InterfaceData(api))
        {
            var span = new AddressRange(v.FixedAddress!.Value, v.FixedAddress.Value + v.Type.Size - 1);
            if (!area.Contains(span))
                throw new CompileError(pos,
                    $"'{api.Name}.{name}' at {span} is outside the overlay area '{areaName}' {area}, so {module.Name} cannot provide it. Move the variable into the area in {api.Name}, or remove 'volatile' if it is not data of the overlay");
            var d = ImplementingData(api, name, v, overlay, pos);
            _named.Add(d);
            _tableData.Add(d);
            _moduleData[(module, v)] = name;
        }
        return result;
    }
}
