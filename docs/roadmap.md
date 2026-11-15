# Roadmap

Milestones in planned order. Each is sized for one to three coding sessions. Progress is tracked in `docs/status.md`, not here. The spec session owns this file and may reorder or split milestones.

Every milestone is done only when: all tests pass, new features have end-to-end emulator tests and error tests, generated code matches spec section 11 (or is shorter), and `docs/status.md` is updated.

## M1: Core compiler (done)

`u8`/`i8`/`u16`/`i16`/`bool`, constants, globals and locals, `@ $addr`, `@ zp`, `u8`/`u16` arrays (split), functions with memory and `using` register parameters, returns in A / A-X / flags, `if`/`while`/`for` (direction, `step`, `using X`, `_`), `break`/`continue`, operators, `.lo`/`.hi`, zero-page allocation, shared frames, built-in assembler with long-branch fixing, C64 `.prg`, CLI.

## M2: Pointers, data and strings

Spec: 2.1 (char and string literals), 3.4, 3.5 (pointer conversions), 4 (`data`), 4.1 (`include_bytes`), 8.2 (dereferenced pointers in zero page).

- `ptr T`, `addr(name)`, `p[i]` with `(zp),y`, `p + n`, pointer `as` conversions, `ptr` ↔ `u16`
- Dereferenced pointers are forced into zero page; error if it is full
- `data` declarations for read-only arrays and strings
- Char literals `'A'` and string literals in PETSCII; `screen"..."` for screen codes
- `include_bytes("file")`, path relative to the source file

Done when: a program can copy a string to the screen through a pointer, a `data` table is readable by index, and pointer arithmetic across page boundaries is tested.

## M2 follow-up (small, at the start of M3)

Spec: 2.1, 3.4. From the M2 questions settled in `docs/decisions.md`.

- `p - q` of two pointers gives a `u16`
- Prefixed char literals (`screen'A'`)
- The `petscii_mixed` and `screen_mixed` encodings; explicit `petscii` prefix
- The encoding is part of the platform description (default per target)

## M3: Assembly blocks and KERNAL calls

Spec: 6.6, 9.

- `asm { }` blocks and `asm fn` with typed signatures, parsed into the compiler's instruction objects
- Operands may name Tern variables, constants, arrays, functions; `name+1`; `.lo`/`.hi`; local `.labels`
- The compiler computes registers and variables used by each block
- Routines at a fixed address: `fn name(...) @ $addr keeps X, Y` with no body; calls are `jsr $addr`; register parameters only; calls allowed in `using X`/`using Y` loops when the routine keeps that register; fixed inputs (`true using carry`, `3 using X`); `-> u16 using XY` (and `AY`)
- An example calling KERNAL `CHROUT` ($FFD2) declared as a routine at a fixed address, inside a `for ... using X` loop

Done when: the example prints text in VICE, the call compiles to a direct `jsr $FFD2` (checked in the listing), and asm blocks mixing with compiled code are tested in the emulator.

## M4: Imports and the first standard library

Spec: 2.2, 10.1.

- `import name`, qualified names (`text.put`), module lookup in the compiler's `lib/` folder then next to the source file, each module loaded once, platform modules only on their platform
- Multi-file programs through the same mechanism
- `lib/math`, `lib/mem`, `lib/text`, and the KERNAL part of `lib/c64` (needs `asm fn` from M3)
- Library functions tested exhaustively (8-bit) or on edge values (16-bit), with cycle counts recorded

Done when: an example imports `text` and `c64`, prints a message and a number with the KERNAL and writes text to screen memory, verified in VICE; an unused import adds no bytes to the program.

## M5: Structs

Spec: 3.3.

- Struct declarations, field offsets, `@` offsets for register maps, nesting
- Struct variables, structs at fixed addresses (volatile), access through `ptr`, `sizeof`
- Arrays of structs as one array per field; `linear` arrays of whole records
- Errors: struct assignment, comparison, struct parameters, `p[i]` on struct pointers, variable index into array fields of struct arrays

- `lib/c64` register structs for the VIC-II, SID and CIA chips

Done when: a VIC-II register struct at `$D000` works in VICE, and a list of enemies updates through both indexing and pointers.

## M5 follow-up (small, at the start of M6)

Spec: 2.2 (`private`), 3.2 (`linear` number arrays), 3.4 (`addr()` of fields). From the M4/M5 questions settled in `docs/decisions.md`.

- `private` on top-level declarations, enforced in Tern code and assembly; mark the library's work variables and tables `private`
- `addr(player.name)` for fields of struct variables and fixed-address structs
- `linear` arrays of `u16`/`i16`/pointers: interleaved layout, `pts[i]` with `asl` (spec 11 shape), at most 128 elements, `addr()` gives `ptr u16`; error on one-byte elements

## M6: match, interrupts and function modifiers

Spec: 5 (`match`), 6.2, 6.3, 8.1 (interrupt roots).

- `match` with values, `,` alternatives, `..=` ranges, `_`; jump tables for dense cases, compare chains otherwise
- `interrupt fn` (saves used registers, `rti`), its own frame root; error for functions reachable from both main and an interrupt
- `inline fn`, `noinit`
- An example raster interrupt on the C64

Done when: the raster example runs in VICE and `match` code shapes are checked in tests.

## M7: Optimizer, first pass

M6 follow-up first (spec 6.3, 6.5, 8.1; settled in `docs/decisions.md`):

- `addr(f)` of functions without memory parameters; frames of address-taken functions and what they reach overlap nothing
- Functions with no memory of their own may be reached from two roots; the error names a hidden temporary when that is the reason
- `lib/c64` functions to install an interrupt handler (e.g. `set_irq`); the coding session proposes the exact set, and the raster example uses them

Then:

- Volatile memory (spec 4): platform volatile areas, the `volatile` keyword, variables shared between roots, pointer accesses; the error for a fixed-address variable overlapping the program
- Keep register parameters in registers when the function does not need the register
- Track register contents to remove redundant loads (e.g. `lda` after `sta` of the same value)
- Remove unreachable code (e.g. `rts` after an endless loop)
- Loop variables in X/Y automatically when the body allows it
- A benchmark set with byte and cycle counts; a regression fails the tests

## M8: Benchmarks against other compilers

M7 follow-up first (spec 3.2, 6.5, 8.1, 12.2; settled in `docs/decisions.md`, 2026-11-02):

- **Private variables shared with the module's own handler** (spec 8.1). In `Checker.CheckPrivateGlobalsPerRoot`, skip a private variable when one of the two roots is an `interrupt fn` declared in the variable's own module. Tests in `InterruptTests`:
  - a `timer` module with `private var ticks: u16`, an `interrupt fn tick()` that increments it, and `fn now() -> u16` that returns it; a main file that calls `timer.now()` compiles and, run with `irqEvery`, sees `ticks` grow (it is volatile, so a wait loop rereads it);
  - the existing case stays an error: `math.mul8` in main and `math.div8` in a handler declared in the main file;
  - a module handler sharing a private variable with main through a *different* module (handler in `a`, variable in `b`) stays an error.
- **A `for` loop with constant bounds that indexes past the end of an array is a compile error** (spec 3.2), e.g. `for i in 0..20 { a[i] }` on a `[u8; 10]`, when the index is the loop counter itself. The message names the array, its length and the loop's last value. Test in the array tests.
- **`c64.interrupts_on()` and `interrupts_off()` inline**: `cli` / `sei` with no `jsr`. Check the listing in `C64LibraryTests`. The comments of `set_irq` and `set_nmi` say the handler must be an `interrupt fn`.

Then:

- The same tasks in Tern, hand-written assembly, Prog8 and cc65
- A report of bytes and cycles per task

## M9: Closing the gap with assembly

Spec: 5.1 (variable step), 6.2 and 7 (`inline asm fn`), 11. From the M8 comparison questions settled in `docs/decisions.md` (2026-11-02); the gaps are in `tests/benchmarks/comparison.md`.

- **Variable `u8` step** in `for` loops that count up with a `u8` counter: evaluated once, stops when adding the step carries past 255; `..=255` gives `adc` / `bcc` with no compare (spec 11 shape, listing test). Errors: a negative or 16-bit variable step, a variable step in a loop counting down. Tests at edge values (step 1, 128, 255; start 0 and 255; end below 255).
- **`inline asm fn`**: the assembly is copied in place of the `jsr`, no `rts` added; register parameters only (memory parameters and fixed inputs are errors); flag returns give `if f()` / `!f()` / `v = f()` with only the branch or store. An empty body reads the flag left by an `asm` block directly before the statement; nothing that changes flags is placed between them. For `while f()` the same holds for the test after each pass when the loop body ends with an `asm` block (added 2026-11-02, M9 question 43; listing test); no diagnostic in any case. `c64.interrupts_on()`/`interrupts_off()` become `inline asm fn`.
- **Optimizer** (no spec change; each with an `OptimizerTests` case and its benchmark delta in the commit):
  - `x & $80 != 0` followed by `x << 1` on the same byte as one `asl` and a carry test (`crc8`);
  - a constant offset folded into the `abs,x` address: `stack[sp - 1]` as `ldx sp` / `lda stack-1,x` (`interp`);
  - X (or Y) reused across array accesses with the same index;
  - shorter `match` exits: an arm that ends the loop body branches to the loop test directly; the range check uses a short branch when the default is in range;
  - asm `cli`/`sei` (and other blocks) forget only what `AsmEffects` says they change (M8 part 1 review finding 2).
- Rewrite `tests/benchmarks/sieve.tern` with the variable step, and other benchmarks where the new features fit; update the baseline and `comparison.md`.

Done when: `sieve`'s inner loop compiles to the same instructions as `tests/benchmarks/asm/sieve.asm` (listing test); the comparison totals are below M8's 1373 bytes / 526134 cycles, with the new numbers in `comparison.md`; all tests pass.

## M10: Cross-platform standard library

Spec: 2.2 (module parameters), 4.1 (`assert`), 6.3 (`cld` in handlers, `interrupt fn` parts), 10.1 ("Planned for M10").

Language first:

- Module parameters: the `module name(P: type = default, ...)` header, `import name(P = value)` in the main file only; errors for a header name that differs from the file, a header in the main file, parameters set outside the main file, unknown or repeated names, values that do not fit
- Compile-time `assert cond, "message"` at the top level; reported at the importing line when a module parameter caused it
- `interrupt fn` called from another `interrupt fn` as a part (`rts`, no saving), with register parameters and return values; installing a part that has them is an error; calling one from non-interrupt code is an error; decision 34 extended to parts. Test: two module parts sharing private state with their module's main-side functions, combined in one handler under `irqEvery`

Then the library:

