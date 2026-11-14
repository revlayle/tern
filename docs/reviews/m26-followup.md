# Review: M26 follow-up, the Apple II's `io` and `@ align(n)`

Branch `m26-apple2-io` (`14dc261`, `f7460eb`, `26f8a5c`, `88daab8`, `5aa05e5`, `0ec5c96`, `2e70325`, `63e1012`), from `main` at `8425bd0`. Roadmap "M26 follow-up"; spec 4, 8.5, 10.1, 12.2; decision 2026-11-13 (the Apple II's `io`, and `@ align(n)`).

## Verdict

**Not ready to merge.** Two must-fix findings in `@ align`. The first wastes up to `n` bytes of RAM, and the second crashes the compiler. A third is a roadmap error case with no test and no specific message. The `io` library, the prefix code and `tern run`'s stand-ins are correct as far as this review checked.

`dotnet test` (in `../6502-language-dev`, on `63e1012`): 3618 compiler tests and 36 CPU tests pass. The status reports no benchmark change (1254 / 350780) and passing `SkillTests`. The AppleWin checks were not rerun by the reviewer.

## Checks

- **MLI parameter lists** in `lib/apple2/io.tern` against the ProDOS 8 Technical Reference: CREATE (7: path, access, type, aux, storage, date, time), OPEN (3), READ/WRITE (4), CLOSE (1), SET_EOF/GET_EOF (2, 3-byte EOF), GET_FILE_INFO (10, 18 bytes) and SET_FILE_INFO (7, the same list with storage and blocks as its 3-byte null field). All match.
- **`io` logic**: `close` with no file open makes no call (CLOSE 0 would close every file); a failed OPEN leaves `close_list.ref` at 0, so `close_after_error` makes no call; `read` sets `ST_EOF` on a short read that succeeded; `load` of an empty file reads `$4C`, then closes with status 0; `save` with a count of 0 writes nothing and still sets the type and aux type; `load_bin` of a 0-byte file succeeds. The short-circuit in `save` (`open(...) || write(...)`) skips the write after a failed open.
- **Prefix code** (`TernCompiler.Apple2.cs`, `Prefix`): scans `$0280` from its last character down to the last `/` (bit 7 cleared), stores that index as the length at `$0200` and copies the characters down to index 1; a partial path or an empty one leaves the prefix alone. GET_PREFIX writes at most 65 bytes at `$0200`, so it never overwrites the path at `$0280` before it is read.
- **Allocator**: offsets of aligned variables count from a block start at a multiple of the largest alignment. `StartResidue` is taken from `globals`, a copy of the offsets made before `lead` is subtracted, so it is the block's start in the modulus (as `PageOffset` was). Clearing starts at `ZeroedStart`, after the aligned `noinit` globals. Small zeroed globals fill the gaps before aligned zeroed ones, and those gaps are inside the cleared range.
- Source files stay under 1,000 lines (`TernCompiler.cs` 718).

## Findings

1. **Must fix: aligned `data` or an aligned initialized `var` moves the variables' block to a multiple of its alignment.** `src/Tern.Compiler/Backend/Allocator.cs:224` takes the block's modulus from every global in `ram`, which includes `plan.ImageGlobals` (initialized variables and `data`). Those are placed in the file with their own padding (`ImagePadding`), not in the block. The block therefore starts at the next multiple of their alignment, with no variable in it that needs that alignment. On the C64:

   ```
   data font: [u8; 8] @ align(2048) = [1, 2, 3, 4, 5, 6, 7, 8]
   var x: u8
   var buf: [u8; 200]
   var out: u8 @ $C000
   fn main() {
       x = font[out]
       buf[x] = 3
       out = buf[out]
   }
   ```

   ```
   Code and data: $0801-$1007 (2055 bytes)
   Variables after the program: 200 bytes, up to $18C7, from $1800, ...
   Aligned font: $1000 (@ align(2048)), 1989 bytes of padding before it
   ```

   Without the `@ align`, `buf` is at `$0900`, where it does not cross a page. With it, `buf` should be at `$1008`, right after `font` (`$1008-$10CF` is within one page), but it is at `$1800`, so 2,040 bytes after `font` stay unused. Take the modulus only from the variables in the block (`plan.ZeroedGlobals` and `plan.NoinitGlobals`). Add a test with aligned `data` and a page array that checks where the block starts.

2. **Must fix: the compiler crashes when the block cannot start at that alignment and holds no globals.** Finding 1 makes this reachable. `src/Tern.Compiler/TernCompiler.cs:290` takes `MaxBy` over the zeroed and `noinit` globals and uses the result with `!`. When the block's modulus comes only from `data`, or the block holds only frames, the list is empty or has no aligned variable. On the C64:

   ```
   data font: [u8; 8] @ align(32768) = [1, 2, 3, 4, 5, 6, 7, 8]
   var out: u8 @ $C000
   fn main() {
       var y: u8 = font[out]
       out = y
   }
   ```

   This gives `Unhandled exception. System.NullReferenceException ... at TernCompiler.Build ... TernCompiler.cs:line 291`. The program is valid: `font` fits at `$8000`, and `y` fits after it. The fix for finding 1 removes this case. With that fix, a block that holds an aligned variable always has one for `widest`. Add the program above as a test that compiles.

3. **Must fix: `@ align` with `zp` or a region has no specific error and no test.** The roadmap lists "with `zp` or a region" among the errors, and its tests line says "each error". `var x: u8 @ zp align(2)` gives "Expected end of statement, found 'align'. Put each statement on its own line, or separate them with ';'", and `@ buffers align(2)` gives the same. In `ParsePlacement` (`src/Tern.Compiler/Syntax/Parser.Declarations.cs:470`), check for `align` after `zp` or an address or region. Report that `@` takes one location (spec 4), with a fix sentence such as "Remove 'align(n)', or the other location". Add a test of each case to `AlignTests`.

4. **Suggestion: `@ align` on a struct field gives "'align' is not a function".** `struct S { a: u8, b: u8 @ align(2) }` reads `align(2)` as an offset expression. A field's `@` is an offset (spec 3.3), so a message such as "A struct field's '@' is an offset from the start of the struct; it cannot be aligned. Write the offset, as in '@ 2'" would say what is wrong.

5. **Suggestion: `tern run`'s OPEN stand-in throws when 8 files are open.** `src/Tern.Compiler/Tools/Apple2Machine.cs:170` uses `Enumerable.Range(1, 8).First(...)`, which throws `InvalidOperationException` on a ninth OPEN without a CLOSE (a program that calls `apple2.mli` itself, or loops over `io.open` without closing). ProDOS returns `$42` (too many files open). Return that instead.

## Done when

- [x] `@ align(n)` (spec 4): alignments 2, 64, 256 and 2048 of `var` (`A_variable_after_the_program_is_at_a_multiple_of_its_alignment`), long buffers (`A_long_noinit_buffer_...`), `data` (`Data_is_at_a_multiple_...`), in an overlay's file (`Aligned_data_and_variables_of_an_overlay_...`), the report's padding (`The_report_counts_the_padding_...`). The block placement is wrong for aligned `data` (finding 1).
- [ ] Each error tested: power of two in range, not constant, on a local, a dereferenced pointer, no parentheses, does not fit (variables and data). **With `zp` or a region: no specific error and no test (finding 3).**
- [x] `lib/apple2/io` through the MLI: `open`, `close`, `read`, `write`, `status`, `load`, `save` (aux type `src`), `load_bin` (GET_FILE_INFO, BIN with a non-zero aux type); one file, 1 KB buffer `@ align(256)`; names with bit 7 cleared and a length byte, cut at 64; system bit map left alone. Tested against `FakeMli` (`Apple2IoTests`).
- [x] Startup sets the prefix from `$0280` when it is empty; four tests. The coding session reports the AppleWin checks at boot and from Bitsy Bye, and of lowercase names (question 150).
- [x] `tern run`: GET_FILE_INFO, OPEN, GET_EOF, READ, CLOSE on the build's overlay files, by name in capitals; a call that writes stops the run.
- [x] Overlays as headerless `.bin`, BIN type with the load address as aux type; `apple2-disk.ps1` takes `file.bin@$addr`.
- [x] 80-column switch-off checked in AppleWin with `PR#3` (coding session; the screen does not show which path turned it off, as the status says).
- [x] Emulator tests of `io` against an MLI and disk modelled in C#. The coding session reports that the files example runs in AppleWin on an enhanced IIe and on a II+ with a language card.
