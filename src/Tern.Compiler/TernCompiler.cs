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
    public required int HaltAddress { get; init; }
    public required string Listing { get; init; }
    /// <summary>Variable and function addresses by name (locals as "function.name").</summary>
    public required IReadOnlyDictionary<string, int> Symbols { get; init; }
    public required string ViceLabels { get; init; }
    public required string Report { get; init; }
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
        var plan = Allocator.Allocate(program, platform);
        foreach (var v in program.Globals.Concat(program.Functions.SelectMany(f => f.FrameVariables)))
            if (v.Address is >= 0 and < 0x100 && v.FixedAddress is null)
                foreach (int o in v.JumpVectorOffsets)
                    if (((v.Address + o) & 0xFF) == 0xFF)
                        throw new CompileError(v.Pos, $"'{v.Name}' is used as jmp ({v.Name}{o:+0;-0;''}) and was placed so that the vector starts at $FF, where the 6502 reads it wrongly");

        var start = new Label("start");
        var halt = new Label("halt");
        var bss = new Label("bss");
        var items = new List<AsmItem> { new LabelDef(start), new Comment("startup") };
        items.AddRange(Startup(plan, bss));
        items.Add(new Instr(Jsr, Absolute, new LabelOperand(gen.LabelOf(program.Main))));
        items.Add(new LabelDef(halt));
        items.Add(new Instr(Jmp, Absolute, new LabelOperand(halt)));
        items.AddRange(functionCode);

        var imageLabels = new Dictionary<VariableSymbol, Label>();
        foreach (var g in plan.ImageGlobals)
        {
            var label = new Label(g.Name);
            imageLabels[g] = label;
            if (g.JumpVectorOffsets.Count > 0)
                items.Add(new Padding(pc => AvoidPageEnd(pc, g.JumpVectorOffsets), $"jmp ({g.Name}) must not read a vector at $xxFF"));
            items.Add(new LabelDef(label));
            items.Add(new DataBytes(MemoryLayout(g, g.InitialBytes!)));
        }
        // Variables after the program start at bss. Move them all, if needed, so that no jmp (...)
        // vector among them starts at an address ending in $FF.
        var ramVectors = plan.ZeroedGlobals.Concat(plan.NoinitGlobals).Concat(plan.FrameVariables)
            .SelectMany(e => e.Var.JumpVectorOffsets.Select(o => e.Offset + o)).ToList();
        if (ramVectors.Count > 0)
            items.Add(new Padding(pc => AvoidPageEnd(pc, ramVectors), "no jmp (...) vector may start at $xxFF"));
        items.Add(new LabelDef(bss));

        int ramEnd = 0;
        var code = Assembler.Assemble(items, platform.CodeStart, end =>
        {
            foreach (var (g, label) in imageLabels) g.Address = label.Address;
            foreach (var (v, offset) in plan.ZeroedGlobals.Concat(plan.NoinitGlobals)) v.Address = end + offset;
            foreach (var (v, offset) in plan.FrameVariables) v.Address = end + offset;
            ramEnd = end + plan.TotalSize - 1;
            if (ramEnd > platform.RamEnd)
                throw new CompileError(new SourcePos(1, 1),
                    $"Program and variables end at ${ramEnd:X4}, past the end of usable RAM (${platform.RamEnd:X4})");
        });

        var image = platform.Header.Concat(code.Bytes).ToArray();
        var prg = new byte[] { (byte)platform.LoadAddress, (byte)(platform.LoadAddress >> 8) }.Concat(image).ToArray();

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

        var report = new StringBuilder();
        report.AppendLine($"Code and data: ${platform.LoadAddress:X4}-${platform.CodeStart + code.Bytes.Length - 1:X4} ({image.Length} bytes)");
        report.AppendLine($"Variables after the program: {plan.TotalSize} bytes, up to ${ramEnd:X4}");
        report.AppendLine("Zero page:");
        report.Append(plan.ZeroPageMap);

        return new CompileResult
        {
            Prg = prg,
            Image = image,
            LoadAddress = platform.LoadAddress,
            Entry = platform.CodeStart,
            HaltAddress = halt.Address,
            Listing = code.Listing,
            Symbols = symbols,
            ViceLabels = vice.ToString(),
            Report = report.ToString(),
        };
    }

    /// <summary>
    /// The number of bytes to skip at <paramref name="pc"/> so that no vector at <c>pc + skip + offset</c>
    /// starts at an address ending in $FF. The NMOS 6502 reads the high byte of <c>jmp ($xxFF)</c> from $xx00.
    /// </summary>
    private static int AvoidPageEnd(int pc, IEnumerable<int> offsets)
    {
        for (int skip = 0; ; skip++)
            if (offsets.All(o => ((pc + skip + o) & 0xFF) != 0xFF)) return skip;
    }

    /// <summary>Startup code: set up zero-page globals and clear the uninitialized ones in RAM (except noinit ones).</summary>
    private static List<AsmItem> Startup(RamPlan plan, Label bss)
    {
        var items = new List<AsmItem> { new Instr(Cld) };

        int? a = null;
        foreach (var g in plan.ZeroPageGlobals.Where(g => !g.NoInit))
        {
            var bytes = MemoryLayout(g, g.InitialBytes ?? new byte[g.Type.Size]);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (a != bytes[i]) items.Add(new Instr(Lda, Immediate, new Imm(bytes[i])));
                a = bytes[i];
                items.Add(new Instr(Sta, Absolute, new VarOperand(g, i)));
            }
        }

        int size = plan.ZeroedSize;
        if (size > 0)
        {
            items.Add(new Instr(Lda, Immediate, new Imm(0)));
            int pages = size / 256;
            for (int p = 0; p < pages; p++)
            {
                var loop = new Label($"clear{p}");
                items.Add(new Instr(Ldx, Immediate, new Imm(0)));
                items.Add(new LabelDef(loop));
                items.Add(new Instr(Sta, AbsoluteX, new LabelOperand(bss, p * 256)));
                items.Add(new Instr(Inx));
                items.Add(new Instr(Bne, Relative, new LabelOperand(loop)));
            }
            int rest = size % 256;
            if (rest > 0)
            {
                var loop = new Label("clear");
                items.Add(new Instr(Ldx, Immediate, new Imm(rest)));
                items.Add(new LabelDef(loop));
                items.Add(new Instr(Sta, AbsoluteX, new LabelOperand(bss, pages * 256 - 1)));
                items.Add(new Instr(Dex));
                items.Add(new Instr(Bne, Relative, new LabelOperand(loop)));
            }
        }
        return items;
    }

    /// <summary>
    /// Converts a value's bytes (little endian, elements in order) to its layout in memory:
    /// arrays of 16-bit values are split into a low-byte table followed by a high-byte table.
    /// </summary>
    private static byte[] MemoryLayout(VariableSymbol v, byte[] bytes)
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
