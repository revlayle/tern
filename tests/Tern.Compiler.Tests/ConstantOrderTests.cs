using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// M20 follow-up 3, spec 2.2 and 4: constants are worked out when they are first used, in any order and
/// any module, also across an import ring; <c>len()</c> and <c>sizeof()</c> of a global, <c>data</c> or a
/// struct may be part of a constant. Only a value that depends on itself is an error.
/// </summary>
public sealed class ConstantOrderTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tern");
    private string Src => _dir.FullName;

    public void Dispose() => _dir.Delete(recursive: true);

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(Src, name), text);

    private Run RunMain(string source) => Run.Program(source, sourcePath: Path.Combine(Src, "main.tern"));

    private CompileError Fail(string source) =>
        Assert.Throws<CompileError>(() => TernCompiler.Compile(source, null, Path.Combine(Src, "main.tern")));

    [Fact]
    public void A_constant_may_use_one_declared_below_it()
    {
        var r = Run.Program("""
            const A: u8 = B + 1
            const B: u8 = C * 2
            const C = 20
            var x: u8
            var buf: [u8; A]
            fn main() { x = A }
            """);
        Assert.Equal(41, r.U8("x"));
        Assert.Equal(41, r.Result.Variables["buf"].Size);
    }

    [Fact]
    public void Len_and_sizeof_in_a_constant_in_the_same_file()
    {
        var r = Run.Program("""
            const KINDS: u8 = len(star_char)          // data below
            const ABOVE: u8 = len(above)              // data above
            const BYTES = sizeof(Point) * len(points)
            const TEXT = len(text)
            data above: [u8; 3] = [1, 2, 3]
            data star_char: [u8; _] = [64, 65, 66, 67, 68, 69, 70, 71]
            struct Point { x: u8, y: u16 }
            var points: [Point; 5]
            var text: [u8; 5] = "HELLO"
            var a: u8
            var b: u8
            var c: u8
            var d: u8
            var seen: [u8; KINDS]
            fn main() {
                a = KINDS
                b = ABOVE
                c = BYTES
                d = TEXT
            }
            """);
        Assert.Equal(8, r.U8("a"));
        Assert.Equal(3, r.U8("b"));
        Assert.Equal(15, r.U8("c"));
        Assert.Equal(5, r.U8("d"));
        Assert.Equal(8, r.Result.Variables["seen"].Size);
    }

    [Fact]
    public void Len_of_another_modules_data_in_a_constant()
    {
        Write("video.tern", "data star_char: [u8; _] = [1, 2, 3, 4, 5, 6]\nstruct Tile { a: u8, b: u8, c: u8 }");
        var r = RunMain("""
            import video
            const KINDS: u8 = len(video.star_char)
            const TILE = sizeof(video.Tile)
            var a: u8
            var b: u8
            fn main() { a = KINDS; b = TILE }
            """);
        Assert.Equal(6, r.U8("a"));
        Assert.Equal(3, r.U8("b"));
    }

    [Fact]
    public void Constants_across_an_import_ring_of_three_modules()
    {
        // The port's ring: video imports stars, stars imports explode, explode imports video.
        Write("video.tern", """
            import stars
            const MC: u8 = 8
            const MAP_W: u8 = 20
            const MAP_H: u8 = 12
            fn shown() -> u8 { return stars.shown }
            """);
        Write("stars.tern", """
            import explode
            var shown: u8 = explode.COLOR
            fn hit() { explode.cover[0] = 1 }
            """);
        Write("explode.tern", """
            import video
            const COLOR: u8 = 3 | video.MC
            var cover: [u8; video.MAP_W * video.MAP_H]
            """);
        var r = RunMain("""
            import video
            import stars
            import explode
            var a: u8
            var size: u8
            fn main() {
                a = video.shown()
                stars.hit()
                size = len(explode.cover)
            }
            """);
        Assert.Equal(11, r.U8("a"));
        Assert.Equal(240, r.U8("size"));
        Assert.Equal(240, r.Result.Variables["explode.cover"].Size);
    }

    [Fact]
    public void A_constant_that_depends_on_itself_names_the_chain()
    {
        Assert.Equal("'A' depends on itself: A -> A. Give one of them a value that does not use the others",
            Fail("const A = A + 1\nfn main() { }").Description);
        Assert.Equal("'A' depends on itself: A -> B -> C -> A. Give one of them a value that does not use the others",
            Fail("const A = B\nconst B = C + 1\nconst C = A * 2\nfn main() { }").Description);
        // Through a global's length: K is len(t), and t's length is K.
        Assert.Equal("'K' depends on itself: K -> t -> K. Give one of them a value that does not use the others",
            Fail("const K = len(t)\nvar t: [u8; K]\nfn main() { }").Description);

        Write("a.tern", "import b\nconst X: u8 = b.Y + 1");
        Write("b.tern", "import a\nconst Y: u8 = a.X");
        var e = Fail("import a\nfn main() { }");
        Assert.Equal("'Y' depends on itself: b.Y -> a.X -> b.Y. Give one of them a value that does not use the others", e.Description);
        Assert.Equal(Path.Combine(Src, "b.tern"), e.Pos.File);
    }

    [Fact]
    public void The_suggestion_for_a_cycle_is_the_fix()
    {
        var d = Tern.Compiler.Tools.Diagnostic.Split("'A' depends on itself: A -> A. Give one of them a value that does not use the others");
        Assert.Equal("Give one of them a value that does not use the others", d.Suggestion);
    }
}
