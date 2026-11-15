using System.Text;
using Tern.Compiler.Backend;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler;

/// <summary>
/// One overlay module's file (spec 8.5): its code, data and variables with initial values, loading at the start of its area.
/// <see cref="File"/> is the file in the platform's format (<c>name.prg</c>, or <c>name.xex</c> without a run address).
/// </summary>
public sealed record OverlayFile(string Name, string Area, int LoadAddress, byte[] Image, byte[] File);

/// <summary>A variable's place: its type as written (<c>u16</c>, <c>[u8; 10]</c>), and the function whose frame holds it (null for a global).</summary>
public sealed record VariableInfo(string Name, int Address, int Size, string Type, string? Function, bool IsData);

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
}

/// <summary>What the compiler does with warnings (spec 13).</summary>
public enum WarningMode { Report, Off, AsErrors }

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
        WarningMode warnings = WarningMode.Report, bool warnLibrary = false, bool optimize = true)
    {
        platform ??= Platform.C64;

        var errors = new ErrorList(maxErrors);
        var found = warnings == WarningMode.Off ? null : new List<CompileError>();
        var modules = ModuleLoader.Load(source, sourcePath, platform, libraryDirectory ?? DefaultLibraryDirectory, errors);
        CheckedProgram program;
        // Warnings are found only when the checks found no error; a later error (the call graph's, allocation,
        // layout) is reported with them, sorted by place (spec 13).
        try { program = Checker.Check(modules, platform, errors, found, warnLibrary); }
        catch (CompileError e) when (found is { Count: > 0 })
        {
            AddWarnings(e);
            throw;
        }
        if (warnings == WarningMode.AsErrors && found!.Count > 0) ThrowWarningsAsErrors();
        try
        {
            var result = Build(program, platform, sourcePath, optimize);
            result.Warnings = found is null ? [] : errors.Sort(found);
            return result;
        }
        catch (CompileError e) when (found is { Count: > 0 })
        {
            AddWarnings(e);
            throw;
        }

        void AddWarnings(CompileError e)
        {
            if (warnings == WarningMode.AsErrors) ThrowWarningsAsErrors();
            e.WithWarnings = errors.Sort(e.All.Concat(found!));
        }

        // --warnings-as-errors: each warning is an error, and the build stops (spec 13).
        void ThrowWarningsAsErrors()
        {
            foreach (var w in found!) errors.Add(new CompileError(w.Pos, w.Description));
            errors.ThrowIfAny();
        }
    }

    /// <summary>Allocation, code generation and layout, for a program whose checks found no error.</summary>
    private static CompileResult Build(CheckedProgram program, Platform platform, string? sourcePath, bool optimize)
    {
        var gen = new CodeGen(program, optimize);
        var functionCode = gen.GenerateFunctions();
        // Indexed arrays placed so that they do not cross a page, with the variables starting on one;
        // if the program areas have no room for that, the variables are placed one after another (F12).
        RamPlan plan = null!;
        List<AsmItem> items = null!;
        AssembledCode code = null!;
        Dictionary<VariableSymbol, Label> imageLabels = null!;
        var layout = program.Layout;
        bool binary = program.IsBinary;
        var start = new Label("start");
        var halt = new Label("halt");
        var bss = new Label("bss");
        var setup = new Label("setup");
        // Apple II: the copies the startup code makes (spec 12.2). They are known only once the program
        // is assembled, and the copying code moves what follows it, so assembly repeats until they agree.
        bool system = platform.Format == OutputFormat.System && !binary;
        var moved = new Label("moved");
        var quit = new Label("quit");
        // The prefix code comes with io (question 150).
        bool withIo = system && program.Modules.Any(m => m.IsLibrary && m.Name == "io");
        var prefix = withIo ? new Label("prefix_parameters") : null;
        List<AreaMove> moves = [];
        byte[]? systemFile = null;
        // A cartridge (the NES): code in ROM, variables in RAM, initial values copied from ROM (spec 12.2).
        var cartridge = layout.Cartridge;
        var initial = new Label("initial");
        var rti = new Label("rti");
        var bankZero = new Label("bank_zero");
        int passes = 0;
        // Indexed reads only: a store or a read-modify-write takes the same cycles across a page.
        var indexed = new Dictionary<VariableSymbol, List<int>>();
        foreach (var i in functionCode.OfType<Instr>())
            if (i.Mode is Tern.Cpu.AddressingMode.AbsoluteX or Tern.Cpu.AddressingMode.AbsoluteY or Tern.Cpu.AddressingMode.ZeroPageX or Tern.Cpu.AddressingMode.ZeroPageY
                && AsmEffects.ReadsMemory(i.Mnemonic, i.Mode) && !AsmEffects.WritesMemory(i.Mnemonic, i.Mode)
                && i.Operand is VarOperand { Variable: var v } o)
                (indexed.TryGetValue(v.Storage, out var list) ? list : indexed[v.Storage] = []).Add(v.AliasOffset + o.Offset);
        // NROM fills $C000-$FFFF first, so a small program is NROM-128; one that does not fit there is
        // laid out again in one area, $8000-$FFFF, so that a function or table may be larger than 16 KB
        // (review M27, finding 4).
        var codeAreas = layout.Program;
        bool CanJoin() => cartridge is { Board.BankArea: null } && Joined(codeAreas).Count < codeAreas.Count;
        for (bool byPage = true; ;)
        {
            plan = Allocator.Allocate(program, platform, byPage, indexed);
            foreach (var v in program.Globals.Concat(program.Functions.SelectMany(f => f.FrameVariables)))
                if (v.Address is >= 0 and < 0x100 && v.FixedAddress is null)
                    foreach (int o in v.JumpVectorOffsets)
                        if (((v.Address + o) & 0xFF) == 0xFF)
                            throw new CompileError(v.Pos, $"'{v.Name}' is used as jmp ({v.Name}{o:+0;-0;''}) and was placed so that the vector starts at $FF, where the 6502 reads it wrongly. Place it at a fixed address whose low byte is not $FF");

            // The C64's BASIC line must be where the file loads; a .xex names its run address.
            if (!binary && platform.Header.Length > 0 && layout.Program[0].First != platform.LoadAddress)
                throw new CompileError(program.Main.Pos,
                    $"A program with 'main' starts at ${platform.LoadAddress:X4}, where the {platform.Name.ToUpperInvariant()} BASIC line goes, but its first program area begins at ${layout.Program[0].First:X4}. " +
                    (layout.ProgramReplaced ? $"Start the first 'program' line of the memory block at ${platform.LoadAddress:X4}" : "Move the 'reserved' or 'region' range that covers it"));
            CheckFixedData(program, plan);
            if (program.ExportTableAddress is int tableAt)
                CheckExportTable(program, new AddressRange(tableAt, tableAt + 3 * program.Exports.Count - 1));

            var vectors = cartridge is null ? default : CheckVectors(plan);
            if (!binary)
            {
                items = [new SectionStart("startup"), new DataBytes(platform.Header), new LabelDef(start), new Comment("startup")];
                if (cartridge is not null) items.Add(new Instr(Sei));
                items.AddRange(Startup(plan, bss, gen.LabelOf,
                    system ? Apple2Start(moves, moved, quit, prefix, exact: passes < 4) : cartridge is not null ? NesStart(cartridge.Board.BankArea is null ? null : bankZero) : [],
                    ramCleared: cartridge is not null, initial));
                items.Add(new Instr(Jsr, Absolute, new LabelOperand(gen.LabelOf(program.Main))));
                items.Add(new LabelDef(halt));
                items.Add(new Instr(Jmp, Absolute, new LabelOperand(halt)));
                if (system) items.AddRange(Apple2Quit(quit, prefix));
                if (cartridge is not null)
                {
                    items.Add(new Comment("an interrupt vector the program does not set"));
                    items.Add(new LabelDef(rti));
                    items.Add(new Instr(Rti));
                    if (cartridge.Board.BankArea is not null)
                    {
                        // The latch also sees this byte (bus conflicts): writing 0 over a 0 selects bank 0.
                        items.Add(new LabelDef(bankZero));
                        items.Add(new DataBytes([0]));
                    }
                }
            }
            else
            {
                // A binary starts with its jump table: setup, then the exports in the order they were listed,
                // or at their routines' addresses in the interface module, with zeros between (spec 12.3).
                items = [new SectionStart("jump table"), new LabelDef(start), new Instr(Jmp, Absolute, new LabelOperand(setup))];
                int at = layout.Program[0].First + 3;
                foreach (var export in program.Exports)
                {
                    if (program.ExportedAs.TryGetValue(export, out var routine))
                    {
                        if (routine.FixedAddress > at) items.Add(new DataBytes(new byte[routine.FixedAddress!.Value - at]));
                        at = routine.FixedAddress!.Value;
                    }
                    items.Add(new Instr(Jmp, Absolute, new LabelOperand(gen.LabelOf(export))));
                    at += 3;
                }
                items.Add(new SectionStart("setup"));
                items.Add(new LabelDef(setup));
                items.AddRange(Startup(plan, bss, gen.LabelOf, []));
                items.Add(new Instr(Rts));
            }
            items.AddRange(functionCode);
            if (cartridge is not null)
            {
                items.AddRange(NesVectors(start, rti, vectors));
                if (plan.CopiedSize > 0) items.AddRange(InitialValues(plan, initial, gen.LabelOf));
            }
            // implements (spec 8.5): a jmp at each interface routine's address, in the overlay's file; its code goes around them.
            foreach (var entry in program.Implementations)
            {
                items.Add(new SectionStart($"{entry.Interface.Name}.{entry.Name}", entry.Routine.FixedAddress, Overlay: entry.Module.Unit));
                items.Add(new Instr(Jmp, Absolute, new LabelOperand(gen.LabelOf(entry.Function))));
            }
            if (program.ExportTableAddress is int table)
            {
                // A program's export table: one jmp per function, in the order listed (spec 12.3).
                items.Add(new SectionStart("export table", table));
                foreach (var export in program.Exports)
                    items.Add(new Instr(Jmp, Absolute, new LabelOperand(gen.LabelOf(export))));
            }

            imageLabels = new Dictionary<VariableSymbol, Label>();
            foreach (var g in plan.ImageGlobals)
            {
                var label = new Label(g.Name);
                imageLabels[g] = label;
                // A bank's data is in the bank, with its code (spec 8.5).
                items.Add(new SectionStart(g.Name, Overlay: g.Module?.InBank == true ? g.OverlayUnit : null));
                if (ImagePadding(g) is { } padding) items.Add(padding);
                items.Add(new LabelDef(label));
                items.AddRange(ImageBytes(g, gen.LabelOf));
            }
            foreach (var g in plan.FixedImageGlobals)
            {
                items.Add(new SectionStart(g.Name, g.Address, Overlay: g.OverlayUnit));
                items.AddRange(ImageBytes(g, gen.LabelOf));
            }
            // Overlay modules: their variables are bytes of the file, zeros where there is no initial value.
            foreach (var g in plan.OverlayGlobals)
            {
                var label = new Label(g.Name);
                imageLabels[g] = label;
                items.Add(new SectionStart(g.Name, Overlay: g.Module!.Name));
                if (ImagePadding(g) is { } padding) items.Add(padding);
                items.Add(new LabelDef(label));
                items.AddRange(ImageBytes(g, gen.LabelOf));
            }
            // Variables after the program start at bss. Move them all, if needed, so that no jmp (...)
            // vector among them starts at an address ending in $FF.
            var ramVectors = plan.ZeroedGlobals.Concat(plan.NoinitGlobals).Concat(plan.CopiedGlobals).Concat(plan.FrameVariables)
                .SelectMany(e => e.Var.JumpVectorOffsets.Select(o => e.Offset + o)).ToList();
            string reason = string.Join(" and ", new[]
            {
                plan.PageArrays.Count > 0 ? "indexed reads of the variables do not cross a page" : null,
                plan.ZeroedGlobals.Concat(plan.NoinitGlobals).Concat(plan.CopiedGlobals).Any(e => e.Var.Alignment > 1) ? "aligned variables start at a multiple of their alignment" : null,
            }.OfType<string>());
            items.Add(new Reserve(bss, plan.TotalSize, ramVectors, plan.StartModulus, plan.StartResidue, reason));

            // Each overlay, and each bank (its modules together), is laid out from its area's start.
            var overlayAreas = program.Modules.Where(m => m.OverlayArea is not null).GroupBy(m => m.Unit!)
                .ToDictionary(g => g.Key, g => layout.PlacedArea(g.First().OverlayArea!)!.Value);
            // Placed by page, the variables may not fit, or may now overlap a fixed-address variable
            // (review M18 part 3, finding 1): then place them one after another. That try reports
            // any error as before.
            try
            {
                code = Assembler.Assemble(items, codeAreas, () =>
                {
                    foreach (var (g, label) in imageLabels) g.Address = label.Address;
                    foreach (var (v, offset) in plan.ZeroedGlobals.Concat(plan.NoinitGlobals).Concat(plan.CopiedGlobals)) v.Address = bss.Address + offset;
                    foreach (var (v, offset) in plan.FrameVariables) v.Address = bss.Address + offset;
                }, overlayAreas, program.Modules.Where(m => m.ImportedBy is not null && m.OverlayArea is not null).GroupBy(m => m.Unit!).ToDictionary(g => g.Key, g => g.First().ImportedBy!.Pos),
                layout.Variables);
                CheckFixedAddresses(program, platform, code, plan);
                if (system)
                {
                    var (built, needed) = SystemFile(platform, code.Segments, start.Address, moves.Count > 0 ? moved.Address - start.Address : 0,
                        program.Main.Pos, program.Modules[^1].Syntax.Memory?.Pos ?? program.Main.Pos);
                    if (!needed.SequenceEqual(moves))
                    {
                        if (++passes > 8) throw new InvalidOperationException("The Apple II's startup copies did not settle");
                        moves = needed;
                        continue;
                    }
                    systemFile = built;
                }
                break;
            }
            catch (CompileError) when (CanJoin())
            {
                codeAreas = Joined(codeAreas);
                byPage = true;
            }
            catch (Exception e) when (byPage && plan.PageArrays.Count > 0 && e is Assembler.NoPageStart or CompileError) { byPage = false; }
            catch (Assembler.NoPageStart)
            {
                // Only aligned variables are left to place the block for.
                var widest = plan.ZeroedGlobals.Concat(plan.NoinitGlobals).Concat(plan.CopiedGlobals).Select(e => e.Var).MaxBy(v => v.Alignment)!;
                throw new CompileError(widest.Pos,
                    $"The variables after the program ({plan.TotalSize} bytes) must start where '{widest.Name}' is at a multiple of ${widest.Alignment:X}, and no program area has room for them there. " +
                    "Make a program area larger, or use a smaller alignment");
            }
        }

        // The file covers every byte from the lowest to the highest address used; gaps are zeros.
        int loadAddress = code.Segments.Min(s => s.Address);
        int fileEnd = code.Segments.Max(s => s.Address + s.Bytes.Length);
        var image = new byte[fileEnd - loadAddress];
        foreach (var (address, bytes) in code.Segments) bytes.CopyTo(image, address - loadAddress);
        byte[] file;
        int prgSize = 0, prgFirst = 0;
        // In the order the main file imports them, which is the order a bank is filled in (spec 8.5).
        var banks = program.Modules.Where(m => m.InBank).OrderBy(m => m.ImportedBy!.Pos.Line).ThenBy(m => m.ImportedBy!.Pos.Column).ToList();
        int bankCount = 0;
        if (cartridge is not null) (file, prgSize, prgFirst, bankCount) = NesFile(cartridge, code.Segments, plan, banks.GroupBy(m => m.Unit!).ToDictionary(g => g.Key, g => g.First().Bank!.Value), code.Overlays);
        else file = systemFile ?? platform.FileOf(loadAddress, image, code.Segments, binary ? null : start.Address);

        var symbols = new Dictionary<string, int>();
        var variables = new Dictionary<string, VariableInfo>();
        foreach (var g in program.Globals)
        {
            symbols[g.Name] = g.Address;
            variables[g.Name] = new VariableInfo(g.Name, g.Address, g.Type.Size, g.Type.ToString(), null, g.IsData);
        }
        foreach (var f in program.Functions)
        {
            // An inline function has no code of its own, only a frame.
            if (!f.IsInline) symbols[f.Name] = gen.LabelOf(f).Address;
            foreach (var v in f.FrameVariables.Where(v => v.Kind != VariableKind.Temp))
            {
                symbols[v.QualifiedName] = v.Address;
                variables[v.QualifiedName] = new VariableInfo(v.QualifiedName, v.Address, v.Type.Size, v.Type.ToString(), f.Name, false);
            }
        }

        // The emulators accept dots inside labels, so names stay as in the listing and cannot clash (util.bump, util_bump).
        string labels = platform.Labels.Write(symbols, cartridge is null ? null : NesLabelMap(prgFirst, bankCount, banks.ToDictionary(m => m.Name, m => m.Bank!.Value)));

        var entries = new List<(string Name, int Address)>();
        string? apiModule = null, apiName = null;
        if (binary)
        {
            var use = new RegisterUse(gen, program.Functions);
            var setupChange = items.SkipWhile(i => i is not LabelDef { Label: var l } || l != setup).OfType<Instr>()
                .TakeWhile(i => i.Mnemonic != Rts)
                .Aggregate(CpuState.None, (acc, i) => acc | (AsmEffects.Of(i.Mnemonic, i.Mode).Writes & (CpuState.A | CpuState.X | CpuState.Y)));
            var table = new List<(string Name, FunctionSymbol? Function, int Address, CpuState Changed)> { ("setup", null, start.Address, setupChange) };
            for (int i = 0; i < program.Exports.Count; i++)
            {
                var f = program.Exports[i];
                table.Add((f.Name, f, program.ExportedAs.GetValueOrDefault(f)?.FixedAddress ?? start.Address + 3 * (i + 1), use.Changed(f)));
            }
            entries.AddRange(table.Select(t => (t.Name, t.Address)));
            // export level_api: each entry keeps what its routine promises to keep.
            if (program.ExportedAs.Count > 0)
            {
                var api = program.ExportedAs.Values.First().Module!;
                var interfaceSetup = (FunctionSymbol)api.Scope["setup"];
                foreach (var (name, f, _, changed) in table)
                {
                    var routine = f is null ? interfaceSetup : program.ExportedAs[f];
                    foreach (var reg in routine.Keeps.OrderBy(r => r))
                        if (changed.HasFlag(reg switch { Register.A => CpuState.A, Register.X => CpuState.X, _ => CpuState.Y }))
                            throw new CompileError(f?.Pos ?? program.Main.Pos,
                                f is null
                                    ? $"'{api.Name}.setup' promises to keep {reg}, but the binary's startup code changes it. Remove {reg} from its 'keeps' list"
                                    : $"'{name}' changes {reg}, which '{api.Name}.{name}' promises to keep. Change the function so that it leaves {reg} alone, or remove {reg} from the routine's 'keeps' list");
                }
            }

            var fileRange = new AddressRange(loadAddress, fileEnd - 1);
            // Every range the binary uses, once: a host may not reserve overlapping ranges.
            var others = new List<(string Prefix, string What, AddressRange Range)>();
            var emitted = new List<AddressRange> { fileRange };
            var counts = new Dictionary<string, int>();
            void Emit(string prefix, string what, AddressRange range)
            {
                foreach (var piece in MemoryLayout.Subtract([range], emitted))
                {
                    int n = counts[prefix] = counts.GetValueOrDefault(prefix) + 1;
                    others.Add((n == 1 ? prefix : prefix + n, what, piece));
                    emitted.Add(piece);
                }
            }
            if (code.Variables is { } vr) Emit("VARS", "variables, zeroed by setup", vr);
            foreach (var (name, use2) in plan.RegionUse) Emit(name.ToUpperInvariant(), $"region {name}", use2);
            var zpUsed = program.Globals.Where(g => g.FixedAddress is null && g.Region is null)
                .Concat(program.Functions.SelectMany(f => f.FrameVariables))
                .Where(v => v.AliasOf is null && v.Address is >= 0 and < 0x100 && v.Register is null).ToList();
            foreach (var zr in layout.ZeroPage.OrderBy(r => r.First))
            {
                var inRange = zpUsed.Where(v => v.Address >= zr.First && v.Address <= zr.Last).ToList();
                if (inRange.Count > 0)
                    Emit("ZP", "zero page used by the binary", new AddressRange(inRange.Min(v => v.Address), inRange.Max(v => v.Address + v.Type.Size - 1)));
            }
            foreach (var g in program.Globals.Where(g => g.FixedAddress is not null && !g.IsData && !g.InVolatileArea).OrderBy(g => g.FixedAddress))
                Emit("FIXED", $"'{g.Name}', at a fixed address", new AddressRange(g.FixedAddress!.Value, g.FixedAddress.Value + g.Type.Size - 1));
            foreach (var r in layout.Reserved.OrderBy(r => r.First)) Emit("RESERVED", "reserved in the binary's memory block", r);

            string baseName = sourcePath is null ? "binary" : Path.GetFileNameWithoutExtension(sourcePath);
            apiName = baseName + "_api";
            apiModule = BinaryInterface.Generate(baseName + platform.BinaryExtension, apiName, table, fileRange, others);
        }

        if (program.ExportTableAddress is int tableAddress)
        {
            // The program's table, for plug-ins: no setup, no memory ranges (spec 12.3).
            var use = new RegisterUse(gen, program.Functions);
            var table = program.Exports.Select((f, i) => (f.Name, (FunctionSymbol?)f, tableAddress + 3 * i, use.Changed(f))).ToList();
            entries.AddRange(table.Select(t => (t.Name, t.Item3)));
            string baseName = sourcePath is null ? "program" : Path.GetFileNameWithoutExtension(sourcePath);
            apiName = baseName + "_api";
            apiModule = BinaryInterface.GenerateTable(baseName + platform.FileExtension, apiName, table);
        }

        if (program.Implementations.Count > 0)
        {
            // Code around a call to an interface routine relies on what the routine promises to keep.
            var use = new RegisterUse(gen, program.Functions);
            foreach (var entry in program.Implementations)
                foreach (var reg in entry.Routine.Keeps.OrderBy(r => r))
                    if (use.Changed(entry.Function).HasFlag(reg switch { Register.A => CpuState.A, Register.X => CpuState.X, _ => CpuState.Y }))
                        throw new CompileError(entry.Function.Pos,
                            $"'{entry.Function.Name}' changes {reg}, which '{entry.Interface.Name}.{entry.Name}' promises to keep. Change the function so that it leaves {reg} alone, or remove {reg} from the routine's 'keeps' list");
        }

        var overlayFiles = new List<OverlayFile>();
        foreach (var module in program.Modules.Where(m => m.OverlayArea is not null && !m.InBank))
        {
            if (!code.Overlays.TryGetValue(module.Name, out var segments)) continue;
            int first = Math.Min(segments.Min(s => s.Address), program.Layout.Overlays[module.OverlayArea!].First);
            var overlayImage = new byte[segments.Max(s => s.Address + s.Bytes.Length) - first];
            foreach (var (address, bytes) in segments) bytes.CopyTo(overlayImage, address - first);
            overlayFiles.Add(new OverlayFile(module.Name, module.OverlayArea!, first, overlayImage,
                platform.FileOf(first, overlayImage, [(first, overlayImage)], null)));
        }

        var report = new StringBuilder();
        // A .prg holds the gaps between areas; a .xex and a system file hold only the runs of bytes.
        report.AppendLine(platform.Format == OutputFormat.Prg
            ? $"Code and data: ${loadAddress:X4}-${fileEnd - 1:X4} ({image.Length} bytes)"
            : $"Code and data: {string.Join(", ", code.Segments.Select(s => $"${s.Address:X4}-${s.Address + s.Bytes.Length - 1:X4}"))} ({code.Segments.Sum(s => s.Bytes.Length)} bytes)");
        if (systemFile is not null)
            report.AppendLine($"System file: {systemFile.Length} bytes, loaded at ${platform.LoadAddress:X4}-${platform.LoadAddress + systemFile.Length - 1:X4}"
                + (moves.Count > 0 ? $"; the startup code copies {Plural(moves.Count, "area", "areas")} into place: {string.Join(", ", moves.Select(m => $"${m.From:X4} to ${m.To:X4}, {m.Count} bytes"))}" : ""));
        if (withIo)
            report.AppendLine($"io: {PrefixSize} bytes of startup code, which set an empty prefix to the program's folder");
        if (cartridge is not null)
            report.AppendLine($"Cartridge: {cartridge.Board.Name} (mapper {cartridge.Board.Mapper}), {(cartridge.VerticalMirroring ? "vertical" : "horizontal")} mirroring, " +
                (cartridge.Board.BankArea is null ? $"{prgSize / 1024} KB of PRG ROM at ${prgFirst:X4}-$FFFF, "
                    : $"{prgSize / 1024} KB of PRG ROM in {Plural(bankCount, "bank", "banks")} of 16 KB, the last fixed at $C000-$FFFF, ") +
                (cartridge.Board.ChrRom ? $"{ChrBank / 1024} KB of CHR ROM" : "CHR RAM") +
                (plan.CopiedSize > 0 ? $"; the startup code copies {Plural(plan.CopiedSize, "byte", "bytes")} of initial values to RAM" : ""));
        string variablesAt = cartridge is null ? "after the program" : "in RAM";
        report.AppendLine(code.Variables is { } vars
            ? $"Variables {variablesAt}: {plan.TotalSize} bytes, up to ${vars.Last:X4}"
              + (plan.PageArrays.Count > 0
                  ? $", from ${vars.First:X4}, placed so that indexed reads do not cross a page: {string.Join(", ", plan.PageArrays.Select(a => $"{a.QualifiedName} ${a.Address:X4}"))}"
                  : "")
            : $"Variables {variablesAt}: 0 bytes");
        foreach (var g in program.Globals.Where(g => g.Alignment > 1).OrderBy(g => g.Address))
            report.AppendLine($"Aligned {g.Name}: ${g.Address:X4} (@ align({g.Alignment})), {Plural(AlignmentPadding(g, plan, code), "byte", "bytes")} of padding before it");
        foreach (var table in program.Globals.Where(g => g.IsOffsetTable))
            report.AppendLine($"Offset table {table.Name}: {table.Type.Size} bytes, in the program");
        // The low table follows the high one.
        foreach (var (function, pos, high, _, span) in gen.MatchTables)
            report.AppendLine($"Jump table of the indexed match at {pos} in {function.Name}: {new AddressRange(high.Address, high.Address + 2 * span - 1)}, {2 * span} bytes for {span} values");
        // The variables' own RAM (the NES) has its line above (review M27, suggestion 7).
        var programAreas = code.Areas.Where(a => !layout.Variables.Any(v => v.Contains(a.Free))).ToList();
        if (programAreas.Count > 1 || layout.Regions.Count > 0)
        {
            report.AppendLine("Program areas:");
            foreach (var a in programAreas)
                report.AppendLine($"  {a.Free}: {a.UsedEnd - a.Free.First} of {a.Free.Size} bytes used");
            foreach (var (name, range) in layout.Regions)
                report.AppendLine(plan.RegionUse.TryGetValue(name, out var use)
                    ? $"  region {name} {range}: {use.Size} of {range.Size} bytes used"
                    : $"  region {name} {range}: unused");
        }
        foreach (var (name, range) in layout.Banks)
        {
            report.AppendLine($"Bank area {name} {range}:");
            foreach (var bank in banks.GroupBy(m => m.Bank!.Value).OrderBy(g => g.Key))
            {
                int used = code.Overlays.TryGetValue(bank.First().Unit!, out var placed) ? placed.Sum(s => s.Bytes.Length) : 0;
                report.AppendLine($"  bank {bank.Key}: {string.Join(", ", bank.Select(m => m.Name))}, {used} of {range.Size} bytes");
            }
        }
        foreach (var (name, range) in layout.Chr)
            report.AppendLine($"Chr area {name} {range}: {plan.ChrUse.GetValueOrDefault(name)} of {range.Size} bytes used");
        foreach (var area in layout.Overlays)
        {
            var inArea = overlayFiles.Where(o => o.Area == area.Key).ToList();
            report.AppendLine($"Overlay area {area.Key} {area.Value}" + (inArea.Count == 0 ? ": no overlay" : ":"));
            foreach (var o in inArea)
                report.AppendLine($"  {o.Name}{platform.BinaryExtension}: {o.Image.Length} of {area.Value.Size} bytes (${o.LoadAddress:X4}-${o.LoadAddress + o.Image.Length - 1:X4})");
        }
        if (binary)
        {
            report.AppendLine($"Jump table at ${start.Address:X4}:");
            foreach (var (name, address) in entries) report.AppendLine($"  ${address:X4}  {name}");
        }
        else if (program.ExportTableAddress is int exportTable)
        {
            report.AppendLine($"Export table at ${exportTable:X4}:");
            foreach (var (name, address) in entries) report.AppendLine($"  ${address:X4}  {name}");
        }
        // Information, not a warning (spec 8.1): a call from one root can overwrite another root's call in progress.
        var shared = program.Functions.Where(f => f.Roots.Count > 1 && !f.IsInline && f.FrameVariables.Sum(v => v.Type.Size) > 0).ToList();
        if (shared.Count > 0)
        {
            report.AppendLine("Functions with memory reachable from two roots:");
            foreach (var f in shared)
                report.AppendLine($"  {f.Name} ({Bytes(f.FrameVariables.Sum(v => v.Type.Size))}): {string.Join(", ", f.Roots.OrderBy(program.Functions.IndexOf).Select(r => r.Name))}");
        }
        report.AppendLine("Zero page:");
        report.Append(plan.ZeroPageMap);

        return new CompileResult
        {
            File = file,
            Image = image,
            Segments = code.Segments,
            LoadAddress = loadAddress,
            Platform = platform,
            Entry = system ? platform.LoadAddress : start.Address,
            HaltAddress = halt.Address,
            Listing = code.Listing,
            Symbols = symbols,
            Labels = labels,
            Report = report.ToString(),
            Sections = code.Sections,
            Areas = code.Areas,
            Entries = entries,
            Overlays = overlayFiles,
            ApiModule = apiModule,
            ApiModuleName = apiName,
            Instructions = code.Instructions,
            Variables = variables,
            DeclaredFunctions = program.Modules.SelectMany(m => m.Scope.Values.OfType<FunctionSymbol>()).Select(f => f.Name).ToHashSet(),
            TextEncoding = program.TextEncoding,
            BankWindow = layout.Banks.Count > 0 ? cartridge?.Board.BankArea : null,
        };
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
