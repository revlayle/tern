using Tern.Cpu;

namespace Tern.Compiler.Semantics;

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
}
