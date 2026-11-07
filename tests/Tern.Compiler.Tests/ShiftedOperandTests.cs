namespace Tern.Compiler.Tests;

/// <summary>M18 F8: a 16-bit shift by a constant as an operand keeps its low byte in A.</summary>
public class ShiftedOperandTests
{
    private static readonly ushort[] Values = [0, 1, 255, 256, 1000, 32768, 40000, 65535];

    private static ushort Apply(string op, int x, int y) => (ushort)(op switch
    {
        "+" => x + y, "-" => x - y, "&" => x & y, "|" => x | y, _ => x ^ y,
    });

    [Theory]
    [InlineData("+", ">>", 3)]
    [InlineData("+", "<<", 1)]
    [InlineData("-", ">>", 1)]
    [InlineData("-", "<<", 4)]
    [InlineData("&", ">>", 7)]
    [InlineData("|", "<<", 8)]
    [InlineData("^", ">>", 9)]
    [InlineData("+", ">>", 15)]
    public void Matches_C_for_edge_values(string op, string shift, int k)
    {
        var data = string.Join(", ", Values);
        var r = Run.Program($$"""
            var xs: [u16; 8] = [{{data}}]
            var ys: [u16; 8] = [{{data}}]
            var a: [u16; 64]
            var b: [u16; 64]
            var c: [u16; 64]
            fn main() {
                var n: u8 = 0
                for i in 0..8 {
                    for j in 0..8 {
                        var x: u16 = xs[i]
                        var y: u16 = ys[j]
                        a[n] = 0
                        var r: u16 = (y {{shift}} {{k}}) {{op}} x
                        a[n] = r
                        var s: u16 = x
                        s = s {{op}} (y {{shift}} {{k}})
                        b[n] = s
                        var t: u16 = y
                        t = (t {{shift}} {{k}}) {{op}} xs[i]
                        c[n] = t
                        n += 1
                    }
                }
            }
            """);
        for (int i = 0; i < 8; i++)
            for (int j = 0; j < 8; j++)
            {
                int x = Values[i], y = Values[j];
                int shifted = (ushort)(shift == "<<" ? y << k : y >> k);
                int n = i * 8 + j;
                Assert.Equal(Apply(op, shifted, x), r.U16At("a", n, 64));
                Assert.Equal(Apply(op, x, shifted), r.U16At("b", n, 64));
                Assert.Equal(Apply(op, shifted, x), r.U16At("c", n, 64));
            }
    }

    [Fact]
    public void The_low_byte_is_shifted_in_a()
    {
        var r = Run.Program("""
            var v: u16 = 1000
            fn main() { v = v + (v >> 3) }
            """);
        Assert.Equal(1125, r.U16("v"));
        Assert.Contains("ror a", r.Result.Listing);
        Assert.DoesNotContain("main._t0+1", r.Result.Listing);
    }

    [Fact]
    public void A_signed_shift_right_is_not_taken_apart()
    {
        var r = Run.Program("""
            var v: i16 = -1000
            var w: i16 = 5
            fn main() { w = w + (v >> 3) }
            """);
        Assert.Equal(5 + (-1000 >> 3), r.I16("w"));
    }
}
