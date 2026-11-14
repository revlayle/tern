using Tern.Compiler.Semantics;

namespace Tern.Compiler;

/// <summary>
/// A cartridge board, named by the <c>cartridge</c> line of the memory block (spec 8.4, 12.2): where its
/// ROM is, which bank area it switches, its options and the library module that selects banks.
/// </summary>
public sealed class Board
{
    public required string Name { get; init; }
    /// <summary>The iNES mapper number.</summary>
    public required int Mapper { get; init; }
    /// <summary>The ROM the resident program fills, in the order the compiler fills it.</summary>
    public required AddressRange[] CodeAreas { get; init; }
    /// <summary>The window in which one bank at a time is visible, or null for a board without banks.</summary>
    public AddressRange? BankArea { get; init; }
    /// <summary>Most banks of <see cref="BankArea"/>'s size the image may have, with the fixed one.</summary>
    public int MaxBanks { get; init; } = 1;
    /// <summary>The board has CHR ROM, declared as a <c>chr</c> area; without it, CHR RAM.</summary>
    public bool ChrRom { get; init; }
    /// <summary>The library module that selects banks, or null.</summary>
    public string? BankModule { get; init; }

    /// <summary>The options of the NES boards in M27: the mirroring, one of the two, required.</summary>
    public static readonly string[] Mirroring = ["vertical", "horizontal"];

    /// <summary>NROM (mapper 0): 16 or 32 KB of PRG ROM, filled from $C000 so that a program of up to 16 KB is NROM-128, and 8 KB of CHR ROM.</summary>
    public static readonly Board Nrom = new()
    {
        Name = "nrom",
        Mapper = 0,
        CodeAreas = [new(0xC000, 0xFFFF), new(0x8000, 0xBFFF)],
        ChrRom = true,
    };

    /// <summary>UxROM (mapper 2): 16 KB banks at $8000-$BFFF, the last bank fixed at $C000-$FFFF; CHR RAM.</summary>
    public static readonly Board Uxrom = new()
    {
        Name = "uxrom",
        Mapper = 2,
        CodeAreas = [new(0xC000, 0xFFFF)],
        BankArea = new(0x8000, 0xBFFF),
        MaxBanks = 16,
        BankModule = "nes/uxrom",
    };
}

/// <summary>The board a build is for and its options, from the <c>cartridge</c> line.</summary>
public sealed record Cartridge(Board Board, bool VerticalMirroring);
