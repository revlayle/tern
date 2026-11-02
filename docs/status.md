# Status

Owned by the coding session. Updated at the end of every coding session.

## Current

- **Milestone:** M6 (`match`, interrupts, function modifiers). The M5 follow-up is done; M6 itself is in progress.
- **Branch:** `m6-match` (from `main` after the M5 merge and the spec commits that settled the M4/M5 questions).
- **Next step:** `match` (spec 5, 11), then `interrupt fn` and its frame root (6.3, 8.1), `inline fn`, `noinit`, and the raster interrupt example.

## Done

- **M1 core compiler**, merged to `main` (2026-11-01).
- **M2 pointers, data and strings**, merged to `main` (2026-11-01).
- **M3 assembly blocks and KERNAL calls**, with the M2 follow-up, merged to `main` (2026-11-01).
- **M4 imports and the standard library**, with the M3 follow-up (fixed inputs), merged to `main` (2026-11-01). Details below.
- **M5 follow-up** (branch `m6-match`, 2026-11-01). 726 tests pass (34 CPU, 692 compiler).
  - `private` before a top-level `const`, `var`, `linear var`, `data`, `struct`, `fn` or `asm fn` (spec 2.2). The parser records the names (`ProgramSyntax.PrivateNames`); `Checker.Lookup` does not find another module's private names, and `NotDeclared` then says "'m.x' is private to the module m". This one lookup serves Tern code, types, `sizeof`, `addr` and assembly operands. `private` inside a function, twice, or before `import` is an error. In the main file it is allowed and changes nothing. `lib/math` (`w0`-`w2`, `SQUARE_*`) and `lib/text` (`DIGITS`, `POWERS`) mark their work bytes and tables private. Tests: `ModuleTests` (`Private_*`), `MathLibraryTests.Library_work_variables_and_tables_are_private`.
  - `addr(player.name)` (spec 3.4): `Checker.CheckAddrOfField` takes the field's view (a scalar or array field is already one; a nested struct field gets one) and `CodeGen.Simple2` loads the storage's address plus the offset as two immediates. Works in zero page, at fixed addresses and across modules (`addr(c64.vic.border)` is `$D020`). Fields through a pointer or in an array of structs are errors. Tests: `StructTests` (`Addr_of_a_field_*`).
  - `linear` arrays of `u16`/`i16`/pointers (spec 3.2), `linear var` and `linear data`. The checker lowers `pts[i]` to `pts[i << 1]` and `pts[c]` to `pts[2c]` (`Checker.LinearIndex`); `CodeGen.HighTable` is 1 for a linear array, so all array code works unchanged and gives the spec 11 shape (`lda i` / `asl a` / `tax` / `lda pts,x` / `lda pts+1,x`). Initial values stay interleaved (`TernCompiler.MemoryLayout`). `addr(pts)` is a `ptr u16` (or the element type). At most 128 elements; one-byte elements are an error. Tests: `LinearArrayTests`.
