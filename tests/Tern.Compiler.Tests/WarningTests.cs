using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// Warnings for code that does nothing (spec 13): an unused local, declaration or import, and a statement
/// that is never reached. Names starting with '_', main, exports, fixed-address globals, parameters, public
/// module declarations and library modules are never warned. Reported only when the checks found no error.
/// </summary>
public sealed class WarningTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tern");

    public void Dispose() => _dir.Delete(recursive: true);

    private string MainPath => Path.Combine(_dir.FullName, "main.tern");

    private CompileResult Compile(string source, WarningMode mode = WarningMode.Report, params (string Name, string Text)[] modules)
    {
        foreach (var (name, text) in modules) File.WriteAllText(Path.Combine(_dir.FullName, name + ".tern"), text);
        return TernCompiler.Compile(source, sourcePath: MainPath, warnings: mode);
    }

    private static string Line(CompileError w) =>
        (w.Pos.File is { } f && Path.GetFileName(f) != "main.tern" ? Path.GetFileName(f) + ":" : "") + $"{w.Pos.Line}: {w.Description}";

    /// <summary>Every warning as "line: text" (with the file name when it is not main.tern).</summary>
    private List<string> Warnings(string source, params (string Name, string Text)[] modules) =>
        Compile(source, WarningMode.Report, modules).Warnings.Select(Line).ToList();

    // ---------- Unused locals ----------

    [Fact]
    public void An_unused_local_is_warned()
    {
        Assert.Equal(["2: 'temp' is never used. Remove it, or start its name with '_' to keep it"], Warnings("""
            fn main() {
                var temp: u8 = 3
            }
            """));
    }

    [Fact]
    public void A_local_only_written_is_unused()
    {
        Assert.Equal([
            "2: 'count' is never used. Remove it, or start its name with '_' to keep it",
            "3: 'buf' is never used. Remove it, or start its name with '_' to keep it",
        ], Warnings("""
            fn main() {
                var count: u8 = 0
                var buf: [u8; 4]
                count = 5
                count += 1
                buf[2] = 7
                buf[3] += 1
            }
            """));
    }

    [Fact]
    public void A_local_read_anywhere_is_used()
    {
        Assert.Empty(Warnings("""
            var out: u8
            fn main() {
                var a: u8 = 1
                var b: u8 = 2
                var c: u8 = 3
                var p: ptr u8 = $C000 as ptr u8
                var buf: [u8; 4]
                const N = 2
                out = a
                if b == 2 { out = 0 }
                p[0] = 1
                buf[0] = c
                out = buf[N]
            }
            """));
    }

    [Fact]
    public void A_for_variable_never_read_is_warned_with_the_underscore_fix()
    {
        Assert.Equal(["3: 'i' is never used. Write '_' as the loop variable"], Warnings("""
            var n: u8
            fn main() {
                for i in 0..10 { n += 1 }
                for _ in 0..10 { n += 1 }
                for j in 0..10 { n += j }
            }
            """));
    }

    [Fact]
    public void An_unused_local_constant_and_a_const_fn_local_are_warned()
    {
        Assert.Equal([
            "2: 'unused' is never used. Remove it, or start its name with '_' to keep it",
            "6: 'K' is never used. Remove it, or start its name with '_' to keep it",
        ], Warnings("""
            const fn twice(x: int) -> int {
                var unused = 5
                return x * 2
            }
            fn main() {
                const K = twice(3)
            }
            """));
    }

    [Fact]
    public void An_asm_fns_var_line_and_a_const_fns_code_after_return_are_warned()
    {
        Assert.Equal([
            "4: 'spare' is never used. Remove it, or start its name with '_' to keep it",
            "11: 'y' is never used. Remove it, or start its name with '_' to keep it",
            "11: This statement is never reached, since the 'return' on line 10 ends the function. Remove it",
        ], Warnings("""
            var out: u8
            asm fn f(v: u8 using A) -> u8 {
                var t: u8
                var spare: u8
                sta t
                adc t
                rts
            }
            const fn g(x: int) -> int {
                return x
                var y = 1
            }
            const K = g(2)
            fn main() { out = f(K) }
            """));
    }

    [Fact]
    public void A_name_used_only_in_assembly_is_used()
    {
        Assert.Empty(Warnings("""
            var screen_color: u8
            fn main() {
                var mask: u8 = 3
                asm {
                    lda mask
                    sta screen_color
                }
            }
            """));
    }

    [Fact]
    public void A_name_starting_with_underscore_is_kept()
    {
        Assert.Empty(Warnings("""
            var _spare: u8
            fn _dump() { }
            fn main() {
                var _temp: u8 = 1
            }
            """));
    }

    // ---------- Unused declarations ----------

    [Fact]
    public void Unused_declarations_of_the_main_file_are_warned()
    {
        Assert.Equal([
            "1: 'LIMIT' is never used. Remove it, or start its name with '_' to keep it",
            "2: 'score' is never used. Remove it, or start its name with '_' to keep it",
            "3: 'TABLE' is never used. Remove it, or start its name with '_' to keep it",
            "4: 'Point' is never used. Remove it, or start its name with '_' to keep it",
            "5: 'helper' is never used. Remove it, or start its name with '_' to keep it",
            "6: 'gen' is never used. Remove it, or start its name with '_' to keep it",
        ], Warnings("""
            const LIMIT = 3
            var score: u8
            data TABLE: [u8; 2] = [1, 2]
            struct Point { x: u8 }
            fn helper() { }
            const fn gen() -> int { return 1 }
            fn main() { }
            """));
    }

    [Fact]
    public void Declarations_used_anywhere_are_not_warned()
    {
        Assert.Empty(Warnings("""
            const SIZE = 4
            const fn cell(i: int) -> int { return i * SIZE }
            data TABLE: [u8; 4] = [cell(i) for i in 0..4]
            struct Point { x: u8 }
            var p: Point
            var where_: u16 = addr(TABLE)
            fn helper() -> u8 { return TABLE[1] }
            fn main() {
                p.x = helper()
                where_ += 1
            }
            """));
    }

    [Fact]
    public void A_constant_used_only_in_a_const_fn_never_called_is_not_warned()
    {
        // The const fn itself is kept with '_'; what it names counts as used (spec 13).
        Assert.Empty(Warnings("""
            const SIZE = 4
            const fn _cell(i: int) -> int { return i * SIZE }
            fn main() { }
            """));
    }

    [Fact]
    public void Fixed_address_globals_main_and_exports_are_not_warned()
    {
        Assert.Empty(Warnings("""
            var border: u8 @ $D020
            var spare: u8 @ zp
            fn main() { }
            """));
        Assert.Empty(Warnings("""
            export start @ $0900
            fn start() { }
            fn main() { }
            """));
    }

    [Fact]
    public void A_function_used_only_by_an_unused_function_is_warned_after_removing_that_one()
    {
        Assert.Equal(["2: 'outer' is never used. Remove it, or start its name with '_' to keep it"], Warnings("""
            fn inner() { }
            fn outer() { inner() }
            fn main() { }
            """));
        Assert.Equal(["1: 'inner' is never used. Remove it, or start its name with '_' to keep it"], Warnings("""
            fn inner() { }
            fn main() { }
            """));
    }

    [Fact]
    public void Private_module_declarations_are_warned_and_public_ones_are_not()
    {
        Assert.Equal(["util.tern:2: 'hidden' is never used. Remove it, or start its name with '_' to keep it"], Warnings("""
            import util
            fn main() { util.go() }
            """, ("util", """
            fn go() { }
            private fn hidden() { }
            fn spare() { }
            private var used: u8
            fn set() { used = 1 }
            """)));
    }

    [Fact]
    public void Parameters_are_not_warned()
    {
        Assert.Empty(Warnings("""
            fn f(a: u8, b: u8 using X) { }
            fn main() { f(1, 2) }
            """));
    }

    [Fact]
    public void A_constant_used_only_in_a_module_parameter_value_is_used()
    {
        Assert.Empty(Warnings("""
            import util(COUNT = N)
            const N = 3
            fn main() { util.go() }
            """, ("util", """
            module util(COUNT: u8 = 1)
            var n: u8
            fn go() { n = COUNT }
            """)));
    }

    [Fact]
    public void Library_modules_are_never_warned()
    {
        // The same module is warned about as the program's own, and not as a library module.
        var lib = Directory.CreateDirectory(Path.Combine(_dir.FullName, "lib"));
        var module = """
            private var spare: u8
            fn go() { var unused: u8 = 1 }
            """;
        File.WriteAllText(Path.Combine(lib.FullName, "util.tern"), module);
        const string program = "import util\nfn main() { util.go() }\n";
        Assert.Empty(TernCompiler.Compile(program, sourcePath: MainPath, libraryDirectory: lib.FullName).Warnings);
        Assert.Equal(2, TernCompiler.Compile(program, sourcePath: MainPath, libraryDirectory: lib.FullName, warnLibrary: true).Warnings.Count);
        Assert.Equal(2, Warnings(program, ("util", module)).Count);
    }

    // ---------- Unused imports ----------

    [Fact]
    public void An_unused_import_is_warned()
    {
        Assert.Equal(["1: 'text' is imported but not used. Remove the import"], Warnings("""
            import text
            fn main() { }
            """));
    }

    [Fact]
    public void An_import_used_in_a_type_or_in_assembly_is_used_and_one_setting_parameters_is_not_warned()
    {
        Assert.Empty(Warnings("""
            import util
            import cfg(SIZE = 2)
            var p: util.Pos
            fn main() {
                p.x = 1
                asm { jsr util.go }
            }
            """, ("util", """
            struct Pos { x: u8 }
            fn go() { }
            """), ("cfg", """
            module cfg(SIZE: u8 = 1)
            """)));
    }

    // ---------- Unreachable statements ----------

    [Fact]
    public void A_statement_after_return_is_warned_once_per_block()
    {
        Assert.Equal(["4: This statement is never reached, since the 'return' on line 3 ends the function. Remove it"], Warnings("""
            var n: u8
            fn main() {
                return
                n = 1
                n = 2
            }
            """));
    }

    // Review code-quality-semantics 20: the missing-return check and this warning use one rule (NeverFinishes).
    // A function whose return is followed by unreachable code cannot reach its end: a warning, not
    // "can reach its end without returning a value".
    [Fact]
    public void A_function_that_never_reaches_its_end_needs_no_return_there()
    {
        Assert.Equal(["5: This statement is never reached, since the 'return' on line 4 ends the function. Remove it"], Warnings("""
            var n: u8
            var r: u8
            fn f() -> u8 {
                return 1
                n = 2
            }
            fn main() { r = f() }
            """));
    }

    [Fact]
    public void Statements_after_break_continue_and_endless_loops_are_warned()
    {
        Assert.Equal([
            "5: This statement is never reached, since the 'break' on line 4 leaves the loop. Remove it",
            "9: This statement is never reached, since the 'continue' on line 8 goes on with the next pass of the loop. Remove it",
            "14: This statement is never reached, since the 'while true' loop on line 11 has no 'break', so it never ends. Remove it",
        ], Warnings("""
            var n: u8
            fn main() {
                while n < 10 {
                    break
                    n = 1
                }
                for _ in 0..3 {
                    continue
                    n = 2
                }
                while true {
                    n += 1
                }
                n = 3
            }
            """));
    }

    [Fact]
    public void An_if_or_match_none_of_whose_branches_finishes_ends_the_block()
    {
        Assert.Equal([
            "8: This statement is never reached, since no branch of the 'if' on line 3 finishes. Remove it",
        ], Warnings("""
            var n: u8
            fn f() -> u8 {
                if n == 1 {
                    return 1
                } else {
                    return 2
                }
                n = 0
                return 0
            }
            fn main() { n = f() }
            """));
        Assert.Equal([
            "8: This statement is never reached, since no arm of the 'match' on line 4 finishes. Remove it",
        ], Warnings("""
            var n: u8
            fn main() {
                while n < 3 {
                    match n {
                        0 => break
                        _ => continue
                    }
                    n = 9
                }
            }
            """));
    }

    [Fact]
    public void Code_that_may_run_is_not_warned()
    {
        // Conditions are not evaluated; an if without else, a match without '_', an asm block and an empty
        // constant range all finish; a break of an inner loop does not end the outer 'while true'.
        Assert.Empty(Warnings("""
            const DEBUG = false
            const COUNT = 0
            var n: u8
            fn main() {
                if DEBUG { return }
                n = 1
                for _ in 0..COUNT { n += 1 }
                match n {
                    0 => return
                    1 => return
                }
                asm { rts }
                n = 2
                while true {
                    while true { break }
                    n += 1
                    if n == 9 { break }
                }
                n = 3
            }
            """));
    }

    // Review Warnings, findings 1 and 4: a warning points at the start of the statement, and at the loop variable.
    [Theory]
    [InlineData("    n = 2", 5)]
    [InlineData("    buf[1] = 2", 5)]
    [InlineData("    pt.a = 2", 5)]
    [InlineData("    buf[pt.a].lo = 2", 5)]
    [InlineData("    asm { nop }", 5)]
    [InlineData("    f()", 5)]
    public void An_unreachable_statement_is_reported_at_its_start(string statement, int column)
    {
        // Other names of the program may be unused; the statement's warning is the one on line 8.
        var w = Assert.Single(Compile($$"""
            struct P { a: u8 }
            var pt: P
            var n: u8
            var buf: [u16; 4]
            fn f() { }
            fn main() {
                return
            {{statement}}
            }
            """).Warnings, w => w.Pos.Line == 8);
        Assert.Equal((8, column), (w.Pos.Line, w.Pos.Column));
    }

    [Fact]
    public void An_unused_loop_variable_is_reported_at_its_name()
    {
        var w = Assert.Single(Compile("""
            var n: u8
            fn main() {
                for count in 0..4 { n += 1 }
            }
            """).Warnings);
        Assert.Equal((3, 9), (w.Pos.Line, w.Pos.Column));
    }

    [Fact]
    public void A_break_of_an_inner_loop_does_not_end_while_true()
    {
        Assert.Equal(["8: This statement is never reached, since the 'while true' loop on line 3 has no 'break', so it never ends. Remove it"], Warnings("""
            var n: u8
            fn main() {
                while true {
                    for _ in 0..3 {
                        if n == 2 { break }
                    }
                }
                n = 1
            }
            """));
    }

    /// <summary>
    /// Every kind of statement inside a block that the unreachable-statement walk enters: the walk throws on a
    /// kind it does not know (review code-quality-semantics 14, R7), so a new one fails here until it is listed.
    /// </summary>
    [Fact]
    public void The_unreachable_walk_knows_every_kind_of_statement()
    {
        const string source = """
            var n: u8
            const fn gen(k: int) {
                if k > 0 {
                    assert k < 10, "too many"
                    emit { nop }
                }
            }
            fn main() {
                if n == 0 {
                    var a: u8 = 1
                    const B = 2
                    n = a + B
                } else if n == 1 {
                    asm { gen(1) }
                } else {
                    while n < 9 {
                        n += 1
                        if n == 5 { continue }
                        if n == 7 { break }
                    }
                }
                for i in 0..3 {
                    match i {
                        0 => { side() }
                        _ => { return }
                    }
                }
            }
            fn side() { n = 3 }
            """;
        var check = TernCompiler.Check(source, sourcePath: MainPath);
        Assert.Empty(check.All);
        var fns = check.Modules[0].Syntax.Functions.Concat(check.Modules[0].Syntax.ConstFns);
        var used = fns.SelectMany(f => f.Body.Descendants()).OfType<Stmt>().Select(s => s.GetType()).ToHashSet();
        var kinds = typeof(Stmt).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Stmt)) && !t.IsAbstract && t != typeof(ErrorStmt));
        Assert.Empty(kinds.Except(used).Select(t => t.Name));
    }

    // ---------- The report's functions with memory reachable from two roots ----------

    [Fact]
    public void The_report_lists_functions_with_memory_reachable_from_two_roots()
    {
        // f has a local and is called from main and both handlers; g has no memory, and k one root.
        var report = TernCompiler.Compile("""
            var v: u8
            fn f() {
                var t = v
                v = t + 1
            }
            fn g() { v = 1 }
            fn k() {
                var u = v
                v = u + 2
            }
            interrupt fn h1() {
                f()
                g()
            }
            interrupt fn h2() { f() }
            fn main() {
                f()
                g()
                k()
            }
            """).Report.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        int at = report.IndexOf("Functions with memory reachable from two roots:");
        Assert.True(at >= 0, string.Join("\n", report));
        Assert.Equal("  f (1 byte): main, h1, h2", report[at + 1]);
        Assert.Equal("Zero page:", report[at + 2]);

        Assert.DoesNotContain("two roots", TernCompiler.Compile("fn main() { }").Report);
    }

    // ---------- When they are reported ----------

    [Fact]
    public void No_warnings_when_the_checks_find_an_error()
    {
        var e = Assert.Throws<CompileError>(() => Compile("""
            fn main() {
                var unused: u8 = 1
                var x: u8 = 300
            }
            """));
        Assert.Single(e.WithWarnings);
        Assert.False(e.WithWarnings[0].IsWarning);
    }

    [Fact]
    public void Warnings_come_with_a_later_error_sorted_by_place()
    {
        // Recursion is found by the call graph, after the checks.
        var e = Assert.Throws<CompileError>(() => Compile("""
            fn main() {
                var unused: u8 = 1
                f()
            }
            fn f() { g() }
            fn g() { f() }
            fn spare() { }
            """));
        Assert.Equal([true, false, true], e.WithWarnings.Select(x => x.IsWarning).ToList());
        Assert.Equal([2, e.Pos.Line, 7], e.WithWarnings.Select(x => x.Pos.Line).ToList());
        Assert.Single(e.All);
    }

    [Fact]
    public void Warnings_come_with_an_allocation_error()
    {
        // Two bytes of zero page for four: the allocator's error, after the checks.
        var e = Assert.Throws<CompileError>(() => Compile("""
            memory {
                $80..=$81 zeropage
            }
            var a: u16 @ zp
            var b: u16 @ zp
            fn main() {
                var unused: u8 = 1
                a = b
            }
            """));
        Assert.Equal([false, true], e.WithWarnings.Select(x => x.IsWarning).ToList());
        Assert.Equal(7, e.WithWarnings[1].Pos.Line);
    }

    [Fact]
    public void No_warnings_mode_reports_none()
    {
        Assert.Empty(Compile("""
            fn main() { var unused: u8 = 1 }
            """, WarningMode.Off).Warnings);
    }

    [Fact]
    public void Warnings_as_errors_stops_the_build_with_each_as_an_error()
    {
        var e = Assert.Throws<CompileError>(() => Compile("""
            import text
            fn main() { var unused: u8 = 1 }
            """, WarningMode.AsErrors));
        Assert.Equal([
            "1: 'text' is imported but not used. Remove the import",
            "2: 'unused' is never used. Remove it, or start its name with '_' to keep it",
        ], e.All.Select(Line).ToList());
        Assert.All(e.WithWarnings, x => Assert.False(x.IsWarning));
    }
}