- Interrupt handlers start with `cld` unless they execute no `adc`/`sbc` and no `jsr`; listing tests both ways
- `wide`: `U24`/`U32` structs; set, copy, add, subtract (also with a `u16`), increment, compare, multiply, divide, decimal (optionally fixed width) and hex output, for both widths
- `bcd`: 1-4 byte packed decimal counters: add, subtract, compare, digits
- `math`: `muls8`, `divs8`, `abs8`, `abs16`, `min`/`max`/`clamp` for `u8` and `i8` as `inline asm fn`, `sin8`/`cos8`
- `random`: 8- and 16-bit xorshift, seed, value below `n`
- `text`: `dec8s`, `dec16s`, fixed-width decimal; `mem.copy_back`
- Spec 10.1 tables updated with the signatures as built; cycle counts in `docs/status.md`'s library table

Tests as in M4: 8-bit routines for every input, 16/24/32-bit on edge values (0, 1, 255/256 boundaries of each byte, the maximum, wrap-around) and against C#; `bcd` on every two-digit pair; `random` checked for its full period. An unused module adds no bytes.

Done when: an example keeps a 32-bit score with `wide` and a BCD score with `bcd`, shows both on screen and plays a sound or moves a sprite with `sin8` and `random`, checked in VICE; a test runs a `bcd` add in main while an IRQ handler with arithmetic fires, and both give correct results.

## M11: C64 graphics library: sprite multiplexer and scrolling

Spec: 10.1 ("Planned for M11"), 6.3 (parts).

- `lib/c64/sprites.tern` with `module sprites(COUNT: u8 = 32, FLICKER: bool = false)` and asserts on `COUNT`
- `lib/c64/scroll.tern`: fine scroll helpers, double-buffered eight-way character scrolling, color RAM fixed or shifted, filling from a character map
- Cycle budgets recorded in `docs/status.md`: `update()` for 32 sorted and unsorted sprites, the raster handler per group, a coarse scroll step

Tests: the handler's register writes per raster line in the emulator (as the raster test), sprite order after `update()` against C#, `COUNT` at 8 and 64, more than 8 sprites on a line with and without `FLICKER`; scroll buffers after each coarse step in all eight directions; both modules combined in one handler.

Done when: an example scrolls a character map in eight directions with at least 24 multiplexed sprites over it, without visible glitches in VICE, using `import sprites(COUNT = 24)` or more, with one program-owned handler calling both modules.

## M12: Memory layout and binaries without `main`

Spec: 8.4 (memory layout, `data` at a fixed address), 12.3 (programs without `main`).

- `memory { ... }` in the main file: `program`, `zeropage`, `reserved`, `region name`, `volatile` (`overlay name` parsed, "not supported yet" until M13); constants from imports in ranges; replace/add rules as in 8.4; errors for a block outside the main file, overlapping ranges, a `main` program whose first area does not start at the platform's code start, code that does not fit (listing the areas)
- `@ region` on `var` and `data`; `data x @ $addr` in a `program` or `reserved` area; the program file covers lowest to highest used address, gaps zero-filled, file size in the report
- `export f, ...` without `main`: jump table (`jmp setup`, then the exports in order), `setup` without the call to `main`, `.prg` with no BASIC line; exports from the main file with register parameters only; all entries one root
- `name_api.tern`: each entry as a routine at a fixed address with `keeps` from the code, constants for every range the binary uses

Tests: placement in each kind of area, functions never crossing an area, a zero-filled gap, every new error; a binary called entry by entry in the emulator (`setup` first), its `_api` module imported by a host that reserves the binary's memory, and the host's calls checked; `keeps` in the `_api` file matching the code.

Done when: a music player built as a binary at `$C000` (`init`, `play`) is loaded by a Tern host that calls `play` from its raster handler, checked in VICE; a game-style program places sprite data with `data ... @ $2000` in a reserved range.

## M13: Overlays

Spec: 8.5.

Follow-up first (settled 2026-11-02/03):

- A module's function called only from the module's own `interrupt fn`s counts as its interrupt side for private variables (spec 8.1, question 48); `sprites.put` can become a plain `fn`. Tests: such a helper using a private variable shared with main-side functions compiles; the same helper also called from main is still the error.
- Constant conditions folded before reachability (spec 2.2, question 49): an `if` on a constant compiles only the branch taken, and functions called only from the other branch are not compiled or allocated. Test: `sprites` without `FLICKER` has no `build_flicker` and no `shown`/`disp`; bytes before and after in the commit message.
- A region named like a constant or other main-file symbol is an error (spec 8.4, question 51), with a test.
- `{` on the line after a block header (spec 2), for every construct listed there; tests in `ErrorTests` or the parser tests for each, plus a `{` after a line break where no block is expected still being an error.

Then:

- `overlay name` areas; `import m @ area` in the main file only; one file per overlay module, loading at its area's start, holding its code, `data` and variables with initial values
- Errors: a call between overlays of the same area, the main file in an overlay, an overlay that does not fit (with its size), `@ area` outside the main file or naming a non-overlay area
- Overlays in one area share their variables' memory (RAM and zero page)
- `c64.load` through the KERNAL

Tests: two overlays in one area loaded in turn in the emulator (stand-in for the KERNAL load), each finding its variables at their initial values; resident code calling each; shared memory shown in the report; every new error.

Done when: an example loads two levels from disk into one area in turn and runs each, checked in VICE with a `.d64` image.

## M13 follow-up (small, at the start of the next coding session)

Spec: 4, 8.4, 8.5. Settled 2026-11-03, after the M13 re-review.

- A `volatile` fixed-address variable may overlap an overlay's file (M13 review finding 3 made every such overlap an error). Without `volatile` the error stays, and its message suggests `volatile` for an intended overlap or naming the overlay's variable. A write to such a variable makes the optimizer forget every variable, as a write through a pointer does. Overlapping the resident program stays an error with `volatile` too.
- Fixed `data` in an overlay module (spec 8.5, question 58): `data x @ addr` inside the module's own area goes in its file, with the module's code, data and variables laid out around it. Errors: overlap with the module's other fixed data or fixed variables; an overlay module's fixed data outside its own area; a resident module's fixed data in an overlay area. Tests: two levels in one area, each with a header at the area's start and code after it, loaded in turn and read by resident code through a `volatile` fixed variable; the file's bytes (data at its address, gap zero-filled); each error.
- Uses of a sibling overlay (spec 8.5, question 56): `addr()` of a function or `data` of another overlay in the same area is an error, as using its `data` is; resident code may still take such addresses. Error tests for each.
- Fixed zero-page addresses in an overlay (spec 8.5, question 55): `var p: ptr u8 @ $FB` works in an overlay module, is not part of its file, and can be dereferenced; the main program naming the same address shares it. The `@ zp` error message suggests a local variable or a fixed address. Tests in `OverlayTests`.
- Long byte buffers (spec 3.2, decision 2026-11-04): `var x: [u8; n]` / `[i8; n]` with `n` above 256, zeroed or `noinit`, no initial value; in the variable area, a region or at a fixed address. Indexing it is an error suggesting a pointer; `addr(x)` gives `ptr u8`, `len(x)` a `u16`. Errors: an initial value, multi-byte elements, indexing (also for long `data`). Tests: a 2048-byte buffer in a region filled and read back through a stepped pointer in the emulator, cleared at startup unless `noinit`, region full, each error.
- `const` declarations before the `memory` block (spec 8.4, decision 2026-11-04): after the imports, only `const` lines may precede the block; any other declaration before it is still the error, whose message becomes "the memory block goes after the imports and constants, before other declarations". Tests in `MemoryTests`: a range using a constant declared above the block, one declared below, and a `var` before the block (error).
- Tests in `OverlayTests`: with `volatile`, the variable reads a value the overlay loaded and a write changes the overlay's variable, which the overlay then reads (both overlays loaded in turn); without it, the error and its message; `volatile` over the resident program is still the error; an optimizer test that a resident function reading an overlay variable, writing the aliased fixed variable, then reading the overlay variable again reloads it.

## M14: C64 device I/O library

Spec: 10.1, 6.6. No language change (decision 2026-11-03).

- `lib/c64/io.tern` (working name `io`; the coding session may propose another): the KERNAL routines `SETLFS`, `SETNAM`, `OPEN`, `CLOSE`, `CHKIN`, `CHKOUT`, `CLRCHN`, `CHRIN`, `CHROUT`, `READST`, `LOAD`, `SAVE` as routines at a fixed address
- Whole files: `load(name, device, address)` and `save(name, device, first, last)`
- Sequential files: open, read and write bytes or blocks, close
- The drive's command channel (15): send a command (scratch, rename, ...), read the status message
- Errors from `READST` and the KERNAL's carry
- `c64.load` built on the same routines (same signature and behavior, spec 8.5)
- Needs the KERNAL banked in (not after `kernal_off()`); the module header says so. Fast loaders are out of scope.
- Signatures in spec 10.1 as built

Tests: KERNAL stand-ins in the emulator (`Run.Program` traps) checking arguments, call order and error results for each function.

Done when: an example saves a high-score file to disk, reads it back and shows the drive's status message, checked in VICE with a `.d64` image.

## M15: Indexing small linear struct arrays

Spec: 3.3 (planned; decisions 2026-11-03 and 2026-11-07).

