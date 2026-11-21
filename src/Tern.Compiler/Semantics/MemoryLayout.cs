using Tern.Compiler.Syntax;

namespace Tern.Compiler.Semantics;

/// <summary>An inclusive range of addresses.</summary>
public readonly record struct AddressRange(int First, int Last)
{
    public int Size => Last - First + 1;
    public bool Overlaps(AddressRange other) => First <= other.Last && Last >= other.First;
    public bool Contains(AddressRange other) => First <= other.First && Last >= other.Last;
    public override string ToString() => First == Last ? $"${First:X4}" : $"${First:X4}-${Last:X4}";
}

/// <summary>
/// What the program may use of the machine's memory: the platform's default map, changed by the main
/// file's <c>memory</c> block (spec 8.4).
/// </summary>
public sealed class MemoryLayout
{
    /// <summary>Areas for code, data and variables, in the order the compiler fills them.</summary>
    public List<AddressRange> Program { get; private init; } = [];
    /// <summary>
    /// Zero-page bytes the compiler may allocate (spec 8.2), in the order it fills them: the platform's
    /// order (<see cref="Platform.ZeroPage"/>), or address order for a memory block's zeropage lines.
    /// </summary>
    public List<AddressRange> ZeroPage { get; private init; } = [];
    /// <summary>Memory the compiler places nothing in, except what the program puts there with <c>@</c>.</summary>
    public List<AddressRange> Reserved { get; } = [];
    /// <summary>Named areas where variables and data are placed on request (<c>@ name</c>).</summary>
    public Dictionary<string, AddressRange> Regions { get; } = [];
    /// <summary>Areas modules are loaded into on request (<c>import m @ name</c>, spec 8.5).</summary>
    public Dictionary<string, AddressRange> Overlays { get; } = [];
    /// <summary>Bank areas: windows where one bank of the cartridge's ROM is visible at a time (<c>import m @ name[n]</c>, spec 8.5, 14.1).</summary>
    public Dictionary<string, AddressRange> Banks { get; } = [];

    /// <summary>The overlay or bank area named <paramref name="name"/>, or null.</summary>
    public AddressRange? PlacedArea(string name) =>
        Overlays.TryGetValue(name, out var o) ? o : Banks.TryGetValue(name, out var b) ? b : null;
    /// <summary>Volatile ranges added by the memory block, in addition to the platform's.</summary>
    public List<AddressRange> Volatile { get; } = [];
    /// <summary>
    /// RAM for the variables' block when code is in ROM (<see cref="Platform.VariableRam"/>, less the
    /// block's other areas); empty when the variables follow the program in <see cref="Program"/>.
    /// </summary>
    public List<AddressRange> Variables { get; private init; } = [];
    /// <summary>
    /// The <c>chr</c> areas (NES): CHR ROM, in the PPU's addresses, so they take no CPU memory. Only
    /// <c>data</c> is placed there, and its bytes go in the image's CHR ROM (spec 12.2).
    /// </summary>
    public Dictionary<string, AddressRange> Chr { get; } = [];
    /// <summary>The board the build is an image for (the <c>cartridge</c> line), or null.</summary>
    public Cartridge? Cartridge { get; private init; }

    /// <summary>The block has <c>program</c> lines, which replace the platform's program areas.</summary>
    public bool ProgramReplaced { get; private init; }

    public bool IsVolatile(int first, int last) => Volatile.Any(a => first <= a.Last && last >= a.First);

    /// <summary>
    /// The platform's map: one program area from the load address to the end of usable RAM; on a
    /// platform where every program is a cartridge, the first board's ROM, with the variables in <see cref="Platform.VariableRam"/>.
    /// </summary>
    public static MemoryLayout Default(Platform platform) => new()
    {
        Program = platform.CartridgeRequired ? [.. platform.Boards[0].CodeAreas] : [new AddressRange(platform.LoadAddress, platform.RamEnd)],
        Variables = platform.VariableRam is var (first, last) ? [new AddressRange(first, last)] : [],
        ZeroPage = PlatformZeroPage(platform),
    };

