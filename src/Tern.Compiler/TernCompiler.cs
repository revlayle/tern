using Tern.Compiler.Backend;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler;

/// <summary>
/// One overlay module's file (spec 8.5): its code, data and variables with initial values, loading at the start of its area.
/// <see cref="File"/> is the file in the platform's format (<c>name.prg</c>, or <c>name.xex</c> without a run address).
/// </summary>
public sealed record OverlayFile(string Name, string Area, int LoadAddress, byte[] Image, byte[] File);

/// <summary>A variable's place: its type as written (<c>u16</c>, <c>[u8; 10]</c>), and the function whose frame holds it (null for a global).</summary>
/// <param name="InChr">Tile data in a NES chr area: <paramref name="Address"/> is in the PPU's memory, not the CPU's.</param>
public sealed record VariableInfo(string Name, int Address, int Size, string Type, string? Function, bool IsData, bool InChr = false);

public sealed class CompileResult
{
    /// <summary>
    /// The program file in the platform's format (<see cref="Platform.FileOf"/>): a .prg, or a .xex with
    /// one load segment per run of bytes and the run address (none for a binary without main).
    /// </summary>
    public required byte[] File { get; init; }
    /// <summary>The bytes from <see cref="LoadAddress"/> to the highest address used, gaps as zeros.</summary>
    public required byte[] Image { get; init; }
    /// <summary>The runs of bytes the program loads, by address (the .xex's load segments).</summary>
    public required IReadOnlyList<(int Address, byte[] Bytes)> Segments { get; init; }
    public required int LoadAddress { get; init; }
    /// <summary>The machine the program was compiled for.</summary>
    public required Platform Platform { get; init; }
    /// <summary>Where execution starts.</summary>
    public required int Entry { get; init; }
    /// <summary>The address main returns to; -1 for a binary, which has no main.</summary>
    public required int HaltAddress { get; init; }
    public required string Listing { get; init; }
    /// <summary>Variable and function addresses by name (locals as "function.name").</summary>
    public required IReadOnlyDictionary<string, int> Symbols { get; init; }
    /// <summary>The label file, in the platform's format (<see cref="Platform.Labels"/>).</summary>
    public required string Labels { get; init; }
    public required string Report { get; init; }
    /// <summary>Every function, data item and the startup code, with the address it was placed at.</summary>
    public required IReadOnlyList<PlacedSection> Sections { get; init; }
    /// <summary>The program areas the compiler filled and how much of each is used.</summary>
    public required IReadOnlyList<PlacedArea> Areas { get; init; }
    /// <summary>One file for each module placed in an overlay area that has code or data in the program; the file is <c>name.prg</c> or <c>name.xex</c>.</summary>
    public IReadOnlyList<OverlayFile> Overlays { get; init; } = [];
    /// <summary>For a binary without <c>main</c>: the jump table, <c>setup</c> first, then the exports, each with its address.</summary>
    public IReadOnlyList<(string Name, int Address)> Entries { get; init; } = [];
    /// <summary>For a binary without <c>main</c>: the text of its interface module (spec 12.3), or null.</summary>
    public string? ApiModule { get; init; }
    /// <summary>The module's name, such as <c>player_api</c>; the file is <c>player_api.tern</c>.</summary>
    public string? ApiModuleName { get; init; }
    /// <summary>Every instruction with the source line it belongs to (the source map; <see cref="Tools.LineTable"/>).</summary>
    public IReadOnlyList<PlacedInstruction> Instructions { get; init; } = [];
    /// <summary>Every global, data item, local and parameter with its address and size (locals as "function.name").</summary>
    public IReadOnlyDictionary<string, VariableInfo> Variables { get; init; } = new Dictionary<string, VariableInfo>();
    /// <summary>Every function declared in the program and its modules ("module.name"), compiled or not.</summary>
    public IReadOnlySet<string> DeclaredFunctions { get; init; } = new HashSet<string>();
    /// <summary>The window where banks are switched (UxROM's $8000-$BFFF), or null: code there is told apart by the selected bank.</summary>
    public AddressRange? BankWindow { get; init; }
    /// <summary>The place (<see cref="CodePlace"/>) of the code at <paramref name="address"/>, with <paramref name="selectedBank"/> shown in <see cref="BankWindow"/>.</summary>
    public int PlaceOf(int address, int selectedBank) =>
        BankWindow is { } w && address >= w.First && address <= w.Last ? CodePlace.Of(address, selectedBank) : address;
    /// <summary>The encoding of the program's literals without a prefix, for showing its screen as text (NES: its nametable); null when it has none.</summary>
    public CharEncoding? TextEncoding { get; init; }
    /// <summary>The warnings (spec 13), sorted by place; none with <see cref="WarningMode.Off"/>.</summary>
    public IReadOnlyList<CompileError> Warnings { get; internal set; } = [];
    /// <summary>On a cartridge: the program sets the IRQ vector, so <c>tern run</c> runs its handler on <c>brk</c> (spec 12.2).</summary>
    public bool SetsIrqVector { get; init; }
    /// <summary>The files the compile read: the main file and every module it imports, the library's included. No output may replace one.</summary>
    public IReadOnlyList<string> SourceFiles { get; init; } = [];
}

