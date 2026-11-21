namespace Tern.Compiler.Tests;

/// <summary>lib/bcd (spec 10.1): packed decimal counters, checked against C# arithmetic.</summary>
public class BcdTests
{
    private const int P = 0x4000, Q = 0x4010, Out = 0x4020;

    private static readonly Library Bcd = new("import bcd", """
            bcd.clear(0, 0)
            bcd.add(0, 0, 1)
            bcd.sub(0, 0, 1)
            bcd.add_byte(0, 1, 0)
            bcd.sub_byte(0, 1, 0)
            bcd.inc(0, 1)
            bcd.dec(0, 1)
            var l: bool = bcd.less(0, 0, 1)
            var e: bool = bcd.equal(0, 0, 1)
            var z: bool = bcd.is_zero(0, 1)
            bcd.digits(0, 0, 1)
            var t: u8 = bcd.text(0, 0, 1)
        """);

    private static long Pow10(int n) => (long)System.Math.Pow(10, n);

    private static void Store(int address, long value, int n)
    {
        for (int i = 0; i < n; i++)
        {
            int pair = (int)(value % 100);
            Bcd.Ram.Bytes[address + i] = (byte)(pair / 10 << 4 | pair % 10);
            value /= 100;
        }
    }

    private static long Load(int address, int n)
    {
        long v = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            byte b = Bcd.Ram.Bytes[address + i];
            Assert.True((b >> 4) <= 9 && (b & 15) <= 9, $"byte {b:X2} is not packed decimal");
            v = v * 100 + (b >> 4) * 10 + (b & 15);
        }
        return v;
    }

    private static readonly long[] Edges =
        [0, 1, 9, 10, 11, 99, 100, 101, 999, 1000, 9999, 10000, 12345, 99999, 100000, 999999, 1000000, 9999999, 12345678, 99999999, 99999998];

    /// <summary>Calls p(P, Q, n) style routines. The decimal flag must be clear again on return.</summary>
    private static void TwoOperand(string function, long a, long b, int n)
    {
        Store(P, a, n);
        Store(Q, b, n);
        Bcd.Set16($"bcd.{function}.p", P);
        Bcd.Set16($"bcd.{function}.q", Q);
        Bcd.Call($"bcd.{function}", x: n);
        Assert.False(Bcd.Cpu.D, "decimal mode left on");
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Add_and_sub_for_every_pair_of_two_digit_numbers()
    {
        lock (Bcd)
            for (int a = 0; a < 100; a++)
            for (int b = 0; b < 100; b++)
            {
                TwoOperand("add", a, b, 1);
                Assert.Equal((a + b) % 100, Load(P, 1));
                Assert.Equal(b, Load(Q, 1));
                TwoOperand("sub", a, b, 1);
                Assert.Equal(((a - b) % 100 + 100) % 100, Load(P, 1));
            }
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Add_and_sub_on_edge_values_for_every_width()
    {
        lock (Bcd)
            for (int n = 1; n <= 4; n++)
            {
                long mod = Pow10(2 * n);
                foreach (long a in Edges.Where(v => v < mod))
                foreach (long b in Edges.Where(v => v < mod))
                {
                    TwoOperand("add", a, b, n);
                    Assert.Equal((a + b) % mod, Load(P, n));
                    TwoOperand("sub", a, b, n);
                    Assert.Equal(((a - b) % mod + mod) % mod, Load(P, n));
                }
            }
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Less_equal_and_is_zero()
    {
        lock (Bcd)
            for (int n = 1; n <= 4; n++)
            {
                long mod = Pow10(2 * n);
                foreach (long a in Edges.Where(v => v < mod))
                foreach (long b in Edges.Where(v => v < mod))
                {
                    TwoOperand("less", a, b, n);
                    Assert.Equal(a < b, Bcd.Cpu.C);
                    Store(P, a, n); Store(Q, b, n);
                    Bcd.Set16("bcd.equal.p", P); Bcd.Set16("bcd.equal.q", Q);
                    Bcd.Call("bcd.equal", y: n);
                    Assert.Equal(a == b, Bcd.Cpu.C);
                }
                foreach (long a in Edges.Where(v => v < mod))
                {
                    Store(P, a, n);
                    Bcd.Set16("bcd.is_zero.p", P);
                    Bcd.Call("bcd.is_zero", y: n);
                    Assert.Equal(a == 0, Bcd.Cpu.Z);
                }
            }
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Add_byte_sub_byte_inc_and_dec()
    {
        lock (Bcd)
            for (int n = 1; n <= 4; n++)
            {
                long mod = Pow10(2 * n);
                foreach (long a in Edges.Where(v => v < mod))
                {
                    for (int v = 0; v < 100; v += 7)
                    {
                        byte packed = (byte)(v / 10 << 4 | v % 10);
                        Store(P, a, n);
                        Bcd.Set16("bcd.add_byte.p", P);
                        Bcd.Call("bcd.add_byte", a: packed, x: n);
                        Assert.Equal((a + v) % mod, Load(P, n));
                        Assert.False(Bcd.Cpu.D);
                        Store(P, a, n);
                        Bcd.Set16("bcd.sub_byte.p", P);
                        Bcd.Call("bcd.sub_byte", a: packed, x: n);
                        Assert.Equal(((a - v) % mod + mod) % mod, Load(P, n));
                        Assert.False(Bcd.Cpu.D);
                    }
                    Store(P, a, n);
                    Bcd.Set16("bcd.inc.p", P);
                    Bcd.Call("bcd.inc", x: n);
                    Assert.Equal((a + 1) % mod, Load(P, n));
                    Store(P, a, n);
                    Bcd.Set16("bcd.dec.p", P);
                    Bcd.Call("bcd.dec", x: n);
                    Assert.Equal(((a - 1) % mod + mod) % mod, Load(P, n));
                }
            }
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Clear_digits_and_text()
    {
        lock (Bcd)
            for (int n = 1; n <= 4; n++)
            {
                long mod = Pow10(2 * n);
                foreach (long a in Edges.Where(v => v < mod))
                {
                    Store(P, a, n);
                    Bcd.Set16("bcd.digits.dst", Out); Bcd.Set16("bcd.digits.p", P); Bcd.Set8("bcd.digits.n", n);
                    Bcd.Call("bcd.digits");
                    string all = a.ToString().PadLeft(2 * n, '0');
                    Assert.Equal(all, string.Concat(Enumerable.Range(0, 2 * n).Select(i => (char)(Bcd.Ram.Bytes[Out + i] - 0x30 + '0'))));

                    Bcd.Set16("bcd.text.dst", Out); Bcd.Set16("bcd.text.p", P); Bcd.Set8("bcd.text.n", n);
                    Bcd.Call("bcd.text");
                    string shown = a.ToString();
                    Assert.Equal(shown.Length, Bcd.Cpu.A);
                    Assert.Equal(shown, string.Concat(Enumerable.Range(0, shown.Length).Select(i => (char)(Bcd.Ram.Bytes[Out + i] - 0x30 + '0'))));

                    Bcd.Set16("bcd.clear.p", P); Bcd.Set8("bcd.clear.n", n);
                    Bcd.Call("bcd.clear");
                    Assert.Equal(0, Load(P, n));
                }
            }
    }

    [Trait("Platform", "c64")]
    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_bcd_add_in_main_survives_an_interrupt_handler_that_adds_in_binary()
    {
        var r = Run.Program("""
            import bcd
            var score: [u8; 3]
            var seven: [u8; 3]
            var acc: u8
            var diff: u8
            var ticks: u16
            interrupt fn frame() {
                acc += 57
                diff = diff - 3
                ticks += 1
            }
            fn main() {
                seven[0] = $07
                asm {
                    sei
                    lda #frame.lo
                    sta $FFFE
                    lda #frame.hi
                    sta $FFFF
                    cli
                }
                for _ in 0..200 {
                    for _ in 0..10 {
                        bcd.add(addr(score), addr(seven), 3)
                    }
                    bcd.inc(addr(score), 3)
                }
                asm { sei }
            }
            """, maxCycles: 5_000_000, irqEvery: 61);
        Assert.True(r.Interrupts > 500, $"{r.Interrupts} interrupts");
        // 2000 additions of 7 and 200 increments, each a whole number of decimal additions despite the interrupts.
        Assert.Equal(2000 * 7 + 200, r.U8("score") / 16 * 10 + r.U8("score") % 16 + 100 * (r.U8("score", 1) / 16 * 10 + r.U8("score", 1) % 16)
            + 10000 * (r.U8("score", 2) / 16 * 10 + r.U8("score", 2) % 16));
        // The handler's additions and subtractions were binary: it cleared decimal mode.
        Assert.Equal(r.U16("ticks") * 57 & 0xFF, r.U8("acc"));
        Assert.Equal(-r.U16("ticks") * 3 & 0xFF, r.U8("diff"));
        var handler = Listing.Instructions(r, "frame");
        Assert.Contains("cld", handler);
        Assert.Equal("rti", handler[^1]);
    }

    [Fact]
    public void An_unused_bcd_module_adds_no_bytes()
    {
        Assert.Equal(TernCompiler.Compile("fn main() { }").Image.Length, TernCompiler.Compile("import bcd\nfn main() { }").Image.Length);
    }
}
