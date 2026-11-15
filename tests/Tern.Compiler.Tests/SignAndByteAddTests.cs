namespace Tern.Compiler.Tests;

/// <summary>
/// M20 follow-up 3, spec 11 (gaps 13 and 14): <c>p = q + e</c> with <c>e</c> an unsigned byte is an 8-bit add
/// with the carry into the high byte, never a 16-bit temporary; a signed <c>s &lt; 0</c> / <c>s &gt;= 0</c> tests
/// the sign bit with <c>bmi</c> / <c>bpl</c>.
/// </summary>
public class SignAndByteAddTests
{
    private static readonly int[] Words = [0, 1, 0x00FF, 0x0100, 0x12F0, 0x7FFF, 0x8000, 0xFFFE, 0xFFFF];
    private static readonly int[] Bytes = [0, 1, 127, 128, 200, 255];

    /// <summary>The instructions of a function before its rts, with its endif labels numbered alike.</summary>
    private static string Body(CompileResult result, string fn) =>
        string.Join("|", Listing.Instructions(result, fn).TakeWhile(i => i != "rts")
            .Select(i => System.Text.RegularExpressions.Regex.Replace(i, @"endif\d+", "endif")));

    [Fact]
    public void A_pointer_plus_a_byte_expression_adds_eight_bits()
    {
        var result = TernCompiler.Compile("""
            var rows: [ptr u8; 12]
            var p: ptr u8
            var q: ptr u8
            var w: u16
            var col: u8
            var row: u8
            fn f() { p = rows[row] + (col << 1) }
            fn g() { p = q + (col << 1) }
            fn h() { w = (col & 7) + w }
            fn main() { f(); g(); h() }
            """);
        Assert.Equal("lda col|asl a|clc|ldx row|adc rows,x|sta p|lda rows+12,x|adc #$00|sta p+1", Body(result, "f"));
        Assert.Equal("lda col|asl a|clc|adc q|sta p|lda q+1|adc #$00|sta p+1", Body(result, "g"));
        Assert.DoesNotContain("_t", Body(result, "h"));
    }

    [Fact]
    public void A_pointer_plus_a_byte_expression_gives_the_right_sum()
    {
        foreach (int q in Words)
            foreach (int col in Bytes)
            {
                var r = Run.Program($$"""
                    var rows: [u16; 4] = [0, {{q}}, 0, 0]
                    var q: ptr u8 = {{q}}
                    var w: u16 = {{q}}
                    var col: u8 = {{col}}
                    var row: u8 = 1
                    var p1: ptr u8
                    var p2: ptr u8
                    var w1: u16
                    var w2: u16
                    var w3: u16
                    fn main() {
                        p1 = q + (col << 1)
                        p2 = (rows[row] + (col >> 1)) as ptr u8
                        w1 = (col | 1) + w
                        w2 = w + (col as u16)
                        w3 = w + ((col + 1) as u16)
                    }
                    """);
                Assert.Equal((q + ((col << 1) & 0xFF)) & 0xFFFF, r.U16("p1"));
                Assert.Equal((q + (col >> 1)) & 0xFFFF, r.U16("p2"));
                Assert.Equal((q + (col | 1)) & 0xFFFF, r.U16("w1"));
                Assert.Equal((q + col) & 0xFFFF, r.U16("w2"));
                Assert.Equal((q + ((col + 1) & 0xFF)) & 0xFFFF, r.U16("w3"));
            }
    }

    [Fact]
    public void A_sign_test_is_one_branch()
    {
        var result = TernCompiler.Compile("""
            var s: i8
            var w: i16
            var d: u8
            var n: u8
            fn a() { if s < 0 { n = 1 } }
            fn b() { if w >= 0 { n = 2 } }
            fn c() { if (d as i8) < 0 { n = 3 } }
            fn e() { if s >= 0 { n = 4 } }
            fn f() { if w < 0 { n = 5 } }
            fn g() { if 0 > w { n = 6 } }
            fn main() { a(); b(); c(); e(); f(); g() }
            """);
        Assert.Equal("lda s|bpl a.endif|lda #$01|sta n", Body(result, "a"));
        Assert.Equal("lda w+1|bmi b.endif|lda #$02|sta n", Body(result, "b"));
        Assert.Equal("lda d|bpl c.endif|lda #$03|sta n", Body(result, "c"));
        Assert.Equal("lda s|bmi e.endif|lda #$04|sta n", Body(result, "e"));
        Assert.Equal("lda w+1|bpl f.endif|lda #$05|sta n", Body(result, "f"));
        Assert.Equal("lda w+1|bpl g.endif|lda #$06|sta n", Body(result, "g"));   // 0 > w is w < 0
    }

    [Fact]
    public void A_sign_test_gives_the_right_result()
    {
        int[] bytes = [-128, -127, -1, 0, 1, 126, 127];
        int[] words = [-32768, -32767, -256, -255, -1, 0, 1, 255, 256, 32767];
        foreach (int s in bytes)
            foreach (int w in words)
            {
                var r = Run.Program($$"""
                    var s: i8 = {{s}}
                    var w: i16 = {{w}}
                    var d: u8 = {{s & 0xFF}}
                    var a: bool
                    var b: bool
                    var c: bool
                    var e: bool
                    var g: u8
                    fn neg() -> i16 { return -w }
                    fn main() {
                        a = s < 0
                        b = s >= 0
                        c = w < 0
                        e = w >= 0
                        if (d as i8) < 0 { g |= 1 }
                        if 0 > s { g |= 2 }
                        if neg() >= 0 { g |= 4 }
                        if 0 > w { g |= 8 }
                        if 0 <= w { g |= 16 }
                        while s < 0 { s += 64 }
                    }
                    """);
                Assert.Equal(s < 0, r.U8("a") != 0);
                Assert.Equal(s >= 0, r.U8("b") != 0);
                Assert.Equal(w < 0, r.U8("c") != 0);
                Assert.Equal(w >= 0, r.U8("e") != 0);
                int g = (s < 0 ? 1 : 0) | (0 > s ? 2 : 0) | ((short)-w >= 0 ? 4 : 0) | (0 > w ? 8 : 0) | (0 <= w ? 16 : 0);
                Assert.Equal(g, r.U8("g"));
                int after = s;
                while (after < 0) after += 64;
                Assert.Equal(after, r.I8("s"));
            }
    }
}
