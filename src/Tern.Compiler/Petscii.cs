namespace Tern.Compiler;

/// <summary>Character codes of the Commodore 64 in its default (upper case and graphics) character set.</summary>
public static class Petscii
{
    /// <summary>
    /// The PETSCII code of a character, or null if it has none. Lower-case letters give the same
    /// codes as upper case, since the default character set shows only upper case.
    /// </summary>
    public static int? FromChar(char c) => c switch
    {
        '\n' => 0x0D,
        >= ' ' and <= '@' => c,
        >= 'A' and <= 'Z' => c,
        >= 'a' and <= 'z' => c - 'a' + 'A',
        '[' => 0x5B,
        '£' => 0x5C,
        ']' => 0x5D,
        '↑' => 0x5E,
        '←' => 0x5F,
        'π' => 0xFF,
        _ => null,
    };

    /// <summary>The screen code (the value stored in screen memory) of a character, or null.</summary>
    public static int? ScreenCode(char c) => FromChar(c) switch
    {
        >= 0x20 and <= 0x3F and var p => p,
        >= 0x40 and <= 0x5F and var p => p - 0x40,
        0xFF => 0x5E,
        _ => null,
    };
}
