using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// Platform folders in a program (spec 2.2; V1 fixes 2): <c>import video</c> finds <c>&lt;folder&gt;/&lt;target&gt;/video.tern</c>
/// before <c>&lt;folder&gt;/video.tern</c>; a file in a platform folder imports as if it were in the folder above;
/// another platform's folder is never searched.
/// </summary>
public sealed class PlatformFolderTests : TempFolderTest
{
    private string Lib => Path.Combine(_dir.FullName, "lib");
    private string Game => Path.Combine(_dir.FullName, "game");

    public PlatformFolderTests() => Directory.CreateDirectory(Lib);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(Game, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private const string Nrom = "memory {\n    cartridge nrom vertical\n}\n";

    /// <summary>Main's text for the platform: the NES needs its cartridge, after the imports.</summary>
    private static string Main(Platform platform, string imports, string rest) =>
        imports + "\n" + (platform == Platform.Nes ? Nrom : "") + rest;

    private Run RunMain(Platform platform, string relative, string imports, string rest) =>
        Run.Program(Main(platform, imports, rest), sourcePath: Path.Combine(Game, relative), libraryDirectory: Lib, platform: platform);

    private CompileError Fails(Platform platform, string relative, string imports, string rest) =>
        Assert.Throws<CompileError>(() => TernCompiler.Compile(Main(platform, imports, rest), platform, Path.Combine(Game, relative), Lib));

    /// <summary>The shared modules and two platform folders of spec 2.2's example.</summary>
    private void WriteGame()
    {
        Write("score.tern", "var points: u8\nfn add(n: u8) { points += n }");
        Write("sound.tern", "fn kind() -> u8 { return 1 }");
        Write("c64/video.tern", "import score\nimport tiles\nfn draw() -> u8 { score.add(10)\n return tiles.ID }");
        Write("c64/tiles.tern", "const ID = 64");
        Write("nes/video.tern", "import score\nimport tiles\nfn draw() -> u8 { score.add(20)\n return tiles.ID }");
        Write("nes/tiles.tern", "const ID = 78");
        Write("nes/sound.tern", "fn kind() -> u8 { return 2 }");
    }

    private const string Imports = "import video\nimport sound\nimport score";
    private const string Body = "var drawn: u8\nvar k: u8\nvar p: u8\nfn main() {\n drawn = video.draw()\n k = sound.kind()\n p = score.points\n}";

    [Theory]
    [InlineData("c64", 64, 1, 10)]
    [InlineData("nes", 78, 2, 20)]
    public void Each_target_finds_its_own_modules(string target, int drawn, int kind, int points)
    {
        WriteGame();
        var r = RunMain(Platform.ByName(target)!, "main.tern", Imports, Body);
        Assert.Equal(drawn, r.U8("drawn"));
        // nes/sound.tern replaces the shared sound.tern on the NES only.
        Assert.Equal(kind, r.U8("k"));
        Assert.Equal(points, r.U8("p"));
        // The module's name is its file name, wherever it was found.
        Assert.Contains("video.draw", r.Result.Symbols.Keys);
    }

    [Fact]
    public void A_main_file_in_a_platform_folder_imports_as_if_from_the_folder_above()
    {
        WriteGame();
        var r = RunMain(Platform.Nes, "nes/main.tern", Imports, Body);
        Assert.Equal(78, r.U8("drawn"));
        Assert.Equal(2, r.U8("k"));
        Assert.Equal(20, r.U8("p"));
    }

    [Fact]
    public void Another_platforms_folder_is_never_searched()
    {
        Write("atari/video.tern", "fn draw() -> u8 { return 1 }");
        var e = Fails(Platform.Nes, "main.tern", "import video", "fn main() { var d: u8 = video.draw() }");
        Expect.Message(e, 1, 8, $"'video' is a module for the atari platform ({Path.Combine(Game, "atari", "video.tern")}); this program is compiled for nes. Add nes/video.tern, or remove the import");
        Assert.Equal("Add nes/video.tern, or remove the import", Tools.Diagnostic.Split(e.Description).Suggestion);

        // A file in the atari folder does not reach it either, in another target's build.
        Write("nes/start.tern", "import video\nfn go() { }");
        e = Fails(Platform.Nes, "main.tern", "import start", "fn main() { start.go() }");
        Assert.StartsWith("'video' is a module for the atari platform", e.Description);
    }

    [Fact]
    public void A_main_file_in_another_platforms_folder_is_an_error()
    {
        var e = Fails(Platform.Nes, "c64/main.tern", "", "fn main() { }");
        Expect.Message(e, 1, 1, $"This file is in the folder for the c64 platform ({Path.Combine(Game, "c64")}); this program is compiled for nes. Use --target c64 for it, or a main file outside c64/");
    }

    [Fact]
    public void A_platform_folders_module_may_not_have_a_library_modules_name()
    {
        Write(Path.Combine(Lib, "random.tern"), "fn seed(s: u8) { }");
        Write("c64/random.tern", "fn seed(s: u8) { }");
        var e = Fails(Platform.C64, "main.tern", "import random", "fn main() { random.seed(1) }");
        Expect.Message(e, 1, 8, $"'random' is both a library module ({Path.Combine(Lib, "random.tern")}) and your file {Path.Combine(Game, "c64", "random.tern")}. Rename your file, e.g. to my_random.tern, and import it by its new name");
    }
}
