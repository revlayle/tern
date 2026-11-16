using System.Text;
using Tern.Compiler.Backend;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler;

/// <summary>Allocation, code generation and layout: <see cref="Builder"/> and the results passed between its steps.</summary>
public static partial class TernCompiler
{
    /// <summary>Allocation, code generation and layout, for a program whose checks found no error.</summary>
    private static CompileResult Build(CheckedProgram program, Platform platform, string? sourcePath, bool optimize) =>
        new Builder(program, platform, sourcePath, optimize).Run();

    /// <summary>The assembled program: the variables' plan, the items it was assembled from, and what the platform's startup needed.</summary>
    /// <param name="Moves">Apple II: the copies the startup code makes (spec 12.2).</param>
    /// <param name="SystemFile">Apple II: the system file, built with those copies; null on other platforms.</param>
    private sealed record Assembled(RamPlan Plan, List<AsmItem> Items, AssembledCode Code, (bool Nmi, bool Irq) Vectors,
        List<AreaMove> Moves, byte[]? SystemFile);

    /// <summary>The program file and its image: every byte from <see cref="LoadAddress"/> to <see cref="End"/>, gaps as zeros.</summary>
    /// <param name="Banks">The modules in banks, in the order the main file imports them (the order a bank is filled in, spec 8.5).</param>
    private sealed record ProgramFile(byte[] File, byte[] Image, int LoadAddress, int End, int PrgSize, int PrgFirst, int BankCount, List<Module> Banks);

    /// <summary>One entry of a binary's or a program's table: its routine, its address, and the registers it changes.</summary>
    private sealed record TableEntry(string Name, FunctionSymbol? Function, int Address, CpuState Changed);

    /// <summary>
    /// The steps of a build, in order (<see cref="Run"/>). What every step reads (the program, the
    /// platform, the startup code's labels) is set once here; what a step makes is its return value.
    /// </summary>
    private sealed partial class Builder(CheckedProgram program, Platform platform, string? sourcePath, bool optimize)
    {
        private readonly CodeGen gen = new(program, platform.InstructionSet, optimize);
        private readonly MemoryLayout layout = program.Layout;
        private readonly bool binary = program.IsBinary;
        // Apple II: the copies the startup code makes (spec 12.2). They are known only once the program
        // is assembled, and the copying code moves what follows it, so assembly repeats until they agree.
        private bool System => platform.Format == OutputFormat.System && !binary;
        // The prefix code comes with io (question 150).
        private bool WithIo => System && program.Modules.Any(m => m.IsLibrary && m.Name == "io");
        // A cartridge (the NES): code in ROM, variables in RAM, initial values copied from ROM (spec 12.2).
        private Cartridge? Cartridge => layout.Cartridge;

        private readonly Label start = new("start"), halt = new("halt"), bss = new("bss"), setup = new("setup");
        private readonly Label moved = new("moved"), quit = new("quit");
        private Label? prefix;
        private readonly Label initial = new("initial"), rti = new("rti"), bankZero = new("bank_zero");

        private RegisterUse? use;
        /// <summary>The registers each function changes, made once the code is generated.</summary>
        private RegisterUse Use => use ??= new RegisterUse(gen, program.Functions);

