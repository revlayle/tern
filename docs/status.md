# Status

Owned by the coding session. Updated at the end of every coding session.

## Current

- **Milestone:** M4 (imports and the first standard library), including the M3 follow-up (fixed inputs). Complete; ready for review.
- **Branch:** `m4-imports` (from `main` after the M3 merge).
- **Next step:** re-review of `m4-imports` against `docs/reviews/m4.md` (both must-fix findings and suggestions 3 and 4 addressed), then merge. After the merge, M5 (structs). The spec session should look at the questions below; the provisional choices are easy to change.

## Done

- **M1 core compiler**, merged to `main` (2026-11-01).
- **M2 pointers, data and strings**, merged to `main` (2026-11-01).
- **M3 assembly blocks and KERNAL calls**, with the M2 follow-up, merged to `main` (2026-11-01).
- **M3 follow-up: fixed inputs** (branch `m4-imports`, 2026-11-01). `fn get_cursor(true using carry) -> u16 using XY @ $FFF0`: a constant in place of a parameter name. Parsed into `ParamSyntax.FixedValue`, checked in `DeclareRoutine` (after the globals, so a variable gets "must be a constant"), stored in `FunctionSymbol.FixedInputs`. A, X, Y take a `u8` constant, the carry `true`/`false`; pairs and other flags are errors. Calls load fixed registers with the other register arguments (A last) and put `sec`/`clc` right before the `jsr` (spec 11 shape tested). Fixed inputs are not in `Parameters`, so callers pass only the other arguments. `-> u16 using XY`/`AY` already worked. Tests: `RoutineTests` (stand-in PLOT at `$C060`), 13 error messages.
- **M4 imports and the standard library** (branch `m4-imports`, 2026-11-01). 575 tests pass (34 CPU, 541 compiler).
  - `import name` at the top of a file (`ImportDecl`; after another declaration it is an error). `ModuleLoader` (`Semantics/Module.cs`) loads every file once, by module name, and returns them with each module before its importers (main last). Modules may import each other. Lookup: `lib/<platform>/name.tern`, `lib/name.tern`, then the importing file's folder. A module found only in another platform's folder (`lib/nes/x.tern` when compiling for the C64) is an error naming the platform.
  - The library directory is `TernCompiler.DefaultLibraryDirectory` (`lib/` next to the compiler); `Compile(..., libraryDirectory:)` overrides it. The source is in `src/lib/` (platform modules in `src/lib/c64/`); `Tern.Compiler.csproj` copies it to the output folders of the compiler, CLI and tests.
  - Qualified names: the parser knows the file's imports and joins `text.put` into one name (`NameExpr`/`CallExpr` with a dotted name), so assembly works too (`jsr util.bump`, `#c64.SCREEN.lo`). Each `Module` has its own `Scope`; `Checker.Lookup` finds `m.name` in the imported module's scope. Declaring a name equal to an imported module is an error; a module name alone gets "use the names in it as util.name".
  - The checker declares file by file (dependencies first), so a file can use imported constants in its declarations (`var buf: [u8; sizes.N]`), then checks all bodies. A module with `fn main` is an error.
  - Top-level symbols of a module are named `module.name` (`FunctionSymbol.Name`, `VariableSymbol.Name`): listing labels, symbol table, VICE labels (`.text.put`; VICE accepts dots in labels). Main-file names are unchanged.
  - Unused imports cost nothing: only reachable functions are compiled (as before), and a module's globals are kept only if a reachable function names them (`FunctionSymbol.UsedGlobals`, filled by names, `addr()` and asm operands). The main file's globals are all kept, as before.
  - `SourcePos.File`: errors in a module report its file (`bad.tern:2:5`); the CLI prints it. The listing shows module lines as `; text.tern:12: ...`.
  - Line breaks inside `( )` and `[ ]` no longer end a statement (lexer), so tables and argument lists can span lines (provisional, question 1).
  - `lib/c64`: `SCREEN`, `COLOR`, the 16 colors; `chrout` ($FFD2, keeps X, Y), `getin` ($FFE4), `cursor` and `get_cursor` (PLOT at $FFF0 with a fixed carry), all routines at a fixed address; `print(src, n)` in Tern (`lda (src),y` / `jsr $FFD2` in a `using Y` loop).
  - `lib/mem`: `copy`, `fill` in Tern (whole pages, then the rest). `lib/text`: `put`, `dec8`, `dec16`, `hex8`, `hex16`, `equal`, `find` in Tern; digits from a `screen"0123456789ABCDEF"` table. `lib/math`: `mul8`, `mul8_fast` (quarter squares, four 256-byte tables), `mul16`, `div8`, `div16` (remainders in `math.remainder8/16`), `shl8`, `shr8`, `shl16`, `shr16`, all `asm fn`.
  - Tests: `ModuleTests` (temporary folders with their own library folder), `MathLibraryTests`, `LibraryTests.cs` (mem, text, c64). `tests/Library.cs` compiles a program that imports the modules and then calls a routine directly in the emulator (registers in, memory parameters set by name), so the 8-bit routines are tested for every input in well under a second. Best and worst cycle counts are asserted in the tests.
  - `examples/library.tern`: prints a message and a number through the KERNAL, writes a label, `6 X 7 = 42` and a row of color to screen memory. Checked in VICE; `C64LibraryTests.Library_example_prints_and_writes_to_the_screen` runs it with KERNAL stand-ins.
  - Roadmap "done when": the example imports `text` and `c64` (and `math`, `mem`), verified in VICE; an unused import adds no bytes (`C64LibraryTests.An_unused_import_adds_no_bytes`, `ModuleTests.An_unused_import_adds_no_bytes`: same image and report).
  - Review fixes (`docs/reviews/m4.md`): 1 and 2 (must fix) and 3 in "Report missing imports and module name case; clearer cycle error": a missing import says to add `import x` (calls, names and asm operands); `import Util` for `util.tern` is an error, so a module cannot load twice on Windows; constants across an import cycle get an error that explains it. 4 in "Keep dots in VICE labels". 5 is question 9 below.

