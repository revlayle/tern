using System.Text;
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
    string Output, Profile Profile, IReadOnlyList<FunctionTime> Functions, IReadOnlyList<IndexError> IndexErrors);

/// <summary>
/// Runs a compiled C64 program in the built-in emulator, from its start until main returns or a cycle
/// limit: <c>tern run</c> and <c>tern bench</c>. Memory is plain RAM: the VIC-II, SID and CIAs are not
/// emulated, so their registers read back what was written. The screen starts cleared (spaces). A few
/// KERNAL routines have stand-ins that take no cycles: CHROUT ($FFD2) prints to the screen and to
/// <see cref="RunResult.Output"/>, GETIN ($FFE4) returns 0 (no key), PLOT ($FFF0) sets or reads the
/// cursor. Reaching any other ROM address stops the run. Only the program file is loaded, not its
/// overlays (the KERNAL LOAD that loads them has no stand-in), so a call into an overlay stops at a brk.
/// Array indexes and <c>indexed match</c> values are checked (<see cref="IndexChecker"/>); a match
/// value outside its span stops the run, since its jump goes to an address no one chose.
/// </summary>
public static class Runner
{
    public const int Screen = 0x0400, Columns = 40, Rows = 25;
    private const int Chrout = 0xFFD2, Getin = 0xFFE4, Plot = 0xFFF0;

