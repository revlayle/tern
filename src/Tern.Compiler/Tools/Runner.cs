using System.Text;
using Tern.Compiler.Backend;
using Tern.Cpu;

namespace Tern.Compiler.Tools;

/// <summary>How <see cref="Runner.Run"/> runs a program.</summary>
public sealed record RunOptions
{
    /// <summary>The run stops after this many cycles if main has not returned.</summary>
    public long MaxCycles { get; init; } = 20_000_000;
    /// <summary>If above 0, an IRQ is raised this many cycles after the last one was taken (through the vector at $FFFE, once the program enables interrupts).</summary>
    public long IrqEvery { get; init; }
    /// <summary>The main file's name, for the source lines of index check errors.</summary>
    public string MainFile { get; init; } = "program.tern";
    /// <summary>Bytes written into memory after the program is loaded, before it starts (<c>--set</c>).</summary>
    public IReadOnlyList<MemorySet> Sets { get; init; } = [];
    /// <summary>On the NES: the buttons of pad 1 or 2 in a frame (<see cref="NesBus.Buttons"/>); null for none.</summary>
    public Func<long, int, byte>? Buttons { get; init; }
}

/// <summary>
/// A byte to write before a run, as <c>--set $DC00=$FF</c> gives it: an address and a value, each
/// decimal or starting with <c>$</c>. Used for inputs such as the joystick, which the emulator does not have.
/// </summary>
public sealed record MemorySet(int Address, byte Value)
{
    public static MemorySet Parse(string text)
    {
        int equals = text.IndexOf('=');
        if (equals < 0)
            throw new FormatException($"'{text}': write an address and a byte, as $DC00=$FF");
        int address = Number(text[..equals].Trim(), 0xFFFF, "an address");
        int value = Number(text[(equals + 1)..].Trim(), 0xFF, "a byte (0 to 255)");
        return new MemorySet(address, (byte)value);

        int Number(string s, int max, string what)
        {
            bool ok = s.StartsWith('$')
                ? int.TryParse(s[1..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int n)
                : int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n);
            if (!ok || n > max) throw new FormatException($"'{text}': '{s}' is not {what}; write $DC00=$FF or 56320=255");
            return n;
        }
    }
}

/// <summary>Cycles spent inside one function, callees included, over a run.</summary>
public sealed record FunctionTime(string Name, long Calls, long Cycles);

/// <summary>
/// The result of a run. <see cref="Cycles"/> counts from the program's first instruction; <see cref="MainCycles"/>
/// from main's first instruction to its return (null if main never started). <see cref="IndexErrors"/>
/// are the array indexes and <c>indexed match</c> values the emulator found outside their tables (M20).
/// </summary>
public sealed record RunResult(
    bool Returned, string Stop, long Cycles, long? MainCycles, int Interrupts, Ram Ram, Cpu6502 Cpu,
    string Output, Profile Profile, IReadOnlyList<FunctionTime> Functions, IReadOnlyList<IndexError> IndexErrors)
{
    /// <summary>The machine the program ran on.</summary>
    public required Platform Platform { get; init; }
    /// <summary>The machine the program ran on, which holds its screen and chips.</summary>
    internal Machine Machine { get; init; } = null!;
    /// <summary>On the NES: the PPU's memory, OAM and frames (<see cref="NesBus"/>); null elsewhere.</summary>
    public NesBus? Nes => (Machine as NesMachine)?.Bus;
}


