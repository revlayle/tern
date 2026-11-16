using Tern.Cpu;

namespace Tern.Compiler.Backend;

/// <summary>Where an instruction reads or writes memory.</summary>
public enum MemoryTarget
{
    /// <summary>Nowhere: the mode names no memory (<c>asl a</c>, <c>lda #1</c>), or the instruction does not touch what it names (<c>jmp</c>, <c>jsr</c>).</summary>
    None,
    /// <summary>One byte of a variable (<c>lda v</c>, <c>sta v+1</c>).</summary>
    Byte,
    /// <summary>Any byte of a variable, through an index (<c>lda a,x</c>).</summary>
    Element,
    /// <summary>Anywhere, through a pointer (<c>lda (p),y</c>); the pointer is read too.</summary>
    Pointer,
    /// <summary>A fixed address given as a number (<c>lda $D020</c>): any variable may be there.</summary>
    Number,
    /// <summary>A label in code (<c>sta .patch+1</c>): the program's own instructions.</summary>
    Code,
}

/// <summary>
/// What an instruction does to memory (review backend 8): whether it reads or writes, and where. The
/// one answer the optimizer, the code generator and the driver use, from the instruction table
/// (<see cref="Instructions"/>), so a new instruction or mode needs no change here.
/// </summary>
/// <param name="Reads">It reads the target (for <see cref="MemoryTarget.Pointer"/>, what the pointer points to).</param>
/// <param name="Writes">It writes the target.</param>
/// <param name="Operand">
/// The variable operand: the byte or array of <see cref="MemoryTarget.Byte"/> and <see cref="MemoryTarget.Element"/>,
/// the pointer of <see cref="MemoryTarget.Pointer"/> (read whole with <c>(zp,x)</c>, else its two bytes).
/// </param>
public readonly record struct MemoryUse(bool Reads, bool Writes, MemoryTarget Target, VarOperand? Operand)
{
    public static MemoryUse Of(Instr i)
    {
        var access = Instructions.Of(i.Mnemonic).Memory;
        bool reads = access is MemoryAccess.Read or MemoryAccess.ReadWrite, writes = access is MemoryAccess.Write or MemoryAccess.ReadWrite;
        var operand = i.Operand as VarOperand;
        if (Instructions.IsIndirect(i.Mode)) return new(reads, writes, MemoryTarget.Pointer, operand);
        if (!Instructions.NamesMemory(i.Mode) || access == MemoryAccess.None) return new(false, false, MemoryTarget.None, null);
        var target = i.Operand switch
        {
            VarOperand => Instructions.IndexOf(i.Mode) == CpuState.None ? MemoryTarget.Byte : MemoryTarget.Element,
            LabelOperand => MemoryTarget.Code,
            _ => MemoryTarget.Number,
        };
        return new(reads, writes, target, operand);
    }
}
