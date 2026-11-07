namespace Tern.Compiler.Tests;

/// <summary>M18 F1: an element is the operand of adc, sbc, cmp, and, ora, eor without a temporary.</summary>
public class IndexedOperandTests
{
    private static readonly byte[] A = [0, 1, 255, 128, 127, 200, 56, 0, 255, 3, 100, 129, 64, 254, 2, 77];
    private static readonly byte[] B = [0, 255, 1, 128, 129, 56, 200, 255, 255, 3, 156, 127, 63, 2, 254, 77];
    private static readonly ushort[] U = [0, 1, 65535, 256, 255, 32768, 1000, 0, 65535, 300, 40000, 25, 4096, 65280, 2, 777];
    private static readonly ushort[] V = [0, 65535, 1, 256, 1, 32768, 64536, 65535, 65535, 300, 25536, 40000, 4095, 256, 65534, 777];

    private static string Arrays => $$"""
        var a: [u8; 16] = [{{string.Join(", ", A)}}]
        var b: [u8; 16] = [{{string.Join(", ", B)}}]
        var c: [u8; 16]
        var u: [u16; 16] = [{{string.Join(", ", U)}}]
        var v: [u16; 16] = [{{string.Join(", ", V)}}]
        var w: [u16; 16]
        var flags: [u8; 16]
        """;

    private static byte Apply(string op, int x, int y) => (byte)(op switch
    {
        "+" => x + y, "-" => x - y, "&" => x & y, "|" => x | y, _ => x ^ y,
    });

    private static string Mnemonic(string op) => op switch
    {
        "+" => "adc", "-" => "sbc", "&" => "and", "|" => "ora", _ => "eor",
    };

