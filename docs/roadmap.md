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

Decision 2026-11-07, scheduled 2026-11-08. User documentation that explains how the language behaves on the machine, with examples; it informs and does not forbid. It grows out of the quick guide in `docs/guide.md`, whose section 6 lists these behaviors briefly. Written by the spec session after M19 (which with M17 gives most of its content), extended as each later milestone lands, and complete before the first release (below), with wrapping arithmetic and unchecked indexes covered in detail. It must cover each behavior below; the spec session adds to this list whenever a decision leaves a behavior that the programmer may rely on, or that may surprise them:

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
- evaluation order: in `a[i] = v` the index is evaluated first; with a call in the index, and inside expressions, the order is unspecified (5)
- a fixed address outside the platform's volatile areas is ordinary memory: unlisted hardware needs `volatile` (4)
- a function shared by main and a handler may have loop counters only in registers; only `using X` / `using Y` guarantees one (8.1)
- `indexed match` does not check its value: outside the span it jumps through whatever follows the tables; a plain `match` skips values it does not list (5.2)

## M20: Index checks in the emulator, and signed loop variables

Spec: 14.4, 3.2, 5.1, 5.2.

- In test runs (`Run.Program`, and the M19 run tool), the built-in emulator reports a variable array index outside its array (`a[i]`, not pointer accesses), with the source line, using the compiler's debug information (the M19 source-to-instruction map). No code is added to the program. Arithmetic overflow is not checked (decision 2026-11-07).
- The check also reports an `indexed match` value outside its span (spec 5.2; built in M19)
- `for` loops with `i8` and `i16` counters, counting up and down, with constant and variable bounds and constant steps (spec 5.1, "Signed loop variables", and the spec 11 rows; decision 2026-11-08). A variable step and `using` on an `i16` counter are errors.
- **First, small** (question 75, spec 13): dedicated errors for `n++` / `n--`, a `*` dereference, `%` binary literals and a keyword used as a name, each with its fix. The skill's `errors.md` shows the new messages (`SkillTests`). Also `x += 2` / `x -= 2` on a `u8` in memory, not `volatile`, as two `inc` / `dec` (spec 11); `BenchmarkTests` and `SkillTests` are rewritten in the same commit, with the old and new totals.

Tests: the check fires on an index past the end (also through a folded offset such as `a[i - 1]`) and on an `indexed match` value outside its span, and stays quiet on a correct program, including one whose arithmetic wraps; loops at edge values (-128, 127, 0, wrap) against C#, the listing shapes of spec 11, the signed empty check with variable bounds, a non-±1 step that overflows past 127 or -128, and the errors for mixed signedness and a variable step; each new C-habit error, and that `a++b` and `a--b` still parse.

## M21: Plug-ins against a fixed interface

Spec: 12.3 (planned).

`export module` in a binary (exports matched by name to the module's fixed-address routines, with checks on parameters, return, `keeps`, the interface's fixed variables defined as `data`, `setup` at entry 0) and `export f, g @ addr` in a program with `main` (a jump table at a fixed address, `name_api.tern` written). Tests: two level binaries built against one interface module, exported in different source orders, loaded in turn into one reserved slot and called by a host; a level calling the host's table; each error. Example: a game with levels on disk plus one built separately, checked in VICE

## M22: `const fn` and code generators

Spec: 4.1 (planned). One milestone, since generators are most of the reason to build `const fn` (decision 2026-11-03); it may take the full three sessions.

- Part 1: `const fn` with compile-time `int` and `real`, the step limit, `const fn` in modules, array-building syntax for `data` tables.
- Part 2: `emit` fragments in asm blocks, `label()`, the listing naming the `emit` line and the call for each generated instruction.

Done when: (part 1) a sine table and a screen-row address table are built by `const fn`s from a module and match C#; (part 2) an unrolled screen copy that skips given rows is generated, runs correctly in the emulator and its listing shows the source of each instruction.

## M23: Doc comments and a language server

- `///` doc comments before declarations, kept by the compiler.
- A Tern language server (LSP): errors as you type (in the M19 JSON format), hover with the doc comment, type and cost (bytes, cycles where fixed), go to definition, e.g. showing that `set_irq` needs an `interrupt fn`.