    public static RunResult Run(CompileResult program, RunOptions? options = null)
    {
        options ??= new RunOptions();
        if (program.HaltAddress < 0)
            throw new InvalidOperationException("A binary without main cannot be run on its own: run the program that loads and calls it");
        var ram = new Ram();
        // As after the C64 starts: a cleared screen in light blue.
        ram.Bytes.AsSpan(Screen, Columns * Rows).Fill(32);
        ram.Bytes.AsSpan(0xD800, Columns * Rows).Fill(14);
        ram.Load((ushort)program.LoadAddress, program.Image);
        foreach (var set in options.Sets) ram.Bytes[set.Address] = set.Value;
        var cpu = new Cpu6502(ram) { PC = (ushort)program.Entry, SP = 0xFF, I = true };

        var inProgram = program.Sections.Select(s => (s.Address, End: s.Address + s.Size)).ToList();
        bool InProgram(int pc) => inProgram.Any(s => pc >= s.Address && pc < s.End);
        var functions = program.Symbols
            .Where(s => program.Sections.Any(p => p.Name == s.Key && p.Address == s.Value))
            .GroupBy(s => s.Value).ToDictionary(g => g.Key, g => g.First().Key);
        int main = program.Symbols.GetValueOrDefault("main", -1);

        var profile = new Profile();
        var output = new StringBuilder();
        var cursor = (Row: 0, Column: 0);
        var calls = new Dictionary<string, (long Calls, long Cycles)>();
        var active = new Stack<(string Name, int Sp, long Start)>();
        long? mainStart = null;
        long nextIrq = options.IrqEvery;
        int interrupts = 0;
        string stop;
        bool returned = false;
        var checker = new IndexChecker(program, options.MainFile);

        while (true)
        {
            if (cpu.PC == program.HaltAddress) { returned = true; stop = "main returned"; break; }
            if (cpu.Cycles >= options.MaxCycles) { stop = $"stopped after {options.MaxCycles} cycles; main had not returned"; break; }
            if (options.IrqEvery > 0 && cpu.Cycles >= nextIrq && cpu.Irq())
            {
                interrupts++;
                nextIrq = cpu.Cycles + options.IrqEvery;
                Entered(cpu.PC);
            }
            int pc = cpu.PC;
            if (pc >= 0xA000 && !(pc is >= 0xC000 and < 0xD000) && !InProgram(pc))
            {
                if (pc is Chrout or Getin or Plot)
                {
                    StandIn(pc);
                    // Return as rts would.
                    ushort low = ram.Bytes[0x100 + (byte)(cpu.SP + 1)], high = ram.Bytes[0x100 + (byte)(cpu.SP + 2)];
                    cpu.SP += 2;
                    cpu.PC = (ushort)((high << 8 | low) + 1);
                    Left();
                    continue;
                }
                stop = $"reached ${pc:X4} in ROM, which the emulator does not have (stand-ins: CHROUT $FFD2, GETIN $FFE4, PLOT $FFF0)";
                break;
            }
            byte opcode = ram.Bytes[pc];
            if (opcode == 0x00) { stop = $"brk at ${pc:X4} (often a jump into empty memory)"; break; }
            if (pc == main && mainStart is null) mainStart = cpu.Cycles;
            if (checker.Before(cpu)) { stop = "stopped at an 'indexed match' value outside its span (see the index check)"; break; }
            long before = cpu.Cycles;
            try { cpu.Step(); }
            catch (IllegalOpcodeException e) { stop = $"illegal opcode ${e.Opcode:X2} at ${e.Address:X4}"; break; }
            profile.Add(pc, cpu.Cycles - before);
            if (opcode == 0x20) Entered(cpu.PC);   // jsr
            Left();
        }
        while (active.Count > 0) Close(active.Pop());

        // Up to halt: main's rts included, the jmp halt after it not run.
        long? mainCycles = cpu.Cycles - mainStart;
        var times = calls.Select(c => new FunctionTime(c.Key, c.Value.Calls, c.Value.Cycles)).OrderByDescending(t => t.Cycles).ToList();
        return new RunResult(returned, stop, cpu.Cycles, mainCycles, interrupts, ram, cpu, output.ToString(), profile, times, checker.Errors);

        void Entered(int at)
        {
            if (functions.TryGetValue(at, out var name)) active.Push((name, cpu.SP, cpu.Cycles));
        }

        // A function's frame ends when the stack pointer rises above where it was on entry (rts, rti).
        void Left()
        {
            while (active.Count > 0 && cpu.SP > active.Peek().Sp) Close(active.Pop());
        }

        void Close((string Name, int Sp, long Start) f)
        {
            var (n, c) = calls.GetValueOrDefault(f.Name);
            calls[f.Name] = (n + 1, c + cpu.Cycles - f.Start);
        }

        void StandIn(int routine)
        {
            switch (routine)
            {
                case Chrout: Print(cpu.A); cpu.C = false; break;
                case Getin: cpu.A = 0; cpu.Z = true; cpu.N = false; cpu.C = false; break;
                default:
                    if (cpu.C) { cpu.X = (byte)cursor.Row; cpu.Y = (byte)cursor.Column; }
                    else cursor = (Math.Min((int)cpu.X, Rows - 1), Math.Min((int)cpu.Y, Columns - 1));
                    break;
            }
            ram.Bytes[0xD6] = (byte)cursor.Row;
            ram.Bytes[0xD3] = (byte)cursor.Column;
        }

        // CHROUT in the upper-case character set: letters, digits and punctuation, return, clear,
        // home and the four cursor moves; other control codes are ignored.
        void Print(byte ch)
        {
            switch (ch)
            {
                case 13: output.Append('\n'); NewLine(); return;
                case 147:
                    ram.Bytes.AsSpan(Screen, Columns * Rows).Fill(32);
                    cursor = (0, 0);
                    return;
                case 19: cursor = (0, 0); return;
                case 17: if (cursor.Row < Rows - 1) cursor.Row++; return;
                case 145: if (cursor.Row > 0) cursor.Row--; return;
                case 29: if (cursor.Column < Columns - 1) cursor.Column++; return;
                case 157: if (cursor.Column > 0) cursor.Column--; return;
            }
            int code = ch switch
            {
                >= 32 and < 64 => ch,
                >= 64 and < 96 => ch - 64,
                >= 96 and < 128 => ch - 32,
                >= 160 and < 192 => ch - 64,
                >= 192 and < 255 => ch - 128,
                255 => 94,
                _ => -1,
            };
            if (code < 0) return;
            output.Append(ScreenChar((byte)code));
            ram.Bytes[Screen + cursor.Row * Columns + cursor.Column] = (byte)code;
            ram.Bytes[0xD800 + cursor.Row * Columns + cursor.Column] = ram.Bytes[0x0286] == 0 ? (byte)14 : ram.Bytes[0x0286];
            if (++cursor.Column == Columns) NewLine();
        }

        void NewLine()
        {
            cursor.Column = 0;
            if (cursor.Row < Rows - 1) { cursor.Row++; return; }
            ram.Bytes.AsSpan(Screen + Columns, Columns * (Rows - 1)).CopyTo(ram.Bytes.AsSpan(Screen));
            ram.Bytes.AsSpan(Screen + Columns * (Rows - 1), Columns).Fill(32);
        }
    }

    /// <summary>
    /// A screen code as text, in the upper-case character set: letters, digits and punctuation as
    /// themselves, reversed characters as the plain ones, graphics characters as '#'.
    /// </summary>
    public static char ScreenChar(byte code)
    {
        code &= 0x7F;
        return code switch
        {
            0 => '@',
            >= 1 and <= 26 => (char)('A' + code - 1),
            27 => '[',
            28 => '\\',
            29 => ']',
            30 => '^',
            31 => '<',
            >= 32 and < 64 => (char)code,
            _ => '#',
        };
    }

    /// <summary>The 25 lines of the screen at $0400 as text, without trailing spaces.</summary>
    public static IReadOnlyList<string> ScreenText(Ram ram) =>
        Enumerable.Range(0, Rows).Select(r => new string(Enumerable.Range(0, Columns)
            .Select(c => ScreenChar(ram.Bytes[Screen + r * Columns + c])).ToArray()).TrimEnd()).ToList();
}
