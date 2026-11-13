using Tern.Compiler.Tools;

namespace Tern.Compiler.Tests;

/// <summary><c>--target</c> (spec 12.2): every command names the machine it builds for.</summary>
public sealed class TargetTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tern-target-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string Write(string source)
    {
        var path = Path.Combine(_dir.FullName, "main.tern");
        File.WriteAllText(path, source);
        return path;
    }

    private static (int Code, string Output, string Error) Tern(params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        int code = Cli.Run(args, output, error);
        return (code, output.ToString(), error.ToString().Trim());
    }

    [Theory]
    [InlineData]
    [InlineData("run")]
    [InlineData("bench")]
    public void Every_command_needs_a_target(params string[] command)
    {
        var (code, _, error) = Tern([.. command, Write("var n: u8\nfn main() { n = 1 }\n")]);
        Assert.Equal(1, code);
        Assert.Equal($"No target given: write {Platform.Choices}. Run 'tern --help' for the options.", error);
    }

    [Fact]
    public void An_unknown_target_lists_the_known_ones()
    {
        var (code, _, error) = Tern("--target", "vic20", Write("fn main() { }\n"));
        Assert.Equal(1, code);
        Assert.Equal($"Unknown target 'vic20': write {Platform.Choices}. Run 'tern --help' for the options.", error);
    }

    [Fact]
    public void The_target_may_come_anywhere_in_the_command()
    {
        var path = Write("var n: u8\nfn main() { n = 7 }\n");
        Assert.Equal(0, Tern(path, "--target", "c64").Code);
        var (code, output, _) = Tern("run", path, "--dump", "n", "--target", "c64");
        Assert.Equal(0, code);
        Assert.Contains("u8 = 7", output);
    }

    [Fact]
    public void The_compile_result_names_its_platform()
    {
        Assert.Same(Platform.C64, TernCompiler.Compile("fn main() { }", Platform.C64).Platform);
    }
}
