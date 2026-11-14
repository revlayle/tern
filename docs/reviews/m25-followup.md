# Review: M25 follow-up, Atari `io` and hex letters

Branch `m25-atari-io` (`5d91d21`, `33b382f`, `fc6fe89`, `0f54521`, `2790522`, `74c1111`, `b0aca00`), from `main` at `1c3e63d`. Roadmap "M25 follow-up"; spec 10.1, 12.2, 13; decision 2026-11-13, questions 131, 132, 136.

## Verdict

**Ready to merge.** No must-fix findings.

`dotnet test`: 3475 compiler tests pass (the 36 CPU tests are unchanged). No benchmark changed (1254 / 350780, as the status says). `SkillTests` pass.

## What changed

- **Hex letters** (`5d91d21`): `text`'s digit table moved to a new library module `digits` (`lib/digits.tern`, screen codes); `lib/atari/digits.tern` replaces it with ATASCII through the existing platform-folder rule (spec 2.2). No language change, and `text` is not copied. C64 bytes are unchanged (benchmarks, `LibraryTests.Hex8_for_every_value`).
- **`lib/atari/io`** (`33b382f`): `open`, `close`, `read`, `write`, `status`, `load`, `save`, `load_xex`, all on IOCB 1. CIO is a routine at a fixed address with the result in N. `read` and `write` skip CIO for a length of 0 (CIO would move one byte through A). `load`, `save` and `load_xex` always close, and `close_after_error` keeps the first error's status. `load_xex` handles an extra `$FF $FF` before a segment and stops cleanly at end of file between segments.
- **`tern run`** (`fc6fe89`): CIO on IOCBs 1-7 opens the build's overlay files for reading; writing and other commands stop the run with a message that lists the stand-ins.
- **Label file** (`0f54521`): `.lbl` is now MADS's label table, which Altirra loads with the `.xex` of the same name. The C64's `.vs` is unchanged. Question 138 asks the spec session to record the format change.
- **Example** `examples/atari/files.tern` with the overlay `greet.tern`; skill updates.

## Checks

- **Library against CIO's documented behavior**: status in Y and ICSTA with N on an error; ICBLL/ICBLH hold the count moved after get bytes, also at end of file; an IOCB stays taken after a failed open, so the whole-file functions close it. All match the library and both stand-ins (the test's `FakeCio` and `tern run`'s `AtariMachine`).
- **Short-circuit in `save`** (`open(...) || write(...)`): `write` is not called after a failed open. Tested by `Save_fails_when_the_iocb_is_taken_and_keeps_the_error`.
- **Altirra**, run by the reviewer: `files.xex` on an 800 with 48 KB, AltirraOS, `/hdpathrw` on the build folder, prints "Saved H:SCORES.DAT", "Loaded 0A 14 1E 28 FF 00 C8 9B", "Same bytes", "Hello from an overlay"; the hex letters show as letters, and the host folder holds an 8-byte `scores.dat`. (Run `altirra-shot.ps1` from PowerShell, not Git Bash: Git Bash rewrites `/tempprofile` and `/hdpathrw` into paths, so Altirra gets no `H:` device and the program reports "Save failed: $82".)

## Findings

1. **Suggestion: the new library module `digits` breaks a program that has its own module named `digits`.** `src/lib/text.tern:5` imports `digits`, which is now a public library module on every target. By spec 2.2 a user module with a library module's name is an error, so this program compiles on `main` and fails on the branch, for both targets:

   ```
   // digits.tern
   const N = 3
   // main.tern
   import digits
   var x: u8
   fn main() { x = digits.N }
   ```
   "error: 'digits' is both a library module (...lib\digits.tern) and your file ...\digits.tern. Rename your file, e.g. to my_digits.tern, and import it by its new name".

   The error is clear and the roadmap suggested this design, but `digits` is a common name for a helper that only exists so a platform can swap one table. Either give it a name a program is unlikely to use, or add it to the library table in spec 10.1 so it is a known reserved name (a spec session question).

