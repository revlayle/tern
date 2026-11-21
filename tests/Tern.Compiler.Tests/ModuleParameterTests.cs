using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>Module parameters (spec 2.2) and compile-time <c>assert</c> (spec 4.1).</summary>
public sealed class ModuleParameterTests : TempFolderTest
{
    private string Lib => Path.Combine(_dir.FullName, "lib");
    private string Src => Path.Combine(_dir.FullName, "src");

    public ModuleParameterTests()
    {
        Directory.CreateDirectory(Lib);
        Directory.CreateDirectory(Src);
    }

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(Src, name), text);

    private Run RunMain(string source) =>
        Run.Program(source, sourcePath: Path.Combine(Src, "main.tern"), libraryDirectory: Lib);

    private CompileResult CompileMain(string source) =>
        TernCompiler.Compile(source, null, Path.Combine(Src, "main.tern"), Lib);

    private CompileError Fail(string source) => Assert.Throws<CompileError>(() => CompileMain(source));

    private const string Sprites = """
        module sprites(COUNT: u8 = 32, WIDE: u16 = 300, FLICKER: bool = false, DELTA: i8 = -5)
        assert COUNT >= 8 && COUNT <= 64, "sprites.COUNT must be 8 to 64"
        var list: [u8; COUNT]
        fn count() -> u8 { return COUNT }
        fn wide() -> u16 { return WIDE }
        fn flicker() -> bool { return FLICKER }
        fn delta() -> i8 { return DELTA }
        """;

    [Fact]
    public void Defaults_are_used_when_the_import_sets_nothing()
    {
        Write("sprites.tern", Sprites);
        var r = RunMain("""
            import sprites
            var a: u8
            var b: u16
            var c: bool
            var d: i8
            var n: u8
            fn main() {
                a = sprites.count()
                b = sprites.wide()
                c = sprites.flicker()
                d = sprites.delta()
                n = sprites.COUNT
            }
            """);
        Assert.Equal(32, r.U8("a"));
        Assert.Equal(300, r.U8("b") | r.U8("b", 1) << 8);
        Assert.Equal(0, r.U8("c"));
        Assert.Equal(251, r.U8("d"));
        Assert.Equal(32, r.U8("n"));
    }

    [Fact]
    public void The_import_sets_parameters_by_name_using_the_main_files_constants()
    {
        Write("sprites.tern", Sprites);
        var r = RunMain("""
            import sprites(COUNT = BASE * 2 + 1, FLICKER = true, WIDE = 65535, DELTA = -128)
            const BASE = 20
            var a: u8
            var b: u16
            var c: bool
            var d: i8
            fn main() {
                a = sprites.count()
                b = sprites.wide()
                c = sprites.flicker()
                d = sprites.delta()
            }
            """);
        Assert.Equal(41, r.U8("a"));
        Assert.Equal(65535, r.U8("b") | r.U8("b", 1) << 8);
        Assert.Equal(1, r.U8("c"));
        Assert.Equal(128, r.U8("d"));
    }

    [Fact]
    public void Another_module_importing_plainly_sees_the_programs_values()
    {
        Write("sprites.tern", Sprites);
        Write("user.tern", "import sprites\nfn get() -> u8 { return sprites.COUNT }");
        var r = RunMain("""
            import user
            import sprites(COUNT = 12)
            var a: u8
            fn main() { a = user.get() }
            """);
        Assert.Equal(12, r.U8("a"));
    }

    [Fact]
    public void A_parameter_value_can_use_a_constant_declared_after_the_import_line()
    {
        Write("sprites.tern", Sprites);
        var r = RunMain("""
            import sprites(COUNT = N)
            const N = 9
            var a: u8
            fn main() { a = sprites.COUNT }
            """);
        Assert.Equal(9, r.U8("a"));
    }

    [Theory]
    [InlineData("import sprites(COUNT = 7)\nfn main() { }", 1, 8, "sprites.COUNT must be 8 to 64 (assert at sprites.tern:2:1)")]
    [InlineData("import sprites(COUNT = 65)\nfn main() { }", 1, 8, "sprites.COUNT must be 8 to 64 (assert at sprites.tern:2:1)")]
    [InlineData("import sprites(COUNT = 256)\nfn main() { }", 1, 24, "256 does not fit in u8 (0 to 255). Use a value in that range, or a wider type")]
    [InlineData("import sprites(DELTA = 128)\nfn main() { }", 1, 24, "128 does not fit in i8 (-128 to 127). Use a value in that range, or a wider type")]
    [InlineData("import sprites(WIDE = -1)\nfn main() { }", 1, 23, "-1 does not fit in u16 (0 to 65535). Use a value in that range, or a wider type")]
    [InlineData("import sprites(FLICKER = 1)\nfn main() { }", 1, 26, "Expected bool, found a number. Write true or false, or compare, as in 'x != 0'")]
    [InlineData("import sprites(COUNT = true)\nfn main() { }", 1, 24, "Expected u8, found bool. Use 'if' to choose a number, as in 'if b { x = 1 }'")]
    [InlineData("import sprites(SIZE = 10)\nfn main() { }", 1, 16, "Module 'sprites' has no parameter 'SIZE'; its parameters are COUNT, WIDE, FLICKER, DELTA. Use one of those names")]
    [InlineData("import sprites(COUNT = 10, COUNT = 11)\nfn main() { }", 1, 28, "Parameter 'COUNT' is set twice. Remove one")]
    [InlineData("import sprites(COUNT = x)\nvar x: u8\nfn main() { }", 1, 24, "'x' is not declared. Check the spelling, or declare it. A parameter value uses literals and the main file's constants that do not depend on imported modules. Write the value as a number")]
    public void Wrong_parameter_values_are_errors_at_the_import_line(string source, int line, int column, string expected)
    {
        Write("sprites.tern", Sprites);
        var e = Fail(source);
        Expect.Message(e, line, column, expected);
        Assert.Equal(1, e.Pos.Line);
        Assert.Equal(Path.Combine(Src, "main.tern"), e.Pos.File);
    }

    [Fact]
    public void A_failed_assert_names_the_import_line_when_a_parameter_caused_it()
    {
        Write("sprites.tern", Sprites);
        var e = Fail("\n\nimport sprites(COUNT = 70)\nfn main() { }");
        Assert.Equal(3, e.Pos.Line);
        Expect.Message(e, 3, 8, "sprites.COUNT must be 8 to 64 (assert at sprites.tern:2:1)");
    }

    [Fact]
    public void A_failed_assert_on_a_constant_computed_from_a_parameter_names_the_import_line()
    {
        Write("sized.tern", "module sized(COUNT: u8 = 8)\nconst SIZE = COUNT * 4\nconst MORE = SIZE + 1\nassert MORE <= 129, \"sized.COUNT must be 32 or less\"");
        var e = Fail("\nimport sized(COUNT = 40)\nfn main() { }");
        Assert.Equal(2, e.Pos.Line);
        Assert.Equal(Path.Combine(Src, "main.tern"), e.Pos.File);
        Expect.Message(e, 2, 8, "sized.COUNT must be 32 or less (assert at sized.tern:4:1)");
    }

    [Fact]
    public void A_parameter_value_can_use_a_main_file_constant_with_sizeof()
    {
        Write("sprites.tern", Sprites);
        var r = RunMain("""
            import sprites(COUNT = S)
            struct Enemy { x: u8, y: u8 }
            const S = sizeof(Enemy) * 4
            var a: u8
            fn main() { a = sprites.COUNT }
            """);
        Assert.Equal(8, r.U8("a"));
    }

    [Fact]
    public void A_parameter_value_naming_a_constant_that_needs_data_says_why()
    {
        Write("sprites.tern", Sprites);
        var e = Fail("import sprites(COUNT = S)\ndata t: [u8; 9] = [1, 2, 3, 4, 5, 6, 7, 8, 9]\nconst S = len(t)\nfn main() { }");
        Expect.Message(e, 1, 24, "'S' cannot be used in a parameter value, since it does not check without the rest of the program: 't' is not declared. Check the spelling, or declare it. Write the value as a number");
    }

    [Fact]
    public void Assert_inside_a_function_says_where_it_goes()
    {
        Expect.Message(Fail("fn main() {\n assert true, \"x\"\n}"), 2, 2, "An assert is checked at compile time. Write it at the top level or in a const fn");
    }

    [Fact]
    public void A_failed_assert_unrelated_to_parameters_is_reported_in_the_module()
    {
        Write("m.tern", "module m(A: u8 = 1)\nconst K = 3\nassert K == 4, \"K must be 4\"");
        var e = Fail("import m(A = 2)\nfn main() { }");
        Assert.Equal("K must be 4", e.Description);
        Assert.Equal(Path.Combine(Src, "m.tern"), e.Pos.File);
        Assert.Equal(3, e.Pos.Line);
    }

    [Fact]
    public void Assert_works_in_the_main_file_and_with_sizeof_and_len()
    {
        RunMain("""
            struct Enemy { x: u8, y: u8, hp: u16 }
            data t: [u8; 4] = [1, 2, 3, 4]
            assert sizeof(Enemy) * 32 <= 256, "the enemy table must fit in a page"
            assert len(t) == 4, "t has four entries"
            fn main() { }
            """);
        var e = Fail("struct Enemy { x: u8, y: u8, hp: u16 }\nassert sizeof(Enemy) * 100 <= 256, \"too many\"\nfn main() { }");
        Assert.Equal("too many", e.Description);
        Assert.Equal(2, e.Pos.Line);
    }

    [Fact]
    public void An_assert_condition_must_be_a_constant_bool()
    {
        Expect.Message(Fail("var x: u8\nassert x == 1, \"m\"\nfn main() { }"), 2, 10, "An assert condition must be known at compile time. Use constants, sizeof and len");
        Expect.Message(Fail("assert 1 + 1, \"m\"\nfn main() { }"), 1, 10, "An assert condition is a bool. Write a comparison, e.g. COUNT <= 64");
        Expect.Message(Fail("assert true\nfn main() { }"), 1, 12, "Expected ',' and a message string after the condition, found end of line");
        Expect.Message(Fail("assert true, 5\nfn main() { }"), 1, 14, "Expected a message string, found '5'");
    }

    [Fact]
    public void Header_errors()
    {
        Write("other.tern", "module wrong(A: u8 = 1)\nfn f() { }");
        Expect.Message(Fail("import other\nfn main() { }"), 1, 1, "The module header says 'wrong' but the file is other.tern; the name must match the file. Rename one of them");
        Expect.Message(Fail("module main(A: u8 = 1)\nfn main() { }"), 1, 1, "The main file has no module header; 'module' is only for modules that have parameters. Remove the header");
        Write("plain.tern", "fn f() { }");
        Expect.Message(Fail("import plain(A = 1)\nfn main() { }"), 1, 14, "Module 'plain' has no parameters (it has no 'module plain(...)' header). Remove the arguments from the import");
        Write("p.tern", "module p(A: u8)\nfn f() { }");
        Expect.Message(Fail("import p\nfn main() { }"), 1, 15, "Expected '= default value' (every module parameter has a default), found ')'");
        Write("q.tern", "module q(A: [u8; 2] = 1)\nfn f() { }");
        Expect.Message(Fail("import q\nfn main() { }"), 1, 13, "A module parameter is an integer type or bool. Use u8, i8, u16, i16 or bool");
        Write("r.tern", "import p\nmodule r(A: u8 = 1)\nfn f() { }");
        Expect.Message(Fail("import r\nfn main() { }"), 2, 1, "'module' must be the first line of the file, before the imports. Move it there");
    }

    [Fact]
    public void Only_the_main_file_sets_parameters()
    {
        Write("sprites.tern", Sprites);
        Write("user.tern", "import sprites(COUNT = 10)\nfn get() -> u8 { return sprites.COUNT }");
        var e = Fail("import user\nfn main() { }");
        Expect.Message(e, 1, 16, "Only the program's main file can set the parameters of 'sprites', since a module is loaded once. Import it plainly here and set COUNT in the main file");
        Assert.EndsWith("user.tern", e.Pos.File);
    }

    [Fact]
    public void Parameters_cost_nothing_at_runtime()
    {
        Write("sprites.tern", Sprites);
        var a = CompileMain("import sprites\nvar a: u8\nfn main() { a = sprites.COUNT }");
        var b = CompileMain("import sprites(COUNT = 40)\nvar a: u8\nfn main() { a = sprites.COUNT }");
        Assert.Equal(a.Image.Length, b.Image.Length);
    }
}
