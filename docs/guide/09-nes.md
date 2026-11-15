# 9. NES

[Guide index](README.md) · previous: [8. Apple II](08-apple2.md) · next: [10. How Tern programs behave](10-behavior.md)

## 9.1 The target

`--target nes` writes a `.nes` file, an iNES cartridge image that emulators and flash cartridges read. Every NES program is a cartridge, so its `memory` block names the board (the cartridge's wiring) and its mirroring:

| Board | Program ROM | Tiles | Banks |
|---|---|---|---|
| `nrom` (mapper 0) | 16 or 32 KB at `$8000`, the size following from the program | 8 KB of ROM, a `chr` area | none |
| `uxrom` (mapper 2) | 16 KB banks at `$8000-$BFFF`; the last bank fixed at `$C000-$FFFF` | 8 KB of RAM, loaded by the program | up to 16 ([9.5](#95-rom-banks)) |

- Memory: code and `data` in ROM; variables in RAM at `$0200-$07FF`; all of zero page `$00-$FF` is free, since there is no OS. 2 KB of RAM is little, so frame sharing and zero page matter most. A global's initial value is kept in ROM and copied to RAM at startup.
- The startup code (the reset vector) turns interrupts and rendering off, selects bank 0 on `uxrom`, waits two frames for the PPU while it clears RAM, copies initial values and calls `main`. When `main` returns, the program stops in an endless loop and the NMI handler keeps running.
- The PPU, APU and controller registers at `$2000-$401F` are volatile with no keyword.
- The NES has no character set, so there is no default encoding: a program declares one for its tiles ([2.12](02-language.md#212-encodings)).
- A `program` line in the `memory` block is an error, since the board gives the code areas; `reserved` and `region` lines narrow ROM and RAM as elsewhere.
- Run it in Mesen: `Mesen game.nes`. The `.mlb` file next to it gives Mesen the program's labels.

## 9.2 A first program

```
import nes

memory {
    cartridge nrom vertical
    $0000..=$1FFF  chr art          // the tile ROM, in the PPU's addresses
}

encoding tiles {
    '0'..='9' = $00
    'A'..='Z' = $0A
    ' '       = $24
}
default encoding tiles

data font @ art = include_bytes("font.chr")
data TITLE = "HELLO"
data PALETTE: [u8; 32] = [$0F, $30, $10, $00, $0F, $30, $10, $00, $0F, $30, $10, $00, $0F, $30, $10, $00,
                          $0F, $16, $27, $18, $0F, $16, $27, $18, $0F, $16, $27, $18, $0F, $16, $27, $18]

var x: u8 = 120

interrupt fn frame() {
    nes.vblank()                       // sprites by DMA, count the frame
    nes.scroll(0, 0)                   // last in vblank
}
data nmi_vector: u16 @ $FFFA = addr(frame)

fn main() {
    nes.set_palette(addr(PALETTE))     // rendering is off after the startup code
    nes.ppu_fill($2000, ' ', 960)
    nes.ppu_copy(addr(TITLE), $2000 + 12 * 32 + 13, len(TITLE))
    nes.hide_sprites()
    nes.oam[0].y = 100
    nes.oam[0].tile = 'A'
    nes.render_on()
    nes.ppu.ctrl = nes.NMI_ON
    while true {
        nes.wait_frame()
        var b = nes.pad(1)
        if b & nes.BTN_LEFT != 0 { x -= 1 }
        if b & nes.BTN_RIGHT != 0 { x += 1 }
        nes.oam[0].x = x
    }
}
```

This is how most NES games are built: game logic in `main`, PPU work in the NMI handler. `nes.vblank()` is a part ([2.9](02-language.md#29-interrupts)) of your handler that sends the sprite buffer `nes.oam` to the PPU and counts the frame, and `nes.wait_frame()` waits for that count to change. PPU memory (palettes, nametables, CHR RAM) is written with rendering off, as here, or inside vblank; `nes.scroll` comes last in vblank, since writing the PPU address changes the scroll position.

## 9.3 Interrupt vectors

The 6502 takes its handlers' addresses from `$FFFA` (NMI), `$FFFC` (reset) and `$FFFE` (IRQ). On the NES these are bytes of the cartridge, so the program sets them when the image is built, with `data`:

```
data nmi_vector: u16 @ $FFFA = addr(frame)
```

- Each must be the address of an `interrupt fn` or `asm fn`; a plain `fn` ends with `rts` and is an error. A vector the program does not set points to an `rti`.
- Reset is the compiler's: `data` at `$FFFC` is an error, since the program starts in `main` after the startup code.
- A vector may not point into a bank area: an interrupt can come with any bank selected. Keep the handler in the fixed bank, and call bank code from it through an interface ([9.5](#95-rom-banks)).
- A handler that changes at run time is a fixed handler that jumps through a variable.

## 9.4 Tiles

On `nrom` a `chr` line declares the tile ROM, with a name of your choice, and `data` placed there goes into it: `@ art` in order from the start, `@ art + $1000` at an offset, which is also the PPU address when the area starts at `$0000`. Any initializer works, `include_bytes` or a table from a `const fn`; a tile is 16 bytes (bitplane 0, then bitplane 1). The CPU cannot read it, so code may use only `addr(font)`, its PPU address, and `len(font)`; `font[i]` is an error.

On `uxrom` the tiles are in CHR RAM, empty at power-on: they are ordinary `data` in a bank, which the program copies to the PPU with `nes.ppu_copy(addr(tiles), $0000, len(tiles))`. A `chr` line is an error there.

## 9.5 ROM banks

Banks work like overlays ([3.5](03-memory.md#35-overlays)), except that the board switches a bank in instead of loading a file. Modules are placed in a bank on their import, a call into a bank is a plain `jsr`, and the program selects the bank first. From `examples/nes/scroll.tern`:

```
import nes
import uxrom
import art @ world[ART]          // bank 0: the graphics and the code that loads them
import motion @ world[MOTION]    // bank 1: the sprites' motion

memory {
    cartridge uxrom vertical
    $8000..=$BFFF bank world
}

const ART = 0
const MOTION = 1

fn main() {
    uxrom.select(ART)
    art.load()                   // jsr into bank 0
    uxrom.select(MOTION)
    ...
    motion.update()
}
```

- Code in the fixed bank (the main file, the library, modules imported without `@`) is always visible. Modules in the same bank may call each other and use each other's `data`; calls and data uses between two banks of one area are errors, since they are never visible together. The bank is on the import only, not on single functions; a binary file goes in a bank through a module that holds its `data` line.
- A bank's global variables are in RAM, with initial values set at startup.
- The image has as many banks as the highest number used needs, with the fixed bank, rounded up to a power of two (UxROM: up to 16). A named constant used both on the import and in `uxrom.select` keeps the two in step.
- `uxrom.select(n)` writes through a table in the fixed bank, since the board sees the ROM's byte as well as the CPU's (a bus conflict).
- `implements` ([3.6](03-memory.md#36-overlay-interfaces)) works for banks too, with each implementing module in its own bank. An NMI handler can then call whichever bank is selected:

```
import level1 @ levels[0] implements level_api
import level2 @ levels[1] implements level_api

interrupt fn frame() {
    nes.vblank()
    level_api.on_frame()         // jsr $8000: the selected bank's version
}
```

Which bank is selected is not checked ([10.9](10-behavior.md#109-overlays-and-banks)).

## 9.6 The `nes` library

| Name | Purpose |
|---|---|
| `ppu`, `apu` | The registers as structs: `nes.ppu.ctrl`, `mask`, `status`, `scroll`, `address`, and `value` for PPUDATA |
| `NMI_ON`, `SHOW_BG`, `VBLANK`, ... | The registers' bits |
| `oam: [Sprite; 64]`, `hide_sprites()` | The sprite buffer (`y`, `tile`, `attributes`, `x`; a `y` of 239 or more hides a sprite), page-aligned for DMA |
| `vblank()`, `wait_frame()`, `frames` | The frame loop |
| `ppu_address`, `ppu_copy`, `ppu_fill`, `set_palette`, `scroll`, `render_on`, `render_off` | PPU memory and the display |
| `pad(n)`, `BTN_A` ... `BTN_RIGHT` | The buttons of pad 1 or 2 |

`uxrom` (`import uxrom`) has `select(n)`. Not built yet: sound, text helpers, and the MMC1 board.

## 9.7 `tern run` on the NES

The built-in emulator models PPU memory and sprite memory, not pixels: writes through `$2006`/`$2007` go to a 16 KB PPU memory, and DMA to the 256-byte OAM. `$2002` reports vblank from the start of each frame until a read or the end of vblank, an NMI comes every frame when bit 7 of `$2000` is set (at once if it is set during vblank), a `brk` runs your IRQ handler if you set one, and writes to the UxROM latch switch the bank at `$8000`. The controllers read as no buttons. `--screen` shows nametable 0, each tile as the character the program's default encoding gives it. Graphics are checked in Mesen.

## 9.8 Examples

In `examples/nes/`: `hello.tern` (a font in CHR ROM and a line of text, on NROM), `scroll.tern` with `art.tern` and `motion.tern` (a scrolling background and bouncing sprites, on UxROM with code in two banks).