- `list[i].field` with a variable index on a struct array of at most 256 bytes stored as records:
  - `linear var`: shifts; struct size 1, 2, 4 or 8, otherwise an error at the index suggesting `_` padding (to the next size) or `indexed` (with the table's size)
  - `indexed var`: a compile-time table of `i * sizeof(T)` (n bytes, only when indexed with a variable), any struct size; a word only before `var`; `indexed linear`, `indexed` on a non-struct array or on one over 256 bytes are errors
  - a constant index is a fixed address; a variable index into a linear array over 256 bytes is an error suggesting a pointer
- Padding fields `_` in structs: several allowed, may have `@`, cannot be read or written (error)

Tests: listing tests for the shift shapes (sizes 1, 2, 4, 8) and the table shape, the table's bytes in the report, every element and field against C# for both forms, `_` padding giving the expected `sizeof` and offsets, each error and its suggestion.

Done when: the tests pass and spec 3.3 describes the feature as built.

## M16: Locals of inline functions in each caller's frame

Spec: 6.2, 14.2 (decision 2026-11-02, M10 review finding 5).

- An inline function's locals and parameters go in each caller's frame, so an inline helper with locals can be called from any root
- `var` lines in an `inline asm fn` body
- Move the `math` inline helpers' work byte into a local

Tests: one inline helper with a local called from main and from a handler under `irqEvery`, both results correct; frames of two callers holding separate copies; `BenchmarkTests` (bytes and cycles in the commit message).

Done when: `math.min8` in main and in a handler compiles and runs correctly.

## M17: Analysis of the gap to hand-written assembly

Spec: 11 (code shapes); no language change in this milestone. Decision 2026-11-05.

The result is a report, `tests/benchmarks/analysis.md`, not features. The spec session reads it and schedules the fixes as later milestones.

- **llvm-mos first**: add the llvm-mos C compiler (`mos-c64-clang`, from the llvm-mos SDK) to `ComparisonTests` as a fifth column, built from the same C sources as cc65 so the two C columns compare compilers, not programs. Flags `-Os` (stated in the report, as cc65's are); the path set with an environment variable like the other tools (`MOS_CLANG`), and nothing runs without `TERN_COMPARE=1`. Bytes counted from `main` on, with the runtime routines it calls (e.g. multiply) and without its startup code; cycles from `main` to its return. If its code reveals a technique none of the others use, it goes into the analysis below like an asm difference.
- **Benchmarks**: for each of the nine tasks in `comparison.md`, compare Tern's listing with the assembly version instruction by instruction, and account for the difference in bytes and cycles. Start with the largest gaps (`bubble`, `fib16`, `enemies`, `copy`, `sum16`).
- **Library**: pick at least six asm routines in `src/lib` (from `math`, `wide`, `bcd`, `text` and `sprites`), write each in plain Tern, and compare bytes and cycles with the asm version. These are measurements and stay out of the library.
- **Classify every difference** as one of:
  1. *optimizer*: the compiler could produce the better code from the same source, with no language change;
  2. *how the Tern is written*: a different way to write the source already gives the better code. These become advice for the guide;
  3. *language*: a feature would let the source say what the assembly does, e.g. a value kept in a register across a loop or call, or a carry passed between statements. Each one is checked against the design rules;
  4. *assembly only*: a trick that should stay in asm, e.g. self-modifying code, unusual uses of flags, or a layout chosen for one task.
- For each difference: bytes and cycles, the listing lines on both sides, and an estimate of what fixing it would save on the benchmark totals.
- Ranked summary: the changes worth making, the largest saving first, with their class.

Done when the report covers every benchmark and the chosen library routines, the class totals add up to the measured gap, and the spec session has a ranked list to schedule from.

## M18: Fixes from the analysis

Spec: 3.2, 11; decisions 2026-11-07. All class 1 (optimizer) items from `tests/benchmarks/analysis.md` section 7; no language change. Three parts, each reviewed before the next starts; the `F` numbers are the report's findings, with its estimated savings (cycles / bytes over the nine benchmarks).

Working material: `analysis.md` section 2 gives each finding with Tern's instructions against the assembly's, section 3 the items per benchmark; `tests/benchmarks/profiles/<task>.txt` and `<task>.asm.txt` give every instruction with its execution count and cycles (regenerate with `TERN_COMPARE=1 dotnet test`, which needs 64tass for the assembly side). Start each fix from the benchmark where the report counts it, then generalize it with tests of its own.

**Part 1: local code shapes** (code generator and peepholes, about 90,000 / 190)

- F2: `a[i + c]` folded into the address (`lda a+c,x`) when the loop counter's bounds prove `i + c` does not wrap: constant bounds, or a variable end of the counter's type (`for i in 0..last` with `last: u8` gives `i <= 254`, so `i + 1` folds). Not from other accesses in the body (question 63 (b), deferred). Tests: the fold in a loop with constant and with variable bounds (listing); no fold for a 256-element ring buffer `buf[head + 1]` with `head = 255` (result is `buf[0]`), for `i + 2` under `0..last`, and outside a loop
- F1: an indexed or indirect operand used directly by `adc`, `sbc`, `cmp`, `and`, `ora`, `eor` (`lda b,x / adc a,x`, `lda (p),y / cmp (q),y`), 8- and 16-bit
- F4: `x > c` with a constant as `cmp #c+1 / bcc skip` (and `x <= c` likewise), keeping x in A; edge values `c = 0`, `254`, `255` (`x > 255` is never true)
- F6: `w += b as u16` as `lda b / clc / adc w / sta w / bcc + / inc w+1` (spec 11), and `-=` likewise
- F8: a 16-bit shift by a constant with one byte in A, without a memory copy
- F10: the small shapes, led by `p += 256` / `n -= 256` as `inc p+1` / `dec n+1` (spec 11); also no `clc` when the carry is known clear, negation as `lda #0 / sec / sbc`, no `cmp #0` after a load, no stores of 0 into memory the startup cleared and main has not written, a field bound used directly, Y known 0 after a `0..=255` loop, zeros stored together, `a[i] += 1` as `inc a,x`, `iny` for `ldy #c` after `ldy #c-1`
- F19: a commutative operand in A used as is (`eor crc` instead of `lda crc / eor b`)
- F21: a `bool using carry` result returned as the compare's own carry, without branches; also `>` and `<=` through `clc / sbc` where that is shorter

**Part 2: dataflow in the optimizer** (about 44,000 / 15)

- F3: no store to a local or temporary that is never read from memory afterwards
- F18: a local copy of an array element that is not written in between is replaced by the element (`cmp values,x`), and loaded into a free register only on the path that needs it after the element is overwritten
- F16: a `using A` (and `using AX`) parameter kept in its register while the body does not need it
- F17: A known on both paths after `bcc / inc hi` (`inc` does not change A)
- F5: after `sta a,x` the optimizer knows A holds the element

**Part 3: registers across loops, and layout** (about 12,000 / 50)

- F7, F11, F15: a global used by one root, or a local, kept in a free register across a loop with no call that reads it, stored back before the loop exits or a call; also across a `match` jump table (`interp`'s `sp` in Y); then the `jmp` over an `else` that became empty goes
- F13: an outer loop counter in a free register when the inner loop takes X
- F9: loop entry `jmp` and empty-range test dropped when the first test is known true; `dec / bne` where the decrement already set Z
- F12: an indexed array placed so its indexed range does not cross a page when there is room (RAM, no bytes in the file); the report shows the placement

Deferred: F20 (moving an independent shift before a bit test, 50 cycles) and F14 (no `match` range check when the value's range is known, 350 cycles; wants a value-range analysis once more cases need it). Question 63 (b), the fold proved from another access, is deferred too. Since 2026-11-08: F20 is rejected and F14 is replaced by `indexed match` (M19).

For every fix: correctness tests on programs other than the benchmarks, at edge values (0, 255, 256, wrap-around), compared with C#; a listing test of the new shape; the benchmark delta in the commit message (`BenchmarkTests` baseline rewritten in the same commit); in `analysis.md`, the measured saving next to the report's estimate.

Done when: the three parts are merged, `comparison.md` has the new totals, and the "how the Tern is written" advice the fixes made unnecessary (`bubble.minus` with F2) is dropped from the report. The guide advice is written by the spec session after M18, from what is still true.

## M19: Tools and skills for coding agents

Decisions 2026-11-05, 2026-11-07 and 2026-11-08; after M18, so the skill does not teach workarounds the optimizer has since removed. Tern is new, so a model has seen no Tern code; what it needs must come from a skill and from tools that answer quickly and exactly. Two parts, each reviewed before the next starts.

**Before part 2 (spec session):** the "writing fast Tern" advice in `docs/guide.md`, from what is still true after M18 (`tests/benchmarks/analysis.md` section 8: `copy`'s records of 4, `enemies`' element stored before its tests, `interp`'s `running` flag). The skill takes its advice from there. Written 2026-11-08: guide section 7.

**Part 1: compiler, tools and two small features**

- **`-> bool using overflow`** (spec 6.1, decision 2026-11-07): the V flag as a return of `inline asm fn`, `asm fn` and routines at a fixed address. Tests: `if f()`, `!f()`, `while f()` and `v = f()` on a flag set by `bit` (bit 6) and by a signed `adc` overflow; the empty-body form after an `asm` block; the error on a plain `fn` and as a fixed input.
- **`cpu` library module** (spec 10.1, decision 2026-11-07): `carry()`, `zero()`, `negative()`, `overflow()` as empty-body `inline asm fn`s. Tests: each function, plain and with `!`, in `if` and `while` after an `asm` block, against the flag the block set; the listing shows only the branch.
- **`indexed match`** (spec 5.2, 11; decisions 2026-11-08): always a jump table with no range check; a plain `match` becomes always a compare chain, and the programs that relied on the automatic table (`interp`, the raster example, others found by the tests) say `indexed match`. `BenchmarkTests` (bytes and cycles in the commit message; `interp` should lose its range check, about 350 / 5). Tests: `u8` and `i8` (negative lowest value), with and without `_`, gaps in the span, ranges, a value masked into the span, the listing shape and the tables in the report; a plain `match` of 8 or more compares stays a chain (listing)
- **From questions 65-67** (decision 2026-11-08): the two-roots error for a loop counter names the loop and suggests `using X` or `using Y` (spec 8.1), with a test; the `MatchTests` program that expects a read of `var reg: u8 @ $C000` to stay declares it `volatile` (spec 4).
- **Agent-friendly compiler output**: diagnostics as JSON (`tern --json`: file, line, column, message, suggestion); a listing that links each source line to its instructions, bytes and cycles.
- **A measuring tool**: compile a snippet or a function, run it in the built-in emulator, and report bytes, cycles and chosen memory, so an agent can compare two ways of writing the same code. It should be a `tern` subcommand, such as `tern bench`, not a separate script.
- **A run tool**: run a program in the emulator for n cycles or until `main` returns, and dump memory ranges or the screen as text, so an agent can check behavior without VICE.
- Tests: each tool's output format.

**Part 2: the Tern skill** (a Claude Code skill in this repository, usable as a plugin elsewhere)

- The language in brief, with the restrictions an agent would not guess (no recursion, arrays of at most 256, one-byte indexes, `using` registers, roots and private variables, flags as values and `cpu`); the cost of each construct; the "writing fast Tern" advice from the guide; common errors and their fixes; when to use an `asm` block; how to use the part 1 tools.
- Tests: the skill's examples compile and give the stated costs, so the skill cannot drift from the compiler.

Done when an agent with only the skill and tools can write, check and measure a small program (e.g. a sprite moving under joystick control), and the skill's examples are checked by the test suite.

## Developer guide (spec session, from M19 on)

Decision 2026-11-07, scheduled 2026-11-08. User documentation that explains how the language behaves on the machine, with examples; it informs and does not forbid. It is chapter 10 of the guide, `docs/guide/10-behavior.md` (decision 2026-11-14: the guide in chapters), each behavior with an example. Written by the spec session after M19 (which with M17 gives most of its content), extended as each later milestone lands, and complete before the first release (below), with wrapping arithmetic and unchecked indexes covered in detail. It must cover each behavior below; the spec session adds to this list whenever a decision leaves a behavior that the programmer may rely on, or that may surprise them:

- all arithmetic wraps, including index expressions: `buf[head + 1]` with `head = 255` is `buf[0]` (3.1, 3.2); when the optimizer folds a constant offset into the address
- an index outside the array is not checked, as in C, and the optimizer assumes it does not happen; reaching other memory on purpose uses a pointer; the emulator reports a bad index in test runs (3.2, 14.4)
- flags after an `asm` block: `if f()`, `while f()` (first test, and each later test when the body ends with an `asm` block) and `v = f()` with an empty-body flag function; after compiled Tern code the flags are unspecified (7)
- a variable `for` step of 0 never ends the loop; stepped loops stop when the step carries past the end of the type (5.1)
- signed `>>` rounds down, not toward zero (3.1)
- `keeps` on a fixed routine is a promise the compiler does not check (6.6); a function reached only through `addr()` has no known root (6.5, 8.1)
- a handler that leaves through an asm `jmp` gets `cld` only if its own code has `adc`, `sbc` or `jsr`; code it jumps to runs in decimal mode if main was in it, unless the asm block clears it (6.3)
- a call into an overlay that is not loaded runs whatever is in the area (8.5)
- an interrupt handler installed from an overlay must be removed before that overlay is replaced (8.5)
- an overlay's zero-page bytes at a fixed address hold whatever was there before it loaded; the overlay sets them (8.5)
- a `volatile` fixed-address variable over an overlay reads whatever overlay is loaded; addresses inside an overlay's file move when the overlay changes (8.5)
- a binary's entries are one root: the host must not call one from an interrupt while another runs (12.3)
- a plug-in's memory is checked only against its own `memory` block, not against the game or other slots; gaps in an interface's jump table are zeros (`brk`) (12.3)
- a game's exported functions are an unknown root: globals they share are always read from memory, and the two-roots check cannot see a plug-in calling them from an interrupt (12.3, 6.5)
- evaluation order: calls and `volatile` reads in one statement happen left to right, the index of `a[f()] = g()` first; a variable that a call in the same statement changes may be read before or after the call (5)
- a fixed address outside the platform's volatile areas is ordinary memory: unlisted hardware needs `volatile` (4)
- `@ align(n)` can leave up to `n - 1` bytes of padding, shown in the report; in `data` or an overlay the padding is in the file (4)
- a function shared by main and a handler may have loop counters only in registers; only `using X` / `using Y` guarantees one (8.1)
- `indexed match` does not check its value: outside the span it jumps through whatever follows the tables; a plain `match` skips values it does not list (5.2)
- on negative constants `/` rounds toward zero and `%` takes the left operand's sign, while `>>` rounds down (3.1)
- when `main` returns, the program stops in an endless loop and interrupt handlers keep running; there is no return to BASIC (12.2)
- fields left out of a struct literal are 0 (3.3)
- which ROM bank is selected is not checked: a call into a bank area runs whatever bank is selected (8.5, 14.1)

## M20: Index checks in the emulator, and signed loop variables

Spec: 14.4, 3.2, 5.1, 5.2.

- In test runs (`Run.Program`, and the M19 run tool), the built-in emulator reports a variable array index outside its array (`a[i]`, not pointer accesses), with the source line, using the compiler's debug information (the M19 source-to-instruction map). No code is added to the program. Arithmetic overflow is not checked (decision 2026-11-07).
- The check also reports an `indexed match` value outside its span (spec 5.2; built in M19)
- `for` loops with `i8` and `i16` counters, counting up and down, with constant and variable bounds and constant steps (spec 5.1, "Signed loop variables", and the spec 11 rows; decision 2026-11-08). A variable step and `using` on an `i16` counter are errors.
- **First, small** (question 75, spec 13): dedicated errors for `n++` / `n--`, a `*` dereference, `%` binary literals and a keyword used as a name, each with its fix. The skill's `errors.md` shows the new messages (`SkillTests`). Also `x += 2` / `x -= 2` on a `u8` in memory, not `volatile`, as two `inc` / `dec` (spec 11); `BenchmarkTests` and `SkillTests` are rewritten in the same commit, with the old and new totals.

Tests: the check fires on an index past the end (also through a folded offset such as `a[i - 1]`) and on an `indexed match` value outside its span, and stays quiet on a correct program, including one whose arithmetic wraps; loops at edge values (-128, 127, 0, wrap) against C#, the listing shapes of spec 11, the signed empty check with variable bounds, a non-±1 step that overflows past 127 or -128, and the errors for mixed signedness and a variable step; each new C-habit error, and that `a++b` and `a--b` still parse.

## M20 follow-up (before M21)

Spec: 3.1, 3.4, 4, 10.1, 11. Decisions 2026-11-08: "M20 provisional choices", "addresses of variables and data in initial values" and "Galaxy Stars port, first gaps". Small items from M20 and from the Galaxy Stars port (`C:\game-dev\galaxy-star\tern\gaps.md`); done before M21 so the port can continue without workarounds.

- **Tools** (from M20): `tern run --set $DC00=$FF` (and `tern bench`) sets a memory byte before the run, so a whole game loop can be checked with a joystick value; `tern bench` prints index errors (`RunResult.IndexErrors`) and fails a variant that has them.
- **`v += 2` / `v -= 2` on a variable held in X or Y** is `inx` / `inx` (`dex`, `iny`, `dey`), the spec 11 row.
- **`addr()` in initial values** (spec 4): of globals, `data`, struct-global fields and functions, alone, plus or minus a constant, or as `.lo` / `.hi`. Tested with split and `linear` 16-bit tables, one-byte `.lo` / `.hi` tables, fields, an overlay's data from resident code, and each remaining error.
- **Constant expressions computed exactly** (spec 3.1, 3.4). Tested at the edges: `320 - BOX` in a `u16`, `400 / 50` in a `u8`, a constant sum as a pointer argument, `~$0F` on `u8` and `u16`, a constant that no longer fits, typed constants with runtime values unchanged. A type error in a runtime expression names the operand that set the type.
- **A typed constant next to a wider value** (question 79, decision 2026-11-09; spec 3.1): the operation's type is chosen first, then the constant part is computed at that width, in an expression and in a compound assignment alike. Tested: `w & ~M`, `w &= ~M` and `w = ~M` with `const M: u8` all give `$FFF0` as the mask; `w + BOX * 30` adds 390; `x + BOX * 30` is an error; `x op= e` gives the same result as `x = x op e` for each operator with a typed constant.
- **An untyped constant computed from typed ones is a plain number** (question 81, decision 2026-11-09; spec 3.1). Tested: with `const BOX: u8 = 13` and `const AREA = BOX * 30`, `w + AREA` adds 390, `var v = AREA` is a `u16`, `x + AREA` is an error that names `x`; `const D = BOX * 2` next to an `i8` is allowed, as `26` is.
- **`.lo` / `.hi` on `addr(...)` in code** (question 83, decision 2026-11-09; spec 3.4), as already in initial values: `lo = addr(map).lo` loads `#<map`. Tested in code and tables, including a struct with a field named `lo`, where `addr(s).lo` is still the address byte.
- **`.lo` / `.hi` on `addr(...) ± constant` in code** (question 84, decision 2026-11-09; spec 3.4): `h = (addr(map) + 299).hi` is `lda #>(map+299)`, as in a table. A pointer variable's `p.hi` stays an error. The error for a runtime number before a pointer (question 85) names the operands as written (`Write 'p + n'`), not `addr() + n`.
- **A pointer converts to `u16` where a `u16` is expected** (decision 2026-11-09; spec 3.5, 4): assignment, initial value and table element, argument, return value, with no code. Tested: each of those, `[u16; n]` tables of `addr()` with offsets, and the errors that remain: `w + p`, `p == w` (a pointer in an operator) and a `u16` variable into a pointer.
- **`[T; _]`** (spec 4), with its errors.
- **`c64.sprite_xy` and `c64.set_kernal_irq`** (spec 10.1), checked in VICE with the KERNAL in and an `io` call. The skill's interrupt notes are updated (`cld`, the KERNAL).

`BenchmarkTests` and `SkillTests` are rewritten in the same commit as any change that moves them, with the old and new totals.

Done when: every item above has its end-to-end and error tests, and the Galaxy Stars workarounds for gaps 1, 3, 4, 5, 7, 8, 9 and 10 compile in their plain form (checked by porting each into a test program).

## M20 follow-up 2 (before M21)

Spec: 2.1, 3, 3.1, 3.3, 4, 4.1, 8.5, 11, 12.2. Decisions 2026-11-09: "struct literals in initial values" and "open points from the consistency review". Small; the Galaxy Stars port would use struct literals for its parallel tables (`pages`, `title`, `score`) before it reaches them.

- **Struct literals in initial values** (spec 3.3): `Name { field = value, ... }` as the initial value of a global struct, the elements of a global array of structs, and `data` (also at a fixed address, also in an overlay module, 8.5). Fields not listed are 0. Values as for any initial value of the field's type, including `addr()` forms filled in at placement, lists and strings for array fields, and nested literals. Every array layout (tables, `linear`, `indexed`). Errors: an unknown field, a field given twice, a `_` field, a literal of another struct type, a value that does not fit, a struct literal in code or as a local's initial value (suggesting field assignments). Tests: each layout against C# (every byte of the tables), nested structs, 16-bit and pointer fields with `addr()`, array fields from a list and a string, `[T; _]`, fixed-address `data` in an overlay module, each error. The 8.5 example compiles.
- **Constant arithmetic in signed 64 bits** (spec 3.1, 4.1): a result outside the range at any step is an error (today it wraps silently: `4000000000 * 4000000000 * 4 / 1000000000 / 1000000000` gives 8, not 64); a constant shift count 0 to 63 (today 0 to 31). Tests at the edges, and the rounding of `/`, `%` and `>>` on negative constants as stated in 3.1.
- **`const` inside a function** (spec 4), scoped to its block, under the scope rules of spec 4. Tests: use, scope, a name clash, a local `const` as a `for` bound and an array length.
- **The string-literal error gets a fix sentence** (spec 2.1): "Declare it as data, e.g. 'data HI = "HI"', and pass addr(HI), len(HI)", with the suggestion in `tern --json`.
- **16-bit shifts by 8 to 15 move a byte first** (spec 11 rows `w = v << 8`, `w = v >> 8`): today eight `asl`/`rol` pairs (32 bytes, 80 cycles for `<< 8`). Unsigned and signed, in place (`w <<= 8`) and into another variable; results against C# for every count 8 to 15 at edge values. `BenchmarkTests` and `SkillTests` rewritten if they move, with the totals.
- The new spec 11 rows for compares, `&&`, `||`, a compare stored in a `bool` and `i8 >> 1` describe the code as built; a listing test for each.
- **After the review, before the merge** (questions 86 and 87, decisions 2026-11-09): number literals up to 2^63 - 1 (spec 2.1), with an accumulation that cannot overflow, and "Use a value up to $7FFFFFFFFFFFFFFF (9223372036854775807)" in the error; `ConstantRangeTests` writes `MAX` and `MIN` as literals. Tests: the largest decimal and hex literals, one above each, `-9223372036854775807 - 1`. Line breaks between struct literal fields stay as built (spec 3.3); a test that a newline between list elements is still the missing-`,` error.

Done when: every item has its end-to-end and error tests, and one of the port's parallel-table groups (`pages.p8`'s `how_row` / `how_col` / `how_text`) compiles as an array of struct literals and gives the same tables as the parallel arrays.

## M20 follow-up 3 (before M21)

Spec: 2, 2.2, 4, 5, 9, 10.1, 11, 13. Decisions 2026-11-09: "Galaxy Stars port, remaining gaps". The open gaps from the finished port (`C:\game-dev\galaxy-star\tern\gaps.md`, gaps 11 to 24).

- **Calls and `volatile` reads in one statement run left to right** (spec 5): operands, arguments, a call in an index. Tests: a function that logs its calls, in `|`, `+`, `-`, a compare, nested arguments, `a[f()] = g()`, `a[i] op= f()`; two `volatile` reads; the port's `(rand_below(K) + 1) << 2 | rand_below(4)` gives Prog8's order. `BenchmarkTests` should not move (no benchmark has two calls in one statement); say so if one does.
- **Constants resolved on demand across modules** (spec 2.2, 4): `len()` / `sizeof()` of a global or `data` in a `const`, in the same module and another; a constant used across an import ring of three modules; the error for a constant that depends on itself (directly and through another module), naming the chain.
- **`var` lines in a plain `asm fn`** (spec 9): in the frame (shared with a function that never runs at the same time; checked in the report), local names (the same name in two functions), a pointer used as `(p),y` placed in zero page; the errors as for `inline asm fn` (initial value, `@`, after an instruction).
- **`[u8; _]` from a string** (spec 4), with an encoding prefix and `\0`; a written length checked against the string.
- **`step` a keyword only after a `for` range** (spec 13): `fn step()`, a local and a field named `step`, `for i in 0..step step 2`. The keyword-name error's example moves to another keyword (`SkillTests`, `errors.md`).
- **A line starting with a binary operator** (spec 2): the error "Move '&&' to the end of the line before"; a test that a line ending with `&&` continues, and that `-1 =>` in a `match` is still an arm.
- **Spec 11 rows:** `p = q + (col << 1)` as an 8-bit add with a carry into the high byte; `s < 0` / `s >= 0` on `i8` and `i16`, and `(d as i8) < 0`, as `bpl` / `bmi`. Listing tests; results against C# at edge values. `BenchmarkTests` and `SkillTests` rewritten if they move, with the totals.
- **Importing the main file** is an error that says the main file cannot be imported and to move the shared code to a module (spec 2.2).
- **`c64.cia_irqs_off()`** (spec 10.1), checked in VICE with `set_kernal_irq` and a raster interrupt.
- The skill: the evaluation-order rule and its one exception (a variable a call changes) in `language.md` near the operators; `var` lines in `asm fn`; line continuation.

Done when: every item has its end-to-end and error tests, and the port's workarounds for gaps 11, 12, 15, 17, 18, 19, 23 (the error only) and 24 compile in their plain form (ported into a test program, as `GalaxyStarsGapTests`).

## M21: Plug-ins against a fixed interface

Spec: 12.3 (planned); 2.2 for the first item.

First, a small item (decision 2026-11-09, "the build's target decides the library"): `import name` that finds both a library module and `name.tern` in the importing file's folder is an error naming both files, saying to rename the program's own. Tests: a program `io.tern` next to `lib/c64/io.tern`, a program `random.tern` next to `lib/random.tern`, a main file named `random.tern` importing `random` (still allowed), the suggestion in `tern --json`.

`export module` in a binary (exports matched by name to the module's fixed-address routines, with checks on parameters, return, `keeps`, the interface's fixed variables defined as `data`, `setup` at entry 0) and `export f, g @ addr` in a program with `main` (a jump table at a fixed address, `name_api.tern` written). Tests: two level binaries built against one interface module, exported in different source orders, loaded in turn into one reserved slot and called by a host; a level calling the host's table; each error. Example: a game with levels on disk plus one built separately, checked in VICE

## M22: `const fn` and code generators

Spec: 4.1 (planned). One milestone, since generators are most of the reason to build `const fn` (decision 2026-11-03); it may take the full three sessions.

First, a small item (question 92, decision 2026-11-10): `level_api.header` in a binary that defines `data header` at the same address is the error "'level_api.header' is this binary's data 'header'. Use 'header'" (spec 12.3), in place of the general overlap error. Tests: the error, its suggestion in `tern --json`, and an unrelated overlap that keeps the general error.

- Part 1: `const fn` with compile-time `int`, `real` (literals, math functions, the four roundings), `bool`, arrays and their return into `[T; _]` tables, reading constants and `data`, the step limit, `const fn` in modules, array-building syntax (decision 2026-11-10). Tests include each error of spec 4.1 and the tables against C# `Math`.
- Part 2: `emit` fragments in asm blocks, `label()`, the listing naming the `emit` line and the call for each generated instruction.
- Implementation (suggested; the coding session decides): a tree-walking interpreter, not code compiled to .NET, since errors, the step limit and `emit` need its state (position, call chain, locals, emitted lines). In `Tern.Compiler`, in its own folder and namespace (`CompileTime/`), called by the checker to evaluate initializers and run generators; a separate project would need the AST and symbols moved out first. The spec session adds the folder to the layout in `CLAUDE.md` once it exists.

Done when: (part 1) a sine table and a screen-row address table are built by `const fn`s from a module and match C#; (part 2) an unrolled screen copy that skips given rows is generated, runs correctly in the emulator and its listing shows the source of each instruction.

## M22 follow-up (before M23)

Spec: 4, 4.1, 5.1, 10, 11. Decisions 2026-11-11 (M22 questions 94-103). On a branch from `main` after M22 is merged.

- **Real constants** (spec 4.1): `const SCALE = 1.5`, used in a `const fn`, a `for` list and another constant; in runtime code the error a `real` literal gets; `const T = f()` with an array result is "'T' is an array, which a const cannot hold. Use data".
- **`offset(T.field)`** (spec 10): an untyped constant like `sizeof`; nested fields; in a constant, a `const fn`, an `emit` operand and a plain asm operand (`ldy #offset(Enemy.hp)` / `lda (p),y` run in the emulator); errors for a type that is not a struct and a field it does not have.
- **`repeat(value, count)` replaces `[value; count]`** (spec 4.1): in `data`, a `var` initial value and a `const fn` (0 elements there); the runtime error; `[7; 4]` is "Write repeat(7, 4)".
- **`for` direction from the step alone** (spec 5.1, 11): without `step` a loop counts up; the errors for a constant range that counts up from a larger start (`9..=0`) and for an unsigned counter ending at constant 0 without `step`; a signed `n..=0` counting up from a negative `n`. The spec 11 rows with `step -1` keep their code (listing tests). Update the library, examples, tests and the skill that count down without `step`; `BenchmarkTests` should not move. The guide's loop examples are updated by the spec session after the merge.
- **Addresses as labels in generators** (spec 4.1): `addr()` of a global, `data` or function as a `label` argument or in a `const fn`, used in `emit` operands alone, with an `int` offset and as `.lo` / `.hi`; a `.label` of the calling asm block as an argument (`clear_rows(0, 3, .done)`), jumped to from the generated code. Errors: placing either kind with `name:`, a `.label` the block does not have, reading or comparing a label's number. Run in the emulator: a generator copying into a buffer the compiler places, and one that jumps to the caller's label.
- **`tern --json` gives the call's location** for an error in a `const fn` or in generated code, as a field of its own (spec 4.1).

## M22 follow-up 2: examples (before M23)

Spec: 4.1, 5.1, 11. On a branch from `main` after the M22 follow-up is merged.

- `examples/sine.tern`: a sine table built by a `const fn` with `real` math (`[... for i in 0..256]`) moves a sprite or raster bar smoothly. It shows that the table costs nothing at run time: only the bytes are in the .prg.
- `examples/speedcode.tern`: a generator unrolls a per-frame loop (e.g. a colour-RAM cycle or a row copy that skips given rows), with a plain-loop version beside it. `tern bench` compares the two (bytes and cycles) and the numbers go in a comment at the top. It uses `label()`, an `addr()` label argument and the calling block's `.label`.
- Both compile in the examples test, run in the emulator with memory checked, and are checked in VICE (screenshot).

Two small changes from questions 104 and 106 (decisions 2026-11-11), in their own commits:

- **`n..=0` without a negative step is allowed** (spec 5.1): an unsigned `n..=0` and `a..=0 step n` compile and run (once when the start is 0), with no warning. `9..=0`, `255..=0 step n` and an unsigned `n..0` keep the error; with a variable step its fix is "Use a constant negative step to count down". Tests in `LoopDirectionTests`, in the emulator for `n` = 0 and 5.
- **`for i in n..0 step -1` (n: `u8`) matches spec 11:** `ldx n` / `beq done`, without the dead `cpx #$00` / `bcc` (`CodeGen.CounterBranch`). A listing test checks the full shape (review suggestion 3 of the M22 follow-up). Rewrite the benchmark baseline if it moves.

Done when: both examples run in VICE and their listings show each generated instruction's `emit` line; the two loop changes are built. The spec session then writes the guide's section on `const fn` and generators from these two examples.

## Cleanup: split the large classes (before M23)

No language change. On a branch from `main` while no other branch is open, since moving code makes merges with open branches hard. Before M23, which adds a lot of code.

The largest classes are hard for a human reviewer to follow in one file. Split each into partial class files named `Class.Area.cs`, as `Checker.Asm.cs` already is, along the section markers already in the code. The coding session chooses the exact cuts; the aim is no file over about 1,000 lines. A starting point (lines as of 2026-11-11):

- `Backend/CodeGen.cs` (3,462): statements and assignment; 8-bit and 16-bit expressions; conditions; pointers, calls and assembly; `for` loops. The class's fields, emission helpers, functions and tree queries stay in `CodeGen.cs`.
- `Semantics/Checker.cs` (2,771): declarations; function bodies; expressions; type rules and the call graph.
- `Backend/Optimizer.cs` (1,233): dead stores; register contents and bit test.
- `Syntax/Parser.cs` (1,181): declarations; statements and assembly; expressions.

Rules: move code only. No renames, no changes to logic, comments or formatting, so `git diff --color-moved` shows only moved lines. Use one commit per class. The test count, `BenchmarkTests` and `SkillTests` don't change, and every example compiles to the same bytes.

Done when: no source file in `src/` is over about 1,000 lines (a file may stay a little over if a section would otherwise be cut in half), all tests pass unchanged, and a review confirms the diff is moves only.

## M23: Several errors per compile

Spec: 13 ("Several errors per compile"). Decision 2026-11-12. No language change. Today the compiler stops at its first error.

- **Parser:** after a syntax error, go on at the next statement at the same brace depth, or at the next top-level declaration. The coding session decides how lines inside `( )` and `[ ]` are skipped.
- **Checker:** at most one error per statement or declaration, then go on. A name whose declaration failed (unknown type, bad initializer, bad signature) is declared with an error type that accepts every use, so it causes no follow-on errors.
- Allocation, code generation and layout run only when the checks found no error; their errors stay one at a time.
- No limit on the number of errors by default; `--max-errors n` stops after `n`, with "more errors were not reported". Text output ends with the count ("3 errors"); `tern --json` keeps its format, with several entries in `diagnostics`. `tern run` and `tern bench` report the same list.
- **Errors without a fix sentence:** a test runs every error test's message through `Diagnostic.Split` and lists those with no suggestion (e.g. "300 does not fit in u8 (0 to 255)"). Each gets a fix sentence, or is listed in the test as having none that helps, with the reason.
- The skill says that `tern --json` lists several errors, so an agent can fix them in one pass.

Tests: a program with errors in several statements, functions and modules reports each once; a misspelled type, a misspelled function name in its declaration and a bad initializer each give one error, with no follow-on errors at their uses; a syntax error followed by a type error reports both; 25 errors report all 25, and with `--max-errors 5` report 5 and the note; an allocation error is still reported alone. Every existing error test still gets its error first.

Done when: the tests above pass, every error test's first error is unchanged, and the errors without a fix sentence are fixed or listed.

## M23 follow-up (before warnings)

Spec: 13. Decision 2026-11-12 (M23 questions 107-111). On a branch from `main`.

- **Errors sorted by place** (question 108): the main file first, then each module in the order it is first imported; within a file by line and column; errors at the same place keep the order found. `--max-errors n` keeps the first `n` after sorting, with the note. Text, `tern --json`, `tern run` and `tern bench` give the same order. Tests whose first error changes are updated to the new first error; the error each test is about must still be reported.
- **An undeclared name is reported at every use** (question 107): `update()` called three times with only `fn updte()` declared gives three errors, each with "Check the spelling, or declare it"; the same for an undeclared variable, type or module member. A name whose declaration failed (`var e: Enmy`) still gives one error, at its declaration.

- **A function may be called from main and from a handler** (decision 2026-11-12, spec 8.1): the error goes. Its frame (temporaries included) shares memory with no frame of either root. The `InterruptTests` error cases become tests that the program compiles and that the shared function's frame overlaps no frame of main or the handler; a run in the emulator shows main's call getting the handler's values when the interrupt lands inside it. The skill's two-roots error examples (`errors.md`) and rules (`SKILL.md`, `language.md`) are updated; the private-variable error stays.

Tests: in `SeveralErrorsTests`, the order for errors found out of source order (a global's type after a function's parameter, lexer and parser errors of one file, two modules), `--max-errors 2` on that program, and the three calls above (three errors, at their lines).

Done when: the tests pass and the skill's error examples match the new order.

## Warnings (after the M23 follow-up, before `implements`)

Spec: 13 ("Warnings"), 5.1, 8.1. Decision 2026-11-12 ("warnings for code that does nothing"). No language change; front end and tools.

- **The four warnings** of spec 13: an unused local (a `for` variable too, with "Write '_' as the loop variable"), an unused `private` declaration of a module or top-level declaration of the main file, an unused import (not one that sets module parameters), an unreachable statement (one per block). Uses are counted in Tern code and assembly operands, in any function, reachable or not. Names starting with `_`, `main`, exports, fixed-address globals of the main file, parameters, fields, module parameters, `pub` declarations and library modules are never warned.
- Each message says what is unused or unreached, then a fix sentence starting with a verb from `Diagnostic.FixVerbs`.
- Reported only when the checks found no error; sorted by place with later errors. Text `warning:` and the summary "1 error, 2 warnings"; `tern --json` with `"severity": "warning"`, `ok` true with only warnings. `--max-errors` counts errors only.
- `--no-warnings`, `--warnings-as-errors` (each warning reported and counted as an error, the build stops), both together an error. `tern run` reports warnings; `tern bench` does not.
- **The report lists functions with memory reachable from two roots** (spec 8.1), with their roots.
- **Clean the repository:** examples, `src/lib` (with warnings turned on for it in a test, though users never see them) and the skill's examples compile with no warnings; a warning that shows deliberate code gets a `_` name or a change, not a switch.
- The skill says what each warning means and that `_` keeps a name; the guide (spec session) mentions them.

Tests (`WarningTests`): each warning and each case that is not warned (above, plus `0..COUNT` with `COUNT` 0, `if DEBUG { return }` followed by code, a `break` of an inner loop under `while true`, an `asm` block before the next statement); a name used only in assembly; a variable only written; a function used only by an unused function (one warning, then one for the callee after removing it); no warnings with a check error, warnings with an allocation error; the order with errors; `--json` severity and `ok`; both switches and the error for both; `tern run` and `tern bench` output; the report's two-roots list.

Done when: the tests pass, and the examples, library and skill compile with no warnings.

## `for _` follow-up (after warnings, before `implements`)

Spec: 5.1 (`_` as the loop variable), 11. Decision 2026-11-12 (question 118). No language change; code generator only.

- A `for _` loop is never slower than the same loop with a named counter the body does not read. Today the repeat path (`ldx #10` … `dex` / `bne`) does not hold a variable in the free index register across the loop, as the named-counter path does: `for _ in 9..=0 step -1 { n += 1 }` takes 107 cycles, `for i in ...` 83 (`ldy n` … `iny` … `sty n`). The repeat path holds a variable under the same conditions as the named path, or uses the named path's code where that is faster.
- The spec 11 row `for _ in 0..10` (`ldx #10` … `dex` / `bne loop`) stays the shape when nothing is held.

Tests: for each `for _` form (constant count, variable count, counting up and down, a count of 0 and 256 where the range allows), the loop and its named twin give the same memory, and the `_` loop takes no more cycles; a listing test for the example above; the skill's `flow.tern` back to at most its 802 cycles from before its counters became `_`. Benchmark and skill outputs rewritten in the same commit if they move.

Done when: the tests pass.

## Overlay interfaces: `implements` (after the `for _` follow-up, before M24)

Spec: 8.5 ("Interfaces: `implements`"). Decision 2026-11-12. Small; reuses the checks of `export` naming a module (M21).

- `import m @ area implements i` (and `implements a, b`): a jump table at each routine's address, the module's code around it; the checks of 12.3; the call graph counts a call to an interface routine as a call to each implementation.
- The errors listed in 8.5, each with a test.
- An example: two levels as overlays implementing one interface, loaded in turn and called through it (`tern run`, and VICE).
- The skill and guide mention `implements` next to overlays.
- Questions 119-122 (decision 2026-11-12): one change to build on the branch before the review: inside an implementing module, the interface's `volatile` variable that matches the module's `data` is an error ("'level_api.header' is this module's data 'header'. Use 'header'"), as in a binary (12.3), with a test.

Tests: calls through the interface reach the loaded overlay (load each in turn in the emulator); frames of the implementations are shared with other code; each check and error; a plain call next to an interface call.

Done when: the tests and the example pass.

## Encodings declared by the program (before M24)

Spec: 2.1 ("Encodings declared by the program"). Decision 2026-11-12. Small; front end only.

- `encoding name { ... }` with single characters and ranges, `encoding name: base { ... }`, `private encoding` and `module.name` prefixes, `default encoding name` in the main file (program files only, not library modules).
- Errors: a character listed twice, a range past `$FF`, an unknown base, a second `default encoding` line, `default encoding` outside the main file, a character the encoding lacks (suggesting `\$NN`), `\n` not mapped.
- An example: a C64 program with its own character set and an encoding for it.
- From the `implements` review (question 123, decision 2026-11-12): an interface module placed in an overlay or bank area is an error at the name after `implements` (spec 8.5), with a test.

Tests: bytes of strings and chars in a declared encoding (ranges, a base with changes, a non-ASCII character), a qualified prefix, the default in the main file and a program module but not in a library module, each error.

Done when: the tests and the example pass.

## M24: Floats as a library

Spec: 10.1 (`float`), 3.3 (an array field's value from a `const fn`). Decisions 2026-11-08 and 2026-11-13.

- The `float` module: the 4-byte format with the exponent in its own byte, rounding to nearest, `overflow`, and every function in the 10.1 table, `float.of` included.
- Language: a struct literal's array field takes an array returned by a `const fn` (3.3), with an error test for a wrong length.
- A C# model of the format (pack, unpack, each operation, rounding), used by the tests as the reference.

Tests: each operation matches the C# model bit for bit over edge values: 0, ±1, the smallest and largest values, ties in rounding, overflow (the result and the flag), division by zero, results that become 0; conversions at 0, -1, 32767, -32768, 65535 and out of range; `round_` on halves; `floor` and `trunc` on negative values with and without a fraction and on values of 2^23 or more; `dec` with 0 and several decimals; `float.of` against the model.

Done when: the tests pass, the bytes and cycles of each routine are in the spec 10.1 table, and an example prints computed values on the C64 (`tern run` and VICE).

## M24 follow-up (small, before M25)

Spec: 4.1 (`assert` inside a `const fn`, a module function hiding a built-in), 10.1. Decision 2026-11-13 (questions 126-130).

- `assert` as a statement inside a `const fn`, reported at its line with "(called from ...)"; in runtime code the error "An assert is checked at compile time. Write it at the top level or in a const fn".
- `float.of` uses it for a value too large, in place of the index out of range; the skill's note about the old error is removed.

Tests: an assert that holds, one that fails (message, line, called-from, `calledFrom` in `tern --json`), a condition on a parameter, one inside a const fn called by another, the runtime-code error, `float.of` of a value too large.

Done when: the tests pass and `float.of`'s error names the value.

## New platforms

In this order (decision 2026-11-07; renumbered 2026-11-08). The Commander X16 and the Commodore 128 come after the first release and the language server (decision 2026-11-13). Each starts with a spec session for the platform's memory map, output format, encoding and library; the scope below is a first outline.

### M25: Atari 8-bit

- `--target` (spec 12.2, decision 2026-11-12): required on `tern`, `tern run` and `tern bench` once the Atari exists; scripts, tests' command lines, the skill and the guide name `--target c64`
- One `atari` target for every model with 48 KB or more, any OS version (spec 12.2, decision 2026-11-13); the library uses only the documented OS entry points and never writes PORTB
- Platform description (spec 12.2, settled 2026-11-13): code at `$2000`, variables up to `$BC1F`, zero page `$80–$FF`, the volatile areas, `atascii` and `atari_screen`; startup as on the C64, an endless loop after `main`
- `.xex` output: one load segment per used area, then the run address
- `tern run`: stand-ins for CIO screen output
- `lib/atari`: OS routines as routines at a fixed address; register structs for ANTIC, GTIA, POKEY, PIA; display-list helpers; `set_dli`, `set_vbi`, `set_vbi_immediate`, `exit`; the Atari's `io` and screen text modules where they make sense
- Altirra checks in portable mode, with a command line that runs without a window and saves a screenshot (written into `CLAUDE.md` through a status question)
- Library modules that are not C64-only (`math`, `mem`, `text`, `wide`, `bcd`, `random`, `cpu`, the M24 floats) work unchanged

Done when: an example with a display list and a DLI color change runs in an Atari emulator (Altirra), on an 800 with 48 KB and on an 800XL, both with AltirraOS (decision 2026-11-13; the real OS-B and XL OS once their ROM images are installed), and the benchmarks compile for the Atari.

### M25 follow-up: Atari `io` and hex letters (before M26)

Spec: 10.1, 12.2. Decision 2026-11-13 (questions 131, 132).

- `lib/atari/io`: files through CIO on an IOCB the module picks (1-7). `open(name, n, mode)` (4 read, 8 write), `close`, `read(dst, n)` and `write(src, n)` (get and put bytes), `status`, and whole-file `load` and `save`. Names are ATASCII with their device (`"D1:SCORES.DAT"`). Errors as in the C64's `io`: the functions return true on failure, and `status` gives CIO's code.
- A loader for overlay files, which are built as `.xex` without a run address.
- `tern run`: a stand-in for CIO's file calls that reads the build's own overlay files, as the C64's LOAD stand-in does (spec 13).
- `text.hex8`/`hex16` write `A`-`F` in ATASCII on the Atari (`$41`-`$46`), through a platform folder's own version of a module (spec 2.2). No language change, and no copy of the whole `text` module (e.g. the digit table in a small module that `lib/atari/` replaces).
- At the Altirra check, load the `.lbl` label file in Altirra's debugger, and fix the format if Altirra rejects it.

Done when: emulator tests cover `io` and the hex letters on both targets, and an example that saves a file and loads it back runs in Altirra on an 800 with 48 KB (AltirraOS), through the `H:` device or a disk image.

### M26: Apple II

Spec: 2.1 (`apple2`), 10.1 (`text_digits`), 12.2, 13. Decision 2026-11-13 (Apple II platform).

- First, label formats as writers named by the platform description (decision 2026-11-13, question 138), not chosen by output format; then the Apple II's, in AppleWin's symbol format
- `--target apple2`: ProDOS 8 system file (`.system`, loaded at `$2000`), one block whose areas the startup code copies into place when the `memory` block puts them elsewhere; code from `$2000`, variables up to `$BEFF`, zero page `$00–$1F` and `$50–$FF`, volatile `$C000–$CFFF`
- Startup: 40-column text page 1 with the ROM's hooks, reset vector to a close-and-quit routine, endless loop after `main`; `apple2.exit()` quits through the MLI
- The `apple2` encoding (high-bit ASCII, `\n` = `$8D`); `lib/apple2/text_digits` with `apple2` digits, sign, point and hex letters
- `tern run`: stand-ins for COUT, HOME and QUIT, a 40×24 screen at `$0400` in the Apple II's row order
- `lib/apple2`: ROM and MLI entry points; screen text (`put`, `print`, `cursor`, inverse); hi-res (row table, clear, plot, page select); keyboard, speaker, soft switches; `exit`
- `tests/tools/apple2-disk.ps1` puts the `.system` file on a ProDOS 2.4.3 boot image with CiderPress II's `cp2` (`C:\Program Files\CiderPress\cp2.exe`); a window-capture script for AppleWin, like `altirra-shot.ps1`. AppleWin and the ProDOS 2.4.3 image are installed (`CLAUDE.md` gives the paths and command lines); the test disk holds `PRODOS` and the program as its first system file, so it boots straight into the program
- Check in AppleWin whether `$00–$1F` is free, and the size limit of the system file loader (ProDOS 2.4.3's Bitsy Bye)
- Library modules that are not platform-specific (`math`, `mem`, `text`, `wide`, `bcd`, `random`, `cpu`, the floats) work unchanged

Done when: an example draws text and hi-res graphics in AppleWin, on an enhanced IIe and on a II+ with 64 KB, with its code placed above the hi-res pages through the `memory` block, and the benchmarks compile for `apple2`.

### M26 follow-up: Apple II `io` (before M27)

Spec: 4 (`@ align(n)`), 8.5, 10.1 (`io` on the Apple II), 12.2. Decision 2026-11-13 (the Apple II's `io`).

- First, **`@ align(n)`** (spec 4): a global `var` or `data` at a multiple of `n` (a power of two, 2 to 32768), in RAM, a `program` area or an overlay's file; padding in the report, which other variables may fill. Errors: `n` not a power of two in range, on a local, with `zp` or a region, an aligned declaration that does not fit. Tests: alignments 2, 64, 256 and 2048 of `var`, long buffers and `data`, also in an overlay (its file's bytes), the report's padding, each error.
- `lib/apple2/io` through the ProDOS MLI (spec 10.1): `open` (`READ`, `WRITE` creating a BIN file or emptying an existing one), `close`, `read`, `write`, `status` (the MLI's code), whole-file `load` and `save` (aux type `src`), and `load_bin`, the overlay loader (GET_FILE_INFO, BIN with a non-zero aux type). One file at a time, with a 1 KB buffer `@ align(256)`; names copied with bit 7 cleared and a length byte, cut at 64. The system bit map is left alone.
- The startup code sets the prefix from `$0280` when ProDOS started the program with an empty one. Check in AppleWin whether the prefix is set at boot and from Bitsy Bye, and that lowercase names work; raise a question if the MLI rejects them.
- `tern run`: a stand-in for the MLI's file calls (OPEN, READ, GET_FILE_INFO, CLOSE) that reads the build's own overlay files by name in capitals (`LEVEL1.BIN`); a write stops the run, as on the Atari.
- Overlay and binary files: headerless `.bin` (question 141), BIN type `$06` with the load address as aux type on the disk. `apple2-disk.ps1` adds them with their type and aux type; the coding session decides how the script gets the address from the build.
- Check the startup's 80-column switch-off in AppleWin with the firmware on (`PR#3`, then start the program; question 142).

Done when: emulator tests cover `io` against an MLI and disk modelled in C# (as `AtariIoTests`) and the `@ align` cases above, and an example that saves a file, loads it back and loads an overlay with `load_bin` runs in AppleWin on an enhanced IIe and a II+ with a language card.

### M27: NES

- First (decision 2026-11-13, question 150): the Apple II startup's prefix code only when the program imports `io`, its bytes counted under `io` in the report. Tests: an Apple II program without `io` has no GET_PREFIX call in its listing; one with `io` has it, and the report shows it under `io`.
- Bank switching (spec 8.4, 8.5, 14.1; direction settled 2026-11-12, syntax 2026-11-14): the `cartridge` line, `bank` areas, `import m @ area[n]`, plain calls, `implements`, one library module per board; boards `nrom` and `uxrom` (spec 12.2, decision 2026-11-14)
- `--target nes` (spec 12.2, decisions 2026-11-14): boards `nrom` and `uxrom`, `chr` areas, `@ area + offset`, vectors as `data`, placed `data` always kept, the memory map and startup, iNES 1.0 output; 2 KB of RAM, so frame sharing and zero page matter most
- `lib/nes` and `uxrom` (`lib/nes/uxrom.tern`) as in spec 12.2: register structs, sprite buffer and DMA, the `nes.vblank()` part and `wait_frame()`, PPU memory writes, controllers; no default encoding, so examples declare one (`encoding`, `default encoding`, spec 2.1)
- `tern run`: PPU memory and OAM modelled (not pixels), vblank and NMI each frame, scripted controllers, UxROM banks
- Questions 157-169 (decision 2026-11-14): three changes from the build, before merging. An error for two modules in one bank implementing the same interface (spec 8.5); an error for an interrupt vector into a bank area (12.2); a `program` line on the NES a plain error, not "not supported yet" (12.2). Each error gets a test.

Done when: an example scrolls a background and moves sprites in a NES emulator (Mesen), with code in more than one bank.

## Before the first release: fixes and cleanup

Decisions 2026-11-15: the code-quality review (`docs/reviews/code-quality.md`, with one file per area) was done so the release ships without its known bugs and with a well-built codebase. Its 93 findings and five spec questions are split into six milestones, each with its own branch, review and merge, so a session reads only the review files of its area. Order: the tests that find bugs first; then the bugs, each with a test that fails before its fix; then the construction cleanup, which those tests guard. The cleanup changes no behavior: no benchmark moves and no `tern run` output changes, except where a cleanup milestone below says so.

### R1: Tests that find bugs where features meet

Review theme 7 (`code-quality-tests.md`), the parts that find bugs.

- The emulator in end-to-end tests starts with random registers and memory (a fixed seed, so a failure repeats).
- A log of bus accesses checks that volatile variables, 16-bit ones included, are read and written exactly once (spec 4), replacing the count of listing lines.
- Optimized runs compared against unoptimized ones for the same program.
- Operands varied by kind and place (constant, variable, zero page, field, indexed element, pointer target, call result), not only by value, for the operators and statements.
- One shared listing reader in the tests instead of about 30 copies.

The new tests that fail are marked (skipped with the finding's number) and listed in `docs/status.md`; R2 and R3 fix them. Done when: the random start and the bus log are on for the whole suite, and every failure they show is marked and listed.

### R2: Fixes in the backend and the checker

`code-quality-backend.md`, `code-quality-semantics.md`, and the failures R1 found in these areas.

- Ranked bugs 1-6, 9, 12 and 13, and the other correctness findings of the two files.
- `@ region` from a module (spec 8.4) and the loop counter typed by the values it takes (5.1), decision 2026-11-15.

Done when: each fixed bug has a test that failed before its fix, R1's marked tests in these areas pass, and the benchmark baseline is rewritten in the commit of any fix that moves it.

### R3: Fixes in the lexer, the driver, the tools and the CLI

`code-quality-syntax.md`, `code-quality-tools.md`, and the failures R1 found in these areas.

- Ranked bugs 7, 8, 10, 11, 14 and 15, and the other correctness findings of the two files.
- ASCII-only names (spec 2), and in `tern run` the NES vblank flag, NMI and `$2006` latch and `brk` with a program's IRQ handler (12.2), decision 2026-11-15.

Done when: as R2, and no marked test is left.

### R4: One description per fact, and the CPU variant

Review themes 1 and 2.

- One table of instruction effects (memory, registers, flags, control flow) for the optimizer, the checker's assembly analysis and the assembler; the CPU variant (spec 12.1) as a build setting passed to the backend, the checker and the emulator, with the variant's instructions in the table instead of hard-coded lists of branches, stores and transfers. The table is where M29's 65C02 rows go.
- One syntax-tree visitor used by every walk, so a new node is never skipped silently.
- One registry of `tern run` stand-ins per machine.
- One CLI option table that the parsers and the help text are made from.

Done when: no list of instruction effects, tree walk over every node kind, stand-in or CLI option exists twice, and the benchmarks and `SkillTests` are unchanged.

### R5: Structure, and readiness for the language server

Review themes 4 and 6.

- Long methods split by job: `TernCompiler.Build` (about 450 lines, six jobs), `ParseProgram`, the checker's `Run`; CodeGen's per-function state in one object instead of fields over four files; parser and checker modes set and reset by hand replaced with scoped helpers.
- For M28: end positions on tokens and nodes, comments kept by the lexer, a check-only entry that returns diagnostics without building, and deep nesting reported as an error instead of a stack overflow.

Done when: no method is longer than about 100 lines without a reason noted in `docs/status.md`, the check-only entry has tests, and the benchmarks and `SkillTests` are unchanged.

### R6: Platforms as data, messages as data

Review themes 3 and 5, and the rest of theme 7.

- Each platform fact in one place, in the platform description: no tests on platform names ("has boards" for the NES, "cartridge" for NES output), and an unknown platform an error instead of the C64's stand-ins. C64 cartridges, the X16 and the C128 then add a description, not branches.
- No control flow decided by matching error text, no exception changed after it is thrown, no meaning read from strings in the assembler.
- Tests check error positions and whole messages, not fragments; the feature suite runs on every platform.

Done when: adding a platform needs no change outside its description and its library, the feature suite passes on all four platforms, and the benchmarks and `SkillTests` are unchanged.

## First release (after R6)

Decision 2026-11-08, changed 2026-11-13: the first release comes once the four platforms C64, Atari, Apple II and NES work; the Commander X16 and the Commodore 128 come after it. Done when: M27 and R1-R6 are merged, the developer guide covers every behavior in its list, the guide (`docs/guide/`) and spec 10.1 describe the compiler and library as built, and every example builds for its platforms.

## After the first release

### M28: Doc comments and a language server

Decision 2026-11-12 (was M23); the first milestone after the first release, before the X16 and the C128 (decision 2026-11-13). Starts with a spec session that settles the design; its spec section is not written yet.

- `///` doc comments before declarations, kept by the compiler.
- A Tern language server (LSP): errors as you type (in the M19 JSON format, several per compile since M23), hover with the doc comment, type and cost (bytes, cycles where fixed), go to definition, e.g. showing that `set_irq` needs an `interrupt fn`.

A draft from the spec session of 2026-11-12, to start from: `///` only right before a declaration or struct field (elsewhere, or with a blank line between, an error); a `///` block at the top of a file followed by a blank line documents the module; Markdown text, no `@param` tags; the library's comments become `///`. The server is `tern lsp` (stdio, no new NuGet package) with a VS Code extension in `editors/vscode/` (colouring and a client); a module is compiled through a main file that imports it, or checked alone; hover shows signature, address, bytes and fixed cycles for functions, type and address for variables, values for constants, offsets for struct fields, from the last version that compiled when the text does not; go to definition for names, `module.name`, `import`, fields, asm `.label`s and `addr()` arguments.

### 65C02: analysis (spec session, before M29)

Decision 2026-11-12. Before the X16 platform and its library, a spec session checks how far the compiler should go with the 65C02, beyond the instructions listed in M29. The result may split M29 into a CPU milestone and a platform milestone. Notes from the spec session of 2026-11-12, a starting point, not decisions:

- **Which variants.** The 65C02 family differs: the base CMOS set (`bra`, `stz`, `phx`/`phy`/`plx`/`ply`, `trb`/`tsb`, `inc a`/`dec a`, `(zp)`, `jmp (abs,x)`, `bit #`/`bit zp,x`/`bit abs,x`) is on every part, the Apple IIc and enhanced IIe included; `rmb`/`smb`/`bbr`/`bbs` are on Rockwell and WDC parts only, `wai`/`stp` on WDC parts. The 65C816, which the X16 can take, has the base set and `wai`/`stp` but not `rmb`/`smb`/`bbr`/`bbs`. To settle: one variant or two (e.g. `65c02` and `wdc65c02`), and which one the X16 uses by default.
- **CPU layer** (small): the table, encoder and emulator; new modes `(zp)`, `(abs,x)` and `zp,relative` (`bbr0 $10, label`); timing differences (decimal mode +1 cycle with valid N and Z, `jmp (abs)` without the page bug, shifts on `abs,x`, unused opcodes as `nop`s of fixed size). Klaus Dormann's 65C02 extended-opcodes test checks it.
- **Inline assembly:** a 65C02 instruction in an `nmos6502` build is an error naming the variant; the spelling of the bit instructions (`rmb3 $10` or `rmb 3, $10`); `AsmEffects` for the new instructions. NMOS read-modify-write instructions write twice and 65C02 ones do not (the C64's `inc $d019` acknowledge), which matters for library asm moved between variants.
- **Code shapes** (spec 11, a column per variant; design rule 4): `stz` for stores of 0; `bra` for `jmp` within range; `lda (p)` for `p[0]` and pointer fields at offset 0, which also frees Y (relaxes `using Y` limits); `phx`/`phy` in interrupt handlers; `inc a` for `+ 1` in A when the carry is not needed; `tsb`/`trb` for `|=` and `&= ~` on memory; `jmp (table,x)` for `indexed match` and calls through pointers; `smb`/`rmb`/`bbr`/`bbs` for `bool`s in zero page (only where the variant has them). The optimizer's register tracking must know each one.
- **Testing:** making the CPU variant a build setting separate from the platform (spec 12) would let the whole end-to-end suite and the benchmarks run as 65C02 code on the C64 description before the X16 platform exists; benchmark baselines then per variant.
- **Library:** whether a module can have variant-specific bodies (e.g. `mem.fill` with `stz`), and how, without a general conditional compilation feature.
- **Out of scope:** the 65C816's native mode (16-bit registers would change the language); an exploration after the first release (Not scheduled).

### M29: 65C02 and Commander X16

- The 65C02 CPU variant (spec 12.1): new instructions in the CPU table, emulator and code generator (`stz`, `bra`, `phx`/`phy`, `inc a`, `(zp)` addressing); code shapes per variant
- X16 platform: memory map, banked RAM, VERA register structs, KERNAL routines

Done when: the benchmarks compile for the 65C02 and use the new instructions where shorter (listing tests), and an X16 example runs in its emulator.

### M30: Commodore 128 (native mode)

Decision 2026-11-10. C64 programs already run on a C128 in C64 mode; this target is for native mode. Starts with a spec session that checks the machine's details against the C128 reference.


- Platform description: the `.prg` with a BASIC 7 `SYS` line at `$1C01`, the free zero page (smaller than the C64's), volatile areas (the C64's plus the MMU and the VDC)
- The library: most of `lib/c64` applies (VIC, SID, CIA, `sprites`, `scroll`, much of `io`). To settle: a C128 build searching `lib/c128/`, then `lib/c64/`, then `lib/` (recommended), shared chips in their own folder, or copies
- `lib/c128`: MMU, 2 MHz mode, `SETBNK` before loading, the 80-column VDC through `$D600`/`$D601`, the KERNAL's own raster interrupts for split screens
- RAM bank 1 (128 KB) uses bank switching from the NES milestone (spec 14.1); it may be a follow-up

Done when: the benchmarks and a sprite example build for the C128 and run in VICE's `x128`.

## Not scheduled

Each has a reason to wait; the spec session schedules one when that reason changes.

- **Question 63 (b)** (deferred 2026-11-07): folding `a[i + c]` proved from another access (no benchmark needs it, and it makes one line's cost depend on another, rule 4). Reconsider when a real program needs it. F20 is rejected and F14 is replaced by `indexed match` (decision 2026-11-08).
- **Arrays of structs as struct fields** (spec 3.3, decision 2026-11-12): designed; built after the first release or when a program needs it. (Second copies of functions called from two roots, 14.2, are not planned: decision 2026-11-12.)
- **`pha`/`pla` temporaries** (spec 14.3, decision 2026-11-12): first count, in the benchmarks and examples, the temporaries outside zero page that are saved from A and restored into A with no `jsr` or `asm` block between; build it with the optimizer work after M21 if there are enough.
- **The `nmos6502+undoc` CPU variant** (spec 12.1, opt-in undocumented opcodes).
- **65C816 native mode** (decision 2026-11-12): an exploration, after the first release is complete. 16-bit A, X and Y, a 24-bit address space and a movable direct page and stack would change types, code shapes and the memory model, so it starts with a spec session on what the language would need. Emulation mode needs nothing new: it runs the base 65C02 set (see the 65C02 analysis).
- **Calls through pointers** (spec 6.5, planned): `var handler: fn() targets [idle, walk] = idle`, then `handler()`, a `jsr` to a `jmp (handler)` stub; the `targets` list keeps the call graph, and so frame sharing, correct. The Tern way to write what an asm `jmp (v)` does today. Wanted by the developer (2026-11-09), not urgent; scope and syntax are checked in a spec session before it is scheduled.
- **A generator that also returns a value** (decision 2026-11-11, question 100): such as the cycles or bytes of the code it emitted, with a built-in that measures them, so a caller can pad cycle-exact raster code to the end of a line. Scheduled when such a generator is written; nothing breaks, since it only removes a restriction.

- **The Galaxy Stars routines kept in assembly, as a measurement set** (decision 2026-11-09, gaps 16, 21, 25): `stars.draw_tile`, `explode.hits` with `hostiles.explosion_hit`, `hostiles.draw` and `title.sweep_all`, in plain Tern and as the asm port, measured and classified as in M17 (optimizer, code shape, or needs assembly). Candidates from the port: a carry known clear after an `adc` that cannot overflow, repeated reloads, the absolute value of a 16-bit difference. After M21; scheduled by a spec session.
- **Quoted import paths and aliases** (decision 2026-11-09; spec 2.2, planned): `import "levels/level1.tern"` (relative to the importing file, `/` only) for subfolders, and `import my_io = "./io.tern"` to use a module under another name. Paths when a program wants subfolders; an alias when renaming a file cannot solve a clash. The points to settle first are in the decision.
- **Constant parts of a pointer or 16-bit sum folded together** (decision 2026-11-09, gap 26; spec 11 row `p = q + e + K`): `p = rows[r] + (c << 1) + (COLOR_RAM - SCREEN)` as one 8-bit add with `adc #$18` in the high byte (about 18 bytes and 26 cycles, today 31 and 44; 37 and 54 with the constant first). The port writes it as two plain statements (21 bytes, 30 cycles), so nothing waits on it. Built with the optimizer work after M21, with the measurement set above.
- **C64 cartridge output** (decision 2026-11-13): `.crt` images with modules in banks (spec 14.1), for EasyFlash, Ocean and Magic Desk cartridges (8 or 16 KB windows at `$8000`/`$A000`, a bank register at `$DE00`), with a platform function that selects the bank. Scheduled after M27, reusing the bank syntax the NES spec session settles.
- **MMC1 for the NES** (decision 2026-11-14): after the first release, or when a game needs it. Same syntax as UxROM (its usual mode fixes the last bank at `$C000`); adds CHR banks, mirroring set at run time (so the `cartridge` line takes no mirroring option), save RAM at `$6000`, and a five-write bank switch that an NMI can break, which `nes/mmc1` must document or guard.
- **Atari banks and cartridges** (decision 2026-11-13): a spec session after M27 settles both, reusing the NES session's bank syntax. Banks: the 130XE's four 16 KB banks at `$4000–$7FFF`, selected through PORTB, with separate CPU and ANTIC access (bits 4 and 5), and perhaps the larger third-party expansions that extend the scheme. Cartridges: 8 and 16 KB images with no banks, and bank-switched ones such as the XEGS's (an 8 KB window at `$8000` with a fixed last bank at `$A000`); the image format (`.car` with its header, or raw `.rom`) is settled then. Scheduled with C64 cartridge output.
- **An `reu` library module** (decision 2026-11-13): block copies between C64 RAM and a RAM Expansion Unit by DMA (`reu.fetch`, `reu.stash`), and the REU as a place overlays are loaded from (8.5). Not bank switching: the 6502 cannot run code or read data in the REU directly. GeoRAM (a 256-byte window at `$DE00`) is considered with it. Scheduled with or after cartridge output.

After the compiler is complete (far off, not planned):

- A standalone assembler extracted from Tern: the asm syntax, `emit` generators, the memory block and the listing, for `.asm` files with no Tern code
