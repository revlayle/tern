using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>Plug-ins against a fixed interface (spec 12.3): <c>export level_api</c> in a binary, <c>export f, g @ addr</c> in a program.</summary>
public sealed class PluginTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tern");

    public void Dispose() => _dir.Delete(recursive: true);

    private string PathOf(string name) => Path.Combine(_dir.FullName, name);

    private void Write(string name, string text) => File.WriteAllText(PathOf(name), text);

    /// <summary>The interface every level is built against, written by hand.</summary>
    private static readonly string LevelApi = """
        struct LevelHeader {
            width: u8
            height: u8
            start_x: u8
        }
        const SLOT_FIRST = $6000
        const SLOT_LAST = $6FFF
        fn setup() @ $6000
        fn enter(level: u8 using A) -> u8 using A @ $6003 keeps X
        fn update(t: u8 using X) -> bool using carry @ $6006
        fn leave() @ $6009 keeps X, Y
        volatile var header: LevelHeader @ $600C
        """.ReplaceLineEndings("\n");

    /// <summary>A level: its functions in another order than the interface's.</summary>
    private static readonly string Level = """
        import level_api
        memory {
            level_api.SLOT_FIRST..=level_api.SLOT_LAST  program
            $FB..=$FE                                   zeropage
        }
        export level_api

        data header: level_api.LevelHeader @ $600C = level_api.LevelHeader { width = 40, height = 12, start_x = 3 }
        var n: u8

        fn leave() { n = 0 }
        fn update(t: u8 using X) -> bool using carry {
            n += t
            return n >= 10
        }
        fn enter(level: u8 using A) -> u8 using A {
            n = level
            return n + 1
        }
        """.ReplaceLineEndings("\n");

    private BinaryTests.Binary Build(string level, string? api = null)
    {
        Write("level_api.tern", api ?? LevelApi);
        Write("level1.tern", level);
        return new BinaryTests.Binary(level, PathOf("level1.tern"));
    }

    private CompileError Error(string level, string? api = null)
    {
        Write("level_api.tern", api ?? LevelApi);
        Write("level1.tern", level);
        return Assert.Throws<CompileError>(() => TernCompiler.Compile(level, sourcePath: PathOf("level1.tern")));
    }

    [Fact]
    public void Each_function_is_placed_at_its_routines_address()
    {
        var b = Build(Level);
        Assert.Equal(0x6000, b.Result.LoadAddress);
        Assert.Equal(new[] { ("setup", 0x6000), ("enter", 0x6003), ("update", 0x6006), ("leave", 0x6009) }, b.Result.Entries);
        for (int i = 0; i < 4; i++) Assert.Equal(0x4C, b.Result.Image[3 * i]);          // jmp
        int Target(int entry) => b.Result.Image[entry + 1] | (b.Result.Image[entry + 2] << 8);
        Assert.Equal(b.Result.Symbols["enter"], Target(3));
        Assert.Equal(b.Result.Symbols["update"], Target(6));
        Assert.Equal(b.Result.Symbols["leave"], Target(9));
        Assert.Equal(new byte[] { 40, 12, 3 }, b.Result.Image[0x0C..0x0F]);           // the header, at $600C
        Assert.Contains("Jump table at $6000:", b.Result.Report);
    }

    [Fact]
    public void The_entries_work_when_called_through_the_interface_addresses()
    {
        var b = Build(Level);
        b.Call("setup");
        b.Call("enter", a: 4, x: 0x77);
        Assert.Equal(5, b.Cpu.A);
        Assert.Equal(0x77, b.Cpu.X);                       // enter keeps X, as promised
        b.Call("update", x: 3);
        Assert.False(b.Cpu.C);                             // 7
        b.Call("update", x: 3);
        Assert.True(b.Cpu.C);                              // 10
        b.Call("leave", x: 0x11, y: 0x22);
        Assert.Equal((0x11, 0x22), (b.Cpu.X, b.Cpu.Y));
        Assert.Equal(0, b.Ram.Bytes[b.Result.Symbols["n"]]);
    }

    [Fact]
    public void Two_levels_written_in_different_orders_have_the_same_table()
    {
        var first = Build(Level);
        var reordered = Level.Replace("fn leave() { n = 0 }\n", "") + "\nfn leave() { n = 0 }\n";
        var second = Build(reordered.Replace("export level_api\n", "") + "\nexport level_api\n");
        Assert.Equal(first.Result.Entries, second.Result.Entries);
    }

    [Fact]
    public void A_gap_between_routines_is_zeros()
    {
        var api = LevelApi.Replace("fn leave() @ $6009", "fn leave() @ $6010").Replace("@ $600C", "@ $6020");
        var b = Build(Level.Replace("@ $600C", "@ $6020"), api);
        Assert.Equal(new[] { ("setup", 0x6000), ("enter", 0x6003), ("update", 0x6006), ("leave", 0x6010) }, b.Result.Entries);
        Assert.Equal(new byte[7], b.Result.Image[0x09..0x10]);
        Assert.Equal(0x4C, b.Result.Image[0x10]);
        b.Call("setup");
        b.Call("enter", a: 1);
        b.Call("leave");
        Assert.Equal(0, b.Ram.Bytes[b.Result.Symbols["n"]]);
    }

    [Fact]
    public void The_interface_module_is_still_written()
    {
        var api = Build(Level).Result.ApiModule!;
        Assert.Contains("fn setup() @ $6000", api);
        Assert.Contains("fn enter(level: u8 using A) -> u8 using A @ $6003 keeps X, Y", api);
        Assert.Contains("fn leave() @ $6009 keeps X, Y", api);
        Assert.Contains("const CODE_FIRST = $6000", api);
    }

    [Theory]
    [InlineData("fn leave() { n = 0 }\n", "", "'level_api.leave' has no function in this binary. Write 'fn leave()' in the main file")]
    [InlineData("fn enter(level: u8 using A) -> u8 using A {", "fn enter(level: u8 using X) -> u8 using A {",
        "'enter' does not match 'level_api.enter': it is 'fn enter(level: u8 using X) -> u8 using A', the interface says 'fn enter(level: u8 using A) -> u8 using A'. Change the function to match")]
    [InlineData("fn enter(level: u8 using A) -> u8 using A {", "fn enter(level: i8 using A) -> u8 using A {", "'enter' does not match 'level_api.enter'")]
    [InlineData("fn update(t: u8 using X) -> bool using carry {", "fn update(t: u8 using X) -> bool using zero {", "'update' does not match 'level_api.update'")]
    [InlineData("fn update(t: u8 using X) -> bool using carry {\n    n += t\n    return n >= 10", "fn update(t: u8 using X) {\n    n += t", "'update' does not match 'level_api.update'")]
    [InlineData("fn leave() { n = 0 }", "fn leave(k: u8 using Y) { n = k }", "'leave' does not match 'level_api.leave'")]
    [InlineData("fn leave() { n = 0 }", "inline fn leave() { n = 0 }", "'leave' is inline")]
    [InlineData("fn leave() { n = 0 }", "interrupt fn leave() { n = 0 }", "'leave' is an interrupt handler")]
    [InlineData("fn leave() { n = 0 }", "var leave: u8", "'level_api.leave' has no function in this binary")]
    [InlineData("fn leave() { n = 0 }", "fn leave() { n = 0 }\nfn setup() { }", "'setup' is the name of the binary's startup entry")]
    [InlineData("data header: level_api.LevelHeader @ $600C = level_api.LevelHeader { width = 40, height = 12, start_x = 3 }\n", "",
        "'level_api.header' is data every binary of level_api provides. Declare 'data header: level_api.LevelHeader @ $600C = ...' in the main file")]
    [InlineData("data header: level_api.LevelHeader @ $600C", "data header: level_api.LevelHeader @ $6010",
        "'header' does not match 'level_api.header'. Declare it as 'data header: level_api.LevelHeader @ $600C = ...'")]
    [InlineData("data header: level_api.LevelHeader @ $600C = level_api.LevelHeader { width = 40, height = 12, start_x = 3 }", "data header: [u8; 3] @ $600C = [40, 12, 3]",
        "'header' does not match 'level_api.header'")]
    [InlineData("data header: level_api.LevelHeader @ $600C = level_api.LevelHeader { width = 40, height = 12, start_x = 3 }", "var header: level_api.LevelHeader @ $600C",
        "'header' does not match 'level_api.header'")]
    [InlineData("export level_api", "export level_api, leave", "'export level_api' takes the entries and their addresses from level_api, so the export line names nothing else. Declare a routine for 'leave' in level_api instead")]
    [InlineData("export level_api", "export leave, level_api", "names nothing else. Declare a routine for 'leave' in level_api instead")]
    [InlineData("level_api.SLOT_FIRST..=", "$5000..=", "'level_api.setup' is at $6000, but setup is entry 0 of the jump table, at the start of the binary's first program area ($5000). Move setup to $5000 in level_api, or start the first program area at $6000")]
    [InlineData("fn leave() { n = 0 }", "fn leave() { n = 0\n    for i in 0..3 using X { n += 1 } }", "'leave' changes X, which 'level_api.leave' promises to keep. Change the function so that it leaves X alone, or remove X from the routine's 'keeps' list")]
    public void Reports_errors_in_the_binary(string replace, string with, string expected)
    {
        Assert.Contains(replace, Level);
        var e = Error(Level.Replace(replace, with));
        Assert.Contains(expected, e.Description);
    }

    [Theory]
    [InlineData("fn setup() @ $6000\n", "", "level_api has no 'setup' routine. The host calls setup first, at entry 0 of the jump table: declare 'fn setup() @ $6000' in level_api")]
    [InlineData("fn setup() @ $6000", "fn setup() @ $6100", "'level_api.setup' is at $6100, but setup is entry 0")]
    [InlineData("fn setup() @ $6000", "fn setup(n: u8 using A) @ $6000", "'level_api.setup' must take nothing and return nothing")]
    [InlineData("@ $6006\n", "@ $6004\n", "'level_api.update' at $6004 overlaps the entry before it: each entry of the jump table is a 3-byte jmp, from $6000. Move it to $6006 or later")]
    [InlineData("fn leave() @ $6009 keeps X, Y", "fn leave(true using carry) @ $6009", "'level_api.leave' has fixed inputs, which a function of the binary cannot receive")]
    [InlineData("fn setup() @ $6000", "fn setup() @ $6000 keeps A", "'level_api.setup' promises to keep A, but the binary's startup code changes it. Remove A from its 'keeps' list")]
    public void Reports_errors_in_the_interface(string replace, string with, string expected)
    {
        Assert.Contains(replace, LevelApi);
        var e = Error(Level, LevelApi.Replace(replace, with));
        Assert.Contains(expected, e.Description);
    }

    [Fact]
    public void A_program_with_main_cannot_export_a_module()
    {
        Write("level_api.tern", LevelApi);
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("import level_api\nfn main() { }\nexport level_api @ $0900", sourcePath: PathOf("host.tern")));
        Assert.Equal("'export level_api' builds a binary that plugs into level_api's jump table, which a program with 'main' does not. List the program's own functions: 'export f, g @ address'", e.Description);
    }
}