### Library cycle counts

Including `jsr` and `rts`; from the tests.

| Routine | Cycles | Notes |
|---|---|---|
| `math.mul8` | 151-183 | every input |
| `math.mul8_fast` | 65-73 | every input; 1 KB of tables |
| `math.div8` | 187-243 | every input, b > 0 |
| `math.mul16` | 37-853 | edge values; stops when no bits of the multiplier are left |
| `math.div16` | 699-1051 | edge values |
| `math.shl8`, `shr8` | 17 + 7 per step | |
| `math.shl16`, `shr16` | 17 + 12 per step | |
| `mem.fill` | 14837 for 1000 bytes | 13 per byte inside whole pages, 17 in the rest |
| `mem.copy` | 16891 for 1000 bytes | 16 per byte inside whole pages, 19 in the rest (+1 when a read crosses a page) |
| `text.put` | 778 for 40 bytes | 19 per byte |
| `text.dec8` | 63-354 | every value |
| `text.dec16` | 296-1640 | edge values and a spread |
| `text.hex8` | 56-58 | every value |
| `text.hex16` | 100-104 | |

## Known limitations and technical debt

Not bugs; things to improve, mostly in M7 (optimizer).

- Register parameters are stored to memory on function entry, then reloaded on each use.
- An unreachable `rts` follows an endless `while true` at the end of a function.
- After the step of a memory loop counter, the counter is reloaded with `lda` right after it was stored.
- A `for` bound that is a global variable is copied to a temporary (only locals and parameters are used directly, since calls could change a global).
- Loop variables live in memory unless `using X`/`using Y` is written.
- Evaluation order: in `a + b` where `b` needs a temporary, `b` is evaluated before `a`. Matters only if both call functions with side effects.
- Only one zero-page range per platform is used (C64: `$02-$8F`; `$FB-$FE` is unused unless a variable is placed there with `@`).
- `Y` is reloaded for every pointer access in an expression, even with the same index: `x = p[i] + q[i]` loads Y twice. (`dst[i] = src[i]` and `p[i] += 1` load it once.)
- When the value of `p[i] = v` changes Y (it reads through a pointer with another index, or calls a function), A is still saved to a temporary while Y is loaded.
- `p += 256` adds `#0` to the low byte instead of `inc p+1`.
- `addr()` cannot be used in global or data initializers: addresses are known only at link time. Tables of pointers to data must be filled at runtime.
- `VariableSymbol.AddressTaken` is set by `addr()` and by `#name.lo`/`#name.hi` in assembly. The M7 optimizer must not cache such variables in registers across pointer writes or asm blocks.
- `AsmEffects` are unions over the whole block (a register "read" may be one the block set itself first, as in `clc` / `adc`). Good enough for the current checks; the optimizer may want "read before written".
- Inside a `for ... using X` loop, only routines that declare `keeps X` may be called. An `asm fn` that never touches X is still rejected; `AsmEffects` would make allowing it easy (spec 6.1: the compiler may use each function's actual register use).
- `mem.fill` reloads `value` on every byte (`lda value` / `sta (dst),y`): 13 cycles per byte instead of 11. Loop-invariant loads are for the optimizer.
- A `for` bound such as `count.hi` (not a plain variable) is copied to a temporary before the loop.
- Import cycles: declarations are processed module by module, so in a cycle (a imports b, b imports a) a constant of the later module cannot be used in a declaration of the earlier one. The error says so and suggests moving the constant. Function bodies are not affected.

## Questions for the spec session

The M3 questions were settled on 2026-11-01. New ones from M4; each is implemented as described and marked provisional:

1. **Line breaks inside brackets.** Implemented: a line break inside `( )` or `[ ]` does not end a statement, so `data t: [u8; 256] = [` can continue over many lines (the spec's `[ ... ]` table example needs this). A `{` or `}` resets the count. Spec 2 says only "statements end at a newline or `;`".
2. **Platform modules.** Implemented: a platform module lives in `lib/<platform>/` (e.g. `lib/c64/c64.tern`); lookup is `lib/<platform>/`, `lib/`, then the importing file's folder. A module that exists only in another platform's folder is an error. This also lets a platform provide its own version of a general module later. Is a folder per platform the intended mechanism?
3. **Unused globals.** Implemented: a module's globals that no reachable function names are left out; the main file's globals are all kept (existing tests rely on unused `@ zp` globals taking zero page). Should the main file's unused globals be dropped too, for consistency?
4. **Library location.** The source is in `src/lib/` (the coding session owns `src/`), copied to `lib/` next to the compiler. `CLAUDE.md`'s layout section could mention it.
5. **Everything in a module is public**, including work bytes (`math.w0`-`w2`) and tables (`math.SQUARE_LO`, `text.DIGITS`). Is a way to keep names private wanted (e.g. a leading `_`)?
6. **`math` differs slightly from spec 10.1:** `mul16(m, n)` and `div16(n, d)` instead of `(a, b)`, since in assembly `asl a` means the accumulator, so a memory parameter named `a` cannot be shifted by name. Callers are not affected (arguments are positional). `mul8_fast` uses 1 KB of tables (f(x) for x = 0-511 as four 256-byte tables), not 2 KB. Signed versions (`muls8`, `divs8`, ...) are not written yet: needed now, or when a program asks?
7. **Calling a routine with fixed inputs from assembly.** `jsr c64.cursor` in an asm block is a plain `jsr $FFF0`; the block must set the carry itself. Allowed, since in assembly a name is only an address. OK?
8. **`c64.get_cursor`** (PLOT read, `-> u16 using XY`) is added to the table in 10.1, from the 6.6 example.
9. **Spec 11 shape of `math.mul8(a, b)`** (review M4, finding 5): the table shows `lda a` / `ldx b` / `jsr`; the compiler gives `ldx b` / `lda a` / `jsr`, since A is loaded last (M3). Same bytes and cycles; the table could show the compiler's order.

The M3 item about where error tests live was settled on 2026-11-01: in each feature's test file.

## Provisional choices made while coding

Listed as questions 1-9 above. Also: `import` is a reserved word.
