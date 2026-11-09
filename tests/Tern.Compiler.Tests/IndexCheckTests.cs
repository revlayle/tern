using Tern.Compiler.Tools;

namespace Tern.Compiler.Tests;

/// <summary>
/// M20: the emulator reports an array index outside its array and an <c>indexed match</c> value
/// outside its span, with the source line (spec 3.2, 5.2, 14.4). The program has no check.
/// </summary>
public class IndexCheckTests
{
    private static IndexError Single(string source)
    {
        var run = Run.Program(source, checkIndexes: false);
        return Assert.Single(run.IndexErrors);
    }

    [Fact]
    public void An_index_past_the_end_is_reported_with_its_line()
    {
        var e = Single("""
            var a: [u8; 10]
            var i: u8 = 10
            fn main() {
                a[i] = 1
            }
            """);
        Assert.Equal(("program.tern", 4, "a[i] = 1"), (e.File, e.Line, e.Source));
        Assert.Equal("Index 10 is outside 'a', which has 10 elements", e.Message);
        Assert.Equal("program.tern:4: Index 10 is outside 'a', which has 10 elements\n    a[i] = 1", e.ToString());
    }

    [Fact]
    public void A_test_run_fails_on_an_index_error()
    {
        var e = Assert.Throws<Xunit.Sdk.XunitException>(() => Run.Program("var a: [u8; 4]\nvar i: u8 = 200\nvar v: u8\nfn main() { v = a[i] }"));
        Assert.Contains("Index 200 is outside 'a', which has 4 elements", e.Message);
    }

    [Fact]
    public void A_folded_offset_is_checked_at_the_element_it_reaches()
    {
        // a[i - 1] is lda a-1,x and a[i + 1] lda a+1,x (spec 3.2); i = 0 reaches index 255, i = 9 index 10.
        var run = Run.Program("""
            var a: [u8; 10]
            var b: [u8; 10]
            var c: [u8; 10]
            fn main() {
                for i in 0..10 {
                    b[i] = a[i - 1]
                    c[i] = a[i + 1]
                }
            }
            """, checkIndexes: false);
        Assert.Contains("lda a-1,x", run.Result.Listing);
        Assert.Contains("lda a+1,x", run.Result.Listing);
        Assert.Equal(["6: Index 255 is outside 'a', which has 10 elements", "7: Index 10 is outside 'a', which has 10 elements"],
            run.IndexErrors.Select(e => $"{e.Line}: {e.Message}"));
        Assert.All(run.IndexErrors, e => Assert.Equal(1, e.Count));
    }

    [Fact]
    public void Each_instruction_is_reported_once_with_a_count()
    {
        var e = Single("""
            var a: [u8; 4]
            var n: u8
            var last: u8 = 10
            fn main() {
                for i in 0..last { n += a[i] }
            }
            """);
        Assert.Equal(6, e.Count);
        Assert.EndsWith("(6 times)\n    for i in 0..last { n += a[i] }", e.ToString());
    }

    [Theory]
    // Both tables of a split u16 array.
    [InlineData("var w: [u16; 4]\nvar i: u8 = 4\nfn main() { w[i] = 1000 }", "Index 4 is outside 'w', which has 4 elements", 1)]
    // A struct array field: one table per field, so index 4 of hp is not x of the next table.
    [InlineData("struct E { x: u8, hp: u8 }\nvar e: [E; 4]\nvar i: u8 = 4\nfn main() { e[i].x = 1 }", "Index 4 is outside 'e.x', which has 4 elements", 1)]
    // A linear u16 array: the index is doubled.
    [InlineData("linear var p: [u16; 4]\nvar i: u8 = 5\nvar w: u16\nfn main() { w = p[i] }", "Index 5 is outside 'p', which has 4 elements", 1)]
    // A scaled index that wraps is checked before the shift: 130 * 2 = 260 would land on p[2] (review M20, finding 1).
    [InlineData("linear var pts: [u16; 10]\nvar j: u8 = 130\nvar out: u16\nfn main() { out = pts[j] }", "Index 130 is outside 'pts', which has 10 elements", 1)]
    [InlineData("struct E { x: u8, y: u8, hp: u8, k: u8 }\nlinear var es: [E; 10]\nvar j: u8 = 66\nvar out: u8\nfn main() { out = es[j].hp }", "Index 66 is outside 'es', which has 10 elements", 1)]
    [InlineData("linear var pts: [u16; 10]\nvar first: u8 = 125\nvar out: u16\nfn main() { for j in first..=255 using X { out = pts[j] } }", "Index 125 is outside 'pts', which has 10 elements", 1)]
    // A linear struct array of 4-byte records.
    [InlineData("struct R { a: u8, b: u8, c: u8, d: u8 }\nlinear var r: [R; 3]\nvar i: u8 = 3\nfn main() { r[i].c = 1 }", "Index 3 is outside 'r', which has 3 elements", 1)]
    // An indexed struct array: the offset table is checked first (the record access then uses whatever byte follows it).
    [InlineData("struct R { a: u8, b: u8, c: u8 }\nindexed var r: [R; 3]\nvar i: u8 = 3\nfn main() { r[i].b = 1 }", "Index 3 is outside 'r.offsets', which has 3 elements", 0)]
    // A local array.
    [InlineData("fn f(i: u8) {\n    var t: [u8; 3]\n    t[i] = 0\n}\nfn main() { f(3) }", "Index 3 is outside 'f.t', which has 3 elements", 1)]
    public void Every_kind_of_array_is_checked(string source, string message, int errors)
    {
        var run = Run.Program(source, checkIndexes: false);
        if (errors > 0) Assert.Equal(errors, run.IndexErrors.Count);
        else Assert.NotEmpty(run.IndexErrors);
        Assert.Equal(message, run.IndexErrors[0].Message);
    }

