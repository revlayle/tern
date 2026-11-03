using Tern.Compiler.Syntax;
using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>Binaries without main (spec 12.3): the jump table, setup, one root, and the interface module.</summary>
public class BinaryTests
{
    private const string Counter = """
        memory {
            $C000..=$C0FF  program
            $80..=$83      zeropage
        }
        var count: u8
        var total: u16
        fn init() { count = 0 }
        fn add(n: u8 using A) -> u8 using A {
            count += n
            return count
        }
        fn add_total(n: u8 using X) -> u16 using AY {
            total += n
            return total
        }
        export init, add, add_total
        """;

    /// <summary>Loads a binary and calls its entries one at a time, as a host does.</summary>
    private sealed class Binary
    {
        private const int Stop = 0x03F0;   // where an entry returns to
        public CompileResult Result { get; }
        public Ram Ram { get; } = new();
        public Cpu6502 Cpu { get; }

        public Binary(string source, string? sourcePath = null)
        {
            Result = TernCompiler.Compile(source, sourcePath: sourcePath);
            Ram.Load((ushort)Result.LoadAddress, Result.Image);
            Cpu = new Cpu6502(Ram) { SP = 0xFF };
        }

        public int Entry(string name) => Result.Entries.Single(e => e.Name == name).Address;

        public void Call(string entry, int a = 0, int x = 0, int y = 0)
        {
            Ram.Bytes[0x01FF] = (byte)((Stop - 1) >> 8);
            Ram.Bytes[0x01FE] = (byte)((Stop - 1) & 0xFF);
            Cpu.SP = 0xFD;
            Cpu.A = (byte)a;
            Cpu.X = (byte)x;
            Cpu.Y = (byte)y;
            Cpu.PC = (ushort)Entry(entry);
            long end = Cpu.Cycles + 100_000;
            while (Cpu.PC != Stop)
            {
                Cpu.Step();
                if (Cpu.Cycles > end) throw new Xunit.Sdk.XunitException($"{entry} did not return (PC=${Cpu.PC:X4})");
            }
        }
    }

    [Fact]
    public void A_binary_starts_with_a_jump_table_and_has_no_BASIC_line()
    {
        var b = new Binary(Counter);
        Assert.Equal(0xC000, b.Result.LoadAddress);
        Assert.Equal(0xC000, b.Result.Entry);
        Assert.Equal(new[] { ("setup", 0xC000), ("init", 0xC003), ("add", 0xC006), ("add_total", 0xC009) }, b.Result.Entries);
        Assert.Equal(new byte[] { 0x00, 0xC0 }, b.Result.Prg[..2]);
        for (int i = 0; i < 4; i++) Assert.Equal(0x4C, b.Result.Image[3 * i]);    // jmp
        int setup = b.Result.Image[1] | (b.Result.Image[2] << 8);
        Assert.Equal(0xC00C, setup);                                              // setup follows the table
        Assert.Contains("Jump table at $C000:", b.Result.Report);
    }

    [Fact]
    public void Entries_are_called_one_by_one_after_setup()
    {
        var b = new Binary(Counter);
        b.Ram.Bytes[0x80] = 0xEE;                    // leftovers: setup zeroes the variables
        b.Ram.Bytes[0x81] = 0xEE;
        b.Ram.Bytes[0x82] = 0xEE;
        b.Call("setup");
        b.Call("init");
        b.Call("add", a: 5);
        Assert.Equal(5, b.Cpu.A);
        b.Call("add", a: 250);
        Assert.Equal(255, b.Cpu.A);                   // 5 + 250
        b.Call("add", a: 3);
        Assert.Equal(2, b.Cpu.A);                     // wraps
        b.Call("add_total", x: 200);
        b.Call("add_total", x: 100);
        Assert.Equal(300, b.Cpu.A | (b.Cpu.Y << 8));
    }

    [Fact]
    public void The_order_of_the_export_line_is_the_order_of_the_jump_table()
    {
        var b = new Binary(Counter.Replace("export init, add, add_total", "export add_total, init, add"));
        Assert.Equal(new[] { "setup", "add_total", "init", "add" }, b.Result.Entries.Select(e => e.Name));
        Assert.Equal(0xC006, b.Entry("init"));
    }

    [Fact]
    public void A_binary_without_a_memory_block_starts_at_the_platforms_load_address()
    {
        var b = new Binary("var n: u8\nfn bump() { n += 1 }\nexport bump");
        Assert.Equal(0x0801, b.Result.LoadAddress);
        Assert.Equal(0x0801, b.Entry("setup"));
        Assert.Equal(0x4C, b.Result.Image[0]);        // no BASIC line: the jump table is the first thing in the file
        b.Call("setup");
        b.Call("bump");
        b.Call("bump");
        Assert.Equal(2, b.Ram.Bytes[b.Result.Symbols["n"]]);
    }

