# Smashy Smashy

A single-screen platformer for the C64 by Rev. Layle, written in Prog8 and ported to Tern (V1's second
validation program, batch 2; roadmap V1, decisions 2026-11-21). MIT license (`LICENSE`).

- **Source:** the game's repository (`C:\Games\c64-platformer-demo`), its Tern port in `tern/src/` on the branch
  `tern-port` at `295793c`. The Prog8 original, the art tool (`tools/gfx.py`), the port's `tools/assets.py` and
  its comparison with the Prog8 build (`tools/compare.py`, `compare_game.py`) stay there.
- **Copied:** `src/` (the Tern source, with `assets.tern`, which `tools/assets.py` generates there), `data/`
  (`chars.bin` and `sprites.bin`, which `gfx.py` writes; `video.tern` reads them from `../data/` here, from
  `../../build/` in the game's repository), `gaps.md` (the port's gap log).
- **Build:** `tern --target c64 src/main.tern -o smashy-smashy.prg`: 21,436 bytes, `$0801-$5BBC`.
- **Known result:** `expected/frame-2500.txt`, the score, time, level, treasures, kills and high score, the screen
  (`$C000`), colour RAM and VIC registers after 2,500 frames of a scripted joystick (through the title into a
  game, running, jumping and smashing; the first game ends and the second is being played), checked by
  `ValidationTests.Smashy_smashy_plays_a_scripted_game` in the test harness's emulator. The game waits for raster
  lines, reads raster lines against its frame IRQ (`frame_lines`, for map rebuilds) and scans the keyboard, so the
  run adds `C64Chips`: the raster line and the VIC's raster interrupt at the line the game sets, on one clock,
  and CIA 1 as the scripted joystick (ANDed with the keyboard row the game drives) and no keys. Its popups use
  the RAM under the KERNAL's IRQ entry at `$FF48`, so the run's KERNAL IRQ entry stub is at `$FFC0`
  (`ValidationTests.Kernal`). `tern run` has none of these, so this result is the harness's, not `tern run`'s.
  With `TERN_REAL=1`, `Smashy_smashy_shows_its_scoring_page_in_vice` runs it in VICE (`C:/Vice/bin/x64sc.exe`)
  for 25 million cycles with no input (the SCORING page, whose chain values need its raster split) and compares
  the screenshot with `expected/vice-25M-cycles.png`, up to 1% of the pixels apart.
  The deeper check is the game repository's `tern/tools/compare.py` against the Prog8 build in py65: 200
  generated levels, 60,000 play frames and 3 whole runs of 15,000 frames, the same as Prog8 throughout
  (`gaps.md`, steps 3-6).
