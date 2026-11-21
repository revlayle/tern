namespace Tern.Compiler.Tests;

/// <summary>
/// Reads the results of a benchmark program from memory. The benchmark checks use it, so that the same
/// check works for the Tern, assembly, Prog8 and C versions, which place their variables differently.
/// </summary>
/// <param name="locate">The address of byte <c>offset</c> of the named variable. An array of structs is
/// addressed as the Tern version lays it out: one table per field, 16 bytes each.</param>
/// <param name="splitWords">Whether a 16-bit array keeps its low bytes and then its high bytes (Tern, Prog8)
/// or interleaves them (C).</param>
public sealed class Results(Tern.Cpu.Ram ram, Func<string, int, int> locate, bool splitWords = true)
{
    public Tern.Cpu.Ram Ram { get; } = ram;

    public byte U8(string name, int offset = 0) => Ram.Bytes[locate(name, offset)];
    public ushort U16(string name) => (ushort)(U8(name) | (U8(name, 1) << 8));

    /// <summary>Element <paramref name="i"/> of a [u16; length] array.</summary>
    public ushort U16At(string name, int i, int length) =>
        splitWords
            ? (ushort)(U8(name, i) | (U8(name, length + i) << 8))
            : (ushort)(U8(name, 2 * i) | (U8(name, 2 * i + 1) << 8));
}
