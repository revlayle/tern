using System.Text.RegularExpressions;
using Tern.Compiler.Tools;

namespace Tern.Compiler.Tests;

/// <summary>M19: the source map (<see cref="CompileResult.Instructions"/>) and the bytes and cycles per source line.</summary>
public class LineTableTests
{
    private const string Crc = """
        var crc: u8

        fn main() {
            var b: u8 = 0
            for i in 0..=255 {
                b = b + 37
                crc ^= b
                for _ in 0..8 {
                    if crc & $80 != 0 { crc = (crc << 1) ^ $07 } else { crc = crc << 1 }
                }
            }
        }
        """;

    [Fact]
    public void Every_instruction_of_the_listing_is_in_the_map_with_its_bytes_and_cycles()
    {
        var result = TernCompiler.Compile(Crc);
        var listed = Regex.Matches(result.Listing, @"^\$([0-9A-F]{4})  ((?:[0-9A-F]{2} ){1,3}) *([a-z]{3}[^;]*); (\d+)", RegexOptions.Multiline)
            .Select(m => (Address: Convert.ToInt32(m.Groups[1].Value, 16), Size: m.Groups[2].Value.Trim().Split(' ').Length,
                Text: m.Groups[3].Value.Trim(), Cycles: int.Parse(m.Groups[4].Value)))
            .ToList();
        Assert.Equal(listed.Count, result.Instructions.Count);
        foreach (var l in listed)
        {
            var i = Assert.Single(result.Instructions, i => i.Address == l.Address);
            Assert.Equal(l.Size, i.Size);
            Assert.Equal(l.Text, i.Text);
            Assert.Equal(l.Cycles, i.Cycles);
        }
    }

    [Fact]
    public void Each_source_line_gets_its_own_code_loop_steps_and_the_rts_included()
    {
        var result = TernCompiler.Compile(Crc);
        var rows = LineTable.Rows(result, "crc.tern");
        Assert.Equal("var b: u8 = 0", Row(4).Source);
        Assert.Equal("b = b + 37", Row(6).Source);
        // lda b / clc / adc #37 / sta b
        Assert.Equal(7, Row(6).Bytes);
        Assert.Equal(10, Row(6).Cycles);
        Assert.False(Row(6).MoreCycles);
        // The outer loop's line has its setup and its step: sta crc / iny / bne.
        var loop = Row(5);
        Assert.Equal(2, loop.Addresses.Count);
        Assert.True(loop.MoreCycles);
        Assert.Contains(result.Instructions, i => i.Line == 5 && i.Text.StartsWith("bne main.for"));
        // The closing rts goes to the function's line.
        Assert.Equal("rts", Assert.Single(result.Instructions, i => i.Line == 3).Text);
        // Code with no source line is listed by its section.
        Assert.Equal("(startup)", rows[^1].Source);
        Assert.Null(rows[^1].Line);
        // Every instruction is in one row.
        Assert.Equal(result.Instructions.Sum(i => i.Size), rows.Sum(r => r.Bytes));
        Assert.Equal(result.Instructions.Count, rows.Sum(r => r.Instructions));

        LineTable.Row Row(int line) => Assert.Single(rows, r => r.Line == line);
    }

    [Fact]
    public void Code_after_an_inlined_call_belongs_to_the_callers_line()
    {
        // Review M19, finding 1.
        var result = TernCompiler.Compile("""
            var a: [u8; 4]
            var k: u8 = 2
            inline fn g() -> u8 { return k + 1 }
            fn main() {
                for i in 0..4 {
                    a[i] = g()
                }
            }
            """);
        var store = Assert.Single(result.Instructions, i => i.Text.StartsWith("sta a,"));
        Assert.Equal(6, store.Line);
        var rows = LineTable.Rows(result, "game.tern");
        var row = Assert.Single(rows, r => r.Line == 6);
        Assert.Contains(row.Addresses, a => a.First <= store.Address && store.Address <= a.Last);
        // The inline function's line has only its own code: lda k / clc / adc #1.
        Assert.Equal(3, Assert.Single(rows, r => r.Line == 3).Instructions);
    }

