using System.Text;

namespace Tern.Compiler.Tests;

/// <summary>
/// The programs in <c>tests/benchmarks</c>: each is run and its results checked, and its size and
/// cycle count must match <c>tests/benchmarks/baseline.txt</c>. A change in either fails the test,
/// so that regressions are seen and improvements are recorded. To accept new numbers, run the
/// tests with the environment variable <c>TERN_UPDATE_BENCHMARKS=1</c>, which rewrites the baseline.
/// </summary>
public class BenchmarkTests
{
    internal static string Directory_
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

    /// <summary>Checks a benchmark's results against the same work done in C#.</summary>
    internal static readonly Dictionary<string, Action<Results>> Checks = new()
    {
        ["bubble"] = r =>
        {
            var values = new List<byte>();
            byte x = 7;
            for (int i = 0; i < 100; i++) values.Add(x = (byte)(x * 5 + 17));
            values.Sort();
            for (int i = 0; i < 100; i++) Assert.Equal(values[i], r.U8("values", i));
        },
        ["calls"] = r =>
        {
            ushort total = 0;
            byte clamped = 0;
            for (int i = 0; i < 200; i++)
            {
                byte s = (byte)(i + (i >> 1) + 3);
                total += s;
                clamped = Math.Min(s, (byte)150);
                if (s > 100) total++;
            }
            Assert.Equal(total, r.U16("total"));
            Assert.Equal(clamped, r.U8("clamped"));
        },
        ["copy"] = r =>
        {
            Assert.Equal(1, r.U8("same"));
            for (int k = 0; k < 250; k++)
            {
                Assert.Equal((byte)k, r.Ram.Bytes[0x5000 + 4 * k]);
                Assert.Equal((byte)(k ^ 0xFF), r.Ram.Bytes[0x5000 + 4 * k + 1]);
                Assert.Equal((byte)(k + 1), r.Ram.Bytes[0x5000 + 4 * k + 2]);
                Assert.Equal((byte)(k >> 1), r.Ram.Bytes[0x5000 + 4 * k + 3]);
            }
        },
        ["crc8"] = r =>
        {
            byte crc = 0, b = 0;
            for (int i = 0; i < 256; i++)
            {
                b += 37;
                crc ^= b;
                for (int k = 0; k < 8; k++) crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ 7) : (byte)(crc << 1);
            }
            Assert.Equal(crc, r.U8("crc"));
        },
        ["enemies"] = r =>
        {
            var x = new byte[16]; var y = new byte[16]; var dx = new sbyte[16]; var dy = new sbyte[16]; var hits = new byte[16];
            for (int i = 0; i < 16; i++) { x[i] = (byte)(i * 9 + 10); y[i] = (byte)(i * 5 + 20); dx[i] = 1; dy[i] = -2; }
            ushort bounces = 0;
            for (int f = 0; f < 50; f++)
                for (int i = 0; i < 16; i++)
                {
                    byte nx = (byte)(x[i] + dx[i]);
                    if (nx < 8 || nx > 200) { dx[i] = (sbyte)-dx[i]; hits[i]++; bounces++; }
                    x[i] = nx;
                    byte ny = (byte)(y[i] + dy[i]);
                    if (ny < 16 || ny > 180) { dy[i] = (sbyte)-dy[i]; bounces++; }
                    y[i] = ny;
                }
            Assert.Equal(bounces, r.U16("bounces"));
            // One table per field: x at 0, y at 16, dx at 32, dy at 48, hits at 64.
            for (int i = 0; i < 16; i++)
            {
                Assert.Equal(x[i], r.U8("enemies", i));
                Assert.Equal(y[i], r.U8("enemies", 16 + i));
                Assert.Equal(hits[i], r.U8("enemies", 64 + i));
            }
        },
        ["fib16"] = r =>
        {
            var fib = new ushort[24];
            fib[1] = 1;
            for (int i = 2; i < 24; i++) fib[i] = (ushort)(fib[i - 1] + fib[i - 2]);
            for (int i = 0; i < 24; i++) Assert.Equal(fib[i], r.U16At("fib", i, 24));
            Assert.Equal((ushort)(fib[20] * 9 + 123 * 45), r.U16("product"));
        },
        ["interp"] = r =>
        {
            Assert.Equal(55, r.U8("result"));
            Assert.Equal(2 + 10 * 7 + 2, r.U16("steps"));
        },
        ["sieve"] = r => Assert.Equal(54, r.U8("count")),
        ["sum16"] = r =>
        {
            ushort v = 1000, total = 0, largest = 0;
            for (int i = 0; i < 200; i++)
            {
                v = (ushort)(v + (v >> 3) + 77);
                Assert.Equal(v, r.U16At("values", i, 200));
                total += v;
                largest = Math.Max(largest, v);
            }
            Assert.Equal(total, r.U16("total"));
            Assert.Equal(largest, r.U16("largest"));
        },
    };

    /// <summary>The benchmarks' names, each with every platform.</summary>
    public static TheoryData<string, string> OnEveryPlatform()
    {
        var data = new TheoryData<string, string>();
        foreach (var path in Directory.GetFiles(Directory_, "*.tern").Order(StringComparer.Ordinal))
            foreach (var platform in Platform.All)
                // copy.tern copies 1000 bytes from $4000 to $5000: RAM the NES does not have (2 KB; $4000 is its APU).
                if (!(Path.GetFileName(path) == "copy.tern" && platform == Platform.Nes)) data.Add(Path.GetFileNameWithoutExtension(path), platform.Name);
        return data;
    }

    /// <summary>
    /// A benchmark's results, on every platform (review code-quality-tests 5), one case per program so that a wrong
    /// result does not hide the others (14). Before R6 the Atari and the Apple II each had a copy, and the NES none.
    /// </summary>
    [Theory]
    [MemberData(nameof(OnEveryPlatform))]
    public void A_benchmark_gives_the_right_results(string name, string target)
    {
        var path = Path.Combine(Directory_, name + ".tern");
        var r = Run.Program(File.ReadAllText(path), sourcePath: path, maxCycles: 20_000_000, platform: Platform.ByName(target));
        Checks[name](new Results(r.Ram, (n, o) => r.Address(n) + o));
    }

    /// <summary>The bytes and cycles of each benchmark on the C64, against the baseline.</summary>
    [Fact]
    public void The_benchmarks_have_the_recorded_size_and_speed()
    {
        string dir = Directory_;
        var names = Directory.GetFiles(dir, "*.tern").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(Checks.Keys.Order(StringComparer.Ordinal), names!);

        var measured = new List<(string Name, int Bytes, long Cycles)>();
        foreach (var name in names)
        {
            var path = Path.Combine(dir, name + ".tern");
            var r = Run.Program(File.ReadAllText(path), sourcePath: path, maxCycles: 20_000_000, platform: Platform.C64);
            measured.Add((name!, r.Result.Image.Length, r.Cpu.Cycles));
        }

        var table = new StringBuilder();
        table.AppendLine("# Bytes of the program image and cycles until main returns, per benchmark.");
        table.AppendLine("# Rewritten by running the tests with TERN_UPDATE_BENCHMARKS=1.");
        table.AppendLine($"# {"name",-10} {"bytes",6} {"cycles",9}");
        foreach (var (name, bytes, cycles) in measured) table.AppendLine($"{name,-12} {bytes,6} {cycles,9}");
        table.AppendLine($"{"total",-12} {measured.Sum(m => m.Bytes),6} {measured.Sum(m => m.Cycles),9}");

        var baselinePath = Path.Combine(dir, "baseline.txt");
        if (Environment.GetEnvironmentVariable("TERN_UPDATE_BENCHMARKS") == "1")
        {
            File.WriteAllText(baselinePath, table.ToString());
            return;
        }
        var baseline = File.Exists(baselinePath)
            ? File.ReadAllLines(baselinePath).Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .ToDictionary(p => p[0], p => (Bytes: int.Parse(p[1]), Cycles: long.Parse(p[2])))
            : [];

        var changes = new StringBuilder();
        foreach (var (name, bytes, cycles) in measured)
        {
            if (!baseline.TryGetValue(name, out var old))
                changes.AppendLine($"{name}: not in the baseline ({bytes} bytes, {cycles} cycles)");
            else if (old.Bytes != bytes || old.Cycles != cycles)
                changes.AppendLine($"{name}: {old.Bytes} -> {bytes} bytes ({bytes - old.Bytes:+0;-0}), {old.Cycles} -> {cycles} cycles ({cycles - old.Cycles:+0;-0})");
        }
        foreach (var name in baseline.Keys.Where(k => k != "total" && measured.All(m => m.Name != k)))
            changes.AppendLine($"{name}: in the baseline, but there is no {name}.tern");
        Assert.True(changes.Length == 0,
            $"Benchmark results differ from tests/benchmarks/baseline.txt:\n{changes}\n" +
            $"If the change is intended, run the tests with TERN_UPDATE_BENCHMARKS=1 to record it.\n\n{table}");
    }

    /// <summary>The instructions of main between a label and the next, as mnemonics (M9: same as the assembly).</summary>
    [Trait("Shape", "nmos6502")]
    [Fact]
    public void The_sieve_inner_loop_is_the_loop_of_the_assembly_version()
    {
        string dir = Directory_;
        var path = Path.Combine(dir, "sieve.tern");
        var main = Listing.Annotated(TernCompiler.Compile(File.ReadAllText(path), sourcePath: path));
        int start = main.FindIndex(l => l.StartsWith("main.for"));
        int end = main.FindIndex(start, l => l.StartsWith("main.fend"));
        var tern = main[start..end].Where(l => !l.EndsWith(':') && !l.StartsWith(';')).Select(l => l.Split(' ')[0]).ToList();

        // The loop of asm/sieve.asm, from "mark" to its bcc.
        var asm = File.ReadAllLines(Path.Combine(dir, "asm", "sieve.asm"));
        int mark = Array.FindIndex(asm, l => l.StartsWith("mark"));
        var expected = new List<string>();
        for (int k = mark; ; k++)
        {
            var op = asm[k].Split(';')[0].Trim();
            if (k == mark) op = op["mark".Length..].Trim();
            expected.Add(op.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
            if (expected[^1] == "bcc") break;
        }
        Assert.Equal(expected, tern);
    }
}
