using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// Overlay interfaces (spec 8.5): <c>import level1 @ levels implements level_api</c> puts a jmp at each of the
/// interface's routines in level1's file, so resident code calls whichever level is loaded.
/// </summary>
public class ImplementsTests
{
    private const string Api = """
        fn init(n: u8 using A) @ $6000
        fn update() -> u8 using A @ $6003
        """;

    private const string Level1 = """
        var base: u8 = 10
        var count: u8
        fn init(n: u8 using A) { count = n }
        fn update() -> u8 using A {
            count += 1
            return base + count
        }
        """;

    private const string Level2 = """
        var steps: u8 = 100
        fn init(n: u8 using A) { steps -= n }
        fn update() -> u8 using A {
            steps -= 1
            return steps
        }
        """;

    private const string Header = """
        import level_api
        import level1 @ levels implements level_api
        import level2 @ levels implements level_api
        memory {
            $0801..=$5FFF  program
            $6000..=$6FFF  overlay levels
        }
        fn load(n: u8 using A) @ $C000 keeps X, Y
        """;

    private static Dictionary<string, string> Files(string? api = null, string? level1 = null, string? level2 = null, params (string Name, string Text)[] more)
    {
        var files = new Dictionary<string, string> { ["level_api"] = api ?? Api, ["level1"] = level1 ?? Level1, ["level2"] = level2 ?? Level2 };
        foreach (var (name, text) in more) files[name] = text;
        return files;
    }

