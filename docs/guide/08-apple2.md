# 8. Apple II

[Guide index](README.md) · previous: [7. Atari 8-bit](07-atari.md) · next: [9. NES](09-nes.md)

## 8.1 The target

`--target apple2` builds for every Apple II with 64 KB under ProDOS 8: a II+ with a language card, the IIe, the IIc and the IIgs. The program is a ProDOS system file (`.system`), which ProDOS loads and starts at `$2000`.

- Memory: code from `$2000`, variables up to `$BEFF` (page `$BF` is ProDOS's); zero page `$50-$FF`, then `$00-$1F`. The bytes the monitor, COUT and ProDOS use are left alone.
- The startup code sets up 40-column text on page 1 with the ROM's input and output hooks (and turns 80-column firmware off), points reset at a routine that closes files and quits, then zeroes globals, copies initial values and calls `main`. When `main` returns, the program stops in an endless loop; `apple2.exit()` closes every file and quits to ProDOS. A program that imports `io` also sets the prefix to its own folder if the launcher left it empty, so partial names find files next to the program.
- The soft switches and I/O at `$C000-$CFFF` are volatile with no keyword. The Apple II has no interrupt source of its own.
- Strings are in the `apple2` encoding: ASCII with bit 7 set, which COUT prints and screen memory shows as normal text. A II+ shows lower case as other characters.
- The compiler makes no disks: `pwsh tests/tools/apple2-disk.ps1 game.system game.po` puts the program on a copy of the ProDOS 2.4.3 boot disk as its first system file, so ProDOS starts it at boot; overlays follow with their load address (`level1.bin@$6000`). Boot the disk in AppleWin; check on an enhanced IIe (`-model apple2ee`) and a II+ with a language card (`-model apple2p -s0 languagecard`). The `.sym` file loads the labels in AppleWin's debugger.

## 8.2 Text

```
import apple2
import text
import math

data HELLO = "HELLO FROM TERN!\n"
data SQUARES = "SQUARES: "
data INVERSE = "INVERSE"

var digits: [u8; 5]

fn main() {
    apple2.print(addr(HELLO), len(HELLO))
    apple2.print(addr(SQUARES), len(SQUARES))
    for i in 1..=8 {
        // text writes digits in apple2 codes, $B0-$B9, which COUT prints.
        var n: u8 = text.dec8(addr(digits), math.mul8(i, i) as u8)
        apple2.print(addr(digits), n)
        apple2.put(' ')
    }
    apple2.cursor(4, 0)
    apple2.inverse()
    apple2.print(addr(INVERSE), len(INVERSE))
    apple2.normal()
    apple2.put('\n')
}
```

`apple2.put` is COUT itself, a routine at a fixed address (`jsr $FDED`). `inverse()`, `flashing()` and `normal()` set how COUT shows what follows. The text screen's rows are not in order in memory; `apple2.TEXT_ROWS` gives each row's address.

## 8.3 Hi-res graphics, keyboard and sound

The hi-res pages are `$2000-$3FFF` and `$4000-$5FFF`, and the default program area starts on page 1, so a program that draws moves out of the way with a `memory` block:

```
memory {
    $6000..=$BEFF program
}
```

The startup code moves the program's areas into place before anything else runs, so the file is not padded over the hi-res page. Then `apple2.show_hires()`, `show_graphics()` and `show_full()` switch the display, `apple2.clear()` clears the page `apple2.draw_page` names (`$20` or `$40`), and `apple2.plot(x, y)` sets a pixel (x 0-279, y 0-191). `apple2.key()` returns the waiting key with bit 7 set, and takes it, or 0; `apple2.read_key()` waits for one. `apple2.tone(half, count)` plays a square wave on the speaker. `examples/apple2/hires.tern` draws on hi-res page 1, with four rows of text under it (`show_mixed()`).

The module also has the monitor's routines (`home`, `put_hex`, `bell`, `wait`) and `apple2.mli(command, parameters)`, a call to ProDOS that returns its error code.

## 8.4 Files: `io`

The Apple II's `io` has the same functions as the Atari's where the machine allows, through the ProDOS MLI. One file is open at a time, since ProDOS needs a 1 KB buffer for each, which `io` declares page-aligned. Names are ProDOS paths in the `apple2` encoding, partial (`"SCORES"`, next to the program) or full (`"/GAMES/SCORES"`):

| Function | Purpose |
|---|---|
| `open(name, n, mode)`, `close()`, `read(dst, n)`, `write(src, n)` | A file, opened `io.READ` or `io.WRITE` (which creates it, or replaces the old one) |
| `load(name, n, dst, max)`, `save(name, n, src, count)` | A whole file; `save` writes a BIN file with `src` as its aux type, as BSAVE does |
| `load_bin(name, n)` | Load a BIN file at the address in its aux type: the loader for overlays ([3.5](03-memory.md#35-overlays)) |
| `status()` | The MLI's error code for the last call, 0 for success; `ST_OK`, `ST_EOF`, `ST_NOT_FOUND` |

Results that are `bool` are true on failure. Overlays are written as `.bin` files, and the disk script stores them with their load address as aux type, ProDOS's own convention. `examples/apple2/files.tern` saves a file, loads it back and loads an overlay.

## 8.5 `tern run` on the Apple II

The built-in emulator has stand-ins for COUT, HOME, the routines the startup code calls, and the MLI's CLOSE and QUIT, and reads the build's own overlay files by name in capitals (`"LEVEL1.BIN"`); a write stops the run, and so does a call to another ROM routine (`read_key`, `bell`, `wait`, `put_hex`). `--screen` shows the text screen, in the Apple II's row order. The keyboard `$C000` and the buttons `$C061`-`$C063` read below `$80` (no key, no button); `--set $C000=$C1` gives a pressed `A`. Graphics are checked in AppleWin.

## 8.6 Examples

In `examples/apple2/`: `hello.tern` (text, inverse and numbers), `hires.tern` (hi-res graphics), `files.tern` with `greet.tern` (files and an overlay).

Not built yet: interrupt helpers (an interrupt needs a card) and waiting for the vertical blank, which differs between models.
