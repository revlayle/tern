using Tern.Cpu;
using static Tern.Cpu.AddressingMode;
using static Tern.Cpu.Mnemonic;

namespace Tern.Cpu.Tests;

public class CpuTests
{
    /// <summary>Assembles instructions at <paramref name="origin"/> and returns a CPU ready to run them.</summary>
    private static (Cpu6502 cpu, Ram ram) Setup(ushort origin, params (Mnemonic m, AddressingMode mode, int operand)[] program)
    {
        var ram = new Ram();
        var bytes = new List<byte>();
        foreach (var (m, mode, operand) in program)
            Encoder.Encode(m, mode, operand, origin + bytes.Count, bytes);
        ram.Load(origin, bytes.ToArray());
        return (new Cpu6502(ram) { PC = origin }, ram);
    }

    [Theory]
    [InlineData(0x10F0, 0x0F, 4)] // stays in page $10
    [InlineData(0x10F0, 0x10, 5)] // crosses into page $11
    public void Absolute_indexed_read_adds_cycle_on_page_cross(int baseAddr, int x, int expected)
    {
        var (cpu, _) = Setup(0x0200, (Lda, AbsoluteX, baseAddr));
        cpu.X = (byte)x;
        Assert.Equal(expected, cpu.Step());
    }

    [Fact]
    public void Indexed_store_always_takes_five_cycles()
    {
        var (cpu, ram) = Setup(0x0200, (Sta, AbsoluteX, 0x10F0));
        cpu.X = 0x20;
        cpu.A = 0x99;
        Assert.Equal(5, cpu.Step());
        Assert.Equal(0x99, ram.Bytes[0x1110]);
    }

    [Fact]
    public void Indirect_indexed_read_adds_cycle_on_page_cross()
    {
        var (cpu, ram) = Setup(0x0200, (Lda, IndirectIndexed, 0xFB));
        ram.Bytes[0xFB] = 0xFF;
        ram.Bytes[0xFC] = 0x20;
        ram.Bytes[0x2100] = 0x5A;
        cpu.Y = 1;
        Assert.Equal(6, cpu.Step());
        Assert.Equal(0x5A, cpu.A);
    }

    [Fact]
    public void Branch_timing_not_taken_taken_and_page_cross()
    {
        var (cpu, _) = Setup(0x0200, (Bne, Relative, 0x0210));
        cpu.Z = true;
        Assert.Equal(2, cpu.Step());

        (cpu, _) = Setup(0x0200, (Bne, Relative, 0x0210));
        Assert.Equal(3, cpu.Step());
        Assert.Equal(0x0210, cpu.PC);

        (cpu, _) = Setup(0x02F0, (Bne, Relative, 0x0310));
        Assert.Equal(4, cpu.Step());
        Assert.Equal(0x0310, cpu.PC);
    }

    [Fact]
    public void Jmp_indirect_wraps_within_page()
    {
        var (cpu, ram) = Setup(0x0200, (Jmp, Indirect, 0x10FF));
        ram.Bytes[0x10FF] = 0x34;
        ram.Bytes[0x1000] = 0x12; // read by the NMOS bug
        ram.Bytes[0x1100] = 0x99; // not read
        cpu.Step();
        Assert.Equal(0x1234, cpu.PC);
    }

    [Fact]
    public void Zero_page_indexed_wraps_within_zero_page()
    {
        var (cpu, ram) = Setup(0x0200, (Lda, ZeroPageX, 0xF0));
        ram.Bytes[0x0010] = 0x77;
        cpu.X = 0x20;
        cpu.Step();
        Assert.Equal(0x77, cpu.A);
    }

    [Fact]
    public void Read_modify_write_writes_old_value_then_new()
    {
        var bus = new RecordingBus();
        bus.Ram.Load(0x0200, [0xEE, 0x19, 0xD0]); // inc $D019
        bus.Ram.Bytes[0xD019] = 0x81;
        new Cpu6502(bus) { PC = 0x0200 }.Step();
        Assert.Equal(new[] { (0xD019, 0x81), (0xD019, 0x82) }, bus.Writes);
    }

    [Fact]
    public void Jsr_and_rts_return_to_next_instruction()
    {
        var (cpu, ram) = Setup(0x0200, (Jsr, Absolute, 0x0300));
        ram.Bytes[0x0300] = 0x60; // rts
        Assert.Equal(6, cpu.Step());
        Assert.Equal(0x0300, cpu.PC);
        Assert.Equal(0xFB, cpu.SP);
        Assert.Equal(6, cpu.Step());
        Assert.Equal(0x0203, cpu.PC);
        Assert.Equal(0xFD, cpu.SP);
    }

    [Theory]
    [InlineData(0x58, 0x46, true, 0x05, true)]   // 58 + 46 + 1 = 105
    [InlineData(0x12, 0x34, false, 0x46, false)]
    [InlineData(0x81, 0x92, false, 0x73, true)]  // 81 + 92 = 173
    public void Decimal_adc(int a, int m, bool carryIn, int expected, bool carryOut)
    {
        var (cpu, _) = Setup(0x0200, (Adc, Immediate, m));
        cpu.A = (byte)a; cpu.C = carryIn; cpu.D = true;
        cpu.Step();
        Assert.Equal(expected, cpu.A);
        Assert.Equal(carryOut, cpu.C);
    }

    [Theory]
    [InlineData(0x46, 0x12, true, 0x34, true)]
    [InlineData(0x40, 0x13, true, 0x27, true)]
    [InlineData(0x32, 0x02, false, 0x29, true)]
    [InlineData(0x12, 0x21, true, 0x91, false)]
    public void Decimal_sbc(int a, int m, bool carryIn, int expected, bool carryOut)
    {
        var (cpu, _) = Setup(0x0200, (Sbc, Immediate, m));
        cpu.A = (byte)a; cpu.C = carryIn; cpu.D = true;
        cpu.Step();
        Assert.Equal(expected, cpu.A);
        Assert.Equal(carryOut, cpu.C);
    }

    [Fact]
    public void Ricoh2A03_ignores_decimal_mode()
    {
        var ram = new Ram();
        ram.Load(0x0200, [0x69, 0x46]); // adc #$46
        var cpu = new Cpu6502(ram, CpuVariant.Ricoh2A03) { PC = 0x0200, A = 0x58, D = true };
        cpu.Step();
        Assert.Equal(0x9E, cpu.A);
    }

    private sealed class RecordingBus : IBus
    {
        public Ram Ram { get; } = new();
        public List<(int, int)> Writes { get; } = [];
        public byte Read(ushort address) => Ram.Read(address);
        public void Write(ushort address, byte value)
        {
            Writes.Add((address, value));
            Ram.Write(address, value);
        }
    }
}