        public CompileResult Run()
        {
            prefix = WithIo ? new Label("prefix_parameters") : null;
            var functionCode = gen.GenerateFunctions();
            var assembled = AssembleWithRetries(functionCode);
            var binaryTable = binary ? BinaryTable(assembled.Items) : null;
            CheckKeeps(binaryTable);
            var file = WriteFile(assembled);
            var (symbols, variables) = SymbolTables();
            // The emulators accept dots inside labels, so names stay as in the listing and cannot clash (util.bump, util_bump).
            string labels = platform.Labels.Write(symbols, Cartridge is null ? null
                : NesLabelMap(file.PrgFirst, file.BankCount, file.Banks.ToDictionary(m => m.Name, m => m.Bank!.Value), program.Globals.Where(g => g.InChr).Select(g => g.Name).ToHashSet()));
            var (entries, apiName, apiModule) = InterfaceModule(binaryTable, assembled, file);
            var overlayFiles = OverlayFiles(assembled.Code);
            var code = assembled.Code;
            return new CompileResult
            {
                File = file.File,
                Image = file.Image,
                Segments = code.Segments,
                LoadAddress = file.LoadAddress,
                Platform = platform,
                Entry = System ? platform.LoadAddress : start.Address,
                HaltAddress = halt.Address,
                SetsIrqVector = assembled.Vectors.Irq,
                SourceFiles = program.Modules.Where(m => m.Path is not null).Select(m => m.Path!).ToList(),
                Listing = code.Listing,
                Symbols = symbols,
                Labels = labels,
                Report = Report(assembled, file, entries, overlayFiles),
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
                BankWindow = layout.Banks.Count > 0 ? Cartridge?.Board.BankArea : null,
            };
        }

