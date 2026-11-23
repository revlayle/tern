# Tern gap log

Findings from porting Smashy Smashy to Tern, for the Tern spec session (roadmap V1, batch 2). Format in
`AGENTS.md`. Numbered from 1; Galaxy Stars' log (`C:\game-dev\galaxy-star\tern\gaps.md`) has its own numbers.

## Summary

Step 1 done (2026-11-22, Tern `main` at `9462661`, commit `c342864`): `video.p8` and `sound.p8` ported, the
frame IRQ through `$0314`, the HUD labels and an empty level with its floor.

Step 2 done (2026-11-22): `input.p8`, `actors.p8`, `player.p8`, and with them `score.p8` and `popup.p8`, which
the player calls (`nav.tern` has only an empty `tile_destroyed()` until step 4). `main.tern` plays a fixed layout
with no enemies; 7,641 bytes. Checked in VICE with `input.SCRIPTED` on in a scratch build (run, fall off a
platform, collect two treasures with their popups, jump, fast-fall cracking a tile; the target highlight and the
timer) and with no input.

Step 3 done (2026-11-22): `levelgen.p8`, the platform-map half of `nav.p8` (the generator's route check needs
it), `video.frame_lines()`, and `main.p8`'s game flow (READY?, play, pause, the level-complete dissolve, game
over) without enemies or title; 12,740 bytes. `tools/compare.py --scenario levels` runs `levelgen.generate()`
and `nav.new_level()` of both builds in py65 from the same random state: 200 levels (levels 1, 2, 5, 9, 15),
every tile map, attempt count, random state and platform map (both table sets, every route) the same.
`generate` averages 123,448 cycles in Tern, 131,586 in Prog8 (6% fewer). The fallback layout was not reached in
those 200 (its parts, `explore` and `place_treasures`, were). In VICE: generated levels, READY?, and in scratch
builds the level-complete screens, level 2 with +60 s, and game over.

Step 4 done (2026-11-22): the rest of `nav.p8` (decisions, the carrier role, steering) and `enemies.p8`;
18,315 bytes. It compiled on the first try. `tools/compare.py --scenario play` plays generated levels in both
builds frame by frame (player, enemies, contact, popups, shake, timer, map rebuild slices) with the same random
joystick input and compares the whole game state after every frame: 40 levels (levels 1, 3, 6, 12), 60,000
frames with kills, stomps, hits, pickups, drops and map rebuilds, every frame the same. A play frame's parts
average 6,845 cycles in Tern, 8,666 in Prog8 (21% fewer). In VICE: enemies spawning, chasing, carrying
treasures and hitting the player.

Step 5 done (2026-11-22): `title.p8`, `pages.p8` and the rest of `main.p8` (the attract cycle); the whole game
is ported, 21,436 bytes (Prog8: 25,932, with its self-tests). It compiled on the first try. In VICE, at the same
cycle counts, the title (letters dropping, then the colour sweep), HOW TO PLAY and SCORING are pixel for pixel
the same as Prog8's, once a timing race in `pages.p8` is kept from showing (below). The `levels` and `play`
comparisons are unchanged.

Step 6 done (2026-11-22): `tools/compare_game.py` (`compare.py --scenario game`) runs both builds whole, from
their start addresses, with a raster line from the cycle count, a frame IRQ every PAL frame and a scripted
joystick, and compares the game state, the screen, colour RAM and the popup sprites at every `wait_frame()`
call: 3 runs of 15,000 frames (title, pages, games, levels 1-9, level complete, game over), 39,620 calls, every
one the same.

## Step 1: video.p8, sound.p8 and the frame IRQ

