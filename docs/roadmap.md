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
- **`inline asm fn`**: the assembly is copied in place of the `jsr`, no `rts` added; register parameters only (memory parameters and fixed inputs are errors); flag returns give `if f()` / `!f()` / `v = f()` with only the branch or store. An empty body reads the flag left by an `asm` block directly before the statement; nothing that changes flags is placed between them. `c64.interrupts_on()`/`interrupts_off()` become `inline asm fn`.
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

## Later (not yet ordered, except that new platforms come last)

- Floats as a library with a 4-byte format, built on `wide` (not a priority)

- Signed loop variables
- `const fn` with compile-time `int` and `real`, array-building syntax (spec 4.1)
- `using` on local variables (spec 14.6)
- Overflow checking and array index checking in the emulator for test runs (spec 14.4, 3.2)
- Doc comments (`///` before a declaration) and a Tern language server (LSP) that shows them, e.g. that `set_irq` needs an `interrupt fn`
- Memory layout declaration: reserved areas, volatile ranges, named regions (spec 14.12), with `data` at a fixed address (14.9) and banks

New platforms, last:

- 65C02 CPU variant and Commander X16 platform
- NES: iNES output, `@ bank(n)`, calls between banks (spec 14.1)
- Apple II and Atari 8-bit platforms
