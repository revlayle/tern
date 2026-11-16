namespace Tern.Cpu;

public enum Mnemonic
{
    Adc, And, Asl, Bcc, Bcs, Beq, Bit, Bmi, Bne, Bpl, Brk, Bvc, Bvs, Clc,
    Cld, Cli, Clv, Cmp, Cpx, Cpy, Dec, Dex, Dey, Eor, Inc, Inx, Iny, Jmp,
    Jsr, Lda, Ldx, Ldy, Lsr, Nop, Ora, Pha, Php, Pla, Plp, Rol, Ror, Rti,
    Rts, Sbc, Sec, Sed, Sei, Sta, Stx, Sty, Tax, Tay, Tsx, Txa, Txs, Tya,
}

public enum AddressingMode
{
    Implied,          // clc
    Accumulator,      // asl a
    Immediate,        // lda #$10
    ZeroPage,         // lda $10
    ZeroPageX,        // lda $10,x
    ZeroPageY,        // ldx $10,y
    Absolute,         // lda $1234
    AbsoluteX,        // lda $1234,x
    AbsoluteY,        // lda $1234,y
    Indirect,         // jmp ($1234)
    IndexedIndirect,  // lda ($10,x)
    IndirectIndexed,  // lda ($10),y
    Relative,         // bne label
}

public enum CpuVariant
{
    /// <summary>Original NMOS 6502 / 6510 (C64, Apple II, Atari 8-bit).</summary>
    Nmos6502,
    /// <summary>NES CPU: NMOS opcodes, but decimal mode has no effect.</summary>
    Ricoh2A03,
}

/// <param name="Cycles">Base cycle count.</param>
/// <param name="PageCrossPenalty">
/// True if crossing a page while indexing adds one cycle (reads only).
/// Branches are handled separately: +1 if taken, +1 more if the target is on another page.
/// </param>
public readonly record struct OpcodeInfo(
    byte Opcode, Mnemonic Mnemonic, AddressingMode Mode, int Cycles, bool PageCrossPenalty)
{
    public int Size => InstructionSet.OperandSize(Mode) + 1;
}

/// <summary>
/// The instructions of one CPU variant (spec 12.1): opcodes, addressing modes and cycles, and how the
/// variant behaves where variants differ. What each instruction does is in <see cref="Instructions"/>.
/// The build passes the target's set to the checker, the backend and the emulator; the 65C02's set
/// is the NMOS list with its changes.
/// </summary>
public sealed class InstructionSet
{
    public static InstructionSet Nmos6502 { get; } = new(CpuVariant.Nmos6502, Nmos(), decimalMode: true, indirectJumpWrapsInPage: true);
    /// <summary>The NES's CPU: the NMOS instructions, with no decimal mode.</summary>
    public static InstructionSet Ricoh2A03 { get; } = new(CpuVariant.Ricoh2A03, Nmos(), decimalMode: false, indirectJumpWrapsInPage: true);