/// <summary>What the compiler does with warnings (spec 13).</summary>
public enum WarningMode { Report, Off, AsErrors }

/// <summary>
/// What the checks found (<see cref="TernCompiler.Check"/>), without building: the files as parsed and
/// declared, also when they have errors, and every error and warning.
/// </summary>
/// <param name="Modules">The main file (last) and the modules it imports, with their syntax trees and scopes; empty if the main file could not be loaded.</param>
/// <param name="Program">The checked program, or null when there are errors.</param>
/// <param name="All">The errors and warnings, sorted by place (spec 13).</param>
/// <param name="MoreNotReported"><c>--max-errors</c> stopped the checks: more errors were not reported.</param>
public sealed record CheckResult(IReadOnlyList<Module> Modules, CheckedProgram? Program, IReadOnlyList<CompileError> All, bool MoreNotReported)
{
    public IReadOnlyList<CompileError> Errors => All.Where(e => !e.IsWarning).ToList();
    public IReadOnlyList<CompileError> Warnings => All.Where(e => e.IsWarning).ToList();
    /// <summary>The checks found no error. The build may still find one (allocation, code generation, layout): see <see cref="TernCompiler.Check"/>.</summary>
    public bool Ok => Program is not null;

    /// <summary>The errors and warnings as tools report them (<c>tern --json</c>); <paramref name="defaultFile"/> for source given without a path.</summary>
    public IReadOnlyList<Tools.Diagnostic> Diagnostics(string defaultFile) => All.Select(e => Tools.Diagnostic.From(e, defaultFile)).ToList();
}

public static partial class TernCompiler
{
    /// <summary>The standard library: the <c>lib</c> folder next to the compiler.</summary>
    public static string DefaultLibraryDirectory => Path.Combine(AppContext.BaseDirectory, "lib");

    /// <param name="sourcePath">The source file's path. <c>include_bytes</c> paths and imports of your own modules are relative to its directory.</param>
    /// <param name="libraryDirectory">Where <c>import</c> looks first; <see cref="DefaultLibraryDirectory"/> if null.</param>
    /// <param name="maxErrors">Report only the first this many errors (<c>--max-errors</c>); null for no limit.</param>
    /// <exception cref="CompileError">
    /// The first error. Errors found by the parser and the checks are all reported (spec 13): <see cref="CompileError.All"/>
    /// has them sorted by place. Allocation, code generation and layout run only without them, and report one error.
    /// </exception>
    /// <param name="warnings">Report warnings (spec 13), leave them out (<c>--no-warnings</c>), or report each as an error (<c>--warnings-as-errors</c>).</param>
    /// <param name="warnLibrary">Warn about library modules too, which programs never are (for the repository's test of its library).</param>
    /// <param name="optimize">Run the optimizer over each function. Off only in tests, which compare a program's results both ways.</param>
    public static CompileResult Compile(string source, Platform? platform = null, string? sourcePath = null, string? libraryDirectory = null, int? maxErrors = null,
        WarningMode warnings = WarningMode.Report, bool warnLibrary = false, bool optimize = true) =>
        OnLargeStack(() => CompileHere(source, platform, sourcePath, libraryDirectory, maxErrors, warnings, warnLibrary, optimize));

