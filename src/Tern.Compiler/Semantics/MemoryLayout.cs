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
    /// <summary>Volatile ranges added by the memory block, in addition to the platform's.</summary>
    public List<AddressRange> Volatile { get; } = [];
    /// <summary>
    /// RAM for the variables' block when code is in ROM (<see cref="Platform.VariableRam"/>, less the
    /// block's other areas); empty when the variables follow the program in <see cref="Program"/>.
    /// </summary>
    public List<AddressRange> Variables { get; private init; } = [];
    /// <summary>The board the build is an image for (the <c>cartridge</c> line), or null.</summary>
    public Cartridge? Cartridge { get; private init; }

    /// <summary>The block has <c>program</c> lines, which replace the platform's program areas.</summary>
    public bool ProgramReplaced { get; private init; }

    public bool IsVolatile(int first, int last) => Volatile.Any(a => first <= a.Last && last >= a.First);

    /// <summary>
    /// The platform's map: one program area from the load address to the end of usable RAM; on a
    /// platform with boards, the first board's ROM, with the variables in <see cref="Platform.VariableRam"/>.
    /// </summary>
    public static MemoryLayout Default(Platform platform) => new()
    {
        Program = platform.Boards.Count > 0 ? [.. platform.Boards[0].CodeAreas] : [new AddressRange(platform.LoadAddress, platform.RamEnd)],
        Variables = platform.VariableRam is var (first, last) ? [new AddressRange(first, last)] : [],
        ZeroPage = PlatformZeroPage(platform),
    };

    /// <summary>The board and options of a <c>cartridge</c> line (spec 8.4, 12.2).</summary>
    private static Cartridge BuildCartridge(Platform platform, CartridgeLine line)
    {
        if (platform.Boards.Count == 0)
            throw new CompileError(line.Pos, platform.Name == "apple2"
                ? "The Apple II has no cartridges. Remove the 'cartridge' line"
                : $"'cartridge' is not supported yet for the {platform.Name}: cartridge images are built only for the NES so far. Remove the 'cartridge' line");
        var board = platform.Boards.FirstOrDefault(b => b.Name == line.Board)
            ?? throw new CompileError(line.BoardPos,
                $"The {platform.Name.ToUpperInvariant()} has no board '{line.Board}'. Use one of: {string.Join(", ", platform.Boards.Select(b => b.Name))}");
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
        if (cartridge is null && platform.Boards.Count > 0)
            throw new CompileError(block, NeedsCartridge(platform));
        var program = new List<AddressRange>();
        var zeroPage = new List<AddressRange>();
        var reserved = new List<AddressRange>();
        var regions = new Dictionary<string, AddressRange>();
        var overlays = new Dictionary<string, AddressRange>();
        var volatileRanges = new List<AddressRange>();
        // Every range that may not overlap another, with the line that declared it.
        var claimed = new List<(AddressRange Range, MemoryLine Line)>();

        foreach (var (line, first, last) in lines)
        {
            if (first < 0 || last > 0xFFFF)
                throw new CompileError(line.Pos, $"The range ${first:X}..=${last:X} is outside memory ($0000-$FFFF). Use addresses in that range");
            if (first > last)
                throw new CompileError(line.Pos, $"The range starts at ${first:X4} and ends at ${last:X4}, before its start. Write the lower address first");
            var range = new AddressRange((int)first, (int)last);
            if (line.Kind is "bank" or "chr" && cartridge is null)
                throw new CompileError(line.Pos, $"A '{line.Kind}' area is part of a cartridge board, which the {platform.Name} does not have. Remove the line");
            if (line.Kind is "bank" or "chr")
                throw new CompileError(line.Pos, $"'{line.Kind}' is not supported yet. Remove the line");
            switch (line.Kind)
            {
                case "program" when cartridge is not null:
                    throw new CompileError(line.Pos,
                        $"'program' lines are not supported yet on the {platform.Name.ToUpperInvariant()}: the board '{cartridge.Board.Name}' gives the program's ROM ({List(cartridge.Board.CodeAreas)}). Remove the line, or use 'reserved' for memory the program keeps for itself");
                case "program": program.Add(range); break;
                case "zeropage":
                    if (last > 0xFF)
                        throw new CompileError(line.Pos, $"A zeropage range must lie in $00-$FF; {range} does not. Use addresses below $100");
                    zeroPage.Add(range);
                    break;
                case "reserved": reserved.Add(range); break;
                case "region":
                    if (overlays.ContainsKey(line.Name!) || !regions.TryAdd(line.Name!, range))
                        throw new CompileError(line.Pos, $"The region '{line.Name}' is declared twice. Give each region its own name");
                    break;
                case "volatile": volatileRanges.Add(range); break;
                case "overlay":
                    if (!overlays.TryAdd(line.Name!, range) || regions.ContainsKey(line.Name!))
                        throw new CompileError(line.Pos, $"The overlay area '{line.Name}' is declared twice. Give each area its own name");
                    break;
            }
            if (line.Kind != "volatile")
            {
                foreach (var (other, otherLine) in claimed)
                    if (range.Overlaps(other))
                        throw new CompileError(line.Pos,
                            $"{range} ({Describe(line)}) overlaps {other} ({Describe(otherLine)}, line {otherLine.Pos.Line}). Ranges may not overlap, except volatile ones. Move one of them");
                claimed.Add((range, line));
            }
        }

        // Memory the compiler may not use is taken out of the platform's areas, when the block does not replace them.
        var taken = reserved.Concat(regions.Values).Concat(overlays.Values).ToList();
        var programAreas = program.Count > 0 ? program
            : Subtract(cartridge is not null ? cartridge.Board.CodeAreas : [new AddressRange(platform.LoadAddress, platform.RamEnd)], taken);
        if (programAreas.Count == 0)
            throw new CompileError(lines[0].Line.Pos, $"No program area is left: the reserved and region ranges cover all of ${platform.LoadAddress:X4}-${platform.RamEnd:X4}. Leave room for the program");
        var layout = new MemoryLayout
        {
            Program = programAreas,
            ProgramReplaced = program.Count > 0,
            ZeroPage = zeroPage.Count > 0 ? zeroPage.OrderBy(z => z.First).ToList() : Subtract(PlatformZeroPage(platform), taken.Concat(program)),
            Variables = platform.VariableRam is var (ramFirst, ramLast) ? Subtract([new AddressRange(ramFirst, ramLast)], taken) : [],
            Cartridge = cartridge,
        };
        if (platform.VariableRam is var (vFirst, vLast) && layout.Variables.Count == 0)
            throw new CompileError(block, $"No RAM is left for variables: the block's ranges cover all of {new AddressRange(vFirst, vLast)}. Leave room for the variables");
        layout.Reserved.AddRange(reserved);
        foreach (var (name, range) in regions) layout.Regions[name] = range;
        foreach (var (name, range) in overlays) layout.Overlays[name] = range;
        layout.Volatile.AddRange(volatileRanges);
        return layout;
    }

    /// <summary>The error for a NES program without a <c>cartridge</c> line.</summary>
    public static string NeedsCartridge(Platform platform) =>
        $"A {platform.Name.ToUpperInvariant()} program is a cartridge image, so its memory block names the board. " +
        $"Write 'memory {{ cartridge {platform.Boards[0].Name} vertical }}' (boards: {string.Join(", ", platform.Boards.Select(b => b.Name))}; mirroring: vertical or horizontal)";

    private static string Describe(MemoryLine line) => line.Name is null ? line.Kind : $"{line.Kind} {line.Name}";

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
