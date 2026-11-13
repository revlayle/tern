using Tern.Cpu;

namespace Tern.Compiler;

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

    public int CodeStart => LoadAddress + Header.Length;

    public bool IsVolatile(int first, int last) => VolatileAreas.Any(a => first <= a.Last && last >= a.First);

    /// <summary>The platforms a build can name with <c>--target</c> (spec 12.2).</summary>
    public static IReadOnlyList<Platform> All => [C64];

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
    };
}
