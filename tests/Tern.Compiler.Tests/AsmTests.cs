using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

public class AsmTests
{
    private static byte[] Bytes(Run r, string name, int n) => r.Ram.Bytes.AsSpan(r.Address(name), n).ToArray();

    /// <summary>The listing lines of one function, from its label to the next label without a dot.</summary>
    private static string ListingOf(Run r, string function)
    {
        var lines = r.Result.Listing.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        int start = lines.IndexOf($"{function}:");
        Assert.True(start >= 0, $"No function {function} in the listing");
        int end = lines.FindIndex(start + 1, l => l.EndsWith(':') && !l.Contains('.') && !l.StartsWith(' '));
        return string.Join("\n", lines[start..(end < 0 ? lines.Count : end)]);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(245)]
    [InlineData(246)]
    [InlineData(0xFFF5)]
    [InlineData(0xFFF6)]
    public void Spec_example_adds_with_carry(int score)
    {
        var r = Run.Program($$"""
            var score: u16 = {{score}}
            fn main() {
                asm {
                    lda score
                    clc
                    adc #10
                    sta score
                    bcc .done
                    inc score+1
                .done:
                }
            }
            """);
        Assert.Equal((score + 10) & 0xFFFF, r.U16("score"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(8)]
    public void Spec_asm_fn_fills_with_register_parameters(int count)
    {
        var r = Run.Program($$"""
            var buffer: [u8; 8]
            asm fn fill(value: u8 using A, count: u8 using X) {
            .loop:
                sta buffer-1,x
                dex
                bne .loop
            }
            fn main() { fill($AA, {{count}}) }
            """);
        var expected = Enumerable.Range(0, 8).Select(i => (byte)(i < count ? 0xAA : 0)).ToArray();
        Assert.Equal(expected, Bytes(r, "buffer", 8));
    }

    [Fact]
    public void Asm_fn_register_parameters_have_no_memory_and_are_not_stored()
    {
        var r = Run.Program("""
            var out: u8
            asm fn put(v: u8 using A) {
                sta out
            }
            fn main() { put(7) }
            """);
        Assert.Equal(7, r.U8("out"));
        Assert.False(r.Result.Symbols.ContainsKey("put.v"));
        Assert.Contains("lda #$07", r.Result.Listing);
        var put = ListingOf(r, "put");
        Assert.Contains("sta out", put);
        Assert.DoesNotContain("put.v", put);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(200, 100)]
    [InlineData(255, 255)]
    public void Asm_fn_with_memory_parameters_and_return_values(int a, int b)
    {
        var r = Run.Program($$"""
            var sum8: u8
            var sum16: u16
            var carry: bool
            // Memory parameters are read by name; the result is returned in A, or in A and X.
            asm fn add8(x: u8, y: u8) -> u8 {
                lda x
                clc
                adc y
            }
            asm fn add16(x: u8, y: u8) -> u16 {
                lda x
                clc
                adc y
                ldx #0
                bcc .done
                inx
            .done:
            }
            asm fn overflows(x: u8, y: u8) -> bool {
                lda x
                clc
                adc y
            }
            fn main() {
                sum8 = add8({{a}}, {{b}})
                sum16 = add16({{a}}, {{b}})
                carry = false
                if overflows({{a}}, {{b}}) { carry = true }
            }
            """);
        Assert.Equal((a + b) & 0xFF, r.U8("sum8"));
        Assert.Equal(a + b, r.U16("sum16"));
        Assert.Equal(a + b > 255 ? 1 : 0, r.U8("carry"));
    }

    [Fact]
    public void Rts_is_added_only_when_the_block_does_not_end_with_rts_rti_or_jmp()
    {
        var r = Run.Program("""
            var n: u8
            asm fn ends_with_rts() {
                inc n
                rts
            }
            asm fn ends_with_label() {
                inc n
                bne .done
            .done:
            }
            asm fn ends_with_jmp() {
                inc n
                jmp ends_with_rts
            }
            fn main() {
                ends_with_rts()
                ends_with_label()
                ends_with_jmp()
            }
            """);
        Assert.Equal(4, r.U8("n"));
        Assert.Equal(1, Count(ListingOf(r, "ends_with_rts"), " rts "));
        Assert.Contains("ends_with_label.done", ListingOf(r, "ends_with_label"));
        Assert.Equal(1, Count(ListingOf(r, "ends_with_label"), " rts "));
        Assert.Equal(0, Count(ListingOf(r, "ends_with_jmp"), " rts "));
    }

    private static int Count(string text, string part) => text.Split(part).Length - 1;

    [Fact]
    public void Constants_chars_and_addresses_as_operands()
    {
        var r = Run.Program("""
            const SCREEN = $0400
            const SIZE = 40 * 25
            var tbl: [u8; 4]
            var w: u16
            fn main() {
                asm {
                    lda #'A'
                    sta tbl
                    lda #screen'A'
                    sta tbl+1
                    lda #SIZE.hi
                    sta tbl+2
                    lda #len(tbl) - 1
                    sta tbl+3
                    lda #$12
                    sta SCREEN+40
                    lda #-1
                    sta SCREEN+41
                    lda #(SCREEN + 2 * 40).lo
                    sta w
                    lda #(SCREEN + 2 * 40).hi
                    sta w+1
                }
            }
            """);
        Assert.Equal([0x41, 0x01, 1000 >> 8, 3], Bytes(r, "tbl", 4));
        Assert.Equal(0x12, r.Ram.Bytes[0x0428]);
        Assert.Equal(0xFF, r.Ram.Bytes[0x0429]);
        Assert.Equal(0x0400 + 80, r.U16("w"));
    }

    [Fact]
    public void Indirect_indexed_through_a_tern_pointer()
    {
        // p is only used in assembly, but (p),y forces it into zero page.
        var r = Run.Program("""
            data msg = screen"HELLO"
            var p: ptr u8
            var dst: [u8; 5]
            fn main() {
                asm {
                    lda #msg.lo
                    sta p
                    lda #msg.hi
                    sta p+1
                    ldy #4
                .copy:
                    lda (p),y
                    sta dst,y
                    dey
                    bpl .copy
                }
            }
            """);
        Assert.Equal([8, 5, 12, 12, 15], Bytes(r, "dst", 5));
        Assert.True(r.Address("p") < 0x100);
        Assert.Contains("B1 ", r.Result.Listing);
        Assert.Contains("lda (p),y", r.Result.Listing);
    }

    [Fact]
    public void Address_bytes_with_an_offset()
    {
        var r = Run.Program("""
            var buf: [u8; 4]
            var lo: u8
            var hi: u8
            var target: u16
            fn main() {
                asm {
                    lda #(buf+3).lo
                    sta lo
                    lda #(buf+3).hi
                    sta hi
                    lda #main.lo
                    sta target
                    lda #main.hi
                    sta target+1
                }
            }
            """);
        Assert.Equal(r.Address("buf") + 3, r.U8("lo") | (r.U8("hi") << 8));
        Assert.Equal(r.Address("main"), r.U16("target"));
    }

    [Fact]
    public void Other_addressing_modes()
    {
        var r = Run.Program("""
            var vec: u16
            var p: ptr u8 = $3000
            var zx: [u8; 3] @ zp
            var r: [u8; 6]
            asm fn target() {
                lda #$5A
                sta r+5
            }
            fn main() {
                asm {
                    lda #3
                    asl a
                    asl
                    sta r            // 12
                    lda #$81
                    sta $3000
                    ldx #0
                    lda (p,x)
                    sta r+3          // $81
                    ldy #2
                    ldx #9
                    stx zx,y         // stx has only a zero-page form for ,y
                    ldx #1
                    lda #$33
                    sta r,x          // r+1 = $33
                    ldy #2
                    sta r,y          // r+2 = $33
                    lda #target.lo
                    sta vec
                    lda #target.hi
                    sta vec+1
                    jsr .indirect
                    jmp .end
                .indirect:
                    jmp (vec)
                .end:
                }
            }
            """);
        Assert.Equal([12, 0x33, 0x33, 0x81, 0, 0x5A], Bytes(r, "r", 6));
        Assert.True(r.Address("zx") < 0x100);
        Assert.Equal([0, 0, 9], Bytes(r, "zx", 3));
        Assert.Contains("6C ", r.Result.Listing);    // jmp (vec)
        Assert.Contains("A1 ", r.Result.Listing);    // lda (p,x)
        Assert.Contains("96 ", r.Result.Listing);    // stx zx,y
    }

    [Fact]
    public void Fixed_address_variables_and_volatile_registers()
    {
        var r = Run.Program("""
            var border: u8 @ $D020
            fn main() {
                asm {
                    lda #2
                    sta border
                }
            }
            """);
        Assert.Equal(2, r.Ram.Bytes[0xD020]);
        Assert.Contains("8D 20 D0  sta border", r.Result.Listing);
    }

    [Fact]
    public void Asm_in_an_x_loop_reads_the_loop_variable()
    {
        var r = Run.Program("""
            var squares: [u8; 10]
            fn main() {
                for i in 0..10 using X {
                    asm {
                        txa
                        sta squares,x
                    }
                    squares[i] += i
                }
            }
            """);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => (byte)(2 * i)).ToArray(), Bytes(r, "squares", 10));
    }

