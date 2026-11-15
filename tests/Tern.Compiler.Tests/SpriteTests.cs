using Tern.Compiler.Syntax;
using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>lib/c64/sprites (spec 10.1): the multiplexer's display list and its raster writes, in the emulator with a stand-in VIC-II.</summary>
public sealed class SpriteTests
{
    public readonly record struct VSprite(int X, int Y, int Frame, int Color, int Flags);

    public const int Visible = 1, Multicolor = 2, ExpandX = 4, Gap = 30;

    /// <summary>The library's algorithm in C#: the order kept between calls, and the display list built from it.</summary>
    public sealed class Model(int count, bool flicker)
    {
        public int[] Order = Enumerable.Range(0, count).ToArray();
        private readonly int[] _shown = new int[count];
        private int _updates;

        /// <summary>Sorts and builds; returns the sprite numbers in display order.</summary>
        public List<int> Update(IReadOnlyList<VSprite> s)
        {
            for (int p = 1; p < count; p++)
            {
                int v = Order[p], j = p;
                while (j > 0 && s[Order[j - 1]].Y > s[v].Y) { Order[j] = Order[j - 1]; j--; }
                Order[j] = v;
            }
            _updates = (_updates + 1) & 255;
            var list = new List<int>();
            int last = 0;
            foreach (int v in Order)
            {
                if ((s[v].Flags & Visible) == 0) continue;
                int n = list.Count;
                if (n >= 8 && ((s[v].Y - s[list[n - 8]].Y) & 255) < Gap)
                {
                    // Busy: with flicker, the sprite that has waited longer takes the last entry's place.
                    if (flicker && ((_updates - _shown[v]) & 255) > ((_updates - _shown[last]) & 255))
                    {
                        list[n - 1] = v;
                        last = v;
                    }
                }
                else
                {
                    list.Add(v);
                    last = v;
                }
            }
            if (flicker) foreach (int v in list) _shown[v] = _updates;
            return list;
        }
    }

    /// <summary>A program run with the stand-in VIC-II.</summary>
    public sealed class Rig
    {
        public CompileResult Result { get; }
        public Ram Ram { get; } = new();
        public FakeVic Vic { get; }
        public Cpu6502 Cpu { get; }
        public int Interrupts { get; private set; }

        public Rig(string source, long cycles, int pointerBase = 0x07F8, Action<Rig>? onStep = null, bool stealCycles = false)
        {
            Result = TernCompiler.Compile(source);
            StartState.Fill(Ram);
            Ram.Load((ushort)Result.LoadAddress, Result.Image);
            Vic = new FakeVic(Ram, pointerBase);
            Cpu = new Cpu6502(Vic) { PC = (ushort)Result.Entry, SP = 0xFF, I = true };
            StartState.Set(Cpu);
            Vic.Cpu = Cpu;
            Vic.StealCycles = stealCycles;
            while (Cpu.Cycles < cycles)
            {
                Cpu.Step();
                Vic.Tick();
                if (Vic.IrqPending && Cpu.Irq()) Interrupts++;
                onStep?.Invoke(this);
            }
        }

        public int Address(string name) => Result.Symbols[name];
        public byte U8(string name, int offset = 0) => Ram.Bytes[Address(name) + offset];
    }

