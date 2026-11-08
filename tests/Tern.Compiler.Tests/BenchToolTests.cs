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
        Assert.Equal(4, v.FirstSnippetLine);
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
        Assert.Contains("There is no function 'bump' with code of its own", r.Error!.Message);
        Assert.Equal("Measure a function that calls it", r.Error.Suggestion);
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
}
