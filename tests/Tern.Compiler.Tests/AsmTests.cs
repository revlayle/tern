using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>Inline assembly (spec 9): asm blocks, asm fn with register and memory parameters, the rts added at the end, and the operands the assembler takes.</summary>
public class AsmTests
{
    private static byte[] Bytes(Run r, string name, int n) => r.Ram.Bytes.AsSpan(r.Address(name), n).ToArray();

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

    [Trait("Shape", "nmos6502")]
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
        Assert.Contains("lda #$07", Listing.Instructions(r));
        var put = string.Join("\n", Listing.Lines(r, "put"));
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

    [Trait("Shape", "nmos6502")]
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
        Assert.Equal(1, Listing.Count(Listing.Instructions(r, "ends_with_rts"), "rts"));
        Assert.Contains("ends_with_label.done", Listing.CodeWithLabels(r, "ends_with_label"));
        Assert.Equal(1, Listing.Count(Listing.Instructions(r, "ends_with_label"), "rts"));
        Assert.Equal(0, Listing.Count(Listing.Instructions(r, "ends_with_jmp"), "rts"));
    }

    // Review R4, suggestion 2: a body that ends in an instruction that does not fall through (the table's
    // FallsThrough) gets no rts after it. The hand list left out rti; the optimizer removed that rts anyway.
    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_function_ending_in_rti_or_an_indirect_jmp_gets_no_rts()
    {
        var result = TernCompiler.Compile("""
            var vec: u16
            fn ends_with_rti() { asm { rti } }
            fn ends_with_jmp() { asm { jmp (vec) } }
            fn main() {
                ends_with_rti()
                ends_with_jmp()
            }
            """);
        Assert.Equal("rti", Listing.Code(result, "ends_with_rti"));
        Assert.Equal("jmp (vec)", Listing.Code(result, "ends_with_jmp"));
    }

    [Trait("Platform", "c64")]
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

    [Trait("Shape", "nmos6502")]
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
        Assert.Matches(@"B1 [0-9A-F]{2} +lda \(p\),y", Listing.Text(r));
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

    [Trait("Platform", "c64")]
    [Trait("Shape", "nmos6502")]
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
        var main = Listing.Text(r);
        Assert.Matches(@"6C [0-9A-F]{2} [0-9A-F]{2} +jmp \(vec\)", main);
        Assert.Matches(@"A1 [0-9A-F]{2} +lda \(p,x\)", main);
        Assert.Matches(@"96 [0-9A-F]{2} +stx zx,y", main);
    }

    [Trait("Platform", "c64")]
    [Trait("Shape", "nmos6502")]
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
        Assert.Contains("8D 20 D0  sta border", Listing.Text(r));
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

    [Trait("Shape", "nmos6502")]
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
        Assert.Contains("; asm: reads A X C D, changes A X C Z N V, reads other score, writes other score", Listing.Text(r));
    }

    [Trait("Shape", "nmos6502")]
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
        var lines = Listing.Instructions(r);
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
        var e = Expect.Error(() => TernCompiler.Compile($"fn main() {{\n    asm {{\n        beq .far\n{nops}    .far:\n    }}\n}}"), 3, 9, "Branch to main.far0 is out of range (130 bytes; a branch reaches -128 to 127). Use jmp");
        Assert.Equal(3, e.Pos.Line);
    }

    [Theory]
    [InlineData("fn main() { asm {\n lda #1 sta 2\n } }", 2, 9, "Expected the end of the line after the instruction, found 'sta'. Put each instruction on its own line")]
    [InlineData("fn main() { asm {\n lda #1; sta 2\n } }", 2, 8, "';' is not used in assembly: write one instruction per line, and comments with //")]
    [InlineData("fn main() { asm {\n foo #1\n } }", 2, 2, "Unknown instruction 'foo'. Use a 6502 mnemonic such as lda, sta or jmp, or write a label as .name:")]
    [InlineData("fn main() { asm {\n loop:\n } }", 2, 2, "Labels in assembly start with '.'. Write .loop:")]
    [InlineData("fn main() { asm {\n .loop\n } }", 2, 7, "Expected ':' after the label, found end of line")]
    [InlineData("asm {\n nop\n}\nfn main() { }", 1, 1, "An asm block must be inside a function. Use 'asm fn name() { ... }' for a whole function in assembly")]
    [InlineData("fn main() { asm fn f() { } }", 1, 13, "'asm fn' must be declared outside functions. Move it to the top level of the file")]
    [InlineData("fn main() { asm {\n nop\n }", 1, 11, "Block is not closed with '}'. Add '}' where it ends")]
    [InlineData("data msg = \"A\"\nfn main() { asm {\n lda #<msg\n } }", 3, 7, "An immediate operand is one byte, and an address has two. Write #name.lo or #name.hi")]
    [InlineData("data msg = \"A\"\nfn main() { asm {\n lda #msg\n } }", 3, 7, "An address has two bytes. Write #name.lo or #name.hi")]
    [InlineData("fn main() { asm {\n lda #300\n } }", 2, 7, "#300 does not fit in a byte. Use a value from -128 to 255, or .lo and .hi of it")]
    [InlineData("fn main() { asm {\n lda #-129\n } }", 2, 7, "#-129 does not fit in a byte. Use a value from -128 to 255, or .lo and .hi of it")]
    [InlineData("fn main() { asm {\n bne .nowhere\n } }", 2, 6, "Label '.nowhere' is not defined in this asm block. Define it with '.nowhere:' on a line of the block")]
    [InlineData("fn main() { asm {\n .a:\n .a:\n } }", 3, 2, "Label '.a' is defined twice in this block. Rename one of them")]
    [InlineData("fn main() { asm {\n inx 5\n } }", 2, 2, "'inx' takes no operand. Remove it")]
    [InlineData("fn main() { asm {\n lda\n } }", 2, 2, "'lda' needs an operand. Write one after it, e.g. lda #0 or lda name")]
    [InlineData("fn main() { asm {\n sta #1\n } }", 2, 2, "'sta' has no immediate mode (#). Remove '#' to use the address, or use another instruction")]
    [InlineData("var p: ptr u8\nfn main() { asm {\n lda (p)\n } }", 3, 2, "'lda' has no (address) mode. Write (pointer),y to read through a pointer")]
    [InlineData("fn main() { asm {\n lda ($1234),y\n } }", 2, 7, "'lda (address),y' needs a zero-page address ($00-$FF), not $1234. Use a variable in zero page")]
    [InlineData("var b: u8 @ $D020\nfn main() { asm {\n lda (b),y\n } }", 3, 7, "'lda (address),y' needs a zero-page address, but 'b' is at $D020. Place it in zero page ('@ zp' or an address below $100)")]
    [InlineData("fn main() { asm {\n lda (main),y\n } }", 2, 7, "'lda (address),y' needs a zero-page address, not a function. Use a variable in zero page")]
    [InlineData("fn main() { asm {\n stx $1234,y\n } }", 2, 6, "'stx address,y' needs a zero-page address ($00-$FF), not $1234. Use a variable in zero page")]
    // Review code-quality-semantics 10: data was moved into zero page.
    [InlineData("data msg = \"HI\"\nvar out: u8\nfn main() { asm {\n ldy #0\n lda (msg),y\n sta out\n } }",
        5, 7, "'lda (address),y' needs a pointer in zero page, but 'msg' is data, not a pointer. Declare one, as in 'var p: ptr u8 = addr(msg)', and use (p),y")]
    [InlineData("data t: [u8; 4] = [1, 2, 3, 4]\nfn main() { asm {\n ldx #0\n lda (t,x)\n } }", 4, 7, "'lda (address,x)' needs a pointer in zero page, but 't' is data, not a pointer. Declare one, as in 'var p: ptr u8 = addr(t)', and use (p),y")]
    [InlineData("data t: [u8; 4] = [1, 2, 3, 4]\nfn main() { asm {\n ldy #0\n stx t,y\n } }",
        4, 6, "'stx address,y' needs a zero-page address, but 't' is data, which is never in zero page. Use another addressing mode, or a variable in zero page")]
    [Trait("Platform", "c64")]
    [InlineData("fn main() { asm {\n stx $12,x\n } }", 2, 2, "'stx' has no address,x mode. Use another addressing mode or instruction")]
    [InlineData("fn main() { asm {\n lda $12,z\n } }", 2, 10, "Expected x or y after ',', found 'z'. Write ',x' or ',y'")]
    [InlineData("fn main() { asm {\n lda ($12,y)\n } }", 2, 11, "Expected x in (address,x), found 'y'. Write (address,x)")]
    [InlineData("fn main() { asm {\n lda ($12),x\n } }", 2, 12, "Expected y in (address),y, found 'x'. Write (address),y")]
    [InlineData("fn main() { asm {\n bne 5\n } }", 2, 2, "'bne' needs a label. Use one, as in 'bne .loop'")]
    [InlineData("var v: u8\nfn main() { asm {\n beq v\n } }", 3, 2, "'beq' needs a label. Use one, as in 'beq .loop'")]
    [InlineData("var v: u16\nfn main() { asm {\n lda v.lo\n } }", 3, 7, ".lo and .hi give a byte value, not an address. Use it as an immediate: #name.lo")]
    [InlineData("var v: u8\nvar w: u8\nfn main() { asm {\n lda v+w\n } }", 4, 7, "In assembly, an address can only have a constant added or subtracted. Write e.g. name+1 or (name+1).hi")]
    [InlineData("var v: u8\nfn main() { asm {\n lda v*2\n } }", 3, 7, "In assembly, an address can only have a constant added or subtracted. Write e.g. name+1 or (name+1).hi")]
    [InlineData("var v: u8\nfn main() { asm {\n lda #v.lo.hi\n } }", 3, 11, ".hi of a value that is already one byte. Remove one '.hi'")]
    [InlineData("var v: u8\nfn main() { asm {\n lda v.x\n } }", 3, 7, "Unknown member '.x'; assembly only has .lo and .hi, and fields of struct variables. Use one of those")]
    [InlineData("struct S { x: u8 }\nvar p: ptr S\nfn main() { asm {\n lda p.x\n } }", 4, 7, "In assembly, a field must belong to a struct variable (lda player.hp). Fields through a pointer or in an array of structs have no fixed address. Read them with an index register: ldy #offset(T.field) and lda (p),y")]
    [InlineData("struct S { x: u8 }\nvar s: S\nfn main() { asm {\n lda s.y\n } }", 4, 7, "Struct 'S' has no field 'y'. Use one of its fields: x")]
    [InlineData("fn main() { asm {\n lda nothing\n } }", 2, 6, "'nothing' is not declared. Check the spelling, or declare it")]
    [InlineData("var t: [u8; 2]\nfn main() { asm {\n lda t[1]\n } }", 3, 7, "An assembly operand must be a constant, or the name of a variable, function or .label. Declare a constant for a computed value")]
    [InlineData("fn main() { asm {\n lda 70000\n } }", 2, 6, "Address 70000 is outside memory. Use an address from $0000 to $FFFF")]
    [InlineData("data B: [u8; 1] = [0]\nfn main() { asm {\n lda B + 65536\n } }", 3, 8, "Offset 65536 from an address is outside memory. Use a smaller offset")]
    // The address itself, once the program is placed (third review, suggestion 3).
    [InlineData("data B: [u8; 1] = [0]\nfn main() { asm {\n lda B + 65000\n } }", 3, 2, "The address B+65000 is $10600, outside memory. Use a smaller offset")]
    [InlineData("data B: [u8; 1] = [0]\nfn main() { asm {\n lda B + 65535\n } }", 3, 2, "The address B+65535 is $10817, outside memory. Use a smaller offset")]
    [InlineData("var v: u8 @ $02\nfn main() { asm {\n lda v - 5\n } }", 3, 2, "The address v-5 is -$3, outside memory. Use a smaller offset")]
    [InlineData("var v: u8 @ $C000\nfn main() { asm {\n sta v + $4000, x\n } }", 3, 2, "The address v+16384 is $10000, outside memory. Use a smaller offset")]
    [InlineData("fn f() { }\nfn main() { asm {\n jmp f + 65000\n } }", 3, 2, "The address f+65000 is $105FF, outside memory. Use a smaller offset")]
    [InlineData("data B: [u8; 1] = [0]\nfn main() { asm {\n lda B + 4294967296\n } }", 3, 8, "Offset 4294967296 from an address is outside memory. Use a smaller offset")]
    [InlineData("var v: u8\nfn main() { asm {\n lda v - 65536\n } }", 3, 8, "Offset -65536 from an address is outside memory. Use a smaller offset")]
    [InlineData("var v: u16\nfn main() { asm {\n lda #(v + 65536).hi\n } }", 3, 18, "Offset 65536 from an address is outside memory. Use a smaller offset")]
    [InlineData("fn main() { asm {\n lda 1/0\n } }", 2, 7, "Division by zero. Check the divisor")]
    [InlineData("fn main() { asm {\n lda #1 == 1\n } }", 2, 9, "Assembly operands can only use + - * / % & | ^ << >>. Declare a constant for anything else")]
    [InlineData("data d: [u8; 2] = [1, 2]\nfn main() { asm {\n inc d\n } }", 3, 6, "'d' is data, which is read-only. Declare it with 'var' to change it")]
    [InlineData("fn main() { for i in 0..3 { asm {\n inc i\n } } }", 2, 6, "Loop variable 'i' cannot be changed inside the loop. Use a 'while' loop to change the counter yourself")]
    [InlineData("fn main() { for i in 0..3 using X { asm {\n stx i\n } } }", 2, 6, "'i' lives in register X; use the register directly")]
    [InlineData("asm fn f(v: u8 using A) {\n sta v\n}\nfn main() { f(1) }", 2, 6, "'v' is passed in register A; use the register directly")]
    [InlineData("fn main() { for i in 0..3 using X { asm {\n ldx #0\n } } }", 2, 2, "This changes X, which holds the loop variable. Remove 'using X' from the loop, or leave X unchanged")]
    [InlineData("fn main() { for i in 0..3 using Y { asm {\n iny\n } } }", 2, 2, "This changes Y, which holds the loop variable. Remove 'using Y' from the loop, or leave Y unchanged")]
    [InlineData("fn main() { for i in 0..3 using X { asm {\n jsr $FFD2\n } } }", 2, 2, "'jsr' calls a subroutine, which can change X, the register of the loop variable. Remove 'using X' from the loop, or call a routine that keeps X")]
    [InlineData("asm fn f() {\n jsr f\n}\nfn main() { f() }", 1, 5, "Recursion is not allowed: f -> f. Use a loop instead, with an array as the stack if needed")]
    [InlineData("fn main() { asm {\n jmp (.x)\n .x:\n lda (.x),y\n } }", 4, 7, "'lda (address),y' needs a zero-page address, not a label. Use a variable in zero page")]
    [InlineData("var score: u8\nfn main() { asm {\n lda -score\n } }", 3, 6, "Only numbers can be negated or inverted. Use a constant here, not an address")]
    [InlineData("var v: u16 @ $10FF\nfn main() { asm {\n jmp (v)\n } }", 3, 7, "jmp (...) at $10FF reads its high byte from $1000 on the 6502, since the address ends in $FF. Move 'v'")]
    [InlineData("var t: [u8; 4] @ $10FD\nfn main() { asm {\n jmp (t+2)\n } }", 3, 8, "jmp (...) at $10FF reads its high byte from $1000 on the 6502, since the address ends in $FF. Move 't'")]
    [InlineData("fn f(v: u8) { }\nfn main() { asm {\n jsr f\n } }", 3, 6, "'f' takes 'v' in memory, which assembly cannot set. Pass it in a register (v: u8 using A), or call f from Tern")]
    [InlineData("fn f(a: u8 using A, v: u16) { }\nfn main() { asm {\n jmp f\n } }", 3, 6, "'f' takes 'v' in memory, which assembly cannot set. Pass it in a register (v: u16 using A), or call f from Tern")]
    public void Reports_errors(string source, int line, int column, string expected)
    {
        Expect.Error(source, line, column, expected);
    }

    [Trait("Platform", "c64")]
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

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Jump_vector_at_a_fixed_address_away_from_a_page_end_is_kept()
    {
        var r = TernCompiler.Compile("var v: u16 @ $10FE\nfn main() { asm {\n jmp (v)\n } }");
        Assert.Contains("6C FE 10", Listing.Text(r));
    }

    [Fact]
    public void Errors_point_at_the_instruction()
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("fn main() {\n    for i in 0..3 using X {\n        asm {\n            nop\n            dex\n        }\n    }\n}"));
        Assert.Equal(new SourcePos(5, 13), e.Pos);
    }

    [Trait("Platform", "c64")]
    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Kernal_example_prints_through_chrout()
    {
        var path = FindExample("kernal.tern");
        var r = Run.Program(File.ReadAllText(path), sourcePath: path, setup: ram =>
        {
            // The emulator has no ROM. CHROUT appends A to a buffer at $C000 (count at $C0FE);
            // PLOT returns row 0 and the column of the last character printed.
            ram.Bytes[0xC0FE] = 0;
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
            Listing.Instructions(r, "print"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Kernal_example_listing_has_no_stand_in_for_chrout()
    {
        var path = FindExample("kernal.tern");
        var result = TernCompiler.Compile(File.ReadAllText(path), sourcePath: path);
        Assert.DoesNotContain("chrout", Listing.Sections(result));
        Assert.Contains("20 D2 FF  jsr $FFD2", Listing.Text(result, "print"));
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

    [Trait("Platform", "c64")]
    /// <summary>
    /// The NMOS 6502 reads the high byte of jmp ($xxFF) from $xx00 (the emulator does too). The
    /// compiler places a vector it allocates so that it never starts at $xxFF. Zero page is full,
    /// so v goes after the program, and the size of pad moves it across a page end.
    /// </summary>
    [Trait("Shape", "nmos6502")]
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
                padded |= !arrays && Listing.Outside(r).Contains("of padding");
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
