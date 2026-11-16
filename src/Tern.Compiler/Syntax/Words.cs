using Tern.Compiler.Semantics;

namespace Tern.Compiler.Syntax;

/// <summary>
/// Words with a meaning in one place that are not keywords, so they stay usable as names elsewhere. The
/// parser compares a token with one only through <see cref="Is"/>, so renaming a word or adding one is a
/// line here (review code-quality-syntax 21).
/// </summary>
public static class Word
{
    public const string Memory = "memory", Cartridge = "cartridge", Encoding = "encoding", Default = "default",
        Indexed = "indexed", Align = "align", Step = "step", Emit = "emit", Ptr = "ptr";

    /// <summary>The name that names nothing: <c>for _ in</c>, the <c>match</c> arm for every other value.</summary>
    public const string Discard = "_";

    /// <summary>True if <paramref name="t"/> is the word <paramref name="word"/>.</summary>
    public static bool Is(Token t, string word) => t.Kind == TokenKind.Identifier && t.Text == word;
}

/// <summary>The kinds of a memory block's lines (spec 8.1), written in lower case.</summary>
public enum MemoryKind { Program, Zeropage, Reserved, Region, Volatile, Overlay, Bank, Chr }

public static class MemoryKinds
{
    /// <summary>The kind as written in the memory block.</summary>
    public static string Word(this MemoryKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>The kind <paramref name="text"/> names, or null.</summary>
    public static MemoryKind? Named(string text) =>
        Enum.GetValues<MemoryKind>().Cast<MemoryKind?>().FirstOrDefault(k => k!.Value.Word() == text);

    /// <summary>A region, overlay, bank or chr line has a name after its kind.</summary>
    public static bool HasName(this MemoryKind kind) => kind is MemoryKind.Region or MemoryKind.Overlay or MemoryKind.Bank or MemoryKind.Chr;

    /// <summary>Every kind, for messages: "program, zeropage, ... or chr".</summary>
    public static string List
    {
        get
        {
            var words = Enum.GetValues<MemoryKind>().Select(k => k.Word()).ToList();
            return string.Join(", ", words.Take(words.Count - 1)) + " or " + words[^1];
        }
    }
}

public static class Registers
{
    /// <summary>
    /// The register or flag <paramref name="text"/> names (spec 7): after <c>using</c> and <c>keeps</c>, A, X, Y,
    /// AX, AY, XY, carry, zero, negative or overflow, as written; in assembly (<paramref name="asm"/>), A, X or Y
    /// in either case, as 6502 assemblers write them. Null for anything else. One reader for both
    /// (review code-quality-syntax 21).
    /// </summary>
    public static Register? Named(string text, bool asm = false) =>
        asm
            ? text.ToUpperInvariant() switch { "A" => Register.A, "X" => Register.X, "Y" => Register.Y, _ => null }
            : text switch
            {
                "A" => Register.A, "X" => Register.X, "Y" => Register.Y,
                "AX" => Register.AX, "AY" => Register.AY, "XY" => Register.XY,
                "carry" => Register.Carry, "zero" => Register.Zero, "negative" => Register.Negative,
                "overflow" => Register.Overflow,
                _ => null,
            };
}
