using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>Modules and imports (spec 2.2): <c>import name</c>, qualified names, lookup, platform modules.</summary>
public sealed class ModuleTests : IDisposable
{
    /// <summary>A temporary folder for the program's files, with a library folder inside it.</summary>
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tern");
    private string Lib => Path.Combine(_dir.FullName, "lib");
    private string Src => Path.Combine(_dir.FullName, "src");

    public ModuleTests()
    {
        Directory.CreateDirectory(Lib);
        Directory.CreateDirectory(Src);
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private void Write(string folder, string name, string text)
    {
        var path = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private Run RunMain(string source) =>
        Run.Program(source, sourcePath: Path.Combine(Src, "main.tern"), libraryDirectory: Lib);

    private CompileResult CompileMain(string source, Platform? platform = null) =>
        TernCompiler.Compile(source, platform, Path.Combine(Src, "main.tern"), Lib);

    private const string Util = """
        const LIMIT = 200
        var counter: u8
        data table: [u8; 4] = [10, 20, 30, 40]

        fn add(a: u8, b: u8) -> u8 { return clamp(a + b) }
        fn clamp(v: u8) -> u8 {
            if v > LIMIT { return LIMIT }
            return v
        }
        fn bump() { counter += 1 }
        asm fn twice(v: u8 using A) -> u8 {
            asl
        }
        """;

    [Fact]
    public void A_program_in_several_files()
    {
        Write(Src, "util.tern", Util);
        var r = RunMain("""
            import util
            var a: u8
            var b: u8
            var c: u8
            var d: u8
            var e: u8
            fn main() {
                a = util.add(5, 6)
                b = util.add(150, 100)
                for i in 0..3 { util.bump() }
                c = util.counter
                d = util.table[2] + util.LIMIT
                e = util.twice(21)
                asm {
                    jsr util.bump
                    lda util.counter
                    sta util.counter
                }
            }
            """);
        Assert.Equal(11, r.U8("a"));
        Assert.Equal(200, r.U8("b"));
        Assert.Equal(3, r.U8("c"));
        Assert.Equal((30 + 200) & 0xFF, r.U8("d"));
        Assert.Equal(42, r.U8("e"));
        Assert.Equal(4, r.U8("util.counter"));
        Assert.Contains("jsr util.add", r.Result.Listing);
        // VICE labels keep the dots, so a module name and a similar main-file name do not clash.
        Assert.Contains($"al C:{r.Address("util.counter"):x4} .util.counter{Environment.NewLine}", r.Result.ViceLabels);
    }

    [Fact]
    public void Names_in_different_modules_do_not_clash()
    {
        Write(Src, "left.tern", "var x: u8\nfn init() { x = 1 }\nfn get() -> u8 { return x }");
        Write(Src, "right.tern", "var x: u8\nfn init() { x = 2 }\nfn get() -> u8 { return x }");
        var r = RunMain("""
            import left
            import right
            var x: u8
            var l: u8
            var rr: u8
            fn init() { x = 3 }
            fn main() {
                left.init()
                right.init()
                init()
                l = left.get()
                rr = right.get()
            }
            """);
        Assert.Equal(1, r.U8("left.x"));
        Assert.Equal(2, r.U8("right.x"));
        Assert.Equal(3, r.U8("x"));
        Assert.Equal(1, r.U8("l"));
        Assert.Equal(2, r.U8("rr"));
    }

    [Fact]
    public void A_module_is_loaded_once_and_modules_may_import_each_other()
    {
        Write(Src, "shared.tern", "var total: u8\nconst STEP = 5");
        Write(Src, "ping.tern", "import shared\nimport pong\nfn hit(n: u8) { shared.total += shared.STEP\n if n > 0 { pong.hit(n - 1) } }");
        Write(Src, "pong.tern", "import shared\nfn hit(n: u8) { shared.total += 1 }");
        var r = RunMain("""
            import shared
            import ping
            var t: u8
            fn main() {
                ping.hit(1)
                shared.total += 100
                t = shared.total
            }
            """);
        Assert.Equal(106, r.U8("t"));
        Assert.Single(r.Result.Symbols.Keys, k => k == "shared.total");
    }

    [Fact]
    public void Constants_of_an_imported_module_can_be_used_in_declarations()
    {
        Write(Src, "sizes.tern", "const N = 3\nconst BASE = $C000");
        var r = RunMain("""
            import sizes
            const LAST = sizes.N - 1
            var buf: [u8; sizes.N]
            var hw: u8 @ sizes.BASE + 1
            fn main() {
                buf[LAST] = 7
                hw = 9
            }
            """);
        Assert.Equal(7, r.U8("buf", 2));
        Assert.Equal(9, r.Ram.Bytes[0xC001]);
    }

    [Fact]
    public void The_library_comes_before_the_source_folder()
    {
        Write(Lib, "pick.tern", "const WHO = 1");
        Write(Src, "pick.tern", "const WHO = 2");
        Write(Src, "mine.tern", "const WHO = 3");
        var r = RunMain("""
            import pick
            import mine
            var a: u8
            var b: u8
            fn main() {
                a = pick.WHO
                b = mine.WHO
            }
            """);
        Assert.Equal(1, r.U8("a"));
        Assert.Equal(3, r.U8("b"));
    }

    [Fact]
    public void A_library_module_imports_user_modules_next_to_itself()
    {
        Write(Lib, "outer.tern", "import inner\nconst V = inner.V + 1");
        Write(Lib, "inner.tern", "const V = 10");
        var r = RunMain("import outer\nvar v: u8\nfn main() { v = outer.V }");
        Assert.Equal(11, r.U8("v"));
    }

    [Fact]
    public void Platform_modules_load_only_on_their_platform()
    {
        Write(Path.Combine(Lib, "c64"), "hw.tern", "const BORDER = $D020");
        var r = RunMain("import hw\nvar b: u16\nfn main() { b = hw.BORDER }");
        Assert.Equal(0xD020, r.U16("b"));

        var other = new Platform
        {
            Name = "other",
            Cpu = Platform.C64.Cpu,
            LoadAddress = Platform.C64.LoadAddress,
            Header = Platform.C64.Header,
            ZeroPage = Platform.C64.ZeroPage,
            RamEnd = Platform.C64.RamEnd,
            DefaultEncoding = Platform.C64.DefaultEncoding,
        };
        var e = Assert.Throws<CompileError>(() => CompileMain("import hw\nfn main() { }", other));
        Assert.Equal("'hw' is a module for the c64 platform; this program is compiled for other", e.Description);
    }

    [Fact]
    public void An_unused_import_adds_no_bytes()
    {
        Write(Src, "util.tern", Util + "\nvar big: [u8; 200]\ndata more: [u8; 3] = [1, 2, 3]\nvar zp_ptr: ptr u8 @ zp");
        var with = CompileMain("import util\nfn main() { }");
        var without = CompileMain("fn main() { }");
        Assert.Equal(without.Image, with.Image);
        Assert.DoesNotContain("util.table", with.Symbols.Keys);
        Assert.DoesNotContain("util.counter", with.Symbols.Keys);
        Assert.Equal(without.Report, with.Report);

        // Using one function keeps only it and the globals it names.
        var one = CompileMain("import util\nvar v: u8\nfn main() { v = util.add(1, 2) }");
        Assert.Contains("util.clamp", one.Symbols.Keys);
        Assert.DoesNotContain("util.bump", one.Symbols.Keys);
        Assert.DoesNotContain("util.counter", one.Symbols.Keys);
        Assert.DoesNotContain("util.table", one.Symbols.Keys);
    }

    [Fact]
    public void The_listing_names_the_module_file()
    {
        Write(Src, "util.tern", Util);
        var r = CompileMain("import util\nvar v: u8\nfn main() { v = util.add(1, 2) }");
        Assert.Contains("; util.tern:5: fn add(a: u8, b: u8) -> u8 { return clamp(a + b) }", r.Listing);
        Assert.Contains(";    3: fn main() { v = util.add(1, 2) }", r.Listing);
    }

    [Fact]
    public void Include_bytes_in_a_module_is_relative_to_the_module()
    {
        Write(Lib, "gfx.tern", "data ship = include_bytes(\"ship.bin\")");
        File.WriteAllBytes(Path.Combine(Lib, "ship.bin"), [7, 8, 9]);
        var r = RunMain("import gfx\nvar v: u8\nfn main() { v = gfx.ship[2] }");
        Assert.Equal(9, r.U8("v"));
    }

    [Fact]
    public void Errors_in_a_module_name_its_file()
    {
        Write(Src, "bad.tern", "fn f() {\n    x = 1\n}");
        var e = Assert.Throws<CompileError>(() => CompileMain("import bad\nfn main() { }"));
        Assert.Equal("'x' is not declared", e.Description);
        Assert.Equal(new SourcePos(2, 5, Path.Combine(Src, "bad.tern")), e.Pos);
        Assert.StartsWith("bad.tern:2:5", e.Message);
    }

    [Theory]
    [InlineData("import nothing\nfn main() { }", "Cannot find module 'nothing': there is no nothing.tern in the library")]
    [InlineData("var x: u8\nimport util\nfn main() { }", "'import' must be at the top of the file, before other declarations")]
    [InlineData("import util\nimport util\nfn main() { }", "'util' is imported twice")]
    [InlineData("import 5\nfn main() { }", "Expected a module name after 'import'")]
    [InlineData("import util\nfn main() { util = 1 }", "'util' is a module; use the names in it as util.name")]
    [InlineData("import util\nvar v: u8\nfn main() { v = util }", "'util' is a module; use the names in it as util.name")]
    [InlineData("import util\nvar v: u8\nfn main() { v = util.nope }", "'util.nope' is not declared")]
    [InlineData("import util\nfn main() { util.nope() }", "'util.nope' is not declared")]
    [InlineData("import util\nfn main() { util.LIMIT() }", "'util.LIMIT' is not a function")]
    [InlineData("import util\nfn main() { util.add(1) }", "'util.add' takes 2 argument(s), not 1")]
    [InlineData("import util\nfn main() { var util: u8 = 1 }", "'util' is the name of an imported module; choose another name")]
    [InlineData("import util\nfn util() { }\nfn main() { }", "'util' is the name of an imported module; choose another name")]
    [InlineData("import util\nfn main() { for util in 0..3 { } }", "'util' is the name of an imported module; choose another name")]
    [InlineData("import util\nfn main() { util. }", "Expected a name in module 'util' after '.'")]
    [InlineData("import util\nvar v: u8\nfn main() { v = clamp(1) }", "'clamp' is not a function")]
    [InlineData("import util\nfn main() { asm {\n jsr util.nope\n } }", "'util.nope' is not declared")]
    [InlineData("import withmain\nfn main() { }", "Only the program's main file has 'fn main()'; 'withmain' is a module")]
    [InlineData("import util", "The program has no 'fn main()'")]
    // A missing import (review M4, finding 1).
    [InlineData("fn main() { text.put(1, 2, 3) }", "'text' is not declared. To use the module text, add 'import text' at the top of the file")]
    [InlineData("var x: u16\nfn main() { x = c64.SCREEN }", "'c64' is not declared. To use the module c64, add 'import c64' at the top of the file")]
    [InlineData("fn main() { asm {\n jsr c64.chrout\n } }", "'c64' is not declared. To use the module c64, add 'import c64' at the top of the file")]
    [InlineData("import util\nvar v: u8\nfn main() { v = other.f() }", "'other' is not declared. To use the module other, add 'import other'")]
    [InlineData("var v: u8\nfn main() { v = nothing.lo }", "'nothing' is not declared")]
    public void Reports_errors(string source, string expected)
    {
        Write(Src, "util.tern", Util);
        Write(Src, "withmain.tern", "fn main() { }");
        var e = Assert.Throws<CompileError>(() => CompileMain(source));
        Assert.Contains(expected, e.Description);
    }

    [Fact]
    public void The_module_name_must_match_the_file_name_exactly()
    {
        // Review M4, finding 2: on Windows the file is found whatever the case, but it must not be
        // loaded twice under two names. Elsewhere it is not found at all; both are errors.
        Write(Src, "util.tern", Util);
        Write(Src, "other.tern", "import util\nfn go() { util.bump() }");
        var e = Assert.Throws<CompileError>(() => CompileMain("import Util\nimport other\nfn main() { Util.bump()\n other.go() }"));
        Assert.True(e.Description is "The module file is named util.tern; write 'import util'"
            || e.Description.StartsWith("Cannot find module 'Util'"), e.Description);
    }

    [Fact]
    public void Constants_across_an_import_cycle_get_a_clear_error()
    {
        // Review M4, finding 3: b is declared before a, so b's declarations cannot use a's constants.
        Write(Src, "a.tern", "import b\nconst N = 4\nfn fa() { b.fb() }");
        Write(Src, "b.tern", "import a\nvar buf: [u8; a.N]\nfn fb() { buf[0] = 1 }");
        var e = Assert.Throws<CompileError>(() => CompileMain("import a\nfn main() { a.fa() }"));
        Assert.Equal("'a.N' cannot be used in a declaration of b: a and b import each other, and a is declared after b. Move N to b, or to a module both import", e.Description);

        // Bodies may use names in both directions.
        Write(Src, "b.tern", "import a\nvar n: u8\nfn fb() { n = a.N }");
        var r = RunMain("import a\nvar v: u8\nfn main() { a.fa()\n v = b2() }\nfn b2() -> u8 { return 0 }");
        Assert.Equal(4, r.U8("b.n"));
    }

    [Fact]
    public void A_module_cannot_use_names_its_importer_did_not_give_it()
    {
        // Each file sees only its own names and the modules it imports.
        Write(Src, "a.tern", "import b\nfn f() -> u8 { return b.V }");
        Write(Src, "b.tern", "const V = 1");
        Write(Src, "c.tern", "fn g() -> u8 { return b.V }");
        var e = Assert.Throws<CompileError>(() => CompileMain("import a\nimport c\nfn main() { }"));
        Assert.Equal("'b' is not declared. To use the module b, add 'import b' at the top of the file", e.Description);
    }

    [Fact]
    public void Structs_of_a_module_are_used_with_qualified_names()
    {
        Write(Lib, "shapes.tern", """
            struct Point { x: u8, y: u8 }
            struct Box { min: Point, max: Point }
            var origin: Point
            const BOX_SIZE = sizeof(Box)
            fn width(b: ptr Box) -> u8 { return b.max.x - b.min.x }
            """);
        var r = RunMain("""
            import shapes
            var b: shapes.Box
            var p: ptr shapes.Point
            var out: [u8; 4]
            fn main() {
                b.min.x = 3
                b.max.x = 10
                shapes.origin.y = 7
                p = addr(shapes.origin)
                out[0] = shapes.width(addr(b))
                out[1] = sizeof(shapes.Box)
                out[2] = shapes.BOX_SIZE
                out[3] = p.y
            }
            """);
        Assert.Equal(new byte[] { 7, 4, 4, 7 }, r.Ram.Bytes[r.Address("out")..(r.Address("out") + 4)]);
        Assert.Equal(7, r.U8("shapes.origin", 1));
    }

    [Fact]
    public void Struct_type_errors_across_modules()
    {
        Write(Lib, "m.tern", "var v: u8\nstruct S { x: u8 }");
        var e = Assert.Throws<CompileError>(() => CompileMain("import m\nstruct T { x: u8 @ m.v }\nfn main() { }"));
        Assert.Equal("A field offset must be known at compile time", e.Description);
        e = Assert.Throws<CompileError>(() => CompileMain("import m\nvar t: m.T\nfn main() { }"));
        Assert.Equal("'m.T' is not declared", e.Description);
        e = Assert.Throws<CompileError>(() => CompileMain("var t: m.S\nfn main() { }"));
        Assert.Equal("'m' is not declared. To use the module m, add 'import m' at the top of the file", e.Description);
    }

    private const string Secret = """
        private const STEP = 3
        private var count: u8
        private data TABLE: [u8; 3] = [7, 8, 9]
        private struct Pair { a: u8, b: u8 }
        private var pair: Pair
        private fn helper() { count += STEP }
        private asm fn twice(v: u8 using A) -> u8 {
            asl
        }

        // Public functions use the private names freely, also in assembly.
        fn bump() -> u8 {
            helper()
            pair.b = TABLE[2]
            asm {
                inc count
            }
            return twice(count) + pair.b
        }
        """;

    [Fact]
    public void Private_names_are_usable_inside_their_module()
    {
        Write(Src, "secret.tern", Secret);
        var r = RunMain("""
            import secret
            var v: u8
            private var w: u8
            fn main() {
                v = secret.bump()
                w = 1
            }
            """);
        Assert.Equal((3 + 1) * 2 + 9, r.U8("v"));
        Assert.Equal(1, r.U8("w"));
    }

    [Theory]
    [InlineData("fn main() { secret.helper() }", "'secret.helper' is private to the module secret; only code in secret can use it")]
    [InlineData("fn main() { var x = secret.twice(1) }", "'secret.twice' is private to the module secret")]
    [InlineData("var x: u8\nfn main() { x = secret.count }", "'secret.count' is private to the module secret")]
    [InlineData("var x: u8\nfn main() { x = secret.TABLE[0] }", "'secret.TABLE' is private to the module secret")]
    [InlineData("var x: u8\nfn main() { x = secret.STEP }", "'secret.STEP' is private to the module secret")]
    [InlineData("var x: u8\nfn main() { x = secret.pair.a }", "'secret.pair' is private to the module secret")]
    [InlineData("var x: secret.Pair\nfn main() { }", "'secret.Pair' is private to the module secret")]
    [InlineData("var x: u8\nfn main() { x = sizeof(secret.Pair) }", "'secret.Pair' is private to the module secret")]
    [InlineData("var p: ptr u8\nfn main() { p = addr(secret.count) }", "'secret.count' is private to the module secret")]
    [InlineData("fn main() { asm {\n inc secret.count\n } }", "'secret.count' is private to the module secret")]
    [InlineData("fn main() { asm {\n jsr secret.helper\n } }", "'secret.helper' is private to the module secret")]
    [InlineData("fn main() { asm {\n lda #secret.TABLE.lo\n } }", "'secret.TABLE' is private to the module secret")]
    [InlineData("fn main() { private var x: u8 }", "'private' is only for top-level declarations")]
    [InlineData("private private var x: u8\nfn main() { }", "'private' goes before a declaration (const, var, data, struct or fn); found 'private'")]
    [InlineData("var private: u8\nfn main() { }", "Expected variable name, found 'private'")]
    public void Private_names_are_errors_outside_their_module(string body, string expected)
    {
        Write(Src, "secret.tern", Secret);
        var e = Assert.Throws<CompileError>(() => CompileMain("import secret\n" + body));
        Assert.Contains(expected, e.Description);
    }

    [Fact]
    public void Private_goes_before_a_declaration()
    {
        var e = Assert.Throws<CompileError>(() => CompileMain("private import util\nfn main() { }"));
        Assert.Equal("'private' goes before a declaration (const, var, data, struct or fn); found 'import'", e.Description);
    }

    [Fact]
    public void A_module_interrupt_handler_is_kept_only_when_named()
    {
        Write(Src, "irq.tern", "var ticks: u8\ninterrupt fn tick() { ticks += 1 }\nfn other() { }");
        var unused = CompileMain("import irq\nfn main() { irq.other() }");
        Assert.DoesNotContain("irq.tick", unused.Symbols.Keys);
        var used = CompileMain("import irq\nfn main() { asm {\n lda #irq.tick.lo\n sta $FFFE\n } }");
        Assert.Contains("irq.tick", used.Symbols.Keys);
        Assert.Contains("irq.ticks", used.Symbols.Keys);
    }

    [Fact]
    public void Private_names_are_private_between_modules_too()
    {
        Write(Src, "secret.tern", Secret);
        Write(Src, "other.tern", "import secret\nfn go() { secret.helper() }");
        var e = Assert.Throws<CompileError>(() => CompileMain("import other\nfn main() { other.go() }"));
        Assert.Equal("'secret.helper' is private to the module secret; only code in secret can use it", e.Description);
        Assert.Equal("other.tern", Path.GetFileName(e.Pos.File));
    }
}
