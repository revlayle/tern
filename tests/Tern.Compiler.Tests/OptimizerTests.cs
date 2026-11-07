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
    [InlineData("asm {\n lda #7\n sta $C100\n }")]
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
        Assert.Equal(between.StartsWith("asm") ? 7 : 9, r.U8("v"));
    }

    [Theory]
    [InlineData("nop", true)]
    [InlineData("cli", true)]
    [InlineData("sei", true)]
    [InlineData("ldy $D012", true)]     // changes Y and the flags, not A or memory
    [InlineData("lda #1", false)]       // changes A
    [InlineData("sta $D020", false)]    // a write to a fixed address may change any variable
    [InlineData("inc $D020", false)]
    public void Assembly_forgets_only_what_it_changes(string instruction, bool kept)
    {
        var r = Run.Program($$"""
            var x: u8
            var y: u8
            fn main() {
                x = 5
                asm {
                    {{instruction}}
                }
                y = x
            }
            """);
        // A held 5 (the value just stored in x). Kept: "sta y" follows the block with no reload.
        Assert.Equal(kept, !Main(r).Contains("lda x"));
        Assert.Equal(5, r.U8("y"));
    }

    [Fact]
    public void Assembly_that_writes_a_variable_makes_the_optimizer_forget_only_that_one()
    {
        var r = Run.Program("""
            var x: u8
            var y: u8
            var z: u8
            fn main() {
                x = 5
                y = 6
                asm {
                    inc x
                }
                z = y
                y = x
            }
            """);
        Assert.Equal(6, r.U8("y"));
        Assert.Equal(6, r.U8("z"));
        // A held 6 and the block did not change it, so "sta z" needs no load; x was changed, so it is loaded again.
        Assert.Contains("inc x / sta z / lda x / sta y", Main(r));
    }

    [Fact]
    public void A_long_function_is_optimized_quickly()
    {
        // Review M7, finding 2: 4500 statements took 228 seconds when each removal was followed by
        // a new analysis of the whole function.
        var body = new System.Text.StringBuilder();
        for (int k = 0; k < 1500; k++)
            body.Append($"    x = {k & 0xFF}\n    y = x\n    z[x & 3] = y\n");
        var source = $"var x: u8\nvar y: u8\nvar z: [u8; 4]\nfn main() {{\n{body}}}\n";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var r = TernCompiler.Compile(source);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        Assert.DoesNotContain("lda x ", r.Listing);   // every y = x uses the A that was just stored
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

    [Fact]
    public void A_transfer_back_is_removed_when_the_registers_are_still_equal()
    {
        var r = Run.Program("""
            var a: [u8; 256]
            var n: u8 = 3
            fn main() {
                for m in 6..=255 step n using Y { a[m] = m }
            }
            """);
        // The step is copied once before the loop (a global may change); inside, A already holds Y after the sta.
        Assert.Contains("tya / sta a,y / clc / adc main._t0 / tay / bcc ", Main(r));
        Assert.Equal(6, r.U8("a", 6));
        Assert.Equal(252, r.U8("a", 252));
        Assert.Equal(0, r.U8("a", 7));
    }

    [Fact]
    public void Equal_registers_are_forgotten_when_one_is_loaded_again()
    {
        // txa / tay makes A, X and Y equal; the second loop's txa copies a different X, so tay must stay.
        var r = Run.Program("""
            var src: [u8; 4] = [5, 6, 7, 8]
            var dst: [u8; 4]
            fn main() {
                var s: ptr u8 = addr(src)
                var d: ptr u8 = addr(dst)
                for i in 0..4 using X { d[i] = s[i] }
                for i in 0..4 using X { s[i] += 1 }
            }
            """);
        Assert.Equal([5, 6, 7, 8], r.Ram.Bytes.AsSpan(r.Address("dst"), 4).ToArray());
        Assert.Equal([6, 7, 8, 9], r.Ram.Bytes.AsSpan(r.Address("src"), 4).ToArray());
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x7F)]
    [InlineData(0x80)]
    [InlineData(0xFF)]
    [InlineData(0xA5)]
    public void A_bit_7_test_followed_by_shifts_in_both_arms_is_one_shift_and_a_carry_test(int start)
    {
        var r = Run.Program($$"""
            var set_arm: u8 = {{start}}
            var clear_arm: u8 = {{start}}
            var plain: u8 = {{start}}
            fn main() {
                for _ in 0..8 {
                    if set_arm & $80 != 0 { set_arm = (set_arm << 1) ^ $07 } else { set_arm = set_arm << 1 }
                    if clear_arm & $80 == 0 { clear_arm = clear_arm << 1 } else { clear_arm = (clear_arm << 1) ^ $1D }
                    if plain & $80 != 0 { plain = plain << 1 }
                }
            }
            """);
        byte a = (byte)start, b = (byte)start, c = (byte)start;
        for (int k = 0; k < 8; k++)
        {
            a = (byte)((a & 0x80) != 0 ? (a << 1) ^ 0x07 : a << 1);
            b = (byte)((b & 0x80) == 0 ? b << 1 : (b << 1) ^ 0x1D);
            if ((c & 0x80) != 0) c = (byte)(c << 1);
        }
        Assert.Equal(a, r.U8("set_arm"));
        Assert.Equal(b, r.U8("clear_arm"));
        Assert.Equal(c, r.U8("plain"));
        // Two folded tests: no "and #$80" is left except the one-armed test, which has no second shift to share.
        Assert.Equal(1, Main(r).Split("and #$80").Length - 1);
        Assert.Contains("asl a / bcc ", Main(r));
        Assert.Contains("asl a / bcs ", Main(r));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_load_is_removed_before_a_jump_to_code_that_sets_the_flags(int c)
    {
        // N and Z are not read on any path after the second "ldx sp", so it can go although a jmp follows.
        var r = Run.Program($$"""
            var stack: [u8; 16] = [0, 4, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]
            var sp: u8 = 2
            var c: u8 = {{c}}
            fn main() {
                if c != 0 { stack[sp - 1] = stack[sp - 1] + stack[sp] } else { sp = 7 }
            }
            """);
        Assert.Equal(c == 1 ? 9 : 4, r.U8("stack", 1));
        Assert.Equal(c == 1 ? 2 : 7, r.U8("sp"));
        Assert.Equal(1, Main(r).Split("ldx sp").Length - 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(9)]
    public void A_jump_to_a_jump_goes_to_the_final_target(int op)
    {
        // The if at the end of the first arm ends in "jmp endif", where endif: is only "jmp mend".
        var r = Run.Program($$"""
            var out: u8
            var n: u8 = {{op}}
            var f: u8 = 1
            fn main() {
                match n {
                    0 => { if f != 0 { out = 10 } else { out = 11 } }
                    1 => out = 20
                    _ => out = 30
                }
            }
            """);
        Assert.Equal(op == 0 ? 10 : op == 1 ? 20 : 30, r.U8("out"));
        var code = Main(r);
        Assert.DoesNotContain("jmp main.endif", code);
    }

    [Fact]
    public void The_bit_7_fold_needs_a_plain_load_not_an_indexed_one()
    {
        // A holds a[0]; the arms load a[i], which is a different byte.
        var r = Run.Program("""
            var a: [u8; 4]
            var b: [u8; 4]
            fn main() {
                a[0] = $80
                a[1] = 1
                a[2] = 2
                a[3] = 3
                for i in 0..4 {
                    var t: u8 = a[0]
                    if t & $80 != 0 { b[i] = a[i] << 1 } else { b[i] = a[i] << 1 }
                }
            }
            """);
        Assert.Equal([0, 2, 4, 6], r.Ram.Bytes.AsSpan(r.Address("b"), 4).ToArray());
    }

    [Fact]
    public void Assembly_that_patches_its_own_code_makes_the_optimizer_forget()
    {
        var r = Run.Program("""
            var out: u8
            fn main() {
                var v: u8 = 7
                asm {
                    lda v
                    sta .smc+1
                .smc:
                    lda #0
                }
                out = 0
            }
            """);
        Assert.Equal(0, r.U8("out"));
    }

    [Fact]
    public void An_index_register_is_loaded_once_for_several_arrays_with_the_same_index()
    {
        var r = Run.Program("""
            var a: [u8; 4] = [1, 2, 3, 4]
            var c: [u8; 4] = [10, 20, 30, 40]
            var b: [u8; 4]
            fn main() {
                var i: u8 = 2
                b[i] = a[i] + c[i]
            }
            """);
        Assert.Equal(33, r.U8("b", 2));
        Assert.Equal(1, Main(r).Split(" / ").Count(s => s.StartsWith("ldx") || s == "tax"));   // X is loaded once
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void A_while_test_after_a_body_ending_in_asm_reads_that_blocks_flag(int start)
    {
        var r = Run.Program($$"""
            var n: u8 = {{start}}
            var count: u8
            inline asm fn is_zero() -> bool using zero {}
            fn main() {
                asm { lda n }
                while !is_zero() {
                    count += 1
                    asm {
                        dec n
                        lda n
                    }
                }
            }
            """);
        Assert.Equal(start, r.U8("count"));
    }

    [Fact]
    public void Nothing_but_the_branch_lies_between_a_bodys_last_asm_and_the_while_test()
    {
        var r = Run.Program("""
            var n: u8 = 3
            var count: u8
            inline asm fn is_zero() -> bool using zero {}
            fn main() {
                asm { lda n }
                while !is_zero() {
                    count += 1
                    asm {
                        dec n
                        lda n
                    }
                }
            }
            """);
        var code = r.Result.Listing.Split((char)10).Where(l => l.StartsWith('$')).ToList();
        int last = code.FindLastIndex(c => c.Contains(" lda n "));
        Assert.Contains(" bne ", code[last + 1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void Code_patched_from_another_function_is_not_trusted(int v)
    {
        var r = Run.Program($$"""
            var out: u8
            fn g() {
                asm { lda #0 }
                out = 0
            }
            fn main() {
                asm {
                    lda #{{v}}
                    sta g+1
                }
                g()
            }
            """);
        Assert.Equal(0, r.U8("out"));
    }

    // M18 F19: lda x / eor y with y already in A is eor x.

    [Theory]
    [InlineData("^", "eor")]
    [InlineData("&", "and")]
    [InlineData("|", "ora")]
    [InlineData("+", "adc")]
    public void A_commutative_operand_already_in_A_is_used_as_is(string op, string mnemonic)
    {
        foreach (int start in new[] { 0, 1, 127, 128, 255 })
        {
            var r = Run.Program($$"""
                var crc: u8 = {{start}}
                var b: u8
                fn main() {
                    for i in 0..=255 {
                        b = b + 37
                        crc = crc {{op}} b
                    }
                }
                """);
            int crc = start, b = 0;
            for (int i = 0; i < 256; i++)
            {
                b = (byte)(b + 37);
                crc = (byte)(op switch { "^" => crc ^ b, "&" => crc & b, "|" => crc | b, _ => crc + b });
            }
            Assert.Equal(crc, r.U8("crc"));
            Assert.Contains($"sta b / {(op == "+" ? "clc / " : "")}{mnemonic} crc / sta crc", Main(r));
        }
    }

    [Fact]
    public void A_subtraction_keeps_its_load()
    {
        var r = Run.Program("""
            var t: u8 = 100
            var b: u8
            fn main() {
                b = 30
                t = t - b
            }
            """);
        Assert.Equal(70, r.U8("t"));
        Assert.Equal("lda #$1E / sta b / lda t / sec / sbc b / sta t / rts", Main(r));
    }

    [Fact]
    public void An_operand_in_A_on_only_one_path_keeps_the_load()
    {
        var r = Run.Program("""
            var t: u8 = 3
            var b: u8 = 5
            var c: u8 = 1
            fn main() {
                if c == 1 { b = 9 }
                t = t ^ b
            }
            """);
        Assert.Equal(3 ^ 9, r.U8("t"));
        Assert.Contains("lda t / eor b", Main(r));
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
    public void A_parameter_in_y_is_stored_when_another_pointer_access_needs_y()
    {
        // Review M7, finding 1: p[3] and w[2] load Y themselves.
        var r = Run.Program("""
            var buf: [u8; 8]
            var words: [u8; 8]
            var p: ptr u8
            var w: ptr u16
            var res: u16
            fn f(n: u8 using Y) { p[3] = n }
            fn g(n: u8 using Y) { res = w[2] + n as u16 }
            fn h(n: u8 using Y) { p[n] = p[n] + 1 }
            fn main() {
                p = addr(buf)
                w = addr(words) as ptr u16
                words[4] = $34
                words[5] = $12
                f(2)
                g(5)
                buf[6] = 9
                h(6)
            }
            """);
        Assert.Equal(2, r.U8("buf", 3));
        Assert.Equal(0x1234 + 5, r.U16("res"));
        Assert.Equal(10, r.U8("buf", 6));
        Assert.Contains("sty f.n", r.Result.Listing);
        Assert.Contains("sty g.n", r.Result.Listing);
        Assert.DoesNotContain("sty h.n", r.Result.Listing);   // only p[n]: stays in Y
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

/// <summary>The counter of an innermost loop goes in X or Y when the body allows it (roadmap M7).</summary>
public class LoopRegisterTests
{
    [Fact]
    public void An_innermost_loop_counts_in_x()
    {
        var r = Run.Program("""
            var a: [u8; 8]
            fn main() {
                for i in 0..8 { a[i] = i + 1 }
            }
            """);
        Assert.Contains("inx", r.Result.Listing);
        Assert.DoesNotContain("main.i", r.Result.Symbols.Keys);
        for (int i = 0; i < 8; i++) Assert.Equal(i + 1, r.U8("a", i));
    }

    [Fact]
    public void A_loop_that_only_indexes_a_pointer_by_its_counter_counts_in_y()
    {
        var r = Run.Program("""
            var p: ptr u8
            var q: ptr u8
            fn main() {
                p = $C000
                q = $C100
                for i in 0..=255 { q[i] = p[i] }
            }
            """, setup: ram => { for (int i = 0; i < 256; i++) ram.Bytes[0xC000 + i] = (byte)(i * 7); });
        Assert.Contains("iny", r.Result.Listing);
        for (int i = 0; i < 256; i++) Assert.Equal((byte)(i * 7), r.Ram.Bytes[0xC100 + i]);
    }

    [Fact]
    public void A_loop_with_other_pointer_accesses_counts_in_x()
    {
        // e.name[i] computes Y = i + offset, so the counter cannot be in Y.
        var r = Run.Program("""
            struct E { hp: u8, name: [u8; 4] }
            var a: E
            fn main() {
                var e: ptr E = addr(a)
                for i in 0..4 { e.name[i] = i + 65 }
            }
            """);
        Assert.Contains("inx", r.Result.Listing);
        Assert.Equal("ABCD", System.Text.Encoding.ASCII.GetString(r.Ram.Bytes, r.Address("a") + 1, 4));
    }

    [Fact]
    public void Only_the_inner_loop_of_two_gets_a_register()
    {
        var r = Run.Program("""
            var a: [u8; 16]
            fn main() {
                for i in 0..4 {
                    for j in 0..4 { a[(i << 2) + j] = i ^ j }
                }
            }
            """);
        Assert.Contains("main.i", r.Result.Symbols.Keys);
        Assert.DoesNotContain("main.j", r.Result.Symbols.Keys);
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++) Assert.Equal(i ^ j, r.U8("a", 4 * i + j));
    }

    [Theory]
    [InlineData("for i in 0..4 { g()\n a[i] = 1 }")]          // a call
    [InlineData("for i in 0..4 { asm {\n nop\n }\n a[i] = 1 }")] // an asm block
    [InlineData("for i in 0..8 step 2 { a[i] = 1 }")]         // a step other than 1
    [InlineData("for i in 0..4 { var p: ptr u8 = addr(i)\n a[i] = p[0] }")] // address taken
    public void A_counter_stays_in_memory_when_the_body_needs_it(string loop)
    {
        var r = Run.Program($$"""
            var a: [u8; 8]
            fn g() { }
            fn main() {
                {{loop}}
            }
            """);
        Assert.Contains("main.i", r.Result.Symbols.Keys);
    }

    [Fact]
    public void A_search_loop_returns_its_counter()
    {
        var r = Run.Program("""
            var a: [u8; 16]
            var at: u8
            fn find(v: u8) -> u8 {
                for i in 0..16 {
                    if a[i] == v { return i }
                }
                return 255
            }
            fn main() {
                a[11] = 5
                at = find(5)
            }
            """);
        Assert.Equal(11, r.U8("at"));
        Assert.DoesNotContain("find.i", r.Result.Symbols.Keys);
    }
}
