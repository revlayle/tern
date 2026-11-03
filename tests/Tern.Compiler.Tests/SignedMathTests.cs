namespace Tern.Compiler.Tests;

/// <summary>lib/math additions of M10 (abs, signed multiply and divide, min/max/clamp, sin8/cos8) and mem.copy_back.</summary>
public class SignedMathTests
{
    private static readonly Library Math = new("import math\nimport mem", """
            math.abs8(0)
            math.abs16(0)
            math.muls8(0, 0)
            math.divs8(0, 1)
            mem.copy_back(0, 0, 0)
        """);

    private static readonly int[] Edges16 =
        new[] { 0, 1, 2, 127, 128, 255, 256, 257, 0x7FFF, 0x8000, 0x8001, 0xFF00, 0xFFFF }
            .Concat(Enumerable.Range(0, 20).Select(i => new Random(i).Next(0x10000))).ToArray();

    [Fact]
    public void Abs8_for_every_input()
    {
        lock (Math)
            for (int x = -128; x < 128; x++)
            {
                Math.Call("math.abs8", x & 0xFF);
                Assert.Equal(System.Math.Abs(x), Math.Cpu.A);
            }
    }

    [Fact]
    public void Abs16_on_edge_values()
    {
        lock (Math)
            foreach (int u in Edges16)
            {
                short x = (short)u;
                Math.Call("math.abs16", u & 0xFF, u >> 8);
                Assert.Equal(System.Math.Abs((int)x) & 0xFFFF, Math.AX);
            }
    }

    [Fact]
    public void Muls8_for_every_input()
    {
        lock (Math)
            for (int a = -128; a < 128; a++)
            for (int b = -128; b < 128; b++)
            {
                Math.Call("math.muls8", a & 0xFF, b & 0xFF);
                if (Math.AX != (a * b & 0xFFFF)) Assert.Fail($"muls8({a}, {b}) = {(short)Math.AX}, expected {a * b}");
            }
    }

    [Fact]
    public void Divs8_for_every_input_rounds_towards_zero_and_the_remainder_has_the_sign_of_a()
    {
        lock (Math)
            for (int a = -128; a < 128; a++)
            for (int b = -128; b < 128; b++)
            {
                if (b == 0) continue;
                Math.Call("math.divs8", a & 0xFF, b & 0xFF);
                int q = a / b, r = a % b;
                if (Math.Cpu.A != (q & 0xFF) || Math.U8("math.remainder8") != (r & 0xFF))
                    Assert.Fail($"divs8({a}, {b}) = {(sbyte)Math.Cpu.A} rem {(sbyte)Math.U8("math.remainder8")}, expected {q} rem {r}");
            }
    }

    /// <summary>Runs every pair (a, b) through the inline function and a plain Tern version, and counts disagreements.</summary>
    private static void CheckPairs(string inlineCall, string reference, string type = "u8")
    {
        var r = Run.Program($$"""
            import math
            var bad: u16
            var n: u16
            var rows: u16
            fn main() {
                for a in 0..=255 {
                    rows += 1
                    for b in 0..=255 {
                        var x: {{type}} = a as {{type}}
                        var y: {{type}} = b as {{type}}
                        var got: {{type}} = {{inlineCall}}
                        var want: {{type}} = {{type}}(0)
                        {{reference}}
                        n += 1
                        if got != want { bad += 1 }
                    }
                }
            }
            """.Replace($"{type}(0)", "0"), maxCycles: 40_000_000);
        Assert.Equal(256, r.U16("rows"));
        Assert.Equal(0, r.U16("n"));   // 65536 passes, wrapped to 16 bits
        Assert.Equal(0, r.U16("bad"));
    }

    [Fact]
    public void Min8_and_max8_for_every_pair()
    {
        CheckPairs("math.min8(x, y)", "if x < y { want = x } else { want = y }");
        CheckPairs("math.max8(x, y)", "if x > y { want = x } else { want = y }");
    }

    [Fact]
    public void Mins8_and_maxs8_for_every_pair()
    {
        CheckPairs("math.mins8(x, y)", "if x < y { want = x } else { want = y }", "i8");
        CheckPairs("math.maxs8(x, y)", "if x > y { want = x } else { want = y }", "i8");
    }

    [Fact]
    public void Clamp8_and_clamps8_for_every_input_and_range()
    {
        foreach (var (type, lo, hi) in new[] { ("u8", "40", "200"), ("u8", "0", "255"), ("u8", "7", "7"), ("i8", "-100", "90"), ("i8", "-128", "127"), ("i8", "-5", "-5") })
        {
            string fn = type == "u8" ? "clamp8" : "clamps8";
            var r = Run.Program($$"""
                import math
                var bad: u8
                var n: u16
                fn main() {
                    for a in 0..=255 {
                        var x: {{type}} = a as {{type}}
                        var got: {{type}} = math.{{fn}}(x, {{lo}}, {{hi}})
                        var want: {{type}} = x
                        if x < {{lo}} { want = {{lo}} }
                        if x > {{hi}} { want = {{hi}} }
                        n += 1
                        if got != want { bad += 1 }
                    }
                }
                """, maxCycles: 2_000_000);
            Assert.Equal(256, r.U16("n"));
            Assert.Equal(0, r.U8("bad"));
        }
    }

