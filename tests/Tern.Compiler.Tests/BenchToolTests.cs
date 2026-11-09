using System.Text.Json;
using Tern.Compiler.Tools;

namespace Tern.Compiler.Tests;

/// <summary>M19: <c>tern bench</c>, comparing ways of writing the same code (<see cref="Bench"/>).</summary>
public class BenchToolTests
{
    private static readonly string[] Decl = ["var a: [u8; 16]"];

    [Fact]
    public void A_snippet_is_the_body_of_main_after_the_declarations()
    {
        var v = BenchVariant.Snippet("code 1", ["var a: u8", "var b: u8"], "a = 1; b = 2");
        Assert.Equal("var a: u8\nvar b: u8\nfn main() {\na = 1; b = 2\n}\n", v.Source);
        Assert.Equal(("decl 2", 1), v.Place(2));
        Assert.Equal(("code 1", 1), v.Place(4));
        Assert.Equal("fn main() {\nx = 1\n}\n", BenchVariant.Snippet("code 1", [], "x = 1").Source);
    }

    [Fact]
    public void An_error_in_a_snippet_with_no_declarations_is_on_its_line()
    {
        // Review M19, finding 2.
        var r = Bench.Measure(BenchVariant.Snippet("code 1", [], "var x: u8 = 1; x = nope"), null, []);
        Assert.Equal("code 1", r.Error!.File);
        Assert.Equal(1, r.Error.Line);
        Assert.Equal(20, r.Error.Column);
        Assert.StartsWith("code 1    error: line 1:20: 'nope' is not declared", Bench.Text([r]).Split('\n')[1]);
    }

    [Theory]
    [InlineData(new[] { "var g: u9" }, "decl 1", 1)]
    [InlineData(new[] { "var a: u8", "var b: u8\nvar g: u9" }, "decl 2", 2)]
    public void An_error_in_a_declaration_names_the_declaration(string[] declarations, string file, int line)
    {
        var r = Bench.Measure(BenchVariant.Snippet("code 1", declarations, "a = 1"), null, []);
        Assert.Equal(file, r.Error!.File);
        Assert.Equal(line, r.Error.Line);
        Assert.Contains($"error: {file}, line {line}:", Bench.Text([r]));
    }

    [Fact]
    public void The_generated_lines_around_the_snippet_map_to_its_first_and_last_lines()
    {
        var v = BenchVariant.Snippet("code 1", ["var a: u8"], "a = 1\na += 1");
        Assert.Equal(("code 1", 1), v.Place(2)); // fn main() {
        Assert.Equal(("code 1", 2), v.Place(4));
        Assert.Equal(("code 1", 2), v.Place(5)); // }
        var r = Bench.Measure(BenchVariant.Snippet("code 1", ["var a: u8"], "a = 1\nif a == 1 {"), null, []);
        Assert.Equal("code 1", r.Error!.File);
        Assert.Contains("is not closed", r.Error.Message);
    }

    [Fact]
    public void Two_snippets_give_their_bytes_cycles_memory_and_results()
    {
        var for_ = Bench.Measure(BenchVariant.Snippet("code 1", Decl, "for i in 0..16 { a[i] = i }"), null, ["a"]);
        var while_ = Bench.Measure(BenchVariant.Snippet("code 2", Decl, "var i: u8 = 0; while i < 16 { a[i] = i; i += 1 }"), null, ["a"]);
        foreach (var r in new[] { for_, while_ })
        {
            Assert.Null(r.Error);
            Assert.True(r.Returned);
            Assert.Equal("main", r.Function);
            Assert.Equal(1, r.Calls);
            Assert.Equal(r.Cycles, r.CyclesPerCall);
            Assert.Equal("00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F", Assert.Single(r.Results).Value);
            Assert.Contains(r.Memory, v => v.Name == "a" && v.Size == 16);
        }
        // The for loop's counter is in X; the while loop's i is memory.
        Assert.DoesNotContain(for_.Memory, v => v.Function == "main");
        Assert.Contains(while_.Memory, v => v.Name == "main.i" && v.Address < 0x100);
        Assert.Equal(0, for_.ZeroPageBytes);
        Assert.Equal(1, while_.ZeroPageBytes);
        Assert.Equal(16, for_.OtherBytes);

        // The cycles are main's, as the run tool measures them.
        var program = TernCompiler.Compile(BenchVariant.Snippet("code 1", Decl, "for i in 0..16 { a[i] = i }").Source);
        var run = Runner.Run(program);
        Assert.Equal(run.MainCycles, for_.Cycles);
        Assert.Equal(program.Sections.Single(s => s.Name == "main").Size, for_.FunctionBytes);
        Assert.Equal(program.Image.Length, for_.ProgramBytes);
    }

