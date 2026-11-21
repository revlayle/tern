namespace Tern.Compiler.Tests;

/// <summary>lib/wide (spec 10.1): 24- and 32-bit numbers on edge values and random values, against C#.</summary>
public class WideTests
{
    private const int P = 0x4000, Q = 0x4010, Out = 0x4020;

    private static readonly Library Wide = new("import wide", """
            wide.set24(0, 0)
            wide.copy24(0, 0)
            wide.add24(0, 0)
            wide.sub24(0, 0)
            wide.add24_u16(0, 0)
            wide.sub24_u16(0, 0)
            wide.inc24(0)
            var l24: bool = wide.less24(0, 0)
            var e24: bool = wide.equal24(0, 0)
            var z24: bool = wide.is_zero24(0)
            wide.mul24(0, 0)
            wide.div24(0, 0)
            var n24: u8 = wide.dec24(0, 0)
            wide.dec24z(0, 0, 1)
            wide.hex24(0, 0)
            wide.set32(0, 0)
            wide.copy32(0, 0)
            wide.add32(0, 0)
            wide.sub32(0, 0)
            wide.add32_u16(0, 0)
            wide.sub32_u16(0, 0)
            wide.inc32(0)
            var l32: bool = wide.less32(0, 0)
            var e32: bool = wide.equal32(0, 0)
            var z32: bool = wide.is_zero32(0)
            wide.mul32(0, 0)
            wide.div32(0, 0)
            var n32: u8 = wide.dec32(0, 0)
            wide.dec32z(0, 0, 1)
            wide.hex32(0, 0)
        """);

    private static readonly ulong[] Edges = new ulong[]
    {
        0, 1, 2, 9, 10, 99, 100, 127, 128, 254, 255, 256, 257, 511, 512, 9999, 10000, 65534, 65535, 65536, 65537, 99999, 100000,
        0x7FFFFF, 0x800000, 0xFFFFFE, 0xFFFFFF, 0x1000000, 0x1000001, 999999999, 1000000000, 0x7FFFFFFF, 0x80000000, 0x80000001,
        0xFFFF0000, 0xFFFFFFFE, 0xFFFFFFFF, 0x00FF00FF, 0xFF00FF00, 0x0000FF00, 0x00010000,
    }.Concat(Enumerable.Range(0, 12).Select(i => (ulong)new Random(i).NextInt64(0, 1L << 32))).ToArray();

    public static IEnumerable<object[]> Widths() => [[24], [32]];

    private static ulong Mask(int w) => (1UL << w) - 1;

    private static IEnumerable<ulong> Values(int w) => Edges.Select(v => v & Mask(w)).Distinct();

    private static void Store(int address, ulong value, int w)
    {
        for (int i = 0; i < w / 8; i++) Wide.Ram.Bytes[address + i] = (byte)(value >> (8 * i));
    }

    private static ulong Load(int address, int w)
    {
        ulong v = 0;
        for (int i = w / 8 - 1; i >= 0; i--) v = v << 8 | Wide.Ram.Bytes[address + i];
        return v;
    }

    private static void Pointers(string function, int w, int dst = P, int src = Q)
    {
        string name = $"wide.{function}{w}";
        string first = Wide.Result.Symbols.Keys.Contains($"{name}.p") ? "p" : "dst";
        string second = first == "p" ? "q" : "src";
        Wide.Set16($"{name}.{first}", dst);
        if (Wide.Result.Symbols.ContainsKey($"{name}.{second}")) Wide.Set16($"{name}.{second}", src);
    }

    private static void Binary(string function, int w, ulong a, ulong b)
    {
        Store(P, a, w);
        Store(Q, b, w);
        Pointers(function, w);
        Wide.Call($"wide.{function}{w}");
        Assert.False(Wide.Cpu.D, "decimal mode left on");
    }

    [Theory, MemberData(nameof(Widths))]
    public void Set_and_copy(int w)
    {
        lock (Wide)
        {
            foreach (ulong v in Values(w).Where(v => v <= 0xFFFF).Concat([0xFFFFUL]))
            {
                Store(P, ulong.MaxValue, w);
                Wide.Set16($"wide.set{w}.p", P);
                Wide.Set16($"wide.set{w}.v", (int)v);
                Wide.Call($"wide.set{w}");
                Assert.Equal(v, Load(P, w));
            }
            foreach (ulong v in Values(w))
            {
                Store(Q, v, w);
                Wide.Ram.Bytes[P + w / 8] = 0x55;
                Binary("copy", w, 0, v);
                Assert.Equal(v, Load(P, w));
                Assert.Equal(0x55, Wide.Ram.Bytes[P + w / 8]);
            }
        }
    }

