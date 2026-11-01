using Tern.Cpu;
using Xunit.Abstractions;

namespace Tern.Cpu.Tests;

/// <summary>
/// Runs Klaus Dormann's 6502 functional test (https://github.com/Klaus2m5/6502_65C02_functional_tests),
/// which checks every documented opcode, including decimal mode.
/// The test ends in a "jmp *" loop: at $3469 on success, anywhere else on failure.
/// </summary>
public class FunctionalTest(ITestOutputHelper output)
{
    private const ushort SuccessAddress = 0x3469;

    [Fact]
    public void Klaus_Dormann_functional_test_passes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "6502_functional_test.bin");
        var ram = new Ram();
        ram.Load(0x0000, File.ReadAllBytes(path));
        var cpu = new Cpu6502(ram) { PC = 0x0400 };

        while (true)
        {
            ushort before = cpu.PC;
            cpu.Step();
            if (cpu.PC == before)
                break; // trapped in a "jmp *" or "bne *" loop
            if (cpu.Cycles > 200_000_000)
                Assert.Fail($"No trap reached; PC=${cpu.PC:X4}");
        }

        output.WriteLine($"Trapped at ${cpu.PC:X4} after {cpu.Cycles:N0} cycles");
        Assert.True(cpu.PC == SuccessAddress,
            $"Failed at ${cpu.PC:X4} (see 6502_functional_test.lst for that address). A={cpu.A:X2} X={cpu.X:X2} Y={cpu.Y:X2} P={cpu.P:X2}");
    }
}
