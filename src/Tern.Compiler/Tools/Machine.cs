using System.Text;
using Tern.Cpu;

namespace Tern.Compiler.Tools;

/// <summary>
/// What <c>tern run</c> knows of one platform beyond the CPU and plain RAM: memory as the machine
/// starts, stand-ins for a few ROM routines, and the text screen.
/// </summary>
internal abstract class Machine
{
    /// <summary>The platform's machine, for a run of <paramref name="program"/> (whose overlay files the C64's LOAD finds).</summary>
    public static Machine For(Platform platform, CompileResult? program = null) => platform.Format switch
    {
        OutputFormat.Xex => new AtariMachine(new OverlayDisk(program?.Overlays ?? [], platform.BinaryExtension)),
        OutputFormat.System => new Apple2Machine(new OverlayDisk(program?.Overlays ?? [], platform.BinaryExtension)),
        OutputFormat.Nes => new NesMachine(),
        // The KERNAL's file names have no extension: "LEVEL1" is level1.prg.
        _ => new C64Machine(new OverlayDisk(program?.Overlays ?? [], "")),
    };

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

    /// <summary>Sets up memory as after the machine starts, before the program is loaded.</summary>
    public abstract void Start(Ram ram);

    /// <summary>Whether <paramref name="pc"/> is a routine with a stand-in.</summary>
    public bool Has(int pc) => Routines.Any(s => s.Address == pc);

    /// <summary>
    /// Does the work of the routine at <paramref name="pc"/> (the caller then returns as rts would). Text
    /// printed goes to <paramref name="output"/>. Returns why the run stops, or null.
    /// </summary>
    public string? Call(int pc, Cpu6502 cpu, Ram ram, StringBuilder output) => Routines.Single(s => s.Address == pc).Run(cpu, ram, output);

    /// <summary>The text screen's address in <paramref name="ram"/>, columns and rows.</summary>
    public abstract (int Address, int Columns, int Rows) Screen(Ram ram);

    /// <summary>A byte of screen memory as text: letters, digits and punctuation as themselves, others as '#'.</summary>
    public abstract char ScreenChar(byte code);

    /// <summary>The address of the screen's row <paramref name="row"/>: rows follow each other, except on the Apple II.</summary>
    public virtual int RowAddress(Ram ram, int row)
    {
        var (address, columns, _) = Screen(ram);
        return address + row * columns;
    }

    /// <summary>The screen's lines as text, without trailing spaces.</summary>
    public IReadOnlyList<string> ScreenText(Ram ram)
    {
        var (_, columns, rows) = Screen(ram);
        return Enumerable.Range(0, rows).Select(r => new string(Enumerable.Range(0, columns)
            .Select(c => ScreenChar(ram.Bytes[(RowAddress(ram, r) + c) & 0xFFFF])).ToArray()).TrimEnd()).ToList();
    }
}