    [Theory, MemberData(nameof(Widths))]
    public void Add_and_sub(int w)
    {
        lock (Wide)
            foreach (ulong a in Values(w))
            foreach (ulong b in Values(w))
            {
                Binary("add", w, a, b);
                Assert.Equal((a + b) & Mask(w), Load(P, w));
                Assert.Equal(b, Load(Q, w));
                Binary("sub", w, a, b);
                Assert.Equal((a - b) & Mask(w), Load(P, w));
            }
    }

    [Theory, MemberData(nameof(Widths))]
    public void Add_and_sub_a_u16(int w)
    {
        lock (Wide)
            foreach (ulong a in Values(w))
            foreach (ulong v in new ulong[] { 0, 1, 255, 256, 257, 0x7FFF, 0x8000, 0xFFFF })
            {
                Store(P, a, w);
                Wide.Set16($"wide.add{w}_u16.p", P);
                Wide.Set16($"wide.add{w}_u16.v", (int)v);
                Wide.Call($"wide.add{w}_u16");
                Assert.Equal((a + v) & Mask(w), Load(P, w));
                Store(P, a, w);
                Wide.Set16($"wide.sub{w}_u16.p", P);
                Wide.Set16($"wide.sub{w}_u16.v", (int)v);
                Wide.Call($"wide.sub{w}_u16");
                Assert.Equal((a - v) & Mask(w), Load(P, w));
            }
    }

    [Theory, MemberData(nameof(Widths))]
    public void Inc(int w)
    {
        lock (Wide)
            foreach (ulong a in Values(w).Concat([0xFFUL, 0xFFFFUL, 0xFFFFFFUL, Mask(w) - 1]))
            {
                Store(P, a & Mask(w), w);
                Wide.Set16($"wide.inc{w}.p", P);
                Wide.Call($"wide.inc{w}");
                Assert.Equal((a + 1) & Mask(w), Load(P, w));
            }
    }

    [Theory, MemberData(nameof(Widths))]
    public void Less_equal_and_is_zero(int w)
    {
        lock (Wide)
        {
            foreach (ulong a in Values(w))
            foreach (ulong b in Values(w))
            {
                Binary("less", w, a, b);
                Assert.Equal(a < b, Wide.Cpu.C);
                Binary("equal", w, a, b);
                Assert.Equal(a == b, Wide.Cpu.C);
            }
            foreach (ulong a in Values(w))
            {
                Store(P, a, w);
                Wide.Set16($"wide.is_zero{w}.p", P);
                Wide.Call($"wide.is_zero{w}");
                Assert.Equal(a == 0, Wide.Cpu.Z);
            }
            // Differences in each single byte are noticed.
            for (int i = 0; i < w / 8; i++)
            {
                Binary("equal", w, 0x0102030405UL & Mask(w), (0x0102030405UL & Mask(w)) ^ (1UL << (8 * i)));
                Assert.False(Wide.Cpu.C);
            }
        }
    }

    [Theory, MemberData(nameof(Widths))]
    public void Mul_gives_the_low_bits_of_the_product(int w)
    {
        lock (Wide)
            foreach (ulong a in Values(w))
            foreach (ulong b in Values(w))
            {
                Binary("mul", w, a, b);
                Assert.Equal((a * b) & Mask(w), Load(P, w));
                Assert.Equal(b, Load(Q, w));
            }
    }

    [Theory, MemberData(nameof(Widths))]
    public void Div_gives_quotient_and_remainder(int w)
    {
        lock (Wide)
            foreach (ulong a in Values(w))
            foreach (ulong b in Values(w).Where(b => b != 0))
            {
                Binary("div", w, a, b);
                Assert.Equal(a / b, Load(P, w));
                Assert.Equal(a % b, Load(Wide.Address($"wide.remainder{w}"), w));
            }
    }

