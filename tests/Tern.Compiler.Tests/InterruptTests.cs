using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// <c>interrupt fn</c> (spec 6.3, 8.1): saves the registers it changes, ends with <c>rti</c>, and is a
/// root of the call graph with frames that never overlap main's.
/// </summary>
public class InterruptTests
{
    /// <summary>The instructions of a function, without addresses, bytes and cycles.</summary>
    private static string Code(Run r, string function)
    {
        var listing = r.Result.Listing;
        var start = listing.IndexOf($"\n{function}:", StringComparison.Ordinal);
        var code = new List<string>();
        foreach (var l in listing[(start + 1)..].Split('\n').Skip(1).Select(l => l.TrimEnd('\r')))
        {
            if (l.EndsWith(':') && !l.StartsWith(' ') && !l.StartsWith('$') && !l.StartsWith(';') && !l.StartsWith($"{function}.")) break;
            if (l.StartsWith('$')) code.Add(l[17..].Split(';')[0].Trim());
        }
        return string.Join("|", code);
    }

    /// <summary>Installs <c>handler</c> in the IRQ vector and enables interrupts.</summary>
    private const string Install = """
        asm {
            sei
            lda #handler.lo
            sta $FFFE
            lda #handler.hi
            sta $FFFF
            cli
        }
        """;

    [Fact]
    public void A_handler_running_during_main_changes_nothing_main_uses()
    {
        var r = Run.Program($$"""
            var src: [u8; 200]
            var copy: [u8; 200]
            var sum: u16
            var ticks: u16
            var hits: [u8; 4]
            var seen: u8

            fn work(n: u8) -> u8 {
                var t: u8 = n
                t = t + (n >> 1)
                return t ^ $5A
            }
            fn bump(k: u8) -> u8 {
                var t: u8 = k
                return t + 1
            }
            interrupt fn handler() {
                ticks += 1
                for i in 0..4 using X { hits[i] += 1 }
                for j in 0..2 { seen = bump(j) }
            }
            fn main() {
                {{Install}}
                for i in 0..200 using X { src[i] = (i << 1) + i }
                for i in 0..200 using Y { copy[i] = src[i] }
                for i in 0..200 { sum = sum + work(src[i]) }
                asm {
                    sei
                }
            }
            """, irqEvery: 400);
        Assert.True(r.Interrupts > 20, $"{r.Interrupts} interrupts");
        int expected = 0;
        for (int i = 0; i < 200; i++)
        {
            int d = i * 3 & 0xFF;
            Assert.Equal(d, r.U8("src", i));
            Assert.Equal(d, r.U8("copy", i));
            expected += ((d + (d >> 1)) & 0xFF) ^ 0x5A;
        }
        Assert.Equal(expected & 0xFFFF, r.U16("sum"));
        Assert.Equal(r.Interrupts, r.U16("ticks"));
        Assert.Equal(r.Interrupts & 0xFF, r.U8("hits", 3));
        Assert.Equal(2, r.U8("seen"));
    }

    [Fact]
    public void Handler_frames_come_after_every_main_frame()
    {
        var r = Run.Program("""
            fn deep(a: u8) -> u8 {
                var x: u8 = a
                var y: u8 = x
                return y
            }
            fn work(n: u8) -> u8 { return deep(n) }
            fn helper(k: u8) -> u8 {
                var t: u8 = k
                return t
            }
            var v: u8
            interrupt fn handler() { v = helper(v) }
            fn main() { v = work(1) }
            """);
        var s = r.Result.Symbols;
        int[] mainFrames = [s["work.n"], s["deep.a"], s["deep.x"], s["deep.y"]];
        int[] handlerFrames = [s["helper.k"], s["helper.t"]];
        foreach (int h in handlerFrames)
            Assert.DoesNotContain(h, mainFrames);
        Assert.Contains("handler", s.Keys);   // a root of its own, though nothing calls it
    }