Ported as Galaxy Stars' `video.tern` and `sound.tern`, which came from near copies of these files: the same
frame IRQ (unrolled, now with `spr_dy` added to each y), and `sound.step` in plain Tern (Galaxy Stars measured
it as fast as the asm). Differences from the Prog8 code, none visible: `tile_color` is `data` with the MC bit in
each entry instead of a table `init()` ORs it into; `set_sprite` and `hide_sprite` take the sprite's bit from a
table (`1 << n` needs a constant count); `message()` takes the text's length and finds its row in a table of row
addresses (`row * 40` at runtime is a multiply); the HUD labels go through `text.put`.

### 1. `SCREEN + col` as a pointer argument: the first fix leads to a second error

`video.p8` `hud_digits`: `put_digits(SCREEN + col, bcd, digits)`, with `const SCREEN = $C000`, `col: u8` and
`put_digits(dst: ptr u8, ...)`.

```text
video.tern:204:16: error: 49152 does not fit in u8 (0 to 255), the type of 'col', which it is combined with. Convert 'col' with 'as u16'
```

Done as told (`SCREEN + col as u16`), the next compile says:

```text
video.tern:204:23: error: Expected ptr u8, found u16. Convert with 'as ptr u8'
```

What works: `(SCREEN + col as u16) as ptr u8`. `text.put(SCREEN + 3, ...)` with a constant needs neither, and
`tile_row_addr[row] + (col << 1)` (a `ptr u8` plus a `u8`) neither. What would have helped: the first message
giving the whole fix where a pointer is expected (`'(SCREEN + col as u16) as ptr u8'`), or an untyped address
constant plus a `u8` being a `ptr u8` where one is expected, as `SCREEN + 3` is.

## Step 2: input.p8, actors.p8, player.p8 (and score.p8, popup.p8)

Ported line by line; the differences, none visible:

- `actors.walk_anim` and `popup.draw` are assembly in Prog8 and plain Tern here (a loop over the 7 rows, not
  unrolled). Both are per-frame; their cycles are measured against Prog8's in step 6.
- `popup.p8` builds its four shifted font tables in `init()`; here they are `data` built at compile time
  (`[(font[i] << 3) & $FF for i in 0..77]`). The compile-time shift is exact, so the `& $FF` that Prog8's
  `ubyte` gives for free must be written; the error said so ("Use '& $FF' to keep the low byte").
- Prog8's `bcd.adduw(points, x)` returns the sum; Tern's `bcd` adds to a counter in memory, so `score.tern`
  adds in place: `bcd.add_byte(addr(points) as ptr u8, 2, x)`. `addr()` of a `u16` is a `ptr u16`, hence the
  `as ptr u8` (the error gives it). `LEVEL_TIME_BONUS` and `HIT_PENALTY` are `u8` (`$60`, `$10`): the
  library adds one BCD byte with the carry going up, which is all these values need.
- `input.read()`'s port reading is its own function, `read_ports()`, so `SCRIPTED` picks one of two calls.

### 2. `tern run` cannot hold the joystick for a program that also scans the keyboard

`input.p8` `read()` reads the joystick at `$DC00`, then writes `$DC00` to select keyboard rows (Run/Stop, F1)
and `$FF` after. `tern run` treats `$DC00` as plain RAM, so `--set '$DC00=$F7'` (right held) lasts until the
first `read()`: after it, `$DC00` reads `$FF`, nothing held. On a C64, port A reads the output register ANDed
with the joystick lines, so the joystick stays held. So `tern run` cannot check the player under the joystick;
this port used `input.SCRIPTED` in VICE instead. What would have helped: `tern run` modelling CIA 1 port A as
the C64 reads it (the written value ANDed with the `--set` joystick state), or a `--joy` option.

## Step 3: levelgen.p8, nav.p8's platform map, the game flow

Ported line by line; the differences, none visible:

- `levelgen.rnd()` is Prog8's `math.rnd` (the x-abc generator) as an `asm fn`, as in Galaxy Stars: its last
  `adc` takes the carry from `lsr`, which plain Tern cannot say. `seed()` is `math.rndseed` with the same reads.
- `cell_col` and `cell_row` are `data` built at compile time (`[i % 20 for i in 0..220]`), not tables `init()`
  fills.
