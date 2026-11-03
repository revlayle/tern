using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>Overlays (spec 8.5): modules placed in an area with <c>import m @ area</c>, one file each, loaded on demand.</summary>
public class OverlayTests
{
    private const string Level1 = """
        import util
        var score: u8 = 10
        var hits: u8
        data TABLE: [u8; 4] = [11, 22, 33, 44]
        fn run() -> u8 using A {
            score = util.twice(score)
            hits += 1
            return score
        }
        fn hits_so_far() -> u8 using A { return hits }
        fn table(i: u8 using X) -> u8 using A { return TABLE[i] }
        """;

    private const string Level2 = """
        var lives: u8 = 3
        var bonus: u16 = 1000
        fn run() -> u8 using A {
            lives += 1
            bonus += 500
            return lives
        }
        fn bonus_now() -> u16 using AX { return bonus }
        """;

    private const string Util = """
        fn twice(x: u8 using A) -> u8 using A { return x + x }
        """;

    private const string Header = """
        import level1 @ levels
        import level2 @ levels
        memory {
            $0801..=$5FFF  program
            $6000..=$6FFF  overlay levels
        }
        fn load(n: u8 using A) @ $C000 keeps X, Y
        """;

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

    private static readonly IReadOnlyDictionary<string, string> Modules =
        new Dictionary<string, string> { ["level1"] = Level1, ["level2"] = Level2, ["util"] = Util };

    /// <summary>Stand-in for the KERNAL load: <c>load(n)</c> copies overlay <c>n</c> (1-based, in module order) to its address.</summary>
    private static readonly IReadOnlyDictionary<int, Action<Run>> Loader = new Dictionary<int, Action<Run>>
    {
        [0xC000] = run =>
        {
            var file = run.Result.Overlays[run.Cpu.A - 1];
            run.Ram.Load((ushort)file.LoadAddress, file.Image);
        },
    };

    private static Run Compile(string main, IReadOnlyDictionary<string, string>? modules = null, long maxCycles = 5_000_000) =>
        With(modules ?? Modules, path => Run.Program(main, maxCycles, sourcePath: path, traps: Loader));

    private static string Message(string main, IReadOnlyDictionary<string, string>? modules = null) =>
        With(modules ?? Modules, path => Assert.Throws<CompileError>(() => TernCompiler.Compile(main, sourcePath: path)).Description);

    // ---------- Loading and running ----------

    [Fact]
    public void Two_overlays_in_one_area_are_loaded_in_turn_and_find_their_variables_at_their_initial_values()
    {
        var r = Compile($$"""
            {{Header}}
            var a1: u8
            var a2: u8
            var a3: u8
            var h1: u8
            var l1: u8
            var l2: u8
            var h2: u8
            var t: u8
            var b: u16
            fn main() {
                load(1)
                a1 = level1.run()           // 20
                a2 = level1.run()           // 40
                h1 = level1.hits_so_far()   // 2
                t = level1.table(2)         // 33
                load(2)
                l1 = level2.run()           // 4
                l2 = level2.run()           // 5
                b = level2.bonus_now()      // 2000
                load(1)                     // the file sets the variables again
                a3 = level1.run()           // 20 again
                h2 = level1.hits_so_far()   // 1, not 3
            }
            """);
        Assert.Equal(20, r.U8("a1"));
        Assert.Equal(40, r.U8("a2"));
        Assert.Equal(2, r.U8("h1"));
        Assert.Equal(33, r.U8("t"));
        Assert.Equal(4, r.U8("l1"));
        Assert.Equal(5, r.U8("l2"));
        Assert.Equal(2000, r.U16("b"));
        Assert.Equal(20, r.U8("a3"));
        Assert.Equal(1, r.U8("h2"));
    }

