using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// <c>linear</c> arrays of 16-bit values (spec 3.2): each value stored whole, low byte first.
/// A variable index is doubled with one <c>asl</c>; a constant index is a fixed offset.
/// </summary>
public class LinearArrayTests
{
    /// <summary>Element <paramref name="i"/> of a linear 16-bit array.</summary>
    private static int At(Run r, string name, int i) => r.U8(name, 2 * i) | r.U8(name, 2 * i + 1) << 8;

    /// <summary>The instructions of main, without addresses, bytes and cycles.</summary>
    private static string Code(Run r)
    {
        var listing = r.Result.Listing;
        var start = listing.IndexOf("\nmain:", StringComparison.Ordinal);
        var code = new List<string>();
        foreach (var l in listing[(start + 1)..].Split('\n').Skip(1))
        {
            if (l.EndsWith(':') && !l.StartsWith(' ') && !l.StartsWith('$') && !l.StartsWith(';')) break;
            if (l.StartsWith('$')) code.Add(l[17..].Split(';')[0].Trim());
        }
        return string.Join("|", code);
    }

    private static readonly int[] Values = [0, 255, 256, 0x1234, 0x7FFF, 0x8000, 0xFFFF, 1];

    [Fact]
    public void Values_are_stored_whole_low_byte_first()
    {
        var r = Run.Program("""
            linear var pts: [u16; 8]
            data src: [u16; 8] = [0, 255, 256, $1234, $7FFF, $8000, $FFFF, 1]
            var back: [u16; 8]
            var c: u16
            fn main() {
                for i in 0..8 { pts[i] = src[i] }
                for i in 0..8 { back[i] = pts[i] }
                c = pts[3]
                pts[7] = 9
            }
            """);
        int pts = r.Address("pts");
        for (int i = 0; i < 7; i++)
        {
            Assert.Equal(Values[i] & 0xFF, r.Ram.Bytes[pts + 2 * i]);
            Assert.Equal(Values[i] >> 8, r.Ram.Bytes[pts + 2 * i + 1]);
            Assert.Equal(Values[i], r.U16At("back", i, 8));
        }
        Assert.Equal(9, At(r, "pts", 7));
        Assert.Equal(0x1234, r.U16("c"));
    }

    [Fact]
    public void Signed_and_pointer_elements()
    {
        var r = Run.Program("""
            linear var s: [i16; 3]
            linear var ps: [ptr u8; 2]
            var x: u8
            var neg: i16
            var sum: i16
            fn main() {
                s[0] = -1
                s[1] = -32768
                s[2] = 300
                for i in 0..3 { sum = sum + s[i] }
                neg = s[1]
                ps[1] = addr(x)
                var p: ptr u8 = ps[1]
                p[0] = 42
            }
            """);
        Assert.Equal(-1, (short)At(r, "s", 0));
        Assert.Equal(-32768, (short)At(r, "s", 1));
        Assert.Equal((short)(-1 - 32768 + 300), r.I16("sum"));
        Assert.Equal(-32768, r.I16("neg"));
        Assert.Equal(r.Address("x"), At(r, "ps", 1));
        Assert.Equal(42, r.U8("x"));
    }

    [Fact]
    public void Initial_values_and_data_are_interleaved()
    {
        var r = Run.Program("""
            linear data TABLE: [u16; 3] = [1, 256, $ABCD]
            linear var v: [u16; 2] = [$0102, $0304]
            var sum: u16
            var last: u16
            fn main() {
                for i in 0..3 { sum = sum + TABLE[i] }
                last = v[1]
            }
            """);
        int t = r.Address("TABLE");
        Assert.Equal(new byte[] { 1, 0, 0, 1, 0xCD, 0xAB }, r.Ram.Bytes[t..(t + 6)]);
        Assert.True(t < r.Result.LoadAddress + r.Result.Image.Length);
        Assert.Equal((1 + 256 + 0xABCD) & 0xFFFF, r.U16("sum"));
        Assert.Equal(0x0304, r.U16("last"));
        Assert.Equal(new byte[] { 2, 1, 4, 3 }, r.Ram.Bytes[r.Address("v")..(r.Address("v") + 4)]);
    }

