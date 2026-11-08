namespace Tern.Compiler.Tests;

/// <summary>
/// M18 part 3, F7 and F11: a one-byte variable used in a loop with no call, asm, return or inner
/// loop is held in a free index register for the loop, loaded before it and stored after it
/// (CodeGen.HoldInRegister).
/// </summary>
public class HeldVariableTests
{
    private static string Code(Run r, string function = "main") => Code(r.Result.Listing, function);

    private static string Code(string listing, string function = "main")
    {
        var start = listing.IndexOf($"\n{function}:", StringComparison.Ordinal);
        var code = new List<string>();
        foreach (var l in listing[(start + 1)..].Split('\n').Skip(1).Select(l => l.TrimEnd('\r')))
        {
            if (l.EndsWith(':') && !l.StartsWith(' ') && !l.StartsWith('$') && !l.StartsWith(';') && !l.StartsWith($"{function}.")) break;
            if (l.StartsWith('$') && l.Length > 17) code.Add(l[17..].Split(';')[0].Trim());
        }
        return string.Join("|", code);
    }

    [Fact]
    public void A_stack_pointer_stays_in_x_through_a_match_jump_table()
    {
        // push, add, dup, drop, swap, dec, jump if not zero, halt: sums 10 + 9 + ... + 1 as interp does.
        byte[] program = [0, 0, 0, 10, 4, 8, 1, 4, 7, 3, 6, 4, 5, 9];
        var r = Run.Program($$"""
            data code: [u8; 14] = [{{string.Join(", ", program)}}]
            var stack: [u8; 16]
            var sp: u8 = 3
            var result: u8
            fn main() {
                var pc: u8 = 0
                var running: bool = true
                while running {
                    var op: u8 = code[pc]
                    pc += 1
                    indexed match op {
                        0 => {
                            stack[sp] = code[pc]
                            sp += 1
                            pc += 1
                        }
                        1 => {
                            sp -= 1
                            stack[sp - 1] = stack[sp - 1] + stack[sp]
                        }
                        2 => {
                            sp -= 1
                            stack[sp - 1] = stack[sp - 1] - stack[sp]
                        }
                        3 => {
                            stack[sp] = stack[sp - 1]
                            sp += 1
                        }
                        4 => {
                            var t: u8 = stack[sp - 1]
                            stack[sp - 1] = stack[sp - 2]
                            stack[sp - 2] = t
                        }
                        5 => sp -= 1
                        6 => {
                            sp -= 1
                            if stack[sp] != 0 { pc = code[pc] } else { pc += 1 }
                        }
                        7 => stack[sp - 1] -= 1
                        8 => {
                            stack[sp] = stack[sp - 2]
                            sp += 1
                        }
                        9 => running = false
                    }
                }
                result = stack[3]
            }
            """);
        Assert.Equal(55, r.U8("result"));
        Assert.Equal(4, r.U8("sp"));
        var code = Code(r);
        Assert.Contains("ldx sp|jmp main.test", code);
        Assert.Equal(1, code.Split("ldx sp").Length - 1);
        Assert.DoesNotContain("inc sp", code);
        Assert.DoesNotContain("dec sp", code);
        Assert.Contains("dec stack-1,x", code);
        Assert.Contains("tay|lda main.jumphi", code);   // the jump table takes Y
        Assert.Contains("stx sp|lda stack+3", code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(250)]
    [InlineData(255)]
    public void A_count_beside_a_counter_in_x_is_counted_in_y(int start)
    {
        var marks = new byte[256];
        for (int k = 0; k < 256; k += 3) marks[k] = 1;
        var r = Run.Program($$"""
            var marks: [u8; 256]
            var count: u8 = {{start}}
            fn main() {
                for k in 0..=255 step 3 { marks[k] = 1 }
                for i in 2..=255 {
                    if marks[i] == 0 { count += 1 }
                }
            }
            """);
        int expected = start;
        for (int i = 2; i < 256; i++) if (marks[i] == 0) expected++;
        Assert.Equal(expected & 0xFF, r.U8("count"));
        var code = Code(r);
        Assert.Contains("ldy count|", code);
        Assert.Contains("iny", code);
        Assert.Contains("sty count", code);
        Assert.DoesNotContain("inc count", code);
    }

    [Fact]
    public void A_local_index_is_stored_after_a_break()
    {
        byte[] values = [4, 8, 15, 16, 23, 42, 0, 7, 9, 1];
        var r = Run.Program($$"""
            var values: [u8; 10] = [{{string.Join(", ", values)}}]
            var out: [u8; 10]
            var at: u8
            fn main() {
                var j: u8 = 0
                while j < 10 {
                    if values[j] == 0 { break }
                    out[j] = values[j] ^ 85
                    j += 1
                }
                at = j
            }
            """);
        int j = 0;
        while (j < 10 && values[j] != 0) j++;
        Assert.Equal(j, r.U8("at"));
        for (int k = 0; k < 10; k++) Assert.Equal(k < j ? (byte)(values[k] ^ 85) : 0, r.U8("out", k));
        var code = Code(r);
        Assert.Contains("inx", code);
        // j is read only from X after the loop, so its store is dead (F3).
        Assert.DoesNotContain("main.j", code);
        Assert.Contains("txa|sta at", code);
    }

    [Fact]
    public void Plain_pointer_accesses_use_y()
    {
        var r = Run.Program("""
            var src: [u8; 8] = [1, 2, 3, 4, 5, 6, 7, 8]
            var dst: [u8; 8]
            var big: [u8; 8]
            var k: u8
            fn main() {
                var p: ptr u8 = addr(src)
                var q: ptr u8 = addr(dst)
                var m: u8 = 7
                while k < 8 {
                    q[k] = p[k] ^ 255
                    big[m] = k
                    k += 1
                }
            }
            """);
        for (int i = 0; i < 8; i++) Assert.Equal(i + 1 ^ 255, r.U8("dst", i));
        Assert.Equal(7, r.U8("big", 7));
        Assert.Equal(8, r.U8("k"));
        var code = Code(r);
        Assert.Contains("ldy k|", code);
        Assert.Contains("iny", code);
        Assert.Contains("sty k", code);
    }

    [Fact]
    public void A_handler_running_beside_the_loop_changes_nothing()
    {
        var r = Run.Program("""
            var stack: [u8; 64]
            var sp: u8
            var hn: u8
            interrupt fn h() { hn += 1 }
            fn main() {
                asm {
                    sei
                    lda #h.lo
                    sta $FFFE
                    lda #h.hi
                    sta $FFFF
                    cli
                }
                var n: u8 = 0
                while n < 200 {
                    stack[sp] = n
                    if n & 3 == 0 { sp += 1 }
                    n += 1
                }
                asm { sei }
            }
            """, irqEvery: 37);
        Assert.True(r.Interrupts > 20);
        Assert.Equal(50, r.U8("sp"));
        for (int k = 0; k < 50; k++) Assert.Equal(k * 4, r.U8("stack", k));
        Assert.DoesNotContain("inc sp", Code(r));
    }

    [Theory]
    [InlineData("fn f() {}", "f()")]                                     // a call may read sp
    [InlineData("", "asm { nop }")]                                        // assembly
    [InlineData("", "out = 3 + sp")]                                       // sp as the right operand
    [InlineData("", "if sp == 9 { return }")]                              // a return
    [InlineData("", "for k in 0..2 { out += k }")]                         // a loop inside
    public void Not_held_when(string decl, string statement)
    {
        var r = Run.Program($$"""
            var stack: [u8; 16]
            var sp: u8
            var out: u8
            {{decl}}
            fn main() {
                while sp < 10 {
                    stack[sp] = sp
                    sp += 1
                    {{statement}}
                }
            }
            """);
        Assert.Contains("inc sp", Code(r));
    }

    [Fact]
    public void Not_held_volatile_address_taken_or_in_an_endless_loop()
    {
        foreach (var (decl, extra) in new[] { ("volatile var sp: u8", ""), ("var sp: u8", "var p: ptr u8 = addr(sp)") })
        {
            var r = Run.Program($$"""
                var stack: [u8; 16]
                {{decl}}
                fn main() {
                    {{extra}}
                    while sp < 10 {
                        stack[sp] = 1
                        sp += 1
                    }
                }
                """);
            Assert.Equal(10, r.U8("sp"));
            Assert.Contains("inc sp", Code(r));
        }
        var endless = TernCompiler.Compile("""
            var stack: [u8; 16]
            var sp: u8
            fn main() {
                while true {
                    stack[sp & 15] = 1
                    sp += 1
                }
            }
            """);
        Assert.Contains("inc sp", Code(endless.Listing));
    }
}
