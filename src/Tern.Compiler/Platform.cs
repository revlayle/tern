using Tern.Cpu;

namespace Tern.Compiler;

/// <summary>Everything the compiler needs to know about a target machine.</summary>
public sealed class Platform
{
    /// <summary>The name <c>--target</c> takes, and the library's folder (<c>c64</c>, <c>apple2</c>).</summary>
    public required string Name { get; init; }
    /// <summary>The machine's name in messages: "C64", "Apple II".</summary>
    public required string Title { get; init; }
    public required CpuVariant Cpu { get; init; }

    /// <summary>The instructions of <see cref="Cpu"/>, which the checker, the backend and <c>tern run</c> use.</summary>
    public InstructionSet InstructionSet => InstructionSet.For(Cpu);
    /// <summary>Where the program file is loaded.</summary>
    public required int LoadAddress { get; init; }
    /// <summary>Bytes placed at <see cref="LoadAddress"/> before the code, e.g. a BASIC SYS line.</summary>
    public required byte[] Header { get; init; }
    /// <summary>What <see cref="Header"/> is, in messages: "BASIC line"; null for a platform with no header.</summary>
    public string? HeaderName { get; init; }
    /// <summary>
    /// Zero-page bytes the program may use: first and last address of each range, inclusive, in the
    /// order the compiler fills them. A variable cannot cross from one range to the next, so the
    /// largest range comes first.
    /// </summary>
    public required (int First, int Last)[] ZeroPage { get; init; }
    /// <summary>Last RAM address usable for variables after the program.</summary>
    public required int RamEnd { get; init; }
    /// <summary>
    /// Memory that changes behind the program's back (I/O registers, bytes the system's interrupt
    /// changes): first and last address, inclusive. Variables placed there are volatile (spec 4).
    /// </summary>
    public required (int First, int Last)[] VolatileAreas { get; init; }
    /// <summary>Encoding of char and string literals without a prefix; null on the NES, where the program declares one (spec 2.1).</summary>
    public required CharEncoding? DefaultEncoding { get; init; }
    /// <summary>The program file's kind (spec 12.2), which builds its file.</summary>
    public required OutputFormat Format { get; init; }
    /// <summary>The program file's extension: ".prg", ".xex", ".system", ".nes".</summary>
    public string FileExtension => Format.Extension;
    /// <summary>
    /// The cartridge boards the <c>cartridge</c> line may name (spec 8.4, 12.2), the default first. On the
    /// NES every program is a cartridge (<see cref="CartridgeRequired"/>).
    /// </summary>
    public IReadOnlyList<Board> Boards { get; init; } = [];
    /// <summary>Every program is a cartridge, so the memory block must name a board (the NES).</summary>
    public bool CartridgeRequired { get; init; }
    /// <summary>
    /// The machine takes cartridges, but the compiler builds none for it yet (spec 8.4: the C64's and the
    /// Atari's): a <c>cartridge</c> line is "not supported yet" rather than an error about the machine.
    /// </summary>
    public bool CartridgesPlanned { get; init; }
    /// <summary>
    /// RAM for variables, when code is in ROM (the NES): the variables' block goes there instead of after
    /// the program, and initial values are copied from ROM. Null: variables follow the program.
    /// </summary>
    public (int First, int Last)? VariableRam { get; init; }
    /// <summary>
    /// The extension of an overlay's file and of a binary without main: as the program's, except on
    /// the Apple II, where they are plain binary files (".bin"), not system files.
    /// </summary>
    public string BinaryExtension => Format.Plain.Extension;
    /// <summary>
    /// The library module whose prefix parameters the startup code fills when the program imports it (the
    /// Apple II's <c>io</c>, question 150), or null (review R6, suggestion 3).
    /// </summary>
    public string? PrefixModule { get; init; }
    /// <summary>The label file's format: the one the platform's main emulator loads (question 138).</summary>
    public required LabelFormat Labels { get; init; }
    /// <summary>
    /// The machine <c>tern run</c> models (<see cref="Tools.Machine"/>), given the platform and the build's
    /// overlay files; null for a platform it has no model of, which <c>tern run</c> then reports.
    /// </summary>
    internal Func<Platform, IReadOnlyList<OverlayFile>, Tools.Machine>? NewMachine { get; init; }
    /// <summary>ROM and I/O, where <c>tern run</c> finds no program code: first and last address, inclusive.</summary>
    public required (int First, int Last)[] Rom { get; init; }

    public int CodeStart => LoadAddress + Header.Length;

    public bool IsVolatile(int first, int last) => VolatileAreas.Any(a => first <= a.Last && last >= a.First);

    /// <summary>The platforms a build can name with <c>--target</c> (spec 12.2).</summary>
    public static IReadOnlyList<Platform> All => [C64, Atari, Apple2, Nes];

    /// <summary>The platform named <paramref name="name"/>, or null.</summary>
    public static Platform? ByName(string name) => All.FirstOrDefault(p => p.Name == name);

    /// <summary>"--target c64, --target atari, --target apple2 or --target nes", for messages that name the choices.</summary>
    public static string Choices => string.Join(", ", All.Take(All.Count - 1).Select(p => "--target " + p.Name)) + " or --target " + All[^1].Name;

