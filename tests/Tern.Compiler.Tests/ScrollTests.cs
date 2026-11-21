using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>lib/c64/scroll (spec 10.1): the hidden screen after every move, in all eight directions.</summary>
public sealed class ScrollTests
{
    private const int MapAt = 0x4000, ColorMapAt = 0x7000, W = 100, H = 60;
    private const int ScreenA = 0x8000, ScreenB = 0x8400;

    private static byte MapCell(int x, int y) => (byte)((x * 7 + y * 13 + (x ^ y)) & 255);
    private static byte ColorCell(int x, int y) => (byte)((x + y * 3) & 15);

    private sealed class Rig
    {
        public Library Lib { get; }

        public Rig(string parameters = "")
        {
            Lib = new Library($"import c64\nimport scroll{parameters}",
                "scroll.set_map(0, 0)\nscroll.set_color_map(0)\nscroll.draw(0, 0)\nscroll.move(0, 0)\nscroll.finish()\nscroll.set_fine(0, 0)",
                setup: ram =>
                {
                    for (int y = 0; y < H; y++)
                        for (int x = 0; x < W; x++)
                        {
                            ram.Bytes[MapAt + y * W + x] = MapCell(x, y);
                            ram.Bytes[ColorMapAt + y * W + x] = ColorCell(x, y);
                        }
                });
        }

        public long Call(string name) => Lib.Call(name);

        public void Draw(int x, int y)
        {
            Lib.Set16("scroll.set_map.m", MapAt);
            Lib.Set16("scroll.set_map.w", W);
            Lib.Call("scroll.set_map");
            Lib.Set16("scroll.set_color_map.m", ColorMapAt);
            Lib.Call("scroll.set_color_map");
            Lib.Set16("scroll.draw.x", x);
            Lib.Set16("scroll.draw.y", y);
            Lib.Call("scroll.draw");
        }

        public long Move(int dx, int dy)
        {
            Lib.Set8("scroll.move.dx", dx);
            Lib.Set8("scroll.move.dy", dy);
            return Lib.Call("scroll.move");
        }

        public int CellX => Lib.U16("scroll.cell_x");
        public int CellY => Lib.U16("scroll.cell_y");
        public int FineX => Lib.U8("scroll.fine_x");
        public int FineY => Lib.U8("scroll.fine_y");
        public int PixelX => CellX * 8 + FineX;
        public int PixelY => CellY * 8 + FineY;

        /// <summary>The shown screen holds the map window at the camera's cell, and the registers match.</summary>
        public void AssertConsistent(string where)
        {
            int front = Lib.U8("scroll.front");
            int screen = front == 0 ? ScreenA : ScreenB;
            for (int r = 0; r < 25; r++)
                for (int c = 0; c < 40; c++)
                    Assert.True(MapCell(CellX + c, CellY + r) == Lib.Ram.Bytes[screen + r * 40 + c],
                        $"{where}: cell ({CellX},{CellY}) screen {front} row {r} col {c}: {Lib.Ram.Bytes[screen + r * 40 + c]} want {MapCell(CellX + c, CellY + r)}");
            Assert.Equal(7 - FineX, Lib.U8("scroll.hw_xs"));
            Assert.Equal(7 - FineY, Lib.U8("scroll.hw_ys"));
            Assert.Equal(front == 0 ? 0 : 0x10, Lib.U8("scroll.hw_sel"));
            Assert.InRange(FineX, 0, 7);
            Assert.InRange(FineY, 0, 7);
        }
    }

    [Fact]
    public void Draw_shows_the_map_window_and_sets_the_registers()
    {
        var r = new Rig();
        r.Draw(10, 5);
        Assert.Equal(10, r.CellX);
        Assert.Equal(5, r.CellY);
        r.AssertConsistent("draw");
        Assert.Equal(7, r.Lib.U8("scroll.hw_xs"));
        Assert.Equal(7, r.Lib.U8("scroll.hw_ys"));
        Assert.Equal(1, r.Lib.Ram.Bytes[0xDD00] & 3);               // VIC bank 2: bits 01
    }

