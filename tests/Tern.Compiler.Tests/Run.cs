using Tern.Compiler;
using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>Compiles a program and runs it in the emulator until main returns.</summary>
public sealed class Run
{
    public CompileResult Result { get; }
    public Ram Ram { get; } = new();
    public Cpu6502 Cpu { get; }

    /// <summary>The number of IRQs the CPU took.</summary>
    public int Interrupts { get; }

    private Run(string source, long maxCycles, string? sourcePath, Action<Ram>? setup, string? libraryDirectory, long irqEvery)
    {
        Result = TernCompiler.Compile(source, sourcePath: sourcePath, libraryDirectory: libraryDirectory);
        Ram.Load((ushort)Result.LoadAddress, Result.Image);
        setup?.Invoke(Ram);
        Cpu = new Cpu6502(Ram) { PC = (ushort)Result.Entry, SP = 0xFF, I = true }; // as after reset: no IRQ until the program enables it
        long nextIrq = irqEvery;
        while (Cpu.PC != Result.HaltAddress)
        {
            if (irqEvery > 0 && Cpu.Cycles >= nextIrq)
            {
                // The IRQ line stays raised until the CPU takes it, as a VIC-II raster interrupt does.
                if (Cpu.Irq())
                {
                    Interrupts++;
                    nextIrq = Cpu.Cycles + irqEvery;
                }
            }
            Cpu.Step();
            if (Cpu.Cycles > maxCycles)
                throw new Xunit.Sdk.XunitException($"Program did not finish within {maxCycles} cycles (PC=${Cpu.PC:X4})\n{Result.Listing}");
        }
    }

    /// <param name="setup">Prepares memory after loading the program, e.g. with stand-ins for ROM routines.</param>
    /// <param name="irqEvery">If set, raises an IRQ this many cycles after the last one was taken (taken once interrupts are enabled).</param>
    public static Run Program(string source, long maxCycles = 5_000_000, string? sourcePath = null, Action<Ram>? setup = null,
        string? libraryDirectory = null, long irqEvery = 0) =>
        new(source, maxCycles, sourcePath, setup, libraryDirectory, irqEvery);

    public int Address(string name) =>
        Result.Symbols.TryGetValue(name, out var a) ? a : throw new KeyNotFoundException($"No symbol '{name}'");

    public byte U8(string name, int offset = 0) => Ram.Bytes[Address(name) + offset];
    public sbyte I8(string name, int offset = 0) => (sbyte)U8(name, offset);
    public ushort U16(string name) => (ushort)(U8(name) | (U8(name, 1) << 8));
    public short I16(string name) => (short)U16(name);

    /// <summary>Element <paramref name="i"/> of a split [u16; n] array.</summary>
    public ushort U16At(string name, int i, int length) => (ushort)(U8(name, i) | (U8(name, length + i) << 8));
}
