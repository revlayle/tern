using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tools;

/// <summary>One way of writing the code: a source file, or a snippet put into <c>main</c> after shared declarations.</summary>
/// <param name="DeclarationLines">For a snippet, the number of lines of each <c>--decl</c>, in order; null for a file.</param>
/// <param name="SnippetLines">For a snippet, its number of lines.</param>
public sealed record BenchVariant(string Name, string Source, string? Path, IReadOnlyList<int>? DeclarationLines = null, int SnippetLines = 0)
{
    /// <summary><c>--decl</c> lines, then <c>fn main() {</c>, the snippet and <c>}</c>.</summary>
    public static BenchVariant Snippet(string name, IReadOnlyList<string> declarations, string code)
    {
        var source = new StringBuilder();
        foreach (var d in declarations) source.Append(d).Append('\n');
        source.Append("fn main() {\n").Append(code).Append("\n}\n");
        return new BenchVariant(name, source.ToString(), null, declarations.Select(d => d.Split('\n').Length).ToList(), code.Split('\n').Length);
    }

    /// <summary>
    /// Where a line of a snippet's generated source came from: <c>decl N</c> and the line in that
    /// declaration, or the variant's name and the line in the snippet.
    /// </summary>
    public (string File, int Line) Place(int line)
    {
        if (DeclarationLines is null) return (Name, line);
        int first = 1;
        for (int i = 0; i < DeclarationLines.Count; i++)
        {
            if (line < first + DeclarationLines[i]) return ($"decl {i + 1}", line - first + 1);
            first += DeclarationLines[i];
        }
        // The line after the declarations is fn main() {, then the snippet, then the closing brace.
        return (Name, Math.Clamp(line - first, 1, Math.Max(1, SnippetLines)));
    }
}

/// <summary>
/// The measurement of one variant. <see cref="Function"/> is what was measured (main, or the
/// function given with <c>--fn</c>): its code bytes, and the cycles of all its calls with what they
/// call (<see cref="Cycles"/>) and per call. <see cref="Memory"/> lists its variables and the
/// program's own globals with the addresses the compiler chose. <see cref="IndexErrors"/> are the
/// index check's errors (a snippet's lines are its own); a variant with any fails.
/// </summary>
public sealed record BenchResult(
    string Variant, Diagnostic? Error, string Function, int ProgramBytes, int FunctionBytes, long Calls, long Cycles,
    bool Returned, string Stop, IReadOnlyList<VariableInfo> Memory, IReadOnlyList<(string What, string Value)> Results,
    IReadOnlyList<IndexError> IndexErrors)
{
    /// <summary>Every error of a variant that did not compile (spec 13), <see cref="Error"/> first.</summary>
    public IReadOnlyList<Diagnostic> Errors { get => _errors ?? (Error is null ? [] : [Error]); init => _errors = value; }
    private readonly IReadOnlyList<Diagnostic>? _errors;
    /// <summary><c>--max-errors</c> (<see cref="MaxErrors"/>) stopped the compile: more errors were not reported.</summary>
    public bool MoreErrors { get; init; }
    public int MaxErrors { get; init; }

    public long CyclesPerCall => Calls == 0 ? 0 : Cycles / Calls;
    public int ZeroPageBytes => Memory.Where(v => v.Address < 0x100).Sum(v => v.Size);
    public int OtherBytes => Memory.Where(v => v.Address >= 0x100).Sum(v => v.Size);
}

/// <summary><c>tern bench</c>: compiles and runs each variant and compares bytes, cycles and memory.</summary>
public static class Bench
{
    /// <param name="maxErrors">Stop compiling after this many errors (<c>--max-errors</c>); null for no limit.</param>
    /// <param name="platform">The target (<c>--target</c>); the C64 if null.</param>
    public static BenchResult Measure(BenchVariant variant, string? function, IReadOnlyList<string> dumps, RunOptions? options = null, int? maxErrors = null, Platform? platform = null)
    {
        string measured = function ?? "main";
        CompileResult program;
        try
        {
            // No warnings: compared snippets often leave values unused on purpose (spec 13).
            program = TernCompiler.Compile(variant.Source, platform, sourcePath: variant.Path, maxErrors: maxErrors, warnings: WarningMode.Off);
        }
        catch (CompileError e)
        {
            // Every error found (spec 13); a snippet's lines are its own.
            var all = e.All.Select(x =>
            {
                var d = Diagnostic.From(x, variant.Name);
                if (variant.Path is null && x.Pos.File is null)
                {
                    var (file, line) = variant.Place(x.Pos.Line);
                    d = d with { File = file, Line = line };
                }
                return d;
            }).ToList();
            return Failed(all[0]) with { Errors = all, MoreErrors = e.MoreNotReported, MaxErrors = maxErrors ?? 0 };
        }
        var section = program.Sections.FirstOrDefault(s => s.Name == measured);
        if (section is null && program.DeclaredFunctions.Contains(measured))
            return Failed(new Diagnostic(variant.Name, 0, 0, "error",
                $"The function '{measured}' has no code of its own (an inline function is copied into each caller, and a function nothing calls is left out).",
                "Measure a function that calls it"));
        if (section is null)
        {
            // A module's function is named with its module: ship.move.
            var inModule = program.DeclaredFunctions.Where(f => f.EndsWith("." + measured)).Order(StringComparer.Ordinal).ToList();
            return Failed(new Diagnostic(variant.Name, 0, 0, "error",
                $"There is no function '{measured}' in the program.",
                inModule.Count == 0 ? null : "Use " + string.Join(" or ", inModule.Select(f => $"'{f}'")) + ": a module's function is named with its module"));
        }

        var run = Runner.Run(program, (options ?? new RunOptions()) with { MainFile = variant.Name });
        var indexErrors = run.IndexErrors.Select(e =>
        {
            if (variant.DeclarationLines is null || e.File != variant.Name || e.Line is not int line) return e;
            var (file, place) = variant.Place(line);
            return new IndexError(file, place, e.Source, e.Message, e.Cycle) { Count = e.Count };
        }).ToList();
        var time = run.Functions.FirstOrDefault(f => f.Name == measured);
        var memory = program.Variables.Values
            .Where(v => v.Function == measured || (v.Function is null && !v.IsData && !v.Name.Contains('.')))
            .OrderBy(v => v.Function is not null).ThenBy(v => v.Address).ToList();
        var results = new List<(string, string)>();
        foreach (var text in dumps)
        {
            string value;
            try
            {
                var d = MemoryDump.Parse(text, program);
                value = d.Value(run.Ram.Bytes) is { } v ? v.ToString() : string.Join(" ", run.Ram.Bytes.AsSpan(d.Address, d.Size).ToArray().Select(b => b.ToString("X2")));
            }
            catch (FormatException e) { value = "(" + e.Message + ")"; }
            results.Add((text, value));
        }
        return new BenchResult(variant.Name, null, measured, program.Image.Length, section.Size, time?.Calls ?? 0, time?.Cycles ?? 0,
            run.Returned, run.Stop, memory, results, indexErrors);

        BenchResult Failed(Diagnostic d) => new(variant.Name, d, measured, 0, 0, 0, 0, false, d.Message, [], [], []);
    }

