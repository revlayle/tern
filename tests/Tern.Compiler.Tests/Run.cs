using Tern.Compiler.Tools;
using Tern.Compiler;
using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>Compiles a program and runs it in the emulator until main returns.</summary>
public sealed class Run
{
    public CompileResult Result { get; }
    public Ram Ram { get; } = new();
    public Cpu6502 Cpu { get; }

    /// <summary>Text tern run's stand-ins printed: in a run with <c>standIns</c>, and in every Apple II run.</summary>
    public System.Text.StringBuilder Output { get; } = new();

    /// <summary>The number of IRQs the CPU took.</summary>
    public int Interrupts { get; }

    /// <summary>On the NES: the PPU's memory, OAM, frames and banks (<see cref="Tools.NesBus"/>); null elsewhere.</summary>
    public Tools.NesBus? Nes { get; }

    /// <summary>Array indexes and indexed match values outside their tables (M20). A run with any fails, unless checkIndexes is false.</summary>
    public IReadOnlyList<Tools.IndexError> IndexErrors { get; }

    /// <summary>The reads and writes of the watched addresses; null when the run watches none.</summary>
    public BusLog? Log { get; }

    private Run(string source, long maxCycles, string? sourcePath, Action<Ram>? setup, string? libraryDirectory, long irqEvery,
        IReadOnlyDictionary<int, Action<Run>>? traps, bool checkIndexes, Platform? platform, Func<long, int, byte>? buttons, bool zeroStart,
        Func<CompileResult, IEnumerable<int>>? watch, bool comparePlain, bool standIns = false, bool optimize = true)
    {
        Result = TernCompiler.Compile(source, platform, sourcePath: sourcePath, libraryDirectory: libraryDirectory, optimize: optimize);
        if (!zeroStart) StartState.Fill(Ram);
        // tern run's machine: its stand-ins for ROM routines and memory as the machine starts (review
        // code-quality-tests 12). An Apple II program always has it, since its startup code calls the ROM
        // (SETNORM, INIT, SETVID, SETKBD, HOME) and the MLI (GET_PREFIX); a test's traps take its place.
        var machine = standIns || Result.Platform.Format == OutputFormat.System ? Machine.For(Result.Platform, Result) : null;
        machine?.Start(Ram);
        // An Apple II system file loads at $2000, and its startup code moves the areas into place.
        if (Result.Platform.Format == OutputFormat.System) Ram.Load((ushort)Result.Platform.LoadAddress, Result.File);
        else if (Result.Platform.Format == OutputFormat.Nes) Nes = new Tools.NesBus(Ram, Result.File) { Buttons = buttons };
        else Ram.Load((ushort)Result.LoadAddress, Result.Image);
        setup?.Invoke(Ram);
        // As after reset: no IRQ until the program enables it.
        IBus bus = Nes is null ? Ram : Nes;
        if (watch is not null) bus = Log = new BusLog(bus, watch(Result));
        Cpu = new Cpu6502(bus, Result.Platform.Cpu) { PC = (ushort)Result.Entry, SP = 0xFF, I = true };
        if (!zeroStart) StartState.Set(Cpu);
        long nextIrq = irqEvery;
        var checker = new Tools.IndexChecker(Result, sourcePath is null ? "program.tern" : Path.GetFileName(sourcePath),
            Nes is null ? null : pc => Result.PlaceOf(pc, Nes.SelectedBank));
        IndexErrors = checker.Errors;
        Action<Run>? trap = null;
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
            bool trapped = traps is not null && traps.TryGetValue(Cpu.PC, out trap);
            if (trapped || machine is not null && machine.Has(Cpu.PC))
            {
                // A stand-in for a ROM routine: do its work, then return as an rts would.
                if (trapped) trap!(this);
                else if (machine!.Call(Cpu.PC, Cpu, Ram, Output) is { } stop)
                    throw new Xunit.Sdk.XunitException($"The run stopped: {stop}");
                ushort low = Ram.Bytes[0x100 + (byte)(Cpu.SP + 1)], high = Ram.Bytes[0x100 + (byte)(Cpu.SP + 2)];
                Cpu.SP += 2;
                Cpu.PC = (ushort)((high << 8 | low) + 1);
                continue;
            }
            if (checker.Before(Cpu)) break;   // an indexed match value outside its span: the jump goes nowhere chosen
            Cpu.Step();
            Nes?.Tick(Cpu);
            if (Cpu.Cycles > maxCycles)
                throw new Xunit.Sdk.XunitException($"Program did not finish within {maxCycles} cycles (PC=${Cpu.PC:X4})\n{Result.Listing}");
        }
        if (checkIndexes && checker.Errors.Count > 0)
            throw new Xunit.Sdk.XunitException("The emulator's index check reported:\n" + string.Join("\n", checker.Errors));
        // A run whose inputs do not depend on the layout or on timing is repeated without the optimizer,
        // and every global must end with the same value (review code-quality-tests, finding 8).
        // Addresses and noinit memory differ with the layout, so programs that hold them are left out.
        if (optimize && comparePlain && setup is null && traps is null && !standIns && irqEvery == 0 && buttons is null
            && !source.Contains("addr(") && !source.Contains("noinit"))
            CompareWithPlain(new Run(source, maxCycles * 4, sourcePath, null, libraryDirectory, 0, null, checkIndexes, platform, null,
                zeroStart, null, comparePlain: false, optimize: false));
    }

    /// <summary>Fails when a global ends with another value in the run of the same program compiled without the optimizer.</summary>
    private void CompareWithPlain(Run plain)
    {
        var differences = new List<string>();
        foreach (var v in Result.Variables.Values.Where(v => v.Function is null && !v.IsData && !v.Type.Contains("ptr")))
        {
            if (!plain.Result.Variables.TryGetValue(v.Name, out var p)) continue;
            var mine = Ram.Bytes.AsSpan(v.Address, v.Size);
            var theirs = plain.Ram.Bytes.AsSpan(p.Address, p.Size);
            if (!mine.SequenceEqual(theirs))
                differences.Add($"{v.Name}: {Convert.ToHexString(mine)} optimized, {Convert.ToHexString(theirs)} without the optimizer");
        }
        if (differences.Count > 0)
            throw new Xunit.Sdk.XunitException("The optimized program ends with other values than the plain one:\n"
                + string.Join("\n", differences) + "\n\nOptimized:\n" + Result.Listing + "\nWithout the optimizer:\n" + plain.Result.Listing);
    }

    /// <param name="setup">Prepares memory after loading the program, e.g. with stand-ins for ROM routines.</param>
    /// <param name="irqEvery">If set, raises an IRQ this many cycles after the last one was taken (taken once interrupts are enabled).</param>
    /// <param name="traps">Stand-ins for ROM routines: when the CPU reaches the address, the action runs and the routine returns without executing.</param>
    /// <param name="platform">The target; the C64 if null.</param>
    /// <param name="buttons">On the NES: the buttons of pad 1 or 2 in a frame (<see cref="Tools.NesBus.Buttons"/>).</param>
    /// <param name="zeroStart">Starts with zero memory and zero registers instead of the <see cref="StartState"/>.</param>
    /// <param name="watch">The addresses whose reads and writes the CPU makes are logged in <see cref="Log"/>, given the compiled program.</param>
    /// <param name="standIns">Runs with tern run's stand-ins for the platform's ROM routines (<see cref="Machine"/>), after any <paramref name="traps"/>.</param>
    /// <param name="comparePlain">
    /// Runs the program a second time compiled without the optimizer and fails if a global ends with another value.
    /// Only for runs with no setup, traps, IRQs or buttons, whose inputs may depend on the layout or the timing.
    /// </param>
    public static Run Program(string source, long maxCycles = 5_000_000, string? sourcePath = null, Action<Ram>? setup = null,
        string? libraryDirectory = null, long irqEvery = 0, IReadOnlyDictionary<int, Action<Run>>? traps = null, bool checkIndexes = true,
        Platform? platform = null, Func<long, int, byte>? buttons = null, bool zeroStart = false,
        Func<CompileResult, IEnumerable<int>>? watch = null, bool comparePlain = true, bool standIns = false) =>
        new(source, maxCycles, sourcePath, setup, libraryDirectory, irqEvery, traps, checkIndexes, platform, buttons, zeroStart, watch, comparePlain, standIns);

    public int Address(string name) =>
        Result.Symbols.TryGetValue(name, out var a) ? a : throw new KeyNotFoundException($"No symbol '{name}'");

    public byte U8(string name, int offset = 0) => Ram.Bytes[Address(name) + offset];
    public sbyte I8(string name, int offset = 0) => (sbyte)U8(name, offset);
    public ushort U16(string name) => (ushort)(U8(name) | (U8(name, 1) << 8));
    public short I16(string name) => (short)U16(name);

    /// <summary>Element <paramref name="i"/> of a split [u16; n] array.</summary>
    public ushort U16At(string name, int i, int length) => (ushort)(U8(name, i) | (U8(name, length + i) << 8));
}

/// <summary>Command lines for <see cref="Tools.Cli.Run"/>.</summary>
/// <summary>The bytes memory starts with (<see cref="StartState"/>), for checking that a run left a byte as it was.</summary>
public static class Seeded
{
    private static readonly byte[] Bytes = Fill();

    private static byte[] Fill()
    {
        var ram = new Ram();
        StartState.Fill(ram);
        return ram.Bytes;
    }

    public static byte At(int address) => Bytes[address];
}

public static class Command
{
    /// <summary>A tern command line built for the C64: <c>--target c64</c> after the subcommand (run, bench), or first.</summary>
    public static string[] C64(params string[] args) =>
        args.Length > 0 && args[0] is "run" or "bench" ? [args[0], "--target", "c64", .. args[1..]] : ["--target", "c64", .. args];
}