    [Fact]
    public void Asm_jsr_to_a_tern_function_keeps_it_in_the_program()
    {
        // twice() is only called from assembly; the call graph must still see it, so its frame
        // does not overlap main's local.
        var r = Run.Program("""
            var out: u8
            fn twice(v: u8 using A) -> u8 {
                var t: u8 = v
                return t + t
            }
            fn main() {
                var keep: u8 = 9
                asm {
                    lda #21
                    jsr twice
                    sta out
                }
                out += keep
            }
            """);
        Assert.Equal(42 + 9, r.U8("out"));
        Assert.NotEqual(r.Address("main.keep"), r.Address("twice.t"));
        Assert.NotEqual(r.Address("main.keep"), r.Address("twice.v"));
    }

    [Fact]
    public void A_bound_changed_by_asm_is_copied_before_the_loop()
    {
        var r = Run.Program("""
            var count: u8
            fn main() {
                var n: u8 = 3
                for i in 0..n {
                    asm {
                        inc n
                    }
                    count += 1
                }
            }
            """);
        Assert.Equal(3, r.U8("count"));
    }

    [Fact]
    public void Labels_are_local_to_each_block()
    {
        var r = Run.Program("""
            var a: u8
            var b: u8
            fn main() {
                asm {
                    ldx #3
                .loop:
                    inc a
                    dex
                    bne .loop
                }
                asm {
                    ldx #5
                .loop:
                    inc b
                    dex
                    bne .loop
                }
            }
            """);
        Assert.Equal(3, r.U8("a"));
        Assert.Equal(5, r.U8("b"));
    }