## M24: Floats as a library

Decision 2026-11-08. A portable library module with a 4-byte format, built on `wide`; no language change. After M22, so `const fn` with `real` can build float constants and tables. Scope settled by a spec session before coding: the format (IEEE 754 single, or a layout closer to the 6502's needs, e.g. exponent byte plus 24-bit mantissa), the value type (a 4-byte struct), the operations (add, subtract, multiply, divide, compare, conversion to and from integers, to text), and how a `const fn` turns a `real` into the format's bytes.

Done when: the operations match C# over edge values (0, ±1, smallest and largest, rounding), their bytes and cycles are in the spec 10.1 table, and an example prints computed values on the C64.

## New platforms

In this order (decision 2026-11-07; renumbered 2026-11-08). Each starts with a spec session for the platform's memory map, output format, encoding and library; the scope below is a first outline.

### M25: Atari 8-bit

- Platform description: memory map, free zero page, volatile areas (hardware registers, OS variables), ATASCII and screen-code encodings
- `.xex` output (load segments, run address); the `memory` block's areas map to segments
- `lib/atari`: OS routines (CIO) as routines at a fixed address; register structs for ANTIC, GTIA, POKEY, PIA; display lists; VBI and DLI handlers through the OS vectors
- Library modules that are not C64-only (`math`, `mem`, `text`, `wide`, `bcd`, `random`, `cpu`, the M24 floats) work unchanged

Done when: an example with a display list and a DLI color change runs in an Atari emulator (Altirra), and the benchmarks compile for the Atari.

### M26: Apple II

- Platform description and memory map (no interrupt source by default, text and hi-res pages, soft switches as volatile areas), high-bit ASCII
- Output: a DOS 3.3 binary and/or ProDOS system file (spec session decides), with disk image creation for testing
- `lib/apple2`: monitor and DOS/ProDOS routines as routines at a fixed address; text and hi-res screen helpers (row address tables)

Done when: an example draws text and hi-res graphics in an Apple II emulator (AppleWin or MAME).

### M27: NES

- Bank switching settled first (spec 14.1): `@ bank(n)`, calls between banks through generated stubs, a first mapper (likely NROM, then UxROM or MMC1)
- iNES output; 2 KB of RAM, so frame sharing and zero page matter most; PPU and APU register structs; NMI-driven frame loop; user-defined encodings for tiles (14.5)

Done when: an example scrolls a background and moves sprites in a NES emulator (Mesen), with code in more than one bank.

### M28: 65C02 and Commander X16

- The 65C02 CPU variant (spec 12.1): new instructions in the CPU table, emulator and code generator (`stz`, `bra`, `phx`/`phy`, `inc a`, `(zp)` addressing); code shapes per variant
- X16 platform: memory map, banked RAM, VERA register structs, KERNAL routines

Done when: the benchmarks compile for the 65C02 and use the new instructions where shorter (listing tests), and an X16 example runs in its emulator.

## First release (after M28)

Decision 2026-11-08: the first release comes once all five platforms work. Done when: M28 is merged, the developer guide covers every behavior in its list, `docs/guide.md` and spec 10.1 describe the compiler and library as built, and every example builds for its platforms.

## Not scheduled

Each has a reason to wait; the spec session schedules one when that reason changes.

- **Question 63 (b)** (deferred 2026-11-07): folding `a[i + c]` proved from another access (no benchmark needs it, and it makes one line's cost depend on another, rule 4). Reconsider when a real program needs it. F20 is rejected and F14 is replaced by `indexed match` (decision 2026-11-08).
- **Second copies of functions called from two roots** (spec 14.2) and **arrays of structs as struct fields** (14.11): discussed after the first release.
- **The `nmos6502+undoc` CPU variant** (spec 12.1, opt-in undocumented opcodes).

After the compiler is complete (far off, not planned):

- A standalone assembler extracted from Tern: the asm syntax, `emit` generators, the memory block and the listing, for `.asm` files with no Tern code
