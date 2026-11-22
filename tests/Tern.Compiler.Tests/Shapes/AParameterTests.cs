namespace Tern.Compiler.Tests;

/// <summary>
/// M18 F16: a <c>using A</c> parameter compared at the start of the body is compared in A
/// (<c>cmp top</c> with two branches), so it is never stored or reloaded.
/// Spec 11 (code shapes).
/// </summary>
public class AParameterTests
{
    private static readonly int[] Tops = [0, 1, 127, 128, 254, 255];

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Clamp_compares_the_parameter_in_a()
    {
        foreach (int top in Tops)
        {
            var r = Run.Program($$"""
                var out: [u8; 256]
                fn clamp(v: u8 using A, top: u8) -> u8 {
                    if v > top { return top }
                    return v
                }
                fn main() {
                    for i in 0..=255 { out[i] = clamp(i, {{top}}) }
                }
                """);
            for (int v = 0; v < 256; v++) Assert.Equal(Math.Min(v, top), r.U8("out", v));
            Assert.Equal("cmp clamp.top|bcc clamp.endif3|beq clamp.endif3|lda clamp.top|rts|rts", Listing.Code(r, "clamp"));
        }
    }

    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData("v > t")]
    [InlineData("v <= t")]
    [InlineData("!(v > t)")]
    [InlineData("!(v <= t)")]
    public void Greater_and_less_or_equal_both_ways(string condition)
    {
        bool Expected(int v, int t) => condition switch
        {
            "v > t" => v > t, "v <= t" => v <= t, "!(v > t)" => !(v > t), _ => !(v <= t),
        };
        foreach (int t in Tops)
        {
            var r = Run.Program($$"""
                var out: [u8; 256]
                var limit: u8 = {{t}}
                fn test(v: u8 using A, t: u8) -> u8 {
                    if {{condition}} { return 1 }
                    return 0
                }
                fn main() {
                    for i in 0..=255 { out[i] = test(i, limit) }
                }
                """);
            for (int v = 0; v < 256; v++) Assert.Equal(Expected(v, t) ? 1 : 0, r.U8("out", v));
            var test = Listing.Instructions(r, "test");
            Assert.DoesNotContain(test, i => i.Contains("test.v"));
            Assert.Contains("cmp test.t", test);
        }
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void After_a_changes_or_at_a_label_the_parameter_is_read_from_memory()
    {
        var r = Run.Program("""
            var out: [u8; 4]
            var g: u8
            fn first(v: u8 using A, t: u8) -> u8 {
                g = 7
                if v > t { return 1 }
                return 0
            }
            fn counted(v: u8 using A, t: u8) -> u8 {
                var n: u8 = 0
                while v > t {
                    v -= 1
                    n += 1
                }
                return n
            }
            fn main() {
                out[0] = first(10, 9)
                out[1] = first(9, 9)
                out[2] = counted(10, 4)
                out[3] = counted(4, 10)
            }
            """);
        Assert.Equal([1, 0, 6, 0], Enumerable.Range(0, 4).Select(k => (int)r.U8("out", k)));
        Assert.Contains("sta first.v", Listing.Instructions(r, "first"));
        Assert.Contains("cmp first.v", Listing.Instructions(r, "first"));
    }

    [Fact]
    public void A_signed_parameter_keeps_the_signed_compare()
    {
        int[] values = [-128, -1, 0, 1, 127];
        foreach (int t in values)
        {
            var r = Run.Program($$"""
                var out: [u8; 256]
                fn test(v: i8 using A, t: i8) -> u8 {
                    if v > t { return 1 }
                    return 0
                }
                fn main() {
                    for i in 0..=255 { out[i] = test(i as i8, {{t}}) }
                }
                """);
            for (int v = 0; v < 256; v++) Assert.Equal((sbyte)v > t ? 1 : 0, r.U8("out", v));
        }
    }

    // Review M18 part 2, finding 1: a using AX parameter (high bytes with cpx, then low bytes with cmp).

    private static readonly int[] Words = [0, 1, 255, 256, 257, 0x7FFF, 0x8000, 0xFFFE, 0xFFFF];

    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData("<")]
    [InlineData("<=")]
    [InlineData(">")]
    [InlineData(">=")]
    [InlineData("==")]
    [InlineData("!=")]
    public void A_using_ax_parameter_is_compared_in_a_and_x(string op)
    {
        bool Holds(int v, int t) => op switch
        {
            "<" => v < t, "<=" => v <= t, ">" => v > t, ">=" => v >= t, "==" => v == t, _ => v != t,
        };
        int n = Words.Length;
        var r = Run.Program($$"""
            var words: [u16; {{n}}] = [{{string.Join(", ", Words)}}]
            var yes: [u8; {{n * n}}]
            var no: [u8; {{n * n}}]
            var k: u8
            fn then(v: u16 using AX, t: u16) -> u8 {
                if v {{op}} t { return 1 }
                return 0
            }
            fn otherwise(v: u16 using AX, t: u16) -> u8 {
                if !(v {{op}} t) { return 0 }
                return 1
            }
            fn main() {
                for i in 0..{{n}} {
                    for j in 0..{{n}} {
                        yes[k] = then(words[i], words[j])
                        no[k] = otherwise(words[i], words[j])
                        k += 1
                    }
                }
            }
            """);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                int expected = Holds(Words[i], Words[j]) ? 1 : 0;
                Assert.Equal(expected, r.U8("yes", i * n + j));
                Assert.Equal(expected, r.U8("no", i * n + j));
            }
        var then = Listing.Instructions(r, "then");
        Assert.DoesNotContain(then, i => i.Contains("then.v"));
        Assert.Contains("cmp then.t", then);     // v's low byte, in A
        Assert.Contains("cpx then.t+1", then);   // v's high byte, in X
        // Ordering compares the high bytes first; equality the low bytes first.
        Assert.StartsWith(op is "==" or "!=" ? "cmp then.t|" : "cpx then.t+1|", Listing.Code(r, "then"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_using_ax_parameter_returned_unchanged_needs_no_memory()
    {
        var r = Run.Program("""
            var out: [u16; 4]
            var top: u16 = 1000
            fn clamp(v: u16 using AX) -> u16 {
                if v > top { return top }
                return v
            }
            fn main() {
                out[0] = clamp(999)
                out[1] = clamp(1000)
                out[2] = clamp(1001)
                out[3] = clamp(65535)
            }
            """);
        Assert.Equal([999, 1000, 1000, 1000], Enumerable.Range(0, 4).Select(k => (int)r.U16At("out", k, 4)));
        Assert.Equal("cpx top+1|bcc clamp.endif0|bne clamp.cmp1|cmp top|bcc clamp.endif0|beq clamp.endif0|ldx top+1|lda top|rts|rts", Listing.Code(r, "clamp"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void After_a_call_a_using_ax_parameter_is_read_from_memory()
    {
        var r = Run.Program("""
            var out: [u8; 2]
            var calls: u8
            fn g() { calls += 1 }
            fn f(v: u16 using AX, t: u16) -> u8 {
                g()
                if v > t { return 1 }
                return 0
            }
            fn main() {
                out[0] = f(300, 299)
                out[1] = f(299, 300)
            }
            """);
        Assert.Equal([1, 0], new[] { (int)r.U8("out"), r.U8("out", 1) });
        Assert.Contains("stx f.v+1", Listing.Instructions(r, "f"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(200)]
    [InlineData(255)]
    public void A_parameter_needed_after_A_changes_is_kept_in_X_or_Y(int v)
    {
        // V1 fixes: tax on entry in place of sta, when that leaves the parameter's memory unused.
        var r = Run.Program($$"""
            var q: u8 = 7
            var u: u8
            var w: u8
            var xs: [u8; 256]
            var ys: [u8; 256]
            var p: ptr u8
            fn later(v: u8 using A) { u = q; w = v }
            fn inside(v: u8 using A) { if q != 0 { u = v + 1 } }
            fn index(i: u8 using A) { xs[i] = q }
            fn pointer(i: u8 using A) { p[i] = q }
            fn first(v: u8 using A) -> u8 { return v + q }
            fn main() {
                p = addr(ys) as ptr u8
                later({{v}})
                inside({{v}})
                index({{v}})
                pointer({{v}})
                q = first({{v}})
            }
            """);
        Assert.Equal(v, r.U8("w"));
        Assert.Equal((v + 1) & 0xFF, r.U8("u"));
        Assert.Equal(7, r.U8("xs", v));
        Assert.Equal(7, r.U8("ys", v));
        Assert.Equal((v + 7) & 0xFF, r.U8("q"));
        Assert.Equal("tax|lda q|sta u|txa|sta w|rts", Listing.Code(r, "later"));
        Assert.Equal("tax|lda q|sta xs,x|rts", Listing.Code(r, "index"));
        Assert.Equal("tay|lda q|sta (p),y|rts", Listing.Code(r, "pointer"));
        // Read first, from A: nothing to move.
        Assert.Equal("clc|adc q|rts", Listing.Code(r, "first"));
        Assert.Matches(@"^tax\|lda q\|beq inside\.endif\d+\|txa\|clc\|adc #\$01\|sta u\|rts$", Listing.Code(r, "inside"));
    }
}
