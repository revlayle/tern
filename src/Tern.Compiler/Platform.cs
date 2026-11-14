using Tern.Cpu;

namespace Tern.Compiler;

/// <summary>The program file's format (spec 12.2).</summary>
public enum OutputFormat
{
    /// <summary>Commodore: a 2-byte load address, then every byte from the lowest to the highest address used, gaps as zeros.</summary>
    Prg,
    /// <summary>Atari DOS: $FF $FF, then load segments (first address, last address, bytes), then the run address in RUNAD.</summary>
    Xex,
    /// <summary>
    /// A ProDOS 8 system file: one block of bytes, loaded at $2000 and started there. A program's areas
    /// follow each other with no gaps; the startup code moves them into place (spec 12.2).
    /// </summary>
    System,
}

/// <summary>Everything the compiler needs to know about a target machine.</summary>
public sealed class Platform
{
    public required string Name { get; init; }
    public required CpuVariant Cpu { get; init; }
    /// <summary>Where the program file is loaded.</summary>
    public required int LoadAddress { get; init; }
    /// <summary>Bytes placed at <see cref="LoadAddress"/> before the code, e.g. a BASIC SYS line.</summary>
    public required byte[] Header { get; init; }
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
    /// <summary>Encoding of char and string literals without a prefix.</summary>
    public required CharEncoding DefaultEncoding { get; init; }
    public required OutputFormat Format { get; init; }
    /// <summary>The program file's extension: ".prg", ".xex", ".system".</summary>
    public string FileExtension => Format switch { OutputFormat.Prg => ".prg", OutputFormat.Xex => ".xex", _ => ".system" };
    /// <summary>
    /// The extension of an overlay's file and of a binary without main: as the program's, except on
    /// the Apple II, where they are plain binary files (".bin"), not system files.
    /// </summary>
    public string BinaryExtension => Format == OutputFormat.System ? ".bin" : FileExtension;
    /// <summary>The label file's format: the one the platform's main emulator loads (question 138).</summary>
    public required LabelFormat Labels { get; init; }
    /// <summary>ROM and I/O, where <c>tern run</c> finds no program code: first and last address, inclusive.</summary>
    public required (int First, int Last)[] Rom { get; init; }

    /// <summary>The Atari's run address vector, RUNAD.</summary>
    public const int Runad = 0x02E0;

    /// <summary>
    /// The file holding <paramref name="segments"/> (merged runs of bytes): a .prg covers every byte
    /// from the lowest to the highest address, a .xex has one load segment per run and, if
    /// <paramref name="run"/> is given, a last segment that writes it to RUNAD. On the Apple II this is
    /// the file of an overlay or a binary, the bytes from the lowest to the highest address; a
    /// program's system file is built with its startup code (<c>TernCompiler.Apple2</c>).
    /// </summary>
    /// <param name="first">The lowest address of the segments.</param>
    /// <param name="image">The segments' bytes from <paramref name="first"/> on, gaps as zeros: the .prg's contents.</param>
    public byte[] FileOf(int first, byte[] image, IReadOnlyList<(int Address, byte[] Bytes)> segments, int? run)
    {
        var file = new List<byte>();
        if (Format == OutputFormat.System) return image;
        if (Format == OutputFormat.Prg)
        {
            file.AddRange([(byte)first, (byte)(first >> 8)]);
            file.AddRange(image);
            return file.ToArray();
        }
        file.AddRange([0xFF, 0xFF]);
        void Segment(int address, byte[] bytes)
        {
            int last = address + bytes.Length - 1;
            file.AddRange([(byte)address, (byte)(address >> 8), (byte)last, (byte)(last >> 8)]);
            file.AddRange(bytes);
        }
        foreach (var (address, bytes) in segments.OrderBy(s => s.Address)) Segment(address, bytes);
        if (run is int at) Segment(Runad, [(byte)at, (byte)(at >> 8)]);
        return file.ToArray();
    }

    public int CodeStart => LoadAddress + Header.Length;

    public bool IsVolatile(int first, int last) => VolatileAreas.Any(a => first <= a.Last && last >= a.First);

    /// <summary>The platforms a build can name with <c>--target</c> (spec 12.2).</summary>
    public static IReadOnlyList<Platform> All => [C64, Atari, Apple2];

    /// <summary>The platform named <paramref name="name"/>, or null.</summary>
    public static Platform? ByName(string name) => All.FirstOrDefault(p => p.Name == name);

    /// <summary>"--target c64, --target atari or --target apple2", for messages that name the choices.</summary>
    public static string Choices => string.Join(", ", All.Take(All.Count - 1).Select(p => "--target " + p.Name)) + " or --target " + All[^1].Name;

    /// <summary>
    /// Commodore 64: a .prg loaded at $0801 that starts with the BASIC line "10 SYS 2061".
    /// Uses BASIC's zero page ($02-$8F), so the program does not return to BASIC.
    /// </summary>
    public static readonly Platform C64 = new()
    {
        Name = "c64",
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
        ZeroPage = [(0x02, 0x8F)],
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
        Format = OutputFormat.System,
        Labels = LabelFormat.AppleWin,
        Rom =
        [
            (0xBF00, 0xBFFF),   // ProDOS's global page: the MLI's entry at $BF00
            (0xC000, 0xFFFF),   // I/O, slot ROMs, BASIC and the monitor
        ],
    };
}