- **M5 structs** (branch `m5-structs`, merged to `main` 2026-11-01). 670 tests pass (34 CPU, 636 compiler).
  - `struct Name { field: type, field: type @ offset }`, fields separated by `,` or line breaks (`StructDecl`, `Parser.ParseStruct`). `Checker.Structs.cs` lays a struct out once (`StructSymbol.Fields`, `Size`): a field without `@` follows the previous one; nested structs are laid out first; a struct that contains itself, overlapping fields and duplicate names are errors. Struct names are declared before the file's constants, so types may name structs declared further down, and `ptr Enemy` inside `Enemy` works (pointer targets are not laid out). `sizeof(T)` works on structs, also qualified (`sizeof(c64.Vic)`); a type in a module is `shapes.Box` (`ParseType`).
  - **Lowering.** The checker turns every field access into plain variables, arrays and pointers (`Expr.Lowered`); `Lowering.Apply` puts the lowered forms into the tree before code generation, so the code generator has almost no struct code:
    - a field of a struct variable is a *view*: a `VariableSymbol` with `AliasOf` (the struct variable) and `AliasOffset`. `player.hp` is `lda player+2`, `player.name[i]` is `lda player+5,x`. `CodeGen.Var()` turns a view into a byte of its storage, so views need no memory and no allocation. Views are cached (`Checker._views`), so the same field is the same symbol. Fixed-address structs give views with a `FixedAddress`, so they are volatile like any `@ addr` variable.
    - an array of structs is stored as one table per byte of the record: byte k of record i is at `base + k * n + i` (`PlaceKind.Table`). `enemies[i].x` is an index into the view `[u8; n]` at `k * n`: `ldx i` / `lda enemies,x`. A 16-bit field is a split array whose high table is `n` further on (`VariableSymbol.HighTable`; `CodeGen.HighTable()` replaced `Type.Length`).
    - through a pointer, a field is `p[0]` with the field's offset added to Y (`IndexExpr.Offset`, `Stride`, `SecondByte`; `CodeGen.LoadPointerIndex`, `NextPointerByte`). `e.hp -= 1` is `ldy #2 / lda (e),y / sec / sbc #1 / sta (e),y`; `e.name[i]` is `lda i / clc / adc #off / tay`. A pointer field (`e.next.hp`) is copied to a zero-page temporary first, as is a pointer that is a field of a struct variable (views are never used directly as `(zp),y` pointers).
  - Struct-typed and array-field expressions carry a `StructPlace` (memory, table or pointer, plus the offset in the record) while being checked; `Checker.Unwrap` gives the lowered form where the checker pattern-matches.
  - `linear var list: [Enemy; 8]`: whole records in order; cannot be indexed; `addr(list)` is a `ptr Enemy`, stepped with `p + sizeof(Enemy)`.
  - Arrays inside a struct use the same layout as array variables: `[u16; n]` is split inside the record (low bytes, then high bytes).
  - Assembly operands may name fields of struct variables: `sta c64.vic.border`, `#c64.vic.background.hi` (`Checker.Asm.cs`).
  - `lib/c64`: `struct Vic` / `var vic @ $D000` (all registers; `sprite0_x` ... `sprite7_y`, `sprite_color: [u8; 8]`), `struct Voice`, `struct Sid` / `var sid @ $D400` (three nested voices), `struct Cia` / `var cia1 @ $DC00`, `var cia2 @ $DD00`. Unused, they add no bytes (`An_unused_import_adds_no_bytes` still passes).
  - Tests: `StructTests` (layout, struct variables, fixed addresses and volatility, pointers, linear arrays, arrays of structs, spec 3.3 code shapes, 46 error messages), `ModuleTests` (structs in modules, qualified types), `C64LibraryTests` (register addresses, fields in asm, the example), `AsmTests` (asm field errors).
  - `examples/structs.tern`: border and background colors and a sprite through `c64.vic`, and eight enemies moved 100 times both by index (`[Enemy; 8]`) and through a pointer (`linear var`). Checked in VICE (needs `-limitcycles 20000000`: loading takes most of 8 million cycles); `C64LibraryTests.Structs_example_moves_both_lists_the_same` checks the screen against the same moves in C#.
  - Roadmap "done when": the VIC-II struct at `$D000` works in VICE (example); a list of enemies updates through indexing and pointers (example, and `StructTests.A_list_of_enemies_updates_through_indexing_and_pointers`).
  - Review suggestions (`docs/reviews/m5.md`), in "Fix the M5 review suggestions": 1. `s.a()` says the field is not a function (and `v.x()` on a plain variable says `v` is a variable), instead of suggesting `import s`; this also fixes M4 suggestion A. 2. Arithmetic on a struct says "A struct is not a number", and `var x = s` says a struct cannot be used as a value. 3. `ReadsVar` compares the bytes views cover (`CodeGen.Overlap`), so `s.w = s.a + s.b` uses `s.b` directly, as with plain variables.

## M4 details

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
- `enemies[i].hp += 1` loads X twice (`ldx i` before the read and again before the write), as `a[i] += 1` on any array does.
- A pointer that is a field of a struct variable (`player.next.hp`) is always copied to a zero-page temporary, even when the struct is in zero page and could be used as `(player+3),y` directly.
- A field used as a `for` bound (`for i in 0..s.count`) is copied to a temporary, like any bound that is not a plain local or parameter.
- Structs are not put in zero page by usage (step 3 of 8.2), only with `@ zp`, like arrays.
- Struct field offsets use a constant expression; a constant declared later in the same file than the struct cannot be used, since structs are laid out right after the file's constants. Globals cannot be used at all (an offset must be a constant anyway).
- A struct with `@` gaps in an array of structs costs one table (n bytes) per gap byte.

## Questions for the spec session

The M3 questions were settled on 2026-11-01. From M4 (still open); each is implemented as described and marked provisional:

1. **Line breaks inside brackets.** Implemented: a line break inside `( )` or `[ ]` does not end a statement, so `data t: [u8; 256] = [` can continue over many lines (the spec's `[ ... ]` table example needs this). A `{` or `}` resets the count. Spec 2 says only "statements end at a newline or `;`".
2. **Platform modules.** Implemented: a platform module lives in `lib/<platform>/` (e.g. `lib/c64/c64.tern`); lookup is `lib/<platform>/`, `lib/`, then the importing file's folder. A module that exists only in another platform's folder is an error. This also lets a platform provide its own version of a general module later. Is a folder per platform the intended mechanism?
3. **Unused globals.** Implemented: a module's globals that no reachable function names are left out; the main file's globals are all kept (existing tests rely on unused `@ zp` globals taking zero page). Should the main file's unused globals be dropped too, for consistency?
4. **Library location.** The source is in `src/lib/` (the coding session owns `src/`), copied to `lib/` next to the compiler. `CLAUDE.md`'s layout section could mention it.
5. **Everything in a module is public**, including work bytes (`math.w0`-`w2`) and tables (`math.SQUARE_LO`, `text.DIGITS`). Is a way to keep names private wanted (e.g. a leading `_`)?
6. **`math` differs slightly from spec 10.1:** `mul16(m, n)` and `div16(n, d)` instead of `(a, b)`, since in assembly `asl a` means the accumulator, so a memory parameter named `a` cannot be shifted by name. Callers are not affected (arguments are positional). `mul8_fast` uses 1 KB of tables (f(x) for x = 0-511 as four 256-byte tables), not 2 KB. Signed versions (`muls8`, `divs8`, ...) are not written yet: needed now, or when a program asks?
7. **Calling a routine with fixed inputs from assembly.** `jsr c64.cursor` in an asm block is a plain `jsr $FFF0`; the block must set the carry itself. Allowed, since in assembly a name is only an address. OK?
8. **`c64.get_cursor`** (PLOT read, `-> u16 using XY`) is added to the table in 10.1, from the 6.6 example.
9. **Spec 11 shape of `math.mul8(a, b)`** (review M4, finding 5): the table shows `lda a` / `ldx b` / `jsr`; the compiler gives `ldx b` / `lda a` / `jsr`, since A is loaded last (M3). Same bytes and cycles; the table could show the compiler's order.

New ones from M5 (structs), also implemented as described and provisional:

10. **Field separators.** Fields are separated by `,` or line breaks (or both), and a trailing `,` is allowed. Spec 3.3 only shows commas on one line; the `lib/c64` structs need one field per line to be readable.
11. **`@` offsets.** A field without `@` follows the previous field (whatever its offset); fields may be given in any order. Overlapping fields are an error, so there are no unions. Should overlapping fields (two names for one register, e.g. a read and a write meaning) be allowed?
12. **Array fields with 16-bit elements** are split inside the record, like array variables (3.2): `pts: [u16; 2]` is two low bytes, then two high bytes. Through a pointer, `e.pts[i]` reads offsets `off + i` and `off + 2 + i` (no `asl`).
13. **Arrays of structs as fields** (`parts: [Point; 4]` inside a struct) give "not supported yet". They would need a layout decision (records or tables inside the record).
14. **Layout of an array of structs.** "One array per field" is implemented as one table per *byte* of the record: byte k of record i is at `base + k * n + i`. For 8-bit fields this is exactly one table per field; a 16-bit field is a split pair of tables, as in 3.2; nested structs and constant-index array fields flatten the same way. Gaps from `@` offsets cost a table each.
15. **`addr()`.** `addr(player)` gives `ptr Player`, `addr(list)` of a linear array gives a pointer to its first record. `addr()` of an array of structs stored as tables is an error that suggests `linear`. `addr()` of a field (`addr(player.name)`, e.g. to pass a name to `text.put`) is not supported, since `addr` takes a name. Wanted?
16. **Initial values and locals.** Structs and arrays of structs cannot have initial values (there is no struct literal); globals are zeroed at startup. Local struct variables are allowed without an initial value, like local arrays (their memory is shared, so it holds leftovers). `data` cannot hold structs.
17. **`linear` on arrays of numbers** (spec 3.2's `linear var pts: [u16; 32]`) gives "not supported yet"; only arrays of structs can be `linear` now. `linear` goes before `var`.
18. **Assembly operands** may name a field of a struct variable: `sta c64.vic.border` is `sta $D020`, and `#c64.vic.background.hi` works. Fields through a pointer or in an array of structs are an error in assembly (no fixed address).
19. **Struct names share the namespace** of constants, variables and functions in a file; a struct cannot be named like a built-in type.
20. **`lib/c64` register names** (spec 10.1 says only "VIC-II, SID and CIA register structs", e.g. `c64.vic.border`): `vic` (`Vic` at `$D000`: `sprite0_x` ... `sprite7_y`, `sprite_x_msb`, `control1`, `raster`, `light_pen_x/y`, `sprite_enable`, `control2`, `sprite_expand_y`, `memory`, `irq_status`, `irq_enable`, `sprite_priority`, `sprite_multicolor`, `sprite_expand_x`, `sprite_collision`, `sprite_bg_collision`, `border`, `background`, `background1-3`, `sprite_mc0/1`, `sprite_color: [u8; 8]`), `sid` (`Sid` at `$D400`: `voice1-3: Voice` with `freq`, `pulse`, `control`, `attack_decay`, `sustain_release`; `cutoff: u16`, `resonance`, `volume`, `paddle_x/y`, `osc3`, `env3`), `cia1` and `cia2` (`Cia` at `$DC00`/`$DD00`: `port_a/b`, `ddr_a/b`, `timer_a/b: u16`, `tod_tenths/seconds/minutes/hours`, `serial`, `icr`, `control_a/b`). The sprite positions are separate fields, since x and y alternate and arrays of structs cannot be fields (13). Are these names right?
21. **Field offsets through a pointer** must be 0-255 (what `(p),y` reaches); a larger offset is an error at the access, not at the declaration, since the struct may still be used as a variable or at a fixed address.

The M3 item about where error tests live was settled on 2026-11-01: in each feature's test file.

## Provisional choices made while coding

Listed as questions 1-21 above. Also: `import` is a reserved word.