    /// <summary>Statements that fill the public tables.</summary>
    public static string Setup(IReadOnlyList<VSprite> s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Count; i++)
            sb.Append($"    sprites.xpos[{i}] = {s[i].X}\n    sprites.ypos[{i}] = {s[i].Y}\n    sprites.frame[{i}] = {s[i].Frame}\n    sprites.color[{i}] = {s[i].Color}\n    sprites.flags[{i}] = {s[i].Flags}\n");
        return sb.ToString();
    }

    internal static string Program(int count, IReadOnlyList<VSprite> s, string parameters = "", bool everyFrame = false) => $$"""
        import c64
        import sprites(COUNT = {{count}}{{parameters}})
        fn main() {
            sprites.start()
        {{Setup(s)}}
            sprites.update()
            while true {
                {{(everyFrame ? "sprites.wait_frame()\n sprites.update()" : "")}}
            }
        }
        """;

    /// <summary>The lines of one frame as the model says they look: each displayed sprite from y for 21 lines.</summary>
    public static Dictionary<int, List<FakeVic.Shown>> Expected(IReadOnlyList<VSprite> s, List<int> display)
    {
        var lines = new Dictionary<int, List<FakeVic.Shown>>();
        for (int n = 0; n < display.Count; n++)
        {
            var v = s[display[n]];
            for (int l = v.Y + 1; l <= v.Y + 21 && l < 256; l++)
            {
                if (!lines.TryGetValue(l, out var list)) lines[l] = list = [];
                list.Add(new FakeVic.Shown(n & 7, v.X, v.Frame, v.Color & 15, (v.Flags & Multicolor) != 0, (v.Flags & ExpandX) != 0));
            }
        }
        return lines;
    }

    /// <summary>Compares what the VIC drew in one whole frame with the expectation.</summary>
    internal static void AssertFrame(Rig r, int frame, Dictionary<int, List<FakeVic.Shown>> expected)
    {
        for (int l = 0; l < 256; l++)
        {
            r.Vic.Seen.TryGetValue((long)frame * FakeVic.Lines + l, out var got);
            expected.TryGetValue(l, out var want);
            var g = (got ?? []).OrderBy(x => x.Slot).ToList();
            var w = (want ?? []).OrderBy(x => x.Slot).ToList();
            Assert.True(g.SequenceEqual(w), $"frame {frame} line {l}: got [{string.Join(", ", g)}] want [{string.Join(", ", w)}]");
        }
    }

    [Fact]
    public void Spread_sprites_are_drawn_where_the_model_puts_them()
    {
        var s = new List<VSprite>();
        for (int i = 0; i < 24; i++)
            s.Add(new VSprite(24 + i * 13 % 300, 30 + (i * 7 % 24) * 9, 64 + i, i % 15 + 1, Visible));
        var model = new Model(24, false);
        var display = model.Update(s);
        Assert.Equal(24, display.Count);
        var r = new Rig(Program(24, s), 6 * 312 * 63);
        AssertFrame(r, 3, Expected(s, display));
        AssertFrame(r, 4, Expected(s, display));
        Assert.True(r.Interrupts > 6 * 8, $"{r.Interrupts} interrupts");
        Assert.True(r.Vic.Seen.Count > 500, $"{r.Vic.Seen.Count} lines with sprites");
        Assert.Equal(24, r.U8("sprites.count"));
    }

    private static List<VSprite> Rows(int count, int perRow, int firstY, int rowGap, int flags = Visible)
    {
        var s = new List<VSprite>();
        for (int i = 0; i < count; i++)
            s.Add(new VSprite(24 + (i % perRow) * 30 + (i / perRow) * 3, firstY + i / perRow * rowGap, 64 + i, i % 15 + 1, flags));
        return s;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Rows_of_sprites_that_start_together_are_drawn_whole(int perRow)
    {
        int count = 24 / perRow * perRow;
        int rows = count / perRow;
        var s = Rows(count, perRow, 30, perRow == 8 ? 40 : Math.Min(33, 200 / rows));
        var model = new Model(24, false);
        s.AddRange(Enumerable.Range(0, 24 - count).Select(_ => new VSprite(0, 0, 0, 0, 0)));
        var display = model.Update(s);
        var r = new Rig(Program(24, s), 6 * 312 * 63);
        AssertFrame(r, 4, Expected(s, display));
    }

    [Fact]
    public void Flags_x_above_255_color_and_pointer_reach_the_registers()
    {
        var s = new List<VSprite>
        {
            new(300, 50, 70, 5, Visible),
            new(24, 60, 71, 6, Visible | Multicolor),
            new(500, 70, 72, 7, Visible | ExpandX),
            new(40, 80, 73, 8, Visible | Multicolor | ExpandX),
            new(41, 90, 74, 9, 0),                     // not visible
            new(42, 100, 75, 10, Visible),
        };
        while (s.Count < 12) s.Add(new VSprite(0, 0, 0, 0, 0));
        var model = new Model(12, false);
        var display = model.Update(s);
        Assert.Equal(5, display.Count);
        var r = new Rig(Program(12, s), 5 * 312 * 63);
        AssertFrame(r, 3, Expected(s, display));
        Assert.Equal(0x1F, r.Vic.Reg(0x15));   // five sprites enabled
        Assert.Equal(5, r.U8("sprites.count"));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(64)]
    public void Count_of_8_and_64(int count)
    {
        var s = new List<VSprite>();
        for (int i = 0; i < count; i++)
            s.Add(new VSprite(24 + i * 5, 40 + (count - 1 - i) % 8 * 25 + i / 8 * 28 % 56, 64 + i % 64, i % 15 + 1, Visible));
        var model = new Model(count, false);
        var display = model.Update(s);
        var r = new Rig(Program(count, s), 6 * 312 * 63);
        Assert.Equal(display.Count, r.U8("sprites.count"));
        // All the entries of the display list are what the model built.
        int b = r.U8("sprites.base");
        for (int n = 0; n < display.Count; n++)
        {
            Assert.Equal(s[display[n]].Y, r.U8("sprites.d_y", b + n));
            Assert.Equal(s[display[n]].X & 255, r.U8("sprites.d_x", b + n));
            Assert.Equal(n & 7, r.U8("sprites.d_slot", b + n));
        }
    }

    [Theory]
    [InlineData(7)]
    [InlineData(65)]
    public void Count_outside_8_to_64_is_an_error(int count)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile($"import c64\nimport sprites(COUNT = {count})\nfn main() {{ sprites.init() }}"));
        Assert.Contains("sprites.COUNT must be 8 to 64", e.Description);
    }

    [Fact]
    public void Sprites_on_one_line_beyond_eight_are_left_out_without_flicker()
    {
        // Twelve sprites within a few lines of each other: eight fit.
        var s = Rows(12, 12, 100, 0);
        for (int i = 0; i < 12; i++) s[i] = s[i] with { Y = 100 + i };
        var model = new Model(12, false);
        var display = model.Update(s);
        Assert.Equal(8, display.Count);
        var r = new Rig(Program(12, s), 6 * 312 * 63);
        AssertFrame(r, 3, Expected(s, display));
        AssertFrame(r, 4, Expected(s, display));
        Assert.Equal(8, r.U8("sprites.count"));
    }

    [Fact]
    public void With_flicker_the_sprites_in_a_crowd_take_turns()
    {
        var s = Rows(12, 12, 100, 0);
        for (int i = 0; i < 12; i++) s[i] = s[i] with { Y = 100 + i };
        var model = new Model(12, true);
        int frames = 30;
        var r = new Rig(Program(12, s, ", FLICKER = true", everyFrame: true), (frames + 2) * 312 * 63);
        // Each frame's picture is one the model produces; every sprite appears in some frame.
        var seenPointers = new HashSet<int>();
        var expectedFrames = new List<Dictionary<int, List<FakeVic.Shown>>>();
        // The program updates once before the loop, then once per frame: the model does the same.
        var lists = new List<List<int>> { model.Update(s) };
        for (int f = 0; f < frames + 1; f++) lists.Add(model.Update(s));
        for (int f = 3; f < frames; f++)
        {
            Assert.True(r.Vic.Seen.TryGetValue((long)f * 312 + 115, out var at110));
            Assert.Equal(8, at110!.Count);
            foreach (var shown in at110) seenPointers.Add(shown.Pointer - 64);
        }
        Assert.Equal(Enumerable.Range(0, 12).ToHashSet(), seenPointers);
        // Every frame's picture is one of the display lists the model built.
        var pictures = lists.Select(l => Expected(s, l)).ToList();
        for (int f = 3; f < frames; f++)
            Assert.Contains(pictures, p => Matches(r, f, p));
    }

    private static bool Matches(Rig r, int frame, Dictionary<int, List<FakeVic.Shown>> expected)
    {
        for (int l = 0; l < 256; l++)
        {
            r.Vic.Seen.TryGetValue((long)frame * FakeVic.Lines + l, out var got);
            expected.TryGetValue(l, out var want);
            if (!(got ?? []).OrderBy(x => x.Slot).SequenceEqual((want ?? []).OrderBy(x => x.Slot))) return false;
        }
        return true;
    }

    [Fact]
    public void A_program_handler_calls_raster_as_a_part_and_learns_the_top_of_the_frame()
    {
        var s = Rows(16, 2, 40, 28);
        var model = new Model(16, false);
        var display = model.Update(s);
        var r = new Rig($$"""
            import c64
            import sprites(COUNT = 16)
            var tops: u8
            var others: u8
            interrupt fn frame() {
                if sprites.raster() { tops += 1 } else { others += 1 }
            }
            fn main() {
                sprites.init()
                c64.kernal_off()
                c64.set_irq(addr(frame))
                c64.vic.raster = 0
                c64.vic.irq_enable = 1
                c64.vic.irq_status = 1
                c64.interrupts_on()
            {{Setup(s)}}
                sprites.update()
                while true { }
            }
            """, 7 * 312 * 63);
        AssertFrame(r, 5, Expected(s, display));
        Assert.Equal(r.U8("sprites.frames"), r.U8("tops"));
        Assert.InRange(r.U8("tops"), 6, 7);
        Assert.True(r.U8("others") >= 6 * 2, $"{r.U8("others")} other interrupts");
    }

    [Fact]
    public void An_unused_sprites_module_adds_no_bytes()
    {
        var with = TernCompiler.Compile("import c64\nimport sprites\nfn main() { }");
        var without = TernCompiler.Compile("import c64\nfn main() { }");
        Assert.Equal(without.Image.Length, with.Image.Length);
    }

    [Fact]
    public void Update_calls_the_flicker_build_only_with_FLICKER()
    {
        const string Main = """
            import c64
            import sprites(COUNT = 16{0})
            fn main() {{
                sprites.start()
                sprites.update()
            }}
            """;
        string main = Main;
        var plain = TernCompiler.Compile(string.Format(main, ""));
        var flicker = TernCompiler.Compile(string.Format(main, ", FLICKER = true"));
        Assert.DoesNotContain("jsr sprites.build_flicker", plain.Listing);
        Assert.Contains("jsr sprites.build_flicker", flicker.Listing);
    }

    /// <summary>Cycle counts for docs/status.md; the limits are generous so only a large slowdown fails.</summary>
    [Fact]
    public void Update_of_32_sprites_with_flicker_costs()
    {
        var lib = new Library("import c64\nimport sprites(COUNT = 32, FLICKER = true)", "sprites.init()\nsprites.update()");
        for (int i = 0; i < 32; i++)
        {
            lib.Ram.Bytes[lib.Address("sprites.ypos") + i] = (byte)(30 + i * 7);
            lib.Ram.Bytes[lib.Address("sprites.flags") + i] = Visible;
        }
        lib.Call("sprites.init");
        lib.Call("sprites.update");
        long again = lib.Call("sprites.update");
        Console.WriteLine($"update(32 sprites, FLICKER): {again} cycles");
        Assert.True(again < 16000, $"{again} cycles");
    }

    [Theory]
    [InlineData(false, 9500)]
    [InlineData(true, 21000)]
    public void Update_of_32_sprites_costs(bool reversed, int limit)
    {
        var lib = new Library("import c64\nimport sprites(COUNT = 32)", "sprites.init()\nsprites.update()");
        for (int i = 0; i < 32; i++)
        {
            int y = reversed ? 240 - i * 7 : 30 + i * 7;
            lib.Set8($"sprites.ypos", y);   // placeholder, replaced below
            lib.Ram.Bytes[lib.Address("sprites.ypos") + i] = (byte)y;
            lib.Ram.Bytes[lib.Address("sprites.flags") + i] = Visible;
            lib.Ram.Bytes[lib.Address("sprites.xpos") + i] = (byte)(i * 8);
        }
        lib.Call("sprites.init");
        long first = lib.Call("sprites.update");
        long second = lib.Call("sprites.update");
        Console.WriteLine($"update(32 sprites, reversed={reversed}): first {first}, again {second} cycles");
        Assert.True(first < limit, $"{first} cycles");
        Assert.True(second < limit, $"{second} cycles");
    }
}
