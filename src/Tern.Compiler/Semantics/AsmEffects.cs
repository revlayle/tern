using Tern.Cpu;
using static Tern.Cpu.Mnemonic;

namespace Tern.Compiler.Semantics;

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
}

/// <summary>
/// What an assembly block reads and changes: registers, flags and Tern variables. Each set is the
/// union over all instructions, whatever path is taken through the block.
/// </summary>
public sealed class AsmEffects
{
    public CpuState Reads { get; set; }
    public CpuState Writes { get; set; }
    public HashSet<VariableSymbol> VariablesRead { get; } = [];
    public HashSet<VariableSymbol> VariablesWritten { get; } = [];
    /// <summary>The block calls a subroutine with <c>jsr</c>, which may change every register.</summary>
    public bool Calls { get; set; }

    /// <summary>Registers and flags that <paramref name="m"/> reads and changes in <paramref name="mode"/>.</summary>
    public static (CpuState Reads, CpuState Writes) Of(Mnemonic m, AddressingMode mode)
    {
        const CpuState NZ = CpuState.N | CpuState.Z;
        bool acc = mode == AddressingMode.Accumulator;
        var (reads, writes) = m switch
        {
            Adc or Sbc => (CpuState.A | CpuState.C | CpuState.D, CpuState.A | CpuState.C | CpuState.V | NZ),
            And or Ora or Eor => (CpuState.A, CpuState.A | NZ),
            Asl or Lsr => (acc ? CpuState.A : CpuState.None, (acc ? CpuState.A : CpuState.None) | CpuState.C | NZ),
            Rol or Ror => ((acc ? CpuState.A : CpuState.None) | CpuState.C, (acc ? CpuState.A : CpuState.None) | CpuState.C | NZ),
            Bit => (CpuState.A, CpuState.V | NZ),
            Bcc or Bcs => (CpuState.C, CpuState.None),
            Beq or Bne => (CpuState.Z, CpuState.None),
            Bmi or Bpl => (CpuState.N, CpuState.None),
            Bvc or Bvs => (CpuState.V, CpuState.None),
            Brk => (CpuState.Flags, CpuState.I),
            Clc or Sec => (CpuState.None, CpuState.C),
            Cld or Sed => (CpuState.None, CpuState.D),
            Cli or Sei => (CpuState.None, CpuState.I),
            Clv => (CpuState.None, CpuState.V),
            Cmp => (CpuState.A, CpuState.C | NZ),
            Cpx => (CpuState.X, CpuState.C | NZ),
            Cpy => (CpuState.Y, CpuState.C | NZ),
            Dec or Inc => (CpuState.None, NZ),
            Dex or Inx => (CpuState.X, CpuState.X | NZ),
            Dey or Iny => (CpuState.Y, CpuState.Y | NZ),
            Jsr => (CpuState.None, CpuState.A | CpuState.X | CpuState.Y | CpuState.C | CpuState.V | NZ),
            Lda or Pla => (CpuState.None, CpuState.A | NZ),
            Ldx => (CpuState.None, CpuState.X | NZ),
            Ldy => (CpuState.None, CpuState.Y | NZ),
            Pha => (CpuState.A, CpuState.None),
            Php => (CpuState.Flags, CpuState.None),
            Plp or Rti => (CpuState.None, CpuState.Flags),
            Sta => (CpuState.A, CpuState.None),
            Stx => (CpuState.X, CpuState.None),
            Sty => (CpuState.Y, CpuState.None),
            Tax => (CpuState.A, CpuState.X | NZ),
            Tay => (CpuState.A, CpuState.Y | NZ),
            Tsx => (CpuState.None, CpuState.X | NZ),
            Txa => (CpuState.X, CpuState.A | NZ),
            Txs => (CpuState.X, CpuState.None),
            Tya => (CpuState.Y, CpuState.A | NZ),
            _ => (CpuState.None, CpuState.None), // jmp, nop, rts
        };
        reads |= mode switch
        {
            AddressingMode.AbsoluteX or AddressingMode.ZeroPageX or AddressingMode.IndexedIndirect => CpuState.X,
            AddressingMode.AbsoluteY or AddressingMode.ZeroPageY or AddressingMode.IndirectIndexed => CpuState.Y,
            _ => CpuState.None,
        };
        return (reads, writes);
    }

    /// <summary>True if <paramref name="m"/> reads the memory its operand names (for indirect modes: the pointer).</summary>
    public static bool ReadsMemory(Mnemonic m, AddressingMode mode) => mode switch
    {
        AddressingMode.IndexedIndirect or AddressingMode.IndirectIndexed or AddressingMode.Indirect => true,
        _ => m is not (Sta or Stx or Sty or Jmp or Jsr),
    };

    /// <summary>True if <paramref name="m"/> writes the memory its operand names (not for indirect modes).</summary>
    public static bool WritesMemory(Mnemonic m, AddressingMode mode) =>
        mode is not (AddressingMode.IndexedIndirect or AddressingMode.IndirectIndexed or AddressingMode.Indirect)
        && m is Sta or Stx or Sty or Inc or Dec or Asl or Lsr or Rol or Ror;

    public static string Describe(CpuState s) =>
        s == CpuState.None ? "nothing" : string.Join(" ", Enum.GetValues<CpuState>().Where(f => f != CpuState.None && f != CpuState.Flags && s.HasFlag(f)));
}
