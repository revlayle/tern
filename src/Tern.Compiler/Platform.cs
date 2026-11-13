using Tern.Cpu;

namespace Tern.Compiler;

/// <summary>The program file's format (spec 12.2).</summary>
public enum OutputFormat
{
    /// <summary>Commodore: a 2-byte load address, then every byte from the lowest to the highest address used, gaps as zeros.</summary>
    Prg,
    /// <summary>Atari DOS: $FF $FF, then load segments (first address, last address, bytes), then the run address in RUNAD.</summary>
    Xex,
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
    /// <summary>Zero-page bytes the program may use: first and last address, inclusive.</summary>
    public required (int First, int Last) ZeroPage { get; init; }
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
    /// <summary>The program file's extension: ".prg", ".xex".</summary>
    public string FileExtension => Format == OutputFormat.Prg ? ".prg" : ".xex";
    /// <summary>
    /// The label file's extension. Both are VICE's format ("al C:0810 .main"): ".vs" for VICE, ".lbl"
    /// for Altirra, which reads it as a cc65 label file.
    /// </summary>
    public string LabelExtension => Format == OutputFormat.Prg ? ".vs" : ".lbl";
    /// <summary>ROM and I/O, where <c>tern run</c> finds no program code: first and last address, inclusive.</summary>
    public required (int First, int Last)[] Rom { get; init; }

    /// <summary>The Atari's run address vector, RUNAD.</summary>
    public const int Runad = 0x02E0;

    /// <summary>
    /// The file holding <paramref name="segments"/> (merged runs of bytes): a .prg covers every byte
    /// from the lowest to the highest address, a .xex has one load segment per run and, if
    /// <paramref name="run"/> is given, a last segment that writes it to RUNAD.
    /// </summary>
    /// <param name="first">The lowest address of the segments.</param>
    /// <param name="image">The segments' bytes from <paramref name="first"/> on, gaps as zeros: the .prg's contents.</param>
    public byte[] FileOf(int first, byte[] image, IReadOnlyList<(int Address, byte[] Bytes)> segments, int? run)
    {
        var file = new List<byte>();
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
    public static IReadOnlyList<Platform> All => [C64, Atari];

    /// <summary>The platform named <paramref name="name"/>, or null.</summary>
    public static Platform? ByName(string name) => All.FirstOrDefault(p => p.Name == name);

    /// <summary>"--target c64 or --target atari", for messages that name the choices.</summary>
    public static string Choices => string.Join(" or ", All.Select(p => "--target " + p.Name));

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
        ZeroPage = (0x02, 0x8F),
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
        ZeroPage = (0x80, 0xFF),
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
        Rom =
        [
            (0xC000, 0xFFFF),   // OS ROM (from $C000 on the XL/XE), I/O at $D000-$D7FF
        ],
    };
}