    [Fact]
    public void Listing_shows_what_a_block_reads_and_changes()
    {
        var r = Run.Program("""
            var score: u16
            var other: u8
            fn main() {
                asm {
                    lda score
                    clc
                    adc #10
                    sta score
                    ldx other
                    inx
                    stx other
                }
            }
            """);
        Assert.Contains("; asm: reads A X C D, changes A X C Z N V, reads other score, writes other score", r.Result.Listing);
    }

    [Fact]
    public void Instructions_are_kept_as_written()
    {
        var r = Run.Program("""
            var v: u8 @ zp
            fn main() {
                asm {
                    lda v
                    lda v
                    nop
                    sta v
                    lda #0
                    cmp #0
                }
            }
            """);
        var lines = ListingOf(r, "main").Split('\n').Where(l => l.StartsWith('$')).Select(l => l[16..].Split(';')[0].Trim()).ToArray();
        Assert.Equal(["lda v", "lda v", "nop", "sta v", "lda #$00", "cmp #$00", "rts"], lines);
    }

    [Fact]
    public void Mnemonics_and_index_registers_ignore_case()
    {
        var r = Run.Program("""
            var t: [u8; 2]
            fn main() {
                asm {
                    LDX #1
                    LDA #7
                    STA t,X
                }
            }
            """);
        Assert.Equal([0, 7], Bytes(r, "t", 2));
    }

