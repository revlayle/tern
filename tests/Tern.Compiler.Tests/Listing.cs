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
        var lines = AllLines(result);
        var range = Range(result, lines, function) ?? throw new Xunit.Sdk.XunitException($"No function {function} in the listing\n{result.Listing}");
        return lines[range];
    }

    public static List<string> Lines(Run run, string function = "main") => Lines(run.Result, function);

    /// <summary>
    /// The listing's lines outside every function and the startup code: data, the variables' areas and the
    /// layout's notes (padding, space left free), for a check of a note that no function's code can pass.
    /// </summary>
    public static string Outside(CompileResult result)
    {
        var lines = AllLines(result);
        var inside = new bool[lines.Count];
        foreach (var section in result.Instructions.Select(i => i.Section).Distinct())
            if (Range(result, lines, section) is { } range)
                for (int i = range.Start.Value; i < range.End.Value; i++) inside[i] = true;
        return string.Join("\n", lines.Where((_, i) => !inside[i]));
    }

    public static string Outside(Run run) => Outside(run.Result);

    private static List<string> AllLines(CompileResult result) => [.. result.Listing.Split('\n').Select(l => l.TrimEnd('\r'))];

    /// <summary>
    /// The startup code's section, for <see cref="Instructions"/>, <see cref="Lines"/> and <see cref="Text"/>:
    /// from the top of the listing (its label is <c>start:</c>) to its last instruction, the quit routine's
    /// where there is one.
    /// </summary>
    public const string Startup = "startup";

    /// <summary>The lines of <paramref name="section"/>, from its label to its last instruction; null when it has no label.</summary>
    private static System.Range? Range(CompileResult result, List<string> lines, string section)
    {
        int start = section == Startup ? 0 : lines.IndexOf($"{section}:");
        if (start < 0) return null;
        var last = result.Instructions.LastOrDefault(i => i.Section == section);
        if (last is null) return start..(start + 1);
        int end = lines.FindIndex(start, l => l.StartsWith($"${last.Address:X4} "));
        return start..(end + 1);
    }

    /// <summary>
    /// <see cref="Lines"/> as one text, for a check of what the listing shows of one function: its bytes
    /// (<c>8D 20 D0  sta border</c>) or its source comments.
    /// </summary>
    public static string Text(CompileResult result, string function = "main") => string.Join("\n", Lines(result, function));

    public static string Text(Run run, string function = "main") => Text(run.Result, function);

    /// <summary>The source comments just above <paramref name="function"/>'s label: the line that declares it.</summary>
    public static List<string> Header(CompileResult result, string function = "main")
    {
        var lines = AllLines(result);
        int label = lines.IndexOf($"{function}:");
        if (label < 0) throw new Xunit.Sdk.XunitException($"No function {function} in the listing\n{result.Listing}");
        int start = label;
        while (start > 0 && lines[start - 1].TrimStart().StartsWith(';')) start--;
        return [.. lines[start..label].Select(l => l.Trim())];
    }

    /// <summary>
    /// The names of the program's placed sections (functions, data, variables' areas), for a check that a
    /// function was or was not compiled, without reading the listing.
    /// </summary>
    public static List<string> Sections(CompileResult result) => [.. result.Sections.Select(s => s.Name)];

    public static List<string> Sections(Run run) => Sections(run.Result);

    /// <summary>
    /// The instructions of <paramref name="function"/> in listing order, with its own labels (<c>main.for1:</c>)
    /// and the source comments (<c>;    7: x = 1</c>) where they stand. Data bytes are left out.
    /// </summary>
    public static List<string> Annotated(CompileResult result, string function = "main")
    {
        var placed = result.Instructions.Where(i => i.Section == function).ToDictionary(i => $"${i.Address:X4} ", i => i.Text);
        var lines = new List<string>();
        foreach (var l in Lines(result, function).Skip(1))
        {
            if (l.Length > 6 && placed.TryGetValue(l[..6], out var text)) lines.Add(text);
            else if (l.TrimStart().StartsWith(';')) lines.Add(l.Trim());
            else if (l.StartsWith($"{function}.") && l.EndsWith(':')) lines.Add(l);
        }
        return lines;
    }

    public static List<string> Annotated(Run run, string function = "main") => Annotated(run.Result, function);

    /// <summary>The instructions of <paramref name="function"/> with its own labels (<c>main.for1:</c>) where they stand.</summary>
    public static string CodeWithLabels(CompileResult result, string function = "main", string separator = "|") =>
        string.Join(separator, Annotated(result, function).Where(l => !l.StartsWith(';')));

    public static string CodeWithLabels(Run run, string function = "main", string separator = "|") =>
        CodeWithLabels(run.Result, function, separator);

    /// <summary>How many of <paramref name="code"/>'s instructions are <paramref name="instruction"/>, e.g. "lda i".</summary>
    public static int Count(IEnumerable<string> code, string instruction) => code.Count(i => i == instruction);
}