    /// <summary>The board and options of a <c>cartridge</c> line (spec 8.4, 12.2).</summary>
    private static Cartridge BuildCartridge(Platform platform, CartridgeLine line)
    {
        if (platform.Boards.Count == 0)
            throw new CompileError(line.Pos, !platform.CartridgesPlanned
                ? $"The {platform.Title} has no cartridges. Remove the 'cartridge' line"
                : $"'cartridge' is not supported yet for the {platform.Title}. Remove the 'cartridge' line");
        var board = platform.Boards.FirstOrDefault(b => b.Name == line.Board)
            ?? throw new CompileError(line.BoardPos,
                $"The {platform.Title} has no board '{line.Board}'. Use one of: {string.Join(", ", platform.Boards.Select(b => b.Name))}");
        foreach (var (name, pos) in line.Options)
            if (!Board.Mirroring.Contains(name))
                throw new CompileError(pos, $"The board '{board.Name}' has no option '{name}'. Use one of: {string.Join(", ", Board.Mirroring)}");
        var mirroring = line.Options.Where(o => Board.Mirroring.Contains(o.Name)).ToList();
        if (mirroring.Count == 0)
            throw new CompileError(line.BoardPos,
                $"The board '{board.Name}' is wired for one mirroring, which the line must name. Write 'cartridge {board.Name} vertical' or 'cartridge {board.Name} horizontal'");
        if (mirroring.Count > 1)
            throw new CompileError(mirroring[1].Pos, $"The board has one mirroring, and the line names {mirroring.Count}. Remove '{mirroring[1].Name}'");
        return new Cartridge(board, mirroring[0].Name == "vertical");
    }

    private static List<AddressRange> PlatformZeroPage(Platform platform) => platform.ZeroPage.Select(z => new AddressRange(z.First, z.Last)).ToList();

    /// <summary>Applies the lines of the main file's memory block to the platform's map (spec 8.4).</summary>
    /// <param name="block">The block's position, for an error about the block as a whole.</param>
    public static MemoryLayout Build(Platform platform, IReadOnlyList<(MemoryLine Line, long First, long Last)> lines, CartridgeLine? cartridgeLine, SourcePos block)
    {
        var cartridge = cartridgeLine is null ? null : BuildCartridge(platform, cartridgeLine);
        if (cartridge is null && platform.CartridgeRequired)
            throw new CompileError(block, NeedsCartridge(platform));
        var areas = new BlockAreas();
        foreach (var (line, first, last) in lines)
        {
            if (first < 0 || last > 0xFFFF)
                throw new CompileError(line.Pos, $"The range ${first:X}..=${last:X} is outside memory ($0000-$FFFF). Use addresses in that range");
            if (first > last)
                throw new CompileError(line.Pos, $"The range starts at ${first:X4} and ends at ${last:X4}, before its start. Write the lower address first");
            var range = new AddressRange((int)first, (int)last);
            if (line.Kind is MemoryKind.Bank or MemoryKind.Chr && cartridge is null)
                throw new CompileError(line.Pos, $"A '{line.Kind.Word()}' area is part of a cartridge board, which the {platform.Title} does not have. Remove the line");
            if (line.Kind == MemoryKind.Bank) AddBank(areas, cartridge!, line, range);
            if (line.Kind == MemoryKind.Chr)
            {
                // The PPU's addresses: not checked against the CPU's ranges.
                AddChr(areas, cartridge!, line, range);
                continue;
            }
            if (cartridge is not null) CheckOnCartridge(platform, cartridge, line, range);
            Add(areas, line, range);
        }
        // Memory the compiler may not use is taken out of the platform's areas, when the block does not replace them.
        var taken = areas.Reserved.Concat(areas.Regions.Values).Concat(areas.Overlays.Values).Concat(areas.Banks.Values).ToList();
        var programAreas = areas.Program.Count > 0 ? areas.Program
            : Subtract(cartridge is not null ? cartridge.Board.CodeAreas : [new AddressRange(platform.LoadAddress, platform.RamEnd)], taken);
        if (programAreas.Count == 0)
            throw new CompileError(lines[0].Line.Pos, cartridge is not null
                ? $"No program area is left: the reserved ranges cover all of the board '{cartridge.Board.Name}'s ROM for the program, {List(cartridge.Board.CodeAreas)}. Leave room for the program"
                : $"No program area is left: the reserved and region ranges cover all of ${platform.LoadAddress:X4}-${platform.RamEnd:X4}. Leave room for the program");
        var layout = new MemoryLayout
        {
            Program = programAreas,
            ProgramReplaced = areas.Program.Count > 0,
            ZeroPage = areas.ZeroPage.Count > 0 ? areas.ZeroPage.OrderBy(z => z.First).ToList() : Subtract(PlatformZeroPage(platform), taken.Concat(areas.Program)),
            Variables = platform.VariableRam is var (ramFirst, ramLast) ? Subtract([new AddressRange(ramFirst, ramLast)], taken) : [],
            Cartridge = cartridge,
        };
        if (platform.VariableRam is var (vFirst, vLast) && layout.Variables.Count == 0)
            throw new CompileError(block, $"No RAM is left for variables: the block's ranges cover all of {new AddressRange(vFirst, vLast)}. Leave room for the variables");
        layout.Reserved.AddRange(areas.Reserved);
        foreach (var (name, range) in areas.Regions) layout.Regions[name] = range;
        foreach (var (name, range) in areas.Overlays) layout.Overlays[name] = range;
        foreach (var (name, range) in areas.Chr) layout.Chr[name] = range;
        foreach (var (name, range) in areas.Banks) layout.Banks[name] = range;
        layout.Volatile.AddRange(areas.VolatileRanges);
        return layout;
    }

