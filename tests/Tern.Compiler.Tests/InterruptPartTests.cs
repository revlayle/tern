using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>An <c>interrupt fn</c> called from another <c>interrupt fn</c> is a part (spec 6.3, decision 34).</summary>
public sealed class InterruptPartTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tern");
    private string Lib => Path.Combine(_dir.FullName, "lib");
    private string Src => Path.Combine(_dir.FullName, "src");

    public InterruptPartTests()
    {
        Directory.CreateDirectory(Lib);
        Directory.CreateDirectory(Src);
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(Src, name), text);

    private Run RunMain(string source, long irqEvery = 0) =>
        Run.Program(source, sourcePath: Path.Combine(Src, "main.tern"), libraryDirectory: Lib, irqEvery: irqEvery);

    private CompileError Fail(string source) =>
        Assert.Throws<CompileError>(() => TernCompiler.Compile(source, null, Path.Combine(Src, "main.tern"), Lib));

    private const string Install = """
        asm {
            sei
            lda #frame.lo
            sta $FFFE
            lda #frame.hi
            sta $FFFF
            cli
        }
        """;

    /// <summary>Review code-quality-semantics 5: both were parts, so no root reached them and they were dropped.</summary>
    [Fact]
    public void Handlers_that_call_each_other_are_recursion()
    {
        var e = Fail("""
            var out: u8
            interrupt fn a() { out += 1
             b() }
            interrupt fn b() { out += 2
             a() }
            fn main() { out = 1 }
            """);
        Assert.Contains("Recursion is not allowed: a -> b -> a", e.Message);
    }

    [Fact]
    public void A_part_with_parameters_and_a_flag_return_runs_inside_the_handler()
    {
        var r = RunMain($$"""
            var total: u8
            var tops: u8
            var frames: u8
            interrupt fn add(n: u8 using X) -> u8 using A {
                total += n
                return total
            }
            interrupt fn top() -> bool using carry {
                frames += 1
                return frames >= 3
            }
            interrupt fn frame() {
                var t: u8 = add(2)
                if top() { tops += 1 }
            }
            fn main() {
                {{Install}}
                while tops < 5 { }
                asm { sei }
            }
            """, irqEvery: 300);
        Assert.True(r.U8("tops") >= 5);
        Assert.Equal(r.U8("frames"), r.U8("total") / 2);
        var add = Listing.Instructions(r, "add");
        Assert.Equal("rts", add[^1]);
        Assert.DoesNotContain("rti", add);
        Assert.DoesNotContain("pha", add);
        var frame = Listing.Instructions(r, "frame");
        Assert.Equal("rti", frame[^1]);
        Assert.Equal(1, frame.Count(i => i == "rti"));
        Assert.Contains(frame, i => i.StartsWith("jsr") && i.EndsWith("add"));
    }

    [Fact]
    public void Two_module_parts_share_private_state_with_their_modules_main_side_functions()
    {
        Write("tick.tern", """
            private var count: u8
            private var wide: u16
            interrupt fn advance() {
                count += 1
                wide += 3
            }
            fn read() -> u8 {
                return count
            }
            fn read_wide() -> u16 {
                return wide
            }
            """);
        Write("beat.tern", """
            private var phase: u8
            interrupt fn pulse() -> bool using carry {
                phase += 1
                return (phase & 3) == 0
            }
            fn get() -> u8 { return phase }
            """);
        var r = RunMain($$"""
            import tick
            import beat
            var beats: u8
            var seen: u8
            var seen_wide: u16
            interrupt fn frame() {
                tick.advance()
                if beat.pulse() { beats += 1 }
            }
            fn main() {
                {{Install}}
                while tick.read() < 40 { }
                asm { sei }
                seen = tick.read()
                seen_wide = tick.read_wide()
            }
            """, irqEvery: 250);
        Assert.True(r.Interrupts >= 40);
        // After sei no more interrupts: the final counters match what main saw.
        Assert.Equal(r.U8("seen"), r.U8("tick.count"));
        Assert.Equal(r.U8("seen") * 3, r.U16("seen_wide"));
        Assert.Equal(r.U8("beat.phase") / 4, r.U8("beats"));
        Assert.Equal(r.U8("tick.count"), r.U8("beat.phase"));
    }

    /// <summary>Handlers clear decimal mode first, unless they run no adc, sbc or jsr (spec 6.3).</summary>
    [Theory]
    [InlineData("var x: u8\nvar y: u8\ninterrupt fn frame() { x += y }", true)]
    [InlineData("var x: u8\nvar y: u8\ninterrupt fn frame() { x = y - 1 }", true)]
    [InlineData("var x: u8\nvar y: u8\ninterrupt fn frame() { asm { sec\n sbc y\n } }", true)]
    [InlineData("var x: u8\nvar y: u8\ninterrupt fn part() { x = 1 }\ninterrupt fn frame() { part() }", true)]
    [InlineData("var x: u8\nvar y: u8\nfn f() { }\ninterrupt fn frame() { f() }", true)]
    [InlineData("var x: u8\nvar y: u8\ninterrupt fn frame() { x += 1\n y = x & 3\n x = y << 1 }", false)]
    [InlineData("var x: u8\nvar y: u8\ninterrupt fn frame() { x = y\n asm { inc y\n } }", false)]
    public void Handlers_clear_decimal_mode_only_when_they_add_subtract_or_call(string source, bool cld)
    {
        var r = Run.Program(source + "\nfn main() { }");
        var code = Listing.Instructions(r, "frame");
        Assert.Equal(cld, code.Contains("cld"));
        if (cld) Assert.Equal("cld", code[0]);
        Assert.Equal("rti", code[^1]);
    }

    [Fact]
    public void A_part_ends_with_rts_and_its_handler_with_rti()
    {
        var r = Run.Program($$"""
            var x: u8
            interrupt fn part() { x += 1 }
            interrupt fn frame() { part() }
            fn main() { {{Install}} }
            """);
        Assert.Equal("rts", Listing.Instructions(r, "part")[^1]);
        Assert.Equal("rti", Listing.Instructions(r, "frame")[^1]);
    }

    [Theory]
    [InlineData("interrupt fn p() { }\nfn main() { p() }", "'p' is an interrupt fn, which only the CPU or another interrupt fn calls")]
    [InlineData("interrupt fn p() { }\nfn helper() { p() }\ninterrupt fn h() { helper() }\nfn main() { }", "'p' is an interrupt fn, which only the CPU or another interrupt fn calls")]
    [InlineData("interrupt fn p(a: u8) { }\nfn main() { }", "'p' has parameters, so it can only be called as a part")]
    [InlineData("interrupt fn p() -> u8 { return 1 }\nfn main() { }", "'p' has a return value, so it can only be called as a part")]
    [InlineData("interrupt fn p() { }\ninterrupt fn h() { p() }\nfn main() { asm {\n lda #p.lo\n } }", "is called by another interrupt fn")]
    [InlineData("interrupt fn p() { h() }\ninterrupt fn h() { p() }\ninterrupt fn r() { p() }\nfn main() { }", "recursion")]
    public void Part_errors(string source, string expected)
    {
        var e = Fail(source);
        Assert.Contains(expected, e.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_part_reached_from_two_handlers_may_have_memory_of_its_own()
    {
        // No memory: allowed.
        Run.Program("""
            var x: u8
            var y: u8
            interrupt fn p() { x += 1 }
            interrupt fn h1() { p() }
            interrupt fn h2() { p() }
            fn main() { }
            """);
        // With a local: allowed too, in a frame that overlaps no frame of either handler (spec 8.1).
        InterruptTests.AssertFrameOverlapsNoOther(TernCompiler.Compile("""
            var x: u8
            interrupt fn p() {
                var t: u8 = x
                x = t + 1
            }
            interrupt fn h1() {
                var a: u8 = x
                p()
                x = a
            }
            interrupt fn h2() {
                var b: u8 = x
                p()
                x = b
            }
            fn main() { }
            """), "p");
    }

    [Fact]
    public void A_handler_called_only_by_an_uncompiled_module_handler_is_still_a_handler()
    {
        Write("timer.tern", "var ticks: u8\ninterrupt fn tick() { ticks += 1 }\ninterrupt fn both() {\n    tick()\n    ticks += 2\n}");
        var r = RunMain("""
            import timer
            fn main() {
                asm {
                    lda #timer.tick.lo
                    sta $FFFE
                    lda #timer.tick.hi
                    sta $FFFF
                }
            }
            """);
        Assert.Equal("rti", Listing.Instructions(r, "timer.tick")[^1]);
        Assert.DoesNotContain("\ntimer.both:", r.Result.Listing);
    }

    [Fact]
    public void A_handler_called_by_a_compiled_module_handler_is_a_part_and_cannot_also_be_installed()
    {
        Write("timer.tern", "var ticks: u8\ninterrupt fn tick() { ticks += 1 }\ninterrupt fn both() {\n    tick()\n}");
        var e = Fail("""
            import timer
            fn main() {
                asm {
                    lda #timer.both.lo
                    lda #timer.tick.lo
                }
            }
            """);
        Assert.Contains("is called by another interrupt fn", e.Description);
    }

    [Fact]
    public void A_private_variable_of_a_module_is_still_an_error_when_a_plain_function_reaches_it_from_a_handler()
    {
        Write("m.tern", """
            private var n: u8
            fn bump() { n += 1 }
            fn read() -> u8 { return n }
            """);
        var e = Fail("""
            import m
            var s: u8
            interrupt fn h() { m.bump() }
            fn main() { s = m.read() }
            """);
        Assert.Contains("a private variable of its module", e.Description);
    }
}
