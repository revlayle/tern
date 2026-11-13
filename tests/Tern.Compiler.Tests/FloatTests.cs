using Tern.Compiler.Syntax;
using System.Text;

namespace Tern.Compiler.Tests;

/// <summary>
/// lib/float (M24, spec 10.1): every routine against <see cref="FloatModel"/>, bit for bit, over edge
/// values (0, ±1, the smallest and largest values, ties in rounding, overflow, results that become 0)
/// and random values.
/// </summary>
public class FloatTests
{
    private const int P = 0x4000, Q = 0x4010, Out = 0x4020;

    private static readonly Library Float = new("import float", """
            float.add(0, 0)
            float.sub(0, 0)
            float.mul(0, 0)
            float.div(0, 0)
            float.neg(0)
            float.abs(0)
            var c: i8 = float.cmp(0, 0)
            var z: bool = float.is_zero(0)
            float.copy(0, 0)
            float.from_u16(0, 0)
            float.from_i16(0, 0)
            var u: u16 = float.to_u16(0)
            var s: i16 = float.to_i16(0)
            u = float.round_u16(0)
            s = float.round_i16(0)
            float.trunc(0)
            float.floor(0)
            var n: u8 = float.dec(0, 0, 0)
        """);

    private static byte[] B(double x) => FloatModel.Bytes(x);

    private static readonly byte[] Smallest = [0, 0, 0, 1];
    private static readonly byte[] Largest = FloatModel.Largest(false);

