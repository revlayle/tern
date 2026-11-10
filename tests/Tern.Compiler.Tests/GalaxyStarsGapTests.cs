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

    // M20 follow-up 3, "done when": the workarounds for gaps 11, 12, 15, 17, 18, 19 and 24 in their
    // plain form, in the shape of the port's video, stars and explode modules (an import ring).

    private const string Video = """
        import stars
        const MC: u8 = 8
        const MAP_W: u8 = 20
        const MAP_H: u8 = 12
        const SCREEN = $C000
        const SPECK_KINDS: u8 = 5
        data star_char: [u8; _] = [70, 74, 78, 82, 86, 90, 94, 98]
        const STAR_KINDS: u8 = len(star_char)           // gap 11: len() of data in a constant
        var specks: [u8; 16]
        data tile_row_addr: [ptr u8; _] = [SCREEN + 40, SCREEN + 120]

        // Gap 15: the work variables are the asm fn's own, and 'scr' is also a local of fill.
        asm fn draw_tile(col: u8 using A, row: u8 using Y) {
            var scr: ptr u8
            var co: u8
            asl
            clc
            adc tile_row_addr,y
            sta scr
            lda tile_row_addr+2,y
            adc #0
            sta scr+1
            lda stars.shown
            sta co
            ldy #0
            sta (scr),y
        }
        fn fill(n: u8) {
            var scr: ptr u8 = SCREEN
            scr[n] = n
        }
        """;

    private const string Stars = """
        import video
        import explode
        var shown: u8 = explode.COLOR
        var rnd_x: u8
        var rnd_c: u8 = $C2
        var rnd_a: u8 = $11
        var rnd_b: u8 = $37

        asm fn rnd() -> u8 {
            inc rnd_x
            clc
            lda rnd_x
            eor rnd_c
            eor rnd_a
            sta rnd_a
            adc rnd_b
            sta rnd_b
            lsr
            eor rnd_a
            adc rnd_c
            sta rnd_c
        }
        fn rand_below(n: u8) -> u8 {
            var mask: u8 = 255
            if n <= 2 { mask = 1 } else if n <= 4 { mask = 3 } else if n <= 8 { mask = 7 }
            while true {
                var r: u8 = rnd() & mask
                if r < n { return r }
            }
        }
        fn place_specks() {
            for i in 0..len(video.specks) {
                // Gap 12: two calls in one expression, the left one first, as in Prog8.
                video.specks[i] = (rand_below(video.SPECK_KINDS) + 1) << 2 | rand_below(4)
            }
        }
        """;

    private const string Explode = """
        import video
        const COLOR: u8 = 2 | video.MC                          // gap 17: across the import ring
        var cover: [u8; video.MAP_W * video.MAP_H] @ $C600
        var frame: u8
        fn step(s: u8) { frame += s }                           // gap 18: 'step' as a name
        """;

    private const string RingMain = """
        import video
        import stars
        import explode

        const BOX: u8 = 12
        var galaxy_text: [u8; _] = screen"GALAXY 00\0"         // gap 24
        var px: u8 = 100
        var py: u8 = 50
        var hx: [u8; 2] = [95, 200]
        var hy: [u8; 2] = [45, 50]
        var hits: u8
        var kinds: u8
        var total: u8
        var cover_size: u8

        fn check_player() {
            for i in 0..2 {
                // Gap 19: the condition continues after '&&' at the end of a line.
                if px < hx[i] + BOX && hx[i] < px + BOX &&
                        py < hy[i] + BOX && hy[i] < py + BOX {
                    hits += 1
                }
            }
        }

        fn main() {
            stars.place_specks()
            video.draw_tile(3, 1)
            video.fill(5)
            explode.step(2)
            explode.step(3)
            var step: u8 = 4
            total = step
            check_player()
            kinds = video.STAR_KINDS
            cover_size = len(explode.cover)
            galaxy_text[8] = galaxy_text[8] + 1
        }
        """;

    /// <summary>The port's random generator (Prog8's x-abc) and rand_below, for the specks.</summary>
    private static byte[] Specks(int count)
    {
        int x = 0, c = 0xC2, a = 0x11, b = 0x37;
        int Rnd()
        {
            x = (x + 1) & 0xFF;
            a = x ^ c ^ a;
            b = (a + b) & 0xFF;
            int carry = b & 1;
            c = (((b >> 1) ^ a) + c + carry) & 0xFF;
            return c;
        }
        int RandBelow(int n)
        {
            int mask = n <= 2 ? 1 : n <= 4 ? 3 : n <= 8 ? 7 : 255;
            while (true) { int r = Rnd() & mask; if (r < n) return r; }
        }
        var result = new byte[count];
        for (int i = 0; i < count; i++)
        {
            int kind = RandBelow(5) + 1;
            result[i] = (byte)((kind << 2) | RandBelow(4));
        }
        return result;
    }

    [Fact]
    public void The_workarounds_for_gaps_11_to_24_are_written_plainly()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "video.tern"), Video);
            File.WriteAllText(Path.Combine(dir.FullName, "stars.tern"), Stars);
            File.WriteAllText(Path.Combine(dir.FullName, "explode.tern"), Explode);
            var r = Run.Program(RingMain, sourcePath: Path.Combine(dir.FullName, "main.tern"));
            Assert.Equal(Specks(16), r.Ram.Bytes.AsSpan(r.Address("video.specks"), 16).ToArray());
            Assert.Equal(10, r.Ram.Bytes[0xC000 + 120 + 6]);          // draw_tile(3, 1) wrote shown = COLOR
            Assert.Equal(5, r.Ram.Bytes[0xC005]);
            Assert.Equal(5, r.U8("explode.frame"));
            Assert.Equal(4, r.U8("total"));
            Assert.Equal(1, r.U8("hits"));
            Assert.Equal(8, r.U8("kinds"));
            Assert.Equal(240, r.U8("cover_size"));
            Assert.Equal(10, r.Result.Variables["galaxy_text"].Size);
            Assert.Equal(49, r.U8("galaxy_text", 8));                   // screen code '0' + 1

            // Gap 23: importing the main file is an error that says what to do.
            File.WriteAllText(Path.Combine(dir.FullName, "demo.tern"), "import main\nfn steer() { main.check_player() }");
            var e = Assert.Throws<Tern.Compiler.Syntax.CompileError>(() => TernCompiler.Compile("import demo\n" + RingMain, null, Path.Combine(dir.FullName, "main.tern")));
            Assert.Equal("'main' is the program's main file, which cannot be imported. Move what demo needs from it into a module that both import", e.Description);
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
