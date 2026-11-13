namespace Tern.Compiler;

/// <summary>
/// A mapping from characters to bytes, used for char and string literals. The target sets the
/// default; a prefix such as <c>screen"..."</c> names another one.
/// </summary>
public sealed class CharEncoding
{
    public string Name { get; }
    private readonly Func<char, int?> _map;

    private CharEncoding(string name, Func<char, int?> map)
    {
        Name = name;
        _map = map;
    }

    /// <summary>The byte for a character, or null if the encoding has none.</summary>
    public int? Encode(char c) => _map(c);

    /// <summary>
    /// Commodore PETSCII for the default (upper case and graphics) character set. Lower-case
    /// letters give the same codes as upper case, since this set shows only upper case.
    /// </summary>
    public static readonly CharEncoding Petscii = new("petscii", c => c switch
    {
        >= 'a' and <= 'z' => c - 'a' + 'A',
        'π' => 0xFF,
        _ => PetsciiCommon(c),
    });

    /// <summary>PETSCII for the lower/upper-case character set: a-z are $41-$5A, A-Z are $C1-$DA.</summary>
    public static readonly CharEncoding PetsciiMixed = new("petscii_mixed", c => c switch
    {
        >= 'a' and <= 'z' => c - 'a' + 0x41,
        >= 'A' and <= 'Z' => c - 'A' + 0xC1,
        _ => PetsciiCommon(c),
    });

    /// <summary>C64 screen codes (the bytes stored in screen memory) for the default character set.</summary>
    public static readonly CharEncoding Screen = new("screen", c => Petscii.Encode(c) switch
    {
        >= 0x20 and <= 0x3F and var p => p,
        >= 0x40 and <= 0x5F and var p => p - 0x40,
        0xFF => 0x5E,
        _ => null,
    });

    /// <summary>C64 screen codes for the lower/upper-case character set: a-z are $01-$1A, A-Z are $41-$5A.</summary>
    public static readonly CharEncoding ScreenMixed = new("screen_mixed", c => PetsciiMixed.Encode(c) switch
    {
        >= 0x20 and <= 0x3F and var p => p,
        >= 0x40 and <= 0x5F and var p => p - 0x40,
        >= 0xC1 and <= 0xDA and var p => p - 0x80,
        _ => null,
    });

    /// <summary>
    /// Atari ATASCII: ASCII from space to '_' and 'a' to 'z', '|', the four card suits, and '\n' as
    /// the end-of-line code $9B. ATASCII has no '{', '}', '~' or '`' (those codes are graphics and
    /// screen-editor controls).
    /// </summary>
    public static readonly CharEncoding Atascii = new("atascii", c => c switch
    {
        >= ' ' and <= '_' => c,
        >= 'a' and <= 'z' => c,
        '|' => 0x7C,
        '\n' => 0x9B,
        '♥' => 0x00,
        '♣' => 0x10,
        '♦' => 0x60,
        '♠' => 0x7B,
        _ => null,
    });

    /// <summary>
    /// Atari internal screen codes (the bytes ANTIC reads from screen memory): ATASCII $20-$5F are
    /// $00-$3F, $00-$1F are $40-$5F, $60-$7F stay. No '\n'.
    /// </summary>
    public static readonly CharEncoding AtariScreen = new("atari_screen", c => c == '\n' ? null : Atascii.Encode(c) switch
    {
        < 0x20 and var a => a + 0x40,
        < 0x60 and var a => a - 0x20,
        var a => a,
    });

    /// <summary>An encoding the program declares (spec 2.1): <paramref name="codes"/>, then the characters of <paramref name="baseEncoding"/>.</summary>
    public static CharEncoding Declared(string name, CharEncoding? baseEncoding, IReadOnlyDictionary<char, int> codes) =>
        new(name, c => codes.TryGetValue(c, out int code) ? code : baseEncoding?.Encode(c));

    /// <summary>The built-in encodings.</summary>
    public static IReadOnlyDictionary<string, CharEncoding> ByName { get; } =
        new[] { Petscii, PetsciiMixed, Screen, ScreenMixed, Atascii, AtariScreen }.ToDictionary(e => e.Name);

    /// <summary>Characters with the same code in both PETSCII character sets.</summary>
    private static int? PetsciiCommon(char c) => c switch
    {
        '\n' => 0x0D,
        >= ' ' and <= '@' => c,
        >= 'A' and <= 'Z' => c,
        '[' => 0x5B,
        '£' => 0x5C,
        ']' => 0x5D,
        '↑' => 0x5E,
        '←' => 0x5F,
        _ => null,
    };
}
