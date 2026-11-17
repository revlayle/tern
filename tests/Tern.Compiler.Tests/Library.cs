using Tern.Compiler.Tools;
using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>
/// Calls standard library routines directly in the emulator, without compiling a program per call.
/// The program imports the modules and names the routines in main, behind a flag that is never set,
/// so that they are compiled; the startup code runs once to set up the globals.
/// </summary>
public sealed class Library
{
    public CompileResult Result { get; }
    public Ram Ram { get; } = new();
    public Cpu6502 Cpu { get; }

    /// <param name="calls">Statements that call the routines to test, e.g. "math.mul8(0, 0)".</param>
    public Library(string imports, string calls, Action<Ram>? setup = null)
    {
        Result = Run.Compile($"{imports}\nvar go: bool\nfn main() {{\n    if go {{\n{calls}\n    }}\n}}\n");
        StartState.Fill(Ram);
        // tern run's machine loads the file, as for a run (review code-quality-tests 6).
        var bus = Machine.For(Result.Platform, Result).Load(Result, Ram);
        setup?.Invoke(Ram);
        Cpu = new Cpu6502(bus, Result.Platform.Cpu) { PC = (ushort)Result.Entry, SP = 0xFF };
        StartState.Set(Cpu);
        RunToHalt(1_000_000);
    }

    public int Address(string name) =>
        Result.Symbols.TryGetValue(name, out var a) ? a : throw new KeyNotFoundException($"No symbol '{name}'");

    public void Set8(string name, int value) => Ram.Bytes[Address(name)] = (byte)value;
    public void Set16(string name, int value)
    {
        Ram.Bytes[Address(name)] = (byte)value;
        Ram.Bytes[Address(name) + 1] = (byte)(value >> 8);
    }
    public byte U8(string name) => Ram.Bytes[Address(name)];
    public ushort U16(string name) => (ushort)(Ram.Bytes[Address(name)] | (Ram.Bytes[Address(name) + 1] << 8));

    /// <summary>The 16-bit result in A (low) and X (high).</summary>
    public int AX => Cpu.A | (Cpu.X << 8);

    /// <summary>The cycles of the <c>jsr</c> a caller makes: the run starts at the routine, so they are added (the rts is run).</summary>
    private const int JsrCycles = 6;

    /// <summary>Calls a routine with the given registers. Returns its cycles, including the jsr and rts.</summary>
    public long Call(string function, int a = 0, int x = 0, int y = 0, bool carry = false, long maxCycles = 1_000_000)
    {
        int ret = Result.HaltAddress - 1;   // rts returns to the address after the one pushed
        Ram.Bytes[0x01FF] = (byte)(ret >> 8);
        Ram.Bytes[0x01FE] = (byte)ret;
        Cpu.SP = 0xFD;
        Cpu.A = (byte)a;
        Cpu.X = (byte)x;
        Cpu.Y = (byte)y;
        Cpu.C = carry;
        Cpu.PC = (ushort)Address(function);
        long start = Cpu.Cycles;
        RunToHalt(maxCycles);
        return Cpu.Cycles - start + JsrCycles;
    }

    private void RunToHalt(long maxCycles)
    {
        long end = Cpu.Cycles + maxCycles;
        while (Cpu.PC != Result.HaltAddress)
        {
            Cpu.Step();
            if (Cpu.Cycles > end)
                throw new Xunit.Sdk.XunitException($"Did not return within {maxCycles} cycles (PC=${Cpu.PC:X4})");
        }
    }
}
