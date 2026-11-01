namespace Tern.Cpu;

/// <summary>Memory and I/O as seen by the CPU.</summary>
public interface IBus
{
    byte Read(ushort address);
    void Write(ushort address, byte value);
}

/// <summary>Flat 64 KB of RAM.</summary>
public sealed class Ram : IBus
{
    public byte[] Bytes { get; } = new byte[0x10000];

    public byte Read(ushort address) => Bytes[address];
    public void Write(ushort address, byte value) => Bytes[address] = value;

    public void Load(ushort address, ReadOnlySpan<byte> data) => data.CopyTo(Bytes.AsSpan(address));
}

public sealed class IllegalOpcodeException(byte opcode, ushort address)
    : Exception($"Illegal opcode ${opcode:X2} at ${address:X4}")
{
    public byte Opcode { get; } = opcode;
    public ushort Address { get; } = address;
}

/// <summary>
/// Instruction-level 6502 emulator with exact cycle counts for documented opcodes.
/// </summary>
public sealed class Cpu6502(IBus bus, CpuVariant variant = CpuVariant.Nmos6502)
{
    public byte A, X, Y, SP = 0xFD;
    public ushort PC;
    public bool C, Z, I, D, V, N;

    /// <summary>Total cycles executed.</summary>
    public long Cycles;

    public CpuVariant Variant { get; } = variant;

    /// <summary>Status register. Bit 5 always reads as 1; the B flag only exists on the stack.</summary>
    public byte P
    {
        get => (byte)((N ? 0x80 : 0) | (V ? 0x40 : 0) | 0x20 | (D ? 0x08 : 0) | (I ? 0x04 : 0) | (Z ? 0x02 : 0) | (C ? 0x01 : 0));
        set
        {
            N = (value & 0x80) != 0; V = (value & 0x40) != 0; D = (value & 0x08) != 0;
            I = (value & 0x04) != 0; Z = (value & 0x02) != 0; C = (value & 0x01) != 0;
        }
    }

    public void Reset()
    {
        SP = 0xFD;
        I = true;
        PC = ReadWord(0xFFFC);
        Cycles += 7;
    }

