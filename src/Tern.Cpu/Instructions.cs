namespace Tern.Cpu;

/// <summary>Registers and flags of the CPU.</summary>
[Flags]
public enum CpuState
{
    None = 0,
    A = 1, X = 2, Y = 4,
    /// <summary>Carry</summary>
    C = 8,
    /// <summary>Zero</summary>
    Z = 16,
    /// <summary>Negative</summary>
    N = 32,
    /// <summary>Overflow</summary>
    V = 64,
    /// <summary>Decimal mode</summary>
    D = 128,
    /// <summary>Interrupt disable</summary>
    I = 256,
    Flags = C | Z | N | V | D | I,
    Registers = A | X | Y,
}

/// <summary>What an instruction does to the memory its operand names: for an indirect mode, the memory the pointer points to.</summary>
public enum MemoryAccess { None, Read, Write, ReadWrite }

/// <summary>Where control goes after an instruction.</summary>
public enum Flow
{
    /// <summary>To the next instruction.</summary>
    Next,
    /// <summary>To the operand's target if the condition holds, else to the next instruction.</summary>
    Branch,
    /// <summary>To the operand's target only (<c>jmp</c>).</summary>
    Jump,
    /// <summary>To a subroutine, which comes back to the next instruction (<c>jsr</c>).</summary>
    Call,
    /// <summary>To the address taken from the stack, plus one (<c>rts</c>).</summary>
    Return,
    /// <summary>To the address and flags taken from the stack (<c>rti</c>).</summary>
    ReturnFromInterrupt,
    /// <summary>Through the IRQ vector (<c>brk</c>).</summary>
    Break,
}

/// <summary>
/// What one instruction does, in any addressing mode and on every CPU variant that has it: the
/// registers and flags it reads and writes, what it does to memory, and where control goes. A
/// branch is taken when <see cref="Condition"/> is set (<see cref="WhenSet"/>) or clear.
/// </summary>
public sealed record Instruction(
    Mnemonic Mnemonic, CpuState Reads, CpuState Writes, MemoryAccess Memory, Flow Flow = Flow.Next,
    CpuState Condition = CpuState.None, bool WhenSet = false);

/// <summary>
/// The one description of what each instruction does (review code-quality, theme 1), used by the
/// checker's assembly analysis, the code generator, the optimizer and the assembler. Which
/// instructions and modes a CPU has is <see cref="InstructionSet"/>'s; a new instruction is one row
/// here and its opcodes there.
/// </summary>
public static class Instructions
{
    private const CpuState NZ = CpuState.N | CpuState.Z;
    private const CpuState A = CpuState.A, X = CpuState.X, Y = CpuState.Y, C = CpuState.C, V = CpuState.V;
    private const MemoryAccess Read = MemoryAccess.Read, Write = MemoryAccess.Write, Modify = MemoryAccess.ReadWrite, No = MemoryAccess.None;

