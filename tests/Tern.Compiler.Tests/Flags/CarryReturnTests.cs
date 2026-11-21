namespace Tern.Compiler.Tests;

/// <summary>M18 F21: an unsigned compare returned as a bool in the carry, without branches. Spec 7 (flags as values).</summary>
public class CarryReturnTests
{
    private static readonly int[] Edge = [0, 1, 2, 127, 128, 200, 201, 254, 255];

    [Theory]
    [InlineData("a > b")]
    [InlineData("a >= b")]
    [InlineData("a < b")]
    [InlineData("a <= b")]
    [InlineData("a > 200")]
    [InlineData("a > 255")]
    [InlineData("a >= 0")]
    [InlineData("a <= 0")]
    [InlineData("a < 1")]
    [InlineData("200 < a")]
    [InlineData("5 >= a")]
    [InlineData("t[a] > b")]
    [InlineData("a > t[b]")]
    [InlineData("(a >> 1) < b")]
    public void Matches_C_for_every_pair(string condition)
    {
        string Fn(string parameters) => $$"""
            fn f({{parameters}}) -> bool {
                return {{condition}}
            }
            """;
        foreach (var parameters in new[] { "a: u8, b: u8", "a: u8 using A, b: u8 using X", "a: u8 using X, b: u8 using Y" })
        {
            var r = Run.Program($$"""
                var edge: [u8; 9] = [{{string.Join(", ", Edge)}}]
                var t: [u8; 256]
                var got: [u8; 81]
                var flags: [u8; 81]
                {{Fn(parameters)}}
                fn main() {
                    for k in 0..=255 { t[k] = 255 - k }
                    var n: u8 = 0
                    for i in 0..9 {
                        for j in 0..9 {
                            got[n] = f(edge[i], edge[j]) as u8
                            if f(edge[i], edge[j]) { flags[n] = 1 }
                            n += 1
                        }
                    }
                }
                """);
            for (int i = 0; i < 9; i++)
                for (int j = 0; j < 9; j++)
                {
                    int a = Edge[i], b = Edge[j];
                    bool want = condition switch
                    {
                        "a > b" => a > b, "a >= b" => a >= b, "a < b" => a < b, "a <= b" => a <= b,
                        "a > 200" => a > 200, "a > 255" => false, "a >= 0" => true, "a <= 0" => a == 0,
                        "a < 1" => a < 1, "200 < a" => 200 < a, "5 >= a" => 5 >= a,
                        "t[a] > b" => 255 - a > b, "a > t[b]" => a > 255 - b, _ => (a >> 1) < b,
                    };
                    Assert.True((want ? 1 : 0) == r.U8("got", i * 9 + j), $"{parameters}: f({a}, {b}) = {condition}");
                    Assert.Equal(want ? 1 : 0, r.U8("flags", i * 9 + j));
                }
        }
    }

    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData("a > b", "lda f.a|clc|sbc f.b|rts")]
    [InlineData("a >= b", "lda f.a|cmp f.b|rts")]
    [InlineData("a < b", "lda f.b|clc|sbc f.a|rts")]
    [InlineData("a <= b", "lda f.b|cmp f.a|rts")]
    [InlineData("a > 200", "lda f.a|cmp #$C9|rts")]
    public void The_compare_leaves_the_result_in_the_carry(string condition, string code)
    {
        var r = Run.Program($$"""
            var x: u8
            fn f(a: u8, b: u8) -> bool { return {{condition}} }
            fn main() { if f(3, 4) { x = 1 } }
            """);
        Assert.Equal(code, Listing.Code(r, "f"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_register_parameter_compares_with_cpx()
    {
        var r = Run.Program("""
            var x: u8
            fn f(a: u8 using X, b: u8) -> bool { return a >= b }
            fn main() { if f(5, 4) { x = 1 } }
            """);
        Assert.Equal(1, r.U8("x"));
        Assert.Equal("cpx f.b|rts", Listing.Code(r, "f"));
    }

}
