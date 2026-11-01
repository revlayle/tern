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
    /// <summary>Encoding of char and string literals, e.g. PETSCII. Null means the character has no code.</summary>
    public required Func<char, int?> TextEncoding { get; init; }
    /// <summary>Encoding of <c>screen"..."</c> strings: the codes stored in screen memory.</summary>
    public required Func<char, int?> ScreenEncoding { get; init; }
    /// <summary>Name of <see cref="TextEncoding"/> for error messages.</summary>
    public required string TextEncodingName { get; init; }

    public int CodeStart => LoadAddress + Header.Length;

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
        TextEncoding = Petscii.FromChar,
        ScreenEncoding = Petscii.ScreenCode,
        TextEncodingName = "PETSCII",
    };
}