    [Fact]
    public void Each_overlay_has_a_file_that_loads_at_the_start_of_its_area_and_overlays_share_memory()
    {
        var r = Compile($$"""
            {{Header}}
            fn main() { load(1); level1.run(); load(2); level2.run() }
            """);
        Assert.Equal(["level1", "level2"], r.Result.Overlays.Select(o => o.Name));
        Assert.All(r.Result.Overlays, o => Assert.Equal(0x6000, o.LoadAddress));
        Assert.All(r.Result.Overlays, o => Assert.Equal("levels", o.Area));
        Assert.Equal(r.Result.Overlays[0].Image.Length + 2, r.Result.Overlays[0].Prg.Length);
        Assert.Equal(new byte[] { 0x00, 0x60 }, r.Result.Overlays[0].Prg[..2]);
        // The resident program does not reach into the area, and both overlays' code and variables are in it.
        Assert.True(r.Result.LoadAddress + r.Result.Image.Length <= 0x6000);
        Assert.True(r.Address("level1.run") >= 0x6000 && r.Address("level2.run") >= 0x6000);
        Assert.True(r.Address("level1.score") >= 0x6000 && r.Address("level2.lives") >= 0x6000);
        Assert.Contains("Overlay area levels $6000-$6FFF", r.Result.Report);
        Assert.Contains("level1.prg", r.Result.Report);
        Assert.Contains("level2.prg", r.Result.Report);
    }

    [Fact]
    public void An_overlay_file_holds_its_variables_with_initial_values_zeros_included()
    {
        var r = Compile($$"""
            {{Header}}
            fn main() { load(1); level1.run(); load(2); level2.run() }
            """);
        var file = r.Result.Overlays[1];
        int lives = r.Address("level2.lives") - file.LoadAddress, bonus = r.Address("level2.bonus") - file.LoadAddress;
        Assert.Equal(3, file.Image[lives]);
        Assert.Equal(1000, file.Image[bonus] | file.Image[bonus + 1] << 8);
        var one = r.Result.Overlays[0];
        Assert.Equal(0, one.Image[r.Address("level1.hits") - one.LoadAddress]);
        Assert.Equal(10, one.Image[r.Address("level1.score") - one.LoadAddress]);
    }

    [Fact]
    public void Overlay_code_calls_resident_modules_which_stay_out_of_the_files()
    {
        var r = Compile($$"""
            {{Header}}
            var v: u8
            fn main() { load(1); v = level1.run() }
            """);
        Assert.Equal(20, r.U8("v"));
        Assert.True(r.Address("util.twice") < 0x6000);
        Assert.DoesNotContain(r.Result.Overlays, o => o.Name == "util");
    }

    [Fact]
    public void Overlays_in_different_areas_may_call_each_other()
    {
        var modules = new Dictionary<string, string>
        {
            ["level1"] = "import level2\nfn go() -> u8 using A { return level2.five() + 1 }",
            ["level2"] = "fn five() -> u8 using A { return 5 }",
        };
        var r = Compile("""
            import level1 @ one
            import level2 @ two
            memory {
                $0801..=$3FFF  program
                $4000..=$4FFF  overlay one
                $5000..=$5FFF  overlay two
            }
            fn load(n: u8 using A) @ $C000 keeps X, Y
            var v: u8
            fn main() { load(1); load(2); v = level1.go() }
            """, modules);
        Assert.Equal(6, r.U8("v"));
        Assert.Equal(0x4000, r.Result.Overlays.Single(o => o.Name == "level1").LoadAddress);
        Assert.Equal(0x5000, r.Result.Overlays.Single(o => o.Name == "level2").LoadAddress);
    }

    [Fact]
    public void An_overlay_that_nothing_uses_has_no_file()
    {
        var r = Compile($$"""
            {{Header}}
            fn main() { load(1); level2.run() }
            """);
        Assert.Equal(["level2"], r.Result.Overlays.Select(o => o.Name));
    }

    [Fact]
    public void Resident_variables_are_set_up_by_the_startup_code_and_not_by_loading()
    {
        var r = Compile($$"""
            {{Header}}
            var total: u8 = 7
            fn main() { load(1); total += level1.run() }
            """);
        Assert.Equal(27, r.U8("total"));
    }

    // ---------- c64.load ----------