    [Fact]
    public void Two_inline_copies_in_one_program_do_not_clash()
    {
        var r = Run.Program("""
            import math
            var a: u8
            var b: u8
            var c: i8
            fn main() {
                a = math.min8(9, 4)
                b = math.max8(9, 4)
                c = math.mins8(-3, 2) + math.maxs8(-3, 2)
            }
            """);
        Assert.Equal(4, r.U8("a"));
        Assert.Equal(9, r.U8("b"));
        Assert.Equal(-1, r.I8("c"));
    }

    [Fact]
    public void Sin8_and_cos8_match_the_rounded_functions()
    {
        var r = Run.Program("""
            import math
            var s: [u8; 256]
            var c: [u8; 256]
            fn main() {
                for i in 0..=255 {
                    s[i] = math.sin8(i) as u8
                    c[i] = math.cos8(i) as u8
                }
            }
            """);
        for (int i = 0; i < 256; i++)
        {
            Assert.Equal((int)System.Math.Round(127 * System.Math.Sin(2 * System.Math.PI * i / 256)), r.I8("s", i));
            Assert.Equal((int)System.Math.Round(127 * System.Math.Cos(2 * System.Math.PI * i / 256)), r.I8("c", i));
        }
        Assert.Equal(127, r.I8("s", 64));
        Assert.Equal(-127, r.I8("s", 192));
        Assert.Equal(0, r.I8("s", 128));
    }

    [Fact]
    public void Sin8_adds_its_table_only_when_used()
    {
        var without = TernCompiler.Compile("import math\nvar a: u8\nfn main() { a = math.min8(a, 3) }");
        var with = TernCompiler.Compile("import math\nvar a: i8\nfn main() { a = math.sin8(a as u8) }");
        Assert.True(with.Image.Length - without.Image.Length >= 240);
        Assert.DoesNotContain("SINE", without.Listing);
    }

    [Fact]
    public void Copy_back_copies_overlapping_blocks_with_dst_above_src()
    {
        const int Base = 0x4000;
        lock (Math)
            foreach (int count in new[] { 0, 1, 2, 255, 256, 257, 511, 512, 513, 700 })
            foreach (int offset in new[] { 1, 2, 255, 256, 257, 1000 })
            {
                var expected = new byte[0x1000];
                for (int i = 0; i < expected.Length; i++) { expected[i] = (byte)(i * 7 + 3); Math.Ram.Bytes[Base + i] = expected[i]; }
                Array.Copy(expected, 0, expected, offset, count);   // Array.Copy handles overlap like memmove
                Math.Set16("mem.copy_back.dst", Base + offset);
                Math.Set16("mem.copy_back.src", Base);
                Math.Set16("mem.copy_back.count", count);
                Math.Call("mem.copy_back");
                for (int i = 0; i < expected.Length; i++)
                    if (Math.Ram.Bytes[Base + i] != expected[i]) Assert.Fail($"count {count}, offset {offset}: byte {i} is {Math.Ram.Bytes[Base + i]}, expected {expected[i]}");
            }
    }

    [Fact]
    public void Min8_in_a_handler_and_mul8_in_main_do_not_share_a_work_byte()
    {
        var r = Run.Program("""
            import math
            var lo: u8
            var prod: u16
            var bad: u8
            interrupt fn h() {
                lo = math.min8(lo + 7, 100)
                lo = math.maxs8(lo as i8, -3) as u8
            }
            fn main() {
                asm {
                    sei
                    lda #h.lo
                    sta $FFFE
                    lda #h.hi
                    sta $FFFF
                    cli
                }
                var want: u16 = 0
                for i in 0..=200 {
                    prod = math.mul8(i, 201)
                    if prod != want { bad += 1 }
                    want += 201
                }
            }
            """, maxCycles: 2_000_000, irqEvery: 97);
        Assert.True(r.Interrupts > 50);
        Assert.Equal(0, r.U8("bad"));
    }

    [Fact]
    public void Min8_from_main_and_a_handler_is_an_error()
    {
        var e = Assert.Throws<Syntax.CompileError>(() => TernCompiler.Compile("""
            import math
            var a: u8
            var b: u8
            interrupt fn h() { b = math.min8(b, 5) }
            fn main() { a = math.min8(a, 3) }
            """));
        Assert.Contains("a private variable of its module", e.Description);
    }
}