    [Fact]
    public void The_entries_share_memory_as_one_root()
    {
        var b = new Binary("""
            memory { $C000..=$C0FF program }
            fn first(n: u8 using A) -> u8 using A { var t: u8 = n; t += 1; return t }
            fn second(n: u8 using A) -> u8 using A { var u: u8 = n; u += 2; return u }
            export first, second
            """);
        Assert.Equal(b.Result.Symbols["first.t"], b.Result.Symbols["second.u"]);
        b.Call("setup");
        b.Call("first", a: 1);
        Assert.Equal(2, b.Cpu.A);
        b.Call("second", a: 1);
        Assert.Equal(3, b.Cpu.A);
    }

    [Fact]
    public void Exported_functions_may_call_each_other()
    {
        var b = new Binary("""
            memory { $C000..=$C0FF program }
            var n: u8
            fn twice() { bump(); bump() }
            fn bump() { n += 1 }
            export twice, bump
            """);
        b.Call("setup");
        b.Call("twice");
        b.Call("bump");
        Assert.Equal(3, b.Ram.Bytes[b.Result.Symbols["n"]]);
    }

    [Fact]
    public void Variables_with_initial_values_come_with_the_file()
    {
        var b = new Binary("""
            memory { $C000..=$C0FF program }
            var base: u16 = 1000
            data names: [u8; 3] = [7, 8, 9]
            fn get() -> u16 using AX { return base + names[2] }
            export get
            """);
        b.Call("setup");
        b.Call("get");
        Assert.Equal(1009, b.Cpu.A | (b.Cpu.X << 8));
    }

    // ---------- The interface module ----------

    [Fact]
    public void The_interface_module_declares_each_entry_with_its_keeps_and_the_memory_used()
    {
        var b = new Binary(Counter);
        var api = b.Result.ApiModule!;
        Assert.Equal("api", b.Result.ApiModuleName![^3..]);
        Assert.Contains("const CODE_FIRST = $C000", api);
        Assert.Matches(@"const CODE_LAST = \$C[0-9A-F]{3}\b", api);
        Assert.Contains("const ZP_FIRST = $80", api);
        Assert.Matches(@"const ZP_LAST = \$8[1-3]\b", api);
        Assert.Contains("fn setup() @ $C000", api);
        Assert.Contains("fn init() @ $C003", api);
        Assert.Contains("fn add(n: u8 using A) -> u8 using A @ $C006 keeps X, Y", api);
        Assert.Contains("fn add_total(n: u8 using X) -> u16 using AY @ $C009", api);
    }

    [Fact]
    public void Keeps_comes_from_the_code_including_what_it_calls()
    {
        var b = new Binary("""
            memory { $C000..=$C0FF program }
            var n: u8
            fn uses_x() { for i in 0..4 using X { n += 1 } }
            fn calls_x() { uses_x() }
            fn uses_y() { for i in 0..4 using Y { n += 1 } }
            fn nothing() { n = 1 }
            fn value() -> u8 using A { return n }
            export uses_x, calls_x, uses_y, nothing, value
            """);
        string api = b.Result.ApiModule!;
        Assert.Contains("fn uses_x() @ $C003 keeps A, Y", api);        // 'inc n' does not touch A
        Assert.Contains("fn calls_x() @ $C006 keeps A, Y", api);       // what it calls counts
        Assert.Contains("fn uses_y() @ $C009 keeps A, X", api);
        Assert.Contains("fn nothing() @ $C00C keeps X, Y", api);       // it loads A
        Assert.Contains("fn value() -> u8 using A @ $C00F keeps X, Y", api);
    }

    /// <summary>The promise in the interface module is checked against the emulator: a kept register comes back unchanged.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Registers_listed_after_keeps_come_back_unchanged(int program)
    {
        string source = program == 0 ? Counter : """
            memory { $C000..=$C0FF program }
            var n: u8
            var big: [u8; 8]
            fn uses_x() { for i in 0..4 using X { n += 1 } }
            fn calls_x() { uses_x() }
            fn uses_y() { for i in 0..4 using Y { n += 1 } }
            fn nothing() { n = 1 }
            fn value() -> u8 using A { return n }
            fn fill(v: u8 using A) { for i in 0..8 { big[i] = v } }
            export uses_x, calls_x, uses_y, nothing, value, fill
            """;
        var b = new Binary(source);
        b.Call("setup");
        int checkedEntries = 0;
        foreach (var line in b.Result.ApiModule!.Split('\n').Where(l => l.StartsWith("fn ")))
        {
            string name = line[3..line.IndexOf('(')];
            var kept = line.Contains(" keeps ") ? line[(line.IndexOf(" keeps ") + 7)..].Trim().Split(", ") : [];
            b.Call(name, a: 0x11, x: 0x22, y: 0x33);
            foreach (var register in kept)
            {
                int value = register switch { "A" => b.Cpu.A, "X" => b.Cpu.X, _ => b.Cpu.Y };
                Assert.True(value == (register == "A" ? 0x11 : register == "X" ? 0x22 : 0x33), $"{name} promises to keep {register}");
            }
            checkedEntries++;
        }
        Assert.True(checkedEntries >= 4);
    }

