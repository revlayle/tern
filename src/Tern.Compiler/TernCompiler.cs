using System.Text;
using Tern.Compiler.Backend;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler;

public sealed class CompileResult
{
    /// <summary>The program file: 2-byte load address followed by <see cref="Image"/>.</summary>
    public required byte[] Prg { get; init; }
    /// <summary>The bytes loaded at <see cref="LoadAddress"/>.</summary>
    public required byte[] Image { get; init; }
    public required int LoadAddress { get; init; }
    /// <summary>Where execution starts.</summary>
    public required int Entry { get; init; }
    /// <summary>Address of the endless loop reached when <c>main</c> returns.</summary>
    /// <summary>The address main returns to; -1 for a binary, which has no main.</summary>
    public required int HaltAddress { get; init; }
    public required string Listing { get; init; }
    /// <summary>Variable and function addresses by name (locals as "function.name").</summary>
    public required IReadOnlyDictionary<string, int> Symbols { get; init; }
    public required string ViceLabels { get; init; }
    public required string Report { get; init; }
    /// <summary>Every function, data item and the startup code, with the address it was placed at.</summary>
    public required IReadOnlyList<PlacedSection> Sections { get; init; }
    /// <summary>The program areas the compiler filled and how much of each is used.</summary>
    public required IReadOnlyList<PlacedArea> Areas { get; init; }
    /// <summary>For a binary without <c>main</c>: the jump table, <c>setup</c> first, then the exports, each with its address.</summary>
    public IReadOnlyList<(string Name, int Address)> Entries { get; init; } = [];
    /// <summary>For a binary without <c>main</c>: the text of its interface module (spec 12.3), or null.</summary>
    public string? ApiModule { get; init; }
    /// <summary>The module's name, such as <c>player_api</c>; the file is <c>player_api.tern</c>.</summary>
    public string? ApiModuleName { get; init; }
}

public static class TernCompiler
{
    /// <summary>The standard library: the <c>lib</c> folder next to the compiler.</summary>
    public static string DefaultLibraryDirectory => Path.Combine(AppContext.BaseDirectory, "lib");