    /// <summary>
    /// Parses and checks a program without building it, and never throws a <see cref="CompileError"/>:
    /// the errors and warnings are in the result, with the files as far as they were understood (for a
    /// language server, M28). The checks are those of <see cref="Compile"/>, with the same errors. Errors that
    /// only allocation, code generation and layout find (zero page full, an area too small, a fixed address
    /// over the program) come from <see cref="Compile"/> only.
    /// </summary>
    /// <param name="openFile">
    /// The text of a module open in an editor, by its full path, used in place of the file on disk; null
    /// for a file that is not open. The main file's text is <paramref name="source"/>.
    /// </param>
    public static CheckResult Check(string source, Platform? platform = null, string? sourcePath = null, string? libraryDirectory = null, int? maxErrors = null,
        WarningMode warnings = WarningMode.Report, Func<string, string?>? openFile = null) =>
        OnLargeStack(() =>
        {
            var checking = new Checking(platform ?? Platform.C64, maxErrors, warnings);
            try
            {
                var program = checking.Run(source, sourcePath, libraryDirectory, warnLibrary: false, openFile);
                return new CheckResult(checking.Modules, program, checking.Warnings, false);
            }
            catch (CompileError e)
            {
                return new CheckResult(checking.Modules, null, e.WithWarnings, e.MoreNotReported);
            }
        });

    /// <summary>
    /// The stack a compile runs on. The passes walk the syntax tree by recursion, up to
    /// <see cref="Parser.MaxDepth"/> levels deep, which takes a few kilobytes a level in the checker; the
    /// caller's stack (1 MB on Windows) is too small for that. Reserved, not committed.
    /// </summary>
    private const int StackSize = 64 * 1024 * 1024;

    /// <summary>Runs <paramref name="work"/> on a thread with a <see cref="StackSize"/> stack and returns its result or throws its exception.</summary>
    private static T OnLargeStack<T>(Func<T> work)
    {
        T result = default!;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? error = null;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var uiCulture = System.Globalization.CultureInfo.CurrentUICulture;
        var thread = new Thread(() =>
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
            System.Globalization.CultureInfo.CurrentUICulture = uiCulture;
            try { result = work(); }
            catch (Exception e) { error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
        }, StackSize);
        thread.Start();
        thread.Join();
        error?.Throw();
        return result;
    }

    private static CompileResult CompileHere(string source, Platform? platform, string? sourcePath, string? libraryDirectory, int? maxErrors,
        WarningMode warnings, bool warnLibrary, bool optimize)
    {
        platform ??= Platform.C64;
        var checking = new Checking(platform, maxErrors, warnings);
        var program = checking.Run(source, sourcePath, libraryDirectory, warnLibrary, openFile: null);
        try
        {
            var result = Build(program, platform, sourcePath, optimize);
            result.Warnings = checking.Warnings;
            return result;
        }
        catch (CompileError e) when (checking.HasWarnings)
        {
            checking.AddWarnings(e);
            throw;
        }
    }

    /// <summary>
    /// The first half of a compile, shared by <see cref="Compile"/> and <see cref="Check"/>: loading, parsing
    /// and the checks, with every error and warning they find (spec 13).
    /// </summary>
    private sealed class Checking(Platform platform, int? maxErrors, WarningMode warnings)
    {
        private readonly ErrorList _errors = new(maxErrors);
        private readonly List<CompileError>? _found = warnings == WarningMode.Off ? null : [];

        /// <summary>The files loaded, as far as they were (see <see cref="CheckResult.Modules"/>).</summary>
        public List<Module> Modules { get; private set; } = [];
        public bool HasWarnings => _found is { Count: > 0 };
        /// <summary>The warnings, sorted by place.</summary>
        public IReadOnlyList<CompileError> Warnings => _found is null ? [] : _errors.Sort(_found);

        /// <exception cref="CompileError">The first error, carrying all of them and the warnings.</exception>
        public CheckedProgram Run(string source, string? sourcePath, string? libraryDirectory, bool warnLibrary, Func<string, string?>? openFile)
        {
            Modules = ModuleLoader.Load(source, sourcePath, platform, libraryDirectory ?? DefaultLibraryDirectory, _errors, openFile);
            CheckedProgram program;
            // Warnings are found only when the checks found no error; a later error (the call graph's, allocation,
            // layout) is reported with them, sorted by place (spec 13).
            try { program = Checker.Check(Modules, platform, _errors, _found, warnLibrary); }
            catch (CompileError e) when (HasWarnings)
            {
                AddWarnings(e);
                throw;
            }
            if (warnings == WarningMode.AsErrors && HasWarnings) ThrowWarningsAsErrors();
            return program;
        }