    private static readonly Instruction[] Table = Build(
    [
        new(Mnemonic.Adc, A | C | CpuState.D, A | C | V | NZ, Read),
        new(Mnemonic.Sbc, A | C | CpuState.D, A | C | V | NZ, Read),
        new(Mnemonic.And, A, A | NZ, Read),
        new(Mnemonic.Ora, A, A | NZ, Read),
        new(Mnemonic.Eor, A, A | NZ, Read),
        new(Mnemonic.Cmp, A, C | NZ, Read),
        new(Mnemonic.Cpx, X, C | NZ, Read),
        new(Mnemonic.Cpy, Y, C | NZ, Read),
        new(Mnemonic.Bit, A, V | NZ, Read),
        new(Mnemonic.Asl, CpuState.None, C | NZ, Modify),
        new(Mnemonic.Lsr, CpuState.None, C | NZ, Modify),
        new(Mnemonic.Rol, C, C | NZ, Modify),
        new(Mnemonic.Ror, C, C | NZ, Modify),
        new(Mnemonic.Inc, CpuState.None, NZ, Modify),
        new(Mnemonic.Dec, CpuState.None, NZ, Modify),
        new(Mnemonic.Lda, CpuState.None, A | NZ, Read),
        new(Mnemonic.Ldx, CpuState.None, X | NZ, Read),
        new(Mnemonic.Ldy, CpuState.None, Y | NZ, Read),
        new(Mnemonic.Sta, A, CpuState.None, Write),
        new(Mnemonic.Stx, X, CpuState.None, Write),
        new(Mnemonic.Sty, Y, CpuState.None, Write),
        new(Mnemonic.Inx, X, X | NZ, No),
        new(Mnemonic.Dex, X, X | NZ, No),
        new(Mnemonic.Iny, Y, Y | NZ, No),
        new(Mnemonic.Dey, Y, Y | NZ, No),
        new(Mnemonic.Tax, A, X | NZ, No),
        new(Mnemonic.Tay, A, Y | NZ, No),
        new(Mnemonic.Txa, X, A | NZ, No),
        new(Mnemonic.Tya, Y, A | NZ, No),
        new(Mnemonic.Tsx, CpuState.None, X | NZ, No),
        new(Mnemonic.Txs, X, CpuState.None, No),
        new(Mnemonic.Pha, A, CpuState.None, No),
        new(Mnemonic.Php, CpuState.Flags, CpuState.None, No),
        new(Mnemonic.Pla, CpuState.None, A | NZ, No),
        new(Mnemonic.Plp, CpuState.None, CpuState.Flags, No),
        new(Mnemonic.Clc, CpuState.None, C, No),
        new(Mnemonic.Sec, CpuState.None, C, No),
        new(Mnemonic.Cld, CpuState.None, CpuState.D, No),
        new(Mnemonic.Sed, CpuState.None, CpuState.D, No),
        new(Mnemonic.Cli, CpuState.None, CpuState.I, No),
        new(Mnemonic.Sei, CpuState.None, CpuState.I, No),
        new(Mnemonic.Clv, CpuState.None, V, No),
        new(Mnemonic.Nop, CpuState.None, CpuState.None, No),
        new(Mnemonic.Bcc, C, CpuState.None, No, Flow.Branch, C, false),
        new(Mnemonic.Bcs, C, CpuState.None, No, Flow.Branch, C, true),
        new(Mnemonic.Bne, CpuState.Z, CpuState.None, No, Flow.Branch, CpuState.Z, false),
        new(Mnemonic.Beq, CpuState.Z, CpuState.None, No, Flow.Branch, CpuState.Z, true),
        new(Mnemonic.Bpl, CpuState.N, CpuState.None, No, Flow.Branch, CpuState.N, false),
        new(Mnemonic.Bmi, CpuState.N, CpuState.None, No, Flow.Branch, CpuState.N, true),
        new(Mnemonic.Bvc, V, CpuState.None, No, Flow.Branch, V, false),
        new(Mnemonic.Bvs, V, CpuState.None, No, Flow.Branch, V, true),
        new(Mnemonic.Jmp, CpuState.None, CpuState.None, No, Flow.Jump),
        // A call may change every register and flag but D and I (the callee's own business).
        new(Mnemonic.Jsr, CpuState.None, A | X | Y | C | V | NZ, No, Flow.Call),
        new(Mnemonic.Rts, CpuState.None, CpuState.None, No, Flow.Return),
        new(Mnemonic.Rti, CpuState.None, CpuState.Flags, No, Flow.ReturnFromInterrupt),
        new(Mnemonic.Brk, CpuState.Flags, CpuState.I, No, Flow.Break),
    ]);

    private static Instruction[] Build(Instruction[] rows)
    {
        var table = new Instruction[Enum.GetValues<Mnemonic>().Length];
        foreach (var row in rows)
        {
            if (table[(int)row.Mnemonic] is not null) throw new InvalidOperationException($"Two rows for {row.Mnemonic}");
            table[(int)row.Mnemonic] = row;
        }
        foreach (var m in Enum.GetValues<Mnemonic>())
            if (table[(int)m] is null) throw new InvalidOperationException($"No row for {m}");
        return table;
    }

    public static Instruction Of(Mnemonic m) => Table[(int)m];

    /// <summary>The registers and flags <paramref name="m"/> reads and writes in <paramref name="mode"/>: the accumulator form works on A, an indexed mode reads its index.</summary>
    public static (CpuState Reads, CpuState Writes) Effects(Mnemonic m, AddressingMode mode)
    {
        var row = Of(m);
        var (reads, writes) = (row.Reads, row.Writes);
        if (mode == AddressingMode.Accumulator) { reads |= A; writes |= A; }
        return (reads | IndexOf(mode), writes);
    }

