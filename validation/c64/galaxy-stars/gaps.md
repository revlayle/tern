# Tern gap log

Findings from porting Galaxy Stars to Tern, for the Tern spec session. Format in `AGENTS.md`.

## Summary

The whole game is ported (steps 1-5, commits d72a312, e418ffe, a288783 on `tern-port`) and behaves as the Prog8
game: `tools/compare.py` played about 13,200 frames of both builds in a 6502 simulator (scripted joystick and the
demo pilot) and every frame matched (game state, screen, color RAM, sprite shadows); the attract cycle in VICE is
pixel for pixel the same, a few frames sooner.

| | Prog8 12.3.4 | Tern, with the workarounds | Tern, plain forms (M20 follow-up 3) |
|---|---|---|---|
| PRG size | 23,500 bytes (includes its self-test code) | 19,113 bytes | 19,003 bytes |
| Play frame, average cycles (compare.py, 7 runs, demo pilot included) | 5,586 | 4,784 | 4,788 |
| compare.py game frames | | 13,211, all the same | 13,211, all the same |

Both Tern columns are built with the M20 follow-up 3 compiler (the old compiler gave 19,287 bytes for the
workaround version). The 4 cycles more are code placement, not the plain forms (see the M20 follow-up 3 section at
the end). Stars routines, like for like at step 3: 2,675 bytes in Prog8, 1,986 in Tern.

Assembly stayed where plain Tern measured slower than Prog8's hand-written asm, or could not say it: BCD score
adds (no decimal mode), tile and explosion drawing, `explode.hits`, hostile movement and sprite drawing, `rnd`,
the logo recolor, `demo.dist` (gaps 16, 20, 21, 25). Plain Tern matched Prog8's asm in `next_busy`, `scan_idle`,
`sound.step` and `demo.find_near`.

The gaps by kind:

- Fixed by the M20 follow-up during the port: 1, 3, 4, 5, 7, 8, 9, 10 (addr() in initial values, constant
  arithmetic, `[T; _]`, `c64.sprite_xy`, `set_kernal_irq`).
- Fixed by M20 follow-up 3, and the port now written plainly: 1's leftover (`c64.cia_irqs_off()`), 11 (`len()`
  in a const), 12 (calls run left to right), 13 (`ptr + (u8 << 1)`), 14 (`(x as i8) < 0`), 15 (`var` lines in
  an `asm fn`), 17 (constants across an import ring), 18 (`step` as a name), 19 (a line ending in an operator
  continues), 24 (`[u8; _]` from a string), and 23's error message.
- Not gaps, per the spec session: 2 (M22's code generators), 16, 20, 21 (no flag expressions, decision 39, and
  no decimal mode; the optimizer ideas in them are optimizer work), 22 (the error already says what to write),
  23 (the main file stays not importable).
- Fixed by V1 fixes (Tern branch `v1-fixes`): 26, 27 (16-bit `t = a ± b` directly, a constant with low
  byte 0 added to the high byte only), 28 (flags reused after `inc`/`dec`; for a table at a fixed address such
  as `cover` only from `4df0268`) and 29 (`keeps` on a function with a body).
- Open: from V1 fixes' measurements, 30 (`(a as u16) + (b >> 1)` goes through two 16-bit temporaries), 31 (a
  16-bit local stored, then read back for its bytes), 32 (a `using Y` loop variable cannot be passed to a
  parameter in Y) and 33 (`if c { break }` is a branch over a `jmp`).
- V1 (2026-11-21): every routine still in assembly measured again in plain Tern; none is as fast yet, so all
  stay assembly (table in the V1 section).
- V1 fixes (2026-11-21): measured again with the gaps 26-29 fixes. Within 5% of the assembly on the average
  and the worst frame: `hostiles.move` (3% / 2% slower), `explode.restore_area` (as fast) and
  `title.sweep_all` (0.2% slower). The plain versions are kept in `plain/` (table in the V1 fixes section at the
  end).
- Skill text: 6 (decimal mode in handlers) and 12 (evaluation order), both now in the skill.

What helped most: `tern run` and `tern bench` for single routines, struct literals for tables, clear error
messages with a fix (most of the gaps above were found from them), and the side-by-side simulator comparison,
which caught gap 12 and would catch any behavior change a screenshot misses.

## Step 1: video.p8 setup and the frame IRQ

### 1. No raster IRQ with the KERNAL left on (fixed in M20 follow-up)

`video.p8` `start_frame_irq()`: Prog8's `sys.set_rasterirq` keeps the KERNAL ROM in and hooks the
IRQ through `$0314` (CINV). The KERNAL's entry at `$FF48` has already pushed A, X and Y, so the
handler must exit with `pla/tay/pla/tax/pla/rti` (or `jmp $EA31`). The `c64` library only supports
a handler through `$FFFE` with the KERNAL off, and an `interrupt fn` saves its own registers and ends
in a plain `rti`, so it cannot be put in `$0314`.

```prog8
sys.set_rasterirq(&frame_irq, FRAME_IRQ_LINE)     ; KERNAL stays in, $0314 hooked
```

```tern
c64.kernal_off()                    // in video.init(): $01 = $35, NMI ignored, interrupts off
...
c64.set_irq(addr(frame_irq))        // in start_frame_irq(): $FFFE
c64.vic.control1 &= $7F
c64.vic.raster = FRAME_IRQ_LINE
c64.vic.irq_enable = 1
c64.vic.irq_status = $FF
c64.interrupts_on()
```

This is fine for Galaxy Stars, which never calls the KERNAL, but it changes two small things:
RESTORE is ignored (the KERNAL NMI handler would reset on RUN/STOP+RESTORE), and the KERNAL's
screen page (`cbm.HIBASE = $C0`) is no longer set, since nothing reads it.

What would have helped: a library way to install a handler under the KERNAL (`c64.set_kernal_irq`
with a handler kind that exits through `$EA81`/`$EA31`), or a line in the skill's section 11 saying
that the KERNAL must be off for any Tern interrupt handler.

**Fixed in M20 follow-up.** The designer chose to match Prog8. `kernal_off` is gone, `init()` sets
`$01` to `$32`/`$36` as `c64.banks()` does, and HIBASE is set again:

```tern
fn start_frame_irq() {
    c64.interrupts_off()
    c64.cia1.icr = $7F              // no CIA interrupts (the KERNAL's timer)
    c64.cia2.icr = $7F
    var ack: u8 = c64.cia1.icr      // reading clears any that are pending
    ack = c64.cia2.icr
    c64.vic.control1 &= $7F
    c64.vic.raster = FRAME_IRQ_LINE
    c64.vic.irq_enable = 1
    c64.vic.irq_status = $FF
    c64.set_kernal_irq(addr(frame_irq))
    c64.interrupts_on()
}
```

`frame_irq` is unchanged. The screen in VICE is the same as before, pixel for pixel. Small leftover:
reading a register only to clear it needs a dummy variable (`var ack = ...`), as Tern has no
expression statement; Prog8 has the same problem and writes `void`. A `c64.cia_irqs_off()` like the
first half of `kernal_off` would cover it.

**Spec session:** the library gets `c64.cia_irqs_off()`, which turns off and acknowledges both CIA chips'
interrupts. When it is in, `start_frame_irq` calls it in place of the four CIA lines and `ack`.

**Done in M20 follow-up 3:** `start_frame_irq` calls `c64.cia_irqs_off()`. 4 bytes more (a `jsr` to the
library routine in place of the stores written in line), run once at startup. The attract cycle in VICE is the same.

### 2. No compile-time loop for unrolled code (not a gap: M22)

**Spec session:** not a gap. Unrolled code is covered by M22's code generators (`emit`).

`video.p8` `frame_irq()`: the 8-sprite copy is an asm block using a 64tass `.for` loop that unrolls
to 32 `lda`/`sta` pairs. Tern has no unrolling in either Tern or `asm`, so the 32 copies are written
out by hand, 4 per line:

```prog8
.for _n := 0, _n < 8, _n += 1
lda  p8b_video.p8v_spr_ptr+_n
sta  p8b_video.p8c_SPRITE_PTRS+_n
...
.endfor
```

```tern
sprite_ptrs[0] = spr_ptr[0]; c64.vic.sprite_color[0] = spr_col[0]; sprite_xy[0] = spr_x[0]; sprite_xy[1] = spr_y[0]
... (8 lines)
```

The code is identical: 32 absolute `lda`/`sta` pairs, 256 cycles. The whole handler is 216 bytes and
about 300 cycles, with only A saved. That is less than the Prog8 version, which also runs the
KERNAL's entry and Prog8's register save and restore.

What would have helped: an unrolled `for` over constant bounds (`inline for n in 0..8`), or a
repeat directive in `asm` blocks.

### 3. `c64.vic` has no indexable sprite positions (fixed in M20 follow-up)

The `Vic` struct names `sprite0_x` ... `sprite7_y` as separate fields, so they cannot be indexed,
while `sprite_color` is an array. I declared my own `var sprite_xy: [u8; 16] @ $D000`. A
`sprite_xy: [u8; 16] @ $00` field (or `sprite_pos: [Point; 8]`) would cover both uses.

**Fixed in M20 follow-up:** my declaration is gone; `frame_irq` writes `c64.sprite_xy[0] = spr_x[0]`.

### 4. Misleading error for a constant expression typed `u8` (fixed in M20 follow-up)

`video.p8` `init()`: the charset copy target. Prog8 also needs a cast here, but Tern's error points
at the wrong operand and does not say why the expression is `u8`:

```tern
mem.copy(CHARSET + assets.FIRST_CHAR * 8, addr(chars_bin), len(chars_bin))
// video.tern:48:14: error: 57344 does not fit in u8 (0 to 255)      (column 14 is CHARSET)
```

`CHARSET` is `const CHARSET = $E000` (no type) and `assets.FIRST_CHAR` is `const ...: u8`, so the
whole expression became `u8`. What would have helped: a message like "this expression is u8
because FIRST_CHAR is u8; write (assets.FIRST_CHAR as u16)", or untyped constants keeping their
size when mixed with a typed one.

### 5. A `u16` constant expression does not convert to a pointer (fixed in M20 follow-up)

After fixing 4, the same argument gave `Expected ptr u8, found u16. Convert with 'as ptr u8'`.
A bare constant (`CHARSET`, `$D000`) converts to a pointer parameter with no `as`, but
`CHARSET + (assets.FIRST_CHAR as u16) * 8` does not, although it is also a constant. The skill
says "A number converts to a pointer without `as`", which suggests both work. Final code:

```tern
mem.copy((CHARSET + (assets.FIRST_CHAR as u16) * 8) as ptr u8, addr(chars_bin), len(chars_bin))
```

What would have helped: constant expressions converting like numbers, or the skill saying which
constants convert.

**Gaps 4 and 5 fixed in M20 follow-up**, written as in Prog8 but with no cast at all. Same code
(`lda #$00 / lda #$E2` into `mem.copy.dst`):

```tern
mem.copy(CHARSET + assets.FIRST_CHAR * 8, addr(chars_bin), len(chars_bin))
```

### 6. Skill does not say when an interrupt handler clears decimal mode

Prog8's IRQ wrapper always runs `cld`; the `bcd` library runs `sed`. The listing of `frame_irq` had
no `cld`, and the skill's section 11 says nothing about decimal mode. The spec (`spec-v0.md`
line 455) says the compiler adds `cld` when the handler executes `adc`, `sbc` or a `jsr`, so it will
appear once `sound.step()` is called from the handler. What would have helped: that one sentence in
the skill's section 11.

The skill's section 11 now has that sentence.

## Step 2: input.p8 and player.p8

### 7. No `addr()` in an initial value (fixed in M20 follow-up)

`input.p8`: the scripted joystick points at its default table in the declaration. Tern refuses:
`addr() cannot be used in an initial value, since addresses are known only after compiling. Assign
it at the start of main`. The error is clear, but the address of `data` is a constant to the
assembler, so this could work (Prog8 and 64tass do it).

```prog8
uword script = &default_script
```

```tern
var script: ptr u8                  // set by init()
fn init() {
    script = addr(default_script)
}
```

`input.p8` has no `init()`; the Tern port needs one, called from `main`.

**Fixed in M20 follow-up:** `input.init()` and its call in `main` are gone. `tern run` shows
`input.script` = `$0857`, the address of `default_script`, before any code sets it.

```tern
var script: ptr u8 = addr(default_script)
```

### 8. A declared `u16` constant still computes in `u8` (more of gap 4; fixed in M20 follow-up)

`player.p8`: `const uword MAX_X = 320 - BOX` with `BOX` a `ubyte` constant.

```tern
const BOX: u8 = 13
const MAX_X: u16 = 320 - BOX            // error: 320 does not fit in u8 (0 to 255)
const MAX_X: u16 = 320 - (BOX as u16)   // what compiles
```

The declared type `u16` does not make the expression `u16`; one typed `u8` operand makes the whole
expression `u8`, and then the literal 320 is rejected. The message again names the literal, not
the operand that decided the type. What would have helped: constant expressions computed with
unlimited integers (the spec says compile time has an `int` type) and checked only against the
declared type at the end.

**Fixed in M20 follow-up:** `const MAX_X: u16 = 320 - BOX` (307).

### 9. A constant that fits still needs `as u8` (fixed in M20 follow-up)

`player.p8`: `const ubyte INVINCIBLE_SECS = INVINCIBLE_FRAMES / 50` (400 / 50 = 8). Tern:
`Expected u8, found u16. Convert with 'as u8'`. The value is known and fits, so the check could
pass it, as it does a literal. Written as `(INVINCIBLE_FRAMES / 50) as u8`.

**Fixed in M20 follow-up:** `const INVINCIBLE_SECS: u8 = INVINCIBLE_FRAMES / 50`.

### 10. `data` lists need their length written (fixed in M20 follow-up)

`data default_script: [u8; 4] = [...]`: leaving the length out is an error (`Data arrays need a
written type`). Prog8 counts the elements (`ubyte[] default_script = [...]`). Every time a script or
table changes length, the count has to be changed by hand, and the scripted tests change their
script tables often. What would have helped: `data t: [u8] = [...]` with the length taken from the
list.

The written length is intended in Tern today; the designer left this open, since the compiler could
count the elements.

**Fixed in M20 follow-up:** every list in `src/` now takes its length from the list:
`data default_script: [u8; _] = [...]`, `data dir_vx: [i8; _] = [...]`, `data sprite_bit: [u8; _] = [...]`.

### Check of the M20 follow-up rewrite

The game is 12 bytes smaller (4390 bytes; `input.init` is gone, the IRQ setup is a few bytes longer).
`check_player.tern` gives the same results byte for byte, and the VICE screenshot after 6 s matches
the old build's.

### Step 2 notes (no gap)

- The movement code (`move`, `bounce`, the speed tables) ported line for line: signed `i8` with
  arithmetic `>>`, `u8` wrap-around and the `u16` clamp all work as in Prog8. `tern run` with a
  harness (`src/check_player.tern`) checked it against hand-worked values, which Prog8 cannot do
  without the emulator.
- `player.update` takes about 370 cycles a frame (`player.move` about 170), measured with
  `tern run --profile`. The comparison with Prog8 comes with step 3.

## Step 3: stars.p8

How it was checked: `tools/compare.py` loads the Prog8 build (`main.prg`, labels from
`main.vice-mon-list` and `main.asm`) and the Tern harness `src/check_stars.tern` into a 6502 simulator
(py65), sets the same RNG seed and star state in both, calls the same routines and compares the
cycles and the memory they leave (tile map, specks, the star tables, screen and color RAM).
Every result is the same in both builds, frame by frame.

### 11. A `const` cannot take `len()` of a `data` list (workaround)

`video.p8`: `const ubyte STAR_KINDS = len(star_char)`. In Tern, `const STAR_KINDS: u8 = len(star_char)`
gives `'star_char' is not declared`, even with the `data` line above it (`len(t)` works in code).
Written the other way round, so the compiler still checks the count:

```tern
const STAR_KINDS: u8 = 8
data star_char: [u8; STAR_KINDS] = [assets.TILE_STAR_0_TL, ...]
```

What would have helped: `len()` of a global or `data` in constant expressions, or an error that says
it is not allowed there.

**Fixed in M20 follow-up 3:** written as in Prog8. Same code; the four `video` tables are laid out in a
different order.

```tern
data star_char: [u8; _] = [assets.TILE_STAR_0_TL, ...]
data star_color: [u8; len(star_char)] = [...]
const STAR_KINDS: u8 = len(star_char)
```

### 12. Two calls in one expression run right to left (the skill does not say the order is open)

`stars.p8` `place_specks()`: `(rand_below(SPECK_KINDS) + 1) << 2 | rand_below(4)`. Prog8 calls the left
`rand_below` first; Tern called `rand_below(4)` first, so the specks came out different from the
Prog8 game for the same seed. compare.py found it; it would not show on a screenshot. The spec
(section on evaluation order) says the order is unspecified and to use a local, but the skill
says nothing. Final code:

```tern
var k: u8 = rand_below(video.SPECK_KINDS) + 1     // the kind first, as in Prog8
video.specks[i] = (k << 2) | rand_below(4)
```

What would have helped: left-to-right calls (what C programmers and Prog8 expect), or the spec's
sentence in the skill's `language.md`, near the operators.

**Fixed in M20 follow-up 3:** calls in one statement run left to right, and `language.md` says so next to
the operators. `place_specks` is Prog8's line, 4 bytes and 6 cycles shorter than with the local (the kind
stays in A for the shifts). compare.py: every frame the same.

```tern
video.specks[i] = (rand_below(video.SPECK_KINDS) + 1) << 2 | rand_below(4)
```

### 13. `ptr + (u8 << 1)` in one expression goes through a 16-bit temporary

`video.p8` `draw_tile`, `stars.p8` `set_color`/`set_look`: the tile's screen address,
`tile_row_addr[row] + (col << 1)`. Tern widens `col << 1` into a 16-bit temporary and adds
16 bits through memory. Two statements give an 8-bit add with a carry into the high byte:

| `tern bench --fn f` | bytes | cycles |
|---|---|---|
| `p = rows[row] + (col << 1)` | 35 | 56 |
| `p = rows[row]` / `p += col << 1` | 25 | 39 |
| `var c2: u8 = col << 1` / `p = rows[row] + c2` | 29 | 45 |

What would have helped: the compiler doing `ptr + u8` as it does `ptr += u8`, or a line in `costs.md`.

**Fixed in M20 follow-up 3:** `ptr + (u8 << 1)` is an 8-bit add with the carry into the high byte. From the
listing:

| | before (two statements) | one statement |
|---|---|---|
| `set_look`: `p = video.tile_row_addr[r] + (c << 1)` | 24 bytes, 33-37 cycles | 18 bytes, 26 cycles |
| `set_color`: the same plus the color RAM offset | 27 bytes, 37-41 cycles | 31 bytes, 44 cycles (gap 26) |

`set_look` is one statement now. `set_color` keeps the offset on a second line, which is shorter than both
(21 bytes, 30 cycles: the high byte stays in A):

```tern
var p: ptr u8 = video.tile_row_addr[r] + (c << 1)
p += video.COLOR_RAM - video.SCREEN    // one statement is slower (gap 26)
```

### 14. `(d as i8) < 0` is a full signed compare, not a test of bit 7

`stars.p8` `scan_idle` (absolute distance). `if (d as i8) < 0` compiles to `sec / sbc #0 / bvc / eor #$80
/ bpl` (8 cycles after the value is in A) where `bpl` alone would do. `if d >= $80` is `cmp #$80 / bcc`.

**Fixed in M20 follow-up 3:** `if (d as i8) < 0` is `bpl` alone, 2 bytes and 2 cycles less than the
`>= $80` form the port had. Both tests in `scan_idle` use it: `pick_hostile` with 27 idle stars takes 2,421
cycles, 2,506 before (Prog8's asm: 2,425).

### 15. `asm fn` has no variables of its own

`video.p8` `draw_tile` keeps its color in a `.section BSS` byte inside the asmsub. Tern says `An asm fn
has no variables of its own. Use 'inline asm fn' ... or a private global variable`: clear. Final:
`private var tile_scr: ptr u8 @ zp`, `tile_clr`, `tile_co` next to the function. The names had to
change because the module already has locals called `scr` (no shadowing), so the work variables
of one routine take module-wide names.

**Fixed in M20 follow-up 3:** an `asm fn` may start with `var` lines. `video.draw_tile`, `explode.draw_area`,
`draw_frame`, `restore_area`, `hits`, `hostiles.move`, `demo.dist` and `title.sweep_all` declare their own work
variables with plain names, and the private globals are gone. Where a name was a parameter's (`c`, `r`, `vy`
in `using` registers) the local is `mid_c`, `mid_r`, `saved_vy`. They are placed in zero page in each
routine's frame, shared only with routines that never run at the same time (`restore_area` calls `draw_tile`
from its assembly, and their bytes are apart). The explode work bytes were absolute before, so the program is
94 bytes smaller.

```tern
asm fn draw_tile(col: u8 using A, row: u8 using Y) {
    var scr: ptr u8
    var clr: ptr u8
    var co: u8
    ...
```

### 16. Assembly kept for `draw_tile` and `rnd`: plain Tern is slower (not a gap: by design)

**Spec session:** by design. Tern has no flag expressions (decision 39), so no add-with-carry. Knowing the carry is clear after an add, and the extra reloads, are optimizer work, not language changes.

