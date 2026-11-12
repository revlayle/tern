using Tern.Compiler.Syntax;
using Tern.Compiler.Tools;

namespace Tern.Compiler.Tests;

/// <summary>
/// Several errors per compile (spec 13): the parser goes on at the next statement or declaration, the
/// checker reports at most one error per statement or declaration, and a name whose declaration failed
/// accepts every use. Errors are sorted by place (decision 108): the main file, then modules in the order
/// first imported; by line and column within a file.
/// </summary>
public sealed class SeveralErrorsTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tern");

    public void Dispose() => _dir.Delete(recursive: true);

    /// <summary>Every error of the compile as "line: message", without the fix sentence (with the file name when it is not main.tern).</summary>
    private List<string> Errors(string source, int? maxErrors = null, params (string Name, string Text)[] modules)
    {
        foreach (var (name, text) in modules) File.WriteAllText(Path.Combine(_dir.FullName, name + ".tern"), text);
        var e = Assert.Throws<CompileError>(() =>
            TernCompiler.Compile(source, sourcePath: Path.Combine(_dir.FullName, "main.tern"), maxErrors: maxErrors));
        Assert.Same(e, e.All[0]);
        return e.All.Select(x => (x.Pos.File is { } f && Path.GetFileName(f) != "main.tern" ? Path.GetFileName(f) + ":" : "") + $"{x.Pos.Line}: {Diagnostic.Split(x.Description).Message.TrimEnd('.')}").ToList();
    }

    [Fact]
    public void Errors_in_several_statements_functions_and_modules_are_each_reported_once()
    {
        var errors = Errors("""
            import util
            var b: u8
            var w: u16
            fn first() {
                b = w
                b = true
                b = 1
            }
            fn second() -> u8 {
                missing()
                return w
            }
            fn main() {
                first()
                b = second()
                util.go()
            }
            """, modules: ("util", """
            var x: u8
            fn go() {
                x = 300
                x = -1
            }
            """));
        Assert.Equal([
            "5: Expected u8, found u16",
            "6: Expected u8, found bool",
            "10: 'missing' is not a function",
            "11: Expected u8, found u16",
            "util.tern:3: 300 does not fit in u8 (0 to 255)",
            "util.tern:4: -1 does not fit in u8 (0 to 255)",
        ], errors);
    }

    /// <summary>
    /// Errors found out of source order: the checker declares functions before globals and checks bodies last,
    /// the lexer and parser run before the checker, and modules are loaded while the main file is parsed.
    /// </summary>
    private List<string> OutOfOrderErrors(int? maxErrors = null) => Errors("""
        import zeta
        import alpha
        var g: Unknwn
        fn f(p: Nope) { }
        fn main() {
            g = 0b12
            var x: u8 = true
            x = )
        }
        """, maxErrors, ("zeta", "fn z() {\n    var a: u8 = 300\n    a = )\n}\n"), ("alpha", "var q: u8 = 'xy'\nvar r: Bad\n"));

    [Fact]
    public void Errors_are_sorted_by_place_main_file_first_then_modules_in_import_order()
    {
        Assert.Equal([
            "3: Unknown type 'Unknwn'",
            "4: Unknown type 'Nope'",
            "6: Invalid digit '2' in number 0b12",
            "7: Expected u8, found bool",
            "8: Expected an expression, found ')'",
            "zeta.tern:2: 300 does not fit in u8 (0 to 255)",
            "zeta.tern:3: Expected an expression, found ')'",
            "alpha.tern:1: A character literal holds exactly one character",
            "alpha.tern:2: Unknown type 'Bad'",
        ], OutOfOrderErrors());
    }

    [Fact]
    public void Max_errors_keeps_the_first_after_sorting()
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("""
            var g: Unknwn
            fn f(p: Nope) { }
            fn main() { g = )
            }
            """, maxErrors: 2));
        Assert.Equal([1, 2], e.All.Select(x => x.Pos.Line));
        Assert.True(e.MoreNotReported);
        Assert.Equal(["3: Unknown type 'Unknwn'", "4: Unknown type 'Nope'"], OutOfOrderErrors(maxErrors: 2));
    }

    [Fact]
    public void A_misspelled_type_gives_one_error_and_its_uses_none()
    {
        var errors = Errors("""
            struct Enemy { x: u8, hp: u8 }
            var e: Enmy
            var f: [Enmy; 4]
            fn hit(p: ptr Enmy) { p.hp -= 1 }
            fn main() {
                e.x = 1
                e.hp = e.x + 2
                f[1].hp = 0
                hit(addr(e))
            }
            """);
        Assert.Equal([
            "2: Unknown type 'Enmy'",
            "3: Unknown type 'Enmy'",
            "4: Unknown type 'Enmy'",
        ], errors);
    }

    [Fact]
    public void A_misspelled_function_name_in_its_declaration_gives_one_error()
    {
        // The calls use the right name: it is reported at the first call, not at every call.
        var errors = Errors("""
            var n: u8
            fn updte() { n += 1 }
            fn main() {
                update()
                update()
                if n > 3 { update() }
            }
            """);
        Assert.Equal(["4: 'update' is not a function"], errors);
    }

    [Fact]
    public void A_name_that_is_not_declared_is_reported_once()
    {
        var errors = Errors("""
            var b: u8
            fn main() {
                b = cnt
                cnt = 2
            }
            fn other() { b = cnt + 1 }
            """);
        Assert.Equal(["3: 'cnt' is not declared"], errors);
    }

    [Fact]
    public void A_bad_initializer_gives_one_error_and_the_uses_none()
    {
        var errors = Errors("""
            var x: u8 = 300
            const K = 1 / 0
            const L = K + 1
            fn main() {
                var y: u8 = x + K
                x = y + L
                var z: u8 = true
                z += 1
            }
            """);
        Assert.Equal([
            "1: 300 does not fit in u8 (0 to 255)",
            "2: Division by zero",
            "7: Expected u8, found bool",
        ], errors);
    }

    [Fact]
    public void A_syntax_error_followed_by_a_type_error_reports_both()
    {
        var errors = Errors("""
            var b: u8
            fn main() {
                b = )
                b = true
            }
            """);
        Assert.Equal(["3: Expected an expression, found ')'", "4: Expected u8, found bool"], errors);
    }

    [Fact]
    public void Syntax_errors_in_several_statements_and_declarations()
    {
        var errors = Errors("""
            var a: u8 = )
            var b: u8
            fn main() {
                b = )
                b = 2 ]
                a = 3
            }
            fn other() {
                if b == { }
                b = 2
            }
            const C = 3 +
            """);
        Assert.Equal(["1", "4", "5", "9", "12"], errors.Select(e => e.Split(':')[0]));
    }

    [Fact]
    public void A_declaration_with_a_word_from_another_language_breaks_its_name()
    {
        // Review M23, finding 1: pub fn and let gave an error at every use of the name.
        var errors = Errors("""
            var g: u8 = 0
            pub fn helper() {
                g = 1
            }
            let count: u8 = 0
            fn main() {
                helper()
                count = 2
                let n = 5
                n = 6
                g = 300
            }
            """);
        Assert.Equal(["2", "5", "9", "11"], errors.Select(e => e.Split(':')[0]));
        Assert.Equal("11: 300 does not fit in u8 (0 to 255)", errors[3]);
    }

    [Fact]
    public void After_an_unclosed_parenthesis_the_next_line_at_the_statement_column_is_checked()
    {
        // Review M23, finding 2: the lexer gives no line breaks inside ( ), so the skip ran to the '}'.
        var errors = Errors("""
            var g: u8
            fn main() {
                g = (1 +
                g = 2
                g = 500
                f(1,
                  2
                g = true
            }
            """);
        Assert.Equal([
            "4: Expected ')', found '='",
            "5: 500 does not fit in u8 (0 to 255)",
            "8: Expected ')', found 'g'",
            "8: Expected u8, found bool",
        ], errors);
    }

    [Fact]
    public void A_semicolon_in_brackets_does_not_end_the_skipped_statement()
    {
        var errors = Errors("""
            fn main() {
                data t: [u8; 2] = [1, 2]
                var x: u8 = true
            }
            """);
        Assert.Equal(["2: 'data' must be declared outside functions", "3: Expected u8, found bool"], errors);
    }

    [Fact]
    public void A_lexer_error_drops_its_statement_and_parsing_goes_on()
    {
        var errors = Errors("""
            var b: u8
            fn main() {
                b = 0b102
                b = 'xy'
                b++
                b = true
            }
            """);
        Assert.Equal([
            "3: Invalid digit '2' in number 0b102",
            "4: A character literal holds exactly one character",
            "5: Tern has no '++'",
            "6: Expected u8, found bool",
        ], errors);
    }

    [Fact]
    public void A_line_starting_with_an_operator_drops_the_statement_before()
    {
        var errors = Errors("""
            var a: bool
            var b: u8
            fn main() {
                a = b
                    == 4
            }
            """);
        Assert.Single(errors);
        Assert.StartsWith("5: A line cannot start with '=='", errors[0]);
    }

    [Fact]
    public void An_unclosed_block_is_reported_once()
    {
        var errors = Errors("""
            fn main() {
                if true {
                    match 1 {
            """);
        Assert.Equal(["3: 'match' is not closed with '}'"], errors);
    }

    [Fact]
    public void A_declaration_swallowed_by_a_syntax_error_gives_no_missing_main_error()
    {
        Assert.Equal(["3: Expected a field 'name: type', found 'fn'"], Errors("struct S {\n    a: u8\nfn main() { }"));
    }

    [Fact]
    public void A_function_with_a_bad_signature_is_not_checked_and_its_calls_add_no_errors()
    {
        var errors = Errors("""
            var b: u8
            fn f(x: u9) -> u8 { return x }
            fn g(a: u8 using Q) { }
            fn main() {
                b = f(1)
                g(2, 3)
                b = true
            }
            """);
        Assert.Equal(3, errors.Count);
        Assert.Equal("2: Unknown type 'u9'", errors[0]);
        Assert.StartsWith("3: ", errors[1]);   // a syntax error
        Assert.Equal("7: Expected u8, found bool", errors[2]);
    }

    [Fact]
    public void A_local_whose_declaration_failed_accepts_every_use()
    {
        var errors = Errors("""
            var b: u8
            fn main() {
                var x: u8 = true
                var y: u8 = )
                b = x + y
                x = 3
                b = true
            }
            """);
        Assert.Equal(["3: Expected u8, found bool", "4: Expected an expression, found ')'", "7: Expected u8, found bool"], errors);
    }

    [Fact]
    public void A_local_with_a_written_type_keeps_it_when_its_initial_value_fails()
    {
        // Review M23, finding 3: k = 999 was not reported, since k was broken with e.
        var errors = Errors("""
            struct Enemy { x: u8, hp: u8 }
            var e: Enmy
            fn main() {
                var k: u8 = e.x
                k = 999
                var j = e.hp
                j = 999
            }
            """);
        Assert.Equal(["2: Unknown type 'Enmy'", "5: 999 does not fit in u8 (0 to 255)"], errors);
    }

    [Fact]
    public void A_const_fn_with_a_syntax_error_is_dropped_and_its_calls_add_no_errors()
    {
        var errors = Errors("""
            const fn twice(n: int) -> int {
                return n *
            }
            const T = twice(3)
            data d: [u8; 2] = [twice(1), 2]
            fn main() { }
            """);
        Assert.Single(errors);
        Assert.StartsWith("3: ", errors[0]);
    }

    [Fact]
    public void A_module_that_cannot_be_loaded_is_reported_once_and_its_uses_add_no_errors()
    {
        var errors = Errors("""
            import nothere
            var b: u8
            fn main() {
                nothere.go()
                b = nothere.LIMIT
                b = true
            }
            """);
        Assert.Equal(2, errors.Count);
        Assert.StartsWith("1: Cannot find module 'nothere'", errors[0]);
        Assert.Equal("6: Expected u8, found bool", errors[1]);
    }

    [Fact]
    public void A_missing_import_is_reported_once()
    {
        var errors = Errors("""
            fn main() {
                text.put('A')
                text.put('B')
            }
            """);
        Assert.Equal(["2: 'text' is not declared"], errors);
    }

    [Fact]
    public void A_bad_module_parameter_value_gives_one_error()
    {
        var errors = Errors("import shapes(COUNT = 300)\nfn main() { shapes.go() }", modules: ("shapes", """
            module shapes(COUNT: u8 = 8, WIDE: bool = false)
            var xs: [u8; COUNT]
            fn go() { if WIDE { xs[0] = 1 } }
            """));
        Assert.Equal(["1: 300 does not fit in u8 (0 to 255)"], errors);
    }

    [Fact]
    public void A_memory_block_with_an_error_gives_no_errors_for_its_regions()
    {
        var errors = Errors("""
            memory { $C000..=$C000 + SZ region buffers }
            var buf: [u8; 16] @ buffers
            fn main() { buf[0] = 1 }
            """);
        Assert.Equal(["1: 'SZ' is not declared"], errors);
    }

    private static string ManyErrors(int count) =>
        "var b: u8\nfn main() {\n" + string.Concat(Enumerable.Range(0, count).Select(_ => "    b = true\n")) + "}\n";

    [Fact]
    public void Twenty_five_errors_are_all_reported()
    {
        var errors = Errors(ManyErrors(25));
        Assert.Equal(Enumerable.Range(3, 25).Select(l => $"{l}: Expected u8, found bool"), errors);
    }

    [Fact]
    public void Max_errors_stops_after_n_and_says_more_were_not_reported()
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(ManyErrors(25), maxErrors: 5));
        Assert.Equal(5, e.All.Count);
        Assert.True(e.MoreNotReported);

        // Exactly n errors: nothing more to say.
        e = Assert.Throws<CompileError>(() => TernCompiler.Compile(ManyErrors(5), maxErrors: 5));
        Assert.Equal(5, e.All.Count);
        Assert.False(e.MoreNotReported);
    }

    [Fact]
    public void Errors_of_the_call_graph_come_alone_after_the_checks()
    {
        // Recursion is found in the call graph, which needs a program without errors.
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("fn a() { b() }\nfn b() { a() }\nfn main() { a() }"));
        Assert.Single(e.All);
        Assert.Contains("Recursion is not allowed", e.Description);
        e = Assert.Throws<CompileError>(() => TernCompiler.Compile("var x: u8\nfn a() { b() }\nfn b() { a() }\nfn main() {\n a()\n x = true\n}"));
        Assert.Equal(["Expected u8, found bool"], e.All.Select(x => x.Description));
    }

    // ---------- The tools ----------

    private string File3Errors()
    {
        var path = Path.Combine(_dir.FullName, "three.tern");
        File.WriteAllText(path, "var b: u8\nfn main() {\n    b = true\n    b = 300\n    b = )\n}\n");
        return path;
    }

    private static (int Code, string Output, string Error) Tern(params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        int code = Cli.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Tern_prints_every_error_then_their_number()
    {
        var path = File3Errors();
        var (code, _, error) = Tern(path);
        Assert.Equal(1, code);
        Assert.Equal([
            $"{path}:3:9: error: Expected u8, found bool",
            $"{path}:4:9: error: 300 does not fit in u8 (0 to 255). Use a value in that range, or a wider type",
            $"{path}:5:9: error: Expected an expression, found ')'",
            "3 errors",
        ], error.TrimEnd().Split(Environment.NewLine));

        (_, _, error) = Tern("--max-errors", "2", path);
        Assert.EndsWith("2 errors; more errors were not reported (--max-errors 2)", error.TrimEnd());
        Assert.Equal(3, error.TrimEnd().Split(Environment.NewLine).Length);
    }

    [Fact]
    public void Tern_json_lists_every_error_in_diagnostics()
    {
        var (code, output, _) = Tern("--json", File3Errors());
        Assert.Equal(1, code);
        using var json = System.Text.Json.JsonDocument.Parse(output);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal([3, 4, 5], json.RootElement.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("line").GetInt32()));
    }

    [Fact]
    public void Tern_run_and_bench_report_every_error()
    {
        var path = File3Errors();
        var (code, _, error) = Tern("run", path, "--max-errors", "5");
        Assert.Equal(1, code);
        Assert.EndsWith("3 errors", error.TrimEnd());

        (code, var output, _) = Tern("bench", path, "--code", "var x: u8 = true; var y: u8 = 300");
        Assert.Equal(1, code);
        var lines = output.Split(Environment.NewLine);
        Assert.Contains(lines, l => l.StartsWith("three.tern") && l.Contains("line 3:9: Expected u8, found bool"));
        Assert.Contains(lines, l => l.TrimStart().StartsWith("error: ") && l.Contains("line 4:9: 300 does not fit in u8"));
        Assert.Contains(lines, l => l.Trim() == "3 errors");
        Assert.Contains(lines, l => l.Trim() == "2 errors");

        (_, output, _) = Tern("bench", "--json", "--max-errors", "1", path);
        using var json = System.Text.Json.JsonDocument.Parse(output);
        var variant = json.RootElement.GetProperty("variants")[0];
        Assert.Equal(1, variant.GetProperty("errors").GetArrayLength());
        Assert.Equal(3, variant.GetProperty("error").GetProperty("line").GetInt32());
    }

    [Fact]
    public void An_allocation_error_is_reported_alone()
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("""
            var a: [u8; 200] @ zp
            var b: [u8; 200] @ zp
            fn main() { a[0] = b[0] }
            """));
        Assert.Single(e.All);
    }
}