    [Fact]
    public void A_region_used_by_the_binary_gets_constants()
    {
        var b = new Binary("""
            memory {
                $C000..=$C0FF  program
                $C800..=$C8FF  region work
            }
            var buf: [u8; 16] @ work
            fn clear() { buf[0] = 0 }
            export clear
            """);
        Assert.Contains("const WORK_FIRST = $C800", b.Result.ApiModule);
        Assert.Contains("const WORK_LAST = $C80F", b.Result.ApiModule);
    }

    // ---------- A Tern host ----------

    private static T WithHost<T>(string binarySource, Func<string, CompileResult, T> work)
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            string player = Path.Combine(dir.FullName, "player.tern");
            var binary = TernCompiler.Compile(binarySource, sourcePath: player);
            File.WriteAllText(Path.Combine(dir.FullName, binary.ApiModuleName + ".tern"), binary.ApiModule);
            return work(Path.Combine(dir.FullName, "host.tern"), binary);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void A_Tern_host_imports_the_interface_module_and_reserves_the_binarys_memory()
    {
        var run = WithHost(Counter, (hostPath, binary) => Run.Program("""
            import player_api
            memory {
                player_api.CODE_FIRST..=player_api.CODE_LAST  reserved
                player_api.ZP_FIRST..=player_api.ZP_LAST      reserved
            }
            var r1: u8
            var r2: u8
            var r3: u16
            var h1: u8
            var h2: u8
            var h3: u8
            var h4: u8
            fn main() {
                player_api.setup()
                player_api.init()
                h1 = 1; h2 = 2; h3 = 3; h4 = 4
                r1 = player_api.add(5)
                r2 = player_api.add(6)
                r3 = player_api.add_total(200)
                r3 = player_api.add_total(100)
            }
            """, sourcePath: hostPath, setup: ram => ram.Load((ushort)binary.LoadAddress, binary.Image)));
        Assert.Equal(5, run.U8("r1"));
        Assert.Equal(11, run.U8("r2"));
        Assert.Equal(300, run.U16("r3"));
        // The host's zero page leaves the binary's bytes alone.
        foreach (var name in new[] { "r1", "r2", "r3", "h1", "h2", "h3", "h4" })
            Assert.False(run.Address(name) is >= 0x80 and <= 0x83, $"{name} at ${run.Address(name):X2} is in the binary's zero page");
        // And its program areas do not touch the binary's code.
        Assert.All(run.Result.Sections.Where(s => s.Size > 0), s => Assert.False(s.Address is >= 0xC000 and <= 0xC0FF));
    }

    [Fact]
    public void A_host_calls_the_binary_from_its_raster_handler()
    {
        var run = WithHost("""
            memory { $C000..=$C0FF program }
            var ticks: u8
            fn init() { ticks = 0 }
            fn play() { ticks += 1 }
            export init, play
            """, (hostPath, binary) => Run.Program("""
            import player_api
            memory { player_api.CODE_FIRST..=player_api.CODE_LAST reserved }
            var frames: u8
            interrupt fn handler() {
                player_api.play()
                frames += 1
            }
            fn main() {
                player_api.setup()
                player_api.init()
                asm {
                    lda #handler.lo
                    sta $FFFE
                    lda #handler.hi
                    sta $FFFF
                    cli
                }
                while frames < 5 { }
            }
            """, sourcePath: hostPath, irqEvery: 3000, setup: ram => ram.Load((ushort)binary.LoadAddress, binary.Image)));
        Assert.True(run.U8("frames") >= 5);
    }


    // ---------- The examples ----------

    /// <summary>Runs a program that never ends for a number of cycles, with an IRQ every <paramref name="irqEvery"/> cycles.</summary>
    private static Ram Emulate(CompileResult result, long cycles, long irqEvery, Action<Ram>? onIrq = null)
    {
        var ram = new Ram();
        ram.Load((ushort)result.LoadAddress, result.Image);
        var cpu = new Cpu6502(ram) { PC = (ushort)result.Entry, SP = 0xFF, I = true };
        long nextIrq = irqEvery;
        while (cpu.Cycles < cycles)
        {
            if (irqEvery > 0 && cpu.Cycles >= nextIrq && cpu.Irq())
            {
                nextIrq = cpu.Cycles + irqEvery;
                onIrq?.Invoke(ram);
            }
            cpu.Step();
        }
        return ram;
    }

