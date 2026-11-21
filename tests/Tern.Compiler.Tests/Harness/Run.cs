using Tern.Compiler.Tools;
using Tern.Compiler;
using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>
/// Compiles a program and runs it in the emulator until main returns. <c>Program(source, ...)</c> compiles and
/// runs; <see cref="Compile"/> and <see cref="Of"/> do the two apart (review code-quality-tests 13), so a test can
/// read a symbol's address before the run. The machine is <c>tern run</c>'s (<see cref="Machine"/>): it loads the
/// file, gives the bus and ticks its chips; its stand-ins for ROM routines are used when asked for.
/// </summary>
public sealed class Run
{
    public CompileResult Result { get; }
    public Ram Ram { get; } = new();
    public Cpu6502 Cpu { get; }

    /// <summary>Text tern run's stand-ins printed: in a run with <c>standIns</c>, and in every Apple II run.</summary>
    public System.Text.StringBuilder Output { get; } = new();

    /// <summary>The number of interrupts the CPU took: the IRQs of <c>irqEvery</c>, and the NES's NMIs.</summary>
    public int Interrupts { get; }

    /// <summary>On the NES: the PPU's memory, OAM, frames and banks (<see cref="Tools.NesBus"/>); null elsewhere.</summary>
    public Tools.NesBus? Nes => (_machine as NesMachine)?.Bus;

    /// <summary>Array indexes and indexed match values outside their tables (M20). A run with any fails, unless checkIndexes is false.</summary>
    public IReadOnlyList<Tools.IndexError> IndexErrors { get; }

    /// <summary>The reads and writes of the watched addresses; null when the run watches none.</summary>
    public BusLog? Log { get; }

    private readonly Machine _machine;

    /// <summary>How a run goes; see the parameters of <c>Program</c>.</summary>
    /// <param name="Source">The program's source, for the comparison with a plain build; null: no comparison.</param>
    private sealed record Settings(string? Source, long MaxCycles, string? SourcePath, Action<Ram>? Setup, string? LibraryDirectory, long IrqEvery,
        IReadOnlyDictionary<int, Action<Run>>? Traps, bool CheckIndexes, Func<long, int, byte>? Buttons, bool ZeroStart,
        Func<CompileResult, IEnumerable<int>>? Watch, bool ComparePlain, bool StandIns);