    /// <summary>Executes one instruction and returns the cycles it took.</summary>
    public int Step()
    {
        ushort start = PC;
        byte opcode = bus.Read(PC++);
        var info = InstructionSet.Decode(opcode) ?? throw new IllegalOpcodeException(opcode, start);
        int cycles = info.Cycles;
        ushort addr = 0;

        switch (info.Mode)
        {
            case AddressingMode.Immediate:
                addr = PC++;
                break;
            case AddressingMode.ZeroPage:
                addr = bus.Read(PC++);
                break;
            case AddressingMode.ZeroPageX:
                addr = (byte)(bus.Read(PC++) + X);
                break;
            case AddressingMode.ZeroPageY:
                addr = (byte)(bus.Read(PC++) + Y);
                break;
            case AddressingMode.Absolute:
                addr = ReadWord(PC);
                PC += 2;
                break;
            case AddressingMode.AbsoluteX:
                addr = Indexed(ReadWord(PC), X, info, ref cycles);
                PC += 2;
                break;
            case AddressingMode.AbsoluteY:
                addr = Indexed(ReadWord(PC), Y, info, ref cycles);
                PC += 2;
                break;
            case AddressingMode.Indirect:
            {
                ushort ptr = ReadWord(PC);
                PC += 2;
                // NMOS bug: the high byte is read from the same page, e.g. jmp ($10FF) reads $10FF and $1000.
                ushort hiAddr = (ushort)((ptr & 0xFF00) | ((ptr + 1) & 0x00FF));
                addr = (ushort)(bus.Read(ptr) | (bus.Read(hiAddr) << 8));
                break;
            }
            case AddressingMode.IndexedIndirect:
            {
                byte zp = (byte)(bus.Read(PC++) + X);
                addr = (ushort)(bus.Read(zp) | (bus.Read((byte)(zp + 1)) << 8));
                break;
            }
            case AddressingMode.IndirectIndexed:
            {
                byte zp = bus.Read(PC++);
                ushort baseAddr = (ushort)(bus.Read(zp) | (bus.Read((byte)(zp + 1)) << 8));
                addr = Indexed(baseAddr, Y, info, ref cycles);
                break;
            }
            case AddressingMode.Relative:
            {
                sbyte offset = (sbyte)bus.Read(PC++);
                addr = (ushort)(PC + offset);
                break;
            }
        }

        bool accumulator = info.Mode == AddressingMode.Accumulator;

        switch (info.Mnemonic)
        {
            case Mnemonic.Adc: AddWithCarry(bus.Read(addr)); break;
            case Mnemonic.Sbc: SubtractWithBorrow(bus.Read(addr)); break;
            case Mnemonic.And: A &= bus.Read(addr); SetNZ(A); break;
            case Mnemonic.Ora: A |= bus.Read(addr); SetNZ(A); break;
            case Mnemonic.Eor: A ^= bus.Read(addr); SetNZ(A); break;
            case Mnemonic.Cmp: Compare(A, bus.Read(addr)); break;
            case Mnemonic.Cpx: Compare(X, bus.Read(addr)); break;
            case Mnemonic.Cpy: Compare(Y, bus.Read(addr)); break;
            case Mnemonic.Bit:
            {
                byte m = bus.Read(addr);
                Z = (A & m) == 0; N = (m & 0x80) != 0; V = (m & 0x40) != 0;
                break;
            }

            case Mnemonic.Asl: Modify(addr, accumulator, v => { C = (v & 0x80) != 0; return (byte)(v << 1); }); break;
            case Mnemonic.Lsr: Modify(addr, accumulator, v => { C = (v & 0x01) != 0; return (byte)(v >> 1); }); break;
            case Mnemonic.Rol: Modify(addr, accumulator, v => { int r = (v << 1) | (C ? 1 : 0); C = (v & 0x80) != 0; return (byte)r; }); break;
            case Mnemonic.Ror: Modify(addr, accumulator, v => { int r = (v >> 1) | (C ? 0x80 : 0); C = (v & 0x01) != 0; return (byte)r; }); break;
            case Mnemonic.Inc: Modify(addr, false, v => (byte)(v + 1)); break;
            case Mnemonic.Dec: Modify(addr, false, v => (byte)(v - 1)); break;

            case Mnemonic.Lda: A = bus.Read(addr); SetNZ(A); break;
            case Mnemonic.Ldx: X = bus.Read(addr); SetNZ(X); break;
            case Mnemonic.Ldy: Y = bus.Read(addr); SetNZ(Y); break;
            case Mnemonic.Sta: bus.Write(addr, A); break;
            case Mnemonic.Stx: bus.Write(addr, X); break;
            case Mnemonic.Sty: bus.Write(addr, Y); break;

            case Mnemonic.Inx: X++; SetNZ(X); break;
            case Mnemonic.Iny: Y++; SetNZ(Y); break;
            case Mnemonic.Dex: X--; SetNZ(X); break;
            case Mnemonic.Dey: Y--; SetNZ(Y); break;
            case Mnemonic.Tax: X = A; SetNZ(X); break;
            case Mnemonic.Tay: Y = A; SetNZ(Y); break;
            case Mnemonic.Txa: A = X; SetNZ(A); break;
            case Mnemonic.Tya: A = Y; SetNZ(A); break;
            case Mnemonic.Tsx: X = SP; SetNZ(X); break;
            case Mnemonic.Txs: SP = X; break;

            case Mnemonic.Pha: Push(A); break;
            case Mnemonic.Php: Push((byte)(P | 0x10)); break;
            case Mnemonic.Pla: A = Pull(); SetNZ(A); break;
            case Mnemonic.Plp: P = Pull(); break;

            case Mnemonic.Clc: C = false; break;
            case Mnemonic.Cld: D = false; break;
            case Mnemonic.Cli: I = false; break;
            case Mnemonic.Clv: V = false; break;
            case Mnemonic.Sec: C = true; break;
            case Mnemonic.Sed: D = true; break;
            case Mnemonic.Sei: I = true; break;

            case Mnemonic.Bcc: Branch(!C, addr, ref cycles); break;
            case Mnemonic.Bcs: Branch(C, addr, ref cycles); break;
            case Mnemonic.Bne: Branch(!Z, addr, ref cycles); break;
            case Mnemonic.Beq: Branch(Z, addr, ref cycles); break;
            case Mnemonic.Bpl: Branch(!N, addr, ref cycles); break;
            case Mnemonic.Bmi: Branch(N, addr, ref cycles); break;
            case Mnemonic.Bvc: Branch(!V, addr, ref cycles); break;
            case Mnemonic.Bvs: Branch(V, addr, ref cycles); break;

            case Mnemonic.Jmp: PC = addr; break;
            case Mnemonic.Jsr:
                PushWord((ushort)(PC - 1));
                PC = addr;
                break;
            case Mnemonic.Rts:
                PC = (ushort)(PullWord() + 1);
                break;
            case Mnemonic.Rti:
                P = Pull();
                PC = PullWord();
                break;
            case Mnemonic.Brk:
                PushWord((ushort)(PC + 1)); // BRK skips one padding byte
                Push((byte)(P | 0x10));
                I = true;
                PC = ReadWord(0xFFFE);
                break;

            case Mnemonic.Nop: break;
        }

        Cycles += cycles;
        return cycles;
    }

