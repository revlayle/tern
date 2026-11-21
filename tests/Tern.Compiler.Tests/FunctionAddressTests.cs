using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// <c>addr(f)</c> of a function (spec 6.5): a <c>u16</c> known when the program is placed, in code
/// and in initial values. Address-taken functions and what they reach get frames of their own.
/// </summary>
public class FunctionAddressTests
{
    /// <summary>Calls the function whose address is in <c>vec</c>.</summary>
    private const string CallVec = """
        asm {
            jsr .go
            jmp .done
        .go:
            jmp (vec)
        .done:
        }
        """;

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Addr_of_a_function_is_its_address()
    {
        var r = Run.Program("""
            var v: u16
            var lo: u8
            var hi: u8
            fn f() { }
            fn main() {
                v = addr(f)
                lo = addr(f).lo
                hi = addr(f).hi
            }
            """);
        int f = r.Address("f");
        Assert.Equal(f, r.U16("v"));
        Assert.Equal(f & 0xFF, r.U8("lo"));
        Assert.Equal(f >> 8, r.U8("hi"));
        Assert.Contains("lda #f.lo", Listing.Instructions(r));
    }

    [Fact]
    public void A_function_reached_only_through_its_address_is_compiled_and_called()
    {
        var r = Run.Program($$"""
            var vec: u16
            var out: u8
            fn f() { out = 42 }
            fn main() {
                vec = addr(f)
                {{CallVec}}
            }
            """);
        Assert.Equal(42, r.U8("out"));
    }

    [Theory]
    [InlineData("data")]
    [InlineData("var")]
    [InlineData("linear data")]
    [InlineData("var @ zp")]
    public void A_jump_table_of_function_addresses_dispatches(string kind)
    {
        string decl = kind switch
        {
            "var @ zp" => "var table: [u16; 3] @ zp = [addr(a), addr(b), addr(c)]",
            _ => $"{kind} table: [u16; 3] = [addr(a), addr(b), addr(c)]",
        };
        var r = Run.Program($$"""
            var vec: u16
            var out: u8
            fn a() { out += 1 }
            fn b() { out += 10 }
            fn c() { out += 100 }
            {{decl}}
            fn call(i: u8) {
                vec = table[i]
                {{CallVec}}
            }
            fn main() { call(0); call(2); call(2); call(1) }
            """);
        Assert.Equal(211, r.U8("out"));
    }

    [Fact]
    public void A_single_function_address_can_be_an_initial_value()
    {
        var r = Run.Program($$"""
            var vec: u16 = addr(f)
            var out: u8
            fn f() { out = 7 }
            fn main() { {{CallVec}} }
            """);
        Assert.Equal(7, r.U8("out"));
        Assert.Equal(r.Address("f"), r.U16("vec"));
    }

