using Tern.Compiler.Backend;
using Tern.Compiler.Semantics;

namespace Tern.Compiler;

/// <summary>
/// The kind of a program file (spec 12.2), chosen by the platform (review code-quality-syntax 6). A kind
/// knows its extension and how runs of bytes become its file, and makes the build's <see cref="OutputBuild"/>,
/// which adds what the file needs to the startup code and around the program. The builder asks the
/// output instead of testing which machine it builds for, so a new kind (a C64 cartridge) is a new class.
/// </summary>
public abstract class OutputFormat
{
    /// <summary>The file's extension: ".prg", ".xex", ".system", ".nes", ".bin".</summary>
    public abstract string Extension { get; }

    /// <summary>The kind of an overlay's file and of a binary without main: this one, except on the Apple II, where they are plain binary files, not system files.</summary>
    internal virtual OutputFormat Plain => this;

    /// <summary>
    /// The report's first line lists one range from the lowest to the highest address, since the file
    /// holds the gaps between the areas (a .prg); otherwise each run of bytes.
    /// </summary>
    internal virtual bool HoldsGaps => false;

    /// <summary>
    /// The file holding <paramref name="segments"/> (merged runs of bytes), for an overlay, a binary, or a
    /// program of a kind with no file of its own (<see cref="OutputBuild.File"/>).
    /// </summary>
    /// <param name="first">The lowest address of the segments.</param>
    /// <param name="image">The segments' bytes from <paramref name="first"/> on, gaps as zeros.</param>
    /// <param name="run">Where a program starts, for a file that names it (a .xex); null for an overlay or a binary.</param>
    internal abstract byte[] FileOf(int first, byte[] image, IReadOnlyList<(int Address, byte[] Bytes)> segments, int? run);

    /// <summary>The build of one program in this kind of file.</summary>
    internal virtual OutputBuild Begin(OutputBuild.Inputs inputs) => new(inputs);

    /// <summary>Commodore: a 2-byte load address, then every byte from the lowest to the highest address used, gaps as zeros.</summary>
    public static readonly OutputFormat Prg = new PrgFormat();
    /// <summary>Atari DOS: $FF $FF, then load segments (first address, last address, bytes), then the run address in RUNAD.</summary>
    public static readonly OutputFormat Xex = new XexFormat();
    /// <summary>
    /// A ProDOS 8 system file: one block of bytes, loaded at $2000 and started there. A program's areas
    /// follow each other with no gaps; the startup code moves them into place (spec 12.2).
    /// </summary>
    public static readonly OutputFormat ProDosSystem = new ProDosSystemFormat();
    /// <summary>
    /// An iNES 1.0 cartridge image (spec 12.2): a 16-byte header, PRG ROM (banks in order, the fixed bank
    /// last), then CHR ROM.
    /// </summary>
    public static readonly OutputFormat Ines = new InesFormat();
    /// <summary>The bytes from the lowest to the highest address, with no header: the Apple II's overlays and binaries (".bin").</summary>
    public static readonly OutputFormat Raw = new RawFormat();

    private sealed class PrgFormat : OutputFormat
    {
        public override string Extension => ".prg";
        internal override bool HoldsGaps => true;

        internal override byte[] FileOf(int first, byte[] image, IReadOnlyList<(int Address, byte[] Bytes)> segments, int? run) =>
            [(byte)first, (byte)(first >> 8), .. image];
    }

    private sealed class XexFormat : OutputFormat
    {
        /// <summary>The Atari's run address vector, RUNAD.</summary>
        private const int Runad = 0x02E0;

        public override string Extension => ".xex";

        /// <summary>One load segment per run of bytes and, with <paramref name="run"/>, a last segment that writes it to RUNAD.</summary>
        internal override byte[] FileOf(int first, byte[] image, IReadOnlyList<(int Address, byte[] Bytes)> segments, int? run)
        {
            var file = new List<byte> { 0xFF, 0xFF };
            void Segment(int address, byte[] bytes)
            {
                int last = address + bytes.Length - 1;
                file.AddRange([(byte)address, (byte)(address >> 8), (byte)last, (byte)(last >> 8)]);
                file.AddRange(bytes);
            }
            foreach (var (address, bytes) in segments.OrderBy(s => s.Address)) Segment(address, bytes);
            if (run is int at) Segment(Runad, [(byte)at, (byte)(at >> 8)]);
            return file.ToArray();
        }
    }