    private Run(CompileResult result, Settings s)
    {
        Result = result;
        if (!s.ZeroStart) StartState.Fill(Ram);
        // tern run's machine. Its stand-ins for ROM routines and memory as the machine starts come when asked
        // for (review code-quality-tests 12), and always where the startup code calls the ROM (the Apple II's
        // SETNORM, INIT, SETVID, SETKBD, HOME and GET_PREFIX); a test's traps take their place.
        _machine = Machine.For(Result.Platform, Result);
        bool standIns = s.StandIns || _machine.StartupCallsRom;
        if (standIns) _machine.Start(Ram);
        IBus bus = _machine.Load(Result, Ram, s.Buttons);
        s.Setup?.Invoke(Ram);
        if (s.Watch is not null) bus = Log = new BusLog(bus, s.Watch(Result));
        // As after reset: no IRQ until the program enables it.
        Cpu = new Cpu6502(bus, Result.Platform.Cpu) { PC = (ushort)Result.Entry, SP = 0xFF, I = true };
        if (!s.ZeroStart) StartState.Set(Cpu);
        long nextIrq = s.IrqEvery;
        var checker = new Tools.IndexChecker(Result, s.SourcePath is null ? "program.tern" : Path.GetFileName(s.SourcePath), _machine.PlaceOf);
        IndexErrors = checker.Errors;
        Action<Run>? trap = null;
        while (Cpu.PC != Result.HaltAddress)
        {
            if (s.IrqEvery > 0 && Cpu.Cycles >= nextIrq)
            {
                // The IRQ line stays raised until the CPU takes it, as a VIC-II raster interrupt does.
                if (Cpu.Irq())
                {
                    Interrupts++;
                    nextIrq = Cpu.Cycles + s.IrqEvery;
                }
            }
            bool trapped = s.Traps is not null && s.Traps.TryGetValue(Cpu.PC, out trap);
            if (trapped || standIns && _machine.Has(Cpu.PC))
            {
                // A stand-in for a ROM routine: do its work, then return as an rts would.
                if (trapped) trap!(this);
                else if (_machine.Call(Cpu.PC, Cpu, Ram, Output) is { } stop)
                    throw new Xunit.Sdk.XunitException($"The run stopped: {stop}");
                ushort low = Ram.Bytes[0x100 + (byte)(Cpu.SP + 1)], high = Ram.Bytes[0x100 + (byte)(Cpu.SP + 2)];
                Cpu.SP += 2;
                Cpu.PC = (ushort)((high << 8 | low) + 1);
                continue;
            }
            if (checker.Before(Cpu)) break;   // an indexed match value outside its span: the jump goes nowhere chosen
            Cpu.Step();
            if (_machine.AfterStep(Cpu)) Interrupts++;   // the NES's NMI, counted as tern run counts it
            if (Cpu.Cycles > s.MaxCycles)
                throw new Xunit.Sdk.XunitException($"Program did not finish within {s.MaxCycles} cycles (PC=${Cpu.PC:X4})\n{Result.Listing}");
        }
        if (s.CheckIndexes && checker.Errors.Count > 0)
            throw new Xunit.Sdk.XunitException("The emulator's index check reported:\n" + string.Join("\n", checker.Errors));
        // A run whose inputs do not depend on the layout or on timing is repeated without the optimizer,
        // and every global must end with the same value (review code-quality-tests, finding 8).
        // Addresses and noinit memory differ with the layout, so programs that hold them are left out.
        if (s is { ComparePlain: true, Source: { } source, Setup: null, Traps: null, StandIns: false, IrqEvery: 0, Buttons: null }
            && !source.Contains("addr(") && !source.Contains("noinit"))
            CompareWithPlain(new Run(Compile(source, s.SourcePath, s.LibraryDirectory, Result.Platform, optimize: false),
                s with { MaxCycles = s.MaxCycles * 4, ComparePlain = false, Watch = null }));
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

    /// <summary>Compiles a program for a run with <see cref="Of"/>, as its target needs it (<see cref="TestTarget.Adapt"/>).</summary>
    /// <param name="platform">The target; <see cref="TestTarget.Default"/> if null.</param>
    public static CompileResult Compile(string source, string? sourcePath = null, string? libraryDirectory = null, Platform? platform = null, bool optimize = true)
    {
        platform ??= TestTarget.Default;
        return TernCompiler.Compile(TestTarget.Adapt(source, platform), platform, sourcePath: sourcePath, libraryDirectory: libraryDirectory, optimize: optimize);
    }

    /// <param name="setup">Prepares memory after loading the program, e.g. with stand-ins for ROM routines.</param>
    /// <param name="irqEvery">If set, raises an IRQ this many cycles after the last one was taken (taken once interrupts are enabled).</param>
    /// <param name="traps">Stand-ins for ROM routines: when the CPU reaches the address, the action runs and the routine returns without executing.</param>
    /// <param name="platform">The target; <see cref="TestTarget.Default"/> if null.</param>
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
        new(Compile(source, sourcePath, libraryDirectory, platform),
            new Settings(source, maxCycles, sourcePath, setup, libraryDirectory, irqEvery, traps, checkIndexes, buttons, zeroStart, watch, comparePlain, standIns));

    /// <summary>Runs a program compiled with <see cref="Compile"/>, as <c>Program</c> does, without the comparison with a plain build.</summary>
    public static Run Of(CompileResult result, long maxCycles = 5_000_000, Action<Ram>? setup = null, long irqEvery = 0,
        IReadOnlyDictionary<int, Action<Run>>? traps = null, bool checkIndexes = true, Func<long, int, byte>? buttons = null, bool standIns = false) =>
        new(result, new Settings(null, maxCycles, null, setup, null, irqEvery, traps, checkIndexes, buttons, false, null, false, standIns));

    public int Address(string name) =>
        Result.Symbols.TryGetValue(name, out var a) ? a : throw new KeyNotFoundException($"No symbol '{name}'");

    public byte U8(string name, int offset = 0) => Ram.Bytes[Address(name) + offset];
    public sbyte I8(string name, int offset = 0) => (sbyte)U8(name, offset);
    public ushort U16(string name) => (ushort)(U8(name) | (U8(name, 1) << 8));
    public short I16(string name) => (short)U16(name);

    /// <summary>Element <paramref name="i"/> of a split [u16; n] array.</summary>
    public ushort U16At(string name, int i, int length) => (ushort)(U8(name, i) | (U8(name, length + i) << 8));
}

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

/// <summary>Command lines for <see cref="Tools.Cli.Run"/>.</summary>
public static class Command
{
    /// <summary>A tern command line built for the C64: <c>--target c64</c> after the subcommand (run, bench), or first.</summary>
    public static string[] C64(params string[] args) =>
        args.Length > 0 && args[0] is "run" or "bench" ? [args[0], "--target", "c64", .. args[1..]] : ["--target", "c64", .. args];

    /// <summary>Runs tern with <paramref name="args"/> and returns its exit code, standard output and standard error (review code-quality-tests 7: one copy).</summary>
    public static (int Code, string Output, string Error) Tern(params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        int code = Cli.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }
}