        /// <summary>Gives a later error the warnings found before it; with <c>--warnings-as-errors</c>, they are errors instead.</summary>
        public void AddWarnings(CompileError e)
        {
            if (warnings == WarningMode.AsErrors) ThrowWarningsAsErrors();
            e.WithWarnings = _errors.Sort(e.All.Concat(_found!));
        }

        // --warnings-as-errors: each warning is an error, and the build stops (spec 13).
        private void ThrowWarningsAsErrors()
        {
            foreach (var w in _found!) _errors.Add(new CompileError(w.Pos, w.Description));
            _errors.ThrowIfAny();
        }
    }

    private static string Bytes(int n) => n == 1 ? "1 byte" : $"{n} bytes";

    private static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    /// <summary>A program's export table is placed like fixed data: in a program or reserved area (spec 12.3).</summary>
    private static void CheckExportTable(CheckedProgram program, AddressRange range)
    {
        var layout = program.Layout;
        if (layout.Program.Concat(layout.Reserved).Any(a => a.Contains(range))) return;
        throw new CompileError(program.Modules[^1].Syntax.ExportTable!.Address.Pos,
            $"The export table at {range} is not in a program or reserved area. " +
            $"Program areas: {MemoryLayout.List(layout.Program)}; reserved: {(layout.Reserved.Count == 0 ? "none" : MemoryLayout.List(layout.Reserved))}. " +
            "Give it an address in one of them");
    }

    /// <summary>
    /// Data placed with <c>@ address</c> must lie in a program or reserved area (spec 8.4): anywhere
    /// else is memory the program was not given.
    /// </summary>
    private static void CheckFixedData(CheckedProgram program, RamPlan plan)
    {
        var layout = program.Layout;
        foreach (var g in plan.FixedImageGlobals.Where(g => g.FixedAddress is not null))
        {
            var range = new AddressRange(g.Address, g.Address + g.Type.Size - 1);
            if (g.OverlayArea is { } areaName)
            {
                if (layout.PlacedArea(areaName) is { } own && own.Contains(range)) continue;
                throw new CompileError(g.Pos,
                    $"'{g.Name}' at {range} is outside the {(g.Module!.InBank ? "bank" : "overlay")} area {areaName}" +
                    (layout.PlacedArea(areaName) is { } a0 ? $" ({a0})" : "") +
                    ", where its module is loaded. Fixed data of an overlay module must lie in its own area; put data for the resident program in the main file or a resident module");
            }
            foreach (var (areaName2, area2) in layout.Overlays.Concat(layout.Banks))
                    if (area2.Overlaps(range))
                        throw new CompileError(g.Pos,
                            $"'{g.Name}' at {range} is in the overlay area {areaName2} ({area2}), but it belongs to the resident program. Fixed data in an overlay area belongs in an overlay module, which is loaded there. Move it to that module");
            if (layout.Program.Concat(layout.Reserved).Any(a => a.Contains(range))) continue;
            throw new CompileError(g.Pos,
                $"'{g.Name}' at {range} is not in a program or reserved area. " +
                $"Program areas: {MemoryLayout.List(layout.Program)}; reserved: {(layout.Reserved.Count == 0 ? "none" : MemoryLayout.List(layout.Reserved))}. " +
                "Add a 'reserved' line for it to the memory block");
        }
    }

