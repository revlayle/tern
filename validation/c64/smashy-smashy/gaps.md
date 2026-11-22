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
