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
