using System.Text;
using Tern.Cpu;

namespace Tern.Compiler.Tools;

/// <summary>
/// What <c>tern run</c> knows of one platform beyond the CPU (review code-quality-tools 1): memory as the
/// machine starts, how a program file loads and the bus it runs on, what happens between instructions,
/// stand-ins for a few ROM routines, and the text screen. Each platform names its machine
/// (<see cref="Platform.NewMachine"/>); the runners (<see cref="Runner"/>, the tests' run) have no test
/// on the platform.
/// </summary>
internal abstract class Machine
{
    /// <summary>The platform's machine, for a run of <paramref name="program"/> (whose overlay files the machine's disk holds).</summary>
    /// <exception cref="RunError">The platform has no model in <c>tern run</c>.</exception>
    public static Machine For(Platform platform, CompileResult? program = null) =>
        platform.NewMachine?.Invoke(platform, program?.Overlays ?? [])
        ?? throw new RunError($"tern run has no model of the {platform.Title}. Use an emulator to run the program");

    /// <summary>
    /// A ROM routine with a stand-in (review code-quality-tools 6): its address, its name, what it
    /// does (returning why the run stops, or null), and a note for the list. <see cref="Has"/>,
    /// <see cref="Call"/> and <see cref="StandIns"/>, which the help prints, all come from <see cref="Routines"/>.
    /// </summary>
    protected sealed record StandIn(int Address, string Name, Func<Cpu6502, Ram, StringBuilder, string?> Run, string Note = "");

    /// <summary>The machine's stand-ins, in the order the help lists them.</summary>
    protected abstract IReadOnlyList<StandIn> Routines { get; }

    /// <summary>The routines with stand-ins, for the message when the run reaches another ROM address and for the help.</summary>
    public string StandIns => Routines.Count == 0 ? "none" : string.Join(", ", Routines.Select(s => $"{s.Name} ${s.Address:X4}{(s.Note == "" ? "" : " " + s.Note)}"));

    /// <summary>
    /// The program's own startup code calls ROM routines (the Apple II's), so a run needs the stand-ins
    /// even where a test leaves them out.
    /// </summary>
    public virtual bool StartupCallsRom => false;

    /// <summary>Sets up memory as after the machine starts, before the program is loaded.</summary>
    public abstract void Start(Ram ram);

    /// <summary>The program that was loaded (<see cref="Load"/>).</summary>
    protected CompileResult? Program { get; private set; }

    /// <summary>
    /// Loads <paramref name="program"/>'s file into <paramref name="ram"/> and returns the bus the CPU runs
    /// on: the RAM itself, unless the machine has chips on its bus (the NES). By default the image goes
    /// to its load address.
    /// </summary>
    /// <param name="buttons">The controllers' buttons in a frame, on a machine that models them (<see cref="NesBus.Buttons"/>).</param>
    public IBus Load(CompileResult program, Ram ram, Func<long, int, byte>? buttons = null)
    {
        Program = program;
        return LoadFile(program, ram, buttons);
    }

    /// <summary>See <see cref="Load"/>.</summary>
    protected virtual IBus LoadFile(CompileResult program, Ram ram, Func<long, int, byte>? buttons)
    {
        ram.Load((ushort)program.LoadAddress, program.Image);
        return ram;
    }

    /// <summary>
    /// After each instruction: the chips' time passes (the NES's frames). Returns whether that raised
    /// an interrupt the CPU took (the NES's NMI).
    /// </summary>
    public virtual bool AfterStep(Cpu6502 cpu) => false;

    /// <summary>The place (<see cref="Backend.CodePlace"/>) of the code at <paramref name="pc"/>: the address, or with banks the bank selected now.</summary>
    public virtual int PlaceOf(int pc) => pc;

    /// <summary>The byte at <paramref name="address"/> as the CPU sees it, read without the bus's side effects (RAM mirrors).</summary>
    public virtual byte Peek(Ram ram, int address) => ram.Bytes[address];

    /// <summary>
    /// A <c>brk</c> goes through the program's IRQ handler instead of stopping the run: on the NES,
    /// when the program sets the IRQ vector (decision 2026-11-15). The other machines run without their
    /// ROMs, so a brk there stops the run.
    /// </summary>
    public virtual bool RunsBrk => false;

    /// <summary>Whether <paramref name="pc"/> is a routine with a stand-in.</summary>
    public bool Has(int pc) => Routines.Any(s => s.Address == pc);

    /// <summary>
    /// Does the work of the routine at <paramref name="pc"/> (the caller then returns as rts would). Text
    /// printed goes to <paramref name="output"/>. Returns why the run stops, or null.
    /// </summary>
    public string? Call(int pc, Cpu6502 cpu, Ram ram, StringBuilder output) => Routines.Single(s => s.Address == pc).Run(cpu, ram, output);

    /// <summary>The text screen's address in <paramref name="ram"/> (in PPU memory on the NES), columns and rows.</summary>
    public abstract (int Address, int Columns, int Rows) Screen(Ram ram);

    /// <summary>A byte of screen memory as text: letters, digits and punctuation as themselves, others as '#'.</summary>
    protected abstract char ScreenChar(byte code);

    /// <summary>The address of the screen's row <paramref name="row"/>: rows follow each other, except on the Apple II.</summary>
    protected virtual int RowAddress(Ram ram, int row)
    {
        var (address, columns, _) = Screen(ram);
        return address + row * columns;
    }

    /// <summary>The screen's lines as text, without trailing spaces.</summary>
    public virtual IReadOnlyList<string> ScreenText(Ram ram)
    {
        var (_, columns, rows) = Screen(ram);
        return Enumerable.Range(0, rows).Select(r => new string(Enumerable.Range(0, columns)
            .Select(c => ScreenChar(ram.Bytes[(RowAddress(ram, r) + c) & 0xFFFF])).ToArray()).TrimEnd()).ToList();
    }
}