    /// <summary>Writes the files into a temporary folder and gives the main file's path.</summary>
    private static T With<T>(IReadOnlyDictionary<string, string> files, Func<string, T> work)
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            foreach (var (name, text) in files) File.WriteAllText(Path.Combine(dir.FullName, name + ".tern"), text);
            return work(Path.Combine(dir.FullName, "main.tern"));
        }
        finally { dir.Delete(recursive: true); }
    }

    /// <summary>Stand-in for the KERNAL load: <c>load(n)</c> copies overlay <c>n</c> (1-based, in module order) to its address.</summary>
    private static readonly IReadOnlyDictionary<int, Action<Run>> Loader = new Dictionary<int, Action<Run>>
    {
        [0xC000] = run =>
        {
            var file = run.Result.Overlays[run.Cpu.A - 1];
            run.Ram.Load((ushort)file.LoadAddress, file.Image);
        },
    };

    private static Run Compile(string main, IReadOnlyDictionary<string, string>? files = null, long irqEvery = 0) =>
        With(files ?? Files(), path => Run.Program(main, 5_000_000, sourcePath: path, traps: Loader, irqEvery: irqEvery));

    private static CompileResult Build(string main, IReadOnlyDictionary<string, string>? files = null) =>
        With(files ?? Files(), path => TernCompiler.Compile(main, sourcePath: path));

    private static string Message(string main, IReadOnlyDictionary<string, string>? files = null) =>
        With(files ?? Files(), path => Assert.Throws<CompileError>(() => TernCompiler.Compile(main, sourcePath: path)).Description);

    private static int Word(byte[] image, int offset) => image[offset] | image[offset + 1] << 8;

    // ---------- Calls through the interface ----------

    [Trait("Platform", "c64")]
    [Fact]
    public void Calls_through_the_interface_reach_the_overlay_that_is_loaded()
    {
        var r = Compile($$"""
            {{Header}}
            var a: u8
            var b: u8
            var c: u8
            var d: u8
            fn main() {
                load(1)
                level_api.init(5)
                a = level_api.update()      // 10 + 6
                b = level_api.update()      // 10 + 7
                load(2)
                level_api.init(5)           // steps 95
                c = level_api.update()      // 94
                load(1)                     // the file sets level1's variables again
                d = level_api.update()      // 10 + 1
            }
            """);
        Assert.Equal(16, r.U8("a"));
        Assert.Equal(17, r.U8("b"));
        Assert.Equal(94, r.U8("c"));
        Assert.Equal(11, r.U8("d"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_call_to_an_interface_routine_is_a_jsr_to_its_address()
    {
        var r = Build($$"""
            {{Header}}
            fn main() { load(1); level_api.init(5) }
            """);
        Assert.Matches(@"20 00 60 +jsr \$6000", Listing.Text(r));
    }

    [Trait("Platform", "c64")]
    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Each_overlay_file_has_a_jmp_at_each_routine_address_to_its_own_function()
    {
        var r = Compile($$"""
            {{Header}}
            fn main() { load(1); level_api.init(5); load(2); level_api.init(5) }
            """);
        foreach (var file in r.Result.Overlays)
        {
            Assert.Equal(0x6000, file.LoadAddress);
            Assert.Equal(0x4C, file.Image[0]);
            Assert.Equal(r.Address($"{file.Name}.init"), Word(file.Image, 1));
            Assert.Equal(0x4C, file.Image[3]);
            Assert.Equal(r.Address($"{file.Name}.update"), Word(file.Image, 4));
        }
        // Each overlay's table entry for update: a jmp at $6003 to its own function.
        Assert.Equal(["jmp level1.update", "jmp level2.update"], Listing.Instructions(r, "level_api.update"));
        Assert.All(r.Result.Instructions.Where(i => i.Section == "level_api.update"), i => Assert.Equal(0x6003, i.Address));
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void The_module_code_goes_around_table_entries_with_gaps_between_them()
    {
        var r = Compile($$"""
            {{Header}}
            var a: u8
            var c: u8
            fn main() {
                load(1)
                level_api.init(5)
                a = level_api.update()      // 16
                load(2)
                level_api.init(5)
                c = level_api.update()      // 94
            }
            """, Files(api: """
            fn init(n: u8 using A) @ $6010
            fn update() -> u8 using A @ $6100
            """));
        Assert.Equal(16, r.U8("a"));
        Assert.Equal(94, r.U8("c"));
        var file = r.Result.Overlays[0];
        Assert.Equal(0x4C, file.Image[0x10]);
        Assert.Equal(r.Address("level1.init"), Word(file.Image, 0x11));
        Assert.Equal(0x4C, file.Image[0x100]);
        Assert.Equal(r.Address("level1.update"), Word(file.Image, 0x101));
        // Code fills the space before the first entry, first fit.
        Assert.True(r.Address("level1.init") < 0x6010 || r.Address("level1.update") < 0x6010);
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void A_plain_call_to_one_module_next_to_a_call_through_the_interface()
    {
        var r = Compile($$"""
            {{Header}}
            var a: u8
            var b: u8
            fn main() {
                load(1)
                level1.init(2)
                a = level1.update()         // 13, plain call
                b = level_api.update()      // 14, through the table
            }
            """);
        Assert.Equal(13, r.U8("a"));
        Assert.Equal(14, r.U8("b"));
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Implementations_are_compiled_even_when_nothing_calls_the_interface()
    {
        var r = Compile($$"""
            {{Header}}
            var a: u8
            fn main() {
                load(2)
                asm {
                    lda #4
                    jsr $6000
                    jsr $6003
                    sta a
                }
            }
            """);
        Assert.Equal(95, r.U8("a"));    // 100 - 4 - 1
        Assert.Contains("level1.update", r.Result.Symbols.Keys);
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Frames_of_the_implementations_are_shared_with_other_code()
    {
        var files = Files(level1: """
            fn init(n: u8 using A) { }
            fn update() -> u8 using A {
                var t: u8 = 3
                var u: u8 = 4
                for i in 0..10 { t += u }
                return t
            }
            """, level2: """
            fn init(n: u8 using A) { }
            fn update() -> u8 using A {
                var w: u8 = 5
                var z: u8 = 6
                for i in 0..10 { w += z }
                return w
            }
            """);
        var r = Compile($$"""
            {{Header}}
            var a: u8
            var b: u8
            var c: u8
            fn other() -> u8 {
                var k: u8 = 1
                var m: u8 = 2
                for i in 0..10 { k += m }
                return k
            }
            fn main() {
                load(1)
                a = level_api.update()      // 43
                load(2)
                b = level_api.update()      // 65
                c = other()                 // 21
            }
            """, files);
        Assert.Equal(43, r.U8("a"));
        Assert.Equal(65, r.U8("b"));
        Assert.Equal(21, r.U8("c"));
        // The two levels never run together, nor with other(): one frame for all three.
        var frames = new[] { "level1.update", "level2.update", "other" }
            .Select(f => r.Result.Variables.Values.Where(v => v.Function == f).Min(v => v.Address)).ToList();
        Assert.Single(frames.Distinct());
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void An_implementation_does_not_share_memory_with_the_function_that_calls_the_interface()
    {
        var r = Compile($$"""
            {{Header}}
            var a: u8
            var b: u8
            fn play(keep: u8) -> u8 {
                var mine: u8 = keep + 1
                var got = level_api.update()
                return mine + got - keep
            }
            fn main() {
                load(1)
                a = play(40)        // 41 + 11 - 40
                load(2)
                b = play(40)        // 41 + 50 - 40
            }
            """, Files(level1: """
            fn init(n: u8 using A) { }
            fn update() -> u8 using A {
                var t: u8 = 11
                var u: u8 = 22
                return t + u - 22
            }
            """, level2: """
            fn init(n: u8 using A) { }
            fn update() -> u8 using A {
                var w: u8 = 50
                var z: u8 = 60
                return w + z - 60
            }
            """));
        Assert.Equal(12, r.U8("a"));
        Assert.Equal(51, r.U8("b"));
        var play = r.Result.Variables.Values.Where(v => v.Function == "play").Max(v => v.Address + v.Size);
        foreach (var level in new[] { "level1.update", "level2.update" })
            Assert.True(r.Result.Variables.Values.Where(v => v.Function == level).Min(v => v.Address) >= play, level);
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void A_function_called_from_both_an_implementation_and_main_does_not_share_its_frame_with_it()
    {
        var helper = ("util", """
            fn twice(x: u8) -> u8 {
                var y: u8 = x
                return y + y
            }
            """);
        var r = Compile($$"""
            {{Header.Replace("import level_api", "import level_api\nimport util")}}
            var a: u8
            var b: u8
            fn main() {
                load(1)
                a = level_api.update()      // twice(11)
                b = util.twice(50)
            }
            """, Files(level1: """
            import util
            fn init(n: u8 using A) { }
            fn update() -> u8 using A {
                var keep: u8 = 11
                var r: u8 = util.twice(keep)
                return r + keep - 11
            }
            """, more: helper));
        Assert.Equal(22, r.U8("a"));
        Assert.Equal(100, r.U8("b"));
        // util.twice runs inside level1.update, so their frames do not overlap.
        var update = r.Result.Variables.Values.Where(v => v.Function == "level1.update").Select(v => (v.Address, v.Address + v.Size - 1)).ToList();
        var twice = r.Result.Variables.Values.Where(v => v.Function == "util.twice").Select(v => (v.Address, v.Address + v.Size - 1)).ToList();
        Assert.DoesNotContain(update, u => twice.Any(t => u.Item1 <= t.Item2 && t.Item1 <= u.Item2));
    }

    [Fact]
    public void An_implementation_reached_through_the_interface_from_main_and_a_handler_is_in_the_report()
    {
        var r = Build($$"""
            {{Header}}
            interrupt fn tick() { level_api.update() }
            fn main() { load(1); level_api.update() }
            """, Files(level1: """
            fn init(n: u8 using A) { }
            fn update() -> u8 using A {
                var t: u8 = 3
                for i in 0..10 { t += i }
                return t
            }
            """));
        Assert.Matches(@"level1\.update \(\d+ bytes?\): main, tick", r.Report);
    }

    [Fact]
    public void An_implementation_calling_its_own_interface_is_recursion()
    {
        Assert.Contains("Recursion is not allowed: level_api.update -> level1.update -> level_api.update", Message($$"""
            {{Header}}
            fn main() { load(1); level_api.update() }
            """, Files(level1: """
            import level_api
            fn init(n: u8 using A) { }
            fn update() -> u8 using A { return level_api.update() }
            """)));
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Volatile_data_of_the_interface_is_provided_by_each_module()
    {
        var r = Compile($$"""
            {{Header}}
            var a: u8
            var b: u8
            fn main() {
                load(1)
                a = level_api.header[1]
                load(2)
                b = level_api.header[1]
            }
            """, Files(api: Api + "\nvolatile var header: [u8; 2] @ $6006", level1: Level1 + "\ndata header: [u8; 2] @ $6006 = [0, 41]", level2: Level2 + "\ndata header: [u8; 2] @ $6006 = [0, 42]"));
        Assert.Equal(41, r.U8("a"));
        Assert.Equal(42, r.U8("b"));
    }

    private const string ApiWithHeader = Api + "\nvolatile var header: [u8; 2] @ $6006\nfn second() -> u8 using A { return header[1] }";

    [Theory]
    [InlineData("count = level_api.header[1]")]                      // a read with an index
    [InlineData("level_api.header[0] = n")]                         // a write
    [InlineData("var p: ptr u8 = addr(level_api.header)\n p[0] = 1")] // addr()
    [InlineData("asm {\n lda level_api.header+1\n sta count\n }")]   // an asm operand
    public void Inside_an_implementing_module_the_interface_name_of_its_own_data_is_an_error(string use)
    {
        var e = With(Files(api: ApiWithHeader,
                level1: "import level_api\nvar count: u8\ndata header: [u8; 2] @ $6006 = [0, 41]\nfn init(n: u8 using A) {\n " + use + "\n}\nfn update() -> u8 using A { return count }",
                level2: Level2 + "\ndata header: [u8; 2] @ $6006 = [0, 42]"),
            path => Assert.Throws<CompileError>(() => TernCompiler.Compile(Header + "\nfn main() { load(1); level_api.init(1) }", sourcePath: path)));
        Assert.Equal("'level_api.header' is this module's data 'header'. Use 'header'", e.Description);
        Assert.EndsWith("level1.tern", e.Pos.File);
        Assert.Equal(5 + use[..use.IndexOf("level_api.header")].Count(c => c == '\n'), e.Pos.Line);   // at the name
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Resident_code_and_the_interface_own_functions_name_the_interface_variable()
    {
        var r = Compile($$"""
            {{Header}}
            var a: u8
            var b: u8
            var c: u8
            fn main() {
                load(2)
                a = level_api.header[1]         // resident code
                b = level_api.second()          // the interface's own function, called from main
                load(1)
                c = level_api.update()          // level1 calls the interface's helper too
            }
            """, Files(api: ApiWithHeader,
                level1: "import level_api\ndata header: [u8; 2] @ $6006 = [0, 41]\nfn init(n: u8 using A) { }\nfn update() -> u8 using A { return level_api.second() + header[0] }",
                level2: Level2 + "\ndata header: [u8; 2] @ $6006 = [0, 42]"));
        Assert.Equal(42, r.U8("a"));
        Assert.Equal(42, r.U8("b"));
        Assert.Equal(41, r.U8("c"));
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void A_module_implements_two_interfaces()
    {
        var music = ("music_api", "fn play() -> u8 using A @ $6010");
        var r = Compile($$"""
            import level_api
            import music_api
            import level1 @ levels implements level_api, music_api
            import level2 @ levels implements level_api
            memory {
                $0801..=$5FFF  program
                $6000..=$6FFF  overlay levels
            }
            fn load(n: u8 using A) @ $C000 keeps X, Y
            var a: u8
            var b: u8
            fn main() {
                load(1)
                level_api.init(1)
                a = level_api.update()      // 12
                b = music_api.play()        // 77
            }
            """, Files(level1: Level1 + "\nfn play() -> u8 using A { return 77 }", more: music));
        Assert.Equal(12, r.U8("a"));
        Assert.Equal(77, r.U8("b"));
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void A_routine_that_promises_to_keep_a_register_can_rely_on_an_implementation_that_keeps_it()
    {
        var r = Compile($$"""
            {{Header}}
            var a: u8
            fn main() {
                load(1)
                for i in 0..3 using X { level_api.tick() }
                a = level_api.update()      // 10 + 3
            }
            """, Files(api: Api + "\nfn tick() @ $6006 keeps X", level1: Level1 + "\nfn tick() { count += 1 }", level2: Level2 + "\nfn tick() { }"));
        Assert.Equal(14, r.U8("a"));
    }

    [Fact]
    public void The_stages_example_runs_each_stage_through_the_interface_in_tern_run()
    {
        var path = AsmTests.FindExample("stages.tern");
        var program = TernCompiler.Compile(File.ReadAllText(path), sourcePath: path);
        var run = Tools.Runner.Run(program);
        Assert.True(run.Returned);
        var screen = run.Ram.Bytes;
        // Screen codes: letters 1-26, space $20, digits $30-$39.
        Assert.Equal(new byte[] { 6, 15, 18, 5, 19, 20, 0x20, 0x20, 0x20, 0x35 }, screen[0x0400..0x040A]);    // "FOREST", 5
        Assert.Equal(new byte[] { 12, 1, 11, 5, 0x20, 0x20, 0x20, 0x20, 0x20, 0x37 }, screen[0x05E0..0x05EA]); // "LAKE", 7
        Assert.All(Enumerable.Range(40, 440), i => Assert.Equal(0x14, screen[0x0400 + i]));    // 'T'
        Assert.All(Enumerable.Range(520, 480), i => Assert.Equal(0x17, screen[0x0400 + i]));   // 'W'
        Assert.Equal(new byte[] { 5, 7, 5 }, screen[program.Symbols["scores"]..(program.Symbols["scores"] + 3)]);
        Assert.Equal(["stage1", "stage2"], program.Overlays.Select(o => o.Name).Order());
        Assert.Empty(program.Warnings);
    }

    // ---------- Errors ----------

    [Fact]
    public void Implements_is_a_keyword()
    {
        Expect.Error(() => TernCompiler.Compile("fn main() { var implements: u8 = 1 }"), 1, 17, "Expected variable name, found 'implements', which is a keyword. Use another name");
    }

    [Fact]
    public void Implements_on_a_module_not_in_an_overlay_is_an_error()
    {
        Assert.Equal("'implements' puts a jump table in an overlay's file, but 'level1' is not placed in an overlay area. " +
            "Place it with 'import level1 @ area implements level_api'; a resident program offers entries with 'export f, g @ address'",
            Message("""
                import level_api
                import level1 implements level_api
                fn main() { }
                """));
    }

    [Fact]
    public void Implements_on_a_module_import_is_an_error()
    {
        Assert.Contains("'level1' is not placed in an overlay area", Message("""
            import level_api
            import other
            fn main() { other.f() }
            """, Files(more: ("other", """
            import level1 implements level_api
            fn f() { }
            """))));
    }

    [Fact]
    public void An_interface_the_main_file_does_not_import_is_an_error()
    {
        Assert.Equal("'level_apii' is not a module the main file imports; 'implements' names interface modules. Add 'import level_apii' to the main file",
            Message(Header.Replace("level2 @ levels implements level_api", "level2 @ levels implements level_apii") + "\nfn main() { }"));
    }

    [Fact]
    public void An_interface_placed_in_the_same_overlay_area_is_an_error()
    {
        var e = With(Files(), path => Assert.Throws<CompileError>(() => TernCompiler.Compile(
            Header.Replace("import level_api", "import level_api @ levels") + "\nfn main() { }", sourcePath: path)));
        Assert.Equal("'level_api' is placed in the overlay area 'levels'; an interface must be resident. Remove '@ levels' from its import", e.Description);
        Assert.Equal((2, 35), (e.Pos.Line, e.Pos.Column));
    }

    [Fact]
    public void An_interface_placed_in_another_overlay_area_is_an_error()
    {
        Assert.Equal("'level_api' is placed in the overlay area 'extra'; an interface must be resident. Remove '@ extra' from its import",
            Message(Header.Replace("import level_api", "import level_api @ extra")
                .Replace("overlay levels", "overlay levels\n    $7000..=$70FF  overlay extra") + "\nfn main() { }"));
    }

    [Fact]
    public void An_interface_named_twice_is_an_error()
    {
        Assert.Equal("'level_api' is named twice after 'implements'. Remove one",
            Message(Header.Replace("level2 @ levels implements level_api", "level2 @ levels implements level_api, level_api") + "\nfn main() { }"));
    }

    [Fact]
    public void An_interface_with_no_routines_at_a_fixed_address_is_an_error()
    {
        Assert.Equal("'shapes' has no routines at a fixed address to implement. Remove it from the 'implements' list, or declare its entries as 'fn name() @ address'",
            Message(Header.Replace("level2 @ levels implements level_api", "level2 @ levels implements shapes").Replace("import level_api", "import level_api\nimport shapes") + "\nfn main() { }",
                Files(more: ("shapes", "pub const SIZE = 4\nfn area() -> u8 { return 16 }"))));
    }

    [Fact]
    public void A_routine_outside_the_area_is_an_error()
    {
        Assert.Equal("'level_api.update' at $5FFE is outside the overlay area 'levels' $6000-$6FFF, where level1's jmp to it would go. Move the routine into the area in level_api",
            Message(Header + "\nfn main() { }", Files(api: """
                fn init(n: u8 using A) @ $6000
                fn update() -> u8 using A @ $5FFE
                """)));
    }

    [Fact]
    public void A_routine_ending_past_the_area_is_an_error()
    {
        Assert.Contains("'level_api.update' at $6FFE is outside the overlay area", Message(Header + "\nfn main() { }", Files(api: """
            fn init(n: u8 using A) @ $6000
            fn update() -> u8 using A @ $6FFE
            """)));
    }

    [Fact]
    public void Routines_less_than_three_bytes_apart_are_an_error()
    {
        Assert.Equal("'level_api.update' at $6002 overlaps the entry before it: each entry of the table is a 3-byte jmp. Move it to $6003 or later in level_api",
            Message(Header + "\nfn main() { }", Files(api: """
                fn init(n: u8 using A) @ $6000
                fn update() -> u8 using A @ $6002
                """)));
    }

    [Fact]
    public void A_routine_with_a_fixed_input_is_an_error()
    {
        Assert.Equal("'level_api.init' has fixed inputs, which a function of level1 cannot receive. Declare them as parameters in registers",
            Message(Header + "\nfn main() { }", Files(api: """
                fn init(true using carry) @ $6000
                fn update() -> u8 using A @ $6003
                """)));
    }

    [Fact]
    public void A_routine_with_no_function_in_the_module_is_an_error()
    {
        Assert.Equal("'level_api.update' has no function in level2. Write 'fn update() -> u8 using A' in level2",
            Message(Header + "\nfn main() { }", Files(level2: "fn init(n: u8 using A) { }")));
    }

    [Fact]
    public void A_function_with_another_signature_is_an_error()
    {
        Assert.Equal("'init' does not match 'level_api.init': it is 'fn init(n: u8 using X)', the interface says 'fn init(n: u8 using A)'. Change the function to match",
            Message(Header + "\nfn main() { }", Files(level2: Level2.Replace("fn init(n: u8 using A)", "fn init(n: u8 using X)"))));
    }

    [Fact]
    public void A_function_with_another_return_is_an_error()
    {
        Assert.Contains("the interface says 'fn update() -> u8 using A'",
            Message(Header + "\nfn main() { }", Files(level1: Level1.Replace("fn update() -> u8 using A {", "fn update() -> u16 using AX {"))));
    }

    [Fact]
    public void An_inline_function_cannot_implement_a_routine()
    {
        Assert.Equal("'init' is inline: it has no code of its own to put in the jump table. Remove 'inline'",
            Message(Header + "\nfn main() { }", Files(level1: Level1.Replace("fn init(", "inline fn init("))));
    }

    [Fact]
    public void An_interrupt_fn_cannot_implement_a_routine()
    {
        Assert.StartsWith("'tick' is an interrupt handler, which the CPU calls through a vector",
            Message(Header + "\nfn main() { }", Files(api: Api + "\nfn tick() @ $6006", level1: Level1 + "\ninterrupt fn tick() { }", level2: Level2 + "\nfn tick() { }")));
    }

    [Fact]
    public void A_function_that_changes_a_register_the_routine_keeps_is_an_error()
    {
        Assert.Equal("'level2.tick' changes X, which 'level_api.tick' promises to keep. Change the function so that it leaves X alone, or remove X from the routine's 'keeps' list",
            Message(Header + "\nfn main() { load(1); level_api.tick() }", Files(api: Api + "\nfn tick() @ $6006 keeps X",
                level1: Level1 + "\nfn tick() { }",
                level2: Level2 + "\ndata T: [u8; 4] = [1, 2, 3, 4]\nfn tick() { for i in 0..4 { steps += T[i] } }")));
    }

    [Fact]
    public void Missing_volatile_data_is_an_error()
    {
        Assert.Equal("'level_api.header' is data every module implementing level_api provides. Declare 'data header: [u8; 2] @ $6006 = ...' in level1",
            Message(Header + "\nfn main() { }", Files(api: Api + "\nvolatile var header: [u8; 2] @ $6006")));
    }

    [Fact]
    public void Volatile_data_of_another_type_is_an_error()
    {
        Assert.Equal("'header' does not match 'level_api.header'. Declare it as 'data header: [u8; 2] @ $6006 = ...'",
            Message(Header + "\nfn main() { }", Files(api: Api + "\nvolatile var header: [u8; 2] @ $6006", level1: Level1 + "\ndata header: [u8; 3] @ $6006 = [1, 2, 3]", level2: Level2 + "\ndata header: [u8; 2] @ $6006 = [1, 2]")));
    }

    [Fact]
    public void Volatile_data_outside_the_area_is_an_error()
    {
        Assert.Equal("'level_api.header' at $5000 is outside the overlay area 'levels' $6000-$6FFF, so level1 cannot provide it. Move the variable into the area in level_api, or remove 'volatile' if it is not data of the overlay",
            Message(Header + "\nfn main() { }", Files(api: Api + "\nvolatile var header: u8 @ $5000")));
    }

    [Fact]
    public void Tables_of_two_interfaces_that_overlap_are_an_error()
    {
        Assert.Equal("The tables of level_api and music_api overlap in level1: 'music_api.play' at $6004-$6006 and an entry of level_api at $6003-$6005. " +
            "Move the routines of one interface so that each one's 3-byte jmp has its own addresses",
            Message($$"""
                {{Header.Replace("level1 @ levels implements level_api", "level1 @ levels implements level_api, music_api").Replace("import level_api", "import level_api\nimport music_api")}}
                fn main() { }
                """, Files(level1: Level1 + "\nfn play() { }", more: ("music_api", "fn play() @ $6004"))));
    }

    [Fact]
    public void Fixed_data_of_the_module_over_a_table_entry_is_an_error()
    {
        var message = Message(Header + "\nvar x: u8\nfn main() { x = level1.spot[0] }", Files(level1: Level1 + "\ndata spot: [u8; 1] @ $6004 = [1]"));
        Assert.Contains("are placed at overlapping addresses", message);
        Assert.Contains("'level_api.update' ($6003-$6005)", message);
    }

    [Fact]
    public void Implements_with_no_name_is_a_parse_error()
    {
        Expect.Error(() => TernCompiler.Compile("import level1 implements\nfn main() { }"), 2, 1, "Expected the name of an interface module after 'implements', found 'fn'");
    }

    // ---------- Warnings ----------

    [Fact]
    public void The_imports_functions_and_data_of_a_table_are_not_warned_as_unused()
    {
        var r = Build($$"""
            {{Header}}
            fn main() { load(1) }
            """, Files(api: Api + "\nvolatile var header: [u8; 2] @ $6006",
                level1: "private fn init(n: u8 using A) { }\nprivate fn update() -> u8 using A { return 1 }\nprivate data header: [u8; 2] @ $6006 = [1, 2]",
                level2: Level2 + "\ndata header: [u8; 2] @ $6006 = [3, 4]"));
        Assert.Empty(r.Warnings);
        // The data is in the file although no code names it.
        Assert.Equal(new byte[] { 1, 2 }, r.Overlays[0].Image[6..8]);
    }
}