    [Fact]
    public void Out_of_range_branch_is_an_error()
    {
        var nops = string.Concat(Enumerable.Repeat("        nop\n", 130));
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile($"fn main() {{\n    asm {{\n        beq .far\n{nops}    .far:\n    }}\n}}"));
        Assert.Contains("Branch to main.far0 is out of range (130 bytes", e.Description);
        Assert.Contains("Use jmp", e.Description);
        Assert.Equal(3, e.Pos.Line);
    }

    [Theory]
    [InlineData("fn main() { asm {\n lda #1 sta 2\n } }", "Expected the end of the line after the instruction")]
    [InlineData("fn main() { asm {\n lda #1; sta 2\n } }", "';' is not used in assembly")]
    [InlineData("fn main() { asm {\n foo #1\n } }", "Unknown instruction 'foo'")]
    [InlineData("fn main() { asm {\n loop:\n } }", "Labels in assembly start with '.'. Write .loop:")]
    [InlineData("fn main() { asm {\n .loop\n } }", "Expected ':' after the label")]
    [InlineData("asm {\n nop\n}\nfn main() { }", "An asm block must be inside a function")]
    [InlineData("fn main() { asm fn f() { } }", "'asm fn' must be declared outside functions")]
    [InlineData("fn main() { asm {\n nop\n }", "Block is not closed")]
    [InlineData("data msg = \"A\"\nfn main() { asm {\n lda #<msg\n } }", "An immediate operand is one byte, and an address has two. Write #name.lo or #name.hi")]
    [InlineData("data msg = \"A\"\nfn main() { asm {\n lda #msg\n } }", "An address has two bytes. Write #name.lo or #name.hi")]
    [InlineData("fn main() { asm {\n lda #300\n } }", "#300 does not fit in a byte")]
    [InlineData("fn main() { asm {\n lda #-129\n } }", "#-129 does not fit in a byte")]
    [InlineData("fn main() { asm {\n bne .nowhere\n } }", "Label '.nowhere' is not defined in this asm block")]
    [InlineData("fn main() { asm {\n .a:\n .a:\n } }", "Label '.a' is defined twice")]
    [InlineData("fn main() { asm {\n inx 5\n } }", "'inx' takes no operand")]
    [InlineData("fn main() { asm {\n lda\n } }", "'lda' needs an operand")]
    [InlineData("fn main() { asm {\n sta #1\n } }", "'sta' has no immediate mode")]
    [InlineData("var p: ptr u8\nfn main() { asm {\n lda (p)\n } }", "'lda' has no (address) mode. Write (pointer),y to read through a pointer")]
    [InlineData("fn main() { asm {\n lda ($1234),y\n } }", "'lda (address),y' needs a zero-page address ($00-$FF), not $1234")]
    [InlineData("var b: u8 @ $D020\nfn main() { asm {\n lda (b),y\n } }", "needs a zero-page address, but 'b' is at $D020")]
    [InlineData("fn main() { asm {\n lda (main),y\n } }", "needs a zero-page address, not a function")]
    [InlineData("fn main() { asm {\n stx $1234,y\n } }", "'stx address,y' needs a zero-page address")]
    [InlineData("fn main() { asm {\n stx $12,x\n } }", "'stx' has no address,x mode")]
    [InlineData("fn main() { asm {\n lda $12,z\n } }", "Expected x or y after ',', found 'z'")]
    [InlineData("fn main() { asm {\n lda ($12,y)\n } }", "Expected x in (address,x), found 'y'")]
    [InlineData("fn main() { asm {\n lda ($12),x\n } }", "Expected y in (address),y, found 'x'")]
    [InlineData("fn main() { asm {\n bne 5\n } }", "'bne' needs a label, e.g. bne .loop")]
    [InlineData("var v: u8\nfn main() { asm {\n beq v\n } }", "'beq' needs a label")]
    [InlineData("var v: u16\nfn main() { asm {\n lda v.lo\n } }", ".lo and .hi give a byte value, not an address")]
    [InlineData("var v: u8\nvar w: u8\nfn main() { asm {\n lda v+w\n } }", "an address can only have a constant added or subtracted")]
    [InlineData("var v: u8\nfn main() { asm {\n lda v*2\n } }", "an address can only have a constant added or subtracted")]
    [InlineData("var v: u8\nfn main() { asm {\n lda #v.lo.hi\n } }", "of a value that is already one byte")]
    [InlineData("var v: u8\nfn main() { asm {\n lda v.x\n } }", "Unknown member '.x'; assembly only has .lo and .hi")]
    [InlineData("struct S { x: u8 }\nvar p: ptr S\nfn main() { asm {\n lda p.x\n } }", "In assembly, a field must belong to a struct variable")]
    [InlineData("struct S { x: u8 }\nvar s: S\nfn main() { asm {\n lda s.y\n } }", "Struct 'S' has no field 'y'")]
    [InlineData("fn main() { asm {\n lda nothing\n } }", "'nothing' is not declared")]
    [InlineData("var t: [u8; 2]\nfn main() { asm {\n lda t[1]\n } }", "An assembly operand must be a constant, or the name of a variable, function or .label")]
    [InlineData("fn main() { asm {\n lda 70000\n } }", "Address 70000 is outside memory")]
    [InlineData("data B: [u8; 1] = [0]\nfn main() { asm {\n lda B + 65536\n } }", "Offset 65536 from an address is outside memory. Use a smaller offset")]
    // The address itself, once the program is placed (third review, suggestion 3).
    [InlineData("data B: [u8; 1] = [0]\nfn main() { asm {\n lda B + 65000\n } }", "outside memory. Use a smaller offset")]
    [InlineData("data B: [u8; 1] = [0]\nfn main() { asm {\n lda B + 65535\n } }", "outside memory. Use a smaller offset")]
    [InlineData("var v: u8 @ $02\nfn main() { asm {\n lda v - 5\n } }", "The address v-5 is -$3, outside memory. Use a smaller offset")]
    [InlineData("var v: u8 @ $C000\nfn main() { asm {\n sta v + $4000, x\n } }", "The address v+16384 is $10000, outside memory")]
    [InlineData("fn f() { }\nfn main() { asm {\n jmp f + 65000\n } }", "outside memory. Use a smaller offset")]
    [InlineData("data B: [u8; 1] = [0]\nfn main() { asm {\n lda B + 4294967296\n } }", "Offset 4294967296 from an address is outside memory")]
    [InlineData("var v: u8\nfn main() { asm {\n lda v - 65536\n } }", "Offset -65536 from an address is outside memory")]
    [InlineData("var v: u16\nfn main() { asm {\n lda #(v + 65536).hi\n } }", "Offset 65536 from an address is outside memory")]
    [InlineData("fn main() { asm {\n lda 1/0\n } }", "Division by zero")]
    [InlineData("fn main() { asm {\n lda #1 == 1\n } }", "Assembly operands can only use + - * / % & | ^ << >>")]
    [InlineData("data d: [u8; 2] = [1, 2]\nfn main() { asm {\n inc d\n } }", "'d' is data, which is read-only")]
    [InlineData("fn main() { for i in 0..3 { asm {\n inc i\n } } }", "Loop variable 'i' cannot be changed inside the loop")]
    [InlineData("fn main() { for i in 0..3 using X { asm {\n stx i\n } } }", "'i' lives in register X; use the register directly")]
    [InlineData("asm fn f(v: u8 using A) {\n sta v\n}\nfn main() { f(1) }", "'v' is passed in register A; use the register directly")]
    [InlineData("fn main() { for i in 0..3 using X { asm {\n ldx #0\n } } }", "This changes X, which holds the loop variable")]
    [InlineData("fn main() { for i in 0..3 using Y { asm {\n iny\n } } }", "This changes Y, which holds the loop variable")]
    [InlineData("fn main() { for i in 0..3 using X { asm {\n jsr $FFD2\n } } }", "'jsr' calls a subroutine, which can change X")]
    [InlineData("asm fn f() {\n jsr f\n}\nfn main() { f() }", "Recursion is not allowed: f -> f")]
    [InlineData("fn main() { asm {\n jmp (.x)\n .x:\n lda (.x),y\n } }", "needs a zero-page address, not a label")]
    [InlineData("var score: u8\nfn main() { asm {\n lda -score\n } }", "Only numbers can be negated or inverted")]
    [InlineData("var v: u16 @ $10FF\nfn main() { asm {\n jmp (v)\n } }", "jmp (...) at $10FF reads its high byte from $1000 on the 6502")]
    [InlineData("var t: [u8; 4] @ $10FD\nfn main() { asm {\n jmp (t+2)\n } }", "jmp (...) at $10FF reads its high byte from $1000")]
    [InlineData("fn f(v: u8) { }\nfn main() { asm {\n jsr f\n } }", "'f' takes 'v' in memory, which assembly cannot set. Pass it in a register (v: u8 using A), or call f from Tern")]
    [InlineData("fn f(a: u8 using A, v: u16) { }\nfn main() { asm {\n jmp f\n } }", "'f' takes 'v' in memory")]
    public void Reports_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
    }

    [Fact]
    public void A_byte_of_an_address_past_memory_is_allowed()
    {
        // Only an address operand is checked; .lo / .hi take a byte of the sum (third review, suggestion 3).
        var r = Run.Program("var v: u16 @ $C000\nvar a: u8\nvar b: u8\nfn main() { asm {\n lda #(v + $4005).lo\n sta a\n lda #(v + $4005).hi\n sta b\n } }");
        Assert.Equal(0x05, r.U8("a"));
        Assert.Equal(0x00, r.U8("b"));
    }

    [Fact]
    public void Address_of_a_function_with_memory_parameters_is_allowed()
    {
        var r = Run.Program("""
            var lo: u8
            fn f(v: u8) { }
            fn main() { asm {
                lda #f.lo
                sta lo
            } }
            """);
        Assert.Equal(r.Address("f") & 0xFF, r.U8("lo"));
    }

    [Fact]
    public void Jump_vector_at_a_fixed_address_away_from_a_page_end_is_kept()
    {
        var r = TernCompiler.Compile("var v: u16 @ $10FE\nfn main() { asm {\n jmp (v)\n } }");
        Assert.Contains("6C FE 10", r.Listing);
    }

    [Fact]
    public void Errors_point_at_the_instruction()
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("fn main() {\n    for i in 0..3 using X {\n        asm {\n            nop\n            dex\n        }\n    }\n}"));
        Assert.Equal(new SourcePos(5, 13), e.Pos);
    }

    [Fact]
    public void Kernal_example_prints_through_chrout()
    {
        var path = FindExample("kernal.tern");
        var r = Run.Program(File.ReadAllText(path), sourcePath: path, setup: ram =>
        {
            // The emulator has no ROM. CHROUT appends A to a buffer at $C000 (count at $C0FE);
            // PLOT returns row 0 and the column of the last character printed.
            ram.Load(0xFFD2, [
                0x8E, 0xFF, 0xC0,   // stx $C0FF
                0xAE, 0xFE, 0xC0,   // ldx $C0FE
                0x9D, 0x00, 0xC0,   // sta $C000,x
                0xEE, 0xFE, 0xC0,   // inc $C0FE
                0xAE, 0xFF, 0xC0,   // ldx $C0FF
                0x60,               // rts
            ]);
            ram.Load(0xFFF0, [
                0xA2, 0x00,         // ldx #0
                0xAC, 0xFE, 0xC0,   // ldy $C0FE
                0x88,               // dey
                0x60,               // rts
            ]);
        });
        const string title = "HELLO FROM TERN, VIA CHROUT.";
        var text = title + $"\nTHE CURSOR WAS AT ROW $00, COLUMN ${title.Length:X2}\n";
        var expected = new[] { (byte)0x93 }.Concat(text.Select(c => (byte)CharEncoding.Petscii.Encode(c)!.Value)).ToArray();
        Assert.Equal(expected.Length, r.Ram.Bytes[0xC0FE]);
        Assert.Equal(expected, r.Ram.Bytes.AsSpan(0xC000, expected.Length).ToArray());
        // chrout is called directly, from a loop that keeps its counter in X.
        Assert.Equal(0xFFD2, r.Address("chrout"));
        Assert.Equal(
            ["ldx #$00", "cpx print.n", "bcs print.fend0", "txa", "tay", "lda (print.s),y", "jsr $FFD2", "inx", "cpx print.n", "bne print.for2", "rts"],
            Instructions(ListingOf(r, "print")));
    }

    private static string[] Instructions(string listing) =>
        listing.Split('\n').Where(l => l.StartsWith('$') && l.Length > 17 && l[16] == ' ' && char.IsLetter(l[17]))
            .Select(l => l[16..].Split(';')[0].Trim()).ToArray();

    [Fact]
    public void Kernal_example_listing_has_no_stand_in_for_chrout()
    {
        var path = FindExample("kernal.tern");
        var result = TernCompiler.Compile(File.ReadAllText(path), sourcePath: path);
        Assert.DoesNotContain("\nchrout:", result.Listing);
        Assert.Contains("20 D2 FF  jsr $FFD2", result.Listing);
    }

    /// <summary>An example program: examples/&lt;platform&gt;/name.</summary>
    internal static string FindExample(string name, string platform = "c64")
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "examples", platform, name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException($"examples/{platform}/{name} not found above {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// The NMOS 6502 reads the high byte of jmp ($xxFF) from $xx00 (the emulator does too). The
    /// compiler places a vector it allocates so that it never starts at $xxFF. Zero page is full,
    /// so v goes after the program, and the size of pad moves it across a page end.
    /// </summary>
    [Fact]
    public void Jump_vectors_never_start_at_the_end_of_a_page()
    {
        // With arrays the variables start on a page (F12); structs keep them one after another,
        // where the padding before them must be used for some size.
        bool padded = false;
        foreach (bool arrays in new[] { true, false })
        foreach (string init in new[] { "", " = 0" })
        {
            for (int n = 1; n <= 256; n++)
            {
                string types = arrays ? $"var pad: [u8; {n}]\nvar t: [u8; 3]"
                    : $"struct Pad {{ b: [u8; {n}] }}\nstruct T {{ a: u8, b: u8, c: u8 }}\nvar pad: Pad\nvar t: T";
                var r = Run.Program($$"""
                    var zp1: [u8; 142] @ zp
                    {{types}}
                    var v: u16{{init}}
                    var hit: u8
                    asm fn target() { inc hit }
                    fn main() {
                        zp1[0] = 1
                        asm {
                            lda #target.lo
                            sta v
                            sta t+1
                            lda #target.hi
                            sta v+1
                            sta t+2
                            jsr .go
                            jsr .go2
                            jmp .end
                        .go:
                            jmp (v)
                        .go2:
                            jmp (t+1)
                        .end:
                        }
                    }
                    """);
                Assert.True((r.Address("v") & 0xFF) != 0xFF, $"v at ${r.Address("v"):X4} with pad {n}");
                Assert.True(((r.Address("t") + 1) & 0xFF) != 0xFF, $"t+1 at ${r.Address("t") + 1:X4} with pad {n}");
                Assert.Equal(2, r.U8("hit"));
                padded |= !arrays && r.Result.Listing.Contains("of padding");
            }
        }
        Assert.True(padded);
    }

    [Fact]
    public void Asm_fn_can_jump_to_itself()
    {
        // jmp to the function's own label is a loop, not recursion.
        var r = Run.Program("""
            var n: u8
            asm fn count_to_ten() {
                inc n
                lda n
                cmp #10
                bcs .done
                jmp count_to_ten
            .done:
            }
            fn main() { count_to_ten() }
            """);
        Assert.Equal(10, r.U8("n"));
    }
}
