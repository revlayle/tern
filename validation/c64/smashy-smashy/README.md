# Smashy Smashy

A single-screen platformer for the C64 by Rev. Layle, written in Prog8 and ported to Tern (V1's second
validation program, batch 2; roadmap V1, decisions 2026-11-21). MIT license (`LICENSE`).

- **Source:** the game's repository (`C:\Games\c64-platformer-demo`), its Tern port in `tern/src/` on the branch
  `tern-port` at `295793c`. The Prog8 original, the art tool (`tools/gfx.py`), the port's `tools/assets.py` and
  its comparison with the Prog8 build (`tools/compare.py`, `compare_game.py`) stay there.
- **Copied:** `src/` (the Tern source, with `assets.tern`, which `tools/assets.py` generates there), `data/`
  (`chars.bin` and `sprites.bin`, which `gfx.py` writes; `c64/video.tern` reads them from `../../data/` here, from
  `../../build/` in the game's repository), `gaps.md` (the port's gap log).
- **Layout** (V1 batch 3, spec 2.2's platform folders): the game logic in `src/` (`main`, `actors`, `player`,
  `enemies`, `nav`, `levelgen`, `score`, `tilemap`, and `assets`, the art's names), the C64 front end in `src/c64/`
  (`video`, `sound`, `input`, `title`, `pages`, `popup`), the Atari front end in `src/atari/` (V1 batch 3, in
  progress; below). A shared module imports `video` and gets the target's. The shared modules
  name no C64 address: the tile map moved from `video` into `tilemap` (no longer at `$C400`), and the score
  flash (`video.hud_score_flash`), the game-over high score (`video.message_score`) and the random seed
  (`input.seed_random`, from the raster line and CIA 1's timer) are front-end calls. `c64/video.tern` reads the
  art from `../../data/`.
- **Build:** `tern --target c64 src/main.tern -o smashy-smashy.prg`: 20,985 bytes (20,946 before the split).
- **The Atari version** (V1 batch 3): `tern --target atari src/main.tern -o smashy-smashy.xex`, about 22 KB. Its
  own display list: a HUD row of ROM-font text (ANTIC mode 2), then the playfield in ANTIC mode 4, whose characters
  are the C64's multicolour ones, so `atari/video.tern` loads `data/chars.bin` as it is; the cell colours become
  one own colour per row (display-list interrupts: red, blue on the floor's rows) and PF3, the inverse characters'
  colour, for treasures and the fast-fall target. Messages and page text use a hand-made 3 x 7 font; the shake is
  VSCROL; sound is POKEY channel 1 with the C64's effect tables; Esc pauses. Sprites (`atari/sprites.tern`):
  player-missile graphics reused down the screen by the DLIs, 5 slots (4 players and the missiles as a fifth) for
  the player, 5 enemies and 2 popups, one colour each, converted from `data/sprites.bin` at compile time; popups in
  `atari/popup.tern`. The title (`atari/title.tern`): the C64's falling block letters, with the colour bands set
  per row by a DLI of its own; HOW TO PLAY and SCORING (`atari/pages.tern`) as on the C64. Checked in Altirra on an
  800 (48 KB, PAL) and an 800XL (NTSC).
- **Known result:** `expected/frame-2500.txt`, the score, time, level, treasures, kills and high score, the screen
  (`$C000`), colour RAM and VIC registers after 2,500 frames of a scripted joystick (through the title into a
  game, running, jumping and smashing; the first game ends and the second is being played), checked by
  `ValidationTests.Smashy_smashy_plays_a_scripted_game` in the test harness's emulator. The game waits for raster
  lines, reads raster lines against its frame IRQ (`frame_lines`, for map rebuilds) and scans the keyboard, so the
  run adds `C64Chips`: the raster line and the VIC's raster interrupt at the line the game sets, on one clock,
  and CIA 1 as the scripted joystick (ANDed with the keyboard row the game drives) and no keys. Its popups use
  the RAM under the KERNAL's IRQ entry at `$FF48`, so the run's KERNAL IRQ entry stub is at `$FFC0`
  (`ValidationTests.Kernal`). `tern run` has none of these, so this result is the harness's, not `tern run`'s.
  The Atari version's (V1 batch 3, step 4): `expected/atari-frame-2500.txt`, the same state, the sprite shadows and
  the screen after 2,500 frames of the same joystick script, checked by
  `ValidationTests.Smashy_smashy_plays_a_scripted_game_on_the_atari` with `AtariChips` (VCOUNT, WSYNC, the VBI and
  DLIs from the display list, the joystick through STICK0/STRIG0) and an OS stub for the NMIs; with `TERN_REAL=1`,
  `Smashy_smashy_shows_its_scoring_page_in_altirra` against `expected/altirra-800-pal-scoring.png`.
  With `TERN_REAL=1`, `Smashy_smashy_shows_its_scoring_page_in_vice` runs it in VICE (`C:/Vice/bin/x64sc.exe`)
  for 25 million cycles with no input (the SCORING page, whose chain values need its raster split) and compares
  the screenshot with `expected/vice-25M-cycles.png`, up to 1% of the pixels apart.
  The deeper check is the game repository's `tern/tools/compare.py` against the Prog8 build in py65: 200
  generated levels, 60,000 play frames and 3 whole runs of 15,000 frames, the same as Prog8 throughout
  (`gaps.md`, steps 3-6).
