namespace Tern.Compiler.Tests;

/// <summary>
/// examples/sine.tern and examples/speedcode.tern (roadmap M22 follow-up 2): a sine table built by a
/// const fn, and a generator that unrolls a per-frame loop, run on the stand-in VIC-II frame by frame.
/// </summary>
public sealed class CompileTimeExampleTests
{
    private const int Frame = FakeVic.Lines * FakeVic.CyclesPerLine;

    private static string Source(string name) => File.ReadAllText(AsmTests.FindExample(name));

    private static int Wave(int i, int mid, double amplitude) =>
        (int)Math.Round(mid + amplitude * Math.Sin(i * 2 * Math.PI / 256), MidpointRounding.AwayFromZero);

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Sine_example_moves_the_ball_along_the_tables_once_per_frame()
    {
        var rig = new SpriteTests.Rig(Source("sine.tern"), 40 * Frame);
        var r = rig.Result;

        // The tables and the ball's shape are only bytes in the program.
        for (int i = 0; i < 256; i++)
        {
            Assert.Equal(Wave(i, 140, 100.0), rig.U8("xs", i));
            Assert.Equal(Wave(i, 150, 80.0), rig.U8("ys", i));
        }
        var ball = rig.Ram.Bytes.AsSpan(0x0C00, 63).ToArray();
        for (int row = 0; row < 21; row++)
            for (int col = 0; col < 24; col++)
            {
                double dx = col - 11.5, dy = row - 10.0;
                bool set = (ball[row * 3 + col / 8] & (0x80 >> (col % 8))) != 0;
                Assert.Equal(dx * dx + dy * dy <= 10.5 * 10.5, set);
            }
        Assert.Equal(0x0C00 / 64, rig.Ram.Bytes[0x07F8]);
        Assert.DoesNotContain("jsr", r.Listing[r.Listing.IndexOf("\nmain:", StringComparison.Ordinal)..]);   // after the startup's jsr main

        // One x and one y write per frame, on line 250, from the tables at a += 2 and b += 3.
        var xs = rig.Vic.Writes.Where(w => w.Reg == 0).ToList();
        var ys = rig.Vic.Writes.Where(w => w.Reg == 1).ToList();
        Assert.InRange(xs.Count, 38, 40);
        Assert.Equal(xs.Count, ys.Count);
        for (int k = 0; k < xs.Count; k++)
        {
            Assert.Equal(250, xs[k].Line % FakeVic.Lines);
            Assert.Equal(k, xs[k].Line / FakeVic.Lines);
            Assert.Equal(Wave(2 * k & 255, 140, 100.0), xs[k].Value);
            Assert.Equal(Wave(3 * k & 255, 150, 80.0), ys[k].Value);
        }
    }