    private sealed class RawFormat : OutputFormat
    {
        public override string Extension => ".bin";

        internal override byte[] FileOf(int first, byte[] image, IReadOnlyList<(int Address, byte[] Bytes)> segments, int? run) => image;
    }
}

/// <summary>
/// One program's build in its kind of file (<see cref="OutputFormat.Begin"/>): what the startup code and
/// the program get beyond what every platform's get, and the file, the labels' places and the report's
/// lines. This base adds nothing, and the file is <see cref="OutputFormat.FileOf"/> of the image: a .prg
/// or a .xex. Its steps are called in the build's order: <see cref="Planned"/> once the variables are
/// placed, the startup items, <see cref="Settled"/> after each assembly, then <see cref="File"/>.
/// </summary>
internal class OutputBuild(OutputBuild.Inputs inputs)
{
    /// <summary>What every build step reads: the program, its platform, the functions' labels and the startup code's first label.</summary>
    internal sealed record Inputs(CheckedProgram Program, Platform Platform, OutputFormat Format, Func<FunctionSymbol, Label> LabelOf, Label Start);

    protected CheckedProgram Program => inputs.Program;
    protected Platform Platform => inputs.Platform;
    protected Label Start => inputs.Start;
    protected Func<FunctionSymbol, Label> LabelOf => inputs.LabelOf;

    /// <summary>Where the program starts: at the startup code, or at the file's load address where the system starts the file there.</summary>
    public virtual int Entry => Start.Address;

    /// <summary>Bytes before an aligned global in the image: zeros, or $FF in ROM, as erased ROM reads.</summary>
    public virtual byte Padding => 0;

    /// <summary>The RAM the startup code clears itself, before the variables are set up: its zeros are not stored again. Null: none.</summary>
    public virtual AddressRange? RamCleared => null;

    /// <summary>The program sets the IRQ vector, so <c>tern run</c> runs its handler on <c>brk</c> (spec 12.2).</summary>
    public virtual bool SetsIrqVector => false;

    /// <summary>The variables are placed (<paramref name="plan"/>): checks of the image that come before assembly.</summary>
    public virtual void Planned(RamPlan plan) { }

    /// <summary>The startup code's first instructions, before <c>cld</c>.</summary>
    public virtual IEnumerable<AsmItem> BeforeStartup() => [];

    /// <summary>The machine's own first steps, after <c>cld</c> and before the variables are set up.</summary>
    public virtual IEnumerable<AsmItem> Prelude() => [];

    /// <summary>What follows the halt loop: code the machine jumps to, and bytes the startup code names.</summary>
    public virtual IEnumerable<AsmItem> AfterHalt() => [];

    /// <summary>What follows the functions: the interrupt vectors of a cartridge.</summary>
    public virtual IEnumerable<AsmItem> AfterCode() => [];

    /// <summary>
    /// The program areas to lay the program out in again when it did not fit in <paramref name="areas"/>, or
    /// null for none: NROM's two areas joined into one, so that a function may cross from one to the other.
    /// </summary>
    public virtual IReadOnlyList<AddressRange>? Rejoined(IReadOnlyList<AddressRange> areas) => null;

    /// <summary>
    /// After an assembly: false when the program must be assembled again because what the startup code
    /// does depends on where the code went (the Apple II's copies).
    /// </summary>
    public virtual bool Settled(AssembledCode code) => true;

    /// <summary>The program file. <paramref name="image"/> holds every byte from <paramref name="first"/> to the highest address used, gaps as zeros.</summary>
    /// <param name="banks">The modules in banks, in the order the main file imports them (the order a bank is filled in, spec 8.5).</param>
    public virtual byte[] File(AssembledCode code, RamPlan plan, int first, byte[] image, IReadOnlyList<Module> banks) =>
        inputs.Format.FileOf(first, image, code.Segments, Program.IsBinary ? null : Start.Address);

    /// <summary>A label's place in the label file given its name and address, or null for the address itself (<see cref="LabelFormat.Write"/>).</summary>
    public virtual Func<string, int, int>? LabelPlaces(IReadOnlyList<Module> banks) => null;

    /// <summary>The report's lines about the file, after the line of the code and data.</summary>
    public virtual IEnumerable<string> ReportLines(RamPlan plan) => [];
}