/// <summary>
/// Runs a compiled program in the built-in emulator, from its start until main returns or a cycle
/// limit: <c>tern run</c> and <c>tern bench</c>. Memory is plain RAM: the chips (the C64's VIC-II, SID
/// and CIAs, the Atari's ANTIC, GTIA, POKEY and PIA) are not emulated, so their registers read back
/// what was written. Memory and registers start as <see cref="StartState"/> sets them, not zero; the
/// text screen starts blank. A few ROM routines have stand-ins that take no cycles (<see cref="C64Machine"/>,
/// <see cref="AtariMachine"/>); text they print goes to the screen and to <see cref="RunResult.Output"/>.
/// Reaching any other ROM address stops the run. Array indexes and <c>indexed match</c> values are
/// checked (<see cref="IndexChecker"/>); a match value outside its span stops the run, since its jump
/// goes to an address no one chose.
/// </summary>
public static class Runner
{
    public static RunResult Run(CompileResult program, RunOptions? options = null)
    {
        options ??= new RunOptions();
        if (program.HaltAddress < 0)
            throw new RunError("A binary without main cannot be run on its own: run the program that loads and calls it");
        var (machine, ram, cpu) = Start(program, options);

        var inProgram = program.Sections.Select(s => (s.Address, End: s.Address + s.Size)).ToList();
        bool InProgram(int pc) => inProgram.Any(s => pc >= s.Address && pc < s.End);
        bool InRom(int pc) => program.Platform.Rom.Any(r => pc >= r.First && pc <= r.Last);
        int main = program.Symbols.GetValueOrDefault("main", -1);

        var profile = new Profile();
        var output = new StringBuilder();
        var calls = new CallTimes(program, cpu, machine.PlaceOf);
        long? mainStart = null;
        long nextIrq = options.IrqEvery;
        int interrupts = 0;
        string stop;
        bool returned = false;
        var checker = new IndexChecker(program, options.MainFile, machine.PlaceOf);

        while (true)
        {
            if (cpu.PC == program.HaltAddress) { returned = true; stop = "main returned"; break; }
            if (cpu.Cycles >= options.MaxCycles) { stop = $"stopped after {options.MaxCycles} cycles; main had not returned"; break; }
            long beforeIrq = cpu.Cycles;
            if (options.IrqEvery > 0 && cpu.Cycles >= nextIrq && cpu.Irq())
            {
                interrupts++;
                profile.InterruptCycles += cpu.Cycles - beforeIrq;
                nextIrq = cpu.Cycles + options.IrqEvery;
                calls.Entered(cpu.PC);
            }
            int pc = cpu.PC;
            if (InRom(pc) && !InProgram(pc))
            {
                if (machine.Has(pc))
                {
                    if (machine.Call(pc, cpu, ram, output) is { } why) { stop = why; break; }
                    // Return as rts would.
                    ushort low = ram.Bytes[0x100 + (byte)(cpu.SP + 1)], high = ram.Bytes[0x100 + (byte)(cpu.SP + 2)];
                    cpu.SP += 2;
                    cpu.PC = (ushort)((high << 8 | low) + 1);
                    calls.Left();
                    continue;
                }
                stop = $"reached ${pc:X4} in ROM, which the emulator does not have (stand-ins: {machine.StandIns})";
                break;
            }
            byte opcode = machine.Peek(ram, pc);
            if (opcode == 0x00 && !machine.RunsBrk)
            {
                stop = $"brk at ${pc:X4} (often a jump into empty memory)";
                break;
            }
            if (pc == main && mainStart is null) mainStart = cpu.Cycles;
            if (checker.Before(cpu)) { stop = "stopped at an 'indexed match' value outside its span (see the index check)"; break; }
            long before = cpu.Cycles;
            int place = machine.PlaceOf(pc);
            try { cpu.Step(); }
            catch (IllegalOpcodeException e) { stop = $"illegal opcode ${e.Opcode:X2} at ${e.Address:X4}"; break; }
            // The instruction's cycles; an NMI the NES takes after it is counted apart.
            profile.Add(place, cpu.Cycles - before);
            long beforeNmi = cpu.Cycles;
            bool interrupted = machine.AfterStep(cpu);
            profile.InterruptCycles += cpu.Cycles - beforeNmi;
            if (interrupted) { interrupts++; calls.Entered(cpu.PC); }
            if (opcode is 0x20 or 0x00) calls.Entered(cpu.PC);   // jsr, or brk into the IRQ handler
            calls.Left();
        }

        // Up to halt: main's rts included, the jmp halt after it not run.
        long? mainCycles = cpu.Cycles - mainStart;
        return new RunResult(returned, stop, cpu.Cycles, mainCycles, interrupts, ram, cpu, output.ToString(), profile, calls.Times(), checker.Errors)
        {
            Platform = program.Platform,
            Machine = machine,
        };
    }

    /// <summary>The machine at the program's start: its stand-ins and memory, the program loaded, the CPU at the entry.</summary>
    private static (Machine Machine, Ram Ram, Cpu6502 Cpu) Start(CompileResult program, RunOptions options)
    {
        var machine = Machine.For(program.Platform, program);
        var ram = new Ram();
        StartState.Fill(ram);
        machine.Start(ram);
        var bus = machine.Load(program, ram, options.Buttons);
        foreach (var set in options.Sets) ram.Bytes[set.Address] = set.Value;
        var cpu = new Cpu6502(bus, program.Platform.Cpu) { PC = (ushort)program.Entry, SP = 0xFF, I = true };
        StartState.Set(cpu);
        return (machine, ram, cpu);
    }

    /// <summary>The calls of each function and the cycles spent in them, from where it is entered until its frame ends.</summary>
    private sealed class CallTimes
    {
        private readonly Cpu6502 _cpu;
        private readonly Func<int, int> _placeOf;
        /// <summary>The functions by the place of their first instruction (code in two banks may share an address).</summary>
        private readonly Dictionary<int, string> _functions;
        private readonly Dictionary<string, (long Calls, long Cycles)> _calls = [];
        private readonly Stack<(string Name, int Sp, long Start)> _active = new();

        public CallTimes(CompileResult program, Cpu6502 cpu, Func<int, int> placeOf)
        {
            (_cpu, _placeOf) = (cpu, placeOf);
            _functions = program.Sections
                .Where(p => program.Symbols.TryGetValue(p.Name, out int at) && at == p.Address)
                .GroupBy(p => CodePlace.Of(p.Address, CodePlace.BankOf(p.Overlay))).ToDictionary(g => g.Key, g => g.First().Name);
        }

        public void Entered(int at)
        {
            if (_functions.TryGetValue(_placeOf(at), out var name)) _active.Push((name, _cpu.SP, _cpu.Cycles));
        }

        /// <summary>A function's frame ends when the stack pointer rises above where it was on entry (rts, rti).</summary>
        public void Left()
        {
            while (_active.Count > 0 && _cpu.SP > _active.Peek().Sp) Close(_active.Pop());
        }

        /// <summary>The functions' times, most cycles first; the functions still running end now.</summary>
        public List<FunctionTime> Times()
        {
            while (_active.Count > 0) Close(_active.Pop());
            return _calls.Select(c => new FunctionTime(c.Key, c.Value.Calls, c.Value.Cycles)).OrderByDescending(t => t.Cycles).ToList();
        }

        private void Close((string Name, int Sp, long Start) f)
        {
            var (n, c) = _calls.GetValueOrDefault(f.Name);
            _calls[f.Name] = (n + 1, c + _cpu.Cycles - f.Start);
        }
    }

    /// <summary>The text screen's address, columns and rows after a run (the C64's at $0400, the Atari's at SAVMSC, the NES's nametable at $2000 in PPU memory).</summary>
    public static (int Address, int Columns, int Rows) Screen(RunResult run) => run.Machine.Screen(run.Ram);

    /// <summary>The lines of the text screen after a run, without trailing spaces.</summary>
    public static IReadOnlyList<string> ScreenText(RunResult run) => run.Machine.ScreenText(run.Ram);
}
