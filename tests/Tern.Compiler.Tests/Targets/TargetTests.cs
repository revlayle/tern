namespace Tern.Compiler.Tests;

/// <summary><c>--target</c> (spec 12.2): every command names the machine it builds for.</summary>
public sealed class TargetTests : TempFolderTest
{

    private string Write(string source)
    {
        var path = Path.Combine(_dir.FullName, "main.tern");
        File.WriteAllText(path, source);
        return path;
    }

    [Theory]
    [InlineData]
    [InlineData("run")]
    [InlineData("bench")]
    public void Every_command_needs_a_target(params string[] command)
    {
        var (code, _, error) = Command.Tern([.. command, Write("var n: u8\nfn main() { n = 1 }\n")]);
        Assert.Equal(1, code);
        Assert.Equal($"No target given. Write {Platform.Choices}. Run 'tern --help' for the options.", error.Trim());
    }

    [Fact]
    public void An_unknown_target_lists_the_known_ones()
    {
        var (code, _, error) = Command.Tern("--target", "vic20", Write("fn main() { }\n"));
        Assert.Equal(1, code);
        Assert.Equal($"Unknown target 'vic20'. Use {Platform.Choices}. Run 'tern --help' for the options.", error.Trim());
    }

    [Fact]
    public void A_second_target_is_an_error()
    {
        // Review M25, note 7.
        var (code, _, error) = Command.Tern("--target", "c64", "--target", "atari", Write("fn main() { }\n"));
        Assert.Equal(1, code);
        Assert.Equal("--target is given more than once. Give one target. Run 'tern --help' for the options.", error.Trim());
    }

    [Fact]
    public void The_target_may_come_anywhere_in_the_command()
    {
        var path = Write("var n: u8\nfn main() { n = 7 }\n");
        Assert.Equal(0, Command.Tern(path, "--target", "c64").Code);
        var (code, output, _) = Command.Tern("run", path, "--dump", "n", "--target", "c64");
        Assert.Equal(0, code);
        Assert.Contains("u8 = 7", output);
    }

    [Fact]
    public void The_compile_result_names_its_platform()
    {
        Assert.Same(Platform.C64, TernCompiler.Compile("fn main() { }", Platform.C64).Platform);
    }
}