    /// <param name="sourcePath">The source file's path. <c>include_bytes</c> paths and imports of your own modules are relative to its directory.</param>
    /// <param name="libraryDirectory">Where <c>import</c> looks first; <see cref="DefaultLibraryDirectory"/> if null.</param>
    public static CompileResult Compile(string source, Platform? platform = null, string? sourcePath = null, string? libraryDirectory = null)
    {
        platform ??= Platform.C64;

        var modules = ModuleLoader.Load(source, sourcePath, platform, libraryDirectory ?? DefaultLibraryDirectory);
        var program = Checker.Check(modules, platform);

        var gen = new CodeGen(program);
        var functionCode = gen.GenerateFunctions();
        Checker.CheckSharedFunctions(program);
        var plan = Allocator.Allocate(program, platform);
        foreach (var v in program.Globals.Concat(program.Functions.SelectMany(f => f.FrameVariables)))
            if (v.Address is >= 0 and < 0x100 && v.FixedAddress is null)
                foreach (int o in v.JumpVectorOffsets)
                    if (((v.Address + o) & 0xFF) == 0xFF)
                        throw new CompileError(v.Pos, $"'{v.Name}' is used as jmp ({v.Name}{o:+0;-0;''}) and was placed so that the vector starts at $FF, where the 6502 reads it wrongly");

        var layout = program.Layout;
        bool binary = program.IsBinary;
        if (!binary && layout.Program[0].First != platform.LoadAddress)
            throw new CompileError(program.Main.Pos,
                $"A program with 'main' starts at ${platform.LoadAddress:X4}, where the {platform.Name.ToUpperInvariant()} BASIC line goes, but its first program area begins at ${layout.Program[0].First:X4}. " +
                (layout.ProgramReplaced ? $"Start the first 'program' line of the memory block at ${platform.LoadAddress:X4}" : "Move the 'reserved' or 'region' range that covers it"));
        CheckFixedData(program, plan);

        var start = new Label("start");
        var halt = new Label("halt");
        var bss = new Label("bss");
        var setup = new Label("setup");
        List<AsmItem> items;
        if (!binary)
        {
            items = [new SectionStart("startup"), new DataBytes(platform.Header), new LabelDef(start), new Comment("startup")];
            items.AddRange(Startup(plan, bss, gen.LabelOf));
            items.Add(new Instr(Jsr, Absolute, new LabelOperand(gen.LabelOf(program.Main))));
            items.Add(new LabelDef(halt));
            items.Add(new Instr(Jmp, Absolute, new LabelOperand(halt)));
        }
        else
        {
            // A binary starts with its jump table: setup, then the exports in the order they were listed (spec 12.3).
            items = [new SectionStart("jump table"), new LabelDef(start), new Instr(Jmp, Absolute, new LabelOperand(setup))];
            foreach (var export in program.Exports)
                items.Add(new Instr(Jmp, Absolute, new LabelOperand(gen.LabelOf(export))));
            items.Add(new SectionStart("setup"));
            items.Add(new LabelDef(setup));
            items.AddRange(Startup(plan, bss, gen.LabelOf));
            items.Add(new Instr(Rts));
        }
        items.AddRange(functionCode);

        var imageLabels = new Dictionary<VariableSymbol, Label>();
        foreach (var g in plan.ImageGlobals)
        {
            var label = new Label(g.Name);
            imageLabels[g] = label;
            items.Add(new SectionStart(g.Name));
            if (g.JumpVectorOffsets.Count > 0)
                items.Add(new Padding(pc => Assembler.AvoidPageEnd(pc, g.JumpVectorOffsets), $"jmp ({g.Name}) must not read a vector at $xxFF"));
            items.Add(new LabelDef(label));
            items.AddRange(ImageBytes(g, gen.LabelOf));
        }
        foreach (var g in plan.FixedImageGlobals)
        {
            items.Add(new SectionStart(g.Name, g.Address));
            items.AddRange(ImageBytes(g, gen.LabelOf));
        }
        // Variables after the program start at bss. Move them all, if needed, so that no jmp (...)
        // vector among them starts at an address ending in $FF.
        var ramVectors = plan.ZeroedGlobals.Concat(plan.NoinitGlobals).Concat(plan.FrameVariables)
            .SelectMany(e => e.Var.JumpVectorOffsets.Select(o => e.Offset + o)).ToList();
        items.Add(new Reserve(bss, plan.TotalSize, ramVectors));

        var code = Assembler.Assemble(items, layout.Program, () =>
        {
            foreach (var (g, label) in imageLabels) g.Address = label.Address;
            foreach (var (v, offset) in plan.ZeroedGlobals.Concat(plan.NoinitGlobals)) v.Address = bss.Address + offset;
            foreach (var (v, offset) in plan.FrameVariables) v.Address = bss.Address + offset;
        });
        CheckFixedAddresses(program, platform, code, plan);

        // The file covers every byte from the lowest to the highest address used; gaps are zeros.
        int loadAddress = code.Segments.Min(s => s.Address);
        int fileEnd = code.Segments.Max(s => s.Address + s.Bytes.Length);
        var image = new byte[fileEnd - loadAddress];
        foreach (var (address, bytes) in code.Segments) bytes.CopyTo(image, address - loadAddress);
        var prg = new byte[] { (byte)loadAddress, (byte)(loadAddress >> 8) }.Concat(image).ToArray();

        var symbols = new Dictionary<string, int>();
        foreach (var g in program.Globals) symbols[g.Name] = g.Address;
        foreach (var f in program.Functions)
        {
            // An inline function has no code of its own, only a frame.
            if (!f.IsInline) symbols[f.Name] = gen.LabelOf(f).Address;
            foreach (var v in f.FrameVariables.Where(v => v.Kind != VariableKind.Temp))
                symbols[v.QualifiedName] = v.Address;
        }

        // VICE accepts dots inside labels, so names stay as in the listing and cannot clash (util.bump, util_bump).
        var vice = new StringBuilder();
        foreach (var (name, address) in symbols.OrderBy(s => s.Value))
            vice.AppendLine($"al C:{address:x4} .{name}");

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
                table.Add((program.Exports[i].Name, program.Exports[i], start.Address + 3 * (i + 1), use.Changed(program.Exports[i])));
            entries.AddRange(table.Select(t => (t.Name, t.Address)));

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
            apiModule = BinaryInterface.Generate(baseName + ".prg", apiName, table, fileRange, others);
        }

        var report = new StringBuilder();
        report.AppendLine($"Code and data: ${loadAddress:X4}-${fileEnd - 1:X4} ({image.Length} bytes)");
        report.AppendLine(code.Variables is { } vars
            ? $"Variables after the program: {plan.TotalSize} bytes, up to ${vars.Last:X4}"
            : "Variables after the program: 0 bytes");
        if (code.Areas.Count > 1 || layout.Regions.Count > 0)
        {
            report.AppendLine("Program areas:");
            foreach (var a in code.Areas)
                report.AppendLine($"  {a.Free}: {a.UsedEnd - a.Free.First} of {a.Free.Size} bytes used");
            foreach (var (name, range) in layout.Regions)
                report.AppendLine(plan.RegionUse.TryGetValue(name, out var use)
                    ? $"  region {name} {range}: {use.Size} of {range.Size} bytes used"
                    : $"  region {name} {range}: unused");
        }
        if (binary)
        {
            report.AppendLine($"Jump table at ${start.Address:X4}:");
            foreach (var (name, address) in entries) report.AppendLine($"  ${address:X4}  {name}");
        }
        report.AppendLine("Zero page:");
        report.Append(plan.ZeroPageMap);