    [Fact]
    public void A_function_is_measured_per_call()
    {
        var r = Bench.Measure(BenchVariant.Snippet("code 1",
            ["var n: u8", "fn bump(v: u8) { n += v }"], "for i in 0..10 { bump(i) }"), "bump", ["n"]);
        Assert.Null(r.Error);
        Assert.Equal("bump", r.Function);
        Assert.Equal(10, r.Calls);
        Assert.Equal(r.Cycles / 10, r.CyclesPerCall);
        Assert.Equal("45", Assert.Single(r.Results).Value);
        Assert.Contains(r.Memory, v => v.Name == "bump.v");
    }

    [Fact]
    public void An_error_in_a_snippet_is_placed_in_the_snippet()
    {
        var r = Bench.Measure(BenchVariant.Snippet("code 3", Decl, "a[0] = 1\na[1] = q"), null, []);
        Assert.NotNull(r.Error);
        Assert.Equal("code 3", r.Error!.File);
        Assert.Equal(2, r.Error.Line);
        Assert.Contains("'q' is not declared", r.Error.Message);
    }

    [Fact]
    public void A_function_with_no_code_of_its_own_is_an_error()
    {
        var r = Bench.Measure(BenchVariant.Snippet("code 1", ["var n: u8", "inline fn bump() { n += 1 }"], "bump()"), "bump", []);
        Assert.Contains("The function 'bump' has no code of its own", r.Error!.Message);
        Assert.Equal("Measure a function that calls it", r.Error.Suggestion);

        var uncalled = Bench.Measure(BenchVariant.Snippet("code 1", ["var n: u8", "fn bump() { n += 1 }"], "n = 1"), "bump", []);
        Assert.Contains("The function 'bump' has no code of its own", uncalled.Error!.Message);
    }

