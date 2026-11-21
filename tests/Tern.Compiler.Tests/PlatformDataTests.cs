using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// Platform facts as data (R6; review code-quality-semantics 16, syntax 6): each rule follows a property
/// of the platform's description, not its name or whether it has boards. The tests make platforms
/// that differ from the four in one property, as a new platform (a C64 with cartridges, the C128) would.
/// </summary>
public class PlatformDataTests
{
    /// <summary>A copy of <paramref name="p"/> with another name, boards and cartridge rule, and without its machine in tern run if <paramref name="machine"/> is false.</summary>
    internal static Platform Like(Platform p, string name, IReadOnlyList<Board>? boards = null, bool cartridgeRequired = false,
        bool machine = true) => new()
    {
        Name = name, Title = name.ToUpperInvariant(), Cpu = p.Cpu, LoadAddress = p.LoadAddress, Header = p.Header, HeaderName = p.HeaderName, ZeroPage = p.ZeroPage,
        RamEnd = p.RamEnd, VolatileAreas = p.VolatileAreas, DefaultEncoding = p.DefaultEncoding, Format = p.Format, Labels = p.Labels,
        Rom = p.Rom, Boards = boards ?? p.Boards, CartridgeRequired = cartridgeRequired, VariableRam = p.VariableRam, NewMachine = machine ? p.NewMachine : null,
    };

    /// <summary>Boards do not make every program a cartridge: before R6 a platform with boards needed a 'cartridge' line.</summary>
    [Fact]
    public void A_platform_with_boards_does_not_require_a_cartridge_line()
    {
        var withBoards = Like(Platform.C64, "c64crt", [Board.Nrom]);
        var run = Run.Program("var x: u8\nfn main() { x = 7 }", platform: withBoards);
        Assert.Equal(7, run.U8("x"));
    }

    /// <summary>Data is ROM only in a cartridge: before R6 an asm store to data was allowed on any platform with boards.</summary>
    [Fact]
    public void Data_is_read_only_without_a_cartridge_on_a_platform_with_boards()
    {
        var withBoards = Like(Platform.C64, "c64crt", [Board.Nrom]);
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("data d: [u8; 2] = [1, 2]\nfn main() { asm {\n inc d\n } }", withBoards));
        Assert.Equal("'d' is data, which is read-only. Declare it with 'var' to change it", e.Description);
    }

    /// <summary>
    /// The startup section names the platform's header (review code-quality-backend 13): before R6 every
    /// platform's message named "the BASIC line and startup code", read from the section's name.
    /// </summary>
    [Theory]
    [InlineData("c64", 1, 1, "'first' at $0801-$0803 overlaps the BASIC line and startup code at $0801-$0813, which must be at the start of the first program area. Move the data, or start the program area lower")]
    [InlineData("atari", 1, 1, "'first' at $2000-$2002 overlaps the startup code at $2000-$2006, which must be at the start of the first program area. Move the data, or start the program area lower")]
    public void Data_over_the_startup_code_names_what_is_there(string target, int line, int column, string expected)
    {
        var platform = Platform.ByName(target)!;
        Expect.Error(() => TernCompiler.Compile($"data first: [u8; 3] @ ${platform.LoadAddress:X4} = [1, 2, 3]\nfn main() {{ }}", platform), line, column, expected);
    }

    /// <summary>The cartridge rule is the platform's, whatever its name.</summary>
    [Fact]
    public void A_platform_that_requires_a_cartridge_says_so_by_its_title()
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("fn main() { }", Like(Platform.Nes, "famicom", cartridgeRequired: true)));
        Expect.Message(e, 1, 1, "A FAMICOM program is a cartridge image, so its memory block names the board. Write 'memory { cartridge nrom vertical }' (boards: nrom, uxrom; mirroring: vertical or horizontal)");
    }
}