        return new CompileResult
        {
            Prg = prg,
            Image = image,
            LoadAddress = loadAddress,
            Entry = start.Address,
            HaltAddress = halt.Address,
            Listing = code.Listing,
            Symbols = symbols,
            ViceLabels = vice.ToString(),
            Report = report.ToString(),
            Sections = code.Sections,
            Areas = code.Areas,
            Entries = entries,
            ApiModule = apiModule,
            ApiModuleName = apiName,
        };
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
            foreach (var (regionName, use) in plan.RegionUse)
                if (range.Overlaps(use))
                    throw new CompileError(g.Pos,
                        $"'{g.Name}' at {range} overlaps the variables the compiler placed in region '{regionName}' at {use}. Move '{g.Name}', or place it with the region");
            foreach (var section in code.Sections.Where(s => s.IsFixed && s.Size > 0))
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

    /// <summary>Startup code: set up zero-page globals and clear the uninitialized ones in RAM (except noinit ones).</summary>
    private static List<AsmItem> Startup(RamPlan plan, Label bss, Func<FunctionSymbol, Label> labelOf)
    {
        var items = new List<AsmItem> { new Instr(Cld) };

        int? a = null;
        foreach (var g in plan.ZeroPageGlobals.Where(g => !g.NoInit))
        {
            var bytes = ByteLayout(g, g.InitialBytes ?? new byte[g.Type.Size]);
            var functions = FunctionBytes(g);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (functions.TryGetValue(i, out var f))
                {
                    items.Add(new Instr(Lda, Immediate, new ImmLabelByte(labelOf(f.Function), f.High)));
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

        if (plan.ZeroedSize > 0)
            ClearBlock(items, plan.ZeroedSize, p => new LabelOperand(bss, p), "clear");
        foreach (var block in plan.RegionClears)
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
    /// The initial value of a global in the program image. Function addresses in it are bytes of
    /// the functions' labels.
    /// </summary>
    private static IEnumerable<AsmItem> ImageBytes(VariableSymbol g, Func<FunctionSymbol, Label> labelOf)
    {
        var bytes = ByteLayout(g, g.InitialBytes!);
        var functions = FunctionBytes(g);
        int start = 0;
        foreach (var (at, (function, high)) in functions.OrderBy(f => f.Key))
        {
            if (at > start) yield return new DataBytes(bytes[start..at]);
            yield return new LabelTable([labelOf(function)], high);
            start = at + 1;
        }
        if (start < bytes.Length) yield return new DataBytes(bytes[start..]);
    }

    /// <summary>Where the bytes of function addresses are in a global's memory layout.</summary>
    private static Dictionary<int, (FunctionSymbol Function, bool High)> FunctionBytes(VariableSymbol g)
    {
        var bytes = new Dictionary<int, (FunctionSymbol, bool)>();
        foreach (var (index, f) in g.FunctionAddresses)
        {
            bytes[LayoutIndex(g, index)] = (f, false);
            bytes[LayoutIndex(g, index + 1)] = (f, true);
        }
        return bytes;
    }

    /// <summary>Where byte <paramref name="i"/> of a value is in its memory layout (see <see cref="ByteLayout"/>).</summary>
    private static int LayoutIndex(VariableSymbol v, int i) =>
        !v.Type.IsArray || v.Type.Element!.Size == 1 || v.Type.Linear ? i
        : i % 2 == 0 ? i / 2 : v.Type.Length + i / 2;

    /// <summary>
    /// Converts a value's bytes (little endian, elements in order) to its layout in memory:
    /// arrays of 16-bit values are split into a low-byte table followed by a high-byte table.
    /// </summary>
    private static byte[] ByteLayout(VariableSymbol v, byte[] bytes)
    {
        if (!v.Type.IsArray || v.Type.Element!.Size == 1 || v.Type.Linear) return bytes;
        int n = v.Type.Length;
        var split = new byte[bytes.Length];
        for (int i = 0; i < n; i++)
        {
            split[i] = bytes[2 * i];
            split[n + i] = bytes[2 * i + 1];
        }
        return split;
    }
}