    public static InstructionSet For(CpuVariant variant) => variant switch
    {
        CpuVariant.Nmos6502 => Nmos6502,
        CpuVariant.Ricoh2A03 => Ricoh2A03,
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "No instruction set for this CPU variant"),
    };

    private readonly OpcodeInfo?[] _byOpcode = new OpcodeInfo?[256];
    private readonly Dictionary<(Mnemonic, AddressingMode), OpcodeInfo> _byMnemonicMode = [];
    private readonly HashSet<Mnemonic> _mnemonics = [];

    public CpuVariant Variant { get; }
    public IReadOnlyList<OpcodeInfo> All { get; }
    /// <summary>With the D flag set, <c>adc</c> and <c>sbc</c> work in decimal (not on the 2A03).</summary>
    public bool DecimalMode { get; }
    /// <summary><c>jmp ($xxFF)</c> reads the high byte from $xx00, not the next page (the NMOS bug).</summary>
    public bool IndirectJumpWrapsInPage { get; }

    private InstructionSet(CpuVariant variant, List<OpcodeInfo> list, bool decimalMode, bool indirectJumpWrapsInPage)
    {
        Variant = variant;
        DecimalMode = decimalMode;
        IndirectJumpWrapsInPage = indirectJumpWrapsInPage;
        foreach (var info in list)
        {
            if (_byOpcode[info.Opcode] is not null)
                throw new InvalidOperationException($"Duplicate opcode ${info.Opcode:X2}");
            _byOpcode[info.Opcode] = info;
            _byMnemonicMode.Add((info.Mnemonic, info.Mode), info);
            _mnemonics.Add(info.Mnemonic);
        }
        All = list;
    }

    public OpcodeInfo? Decode(byte opcode) => _byOpcode[opcode];

    public bool TryGet(Mnemonic mnemonic, AddressingMode mode, out OpcodeInfo info) =>
        _byMnemonicMode.TryGetValue((mnemonic, mode), out info);

    /// <summary>True if the variant has <paramref name="mnemonic"/> in some mode.</summary>
    public bool Has(Mnemonic mnemonic) => _mnemonics.Contains(mnemonic);

    /// <summary>The 151 documented opcodes of the NMOS 6502.</summary>
    private static List<OpcodeInfo> Nmos()
    {
        var list = new List<OpcodeInfo>();
        void Op(int opcode, Mnemonic m, AddressingMode mode, int cycles, bool pageCross = false) =>
            list.Add(new OpcodeInfo((byte)opcode, m, mode, cycles, pageCross));

        // The eight "group one" instructions share one pattern of modes and timings.
        void Group1(Mnemonic m, int imm, int zp, int zpx, int abs, int absx, int absy, int indx, int indy)
        {
            Op(imm, m, AddressingMode.Immediate, 2);
            Op(zp, m, AddressingMode.ZeroPage, 3);
            Op(zpx, m, AddressingMode.ZeroPageX, 4);
            Op(abs, m, AddressingMode.Absolute, 4);
            Op(absx, m, AddressingMode.AbsoluteX, 4, true);
            Op(absy, m, AddressingMode.AbsoluteY, 4, true);
            Op(indx, m, AddressingMode.IndexedIndirect, 6);
            Op(indy, m, AddressingMode.IndirectIndexed, 5, true);
        }

        // Read-modify-write shifts and rotates.
        void Shift(Mnemonic m, int acc, int zp, int zpx, int abs, int absx)
        {
            Op(acc, m, AddressingMode.Accumulator, 2);
            Op(zp, m, AddressingMode.ZeroPage, 5);
            Op(zpx, m, AddressingMode.ZeroPageX, 6);
            Op(abs, m, AddressingMode.Absolute, 6);
            Op(absx, m, AddressingMode.AbsoluteX, 7);
        }

        Group1(Mnemonic.Adc, 0x69, 0x65, 0x75, 0x6D, 0x7D, 0x79, 0x61, 0x71);
        Group1(Mnemonic.And, 0x29, 0x25, 0x35, 0x2D, 0x3D, 0x39, 0x21, 0x31);
        Group1(Mnemonic.Cmp, 0xC9, 0xC5, 0xD5, 0xCD, 0xDD, 0xD9, 0xC1, 0xD1);
        Group1(Mnemonic.Eor, 0x49, 0x45, 0x55, 0x4D, 0x5D, 0x59, 0x41, 0x51);
        Group1(Mnemonic.Lda, 0xA9, 0xA5, 0xB5, 0xAD, 0xBD, 0xB9, 0xA1, 0xB1);
        Group1(Mnemonic.Ora, 0x09, 0x05, 0x15, 0x0D, 0x1D, 0x19, 0x01, 0x11);
        Group1(Mnemonic.Sbc, 0xE9, 0xE5, 0xF5, 0xED, 0xFD, 0xF9, 0xE1, 0xF1);

        // STA has no immediate mode and never gets the page-cross shortcut.
        Op(0x85, Mnemonic.Sta, AddressingMode.ZeroPage, 3);
        Op(0x95, Mnemonic.Sta, AddressingMode.ZeroPageX, 4);
        Op(0x8D, Mnemonic.Sta, AddressingMode.Absolute, 4);
        Op(0x9D, Mnemonic.Sta, AddressingMode.AbsoluteX, 5);
        Op(0x99, Mnemonic.Sta, AddressingMode.AbsoluteY, 5);
        Op(0x81, Mnemonic.Sta, AddressingMode.IndexedIndirect, 6);
        Op(0x91, Mnemonic.Sta, AddressingMode.IndirectIndexed, 6);

        Shift(Mnemonic.Asl, 0x0A, 0x06, 0x16, 0x0E, 0x1E);
        Shift(Mnemonic.Lsr, 0x4A, 0x46, 0x56, 0x4E, 0x5E);
        Shift(Mnemonic.Rol, 0x2A, 0x26, 0x36, 0x2E, 0x3E);
        Shift(Mnemonic.Ror, 0x6A, 0x66, 0x76, 0x6E, 0x7E);

        Op(0xC6, Mnemonic.Dec, AddressingMode.ZeroPage, 5);
        Op(0xD6, Mnemonic.Dec, AddressingMode.ZeroPageX, 6);
        Op(0xCE, Mnemonic.Dec, AddressingMode.Absolute, 6);
        Op(0xDE, Mnemonic.Dec, AddressingMode.AbsoluteX, 7);
        Op(0xE6, Mnemonic.Inc, AddressingMode.ZeroPage, 5);
        Op(0xF6, Mnemonic.Inc, AddressingMode.ZeroPageX, 6);
        Op(0xEE, Mnemonic.Inc, AddressingMode.Absolute, 6);
        Op(0xFE, Mnemonic.Inc, AddressingMode.AbsoluteX, 7);

        Op(0xA2, Mnemonic.Ldx, AddressingMode.Immediate, 2);
        Op(0xA6, Mnemonic.Ldx, AddressingMode.ZeroPage, 3);
        Op(0xB6, Mnemonic.Ldx, AddressingMode.ZeroPageY, 4);
        Op(0xAE, Mnemonic.Ldx, AddressingMode.Absolute, 4);
        Op(0xBE, Mnemonic.Ldx, AddressingMode.AbsoluteY, 4, true);
        Op(0xA0, Mnemonic.Ldy, AddressingMode.Immediate, 2);
        Op(0xA4, Mnemonic.Ldy, AddressingMode.ZeroPage, 3);
        Op(0xB4, Mnemonic.Ldy, AddressingMode.ZeroPageX, 4);
        Op(0xAC, Mnemonic.Ldy, AddressingMode.Absolute, 4);
        Op(0xBC, Mnemonic.Ldy, AddressingMode.AbsoluteX, 4, true);

        Op(0x86, Mnemonic.Stx, AddressingMode.ZeroPage, 3);
        Op(0x96, Mnemonic.Stx, AddressingMode.ZeroPageY, 4);
        Op(0x8E, Mnemonic.Stx, AddressingMode.Absolute, 4);
        Op(0x84, Mnemonic.Sty, AddressingMode.ZeroPage, 3);
        Op(0x94, Mnemonic.Sty, AddressingMode.ZeroPageX, 4);
        Op(0x8C, Mnemonic.Sty, AddressingMode.Absolute, 4);

        Op(0xE0, Mnemonic.Cpx, AddressingMode.Immediate, 2);
        Op(0xE4, Mnemonic.Cpx, AddressingMode.ZeroPage, 3);
        Op(0xEC, Mnemonic.Cpx, AddressingMode.Absolute, 4);
        Op(0xC0, Mnemonic.Cpy, AddressingMode.Immediate, 2);
        Op(0xC4, Mnemonic.Cpy, AddressingMode.ZeroPage, 3);
        Op(0xCC, Mnemonic.Cpy, AddressingMode.Absolute, 4);

        Op(0x24, Mnemonic.Bit, AddressingMode.ZeroPage, 3);
        Op(0x2C, Mnemonic.Bit, AddressingMode.Absolute, 4);

        Op(0x4C, Mnemonic.Jmp, AddressingMode.Absolute, 3);
        Op(0x6C, Mnemonic.Jmp, AddressingMode.Indirect, 5);
        Op(0x20, Mnemonic.Jsr, AddressingMode.Absolute, 6);
        Op(0x60, Mnemonic.Rts, AddressingMode.Implied, 6);
        Op(0x40, Mnemonic.Rti, AddressingMode.Implied, 6);
        Op(0x00, Mnemonic.Brk, AddressingMode.Implied, 7);

        Op(0x10, Mnemonic.Bpl, AddressingMode.Relative, 2);
        Op(0x30, Mnemonic.Bmi, AddressingMode.Relative, 2);
        Op(0x50, Mnemonic.Bvc, AddressingMode.Relative, 2);
        Op(0x70, Mnemonic.Bvs, AddressingMode.Relative, 2);
        Op(0x90, Mnemonic.Bcc, AddressingMode.Relative, 2);
        Op(0xB0, Mnemonic.Bcs, AddressingMode.Relative, 2);
        Op(0xD0, Mnemonic.Bne, AddressingMode.Relative, 2);
        Op(0xF0, Mnemonic.Beq, AddressingMode.Relative, 2);

        Op(0x48, Mnemonic.Pha, AddressingMode.Implied, 3);
        Op(0x08, Mnemonic.Php, AddressingMode.Implied, 3);
        Op(0x68, Mnemonic.Pla, AddressingMode.Implied, 4);
        Op(0x28, Mnemonic.Plp, AddressingMode.Implied, 4);

        Op(0x18, Mnemonic.Clc, AddressingMode.Implied, 2);
        Op(0xD8, Mnemonic.Cld, AddressingMode.Implied, 2);
        Op(0x58, Mnemonic.Cli, AddressingMode.Implied, 2);
        Op(0xB8, Mnemonic.Clv, AddressingMode.Implied, 2);
        Op(0x38, Mnemonic.Sec, AddressingMode.Implied, 2);
        Op(0xF8, Mnemonic.Sed, AddressingMode.Implied, 2);
        Op(0x78, Mnemonic.Sei, AddressingMode.Implied, 2);

        Op(0xCA, Mnemonic.Dex, AddressingMode.Implied, 2);
        Op(0x88, Mnemonic.Dey, AddressingMode.Implied, 2);
        Op(0xE8, Mnemonic.Inx, AddressingMode.Implied, 2);
        Op(0xC8, Mnemonic.Iny, AddressingMode.Implied, 2);
        Op(0xAA, Mnemonic.Tax, AddressingMode.Implied, 2);
        Op(0xA8, Mnemonic.Tay, AddressingMode.Implied, 2);
        Op(0xBA, Mnemonic.Tsx, AddressingMode.Implied, 2);
        Op(0x8A, Mnemonic.Txa, AddressingMode.Implied, 2);
        Op(0x9A, Mnemonic.Txs, AddressingMode.Implied, 2);
        Op(0x98, Mnemonic.Tya, AddressingMode.Implied, 2);
        Op(0xEA, Mnemonic.Nop, AddressingMode.Implied, 2);
        return list;
    }

    public static int OperandSize(AddressingMode mode) => mode switch
    {
        AddressingMode.Implied or AddressingMode.Accumulator => 0,
        AddressingMode.Absolute or AddressingMode.AbsoluteX or AddressingMode.AbsoluteY
            or AddressingMode.Indirect => 2,
        _ => 1,
    };
}