2. **Suggestion: `tern run` accepts overlay names that Atari DOS cannot store.** `Machine.cs:253` matches any overlay's name in capitals after the device, so an overlay module `level_1` or `level_one_boss` loads as `D1:LEVEL_1.XEX` in `tern run`. DOS 2 file names are 1-8 letters and digits starting with a letter, plus a 3-character extension; `_` and longer names do not fit. Altirra's `H:` device maps to host files, so the example's check would not show it either. A program that passes in `tern run` can then fail on a real disk. Options: the stand-in fails such a name with the DOS error, or the compiler warns when an Atari overlay's name is not a valid DOS 2 name. Belongs with question 139.

3. **Suggestion: `status()`'s comment says the status is `ST_OK` or 128 and above** (`src/lib/atari/io.tern:14-15`, `:96`). CIO's statuses 2-127 are also success, and the disk handler can return 3 when the byte just read is the last one in the file (Atari DOS documents 3 as "end of file next"). The code is correct, since every check uses N or `>= 128`; only the comment and the skill's wording ("1 is success") would lead a program to test `status() == io.ST_OK` and treat that read as a failure. Write "below 128 is success".

4. **Suggestion: `load_xex` does not check that a segment's last address is at or above its first** (`src/lib/atari/io.tern:153-158`). In a malformed file with last = first - 1, `size` is 0 and the segment loads nothing without an error; with last further below first, `size` wraps and `read` writes up to 64 KB over memory, the loader's own variables included. Only malformed files reach this, and DOS behaves no better, so this is optional: a `last < first` check that fails with `close_after_error()` costs a few bytes.

## Done when

- [x] Emulator tests cover `io`: `AtariIoTests` (24 tests: open, name length 0-39 and over, read to end, read errors, write, load/save round trips at several sizes, `max`, missing file, full disk at close, IOCB taken, `load_xex` with page crossing, extra `$FF $FF`, RUNAD, and five truncated files); `AtariRunTests` for the `tern run` stand-in. The C64's `io` keeps its M14 tests.
- [x] Emulator tests cover the hex letters on both targets: `AtariLibraryTests.Hex_digits_are_atascii` and `Hex_digits_print_as_letters` on the Atari; `LibraryTests.Hex8_for_every_value` and `Hex16_on_edge_values` on the C64.
- [x] An example that saves a file and loads it back runs in Altirra on an 800 with 48 KB (AltirraOS) through `H:`: `examples/atari/files.tern`, checked by the coding session on the 800 and the 800XL, and again by the reviewer on the 800.
- [x] Roadmap items: `io` functions as listed, ATASCII names with their device, true on failure, `status` with CIO's code; an overlay loader (`load_xex`); the `tern run` stand-in for the build's overlay files; `hex8`/`hex16` in ATASCII through a platform module, without copying `text`; the `.lbl` file loaded in Altirra and its format fixed (question 138).

## Re-review

Commits `ebc279e`, `ca961b3`, `4fc3f34`, `f345171`, after `main` was merged in (`eec8c1c`). `dotnet test`: 3484 compiler tests pass. No benchmark changed.

**Verdict: ready to merge.** All four suggestions are handled, each with a test:

1. **Done.** The module is now `text_digits` (both the generic and the Atari file). `ModuleTests.A_program_may_have_its_own_module_named_digits` compiles the program from finding 1 together with `text.hex8`, on both targets. No reference to the old name remains in the sources, the skill or the tests.
2. **Done in `tern run`.** The CIO stand-in checks the part after `:` against DOS 2's rule (`^[A-Z][A-Z0-9]{0,7}(\.[A-Z0-9]{0,3})?$`) and fails a name that does not fit with `$A5`, before it looks for an overlay. A valid name that is not an overlay still gives `$AA`. `AtariRunTests` covers lowercase, `_`, 9 characters, a 4-character extension and a leading digit. The skill says to keep Atari overlay names to 8 letters and digits. The compiler does not warn about such an overlay name; the status lists this as an open point, which is fine.
3. **Done.** The comments say that a status below 128 is success. `A_status_below_128_other_than_1_is_success` makes the test CIO return 3 on the last byte and runs `load`, `load_xex` and `read` through it.
4. **Done.** `load_xex` fails when the last address is below the first (`size` still holds the last address at that point, so `size < first` is the right test), keeping the read's success code for `status()`. The new rows in `Load_xex_fails_on_a_file_that_is_not_whole` cover last = first - 1 and a value that would wrap.

No new findings. The status's open points (spec 10.1 does not list `text_digits` and still says hex letters are screen codes; questions 138 and 139) belong to the spec session.