    /// <summary>
    /// Commodore 64: a .prg loaded at $0801 that starts with the BASIC line "10 SYS 2061".
    /// Uses BASIC's zero page ($02-$8F), so the program does not return to BASIC, and $FB-$FE.
    /// </summary>
    public static readonly Platform C64 = new()
    {
        Name = "c64",
        HeaderName = "BASIC line",
        Title = "C64",
        CartridgesPlanned = true,
        Cpu = CpuVariant.Nmos6502,
        LoadAddress = 0x0801,
        Header =
        [
            0x0B, 0x08,             // address of the next BASIC line
            0x0A, 0x00,             // line number 10
            0x9E,                   // SYS
            0x32, 0x30, 0x36, 0x31, // "2061" = $080D
            0x00,                   // end of line
            0x00, 0x00,             // end of program
        ],
        ZeroPage = [(0x02, 0x8F), (0xFB, 0xFE)],    // spec 12.2; $FB-$FE is free in the KERNAL too
        RamEnd = 0x9FFF,
        VolatileAreas =
        [
            (0x00, 0x01),       // CPU port
            (0x91, 0x91),       // STOP key flag
            (0xA0, 0xA2),       // jiffy clock
            (0xC5, 0xC6),       // last key, number of keys in the buffer
            (0xCB, 0xCF),       // current key, cursor blink
            (0xF5, 0xF6),       // keyboard decode table
            (0x0277, 0x0280),   // keyboard buffer
            (0x028D, 0x028E),   // shift keys
            (0xD000, 0xDFFF),   // VIC-II, SID, color RAM, CIAs, cartridge I/O
        ],
        DefaultEncoding = CharEncoding.Petscii,
        Format = OutputFormat.Prg,
        Labels = LabelFormat.Vice,
        // The KERNAL's file names have no extension: "LEVEL1" is level1.prg.
        NewMachine = (_, overlays) => new Tools.C64Machine(new Tools.OverlayDisk(overlays, "")),
        Rom =
        [
            (0xA000, 0xBFFF),   // BASIC
            (0xD000, 0xFFFF),   // I/O and the KERNAL
        ],
    };

    /// <summary>
    /// Atari 8-bit with 48 KB or more, any OS version (spec 12.2): a .xex whose code starts at $2000,
    /// above DOS; variables up to $BC1F, below the OS's 40-column screen and its display list.
    /// </summary>
    public static readonly Platform Atari = new()
    {
        Name = "atari",
        Title = "Atari",
        CartridgesPlanned = true,
        Cpu = CpuVariant.Nmos6502,
        LoadAddress = 0x2000,
        Header = [],
        ZeroPage = [(0x80, 0xFF)],
        RamEnd = 0xBC1F,
        VolatileAreas =
        [
            (0x12, 0x14),       // RTCLOK, the frame clock
            (0x4D, 0x4F),       // ATRACT, DRKMSK, COLRSH: attract mode
            (0x0200, 0x02FF),   // OS vectors, frame-interrupt timers, shadow registers, controller copies, keyboard
            (0xD000, 0xD7FF),   // GTIA, POKEY, PIA, ANTIC, cartridge control
        ],
        DefaultEncoding = CharEncoding.Atascii,
        Format = OutputFormat.Xex,
        Labels = LabelFormat.Mads,
        NewMachine = (p, overlays) => new Tools.AtariMachine(new Tools.OverlayDisk(overlays, p.BinaryExtension)),
        Rom =
        [
            (0xC000, 0xFFFF),   // OS ROM (from $C000 on the XL/XE), I/O at $D000-$D7FF
        ],
    };

    /// <summary>
    /// Apple II with 64 KB under ProDOS 8 (spec 12.2): a system file loaded and started at $2000;
    /// variables up to $BEFF, below ProDOS's global page. The zero page leaves the monitor's, the
    /// disk drivers' and the MLI's bytes ($20-$4F) alone.
    /// </summary>
    public static readonly Platform Apple2 = new()
    {
        Name = "apple2",
        Title = "Apple II",
        Cpu = CpuVariant.Nmos6502,
        LoadAddress = 0x2000,
        Header = [],
        ZeroPage = [(0x50, 0xFF), (0x00, 0x1F)],    // filled from $50, so a variable meets the gap only after 176 bytes
        RamEnd = 0xBEFF,
        VolatileAreas =
        [
            (0xC000, 0xCFFF),   // soft switches, I/O and slot ROMs: a read alone can have an effect
        ],
        DefaultEncoding = CharEncoding.Apple2,
        Format = OutputFormat.ProDosSystem,
        PrefixModule = "io",
        Labels = LabelFormat.AppleWin,
        NewMachine = (p, overlays) => new Tools.Apple2Machine(new Tools.OverlayDisk(overlays, p.BinaryExtension)),
        Rom =
        [
            (0xBF00, 0xBFFF),   // ProDOS's global page: the MLI's entry at $BF00
            (0xC000, 0xFFFF),   // I/O, slot ROMs, BASIC and the monitor
        ],
    };

    /// <summary>
    /// NES (spec 12.2): every program is a cartridge image (.nes) for the board its memory block names.
    /// Code and data in ROM, variables in the 2 KB of RAM above the stack, all of zero page free; the
    /// PPU, APU and controller registers are volatile. No default encoding.
    /// </summary>
    public static readonly Platform Nes = new()
    {
        Name = "nes",
        Title = "NES",
        CartridgeRequired = true,
        Cpu = CpuVariant.Ricoh2A03,
        LoadAddress = 0x8000,
        Header = [],
        ZeroPage = [(0x00, 0xFF)],
        RamEnd = 0x07FF,
        VariableRam = (0x0200, 0x07FF),
        VolatileAreas =
        [
            (0x2000, 0x401F),   // PPU, APU, controller and OAM DMA registers
        ],
        DefaultEncoding = null,
        Format = OutputFormat.Ines,
        Labels = LabelFormat.Mesen,
        NewMachine = (_, _) => new Tools.NesMachine(),
        Rom = [],
        Boards = [Board.Nrom, Board.Uxrom],
    };
}
