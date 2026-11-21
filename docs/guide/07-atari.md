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