| Routine | Prog8 | plain Tern | Tern `asm fn` (kept) |
|---|---|---|---|
| `draw_tile`, a star | 128 bytes, 147 cycles | 190 cycles | 125 bytes, 145 cycles |
| `draw_tile`, empty with a speck | 170 cycles | 194 cycles | 168 cycles |
| `rnd` (Prog8's `math.rnd`, x-abc) | library asm, the same code | 33 bytes, 57 cycles | 23 bytes, 42 cycles |

`draw_tile` in plain Tern (after gap 13's fix): it reloads `t` after storing it, and `ch + 1`, `ch + 2`,
`ch + 3` each reload `ch` and `clc` (the asm chains `adc #1` with the carry known clear). The asm
port is Prog8's assembly line for line. `rnd` needs the carry from `lsr` in the next `adc`; plain Tern
has to add `rnd_b & 1` by hand:

```tern
rnd_c += ((rnd_b >> 1) ^ rnd_a) + (rnd_b & 1)    // the 6502 code adds the carry from lsr
```

What would have helped: an add-with-carry form, or the optimizer knowing the carry is clear after an
`adc` that cannot overflow (`ch` is at most 252).

Tern's `random` library is a different generator (xorshift); the port uses Prog8's so that the game's
random choices (layouts, specks, reveal order, hostile picks) are the same for the same seed.

### Step 3 notes (no gap)

- `next_busy` and `scan_idle` are assembly in Prog8 and plain Tern here. `next_busy` with
  `for i in from..count using X` compiles to the same loop as the Prog8 asm (39 bytes against 32:
  `count` is copied and `from` is passed in memory). `scan_idle` came within 2% after writing it the
  way the asm works (one subtract, negate if bit 7 is set, skip the row when the column is far):
  `pick_hostile` 2468 cycles against 2425 (2506 after step 4's changes moved the code), and it runs
  only when a hostile spawns.
- `video` and `stars` import each other (draw_tile reads `stars.shown`); that works.
- Frame time, cycles in the simulator (Prog8 includes the calls into sound, which Tern does not have yet):

| Routine | Prog8 | Tern |
|---|---|---|
| `stars.update`, one hit lockout, no countdowns (55 stars) | 1091 | 1002 |
| `stars.update`, 5 countdowns and a warning, worst frame | 3188 | 2302 |
| `stars.update`, the same, average of 29 frames | 2295 | 1947 |
| `stars.player_contact`, nothing near | 433 | 428 |
| `stars.player_contact`, hits a star | 3190 | 2649 |
| `stars.reveal_next`, worst | 263 | 236 |
| `video.draw_playfield`, 33 stars | 42008 | 39807 |
| `stars.place(55)` with specks | 103252 | 79172 |

- Code size of the stars routines and `draw_tile`/`draw_playfield`: 1986 bytes in Tern, 2675 in Prog8
  (Prog8's include the explode, score, sound and quasar calls and the `explode.cover` checks, so
  `supernova`, `warning`, `set_look` are not like for like). Smaller in Tern everywhere else but
  `next_busy` (39/32) and `scan_idle` (85/83).

## Step 4: hostiles.p8, explode.p8, score.p8 (and quasar.p8)

`quasar.p8` is in no step of the plan; it came with this one because `stars.supernova` calls it.

How it was checked: `tools/compare.py --scenario game` plays whole frames of both games in the simulator:
the galaxy start as `main.galaxy_start`, then the routines of `main.play` in order, with a scripted
joystick that moves, turns and boosts. After every frame it compares the stars, hostiles,
explosions, score, player, quasar, the sprite shadows, the screen and color RAM. Four runs (galaxies 1, 5, 21,
21; 9516 frames with 130 hostile kills, 35 hurl explosions, 187 explosions, deaths and respawns, 2 quasar
pickups, 2 galaxies cleared): every frame the same.

### 17. A constant cannot be used across an import ring

`explode.p8`: `const ubyte COLOR = assets.TILE_EXPLODE_0_COLOR | video.MC` and
`&ubyte[video.MAP_W * video.MAP_H] cover = $C600`. `video` imports `stars` (draw_tile reads `stars.shown`),
`stars` imports `explode`, `explode` imports `video`. Tern:

```text
explode.tern:20:49: error: 'video.MC' cannot be used in a declaration of explode: video and explode import
each other, and video is declared after explode. Move MC to explode, or to a module both import
```

The error is clear and says what to do, but moving the constants means splitting `video` (map size,
colors, memory) into a new module that Prog8 does not have. Written out with a note instead:

```tern
const COLOR: u8 = assets.TILE_EXPLODE_0_COLOR | 8              // video.MC
var cover: [u8; 240] @ $C600                    // video.MAP_W * video.MAP_H
```

What would have helped: constants resolved across modules before anything else (they cannot depend on
code), so a ring of imports only matters for things that do.

**Fixed in M20 follow-up 3:** written as in Prog8; the same program, byte for byte.

```tern
const COLOR: u8 = assets.TILE_EXPLODE_0_COLOR | video.MC
var cover: [u8; video.MAP_W * video.MAP_H] @ $C600
```

### 18. `step` is a keyword

`explode.p8` has `sub step(ubyte s)`, `score.p8` and `hostiles.p8` have locals called `step`. Tern reserves
`step` (for `for ... step`): `Expected function name, found 'step', which is a keyword. Use another name`.
Renamed to `step_anim`, `k` and `dir`, and `sound.step` (step 5) to `sound.tick`. Clear error; a word that common could be a keyword only after a `for`
range.

**Fixed in M20 follow-up 3:** `step` can be a name again. `explode.step`, `sound.step` and the locals `step`
in `score.chain_kill` and `hostiles.aim` have their Prog8 names; the same program, byte for byte.

### 19. A condition over two lines needs parentheses; the error does not say so

`hostiles.p8` `check_player`, `quasar.p8` `update`: Prog8 continues a condition on the next line after `and`.
In Tern the newline ends the condition:

```tern
if state[i] == CHASE && player.x < x[i] + BOX && x[i] < player.x + player.BOX
        && player.y < y[i] + BOX && y[i] < player.y + player.BOX {
// hostiles.tern:438:86: error: Expected '{', found end of line
```

The skill says a newline inside `( )` does not end a statement, so wrapping the condition in parentheses
works. What would have helped: continuing after a line that ends in `&&`, `||` or another operator, or the
error suggesting the parentheses.

**Fixed in M20 follow-up 3:** a line that ends with an operator continues; one that starts with `&&` is an
error that says to move it to the line before. `hostiles.check_player`, `quasar.update` and `demo`'s
`can_boost` have `&&` at the end of the first line and no extra parentheses; the same program, byte for byte.

```tern
if state[i] == CHASE && player.x < x[i] + BOX && x[i] < player.x + player.BOX &&
        player.y < y[i] + BOX && y[i] < player.y + player.BOX {
```

### 20. BCD score adds stay assembly (not a gap: by design)

**Spec session:** by design: Tern has no decimal mode.

`score.p8` `add_once`, `add_acc`, `raise_next`: decimal mode, which Tern does not express. The `bcd`
library has general n-byte routines through pointers, but `add_once` stops at 999999 and all three are
fixed-size adds of a few instructions, so the Prog8 assembly was ported as `asm fn` (with `php / sei / sed`
... `plp` as in Prog8). Expected, not a problem; noted so the spec session knows where asm remained.

### 21. Per-frame assembly kept: plain Tern measured slower (not a gap: by design)

**Spec session:** by design (no flag expressions, decision 39). The reloads and 16-bit adds are optimizer work, not language changes.

The Prog8 assembly in `explode` (`draw_area`, `draw_frame`, `restore_area`, `hits`) and `hostiles` (`move`,
`explosion_hit`, `draw`) was ported as `asm fn`. Three were also written in plain Tern and measured in the game
runs above (cycles per frame, average and worst):

| Part of the frame | Tern asm | plain Tern |
|---|---|---|
| `hostiles.check_explosions` (`explosion_hit` + `explode.hits`) | 118 / 3602 | 178 / 5476 |
| `explode.check_player` (`explode.hits`) | 53 / 698 | 57 / 850 |
| `hostiles.draw` | 272 / 580 | 316 / 718 |

`explosion_hit` and `hits` go together: the asm loop keeps the slot in X across `jsr explode.hits`, which a
plain Tern `hits` does not promise to keep. The plain `hits` loses most in the absolute value of a 16-bit
difference (`dxw.hi` tests and a negate), where the asm uses the carry of `sbc`. `draw` loses in
`x[i] + (24 - BOX_INSET)` as a full 16-bit add and the `|=` / `&= ~` on memory.

### Step 4 notes (no gap)

- Frame time from the same runs, cycles per frame (Prog8 includes its sound.play calls, which Tern does not
  have yet, a few dozen cycles on frames with an event):

| Part | Prog8 avg | Tern avg | Prog8 worst | Tern worst |
|---|---|---|---|---|
| `score.update` | 41 | 40 | 330 | 268 |
| `player.update` | 473 | 341 | 3792 | 2902 |
| `hostiles.update` | 1165 | 996 | 6166 | 5682 |
| `stars.player_contact` | 326 | 325 | 1700 | 1376 |
| `hostiles.check_player` | 429 | 282 | 2379 | 1859 |
| `explode.check_player` | 65 | 53 | 810 | 698 |
| `hostiles.check_explosions` | 123 | 118 | 3906 | 3602 |
| `stars.update` | 1413 | 1273 | 10418 | 8423 |
| `explode.update` | 299 | 273 | 2557 | 2395 |
| `quasar.update` | 20 | 18 | 447 | 362 |
| `player.draw` | 205 | 185 | 263 | 228 |
| `hostiles.draw` | 279 | 272 | 600 | 580 |
| `quasar.draw` | 16 | 14 | 218 | 185 |
| `score.flush` | 17 | 14 | 261 | 212 |
| all parts | 4878 | 4209 | | |

- A function's result can be ignored by calling it as a statement (`move(i, vx, vy)` in `chase`), where
  Prog8 needs `void`.
- `video.put_score` is assembly in Prog8 and plain Tern here: it runs at most once a frame.

## Step 5: sound.p8, title.p8, demo.p8, pages.p8 and the rest of main.p8

How it was checked: the game scenario of `tools/compare.py` now also has three runs played by the demo pilot
(each build's own `demo.steer()` gives the joystick): 3695 frames, every one the same, as are the 9516
scripted frames of step 4. The attract cycle in VICE from power-on (title, logo build, HOW TO PLAY, SCORING,
the demo game) is pixel for pixel the Prog8 one, a few frames sooner: the Tern build is one frame ahead
after startup and three by 40 s (its one-off work, placing stars and drawing the playfield, fits in fewer
frames). At 60 s, in the demo, the characters and HUD still match but the player and a hostile sprite are drawn
a little differently. Likely cause, not checked: on frames where Prog8's work runs past the frame IRQ, the
sprite shadows go out a frame late while the characters are already new. The simulator runs show the game
logic itself is the same.

### 22. An untyped address constant plus a `u8` variable is computed in `u8` (not a gap)

**Spec session:** not a gap. If the type changed to fit the constant, the result's type would depend on the constant's value. The current error already says what to write.

`video.p8` `hud_text`: `^^ubyte scr = SCREEN + col` with `const uword SCREEN = $C000`. In Tern,
`const SCREEN = $C000` has no type, so next to `col: u8` the add is `u8`:

```text
video.tern:204:23: error: 49152 does not fit in u8 (0 to 255), the type of 'col', which it is combined with.
Convert 'col' with 'as u16'
```

The error is clear and says what to do. Written pointer first, which also gives the short 8-bit add (gap 13):
`var scr: ptr u8 = SCREEN` / `scr += col`. What would have helped: an untyped constant that does not fit the
other operand's type widening the operation instead (it is known to need 16 bits).

### 23. The main file cannot be imported (by design; the error message is the gap)

**Spec session:** the main file has rules of its own (its globals are always kept, the memory block, module parameters), so it stays not importable. The error message is wrong ("'main' is a module") and will say what to move instead. `game.tern` stays.

`demo.p8` plays a game through `main.new_game()`, `main.galaxy_start()`, `main.play()` and `main.galaxy_clear()`.
`import main` in `demo.tern` turns `main.tern` into a module, and then `Only the program's main file has 'fn main()';
'main' is a module`. The game flow moved to `src/game.tern`; `main.tern` keeps `main()` and the attract cycle.
What would have helped: the main file importable like any module (its `fn main` is still the entry), or the
error saying that the main file cannot be imported.

**Message fixed in M20 follow-up 3:** `'main' is the program's main file, which cannot be imported. Move what
other needs from it into a module that both import`.

### 24. `[u8; _]` does not take its length from a string

`main.p8`: `str galaxy_text = sc:"galaxy 00"` (changed at runtime, so a `var`). `var galaxy_text: [u8; _] =
screen"GALAXY 00\0"` gives `The length '_' is taken from a list initial value ... Write the length here`.
`data t = screen"..."` takes the length from a string, so a `var` could too. Written
`var galaxy_text: [u8; GALAXY_TEXT_LEN + 1] = screen"GALAXY 00\0"`.

**Fixed in M20 follow-up 3:** `var galaxy_text: [u8; _] = screen"GALAXY 00\0"`. `GALAXY_TEXT_LEN` is gone;
`restore_tiles` takes `len(galaxy_text) - 1` (Prog8's `len()` of a `str` leaves out the 0). The same program,
byte for byte.

### 25. Assembly and plain Tern in step 5

| Routine | Prog8 | plain Tern | kept |
|---|---|---|---|
| `sound.step` (`sound.tick`, gap 18), per frame in the IRQ | asm | 33 cycles a call, 109 bytes; the asm port 33 cycles, 100 bytes | plain |
| `title.sweep_all`, 93 logo chars, before the beam reaches the logo | asm, 4930 cycles | 6063 cycles | asm port, 4750 cycles |
| `demo.find_near`, every star each frame | asm | part of `demo.steer`: 669 cycles a frame on average against Prog8's 779 | plain |
| `demo.dist` | asm | not tried (16-bit absolute difference through the carry, as `explode.hits` in gap 21) | asm port |
| `video.put_score` | asm | at most once a frame | plain |

With `sound.tick` called from the frame IRQ, the handler now starts with `cld` and saves A, X and Y (gap 6).

### Step 5 notes (no gap)

- Struct literals in `data` worked as described for the page text (`pages.tern`: `Line { row, col, text }`) and
  the logo letters (`title.tern`: `Letter { shape, col, row }`), in place of Prog8's parallel arrays.
- Prog8's `title.launch` divides a signed word by 8; Prog8 compiles it as an arithmetic shift (rounds down),
  which is what Tern's `>>` on an `i16` does, so the fliers' paths are the same.
- `input` and `demo` import each other (`input.read` calls `demo.steer`), and so do `demo` and `game`; that works.
- Not ported: the Prog8 self-tests (`main.TEST`, `DEBUG_HUD`, `PROFILE`, `prof.p8`, self-test 40 in `demo.p8`).
  `tools/compare.py` does their job for the port.

## M20 follow-up 3: the workarounds removed

Each gap's plain form was its own commit, built and checked. `tools/compare.py` ran after gap 1, after gap 12,
after gap 15 and at the end: all 13,211 game frames (4 scripted runs, 3 played by the demo pilot) and every stars
result the same as the Prog8 build. `check_player.tern` gives the same values byte for byte. In VICE the attract
cycle (title, logo, HOW TO PLAY, SCORING, the demo game) is pixel for pixel the same as the baseline build at 5,
12, 20, 30, 40 and 50 s. (Two VICE runs of one build can differ in the logo's random twinkles, as the start
timing varies; the final build matched a baseline run exactly.)

| After | PRG bytes | Change |
|---|---|---|
| baseline (the workarounds, M20 follow-up 3 compiler) | 19,113 | |
| gap 1, `c64.cia_irqs_off()` | 19,117 | +4 |
| gap 11, `len()` in a const | 19,117 | same code |
| gap 12, one expression | 19,113 | -4 |
| gap 13, `ptr + (u8 << 1)` | 19,101 | -12 |
| gap 14, `(d as i8) < 0` | 19,097 | -4 |
| gap 15, `var` in `asm fn` | 19,003 | -94 |
| gaps 17, 18, 19, 24 | 19,003 | byte for byte the same |

The play frame went from 4,784 to 4,788 cycles on average, although every changed routine got shorter or
stayed the same. The difference is code placement: `hostiles.draw` (232 to 241) and `demo.steer` (666 to
670) moved, and `hostiles.draw`'s loop now branches across the $0D00 page boundary, a cycle more per taken
branch. The same happened in the stars harness: `next_busy`'s loop now straddles $0C00, so `stars.update` with
one hit lockout went from 1,002 to 1,064 cycles with the same instructions. The parts that changed got faster
(`player.update` 330 to 325, `stars.update` 1,295 to 1,293, `explode.update` 292 to 291 cycles a frame).
Not logged as a gap: a compiler could keep short loops off page boundaries, but that is optimizer work.

### 26. A constant added after `ptr + (u8 << 1)` in the same statement is a second 16-bit add

`stars.p8` `set_color`: `^^ubyte @zp p = video.tile_row_addr[r] + (c << 1) + (video.COLOR_RAM - video.SCREEN)`.
Gap 13's fix gives the short add for `tile_row_addr[r] + (c << 1)`, but the constant (`$1800`) then goes
through `p` in a second 16-bit add, with `adc #$00` for its low byte:

```tern
var p: ptr u8 = video.tile_row_addr[r] + (c << 1) + (video.COLOR_RAM - video.SCREEN)
```

```text
lda stars.set_color.c       ; 3
asl a                       ; 2
clc                         ; 2
ldx stars.set_color.r       ; 3
adc video.tile_row_addr,x   ; 4+
sta stars.set_color.p       ; 3
lda video.tile_row_addr+12,x; 4+
adc #$00                    ; 2
sta stars.set_color.p+1     ; 3
clc                         ; 2
lda stars.set_color.p       ; 3
adc #$00                    ; 2
sta stars.set_color.p       ; 3
lda stars.set_color.p+1     ; 3
adc #$18                    ; 2
sta stars.set_color.p+1     ; 3
```

With the constant first (`tile_row_addr[r] + (COLOR_RAM - SCREEN) + (c << 1)`), `c << 1` goes through a 16-bit
temporary, as before gap 13's fix. From the listings:

| `set_color`'s address | bytes | cycles |
|---|---|---|
| one statement, Prog8's order | 31 | 44 |
| one statement, the constant first | 37 | 54 |
| the old workaround: `tile_row_addr[r] + $1800`, then `p += c << 1` | 27 | 37-41 |
| kept: `tile_row_addr[r] + (c << 1)`, then `p += COLOR_RAM - SCREEN` | 21 | 30 |

The kept form is two statements, but both are plain ones (the second is the same line `set_look` has):

```tern
var p: ptr u8 = video.tile_row_addr[r] + (c << 1)
p += video.COLOR_RAM - video.SCREEN    // one statement is slower (gap 26)
```

What would have helped: the constant terms of a pointer sum added together and folded into the high-byte add
(`adc #$18` in place of `adc #$00`, nothing for the low byte): 18 bytes and 26 cycles in one statement.

## V1: the assembly measured again (2026-11-21)

Roadmap V1, Galaxy Stars step 2, with the compiler at `b409d9a` (Tern `main` after R8). The port rebuilt as it is:
18,993 bytes, 4,782 cycles per play frame (Prog8 5,586), all 13,211 `compare.py` game frames the same, the
title screen in VICE as before. Each routine still in assembly was written in plain Tern again, checked for the
same results, and measured: with `tern bench` and a harness for those outside `compare.py`'s frame parts
(`draw_tile`, `rnd`, the explode drawing, the logo recolor), and in the game runs for the rest. Every plain
version gave the same results (the same screen and color RAM, or all game frames the same); none was as fast,
so all stay assembly, with these numbers. The source is unchanged.

| Routine | Measured | asm | best plain Tern | plain, first try |
|---|---|---|---|---|
| `stars.rnd` | per call | 23 B, 42 cycles | 33 B, 57 cycles | |
| `video.draw_tile` | 240 tiles, average | 125 B, 145 cycles | 165 B, 193 cycles (a color RAM row table) | 170 B, 199 cycles |
| `explode.draw_area` | 72 areas, average | 191 B, 1,422 cycles | 226 B, 1,750 cycles (`while` loops) | 242 B, 1,799 cycles |
| `explode.draw_frame` | 72 areas, average | 142 B, 985 cycles | 158 B, 1,180 cycles (`while` loops) | 168 B, 1,190 cycles |
| `explode.restore_area` | 72 areas, average, with its `draw_tile` calls | 52 B, 355 cycles | 65 B, 401 cycles (`while` loops) | 77 B, 434 cycles |
| `explode.hits` + `hostiles.explosion_hit` | `hostiles.check_explosions`, a frame, average / worst | 122 / 3,674 | 179 / 5,379 | `hits` inline: 185 / 5,261 |
| `explode.hits` | `explode.check_player`, a frame | 80 / 784 | 84 / 878 | `hits` inline: 84 / 820 |
| `hostiles.draw` | a frame | 237 / 576 | 253 / 643 (`C + x[i]`, gap 27) | 255 / 665 |
| `hostiles.move` | `hostiles.update`, a frame | 878 / 5,687 | 906 / 5,718 (`d < 0` first, `x[i]` in place) | 949 / 5,888 |
| `demo.dist` | `demo.steer`, a frame | 669 / 4,920 | 710 / 5,218 (one branch per axis) | 740 / 5,538 |
| `title.sweep_all` | 93 logo chars, per call | 37 B, 4,750 cycles | 47 B, 4,947 cycles (`(COLOR_RAM + cell_off[c]) as ptr u8`) | 55 B, 6,063 cycles (`p = COLOR_RAM; p += cell_off[c]`, the port's form) |
| `score.add_once`, `add_acc`, `raise_next` | | | not tried: decimal mode (gap 20) | |

The closest are `sweep_all` (4% slower; it was 28% at the port), `hostiles.move` and `hostiles.draw`. What the
plain versions lose, from their listings:

- `rnd`, `draw_tile`, the explode drawing: the carry. `rnd` adds the carry of `lsr` by hand; `ch + 1`, `ch + 2`,
  `ch + 3` each reload `ch` and clear the carry, where the asm chains `adc #1` (gap 16, unchanged).
- `draw_tile`: `clr = scr + (COLOR_RAM - SCREEN)` is a copy and a full 16-bit add (30 cycles; the asm adds only
  the high byte), and the color is reloaded after each `sta (clr),y`, correctly, since `clr` could point at it.
- The explode loops: `for tr in r0..=r1` copies its bound and checks for an empty range on entry (about 19
  cycles a loop; the asm knows the area is never empty); `while true` with `break` at the last value saves it.
  `restore_area` also reloads `cover[i]` after its `dec` (gap 28).
- `explosion_hit`: the slot loop's counter lives in memory, 22 cycles per empty explosion slot against the asm's
  11 with the slot in Y kept across `jsr hits` (gap 29), up to 48 times a frame; and `obj_x = x[i] + BOX / 2` is
  a copy and an add (gap 27).
- `hostiles.draw`: the sprite x is `x[i] + 20` as a copy and an add (gap 27), and then reloaded for the store and
  the high-byte test, where the asm uses the carry of its `adc`: about 8 cycles a hostile.
- `hostiles.move`: `d > 0` on an `i8` is a full signed compare (`sec`, `sbc #1`, `bvc`, `eor #$80`, `bmi`, 10
  cycles) where the asm branches on the flags its `ror` left; the parameters, `t`, `d` and the result go
  through memory; one `jmp` lands on another `jmp`. Testing `d < 0` first and then `d != 0` takes most of the
  loss back.
- `demo.dist`: both 16-bit subtracts are a copy and a subtract (gap 27), and the sign needs a compare of its own
  where the asm uses the carry of its `sbc`.

Code generators (gap 2): `video.frame_irq`'s 32 hand-written sprite copies as a generator (`const fn
copy_sprites()` with an `emit` of 8 `lda`/`sta` per sprite, called in an `asm { }` line) give the same 229 bytes
and 330 cycles, instruction for instruction. The handler keeps the plain statements: the generator would put
plain Tern into an `asm` block for no gain.

### 27. A 16-bit `a + b` or `a - b` with a plain variable or constant on the right is a copy and an in-place add

`demo.p8` `dist`, `hostiles.p8` `draw` and `explosion_hit`, in their plain Tern versions. Assigned to a 16-bit
variable, `a - b` or `a + b` is compiled as `t = a` and then `t -= b` in place when `b` is a variable or a
constant; with an array element on the right it is the direct form. So the order of the operands changes the
code:

```tern
var dx: u16 = cx - pcx              // copy, then subtract in place: 34 cycles
var hx: u16 = x[i] + (24 - BOX_INSET)   // copy, then add in place: 26 cycles or more
var hx: u16 = (24 - BOX_INSET) + x[i]   // direct: 20 cycles
var dxw: u16 = obj_x - core_x[s]    // direct
```

```text
; var dx: u16 = cx - pcx            (cx is in A and Y on entry, stored first)
lda demo.dist.cx       ; 3
sta demo.dist.dx       ; 3
tya                    ; 2
sta demo.dist.dx+1     ; 3
sec                    ; 2
lda demo.dist.dx       ; 3
sbc demo.pcx           ; 3
sta demo.dist.dx       ; 3
lda demo.dist.dx+1     ; 3
sbc demo.pcx+1         ; 3
sta demo.dist.dx+1     ; 3
```

What would have helped: the direct form (`sec`, `lda cx`, `sbc pcx`, `sta dx`, `lda cx+1`, `sbc pcx+1`,
`sta dx+1`: 20 cycles, or less with `cx` still in A and Y) whatever is on the right. `-` cannot be swapped, so
`dist` has no workaround.

### 28. `m[i] -= 1` then `if m[i] == 0` reloads `m[i]`

`explode.p8` `restore_area`, plain version: `dec explode.cover,x` is followed by `lda explode.cover,x` before the
`bne`, although `dec` set the zero flag for the same byte (`cover` is not volatile). 4 cycles per tile, 9 tiles
per explosion.

```tern
cover[i] -= 1
if cover[i] == 0 {
    video.draw_tile(tc, tr)
}
```

What would have helped: the optimizer testing the flags `inc`/`dec` left when the next instruction reloads the
same byte to test it for zero.

### 29. A `using Y` loop cannot call a compiled function, even one that never changes Y

`hostiles.p8` `explosion_hit`: the asm keeps the explosion slot in Y across `jsr explode.hits`. In plain Tern,
`for s in 0..explode.MAX using Y` around `explode.hits(s)` is an error, also when `hits` is an `inline fn`
whose code never writes Y:

```text
hostiles.tern:592:13: error: The loop body calls a function, which can change Y. Remove 'using Y', or call only routines that keep Y
```

Only a routine at a fixed address can promise `keeps X, Y`. The loop counter goes to memory instead: 22 cycles
per empty slot against 11.

What would have helped: the compiler working out which registers a compiled function leaves alone (it knows
its code) and allowing the call, or a `keeps` on an `asm fn`; for an `inline fn`, checking the inlined code.

## V1 fixes: the assembly measured again (2026-11-21)

Roadmap "V1 fixes", its last "done when" item, with the compiler of Tern's `v1-fixes` branch at `4df0268` (gaps
26-29 fixed). The port as it is: 18,682 bytes, 4,680 cycles per play frame (Prog8 5,586), all 13,211
`compare.py` game frames the same. Each routine still in assembly was written in plain Tern again; the versions
are kept in `plain/`, with the scripts that measure them (`plain/README.md`), so they can be measured again
later. Every plain version gave the same results: all game frames the same in `plain/game.sh`, the same memory
in `plain/bench.sh`. The harnesses are new, so the assembly's numbers differ from the V1 table where it used
other ones (`draw_tile`, the explode drawing). The source is unchanged.

| Routine | Measured | asm | best plain Tern | slower | other forms |
|---|---|---|---|---|---|
| `stars.rnd` | per call (`bench_rnd`) | 23 B, 42 cycles | 33 B, 57 cycles | 36% | |
| `video.draw_tile` | 240 tiles, average (`bench_draw_tile`) | 125 B, 153 cycles | 160 B, 191 cycles (`clr = scr + (COLOR_RAM - SCREEN)`, the high byte only now) | 25% | `ch += 1` between stores: 157 B, 191 |
| `explode.draw_area` | 72 areas, average (`bench_explode`) | 191 B, 1,407 | 223 B, 1,692 (`while` loops) | 20% | `for` loops: 239 B, 1,768 |
| `explode.draw_frame` | 72 areas, average | 142 B, 785 | 163 B, 900 (`while` loops) | 15% | `for` loops: 173 B, 926 |
| `explode.restore_area` | 72 areas, average, with its `draw_tile` calls | 52 B, 745 | 63 B, 742 (`while` loops) | as fast | `for` loops: 75 B, 792 |
| `explode.hits` + `hostiles.explosion_hit` | `hostiles.check_explosions`, a frame, average / worst | 122 / 3,659 | 153 / 4,817 (the slot loop in Y, `hits` keeps Y) | 25% / 32% | `hits` comparing, then subtracting: 159 / 5,096 |
| `explode.hits` | `explode.check_player`, a frame | 78 / 769 | 84 / 910 | 8% / 18% | |
| `hostiles.draw` | a frame | 244 / 588 | 262 / 648 (`x[i] + C`, either order now) | 7% / 10% | |
| `hostiles.move` | `hostiles.update`, a frame | 861 / 5,561 | 890 / 5,670 (the slot in X; `d < 0` first) | 3.4% / 2.0% | the slot in memory: 901 / 5,707 |
| `demo.dist` | `demo.steer`, a frame | 660 / 4,878 | 710 / 5,229 (one branch per axis, the sum saturated in `u8`) | 7.6% / 7.2% | `u16` sums and `dx < 256`: 762 / 5,670 |
| `title.sweep_all` | 93 logo chars, per call (`bench_sweep_all`) | 37 B, 4,750 | 45 B, 4,761 (`(COLOR_RAM + cell_off[c]) as ptr u8`) | 0.2% | |
| `score.add_once`, `add_acc`, `raise_next` | | | not tried: decimal mode (gap 20) | | |

Program bytes with each best plain version (the asm build is 18,682): `dist` 18,781, `move` 18,731, `draw`
18,690, `hits` with `explosion_hit` 18,741, `restore_area` 18,693. Other frame parts move by a few cycles in each
build (page crossings after the code moved), so each row compares only its own part.

Within 5% on the average and the worst frame, so they can become plain Tern in batch 2: `hostiles.move`,
`explode.restore_area` and `title.sweep_all`. A build with all three (18,750 bytes, 4,706 cycles per play
frame; the 26 cycles are `move`'s) plays all 13,211 frames the same, and its title screen in VICE shows the logo
with its color band as the asm build does. The rest stay assembly. What the fixes changed, from the listings:

- Gap 27: `dist`'s subtracts, `draw`'s `x[i] + 20` and `explosion_hit`'s `x[i] + BOX / 2` are direct now.
- Gaps 26-27: `draw_tile`'s and `draw_area`'s color pointer adds only the high byte; `sweep_all`'s sum is the
  asm's code but for one instruction.
- Gap 28: `restore_area`'s `cover[i] -= 1` then `== 0` is `dec` and `bne`, since `4df0268` (before it, a table
  at a fixed address still reloaded: 822 cycles in the `for` form, 792 now).
- Gap 29: `explosion_hit`'s slot loop is in Y across the call to `hits` (`keeps Y`), as in the asm.
- `hostiles.move` with the slot in X (a `using X` parameter) takes most of the rest of its loss back.

What the plain versions still lose:

- `rnd`, `draw_tile`, `draw_area`, `draw_frame`: the carry (gap 16, unchanged): `ch + 1`, `ch + 2`, `ch + 3`
  each reload `ch` and clear the carry, where the asm chains `adc #1`.
- `dist`, `hits`: a compare and then the subtract again, where the asm subtracts once and branches on the
  carry; plain Tern has no way to read the carry of a subtract (`cpu.carry()` is only after an `asm` block).
  And gaps 30 and 31.
- `draw`, `hits`: gap 31.
- `hits`: the slot goes through memory (gap 32): `tya` / `sta hits.s` / `ldx hits.s` per call.
- The explode loops: gap 33, one cycle and 3 bytes per `break` test; `draw_area` also keeps the tile index and
  the column in two counters.

### 30. `(a as u16) + (b >> 1)` goes through two 16-bit temporaries

`demo.p8` `dist`, plain version, first try (`plain/demo.dist.first.tern`): the longer axis plus half the
shorter, 255 at most, as a `u16` sum:

```tern
var s: u16 = (d_ay as u16) + (d_ax >> 1)
if s > 255 {
    return 255
}
return s as u8
```

```text
lda demo.d_ax          ; 3
lsr a                  ; 2
sta demo.dist._t43     ; 4
lda #$00               ; 2
sta demo.dist._t43+1   ; 4
lda demo.d_ay          ; 3
sta demo.dist.s        ; 4
lda #$00               ; 2
sta demo.dist.s+1      ; 4
clc                    ; 2
lda demo.dist.s        ; 4
adc demo.dist._t43     ; 4
sta demo.dist.s        ; 4
lda demo.dist.s+1      ; 4
adc demo.dist._t43+1   ; 4
sta demo.dist.s+1      ; 4
```

Both widened bytes are stored as words and added as words: 51 cycles, then a 16-bit compare with 255. What
would have helped: a sum of two widened bytes as `lda a / lsr / clc / adc b / sta s / lda #0 / adc #0 / sta
s+1` (or, for `s > 255`, the carry). The form kept (`plain/demo.dist.tern`) saturates in `u8` (`if h > 255 -
d_ay { return 255 }`), which reads worse. Also: `dx < 256` on a `u16` compares both bytes (`cmp #0` / `sbc #1`);
`(dx >> 8) as u8 == 0` tests only the high byte, which here is the subtract's last result (`bne`).

### 31. A 16-bit local is stored and read back for its bytes

`hostiles.p8` `draw` and `explode.p8` `hits`, plain versions. A `u16` local computed and then used as its two
bytes is stored whole and each byte loaded again:

```tern
var sx: u16 = x[i] + (24 - BOX_INSET)
video.spr_x[s] = sx as u8
if (sx >> 8) as u8 != 0 {
```

```text
clc                    ; 2
lda hostiles.x,x       ; 4
adc #$14               ; 2
sta hostiles.draw.sx   ; 3
lda hostiles.x+6,x     ; 4
adc #$00               ; 2
sta hostiles.draw.sx+1 ; 3
lda hostiles.draw.sx   ; 3
sta video.spr_x,y      ; 5
lda hostiles.draw.sx+1 ; 3
beq hostiles.draw.else61
```

The asm stores the low byte straight to `spr_x` and tests the high byte in A after its `adc #0`: about 12
cycles less per hostile, most of `draw`'s loss. `hits` does the same with `d = obj_x - core_x[s]`. What would
have helped: the low byte stored where it goes while it is in A, and the high byte tested in A, when the local
is not read again.

### 32. A `using Y` loop variable cannot be passed to a parameter in Y

`hostiles.p8` `explosion_hit`, plain version: with `hits(s: u8 using Y) -> bool keeps Y`, the loop that keeps the
explosion slot in Y cannot pass it, though the value is already where the parameter wants it:

```tern
for s in 0..explode.MAX using Y {
    if explode.timer[s] != 0 && explode.hits(s) {
```

```text
hostiles.tern:593:45: error: 'explode.hits' takes 's' in Y, which holds the loop variable. Remove 'using Y' from the loop
```

With `s` in memory instead (`fn hits(s: u8) -> bool keeps Y`) the call is `tya / sta explode.hits.s` and `hits`
loads it into X: 8 cycles per call the asm does not spend, up to 48 calls a frame. What would have helped:
allowing the call when the argument is the loop variable itself and the function keeps Y.

### 33. `if c { break }` is a branch over a `jmp`

`explode.p8` `draw_area`, `draw_frame`, `restore_area`, plain versions with `while true` loops (the area is
never empty, so the asm tests at the end):

```tern
if tc == c1 {
    break
}
tc += 1
```

```text
lda explode.draw_frame.tc  ; 3
cmp explode.c1             ; 3
bne explode.draw_frame.endif40
jmp explode.draw_frame.wend37
explode.draw_frame.endif40:
inc explode.draw_frame.tc  ; 5
```

What would have helped: `beq wend37` when the target is in reach (1 cycle and 3 bytes per test).

## V1 batch 2: three routines moved to plain Tern (2026-11-22)

The routines within 5% of the assembly after V1 fixes (section above; decision 2026-11-22) are now plain Tern in
`src/`: `hostiles.move`, `explode.restore_area` and `title.sweep_all`, each the version in `plain/` without its
first comment line. `hostiles.move_edge` and `title.COLOR_RAM_HI`, read only by the assembly, are gone. Built with
the compiler at `main` `9462661` (V1 fixes merged), `compare.py --scenario game`: all 13,211 frames the same,
18,750 bytes (was 18,682 with all three in assembly) and 4,706 cycles per play frame (was 4,680; Prog8 5,586).
`hostiles.update` 890 / 5,671 (was 861 / 5,561); `explode.update` 298 / 3,212 (was 298 / 3,228). The title
screen shows in VICE. The other eleven routines stay assembly with the numbers in "V1 fixes".

## V1 fixes 2: the assembly measured again (2026-11-22)

Roadmap "V1 fixes 2", its "done when", with the compiler of Tern's `v1-fixes-2` branch (gaps 30-33 and smashy
smashy's gaps 1 and 3 fixed, and the small shapes). The port as it is: 18,674 bytes (was 18,750), 4,740 cycles per
play frame (was 4,706; Prog8 5,586), all 13,211 `compare.py` game frames the same. The source is unchanged. The
34 cycles are layout, not code: the listings differ only by removed instructions, and the part that got slower
(`stars.update`, +31) has the same code; with the code 76 bytes shorter, the loop branch of `stars.next_busy`
(`bne for115`, back) now crosses a page, a cycle per slot it passes. Every plain version gave the same results again (all game frames,
the same memory in the benches). New forms: `explode.hits.y` (the slot in Y, gap 32) and `explode.hits.carry`
(the asm's signature, so it replaces the asm alone).

| Routine | Measured | asm | best plain Tern | slower | V1 fixes | other forms |
|---|---|---|---|---|---|---|
| `stars.rnd` | per call | 23 B, 42 cycles | 33 B, 57 | 36% | 33 B, 57 | |
| `video.draw_tile` | 240 tiles, average | 125 B, 153 | 156 B, 185 | 21% | 160 B, 191 | `.step`: 153 B, 185 |
| `explode.draw_area` | 72 areas, average | 191 B, 1,410 | 213 B, 1,660 (`while`) | 18% | 223 B, 1,692 | `for`: 235 B, 1,751 |
| `explode.draw_frame` | 72 areas, average | 142 B, 785 | 157 B, 886 (`while`) | 13% | 163 B, 900 | `for`: 173 B, 926 |
| `explode.hits` + `hostiles.explosion_hit` | `check_explosions`, average / worst | 124 / 3,664 | 144 / 4,225 (`hits.y`) | 16% / 15% | 153 / 4,817 | s in memory: 148 / 4,465; `.branches`: 161 / 5,103 |
| `explode.hits` alone (`hits.carry`) | `check_player`; `check_explosions` | 78 / 769; 124 / 3,664 | 80 / 792; 127 / 3,931 | 2.6% / 3.0%; 2.4% / 7.3% | 84 / 910 (`check_player`) | |
| `hostiles.draw` | a frame | 242 / 582 | 265 / 660 | 9.5% / 13% | 262 / 648 | |
| `demo.dist` | `demo.steer` | 657 / 4,865 | 706 / 5,202 (`.first`: `u16` sums and `dx < 256`) | 7.5% / 6.9% | 710 / 5,229 | one branch per axis: 709 / 5,230 |

None is within 5% on both the average and the worst frame, so none moves to plain Tern in batch 3. The closest is
`explode.hits` alone with the asm's signature: within 3% on `check_player`, but 7.3% on the worst
`check_explosions` frame. What the fixes changed, from the listings:

- Gap 30: `demo.dist.first`'s `(d_ay as u16) + (d_ax >> 1)` is the byte add with the carry into the high byte, and
  `dx < 256` tests only the high byte, which made the first form the faster one (5,670 to 5,202 on the worst frame).
- Gap 31: the 16-bit temporaries go in `stars.player_contact`, `hostiles.fly`, `hostiles.aim`, `video.row_addr`
  and `title.parse_logo` (48 bytes of the 76).
- Gap 32: `explosion_hit`'s loop passes the slot to `hits` in Y with no load (`hits.y`).
- Gap 33: each `break` test in the explode loops is one branch.

What the plain versions still lose:

- `rnd`, `draw_tile`, `draw_area`, `draw_frame`: the carry (gap 16, unchanged).
- `draw`: gap 31's fix does not reach it. `video.spr_x` is read by the raster interrupt, so it is volatile, and the
  compiler does not move a volatile store up to where the byte was computed (`sta sx / lda x+6,x / adc #0 /
  sta _t2 / lda sx / sta spr_x,y / lda _t2`); question 186 in Tern's `docs/status.md`.
- `hits`, `dist`: the octagon test and the saturated sum; `hits` with the slot in memory also pays for the store.