    /// <summary>Edge values, then random ones of every magnitude.</summary>
    private static readonly byte[][] Values = new[]
    {
        B(0), B(1), B(-1), B(0.5), B(-0.5), B(2), B(-2), B(3), B(-3), B(1.5), B(2.5), B(-2.5), B(0.75), B(10), B(-10), B(0.1), B(-0.1),
        B(1.0 / 3), B(123.456), B(-7.25), B(1e-30), B(-1e30), B(1e38), B(-1e-38),
        B(32767), B(32767.5), B(32768), B(-32768), B(-32768.5), B(65535), B(65535.5), B(65536), B(8388608), B(8388607.5), B(16777215),
        Smallest, FloatModel.Neg(Smallest), [0, 0, 0x40, 1], Largest, FloatModel.Largest(true), [0xFE, 0xFF, 0x7F, 0xFF],
        [1, 0, 0, 0x81], [0xFF, 0xFF, 0x7F, 0x80], [0xFF, 0xFF, 0xFF, 0x80], [1, 0, 0, 0x80], [0, 0, 0, 0x68], [0, 0, 0, 0x69],
    }.Concat(Enumerable.Range(0, 16).Select(i =>
    {
        var r = new Random(i);
        return new[] { (byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(1, 256) };
    })).Concat(Enumerable.Range(0, 8).Select(i =>
    {
        // Close to 1, where additions and subtractions cancel.
        var r = new Random(100 + i);
        return new[] { (byte)r.Next(256), (byte)r.Next(256), (byte)(r.Next(128) | (i & 1) << 7), (byte)(0x7F + r.Next(4)) };
    })).ToArray();

    private static void Store(int address, byte[] value) => value.CopyTo(Float.Ram.Bytes, address);

    private static byte[] Load(int address) => Float.Ram.Bytes[address..(address + 4)];

    private static string Show(byte[] b) => $"{b[0]:X2} {b[1]:X2} {b[2]:X2} {b[3]:X2} ({FloatModel.ToDouble(b):R})";

    private static bool Overflow { get => Float.U8("float.overflow") != 0; set => Float.Set8("float.overflow", value ? 1 : 0); }

    /// <summary>Calls float.name(p, q) on copies of a and b at P and Q. Returns the cycles.</summary>
    private static long Binary(string name, byte[] a, byte[] b)
    {
        Store(P, a);
        Store(Q, b);
        Overflow = false;
        Float.Set16($"float.{name}.p", P);
        Float.Set16($"float.{name}.q", Q);
        long cycles = Float.Call($"float.{name}");
        Assert.Equal(b, Load(Q));
        return cycles;
    }

    private static long Unary(string name, byte[] a)
    {
        Store(P, a);
        Overflow = false;
        Float.Set16($"float.{name}.p", P);
        return Float.Call($"float.{name}");
    }

    public static IEnumerable<object[]> Operations() => [["add"], ["sub"], ["mul"], ["div"]];

    private static FloatModel.Result Model(string name, byte[] a, byte[] b) => name switch
    {
        "add" => FloatModel.Add(a, b),
        "sub" => FloatModel.Sub(a, b),
        "mul" => FloatModel.Mul(a, b),
        _ => FloatModel.Div(a, b),
    };

    private static void Check(string name, byte[] a, byte[] b)
    {
        var expected = Model(name, a, b);
        Binary(name, a, b);
        var actual = Load(P);
        if (!expected.Bytes.SequenceEqual(actual) || expected.Overflow != Overflow)
            Assert.Fail($"{name}({Show(a)}, {Show(b)}): expected {Show(expected.Bytes)}{(expected.Overflow ? " overflow" : "")}, " +
                $"found {Show(actual)}{(Overflow ? " overflow" : "")}");
    }

    [Theory, MemberData(nameof(Operations))]
    public void Arithmetic_matches_the_model(string name)
    {
        lock (Float)
            foreach (var a in Values)
            foreach (var b in Values)
                Check(name, a, b);
    }

    [Theory, MemberData(nameof(Operations))]
    public void Arithmetic_matches_the_model_on_random_values(string name)
    {
        var r = new Random(name.Length * 7 + name[0]);
        lock (Float)
            for (int i = 0; i < 1500; i++)
            {
                // Mostly close exponents, where the mantissas meet.
                var a = new[] { (byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(100, 160) };
                var b = new[] { (byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256), (byte)(a[3] + r.Next(-30, 31)) };
                Check(name, a, b);
            }
    }

    [Fact]
    public void Ties_round_away_from_zero()
    {
        lock (Float)
        {
            double ulp = Math.Pow(2, -23);          // of values from 1 to 2
            // 1 + half an ulp, and 1 + 1.5 ulp: both up.
            Check("add", B(1), B(ulp / 2));
            Assert.Equal(B(1 + ulp), Load(P));
            Check("add", B(1), B(ulp * 1.5));
            Assert.Equal(B(1 + 2 * ulp), Load(P));
            Check("add", B(-1), B(-ulp / 2));
            Assert.Equal(B(-1 - ulp), Load(P));
            // 1 - 2^-25 is halfway between 1 - 2^-24 and 1: to 1.
            Check("sub", B(1), B(Math.Pow(2, -25)));
            Assert.Equal(B(1), Load(P));
            // 4097 * 4097 = 2^24 + 2^13 + 1 has 25 bits: the last one is a half.
            Check("mul", B(4097), B(4097));
            Assert.Equal(B(16785410), Load(P));
            Check("mul", B(-4097), B(4097));
            Assert.Equal(B(-16785410), Load(P));
            // Below a half: down. Far below the guard byte, with a borrow: the sticky bit decides.
            Check("add", B(1), B(ulp / 2 - Math.Pow(2, -40)));
            Assert.Equal(B(1), Load(P));
            // 2 - 2^-24 alone is a tie (up to 2); the lowest bit of 2^-24 * (1 + 2^-23) is shifted past the
            // guard byte, and only the sticky bit keeps the result below the half.
            Check("sub", B(2), B(Math.Pow(2, -24)));
            Assert.Equal(B(2), Load(P));
            Check("sub", B(2), B(Math.Pow(2, -24) * (1 + ulp)));
            Assert.Equal(B(2 - ulp), Load(P));
        }
    }

    [Fact]
    public void Overflow_gives_the_largest_value_and_results_too_small_become_0()
    {
        lock (Float)
        {
            Check("add", Largest, Largest);
            Assert.Equal(Largest, Load(P));
            Assert.True(Overflow);
            Check("mul", FloatModel.Largest(true), B(2));
            Assert.Equal(FloatModel.Largest(true), Load(P));
            Assert.True(Overflow);
            Check("div", B(1e30), B(1e-30));
            Assert.True(Overflow);
            Check("div", B(-3), B(0));
            Assert.Equal(FloatModel.Largest(true), Load(P));
            Assert.True(Overflow);
            Check("div", B(0), B(0));
            Assert.Equal(Largest, Load(P));
            Check("mul", Smallest, B(0.5));
            Assert.Equal(new byte[4], Load(P));
            Assert.False(Overflow);
            Check("div", Smallest, B(3));
            Assert.Equal(new byte[4], Load(P));
            Check("sub", [0, 0, 0x40, 1], Smallest);       // 1.5 * smallest - smallest
            Assert.Equal(new byte[4], Load(P));
            Check("sub", B(1.25), B(1.25));
            Assert.Equal(new byte[4], Load(P));
            // The flag stays set until cleared.
            Float.Set8("float.overflow", 1);
            Store(P, B(1));
            Store(Q, B(1));
            Float.Set16("float.add.p", P);
            Float.Set16("float.add.q", Q);
            Float.Call("float.add");
            Assert.True(Overflow);
        }
    }

    [Fact]
    public void Neg_abs_copy_and_is_zero()
    {
        lock (Float)
            foreach (var a in Values)
            {
                Unary("neg", a);
                Assert.Equal(FloatModel.Neg(a), Load(P));
                Unary("abs", a);
                Assert.Equal(FloatModel.Abs(a), Load(P));
                Unary("is_zero", a);
                Assert.Equal(a[3] == 0, Float.Cpu.Z);
                Store(Q, a);
                Store(P, [0x55, 0x55, 0x55, 0x55]);
                Float.Ram.Bytes[P + 4] = 0x66;
                Float.Set16("float.copy.dst", P);
                Float.Set16("float.copy.src", Q);
                Float.Call("float.copy");
                Assert.Equal(a, Load(P));
                Assert.Equal(0x66, Float.Ram.Bytes[P + 4]);
            }
    }

    [Fact]
    public void Cmp_orders_values()
    {
        var values = Values.Append([0x12, 0x34, 0x80, 0x00]).ToArray();     // a 0 with the sign bit set
        lock (Float)
            foreach (var a in values)
            foreach (var b in values)
            {
                Binary("cmp", a, b);
                Assert.True(FloatModel.Cmp(a, b) == (sbyte)Float.Cpu.A, $"cmp({Show(a)}, {Show(b)})");
            }
    }

    [Fact]
    public void From_u16_and_from_i16_are_exact()
    {
        lock (Float)
        {
            for (int v = 0; v < 65536; v += v < 1100 ? 1 : 97)
                foreach (int x in new[] { v, 65535 - v })
                {
                    Store(P, [0x55, 0x55, 0x55, 0x55]);
                    Float.Set16("float.from_u16.p", P);
                    Float.Set16("float.from_u16.v", x);
                    Float.Call("float.from_u16");
                    Assert.True(FloatModel.FromInt(x).Bytes.SequenceEqual(Load(P)), $"from_u16({x})");
                    int s = (short)x;
                    Float.Set16("float.from_i16.p", P);
                    Float.Set16("float.from_i16.v", x);
                    Float.Call("float.from_i16");
                    Assert.True(FloatModel.FromInt(s).Bytes.SequenceEqual(Load(P)), $"from_i16({s})");
                }
        }
    }

    private static readonly byte[][] Conversions = Values.Concat(new[]
    {
        0, -1, 1, 0.4, -0.4, 0.5, -0.5, 0.9999, -0.9999, 1.5, -1.5, 2.5, -2.5, 3.5, 100.5, -100.5, 32766.5, 32767.4, 32767.5, -32767.5,
        -32768.4, -32768.5, -32769, 65534.5, 65535.4, 65535.5, 65536, -65535, 1e10, -1e10, 1e-10, 255.5, 256.5,
    }.Select(B)).ToArray();

    [Theory]
    [InlineData("to_u16", false, 0, 65535)]
    [InlineData("round_u16", true, 0, 65535)]
    [InlineData("to_i16", false, -32768, 32767)]
    [InlineData("round_i16", true, -32768, 32767)]
    public void Conversions_to_integers(string name, bool round, int min, int max)
    {
        lock (Float)
            foreach (var a in Conversions)
            {
                Unary(name, a);
                var (value, overflow) = FloatModel.ToInt(a, round, min, max);
                int actual = min < 0 ? (short)Float.AX : Float.AX;
                Assert.True(value == actual && overflow == Overflow, $"{name}({Show(a)}): expected {value}{(overflow ? " overflow" : "")}, found {actual}{(Overflow ? " overflow" : "")}");
            }
    }

    [Fact]
    public void Trunc_and_floor()
    {
        var values = Conversions.Concat(new[] { -2.7, 2.7, -3, -0.25, -8388607.5, 8388607.5, -8388608, 8388608.5 * 2, -1e20, 1e-20, -1e-20 }.Select(B));
        lock (Float)
            foreach (var a in values)
            {
                Unary("trunc", a);
                Assert.True(FloatModel.Trunc(a).SequenceEqual(Load(P)), $"trunc({Show(a)}): found {Show(Load(P))}");
                Unary("floor", a);
                Assert.True(FloatModel.Floor(a).SequenceEqual(Load(P)), $"floor({Show(a)}): found {Show(Load(P))}");
                Assert.False(Overflow);
            }
        lock (Float)
        {
            Unary("floor", B(-2.7));
            Assert.Equal(B(-3), Load(P));
            Unary("floor", B(-0.5));
            Assert.Equal(B(-1), Load(P));
            Unary("floor", B(-16777215.0 / 2));      // -8388607.5: the magnitude carries into a new bit
            Assert.Equal(B(-8388608), Load(P));
        }
    }

    private static string Text(int count) =>
        new(Float.Ram.Bytes[Out..(Out + count)].Select(c => (char)c).ToArray());

    [Fact]
    public void Dec_writes_fixed_point_text()
    {
        var values = Conversions.Concat(new[] { 0.005, -0.005, 0.0049, 9.995, 99.5, -0.04, 1e-9, 5e-10, 4.9e-10, 3.14159265 }.Select(B));
        lock (Float)
            foreach (var a in values)
                for (int d = 0; d <= 10; d++)
                {
                    Store(P, a);
                    Array.Fill(Float.Ram.Bytes, (byte)0xEE, Out, 64);
                    Float.Set16("float.dec.dst", Out);
                    Float.Set16("float.dec.p", P);
                    Float.Set8("float.dec.decimals", d);
                    Float.Call("float.dec", maxCycles: 200_000);
                    string expected = FloatModel.Dec(a, d);
                    int n = Float.Cpu.A;
                    Assert.True(expected == Text(n), $"dec({Show(a)}, {d}): expected {expected}, found {Text(n)}");
                    Assert.Equal(0xEE, Float.Ram.Bytes[Out + n]);
                    Assert.False(Float.Cpu.D);
                }
        Assert.Equal("39", FloatModel.Dec(Largest, 0).Length.ToString());
    }

    [Fact]
    public void Dec_examples()
    {
        lock (Float)
        {
            foreach (var (x, d, text) in new[]
            {
                (0.0, 2, "0.00"), (1.0, 0, "1"), (-2.5, 0, "-3"), (-2.5, 1, "-2.5"), (3.14159265, 4, "3.1416"), (-0.004, 2, "0.00"),
                (-0.125, 2, "-0.13"), (0.125, 2, "0.13"), (100.0, 3, "100.000"), (1e10, 2, "10000000000.00"), (0.5, 0, "1"),
            })
            {
                Store(P, B(x));
                Float.Set16("float.dec.dst", Out);
                Float.Set16("float.dec.p", P);
                Float.Set8("float.dec.decimals", d);
                Float.Call("float.dec", maxCycles: 200_000);
                Assert.Equal(text, Text(Float.Cpu.A));
            }
        }
    }

    [Fact]
    public void Of_matches_the_model()
    {
        double[] xs =
        [
            0, 1, -1, 0.5, 9.81, -9.81, 0.1, 1.0 / 3, Math.PI, -Math.E, 1e-38, 2.9e-39, 2.93e-39, 1e-45, 1.7e38, -1.7e38, 1.70141173e38,
            16777215, 16777216, 16777217, 33554431, 1 + Math.Pow(2, -24), 1 + 3 * Math.Pow(2, -24), 123456.789, -0.0001, 1e-300, 1e300 * 0,
            Math.Pow(2, -128) * (1 - Math.Pow(2, -25)), Math.Pow(2, -128) * (1 - Math.Pow(2, -26)), Math.Pow(2, 127) * (1 - Math.Pow(2, -24)), Math.Pow(2, 127) * (1 - Math.Pow(2, -24) - Math.Pow(2, -30)),
        ];
        var source = new StringBuilder("import float\n");
        for (int i = 0; i < xs.Length; i++)
            source.Append($"data d{i} = float.F32 {{ bytes = float.of({xs[i]:R}) }}\n".Replace("E", "e"));
        source.Append("var t: [u8; 1]\nfn main() {\n");
        for (int i = 0; i < xs.Length; i++) source.Append($"    t[0] = d{i}.bytes[0]\n");
        source.Append("}\n");
        var r = Run.Program(source.ToString());
        for (int i = 0; i < xs.Length; i++)
        {
            var expected = FloatModel.Of(xs[i])!;
            var actual = r.Ram.Bytes[r.Address($"d{i}")..(r.Address($"d{i}") + 4)];
            Assert.True(expected.SequenceEqual(actual), $"of({xs[i]:R}): expected {Show(expected)}, found {Show(actual)}");
        }
    }

    [Theory]
    [InlineData("1.8e38")]
    [InlineData("-1e300")]
    [InlineData("1.7014118e38")]
    public void Of_a_value_too_large_is_an_error(string x)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile($"import float\ndata g = float.F32 {{ bytes = float.of({x}) }}\nfn main() {{ }}\n"));
        Assert.Contains("float.tern", e.Pos.File ?? "");
        Assert.Contains("called from", e.Description);
    }