    [Theory]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("&")]
    [InlineData("|")]
    [InlineData("^")]
    public void An_element_is_the_right_operand_of_an_8_bit_operator(string op)
    {
        var r = Run.Program($$"""
            {{Arrays}}
            fn main() {
                var j: u8 = 15
                for i in 0..16 {
                    c[i] = a[i] {{op}} b[j]
                    j -= 1
                }
            }
            """);
        for (int i = 0; i < 16; i++) Assert.Equal(Apply(op, A[i], B[15 - i]), r.U8("c", i));
        Assert.Contains($"{Mnemonic(op)} b,y", r.Result.Listing);   // the loop holds X
        Assert.DoesNotContain("_t0", r.Result.Listing);
    }

    [Fact]
    public void An_element_whose_index_needs_code_goes_through_a_temporary()
    {
        var r = Run.Program($$"""
            {{Arrays}}
            fn main() {
                for i in 0..16 { c[i] = a[i] - b[15 - i] }
            }
            """);
        for (int i = 0; i < 16; i++) Assert.Equal((byte)(A[i] - B[15 - i]), r.U8("c", i));
    }

    [Theory]
    [InlineData("+")]
    [InlineData("&")]
    [InlineData("|")]
    [InlineData("^")]
    public void A_commutative_operator_takes_the_element_from_the_left(string op)
    {
        var r = Run.Program($$"""
            {{Arrays}}
            fn main() {
                for i in 0..16 { c[i] = b[i] {{op}} (a[i] >> 1) }
            }
            """);
        for (int i = 0; i < 16; i++) Assert.Equal(Apply(op, B[i], A[i] >> 1), r.U8("c", i));
        Assert.Contains($"{Mnemonic(op)} b,x", r.Result.Listing);
    }

    [Fact]
    public void A_subtraction_from_an_element_keeps_the_order()
    {
        var r = Run.Program($$"""
            {{Arrays}}
            fn main() {
                for i in 0..16 { c[i] = b[i] - (a[i] >> 1) }
            }
            """);
        for (int i = 0; i < 16; i++) Assert.Equal((byte)(B[i] - (A[i] >> 1)), r.U8("c", i));
    }

    [Fact]
    public void Pointer_elements_are_operands_through_y()
    {
        var r = Run.Program($$"""
            {{Arrays}}
            fn main() {
                var p: ptr u8 = addr(a)
                var q: ptr u8 = addr(b)
                for i in 0..16 {
                    c[i] = p[i] + q[i]
                    if p[i] == q[i] { flags[i] = 1 }
                    if p[i] < q[i] { flags[i] += 2 }
                }
            }
            """);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal((byte)(A[i] + B[i]), r.U8("c", i));
            Assert.Equal((A[i] == B[i] ? 1 : 0) + (A[i] < B[i] ? 2 : 0), r.U8("flags", i));
        }
        Assert.Contains("adc (main.q),y", r.Result.Listing);
        Assert.Contains("cmp (main.q),y", r.Result.Listing);
        Assert.DoesNotContain("_t0", r.Result.Listing);
    }

    [Fact]
    public void Elements_are_compared_in_place()
    {
        var r = Run.Program($$"""
            {{Arrays}}
            fn main() {
                for i in 0..16 {
                    var f: u8 = 0
                    var j: u8 = 15 - i
                    if a[i] == b[i] { f |= 1 }
                    if a[i] != b[j] { f |= 2 }
                    if a[i] < b[i] { f |= 4 }
                    if a[i] >= b[i] { f |= 8 }
                    if a[i] > b[i] { f |= 16 }
                    if a[i] <= b[i] { f |= 32 }
                    if (a[i] >> 1) < b[i] { f |= 64 }
                    if b[i] == (a[i] >> 1) { f |= 128 }
                    flags[i] = f
                }
            }
            """);
        for (int i = 0; i < 16; i++)
        {
            int x = A[i], y = B[i];
            int f = (x == y ? 1 : 0) | (x != B[15 - i] ? 2 : 0) | (x < y ? 4 : 0) | (x >= y ? 8 : 0)
                | (x > y ? 16 : 0) | (x <= y ? 32 : 0) | ((x >> 1) < y ? 64 : 0) | (y == (x >> 1) ? 128 : 0);
            Assert.Equal(f, r.U8("flags", i));
        }
        Assert.Contains("cmp b,x", r.Result.Listing);
        Assert.Contains("cmp b,y", r.Result.Listing);
        Assert.DoesNotContain("cmp main._t", r.Result.Listing);
    }

    [Fact]
    public void Signed_elements_are_compared_in_place()
    {
        sbyte[] s = [0, -128, 127, -1, 1, -128, 127, 5, -5, 100, -100, 0, 64, -64, 2, -2];
        sbyte[] t = [0, 127, -128, 1, -1, -128, 127, -5, 5, -100, 100, -1, -64, 64, 3, -3];
        var r = Run.Program($$"""
            var s: [i8; 16] = [{{string.Join(", ", s)}}]
            var t: [i8; 16] = [{{string.Join(", ", t)}}]
            var flags: [u8; 16]
            fn main() {
                for i in 0..16 {
                    var f: u8 = 0
                    if s[i] < t[i] { f |= 1 }
                    if s[i] >= t[i] { f |= 2 }
                    if s[i] > t[i] { f |= 4 }
                    if s[i] <= t[i] { f |= 8 }
                    flags[i] = f
                }
            }
            """);
        for (int i = 0; i < 16; i++)
        {
            int f = (s[i] < t[i] ? 1 : 0) | (s[i] >= t[i] ? 2 : 0) | (s[i] > t[i] ? 4 : 0) | (s[i] <= t[i] ? 8 : 0);
            Assert.Equal(f, r.U8("flags", i));
        }
        Assert.Contains("sbc t,x", r.Result.Listing);
    }

    [Fact]
    public void A_loop_counter_in_a_register_compares_through_a()
    {
        // cpx has no indexed form, so x < b[i] with x in X goes through A or a temporary.
        var r = Run.Program($$"""
            {{Arrays}}
            fn main() {
                for i in 0..16 using X { if i < b[i] { flags[i] = 1 } }
            }
            """);
        for (int i = 0; i < 16; i++) Assert.Equal(i < B[i] ? 1 : 0, r.U8("flags", i));
    }

    [Theory]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("&")]
    [InlineData("|")]
    [InlineData("^")]
    public void Sixteen_bit_elements_are_combined_byte_by_byte_into_an_element(string op)
    {
        var r = Run.Program($$"""
            {{Arrays}}
            fn main() {
                var j: u8 = 15
                for i in 0..16 {
                    w[i] = u[i] {{op}} v[j]
                    j -= 1
                }
            }
            """);
        for (int i = 0; i < 16; i++)
        {
            int x = U[i], y = V[15 - i];
            var want = (ushort)(op switch { "+" => x + y, "-" => x - y, "&" => x & y, "|" => x | y, _ => x ^ y });
            Assert.Equal(want, r.U16At("w", i, 16));
        }
        Assert.Contains($"{Mnemonic(op)} v,y", r.Result.Listing);
        Assert.Contains($"{Mnemonic(op)} v+16,y", r.Result.Listing);
        Assert.DoesNotContain("_t0", r.Result.Listing);
    }

    [Fact]
    public void Adjacent_sixteen_bit_elements_add_like_the_assembly()
    {
        // fib16's loop: lda fib-1,x / clc / adc fib-2,x / sta fib,x, then the high bytes.
        var r = Run.Program("""
            var fib: [u16; 24]
            fn main() {
                fib[1] = 1
                for i in 2..24 { fib[i] = fib[i - 1] + fib[i - 2] }
            }
            """);
        int f0 = 0, f1 = 1;
        for (int i = 2; i < 24; i++) { (f0, f1) = (f1, (ushort)(f0 + f1)); Assert.Equal(f1, r.U16At("fib", i, 24)); }
        Assert.Contains("adc fib-2,x", r.Result.Listing);
        Assert.Contains("sta fib,x", r.Result.Listing);
        Assert.DoesNotContain("_t0", r.Result.Listing);
    }

    [Fact]
    public void A_sixteen_bit_element_is_added_to_and_compared_with_a_variable()
    {
        var r = Run.Program($$"""
            {{Arrays}}
            var total: u16
            var largest: u16
            var count: u8
            fn main() {
                for i in 0..16 {
                    total += u[i]
                    if u[i] > largest { largest = u[i] }
                    if u[i] == v[i] { count += 1 }
                    if v[i] != total { count += 16 }
                }
            }
            """);
        ushort total = 0, largest = 0;
        int count = 0;
        for (int i = 0; i < 16; i++)
        {
            total += U[i];
            if (U[i] > largest) largest = U[i];
            if (U[i] == V[i]) count += 1;
            if (V[i] != total) count += 16;
        }
        Assert.Equal(total, r.U16("total"));
        Assert.Equal(largest, r.U16("largest"));
        Assert.Equal((byte)count, r.U8("count"));
        Assert.Contains("adc u,x", r.Result.Listing);
        Assert.Contains("sbc u+16,x", r.Result.Listing);
        Assert.DoesNotContain("_t0", r.Result.Listing);
    }

    [Theory]
    [InlineData("k[vi] = k[j] + w")]
    [InlineData("w = k[vi] + w")]
    [InlineData("if k[vi] < w { w = 0 }")]
    [InlineData("w = (w >> 2) + k[vi]")]
    public void A_volatile_index_of_a_16_bit_element_is_read_once(string statement)
    {
        // Review M18 finding 1: in-place shapes load the index for each byte (spec 4).
        var r = Run.Program($$"""
            volatile var vi: u8 = 1
            var j: u8 = 2
            var k: [u16; 4] = [1, 2, 3, 4]
            var w: u16 = 5
            fn main() { {{statement}} }
            """);
        Assert.Equal(1, r.Result.Listing.Split('\n').Count(l => l.Contains("ldx vi") || l.Contains("ldy vi") || l.Contains("lda vi")));
    }

    [Fact]
    public void An_index_that_reads_the_result_is_not_used_in_place()
    {
        // The low byte of n is written before the high bytes are read: they must use the old n.lo.
        var r = Run.Program($$"""
            {{Arrays}}
            var n: u16 = 3
            fn main() {
                n = u[n.lo] + v[n.lo]
            }
            """);
        Assert.Equal((ushort)(U[3] + V[3]), r.U16("n"));
    }
}