    /// <summary>Stand-ins for SETNAM, SETLFS and LOAD that record their inputs and load the overlay whose name, in capitals, is the file name.</summary>
    private sealed class FakeKernal
    {
        public string Name = "";
        public int Device = -1, Secondary = -1, LogicalFile = -1, LoadA = -1;

        public IReadOnlyDictionary<int, Action<Run>> Traps => new Dictionary<int, Action<Run>>
        {
            [0xFFBD] = run =>
            {
                int at = run.Cpu.X | run.Cpu.Y << 8;
                Name = string.Concat(Enumerable.Range(0, run.Cpu.A).Select(i => (char)run.Ram.Bytes[at + i]));
            },
            [0xFFBA] = run => (LogicalFile, Device, Secondary) = (run.Cpu.A, run.Cpu.X, run.Cpu.Y),
            [0xFFD5] = run =>
            {
                LoadA = run.Cpu.A;
                var file = run.Result.Overlays.FirstOrDefault(o => o.Name.ToUpperInvariant() == Name);
                run.Cpu.C = file is null;
                if (file is not null) run.Ram.Load((ushort)file.LoadAddress, file.Image);
            },
        };
    }

    private const string LoadProgram = """
        import c64
        import level1 @ levels
        memory {
            $0801..=$5FFF  program
            $6000..=$6FFF  overlay levels
        }
        data GOOD = "LEVEL1"
        data BAD = "NOPE"
        var failed: bool
        var failed2: bool
        var v: u8
        fn main() {
            failed = c64.load(addr(GOOD), len(GOOD), 8)
            if !failed { v = level1.run() }
            failed2 = c64.load(addr(BAD), len(BAD), 9)
        }
        """;

    [Fact]
    public void C64_load_calls_the_KERNAL_and_returns_its_carry()
    {
        var kernal = new FakeKernal();
        var r = With(Modules, path => Run.Program(LoadProgram, sourcePath: path, traps: kernal.Traps));
        Assert.False(r.U8("failed") != 0);
        Assert.Equal(20, r.U8("v"));           // level1 was loaded by the call and ran
        Assert.True(r.U8("failed2") != 0);     // the second load failed
        Assert.Equal("NOPE", kernal.Name);
        Assert.Equal(9, kernal.Device);
        Assert.Equal(1, kernal.Secondary);     // load to the address in the file
        Assert.Equal(1, kernal.LogicalFile);
        Assert.Equal(0, kernal.LoadA);         // LOAD, not VERIFY
    }

    [Fact]
    public void C64_load_is_a_direct_sequence_of_KERNAL_calls()
    {
        var r = With(Modules, path => Run.Program(LoadProgram, sourcePath: path, traps: new FakeKernal().Traps));
        Assert.Contains("jsr $FFBD", r.Result.Listing);
        Assert.Contains("jsr $FFBA", r.Result.Listing);
        Assert.Contains("jsr $FFD5", r.Result.Listing);
    }

    [Fact]
    public void The_levels_example_loads_two_levels_in_turn_and_a_reload_resets_the_variables()
    {
        var path = AsmTests.FindExample("levels.tern");
        var r = Run.Program(File.ReadAllText(path), sourcePath: path, traps: new FakeKernal().Traps);
        var screen = r.Ram.Bytes;
        Assert.Equal(0x34, screen[0x0400]);           // '4': level1.lives is 3 after each load, 4 after drawing
        Assert.All(Enumerable.Range(1, 479), i => Assert.Equal(1, screen[0x0400 + i]));          // 'A'
        Assert.Equal(0x38, screen[0x0400 + 480]);     // '8': level2.lives 7, then 8
        Assert.All(Enumerable.Range(481, 519), i => Assert.Equal(2, screen[0x0400 + i]));        // 'B'
        Assert.Equal(["level1", "level2"], r.Result.Overlays.Select(o => o.Name).Order());
    }

    // ---------- Errors ----------