    /// <summary>
    /// A fixed-address variable must not overlap the program, its data or the variables the compiler
    /// places (spec 4); a growing program would otherwise overwrite it silently. Fixed-address
    /// variables may overlap each other.
    /// </summary>
    private static void CheckFixedAddresses(CheckedProgram program, Platform platform, AssembledCode code, RamPlan plan)
    {
        var inZeroPage = program.Globals.Where(g => g.FixedAddress is null && g.Region is null)
            .Concat(program.Functions.SelectMany(f => f.FrameVariables))
            .Where(v => v.Address is >= 0 and < 0x100).ToList();
        var zeroPage = program.Layout.ZeroPage;
        foreach (var g in program.Globals)
        {
            if (g.FixedAddress is not int first || g.IsData) continue;
            var range = new AddressRange(first, first + g.Type.Size - 1);
            foreach (var area in code.Areas)
            {
                var used = new AddressRange(area.Free.First, area.UsedEnd - 1);
                if (area.UsedEnd > area.Free.First && range.Overlaps(used))
                    throw new CompileError(g.Pos,
                        $"'{g.Name}' at {range} overlaps the program and its variables at {used}. " +
                        $"Move '{g.Name}', or make the program smaller");
            }
            foreach (var module in code.Sections.Where(s => s.Overlay is not null && s.Size > 0).GroupBy(s => s.Overlay!))
            {
                var file = new AddressRange(module.Min(s => s.Address), module.Max(s => s.Address + s.Size - 1));
                if (!g.DeclaredVolatile && module.Any(s => range.Overlaps(new AddressRange(s.Address, s.Address + s.Size - 1))))
                    throw new CompileError(g.Pos,
                        $"'{g.Name}' at {range} overlaps the overlay {module.Key} at {file}, which is loaded there. " +
                        $"Declare it 'volatile var {g.Name}' if the overlap is intended; otherwise name the overlay's own variable ({module.Key}.name) or move '{g.Name}' out of the overlay's area");
            }
            foreach (var (regionName, use) in plan.RegionUse)
                if (range.Overlaps(use))
                    throw new CompileError(g.Pos,
                        $"'{g.Name}' at {range} overlaps the variables the compiler placed in region '{regionName}' at {use}. Move '{g.Name}', or place it with the region");
            foreach (var section in code.Sections.Where(s => s.IsFixed && s.Size > 0 && (s.Overlay is null || (g.Module?.OverlayArea is not null && g.Module.Unit == s.Overlay))))
                if (range.Overlaps(new AddressRange(section.Address, section.Address + section.Size - 1)))
                    throw new CompileError(g.Pos,
                        $"'{g.Name}' at {range} overlaps the data '{section.Name}' at {new AddressRange(section.Address, section.Address + section.Size - 1)}. Move one of them");
            foreach (var v in inZeroPage)
                if (first <= v.Address + v.Type.Size - 1 && range.Last >= v.Address)
                    throw new CompileError(g.Pos,
                        $"'{g.Name}' at {range} overlaps '{v.QualifiedName}' at {new AddressRange(v.Address, v.Address + v.Type.Size - 1)}, " +
                        $"which the compiler placed in zero page. Move '{g.Name}' out of {MemoryLayout.ListZeroPage(zeroPage)}");
        }
    }

    /// <summary>
    /// Startup code: the platform's own first steps (<paramref name="prelude"/>), then set up zero-page
    /// globals and clear the uninitialized ones in RAM (except noinit ones).
    /// </summary>
    /// <param name="ramCleared">The prelude cleared all of RAM (the NES): zeros are not stored again.</param>
    /// <param name="initial">The initial values in ROM that the startup copies (<see cref="RamPlan.CopiedGlobals"/>).</param>
    private static List<AsmItem> Startup(RamPlan plan, Label bss, Func<FunctionSymbol, Label> labelOf, IEnumerable<AsmItem> prelude,
        bool ramCleared = false, Label? initial = null)
    {
        var items = new List<AsmItem> { new Instr(Cld) };
        items.AddRange(prelude);

        int? a = null;
        foreach (var g in plan.ZeroPageGlobals.Where(g => !g.NoInit && !(ramCleared && g.InitialBytes is null)))
        {
            var bytes = ByteLayout(g, g.InitialBytes ?? new byte[g.Type.Size]);
            var addresses = AddressBytes(g);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (addresses.TryGetValue(i, out var f))
                {
                    Operand half = f.Fill.Function is { } fn
                        ? new ImmLabelByte(labelOf(fn), f.High, f.Fill.Offset)
                        : new ImmVarByte(f.Fill.Variable!, f.High, f.Fill.Offset);
                    items.Add(new Instr(Lda, Immediate, half));
                    a = null;
                }
                else if (a != bytes[i])
                {
                    items.Add(new Instr(Lda, Immediate, new Imm(bytes[i])));
                    a = bytes[i];
                }
                items.Add(new Instr(Sta, Absolute, new VarOperand(g, i)));
            }
        }