    /// <summary>The block's areas by kind, as its lines are read.</summary>
    private sealed class BlockAreas
    {
        public readonly List<AddressRange> Program = [], ZeroPage = [], Reserved = [], VolatileRanges = [];
        public readonly Dictionary<string, AddressRange> Regions = [], Overlays = [], Chr = [], Banks = [];
        /// <summary>Every range that may not overlap another, with the line that declared it.</summary>
        public readonly List<(AddressRange Range, MemoryLine Line)> Claimed = [];
    }

    /// <summary>A <c>bank</c> line: the board's bank window, once (spec 8.4).</summary>
    private static void AddBank(BlockAreas areas, Cartridge cartridge, MemoryLine line, AddressRange range)
    {
        var board = cartridge!.Board;
        if (board.BankArea is not { } window)
            throw new CompileError(line.Pos, $"The board '{board.Name}' has no banks. Remove the line, or name a board with banks, such as 'cartridge uxrom'");
        if (range != window)
            throw new CompileError(line.Pos, $"The board '{board.Name}' switches banks at {window}, not at {range}. Write '${window.First:X4}..=${window.Last:X4} bank {line.Name}'");
        if (areas.Banks.Count > 0)
            throw new CompileError(line.Pos, $"The board '{board.Name}' has one bank area, and the block already declares it as '{areas.Banks.Keys.First()}'. Remove the line");
        if (areas.Regions.ContainsKey(line.Name!) || areas.Overlays.ContainsKey(line.Name!) || areas.Chr.ContainsKey(line.Name!))
            throw new CompileError(line.Pos, $"The area '{line.Name}' is declared twice. Give each area its own name");
        areas.Banks[line.Name!] = range;
    }

    /// <summary>A <c>chr</c> line: the board's CHR ROM, in the PPU's pattern tables, once (spec 8.4).</summary>
    private static void AddChr(BlockAreas areas, Cartridge cartridge, MemoryLine line, AddressRange range)
    {
        // The PPU's addresses: not checked against the CPU's ranges.
        if (!cartridge.Board.ChrRom)
            throw new CompileError(line.Pos,
                $"The board '{cartridge.Board.Name}' has CHR RAM, empty at power-on, not CHR ROM. Remove the line, make the tiles ordinary data and copy them to the PPU with nes.ppu_copy");
        if (range.Last > 0x1FFF)
            throw new CompileError(line.Pos, $"A chr area lies in the PPU's pattern tables, $0000-$1FFF; {range} does not. Use addresses in that range");
        if (areas.Chr.Count > 0)
            throw new CompileError(line.Pos, $"The board '{cartridge.Board.Name}' has one CHR ROM, and the block already declares it as '{areas.Chr.Keys.First()}'. Remove the line, or join the two");
        if (areas.Regions.ContainsKey(line.Name!) || areas.Overlays.ContainsKey(line.Name!))
            throw new CompileError(line.Pos, $"The area '{line.Name}' is declared twice. Give each area its own name");
        areas.Chr[line.Name!] = range;
    }

    /// <summary>A cartridge has only the machine's RAM and the board's ROM (review M27, findings 1 and 2).</summary>
    private static void CheckOnCartridge(Platform platform, Cartridge cartridge, MemoryLine line, AddressRange range)
    {
        var ram = new AddressRange(0, platform.RamEnd);
        var board = cartridge.Board;
        var rom = new AddressRange(board.CodeAreas.Concat(board.BankArea is { } b ? [b] : []).Min(a => a.First),
            board.CodeAreas.Concat(board.BankArea is { } b2 ? [b2] : []).Max(a => a.Last));
        // Decision 2026-11-14 (question 160): the board gives the code areas, so the line has no meaning.
        if (line.Kind == MemoryKind.Program)
            throw new CompileError(line.Pos,
                $"A 'program' line has no meaning on the {platform.Title}: the board '{board.Name}' gives the code areas ({List(board.CodeAreas)}), and the variables are in RAM, {ram}. Remove the line, or use 'reserved' or 'region' to take a range out of ROM or RAM");
        if (line.Kind == MemoryKind.Overlay)
            throw new CompileError(line.Pos,
                $"A cartridge has no disk to load an overlay from, and the board '{board.Name}' no RAM at {range} to load it into. " +
                (board.BankArea is { } window
                    ? $"Remove the line, and place modules in banks: '${window.First:X4}..=${window.Last:X4} bank {line.Name}' and 'import m @ {line.Name}[0]'"
                    : "Remove the line, or use a board with banks ('cartridge uxrom') and place modules in a bank area"));
        if (line.Kind == MemoryKind.Region && !ram.Contains(range))
            throw new CompileError(line.Pos,
                $"A region must lie in RAM, {ram}; {range} does not ({new AddressRange(platform.RamEnd + 1, 0x1FFF)} repeats the RAM, and the board '{board.Name}' has no RAM elsewhere). Use addresses in that range");
        if (line.Kind == MemoryKind.Reserved && !ram.Contains(range) && !rom.Contains(range))
            throw new CompileError(line.Pos,
                $"A reserved range must lie in RAM, {ram}, or in the board's ROM, {rom}; {range} does not. Use addresses in one of them");
    }