    [Fact]
    public void A_module_table_keeps_its_functions_when_it_is_used()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "ops.tern"), """
                var out: u8
                fn inc() { out += 1 }
                fn dbl() { out = out << 1 }
                data table: [u16; 2] = [addr(inc), addr(dbl)]
                fn unused() { }
                data unused_table: [u16; 1] = [addr(unused)]
                """);
            string main = $$"""
                import ops
                var vec: u16
                fn run(i: u8) {
                    vec = ops.table[i]
                    {{CallVec}}
                }
                fn main() { run(0); run(1); run(1); run(0) }
                """;
            var r = Run.Program(main, sourcePath: Path.Combine(dir.FullName, "main.tern"));
            Assert.Equal(5, r.U8("ops.out"));
            Assert.DoesNotContain("ops.unused", r.Result.Symbols.Keys);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Addr_of_a_routine_at_a_fixed_address_is_that_address()
    {
        var r = Run.Program("""
            fn chrout(ch: u8 using A) @ $FFD2
            var v: u16
            data t: [u16; 1] = [addr(chrout)]
            fn main() { v = addr(chrout) }
            """);
        Assert.Equal(0xFFD2, r.U16("v"));
        Assert.Equal(0xD2, r.U8("t"));
        Assert.Equal(0xFF, r.U8("t", 1));
    }

    [Fact]
    public void Addr_of_a_module_interrupt_handler_keeps_it()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "timer.tern"), """
                var ticks: u8
                interrupt fn tick() { ticks += 1 }
                interrupt fn unused() { }
                """);
            var r = Run.Program("""
                import timer
                var v: u16
                fn main() { v = addr(timer.tick) }
                """, sourcePath: Path.Combine(dir.FullName, "main.tern"));
            Assert.Equal(r.Address("timer.tick"), r.U16("v"));
            Assert.DoesNotContain("timer.unused", r.Result.Symbols.Keys);
        }
        finally { dir.Delete(true); }
    }

    /// <summary>The first and last byte of each local and parameter (all u8 here), by function.</summary>
    private static Dictionary<string, List<int>> Frames(Run r) =>
        r.Result.Symbols.Where(s => s.Key.Contains('.') && s.Value >= 0)
            .GroupBy(s => s.Key[..s.Key.IndexOf('.')])
            .ToDictionary(g => g.Key, g => g.Select(s => s.Value).ToList());

    [Fact]
    public void Frames_of_address_taken_functions_and_their_callees_overlap_nothing()
    {
        // Without addr(), a and g are on different call paths and would share memory.
        var r = Run.Program($$"""
            var vec: u16
            var out: u8
            fn h(n: u8) -> u8 { var t: u8 = n + 1
                return t }
            fn a(k: u8 using A) { var x: u8 = k
                out = h(x) }
            fn g(n: u8) { var y: u8 = n
                var z: u8 = y + n
                out = z }
            fn main() {
                var m: u8 = 3
                g(m)
                vec = addr(a)
                asm {
                    lda #5
                    jsr .go
                    jmp .done
                .go:
                    jmp (vec)
                .done:
                }
            }
            """);
        Assert.Equal(6, r.U8("out"));
        var frames = Frames(r);
        foreach (var isolated in new[] { "a", "h" })
            foreach (var (other, addresses) in frames.Where(f => f.Key != isolated))
                Assert.Empty(frames[isolated].Intersect(addresses));
    }

    [Fact]
    public void Assembly_taking_an_address_isolates_the_frame_too()
    {
        var r = Run.Program("""
            var vec: u16
            var out: u8
            fn a() { var x: u8 = out
                out = x + 1 }
            fn g() { var y: u8 = out
                out = y + 2 }
            fn main() {
                g()
                asm {
                    lda #a.lo
                    sta vec
                    lda #a.hi
                    sta vec+1
                }
            }
            """, comparePlain: false);   // the asm block stores an address, which differs without the optimizer
        Assert.NotEqual(r.Address("a.x"), r.Address("g.y"));
    }

    [Fact]
    public void Without_an_address_sibling_frames_share_memory()
    {
        // The control case for the two tests above.
        var r = Run.Program("""
            var out: u8
            fn a() { var x: u8 = out
                out = x + 1 }
            fn g() { var y: u8 = out
                out = y + 2 }
            fn main() { g(); a() }
            """);
        Assert.Equal(r.Address("a.x"), r.Address("g.y"));
    }

    [Theory]
    [InlineData("inline fn f() { }\nvar v: u16\nfn main() { v = addr(f) }", 3, 22, "'f' is inline: it is copied into each caller and has no address of its own. Remove 'inline'")]
    [InlineData("fn f(n: u8) { }\nvar v: u16\nfn main() { v = addr(f) }",
        3, 22, "'f' takes 'n' in memory, which a call through an address cannot set. Pass it in a register (n: u8 using A)")]
    [InlineData("fn f() { }\nvar v: ptr u8\nfn main() { v = addr(f) }", 3, 17, "Expected ptr u8, found u16. Convert with 'as ptr u8'")]
    [InlineData("fn f() { }\nvar v: u8\nfn main() { v = addr(f) }", 3, 17, "Expected u8, found u16. Convert with 'as u8'")]
    [InlineData("fn f() { }\ndata t: [u8; 1] = [addr(f)]\nfn main() { }", 2, 20, "Expected u8, found u16. Convert with 'as u8'")]
    [InlineData("fn f() { }\nconst C = addr(f)\nfn main() { }", 2, 16, "addr(f) is known only when the program is placed, so it cannot be part of a constant. Use it in a var, data or code")]
    public void Reports_errors(string source, int line, int column, string expected)
    {
        Expect.Error(source, line, column, expected);
    }
}
