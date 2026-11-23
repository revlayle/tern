# 7. Atari 8-bit

[Guide index](README.md) · previous: [6. Commodore 64](06-c64.md) · next: [8. Apple II](08-apple2.md)

## 7.1 The target

`--target atari` builds for every Atari 8-bit with 48 KB or more: the 800, a 400 expanded to 48 KB, and every XL and XE, with the 800's OS or the XL/XE OS. The program is a `.xex` of load segments, one per used area, with a run address; DOS, or Altirra, loads and starts it.

- Memory: code from `$2000`, above DOS; variables up to `$BC1F`, below the screen the OS sets up; zero page `$80-$FF`. A 64 KB XL/XE gives no more room, since its extra 16 KB lies under the OS ROM, which Tern programs keep.
- The startup code zeroes globals, copies initial values and calls `main`; when `main` returns, the program stops in an endless loop. `atari.exit()` returns to DOS.
- The OS's frame clock, attract mode, page 2 (vectors, shadow registers, controllers) and the chips at `$D000-$D7FF` are volatile with no keyword.
- Strings are ATASCII; `atari_screen"..."` gives the codes screen memory takes.
- The library calls only the OS's documented entry points and never writes PORTB, so it works on every model. A machine with 16 KB is reached through a `memory` block (`$2000..=$3BFF program`).
- Run it in Altirra: `Altirra64 game.xex`. The `.lbl` file next to it gives Altirra's debugger the program's labels. Check on an 800 with 48 KB and on an 800XL (`/hardware:800 /memsize:48K`, `/hardware:800xl /memsize:64K`).

## 7.2 Text

```
import atari
import text
import math

data HELLO = "Hello from Tern!\n"
data SQUARES = "Squares: "

var digits: [u8; 5]

fn main() {
    atari.print(addr(HELLO), len(HELLO))
    atari.print(addr(SQUARES), len(SQUARES))
    for i in 1..=8 {
        // text writes digits as $30-$39, which are also their ATASCII codes.
        var n: u8 = text.dec8(addr(digits), math.mul8(i, i) as u8)
        atari.print(addr(digits), n)
        atari.put(' ')
    }
    atari.put('\n')
}
```

`atari.put`, `atari.print` and `atari.cursor` go through the OS's screen editor, so `'\n'` ends a line and `$7D` clears the screen. The `text` library writes numbers in ATASCII, so `atari.print` shows them; screen memory itself takes `atari_screen` codes.

## 7.3 Display lists

ANTIC draws the screen from a display list, a table it reads every frame. The `atari` module builds a whole-screen list at compile time with a `const fn`, so it is ordinary `data`:

```
import atari

const SCREEN = $4000                // 40 x 24 characters
const LIST = $3C00

memory { SCREEN..=SCREEN + 959 reserved }

// 24 rows of text from SCREEN, a DLI at the end of rows 0 to 22.
data list: [u8; _] @ LIST = atari.display_list(atari.MODE_TEXT, 24, SCREEN, LIST, (1 << 23) - 1)

fn main() {
    atari.savmsc = SCREEN           // the screen editor writes to this screen
    atari.set_display_list(addr(list))
}
```

The list may not cross a 1 KB boundary, nor the screen a 4 KB one; `display_list` gives a compile error when they do. The `DL_` and `MODE_` constants are there for lists written by hand. The OS's shadow registers (`atari.color`, `atari.sdmctl`, `atari.chbas`, ...) are copied to the chips every frame, so most programs write those rather than the chips.

## 7.4 Interrupts

The OS takes every interrupt and calls the program's handlers through vectors; a handler is an ordinary `interrupt fn`.

- `atari.set_dli(addr(dli))` installs a display-list interrupt and turns DLIs on. It runs at the end of each row whose display-list byte has `DL_DLI` set.
- `atari.set_vbi(addr(frame))` installs a handler that runs once a frame after the OS's frame work (shadow registers, clock, controllers). A stub undoes the OS's register pushes, so the handler is a plain `interrupt fn` (about 20 cycles a frame).
- `atari.set_vbi_immediate(addr(frame))` runs first in the vertical blank, and the OS's frame work then no longer runs.
- `atari.wait_frame()` waits for the next frame.

`examples/atari/dli.tern` gives each row of text its own background color: a DLI sets the next row's color, and a VBI starts the colors again each frame.

**Frame time.** ANTIC's DMA takes its cycles from the CPU: a full-width character mode with player-missile graphics on every line leaves about 20,000 of a PAL frame's 35,568 cycles, and about 15,000 of an NTSC frame's 29,868. A game whose frame runs long now and then should not lose a tick for it. Count ticks in the VBI and run one whenever one is owed, so a long frame borrows from the next:

```tern
var owed: u8

interrupt fn frame() {
    if owed < 2 {
        owed += 1
    }
}

fn wait_tick() {
    while owed == 0 { }
    owed -= 1
}
```

A game timed for PAL keeps its speed on NTSC when the VBI adds no tick every sixth frame. The OS's frame work costs about 1,500 cycles; a handler installed with `set_vbi_immediate` replaces it, and copies only the shadow registers the program uses and reads the joystick itself (`atari.pia.porta`, `atari.gtia_in.trig`).

## 7.5 Files: `io`

The Atari's `io` works through CIO, on one file at a time (IOCB 1). Names are ATASCII with their device, `"D1:SCORES.DAT"`:

| Function | Purpose |
|---|---|
| `open(name, n, mode)`, `close()`, `read(dst, n)`, `write(src, n)` | A file, opened `io.READ` or `io.WRITE`; counts are `u16` |
| `load(name, n, dst, max)`, `save(name, n, src, count)` | A whole file |
| `load_xex(name, n)` | Load an executable's segments without running it: the loader for overlays ([3.5](03-memory.md#35-overlays)) |
| `status()` | CIO's code for the last call: below 128 is success; `ST_OK`, `ST_EOF`, `ST_NOT_FOUND` |

Results that are `bool` are true on failure. A write's last bytes reach the disk at `close`, so a full disk shows there. `examples/atari/files.tern` saves a file, loads it back and loads an overlay.

## 7.6 `tern run` on the Atari

The built-in emulator has no ANTIC, GTIA, POKEY or PIA. CIO's screen output has a stand-in (the screen is the OS's 40 x 24 text screen, with a left margin of 2), and so have `SETVBV` and CIO's reads of the build's own overlay files, named in capitals after the device (`"D1:LEVEL1.XEX"`). Opening a file for writing stops the run. `--screen` shows the text screen. Input reads as idle: `CH` (`$2FC`) is `$FF` (no key), `STICK0`-`3` are `$0F` (centered), `STRIG0`-`3` are 1 (not pressed) and `CONSOL` reads 7 (no console key); `--set` gives other values. Display lists and interrupts are checked in Altirra.

## 7.7 Examples

In `examples/atari/`: `hello.tern` (text and numbers), `dli.tern` (a display list and display-list interrupts), `files.tern` with `greet.tern` (files and an overlay).

Not built yet: the 130XE's extra memory banks and cartridge images.

## 7.8 Techniques for games

What Atari programmers have worked out over the years, as Tern. Cycle facts come from the playermissile.com tutorials, De Re Atari and the Altirra Hardware Reference Manual; costs marked as measured come from Tern's listings.

**The frame's cycles.** A scan line is 114 cycles; a PAL frame has 312 lines (35,568 cycles), an NTSC frame 262 (29,868). ANTIC takes its share before the program gets any: 9 cycles a line for memory refresh, 1 per display-list instruction (3 with `DL_LMS`), 5 a line for player-missile graphics, and the screen's own bytes. A character mode (`MODE_TEXT`, mode 4) fetches its 40 character codes on a row's first line and 40 font bytes on every line, so a 40-column text screen of 24 rows takes about 8,600 cycles a frame; a narrow playfield (32 columns, `sdmctl` bits 0-1 = 1) takes a fifth less, a wide one (48) a fifth more. On a mode 4 line the CPU keeps about 60 cycles, and on a row's first line, with scrolling and players on, fewer than 10. Section 7.4 gives what is left of a whole frame, and how a game paces itself when a frame runs long.

**DLIs.**

- A DLI runs at the end of its row's last line; the next row's first line is the busiest, so a DLI does its stores right after `WSYNC` and leaves the rest. `WSYNC` lets the CPU go near cycle 105 of 114, in the horizontal blank, where color and position changes do not show.
- Work out the values before the `WSYNC` store, and store the value itself to `WSYNC` (any value works), so it is already in A:

  ```tern
  interrupt fn dli() {
      var c: u8 = row_color[row]
      row += 1
      atari.antic.wsync = c           // any value works; storing c leaves it in A
      atari.gtia.colpf[2] = c         // sta COLPF2, 4 cycles after WSYNC lets go
  }
  ```

