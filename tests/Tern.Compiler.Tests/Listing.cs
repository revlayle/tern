namespace Tern.Compiler.Tests;

/// <summary>
/// Reads one function's code for shape tests (review code-quality-tests, finding 7), so no test cuts
/// instructions out of the listing's text by hand. A function's instructions are those the assembler
/// placed in its section (<see cref="CompileResult.Instructions"/>), inlined code included; the text is
/// the listing's, such as <c>lda x</c> or <c>sta t,x</c>.
/// </summary>
public static class Listing
{
    /// <summary>The instructions of <paramref name="function"/>, in order. Fails if it has none.</summary>
    public static List<string> Instructions(CompileResult result, string function = "main")
    {
        var code = result.Instructions.Where(i => i.Section == function).Select(i => i.Text).ToList();
        if (code.Count == 0) throw new Xunit.Sdk.XunitException($"No function {function} in the listing\n{result.Listing}");
        return code;
    }

    public static List<string> Instructions(Run run, string function = "main") => Instructions(run.Result, function);

    /// <summary>The instructions of <paramref name="function"/> on one line, e.g. <c>lda x|sta y|rts</c>.</summary>
    public static string Code(CompileResult result, string function = "main", string separator = "|") =>
        string.Join(separator, Instructions(result, function));

    public static string Code(Run run, string function = "main", string separator = "|") => Code(run.Result, function, separator);

    /// <summary>
    /// The listing lines of <paramref name="function"/>, from its label to its last instruction: labels,
    /// source comments and instructions as listed.
    /// </summary>
    public static List<string> Lines(CompileResult result, string function = "main")
    {
        var lines = result.Listing.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        int start = lines.IndexOf($"{function}:");
        if (start < 0) throw new Xunit.Sdk.XunitException($"No function {function} in the listing\n{result.Listing}");
        var last = result.Instructions.LastOrDefault(i => i.Section == function);
        if (last is null) return [lines[start]];
        int end = lines.FindIndex(start, l => l.StartsWith($"${last.Address:X4} "));
        return lines[start..(end + 1)];
    }

    public static List<string> Lines(Run run, string function = "main") => Lines(run.Result, function);

    /// <summary>The instructions of <paramref name="function"/> with its own labels (<c>main.for1:</c>) where they stand.</summary>
    public static string CodeWithLabels(CompileResult result, string function = "main", string separator = "|")
    {
        var placed = result.Instructions.Where(i => i.Section == function).Select(i => $"${i.Address:X4} ").ToHashSet();
        var code = new List<string>();
        foreach (var l in Lines(result, function).Skip(1))
        {
            if (l.StartsWith($"{function}.") && l.EndsWith(':')) code.Add(l);
            else if (l.Length > 6 && placed.Contains(l[..6])) code.Add(l[17..].Split(';')[0].Trim());
        }
        return string.Join(separator, code);
    }

    public static string CodeWithLabels(Run run, string function = "main", string separator = "|") =>
        CodeWithLabels(run.Result, function, separator);

    /// <summary>How many of <paramref name="code"/>'s instructions are <paramref name="instruction"/>, e.g. "lda i".</summary>
    public static int Count(IEnumerable<string> code, string instruction) => code.Count(i => i == instruction);
}
