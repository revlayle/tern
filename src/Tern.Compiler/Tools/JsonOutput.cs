using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tern.Compiler.Tools;

/// <summary>
/// The JSON the tools write (<c>tern --json</c>, <c>tern run --json</c>, <c>tern bench --json</c>; review
/// code-quality-tools 7): each output is a named record below, written with one set of options. A field's
/// name is its property's in camelCase, in the order declared; null fields are written as null.
/// </summary>
internal static class JsonOutput
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}

/// <summary><c>tern --json</c>, and a command-line problem or failed compile of any command: <c>ok</c>, the files written, the report's lines and the diagnostics.</summary>
internal sealed record CompileOutput(bool Ok, IReadOnlyList<string> Files, IReadOnlyList<string> Report, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary><c>tern run --json</c> after a run.</summary>
internal sealed record RunOutput(bool Ok, bool Returned, string Stop, long Cycles, long? MainCycles, int Interrupts, long InterruptCycles,
    RegistersOutput Registers, string Output, IEnumerable<IndexErrorOutput> IndexErrors, IEnumerable<MemoryOutput> Memory,
    IReadOnlyList<string>? Screen, IReadOnlyList<FunctionTime>? Functions, IEnumerable<LineOutput>? Lines, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>The CPU's registers when a run stopped.</summary>
internal sealed record RegistersOutput(byte A, byte X, byte Y, byte Sp, ushort Pc);

/// <summary>A <c>--dump</c>: its bytes, and the value as the variable's type reads it.</summary>
internal sealed record MemoryOutput(string What, int Address, int Size, string? Type, long? Value, int[] Bytes);

/// <summary>A line of <c>--profile</c>: its code and what it cost in the run.</summary>
internal sealed record LineOutput(string File, int? Line, string Source, int Bytes, int Cycles, bool MoreCycles, long? Runs, long? RunCycles)
{
    public static LineOutput From(LineTable.Row r) => new(r.File, r.Line, r.Source, r.Bytes, r.Cycles, r.MoreCycles, r.Runs, r.Executed);
}

/// <summary>An index outside its table that the run found (<see cref="IndexError"/>), in <c>tern run</c> and <c>tern bench</c>.</summary>
internal sealed record IndexErrorOutput(string File, int? Line, string Source, string Message, long Cycle, int Count)
{
    public static IndexErrorOutput From(IndexError e) => new(e.File, e.Line, e.Source, e.Message, e.Cycle, e.Count);
}

/// <summary><c>tern bench --json</c>: <c>ok</c> and each variant.</summary>
internal sealed record BenchOutput(bool Ok, IEnumerable<BenchVariantOutput> Variants);

/// <summary>One variant of <c>tern bench</c>: its error or its bytes, cycles, memory and results.</summary>
internal sealed record BenchVariantOutput(string Variant, Diagnostic? Error, IReadOnlyList<Diagnostic> Errors, string Function, int ProgramBytes, int FunctionBytes,
    long Calls, long Cycles, long CyclesPerCall, bool Returned, string Stop, int ZeroPageBytes, int OtherBytes, IEnumerable<BenchMemoryOutput> Memory,
    IEnumerable<BenchValueOutput> Results, IEnumerable<IndexErrorOutput> IndexErrors)
{
    public static BenchVariantOutput From(BenchResult r) => new(r.Variant, r.Error, r.Errors, r.Function, r.ProgramBytes, r.FunctionBytes,
        r.Calls, r.Cycles, r.CyclesPerCall, r.Returned, r.Stop, r.ZeroPageBytes, r.OtherBytes,
        r.Memory.Select(v => new BenchMemoryOutput(v.Name, v.Address, v.Size, v.Type)), r.Results.Select(x => new BenchValueOutput(x.What, x.Value)),
        r.IndexErrors.Select(IndexErrorOutput.From));
}

/// <summary>A variable a bench variant uses, with the address the compiler chose.</summary>
internal sealed record BenchMemoryOutput(string Name, int Address, int Size, string Type);

/// <summary>A <c>--dump</c> of a bench variant, as text.</summary>
internal sealed record BenchValueOutput(string What, string Value);
