using System.Text;
using Tern.Compiler.Semantics;

namespace Tern.Compiler.Backend;

/// <summary>
/// The interface module of a binary without <c>main</c> (spec 12.3): each entry of the jump table as a
/// routine at a fixed address with the registers the code leaves alone, and constants for the memory
/// the binary uses, so that a Tern host can reserve it.
/// </summary>
public static class BinaryInterface
{
    /// <param name="binaryFile">The file name of the binary, for the comment at the top.</param>
    /// <param name="entries">The jump table: <c>setup</c> first, then the exports, each with its address.</param>
    /// <param name="fileRange">The addresses the program file covers (code, data, and the gaps between).</param>
    /// <param name="ranges">Other memory the binary uses, by constant prefix: variables, regions, zero page.</param>
    public static string Generate(
        string binaryFile, string moduleName, IReadOnlyList<(string Name, FunctionSymbol? Function, int Address, CpuState Changed)> entries,
        AddressRange fileRange, IReadOnlyList<(string Prefix, string What, AddressRange Range)> ranges)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// Interface to {binaryFile}, written by the Tern compiler. Import it as '{moduleName}'.");
        sb.AppendLine("// Call setup() once, before any other entry, and call one entry at a time: never an entry from an");
        sb.AppendLine("// interrupt while another entry is running.");
        sb.AppendLine("// In the host's memory block, list each range below as 'reserved': the host places nothing there.");
        sb.AppendLine("// For the ZP range this also takes those bytes out of the host's own zero page.");
        sb.AppendLine();
        sb.AppendLine($"const CODE_FIRST = ${fileRange.First:X4}      // the program file: code, data and the gaps between");
        sb.AppendLine($"const CODE_LAST = ${fileRange.Last:X4}");
        foreach (var (prefix, what, range) in ranges)
        {
            string format = range.Last < 0x100 ? "X2" : "X4";
            sb.AppendLine($"const {prefix}_FIRST = ${range.First.ToString(format)}      // {what}");
            sb.AppendLine($"const {prefix}_LAST = ${range.Last.ToString(format)}");
        }
        sb.AppendLine();
        foreach (var (name, function, address, changed) in entries)
        {
            var kept = new List<string>();
            foreach (var (flag, register) in new[] { (CpuState.A, Register.A), (CpuState.X, Register.X), (CpuState.Y, Register.Y) })
                if (!changed.HasFlag(flag) && !(function?.ReturnRegister is { } r && r.Uses(register))) kept.Add(register.ToString());
            sb.AppendLine($"fn {name}({Parameters(function)}){Return(function)} @ ${address:X4}" + (kept.Count > 0 ? $" keeps {string.Join(", ", kept)}" : ""));
        }
        return sb.ToString();
    }

    private static string Parameters(FunctionSymbol? f) =>
        f is null ? "" : string.Join(", ", f.Parameters.Select(p => $"{p.Name}: {TypeText(p.Type)} using {p.PassedIn}"));

    private static string Return(FunctionSymbol? f)
    {
        if (f is null || f.ReturnType.Kind == TypeKind.Void) return "";
        string register = f.ReturnRegister!.Value switch
        {
            Register.Carry => "carry", Register.Zero => "zero", Register.Negative => "negative", Register.Overflow => "overflow",
            var other => other.ToString(),
        };
        return $" -> {TypeText(f.ReturnType)} using {register}";
    }

    /// <summary>A pointer to a struct is a plain 16-bit value to the host, which does not know the struct.</summary>
    private static string TypeText(TernType t) => t.IsPointer && t.Element!.IsStruct ? "u16" : t.ToString();
}
