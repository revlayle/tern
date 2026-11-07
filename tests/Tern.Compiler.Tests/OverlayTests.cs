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

    private static string SiblingMessage(string level1) => Message($$"""
        {{Header}}
        fn main() { load(1); level1.peek() }
        """, new Dictionary<string, string> { ["level1"] = "import level2\n" + level1, ["level2"] = "var lives: u8 = 3\ndata T: [u8; 2] = [5, 6]\nfn five() { }" });

    [Fact]
    public void Using_or_taking_the_address_of_data_or_a_function_of_a_sibling_overlay_is_an_error()
    {
        Assert.Contains("'level2.T' belongs to level2", SiblingMessage("fn peek() -> u8 using A { return level2.T[1] }"));
        Assert.Contains("'level2.T' belongs to level2", SiblingMessage("fn peek() -> u16 using AX { return addr(level2.T) }"));
        Assert.Contains("'level2.lives' belongs to level2", SiblingMessage("fn peek() -> u16 using AX { return addr(level2.lives) }"));
        var message = SiblingMessage("fn peek() -> u16 using AX { return addr(level2.five) }");
        Assert.Contains("addr(level2.five) is in level2, which is in the same overlay area 'levels' as level1", message);
        Assert.Contains("Take the address in resident code", message);
    }

    [Fact]
    public void Resident_code_may_take_addresses_in_any_overlay_and_so_may_an_overlay_in_another_area()
    {
        var modules = new Dictionary<string, string>
        {
            ["level1"] = "fn one() -> u8 using A { return 1 }",
            ["level2"] = "data T: [u8; 2] = [5, 6]\nfn five() -> u8 using A { return 5 }",
        };
        var r = Compile($$"""
            {{Header}}
            var f: u16
            var t: ptr u8
            fn main() { f = addr(level2.five); t = addr(level2.T); load(1); level1.one() }
            """, modules);
        Assert.Equal(r.Address("level2.five"), r.U16("f"));
        Assert.Equal(r.Address("level2.T"), r.U16("t"));
        var other = new Dictionary<string, string>
        {
            ["level1"] = "import level2\nfn peek() -> u16 using AX { return addr(level2.five) }",
            ["level2"] = "fn five() -> u8 using A { return 5 }",
        };
        var r2 = Compile("""
            import level1 @ a
            import level2 @ b
            memory {
                $0801..=$4FFF  program
                $5000..=$5FFF  overlay a
                $6000..=$6FFF  overlay b
            }
            var p: u16
            fn load(n: u8 using A) @ $C000 keeps X, Y
            fn main() { load(2); p = level1.peek() }   // level2 comes first in the list: level1 imports it
            """, other);
        Assert.Equal(r2.Address("level2.five"), r2.U16("p"));
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

    private static readonly IReadOnlyDictionary<string, string> Ov = new Dictionary<string, string>
    {
        ["ov"] = "var x: u8 = 7\nfn get() -> u8 using A { return x }\nfn put(v: u8 using A) { x = v }",
    };

    private const string OvHeader = """
        import ov @ levels
        memory {
            $0801..=$5FFF  program
            $6000..=$6FFF  overlay levels
        }
        fn load(n: u8 using A) @ $C000 keeps X, Y
        """;

    private static int OvAddress(string name) =>
        With(Ov, path => Run.Program($"{OvHeader}\nfn main() {{ load(1); ov.put(ov.get()) }}", 100_000, sourcePath: path, traps: Loader).Address(name));

    [Fact]
    public void A_volatile_fixed_variable_over_an_overlay_reads_and_writes_the_overlays_memory()
    {
        int x = OvAddress("ov.x");
        var r = Compile($$"""
            {{OvHeader}}
            volatile var hdr: u8 @ ${{x:X4}}
            var a: u8
            var b: u8
            var c: u8
            var d: u8
            fn main() {
                load(1)
                a = hdr            // the overlay's initial value
                hdr = 9
                b = ov.get()       // the overlay sees the write
                ov.put(33)
                c = hdr            // and the write of the overlay is seen
                d = ov.x
                hdr = 12
                d = ov.x           // read again after the write
            }
            """, Ov);
        Assert.Equal(7, r.U8("a"));
        Assert.Equal(9, r.U8("b"));
        Assert.Equal(33, r.U8("c"));
        Assert.Equal(12, r.U8("d"));
    }

    [Fact]
    public void A_volatile_fixed_variable_over_the_first_byte_of_an_overlays_code_reads_the_loaded_file()
    {
        var r = Compile($$"""
            {{OvHeader}}
            volatile var first: u8 @ $6000
            var a: u8
            fn main() { load(1); a = first; ov.put(1) }
            """, Ov);
        Assert.Equal(r.Result.Overlays[0].Image[0], r.U8("a"));
    }

    [Fact]
    public void A_fixed_variable_over_an_overlay_without_volatile_suggests_volatile_or_the_overlays_own_name()
    {
        var message = Message($$"""
            {{OvHeader}}
            var first: u8 @ $6000
            fn main() { load(1); first = 1; ov.put(1) }
            """, Ov);
        Assert.Contains("'first' at $6000 overlaps the overlay ov", message);
        Assert.Contains("volatile var first", message);
        Assert.Contains("ov.name", message);
    }

    [Fact]
    public void A_volatile_fixed_variable_over_the_resident_program_is_still_an_error()
    {
        var message = Message($$"""
            {{OvHeader}}
            volatile var first: u8 @ $0801
            fn main() { first = 1; ov.put(1) }
            """, Ov);
        Assert.Contains("'first' at $0801 overlaps the program", message);
    }

    [Fact]
    public void A_volatile_fixed_variable_in_an_overlay_area_no_file_covers_needs_no_overlap_check()
    {
        var r = Compile($$"""
            {{OvHeader}}
            volatile var spare: u8 @ $6800
            var a: u8
            fn main() { load(1); spare = 5; a = spare; ov.put(1) }
            """, Ov);
        Assert.Equal(5, r.U8("a"));
    }

    // ---------- Fixed data in an overlay ----------

    private const string FixedHeader = """
        import lv1 @ levels
        import lv2 @ levels
        memory {
            $0801..=$5FFF  program
            $6000..=$6FFF  overlay levels
        }
        fn load(n: u8 using A) @ $C000 keeps X, Y
        """;

    private static IReadOnlyDictionary<string, string> Levels(string lv1Extra = "", string lv2Extra = "") => new Dictionary<string, string>
    {
        ["lv1"] = "data header: [u8; 3] @ $6000 = [1, 2, 3]\nvar n: u8 = 4\nfn go() -> u8 using A { n += 1; return n + header[2] }\n" + lv1Extra,
        ["lv2"] = "data header: [u8; 3] @ $6000 = [10, 20, 30]\ndata late: [u8; 2] @ $6010 = [7, 8]\nfn go() -> u8 using A { return late[1] + header[0] }\n" + lv2Extra,
    };

    [Fact]
    public void Two_levels_each_with_a_header_at_the_areas_start_and_code_after_it()
    {
        var r = Compile($$"""
            {{FixedHeader}}
            volatile var h0: u8 @ $6000
            volatile var h1: u8 @ $6001
            volatile var h2: u8 @ $6002
            var a: u8
            var b: u8
            var c: u8
            var d: u8
            var e: u8
            fn main() {
                load(1)
                a = h0
                b = h2
                c = lv1.go()      // 5 + 3
                load(2)
                d = h1
                e = lv2.go()      // 8 + 10
            }
            """, Levels());
        Assert.Equal(1, r.U8("a"));
        Assert.Equal(3, r.U8("b"));
        Assert.Equal(8, r.U8("c"));
        Assert.Equal(20, r.U8("d"));
        Assert.Equal(18, r.U8("e"));
        // The files start at the area's start; data is at its address and the gap before $6010 is zeros.
        var one = r.Result.Overlays.Single(o => o.Name == "lv1");
        var two = r.Result.Overlays.Single(o => o.Name == "lv2");
        Assert.Equal(0x6000, one.LoadAddress);
        Assert.Equal(0x6000, two.LoadAddress);
        Assert.Equal(new byte[] { 1, 2, 3 }, one.Image[..3]);
        Assert.Equal(new byte[] { 10, 20, 30 }, two.Image[..3]);
        Assert.Equal(new byte[] { 7, 8 }, two.Image[0x10..0x12]);
        Assert.True(r.Address("lv1.go") >= 0x6003);
        Assert.True(r.Address("lv1.n") >= 0x6003);
        // The code goes around the data at $6010 in lv2.
        Assert.True(r.Address("lv2.go") is >= 0x6003 and < 0x6010 || r.Address("lv2.go") >= 0x6012);
    }

    [Fact]
    public void Code_goes_around_fixed_data_in_the_middle_of_an_overlay()
    {
        var mods = new Dictionary<string, string>
        {
            ["lv1"] = "data mid: [u8; 4] @ $6001 = [9, 8, 7, 6]\nfn go() -> u8 using A { return mid[3] }",
            ["lv2"] = "fn go() -> u8 using A { return 1 }",
        };
        var r = Compile($$"""
            {{FixedHeader}}
            var a: u8
            fn main() { load(1); a = lv1.go(); load(2); lv2.go() }
            """, mods);
        Assert.Equal(6, r.U8("a"));
        var one = r.Result.Overlays.Single(o => o.Name == "lv1");
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, one.Image[1..5]);
        Assert.True(r.Address("lv1.go") is 0x6000 || r.Address("lv1.go") >= 0x6005);
    }

    [Fact]
    public void Fixed_data_of_two_overlays_may_share_an_address()
    {
        // Already the case in Two_levels_...: both headers are at $6000. Here the resident program reads it as plain data.
        var r = Compile($$"""
            {{FixedHeader}}
            volatile var h: u8 @ $6000
            var a: u8
            var b: u8
            fn main() { load(1); a = h; lv1.go(); load(2); b = h; lv2.go() }
            """, Levels());
        Assert.Equal(1, r.U8("a"));
        Assert.Equal(10, r.U8("b"));
    }

    [Fact]
    public void Fixed_data_in_an_overlay_may_not_overlap_the_modules_other_fixed_data()
    {
        var message = Message($$"""
            {{FixedHeader}}
            fn main() { lv1.go(); lv1.use(); lv2.go() }
            """, Levels(lv1Extra: "data clash: [u8; 2] @ $6002 = [1, 1]"+"\nfn use() -> u8 using A { return clash[0] }"));
        Assert.Contains("overlapping addresses", message);
    }

    [Fact]
    public void A_fixed_variable_of_the_module_may_not_overlap_its_fixed_data()
    {
        var message = Message($$"""
            {{FixedHeader}}
            fn main() { lv1.go(); lv1.use(); lv2.go() }
            """, Levels(lv1Extra: "volatile var clash: u8 @ $6001"+"\nfn use() -> u8 using A { return clash }"));
        Assert.Contains("'lv1.clash' at $6001 overlaps the data", message);
    }

    [Fact]
    public void An_overlay_modules_fixed_data_outside_its_own_area_is_an_error()
    {
        var mods = new Dictionary<string, string> { ["lv1"] = "data d: [u8; 2] @ $7000 = [1, 2]\nfn go() -> u8 using A { return d[0] }", ["lv2"] = "fn go() { }" };
        var message = Message($$"""
            {{FixedHeader}}
            fn main() { lv1.go(); lv2.go() }
            """, mods);
        Assert.Contains("'lv1.d' at $7000-$7001 is outside the overlay area levels ($6000-$6FFF)", message);

        mods["lv1"] = "data d: [u8; 2] @ $5000 = [1, 2]\nfn go() -> u8 using A { return d[0] }";
        message = Message($$"""
            {{FixedHeader}}
            fn main() { lv1.go(); lv2.go() }
            """, mods);
        Assert.Contains("'lv1.d' at $5000-$5001 is outside the overlay area levels", message);
    }

    [Fact]
    public void A_resident_modules_fixed_data_in_an_overlay_area_is_an_error()
    {
        var mods = new Dictionary<string, string>
        {
            ["lv1"] = "fn go() { }", ["lv2"] = "fn go() { }",
            ["res"] = "data d: [u8; 2] @ $6100 = [1, 2]\nfn f() -> u8 using A { return d[0] }",
        };
        var message = Message($$"""
            import res
            {{FixedHeader}}
            fn main() { res.f(); lv1.go(); lv2.go() }
            """, mods);
        Assert.Contains("'res.d' at $6100-$6101 is in the overlay area levels ($6000-$6FFF), but its module is resident", message);
        var main = Message($$"""
            {{FixedHeader}}
            data d: [u8; 2] @ $6100 = [1, 2]
            var q: u8
            fn main() { q = d[0]; lv1.go(); lv2.go() }
            """, Levels());
        Assert.Contains("'d' at $6100-$6101 is in the overlay area levels", main);
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
    public void A_fixed_zero_page_pointer_in_an_overlay_works_is_not_in_the_file_and_is_shared_with_the_main_program()
    {
        var modules = new Dictionary<string, string>
        {
            ["level1"] = "var p: ptr u8 @ $FB\nfn first() -> u8 using A { return p[1] }",
            ["level2"] = "fn five() { }",
        };
        var r = Compile($$"""
            {{Header}}
            data T: [u8; 3] = [7, 8, 9]
            var main_p: ptr u8 @ $FB
            var a: u8
            var b: u8
            fn main() { load(1); main_p = addr(T); a = level1.first(); b = main_p[2] }
            """, modules);
        Assert.Equal(8, r.U8("a"));
        Assert.Equal(9, r.U8("b"));
        // Not part of the file: it holds only the code (the pointer is a name for $FB and $FC).
        var file = r.Result.Overlays.Single(o => o.Name == "level1");
        Assert.True(file.Image.Length < 16);
        Assert.Equal(0xFB, r.Address("level1.p"));
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
        Assert.Contains("name a zero-page address (var p: ptr u8 @ $FB)", message);
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