    [Fact]
    public void An_indexed_match_value_outside_its_span_is_reported_and_stops_the_run()
    {
        var run = Run.Program("""
            var v: u8 = 7
            var r: u8
            fn main() {
                indexed match v {
                    2 => r = 1
                    3, 4 => r = 2
                    5 => r = 3
                }
            }
            """, checkIndexes: false);
        var e = Assert.Single(run.IndexErrors);
        Assert.Equal((4, "The 'indexed match' value is 7, outside its patterns 2..5"), (e.Line, e.Message));
        Assert.Equal(0, run.U8("r"));
    }

    [Fact]
    public void An_i8_indexed_match_shows_the_signed_value()
    {
        var e = Single("""
            var v: i8 = -6
            var r: u8
            fn main() {
                indexed match v {
                    -4 => r = 1
                    -3..=0 => r = 2
                    _ => r = 3
                }
            }
            """);
        Assert.Equal("The 'indexed match' value is -6, outside its patterns -4..0", e.Message);
    }

    [Fact]
    public void A_correct_program_with_wrapping_arithmetic_is_quiet()
    {
        // A 256-element ring buffer relies on the u8 index wrapping (spec 3.2), and values wrap (3.1).
        var run = Run.Program("""
            var ring: [u8; 256]
            var small: [u8; 8]
            var head: u8 = 250
            var x: u8 = 250
            var s: i8 = 127
            var total: u8
            fn main() {
                for _ in 0..20 {
                    ring[head + 1] = head
                    head += 1
                    x += 10
                    s += 1
                    small[x & 7] = x
                }
                for i in 1..8 { total += small[i - 1] }
                indexed match x & 3 {
                    0 => total += 1
                    1 => total += 2
                    2 => total += 3
                    3 => total += 4
                }
            }
            """);
        Assert.Empty(run.IndexErrors);
        Assert.Equal(14, run.U8("head"));
    }

    [Fact]
    public void Pointer_accesses_and_asm_are_not_checked()
    {
        var run = Run.Program("""
            var a: [u8; 4]
            var b: [u8; 4]
            var p: ptr u8
            fn main() {
                p = addr(a)
                p[6] = 1
                asm {
                    ldx #5
                    sta a,x
                }
            }
            """);
        Assert.Empty(run.IndexErrors);
    }

    [Fact]
    public void Tern_run_prints_the_errors_and_exits_with_3()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tern-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "bad.tern");
            File.WriteAllText(file, "var a: [u8; 10]\nvar i: u8 = 12\n\nfn main() {\n    a[i] = 1\n}\n");
            var output = new StringWriter();
            Assert.Equal(3, Cli.Run(["run", file], output, output));
            Assert.Contains("Index check (the program has no check; the emulator found these):\n  bad.tern:5: Index 12 is outside 'a', which has 10 elements\n      a[i] = 1\n",
                output.ToString().Replace("\r\n", "\n"));

            output = new StringWriter();
            Assert.Equal(3, Cli.Run(["run", file, "--json"], output, output));
            Assert.Contains("\"message\": \"Index 12 is outside 'a', which has 10 elements\"", output.ToString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_check_changes_no_code()
    {
        // The same program compiles to the same bytes; the checks are only in the source map.
        const string source = "var a: [u8; 10]\nvar i: u8\nfn main() { a[i] = 1 }";
        var r = TernCompiler.Compile(source);
        Assert.Contains(r.Instructions, i => i.Index is not null);
        Assert.Equal(r.Image, TernCompiler.Compile(source).Image);
    }
}
