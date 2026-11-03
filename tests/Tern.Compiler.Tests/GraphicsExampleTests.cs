namespace Tern.Compiler.Tests;

/// <summary>
/// examples/scroller.tern: the sprite multiplexer and the scroller combined in one handler of the
/// program's own (spec 6.3), with the stand-in VIC-II checking what is drawn in every frame.
/// </summary>
public sealed class GraphicsExampleTests
{
    private sealed record TopSnapshot(long Line, int Control2, int Control1, int Memory, int Enable, List<(int Slot, int X, int Y, int Pointer, int Color, bool Mc, bool Ex)> List);

    [Fact]
    public void Scroller_example_draws_the_display_list_and_the_scroll_registers_in_every_frame()
    {
        var path = AsmTests.FindExample("scroller.tern");
        var source = File.ReadAllText(path);
        var tops = new List<TopSnapshot>();
        int seenWrites = 0;
        const int Frames = 140;
        int quiet = 0;
        var rig = new SpriteTests.Rig(source, (Frames + 40) * 312 * 63, pointerBase: 0x83F8, stealCycles: true, onStep: r =>
        {
            // Where the main loop waits for the next frame, the camera, the screen and the registers agree.
            if (r.Cpu.PC == r.Address("sprites.wait_frame") && r.U8("sprites.frames") > 0)
            {
                var ram = r.Ram.Bytes;
                int front = r.U8("scroll.front");
                int cx = r.U8("scroll.cell_x"), cy = r.U8("scroll.cell_y");
                for (int row = 0; row < 25; row++)
                    for (int c = 0; c < 40; c++)
                        Assert.True(ram[0x4000 + (cy + row) * 100 + cx + c] == ram[(front == 0 ? 0x8000 : 0x8400) + row * 40 + c], $"cell ({cx},{cy}) front {front} row {row} col {c}");
                Assert.Equal(7 - r.U8("scroll.fine_x"), r.U8("scroll.hw_xs"));
                quiet++;
            }
            // The top of a frame: the multiplexer writes $D015, then the scroller its registers.
            var w = r.Vic.Writes;
            for (; seenWrites < w.Count; seenWrites++)
            {
                if (w[seenWrites].Reg != 0x18) continue;   // $D018 is the last of the scroller's three writes
                int count = r.U8("sprites.count"), b = r.U8("sprites.base");
                var list = new List<(int, int, int, int, int, bool, bool)>();
                for (int n = 0; n < count; n++)
                {
                    int e = b + n;
                    list.Add((r.U8("sprites.d_slot", e), r.U8("sprites.d_x", e) | (r.U8("sprites.d_msb", e) != 0 ? 256 : 0),
                        r.U8("sprites.d_y", e), r.U8("sprites.d_ptr", e), r.U8("sprites.d_col", e) & 15, r.U8("sprites.d_mc", e) != 0, r.U8("sprites.d_ex", e) != 0));
                }
                tops.Add(new TopSnapshot(w[seenWrites].Line, r.Vic.Reg(0x16), r.Vic.Reg(0x11), r.Vic.Reg(0x18), r.Vic.Reg(0x15), list));
            }
        });

        Assert.True(tops.Count >= Frames, $"{tops.Count} frames");
        // One top per frame, early in the frame.
        for (int i = 1; i < tops.Count; i++)
            Assert.Equal(1, tops[i].Line / 312 - tops[i - 1].Line / 312);
        Assert.All(tops, t => Assert.InRange(t.Line % 312, 0, 40));

        // The sprites: what the VIC drew in the frame after each top is that frame's display list.
        int checkedFrames = 0;
        foreach (var t in tops.Skip(3).Take(Frames - 6))
        {
            long frame = t.Line / 312;
            for (int l = 30; l < 250; l++)
            {
                rig.Vic.Seen.TryGetValue(frame * 312 + l, out var got);
                var want = t.List.Where(s => s.Y <= l && l < s.Y + 21)
                    .Select(s => new FakeVic.Shown(s.Slot, s.X, s.Pointer, s.Color, s.Mc, s.Ex)).OrderBy(x => x.Slot).ToList();
                var have = (got ?? []).OrderBy(x => x.Slot).ToList();
                Assert.True(have.SequenceEqual(want), $"frame {frame} line {l}: got [{string.Join(", ", have)}] want [{string.Join(", ", want)}]");
            }
            checkedFrames++;
        }
        Assert.True(checkedFrames > 100);
        Assert.True(tops.Skip(3).All(t => t.List.Count >= 20), "most of the 24 sprites are on screen");

        // The scroll registers: one pixel to the right every second frame in the first 128 frames: the x scroll counts
        // down, and the screen is swapped when it wraps from 0 to 7.
        var steps = new List<(int Xs, int Screen, int Control1)>();
        foreach (var t in tops.Skip(4).Take(100))
        {
            var s = (t.Control2 & 7, t.Memory & 0xF0, t.Control1);
            if (steps.Count == 0 || steps[^1] != s) steps.Add(s);
            Assert.Equal(0, t.Control2 & 8);      // 38 columns
            Assert.Equal(0, t.Control1 & 8);      // 24 rows
        }
        int swaps = 0;
        for (int i = 0; i + 1 < steps.Count; i++)
        {
            Assert.Equal((steps[i].Xs + 7) & 7, steps[i + 1].Xs);
            Assert.Equal(steps[i].Control1, steps[i + 1].Control1);
            if (steps[i + 1].Xs == 7) { Assert.NotEqual(steps[i].Screen, steps[i + 1].Screen); swaps++; }
            else Assert.Equal(steps[i].Screen, steps[i + 1].Screen);
        }
        Assert.InRange(steps.Count, 45, 51);      // 100 frames, a pixel every 2 frames
        Assert.InRange(swaps, 5, 7);

        Assert.True(quiet > 100, $"{quiet} checks");
    }

    /// <summary>Cycles of the program's handler, from the interrupt to its rti, for docs/status.md.</summary>
    [Fact]
    public void Handler_cycles_at_the_top_of_the_frame_and_per_group()
    {
        var path = AsmTests.FindExample("scroller.tern");
        var source = File.ReadAllText(path);
        var tops = new List<long>();
        var groups = new List<long>();
        long start = -1;
        bool isTop = false;
        int handler = 0, interrupts = 0;
        _ = new SpriteTests.Rig(source, 120L * 312 * 63, pointerBase: 0x83F8, stealCycles: false, onStep: r =>
        {
            if (handler == 0) handler = r.Address("frame");
            if (r.Interrupts != interrupts)
            {
                interrupts = r.Interrupts;
                start = r.Cpu.Cycles;
                isTop = r.Vic.RasterLine < 40 && r.U8("sprites.next") == 0;
            }
            else if (start >= 0 && r.Ram.Bytes[r.Cpu.PC] == 0x40 && r.Cpu.PC >= handler && r.Cpu.PC < handler + 200)
            {
                (isTop ? tops : groups).Add(r.Cpu.Cycles + 6 - start);
                start = -1;
            }
        });
        Console.WriteLine($"handler: top of frame (8 sprites + scroll.top) {tops.Skip(2).Average():F0} cycles, {groups.Count} other interrupts, mean {groups.Average():F0}, max {groups.Max()}");
        Assert.True(tops.Count > 50);
        Assert.True(tops.Skip(2).Max() < 2500, $"{tops.Max()}");
    }
}