- **Chain handlers** instead of testing `VCOUNT`: each DLI sets `atari.vdslst` to the next row's handler, and the VBI sets it back to the first. Handlers in one page need only the low byte changed (6 cycles less).
- Keep the VBI short: a long VBI delays the first DLI.
- A DLI may change `chbase` too, so each band of rows has its own 128 characters: more tiles, or software sprites in a character mode, than one set allows.

**Player-missile graphics.**

- Memory: single-line resolution takes 2 KB on a 2 KB boundary (`noinit var pm: [u8; 2048] @ align(2048)`; missiles at `+$300`, players 0-3 at `+$400` to `+$700`), double-line 1 KB on a 1 KB boundary (missiles at `+$180`, players at `+$200` to `+$380`). `atari.antic.pmbase = addr(pm).hi` has no shadow register; then `atari.sdmctl = $3E` (single line; `$2E` for double) and `atari.gtia.gractl = 3`.
- Horizontal moves are free (`hposp`); a vertical move rewrites the player's bytes. Pad each frame with blank rows above and below, as many as the most it moves between two draws, and a move overwrites the old image with no clear. When a move is larger, clear only the rows between the old and the new place, not the whole image.
- **Compiled sprites:** a frame as code, made by a generator ([4.3](04-compile-time.md#43-generators)): each row an immediate, rows with the same value sharing one load. The padded 11-row ship below takes about 98 cycles against 186 for a copy loop (measured), and its blank rows clear as it draws. The cost is memory: about 4 bytes of code per row.

  ```tern
  var dst: ptr u8 @ zp                // a player's memory at the sprite's top line

  const fn compiled_sprite(rows: [int; _], to: label) {
      var done = [false for i in 0..len(rows)]
      for k in 0..len(rows) {
          if !done[k] {
              emit { lda #rows[k] }
              for j in k..len(rows) {
                  if rows[j] == rows[k] {
                      done[j] = true
                      emit {
                          ldy #j
                          sta (to), y
                      }
                  }
              }
          }
      }
  }

  asm fn draw_ship() {
      compiled_sprite([0, 0, $18, $3C, $7E, $FF, $7E, $3C, $18, 0, 0], addr(dst))
  }
  ```

- **More objects than players:** a DLI moves a player (`hposp`, `colpm`) after its last line, so it shows again lower down. The work is in the VBI or the main loop: sort the objects by their top row, give each a player no object in its rows has, and build the DLIs' tables; each DLI only copies its entries. General-purpose multiplexers flicker once more than four objects share a line. A flicker-free one is designed around the game's own limits (how many objects can share a row, which never move vertically), and the object the player controls keeps a player of its own and is never the one left out.
- The four missiles side by side make a fifth player: `atari.gprior` bit 4 (`$10`) gives them `color[3]`, and their `hposm` are set 2 apart. `gprior` bit 5 (`$20`) gives a third color where players 0 and 1 (or 2 and 3) overlap.
- Collisions (`gtia_in.ppf`, `ppl`, ...) are read once a frame and cleared with `atari.gtia.hitclr`; with players reused down the screen they no longer say which object hit, so games compare positions instead.

**Scrolling.** A coarse scroll rewrites the screen addresses in the display list (an `DL_LMS` on every row), never copies the screen: 2 bytes a row. A fine scroll sets `atari.antic.hscrol` (0-15 color clocks) or `vscrol` (lines) on rows marked `DL_HSCROLL` / `DL_VSCROLL`; a horizontally scrolled row fetches the next wider playfield's bytes, so its screen rows are laid out wider and its DMA grows.

**The vertical blank.** `set_vbi` runs after the OS's frame work, about 1,500 cycles a frame (shadow registers, the clock, controllers, attract mode); the OS skips it while time-critical I/O runs. A game that needs those cycles uses `set_vbi_immediate`, copies only the shadow registers it uses, and reads the joystick from `atari.pia.porta` (player 0 in bits 0-3) and `atari.gtia_in.trig[0]`; the keyboard (`ch`) still works, since an IRQ sets it. `atari.atract = 0` once a frame keeps the colors from cycling under the OS's VBI.

**PAL and NTSC.** `atari.gtia_in.pal & $0E == 0` on a PAL machine. Colors differ a little between the two (PAL's reds lean to magenta), and a game timed in frames runs 20% faster on NTSC unless it skips a tick every sixth frame (7.4).

**Sound and random numbers.** POKEY's four channels (`atari.pokey.audf1`, `audc1`, ...) are set from the VBI from effect tables; `audctl` joins two channels for finer pitch. `atari.pokey_in.random` gives a new random byte on each read, at no cost; a game that must repeat a run (a test, a replay) keeps its own generator and seeds it from `random`.
