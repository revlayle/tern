using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>
/// Runs the benchmarks of <c>tests/benchmarks</c> written in Tern, hand-written assembly (<c>asm</c>, 64tass),
/// Prog8 (<c>prog8</c>) and C (<c>c</c>, cc65), all in the Tern emulator, checks each result against the same
/// C# computation as <see cref="BenchmarkTests"/>, and writes <c>tests/benchmarks/comparison.md</c>.
///
/// It needs Java and the Prog8 jar, 64tass and cc65, so it only runs when <c>TERN_COMPARE=1</c>.
/// Paths can be set with <c>PROG8_JAR</c>, <c>TASS64</c> and <c>CL65</c>.
/// </summary>
public class ComparisonTests
{
    private const long MaxCycles = 20_000_000;
    private const ushort Sentinel = 0xFFF0;

    private static string BenchmarkDirectory
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, "tests", "benchmarks");
                if (Directory.Exists(path)) return path;
            }
            throw new DirectoryNotFoundException($"tests/benchmarks not found above {AppContext.BaseDirectory}");
        }
    }

    private sealed record Measure(int Bytes, long Cycles);

    [Fact]
    public void Every_implementation_gives_the_right_results_and_the_comparison_report_is_written()
    {
        if (Environment.GetEnvironmentVariable("TERN_COMPARE") != "1") return;

        string dir = BenchmarkDirectory;
        string work = Path.Combine(Path.GetTempPath(), "tern-compare-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var names = BenchmarkTests.Checks.Keys.Order(StringComparer.Ordinal).ToList();
        var rows = new List<(string Name, Measure Tern, Measure Asm, Measure Prog8, Measure C)>();
        try
        {
            foreach (var name in names)
            {
                var path = Path.Combine(dir, name + ".tern");
                var run = Run.Program(File.ReadAllText(path), sourcePath: path, maxCycles: MaxCycles);
                BenchmarkTests.Checks[name](new Results(run.Ram, (n, o) => run.Address(n) + o));
                var tern = new Measure(run.Result.Image.Length, run.Cpu.Cycles);

                rows.Add((name, tern, RunAsm(dir, work, name), RunProg8(dir, work, name), RunC(dir, work, name)));
            }
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
        File.WriteAllText(Path.Combine(dir, "comparison.md"), Report(rows));
    }

    // ---- hand-written assembly ----

    private static Measure RunAsm(string dir, string work, string name)
    {
        string prg = Path.Combine(work, name + "-asm.prg"), labels = Path.Combine(work, name + "-asm.lbl");
        Exec(Tool("TASS64", @"C:\prog8-stuff\64tass\64tass.exe", "64tass"),
            ["-a", Path.Combine(dir, "asm", name + ".asm"), "-o", prg, "-l", labels], work);
        var file = File.ReadAllBytes(prg);
        var ram = new Ram();
        int load = file[0] | (file[1] << 8);
        ram.Load((ushort)load, file.AsSpan(2));
        var symbols = ParseLabels(File.ReadAllLines(labels), @"^(\w+)\s*=\s*\$([0-9a-fA-F]+)");
        long cycles = CallRoutine(ram, symbols["start"], out _);
        Verify(name, new Results(ram, (n, o) => symbols[n] + o), "assembly");
        return new Measure(file.Length - 2, cycles);
    }

    // ---- Prog8 ----

    private static Measure RunProg8(string dir, string work, string name)
    {
        string source = Path.Combine(work, name + ".p8");
        File.Copy(Path.Combine(dir, "prog8", name + ".p8"), source, true);
        string jar = Environment.GetEnvironmentVariable("PROG8_JAR") ?? @"C:\prog8-stuff\bin\prog8c-12.0-SNAPSHOT-all.jar";
        var tass = Tool("TASS64", @"C:\prog8-stuff\64tass\64tass.exe", "64tass");
        Exec("java", ["-jar", jar, "-target", "c64", source], work, tassDirectory: Path.GetDirectoryName(tass));

        var file = File.ReadAllBytes(Path.Combine(work, name + ".prg"));
        var ram = new Ram();
        ram.Load((ushort)(file[0] | (file[1] << 8)), file.AsSpan(2));
        var monitor = ParseLabels(File.ReadAllLines(Path.Combine(work, name + ".vice-mon-list")), @"^al\s+([0-9a-fA-F]+)\s+\.(\S+)", addressFirst: true);
        var asmLines = File.ReadAllLines(Path.Combine(work, name + ".asm"));

        // The entry code is `cld; tsx; stx; jsr init_system_phase2; jsr start; jmp cleanup_at_exit`.
        int entry = monitor["prog8_entrypoint"];
        int start = ram.Bytes[entry + 9] | (ram.Bytes[entry + 10] << 8);
        long cycles = CallRoutine(ram, start, out _);

        // Variables of the main block: in the monitor list, or zero page equates in the assembly.
        var zeroPage = new Dictionary<string, int>();
        foreach (var line in asmLines)
        {
            var m = Regex.Match(line, @"^p8v_(\w+)\s*=\s*(\d+)\s*;\s*zp");
            if (m.Success) zeroPage.TryAdd(m.Groups[1].Value, int.Parse(m.Groups[2].Value));
        }
        int Variable(string v)
        {
            if (monitor.TryGetValue("p8b_main:p8v_" + v, out var a)) return a;
            if (monitor.TryGetValue("p8b_main:p8v_" + v + "_lsb", out a)) return a;
            return zeroPage.TryGetValue(v, out a) ? a : throw new KeyNotFoundException($"{name}.p8: no variable '{v}'");
        }
        int Locate(string v, int offset)
        {
            if (v != "enemies") return Variable(v) + offset;
            string[] fields = ["ex", "ey", "edx", "edy", "ehits"];
            return Variable(fields[offset / 16]) + offset % 16;
        }
        Verify(name, new Results(ram, Locate), "Prog8");

        // Bytes: the program's own block and the library routines it uses, without the BASIC line,
        // the entry code and the C64 startup and exit block (p8_sys_startup).
        int bytes = monitor["prog8_bss_section_start"] - monitor["p8b_main"] - (monitor["prog8_lib"] - monitor["p8_sys_startup"]);
        return new Measure(bytes, cycles);
    }

    // ---- C (cc65) ----

    private static Measure RunC(string dir, string work, string name)
    {
        string bin = Path.Combine(work, name + "-c.bin"), map = Path.Combine(work, name + "-c.map"), labels = Path.Combine(work, name + "-c.lbl");
        string cl65 = Tool("CL65", @"C:\cc65\bin\cl65.exe", "cl65");
        // -Cl: locals are static, as variables are in Tern (no recursion, no stack frames).
        Exec(cl65, ["-t", "none", "-Oirs", "-Cl", "-m", map, "-Ln", labels, "-o", bin, Path.Combine(dir, "c", name + ".c")], work);

        var file = File.ReadAllBytes(bin);
        var ram = new Ram();
        ram.Load(0x1000, file);
        var symbols = ParseLabels(File.ReadAllLines(labels), @"^al\s+([0-9a-fA-F]+)\s+\.(\S+)", addressFirst: true);

        // Run the startup code, then time main alone.
        var cpu = new Cpu6502(ram) { PC = 0x1000, SP = 0xFF, I = true };
        int main = symbols["_main"];
        while (cpu.PC != main) Step(cpu, "startup");
        int returnTo = (ram.Bytes[0x100 + cpu.SP + 1] | (ram.Bytes[0x100 + cpu.SP + 2] << 8)) + 1;
        long from = cpu.Cycles;
        while (cpu.PC != returnTo) Step(cpu, name + ".c");
        long cycles = cpu.Cycles - from;

        Verify(name, new Results(ram, (v, o) =>
        {
            if (v != "enemies") return symbols["_" + v] + o;
            int field = o / 16, i = o % 16;
            return symbols["_enemies"] + 5 * i + field; // struct Enemy { x, y, dx, dy, hits }
        }, splitWords: false), "C");

        // Bytes: code, read-only and initialised data of the program and the library routines it uses,
        // without the startup modules (crt0, condes, zerobss, zeropage) and BSS.
        int bytes = 0;
        string? module = null;
        foreach (var line in File.ReadAllLines(map).TakeWhile(l => !l.StartsWith("Segment list")))
        {
            if (line.Length > 0 && !char.IsWhiteSpace(line[0])) { module = line; continue; }
            var m = Regex.Match(line, @"^\s+(CODE|RODATA|DATA)\s+Offs=\w+\s+Size=([0-9A-F]+)");
            if (m.Success && module is not null && !Regex.IsMatch(module, @"(crt0|condes|zerobss|zeropage|copydata|callmain)\.o"))
                bytes += Convert.ToInt32(m.Groups[2].Value, 16);
        }
        return new Measure(bytes, cycles);
    }

    // ---- shared ----

    /// <summary>Runs a subroutine as if called with jsr, and returns the cycles it took including its final rts.</summary>
    private static long CallRoutine(Ram ram, int entry, out Cpu6502 cpu)
    {
        ram.Bytes[0x1FF] = (byte)((Sentinel - 1) >> 8);
        ram.Bytes[0x1FE] = (byte)((Sentinel - 1) & 0xFF);
        cpu = new Cpu6502(ram) { PC = (ushort)entry, SP = 0xFD, I = true };
        while (cpu.PC != Sentinel) Step(cpu, $"routine at ${entry:X4}");
        return cpu.Cycles;
    }

    private static void Step(Cpu6502 cpu, string what)
    {
        cpu.Step();
        if (cpu.Cycles > MaxCycles) throw new Xunit.Sdk.XunitException($"{what} did not finish within {MaxCycles} cycles (PC=${cpu.PC:X4})");
    }

    private static void Verify(string name, Results results, string implementation)
    {
        try { BenchmarkTests.Checks[name](results); }
        catch (Exception e) { throw new Xunit.Sdk.XunitException($"{name} in {implementation} gives the wrong result: {e.Message}"); }
    }

    /// <summary>Reads `name = $addr` lines (64tass) or `al addr .name` lines (VICE monitor, ld65).</summary>
    private static Dictionary<string, int> ParseLabels(IEnumerable<string> lines, string pattern, bool addressFirst = false)
    {
        var symbols = new Dictionary<string, int>();
        foreach (var line in lines)
        {
            var m = Regex.Match(line, pattern);
            if (!m.Success) continue;
            string symbol = m.Groups[addressFirst ? 2 : 1].Value, address = m.Groups[addressFirst ? 1 : 2].Value;
            symbols.TryAdd(symbol, Convert.ToInt32(address, 16));
        }
        return symbols;
    }

    private static string Tool(string variable, string defaultPath, string fallback) =>
        Environment.GetEnvironmentVariable(variable) ?? (File.Exists(defaultPath) ? defaultPath : fallback);

    private static void Exec(string program, IEnumerable<string> arguments, string workingDirectory, string? tassDirectory = null)
    {
        var info = new ProcessStartInfo(program)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        if (tassDirectory is not null) info.Environment["PATH"] = tassDirectory + Path.PathSeparator + info.Environment["PATH"];
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new Xunit.Sdk.XunitException($"{program} failed with exit code {process.ExitCode}:\n{output.Result}\n{error.Result}");
    }

    private static string Report(List<(string Name, Measure Tern, Measure Asm, Measure Prog8, Measure C)> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Benchmark comparison");
        sb.AppendLine();
        sb.AppendLine("Generated by `ComparisonTests` (`TERN_COMPARE=1 dotnet test`). Bytes and cycles of the same nine tasks");
        sb.AppendLine("in Tern, hand-written assembly (64tass), Prog8 and C (cc65). Every version runs in the Tern emulator and");
        sb.AppendLine("its results are checked against the same C# computation. Lower is better.");
        sb.AppendLine();
        sb.AppendLine("| task | | Tern | assembly | Prog8 | cc65 |");
        sb.AppendLine("|---|---|--:|--:|--:|--:|");
        foreach (var r in rows)
        {
            sb.AppendLine($"| {r.Name} | bytes | {r.Tern.Bytes} | {r.Asm.Bytes} | {r.Prog8.Bytes} | {r.C.Bytes} |");
            sb.AppendLine($"| | cycles | {r.Tern.Cycles:N0} | {r.Asm.Cycles:N0} | {r.Prog8.Cycles:N0} | {r.C.Cycles:N0} |");
        }
        long Bytes(Func<(string Name, Measure Tern, Measure Asm, Measure Prog8, Measure C), Measure> f) => rows.Sum(r => f(r).Bytes);
        long Cycles(Func<(string Name, Measure Tern, Measure Asm, Measure Prog8, Measure C), Measure> f) => rows.Sum(r => f(r).Cycles);
        sb.AppendLine($"| **total** | bytes | {Bytes(r => r.Tern)} | {Bytes(r => r.Asm)} | {Bytes(r => r.Prog8)} | {Bytes(r => r.C)} |");
        sb.AppendLine($"| | cycles | {Cycles(r => r.Tern):N0} | {Cycles(r => r.Asm):N0} | {Cycles(r => r.Prog8):N0} | {Cycles(r => r.C):N0} |");
        sb.AppendLine();
        sb.AppendLine("Relative to the Tern version (total, Tern = 100%):");
        sb.AppendLine();
        sb.AppendLine("| | assembly | Prog8 | cc65 |");
        sb.AppendLine("|---|--:|--:|--:|");
        sb.AppendLine($"| bytes | {100.0 * Bytes(r => r.Asm) / Bytes(r => r.Tern):F0}% | {100.0 * Bytes(r => r.Prog8) / Bytes(r => r.Tern):F0}% | {100.0 * Bytes(r => r.C) / Bytes(r => r.Tern):F0}% |");
        sb.AppendLine($"| cycles | {100.0 * Cycles(r => r.Asm) / Cycles(r => r.Tern):F0}% | {100.0 * Cycles(r => r.Prog8) / Cycles(r => r.Tern):F0}% | {100.0 * Cycles(r => r.C) / Cycles(r => r.Tern):F0}% |");
        sb.AppendLine();
        sb.AppendLine("## How the numbers are taken");
        sb.AppendLine();
        sb.AppendLine("- **Cycles** run from the first instruction of the program's `main` to its final `rts`. System startup is");
        sb.AppendLine("  not counted (for cc65 the startup code runs first, and timing starts when `main` is entered). The Tern");
        sb.AppendLine("  number is the one in `baseline.txt`: until `main` returns, which includes the few instructions of the entry code.");
        sb.AppendLine("- **Bytes** are the program's code and data, and for Prog8 and cc65 the library routines it uses. Left out: the BASIC");
        sb.AppendLine("  line, entry code and the C64 startup/exit block (Prog8); the startup modules and BSS (cc65); variables in BSS (all).");
        sb.AppendLine("- Variables start at zero in every version. Prog8 pays cycles for clearing its BSS and initialising variables at the");
        sb.AppendLine("  start of `main`; Tern, the assembly and cc65 rely on zeroed memory and do not.");
        sb.AppendLine("- **Prog8**: 12.0-SNAPSHOT, `%zeropage basicsafe`, `%option no_sysinit`, default optimisation.");
        sb.AppendLine("- **cc65**: `cl65 -t none -Oirs -Cl` (inlining, register variables, static locals).");
        sb.AppendLine("- **Assembly**: written for the task, in the style of an experienced programmer: registers and zero page for");
        sb.AppendLine("  values, constants where the task fixes them (the copy sizes), no generic library routines.");
        return sb.ToString();
    }
}
