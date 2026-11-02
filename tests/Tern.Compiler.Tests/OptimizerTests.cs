namespace Tern.Compiler.Tests;

/// <summary>
/// The optimizer (roadmap M7): unreachable code and loads of values a register already holds are
/// removed; volatile memory, calls, pointer writes and assembly blocks are respected.
/// </summary>
public class OptimizerTests
{
    /// <summary>The instructions of main, without addresses, bytes and cycles.</summary>
    private static string Main(Run r) => Main(r.Result);

    private static string Main(CompileResult result)
    {
        var listing = result.Listing;
        var start = listing.IndexOf("\nmain:", StringComparison.Ordinal);
        var code = new List<string>();
        foreach (var l in listing[(start + 1)..].Split('\n').Skip(1).Select(l => l.TrimEnd('\r')))
        {
            if (l.EndsWith(':') && !l.StartsWith(' ') && !l.StartsWith('$') && !l.StartsWith("main.")) break;
            if (l.StartsWith('$')) code.Add(l[17..].Split(';')[0].Trim());
        }
        return string.Join(" / ", code);
    }

    [Fact]
    public void A_stored_value_is_not_loaded_again()
    {
        var r = Run.Program("""
            var x: u8
            var y: u8
            fn main() {
                x = 5
                y = x
            }
            """);
        Assert.Equal("lda #$05 / sta x / sta y / rts", Main(r));
        Assert.Equal(5, r.U8("y"));
    }

    [Fact]
    public void An_index_register_is_not_loaded_twice()
    {
        var r = Run.Program("""
            var a: [u8; 4]
            var b: [u8; 4]
            var i: u8 = 2
            fn main() {
                a[i] = 7
                b[i] = 9
            }
            """);
        Assert.Equal("lda #$07 / ldx i / sta a,x / lda #$09 / sta b,x / rts", Main(r));
        Assert.Equal(9, r.U8("b", 2));
    }

    [Fact]
    public void An_indexed_write_changes_only_its_array()
    {
        var r = Run.Program("""
            var a: [u8; 4] @ $C000
            var b: u8 @ $C002
            var i: u8 = 2
            var v: u8
            fn main() {
                b = 1
                a[i] = 7
                v = b
            }
            """);
        // a and b are both at fixed addresses, so they may overlap: b is read again.
        Assert.Equal(7, r.U8("v"));
        Assert.Contains("lda b / sta v", Main(r));
    }

    [Fact]
    public void A_value_known_on_every_path_into_a_label_is_kept()
    {
        var r = Run.Program("""
            var c: u8 = 1
            var x: u8
            var y: u8
            fn main() {
                x = 3
                if c == 1 { y = 1 }
                y = x
            }
            """);
        // After the if, A is 3 only on the path that skips it; the store of 1 changed A.
        Assert.Contains("lda x / sta y", Main(r));
        Assert.Equal(3, r.U8("y"));
    }

    [Fact]
    public void Volatile_memory_is_read_every_time()
    {
        var r = Run.Program("""
            var reg: u8 @ $D020
            var v: u8
            fn main() {
                reg = 1
                v = reg
            }
            """);
        Assert.Equal("lda #$01 / sta reg / lda reg / sta v / rts", Main(r));
    }

    [Fact]
    public void Fixed_address_variables_may_overlap_so_a_write_to_one_forgets_the_others()
    {
        var r = Run.Program("""
            var a: u8 @ $C000
            var b: u8 @ $C000
            var v: u8
            fn main() {
                a = 1
                b = 2
                v = a
            }
            """);
        Assert.Equal(2, r.U8("v"));
    }

    [Theory]
    [InlineData("f()")]
    [InlineData("p[0] = 9")]
    [InlineData("asm {\n nop\n }")]
    public void Calls_pointer_writes_and_assembly_make_the_optimizer_forget(string between)
    {
        var r = Run.Program($$"""
            var x: u8 @ $C100
            var v: u8
            var p: ptr u8
            fn f() { x = 9 }
            fn main() {
                p = $C100
                x = 4
                {{between}}
                v = x
            }
            """);
        Assert.Contains("lda x / sta v", Main(r));
        Assert.Equal(between == "asm {\n nop\n }" ? 4 : 9, r.U8("v"));
    }

    [Fact]
    public void An_endless_loop_has_no_rts_after_it()
    {
        var r = TernCompiler.Compile("""
            var x: u8
            fn main() {
                while true { x += 1 }
            }
            """);
        Assert.Equal("inc x / jmp main.while0", Main(r));
    }

    [Fact]
    public void Assembly_is_never_changed()
    {
        var r = Run.Program("""
            var x: u8
            fn main() {
                asm {
                    lda #1
                    sta x
                    lda x
                    sta x
                    rts
                    nop
                }
            }
            """);
        // Only the rts the compiler adds after the block is removed: nothing reaches it.
        Assert.Equal("lda #$01 / sta x / lda x / sta x / rts / nop", Main(r));
    }