/// <summary>
/// The repository compiles with no warnings (roadmap "Warnings"): the examples, and the library with
/// warnings turned on for it, though programs never see them. The skill's examples are checked by
/// <see cref="SkillTests"/>, which compiles them with --warnings-as-errors.
/// </summary>
public sealed class RepositoryWarningTests
{
    private static string Line(CompileError w) => $"{Path.GetFileName(w.Pos.File)}:{w.Pos.Line}: {w.Description}";

    [Theory]
    [InlineData("c64", 15)]
    [InlineData("atari", 3)]
    [InlineData("apple2", 2)]
    [InlineData("nes", 2)]
    public void The_examples_compile_with_no_warnings(string target, int programs_at_least)
    {
        var platform = Platform.ByName(target)!;
        var examples = Path.GetDirectoryName(AsmTests.FindExample("hello.tern", target))!;
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            foreach (var file in Directory.GetFiles(examples, "*.tern", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(dir.FullName, Path.GetRelativePath(examples, file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
            }
            // Programs and binaries; a host imports the interface module and includes the file its binary's
            // compile writes, so a program that cannot find one yet is compiled again after the others.
            var programs = Directory.GetFiles(dir.FullName, "*.tern", SearchOption.AllDirectories)
                .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"^(fn main\(|export\b)", System.Text.RegularExpressions.RegexOptions.Multiline))
                .Order(StringComparer.Ordinal).ToList();
            var warnings = new List<string>();
            int compiled = 0;
            for (int pass = 0; pass < 3 && programs.Count > 0; pass++)
            {
                foreach (var path in programs.ToList())
                {
                    CompileResult result;
                    try { result = TernCompiler.Compile(File.ReadAllText(path), platform, sourcePath: path); }
                    catch (CompileError e) when (e.Description.StartsWith("Cannot") && pass < 2) { continue; }
                    programs.Remove(path);
                    compiled++;
                    File.WriteAllBytes(Path.ChangeExtension(path, platform.FileExtension), result.File);
                    if (result.ApiModule is not null) File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, result.ApiModuleName + ".tern"), result.ApiModule);
                    warnings.AddRange(result.Warnings.Select(Line));
                }
            }
            Assert.True(compiled >= programs_at_least, $"only {compiled} examples compiled");
            Assert.True(warnings.Count == 0, string.Join("\n", warnings));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void The_library_compiles_with_no_warnings()
    {
        var library = TernCompiler.DefaultLibraryDirectory;
        var warnings = new List<string>();
        foreach (var platform in Platform.All)
            foreach (var file in Directory.GetFiles(library, "*.tern").Concat(Directory.GetFiles(Path.Combine(library, platform.Name), "*.tern")))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                // A NES program names its board (spec 8.4).
                // A board's own module (uxrom) is built for that board.
                var board = platform.Boards.FirstOrDefault(b => b.BankModule?.EndsWith("/" + name) == true) ?? platform.Boards.FirstOrDefault();
                string memory = board is not null ? $"memory {{\n    cartridge {board.Name} vertical\n}}\n" : "";
                var result = TernCompiler.Compile($"import {name}\n{memory}fn main() {{ }}\n", platform, warnLibrary: true);
                warnings.AddRange(result.Warnings.Where(w => w.Pos.File is not null).Select(Line));
            }
        Assert.True(warnings.Count == 0, string.Join("\n", warnings.Distinct()));
    }
}
