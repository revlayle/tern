# Review: V1 batch 3, smashy smashy on the Atari, step 1 (interim)

Branch `v1-smashy-atari` at `52bd2f8` (from `v1-smashy-nes`, with `main` at `20841cc` merged in). An interim review,
asked for by the developer (2026-11-23) while the batch is at step 1 of 5. It covers the C64 split (`199b29a`), the
validation copies of the V1 fixes 2 gap logs (`bc822ca`) and Atari step 1 (`60cd167`). The branch changes only
`validation/`, `ValidationTests` and `docs/status.md`; no compiler or library code.

## Verdict

**No must-fix findings in the work so far.** The split is clean, the C64 known result is unchanged, and the Atari
front end's display list, DLIs, VSCROL shake, VCOUNT line count, POKEY divider table and input are correct as far as
reading them and the build can check. Not for merging yet: most of the batch's "done when" items are steps 2-5
(checklist below). The four suggestions below are small; finding 1 is worth doing before step 2 adds more DLIs.

`dotnet test` on the branch: 11,871 passed, 3 skipped (`TERN_COMPARE` and the two `TERN_REAL` checks), as the
status says.

## Findings

### 1. Suggestion: the DLI picks its branch from a flag that only the deferred VBI clears

`validation/c64/smashy-smashy/src/atari/video.tern:181-197` and `:386`. `dli()` decides between "end of the HUD"
and "end of row 22" by `dli_floor`, which the HUD branch sets and `frame_vbi`, the **deferred** VBI, clears. The OS
skips the deferred VBI when it interrupted time-critical code (CRITIC set, or the I flag set; the library's own
comment on `set_vbi` says so). In a frame where it is skipped, `dli_floor` stays true. In the next frame the HUD's
DLI then takes the floor branch, while the OS's immediate VBI has put the ROM font back in CHBASE from the CHBAS
shadow. The whole playfield then shows ROM-font glyphs as mode 4 characters for one frame.

Not reachable in this build: the Atari build has no `sei` (checked in the listing), and the game does no SIO. A
related case is harmless: `start_frame_irq` turns DLIs on mid-frame, so the first DLI can be row 22's and run the
HUD branch for one frame. But step 2 is likely to add DLIs (player-missile reuse down the screen), with more state
of this kind. A rule that does not depend on the deferred VBI is safer: choose the branch from `VCOUNT`, or reset a
DLI index in an immediate VBI (`atari.set_vbi_immediate` exists).

### 2. Suggestion: `var page` in the DLI is stored and reloaded

`video.tern:191-193`. `var page: u8 = addr(charset).hi` before `wsync` compiles to `lda #video.charset.hi` /
`sta video.dli.page`, then after `wsync` `lda video.dli.page` / `sta atari.antic+9` (listing of the Atari build,
`video.dli`). The local does not hold the value in a register over the `wsync` write, so it gains nothing: the
store after WSYNC is 2 cycles later than `atari.antic.chbase = addr(charset).hi` would be, and the DLI is 6 bytes
longer. Write the store directly. Whether a local set once from a link-time constant should be folded is a code
shape the spec session could sort; it could go in `gaps.md` as gap 5.

### 3. Suggestion: two byte counts for the same C64 build

`docs/status.md` (Atari step 1, first item) says the C64 build is "byte for byte the same, 20,983 bytes". The split's
paragraph and `validation/c64/smashy-smashy/README.md` say 20,985. The `.prg` built from the branch is 20,985 bytes.
The 20,983 is probably without the load address. Use one count, and say which one.

### 4. Suggestion: the Atari build test could check the player-missile area too

`ValidationTests.Smashy_smashy_builds_for_the_atari` checks the alignment of the screen, the character set and the
display list, but not `video.pm` (2 KB aligned, as single-line PMBASE needs). It is unused until step 2, so add the
check with step 2.

## Checked and found right

