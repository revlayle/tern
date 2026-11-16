using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>
/// Runs the benchmarks of <c>tests/benchmarks</c> written in Tern, hand-written assembly (<c>asm</c>, 64tass),
/// Prog8 (<c>prog8</c>) and C (<c>c</c>, cc65 and llvm-mos), all in the Tern emulator, checks each result against the same
/// C# computation as <see cref="BenchmarkTests"/>, and writes <c>tests/benchmarks/comparison.md</c>.
///
/// It needs Java and the Prog8 jar, 64tass, cc65 and llvm-mos, so it only runs when <c>TERN_COMPARE=1</c>.
/// Paths can be set with <c>PROG8_JAR</c>, <c>TASS64</c>, <c>CL65</c> and <c>MOS_CLANG</c>.
/// </summary>
public class ComparisonTests
{
    private const long MaxCycles = 20_000_000;
    private const ushort Sentinel = 0xFFF0;

    internal sealed record Measure(int Bytes, long Cycles)
    {
        public static implicit operator Measure((int Bytes, long Cycles) m) => new(m.Bytes, m.Cycles);
    }

    private sealed record Row(string Name, Measure Tern, Measure Asm, Measure Prog8, Measure C, Measure Mos);

    [Fact]
    public void Every_implementation_gives_the_right_results_and_the_comparison_report_is_written()
    {
        if (Environment.GetEnvironmentVariable("TERN_COMPARE") != "1") return;

        string dir = BenchmarkTests.Directory_;
        string work = Path.Combine(Path.GetTempPath(), "tern-compare-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var names = BenchmarkTests.Checks.Keys.Order(StringComparer.Ordinal).ToList();
        var rows = new List<Row>();
        try
        {
            foreach (var name in names)
            {
                var tern = RunTern(dir, name);

                rows.Add(new Row(name, tern, RunAsm(dir, work, name), RunProg8(dir, work, name), RunC(dir, work, name), RunMos(dir, work, name)));
            }
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
        File.WriteAllText(Path.Combine(dir, "comparison.md"), Report(rows));
    }

    // ---- Tern ----

    /// <summary>Bytes from the label <c>main</c> to the end of the image, and the cycles from entering main to its rts, as for the others.</summary>
    private static Measure RunTern(string dir, string name) => MeasureTern(Path.Combine(dir, name + ".tern"), name);

    /// <summary>Compiles, runs and checks one Tern source against the result check of benchmark <paramref name="check"/>.</summary>
    internal static (int Bytes, long Cycles) MeasureTern(string path, string check) =>
        MeasureTern(path, BenchmarkTests.Checks[check]);

    /// <summary>Same, with the result check given as a function.</summary>
    internal static (int Bytes, long Cycles) MeasureTern(string path, Action<Results> check)
    {
        var name = Path.GetFileName(path);
        var result = TernCompiler.Compile(File.ReadAllText(path), sourcePath: path);
        var ram = new Ram();
        ram.Load((ushort)result.LoadAddress, result.Image);
        var cpu = new Cpu6502(ram, result.Platform.Cpu) { PC = (ushort)result.Entry, SP = 0xFF, I = true };
        int main = result.Symbols["main"];
        while (cpu.PC != main) Step(cpu, name + " startup");
        long from = cpu.Cycles;
        while (cpu.PC != result.HaltAddress) Step(cpu, name);
        long cycles = cpu.Cycles - from;
        try { check(new Results(ram, (n, o) => result.Symbols[n] + o)); }
        catch (Exception e) { throw new Xunit.Sdk.XunitException($"{name} gives the wrong result: {e.Message}"); }
        return (result.LoadAddress + result.Image.Length - main, cycles);
    }

    /// <summary>
    /// The listing of a Tern program with, in front of each instruction, how many times main ran it and the cycles it took in all
    /// (from entering main to its return). The exact counts behind the estimates in <c>analysis.md</c>.
    /// </summary>
    internal static string Profile(string path)
    {
        var result = TernCompiler.Compile(File.ReadAllText(path), sourcePath: path);
        var ram = new Ram();
        ram.Load((ushort)result.LoadAddress, result.Image);
        var cpu = new Cpu6502(ram, result.Platform.Cpu) { PC = (ushort)result.Entry, SP = 0xFF, I = true };
        int main = result.Symbols["main"];
        while (cpu.PC != main) Step(cpu, "startup");
        var count = new Dictionary<int, int>();
        var cycles = new Dictionary<int, long>();
        while (cpu.PC != result.HaltAddress)
        {
            int pc = cpu.PC;
            long before = cpu.Cycles;
            Step(cpu, path);
            count[pc] = count.GetValueOrDefault(pc) + 1;
            cycles[pc] = cycles.GetValueOrDefault(pc) + (cpu.Cycles - before);
        }
        // A listing line can hold two instructions (`bcc *+5 / jmp x`): the second one's cycles go to the line it is on.
        var lines = result.Listing.Split((char)10);
        var starts = lines.Select(l => Regex.Match(l, @"^\$([0-9A-F]{4})  ")).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber)).Distinct().Order().ToList();
        foreach (var pc in cycles.Keys.Where(pc => !starts.Contains(pc)).ToList())
        {
            int at = starts.Last(s => s < pc);
            cycles[at] = cycles.GetValueOrDefault(at) + cycles[pc];
            count.TryAdd(at, 0);
        }
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var m = Regex.Match(line, @"^\$([0-9A-F]{4})  ");
            if (m.Success && int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber) is var a && count.TryGetValue(a, out var n))
                sb.AppendLine($"{n,7} x {cycles[a],8}  {line.TrimEnd((char)13)}");
            else
                sb.AppendLine($"{"",19}  {line.TrimEnd((char)13)}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// The same for the hand-written assembly version of a task: the 64tass listing with the count and cycles of each instruction,
    /// from <c>start</c> to its return, and the total at the end.
    /// </summary>
    internal static string AsmProfile(string dir, string name)
    {
        string work = Path.Combine(Path.GetTempPath(), "tern-asm-profile");
        Directory.CreateDirectory(work);
        string prg = Path.Combine(work, name + ".prg"), labels = Path.Combine(work, name + ".lbl"), listing = Path.Combine(work, name + ".lst");
        Exec(Tool("TASS64", @"C:\prog8-stuff\64tass\64tass.exe", "64tass"),
            ["-a", Path.Combine(dir, "asm", name + ".asm"), "-o", prg, "-l", labels, "-L", listing, "--no-monitor"], work);
        var file = File.ReadAllBytes(prg);
        var ram = new Ram();
        ram.Load((ushort)(file[0] | (file[1] << 8)), file.AsSpan(2));
        int entry = ParseLabels(File.ReadAllLines(labels), @"^(\w+)\s*=\s*\$([0-9a-fA-F]+)")["start"];
        ram.Bytes[0x1FF] = (byte)((Sentinel - 1) >> 8);
        ram.Bytes[0x1FE] = (byte)((Sentinel - 1) & 0xFF);
        var cpu = new Cpu6502(ram, Platform.C64.Cpu) { PC = (ushort)entry, SP = 0xFD, I = true };
        var count = new Dictionary<int, int>();
        var cycles = new Dictionary<int, long>();
        while (cpu.PC != Sentinel)
        {
            int pc = cpu.PC;
            long before = cpu.Cycles;
            Step(cpu, name);
            count[pc] = count.GetValueOrDefault(pc) + 1;
            cycles[pc] = cycles.GetValueOrDefault(pc) + (cpu.Cycles - before);
        }
        var sb = new StringBuilder();
        foreach (var line in File.ReadAllLines(listing).Where(l => !l.StartsWith(';')))
        {
            var m = Regex.Match(line, @"^\.([0-9a-f]{4})\t[0-9a-f]");
            if (m.Success && count.TryGetValue(Convert.ToInt32(m.Groups[1].Value, 16), out var n))
                sb.AppendLine($"{n,7} x {cycles[Convert.ToInt32(m.Groups[1].Value, 16)],8}  {line.Replace('\t', ' ')}");
            else
                sb.AppendLine($"{"",19}  {line.Replace('\t', ' ')}");
        }
        sb.AppendLine($"total {cpu.Cycles} cycles");
        return sb.ToString();
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
        long cycles = CallRoutine(ram, symbols["start"]);
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

        int start = monitor["p8b_main:p8s_start"];
        long cycles = CallRoutine(ram, start);

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
        var cpu = new Cpu6502(ram, Platform.C64.Cpu) { PC = 0x1000, SP = 0xFF, I = true };
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

    // ---- C (llvm-mos) ----

    /// <summary>
    /// The same C source with <c>mos-c64-clang -Os</c> (without LTO, which would delete the result variables nobody reads). The startup code runs in the emulator
    /// (the KERNAL's CHROUT at $FFD2 is an <c>rts</c>), then main alone is timed, as for cc65. Bytes are the sections of
    /// the program and of any library routine it calls (<c>memcpy</c>, multiply), without the startup modules, the BASIC
    /// header, <c>exit</c> and <c>__memset</c> (only the startup's BSS clearing calls it) and without BSS.
    /// </summary>
    private static Measure RunMos(string dir, string work, string name)
    {
        string clang = Tool("MOS_CLANG", @"C:\llvm-mos\bin\mos-clang.exe", "mos-clang");
        string bin = Path.GetDirectoryName(clang) is { Length: > 0 } d ? d : "";
        string prg = Path.Combine(work, name + "-mos.prg"), map = Path.Combine(work, name + "-mos.map");
        // The SDK's mos-c64-clang.bat is mos-clang with --config mos-c64.cfg.
        var arguments = new List<string> { "--config", "mos-c64.cfg" };
        arguments.AddRange(["-Os", "-fno-lto", "-w", "-o", prg, "-Wl,-Map=" + map, Path.Combine(dir, "c", name + ".c")]);
        Exec(clang, arguments, work);

        var file = File.ReadAllBytes(prg);
        var ram = new Ram();
        ram.Load((ushort)(file[0] | (file[1] << 8)), file.AsSpan(2));
        ram.Bytes[0xFFD2] = 0x60; // KERNAL CHROUT: the startup code prints one character
        string nm = Path.Combine(bin, "llvm-nm.exe");
        var symbols = new Dictionary<string, int>();
        foreach (var line in ExecOutput(File.Exists(nm) ? nm : "llvm-nm", [prg + ".elf"], work).Split('\n'))
        {
            var m = Regex.Match(line, @"^([0-9a-fA-F]{8})\s+\w\s+(\S+)\s*$");
            if (m.Success) symbols.TryAdd(m.Groups[2].Value, Convert.ToInt32(m.Groups[1].Value, 16));
        }

        // Run the startup code, then time main alone.
        // The BASIC header is a line of tokens, not code: start at _start.
        var cpu = new Cpu6502(ram, Platform.C64.Cpu) { PC = (ushort)symbols["_start"], SP = 0xFF, I = true };
        int main = symbols["main"];
        while (cpu.PC != main) Step(cpu, name + " (llvm-mos) startup");
        int returnTo = (ram.Bytes[0x100 + cpu.SP + 1] | (ram.Bytes[0x100 + cpu.SP + 2] << 8)) + 1;
        long from = cpu.Cycles;
        while (cpu.PC != returnTo) Step(cpu, name + ".c (llvm-mos)");
        long cycles = cpu.Cycles - from;

        Verify(name, new Results(ram, (v, o) =>
        {
            if (v != "enemies") return symbols[v] + o;
            int field = o / 16, i = o % 16;
            return symbols["enemies"] + 5 * i + field; // struct Enemy { x, y, dx, dy, hits }
        }, splitWords: false), "C (llvm-mos)");

        int bytes = 0;
        foreach (var line in File.ReadAllLines(map))
        {
            var m = Regex.Match(line, @"^\s+[0-9a-f]+\s+[0-9a-f]+\s+([0-9a-f]+)\s+1\s+(.+?):\((\.\S+)\)\s*$");
            if (!m.Success) continue;
            string obj = m.Groups[2].Value, section = m.Groups[3].Value;
            if (Regex.IsMatch(obj, @"crt0|unmap-basic|basic-header|libcrt0")) continue;
            if (section is ".text.exit" or ".text.__memset") continue;
            if (Regex.IsMatch(section, @"^\.(text|rodata|data|zp\.data|zp\.rodata)(\.|$)"))
                bytes += Convert.ToInt32(m.Groups[1].Value, 16);
        }
        return new Measure(bytes, cycles);
    }

    // ---- shared ----

    /// <summary>Runs a subroutine as if called with jsr, and returns the cycles it took including its final rts.</summary>
    private static long CallRoutine(Ram ram, int entry)
    {
        ram.Bytes[0x1FF] = (byte)((Sentinel - 1) >> 8);
        ram.Bytes[0x1FE] = (byte)((Sentinel - 1) & 0xFF);
        var cpu = new Cpu6502(ram, Platform.C64.Cpu) { PC = (ushort)entry, SP = 0xFD, I = true };
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

    private static string ExecOutput(string program, IEnumerable<string> arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo(program) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return output.Result;
    }

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

    private static string Report(List<Row> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Benchmark comparison");
        sb.AppendLine();
        sb.AppendLine("Generated by `ComparisonTests` (`TERN_COMPARE=1 dotnet test`). Bytes and cycles of the same nine tasks");
        sb.AppendLine("in Tern, hand-written assembly (64tass), Prog8 and C (cc65 and llvm-mos). Every version runs in the Tern");
        sb.AppendLine("emulator and its results are checked against the same C# computation. Lower is better.");
        sb.AppendLine();
        sb.AppendLine("| task | | Tern | assembly | Prog8 | cc65 | llvm-mos |");
        sb.AppendLine("|---|---|--:|--:|--:|--:|--:|");
        foreach (var r in rows)
        {
            sb.AppendLine($"| {r.Name} | bytes | {r.Tern.Bytes} | {r.Asm.Bytes} | {r.Prog8.Bytes} | {r.C.Bytes} | {r.Mos.Bytes} |");
            sb.AppendLine($"| | cycles | {r.Tern.Cycles:N0} | {r.Asm.Cycles:N0} | {r.Prog8.Cycles:N0} | {r.C.Cycles:N0} | {r.Mos.Cycles:N0} |");
        }
        long Bytes(Func<Row, Measure> f) => rows.Sum(r => f(r).Bytes);
        long Cycles(Func<Row, Measure> f) => rows.Sum(r => f(r).Cycles);
        sb.AppendLine($"| **total** | bytes | {Bytes(r => r.Tern)} | {Bytes(r => r.Asm)} | {Bytes(r => r.Prog8)} | {Bytes(r => r.C)} | {Bytes(r => r.Mos)} |");
        sb.AppendLine($"| | cycles | {Cycles(r => r.Tern):N0} | {Cycles(r => r.Asm):N0} | {Cycles(r => r.Prog8):N0} | {Cycles(r => r.C):N0} | {Cycles(r => r.Mos):N0} |");
        sb.AppendLine();
        sb.AppendLine("Relative to the Tern version (total, Tern = 100%):");
        sb.AppendLine();
        sb.AppendLine("| | assembly | Prog8 | cc65 | llvm-mos |");
        sb.AppendLine("|---|--:|--:|--:|--:|");
        sb.AppendLine($"| bytes | {100.0 * Bytes(r => r.Asm) / Bytes(r => r.Tern):F0}% | {100.0 * Bytes(r => r.Prog8) / Bytes(r => r.Tern):F0}% | {100.0 * Bytes(r => r.C) / Bytes(r => r.Tern):F0}% | {100.0 * Bytes(r => r.Mos) / Bytes(r => r.Tern):F0}% |");
        sb.AppendLine($"| cycles | {100.0 * Cycles(r => r.Asm) / Cycles(r => r.Tern):F0}% | {100.0 * Cycles(r => r.Prog8) / Cycles(r => r.Tern):F0}% | {100.0 * Cycles(r => r.C) / Cycles(r => r.Tern):F0}% | {100.0 * Cycles(r => r.Mos) / Cycles(r => r.Tern):F0}% |");
        sb.AppendLine();
        sb.AppendLine("## How the numbers are taken");
        sb.AppendLine();
        sb.AppendLine("- **Cycles** run from the first instruction of the program's `main` to its final `rts`. Startup is not counted:");
        sb.AppendLine("  for Tern, cc65 and llvm-mos the startup code runs first and timing starts when `main` is entered; the assembly has none.");
        sb.AppendLine("  The numbers in `baseline.txt` are different: they cover the whole Tern program, startup included.");
        sb.AppendLine("- **Bytes** are the program's code and data, and for Prog8, cc65 and llvm-mos the library routines it uses. Left out: the BASIC");
        sb.AppendLine("  line and startup code (Tern, from the `main` label on); the BASIC line, entry code and the C64 startup/exit block");
        sb.AppendLine("  (Prog8); the startup modules (cc65); the BASIC header, startup modules, `exit` and `__memset` (llvm-mos); variables in BSS (all).");
        sb.AppendLine("- Variables start at zero in every version. Tern, the assembly, cc65 and llvm-mos do the clearing outside the measured part");
        sb.AppendLine("  (Tern's startup, zeroed memory, cc65's `zerobss`, llvm-mos's `__zero_bss`). Prog8 clears its BSS and initialises variables at the start of");
        sb.AppendLine("  `main`, so its bytes and cycles include that.");
        sb.AppendLine("- **Prog8**: 12.0-SNAPSHOT, `%zeropage basicsafe`, `%option no_sysinit`, default optimisation.");
        sb.AppendLine("- **cc65**: `cl65 -t none -Oirs -Cl` (inlining, register variables, static locals).");
        sb.AppendLine("- **llvm-mos**: `mos-c64-clang -Os -fno-lto` (the llvm-mos SDK; without link-time optimisation, which would remove the result variables that only the test reads). The same C sources as cc65.");
        sb.AppendLine("  Its startup code runs in the emulator with the KERNAL's `CHROUT` stubbed out as an `rts`. It keeps small globals in zero page.");
        sb.AppendLine("- **Assembly**: written for the task, in the style of an experienced programmer: registers and zero page for");
        sb.AppendLine("  values, constants where the task fixes them (the copy sizes), no generic library routines. The `interp` dispatch");
        sb.AppendLine("  does not range-check the opcode, where Tern's `match` sends other values to the `_` arm.");
        sb.AppendLine("- **Data layout**: Tern, the assembly and Prog8 keep `enemies` as one table per field; the C versions use an array of");
        sb.AppendLine("  structs (natural C), so each access multiplies the index by 5. Their large `enemies` cycle counts are that choice, not");
        sb.AppendLine("  only the C compilers' code generation.");
        return sb.ToString();
    }
}