    /// <summary>A program, zeropage, reserved, region, volatile or overlay line; no range but a volatile one may overlap another.</summary>
    private static void Add(BlockAreas areas, MemoryLine line, AddressRange range)
    {
        switch (line.Kind)
        {
            case MemoryKind.Program: areas.Program.Add(range); break;
            case MemoryKind.Zeropage:
                if (range.Last > 0xFF)
                    throw new CompileError(line.Pos, $"A zeropage range must lie in $00-$FF; {range} does not. Use addresses below $100");
                areas.ZeroPage.Add(range);
                break;
            case MemoryKind.Reserved: areas.Reserved.Add(range); break;
            case MemoryKind.Region:
                if (areas.Overlays.ContainsKey(line.Name!) || !areas.Regions.TryAdd(line.Name!, range))
                    throw new CompileError(line.Pos, $"The region '{line.Name}' is declared twice. Give each region its own name");
                break;
            case MemoryKind.Volatile: areas.VolatileRanges.Add(range); break;
            case MemoryKind.Overlay:
                if (!areas.Overlays.TryAdd(line.Name!, range) || areas.Regions.ContainsKey(line.Name!) || areas.Banks.ContainsKey(line.Name!))
                    throw new CompileError(line.Pos, $"The overlay area '{line.Name}' is declared twice. Give each area its own name");
                break;
        }
        if (line.Kind != MemoryKind.Volatile)
        {
            foreach (var (other, otherLine) in areas.Claimed)
                if (range.Overlaps(other))
                    throw new CompileError(line.Pos,
                        $"{range} ({Describe(line)}) overlaps {other} ({Describe(otherLine)}, line {otherLine.Pos.Line}). Ranges may not overlap, except volatile ones. Move one of them");
            areas.Claimed.Add((range, line));
        }
    }

    /// <summary>The error for a NES program without a <c>cartridge</c> line.</summary>
    public static string NeedsCartridge(Platform platform) =>
        $"A {platform.Title} program is a cartridge image, so its memory block names the board. " +
        $"Write 'memory {{ cartridge {platform.Boards[0].Name} vertical }}' (boards: {string.Join(", ", platform.Boards.Select(b => b.Name))}; mirroring: vertical or horizontal)";

    private static string Describe(MemoryLine line) => line.Name is null ? line.Kind.Word() : $"{line.Kind.Word()} {line.Name}";

    /// <summary>The parts of <paramref name="areas"/> that lie outside <paramref name="taken"/>, in order.</summary>
    public static List<AddressRange> Subtract(IEnumerable<AddressRange> areas, IEnumerable<AddressRange> taken)
    {
        var result = areas.ToList();
        foreach (var t in taken.OrderBy(t => t.First))
        {
            var next = new List<AddressRange>();
            foreach (var a in result)
            {
                if (!a.Overlaps(t)) { next.Add(a); continue; }
                if (a.First < t.First) next.Add(new AddressRange(a.First, t.First - 1));
                if (a.Last > t.Last) next.Add(new AddressRange(t.Last + 1, a.Last));
            }
            result = next;
        }
        return result;
    }

    /// <summary>Zero-page ranges as listed in error messages: "$02-$8F, $FB-$FE".</summary>
    public static string ListZeroPage(IEnumerable<AddressRange> areas) =>
        string.Join(", ", areas.Select(a => a.First == a.Last ? $"${a.First:X2}" : $"${a.First:X2}-${a.Last:X2}"));

    /// <summary>The areas as listed in error messages: "$0801-$1FFF, $4000-$9FFF".</summary>
    public static string List(IEnumerable<AddressRange> areas) => string.Join(", ", areas);
}
