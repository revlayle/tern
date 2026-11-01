namespace Tern.Cpu;

/// <summary>Turns a single instruction into machine code bytes.</summary>
public static class Encoder
{
    /// <summary>
    /// Encodes one instruction and appends its bytes to <paramref name="output"/>.
    /// For <see cref="AddressingMode.Relative"/>, <paramref name="operand"/> is the branch target
    /// address and <paramref name="pc"/> is the address of the branch instruction itself.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    public static int Encode(Mnemonic mnemonic, AddressingMode mode, int operand, int pc, List<byte> output)
    {
        if (!InstructionSet.TryGet(mnemonic, mode, out var info))
            throw new EncodingException($"{mnemonic} has no {mode} addressing mode");

        output.Add(info.Opcode);
        switch (InstructionSet.OperandSize(mode))
        {
            case 0:
                break;
            case 1:
                int value = mode == AddressingMode.Relative ? BranchOffset(operand, pc) : operand;
                if (mode != AddressingMode.Relative && (value < 0 || value > 0xFF))
                    throw new EncodingException($"{mnemonic} {mode}: operand ${operand:X} does not fit in one byte");
                output.Add((byte)value);
                break;
            case 2:
                if (operand < 0 || operand > 0xFFFF)
                    throw new EncodingException($"{mnemonic} {mode}: operand ${operand:X} does not fit in two bytes");
                output.Add((byte)operand);
                output.Add((byte)(operand >> 8));
                break;
        }
        return info.Size;
    }

    /// <summary>True if a branch at <paramref name="pc"/> can reach <paramref name="target"/>.</summary>
    public static bool BranchInRange(int target, int pc)
    {
        int offset = target - (pc + 2);
        return offset >= -128 && offset <= 127;
    }

    private static int BranchOffset(int target, int pc)
    {
        if (!BranchInRange(target, pc))
            throw new EncodingException($"Branch from ${pc:X4} to ${target:X4} is out of range");
        return target - (pc + 2);
    }
}

public sealed class EncodingException(string message) : Exception(message);
