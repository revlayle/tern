namespace Tern.Compiler.Tests;

/// <summary>
/// lib/nes (spec 12.2): the PPU memory writes, the sprite buffer and its DMA, the frame loop through
/// vblank() and wait_frame(), and the controllers, run in the emulator's model of the NES.
/// </summary>
public class NesLibraryTests
{
    private const string Head = "import nes\nmemory {\n    cartridge nrom vertical\n}\n";

    private static Run RunNes(string source, Func<long, int, byte>? buttons = null) =>
        Run.Program(Head + source, platform: Platform.Nes, buttons: buttons);

    [Fact]
    public void Ppu_copy_writes_ppu_memory_from_the_address_on()
    {
        var r = RunNes("""
            data row: [u8; _] = [n for n in 0..=255]
            data rest: [u8; _] = [n + 1 for n in 0..44]
            fn main() {
                nes.render_off()
                nes.ppu_copy(addr(row), $2000, 300)
            }
            """);
        var ppu = r.Nes!.Ppu;
        Assert.Equal(Enumerable.Range(0, 256).Select(i => (byte)i), ppu[0x2000..0x2100]);
        // The 44 bytes after the first 256 are the data after 'row': 'rest'.
        Assert.Equal(Enumerable.Range(1, 44).Select(i => (byte)i), ppu[0x2100..0x212C]);
        Assert.Equal(0, ppu[0x212C]);
        Assert.Equal(0, r.Nes.Mask);
    }

    [Fact]
    public void Ppu_fill_and_a_nametable_seen_through_vertical_mirroring()
    {
        var r = RunNes("""
            fn main() {
                nes.ppu_fill($2400, 7, 960)
                nes.ppu_fill($2C00, 9, 1)
            }
            """);
        var table = r.Nes!.Nametable(1);
        Assert.All(table.SelectMany(row => row).Skip(1), b => Assert.Equal(7, b));
        // $2C00 is $2400 with vertical mirroring.
        Assert.Equal(9, table[0][0]);
        Assert.Equal(9, r.Nes.Nametable(3)[0][0]);
        Assert.All(r.Nes.Nametable(0).SelectMany(row => row), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Set_palette_writes_the_32_palette_bytes_with_their_mirrors()
    {
        var r = RunNes("""
            data colors: [u8; _] = [n + $10 for n in 0..32]
            fn main() { nes.set_palette(addr(colors)) }
            """);
        var nes = r.Nes!;
        for (int i = 0; i < 32; i++)
            if (i % 4 != 0 || i >= 16) Assert.Equal(0x10 + i, nes.Ppu[nes.Map(0x3F00 + i)]);
        // $3F10, $3F14, $3F18 and $3F1C are $3F00, $3F04, ...: the sprite palettes' first bytes were written last.
        Assert.Equal(0x10 + 16, nes.Ppu[nes.Map(0x3F00)]);
        Assert.Equal(0x10 + 20, nes.Ppu[nes.Map(0x3F04)]);
    }

    [Fact]
    public void Scroll_and_rendering()
    {
        var r = RunNes("""
            fn main() {
                nes.ppu_address($2000)
                nes.scroll(123, 45)
                nes.render_on()
            }
            """);
        Assert.Equal(123, r.Nes!.ScrollX);
        Assert.Equal(45, r.Nes.ScrollY);
        Assert.Equal(0x1E, r.Nes.Mask);
    }

    [Fact]
    public void The_frame_loop_sends_the_sprites_by_dma_and_counts_frames()
    {
        var r = RunNes("""
            var seen: u8
            interrupt fn frame() {
                nes.vblank()
            }
            data nmi_vector: u16 @ $FFFA = addr(frame)
            fn main() {
                nes.hide_sprites()
                nes.oam[0].y = 100
                nes.oam[0].tile = 1
                nes.oam[0].attributes = nes.FLIP_X | 2
                nes.oam[0].x = 50
                nes.oam[63].x = 7
                nes.ppu.ctrl = nes.NMI_ON
                nes.wait_frame()
                nes.wait_frame()
                seen = nes.frames
            }
            """);
        var nes = r.Nes!;
        Assert.Equal(0, r.Address("nes.oam") % 256);
        Assert.Equal([100, 1, 0x42, 50], nes.Oam[..4]);
        Assert.Equal(0xFF, nes.Oam[4]);
        Assert.Equal(0xFF, nes.Oam[252]);
        Assert.Equal(7, nes.Oam[255]);
        Assert.Equal(2, r.U8("seen"));
        Assert.True(nes.Dmas >= 2);
        // The handler saves only what the part changes, and ends with rti.
        Assert.Contains("rti", r.Result.Listing);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x80)]
    [InlineData(0x01)]
    [InlineData(0x91)]
    [InlineData(0xFF)]
    public void Pad_reads_the_buttons_as_bits(int buttons)
    {
        var r = RunNes("""
            var one: u8
            var two: u8
            fn main() {
                one = nes.pad(1)
                two = nes.pad(2)
                if one & nes.BTN_A != 0 { two |= 0 }
            }
            """, (_, pad) => (byte)(pad == 1 ? buttons : buttons ^ 0xFF));
        Assert.Equal(buttons, r.U8("one"));
        Assert.Equal(buttons ^ 0xFF, r.U8("two"));
    }

    [Fact]
    public void Buttons_follow_the_frame()
    {
        // Right in frame 3 and later: the loop waits for it.
        var r = RunNes("""
            var frames: u8
            interrupt fn frame() { nes.vblank() }
            data nmi_vector: u16 @ $FFFA = addr(frame)
            fn main() {
                nes.ppu.ctrl = nes.NMI_ON
                while nes.pad(1) & nes.BTN_RIGHT == 0 { nes.wait_frame() }
                frames = nes.frames
            }
            """, (frame, pad) => (byte)(frame >= 3 && pad == 1 ? 0x01 : 0));
        Assert.InRange(r.U8("frames"), 1, 3);
        Assert.True(r.Nes!.Frame >= 3);
    }
}