    [Fact]
    public void A_constant_in_a_var_and_in_code()
    {
        var r = Run.Program("""
            import float
            var g: float.F32 = float.F32 { bytes = float.of(9.81) }
            var t: float.F32 = float.F32 { bytes = float.of(2.0) }
            var v: float.F32
            var n: u8
            var text: [u8; 20]
            fn main() {
                float.from_i16(addr(v), -3)
                float.mul(addr(v), addr(g))
                float.mul(addr(v), addr(t))
                n = float.dec(addr(text), addr(v), 2)
            }
            """);
        Assert.Equal("-58.86", new string(r.Ram.Bytes[r.Address("text")..(r.Address("text") + r.U8("n"))].Select(c => (char)c).ToArray()));
    }

    /// <summary>
    /// Best and worst cycles of each routine over the test values, including jsr and rts: the numbers in
    /// docs/status.md ("Library cycle counts"). A change in any of them fails here.
    /// </summary>
    [Fact]
    public void Cycle_counts()
    {
        var counts = new Dictionary<string, (long Min, long Max)>();
        void Add(string name, long c) => counts[name] = counts.TryGetValue(name, out var m) ? (Math.Min(m.Min, c), Math.Max(m.Max, c)) : (c, c);
        lock (Float)
        {
            foreach (var a in Values)
            {
                foreach (var b in Values)
                    foreach (var op in new[] { "add", "sub", "mul", "div", "cmp" })
                        Add(op, Binary(op, a, b));
                foreach (var op in new[] { "neg", "abs", "is_zero", "to_u16", "round_u16", "to_i16", "round_i16", "trunc", "floor" })
                    Add(op, Unary(op, a));
                Store(P, a);
                Float.Set16("float.dec.dst", Out);
                Float.Set16("float.dec.p", P);
                Float.Set8("float.dec.decimals", 2);
                long dec = Float.Call("float.dec", maxCycles: 200_000);
                Add("dec", dec);
                if (Math.Abs(FloatModel.ToDouble(a)) < 65536) Add("dec below 65536", dec);
            }
            foreach (int v in new[] { 0, 1, 2, 255, 256, 1000, 32767, 32768, 65535 })
            {
                Float.Set16("float.from_u16.p", P);
                Float.Set16("float.from_u16.v", v);
                Add("from_u16", Float.Call("float.from_u16"));
                Float.Set16("float.from_i16.p", P);
                Float.Set16("float.from_i16.v", v);
                Add("from_i16", Float.Call("float.from_i16"));
            }
        }
        string actual = string.Join(", ", counts.Select(c => $"{c.Key} {c.Value.Min}-{c.Value.Max}"));
        Assert.Equal("add 108-961, sub 116-951, mul 153-2490, div 218-4418, cmp 58-138, neg 22-36, abs 27-27, is_zero 19-19, " +
            "to_u16 158-461, round_u16 158-487, to_i16 164-469, round_i16 164-495, trunc 94-306, floor 94-394, dec 1245-91222, " +
            "dec below 65536 1245-7692, from_u16 102-509, from_i16 106-524", actual);
    }

