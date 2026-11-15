using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Tern.Compiler.Tools;

/// <summary>
/// A range of memory to show after a run: <c>$C000-$C00F</c>, <c>$C000:16</c>, a variable's name
/// (<c>score</c>, <c>main.count</c>) or a name with a byte count (<c>table:4</c>). Addresses may be
/// decimal or start with <c>$</c>.
/// </summary>
public sealed record MemoryDump(string What, int Address, int Size, VariableInfo? Variable)
{
    public static MemoryDump Parse(string text, CompileResult program)
    {
        string what = text.Trim(), count = "";
        int colon = what.LastIndexOf(':');
        if (colon >= 0) { count = what[(colon + 1)..]; what = what[..colon]; }
        if (what.Length == 0)
            throw new FormatException($"'{text}': --dump needs a variable or an address (score, main.count, $C000-$C0FF, $C000:16)");
        int? size = colon < 0 ? null : Number(count, text);
        if (size == 0) throw new FormatException($"'{text}': the byte count must be at least 1");

        if (what.StartsWith('$') || char.IsDigit(what[0]))
        {
            int dash = what.IndexOf('-');
            int first = Number(dash < 0 ? what : what[..dash], text);
            if (dash >= 0)
            {
                int last = Number(what[(dash + 1)..], text);
                if (last < first) throw new FormatException($"'{text}': the end is before the start");
                size = last - first + 1;
            }
            if (first + (size ?? 1) > 0x10000) throw new FormatException($"'{text}' goes past $FFFF");
            return new MemoryDump(text, first, size ?? 1, null);
        }
        if (!program.Variables.TryGetValue(what, out var v))
        {
            var near = program.Variables.Keys.Where(k => k.EndsWith("." + what) || k.Equals(what, StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
            throw new FormatException($"No variable '{what}'" + (near.Count > 0 ? $". Did you mean {string.Join(" or ", near.Select(n => $"'{n}'"))}?" : ". Locals are written 'function.name'"));
        }
        if (v.InChr)
            throw new FormatException($"'{v.Name}' is tile data in a chr area, at ${v.Address:X4} in the PPU's memory, and --dump shows the CPU's memory");
        if (v.Address + (size ?? v.Size) > 0x10000) throw new FormatException($"'{text}' goes past $FFFF ('{v.Name}' is at ${v.Address:X4})");
        return new MemoryDump(text, v.Address, size ?? v.Size, v);
    }

    private static int Number(string s, string whole)
    {
        bool ok = s.StartsWith('$')
            ? int.TryParse(s[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int n)
            : int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);
        if (!ok || n < 0 || n > 0xFFFF) throw new FormatException($"'{whole}': '{s}' is not an address or count (write $C000 or 49152)");
        return n;
    }

    /// <summary>The value of a one- or two-byte variable as its type reads it; null for other dumps.</summary>
    public long? Value(byte[] memory)
    {
        if (Variable is null || Size != Variable.Size) return null;
        int lo = memory[Address];
        return Variable.Type switch
        {
            "u8" or "bool" => lo,
            "i8" => (sbyte)lo,
            "u16" => lo | memory[Address + 1] << 8,
            "i16" => (short)(lo | memory[Address + 1] << 8),
            _ when Variable.Type.StartsWith("ptr ") => lo | memory[Address + 1] << 8,
            _ => null,
        };
    }
}

/// <summary>The text and JSON output of <c>tern run</c>.</summary>
public static class RunReport
{
    public static string Text(RunResult run, IReadOnlyList<MemoryDump> dumps, bool screen, IReadOnlyList<LineTable.Row>? profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Capitalize(run.Stop)}: {run.Cycles} cycles" + (run.MainCycles is { } m ? $" ({m} in main)" : "")
            + (run.Interrupts > 0 ? $", {run.Interrupts} interrupts" : ""));
        sb.AppendLine($"A=${run.Cpu.A:X2} X=${run.Cpu.X:X2} Y=${run.Cpu.Y:X2} SP=${run.Cpu.SP:X2} PC=${run.Cpu.PC:X4}");
        if (run.IndexErrors.Count > 0)
        {
            sb.AppendLine("Index check (the program has no check; the emulator found these):");
            foreach (var e in run.IndexErrors) foreach (var line in e.ToString().Split('\n')) sb.AppendLine("  " + line);
        }
        if (run.Output.Length > 0)
        {
            sb.AppendLine("Printed:");
            foreach (var line in run.Output.Split('\n')) sb.AppendLine("  " + line);
        }
        foreach (var d in dumps)
        {
            var bytes = run.Ram.Bytes;
            string head = d.Variable is null ? $"${d.Address:X4}" : $"{d.Variable.Name} ${d.Address:X4} {d.Variable.Type}";
            if (d.Value(bytes) is { } value)
            {
                sb.AppendLine($"{head} = {value}" + (d.Variable!.Type == "bool" ? (value != 0 ? " (true)" : " (false)") : $" (${(d.Size == 1 ? value & 0xFF : value & 0xFFFF).ToString(d.Size == 1 ? "X2" : "X4")})"));
                continue;
            }
            sb.AppendLine($"{head} ({d.Size} bytes):");
            for (int k = 0; k < d.Size; k += 16)
            {
                var chunk = bytes.AsSpan(d.Address + k, Math.Min(16, d.Size - k)).ToArray();
                sb.AppendLine($"  ${d.Address + k:X4}  {string.Join(" ", chunk.Select(b => b.ToString("X2")))}");
            }
        }
        if (screen)
        {
            var (address, columns, rows) = Runner.Screen(run);
            sb.AppendLine($"Screen (${address:X4}, {columns} x {rows}):");
            foreach (var line in Runner.ScreenText(run)) sb.AppendLine("  |" + line.PadRight(columns) + "|");
        }
        if (profile is not null)
        {
            if (run.Functions.Count > 0)
            {
                sb.AppendLine("Functions (cycles include the functions they call):");
                foreach (var f in run.Functions) sb.AppendLine($"  {f.Name,-24} {f.Calls,8} calls {f.Cycles,10} cycles");
            }
            sb.Append(LineTable.Format(profile, (run.Profile.InterruptCycles, run.Interrupts)));
        }
        return sb.ToString();
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <param name="warnings">The program's warnings (spec 13).</param>
    public static string Json(RunResult run, IReadOnlyList<MemoryDump> dumps, bool screen, IReadOnlyList<LineTable.Row>? profile, IReadOnlyList<Diagnostic>? warnings = null) =>
        JsonSerializer.Serialize(new
        {
            ok = true,
            returned = run.Returned,
            stop = run.Stop,
            cycles = run.Cycles,
            mainCycles = run.MainCycles,
            interrupts = run.Interrupts,
            interruptCycles = run.Profile.InterruptCycles,
            registers = new { a = run.Cpu.A, x = run.Cpu.X, y = run.Cpu.Y, sp = run.Cpu.SP, pc = run.Cpu.PC },
            output = run.Output,
            indexErrors = run.IndexErrors.Select(e => new { file = e.File, line = e.Line, source = e.Source, message = e.Message, cycle = e.Cycle, count = e.Count }),
            memory = dumps.Select(d => new
            {
                what = d.What,
                address = d.Address,
                size = d.Size,
                type = d.Variable?.Type,
                value = d.Value(run.Ram.Bytes),
                bytes = run.Ram.Bytes.AsSpan(d.Address, d.Size).ToArray().Select(b => (int)b).ToArray(),
            }),
            screen = screen ? Runner.ScreenText(run) : null,
            functions = profile is null ? null : run.Functions,
            lines = profile?.Select(r => new
            {
                file = r.File, line = r.Line, source = r.Source, bytes = r.Bytes, cycles = r.Cycles, moreCycles = r.MoreCycles,
                runs = r.Runs, runCycles = r.Executed,
            }),
            diagnostics = warnings ?? [],
        }, Options);

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