    [Fact]
    public void Two_variants_with_the_same_file_name_are_named_by_their_paths()
    {
        // Review M19 part 2, suggestion 5.
        string dir = Path.Combine(Path.GetTempPath(), "tern-bench-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var sub in new[] { "a", "b" })
            {
                Directory.CreateDirectory(Path.Combine(dir, sub));
                File.WriteAllText(Path.Combine(dir, sub, "check.tern"), "var n: u8\nfn main() { n = 1 }\n");
            }
            File.WriteAllText(Path.Combine(dir, "other.tern"), "var n: u8\nfn main() { n = 2 }\n");
            string a = Path.Combine(dir, "a", "check.tern"), b = Path.Combine(dir, "b", "check.tern"), other = Path.Combine(dir, "other.tern");
            var output = new StringWriter();
            Assert.Equal(0, Cli.Run(["bench", a, b, other], output, output));
            var rows = output.ToString().Split('\n').Skip(1).Take(3).Select(l => l.Split("  ")[0].Trim()).ToList();
            Assert.Equal([a, b, "other.tern"], rows);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void An_unknown_function_is_not_called_inline_or_uncalled()
    {
        // Review M19 part 2, finding 2.
        var r = Bench.Measure(BenchVariant.Snippet("code 1", ["var n: u8"], "n = 1"), "nosuch", []);
        Assert.Equal("There is no function 'nosuch' in the program.", r.Error!.Message);
        Assert.Null(r.Error.Suggestion);
    }

    [Fact]
    public void A_module_function_without_its_module_name_suggests_the_full_name()
    {
        // Review M19 part 2, finding 2: the agent wrote --fn update for ball.update.
        string dir = Path.Combine(Path.GetTempPath(), "tern-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "ball.tern"), "var x: u8\nfn update() { x += 1 }\n");
            string main = Path.Combine(dir, "check.tern");
            string source = "import ball\nfn main() { ball.update() }\n";
            File.WriteAllText(main, source);
            var r = Bench.Measure(new BenchVariant("check.tern", source, main), "update", []);
            Assert.Equal("There is no function 'update' in the program.", r.Error!.Message);
            Assert.Equal("Use 'ball.update': a module's function is named with its module", r.Error.Suggestion);
            Assert.Null(Bench.Measure(new BenchVariant("check.tern", source, main), "ball.update", []).Error);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_text_table()
    {
        var results = new[]
        {
            Bench.Measure(BenchVariant.Snippet("code 1", Decl, "for i in 0..16 { a[i] = i }"), null, ["a:2"]),
            Bench.Measure(BenchVariant.Snippet("code 2", Decl, "a[0] = 1"), null, ["a:2"]),
            Bench.Measure(BenchVariant.Snippet("code 3", Decl, "a[0] = q"), null, ["a:2"]),
        };
        var lines = Bench.Text(results).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal("variant   program  main bytes   calls     cycles  per call    zp  other", lines[0]);
        Assert.Matches(@"^code 1 +\d+ +\d+ +1 +\d+ +\d+ +0 +16$", lines[1]);
        Assert.Matches(@"^code 2 +\d+ +\d+ +1 +\d+ +\d+ +0 +16$", lines[2]);
        Assert.Equal("code 3    error: line 1:8: 'q' is not declared", lines[3]);
        Assert.StartsWith("Memory (zp: zero page bytes", lines[4]);
        Assert.Matches(@"^  code 1: a \$[0-9A-F]{4} \[u8; 16\]$", lines[5]);
        Assert.Equal("Results:", lines[7]);
        Assert.Equal("  code 1: a:2 = 00 01", lines[8]);
        Assert.Equal("  code 2: a:2 = 01 00", lines[9]);
        Assert.Equal("  The variants give different results.", lines[10]);
    }

    [Fact]
    public void A_run_that_does_not_return_says_why()
    {
        var r = Bench.Measure(BenchVariant.Snippet("code 1", Decl, "while true { a[0] += 1 }"), null, [], new RunOptions { MaxCycles = 5000 });
        Assert.False(r.Returned);
        Assert.Contains("(stopped after 5000 cycles; main had not returned)", Bench.Text([r]));
    }

    [Fact]
    public void The_json()
    {
        var results = new[]
        {
            Bench.Measure(BenchVariant.Snippet("code 1", Decl, "for i in 0..16 { a[i] = i }"), null, ["a:1"]),
            Bench.Measure(BenchVariant.Snippet("code 2", Decl, "a[0] = q"), null, []),
        };
        using var doc = JsonDocument.Parse(Bench.Json(results));
        var root = doc.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        var first = root.GetProperty("variants")[0];
        Assert.Equal("code 1", first.GetProperty("variant").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("error").ValueKind);
        Assert.Equal("main", first.GetProperty("function").GetString());
        Assert.Equal(results[0].Cycles, first.GetProperty("cycles").GetInt64());
        Assert.Equal(results[0].FunctionBytes, first.GetProperty("functionBytes").GetInt32());
        Assert.Equal(16, first.GetProperty("otherBytes").GetInt32());
        Assert.Equal("[u8; 16]", first.GetProperty("memory")[0].GetProperty("type").GetString());
        Assert.Equal("00", first.GetProperty("results")[0].GetProperty("value").GetString());
        var error = root.GetProperty("variants")[1].GetProperty("error");
        Assert.Equal(1, error.GetProperty("line").GetInt32());
        Assert.Contains("'q' is not declared", error.GetProperty("message").GetString());
    }

    [Fact]
    public void A_variant_with_an_index_error_fails_and_prints_it()
    {
        // M20 follow-up: a wrong index gives misleading cycles, so the variant fails.
        var good = Bench.Measure(BenchVariant.Snippet("code 1", Decl, "for i in 0..16 { a[i] = i }"), null, []);
        var bad = Bench.Measure(BenchVariant.Snippet("code 2", Decl, "var n: u8 = 17\nfor i in 0..n {\n  a[i] = i\n}"), null, []);
        Assert.Empty(good.IndexErrors);
        var e = Assert.Single(bad.IndexErrors);
        Assert.Equal("code 2", e.File);
        Assert.Equal(3, e.Line);
        Assert.Equal("Index 16 is outside 'a', which has 16 elements", e.Message);
        string text = Bench.Text([good, bad]);
        Assert.Contains("Index check (these variants fail; the emulator found these):\n  code 2:\n    code 2:3: Index 16 is outside 'a', which has 16 elements\n        a[i] = i", text.Replace("\r", ""));
        using var doc = JsonDocument.Parse(Bench.Json([good, bad]));
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(3, doc.RootElement.GetProperty("variants")[1].GetProperty("indexErrors")[0].GetProperty("line").GetInt32());

        var output = new StringWriter();
        Assert.Equal(1, Cli.Run(["bench", "--decl", Decl[0], "--code", "for i in 0..16 { a[i] = i }", "--code", "var n: u8 = 17; for i in 0..n { a[i] = i }"], output, output));
        Assert.Equal(0, Cli.Run(["bench", "--decl", Decl[0], "--code", "for i in 0..16 { a[i] = i }"], output, output));
    }

    [Fact]
    public void Set_writes_bytes_before_each_variant_runs()
    {
        var output = new StringWriter();
        Assert.Equal(0, Cli.Run(["bench", "--decl", "var joy: u8 @ $DC00\nvar seen: u8", "--code", "seen = joy", "--code", "seen = joy & $FF", "--set", "$DC00=$7B", "--dump", "seen"], output, output));
        Assert.Contains("code 1: seen = 123", output.ToString());
        Assert.Contains("code 2: seen = 123", output.ToString());
    }
}
