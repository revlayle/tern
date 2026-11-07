namespace Tern.Compiler.Tests;

/// <summary>M18 F4: x > c as cmp #c+1, keeping x in A; every value against edge constants.</summary>
public class ConstantCompareTests
{
    private static string Program(string type, string c) => $$"""
        var flags: [u8; 256]
        fn main() {
            for k in 0..=255 {
                var x: {{type}} = k as {{type}}
                var f: u8 = 0
                if x > {{c}} { f |= 1 }
                if x <= {{c}} { f |= 2 }
                if x < {{c}} { f |= 4 }
                if x >= {{c}} { f |= 8 }
                if {{c}} < x { f |= 16 }
                if {{c}} >= x { f |= 32 }
                if {{c}} > x { f |= 64 }
                if {{c}} <= x { f |= 128 }
                flags[k] = f
            }
        }
        """;

    private static int Flags(int x, int c) =>
        (x > c ? 1 : 0) | (x <= c ? 2 : 0) | (x < c ? 4 : 0) | (x >= c ? 8 : 0)
        | (c < x ? 16 : 0) | (c >= x ? 32 : 0) | (c > x ? 64 : 0) | (c <= x ? 128 : 0);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(200)]
    [InlineData(254)]
    [InlineData(255)]
    public void Unsigned_compares_with_a_constant_match_C(int c)
    {
        var r = Run.Program(Program("u8", c.ToString()));
        for (int k = 0; k < 256; k++) Assert.Equal(Flags(k, c), r.U8("flags", k));
    }

    [Theory]
    [InlineData(-128)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(126)]
    [InlineData(127)]
    public void Signed_compares_with_a_constant_match_C(int c)
    {
        var r = Run.Program(Program("i8", c.ToString()));
        for (int k = 0; k < 256; k++) Assert.Equal(Flags((sbyte)k, c), r.U8("flags", k));
    }

    [Fact]
    public void Greater_than_a_constant_compares_with_the_next_value()
    {
        var r = Run.Program("""
            var x: u8 = 201
            var hit: u8
            fn main() {
                var y: u8 = x + 1
                if y > 200 { hit = y }
            }
            """);
        Assert.Equal(202, r.U8("hit"));
        Assert.Contains("cmp #$C9", r.Result.Listing);
        Assert.DoesNotContain("lda #$C8", r.Result.Listing);
    }
}