    [Theory, MemberData(nameof(Widths))]
    public void Dec_and_hex(int w)
    {
        lock (Wide)
            foreach (ulong a in Values(w))
            {
                Store(P, a, w);
                Array.Fill(Wide.Ram.Bytes, (byte)0xEE, Out, 16);
                Wide.Set16($"wide.dec{w}.dst", Out);
                Wide.Set16($"wide.dec{w}.p", P);
                Wide.Call($"wide.dec{w}");
                string expected = a.ToString();
                Assert.Equal(expected.Length, Wide.Cpu.A);
                Assert.Equal(expected, Text(Out, expected.Length));
                Assert.Equal(0xEE, Wide.Ram.Bytes[Out + expected.Length]);
                Assert.False(Wide.Cpu.D);

                int digits = w == 32 ? 10 : 8;
                foreach (int width in new[] { 1, 2, 5, digits - 1, digits })
                {
                    Array.Fill(Wide.Ram.Bytes, (byte)0xEE, Out, 16);
                    Wide.Set16($"wide.dec{w}z.dst", Out);
                    Wide.Set16($"wide.dec{w}z.p", P);
                    Wide.Set8($"wide.dec{w}z.width", width);
                    Wide.Call($"wide.dec{w}z");
                    string padded = a.ToString().PadLeft(digits, '0')[(digits - width)..];
                    Assert.Equal(padded, Text(Out, width));
                    Assert.Equal(0xEE, Wide.Ram.Bytes[Out + width]);
                }

                Array.Fill(Wide.Ram.Bytes, (byte)0xEE, Out, 16);
                Wide.Set16($"wide.hex{w}.dst", Out);
                Wide.Set16($"wide.hex{w}.p", P);
                Wide.Call($"wide.hex{w}");
                string hex = a.ToString($"X{w / 4}");
                Assert.Equal(hex, string.Concat(Enumerable.Range(0, w / 4).Select(i => "0123456789ABCDEF"[Wide.Ram.Bytes[Out + i] is var c && c >= 0x30 ? c - 0x30 : c + 9])));
                Assert.Equal(0xEE, Wide.Ram.Bytes[Out + w / 4]);
            }
    }

    private static string Text(int address, int length) =>
        string.Concat(Enumerable.Range(0, length).Select(i => (char)(Wide.Ram.Bytes[address + i] - 0x30 + '0')));

    [Fact]
    public void Halves_are_plain_fields()
    {
        var r = Run.Program("""
            import wide
            var score: wide.U32
            var lo: u16
            var hi: u16
            var b: u8
            var c: wide.U24
            var top: u8
            fn main() {
                wide.set32(addr(score), $1234)
                wide.add32_u16(addr(score), $F000)
                score.hi = score.hi + 5
                lo = score.lo
                hi = score.hi
                b = score.lo.hi
                c.hi = 7
                top = c.hi
            }
            """);
        Assert.Equal((0x1234 + 0xF000) & 0xFFFF, r.U16("lo"));
        Assert.Equal(1 + 5, r.U16("hi"));
        Assert.Equal(((0x1234 + 0xF000) >> 8) & 0xFF, r.U8("b"));
        Assert.Equal(7, r.U8("top"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void The_arcade_example_compiles_with_every_new_module()
    {
        var path = AsmTests.FindExample("arcade.tern");
        var result = TernCompiler.Compile(File.ReadAllText(path), sourcePath: path);
        foreach (var name in new[] { "wide.add32_u16", "wide.dec32z", "bcd.add", "bcd.digits", "random.next8", "random.below", "math.mul8", "text.hex16" })
            Assert.Contains(name, Listing.Sections(result));
        Assert.Contains("math.SINE", Listing.Sections(result));
    }

    [Fact]
    public void An_unused_wide_module_adds_no_bytes()
    {
        Assert.Equal(TernCompiler.Compile("fn main() { }").Image.Length, TernCompiler.Compile("import wide\nfn main() { }").Image.Length);
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Only_the_widths_that_are_used_are_compiled()
    {
        var u24 = TernCompiler.Compile("import wide\nvar a: wide.U24\nvar b: wide.U24\nfn main() { wide.add24(addr(a), addr(b)) }");
        Assert.DoesNotContain("wide.add32", Listing.Sections(u24));
        Assert.DoesNotContain("wide.mul24", Listing.Sections(u24));
        Assert.Contains("wide.add24", Listing.Sections(u24));
    }
}
