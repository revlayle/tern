# Galaxy Stars

A single-screen action game for the C64 by Rev. Layle, written in Prog8 and ported to Tern (V1's first
validation program; roadmap V1, decision 2026-11-21). MIT license (`LICENSE`).

- **Source:** the game's repository (`C:\game-dev\galaxy-star`), its Tern port in `tern/src/` on the branch
  `tern-port` at `fb0db78`, with V1 batch 2's changes: `hostiles.move`, `explode.restore_area` and
  `title.sweep_all` in plain Tern instead of assembly. The Prog8 original, the art tools and `compare.py` stay
  there.
- **Copied:** `src/` (the Tern source), `data/` (`chars.bin` and `sprites.bin`, which the game's art tool
  writes; `video.tern` reads them from `../data/` here, from `../../build/` in the game's repository) and
  `gaps.md` (the port's gap log).
- **Build:** `tern --target c64 src/main.tern -o galaxy-stars.prg`: 18,750 bytes, `$0801-$513E`.
- **Known result:** `expected/frame-3000.txt`, the screen (`$C000`), colour RAM and VIC registers after 3,000
  frames with no input (title, attract pages, then the demo playing), checked by
  `ValidationTests.Galaxy_stars_plays_its_demo` in the emulator, with the KERNAL's IRQ entry as the only ROM.
  With `TERN_REAL=1`, `Galaxy_stars_plays_its_demo_in_vice` runs it in VICE (`C:/Vice/bin/x64sc.exe`) for 60
  million cycles and compares the screenshot with `expected/vice-60M-cycles.png`: at most 1% of the pixels may
  differ, since VICE's timing varies by a frame or two from run to run (five runs: up to 0.29%).
  The deeper check is the game repository's `tools/compare.py --scenario game` against the Prog8 build: 7
  runs, 13,211 frames, every frame the same (4,706 cycles per play frame; Prog8 5,586).
