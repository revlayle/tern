using Tern.Compiler;
using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>Compiles a program and runs it in the emulator until main returns.</summary>
public sealed class Run
{
    public CompileResult Result { get; }
    public Ram Ram { get; } = new();
    public Cpu6502 Cpu { get; }

    private Run(string source, long maxCycles, string? sourcePath, Action<Ram>? setup)
    {
        Result = TernCompiler.Compile(source, sourcePath: sourcePath);
        Ram.Load((ushort)Result.LoadAddress, Result.Image);
        setup?.Invoke(Ram);
        Cpu = new Cpu6502(Ram) { PC = (ushort)Result.Entry, SP = 0xFF };
        while (Cpu.PC != Result.HaltAddress)
        {
            Cpu.Step();
            if (Cpu.Cycles > maxCycles)
                throw new Xunit.Sdk.XunitException($"Program did not finish within {maxCycles} cycles (PC=${Cpu.PC:X4})\n{Result.Listing}");
        }
    }

    /// <param name="setup">Prepares memory after loading the program, e.g. with stand-ins for ROM routines.</param>
    public static Run Program(string source, long maxCycles = 5_000_000, string? sourcePath = null, Action<Ram>? setup = null) =>
        new(source, maxCycles, sourcePath, setup);

    public int Address(string name) =>
        Result.Symbols.TryGetValue(name, out var a) ? a : throw new KeyNotFoundException($"No symbol '{name}'");

    public byte U8(string name, int offset = 0) => Ram.Bytes[Address(name) + offset];
    public sbyte I8(string name, int offset = 0) => (sbyte)U8(name, offset);
    public ushort U16(string name) => (ushort)(U8(name) | (U8(name, 1) << 8));
    public short I16(string name) => (short)U16(name);

    /// <summary>Element <paramref name="i"/> of a split [u16; n] array.</summary>
    public ushort U16At(string name, int i, int length) => (ushort)(U8(name, i) | (U8(name, length + i) << 8));
}