- **The split:** the shared modules differ from `main` only by the renames `video.tiles`/`T_*`/`MAP_*`/`row_start`
  -> `tilemap.*`, `levelgen.seed()` -> `input.seed_random()` (the same four reads, moved to `c64/input.tern`), and the
  game-over score and HUD flash behind `video.message_score` / `video.hud_score_flash` (the same writes). No shared
  module imports `c64` or names a `$Dxxx` address or a C64 `video` internal (grep). `expected/frame-2500.txt` is
  unchanged and `Smashy_smashy_plays_a_scripted_game` passes. The old tile map at `$C400` was outside the dumped
  screen (`$C000-$C3E7`), so moving it changes no expected byte.
- **The display list:** 40 of its 48 bytes. The vertically scrolled area shows (8 - v) + 24 x 8 + (v + 1) lines
  whatever VSCROL is, so the screen keeps its height through the shake (2 -> 0, the playfield moves down 2 lines).
  The floor DLI is on char row 22, the last before tile row 11 (char rows 23-24). The screen (1040 bytes, 2 KB
  aligned) cannot cross a 4 KB boundary.
- **Mode 4 colours:** pixel values 01/10/11 = PF0/PF1/PF2 (PF3 for inverse characters), as the comments say. Char 0
  is the empty tile's top-left (`TILE_EMPTY_TL - TILE_FIRST_CHAR = 0`), so the blank row and spaces in messages are
  empty playfield. 76 tile chars + 41 font glyphs = 117 of 128.
- **The message font:** screen code ^ `$20` gives the ROM font's internal code (`A` 1 -> 33, `0` `$30` -> `$10`).
  `halve` maps ROM pixels 1-2, 3-4, 5-6 to the first three mode-4 pixels as PF1. Every `screen"..."` string in the
  shared modules and the Atari title uses only letters, digits and `? + - / !`.
- **`frame_lines`:** VCOUNT 124 is line 248, where the VBI starts. PAL: 0-62 in the blank, then 64 + 2v up to 254,
  then 255. NTSC (frame_tail 14): 255 from VCOUNT 121. GTIA's PAL register is read the right way round
  (`& $0E != 0` is NTSC).
- **Sound:** `N = 2107 / h - 1` follows from the SID's ~15.03 Hz per high-byte step and POKEY's 63,337 Hz clock
  (PAL; about 1% off on NTSC). It is clamped to 255 below h = 9. No effect's slide wraps its pitch.
- **Input:** STICK0's bits are the C64 port's order. Esc is read from SKSTAT bit 2 and KBCODE `$1C`. CH is kept
  empty.
- **Score flash:** the inverse bit (`hud_flash`) cannot carry into a new game, since `game_over()` calls
  `score.stop_flash()`.
- **Gap 4** reproduces as logged: `[addr(screen) + r * 40 for r in 0..25]` gives "Element 0 is a label, but the table
  holds ptr u8. Use round() or floor() for a real number", whose fix is about real numbers and does not apply.

## Checklist (roadmap V1, smashy smashy's batches and the Atari batch)

| Item | State |
|---|---|
| Starts from `v1-smashy-nes`, C64 port split with platform folders; the C64 front end in `c64/` | done (`199b29a`) |
| Shared modules name no C64 address; random seed behind a front-end call | done |
| C64 known result kept | done (expected file unchanged, test passes) |
| Atari: 7 moving objects, PMG with DLIs or software sprites, whichever measures better | not yet (step 2; the trade-offs are written in the status) |
| Atari: the shake through VSCROL | done (played in Altirra) |
| Atari: POKEY for sound | done in code; not heard yet (the status says so) |
| Atari: the map rebuild's budget from VCOUNT (question 189) | partly: `frame_lines()` from VCOUNT; the budget is measured in step 5 |
| Known result on the Atari in the test suite's emulator, and an Altirra screenshot with `TERN_REAL=1` | not yet (step 4); only a build-and-layout test so far |
| Cross-machine known result (generated levels and route tables) | not yet (step 4) |
| Gaps logged in `gaps.md` | done for step 1 (gap 4; finding 2 is a candidate) |
| `examples/smashy-smashy/` with README | after the last platform batch (roadmap) |