- `band * MAX_SEGS` is `band << 2` (no runtime multiply; Prog8 makes the same shift).
- `nav.scan_row` is plain Tern here, assembly in Prog8. With it, `generate` (which runs `scan_row` for every
  row of every attempt's route check) is 6% faster than Prog8's.
- `video.frame_lines()` was left out of step 1 as self-test code by mistake: `nav.idle_build()` uses it to start
  map rebuild slices only while the frame has time left. Ported now.

### 3. `a[f()] += 1` is not supported

`levelgen.p8` `make_band`: `gap_extra[rand_below(n + 1)]++`.

```text
levelgen.tern:189:38: error: Compound assignment to an element whose index calls a function is not supported. Store the index in a variable first
```

Written as the error says: `var g: u8 = rand_below(n + 1)` then `gap_extra[g] += 1`. The message is clear; the
gap is the rule itself: `a[f()] = g()` is allowed and its order is defined (`f` first), so `a[f()] += 1`, which
calls `f` once, could be too.

### Not a Tern gap: a bug in the Prog8 game, found by the comparison

`nav.p8` `scan_row` ends with its `%asm` block and no `return`. Prog8 puts no `rts` after a sub's final asm
block (`popup.p8`'s `draw` has a `return` for this reason, with a comment), so every row scan runs on into
`make_links(p)` with `p` = A (mostly 255): junk links at the start of the link table, and `plat_link`,
`plat_nlinks` and `plat_*` reads at `bp + 255`, past the 64-entry tables. The port does what the code means
(the designer's choice, 2026-11-22). `tools/compare.py` compares against the Prog8 build with the `rts` it was
meant to have: in the simulator, a fall-through from `scan_row` into `make_links` returns instead
(`PROG8_FALLTHROUGH`). The Prog8 game itself is unchanged.

## Step 4: nav.p8's decisions, enemies.p8

Ported line by line. No new gaps: it compiled on the first try. Differences, none visible:

- `nav.find_hideout`'s two passes are assembly in Prog8 (with self-modifying loads of the row addresses) and
  plain Tern here, with three pointers to the rows.
- The self-test switches `nav.no_mistakes` and `enemies.frozen` and the debug counters are left out.

Cycles per play frame, `compare.py --scenario play` (40 levels, 60,000 frames; the frame IRQ, input reading
and the VIC are not in it; Tern's plain `walk_anim`, `popup.draw`, `scan_row` and `find_hideout` against
Prog8's assembly are inside these parts):

| Part | Prog8 | Tern |
|---|---|---|
| `score.flash_step` | 13 | 12 |
| `player.update` | 814 | 738 |
| `enemies.update` | 4,532 | 3,636 |
| `enemies.contact` | 552 | 411 |
| `player.draw` | 305 | 262 |
| `enemies.draw` | 2,175 | 1,573 |
| `popup.update` | 94 | 85 |
| `video.shake_step` | 34 | 30 |
| `score.tick` | 49 | 35 |
| `nav.idle_build` | 93 | 59 |
| all parts | 8,666 | 6,845 |

## Step 5: title.p8, pages.p8, the attract cycle

Ported line by line. No new gaps: it compiled on the first try. Differences, none visible:

- `title.move_rows` (moving a letter down one row, before the beam comes back to the letters) is assembly in
  Prog8 and plain Tern here. It keeps to the frame: in VICE the title is the same as Prog8's at the same
  cycle counts, with no torn rows.
- `title.p8` fills `row_addr` and `top_addr` in `init()`; here `top_addr` is `data` built at compile time, and
  `video.text_row` (from step 1) is `row_addr`. `1 << c` is a table (`bit`). The shapes are `data` strings
  passed with their length.
- The page texts are a table of `Line` struct literals (row, column, text, length), as in Galaxy Stars.

### Not a Tern gap: a timing race in pages.p8, shown by the faster code

On SCORING the chain values 400 and 800 (colours 8 and 10, which need the page's hi-res split below row 15)
showed garbled in the Tern build. VICE's monitor showed why: `pages.wait()` clears multicolour at line 170 as
it should, but its next `video.wait_frame()` returns at once, at line 172, and turns multicolour back on. A
`frame_ready` left over from before the page was drawn lets the first `wait_frame()` return mid-frame; if that
is between lines 170 and 251, the loop locks one frame out of step for the whole page. Prog8's slower page
drawing happens to end where it does not show; Tern's ends where it does. The port clears `frame_ready` before
the loop (as `main.p8`'s `level_start` does, "start the first frame on a fresh one"), and the page is then the
same as Prog8's. The Prog8 game has the same race; it would show if its drawing got faster.

## Step 6: the whole game side by side

`tools/compare_game.py` runs each build from its `SYS` address in py65 with hooks for what a C64 adds: `$D012`
(and bit 8 in `$D011`) from the cycle count; a frame IRQ at line 251 every 19,656 cycles through `$FFFE` to a
KERNAL IRQ entry stub at `$FFC0` (the real one, `$FF48`, is under `popup.frame_data` in RAM); the KERNAL's own
handler and its exit done in Python; CIA 1 port A from the joystick script, indexed by `input.read()` calls; other
KERNAL calls return at once. The builds run at different speeds, so four things are set apart from the game:

- `levelgen.seed()` sets the same random state in both (it reads the raster and a CIA timer);
- `video.frame_lines()` returns 61 and, inside `nav.idle_build()`, `frame_ready` reads clear, so a map rebuild
  ends in the frame it starts in both builds (the game slices it into the time each frame has left, so a faster
  build finishes it sooner, by design);
- Prog8's start-up is longer, so its `video.frames` is behind at the first call: Tern's is set to Prog8's once;
- a frame that overruns drops a frame IRQ, and then `video.frames` differs: counted, Tern's set to Prog8's, and
  that one call not compared (the float pose takes its picture from `video.frames`). The counts per run (Prog8 /
  Tern dropped more): 7 / 4, 7 / 6, 10 / 3. They were not looked into; the forced whole rebuilds above may cause
  some of them.

Every 1,500 calls `score.treasures_left` is set to 0 in both builds, which completes the level being played, since
random play rarely collects all 10 treasures.

Result: 3 runs of 15,000 frames, 39,620 `wait_frame()` calls, the game state (every actor, the enemy AI and its
routes, tiles, score and timer, popups, sprite shadows, the random generator), the screen, colour RAM and the
popup digits the same at every call; levels 1-9 reached in each run, game over in two.

## V1 fixes 2: measured again (2026-11-22)

Roadmap "V1 fixes 2", with the compiler of Tern's `v1-fixes-2` branch (gaps 1 and 3 above fixed, and Galaxy
Stars' gaps 30-33 and some small shapes). The source is unchanged; the port has no assembly left to measure, so the
numbers are the whole game's. 21,436 -> 20,944 bytes. `compare.py --scenario play` (40 levels, 52,340 frames, every
frame the same):

| Part | Prog8 | Tern before | Tern now |
|---|---|---|---|
| `score.flash_step` | 13 | 12 | 12 |
| `player.update` | 812 | 757 | 737 |
| `enemies.update` | 4,526 | 3,741 | 3,623 |
| `enemies.contact` | 556 | 414 | 414 |
| `player.draw` | 305 | 276 | 256 |
| `enemies.draw` | 2,173 | 1,647 | 1,544 |
| `popup.update` | 93 | 86 | 81 |
| `video.shake_step` | 34 | 30 | 30 |
| `score.tick` | 54 | 38 | 37 |
| `nav.idle_build` | 84 | 52 | 51 |
| all parts | 8,654 | 7,056 | 6,789 |

Most of it is zero page: temporaries and register parameters that no instruction names no longer get memory, so
more variables fit in zero page (4-cycle stores become 3-cycle ones, a byte shorter); then gap 31's fix (the 16-bit
temporaries in `nav.steer` and `nav.link_done` go, 60 bytes, 42 cycles a frame). Gap 3 changes nothing here, since
the port stores the index first; gap 1 is a message. `compare.py --scenario game`: see Tern's `docs/status.md`.

## V1 batch 3, Atari step 1: the screen, HUD, input and sound (2026-11-22)

Kept in this copy (`validation/c64/smashy-smashy/`; the batch works here, not in the game repository). The shared
modules with a new front end in `src/atari/`: `video` (a display list built at startup, the C64's `chars.bin` as
ANTIC mode 4 characters, the HUD in ANTIC mode 2, messages in a 3-pixel font made from the ROM font, two DLIs, the
shake through VSCROL, the frame interrupt as the OS's deferred VBI, `frame_lines()` from VCOUNT), `sound` (POKEY,
the C64's effect tables), `input` (STICK0/STRIG0 and Esc from POKEY); `title`, `pages` and `popup` are placeholders,
and sprites are not shown yet. `assets.tern` moved from `src/c64/` to `src/`: both front ends use the art's names
(the C64 build is byte for byte the same). It compiled on the first try; Altirra shows the title and a level being
played (no sprites) on an 800 and an 800XL.

The screen, character set and player-missile memory are `noinit var`s with `@ align(n)`, since `main.tern`, the
only file that may have a `memory` block, is shared: a `var` at a fixed address inside the Atari's program area is
an error once the variables reach it, and `align` lets the compiler place them.

### 4. A table of a variable's address plus an offset cannot be built with a comprehension

```
noinit var screen: [u8; 1040] @ align(2048)
data rows: [ptr u8; _] = [addr(screen) + r * 40 for r in 0..25]
```

gives "Element 0 is a label, but the table holds ptr u8. Use round() or floor() for a real number". The same
elements written out as a list (`[addr(screen), addr(screen) + 40, ...]`) compile, for `ptr u8` and `u16` tables,
and `addr(screen).lo` / `.hi` work in a list. The message's fix is about real numbers and does not apply. Worked
around: `video.init()` fills `text_row` and `tile_row_addr` at startup (the C64's `video` builds the same tables
from the constant `SCREEN` at compile time).

### 5. A local set once from a link-time constant is stored and reloaded

Review V1 batch 3 Atari step 1, finding 2. In the DLI,

```
var page: u8 = addr(charset).hi
atari.antic.wsync = 0
atari.antic.chbase = page
```

compiles to `lda #video.charset.hi` / `sta video.dli.page`, then after the WSYNC store `lda video.dli.page` /
`sta atari.antic+9`: the local holds nothing in a register over the volatile store, so it is 6 bytes and 2 cycles
(in the DLI, after WSYNC) more than writing `atari.antic.chbase = addr(charset).hi`, which the front end now does.
A local that is only ever set from a constant (here a link-time one, `addr(x).hi`) could be folded into its uses.

## V1 batch 3, Atari step 2: sprites (in progress, 2026-11-23)

Player-missile graphics reused down the screen, chosen by measuring the C64 build's own play in the test
harness (30,000 frames of the suite's joystick script, 26,641 play frames): the most sprites touching one
8-line character row was 1-4 in 26,396 frames, 5 in 220, 6 in 25, never more; counting each popup as two slots
(it is two players wide on the Atari), more than 5 in 37 frames (0.14%), more than 4 in 257 (1.0%). So 5 slots (4
players and the missiles as a fifth player) leave an object out in about 1 frame in 700. Software sprites in a
character mode were not built: 117 of ANTIC mode 4's 128 characters already hold the tiles and the message font,
and an object needs about 9.

No new gap. The DLI is plain Tern; its moves land a few lines into the next row (about 100 cycles before its
WSYNC: the OS's NMI dispatch, the `interrupt fn`'s saves of A, X and Y, the HUD test, two table lookups), which
the slot rule allows for rather than the code. Whether an assembly DLI would reach WSYNC within the line is not
measured yet.

**The cost** (`tern run --profile` on a scratch program that places 6 actors and 2 popups and calls `render()`
and `vbi()` 3 times):

| Version | `render()` per frame |
|---|---|
| first: a loop per row to draw, `mem.fill` to clear, a 25-row index for the DLIs, every row's DLI | about 13,300 |
| the copies and clears unrolled (`draw16`, `clear16`, ...: about 11 cycles a byte against 20) | about 12,100 |
| a part drawn at the same place with the same picture in that copy is not drawn again; the DLI bits only on rows with moves, set by the VBI; the position in colour clocks and the graphics pointer worked out in `set_actor` | about 9,000 when nothing moved vertically or changed picture, 10,800 averaged over the 3 calls |

What is left is bookkeeping, about 1,000 cycles per object: the insertion sort, the slot search, and in
`draw_parts` the compares of two pointers per part (`new_at[i] != old || new_src[i] != drawn_src[d + i]` is 86
cycles) and the copies of pointers between arrays. The DLIs now cost only on the rows with moves and the floor's
row (typically 2 to 5 a frame, against 23).

**Where the frame's time went:** the first version drew the sprites in `video.wait_frame()`, after
`nav.idle_build()`, which fills the frame's spare time up to its budget (`BUILD_START_LINES`, as on the C64, where
the frame IRQ only copies shadows), so `render()` ran past the next VBI: a scratch build that counted the frames
where `wait_frame()` found the next frame already begun showed hundreds in about 1,000 play frames on PAL. Drawn
in `video.shake_step()`, the play loop's last drawing call before `idle_build()`, and with the cheaper version:
9 in about 1,250 frames on PAL (levels, game over and rebuild slices together; step 5 measures the rebuild's
budget) and none on NTSC. Every map rebuild gets less of the frame than on the C64 (decision 189: each machine
its own budget).

## V1 batch 3, Atari step 4: the known result in the test harness (2026-11-23)

The harness's Atari pieces (`AtariChips`, Tern's `tests/Tern.Compiler.Tests/Harness/`): ANTIC's `VCOUNT` and
`WSYNC`, the VBI at line 248 and the DLIs where the display list asks for them (read each frame, with its vertical
scroll), the joystick through the OS's `STICK0`/`STRIG0`, no key, a PAL GTIA, `RANDOM` a fixed sequence (one byte per read, so the seed does not move with the timing); an OS
stub for the NMI entry, `SYSVBV` and `XITVBV`. `ValidationTests.Smashy_smashy_plays_a_scripted_game_on_the_atari`
runs 2,500 frames with the C64 test's joystick script and compares the game state, the sprite shadows and the
screen with `expected/atari-frame-2500.txt`. ANTIC's DMA is not modelled (every line gives the CPU 114 cycles), so
the rebuild slices get more of a frame than on the machine: the result is the harness's (question 189).
`TERN_REAL=1`: `Smashy_smashy_shows_its_title_in_altirra`, the title on an 800 (PAL) 6 s after the start,
against `expected/altirra-800-pal-title.png` (two runs gave the same pixels).

No new gap.

### Not a Tern gap: a read outside the tile map in `levelgen`, found by the emulator's index check

The first run of the Atari known result stopped on "Index 255 is outside 'tilemap.tiles', which has 240 elements"
at `if tilemap.tiles[rise - 1] != tilemap.T_EMPTY` in `levelgen.reachable`'s jump search: a standing cell in column
0 of map row 1 rises into row 0, where `rise - 1` is -1. The Prog8 game reads there too (`video.tiles[rise - 1]`,
the byte before the map). It changes nothing: at column 0 `left_covered` is already false, and the read at column
19 (`rise + 1`, the next row's first cell) can only clear `right_covered`, which is false there too. The C64 runs
never generated such a level; the Atari run's seed (POKEY's `RANDOM` in the harness) did. Both reads are now only
made within the row (`c != 0`, `c != MAP_W - 1`), which gives the same result: the C64 known result is unchanged
(20,983 -> 20,993 bytes). Tern's index check in the emulator did its job: the read was silent in Prog8.

The message font's digits are the popups' hand-made 3 x 7 digits now (the ROM font's slashed 0 read as 8 in
"HIGH SCORE 000000").

## V1 batch 3, Atari step 3: the title and the attract pages (2026-11-23)

`atari/title.tern` is `c64/title.tern` with the same shapes, drop order, timing and shake; the letters are 5 new
characters (a block and four corner triangles, the C64's hi-res triangles made 4 pixels wide), all PF2. The C64
colours each letter cell from its screen row (colour RAM); here a DLI on every row sets PF2, so a step of the
colour sweep is a table of 25 colours, not a pass over the letter cells. The title has its own DLI: through the
game's DLI (its title test, then a VCOUNT lookup before WSYNC) each band's first line showed the colour above; the
title's DLI writes a colour worked out on the DLI before and looks the next one up after WSYNC, 12 cycles from its
entry to WSYNC, and the bands' edges are clean. `atari/pages.tern` is `c64/pages.tern` without its raster split
(text and tiles share one mode here); the chain values are in the text's colour. Both compiled on the first try.

The message font is hand-made, 3 x 7 (`video.letter_nib`, `digit_nib`, `punct_nib`): the ROM font halved across
read R as A and made M, N and W blocks. N is a lowercase n at full height, to tell it from H and M (the developer's
suggestion; a usual way on 8-bit machines).

`TERN_REAL=1`: the Altirra check photographs SCORING at 26 s (the page that stands still longest, 20-32 s); a shot
at 15 s of HOW TO PLAY once caught the title still on screen (Altirra's start takes a varying time), so a run that
misses is tried once more.

No new gap.

## V1 batch 3, Atari step 5: the frame's time on the Atari (2026-11-23)

**A correction to step 2's overrun counts:** the scratch counter showed 2 BCD digits, so its "9" on PAL and "none"
on NTSC were counts modulo 100. Counted again with 3 digits and split by whether a map rebuild was in progress
(the play loop only, about 2,000 play frames of the suite's joystick script in Altirra each):

| Build | PAL: rebuilding / other | NTSC: rebuilding / other |
|---|---|---|
| step 2's (`render()` about 7,600 cycles) | 51 / 104+ | 35+ / 92+ (wrapped) |
| now (`render()` about 6,900, the NTSC reserve) | 0 / 22 (about 1%) | 0 / 727 (about a third) |

**The cycles of a play frame** in the test harness (no DMA; 4,568 play frames of 6,000), per frame:

| Part | average | 90% | 99% | max |
|---|---|---|---|---|
| the game's logic, with the VBI and the DLIs | 8,143 | 9,635 | 12,422 | 13,786 |
| `render()` | 6,923 | 8,261 | 9,964 | 11,881 |
| logic and `render()` | 15,066 | 17,446 | 20,114 | 23,832 |

A PAL frame is 35,568 cycles; ANTIC's DMA for this screen (the mode 4 rows' character and font fetches, the HUD,
player-missile graphics on every line, refresh) takes about 13,000 and the OS's VBI about 1,500, so about
19,000-20,000 are left; the 1% of PAL frames over it are the logic's and `render()`'s worst together. An NTSC frame
is 29,868 cycles with the same DMA, about 15,000 left: the average frame does not fit, so on NTSC the game drops a
frame in about three, and in effect runs at about PAL's speed (question 190's 20% is then mostly gone, by
accident). The C64's frame IRQ copies 8 sprites' shadows to the VIC-II in about 300 cycles; the Atari's sprites cost
`render()`, and this is the difference.

**What was tried:** the parts' places and pictures as bytes instead of pointers, and `video.set_sprite` reaching
`sprites.set_actor` inline (`render()` 7,592 -> 6,923, the logic 8,461 -> 8,143); keeping the last frame's slots
while no object changed rows (taken back: objects change rows often, and the check cost as much as it saved). What
is left is bookkeeping per object (the sort, the slot search, the tables for the DLIs and the drawn parts: about 40
cycles for each empty part of 10 and some hundreds per object), and plain Tern's code for it is about what one
would write by hand for the same tables; the cut that NTSC needs (to about 3,000) means fewer passes over fewer
tables, or assembly. Question 191.

**The rebuild's budget** (decision 189): `nav.BUILD_START_LINES` (185) stays; `video.frame_lines()` counts scan lines
since the VBI, and on NTSC 11 more (`NTSC_RESERVE`), so a slice (up to about 80 scan lines here) starts only while
it ends before the next VBI. With it, no overrun came during a rebuild on either.

No new gap.

### The frame-time decision built (decision 2026-11-23, questions 190-191)

`video.tern`: the VBI owes ticks (at most 2) and `wait_frame()` runs one whenever one is owed; `frame_ready` is
true while one is owed (`nav.idle_build()`'s test); on NTSC the VBI owes no tick every sixth frame, and `frames` and
`sound.step()` follow ticks, so the game, its timer and its sounds run at PAL's 50 a second on both. The game's own
immediate VBI (`atari.set_vbi_immediate`) copies the shadows it uses (`SDLST`, `SDMCTL`, `CHBAS`, `GPRIOR`, the
colours) in place of the OS's frame work; `input.tern` reads `PORTA` and `TRIG0`. Nothing in play read the OS's
clock or other shadows (checked). `AtariChips` gives `PORTA` and `TRIG0` too.

Counted in Altirra as play frames that ended with a tick already owed (about 2,000 each): PAL 6, none during a
rebuild; NTSC 612 (108 during a rebuild). No tick is lost now, but such a frame's sprites show a frame late, and
the developer still sees that under heavy activity. An NTSC frame leaves about 15,000 cycles; the skipped tick
every sixth frame gives the game time to catch up, not more time per frame.

Memory: variables reach `$B7A5`, about 1,150 bytes under `$BC1F`. A scratch build with about 300 bytes more code
failed: the variables' block then started past a 2 KB boundary and the aligned player-missile memory no longer
fitted. Each copy's first `$300` bytes are unused by ANTIC (single-line players start at +`$400`, missiles at
+`$300`), and the padding before an aligned variable stays empty (spec 4: the compiler may place other variables
in it but does not yet): about 3.5 KB in all. Recorded as a constraint, not a gap: the spec already names it.

### PAL first (the developer: "lets get PAL working great - worry about NTSC later")

The worst PAL frames in the harness (logic and `render()` over 19,000 cycles: 100 of 4,630 play frames) averaged
11,500 cycles of the game's logic and 8,500 of `render()`: both matter, and only `render()` is the front end's.
Two cuts in plain Tern: `place()` makes the DLIs' groups as it makes the entries, which come in row order already
(no sort, no `make_groups` pass), and the sort compares a parallel array of keys instead of `need_row[order[i - 1]]`
(34 cycles a test). `render()` 6,813 -> 6,385 cycles a frame on average (99%: 9,671 -> 9,041); frames over 19,000:
100 -> 57; 247 bytes less. The decision's other follow-ups were not taken: compiled sprites would take about 2 KB
(about 1,150 bytes are free), and chained DLIs, padded frames and values before WSYNC are each worth 100-200 cycles
a frame at most. In Altirra on PAL, two runs of about 2,750 play frames: 15 and 12 frames ending with a tick owed
(about 0.5%; the level layouts differ between runs), none during a rebuild. The developer: "pal version
performance looking great".
