namespace Tern.Compiler.Tests;

/// <summary>lib/text additions of M10: signed and fixed-width decimal. Spec 10 (built-ins and the standard library).</summary>
public class TextSignedTests
{
    private const int Buf = 0x4000;

    private static readonly Library Text = new("import text", """
            text.dec8s(0, 0)
            text.dec16s(0, 0)
            text.dec8z(0, 0, 1)
            text.dec16z(0, 0, 1)
        """);

    private static string Screen(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(i => (char)(Text.Ram.Bytes[Buf + i] == 0x2D ? '-' : Text.Ram.Bytes[Buf + i] - 0x30 + '0')));

    private static void Clear() => Array.Fill(Text.Ram.Bytes, (byte)0xEE, Buf, 16);

    [Trait("Platform", "c64")]
    [Fact]
    public void Dec8s_for_every_value()
    {
        lock (Text)
            for (int v = -128; v < 128; v++)
            {
                Clear();
                Text.Set16("text.dec8s.dst", Buf);
                Text.Set8("text.dec8s.value", v);
                Text.Call("text.dec8s");
                string expected = v.ToString();
                Assert.Equal(expected.Length, Text.Cpu.A);
                Assert.Equal(expected, Screen(expected.Length));
                Assert.Equal(0xEE, Text.Ram.Bytes[Buf + expected.Length]);   // nothing written past the end
            }
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Dec16s_on_edge_values()
    {
        lock (Text)
            foreach (int v in new[] { 0, 1, -1, 9, 10, -10, 99, 100, 255, 256, -255, -256, 9999, 10000, -10000, 32767, -32767, -32768, 12345, -12345 }
                         .Concat(Enumerable.Range(0, 40).Select(i => new Random(i).Next(-32768, 32768))))
            {
                Clear();
                Text.Set16("text.dec16s.dst", Buf);
                Text.Set16("text.dec16s.value", v & 0xFFFF);
                Text.Call("text.dec16s");
                string expected = v.ToString();
                Assert.Equal(expected.Length, Text.Cpu.A);
                Assert.Equal(expected, Screen(expected.Length));
                Assert.Equal(0xEE, Text.Ram.Bytes[Buf + expected.Length]);
            }
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Dec16z_writes_exactly_width_digits_with_leading_zeros()
    {
        lock (Text)
            foreach (int v in new[] { 0, 1, 9, 10, 99, 100, 255, 256, 999, 1000, 9999, 10000, 12345, 40000, 65535 })
            for (int width = 1; width <= 5; width++)
            {
                Clear();
                Text.Set16("text.dec16z.dst", Buf);
                Text.Set16("text.dec16z.value", v);
                Text.Set8("text.dec16z.width", width);
                Text.Call("text.dec16z");
                string expected = v.ToString("D5")[(5 - width)..];
                if (v >= System.Math.Pow(10, width)) continue;   // too wide: unspecified, but nothing is written past the end
                Assert.Equal(expected, Screen(width));
                Assert.Equal(0xEE, Text.Ram.Bytes[Buf + width]);
            }
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Dec8z_writes_exactly_width_digits_with_leading_zeros()
    {
        lock (Text)
            for (int v = 0; v < 256; v++)
            for (int width = 1; width <= 3; width++)
            {
                Clear();
                Text.Set16("text.dec8z.dst", Buf);
                Text.Set8("text.dec8z.value", v);
                Text.Set8("text.dec8z.width", width);
                Text.Call("text.dec8z");
                if (v < System.Math.Pow(10, width)) Assert.Equal(v.ToString("D3")[(3 - width)..], Screen(width));
                Assert.Equal(0xEE, Text.Ram.Bytes[Buf + width]);
            }
    }
}