    /// <summary>The index register <paramref name="mode"/> reads, or none.</summary>
    public static CpuState IndexOf(AddressingMode mode) => mode switch
    {
        AddressingMode.AbsoluteX or AddressingMode.ZeroPageX or AddressingMode.IndexedIndirect => X,
        AddressingMode.AbsoluteY or AddressingMode.ZeroPageY or AddressingMode.IndirectIndexed => Y,
        _ => CpuState.None,
    };

    /// <summary>A mode whose operand is a pointer in memory: the instruction reads the pointer and works on what it points to.</summary>
    public static bool IsIndirect(AddressingMode mode) =>
        mode is AddressingMode.Indirect or AddressingMode.IndexedIndirect or AddressingMode.IndirectIndexed;

    /// <summary>A mode whose operand names memory (directly, or a pointer).</summary>
    public static bool NamesMemory(AddressingMode mode) =>
        mode is not (AddressingMode.Implied or AddressingMode.Accumulator or AddressingMode.Immediate or AddressingMode.Relative);

    /// <summary>True if <paramref name="m"/> reads the memory its operand names: for an indirect mode, the pointer.</summary>
    public static bool ReadsMemory(Mnemonic m, AddressingMode mode) =>
        IsIndirect(mode) || (NamesMemory(mode) && Of(m).Memory is MemoryAccess.Read or MemoryAccess.ReadWrite);

    /// <summary>True if <paramref name="m"/> writes the memory its operand names (not the pointer of an indirect mode, which it never writes).</summary>
    public static bool WritesMemory(Mnemonic m, AddressingMode mode) =>
        NamesMemory(mode) && !IsIndirect(mode) && Of(m).Memory is MemoryAccess.Write or MemoryAccess.ReadWrite;

    /// <summary>True if <paramref name="m"/> writes memory through the pointer of an indirect mode.</summary>
    public static bool WritesThroughPointer(Mnemonic m, AddressingMode mode) =>
        IsIndirect(mode) && Of(m).Memory is MemoryAccess.Write or MemoryAccess.ReadWrite;

    /// <summary>The one register of <paramref name="s"/> (A, X or Y), or null if it names none or several.</summary>
    private static CpuState? One(CpuState s) => (s & CpuState.Registers) is var r && r is A or X or Y ? r : null;

    /// <summary>The register a load from memory sets (<c>lda</c>, <c>ldx</c>, <c>ldy</c>): reads memory and no register.</summary>
    public static CpuState? Loaded(Mnemonic m) =>
        Of(m) is { Memory: MemoryAccess.Read, Flow: Flow.Next } row && (row.Reads & CpuState.Registers) == 0 ? One(row.Writes) : null;

    /// <summary>The register a store writes to memory (<c>sta</c>, <c>stx</c>, <c>sty</c>).</summary>
    public static CpuState? Stored(Mnemonic m) =>
        Of(m) is { Memory: MemoryAccess.Write } row && (row.Writes & CpuState.Registers) == 0 ? One(row.Reads) : null;

    /// <summary>The registers a transfer copies from and to (<c>tax</c>, <c>txa</c>, ...).</summary>
    public static (CpuState From, CpuState To)? Transfer(Mnemonic m) =>
        Of(m) is { Memory: MemoryAccess.None, Flow: Flow.Next } row && One(row.Reads) is { } from && One(row.Writes) is { } to && from != to
            ? (from, to) : null;

    public static bool IsBranch(Mnemonic m) => Of(m).Flow == Flow.Branch;

    /// <summary>The branch taken on the opposite condition (<c>bcc</c> for <c>bcs</c>).</summary>
    public static Mnemonic Inverse(Mnemonic branch)
    {
        var row = Of(branch);
        if (row.Flow != Flow.Branch) throw new ArgumentException($"{branch} is not a branch", nameof(branch));
        return Table.Single(r => r.Flow == Flow.Branch && r.Condition == row.Condition && r.WhenSet != row.WhenSet).Mnemonic;
    }

    /// <summary>
    /// True if control may go on to the next instruction after <paramref name="m"/>: also after
    /// <c>brk</c>, whose handler may return past its padding byte.
    /// </summary>
    public static bool FallsThrough(Mnemonic m) => Of(m).Flow is not (Flow.Jump or Flow.Return or Flow.ReturnFromInterrupt);

    public static string Describe(CpuState s) =>
        s == CpuState.None ? "nothing"
        : string.Join(" ", Enum.GetValues<CpuState>().Where(f => f is not (CpuState.None or CpuState.Flags or CpuState.Registers) && s.HasFlag(f)));
}
