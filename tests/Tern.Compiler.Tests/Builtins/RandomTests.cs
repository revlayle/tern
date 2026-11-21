namespace Tern.Compiler.Tests;

/// <summary>lib/random (spec 10.1): xorshift generators checked against C# and for their full period.</summary>
public class RandomTests
{
    private static byte Step8(byte x)
    {
        x ^= (byte)(x << 3);
        x ^= (byte)(x >> 5);
        x ^= (byte)(x << 4);
        return x;
    }

    private static ushort Step16(ushort x)
    {
        x ^= (ushort)(x << 7);
        x ^= (ushort)(x >> 9);
        x ^= (ushort)(x << 8);
        return x;
    }

    [Fact]
    public void Next8_has_a_period_of_255_and_matches_the_reference()
    {
        var r = Run.Program("""
            import random
            var period: u16
            var v: u8
            fn main() {
                v = random.next8()
                period = 1
                while v != 1 {
                    v = random.next8()
                    period += 1
                }
            }
            """, maxCycles: 500_000);
        // The loop ends when the value 1 comes round again.
        Assert.Equal(255, r.U16("period"));
    }

    [Fact]
    public void Next8_produces_every_value_from_1_to_255_once_per_period()
    {
        var r = Run.Program("""
            import random
            var count: [u8; 256]
            fn main() {
                for _ in 0..255 {
                    var v: u8 = random.next8()
                    count[v] += 1
                }
            }
            """, maxCycles: 500_000);
        Assert.Equal(0, r.U8("count", 0));
        for (int v = 1; v < 256; v++) Assert.Equal(1, r.U8("count", v));
    }

    [Fact]
    public void Next8_matches_the_reference_sequence_from_a_seed()
    {
        var r = Run.Program("""
            import random
            var out: [u8; 200]
            fn main() {
                random.seed($1234)
                for i in 0..200 { out[i] = random.next8() }
            }
            """, maxCycles: 500_000);
        byte x = 0x12 ^ 0x34;
        for (int i = 0; i < 200; i++)
        {
            x = Step8(x);
            Assert.Equal(x, r.U8("out", i));
        }
    }

    [Fact]
    public void Next16_has_a_period_of_65535()
    {
        var r = Run.Program("""
            import random
            var period: u16
            var v: u16
            var hits: u8
            fn main() {
                v = random.next16()
                period = 1
                while v != 1 {
                    v = random.next16()
                    period += 1
                }
            }
            """, maxCycles: 30_000_000);
        // Starting from state 1, the first value is Step16(1); the sequence comes back to 1 after 65535 steps.
        Assert.Equal(65535, r.U16("period"));
    }

    [Fact]
    public void Next16_matches_the_reference_sequence_and_seed_replaces_zero()
    {
        var r = Run.Program("""
            import random
            var out: [u16; 100]
            var zero: u16
            fn main() {
                random.seed($ABCD)
                for i in 0..100 { out[i] = random.next16() }
                random.seed(0)
                zero = random.next16()
            }
            """, maxCycles: 500_000);
        ushort x = 0xABCD;
        for (int i = 0; i < 100; i++)
        {
            x = Step16(x);
            Assert.Equal(x, r.U16At("out", i, 100));
        }
        Assert.Equal(Step16(1), r.U16("zero"));
    }

    [Fact]
    public void Below_stays_below_n_and_reaches_every_value()
    {
        foreach (int n in new[] { 1, 2, 3, 10, 100, 255 })
        {
            var r = Run.Program($$"""
                import random
                var hit: [u8; 256]
                var bad: u8
                fn main() {
                    for _ in 0..255 {
                        var v: u8 = random.below({{n}})
                        hit[v] = 1
                        if v >= {{n}} { bad += 1 }
                    }
                }
                """, maxCycles: 2_000_000);
            Assert.Equal(0, r.U8("bad"));
            for (int v = 0; v < n; v++) Assert.Equal(1, r.U8("hit", v));
        }
        var zero = Run.Program("import random\nvar v: u8\nfn main() { v = random.below(0) }");
        Assert.Equal(0, zero.U8("v"));
    }

    [Fact]
    public void Using_the_generator_from_main_and_a_handler_is_an_error()
    {
        var e = Assert.Throws<Syntax.CompileError>(() => TernCompiler.Compile("""
            import random
            var a: u8
            var b: u8
            interrupt fn h() { b = random.next8() }
            fn main() { a = random.next8() }
            """));
        Expect.Message(e, 4, 11, "'random.next8' (called from main) and 'random.next8' (called from the interrupt handler 'h') both use 'random.state8', a private variable of its module. An interrupt could change it while the other is using it. Call functions of this module from only main or only one interrupt handler");
    }

    [Fact]
    public void An_unused_random_module_adds_no_bytes()
    {
        var with = TernCompiler.Compile("import random\nfn main() { }");
        var without = TernCompiler.Compile("fn main() { }");
        Assert.Equal(without.Image.Length, with.Image.Length);
    }
}