    [Fact]
    public void Addr_gives_a_pointer_to_the_first_value()
    {
        var r = Run.Program("""
            linear var pts: [u16; 4]
            var w: u16
            var p: ptr u16
            fn main() {
                p = addr(pts)
                pts[2] = 1000
                w = p[2]
                p[1] = 777
                p = p + 2
                p[2] = 555
            }
            """);
        Assert.Equal(1000, r.U16("w"));
        Assert.Equal(777, At(r, "pts", 1));
        Assert.Equal(555, At(r, "pts", 3));
    }

    [Fact]
    public void Addr_of_a_linear_signed_array_is_a_ptr_i16()
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(
            "linear var s: [i16; 4]\nvar p: ptr u16\nfn main() { p = addr(s) }"));
        Assert.Contains("found ptr i16", e.Description);
    }

    [Fact]
    public void All_128_elements_are_reached()
    {
        var r = Run.Program("""
            linear var big: [u16; 128]
            var last: u16
            fn main() {
                for i in 0..128 { big[i] = (i as u16) << 8 | i }
                last = big[127]
            }
            """);
        for (int i = 0; i < 128; i++) Assert.Equal(i << 8 | i, At(r, "big", i));
        Assert.Equal(127 << 8 | 127, r.U16("last"));
    }

    [Fact]
    public void Compound_assignment_and_loop_registers()
    {
        var r = Run.Program("""
            linear var pts: [u16; 4]
            fn main() {
                for k in 0..4 using X { pts[k] = 250 }
                for k in 0..4 { pts[k] += 10 }
                pts[2] -= 300
                for k in 0..4 using Y { pts[k] += k }
            }
            """);
        Assert.Equal(260, At(r, "pts", 0));
        Assert.Equal(261, At(r, "pts", 1));
        Assert.Equal((260 - 300 + 2) & 0xFFFF, At(r, "pts", 2));
        Assert.Equal(263, At(r, "pts", 3));
    }

    [Fact]
    public void A_local_linear_array()
    {
        var r = Run.Program("""
            var out: u16
            fn f() -> u16 {
                linear var t: [u16; 3]
                for i in 0..3 { t[i] = 1000 + (i as u16) }
                return t[0] + t[1] + t[2]
            }
            fn main() { out = f() }
            """);
        Assert.Equal(3003, r.U16("out"));
    }

    [Theory]
    // Spec 11: a variable index costs one asl.
    [InlineData("w = pts[i]", "lda i|asl a|tax|lda pts,x|sta w|lda pts+1,x|sta w+1|rts")]
    // A constant index is a fixed offset.
    [InlineData("w = pts[3]", "lda pts+6|sta w|lda pts+7|sta w+1|rts")]
    [InlineData("pts[3] = w", "lda w|sta pts+6|lda w+1|sta pts+7|rts")]
    public void Code_shapes(string statement, string expected)
    {
        var r = Run.Program($$"""
            linear var pts: [u16; 8]
            var w: u16
            var i: u8
            fn main() { {{statement}} }
            """);
        Assert.Equal(expected, Code(r));
    }

    [Theory]
    [InlineData("linear var a: [u8; 4]\nfn main() { }", "'linear' is only for arrays of 16-bit values or structs: the elements of a [u8; 4] are one byte each")]
    [InlineData("linear var a: [bool; 4]\nfn main() { }", "the elements of a [bool; 4] are one byte each")]
    [InlineData("linear data a: [u8; 2] = [1, 2]\nfn main() { }", "the elements of a [u8; 2] are one byte each")]
    [InlineData("linear var a: u16\nfn main() { }", "'linear' is for arrays of 16-bit values or structs")]
    [InlineData("linear var a: [u16; 129]\nfn main() { }", "A linear array of 16-bit values has at most 128 elements, so that the doubled index fits in a register; 'a' has 129")]
    [InlineData("linear var a: [u16; 4]\nfn main() { a[4] = 1 }", "Index 4 is past the end of 'a' (length 4)")]
    [InlineData("linear var a: [u16; 2] = [1]\nfn main() { }", "Expected 2 values, found 1")]
    [InlineData("fn main() { linear data a: [u16; 2] = [1, 2] }", "'data' must be declared outside functions")]
    [InlineData("linear const a = 1\nfn main() { }", "'linear' goes before 'var' or 'data'")]
    public void Reports_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
    }
}