    public static string Text(IReadOnlyList<BenchResult> results)
    {
        var sb = new StringBuilder();
        string fn = results.Count == 0 ? "main" : results[0].Function;
        int width = Math.Max(8, results.Max(r => r.Variant.Length));
        sb.AppendLine($"{"variant".PadRight(width)}  {"program",7}  {fn + " bytes",10}  {"calls",6}  {"cycles",9}  {"per call",8}  {"zp",4}  {"other",5}");
        foreach (var r in results)
        {
            if (r.Error is not null)
            {
                // One line per error, then their number when there are several.
                for (int i = 0; i < r.Errors.Count; i++)
                {
                    var e = r.Errors[i];
                    string where = e.Line <= 0 ? "" : e.File == r.Variant ? $"line {e.Line}:{e.Column}: " : $"{e.File}, line {e.Line}:{e.Column}: ";
                    sb.AppendLine($"{(i == 0 ? r.Variant : "").PadRight(width)}  error: {where}{e.Message}{(e.Suggestion is null ? "" : " " + e.Suggestion)}");
                }
                if (r.Errors.Count > 1 || r.MoreErrors)
                    sb.AppendLine($"{"".PadRight(width)}  {Diagnostic.Count(r.Errors.Count, r.MoreErrors, r.MaxErrors)}");
                continue;
            }
            sb.AppendLine($"{r.Variant.PadRight(width)}  {r.ProgramBytes,7}  {r.FunctionBytes,10}  {r.Calls,6}  {r.Cycles,9}  {r.CyclesPerCall,8}  {r.ZeroPageBytes,4}  {r.OtherBytes,5}"
                + (r.Returned ? "" : $"  ({r.Stop})"));
        }
        if (results.Any(r => r.IndexErrors.Count > 0))
        {
            sb.AppendLine("Index check (these variants fail; the emulator found these):");
            foreach (var r in results.Where(r => r.IndexErrors.Count > 0))
            {
                sb.AppendLine($"  {r.Variant}:");
                foreach (var e in r.IndexErrors) foreach (var line in e.ToString().Split('\n')) sb.AppendLine("    " + line);
            }
        }
        sb.AppendLine("Memory (zp: zero page bytes, other: the rest; the measured function's variables and the program's globals):");
        foreach (var r in results.Where(r => r.Error is null))
            sb.AppendLine($"  {r.Variant}: " + (r.Memory.Count == 0 ? "none" : string.Join(", ", r.Memory.Select(v => $"{v.Name} ${v.Address:X4} {v.Type}"))));
        if (results.Any(r => r.Results.Count > 0))
        {
            sb.AppendLine("Results:");
            foreach (var r in results.Where(r => r.Error is null))
                sb.AppendLine($"  {r.Variant}: " + string.Join(", ", r.Results.Select(x => $"{x.What} = {x.Value}")));
            var distinct = results.Where(r => r.Error is null).Select(r => string.Join("|", r.Results.Select(x => x.Value))).Distinct().Count();
            if (distinct > 1) sb.AppendLine("  The variants give different results.");
        }
        return sb.ToString();
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Json(IReadOnlyList<BenchResult> results) =>
        JsonSerializer.Serialize(new
        {
            ok = results.All(r => r.Error is null && r.IndexErrors.Count == 0),
            variants = results.Select(r => new
            {
                variant = r.Variant,
                error = r.Error,
                errors = r.Errors,
                function = r.Function,
                programBytes = r.ProgramBytes,
                functionBytes = r.FunctionBytes,
                calls = r.Calls,
                cycles = r.Cycles,
                cyclesPerCall = r.CyclesPerCall,
                returned = r.Returned,
                stop = r.Stop,
                zeroPageBytes = r.ZeroPageBytes,
                otherBytes = r.OtherBytes,
                memory = r.Memory.Select(v => new { name = v.Name, address = v.Address, size = v.Size, type = v.Type }),
                results = r.Results.Select(x => new { what = x.What, value = x.Value }),
                indexErrors = r.IndexErrors.Select(e => new { file = e.File, line = e.Line, source = e.Source, message = e.Message, cycle = e.Cycle, count = e.Count }),
            }),
        }, Options);
}