    [Fact]
    public void The_music_player_example_plays_a_scale_when_the_host_calls_it_from_the_raster_handler()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            foreach (var name in new[] { "player.tern", "player_host.tern" })
                File.Copy(AsmTests.FindExample(name), Path.Combine(dir.FullName, name));
            var player = TernCompiler.Compile(File.ReadAllText(Path.Combine(dir.FullName, "player.tern")), sourcePath: Path.Combine(dir.FullName, "player.tern"));
            File.WriteAllBytes(Path.Combine(dir.FullName, "player.prg"), player.Prg);
            File.WriteAllText(Path.Combine(dir.FullName, player.ApiModuleName + ".tern"), player.ApiModule);
            string hostPath = Path.Combine(dir.FullName, "player_host.tern");
            var host = TernCompiler.Compile(File.ReadAllText(hostPath), sourcePath: hostPath);

            Assert.Equal(0xC000, player.LoadAddress);
            Assert.Equal(new[] { "setup", "init", "play" }, player.Entries.Select(e => e.Name));
            // The host's file holds the player at its own address, after a zero-filled gap.
            Assert.Equal(0x0801, host.LoadAddress);
            Assert.Equal(player.Image, host.Image[(0xC000 - 0x0801)..]);

            var notes = new List<int>();
            var ram = Emulate(host, 3_000_000, irqEvery: 20_000, onIrq: ram =>
            {
                int freq = ram.Bytes[0xD400] | (ram.Bytes[0xD401] << 8);
                if (freq != 0 && (notes.Count == 0 || notes[^1] != freq)) notes.Add(freq);
            });
            Assert.Equal(new[] { 4455, 5001, 5613, 5947, 6675, 7493, 8410, 8910, 4455 }, notes.Take(9));
            Assert.Equal(15, ram.Bytes[0xD418]);                              // init set the volume
            Assert.Equal(0x11, ram.Bytes[0xD404] & 0xFF);                     // gate on
            Assert.True(ram.Bytes[host.Symbols["frames"]] > 100);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void The_sprite_data_example_loads_its_shape_at_the_fixed_address()
    {
        var path = AsmTests.FindExample("sprite_data.tern");
        var result = TernCompiler.Compile(File.ReadAllText(path), sourcePath: path);
        var ram = Emulate(result, 200_000, irqEvery: 0);
        Assert.Equal(0x0801, result.LoadAddress);
        Assert.Equal(0x203F - 0x0801, result.Image.Length);
        Assert.Equal(0x00, ram.Bytes[0x2000]);
        Assert.Equal(0x3C, ram.Bytes[0x2001]);
        Assert.Equal(0xFF, ram.Bytes[0x2000 + 3 * 10 + 1]);
        Assert.Equal(128, ram.Bytes[0x07F8]);                                // sprite pointer: $2000 / 64
        Assert.Equal(1, ram.Bytes[0xD015]);
        Assert.Equal(63, result.Sections.Single(s => s.Name == "ball").Size);
    }

    // ---------- Errors ----------

    [Theory]
    [InlineData("fn main() { }\nfn f() { }\nexport f", "A program with 'main' cannot export functions")]
    [InlineData("fn f() { }\nexport f, f", "'f' is exported twice")]
    [InlineData("export nope", "'nope' is not declared; 'export' names functions of the main file")]
    [InlineData("var v: u8\nexport v", "'v' is not a function, so it cannot be exported")]
    [InlineData("fn setup() { }\nexport setup", "'setup' is the name of the binary's startup entry")]
    [InlineData("interrupt fn h() { }\nexport h", "'h' is an interrupt handler")]
    [InlineData("inline fn h() { }\nexport h", "'h' is inline")]
    [InlineData("fn chrout(c: u8 using A) @ $FFD2\nexport chrout", "'chrout' is a routine at a fixed address, not part of this binary")]
    [InlineData("fn f(n: u8) { }\nexport f", "'f' takes 'n' in memory, which the caller of a binary cannot set")]
    [InlineData("fn f() { }\nexport", "Expected a function name after 'export'")]
    [InlineData("fn f() { }", "The program has no 'fn main()'. A binary for another program to call has an 'export' line instead")]
    [InlineData("fn f() { f() }\nexport f", "Recursion is not allowed")]
    public void Reports_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
    }

    [Fact]
    public void A_module_cannot_export()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "m.tern"), "fn f() { }\nexport f\n");
            var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(
                "import m\nfn main() { }", sourcePath: Path.Combine(dir.FullName, "main.tern")));
            Assert.Contains("Only the program's main file may export functions", e.Description);
        }
        finally { dir.Delete(recursive: true); }
    }
}