    [Fact]
    public void A_call_between_overlays_of_the_same_area_is_an_error()
    {
        var modules = new Dictionary<string, string>
        {
            ["level1"] = "import level2\nfn go() -> u8 using A { return level2.five() }",
            ["level2"] = "fn five() -> u8 using A { return 5 }",
        };
        var message = Message($$"""
            {{Header}}
            var v: u8
            fn main() { load(1); v = level1.go() }
            """, modules);
        Assert.Contains("'level1.go' calls 'level2.five'", message);
        Assert.Contains("overlay area 'levels'", message);
        Assert.Contains("never loaded together", message);
    }

    [Fact]
    public void A_call_between_overlays_in_assembly_is_an_error_too()
    {
        var modules = new Dictionary<string, string>
        {
            ["level1"] = "import level2\nfn go() { asm { jsr level2.five } }",
            ["level2"] = "fn five() { }",
        };
        var message = Message($$"""
            {{Header}}
            fn main() { load(1); level1.go() }
            """, modules);
        Assert.Contains("'level1.go' calls 'level2.five'", message);
    }

    [Fact]
    public void Using_the_variable_of_another_overlay_in_the_same_area_is_an_error()
    {
        var modules = new Dictionary<string, string>
        {
            ["level1"] = "import level2\nfn peek() -> u8 using A { return level2.lives }",
            ["level2"] = "var lives: u8 = 3\nfn five() { }",
        };
        var message = Message($$"""
            {{Header}}
            fn main() { load(1); level1.peek() }
            """, modules);
        Assert.Contains("'level2.lives' belongs to level2", message);
        Assert.Contains("never loaded together and share their memory", message);
    }

    [Fact]
    public void An_overlay_that_does_not_fit_its_area_is_an_error_with_its_size()
    {
        var message = Message("""
            import level1 @ levels
            memory {
                $0801..=$5FFF  program
                $6000..=$6007  overlay levels
            }
            fn main() { level1.run() }
            """);
        Assert.Contains("The overlay 'level1' needs ", message);
        Assert.Contains("$6000-$6007 has 8", message);
    }

    [Fact]
    public void An_inline_function_of_another_overlay_may_not_use_that_overlays_variables()
    {
        var modules = new Dictionary<string, string>
        {
            ["level1"] = "import level2\nfn go() -> u8 using A { return level2.peek() }",
            ["level2"] = "var lives: u8 = 3\ninline fn peek() -> u8 using A { return lives }\nfn run() { lives += 1 }",
        };
        var message = Message($$"""
            {{Header}}
            var r: u8
            fn main() { load(1); r = level1.go(); load(2); level2.run() }
            """, modules);
        Assert.Contains("'level1.go' calls the inline function 'level2.peek', which is copied into it and uses 'level2.lives'", message);
        Assert.Contains("overlay area 'levels'", message);
    }

    [Fact]
    public void An_inline_function_of_another_overlay_may_not_call_that_overlays_functions()
    {
        var modules = new Dictionary<string, string>
        {
            ["level1"] = "import level2\nfn go() -> u8 using A { return level2.five() }",
            ["level2"] = "fn real() -> u8 using A { return 5 }\ninline fn five() -> u8 using A { return real() }",
        };
        var message = Message($$"""
            {{Header}}
            var r: u8
            fn main() { load(1); r = level1.go(); load(2); level2.five() }
            """, modules);
        Assert.Contains("calls the inline function 'level2.five', which is copied into it and calls 'level2.real'", message);
    }

    [Fact]
    public void An_inline_function_of_another_overlay_that_uses_nothing_of_its_module_is_fine()
    {
        var modules = new Dictionary<string, string>
        {
            ["level1"] = "import level2\nvar own: u8 = 4\nfn go() -> u8 using A { return level2.five() + own }",
            ["level2"] = "inline fn five() -> u8 using A { return 5 }\nfn run() { }",
            ["util"] = Util,
        };
        var r = Compile($$"""
            {{Header}}
            var v: u8
            fn main() { load(2); v = level1.go(); load(1); level2.run() }   // level2 is first in the list: level1 imports it
            """, modules);
        Assert.Equal(9, r.U8("v"));
    }