    [Theory]
    // Changes nothing: no saving at all.
    [InlineData("interrupt fn handler() { count += 1 }", "inc count|rti")]
    // Changes A only.
    [InlineData("interrupt fn handler() { count = 5 }", "pha|lda #$05|sta count|pla|rti")]
    // X is saved through A.
    [InlineData("interrupt fn handler() { buf[count] = 0 }", "pha|txa|pha|lda #$00|ldx count|sta buf,x|pla|tax|pla|rti")]
    // A call may change every register.
    [InlineData("interrupt fn handler() { f() }", "cld|pha|txa|pha|tya|pha|jsr f|pla|tay|pla|tax|pla|rti")]
    // return goes to the code that restores the registers.
    [InlineData("interrupt fn handler() {\n if count == 0 { return }\n count = 0\n }",
        "pha|lda count|bne handler.endif1|jmp handler.exit0|lda #$00|sta count|pla|rti")]
    public void Code_shapes(string handler, string expected)
    {
        var r = Run.Program($$"""
            var count: u8
            var buf: [u8; 4]
            fn f() { }
            {{handler}}
            fn main() { }
            """);
        Assert.Equal(expected, Code(r, "handler"));
    }

    [Fact]
    public void A_return_in_a_handler_restores_the_registers()
    {
        var r = Run.Program($$"""
            var ticks: u8
            var odd: u8
            var total: u16
            interrupt fn handler() {
                ticks += 1
                if ticks & 1 == 0 { return }
                odd += 1
            }
            fn main() {
                {{Install}}
                for i in 0..250 using X { total = total + (i as u16) }
                asm {
                    sei
                }
            }
            """, irqEvery: 61);
        Assert.True(r.Interrupts > 10);
        Assert.Equal((r.Interrupts + 1) / 2, r.U8("odd"));
        Assert.Equal(249 * 250 / 2, r.U16("total"));
    }

    [Fact]
    public void A_routine_at_a_fixed_address_may_be_called_from_main_and_a_handler()
    {
        var r = Run.Program("""
            fn store(v: u8 using A) @ $C000
            interrupt fn handler() { store(1) }
            fn main() { store(2) }
            """, setup: ram => ram.Bytes[0xC000] = 0x60); // rts
        Assert.Contains("handler", r.Result.Symbols.Keys);
    }

