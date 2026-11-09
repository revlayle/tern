namespace Tern.Compiler.Tests;

/// <summary>
/// M20 follow-up, "done when": the Galaxy Stars port's workarounds for gaps 1, 3, 4, 5, 7, 8, 9 and
/// 10 (galaxy-star/tern/gaps.md) compile and run in their plain form, taken from the port's code.
/// </summary>
public class GalaxyStarsGapTests
{
    private const string Assets = """
        const FIRST_CHAR: u8 = 64           // chars.bin starts at this char code
        data chars_bin: [u8; _] = [1, 2, 3, 4, 5, 6, 7, 8]
        """;

    private const string Input = """
        // Gap 10: the length from the list. Gap 7: the pointer set in its declaration, no init().
        data default_script: [u8; _] = [
            100, 0,
            0, 0
        ]
        var script: ptr u8 = addr(default_script)
        var first: u8
        fn read() { first = script[0] }
        """;

    private const string Main = """
        import mem
        import c64
        import assets
        import input

        const CHARSET = $E000

        // Gaps 8 and 9: constants computed exactly, with no 'as'.
        const BOX: u8 = 13
        const MAX_X: u16 = 320 - BOX
        const INVINCIBLE_FRAMES: u16 = 400
        const INVINCIBLE_SECS: u8 = INVINCIBLE_FRAMES / 50

        var spr_x: [u8; 8]
        var spr_y: [u8; 8]
        var max_x: u16
        var secs: u8
        var length: u8

        fn main() {
            // Gaps 4 and 5: a constant sum with a typed u8 constant, passed as a pointer.
            mem.copy(CHARSET + assets.FIRST_CHAR * 8, addr(assets.chars_bin), len(assets.chars_bin))
            for n in 0..8 {
                spr_x[n] = 24 + (n << 4)
                spr_y[n] = 50 + n
            }
            // Gap 3: the library's indexable sprite positions.
            for n in 0..8 {
                c64.sprite_xy[n << 1] = spr_x[n]
                c64.sprite_xy[(n << 1) + 1] = spr_y[n]
            }
            max_x = MAX_X
            secs = INVINCIBLE_SECS
            length = len(input.default_script)
            input.read()
        }
        """;

    [Fact]
    public void The_workarounds_for_gaps_3_to_10_are_written_plainly()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "assets.tern"), Assets);
            File.WriteAllText(Path.Combine(dir.FullName, "input.tern"), Input);
            var r = Run.Program(Main, sourcePath: Path.Combine(dir.FullName, "main.tern"));
            Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], r.Ram.Bytes.AsSpan(0xE000 + 64 * 8, 8).ToArray());
            for (int n = 0; n < 8; n++)
            {
                Assert.Equal(24 + 16 * n, r.Ram.Bytes[0xD000 + 2 * n]);
                Assert.Equal(50 + n, r.Ram.Bytes[0xD001 + 2 * n]);
            }
            Assert.Equal(307, r.U16("max_x"));
            Assert.Equal(8, r.U8("secs"));
            Assert.Equal(4, r.U8("length"));
            Assert.Equal(100, r.U8("input.first"));
            Assert.Equal(r.Address("input.default_script"), r.U16("input.script"));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Gap_1_the_frame_interrupt_with_the_kernal_left_in()
    {
        // start_frame_irq() from video.tern with set_kernal_irq and no kernal_off.
        var r = Run.Program("""
            import c64
            const FRAME_IRQ_LINE = 250
            var frames: u8
            var seen: u8
            interrupt fn frame_irq() {
                c64.vic.irq_status = $FF
                frames += 1
            }
            fn start_frame_irq() {
                c64.set_kernal_irq(addr(frame_irq))
                c64.vic.control1 &= $7F
                c64.vic.raster = FRAME_IRQ_LINE
                c64.vic.irq_enable = 1
                c64.vic.irq_status = $FF
                c64.interrupts_on()
            }
            fn main() {
                c64.cia1.icr = $7F
                start_frame_irq()
                while frames < 5 { }
                seen = frames
            }
            """, irqEvery: 1000, setup: C64LibraryTests.KernalIrqEntry);
        Assert.True(r.U8("seen") >= 5);
        Assert.Equal(250, r.Ram.Bytes[0xD012]);
        Assert.Equal(0x7F, r.Ram.Bytes[0xDC0D]);
    }
}
