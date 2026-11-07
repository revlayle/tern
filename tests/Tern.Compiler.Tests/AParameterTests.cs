namespace Tern.Compiler.Tests;

/// <summary>
/// M18 F16: a <c>using A</c> parameter compared at the start of the body is compared in A
/// (<c>cmp top</c> with two branches), so it is never stored or reloaded.
/// </summary>
public class AParameterTests
{
    private static string Code(Run r, string function)
    {
        var listing = r.Result.Listing;
        var start = listing.IndexOf($"\n{function}:", StringComparison.Ordinal);
        var code = new List<string>();
        foreach (var l in listing[(start + 1)..].Split('\n').Skip(1).Select(l => l.TrimEnd('\r')))
        {
            if (l.EndsWith(':') && !l.StartsWith(' ') && !l.StartsWith('$') && !l.StartsWith(';') && !l.StartsWith($"{function}.")) break;
            if (l.StartsWith('$')) code.Add(l[17..].Split(';')[0].Trim());
        }
        return string.Join("|", code);
    }

    private static readonly int[] Tops = [0, 1, 127, 128, 254, 255];

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
            Assert.Equal("cmp clamp.top|bcc clamp.endif3|beq clamp.endif3|lda clamp.top|rts|rts", Code(r, "clamp"));
        }
    }

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
            Assert.DoesNotContain("sta test.v", r.Result.Listing);
        }
    }

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
        Assert.Contains("sta first.v", r.Result.Listing);
        Assert.Contains("cmp first.v", r.Result.Listing);
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
}