    /// <summary>
    /// examples/floats.tern: the screen holds the values the model computes with the same steps, each
    /// placed so that its point is in the column the example gives.
    /// </summary>
    [Fact]
    public void Example_prints_computed_values()
    {
        string path = AsmTests.FindExample("floats.tern");
        var r = Run.Program(File.ReadAllText(path), sourcePath: path);
        string Row(int row) => new(r.Ram.Bytes[(0x0400 + row * 40)..(0x0400 + row * 40 + 40)].Select(c => (char)(c < 0x20 ? c + 0x40 : c)).ToArray());
        static string At(string line, int point, byte[] x, int decimals)
        {
            string s = FloatModel.Dec(x, decimals);
            int start = point + decimals + 1 - s.Length;
            return line[..start] + s + line[(start + s.Length)..];
        }
        byte[] g = B(-9.81), dt = B(0.05), y = B(0), v = B(12.5), t = B(0);
        Assert.Equal("BALL THROWN UP AT 12.5 M/S".PadRight(40), Row(0));
        for (int i = 0; i < 11; i++)
        {
            string line = new(' ', 40);
            line = At(At(At(line, 5, t, 2), 14, y, 2), 23, v, 2);
            Assert.Equal(line, Row(3 + i));
            for (int k = 0; k < 5; k++)
            {
                y = FloatModel.Add(y, FloatModel.Mul(v, dt).Bytes).Bytes;
                v = FloatModel.Add(v, FloatModel.Mul(g, dt).Bytes).Bytes;
                t = FloatModel.Add(t, dt).Bytes;
            }
        }
        Assert.Equal(new string(' ', 40), Row(14));
        Assert.Equal("1 / 3 =        0.3333333", Row(17).TrimEnd());
        var area = FloatModel.Mul(FloatModel.Mul(B(Math.PI), B(2.5)).Bytes, B(2.5)).Bytes;
        Assert.Equal("AREA, R = 2.5:  " + FloatModel.Dec(area, 4), Row(18).TrimEnd());
        Assert.Equal("AREA, R = 2.5:  19.6350", Row(18).TrimEnd());
        Assert.Equal("ROUND(-2.5) = -3", Row(19).TrimEnd());
        Assert.False(r.U8("float.overflow") != 0);
    }
}
