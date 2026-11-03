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
        var marks = new List<string>();
        var rig = new SpriteTests.Rig(source, (Frames + 40) * 312 * 63, pointerBase: 0x83F8, stealCycles: true, onStep: r =>
        {
            if (marks.Count < 400 && r.Cpu.Cycles > 20L * 312 * 63)
            {
                foreach (var (name, label) in new[] { ("move", "scroll.move"), ("update", "sprites.update"), ("wait", "sprites.wait_frame"), ("msprites", "move_sprites"), ("sort", "sprites.sort"), ("bf", "sprites.build_flicker") })
                    if (r.Cpu.PC == r.Address(label)) marks.Add($"{name}@{r.Cpu.Cycles}");
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

        Console.WriteLine("marks: " + string.Join(" ", marks.Take(60)));
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

        // The scroll registers: one pixel to the right per frame in the first 64 frames: the x scroll counts down,
        // and the screen is swapped when it wraps from 0 to 7.
        Console.WriteLine("xs: " + string.Join(" ", tops.Take(80).Select(t => (t.Control2 & 7) + "@" + (t.Line / 312) + "/" + (t.Line % 312))));
        int swaps = 0;
        for (int i = 6; i < 56; i++)
        {
            int xs0 = tops[i].Control2 & 7, xs1 = tops[i + 1].Control2 & 7;
            Assert.Equal((xs0 + 7) & 7, xs1);
            if (xs1 == 7)
            {
                Assert.NotEqual(tops[i].Memory & 0xF0, tops[i + 1].Memory & 0xF0);
                swaps++;
            }
            else Assert.Equal(tops[i].Memory, tops[i + 1].Memory);
            Assert.Equal(tops[i].Control1, tops[i + 1].Control1);
            Assert.Equal(0, tops[i].Control2 & 8);      // 38 columns
            Assert.Equal(0, tops[i].Control1 & 8);      // 24 rows
        }
        Assert.InRange(swaps, 5, 7);

        // The shown screen is the map window of the camera, at the end of the run.
        var ram = rig.Ram.Bytes;
        int front = rig.U8("scroll.front");
        int cx = rig.U8("scroll.cell_x"), cy = rig.U8("scroll.cell_y");
        for (int r = 0; r < 25; r++)
            for (int c = 0; c < 40; c++)
                Assert.Equal(ram[0x4000 + (cy + r) * 100 + cx + c], ram[(front == 0 ? 0x8000 : 0x8400) + r * 40 + c]);
    }
}