    [Theory]
    [InlineData("if cpu.carry() { n = 1 }", "bcc")]
    [InlineData("while cpu.overflow() {\n        n += 1\n        asm { clv }\n    }", "bvs")]
    [InlineData("r = cpu.overflow() as u8", "bvc")]
    public void A_cpu_flag_test_is_on_the_line_that_uses_it(string statement, string branch)
    {
        var result = TernCompiler.Compile($$"""
            import cpu
            var n: u8
            var r: u8
            fn main() {
                asm { sec }
                {{statement}}
            }
            """);
        Assert.Equal(6, Assert.Single(result.Instructions, i => i.Text.StartsWith(branch)).Line);
        Assert.DoesNotContain(result.Instructions, i => i.File == "cpu.tern");
    }

    [Fact]
    public void The_table_text()
    {
        var result = TernCompiler.Compile("var x: u8\nfn main() {\n    x = 5\n}\n");
        var text = LineTable.Format(LineTable.Rows(result, "game.tern")).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.StartsWith("; Per source line: bytes, and the cycles", text[0]);
        Assert.Equal(";  line bytes  cycles  addresses    source", text[1]);
        // lda #5 / sta x, then the rts on the function's line; the address column is as wide as the widest entry.
        Assert.Matches(@"^      3     4       5  \$[0-9A-F]{4}-\$[0-9A-F]{4}  x = 5$", text[3]);
        Assert.Matches(@"^      2     1       6  \$[0-9A-F]{4}        fn main\(\) \{$", text[2]);
        Assert.Matches(@"^      -    \d+ +\d+  \$[0-9A-F]{4}-\$[0-9A-F]{4}  \(startup\)$", text[4]);
    }

    [Fact]
    public void Lines_of_a_module_show_its_file()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "util.tern"), "var n: u8\nfn bump() {\n    n += 3\n}\n");
            var result = TernCompiler.Compile("import util\nfn main() {\n    util.bump()\n}\n", sourcePath: Path.Combine(dir.FullName, "main.tern"));
            var rows = LineTable.Rows(result, "main.tern");
            Assert.Contains(rows, r => r.File == "util.tern" && r.Line == 3 && r.Source == "n += 3");
            Assert.Contains(rows, r => r.File == "main.tern" && r.Line == 3 && r.Source == "util.bump()");
            // The main file comes first.
            Assert.True(rows.FindIndex(r => r.File == "main.tern") < rows.FindIndex(r => r.File == "util.tern"));
            var text = LineTable.Format(rows);
            Assert.Contains("file", text.Split('\n')[1]);
            Assert.Matches(@"util\.tern\s+3\s+", text);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void A_profile_gives_each_line_its_runs_and_cycles()
    {
        var result = TernCompiler.Compile(Crc);
        var profile = new Profile();
        var ram = new Tern.Cpu.Ram();
        ram.Load((ushort)result.LoadAddress, result.Image);
        var cpu = new Tern.Cpu.Cpu6502(ram) { PC = (ushort)result.Entry, SP = 0xFF, I = true };
        long start = cpu.Cycles;
        while (cpu.PC != result.HaltAddress)
        {
            int pc = cpu.PC;
            long before = cpu.Cycles;
            cpu.Step();
            profile.Add(pc, cpu.Cycles - before);
        }
        var rows = LineTable.Rows(result, "crc.tern", profile);
        Assert.Equal(cpu.Cycles - start, rows.Sum(r => r.Executed));
        var b = Assert.Single(rows, r => r.Line == 6);
        Assert.Equal(256, b.Runs);
        Assert.Equal(256 * 10, b.Executed);
        var text = LineTable.Format(rows);
        Assert.Contains("runs", text);
        Assert.Contains($"; total {cpu.Cycles - start} cycles", text);
    }
}
