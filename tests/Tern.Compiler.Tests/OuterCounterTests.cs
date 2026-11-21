namespace Tern.Compiler.Tests;

/// <summary>
/// M18 part 3, F13: the counter of a loop with loops inside goes in a register, Y if nothing else
/// needs it, leaving X to the inner loops (CodeGen.ChooseOuterCounterRegister, and
/// ChooseCounterRegister beside a held register).
/// </summary>
public class OuterCounterTests
{
    [Trait("Shape", "nmos6502")]
    [Fact]
    public void The_outer_counter_is_in_y_and_the_inner_one_in_x()
    {
        var r = Run.Program("""
            var a: [u8; 256]
            var b: [u8; 5]
            fn main() {
                for i in 0..=255 {
                    a[i] = i ^ 90
                    for j in 0..5 { b[j] += 1 }
                }
            }
            """);
        for (int i = 0; i < 256; i++) Assert.Equal(i ^ 90, r.U8("a", i));
        for (int j = 0; j < 5; j++) Assert.Equal(0, r.U8("b", j));   // 256 times 1, wrapped
        var code = Listing.Code(r);
        Assert.Contains("tya|eor #$5A|sta a,y", code);
        Assert.Contains("inc b,x", code);
        Assert.Contains("iny|bne", code);
        Assert.DoesNotContain("main.i", code);
        Assert.DoesNotContain("main.j", code);
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void An_inner_loop_indexing_by_the_outer_counter_takes_x()
    {
        var r = Run.Program("""
            var m: [u8; 8]
            fn main() {
                for i in 0..8 {
                    for j in 1..=4 { m[i] += j }
                }
            }
            """);
        for (int i = 0; i < 8; i++)
        {
            int sum = 0;
            for (int j = 1; j <= 4; j++) sum += j;
            Assert.Equal(sum & 0xFF, r.U8("m", i));
        }
        var code = Listing.Code(r);
        Assert.DoesNotContain("main.i", code);
        Assert.DoesNotContain("main.j", code);
        // i counts in Y and j in X.
        Assert.StartsWith("ldy #$00|ldx #$01|", code);
        Assert.Contains("|inx|cpx #$05|", code);
        Assert.Contains("|iny|cpy #$08|", code);
    }

    [Trait("Platform", "c64")]
    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Beside_an_inner_using_y_loop_the_outer_counter_is_in_x()
    {
        var src = Enumerable.Range(0, 768).Select(k => (byte)(k * 7)).ToArray();
        var r = Run.Program("""
            const SRC = $4000
            const DST = $5000
            var same: u8 = 1
            fn main() {
                var p: ptr u8 = SRC
                var q: ptr u8 = DST
                for _ in 0..3 {
                    for j in 0..=255 using Y { q[j] = p[j] }
                    p += 256
                    q += 256
                }
                var a: ptr u8 = SRC
                var b: ptr u8 = DST
                for _ in 0..3 {
                    for j in 0..=255 using Y {
                        if a[j] != b[j] { same = 0 }
                    }
                    a += 256
                    b += 256
                }
            }
            """, setup: ram => { for (int k = 0; k < src.Length; k++) ram.Bytes[0x4000 + k] = src[k]; });
        Assert.Equal(1, r.U8("same"));
        for (int k = 0; k < src.Length; k++) Assert.Equal(src[k], r.Ram.Bytes[0x5000 + k]);
        var code = Listing.Code(r);
        Assert.Contains("ldx #$03", code);
        Assert.Contains("dex|bne", code);
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_pointer_access_in_the_outer_loop_leaves_y_free()
    {
        var r = Run.Program("""
            var buf: [u8; 16]
            var out: [u8; 4]
            fn main() {
                var p: ptr u8 = addr(buf)
                for i in 0..4 {
                    out[i] = p[3]
                    for j in 0..4 { buf[j] += 1 }
                }
            }
            """);
        for (int j = 0; j < 4; j++) Assert.Equal(4, r.U8("buf", j));
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, Enumerable.Range(0, 4).Select(k => r.U8("out", k)).ToArray());
        var code = Listing.Code(r);
        Assert.Contains("sta out,x", code);       // i in X
        Assert.Contains("sta buf,y|iny", code);   // j in Y, which p[3] needs only outside the inner loop
        Assert.DoesNotContain("main.i", code);
        Assert.DoesNotContain("main.j", code);
    }

    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData("for j in 0..i { t += 1 }")]           // the counter is the inner loop's bound
    [InlineData("for j in 0..4 { t += 1 }\n g()")]     // a call
    [InlineData("var k: u8 = 0\n while k < 3 { k += 1 }")]   // a while loop
    public void The_outer_counter_stays_in_memory(string body)
    {
        var r = Run.Program($$"""
            var t: u8
            fn g() {}
            fn main() {
                for i in 0..6 {
                    {{body}}
                }
            }
            """);
        Assert.Contains("inc main.i", Listing.Code(r));
    }
}