    [Fact]
    public void A_fixed_address_variable_may_not_overlap_an_overlays_code()
    {
        var modules = new Dictionary<string, string> { ["l2"] = "fn run() { }" };
        var message = Message("""
            import l2 @ levels
            memory {
                $0801..=$5FFF  program
                $6000..=$6FFF  overlay levels
            }
            var x: u8 @ $6000
            fn main() { x = 1; l2.run() }
            """, modules);
        Assert.Contains("'x' at $6000 overlaps the overlay l2 at $6000", message);
    }

    [Fact]
    public void The_overlay_that_does_not_fit_error_is_reported_at_its_import()
    {
        var e = With(Modules, path => Assert.Throws<CompileError>(() => TernCompiler.Compile("""
            import level2
            import level1 @ levels
            memory {
                $0801..=$5FFF  program
                $6000..=$6007  overlay levels
            }
            fn main() { level1.run() }
            """, sourcePath: path)));
        Assert.Equal(2, e.Pos.Line);
        Assert.Contains("The overlay 'level1' needs", e.Description);
    }

    [Theory]
    [InlineData("import level2 @ nowhere\nmemory {\n $0801..=$5FFF program\n $6000..=$6FFF overlay levels\n}\nfn main() { level2.run() }", "The memory block has no overlay area 'nowhere'")]
    [InlineData("import level2 @ buf\nmemory {\n $0801..=$5FFF program\n $6000..=$6FFF region buf\n}\nfn main() { level2.run() }", "'buf' is a region, not an overlay area")]
    [InlineData("import level2 @ levels\nfn main() { level2.run() }", "The memory block has no overlay area 'levels'")]
    [InlineData("memory {\n $0801..=$5FFF program\n $6000..=$6FFF overlay levels\n $7000..=$7FFF overlay levels\n}\nfn main() { }", "The overlay area 'levels' is declared twice")]
    [InlineData("memory {\n $0801..=$5FFF program\n $6000..=$6FFF overlay levels\n $7000..=$7FFF region levels\n}\nfn main() { }", "declared twice")]
    [InlineData("memory {\n $0801..=$5FFF program\n $6000..=$6FFF overlay levels\n $6800..=$7FFF reserved\n}\nfn main() { }", "overlaps")]
    [InlineData("import level2 @ 5\nmemory {\n $0801..=$5FFF program\n $6000..=$6FFF overlay levels\n}\nfn main() { }", "the name of an overlay area after '@'")]
    public void Placement_errors(string main, string expected) => Assert.Contains(expected, Message(main));

    [Fact]
    public void Only_the_main_file_places_modules_in_overlay_areas()
    {
        var modules = new Dictionary<string, string>
        {
            ["level1"] = "import level2 @ levels\nfn go() { }",
            ["level2"] = "fn five() { }",
        };
        var message = Message("""
            import level1
            memory {
                $0801..=$5FFF  program
                $6000..=$6FFF  overlay levels
            }
            fn main() { level1.go() }
            """, modules);
        Assert.Contains("Only the program's main file places modules in overlay areas", message);
    }

    [Fact]
    public void A_zero_page_variable_in_an_overlay_is_an_error_since_loading_cannot_set_it()
    {
        var modules = new Dictionary<string, string>
        {
            ["level1"] = "var p: ptr u8\nfn first() -> u8 using A { return p[0] }",
            ["level2"] = "fn five() { }",
        };
        var message = Message($$"""
            {{Header}}
            fn main() { load(1); level1.first() }
            """, modules);
        Assert.Contains("'level1.p' must be in zero page, but it belongs to the overlay level1", message);
        Assert.Contains("local variable", message);
    }

    [Fact]
    public void The_listing_shows_the_overlay_code_at_the_area_address()
    {
        var r = Compile($$"""
            {{Header}}
            fn main() { load(1); level1.run(); load(2); level2.run() }
            """);
        Assert.Contains("$6000", r.Result.Listing);
        Assert.Contains(r.Result.Sections, s => s.Overlay == "level2" && s.Address == 0x6000);
    }
}