    [Fact]
    public void Speedcode_example_moves_the_colours_of_all_but_the_text_rows()
    {
        // A model of the screen: bands of 2 columns, moved one column left on every other call.
        // Checked each time the main loop calls cycle_unrolled; tick = 1 there means this call moves.
        int[] bands = [2, 8, 7, 5, 3, 6, 4, 10];
        int steps = 0, calls = 0, lastLine = -1, beamChecks = 0;
        long checkedLine = -1;
        void CheckRows(SpriteTests.Rig r, int from, int to)
        {
            var m = r.Ram.Bytes;
            for (int row = from; row <= to; row++)
                for (int c = 0; c < 40; c++)
                {
                    bool text = row is 0 or 12 or 24;
                    int expected = text ? 1 : bands[((c + steps) % 40 >> 1) & 7];
                    Assert.True(expected == (m[0xD800 + row * 40 + c] & 15), $"row {row} col {c} after {steps} steps, line {r.Vic.RasterLine}");
                    if (!text) Assert.Equal(0xA0, m[0x0400 + row * 40 + c]);
                }
        }
        var rig = new SpriteTests.Rig(Source("speedcode.tern"), 14 * Frame, onStep: r =>
        {
            // When the beam reaches row 1 (line 51) and row 13 (line 155), the rows of that
            // loop have finished moving: no row is drawn half moved.
            if (calls > 0 && r.Vic.Line != checkedLine && r.Vic.RasterLine is 51 or 155)
            {
                checkedLine = r.Vic.Line;
                if (r.Vic.RasterLine == 51) CheckRows(r, 1, 11); else CheckRows(r, 13, 23);
                beamChecks++;
            }
            if (r.Cpu.PC != r.Address("cycle_unrolled")) return;
            CheckRows(r, 0, 24);
            // Once per frame, on line 250.
            Assert.Equal(250, r.Vic.RasterLine);
            Assert.NotEqual(lastLine, (int)r.Vic.Line);
            lastLine = (int)r.Vic.Line;
            if (r.U8("tick") == 1) steps++;
            calls++;
        });
        Assert.InRange(calls, 12, 14);
        Assert.Equal(calls / 2, steps);
        Assert.True(beamChecks > 20, $"{beamChecks} checks");
        Assert.Equal(" SPEEDCODE:", string.Concat(rig.Ram.Bytes.AsSpan(0x0401, 11).ToArray().Select(b => (char)(b < 32 ? b + 64 : b))));
    }

    [Fact]
    public void Speedcode_example_unrolled_matches_the_plain_loop()
    {
        // Both routines from the example, on the same colours: one step each.
        var decls = Source("speedcode.tern").Split("\nfn main()")[0];
        string Program(string routine) => decls + $$"""

            fn main() {
                for r in 0..25 {
                    var colors = row_colors[r]
                    for c in 0..40 { colors[c] = (r << 2) + c }
                }
                tick = 1
                {{routine}}()
            }
            """;
        var plain = Run.Program(Program("_cycle_plain"));
        var unrolled = Run.Program(Program("cycle_unrolled"));
        Assert.Equal(plain.Ram.Bytes.AsSpan(0xD800, 1000).ToArray(), unrolled.Ram.Bytes.AsSpan(0xD800, 1000).ToArray());
        for (int row = 0; row < 25; row++)
            for (int c = 0; c < 40; c++)
            {
                int moved = row is 0 or 12 or 24 ? c : (c + 1) % 40;
                Assert.Equal((byte)(row * 4 + moved), unrolled.Ram.Bytes[0xD800 + row * 40 + c]);
            }
        // The setup is the same in both, so the difference is the routines' (29,537 and 8,697 in the example's comment).
        Assert.True(plain.Cpu.Cycles - unrolled.Cpu.Cycles > 15_000, $"{plain.Cpu.Cycles} and {unrolled.Cpu.Cycles} cycles");
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Speedcode_listing_shows_each_generated_instruction_under_its_emit_line()
    {
        var r = TernCompiler.Compile(Source("speedcode.tern"), sourcePath: AsmTests.FindExample("speedcode.tern"));
        var listing = r.Listing.Replace("\r", "");
        int call = Array.FindIndex(Source("speedcode.tern").Replace("\r", "").Split('\n'), l => l.Trim() == "cycle_rows(rows, addr(saved), .done)") + 1;
        Assert.Contains($";  {call}: cycle_rows(rows, addr(saved), .done)\n", listing);
        // 22 rows: 22 saves and two loops of 11 lda / sta pairs, each under its emit line.
        Assert.Equal(22 * 2, CountLines(listing, $"lda c64.COLOR + which[k] * 40", call));
        Assert.Equal(2, CountLines(listing, "bne top", call));
        Assert.Contains("jmp cycle_unrolled.done", listing);
        Assert.Contains("sta saved+21", listing);

        static int CountLines(string listing, string emit, int call) =>
            listing.Split('\n').Count(l => l.Contains($": {emit}") && l.EndsWith($"(generated by line {call})"));
    }
}