        if (plan.CopiedSize > 0)
        {
            items.Add(new Comment("copy the initial values from ROM"));
            CopyBlock(items, plan.CopiedSize, p => new LabelOperand(initial!, p), p => new LabelOperand(bss, plan.CopiedStart + p), "copy");
        }
        if (plan.ZeroedSize > 0 && !ramCleared)
            ClearBlock(items, plan.ZeroedSize, p => new LabelOperand(bss, plan.ZeroedStart + p), "clear");
        foreach (var block in plan.RegionClears.Where(b => !ramCleared || b.First > 0x07FF))
            ClearBlock(items, block.Size, p => new AddrOperand(block.First + p), $"clear_{block.First:X4}_");
        return items;
    }

    /// <summary>Zeroes <paramref name="size"/> bytes, 256 at a time; <paramref name="at"/> gives the address of byte <c>p</c>.</summary>
    private static void ClearBlock(List<AsmItem> items, int size, Func<int, Operand> at, string name)
    {
        items.Add(new Instr(Lda, Immediate, new Imm(0)));
        for (int p = 0; p < size / 256; p++)
        {
            var loop = new Label($"{name}{p}");
            items.Add(new Instr(Ldx, Immediate, new Imm(0)));
            items.Add(new LabelDef(loop));
            items.Add(new Instr(Sta, AbsoluteX, at(p * 256)));
            items.Add(new Instr(Inx));
            items.Add(new Instr(Bne, Relative, new LabelOperand(loop)));
        }
        int rest = size % 256;
        if (rest > 0)
        {
            var loop = new Label(name);
            items.Add(new Instr(Ldx, Immediate, new Imm(rest)));
            items.Add(new LabelDef(loop));
            items.Add(new Instr(Sta, AbsoluteX, at(size / 256 * 256 - 1)));
            items.Add(new Instr(Dex));
            items.Add(new Instr(Bne, Relative, new LabelOperand(loop)));
        }
    }

    /// <summary>
    /// The initial value of a global in the program image. Addresses in it (spec 4) are filled in
    /// by the assembler from the functions' labels and the variables' addresses.
    /// </summary>
    private static IEnumerable<AsmItem> ImageBytes(VariableSymbol g, Func<FunctionSymbol, Label> labelOf)
    {
        var bytes = ByteLayout(g, g.InitialBytes ?? new byte[g.Type.Size]);
        int start = 0;
        foreach (var (at, (fill, high)) in AddressBytes(g).OrderBy(f => f.Key))
        {
            if (at > start) yield return new DataBytes(bytes[start..at]);
            yield return new AddressByte(fill.Function is { } f ? labelOf(f) : null, fill.Variable, fill.Offset, high, fill.Name);
            start = at + 1;
        }
        if (start < bytes.Length) yield return new DataBytes(bytes[start..]);
    }

    /// <summary>Where the bytes of addresses are in a global's memory layout, and which half of the address each is.</summary>
    private static Dictionary<int, (AddressFill Fill, bool High)> AddressBytes(VariableSymbol g)
    {
        var bytes = new Dictionary<int, (AddressFill, bool)>();
        foreach (var fill in g.AddressFills)
        {
            if (fill.Part == AddressPart.Word)
            {
                bytes[LayoutIndex(g, fill.Index)] = (fill, false);
                bytes[LayoutIndex(g, fill.Index + 1)] = (fill, true);
            }
            else bytes[LayoutIndex(g, fill.Index)] = (fill, fill.Part == AddressPart.High);
        }
        return bytes;
    }

    /// <summary>Where byte <paramref name="i"/> of a value is in its memory layout (see <see cref="ByteLayout"/>).</summary>
    private static int LayoutIndex(VariableSymbol v, int i)
    {
        if (!v.Type.IsArray || v.Type.Element!.Size == 1 || v.Type.Linear) return i;
        int size = v.Type.Element.Size;
        return i % size * v.Type.Length + i / size;
    }

    /// <summary>
    /// Converts a value's bytes (elements in order, each little endian or in its record's layout) to
    /// its layout in memory: an array of 16-bit values or of structs, unless linear, is split into one
    /// table per element byte (spec 3.2, 3.3): byte k of element i is at k * length + i.
    /// </summary>
    private static byte[] ByteLayout(VariableSymbol v, byte[] bytes)
    {
        if (!v.Type.IsArray || v.Type.Element!.Size == 1 || v.Type.Linear) return bytes;
        var split = new byte[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) split[LayoutIndex(v, i)] = bytes[i];
        return split;
    }
}
