# Status

Owned by the coding session. Updated at the end of every coding session.

## Current

- **Milestone:** M6 (`match`, interrupts, function modifiers), with the M5 follow-up. Complete; reviewed (`docs/reviews/m6.md`: three must-fix, three suggestions), and all six findings are fixed.
- **Branch:** `m6-match` (from `main` after the M5 merge and the spec commits that settled the M4/M5 questions).
- **Next step:** re-review the fixes, then `/merge m6-match`. After the merge, M7 (optimizer); the spec session settles open question 14.10 first. The spec session should also look at the M6 questions below (22-31); the provisional choices are easy to change.

## Done

- **M1 core compiler**, merged to `main` (2026-11-01).
- **M2 pointers, data and strings**, merged to `main` (2026-11-01).
- **M3 assembly blocks and KERNAL calls**, with the M2 follow-up, merged to `main` (2026-11-01).
- **M4 imports and the standard library**, with the M3 follow-up (fixed inputs), merged to `main` (2026-11-01). Details below.
- **M6 match, interrupts and function modifiers** (branch `m6-match`, 2026-11-01). 810 tests pass (36 CPU, 774 compiler).
  - **`match`** (spec 5; `MatchStmt`, `Parser.ParseMatch`, `Checker.CheckMatch`, `CodeGen.GenMatch`). On `u8` and `i8` values. One arm per line, `patterns => body`, where a body is a block or one statement; patterns are constants, `,` alternatives and inclusive ranges `lo..=hi`; `_` is the default. The checker stores each pattern as unsigned values `From`..`To` (an `i8` adds 128), so values matched twice are found and the code generator only compares bytes. The value is loaded into A once (an `i8` gets `eor #$80`, turning signed order into unsigned order).
    - **Jump table** when a compare chain would need at least 8 compares (a range needs 2) and the values span at most twice that: `sec` / `sbc #low` (if low > 0), `cmp #span` / `bcs default`, `tax`, then `lda hi,x` / `pha` / `lda lo,x` / `pha` / `rts` to the target minus 1. The tables are a new assembler item, `LabelTable`. Uses Y if X holds a loop variable.
    - **Compare chain** otherwise, in source order: `cmp #v` / `beq`; ranges at either end of the byte take one compare, others two. The last test is inverted to branch to the default (or the end), so the last arm's body follows it directly.
    - Bodies follow the dispatch, each ending in `jmp` to the end; the default arm is last. `break`/`continue` in an arm leave the enclosing loop. The statement walkers (`ContainsCall`, `AlwaysReturns`, `ContainsBreak`, `CodeGen.AssignsVar`, `Lowering`) look inside arms.
    - Tests: `MatchTests` (every byte value through chains, tables, signed values; the value evaluated once; nesting; exact code shapes; 19 error messages).
  - **`interrupt fn`** (spec 6.3, 8.1). `CodeGen.GenInterrupt` generates the body, then inserts code that saves the registers it changes (from `AsmEffects.Of` over the generated instructions; all three if it calls anything; A whenever X or Y is saved, since they go through A) and restores them before `rti`. `return` jumps to the restore code. No parameters or return value; calling it from Tern or with `jsr` is an error.
    - Roots (`Checker.OrderRoots`): `main`, every handler in the main file, and module handlers that a compiled function names in assembly (`FunctionSymbol.UsedInterrupts`: `lda #h.lo` installs a handler; it does not count as a call, unlike other functions, decisions 2026-11-01). A function with a body reachable from two roots is an error (main and a handler, or two handlers, since an NMI can interrupt an IRQ handler). Routines at a fixed address have no frame and may be shared.
    - `Allocator.FrameOffsets`: a handler's frame starts after every frame placed before it (main's tree, then earlier handlers'), so handler frames never overlap main's.
    - Emulator: `Cpu6502.Irq()` (taken only when I is clear) and `Nmi()`, with CPU tests. `Run.Program(..., irqEvery: n)` raises an IRQ n cycles after the last one was taken; the test CPU now starts with I set, as after reset.
    - Tests: `InterruptTests` (a handler with loops, calls and locals firing every 400 cycles during main's register-heavy loops changes nothing main uses; checked to fail if X is not saved; frame placement; code shapes; 12 error messages), `ModuleTests.A_module_interrupt_handler_is_kept_only_when_named`.
  - **`inline fn`** (spec 6.2; `CodeGen.GenInline`). Arguments are stored as for a call, then the body is generated in place of `jsr`; `return` jumps to the end of the copy (`ReturnJump`). The function keeps its static frame and its call-graph edges, so frame sharing, recursion and the interrupt rules are as for a call. Its temporaries use their own pool (they are in its frame). It has no code or symbol of its own; naming it in assembly is an error. `inline asm fn` is "not supported yet". Automatic inlining (6.2: "small non-recursive functions with a single caller") is left to M7. Tests: `InlineTests`.
  - **`noinit var`** (spec 4). Not zeroed at startup: in RAM such globals go after the zeroed ones (`RamPlan.NoinitGlobals`), so the cleared range shrinks; in zero page the startup code skips them. Works with `@ zp`, `linear`, `private`. An initial value, a fixed address, `data` and locals are errors. Tests: `NoinitTests` (runs over memory filled with $AA).
  - `RejectUnsupported` is gone: every keyword the parser reserves is implemented.
  - **`examples/raster.tern`**: eight color bars in the border from a VIC-II raster interrupt. KERNAL off (`$01 = $35`), handlers in the RAM vectors `$FFFE` (raster) and `$FFFA` (an empty NMI handler, so RESTORE does nothing), installed with an asm block. The handler acknowledges through an `inline fn`, picks the color with a `match` (a jump table) and sets the next raster line; main prints the frame count. Checked in VICE (`-limitcycles 20000000`): the bars and a running counter show. `C64LibraryTests.Raster_example_cycles_the_border_colors_from_its_interrupt` runs it with an IRQ every 1000 cycles and checks every border write, the vectors and the counter on screen.
  - Roadmap "done when": the raster example runs in VICE; `match` code shapes are checked (`MatchTests.Code_shapes`, `A_dense_match_uses_a_jump_table`).
  - Review fixes (`docs/reviews/m6.md`), in "Fix the M6 review findings" and "Move the OrderReachable summary back to it". 816 tests pass (36 CPU, 780 compiler).
    1. A module's private variable used by functions from two roots is an error (`Checker.CheckPrivateGlobalsPerRoot`), reported at the handler and naming both functions: `math.mul8` in main with `math.div8` in a handler share `math.w0`-`w2`. Public globals and private `data` (read-only, e.g. `text.DIGITS`) may be shared. Tests: `InterruptTests.Private_module_variables_used_from_two_roots_are_errors`, `Public_variables_and_private_data_may_be_shared_with_a_handler`.
    2. A dense `match` uses the compare chain when X and Y both hold loop variables (`MatchTests.A_dense_match_inside_loops_using_x_and_y_uses_a_compare_chain`).
    3. A `match` with no value arms always evaluates its value, so a volatile read is kept (`MatchTests.A_match_with_only_a_default_still_reads_a_volatile_value`).
    4. Copies of an inline function reuse its free temporaries (`CodeGen._inlineTemps`), so its frame does not grow per call site (`InlineTests.Copies_of_an_inline_function_share_its_temporaries`).
    5. `examples/raster.tern` explains that the bars are not stabilized and their right edge jitters.
    6. The `OrderReachable` summary is back above it.
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
- A `match` jump table always checks the bounds (`cmp #span` / `bcs`), even when the value cannot be out of range (e.g. `band` in the raster example, which is always 0-7).
- A `match` on a value already in a register (a `using X` loop variable) copies it to A first (`txa`).
- An inline function's parameters go through its frame in memory, as for a call; register parameters are stored and reloaded.
- Calling an inline function counts as a call: it is not allowed inside a `for ... using X` loop, and it may not be shared between main and an interrupt handler, even when it has no variables at all.
- An interrupt handler saves every register that any of its instructions changes, on every path.
- An interrupt handler does not execute `cld`. Tern code never sets decimal mode, but an asm block in main could.

## Questions for the spec session

The M4 and M5 questions (1-21) were settled on 2026-11-01 (decisions.md) and are implemented (`private`, `addr()` of fields, `linear` 16-bit arrays). New from M6, each implemented as described and provisional:

22. **`match` value types.** `u8` and `i8` only; a 16-bit value gives "not supported yet" (suggests `.lo`/`.hi` or `if`). `bool`, pointers and structs are errors. Wanted for `u16`?
23. **`match` syntax.** One arm per line; a body is a block or a single statement. Patterns are constant expressions (numbers, chars, consts), `,` alternatives and inclusive ranges `lo..=hi`; `lo..hi` is an error ("a range in a match includes its end"), since spec 5 only shows `..=`. `_` must be last and alone in its arm. A value in two patterns, or an empty range, is an error. There is no fall-through, and no `_` is needed: values that match nothing skip the match.
24. **When `match` uses a jump table** (spec 11 says "dense constant cases"). Implemented: at least 8 compares in the chain it replaces (a range counts 2) and a span of at most twice that. The dispatch costs about 29 cycles (plus 4 for `sec`/`sbc` when the lowest value is not 0) against 5 per compare, so below 8 the chain is about as fast and shorter. The jump uses `rts` (target minus 1 pushed from a high and a low table), which needs no temporary and has no `jmp ($xxFF)` problem. Should the rule be in the spec?
25. **Which registers a handler saves.** Those its generated code changes (all three if it calls anything), A whenever X or Y is saved (they go through A), in the order A, X, Y. The flags are saved by the CPU. No `cld`. OK?
26. **Interrupt roots and installation.** Every `interrupt fn` in the main file is compiled, even if nothing installs it. A module's handler is compiled only if a compiled function names it in assembly. Installing is left to the program (an asm block writing the vector), since `addr()` of a function is not supported; `lib/c64` could get helpers later (e.g. `c64.set_irq`, switching the KERNAL off as the example does). Naming a handler in assembly (`lda #h.lo`) is not a call; `jsr h` and calling it from Tern are errors.
27. **Functions shared between roots.** Any function with a body reachable from two roots (main and a handler, or two handlers) is an error, as spec 8.1 says. This includes an inline function with no variables (the raster example wanted `acknowledge()` in main and in the handler); such a function would be safe to share. Relax the rule for functions without a frame? Routines at a fixed address are allowed.
28. **`inline fn`.** Keeps its static frame (parameters and locals have fixed addresses, as for a call), so it costs the same memory but saves the `jsr`/`rts`. Automatic inlining of small single-caller functions (spec 6.2) is left to the optimizer (M7). `inline asm fn` gives "not supported yet" (its `rts` and local labels would need rewriting); naming an inline function in assembly is an error.
29. **`noinit` syntax.** Spec 4 says only "a variable can be marked `noinit`". Implemented as a prefix, like `linear`: `noinit var buf: [u8; 256]`, also `noinit linear var` and `private noinit var`; `@ zp` works. Errors: with an initial value, at a fixed address (never zeroed anyway), on `data`, on locals.
30. **Raster example** (roadmap M6): uses the RAM vectors with the KERNAL switched off, rather than the KERNAL's `$0314` vector, since a Tern handler saves its own registers and ends with `rti`, while a `$0314` handler must end by jumping into the KERNAL's exit code.
31. **Private module variables and interrupts** (review M6, finding 1). A module's private variable used by compiled functions from two roots is an error; the library's work bytes are the case that matters (`math.w0`-`w2`). Public variables and private `data` may be shared. Spec 8.1 could say so. A later improvement could move work bytes into the functions' frames, so that e.g. `math.mul8` in main and `math.div8` in a handler would be allowed.

## Provisional choices made while coding

Listed as questions 22-31 above. Also: `import` and `private` are reserved words.