    [Theory]
    // Review M6, finding 1: mul8 and div8 share math's private work bytes.
    [InlineData("import math\nvar q: u8\nvar p: u16\ninterrupt fn handler() { q = math.div8(200, 7) }\nfn main() { p = math.mul8(3, 13) }",
        "'math.mul8' (called from main) and 'math.div8' (called from the interrupt handler 'handler') both use 'math.w0', a private variable of its module")]
    [InlineData("import math\nvar q: u8\nvar p: u16\ninterrupt fn h1() { q = math.div8(200, 7) }\ninterrupt fn h2() { p = math.mul16(3, 13) }\nfn main() { }",
        "(called from the interrupt handler 'h1') and 'math.mul16' (called from the interrupt handler 'h2') both use 'math.w")]
    public void Private_module_variables_used_from_two_roots_are_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
        Assert.Null(e.Pos.File); // reported in the program, at the handler
    }

    /// <summary>Writes the given user modules to a temporary folder and runs <c>work</c> with the path of a main file there.</summary>
    private static T WithModules<T>((string Name, string Text)[] modules, Func<string, T> work)
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            foreach (var (name, text) in modules) File.WriteAllText(Path.Combine(dir.FullName, name + ".tern"), text);
            return work(Path.Combine(dir.FullName, "main.tern"));
        }
        finally { dir.Delete(recursive: true); }
    }

    private static Run RunWithModules(string main, (string Name, string Text)[] modules, long irqEvery = 0) =>
        WithModules(modules, path => Run.Program(main, sourcePath: path, irqEvery: irqEvery));

    [Fact]
    public void A_module_handler_may_share_the_modules_private_variable()
    {
        // Spec 8.1: the module's author wrote both sides, so a handler in the module may use its private variable.
        var r = RunWithModules("""
            import c64
            import timer
            var seen: u16
            fn main() {
                c64.kernal_off()
                c64.set_irq(addr(timer.tick))
                c64.interrupts_on()
                for _ in 0..200 { seen = timer.now() }
            }
            """, [("timer", """
            private var ticks: u16
            interrupt fn tick() { ticks += 1 }
            fn now() -> u16 { return ticks }
            """)], irqEvery: 300);
        Assert.True(r.Interrupts > 2);
        Assert.Equal(r.Interrupts, r.U16("seen"));
    }

    private const string Main48 = """
        import c64
        import timer
        var seen: u16
        fn main() {
            c64.kernal_off()
            c64.set_irq(addr(timer.tick))
            c64.interrupts_on()
            for _ in 0..200 { seen = timer.now() }
        }
        """;

    [Fact]
    public void A_helper_called_only_from_the_modules_handler_is_on_the_handler_side()
    {
        // Spec 8.1: bump and bump2 are plain functions, called only from tick, so they may use the private variable.
        var r = RunWithModules(Main48, [("timer", """
            private var ticks: u16
            private fn bump2() { ticks += 1 }
            private fn bump() { bump2() }
            interrupt fn tick() { bump() }
            fn now() -> u16 { return ticks }
            """)], irqEvery: 300);
        Assert.True(r.Interrupts > 2);
        Assert.Equal(r.Interrupts, r.U16("seen"));
    }

    [Theory]
    [InlineData("""
        private var ticks: u16
        private fn bump() { ticks += 1 }
        interrupt fn tick() { bump() }
        fn poke() { bump() }
        fn now() -> u16 { return ticks }
        """)]                                           // the helper is also called from main-side code
    [InlineData("""
        private var ticks: u16
        fn bump() { ticks += 1 }
        interrupt fn tick() { bump() }
        fn now() -> u16 { return ticks }
        """)]                                           // a public helper that the main file calls too (below)
    public void A_helper_also_called_from_main_is_still_an_error(string timer)
    {
        // Main also calls the module's helper: a function reached from both sides is not on the handler side.
        string call = timer.Contains("fn poke") ? "timer.poke()" : "timer.bump()";
        string main = Main48.Replace("seen = timer.now() }", "seen = timer.now(); " + call + " }");
        var e = Assert.Throws<CompileError>(() => WithModules([("timer", timer)], path => TernCompiler.Compile(main, null, path, "lib")));
        Assert.Contains("a private variable of its module", e.Description);
        Assert.Contains("'timer.ticks'", e.Description);
    }

    [Fact]
    public void A_helper_called_by_the_modules_handler_and_by_a_handler_of_another_file_is_still_an_error()
    {
        var e = Assert.Throws<CompileError>(() => WithModules([("timer", """
            private var ticks: u16
            fn bump() { ticks += 1 }
            interrupt fn tick() { bump() }
            fn now() -> u16 { return ticks }
            """)], path => TernCompiler.Compile("""
            import c64
            import timer
            var seen: u16
            interrupt fn frame() { timer.bump() }
            fn main() {
                c64.kernal_off()
                c64.set_irq(addr(frame))
                seen = timer.now()
            }
            """, null, path, "lib")));
        Assert.Contains("a private variable of its module", e.Description);
    }

    [Fact]
    public void A_handler_of_another_module_may_not_share_a_private_variable()
    {
        var modules = new[]
        {
            ("b", """
                private var n: u8
                fn poke() { n += 1 }
                fn peek() -> u8 { return n }
                """),
            ("a", """
                import b
                interrupt fn h() { b.poke() }
                """),
        };
        var main = """
            import a
            import b
            import c64
            var x: u8
            fn main() {
                c64.kernal_off()
                c64.set_irq(addr(a.h))
                x = b.peek()
            }
            """;
        var e = Assert.Throws<CompileError>(() =>
            WithModules(modules, path => TernCompiler.Compile(main, null, path, "lib")));
        Assert.Contains("both use 'b.n', a private variable of its module", e.Description);
    }

    [Fact]
    public void Functions_with_no_memory_may_be_shared_with_a_handler()
    {
        // bump, ack and the routine at a fixed address have no memory of their own (spec 8.1).
        var r = Run.Program($$"""
            var count: u8
            var acks: u8
            var done: u8
            fn store(v: u8 using A) @ $C000 keeps X, Y
            fn bump() { count += 1 }
            fn twice() { bump()
                bump() }
            inline fn ack() { acks += 1 }
            interrupt fn handler() {
                twice()
                ack()
                store(1)
            }
            fn main() {
                {{Install}}
                for _ in 0..100 { twice()
                    ack() }
                store(2)
                asm {
                    sei
                }
                done = 1
            }
            """, setup: ram => ram.Bytes[0xC000] = 0x60, irqEvery: 300); // rts
        Assert.True(r.Interrupts > 0);
        Assert.Equal(1, r.U8("done"));
        Assert.Equal((byte)(200 + 2 * r.Interrupts), r.U8("count"));
        Assert.Equal((byte)(100 + r.Interrupts), r.U8("acks"));
    }

    [Fact]
    public void Public_variables_and_private_data_may_be_shared_with_a_handler()
    {
        // math.remainder8 is public; text.DIGITS, used by hex8 and dec8, is private but read-only data.
        var r = Run.Program($$"""
            import math
            import text
            var shown: [u8; 4]
            var q: u8
            var rest: u8
            interrupt fn handler() {
                text.hex8(addr(shown), 171)
                rest = math.remainder8
            }
            fn main() {
                {{Install}}
                q = math.div8(200, 7)
                for _ in 0..50 { var n: u8 = text.dec8(addr(shown) + 2, 42) }
                asm {
                    sei
                }
            }
            """, irqEvery: 300);
        Assert.True(r.Interrupts > 0);
        Assert.Equal(28, r.U8("q"));
        Assert.Equal(new byte[] { 1, 2, 0x34, 0x32 }, r.Ram.Bytes[r.Address("shown")..(r.Address("shown") + 4)]); // "AB", "42"
    }

    [Theory]
    [InlineData("interrupt fn h(x: u8) { }\nfn main() { }", "An interrupt handler takes no parameters and returns no value")]
    [InlineData("interrupt fn h() -> u8 { return 1 }\nfn main() { }", "An interrupt handler takes no parameters and returns no value")]
    [InlineData("interrupt fn h() { }\nfn main() { h() }", "'h' is an interrupt fn, which only the CPU or another interrupt fn calls")]
    [InlineData("interrupt fn h() { }\nfn main() { asm {\n jsr h\n } }", "'h' is an interrupt handler: it ends with rti, so jsr cannot call it")]
    [InlineData("var v: u8\nfn f() { var t = v\n v = t + 1 }\ninterrupt fn h() { f() }\nfn main() { f() }",
        "'f' is called from main and from the interrupt handler 'h'. An interrupt can stop it halfway and run it again, which would overwrite its memory: the local variable 't'")]
    [InlineData("var v: u8\nfn f() { var t = v\n v = t + 1 }\nfn g() { f() }\ninterrupt fn h() { g() }\nfn main() { f() }", "'f' is called from main and from the interrupt handler 'h'")]
    [InlineData("var v: u8\nfn f() { var t = v\n v = t + 1 }\ninterrupt fn h1() { f() }\ninterrupt fn h2() { f() }\nfn main() { }",
        "'f' is called from the interrupt handler 'h1' and from the interrupt handler 'h2'")]
    [InlineData("var v: u8\nfn f(n: u8) { v = n }\ninterrupt fn h() { f(1) }\nfn main() { f(2) }", "its memory: the parameter 'n' in memory")]
    [InlineData("var v: u8\nfn f(n: u8 using A) { v = n }\ninterrupt fn h() { f(1) }\nfn main() { f(2) }", "its memory: the parameter 'n', which it keeps in memory (it arrives in A)")]
    // The inner counter goes in X; the outer one stays in memory.
    [InlineData("var v: u8\nfn f() {\n for _ in 0..v {\n for _ in 0..v { v += 1 }\n }\n}\ninterrupt fn h() { f() }\nfn main() { f() }", "its memory: the counter of the loop at line 3")]
    [InlineData("var v: u16\nvar w: u16\nfn g() -> u8 { return 1 }\nfn f() {\n v = w + (g() as u16)\n}\ninterrupt fn h() { f() }\nfn main() { f() }",
        "its memory: a hidden temporary for the code at line 5")]
    [InlineData("var v: u8\nfn g() { var t = v\n v = t + 1 }\nfn f() { g() }\ninterrupt fn h() { f() }\nfn main() { f() }", "'g' is called from main and from the interrupt handler 'h'")]
    [InlineData("fn main() { interrupt fn h() { } }", "'interrupt fn' must be declared outside functions")]
    [InlineData("interrupt asm fn h() {\n rti\n }\nfn main() { }", "An interrupt handler in assembly is an 'asm fn' that saves what it changes and ends with rti")]
    [InlineData("interrupt var x: u8\nfn main() { }", "'interrupt' goes before 'fn'")]
    [InlineData("interrupt fn h() @ $1000\nfn main() { }", "An interrupt handler needs a body")]
    [InlineData("interrupt fn main() { }", "'main' cannot be an interrupt handler")]
    public void Reports_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
    }
}