        /// <summary>
        /// Allocates the variables and assembles the program, again until it fits. It repeats for three reasons:
        /// the variables placed by page did not fit (then one after another), NROM's two areas must be joined,
        /// or the Apple II's startup copies changed.
        /// </summary>
        private Assembled AssembleWithRetries(List<AsmItem> functionCode)
        {
            // Indexed arrays placed so that they do not cross a page, with the variables starting on one;
            // if the program areas have no room for that, the variables are placed one after another (F12).
            var indexed = IndexedReads(functionCode);
            List<AreaMove> moves = [];
            int passes = 0;
            // NROM fills $C000-$FFFF first, so a small program is NROM-128; one that does not fit there is
            // laid out again in one area, $8000-$FFFF, so that a function or table may be larger than 16 KB
            // (review M27, finding 4).
            var codeAreas = layout.Program;
            bool CanJoin() => Cartridge is { Board.BankArea: null } && Joined(codeAreas).Count < codeAreas.Count;
            for (bool byPage = true; ;)
            {
                var plan = Allocator.Allocate(program, platform, byPage, indexed);
                CheckPlan(plan);
                var vectors = Cartridge is null ? default : CheckVectors(plan);
                var (items, imageLabels) = Items(functionCode, plan, vectors, moves, exact: passes < 4);
                // Each overlay, and each bank (its modules together), is laid out from its area's start.
                var overlayAreas = program.Modules.Where(m => m.OverlayArea is not null).GroupBy(m => m.Unit!)
                    .ToDictionary(g => g.Key, g => layout.PlacedArea(g.First().OverlayArea!)!.Value);
                // Placed by page, the variables may not fit, or may now overlap a fixed-address variable
                // (review M18 part 3, finding 1): then place them one after another. That try reports
                // any error as before.
                try
                {
                    var code = Assembler.Assemble(items, codeAreas, platform.InstructionSet, () =>
                    {
                        foreach (var (g, label) in imageLabels) g.Address = label.Address;
                        foreach (var (v, offset) in plan.ZeroedGlobals.Concat(plan.NoinitGlobals).Concat(plan.CopiedGlobals)) v.Address = bss.Address + offset;
                        foreach (var (v, offset) in plan.FrameVariables) v.Address = bss.Address + offset;
                    }, overlayAreas, program.Modules.Where(m => m.ImportedBy is not null && m.OverlayArea is not null).GroupBy(m => m.Unit!).ToDictionary(g => g.Key, g => g.First().ImportedBy!.Pos),
                    layout.Variables);
                    CheckFixedAddresses(program, platform, code, plan);
                    byte[]? systemFile = null;
                    if (System)
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
                    return new Assembled(plan, items, code, vectors, moves, systemFile);
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
        }

        /// <summary>The offsets each variable is read at with an index. Indexed reads only: a store or a read-modify-write takes the same cycles across a page.</summary>
        private static Dictionary<VariableSymbol, List<int>> IndexedReads(List<AsmItem> functionCode)
        {
            var indexed = new Dictionary<VariableSymbol, List<int>>();
            foreach (var i in functionCode.OfType<Instr>())
                if (MemoryUse.Of(i) is { Target: MemoryTarget.Element, Reads: true, Writes: false, Operand: { Variable: var v } o })
                    (indexed.TryGetValue(v.Storage, out var list) ? list : indexed[v.Storage] = []).Add(v.AliasOffset + o.Offset);
            return indexed;
        }

        /// <summary>The checks of an allocation that come before assembly.</summary>
        private void CheckPlan(RamPlan plan)
        {
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
        }

        /// <summary>
        /// What the assembler places: the startup code or the jump table, the functions, the tables after them,
        /// the globals' bytes, and the block of variables after the program. The labels are those of the globals in the image.
        /// </summary>
        private (List<AsmItem> Items, Dictionary<VariableSymbol, Label> ImageLabels) Items(List<AsmItem> functionCode, RamPlan plan,
            (bool Nmi, bool Irq) vectors, List<AreaMove> moves, bool exact)
        {
            var items = binary ? JumpTable(plan) : Startup(plan, moves, exact);
            items.AddRange(functionCode);
            if (Cartridge is not null)
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
            var imageLabels = ImageItems(items, plan);
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
            return (items, imageLabels);
        }

        /// <summary>A program's startup code: the platform's first steps and the variables' setup, then <c>jsr main</c> and the halt loop.</summary>
        private List<AsmItem> Startup(RamPlan plan, List<AreaMove> moves, bool exact)
        {
            var cartridge = Cartridge;
            List<AsmItem> items = [new SectionStart("startup"), new DataBytes(platform.Header), new LabelDef(start), new Comment("startup")];
            if (cartridge is not null) items.Add(new Instr(Sei));
            items.AddRange(TernCompiler.Startup(plan, bss, gen.LabelOf,
                System ? Apple2Start(moves, moved, quit, prefix, exact) : cartridge is not null ? NesStart(cartridge.Board.BankArea is null ? null : bankZero) : [],
                ramCleared: cartridge is not null, initial));
            items.Add(new Instr(Jsr, Absolute, new LabelOperand(gen.LabelOf(program.Main))));
            items.Add(new LabelDef(halt));
            items.Add(new Instr(Jmp, Absolute, new LabelOperand(halt)));
            if (System) items.AddRange(Apple2Quit(quit, prefix));
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
            return items;
        }

        /// <summary>
        /// A binary starts with its jump table: setup, then the exports in the order they were listed,
        /// or at their routines' addresses in the interface module, with zeros between (spec 12.3). Then setup itself.
        /// </summary>
        private List<AsmItem> JumpTable(RamPlan plan)
        {
            List<AsmItem> items = [new SectionStart("jump table"), new LabelDef(start), new Instr(Jmp, Absolute, new LabelOperand(setup))];
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
            items.AddRange(TernCompiler.Startup(plan, bss, gen.LabelOf, []));
            items.Add(new Instr(Rts));
            return items;
        }

        /// <summary>The bytes of the globals the program image holds: data and initial values, fixed-address data, and overlay modules' variables.</summary>
        private Dictionary<VariableSymbol, Label> ImageItems(List<AsmItem> items, RamPlan plan)
        {
            var imageLabels = new Dictionary<VariableSymbol, Label>();
            foreach (var g in plan.ImageGlobals)
            {
                var label = new Label(g.Name);
                imageLabels[g] = label;
                // A bank's data is in the bank, with its code (spec 8.5).
                items.Add(new SectionStart(g.Name, Overlay: g.Module?.InBank == true ? g.OverlayUnit : null));
                if (ImagePadding(g, Cartridge is null ? (byte)0 : (byte)0xFF) is { } padding) items.Add(padding);
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
            return imageLabels;
        }

        /// <summary>A binary's jump table: setup (with the registers its startup code changes), then the exports.</summary>
        private List<TableEntry> BinaryTable(List<AsmItem> items)
        {
            var setupChange = items.SkipWhile(i => i is not LabelDef { Label: var l } || l != setup).OfType<Instr>()
                .TakeWhile(i => i.Mnemonic != Rts)
                .Aggregate(CpuState.None, (acc, i) => acc | (Instructions.Effects(i.Mnemonic, i.Mode).Writes & (CpuState.A | CpuState.X | CpuState.Y)));
            var table = new List<TableEntry> { new("setup", null, start.Address, setupChange) };
            for (int i = 0; i < program.Exports.Count; i++)
            {
                var f = program.Exports[i];
                table.Add(new(f.Name, f, program.ExportedAs.GetValueOrDefault(f)?.FixedAddress ?? start.Address + 3 * (i + 1), Use.Changed(f)));
            }
            return table;
        }

        /// <summary>
        /// The registers an interface routine promises to keep (spec 12.3): a binary's entries for <c>export level_api</c>,
        /// and an overlay's implementations for <c>implements</c>, on which the code around a call to the routine relies.
        /// </summary>
        private void CheckKeeps(List<TableEntry>? binaryTable)
        {
            if (binaryTable is not null && program.ExportedAs.Count > 0)
            {
                var api = program.ExportedAs.Values.First().Module!;
                var interfaceSetup = (FunctionSymbol)api.Scope["setup"];
                foreach (var (name, f, _, changed) in binaryTable)
                    foreach (var reg in Changed(f is null ? interfaceSetup : program.ExportedAs[f], changed))
                        throw new CompileError(f?.Pos ?? program.Main.Pos,
                            f is null
                                ? $"'{api.Name}.setup' promises to keep {reg}, but the binary's startup code changes it. Remove {reg} from its 'keeps' list"
                                : KeepsMessage(name, reg, $"{api.Name}.{name}"));
            }
            foreach (var entry in program.Implementations)
                foreach (var reg in Changed(entry.Routine, Use.Changed(entry.Function)))
                    throw new CompileError(entry.Function.Pos, KeepsMessage(entry.Function.Name, reg, $"{entry.Interface.Name}.{entry.Name}"));

            static IEnumerable<Register> Changed(FunctionSymbol routine, CpuState changed) =>
                routine.Keeps.OrderBy(r => r).Where(reg => changed.HasFlag(reg switch { Register.A => CpuState.A, Register.X => CpuState.X, _ => CpuState.Y }));
            static string KeepsMessage(string name, Register reg, string routine) =>
                $"'{name}' changes {reg}, which '{routine}' promises to keep. Change the function so that it leaves {reg} alone, or remove {reg} from the routine's 'keeps' list";
        }

        /// <summary>The program file in the platform's format: a cartridge, an Apple II system file, or the image in the platform's file.</summary>
        private ProgramFile WriteFile(Assembled assembled)
        {
            var segments = assembled.Code.Segments;
            // The file covers every byte from the lowest to the highest address used; gaps are zeros.
            int loadAddress = segments.Min(s => s.Address);
            int fileEnd = segments.Max(s => s.Address + s.Bytes.Length);
            var image = new byte[fileEnd - loadAddress];
            foreach (var (address, bytes) in segments) bytes.CopyTo(image, address - loadAddress);
            byte[] file;
            int prgSize = 0, prgFirst = 0, bankCount = 0;
            // In the order the main file imports them, which is the order a bank is filled in (spec 8.5).
            var banks = program.Modules.Where(m => m.InBank).OrderBy(m => m.ImportedBy!.Pos.Line).ThenBy(m => m.ImportedBy!.Pos.Column).ToList();
            if (Cartridge is { } cartridge)
                (file, prgSize, prgFirst, bankCount) = NesFile(cartridge, segments, assembled.Plan, banks.GroupBy(m => m.Unit!).ToDictionary(g => g.Key, g => g.First().Bank!.Value), assembled.Code.Overlays);
            else file = assembled.SystemFile ?? platform.FileOf(loadAddress, image, segments, binary ? null : start.Address);
            return new ProgramFile(file, image, loadAddress, fileEnd, prgSize, prgFirst, bankCount, banks);
        }

        /// <summary>The addresses of the globals, functions and locals (locals as "function.name"), and the variables' places.</summary>
        private (Dictionary<string, int> Symbols, Dictionary<string, VariableInfo> Variables) SymbolTables()
        {
            var symbols = new Dictionary<string, int>();
            var variables = new Dictionary<string, VariableInfo>();
            foreach (var g in program.Globals)
            {
                symbols[g.Name] = g.Address;
                variables[g.Name] = new VariableInfo(g.Name, g.Address, g.Type.Size, g.Type.ToString(), null, g.IsData, g.InChr);
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
            return (symbols, variables);
        }

        /// <summary>The entries of a binary's jump table or a program's export table, and the interface module made from them (spec 12.3).</summary>
        private (List<(string Name, int Address)> Entries, string? ApiName, string? ApiModule) InterfaceModule(List<TableEntry>? binaryTable, Assembled assembled, ProgramFile file)
        {
            var entries = new List<(string Name, int Address)>();
            string? apiModule = null, apiName = null;
            if (binaryTable is not null)
            {
                entries.AddRange(binaryTable.Select(t => (t.Name, t.Address)));
                var fileRange = new AddressRange(file.LoadAddress, file.End - 1);
                string baseName = sourcePath is null ? "binary" : Path.GetFileNameWithoutExtension(sourcePath);
                apiName = baseName + "_api";
                apiModule = BinaryInterface.Generate(baseName + platform.BinaryExtension, apiName, Tuples(binaryTable), fileRange, BinaryRanges(fileRange, assembled));
            }
            if (program.ExportTableAddress is int tableAddress)
            {
                // The program's table, for plug-ins: no setup, no memory ranges (spec 12.3).
                var table = program.Exports.Select((f, i) => new TableEntry(f.Name, f, tableAddress + 3 * i, Use.Changed(f))).ToList();
                entries.AddRange(table.Select(t => (t.Name, t.Address)));
                string baseName = sourcePath is null ? "program" : Path.GetFileNameWithoutExtension(sourcePath);
                apiName = baseName + "_api";
                apiModule = BinaryInterface.GenerateTable(baseName + platform.FileExtension, apiName, Tuples(table));
            }
            return (entries, apiName, apiModule);

            static List<(string, FunctionSymbol?, int, CpuState)> Tuples(List<TableEntry> table) =>
                table.Select(t => (t.Name, t.Function, t.Address, t.Changed)).ToList();
        }

        /// <summary>Every range a binary uses outside its file, once: a host may not reserve overlapping ranges.</summary>
        private List<(string Prefix, string What, AddressRange Range)> BinaryRanges(AddressRange fileRange, Assembled assembled)
        {
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
            if (assembled.Code.Variables is { } vr) Emit("VARS", "variables, zeroed by setup", vr);
            foreach (var (name, use2) in assembled.Plan.RegionUse) Emit(name.ToUpperInvariant(), $"region {name}", use2);
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
            return others;
        }

        /// <summary>One file for each module in an overlay area (not a bank) that has code or data, loading at its area's start.</summary>
        private List<OverlayFile> OverlayFiles(AssembledCode code)
        {
            var overlayFiles = new List<OverlayFile>();
            foreach (var module in program.Modules.Where(m => m.OverlayArea is not null && !m.InBank))
            {
                if (!code.Overlays.TryGetValue(module.Name, out var segments)) continue;
                int first = Math.Min(segments.Min(s => s.Address), layout.Overlays[module.OverlayArea!].First);
                var overlayImage = new byte[segments.Max(s => s.Address + s.Bytes.Length) - first];
                foreach (var (address, bytes) in segments) bytes.CopyTo(overlayImage, address - first);
                overlayFiles.Add(new OverlayFile(module.Name, module.OverlayArea!, first, overlayImage,
                    platform.FileOf(first, overlayImage, [(first, overlayImage)], null)));
            }
            return overlayFiles;
        }
    }
}