    [Fact]
    public void Flags_are_kept_when_a_load_sets_them_for_a_branch()
    {
        // The second load of x sets Z for the test, after cmp changed it.
        var r = Run.Program("""
            var x: u8
            var y: u8
            var r: u8
            fn main() {
                x = y
                if y < 3 { r = 1 }
                if x == 0 { r += 2 }
            }
            """);
        Assert.Equal(3, r.U8("r"));
    }
}

/// <summary>A parameter passed in X or Y stays in its register when the body allows it (roadmap M7).</summary>
public class RegisterParameterTests
{
    private static string Code(Run r, string function)
    {
        var listing = r.Result.Listing;
        var start = listing.IndexOf($"\n{function}:", StringComparison.Ordinal);
        var code = new List<string>();
        foreach (var l in listing[(start + 1)..].Split('\n').Skip(1).Select(l => l.TrimEnd('\r')))
        {
            if (l.EndsWith(':') && !l.StartsWith(' ') && !l.StartsWith('$') && !l.StartsWith($"{function}.")) break;
            if (l.StartsWith('$')) code.Add(l[17..].Split(';')[0].Trim());
        }
        return string.Join(" / ", code);
    }

    [Fact]
    public void A_parameter_in_x_indexes_directly()
    {
        var r = Run.Program("""
            var a: [u8; 8]
            fn set(i: u8 using X, v: u8) { a[i] = v }
            fn main() {
                set(3, 7)
                set(5, 9)
            }
            """);
        Assert.Equal("lda set.v / sta a,x / rts", Code(r, "set"));
        Assert.Equal(7, r.U8("a", 3));
        Assert.Equal(9, r.U8("a", 5));
        Assert.DoesNotContain("set.i", r.Result.Symbols.Keys);
    }

    [Fact]
    public void A_parameter_in_y_indexes_a_pointer_directly()
    {
        var r = Run.Program("""
            var p: ptr u8
            var out: u8
            fn get(n: u8 using Y) -> u8 { return p[n] }
            fn main() {
                p = $C000
                out = get(2)
            }
            """, setup: ram => ram.Bytes[0xC002] = 42);
        Assert.Equal("lda (p),y / rts", Code(r, "get"));
        Assert.Equal(42, r.U8("out"));
    }

    [Fact]
    public void A_loop_inside_uses_the_other_index_register()
    {
        var r = Run.Program("""
            var a: [u8; 8]
            var b: [u8; 8]
            fn fill(i: u8 using X) {
                for k in 0..8 { b[k] = a[i] + k }
            }
            fn main() {
                a[6] = 10
                fill(6)
            }
            """);
        for (int k = 0; k < 8; k++) Assert.Equal(10 + k, r.U8("b", k));
        Assert.DoesNotContain("stx fill.i", r.Result.Listing);
    }

    [Theory]
    [InlineData("fn f(i: u8 using X) { g()\n a[i] = 1 }")]                   // a call
    [InlineData("fn f(i: u8 using X) { i += 1\n a[i] = 1 }")]                // assigned
    [InlineData("fn f(i: u8 using X) { asm {\n nop\n }\n a[i] = 1 }")]       // an asm block
    [InlineData("fn f(i: u8 using X) { for k in 0..2 using Y { a[k] = i } }")] // a loop in a register
    [InlineData("fn f(i: u8 using X) -> u16 { a[i] = 1\n return 300 }")]    // returns in AX
    public void A_parameter_is_stored_when_the_body_needs_the_register(string f)
    {
        var r = Run.Program($$"""
            var a: [u8; 8]
            fn g() { }
            {{f}}
            fn main() { f(2) }
            """);
        Assert.Contains("stx f.i", r.Result.Listing);
    }

    [Fact]
    public void An_inline_function_keeps_its_parameter_in_the_register()
    {
        var r = Run.Program("""
            var a: [u8; 8]
            inline fn put(i: u8 using X, v: u8) { a[i] = v }
            fn main() {
                put(1, 4)
                put(2, 5)
            }
            """);
        Assert.Equal([0, 4, 5], new[] { r.U8("a"), r.U8("a", 1), r.U8("a", 2) });
        Assert.DoesNotContain("stx put.i", r.Result.Listing);
    }

    [Fact]
    public void A_function_whose_only_parameter_stays_in_a_register_may_be_shared_with_a_handler()
    {
        // It has no memory of its own (spec 8.1).
        var r = Run.Program("""
            var a: [u8; 8]
            fn bump(i: u8 using X) { a[i] += 1 }
            interrupt fn handler() { bump(1) }
            fn main() {
                asm {
                    lda #handler.lo
                    sta $FFFE
                    lda #handler.hi
                    sta $FFFF
                    cli
                }
                for _ in 0..100 { bump(0) }
                asm {
                    sei
                }
            }
            """, irqEvery: 500);
        Assert.Equal(100, r.U8("a"));
        Assert.Equal(r.Interrupts, r.U8("a", 1));
    }
}
