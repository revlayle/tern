# Review V1 batch 2: Galaxy Stars' last steps, smashy smashy (branch `v1-smashy`)

Branch at `61e08b5`, from `main` at `9462661`. Eight commits; the compiler is not changed. The branch adds the test harness's `C64Chips`, `Png` and two `Run.Of` parameters (`until`, `bus`), `ValidationTests` (two emulator runs, two `TERN_REAL` VICE checks), and `validation/c64/galaxy-stars/` and `validation/c64/smashy-smashy/`.

**Not ready to merge**, for two small items: finding 1 (the harness's frame IRQ is not at the raster line the game sets, so smashy smashy's known result is not what a C64 does) and finding 2 (the known results cannot be reproduced in `tern run`, which decision 179 names, and the status does not say so). Everything else is done and checks out.

Checked:

- `dotnet test` in `../6502-language-dev` at `61e08b5`: 11,746 compiler tests passed, 3 skipped (`TERN_COMPARE` and the two VICE checks), CPU tests pass. With `TERN_REAL=1`, the four `ValidationTests` pass (VICE, 27 s).
- The expected screenshots show what the tests say: Galaxy Stars' demo playing (score 13000, DEMO on the HUD) and smashy smashy's SCORING page with its hi-res chain values.
- The copies match the game repositories: smashy smashy's `src/` is `tern-port` at `295793c` (`tern/src/`) except `video.tern`'s two `include_bytes` paths; Galaxy Stars' `src/` is `tern-port` at `b206fc2` except the same paths in `video.tern` and line endings in `demo.tern` (suggestion 5). Built with the branch's `tern`: 21,438 and 18,752 bytes with the load address, the READMEs' 21,436 and 18,750.
- Each README names the source, the author and the license (MIT, the developer's own code, so no license check is needed for either).

## Findings

### 1. The frame IRQ is not tied to the raster line, so `frame_lines()` and the joystick script read a clock the IRQ does not follow (must fix)

`tests/Tern.Compiler.Tests/Harness/C64Chips.cs:19` gives `$D012` as `Cycles % 19656 / 63`, a raster that starts at line 0 at cycle 0. The IRQ comes from `Run` (`Run.cs:59-72`): raised once `Cycles >= nextIrq`, taken when the CPU allows it, and the next one set to `Cpu.Cycles + IrqEvery` from the moment it was taken. So:

- The first IRQ is taken at the game's first `cli` (`video.start_frame_irq`), wherever the raster is then, not at `FRAME_IRQ_LINE` (251, `video.tern:243`) where the game sets the VIC's compare (`video.tern:295`).
- Each later IRQ comes `IrqEvery` after the cycle the last one was *taken*, so the IRQ drifts later against `C64Chips`'s raster by the instruction it waited for (0-6 cycles) plus any `sei` section, every frame: over 3000 frames, thousands of cycles, many raster lines. A VIC raster interrupt has a fixed period.

Smashy smashy reads the raster against its IRQ: `nav.idle_build` (`nav.tern:223`) runs map-rebuild slices while `video.frame_lines()` (lines since the IRQ line, `video.tern:321`) is under 185. On a C64 it is near 0 just after the IRQ. In the harness, with the IRQ taken near raster line 0, it is 61 (`lo + 312 - 251`) at that moment, and it moves as the IRQ drifts. The harness's game builds a different number of slices per frame than a C64, so a map rebuild finishes on a different frame and the enemies steer on the old map for a different time; the run is deterministic, so `expected/frame-3000.txt` is a stable snapshot, but not the game's behavior on the machine. The same drift moves `SmashyJoystick`'s frame (`Cycles / FrameCycles`, `C64Chips.cs:24`) away from the IRQ count that `until` uses.

The VICE check does not cover this: it stops on the SCORING page, before any play.

Fix: make the raster and the IRQ one clock. For example, `C64Chips` raises the IRQ itself when its line reaches the compare line the program wrote to `$D012` (with bit 8 from `$D011`) and the raster interrupt is enabled in `$D01A`, or `Run` keeps a fixed schedule (`nextIrq += IrqEvery`) and `C64Chips`'s line is offset so that the IRQ falls on line 251. Index the joystick by the IRQ count (`Run.Interrupts`) or by the same frame clock. Then rewrite `expected/frame-3000.txt` and read the diff. Galaxy Stars runs without `C64Chips` and reads the raster only for its random seed (`stars.tern:76`), so its result only moves if `Run`'s schedule changes for it too; a fixed schedule is right for it as well (its IRQ is the VIC's), but its expected file would move.

### 2. Neither known result can be reproduced in `tern run`, and the status does not say so (must fix)

Decision 179 and the roadmap's V1 ("The suite's known result is the screen and memory after a set number of frames in `tern run`"; done when "every program ... gives its known result in `tern run`") name the tool. Both results are checked only through the test harness, with parts `tern run` lacks:

- Galaxy Stars: `tern run --target c64 src/main.tern --irq 19656 --cycles 58968000 --screen` stops with "Illegal opcode $54 at $69F2: 59804 cycles ..., 1 interrupts": the IRQ goes through `$FFFE` with no KERNAL IRQ entry (the test's `ValidationTests.Kernal` puts one in). `tern run` also has no stop after a number of frames, and `--screen` shows `$0400`, where both games use `$C000` (`--dump $C000:1000` works).
- Smashy smashy: as above, plus the raster line and CIA 1 port A (`C64Chips`), which question 185 (gap 2) raises for port A and the raster only.

The fix asked of this branch is in the status, not the compiler: say that the known results are the harness's, list what `tern run` would need (a KERNAL IRQ entry stand-in, a raster line, port A as a joystick ANDed with the keyboard scan, a stop after n frames, the screen at the VIC bank's address), and widen question 185 so the spec session decides whether "in `tern run`" means the tool (a coding item for V1 fixes 2) or the suite's emulator (a roadmap edit).

### 3. Two KERNAL stubs where one would do (suggestion)

`ValidationTests.cs:189` (`Kernal`, entry at `$FF48`, exit at `$FF5B`) and `:204` (`KernalAt`, entry and exit at a given address) do the same job. Galaxy Stars keeps `Kernal` only so that its expected file does not move. `KernalAt(ram, 0xFFC0)` for both, with Galaxy Stars' expected file rewritten (and read), leaves one. If finding 1's fix moves Galaxy Stars' expected file anyway, this costs nothing more.

### 4. `C64Chips`'s port A ignores what the program drives (suggestion)

`C64Chips.cs:24`: `$DC00` reads the joystick with bits 5-7 always 1. On a C64, port A's lines driven low by the program read low (the "ANDed with the joystick" of question 185's gap 2). Neither game reads bits 5-7 or reads port A while scanning rows, so the results are right; a comment that says what is left out, or `inner.Read(0xDC00) & (joystick bits | 0xE0)`, would keep the next program from tripping on it. Port B (`$DC01`) as `$FF` is right for a joystick in port 2 with no keys, since a joystick in port 2 does not touch port B.

### 5. `demo.tern` has CRLF line endings (suggestion)

`validation/c64/galaxy-stars/src/demo.tern` is the only file in either copy with CRLF; the game repository's has LF. The build is the same; a diff against the game repository shows every line changed. Copy it again with LF.

### 6. `RunVice` discards two tasks (suggestion)

`ValidationTests.cs:136-137`: `ReadToEndAsync()` is called for both streams and the tasks dropped, to keep VICE from blocking on a full pipe. It works; `RedirectStandardOutput = false` (and error) does the same with less, since nothing reads the output.

## Done when (roadmap V1, the items for this batch)

- [x] Galaxy Stars' routines within 5% in plain Tern (`hostiles.move`, `explode.restore_area`, `title.sweep_all`), all 13,211 frames the same in `compare.py`, recorded in the port's `gaps.md`; committed there after the developer's approval (`b206fc2`).
- [x] Galaxy Stars' Tern source and the files its build reads copied into `validation/c64/galaxy-stars/`, with `README`, `LICENSE` and `gaps.md`; the Prog8 original, `compare.py` and the art tools stay in the game's repository.
- [x] Smashy smashy ported from the `tern` skill, natural Tern (one small `asm` block, in `levelgen.tern`), checked against the Prog8 build (200 levels, 60,000 play frames, 3 whole runs of 15,000 frames), copied into `validation/c64/smashy-smashy/`.
- [x] A gap log for smashy smashy (gaps 1-3), sent to the spec session (question 185).
- [ ] Each program gives its known result in `tern run`: in the test harness only (finding 2); smashy smashy's harness run does not model the frame IRQ's timing (finding 1).
- [x] Each program gives its known result on its emulator with `TERN_REAL=1` (VICE, both pass; the 1% tolerance is question 184).
- [x] The source and license of each port recorded (the developer's own games, MIT).
- [ ] Every gap sorted and each bug fixed: for the spec session (question 185) and V1 fixes 2; not this branch's to finish.