    private static ushort Indexed(ushort baseAddr, byte index, OpcodeInfo info, ref int cycles)
    {
        ushort addr = (ushort)(baseAddr + index);
        if (info.PageCrossPenalty && ((baseAddr ^ addr) & 0xFF00) != 0)
            cycles++;
        return addr;
    }

    private void Branch(bool taken, ushort target, ref int cycles)
    {
        if (!taken) return;
        cycles++;
        if ((PC & 0xFF00) != (target & 0xFF00))
            cycles++;
        PC = target;
    }

    /// <summary>Read-modify-write. Like the real NMOS chip, the old value is written back before the new one.</summary>
    private void Modify(ushort addr, bool accumulator, Func<byte, byte> op)
    {
        if (accumulator)
        {
            A = op(A);
            SetNZ(A);
            return;
        }
        byte old = bus.Read(addr);
        bus.Write(addr, old);
        byte result = op(old);
        bus.Write(addr, result);
        SetNZ(result);
    }

    private void AddWithCarry(byte m)
    {
        int carry = C ? 1 : 0;
        int sum = A + m + carry;

        if (!D || Variant == CpuVariant.Ricoh2A03)
        {
            V = (~(A ^ m) & (A ^ sum) & 0x80) != 0;
            C = sum > 0xFF;
            A = (byte)sum;
            SetNZ(A);
            return;
        }

        // NMOS decimal mode. N and V come from the intermediate result, Z from the binary sum.
        // Follows "Decimal Mode" by Bruce Clark, appendix A.
        int lo = (A & 0x0F) + (m & 0x0F) + carry;
        if (lo >= 0x0A) lo = ((lo + 0x06) & 0x0F) + 0x10;
        int result = (A & 0xF0) + (m & 0xF0) + lo;
        int signed = (sbyte)(A & 0xF0) + (sbyte)(m & 0xF0) + lo;
        N = (result & 0x80) != 0;
        V = signed < -128 || signed > 127;
        Z = (byte)sum == 0;
        if (result >= 0xA0) result += 0x60;
        C = result >= 0x100;
        A = (byte)result;
    }

    private void SubtractWithBorrow(byte m)
    {
        int borrow = C ? 0 : 1;
        int diff = A - m - borrow;
        byte binary = (byte)diff;

        // Flags are always those of the binary subtraction on NMOS chips.
        V = ((A ^ m) & (A ^ binary) & 0x80) != 0;
        C = diff >= 0;
        SetNZ(binary);

        if (!D || Variant == CpuVariant.Ricoh2A03)
        {
            A = binary;
            return;
        }

        int lo = (A & 0x0F) - (m & 0x0F) - borrow;
        if (lo < 0) lo = ((lo - 0x06) & 0x0F) - 0x10;
        int result = (A & 0xF0) - (m & 0xF0) + lo;
        if (result < 0) result -= 0x60;
        A = (byte)result;
    }

    private void Compare(byte register, byte m)
    {
        C = register >= m;
        SetNZ((byte)(register - m));
    }

    private void SetNZ(byte value)
    {
        Z = value == 0;
        N = (value & 0x80) != 0;
    }

    private ushort ReadWord(ushort address) =>
        (ushort)(bus.Read(address) | (bus.Read((ushort)(address + 1)) << 8));

    private void Push(byte value) => bus.Write((ushort)(0x0100 | SP--), value);
    private byte Pull() => bus.Read((ushort)(0x0100 | ++SP));

    private void PushWord(ushort value)
    {
        Push((byte)(value >> 8));
        Push((byte)value);
    }

    private ushort PullWord()
    {
        byte lo = Pull();
        return (ushort)(lo | (Pull() << 8));
    }
}
