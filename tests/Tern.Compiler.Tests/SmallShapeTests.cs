namespace Tern.Compiler.Tests;

/// <summary>M18 F10: small code shapes, each checked against C# at edge values and in the listing.</summary>
public class SmallShapeTests
{
    private static string Code(Run r, string function = "main")
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

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(0xFEFF)]
    [InlineData(0xFF00)]
    [InlineData(0xFFFF)]
    public void Adding_a_multiple_of_256_changes_the_high_byte_only(int w)
    {
        var r = Run.Program($$"""
            var a: u16 = {{w}}
            var b: u16 = {{w}}
            var c: u16 = {{w}}
            var d: u16 = {{w}}
            var p: ptr u8 = {{w}}
            fn main() {
                a += 256
                b -= 256
                c += $0300
                d -= $FF00
                p += 256
            }
            """);
        Assert.Equal((ushort)(w + 256), r.U16("a"));
        Assert.Equal((ushort)(w - 256), r.U16("b"));
        Assert.Equal((ushort)(w + 0x300), r.U16("c"));
        Assert.Equal((ushort)(w - 0xFF00), r.U16("d"));
        Assert.Equal((ushort)(w + 256), r.U16("p"));
        Assert.Equal("inc a+1|dec b+1|lda c+1|clc|adc #$03|sta c+1|lda d+1|sec|sbc #$FF|sta d+1|inc p+1|rts", Code(r));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(255)]
    public void An_element_is_incremented_in_place(int v)
    {
        var r = Run.Program($$"""
            var a: [u8; 8] = [{{v}}, {{v}}, {{v}}, {{v}}, {{v}}, {{v}}, {{v}}, {{v}}]
            var i: u8 = 3
            fn main() {
                a[i] += 1
                a[i - 1] -= 1
                for k in 5..7 { a[k + 1] += 1 }
            }
            """);
        byte[] want = [.. Enumerable.Repeat((byte)v, 8)];
        want[3]++; want[2]--; want[6]++; want[7]++;
        for (int k = 0; k < 8; k++) Assert.Equal(want[k], r.U8("a", k));
        Assert.Contains("ldx i|inc a,x|dec a-1,x", Code(r));
        Assert.Contains("inc a+1,x", Code(r));
    }

    [Fact]
    public void An_element_indexed_by_y_is_incremented_through_a()
    {
        // inc has no abs,y form: with X held by a loop, a[j] += 1 adds in A.
        var r = Run.Program("""
            var a: [u8; 8]
            var b: [u8; 8]
            fn main() {
                var j: u8 = 2
                for i in 0..4 using X {
                    b[i] = 1
                    a[j] += 1
                }
            }
            """);
        Assert.Equal(4, r.U8("a", 2));
        Assert.DoesNotContain("inc a,y", r.Result.Listing);
    }
}