    public static IEnumerable<object[]> Directions() =>
        new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1) }.Select(d => new object[] { d.Item1, d.Item2 });

    [Theory]
    [MemberData(nameof(Directions))]
    public void Every_move_in_each_of_eight_directions_leaves_the_right_screen(int dx, int dy)
    {
        var r = new Rig();
        r.Draw(30, 20);
        r.AssertConsistent("start");
        int startX = r.PixelX, startY = r.PixelY;
        for (int i = 0; i < 40; i++)
        {
            r.Move(dx, dy);
            r.AssertConsistent($"move {i} ({dx},{dy})");
        }
        // The camera may wait up to six frames for the hidden screen when a direction starts at a coarse step.
        int moved = Math.Max(Math.Abs(r.PixelX - startX), Math.Abs(r.PixelY - startY));
        Assert.InRange(moved, 33, 40);
        if (dx == 0 || dy == 0)
        {
            Assert.Equal(dx * moved, r.PixelX - startX);
            Assert.Equal(dy * moved, r.PixelY - startY);
        }
        else
        {
            Assert.InRange(Math.Abs(r.PixelX - startX), 28, 40);
            Assert.InRange(Math.Abs(r.PixelY - startY), 28, 40);
        }
    }

    [Fact]
    public void A_diagonal_that_starts_aligned_moves_both_axes_every_frame()
    {
        var r = new Rig();
        r.Draw(30, 20);
        r.Lib.Set8("scroll.set_fine.x", 0);
        r.Lib.Set8("scroll.set_fine.y", 7);      // 7 pixels from the next step to the right, 7 to the one above
        r.Lib.Call("scroll.set_fine");
        int y0 = r.PixelY;
        for (int i = 0; i < 24; i++)
        {
            r.Move(1, -1);
            r.AssertConsistent($"move {i}");
            Assert.Equal(30 * 8 + i + 1, r.PixelX);
            Assert.Equal(y0 - i - 1, r.PixelY);
        }
    }

    [Fact]
    public void A_diagonal_that_has_to_align_first_prepares_the_diagonal_screen_and_never_holds_the_camera()
    {
        var r = new Rig();
        r.Draw(30, 20);
        r.Lib.Set8("scroll.set_fine.x", 3);
        r.Lib.Set8("scroll.set_fine.y", 0);      // 4 pixels to the next step to the right, 7 down
        r.Lib.Call("scroll.set_fine");
        for (int i = 0; i < 24; i++)
        {
            int x = r.PixelX, y = r.PixelY;
            r.Move(1, 1);
            r.AssertConsistent($"move {i}");
            Assert.True(r.PixelX + r.PixelY > x + y, $"move {i} held the camera");
        }
    }

    [Fact]
    public void Turning_around_in_the_middle_of_a_phase_and_stopping_keep_the_screen_right()
    {
        var r = new Rig();
        r.Draw(40, 25);
        (int, int)[] script = [(1, 0), (1, 0), (1, 0), (-1, 0), (-1, 0), (0, 0), (0, 1), (0, 1), (0, -1), (0, -1), (0, -1), (0, -1), (0, -1), (0, -1), (0, -1), (0, -1), (0, -1),
            (-1, 0), (-1, 0), (-1, 0), (-1, 0), (-1, 0), (-1, 0), (-1, 0), (-1, 0), (-1, 0), (1, 1), (1, 1), (-1, 1), (-1, 1)];
        foreach (var (dx, dy) in script)
        {
            r.Move(dx, dy);
            r.AssertConsistent($"move ({dx},{dy})");
        }
    }

    [Fact]
    public void A_random_walk_of_500_moves()
    {
        var r = new Rig();
        r.Draw(50, 30);
        var rnd = new Random(12345);
        int dx = 0, dy = 0;
        for (int i = 0; i < 500; i++)
        {
            if (rnd.Next(10) < 3) { dx = rnd.Next(3) - 1; dy = rnd.Next(3) - 1; }   // mostly keeps its direction
            if (r.CellX <= 3 && dx < 0) dx = 1;
            if (r.CellX >= W - 45 && dx > 0) dx = -1;
            if (r.CellY <= 3 && dy < 0) dy = 1;
            if (r.CellY >= H - 30 && dy > 0) dy = -1;
            r.Move(dx, dy);
            r.AssertConsistent($"step {i} ({dx},{dy})");
        }
    }

    [Fact]
    public void Move_cycles_for_a_straight_move_at_one_pixel_per_frame()
    {
        var r = new Rig();
        r.Draw(30, 20);
        var costs = new List<long>();
        for (int i = 0; i < 64; i++) costs.Add(r.Move(1, 0));
        Console.WriteLine($"move(1,0): max {costs.Max()}, min {costs.Min()}, mean {costs.Average():F0}, coarse step {costs.Where((c, i) => (i + 1) % 8 == 0).Average():F0}");
        Assert.True(costs.Max() < 12000, $"max {costs.Max()}");
    }

    [Fact]
    public void A_turn_costs_no_more_than_a_few_steps_in_any_one_call()
    {
        var r = new Rig();
        r.Draw(40, 25);
        long max = 0;
        var rnd = new Random(7);
        for (int i = 0; i < 300; i++)
        {
            int dx = (i / 11 % 3) - 1, dy = (i / 17 % 3) - 1;
            if (rnd.Next(4) == 0) dx = -dx;
            max = Math.Max(max, r.Move(dx, dy));
            r.AssertConsistent($"move {i}");
        }
        Console.WriteLine($"move: most expensive call in a turning run {max} cycles");
        Assert.True(max < 9000, $"{max} cycles");
    }

    [Fact]
    public void Colors_are_redrawn_from_the_color_map_at_each_coarse_step()
    {
        var r = new Rig("(COLORS = true)");
        r.Draw(20, 10);
        void Check(string where)
        {
            for (int row = 0; row < 25; row++)
                for (int c = 0; c < 40; c++)
                    Assert.True(ColorCell(r.CellX + c, r.CellY + row) == r.Lib.Ram.Bytes[0xD800 + row * 40 + c], $"{where}: row {row} col {c}");
        }
        Check("draw");
        for (int i = 0; i < 30; i++)
        {
            r.Move(1, 1);
            r.Call("scroll.finish");
            Check($"move {i}");
        }
    }

    [Fact]
    public void Without_COLORS_the_color_RAM_is_left_alone()
    {
        var r = new Rig();
        for (int i = 0; i < 1000; i++) r.Lib.Ram.Bytes[0xD800 + i] = 5;
        r.Draw(20, 10);
        for (int i = 0; i < 20; i++) { r.Move(1, 1); r.Call("scroll.finish"); }
        for (int i = 0; i < 1000; i++) Assert.Equal(5, r.Lib.Ram.Bytes[0xD800 + i]);
    }

    [Fact]
    public void Top_writes_the_scroll_registers_and_keeps_the_other_bits()
    {
        var r = Run.Program("""
            import c64
            import scroll
            var map: [u8; 4]
            interrupt fn frame() { scroll.top() }
            fn main() {
                scroll.set_map(addr(map), 40)
                c64.vic.control1 = $9B
                c64.vic.control2 = $D8
                c64.vic.memory = $15
                scroll.set_fine(3, 6)
                asm {
                    sei
                    lda #frame.lo
                    sta $FFFE
                    lda #frame.hi
                    sta $FFFF
                    cli
                }
                while scroll.frames < 3 { }
                asm { sei }
            }
            """, irqEvery: 400);
        Assert.Equal(0x05, r.Ram.Bytes[0xD018]);                                // screen A: nibble 0, the character set bits kept
        Assert.Equal(0xD0 | (7 - 3), r.Ram.Bytes[0xD016]);                      // 38 columns (bit 3 off), bits 4-7 kept, x scroll 4
        Assert.Equal(0x10 | (7 - 6), r.Ram.Bytes[0xD011]);                      // bits 4-6 kept, 24 rows (bit 3 off), bit 7 off, y scroll 1
    }

    [Theory]
    [InlineData(", SCREEN_A = $8100", 2, 8, "scroll.SCREEN_A and SCREEN_B must be multiples of $0400 (assert at scroll.tern:54:1)")]
    [InlineData(", SCREEN_B = $C000", 2, 8, "scroll.SCREEN_A and SCREEN_B must be in the same 16 KB VIC bank (assert at scroll.tern:55:1)")]
    [InlineData(", SCREEN_B = $8000", 2, 8, "scroll.SCREEN_A and SCREEN_B must be different screens (assert at scroll.tern:56:1)")]
    public void Wrong_screen_addresses_are_errors(string parameters, int line, int column, string message)
    {
        Expect.Error(() => TernCompiler.Compile($"import c64\nimport scroll(COLORS = false{parameters})\nfn main() {{ scroll.set_fine(0, 0) }}"), line, column, message);
    }

    [Fact]
    public void An_unused_scroll_module_adds_no_bytes()
    {
        var with = TernCompiler.Compile("import c64\nimport scroll\nfn main() { }");
        var without = TernCompiler.Compile("import c64\nfn main() { }");
        Assert.Equal(without.Image.Length, with.Image.Length);
    }
}
