using Tern.Cpu;

namespace Tern.Compiler.Tools;

/// <summary>
/// The machine's state before a program runs, in <c>tern run</c> and in the tests (review
/// code-quality-tests, finding 2; decision 2026-11-15, question 171). A real machine
/// does not start with zero registers and zero memory, so memory outside the image is filled with bytes
/// from a fixed seed, and A, X, Y and the carry are not zero. A removed load or a variable the startup
/// code forgot to clear then gives a wrong result instead of a lucky 0. The seed is fixed, so a failure repeats.
/// </summary>
public static class StartState
{
    private const int Seed = 6502;

    /// <summary>Fills all 64 KB with the seeded bytes; load the program over them afterwards.</summary>
    public static void Fill(Ram ram) => new Random(Seed).NextBytes(ram.Bytes);

    /// <summary>Sets A, X, Y and the flags as left by the code that ran before the program (decimal mode off).</summary>
    public static void Set(Cpu6502 cpu)
    {
        cpu.A = 0xA5;
        cpu.X = 0x5A;
        cpu.Y = 0xC3;
        cpu.C = true;
        cpu.V = true;
        cpu.N = true;
    }
}
