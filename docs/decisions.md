# Design decisions

Settled decisions and the reasons for them. Do not reopen one without new information; if a change is needed, add a new entry that replaces the old one.

## 2026-11-01: founding decisions

**Purpose.** A higher-level way to write 6502 programs that still produces efficient assembly. Restrictive is fine; assembly is too. Cycle-heavy work goes into library functions.

**Compiler in C#, with its own assembler and emulator.** No external assembler. The compiler works on typed instruction objects, knows exact sizes and cycles, fixes branch ranges itself, and parses inline assembly so it understands what a block uses.

**Targets.** C64 first. NES, Commander X16, Apple II and Atari 8-bit later. CPU variant (instruction set) and platform (memory map, file format) are separate settings.

**Less like Prog8.** The user knows Prog8 and wants Tern to look different where Prog8 has its own conventions. C-style syntax shared by most curly-brace languages is fine.

## Cost and restriction

- **Operators never hide loops.** `*`, `/`, `%` work only on compile-time constants. Runtime multiply/divide and variable shifts are library functions (`mul8`, `div8`, `shl8`, ...). Reason: developers should consciously choose cycle-heavy operations.
- **The library is ordinary code** (Tern or `asm fn`), not compiler built-ins. Multiplying by a constant is written out with shifts and adds.
- **No recursion, ever.** A software stack would make every local variable access slower in every function. Recursive algorithms use an explicit stack array.
- **No C compatibility.**
- **Division leaves the remainder in a library variable** (`remainder8`), as assembly routines do.

## Symbols and keywords

- **One meaning per symbol and keyword.** Exceptions: unary `+`/`-` (on numbers and variables, as in mathematics), and assembly blocks (standard 6502 syntax).
- **`@` means location only:** `@ $D020` (fixed address), `@ zp`, `@ bank(n)`, and field offsets inside structs. Prog8 overloaded `@`; Tern does not.
- **`using` means "kept in this register or flag":** parameters (`x: u8 using X`), returns (`-> bool using carry`), loop variables (`for i in 0..n using X`).
- **`ptr T`** for pointers; no `*` dereference (use `p[0]`); **`addr(name)`** for address-of.
- **`&&`, `||`, `!`** for bool logic (C style), only on `bool`. `&`, `|`, `^`, `~` only on integers.
- **`const` means compile time**; **`data`** declares read-only tables in memory.
- **Hex is `$C000`, binary `0b1010`.** No `0x`, no `%` binary (`%` is modulo).
- **`match` alternatives use `,`**, not `|`.
- **No `peek`/`poke`**: a variable `@ addr` does the same.
- **`.lo` / `.hi`** for bytes of a 16-bit value, also in assembly (instead of `<` / `>`).

## Types and conversions

- **No integer promotion.** `u8 + u8` is `u8` and wraps.
- **Literals take their type from context**; a literal that does not fit is a compile error.
- **Automatic widening** within the same signedness; everything else needs `as`. Mixing signed and unsigned is an error.
- **Any pointer converts to any other pointer type** with `as`, with no restrictions. Restricting this is infuriating in other languages.
- **`>>` on signed types keeps the sign** (rounds down); programmers are expected to know shift behavior.
- **Array indexes are always `u8`.** Larger memory goes through a `ptr`.

## Structs

- **Structs are memory layouts (overlays)**, always used in place, never copied: no struct assignment, comparison, parameters or return values.
- Can be used as a variable, at a fixed address (hardware registers, with `@` field offsets), or through a pointer.
- **Nesting is allowed**; offsets add up at compile time.
- **Arrays of structs are stored as one array per field**; `linear` stores whole records for pointer access.

## Loops

- **`for` is kept** (not just `while`) because it guarantees how the loop variable changes, which lets the compiler choose fast loop shapes. The loop variable is read-only in the body; bounds and step are evaluated once.
- **`..` excludes the end, `..=` includes it** (Rust style).
- **Direction comes from the bounds** when both are constants (`9..=0` counts down), or when the end is `0`. Otherwise up, unless `step` is negative. Decided at compile time, never at runtime.
- **`step`** as in BASIC, a non-zero constant.
- **Constant ranges:** empty range generates no code; one value runs the body once without a loop.
- **`for _ in`** when the counter is not needed. **`while true`** is the only endless loop (`loop` was dropped).

## Memory and calls

- **Static frames.** Parameters are ordinary variables in the callee's frame; the caller writes them directly. Callers cannot read them back.
- **Functions that never run at the same time share memory**, computed from the call graph.
- **Calls inside arguments run first**, before any parameter of the outer call is written.
- **`@ zp` is a guarantee**: compilation fails if zero page is full. Other variables go to zero page by usage, best effort.
- **Local variables must have an initial value** (their memory is shared, so it holds leftovers).

## Compile time

- **v0:** constant expressions plus `include_bytes`. **Later:** `const fn`, a compile-time interpreter for a subset of Tern, with its own `int` (64-bit, question 87) and `real` types, range-checked when values enter the program.

## Settled during M1 (2026-11-01)

- A `for ... using X` loop cannot contain a function call (the call would change X).
- Register parameters are stored into the frame on entry. The optimizer (M7) should keep them in registers when possible.
- Signed loop variables are not supported yet.

## 2026-11-01: standard library and imports

- **The standard library is ordinary Tern and `asm fn` source** in the compiler's `lib/` folder, not language features. Reason: lower the entry barrier with ready-made routines, keep the language small, and let users read and change the routines.
- **Modules: `math`, `mem`, `text`, `c64`** (KERNAL calls first; VIC-II/SID/CIA register structs once structs exist).
- **Included with `import name`, used with qualified names** (`text.put`). Reasons: no name clashes as the library grows; platform modules only load on their platform; the same mechanism splits user programs into files. `.` keeps one meaning, "member of". Replaces "v0 is a single file with an always-available library".
- **Strings and memory blocks are passed as pointer plus length.** Shortest loops, matches `len()` on literals, strings may contain any byte. No zero-terminated variants in the library.
- **The library arrives early (M4, right after assembly blocks)**, since KERNAL calls need `asm fn`, and programs cannot multiply at runtime without `math`.

## 2026-11-01: M2 questions settled

- **Encodings are built into the compiler, per target.** The target sets the default for unprefixed literals; a prefix (`screen"..."`, `petscii_mixed'a'`) overrides it. Reason: every 8-bit system has its own character codes, and lower/upper-case sets differ even on one machine. First set: `petscii`, `petscii_mixed`, `screen`, `screen_mixed`, `ascii`, `apple`, `atascii`, `atari_screen`, each added with its platform. The NES has no default; user-defined encodings later.
- **`screen'A'`** and other prefixed char literals are allowed, matching strings.
- **Escapes:** `\0`, `\n`, `\\`, `\"`, `\'`, `\$NN` (raw byte). Characters an encoding cannot represent are errors.
- **`addr(name)` gives a pointer** (`ptr T`), not a `u16`. Arrays give a pointer to the first element; split 16-bit arrays a `ptr u8` to the low-byte table.
- **Number literals convert to pointers without `as`**, consistent with literals taking their type from context. Typed `u16` values need `as`.
- **`p[i]` on 16-bit pointers counts elements** (a shift, a short fixed sequence). The byte offset must fit in 256: constant indexes above 127 are errors; variable ones wrap.
- **No `.lo`/`.hi` on pointers**: `p.name` is reserved for fields of the pointed-to struct.
- **`p - q` gives a `u16` byte distance.** Pointer offsets are unsigned; `p - n` moves back.
- **`data` from strings and `include_bytes` may exceed 256 bytes**, used through pointers, not indexes.
- **Strings can initialize global `var` arrays** of exactly their length.
- **Only functions are stopped from reusing a built-in's name.**
- **Deferred:** `data` at a fixed address (needed with banking), user-defined encodings.

## 2026-11-01: routines at a fixed address

- **A function with no body, placed with `@`, is a routine that already exists at that address**: `fn chrout(ch: u8 using A) @ $FFD2`. Calls are a direct `jsr $FFD2`. Reason: wrapping each KERNAL routine in an `asm fn` costs a `jmp` (3 bytes, 3 cycles) per routine and call. `@` keeps its one meaning (location), so no new keyword like `extern` is needed. A body plus `@ $addr` is not supported yet.
- **Register parameters only.** The routine has no frame for memory parameters.
- **The compiler assumes a call changes A, X, Y and the flags; `keeps X, Y` declares registers the routine leaves unchanged.** Reason: the compiler cannot see inside ROM, so the safe default needs nothing written. Listing what is kept (not Prog8's `clobbers`) makes leaving it out safe. It lets a `for ... using X` loop call e.g. `chrout`, an exception to the M1 rule that such loops contain no calls. Flags always count as changed. `asm fn` needs no `keeps`, since the compiler reads the block.
- **Implemented in M3** with assembly blocks. `lib/c64` (M4) declares KERNAL routines this way where they fit the calling convention, and uses `asm fn` for the rest. Replaces "KERNAL calls need `asm fn`" as the reason for the library's timing; it still follows M3.

## 2026-11-01: fixed inputs for routines at a fixed address (M3 question 10)

- **A constant in place of a parameter name is a fixed input**: `fn get_cursor(true using carry) -> u16 using XY @ $FFF0`. The compiler passes it on every call (`sec`/`clc` right before the `jsr`, or `lda`/`ldx`/`ldy #n`); callers do not write it. Fixed inputs mix with ordinary register parameters. Reason: KERNAL routines such as `PLOT` select a mode with the carry; declaring one name per mode at the same address gives readable calls with no meaningless `true`/`false` argument. `using` keeps its meaning, and no keyword is added.
- **Carry is the only flag allowed**; it is the only one with both a set and a clear instruction. Registers A, X, Y are allowed too.
- **Only on routines at a fixed address.** An `asm fn` sets up its own inputs in code.
- **Not now: carry as a runtime parameter** (`read: bool using carry`). No need once fixed inputs exist; add if a routine needs a variable flag input.
- **16-bit returns may use any register pair** (`-> u16 using XY`), stated in 6.1; needed for `PLOT`'s row and column.

## 2026-11-01: M3 questions 1-9 settled (provisional choices confirmed)

- **`;` in an asm block is an error**, not a comment. Reason: `;` already separates Tern statements; pasted assembly fails loudly instead of being misread.
- **Zero-page operands use the zero-page form** of the instruction, as in any assembler. Branches are never rewritten. Forcing absolute addressing is left until timing-exact code needs it.
- **`asl a`, `asl A` and `asl` mean the accumulator** (also `lsr`, `rol`, `ror`), even if a variable `a` exists.
- **`#name` for an address is an error**; write `#name.lo` / `#name.hi`. Immediate numbers are -128 to 255.
- **Mnemonics and `,x`/`,y` ignore case**; labels and Tern names do not.
- **Assembly can `jsr`/`jmp` only to functions without memory parameters.** Parameters cannot be named outside their function (6.1), so asm cannot set them.
- **`#f.lo` / `#f.hi` count as a call** from the enclosing function. Interrupt handlers (M6) will need their own rule.
- **`p - q` needs pointers of the same type**, like comparisons; `as` costs nothing.
- **`petscii_mixed` and `screen_mixed` have no `π`**; `\$FF` gives the raw byte.

## 2026-11-01: error tests live with their feature

- **A compile error's test goes in the feature's test file** (`AsmTests`, `PointerTests`, `RoutineTests`, ...); `ErrorTests.cs` keeps errors with no feature file (lexer, parser, general type errors). Every error still needs a test. Reason: the error tests for a feature sit next to its other tests, and M3 already works this way. Replaces "every new compile error gets a test in `ErrorTests.cs`" in `CLAUDE.md`.
- **The spec session owns `CLAUDE.md`**, since it holds the design rules and conventions.

## 2026-11-01: M4 and M5 questions settled

Provisional choices confirmed as built (status questions 1, 2, 4, 6-10, 12, 14, 16, 18-21):

- **A newline inside `( )` or `[ ]` does not end a statement**, so tables and argument lists can span lines.
- **Platform modules live in `lib/<platform>/`**; lookup is `lib/<platform>/`, `lib/`, then the importing file's folder. A platform folder can also replace a general module.
- **`math.mul16(m, n)` / `div16(n, d)`**: `asm fn` memory parameters avoid the name `a` (`asl a` is the accumulator). `mul8_fast` uses 1 KB of tables. Signed versions are written when a program needs them.
- **`jsr` to a routine with fixed inputs in assembly is a plain `jsr`**; the block sets them up. In assembly a name is only an address.
- **`c64.get_cursor`** is part of the library; spec 11 shows `ldx b` / `lda a` for `math.mul8` (A is loaded last).
- **Struct fields are separated by `,` or newlines**, with an optional trailing `,`.
- **`[u16; n]` fields are split inside the record**, like array variables.
- **Arrays of structs store one table per byte of the record**; `@` gaps cost a table each.
- **No struct initial values or struct literals**; local structs are allowed without one, like local arrays; `data` cannot hold structs.
- **Assembly may name fields of struct variables** (fixed addresses), not fields through a pointer.
- **Struct names share the file's namespace**; offsets through a pointer must be 0-255, checked at the access.
- **`lib/c64` register names** as listed in spec 10.1.

Decided:

- **Unused globals in the main file are kept** (question 3); a module's unused globals and `data` are dropped. No keyword to pin a global. Reason: in the main file an unused `@ zp` or `@ addr` global usually reserves memory on purpose; library leftovers are clutter. A pin keyword would only be needed if dropping were the default.
- **`private` keyword for module members** (question 5). `private` before a top-level declaration limits it to its own module, also in assembly; the default stays public. Reason: a leading `_` would be either an unchecked convention or a second meaning for `_` next to `for _ in`. A keyword is explicit and keeps existing code unchanged. The library marks its work bytes and tables `private`.
- **Overlapping struct fields stay an error** (question 11). It catches typos in register maps. Revisit if a real case appears (likely: allow overlap only between fields placed with `@`).
- **Arrays of structs as struct fields: "not supported yet"** (question 13). A limited form is wanted eventually; discuss after a first complete version (spec 14.11).
- **`addr()` of a field** of a struct variable or fixed-address struct (question 15): `addr(player.name)`. The address is a constant, so it costs nothing; needed to pass a field to `text.put`. Fields through a pointer or in an array of structs are errors.
- **Spec 2.2 now states that unused module data is dropped**, not only unreachable functions (already implemented, tested in `MathLibraryTests`).

## 2026-11-01: `linear` arrays of 16-bit numbers (M5 question 17)

- **`linear` stays for `u16`/`i16`/pointer arrays**, as spec 3.2 already promised: values stored whole, low byte first. Reason: data shared with assembly, files and hardware often uses this layout, and `addr()` of a linear array is a real `ptr u16` that works with `p[i]`.
- **A variable index is allowed and costs one `asl`** (`lda i` / `asl` / `tax`), documented in spec 11. At most 128 elements. Reason: doubling is a short fixed sequence, the same rule as `p[i]` on 16-bit pointers (M2), and the cost is visible in the spec. Linear struct arrays stay unindexed, since their size is not a power of two in general.
- **`linear` on one-byte elements is an error**: they are already in order.

## 2026-11-02: volatile memory (spec 14.10)

- **Volatile is decided by where memory is, not by `@`.** Memory in a platform's volatile area (C64: `$00–$01`, `$D000–$DFFF`, the KERNAL interrupt's bytes) is volatile with no keyword; other fixed-address memory is ordinary and may be optimized. Replaces "all `@ addr` variables are volatile". Reason: hardware registers are declared once, in the platform library or in its I/O area, while most fixed-address variables in programs are RAM (screen buffers, sprite data, tables). Making them all volatile would block the M7 optimizer or need a keyword on almost every declaration. A wrong entry in the platform's list costs a missed optimization, never a wrong result.
- **`volatile` prefix keyword** for RAM that something else changes (REU transfers, outside code): `volatile var flags: u8 @ $C000`. Global `var`s only. A familiar word from C, and it describes the behavior, not the location; the alternatives (`ram`, `plain`, `fixed`) read as allocation or as a second word for "fixed address". Override goes one way only: memory in a volatile area cannot be marked ordinary. Reason: the cases (RAM under I/O, color RAM) are rare and cost only cycles; a second keyword can come later if benchmarks show a need.
- **Variables used from two roots** (main and a handler, or two handlers) are volatile automatically, from the call graph. Reason: a main loop waiting for a flag set by a handler would hang once the flag is cached in a register.
- **Pointer reads and writes are volatile.** The compiler cannot know where a pointer points, and they can rarely be cached anyway.
- **Asm blocks and calls to routines at a fixed address make the optimizer forget** the values it holds for ordinary memory. They can change memory the compiler cannot see.
- **A fixed-address variable overlapping the program or its variables is a compile error.** Reason: graphics data must often sit inside the program's RAM area (VIC bank 0 is `$0000–$3FFF`), and a growing program would otherwise overwrite it silently. Placing the program around reserved areas is left to a memory layout declaration.
- **Memory layout declaration: new open question** (spec 14.12): reserved areas, volatile ranges, named regions, later banks, decided with 14.1 and 14.9. Volatile ranges declared in code wait for it; a per-variable `volatile` covers the cases now.
- **The sketched `memory { ... }` syntax is the likely direction** for 14.12 (address range, then `program`, `reserved`, `volatile` or `region name`). The open points stay open until it is built.
- Modifier keywords are now six (`private`, `noinit`, `linear`, `inline`, `interrupt`, `volatile`), the limit in 14.7; no decorator syntax yet.

## 2026-11-02: M6 questions settled (status questions 22-31)

Provisional choices confirmed as built (22-25, 28-31), now in spec 4, 5.2, 6.2, 6.3, 8.1 and 11:

- **`match` on `u8` and `i8` only** (22); 16-bit gives "not supported yet". Reason: twice the compares and no jump table; add when a program needs it.
- **`match` syntax** (23): one arm per line, `,` alternatives, `lo..=hi` ranges, `_` alone in the last arm, no fall-through, `_` not required. `lo..hi` is an error, so it is never confused with `for`'s range that excludes its end.
- **Jump table rule in spec 11** (24): at least 8 compares replaced and a span of at most twice that. Reason: design rule 4, users should be able to predict when they get a table.
- **A handler saves the registers its instructions change** (25), asm blocks included, all three for any `jsr`, order A, X, Y, no `cld`. No override: code the compiler cannot follow (an asm `jmp` out of the handler) saves registers itself.
- **`inline fn` keeps its frame** (28); automatic inlining is the optimizer's job; `inline asm fn` "not supported yet".
- **`noinit` is a prefix** (29), like `linear` and `volatile`.
- **The raster example uses the RAM vectors with the KERNAL off** (30), since a Tern handler ends with `rti`.
- **A module's private variable used from two roots is an error** (31); public variables and `data` may be shared. The volatile rule does not help here: two users of the same work bytes corrupt each other.

Decided:

- **`addr(f)` of functions** (26): a `u16` constant, for functions without memory parameters (a call through an address cannot set them). A function whose address is taken, and what it reaches, get frames that overlap nothing, since the compiler cannot see calls through an address. Reason: needed for interrupt vectors, KERNAL vectors and jump tables without asm.
- **Installing interrupts is done by platform library functions** (26), not by syntax. Reason: platforms differ too much (C64 RAM vectors swapped at run time, NES vectors fixed in ROM), so a common syntax would hide real differences; each platform library offers what its machine allows.
- **Functions with no memory of their own may be shared between roots** (27): no memory parameters, locals or compiler temporaries, and calling only such functions. Their only state is registers, which the handler saves. The error names a hidden temporary when that is the reason. Reason: the raster example's `acknowledge()` is safe to share; the full solution (a copy per root) stays open (14.2). To be revisited with real programs.

## 2026-11-02: M7 questions settled (status questions 32-37)

- **An array index outside the array is not supported** (32; spec 3.2). The optimizer may assume an indexed write changes only its array. No check is ever generated in the program (design rule 1); a constant index past the end is an error, and so is a `for` loop with constant bounds indexing past the end, since the range is known. Variable indexes are checked by the emulator in test runs, with the overflow checks (14.4). Reason: a check per index is a hidden cost, and without the assumption every indexed write would make the optimizer forget all memory. Writing across variables on purpose uses a pointer.
- **`#f.lo` in assembly takes an address, not a call** (33; spec 6.5), confirmed as built. A function reached only through its address has no known root; calling it from two roots through its address is the program's responsibility, like `keeps`. Reason: the compiler cannot see such calls, and guessing a root (e.g. whoever takes the address) is wrong for installed vectors.
- **A module's own handler may share the module's private variables** (34; spec 8.1). Rule 31 stays for every other case. Reason: rule 31 exists for work bytes such as `math.w0`, where the module's author cannot know the user calls it from main and a handler. When the handler is in the same module, its author wrote both sides, and the sharing is by design (a `timer` module counting private `ticks`). Without the exception the workaround is making the variable public, which defeats `private`. Accepted gap: private scratch bytes shared by the module's own handler and functions are not reported.
- **`lib/c64` interrupt functions** (35): `kernal_off`, `set_irq`, `set_nmi`, `interrupts_on`, `interrupts_off`, as built. `interrupts_on`/`interrupts_off` become inline, since a `jsr` costs 14 cycles for one 2-cycle instruction. That `set_irq` needs the address of an `interrupt fn` is a documented convention: `addr()` is a plain `u16`, so a library function cannot check it, and no function type is added for this. Doc comments and a language server (roadmap, later) are where such notes will show.
- **The C64's volatile areas are listed in spec 12.2** (36). Reason: whether an address is volatile changes the generated code, so it must be predictable from the spec (design rule 4).
- **The benchmark baseline fails on any change** (37), better or worse, so every change in bytes or cycles is recorded. Process rule, in `CLAUDE.md`.

## 2026-11-02: M8 comparison questions 38 and 40

- **A `for` loop counting up with a `u8` counter may have a variable `u8` step** (38; spec 5.1, 11). The existing rule for stepped loops with a variable end (stop when adding the step carries past the end of the type) already gives the right meaning, so `for m in (n << 1)..=255 step n` compiles to `adc n` / `bcc loop`, as hand-written assembly does in `sieve`. No new syntax, and the code shape is fixed (design rule 1). A step of 0 loops forever, with no check (a check would cost every loop for a mistake). Rejected: range analysis that turns a `u16` counter into 8 bits (whether a program gets the fast code would depend on rules the user cannot see, against rule 4); a library add that returns the carry (a measurement, not a fix).
- **`using` on locals stays deferred** (40; spec 14.6). The `crc8` case needs A, the register every expression uses, so a declared `using A` local would conflict with almost all code in its scope. The optimizer recognises `x & $80 != 0` followed by `x << 1` as one `asl` and a carry test instead. Revisit with programs where X or Y would hold a local.
- **No flag-test statement or flag expression** (39; spec 6.2, 7). Rejected: Prog8-style `if_cs` and an expression such as `if (bool using carry)`. Generated code makes no promise about the flags it leaves (`x += 1` is `inc`, which sets Z but not the carry), so reading a flag after Tern code would freeze code shapes and block the optimizer, and a flag left by an asm block is better tied to that block. Instead **`inline asm fn`** is supported: register parameters only, no `jsr`/`rts`, and flag returns (`-> bool using carry/zero/negative`) give `if f()` / `!f()` / `v = f()` at the cost of the branch or store only. An empty body reads the flag left by the preceding code; this is defined only directly after an `asm` block, and unspecified (no diagnostic) after compiled code, since the compiler cannot reliably tell the cases apart.

## 2026-11-02: order after M8

- **M9 closes the gap with hand-written assembly** (the M8 comparison decisions, plus optimizer work). **New platforms come last**, after the C64 language and library are complete. Reason: the language and its code quality are still changing; each platform added earlier multiplies the work of every later change (library modules, tests, VICE-like checks per machine). The cross-platform standard library grows before them, so that platforms arrive with a library to port.

## 2026-11-02: cross-platform library (M10)

- **M10 grows the cross-platform library**, after M9 (its small helpers need `inline asm fn`) and before new platforms, so platforms arrive with a library to port. Contents in spec 10.1: `wide`, `bcd`, signed `math` with `min`/`max`/`clamp` and `sin8`/`cos8`, `random`, signed and fixed-width decimal in `text`, `mem.copy_back`. Left for when an example needs them: `sqrt`, `atan2`, a byte queue, rectangle overlap.
- **24- and 32-bit numbers are a library, `wide`, not primitive types.** Structs `wide.U32 { lo: u16, hi: u16 }` and `wide.U24 { lo: u16, hi: u8 }`, so the 16-bit halves are ordinary fields (`score.hi`) with no cast or overlapping fields (which stay an error, 3.3); operations through pointers with the result in the first operand. Reason: games need 24/32-bit scores and counters, but operators on them would be loops or long sequences on every use (design rule 1), and two more types would multiply the arithmetic rules (3.1). Kept simple: add, subtract, compare, multiply, divide, decimal and hex output. Named `wide` since it holds both widths.
- **Interrupt handlers start with `cld`** (replaces "no `cld`" in the M6 decisions; spec 6.3), unless they execute no `adc`/`sbc` and no `jsr`. Reason: the `bcd` library puts main in decimal mode, and an interrupt during it would make the handler's arithmetic decimal. 1 byte and 2 cycles per interrupt; rejected: `bcd` blocking interrupts around its decimal code, which leaves asm blocks with `sed` unsafe.
- **Floats later, as a library with its own 4-byte format** (8-bit exponent, 24-bit mantissa, built on `wide`), not the C64 BASIC ROM routines, which are C64-only, use a 5-byte format and need BASIC banked in. Not a priority.

## 2026-11-02: language name

- **"Tern" stays, for now** (spec 14.8). It is short, gives a clean extension and command (`.tern`, `tern`), and the bird fits a small, quick language. Its only known clash is the old TernJS analysis tool. Rejected: "Moss" (MOS Technology), taken by at least four small languages, the `moss` crate, and Stanford's MOSS plagiarism checker, which dominates searches. The item stays open in case a better name comes up.

## 2026-11-02: interrupt functions as callable parts

- **An `interrupt fn` may be called from another `interrupt fn`**, compiled as a part (body and `rts`, no saving, no `rti`); the installed handler saves registers once (spec 6.3). Calling one from non-interrupt code stays an error. Reason: a program combining library interrupt code (multiplexer, scroller) needs one installed handler that calls each module's code; the mark `interrupt fn` says "runs in interrupt context", so the compiler can reject calls from main with a clear message, and no new keyword is needed.
- **Decision 34 extends to parts:** a module's private variable may be shared between main and the handler side when the handler side reaches it through an `interrupt fn` of the variable's own module, root or part. A plain function called from a handler (`math.div8`) is still rule 31. Rejected: plain `fn`s called from the program's handler, which work today but force the module's shared tables to be public, and give a generic error when main calls one by mistake.
- **A part may have register parameters and a return value**, so a multiplexer can report "top of frame" (`-> bool using carry`) and the program's handler decides what else runs there. Such a function cannot be installed. Reason: composing raster code is a scheduling question (who sets the next raster line); the multiplexer schedules, and the program wires the modules together.

## 2026-11-02: module parameters, compile-time assert, C64 graphics library (M11)

- **Module parameters** (spec 2.2): a module declares settable constants in a header, `module sprites(COUNT: u8 = 32, FLICKER: bool = false)`; the main file sets them with `import sprites(COUNT = 40)`. Reason: library sizes and options (multiplexer sprite count, flicker) differ per program; constants cost nothing at runtime, and copying a module to change one number does not scale. **Only the main file may set them**, since a module is loaded once and two settings would conflict; may be loosened if an engine module needs it. A header, not a per-constant modifier (`option const`): one place lists a module's settings, and it is a declaration, so the six-modifier limit (14.7) is not reached.
- **Compile-time `assert cond, "message"`** at the top level (spec 4.1). Reason: a module must reject bad parameter values with its own message, not a confusing error deep inside it; also useful for layout checks (a table fits in a page). Reported at the main file's import when a parameter caused it.
- **M11 is the C64 graphics library**, `sprites` and `scroll` (spec 10.1), as proposed: `COUNT` virtual sprites (default 32); extra sprites on a line skipped, rotating with `FLICKER`; a frame counter for main, no other hook in v1; multicolor and expand-X flags, no expand-Y (it changes the spacing rule); double-buffered scrolling with color RAM fixed or shifted; character maps only, tile maps later. The modules combine through `interrupt fn` parts in the program's handler: the multiplexer schedules the raster lines and reports the top of the frame. Reason for the order: C64-only, so not part of M10, and it benefits from M9's `inline asm fn` and optimizer.
- `module` and `assert` become reserved words.

## 2026-11-02: M9 questions 42 and 43

- **Array indexes keep wrapping** (42; spec 3.2). `a[i + 1]` with `i = 255` is `a[0]`, as `+` wraps everywhere (3.1). The optimizer folds a constant offset into the `abs,x` address only where the result is unchanged: a wrapped index that would be outside the array anyway (`a[i - c]` with `length + c <= 256`, as M9 built), or a sum it can prove does not wrap (a loop counter with constant bounds). No spec rule for the optimizer beyond that. Rejected: indexes computed without wrapping, so `a[i ± c]` always folds. It would give `+` a second meaning inside `[]` (design rule 3) and silently break 256-byte ring buffers (`buf[head + 1]`), for 3 bytes and 4 cycles per access; `stack[sp - 1]` already folds.
- **`while f()` with an empty-body flag function is allowed, with no diagnostic** (43; spec 7). The test after each pass is defined like the first one when the loop body ends with an `asm` block: nothing that changes flags is placed between that block and the test, so a read-until-flag loop works. Reached any other way, the test reads whatever flags the code left. Reason: reading a flag is a deliberate choice for the programmer to make; the compiler should not forbid an unusual pattern because it is unusual. Rejected: an error or warning when the body ends in compiled code.

## 2026-11-02: a developer guide documents machine-level behavior

- **Tern trusts the programmer, and the documentation tells them how things behave** (roadmap, Later). Behaviors such as a wrapping index (`buf[head + 1]` is `buf[0]` when `head = 255`) or a `while` loop testing a flag left by an `asm` block are often exactly what a 6502 programmer wants, so they stay legal with no warning. In exchange, the user documentation (a developer guide, planned) explains each of them with an example. It does not discourage their use. The roadmap keeps the list of behaviors it must cover; the spec session adds to it with each decision that creates one. Reason: rejecting or warning about unusual but deliberate code gets in the programmer's way, but silent behavior that nobody documents is a trap.

## 2026-11-02: M10 questions 44 and 45

- **Spec 10.1 lists the M10 library as built** (44): `wide`, `bcd`, `random`, and the additions to `math`, `text` and `mem`, replacing the "Planned for M10" text. Changes from the plan: `wide` names carry the width (`set32`, `copy32`, `add32_u16`, `dec32z`), since one module holds both widths; added `text.dec8z`/`dec16z`, `bcd.digits`/`bcd.text` and `wide.remainder24`/`remainder32`. `random.below` is not exactly uniform (high byte of `next8() * n`), and `math.divs8` rounds towards zero.
- **An `interrupt fn` has one form, a part or a handler** (45; spec 6.3). Calling one and also installing it is an error, as built. Reason: two forms would need two copies of the body with two labels and two frame layouts, and a program that used both would reach the part from two handlers, which is already an error (8.1). A library that offers a part and a ready handler (M11's `scroll.top()` and `scroll.start()`) writes a one-line handler that calls the part, for one `jsr`/`rts` (12 cycles) per interrupt, only in programs that install it.

## 2026-11-02: memory layout, overlays and binaries without main (M12, M13)

- **The memory layout declaration is settled** (spec 8.4; was 14.12): one `memory` block in the main file. `program` and `zeropage` lines replace the platform's areas of that kind; `reserved`, `region`, `volatile` and `overlay` add to the platform's map. Reason: a binary at `$C000` must be able to say "only here", which pure adjusting cannot express, while a game that only reserves VIC memory should not have to restate the platform's map. Functions are placed whole, never split across areas with a hidden `jmp` (design rule 1). The kinds are words only inside the block.
- **`data` at a fixed address** (spec 8.4; was 14.9), in a `program` or `reserved` area. The program file covers its lowest to its highest used address, gaps filled with zeros. Reason: the classic C64 layout (graphics at a fixed address loaded with the program) needs it, and one load block is what the KERNAL loads.
- **Overlays** (spec 8.5): modules placed in an overlay area with `import m @ area` in the main file, one file per overlay, all compiled in one build. Reason: games load levels and screens from disk; one build keeps the whole call graph, so frame sharing and zero page work unchanged, and overlays in one area can share memory. Calls between overlays of the same area are errors; a resident call into an overlay is a plain `jsr` and having it loaded is the program's responsibility. Rejected: a check or stub on every call into an overlay, which costs every call (design rule 1). An overlay's variables are set by loading its file. Bank switching (14.1) follows the same model.
- **Binaries without `main`** (spec 12.3): `export f, g, ...` in a main file with no `main` builds a binary for another program to call (music player, loader), starting with a jump table: entry 0 is the compiler's `setup`, then the exports in the listed order. Exports take register parameters only. The compiler writes a `name_api.tern` module of fixed-address declarations, with `keeps` from the code and constants for the memory the binary uses. Reason: most of the pieces exist (routines at a fixed address, 6.6); the missing parts are an output without the BASIC line and a way to limit the binary's memory, which the memory block gives.
- **All entries of a binary are one root**, so `init` and `play` may share private state. The host must not call an entry from an interrupt while another runs; documented in the developer guide, not checked. Rejected: one root per entry, which would make the usual player (`init` from main, `play` from an IRQ) an error under rule 31.
- **Order:** M12 is the memory layout and binaries without `main`; M13 is overlays. Reason: binaries are smaller and mostly need the memory block; overlays build on both.
- `export` becomes a reserved word.

## 2026-11-02: M10 review finding 4, `cld` and an asm `jmp` out of a handler

- **An asm `jmp` out of an interrupt handler does not count as a `jsr` for the `cld` rule** (spec 6.3). The handler gets `cld` only when its own code has `adc`, `sbc` or `jsr`, as built. A handler that chains to other code (`jmp $EA31`) and may interrupt decimal-mode code clears the flag in its asm block. Reason: a `jmp` that leaves the handler hands control to code the compiler cannot see, and the existing rule for that case is that the developer's code handles it (it already saves its own registers). The compiler should not guess what the target needs. Documented in the developer guide. Rejected: treating such a `jmp` like a `jsr` (1 byte, 2 cycles in those handlers).

## 2026-11-02: M10 review finding 5, work bytes of the inline `math` helpers

- **Now: `min8`, `max8`, `clamp8` and the signed ones get their own private work byte**, separate from `math.w0`-`w2` (spec 10.1). Reason: these inline helpers are what a handler most wants to use, and sharing a byte with `mul8`/`div8` made `mul8` in main plus `min8` in a handler an error. No cost in bytes or cycles. Calling one helper from both main and a handler stays an error (8.1). Rejected: rewriting them to use the hardware stack and no memory, about twice the cycles at every call to fix a rare case (design rule 1).
- **Later: an inline function's locals go in each caller's frame** (spec 6.2, 14.2), with `var` lines allowed in an `inline asm fn` body. Each copy of the code then uses its caller's memory, so an inline helper with locals can be called from any root at no cost in code or speed. This is how inlining works in C, C++ and Rust (an inlined call's locals belong to that copy), and a first step towards 14.2's per-root copy, which Microchip's XC8 does for functions called from main and interrupt code. Reverses "an inline function keeps its frame" when built. Not scheduled; listed under Later in the roadmap.

## 2026-11-03: code generators with `emit` (planned)

- **A `const fn` may emit assembly** (spec 4.1, planned; roadmap Later, with `const fn`). Generators use all of `const fn`'s control flow, and each `emit { ... }` adds instructions to the asm block that called the generator. Reason: C64 programs commonly need generated "speedcode" (unrolled copies, plotters, sprite clearing) whose shape depends on parameters; a fixed repeat (`unroll`) cannot skip rows or nest conditions. Prior art: KickAssembler's `.for`/`.while`/`.if` scripting, Zig's `comptime`.
- **Emitted code is parsed instructions, not text.** Fragments are parsed when the `const fn` is read, so errors point at source lines, the compiler still computes the finished block's effects, and half an instruction cannot be built. Rejected: string pasting.
- **Generators are called only inside assembly**, so a Tern statement never expands into generated code: asm already means "exactly these instructions", which keeps the cost visible where the call is (design rule 1). Code shapes depend on the generator's logic, so rule 4 is met through the listing, which names the `emit` line and the call for each generated instruction.
- **Labels:** an `emit`'s local labels are its own; labels shared between fragments are compile-time values (`label()`), the one new piece of machinery.
- **No Tern-statement generation, no macros, no textual preprocessor.** Generators, module parameters and `assert` are the compile-time tools. Reason: generated Tern statements would be type checked after expansion, which makes errors hard to follow, and functions (`inline fn`) already cover reuse in Tern code.
- **No `unroll`.** Rejected: a compile-time repeat statement in Tern and asm (as Prog8's `unroll`), built before generators. A generator does everything it does, and two ways to repeat code would be one too many. M11's scroller writes its unrolled copies by hand in asm, as spec 10.1 already says.

## 2026-11-03: Tern's purpose, and a standalone assembler far off

- **Tern is a language first; its assembler serves the mixed use.** A whole program could be written as asm, but that is not the purpose: readable code that compiles to efficient 6502 code, with assembly where a routine needs it (goals 1 and 6). An assembler feature is added when Tern programs mixing in asm need it (generators for speedcode, alignment for tables), not because standalone assemblers have it; macros stay out.
- **A standalone assembler extracted from Tern is a far-off goal**, after the compiler is complete (roadmap, last). Not planned.

## 2026-11-03: braces on their own line

- **A `{` may go on the line after a block header** (spec 2): newlines are skipped where a block must follow (`fn`, `if`, `else`, `while`, `for`, `match`, `struct`, `asm`, `memory`). Reason: a header such as `if x == 0` is never a complete statement, so the line break has no other meaning; many Tern users come from C#, C and assembly, and refusing a common brace style gains nothing in the code. Rejected: one brace style only (as Go), which needs a formatter culture Tern does not have. Unchanged and now written down: `;` is optional at line ends, empty statements are ignored, indentation has no meaning.

## 2026-11-03: a C64 device I/O library (planned)

- **A C64 library for device I/O** through the KERNAL's serial and tape routines (roadmap, Later): whole-file load and save, sequential files, the drive's command channel, KERNAL error status. Reason: programs need to save high scores, settings and levels, and the KERNAL routines are the standard, device-independent way (disk, datasette, printer). Built on routines at a fixed address (6.6), so it needs no language change. Not scheduled; scope and name settled when it is.

## 2026-11-03: M11 and M12 questions 46-54

- **The M11 library is recorded as built** (46, 47; spec 10.1), replacing "Planned for M11": the sprite table is public parallel arrays, not an array of structs, since the asm sort and copy cannot name an array of structs; `sprites` takes `SCREEN`/`SCREEN2` and `scroll` takes `SCREEN_A`/`SCREEN_B`/`COLORS`; `init()` for programs with their own handler; the multiplexer's limits (30 lines between uses of a hardware sprite, y from 24 to 251, groups of up to 5). `COLORS` redraws the color RAM instead of shifting it, and may fall behind the beam with the multiplexer; the limit is stated in the spec, and a beam-racing copy is left for code generators.
- **A module's interrupt-side helpers need not be `interrupt fn`s** (48; spec 8.1): a function of the module called only from the module's own `interrupt fn`s counts as that side for decision 34. Reason: requiring `interrupt fn` on helpers works but is not obvious, and the rule stays safe, since a function reached from main, or from another module's handler, is still rule 31. Rejected: only documenting the workaround.
- **Constant conditions are folded before reachability** (49; spec 2.2): an `if` on a constant compiles only the branch taken, and functions called only from the other branch are not compiled and get no memory. Reason: module parameters (`FLICKER`) are only worth having if an option that is off costs nothing; without it `sprites` carries about 110 bytes and `2 * COUNT` bytes of RAM for `FLICKER`.
- **Several program areas, zero page, interface names** (50, 52, 53, 54; spec 8.4, 12.3), as built: variables as one block in the first area with room after its code; `reserved`/`region`/`overlay` ranges also leave the free zero page; the `_api` constants `CODE_*`, `VARS_*`, `<REGION>_*`, `ZP_*`; a zero-page variable crossing a gap between ranges is an error.
- **A region named like a main-file symbol is an error** (51; spec 8.4), not resolved in favor of the symbol. Reason: `@ name` would otherwise silently mean an address or a region depending on other declarations (design rule 3).
- 48, 49 and 51 are follow-up work at the start of M13. The M10 follow-up (own work bytes for the inline `math` helpers) was already built in M11, so it is removed from the roadmap.

## 2026-11-03: fixed-address variables over an overlay

- **A fixed-address variable may overlap an overlay's file only when it is marked `volatile`** (spec 4, 8.5; changes M13 review finding 3, which made every such overlap an error). Reason: the resident program has real reasons to read or change the memory an overlay loads, such as a level header that every level puts at the same address, but the overlap must be intended, and the source must show it. `volatile` already means "memory that something else changes behind the program's back", and loading an overlay is exactly that, so no new keyword is needed (design rule 3). It also makes the overlap correct: every access goes to memory, and a write counts as a write through a pointer, so the optimizer never keeps the overlay's variable stale in a register. Without `volatile` the error stays, since an accidental overlap points at a byte that moves whenever the overlay changes; the message suggests `volatile`, or naming the overlay's variable.
- Rejected: allowing the overlap silently (the two problems above); a new keyword or attribute for the override (`volatile` already says it); keeping the error with a pointer or asm block as the only way (they work, but cost zero page and slower `(zp),Y` access just to show intent).
- Overlapping the resident program stays an error with or without `volatile`: that memory is always the program's own, and every byte of it has a name.

## 2026-11-03: indexing small linear struct arrays (planned)

- **`list[i].field` on a `linear` array of structs whose whole size is at most 256 bytes** (spec 3.3, planned; roadmap Later). The record's offset goes in Y: shifts when the struct size is 1, 2, 4 or 8, otherwise a compile-time table of `i * sizeof(T)` (`ldx i` / `ldy offsets,x` / `lda list+off,y`). Reason: records that must stay contiguous (for hardware, assembly or pointers) can then be indexed almost as cheaply as the default table-per-field layout: 11 cycles for the first field and 4 for each further one, against 17 to load a pointer from a pointer table and 7 per field through `(zp),Y`, or about 70 to compute `base + i * size` at runtime. It needs no zero-page pointer.
- The code is a fixed short sequence (design rule 1). The offset table is a cost the source does not show, of `n` bytes; the precedent is the `match` jump table, and the listing and report show it. Larger arrays stay an error, since the offset must fit in Y.
- Today's alternatives stay documented: an array of pointers (`[ptr Enemy; n]`, filled at startup) for many fields of one record, and pointer stepping in loops.

## 2026-11-03: fixed data in an overlay (question 58)

- **An overlay module may place `data` at a fixed address inside its own area** (spec 8.4, 8.5; replaces the provisional error). The bytes go in that module's file, and the rest of the module is laid out around them, as fixed data already is in program areas. Reason: a multi-load game loads each level or area with its own code and data, and the resident code needs some of it at known addresses (a header, tables); this is the counterpart of a `volatile` fixed variable over an overlay (decided earlier today), which otherwise has no reliable way to find what it reads. No runtime cost, only assembler layout.
- **An overlay module's fixed data anywhere else is an error** (another overlay area, a program or reserved area), not placed in the main file as the provisional choice did. Reason: data written in a level module that is silently loaded once with the main program, and never with the level, is surprising; resident data belongs in the main file or a resident module. A resident module's fixed data in an overlay area stays an error, since the first load would overwrite it.

## 2026-11-03: plug-ins against a fixed interface (planned)

- **Levels or other parts loaded from files that the game was not built with** (spec 12.3, planned; roadmap Later), e.g. an add-on disk with more levels. They use what already exists: each level is a binary without `main` (12.3), the interface is a hand-written module of routines at fixed addresses (6.6), and the game loads a file and calls the routines. Reason: M13 overlays are compiled with the game, so their number and contents are fixed at build time; a binary works with any number of levels and with code not written in Tern.
- **`export level_api`**: `export` naming a module exports a function for each routine in it, by name and at the routine's address, and checks the parameters, return, `keeps`, and that each of its `volatile` fixed variables (data every level provides) is defined as `data` at that address. Reason: an export line's order is the interface, so swapping two names would silently break a level; the module states the addresses once for every level. No new keyword: `export` still means "these are the entries".
- **`export f, g @ addr` in a program with `main`**: a jump table at a fixed address, so plug-ins can call the game's services across game versions. Changes 12.3's "no `main` next to exports" for this form only. The entries count as an unknown root (6.5).
- No change to generated calls (a `jsr` to a fixed address, as today); the additions are checks and a table. Not scheduled.

## 2026-11-03: M13 questions 55-57

- **An overlay declares no zero-page variables of its own** (55; spec 8.5, as built). Its globals are never placed in zero page, and one that must be there (`@ zp`, a dereferenced pointer) is an error suggesting a local or a fixed address. Reason: loading a file cannot set zero page, and startup never touches an overlay's variables, so such a variable would start as garbage. Its frames use zero page as usual. **Fixed zero-page addresses are allowed** (`var p: ptr u8 @ $FB`): they name memory rather than declare it, the overlay sets them itself, and the main program may name the same bytes to share them. Rejected: zero-page variables set by entry code the compiler adds (hidden code at every entry), and `noinit` zero-page globals (a declared variable the file cannot set). The spec's "share memory (in the area, and in zero page)" is corrected.
- **Every use of a sibling overlay is an error** (56; spec 8.5): besides calls, its global variables and `data`, and `addr()` of its functions or `data`. Reason: the sibling is never loaded at the same time, so the value or address is never valid, and the compiler can see each of these uses. Changes the provisional choice, which did not check `addr()`. Resident code may take addresses in any overlay. Interrupt handlers in an overlay are allowed; installing and removing them is the program's responsibility.
- **`c64.load(name: ptr u8, n: u8, device: u8) -> bool`** (57; spec 8.5, as built): logical file 1, secondary address 1, returns the KERNAL's carry; files named after the module, disk names chosen by the program. The planned device I/O library adds the drive's error channel.

## 2026-11-03: order after M13

- **M14 is the C64 device I/O library, M15 indexing small `linear` struct arrays, M16 an inline function's locals in each caller's frame.** Reason: smallest and most independent first. The I/O library needs no language change and is what games need next (saving scores and settings; `c64.load` moves onto it). The other two are contained compiler changes already designed. Plug-ins and `const fn` with generators are larger and stay in Later.

## 2026-11-04: long byte buffers

- **A `var` array of one-byte elements may be longer than 256** (spec 3.2), with no initial value (zeroed or `noinit`), and like long `data` it is used only through `addr()`; indexing it is an error that suggests a pointer. Reason: regions and reserved memory are mostly for large buffers (maps, decompression space, sound data), and the spec's own region example (`var map: [u8; 2048] @ buffers`) was invalid. It extends the existing long-`data` exception into one rule: an array over 256 bytes is reached through a pointer. Multi-byte elements stay at most 256, since a long split array has no clear pointer.
- **No window syntax.** To reach byte 300 the developer steps a pointer (`addr(map) + 256`, then `p[44]`), which already exists and shows the cost ((zp),Y). Rejected: a constant-offset window (`x[256..]`), new syntax for what a pointer or fixed one-page arrays already do.

## 2026-11-04: constants before the memory block

- **`const` declarations may come between the imports and the `memory` block** (spec 8.4); nothing else may. Reason: the constants a range uses read best directly above it, and the checker already evaluates the block after all constants, so only the parser rule changes, at no cost in the program. Rejected: the block anywhere at the top level, which could put the memory map at the bottom of a long file, below the variables that use its regions.

## 2026-11-05: an analysis of the gap to assembly (M17)

- **After M16, a milestone that measures and explains the gap to hand-written assembly** instead of adding features. The benchmarks and a set of library asm routines rewritten in plain Tern are compared line by line, and every difference is classified as optimizer, how the Tern is written, language, or assembly only. Reason: at 77% of the bytes and 65% of the cycles, the remaining gap is spread over many small causes, and fixing them one by one without a full account risks work on the wrong ones. The aim is to get closer while keeping the language's conveniences, not to match hand-written assembly. Language features found this way still go through the design rules; "how the Tern is written" findings go into the guide.

## 2026-11-05: tools and skills for coding agents (M18)

- **After M17, a milestone for coding agents**: a Tern skill (language rules an agent would not guess, the cost of each construct, M17's advice on writing fast Tern), JSON diagnostics, a listing that links source lines to bytes and cycles, and `tern` subcommands to measure a snippet and to run a program and dump memory. Reason: no model has been trained on Tern, so an agent depends on documentation it is given and on tools that answer exactly; a 6502 target makes exact costs especially worth having. It comes after M17 because the analysis is what the skill's advice on writing fast code is built from. The skill's examples are checked by tests so they cannot drift from the compiler. Relates to the planned language server (Later), which would share the diagnostics format.

## 2026-11-06: llvm-mos in the comparison

- **The llvm-mos C compiler joins the benchmark comparison** at the start of M17, built from the same C sources as cc65 with `-Os`. Reason: it is the 6502 compiler most often used to judge how good compiled 6502 code can be (an LLVM backend with zero-page "imaginary registers" and whole-program static allocation of locals, close to Tern's frames), so it is a stronger comparison than cc65, and its output may show techniques the analysis should consider. Same C sources, so the two C columns differ only by compiler. An external tool like the others; no NuGet dependency, and normal test runs do not need it.

## 2026-11-07: the developer chooses shifts or an offset table for linear struct arrays

- **`linear var` indexes by shifting and needs a struct size of 1, 2, 4 or 8; `indexed var` adds an offset table and works for any size** (spec 3.3, planned for M15; replaces the automatic choice of 2026-11-03). Reason: the table is a memory cost the source did not show (design rule 1), and the trade-off depends on the program: the table is faster even for sizes 4 and 8 (7 cycles to the offset against 9 and 11), while shifts use no memory. Now the declaration states it. The word is `indexed`, a word only before `var`: it names what the developer gets. Rejected: `linear(table)` (new modifier syntax), and `table`, which reads like the default layout the spec calls "one table per field", and is a common variable name.
- **The size error is at a variable index, not at the declaration**, so a `linear` array of 5-byte records stays valid for pointer use and constant indexes. The message suggests `_` padding or `indexed`.
- **`_` fields pad a struct**: unnamed space, as `_` already means "nothing named here" in `for` and `match` (rule 3). Also useful for hardware and file layouts.

## 2026-11-07: order from M17 on, and the order of new platforms

- **M17 analysis, M18 fixes from it, M19 agent tools** (renumbers the agent tools milestone, planned 2026-11-05 as M18). Reason: the skill's advice on writing fast Tern should describe the compiler after the analysis's fixes, not workarounds that are about to become unnecessary; and new platforms should start from the fixed code generator, so fixes are not made twice. M18's scope is set from M17's ranked list. M17 stays after M16, since M14-M16 barely change the benchmarks' code.
- **Then M20 checks in the emulator and signed loop variables** (needs M19's source-to-instruction map), **M21 plug-ins** (already designed), **M22 `const fn` and generators** (one milestone, two "done when" steps), **M23 doc comments and a language server**.
- **New platforms: Atari 8-bit (M24), Apple II (M25), NES (M26), 65C02 and Commander X16 (M27)**, each starting with a spec session. Reason: the developer's priority. The Atari and Apple II need no new language machinery, so they test portability first; the NES needs bank switching (14.1) settled; the 65C02 changes code shapes, so it comes after the plain 6502 platforms.
- Not scheduled: `using` on locals (decided from M17's evidence), floats; the developer guide is spec-session work after M19.

## 2026-11-07: no `using` on local variables

- **`using` on local variables is not planned** (spec 14.6; removed from the roadmap). Reason: the cases where a register-held value pays are already covered (loop counters in X or Y, `for ... using`, kept register parameters, redundant-load removal, the `crc8` shift-and-test fold), and A, X and Y are needed by almost every expression and index, so a local pinned to one would mostly turn ordinary statements into errors; saving and reloading it silently would be hidden cost (rule 1). Code that needs exact register use is written as an `asm` block or an `inline asm fn`. It comes back only if the M17 analysis finds a gap, with numbers, that nothing else closes.

## 2026-11-07: emulator checks indexes only, not arithmetic

- **The built-in emulator reports variable array indexes outside the array in test runs; arithmetic overflow is not checked** (spec 14.4, roadmap M20). Indexes work as in C: the program never checks them, an index past the end is the developer's responsibility, and the optimizer assumes it does not happen (3.2, unchanged); reaching other memory on purpose uses a pointer. The emulator check is a debugging aid for the accidental case, which on a 6502 corrupts a neighbouring variable (often another function's local in a shared frame) with no trap, so the symptom shows up elsewhere. Reason for dropping the overflow check: wrapping is defined for all arithmetic (3.1) and deliberate in counters, hashes and ring buffers, so a check would flag correct code, and marking intended wraps would need new syntax for little gain. Wrapping and unchecked indexes are documented in the developer guide instead, before the first release.

## 2026-11-07: long `data` from a list literal

- **Long `data` of one-byte elements may come from any initializer, a list literal included** (spec 3.2; `docs/reviews/m13-followup.md`, finding 6). The compiler already accepted `data d: [u8; 300] = [ ... ]`; the spec named only strings and `include_bytes`. Reason: `data` is laid into the file as it is, with no startup copy and no extra code, so a list costs nothing more than a string; tables printed by tools (maps, music, lookup tables) would otherwise need a separate binary file. It completes the rule of 2026-11-04: an array over 256 bytes is reached through a pointer, whatever its bytes come from. Unchanged: multi-byte elements stay at most 256, and a long `var` has no initial value (that would need a startup copy loop).

## 2026-11-07: inline functions' variables in each caller's frame (question 61)

- **An inline function has no frame of its own: its parameters, locals and temporaries go in each caller's frame** (spec 6.2, 14.2; built in M16; replaces "an inline function keeps its frame"). Copies in one caller share one set; two callers have two sets. Reason: an inline helper with a local could not be used from main and a handler, although its code is copied into each; with the variables in the caller, as in C, it can, and the `math` helpers (`min8`, `clamp8`, ...) no longer need private work bytes. Cost: each caller's frame grows by the inline function's variables, usually little since frames are shared (8.1). Still errors: a function an inline function calls stays one function, and a function reachable from two roots that calls an inline function with variables has memory of its own (8.1).
- **`var name: type` lines at the start of an `inline asm fn` body** (spec 9): any type, before the first instruction, no initial value (it would add code the block does not show, rule 1), no `@` (the copies would share the address). Not in a plain `asm fn` (it has a frame; declare the variable at top level) or an `asm { }` statement (declare it before the block).

## 2026-11-07: the overflow flag as a return value (planned)

- **`-> bool using overflow` is planned** (spec 6.1; roadmap, Later). A function returns a `bool` in the V flag, as it can already in carry, zero and negative. Uses: `bit addr` copies bit 6 of memory into V, so testing bit 6 of a hardware or status register is `bit / bvc`, with no `and #$40`; and the signed overflow of an `adc`/`sbc` can be tested after an `asm` block. `using` keeps its meaning ("kept in this flag", rule 3), and the cost is one branch (rule 1).
- **Only on `inline asm fn`, `asm fn` and routines at a fixed address, and only as a return.** The 6502 has no instruction that sets V (`clv` only clears it), so a plain `fn` could not compile `return true` to a fixed short sequence; rule 2 leaves it out. For the same reason V cannot be a fixed input (6.6), where the carry is allowed because it has `sec` and `clc`.
- **Not added: flag tests after Tern code** (`if carry`, `if_cs`). Decision 39 stands: the flags after compiled code are unspecified.
- Timing: not urgent, so it goes into the "Later" list and can join any milestone.

## 2026-11-07: a `cpu` library module for flag tests (planned)

- **`cpu.carry()`, `cpu.zero()`, `cpu.negative()`, and `cpu.overflow()` once `using overflow` is built** (spec 10.1; roadmap, Later). Each is an empty-body `inline asm fn` with a flag return (section 7), so `asm { ... }` followed by `if cpu.carry()` compiles to the branch alone. Reason: every program that tests a flag left by an `asm` block declares these same one-line functions itself; the library gives them one name. No language change: they use only `inline asm fn` and flag returns, which exist. Platform independent, since the flags are the CPU's.
- Rejected: `carry` and `zero` as keywords (`if carry { }`). It would add syntax for what a library function already does at no cost.

## 2026-11-07: M18 scope, and folding `a[i + c]` (questions 62 and 63)

- **M18 is the M17 report's class 1 items, in three reviewed parts** (62; roadmap): local code shapes (F2, F1, F4, F6, F8, F10, F19, F21), dataflow in the optimizer (F3, F18, F16, F17, F5), registers across loops and layout (F7/F11/F15, F13, F9, F12). No language change: the report's class 3 is empty, so spec 14.6 (`using` on locals) stays not planned. Reason: grouping by compiler area keeps each review focused, and the order follows savings and dependencies (F3's part in `enemies` needs F4; F18 needs F2 and F3). Every fix gets correctness tests outside the benchmarks, since the risk is optimizer bugs and tuning to nine programs. Deferred: F20 (statement reordering, the riskiest change for 50 cycles) and F14 (needs a value-range analysis; 350 cycles). The class 2 guide advice is written after M18, from what is still true (`bubble.minus` becomes unnecessary with F2).
- **`a[i + c]` folds when the loop counter's bounds prove no wrap** (63 (a); spec 3.2, 11): constant bounds, or a variable end of the counter's type (`for i in 0..last`, `last: u8`, so `i + 1 <= 255`). Folding never changes a result: an index that can wrap, such as a ring buffer's `buf[head + 1]`, keeps the wrapping code. Reason: 57,216 cycles in `bubble`, 35% of the gap, and the proof sits in the loop header, where the developer sees it (rule 1).
- **Deferred: the proof from another access in the body** (63 (b)): `values[i]` read in the same pass implies `i < len`. Sound only when that access runs on every path before the folded one with `i` unchanged, and it makes the cost of one line depend on another (deleting a read slows a different access: rule 4). No benchmark needs it. Reconsider if a real program does.
- **Spec 11 gains three shapes** a programmer relies on: `w += b as u16`, `p += 256` / `w -= 256`, and the folded `a[i + 1]`.

## 2026-11-07: questions 44, 45, 59, 60 and 61

- **One form per `interrupt fn`** (45; spec 6.3): a part or a handler, never both; using both stays an error. Reason: two forms need two labels and two frames for one body, and the workaround (a small handler calling the part) costs one `jsr`/`rts`. A second copy can come with spec 14.2's second copies of ordinary functions.
- **Evaluation order** (60; spec 5): in an assignment to an indexed place the index is evaluated before the value; with a call in the index, and between the parts of an expression, the order is unspecified. Reason: the M15 code generator loads the index first, which avoids saving A in a temporary; a call in the index is rare, and fixing its order would cost code. The order matters only for `volatile` reads and calls with side effects, and a local fixes it. Added to the developer guide's list.
- **`io` as built** (59; spec 10.1): module `io`; addresses `ptr u8`; `save`'s `last` included, like `..=`; `u8` counts for `read`/`write`; `OPEN` named `kernal_open`, `kernal_load` public, no raw `SAVE`; `io.chrout` next to `c64.chrout` (same address). Reason: each matches the rest of the language and library (`addr()` gives a `ptr u8`, `c64.print` counts in `u8`, `..=` includes the end).
- **Spec 3.3 and 11 as built in M15** (60): the shift shape ends in `tax` (the code generator takes X first), the table is `list.offsets`, a linear array of 1-byte structs needs no shift.
- **44 and 61** needed no change: spec 10.1 already had the M10 signatures, and 6.2, 9 and 14.2 already described M16's inline locals.

## 2026-11-08: the schedule after M18, and questions 65-67

- **M19 has two reviewed parts**: the compiler side first (`-> bool using overflow`, the `cpu` module, `tern --json`, the source-to-cycles listing, `tern bench`, `tern run`), then the skill. Before part 2 the spec session writes the "writing fast Tern" advice into `docs/guide.md` from what is still true after M18, and the skill takes it from there. Reason: the skill describes the tools and the advice, so both must exist first; and one place for the advice keeps the guide and the skill from disagreeing.
- **`using overflow` and `cpu` are built at the start of M19** (from "Later"). Reason: both are small and already designed, and with them in, the skill teaches `if cpu.carry()` after an `asm` block from the start instead of every program declaring its own flag functions.
- **The developer guide is spec-session work from M19 on**, extended as later milestones land, and complete before the first release.
- **The first release comes after all five platforms work** (after M28). Reason: the developer's choice; the guide and library are then described once for every platform.
- **Floats are M24, after M23 and before the platforms** (Atari M25, Apple II M26, NES M27, 65C02 and Commander X16 M28; replaces the numbering of 2026-11-07). Reason: the developer's choice. A library on `wide` needs no language change, comes after M22 so `const fn` with `real` can build float constants, and then works on every new platform as it is added. The format and operations are settled in a spec session before coding.
- **Not scheduled, with the reason to wait** (roadmap): F20, F14 and question 63 (b) stay deferred (about 400 cycles together, against M18's remaining gap of 21 bytes / 17,550 cycles, which is mostly advice on writing the source); spec 14.2 and 14.11 are discussed after the first release; the `nmos6502+undoc` variant has no slot yet.
- **A fixed address outside the volatile areas is ordinary memory** (65; spec 4, unchanged in substance): the optimizer may remove a dead or repeated read of `var reg: u8 @ $C000`, but need not, so M18's choice to keep dead loads from fixed addresses is allowed. Hardware the platform table does not list is marked `volatile`; the `MatchTests` case that expects such a read to stay declares its variable `volatile`. Reason: one rule for what is volatile (rule 4), and the keyword exists for this case.
- **Spec 11 states the code as built** (66): `-x` is `lda #0` / `sec` / `sbc x`, shorter than the old row; a store of 0 at the start of `main` into a global zeroed at startup, before any label, branch, call or asm block and before `main` writes it, generates no code (spec 4, noted at the `x = 0` row).
- **In a function shared by two roots, only `using X` / `using Y` guarantees a loop counter is not memory** (67; spec 8.1). Other counters may be put in a register and the function is then accepted, but that depends on the loop body, so the error names the loop and suggests `using`. Reason: nothing that compiles today breaks, the spec does not tie itself to the optimizer's register rules, and the developer has a way to make the function's acceptance predictable. Rejected: requiring `using` (breaks accepted code) and listing the optimizer's rules in the spec (they change with the optimizer).

## 2026-11-08: `indexed match`; F14 replaced, F20 rejected

- **`indexed match` is always a jump table; a plain `match` is always a chain of compares** (spec 5.2, 11; M20). Replaces the automatic choice (a table when the chain would need at least 8 compares). Reason: as with `indexed var` (2026-11-07), the table is a memory cost the source did not show (rule 1), and which code a `match` gets should be read from the source, not from a size rule (rule 4). `indexed` already means "through a table the compiler generates" (3.3), so it gains a second place, not a second meaning (rule 3), and no reserved word is added.
- **`indexed match` has no range check.** Its value indexes a table of arm addresses from the lowest pattern value to the highest; a value in that span with no pattern goes to `_`, or skips the match without `_`; a value outside it is not checked, as an array index is not (3.2), and the M20 emulator check reports it in test runs. Reason: the check is what `interp` pays over the assembly (F14, 350 cycles / 5 bytes), and the only sound ways for the compiler to remove it were a proof from the contents of a data table or from earlier statements (the cost of one line depending on another, rule 4) or a promise such as `_ => unreachable` (an assert-like keyword). Indexing unchecked is already how Tern works; `indexed match x & 15` stays safe when the value is masked.
- Rejected: omitting `_` to mean "no other value occurs" (it already means "other values skip the match", rule 3); `_ => unreachable` (a promise rather than code, and a second way to say what `indexed match` says); F14 as an optimizer proof (above); a new keyword such as `dispatch` (`indexed` already names the idea).
- **Breaking change**: programs whose long `match` got a table now get a chain until they say `indexed match` (`interp`, the raster example). One word per `match`; the M20 coding session updates them.
- **F20 is rejected** (deferred 2026-11-07): moving a statement past another is the riskiest kind of optimizer change, for 50 cycles over the benchmarks.
- **`indexed match` is built in M19 part 1, not M20** (same day). Reason: the skill teaches `match`, so the language it describes should be final before part 2; the emulator's report of a value outside the span stays in M20 with the index check.

## 2026-11-08: "writing fast Tern" advice, and questions 68-71

- **The advice is guide section 7**, ten rules, each measured with `tern bench` on the M19 part 1 compiler: arrays before pointers; through a pointer, index with Y and step by pages; store a result before testing it; leave a loop with `break` or `return`, not a flag; `inline fn` for small functions in a hot loop; `using` on a parameter only where the body needs that register; constant multiplies in `u8` then widened, or a table; `match` against `indexed match`; what not to rewrite (what M18's optimizer already does); how to measure. The guide gives each rule with its benchmark or example and a percentage; exact cycle counts go in the M19 skill, whose examples the tests check. Reason: percentages over named pairs stay true longer than exact counts, and the checked skill catches drift.
- **Register parameters are no longer general advice.** The M17 analysis advised `using` on parameters; after M18, `calls.regs` is slower than `calls` as written (27,353 against 26,965 cycles), because a plain argument is one store straight into the function's variable and a body that compares two parameters must store one anyway. `using` pays when the body needs that register for the value (an index, or once in A).
- **`match` against `indexed match`:** a chain is faster up to about 8 evenly spread values, `indexed match` from about 16 (measured). Advice only; the two keep their fixed meanings (decision of the same day).
- **68:** `src/Tern.Compiler/Tools/` holds the agent tools' code; added to the CLAUDE.md layout.
- **69:** the suggestion in `tern --json` stays split from the one message text, as built: the fix is the first sentence starting with a verb from `Diagnostic.FixVerbs`, now a coding convention in CLAUDE.md. A separate field is reconsidered if the language server (M23) needs one. Reason: one text per error reads the same in the terminal and in JSON, and the convention keeps the split reliable.
- **70:** spec 11's `indexed match` row states the code as built: for an `i8` the lowest value is subtracted as a byte, with no `eor #$80`, and a lowest value of 0 needs no subtract (shorter, the same result). The spec's "planned" notes for `indexed match` and the old automatic-table row are removed, since it is built.
- **71:** `interp` lists `HALT` and has no `_`, as built: an `indexed match` lists every value that can occur (guide 7.8, and the skill).
- **Guide cleanup:** the `match` text, the overlay `volatile` note, the intro's gap to assembly (now about 2% in bytes and 5% in cycles) and the "future" list describe the compiler as built.

## 2026-11-08: skill ownership, operator precedence, skill tests (questions 72-74)

- **72: the coding session owns `.claude/skills/` and `.claude-plugin/`; the spec session owns `.claude/commands/`** (`docs/workflow.md`, CLAUDE.md layout). The skill is built in a milestone, checked by `SkillTests`, and its outputs are rewritten when the code changes, like `tests/benchmarks/`. The guide stays the source of the advice and the skill follows it (decision of 2026-11-08); where they disagree, the coding session asks under "Questions for the spec session". The session commands describe the workflow, which the spec session already owns. Rejected: the spec session owning the skill's text, which would split each skill file between two sessions, since the tests rewrite its outputs.
- **73: operator precedence is spec 3.6, as built** (`Parser.ParseExpr`). Tightest first: postfix (`[]`, `.`, `as`); unary `- + ~ !`; `* / %`; `+ -`; `<< >>`; `&`; `^`; `|`; the compares (not chained); `&&`; `||`. Reason: it is language behavior, so it belongs in the spec, not only in the skill. The bitwise operators above the compares (as in Rust, unlike C) make `flags & 4 != 0` mean what it reads as. The spec states that `-x as i16` is `-(x as i16)`, which follows from "`as` binds tighter than every operator" (3.5) but differs from Rust.
- **74: `SkillTests` gets a CLAUDE.md convention like `BenchmarkTests`**: rewrite the outputs with `TERN_UPDATE_SKILL=1 dotnet test --filter SkillTests` in the same commit as the change that moved them, read the diff and fix prose that no longer matches; listing excerpts are updated by hand.

## 2026-11-08: signed loop variables, and errors for C habits (question 75)

- **Signed loop variables** (spec 5.1, 11; M20). A negative constant bound or a signed bound makes the counter `i8` if the bounds fit in -128..127, `i16` otherwise; a signed bound with an unsigned variable bound is an error, as mixing is everywhere (3.1). With step ±1 the code is the unsigned code, since the counter steps through the same bytes and is tested for equality; only the empty check before a loop with variable bounds is a signed compare (once per loop), and `n..=0` / `n..0` use the sign flag of the `ldx n` as that check. A step other than ±1 with a variable end stops on the V flag (the signed form of the carry rule) and compares against the end flipped with `eor #$80` once before the loop. Reason: signed order costs code only where order is tested, so the cost stays per loop except in the case that already pays per pass for unsigned counters (rule 1, rule 4).
- **Limits:** an `i8` or `i16` counter takes only a constant step, and `using X` / `using Y` is allowed on `i8`, not `i16` (as for `u16`). Reason: rule 2; a signed variable step would need a direction test per pass, and no program has needed one.
- **75: the parser names C habits** (spec 13; M20, first): `n++` / `n--` at the end of a statement or before a closing bracket, a `*` dereference, `%` binary literals and a keyword used as a name each get an error with the Tern spelling. Reason: these are the first mistakes an agent makes, and the generic error points at the wrong token. No language change; `a++b` and `a--b` stay valid (`a + (+b)`, `a - (-b)`), which is why `++` / `--` are recognised only before the end of a statement or a closing bracket.
- **Rejected: `++` / `--`, also as statements only (as in Go), and `inc(x)` / `dec(x)` built-ins.** `x += 1` and `x -= 1` already have guaranteed shapes (spec 11: `inc x`, the 16-bit `inc` / `bne` / `inc`, `inc a,x`, `inx`), so each would be a second spelling with no new code (as `unroll` was, 2026-11-07). In expressions, `++` would also bring back C's evaluation-order bugs (spec 5) and hide a store (rule 1). The question 75 error teaches `+= 1` in one compile.
- **`x += 2` / `x -= 2` on a `u8` in memory is `inc x` / `inc x`** (spec 11; M20, first). 4 bytes against 7 (6 against 9 at an absolute address), the same cycles, and A is left unchanged; measured with `tern bench`. Not on a `volatile` variable, where the value in between would be written to the hardware. Not for `u16` (the add is shorter and faster) or for 3 and more (the add is faster). Reason: today `x += 1; x += 1` is shorter than `x += 2`, and the source should not need tricks for the compiler's best code (rule 4).

## 2026-11-08: M20 provisional choices (questions 76-78), and M21's small items

- **76: the keyword error keeps what was expected**, as built: "Expected function name, found 'step', which is a keyword. Use another name" (spec 13). Reason: saying what kind of name was expected helps more than the shorter message, and the suggestion (`--json`) is the same.
- **77: `x += 2` / `x -= 2` as two `inc` / `dec` applies to any one-byte variable in memory (`u8`, `i8`), not `volatile`** (spec 11). Reason: the bytes wrap the same way for `i8`, and Tern code does not use the flags after an add.
- **78: after a bad array index a test run goes on; after an `indexed match` value outside its span it stops** (spec 14.4). The exit codes (0, 3 when main returned but the check reported, 2 when main did not return) are tool behavior, documented in `docs/guide.md` 7.10, not the spec. Reason: going on lets one run report every bad access, as the compiler reports every error; after the bad `match` jump nothing meaningful runs. Rejected: stopping at the first bad index (simpler, but one bug per run).
- **M21 starts with three small items from M20** (roadmap): `tern run --set addr=value` for inputs such as the joystick, `tern bench` printing index errors, and `inx` / `inx` (`dey` / `dey`, ...) for `+= 2` / `-= 2` on a variable held in X or Y.

## 2026-11-08: addresses of variables and data in initial values

- **`addr()` of a global, `data`, a field of a struct global or a function may be used in the initial value of a global or `data`**, alone, plus or minus a constant, or as `.lo` / `.hi` (spec 4, 6.5; roadmap M21, first). The compiler fills it in when the program is placed, as it has done for `addr(f)` since M6 (decision 26). Reason: found by the Galaxy Stars port (Prog8 allows both). The M2 restriction ("addresses are known only after compiling") stopped being true when function addresses got a fill-in step, and the error gave that reason anyway. Tables of pointers to screen rows, strings or level data otherwise need a fill loop at startup, which costs code and time that a hand-written assembly table does not (rules 1 and 2: the table is data, with no code). The constant offset covers row tables and the `rts` jump-table idiom (`addr(f) - 1`); `.lo` / `.hi` cover split tables of address halves.
- **Still errors:** an address combined with anything but a constant offset, and addresses in a sibling overlay (8.5). The new messages say what is allowed instead of the old reason.

## 2026-11-08: Galaxy Stars port, first gaps

From `galaxy-star/tern/gaps.md`, steps 1 and 2. All are in M21's "First, small" list.

- **Constant expressions are computed exactly** (gaps 4, 5, 8, 9; spec 3.1, 3.4). An expression of literals and constants, typed or not, is evaluated as a compile-time `int` and takes its type from where it is used, like a literal; only the final value is checked. A typed constant mixed with a runtime value keeps its type. `~` and negative values take the destination's width. Reason: one `u8` operand made the whole constant expression `u8`, so `320 - BOX` failed in a `u16` constant, `400 / 50` needed `as u8`, a constant sum did not convert to a pointer, and the error named the literal, not the operand that set the type. No code changes (rule 1); spec 3.5 already says compile-time values are checked. Changes behavior: a constant expression that wrapped (`BOX + 250` as a `u8`) is now an error; `as` keeps the low bits on purpose. Prior art: Go's untyped constants. For runtime expressions, a type error names the operand that set the type.
- **`c64.set_kernal_irq(handler)`** (gap 1; spec 10.1): installs an `interrupt fn` through `$0314` with the KERNAL in, through a library stub that pulls the KERNAL's saved registers and jumps to the handler. Reason: with the KERNAL ROM in, `$0314` is the only way in, so a program using `io` (M14) could not have a raster interrupt. No language change: the stack then looks as for `$FFFE`, so `interrupt fn` is unchanged. Limit: the KERNAL's keyboard scan and clock stop. Rejected: a handler kind that ends in `jmp $EA31` (a new modifier for one platform's ROM, rule 3).
- **`c64.sprite_xy: [u8; 16] @ $D000`** (gap 3; spec 10.1), beside `vic`. Reason: the named fields cannot be indexed, and struct fields cannot overlap (3.3); fixed-address variables may.
- **`[T; _]` takes the length from the list** (gap 10; spec 4), for `data` and `var` globals with a list. Reason: tables and test scripts change length often. `_` already means "not given" (loop variables, `match`, struct gaps), so rule 3 holds. Rejected: `[u8]`, which reads as a slice type Tern does not have. A written length is still checked.
- **Unrolling stays with M22's generators** (gap 2); `unroll` stays rejected (2026-11-07). The hand-written Tern copy compiles to the same 32 `lda` / `sta` pairs. Evidence for M22: this copy is Tern statements, and generators emit only asm.
- **The guide says when a handler gets `cld`** (gap 6; guide 3.8); the skill follows the guide.

## 2026-11-09: the small items become the M20 follow-up

- **The "First, small" items scheduled at the start of M21 (decisions of 2026-11-08) are their own milestone, the M20 follow-up** (roadmap), built before M21. Reason: with eight items they are about one coding session, the Galaxy Stars port is waiting on them (the constant rule touches most of its remaining modules), and `/code` takes the next milestone whole, so a list inside M21 could start the plug-in work too.

## 2026-11-09: a typed constant next to a wider value (question 79)

- **Next to a runtime value, a typed constant helps choose the operation's type, as a variable of its type would; the constant part is then computed exactly at the operation's width** (spec 3.1, guide 3.2). With `const M: u8 = $0F` and `w: u16`, `w & ~M` is `w & $FFF0`, as `w & ~$0F` is; `w + BOX * 30` adds 390. A compound assignment is checked as the operation it stands for (`w &= ~M` is `w = w & ~M`). Reason: as built, the constant's own type was applied before widening, so `w & ~M` cleared `w`'s high byte (`$0030` from `$1234`) while `w &= ~M` and `w = ~M` used `$FFF0`, and writing `: u8` on a constant changed what an expression computed though the value was the same. A programmer reads `w & ~M` as "clear M's bits in w". The written type still matters where a variable's would: `x + BOX` with `x: u8` is `u8`, and signed and unsigned do not mix.
- Differs from runtime on purpose: with `var m: u8`, `w & ~m` widens the `u8` result `$F0` to `$00F0`, since a runtime `~` has its operand's width. Constants already differ from runtime values by being exact (decision "Galaxy Stars port, first gaps").
- Done in the M20 follow-up branch before its review (roadmap). The skill's `language.md` follows the guide.

## 2026-11-09: untyped constants stay untyped (question 81); a guide section on constants

- **A constant has a type only when its declaration writes one** (spec 3.1). `const AREA = BOX * 30` with `const BOX: u8 = 13` is the plain number 390, used like the literal; it gets a runtime type only where code needs one (where it goes, the operand next to it, or the smallest type that holds it). Reason: as built, `AREA` inherited `u8`, so `c + AREA` with `c: u16` and `var v = AREA` failed with "390 does not fit in u8", which brought back the failures exact constants were meant to remove (gaps 4 and 5). A type that is written is visible where the constant is declared; an inherited one is not. Prior art: Go's untyped constants. Rejected: inheriting the type, as built.
- **Spec 3.1 states the type of a literal where nothing gives one**: the smallest of `u8`, `u16` (or `i8`, `i16` for a negative value) that holds it. This is what the compiler already does.
- **The guide has a section on constants** (3.3, "Constants"): exact arithmetic between constants, the three places a constant gets a runtime type, what a written type adds, and `~` at the width it is used, with the one case where a constant and a variable of the same type differ (`w & ~m`). Reason: the rules are powerful but surprising without a description in one place.

## 2026-11-09: a typed constant on its own fits like a literal (question 80)

- **As built: a typed constant assigned, passed or stored on its own takes the destination's type, like a literal, and its value must fit** (spec 3.1): with `const BOX: u8 = 13`, `var s: i8 = BOX` and `f(BOX)` for an `i16` parameter compile. Its written type applies next to a runtime value (question 79), where nothing gives a type (`var v = BOX` is a `u8`), and to a `for` bound. Reason: it follows from the decision that a constant expression, typed or not, takes its type from where it is used; a lone constant is such an expression, and the value is still checked, so nothing is lost. Rejected: Go's rule that a typed constant converts only with a cast, which would bring back `as` on constants that fit.

## 2026-11-09: `tern run --set` (question 82); `.lo` / `.hi` on `addr()` (question 83, second part)

- **82: `--set` takes addresses only**, as built (guide 7.10). Reason: the startup code zeroes and sets the program's globals after the load, so `--set score=5` would be overwritten and seem to work while doing nothing. The option is for inputs such as the joystick and I/O registers, which have addresses. Tool behavior, so in the guide, not the spec (as for question 78).
- **83: `.lo` / `.hi` directly on `addr(...)` give the address bytes, in code as well as in initial values** (spec 3.4). As built they worked only in initial values, so the same text was accepted in a table and rejected in a statement. It is the assembler's `#<label` / `#>label`, with no code beyond the load. On `addr(...)` they always mean the address bytes, even for a struct with a field named `lo` or `hi`: writing `.field` on `addr(s)` to reach a field through the pointer would be odd anyway, since `s.field` says it directly. Rejected: requiring `(addr(map) as u16).lo` everywhere (consistent with 3.4's rule for pointers, but wordy in the address-half tables the feature was made for).

## 2026-11-09: a pointer converts to `u16` where a `u16` is expected (from question 83)

- **A pointer converts to `u16` by itself in an assignment, an initial value or table element, an argument and a return value** (spec 3.4, 3.5, 4). No code: the 16 bits are the address. Reason: the conversion loses nothing but the element type, like widening (3.5's other automatic conversions), and pointer arithmetic in Tern is in bytes, so a pointer and a `u16` with the same address behave the same under `+` and `-`. The decision of 2026-11-01 required `as` only from `u16` to a pointer, which stays: turning any number into something the program reads and writes through should be written. Found with question 83: `[addr(a), addr(b)]` in a `[u16; n]` table needed `as u16` on each element.
- **Inside an operator a pointer still needs `as`**: `w + p` and `p == w` are errors. Reason: `p + w` is pointer arithmetic giving a pointer, so a conversion in operators would make `w + p` a `u16` and let operand order change the type; adding two addresses is usually a bug. Rejected: converting everywhere, like widening.
- Cost: swapped arguments `f(p, n)` for `(n: u16, dst: ptr u8)` are caught only on the `u16`-into-pointer side.
- **83, first part, settled with it:** spec 4 lists `as` to another pointer type or to `u16` among the forms of an address in an initial value; a `[u16; n]` table needs no `as`.

## 2026-11-09: `.lo` / `.hi` on an address with an offset (question 84); a number before a pointer (question 85)

- **84: `.lo` / `.hi` work in code on every address form an initial value allows**: `addr(...)` alone or plus or minus a constant (spec 3.4). `(addr(map) + 299).hi` is `lda #>(map+299)`. Reason: the decision on question 83 was that the same text should be accepted in a table and in a statement; covering only `addr(...)` itself left the offset form split. The address is still filled in when the program is placed, so there is no code beyond the load. A pointer variable's `p.hi` stays an error, as `p.name` is a field.
- **85: pointer arithmetic is written with the pointer first; a runtime number before a pointer is an error**, as built (spec 3.4). A constant may come first (`2 + addr(map)`), since it takes its type from where it is used. Reason: one way to write it, and it follows the rule of question 83 that operand order must not change a result's type (`w + p` is an error); accepting `n + p` for a `u8` but not for a `u16` would be harder to explain. The error names the operands as written ("Write 'p + n'"), as other errors do since M20. Rejected: `n + p` as in C.

## 2026-11-09: spec cleanup after a consistency review

- **Text fixes, no change in behavior** (spec 3, 3.2, 3.3, 3.5, 3.6, 4, 5.2, 6.1, 6.4, 6.5, 8.5, 10, 10.1, 13.1). A review of the spec against itself found places where it contradicted itself or described the compiler as it was before M19: the array type table now mentions long byte arrays; `len()` is a constant with no type, as 3.1 says of constants; a struct is copied with `mem.copy`, not a `copy()` that does not exist; `as` keeps the low bits of a constant as well as of a runtime value; locals without an initial value include structs; the operator notes that sat under `match` moved to 3.6, with "assignment is a statement"; `using overflow`, `cpu` and the optimizer are no longer "planned"; the `c64` table lists `load`, `kernal_off`, `set_irq`, `set_nmi`, `interrupts_on` and `interrupts_off`; the built-ins table lists `include_bytes`.
- **Calls through pointers (`fn() targets [...]`) are marked planned and not scheduled** (spec 6.5). The text read as a built feature and had a section number of its own that repeated 6.5. Whether to build it is open; `indexed match` and `addr(f)` tables cover dispatch for now.
- **The fixed-data example in 8.5 no longer uses a struct literal**, which 3.3 rules out. Struct literals in `data` are a candidate feature, discussed separately.

## 2026-11-09: struct literals in initial values

- **A global struct, a global array of structs and `data` may be initialized with struct literals**: `Line { row = 1, col = 14, text = addr(TITLE) }` (spec 3.3; replaces "no struct literals" and "`data` cannot hold structs" of the M4/M5 decisions). Fields not listed are 0; values are what an initial value of the field's type allows (4), so `addr()` forms work and records can link to each other. Reason: tables of records (level headers, screen text lines, enemy types) were written as one array per field, kept in step by hand; the Galaxy Stars port has several such groups (`pages`, `title`, `score`). An array of structs is already stored one table per field, so a list of literals fills those tables at compile time: the same bytes, with each record on one line. No code (rule 1), no runtime struct values.
- **The struct's name is always written**, also where the destination gives the type. Reason: a long table reads line by line, and a literal in the wrong table is an error. Rejected: `{ hp = 3 }` with the type taken from the destination.
- **`field = value`, not `field: value`**: `:` means "has type" (rule 3), and `import m(P = value)` already names values with `=`.
- **Unlisted fields are 0**, which costs nothing, since a global with no initial value is zeroed anyway. Rejected: requiring every field (noisy in long tables).
- **Only in initial values.** A struct literal in code, or as a local's initial value, is an error: it would be runtime stores. Rejected for now: a statement form for setting several fields at once (`player.{ x = 10, y = 20 }`, field stores in source order); it adds readability, not code quality, and was not wanted.

## 2026-11-09: open points from the consistency review

From the review that also gave the spec cleanup of the same day. Most state what the compiler already does.

- **Constant arithmetic is signed 64-bit; leaving the range is an error** (spec 3.1, 4.1; replaces the `int` "with no size limit" of 4.1). `/` rounds toward zero and `%` takes the left operand's sign (as built, and as `math.divs8`); `>>` rounds down, as at runtime. A constant shift count is 0 to 63. Reason: the compiler computes in 64 bits and wrapped silently past them (`4000000000 * 4000000000 * 4 / 10^18` gave 8), which broke "computed exactly"; no 6502 program needs larger constants, so an error keeps the rule true without unbounded numbers.
- **A runtime shift count must be less than the type's width** (spec 3.1), as built: `x << 8` on a `u8` is an error, since it is almost always a mistake.
- **When `main` returns, the program stops in an endless loop** with interrupts running (spec 12.2), as built. It cannot return to BASIC, whose zero page it used.
- **Scope** (spec 4), as built: locals are visible to the end of their block; blocks that do not contain each other may reuse a name; a local never hides a parameter, an enclosing local or a top-level name of its module; top-level declarations may be used before they are declared, except constants in a struct's `@` offsets, which must come above the struct. Reason: a name means one thing wherever it can be seen, which keeps code readable. **New: `const` inside a function**, scoped to its block, so a constant can stay next to its only use.
- **`bool`** (spec 3), as built: `==` and `!=` on bools, `bool` arrays and fields (one byte, 0 or 1); the condition of `if` and `while` must be a `bool`.
- **A string literal is only an initial value** (spec 2.1), as built: in code or as an argument it is an error, now with a fix sentence. Reason: an unnamed table would be memory the source does not show (rule 1).
- **`\n` is an error in an encoding with no line break** (`screen`, `screen_mixed`; spec 2.1), as built.
- **Spec 11 gains rows** for signed and 16-bit compares, `&&`, `||`, a compare stored in a `bool`, `i8 >> 1` (as built), and 16-bit shifts by 8 to 15, which are to move a byte first instead of shifting eight times (today 32 bytes and 80 cycles for `w << 8`).
- **Order:** the coding items (struct literals, 64-bit constants, local `const`, the string error's fix, the byte-move shifts) are a small milestone, the M20 follow-up 2, before M21 (roadmap). Reason: the Galaxy Stars port would use struct literals before it reaches `pages`, `title` and `score`, and `/code` takes a milestone whole, so a list inside M21 would start the plug-in work too.

## 2026-11-09: section D of the consistency review

- **`@ align(n)` is planned, not scheduled** (roadmap, Not scheduled). It fits `@` (a location) and costs no code, but fixed addresses in a `reserved` area cover today's cases, and the Galaxy Stars source has no aligned data. Built when a program needs alignment that a fixed address handles badly.
- **Unchanged, each waiting for a program that needs it:** calls through function pointers (spec 6.5, planned), second copies of functions used from two roots (14.2, after the first release), bank switching (14.1, with the NES), leaving nested loops, 16-bit `match`, a function body at a fixed address, and `muls16` / `divs16` (added to the library when a program needs them, as decided for M10).

## 2026-11-09: separators in struct literals (question 86); the size of a number literal (question 87)

- **86: struct literal fields may be separated by `,` or a newline, with an optional trailing `,`; list elements are separated by `,` only**, as built (spec 2, 3.3). Reason: both follow spec 2's existing rule. Inside `{ }` a newline separates (statements, struct declaration fields, and now literal fields, so a literal reads like its declaration); inside `( )` and `[ ]` a newline means nothing, which is what lets tables span lines. Letting a newline separate list elements would break that rule where it hurts most: in a table of signed numbers, `10` / `-5` on two lines would be either two elements or `10 - 5`. A missing `,` between list elements has its own error with a fix. Rejected: newlines between list elements (the review's "one rule for both levels"; the rule follows the bracket, not the level); commas required in literals (unlike struct declarations).
- **87: a number literal may be up to 2^63 - 1** (`$7FFF_FFFF_FFFF_FFFF`; spec 2.1), the range of constant arithmetic (3.1) and of `int` in `const fn` (4.1). The literal has no sign, so the lowest value is `-9223372036854775807 - 1`; a hex literal with the top bit set is an error, and `~0` gives all bits set. The error says "Use a value up to $7FFFFFFFFFFFFFFF (9223372036854775807)". Reason: one range for compile-time numbers; the lexer's 32-bit limit had no reason, and made 64-bit edges be built with shifts. Rejected: stating the 32-bit limit in the spec, a second range to remember. Built on the M20 follow-up 2 branch before the merge (roadmap).

## 2026-11-09: Galaxy Stars port, remaining gaps

From `galaxy-star/tern/gaps.md`, steps 3 to 5 (gaps 11 to 25) and the leftover of gap 1. The port is complete and matches the Prog8 game frame by frame; each gap was checked against the spec.

- **Evaluation order: calls and `volatile` reads in one statement happen left to right** (gap 12; spec 5; replaces the "unspecified between the parts of an expression" of decision 60, and its "with a call in the index"). Includes arguments (`f(g(), h())`) and a call in an index (`a[f()] = g()`). Reason: `(rand_below(K) + 1) << 2 | rand_below(4)` ran the right call first, so the port's specks silently differed from Prog8's for the same seed; only the frame-by-frame comparison found it. Source order is what Prog8, Java and C# do and what a reader expects (rule 4). Cost: none unless two parts have side effects; then nothing for `+ & | ^`, and about one `sta`/`lda` (4 bytes, 6 cycles) for `-` or a compare, where the right side can no longer be computed first. Programs without such statements compile to the same bytes.
- **Still unspecified: a variable that a call in the same statement changes** (`seed + next()` where `next` changes `seed`). Guaranteeing the old value would mean saving the variable before every such call, and writes through pointers cannot be seen; the guide and the skill say to use a local. Rejected: a full left-to-right rule (code in the common case, or holes); documentation only (the trap stays for the common case of two calls).
- **Constants across modules resolve on demand** (gaps 11, 17; spec 2.2, 4). `const STAR_KINDS: u8 = len(star_char)` and constants used across an import ring (`video` → `stars` → `explode` → `video`) work; only a constant that depends on itself is an error. Reason: the spec already said declarations may be used in any order and modules may import each other; the "declared after" error came from the order the compiler declares modules in, and pushed the port to split modules Prog8 did not need.
- **`var` lines in a plain `asm fn`** (gap 15; spec 9; replaces "not in a plain `asm fn`" of 2026-11-07). Same rules as in an `inline asm fn`, placed in the function's frame. Reason: the earlier answer, a top-level variable, costs memory that is never shared and needs module-wide names that may not clash with any local (the port renamed `scr` to `tile_scr`). The frame is where a function's work bytes belong. An `asm { }` statement still has none.
- **`[u8; _]` from a string** (gap 24; spec 4), as `data` already does.
- **`step` is a keyword only right after a `for` range** (gap 18; spec 13). Reason: the port hit it four times (`explode.step`, `sound.step`, two locals). A name cannot continue a finished expression, so `for i in 0..step step 2` is unambiguous. Rule 3 holds: as a keyword it has one meaning. Other keywords stay reserved; the same treatment is for a later port that hits one.
- **A line ending with a binary operator continues** (gap 19; spec 2), as built and now written down; a line starting with one is an error that says to move the operator to the end of the line before. Rejected: continuing before a leading operator, since `-1 =>` starts a new `match` arm.
- **Spec 11 rows:** `p = q + e` with a `u8` expression `e` is an 8-bit add with the carry into the high byte (gap 13; today a 16-bit temporary, 35 bytes and 56 cycles against 25 and 39), and `s < 0` / `s >= 0` on a signed value is `bpl` / `bmi` (gap 14; today a full signed compare).
- **The main file cannot be imported**, as built (gap 23; spec 2.2), with an error that says so and to move the shared code to a module. Reason: the main file has rules no module has (its globals are always kept, the `memory` block, module parameters, overlays); a game-flow module is a clean split. Today's message ("'main' is a module") is wrong.
- **`c64.cia_irqs_off()`** (gap 1's leftover; spec 10.1): turns off both CIAs' interrupts and clears pending ones, so `set_kernal_irq` setup needs no dummy variable to read `icr`. Rejected: expression statements or `_ = expr`, for one use.
- **Not gaps:** 2 (unrolling: M22's generators), 20 (decimal mode stays assembly; `bcd` covers the general case), 22 (an untyped constant that does not fit the other operand stays an error: widening by value would make an expression's type depend on a constant's value, rule 4; the error names the fix), 16 and 21 (no flag expressions, decision 39; the optimizer ideas in them, a carry known clear and repeated reloads, are optimizer work).
- **The port's slower plain-Tern routines become a measurement set, not scheduled** (gaps 16, 21, 25; roadmap). `draw_tile`, `explode.hits` with `explosion_hit`, `hostiles.draw` and `title.sweep_all` were 10 to 50% slower than their asm. Measured and classified as in M17 after M21, so optimizer work targets what a real game needs.
- **Order:** all coding items are the M20 follow-up 3, before M21 (roadmap). Reason: about one coding session, and the port drops its workarounds before the plug-in work.

## 2026-11-09: constants in struct field offsets (question 88)

- **A struct field's `@` offset may use a constant declared anywhere**, as built (spec 4; replaces "except constants in a struct's `@` offsets, which must come above the struct" of the scope decision above). `struct S { a: u8, b: u8 @ OFF }` with `const OFF = 4` below it compiles. Reason: the rule existed only because constants were resolved in declaration order; since the M20 follow-up 3 they resolve on demand, so the exception has no reason left, and one ordering rule for every top-level declaration is simpler to remember. A constant that depends on itself is still the error that names the chain. Rejected: enforcing the old rule, an extra check that rejects code that works.

## 2026-11-09: constant parts of a pointer sum (Galaxy Stars gap 26)

- **In a 16-bit or pointer sum, the constant parts are added together at compile time wherever they are written** (spec 11, new row `p = q + e + K`; planned, not scheduled). The `u8` part keeps the 8-bit add of the `p = q + e` row; a combined constant with a low byte of 0 goes into the high-byte add (`adc #>K` in place of `adc #0`), otherwise it is added once at the end. Reason: `stars.set_color`'s `tile_row_addr[r] + (c << 1) + (COLOR_RAM - SCREEN)` adds `$1800` in a second 16-bit add (31 bytes, 44 cycles), and with the constant written first `c << 1` goes through a 16-bit temporary (37, 54): the order of the terms changed the code, against rule 4. The result is exact, since 16-bit adds wrap at 65536, and the left-to-right rule (decision of 2026-11-09, spec 5) covers only calls and `volatile` reads.
- **Order:** not before M21 (roadmap, Not scheduled). Reason: the port's two-statement form (`p = rows[r] + (c << 1)`, then `p += COLOR_RAM - SCREEN`, 21 bytes and 30 cycles) is plain Tern and faster than either one-statement form today, so nothing waits on it; it goes with the optimizer work on the port's measurement set. Rejected: an M20 follow-up 4 for one code shape.

## 2026-11-09: the build's target decides the library; no name clash with your own modules

- **Imports stay as they are: the target platform decides which library modules exist** (spec 2.2). `import io` loads the target's `io` (`lib/c64/io.tern` in a C64 build, `lib/apple2/io.tern` in an Apple II one), so Apple II, Atari and X16 `io` modules never compete: every build names its platform. Reason: a build is always for one platform, so a platform name in each import would repeat what the build already says.
- **New: a program module with a library module's name is an error** that names both files and says to rename the program's own. Reason: the lookup searches the library before the importing file's folder, so a program's `io.tern` or `random.tern` was silently hidden by the library's; with the error, each `import name` means one module. A main file may keep a library module's name (it is never imported, M20 follow-up 3).
- Rejected: platform-qualified imports (`import c64.sprites`, `import c64.io`, discussed the same day). They would show in the source which machine a program is for, but the build already decides that, and they would change every program's imports; the remaining ambiguity, a program file hidden by the library, is removed by the error above.
- **Order:** the error is a small item at the start of M21 (roadmap).

## 2026-11-09: quoted import paths and aliases (planned, not scheduled)

- **`import "levels/level1.tern"`**: a program's own module by its path, relative to the importing file, always with `/`; no absolute paths (spec 2.2, planned). Reason: subfolders for a larger program (levels, graphics). An absolute path breaks when the project moves to another machine.
- **`import my_io = "./io.tern"`**: an alias, the name the module is used by in code. Reason: keeps a program's own module next to a library module of the same name, where renaming the file is not wanted. Not `import ... as my_io`: `as` is the conversion keyword, and rule 3 gives it one meaning.
- **To settle when scheduled:** whether the quoted form becomes the only way to import a program's own modules (one way to write it, but every multi-file program changes) or sits next to `import name`; for an alias, unique internal names for two modules of one name in labels, the listing and the report, and how an aliased module is named in the `memory` block's overlay placement, by module parameters and by its `module` header.
- **Order:** not scheduled (roadmap). Reason: no program needs either yet; the name-clash error (same day) removes the ambiguity, and renaming a file does what an alias would. Paths come first, when a program wants subfolders; an alias when a clash cannot be solved by renaming, such as two modules from other people.

## 2026-11-10: M21 questions 89-93

- **89: an interface module must declare `setup`**, taking and returning nothing, at entry 0 (spec 12.3), as built. Reason: entry 0 is the binary's startup code (zeroing globals, copying initial values); a host that cannot name it can skip it and run a level with uninitialized variables. Rejected: allowing an interface without `setup`, which saves the host one `jsr`.
- **90: as built.** Gaps between routines are allowed and filled with zeros, so a call into one stops at a `brk`; routines less than 3 bytes apart are an error (one `jmp`); a routine with a fixed input is an error, since a function cannot receive one; the module's other declarations are not checked. Reason: the checks cover exactly what a level must match; constants, structs and functions with a body are the interface's helpers, not entries. The gap behavior is now in the spec, since a host may rely on it.
- **91: as built.** `export f, g @ addr` puts its table in a `program` or `reserved` area like fixed `data`; several `export` lines form one table in line order, one of them with the address; `name_api.tern` holds the entries and their `keeps` but no memory constants; `setup` may be an export's name, `main` may not. Reason: one placement rule for fixed bytes; long lists can be split while the order stays visible; the plug-in's memory is its slot (the interface module), not the host's ranges; `main` is the program's root, and a plug-in calling it would restart the game.
- **92: a specific error, no alias.** A binary that names the interface's variable (`level_api.header`) where it defines its own `data header` at that address gets "'level_api.header' is this binary's data 'header'. Use 'header'"; other overlaps keep the general error. Reason: the general message's fix ("Move one of them") does not fit; letting the interface's name stand for the binary's data would give the same bytes two names (rule 3). Built as a small item at the start of M22 (roadmap).
- **93: spec 12.3 says a `volatile` variable of the module needs `data`** (a plain fixed variable is the host's), matching the decision of 2026-11-03 and the build; the spec's wording was looser than the decision. The guide notes that an export table far from the code makes the program file cover the gap.
- **Spec 12.3's plug-ins are recorded as built** (M21), no longer planned.

## 2026-11-10: `const fn` details for M22

Spec 4.1 left these open; settled before M22's coding session.

- **Arrays are values of fixed length** (`[0; n]`, a `for` list, `len(t)`), and a `const fn` may return one. A table takes it with a checked length or `[T; _]`: `data sine: [u8; _] = tables.sine(256)`. Reason: `[T; _]` already means "length from the value" (gap 10), and the element type must be written, since an `int` array has no byte width; the spec's untyped `data sine = tables.sine(256)` is replaced. Growable lists are left out: a generator knows how many entries it makes. `data x = include_bytes(...)` keeps its untyped form, since a file is bytes.
- **A `real` never becomes an `int` by itself**: `round` (halves away from zero), `floor`, `ceil`, `trunc`. An `int` becomes a `real` freely. Reason: in sine tables the rounding shows directly in the bytes; implicit truncation biases the negative half by half a step. `as` keeps its one meaning (rule 3).
- **Only compile-time types in a `const fn`**: `int`, `real`, `bool`, arrays of them, `label`. Runtime types are errors; arguments widen. Reason: one integer type that never wraps, with no `as` inside generators; 8-bit behavior is written as `& $FF`, where it shows. Strings and structs are left out until a generator needs them.
- **`[expr for i in range]`** in `const`, `data`, `var` initial values and `const fn`, with a `for` loop's range forms; one `for`, no filter, no nesting; an error in runtime code (rule 1). Reason: one short form for simple tables, and a `const fn` with ordinary loops for anything more, rather than two ways.
- **Step limit: 10,000,000 per top-level evaluation**, no setting. Reason: a 64 KB table needs well under 1% of it, and a runaway loop fails in about half a second; a setting waits for a generator that needs one.
- **`real` is a 64-bit double**, with `.`/exponent literals only in compile-time code, the usual math functions, `PI` and `TAU`; division by zero and `sqrt`/`log` of a negative number are errors. Reason: results match C# exactly in tests, and 53 bits are enough for M24's float constants.
- **A `const fn` reads constants, module parameters, `sizeof`, field offsets and `data` tables** (including `include_bytes`), but not `var`, `volatile` or fixed data over an overlay, nor `addr()`, which is decided after evaluation. Reason: everything known at compile time may be read, so one table can be built from another or from a file; an `emit` operand can still name an address, since it is assembled after placement.
- **No built-in for a `real`'s bits** in M22: `floor`, `log2` and `pow` split it. Reason: a built-in would tie the language to one float layout before M24 chooses its format; M24's spec session may add one.

## 2026-11-10: the Commodore 128 as a later platform

- **The C128 in native mode is a planned platform, M29, after the first release** (roadmap), not part of the release's five platforms. Reason: C64 programs already run on a C128 in its C64 mode, so native mode adds 2 MHz, 80 columns, 128 KB and the extra keys, and is wanted eventually rather than now. It reuses most of the C64 work (same CPU instructions, VIC, SID, CIAs, KERNAL entries), so it is a small platform when its turn comes.
- **Open for its spec session:** how a C128 build reaches the C64 library modules (a C128 build searching `lib/c128/`, then `lib/c64/`, recommended; a shared folder would change every C64 program's imports, against the decision of 2026-11-09), the free zero page, the BASIC 7 stub, and whether RAM bank 1 waits for a follow-up after bank switching (spec 14.1).


## 2026-11-11: M22 questions 94-97: real constants, `offset()`, recursion in a `const fn`, `repeat()`

- **94: a `const` may hold a `real`** (`const SCALE = 1.5`), untyped and used only in compile-time code; in runtime code it is the error a `real` literal gets. **A `const` never holds an array**; an array the program reads is `data`. Reason: a `const` is already compile-time code, so naming a real value (a table's amplitude, M24's float constants) needs no `const fn` returning a literal; reals still never reach the 6502. An array constant would be a second way to declare a table, and an array needs memory, which a constant does not take.
- **95: `offset(T.field)`**, a built-in like `sizeof`: a field's offset in bytes, an untyped constant usable wherever a constant is, nested fields too (`offset(Enemy.pos.x)`). Replaces the spec's "field offsets" that a `const fn` reads, which had no spelling. Reason: assembly through a pointer (`ldy #3` / `lda (p),y`) and generators emitting such code had to hard-code offsets, which break silently when the struct changes. Rejected: `Enemy.hp` alone as a value, which reads like a field access.
- **96: recursion in a `const fn` is allowed**, at most 200 calls deep, with the error as built. Reason: rule 5 exists because of the 6502's stack; a `const fn` runs in the compiler, and generators should be more flexible than runtime code (recursive subdivision, divide-and-conquer tables). The limit turns a runaway recursion into an error, not a crash.
- **97: `repeat(value, count)` replaces `[value; count]`**, wherever compile-time code runs (`data spaces: [u8; 40] = repeat($20, 40)`), an error in runtime code; `[7; 4]` is an error that says "Write repeat(7, 4)". Inside a `const fn` an array may be empty; a table needs at least 1 element. Reason: `[7; 4]` looks like the type `[u8; 4]` on the other side of `=` (rule 3); a named built-in reads as what it is, next to the general `[v for i in 0..n]`. Rejected: `[7 repeat 4]`, a new contextual word.

## 2026-11-11: M22 questions 98-99; a `for` loop's direction comes from its step

- **98: as built.** A `const` inside a function runs compile-time code like a top-level one; an asm operand outside `emit` does not (`lda #round(1.5)` is an error; declare a constant), while constant built-ins (`len`, `sizeof`, `offset`) still work there. Reason: compile-time code runs in declarations and inside `const fn`s only, so a call among instructions or statements is never a hidden compile-time one; a constant gives the value a name.
- **99: as built.** A `const fn`'s variables follow spec 4's scope rule, and its `for` loops follow 5.1 exactly, deciding direction from the text, not from the values the interpreter knows. Reason: the same text means the same in runtime and compile-time code.
- **A `for` loop counts up unless a negative `step` is written** (spec 5.1, 11). Replaces the rules that took the direction from two constant bounds (`9..=0` counted down) or a constant end of 0 (`n..0` counted down). A loop that could only be a down-count without its step is an error: a constant range whose start is past its end, and an unsigned counter ending at constant 0. Reason (the developer's): the old rules made the direction depend on which bounds are constants, too much for a reader to work out; now it is read from the step. The generated code is the same (`for i in n..0 step -1` is `ldx n` / `beq` / `dex` / `bne`). Prior art: Python's `range`, Rust's ranges (`.rev()`). Few programs counted down without a step (four tests, the spec, guide and skill). Built in the M22 follow-up.

## 2026-11-11: M22 questions 100-103: generators, `emit` as a contextual word, errors, labels

- **100: as built.** A generator is any `const fn` without a return value named on an asm line, also one that only calls other generators; an `emit` reached through a `const fn` that returns a value is an error when it runs; arguments are compile-time values, so a program variable cannot be passed. **Added: `addr(name)` passes an address as a `label`** (follow-up). Reason: generators are built from smaller generators; one that returns a value can be used in `data`, so a hidden `emit` would fail far from its cause; without addresses as labels, a generator could not be told which buffer to work on when the compiler places it. **A generator that also returns a value is left out** (roadmap, not scheduled): its use, padding cycle-exact code, needs a built-in for the cycles of emitted code, and allowing it later breaks nothing.
- **101: `emit` is a contextual word**, a keyword only at the start of a statement in a `const fn`, before `{`; listed in spec 13 with `step`. Reason: it is unambiguous there, and reserving it would break `lib/c64/sprites.tern`'s `emit` and user names. Prior art: contextual keywords in C#, Python, Kotlin and Swift.
- **102: as built, plus a field.** Every error while a `const fn` runs has "(called from file:line)"; an error in generated code is at the `emit` line with "(generated by the call at file:line)"; the listing shows the call line, then each instruction under its `emit` line. `tern --json` also gives the call's location as a field (follow-up), for M23's language server.
- **103: as built, plus two kinds of label.** `name:` in an `emit` places only a `label()` value; `label()` outside a `const fn` is an error. A `label` is an address decided at placement, from `label()` (placed once by the generator), `addr()` (placed by the compiler) or a `.label` of the calling asm block written as an argument (placed by the block); placing either of the last two is an error. Reason: a label is an address either way, so one value type covers all three; an asm block cannot call `label()`, so passing its `.label` on the call line is how a generator jumps to the caller's exit. Rejected: passing an `emit`'s local `.label` out of its fragment, a second spelling of `label()` that would let a fragment run in a loop clash with itself.
- **`CompileTime/`** is added to the layout in `CLAUDE.md` (roadmap M22).

## 2026-11-11: M22 follow-up questions 104-106

- **104: a loop that can never run stays an error; one whose variables decide is allowed** (spec 5.1). The "counts up, so it never runs" error is kept where the loop cannot run whatever its variables hold: constant bounds with the start past the end (`9..=0`, also `255..=0 step n` with a variable step) and an unsigned counter ending before 0 (`n..0`). An unsigned `n..=0` and `a..=0 step n`, which run once when the start is 0, are no longer errors and get no warning. With a variable step the fix is "Use a constant negative step to count down". Reason (the developer's): a loop that does not run because of the values in its variables is often intended, and the compiler should not second-guess it (as decided for wrapping indexes and flag loops); a loop that provably never runs is a mistake. Accepted risk: code written before 2026-11-11 that counted down with `n..=0` now runs at most once with no diagnostic; the repository's down-counts were converted in the M22 follow-up and there are no outside users yet. The empty constant range `5..5` keeps its warning (5.1), since its start is not past its end.
- **105: as built.** `calledFrom` is in every `tern --json` diagnostic, `null` outside a `const fn` and generated code (spec 4.1). Reason: it follows `suggestion`, and a fixed set of fields is simpler for tools to read.
- **106: fix the code generator.** `for i in n..0 step -1` (n: `u8`) compiles to `ldx n` / `beq done`, as spec 11 gives; the `cpx #$00` / `bcc` it emits now is dead (an unsigned value is never below 0, and `ldx` sets Z). Reason: design rule 4, generated code matches spec 11 or is shorter. Built in M22 follow-up 2; benchmarks may move.

## 2026-11-12: several errors per compile is M23; doc comments and the language server move last

- **M23 is now "several errors per compile"** (spec 13). One run reports every error it finds (no limit unless `--max-errors n` is given): the parser goes on at the next statement or declaration after a syntax error, the checker reports at most one error per statement or declaration, and a name whose declaration failed gets a type that accepts every use, so it causes no follow-on errors. Allocation, code generation and layout run only without errors, and their errors stay one at a time. The milestone also gives a fix sentence to errors that have none (CLAUDE.md's convention). Reason (the developer's): the compiler stopped at its first error, so a file with several mistakes needed one compile per mistake; better feedback is wanted before more features. No limit by default, as in C# and GCC, with a switch for a short list (Clang's `-ferror-limit`, whose default is 20); the error type keeps follow-on errors few, so a long list is mostly real errors. Rejected: recovery in every stage, since late errors (overlaps, two roots) are rare and their stages need a correct program.
- **Doc comments and the language server become M30, the last milestone**, after the Commodore 128 (M29). Floats and the platforms keep their numbers (M24-M29). Reason: the developer's choice; the editor tools are wanted once the language and its platforms are complete. The spec session's draft design of 2026-11-12 is kept in the roadmap as a starting point, not a decision. Earlier entries that name "M23's language server" (questions 69 and 102) now mean M30.
- **No warnings for now; an analysis of warnings comes after M23** (roadmap). The compiler has never had warnings, though spec 5.1 said an empty constant range (`5..5`) gives one; the spec now says it generates no code, with no warning yet. Reason (the developer's): warnings are not ruled out, but which ones Tern should have needs an analysis of the language first, against "Tern trusts the programmer" (2026-11-07), and M23 is large enough without them.

## 2026-11-12: a 65C02 analysis before M28

- **A spec session analyses 65C02 support before M28** (roadmap, "65C02: analysis"), before any work on the Commander X16 library. Reason (the developer's): the X16 depends on the 65C02, and how far the compiler goes with it (which variants, code shapes per variant, testing CPU and platform separately, variant-specific library code) should be settled first. Not now: it is a later analysis, like the warnings one. The roadmap keeps the spec session's first notes as a starting point, not decisions.
- **65C816 native mode: explored later, after the first release is complete** (roadmap, Not scheduled). Reason (the developer's): it could be useful, but its 16-bit registers and 24-bit addresses would change the language, so it waits until v1.0 is done. The 65C02 analysis keeps the X16's 65C816 in view only as a chip running 65C02 code.

## 2026-11-12: M23 questions 107-111

- **107: an undeclared name is reported at every use**, not once per file as built. A name whose declaration failed still gives one error, at the declaration. Reason (the developer's): the error list is meant to serve the language server (M30), which shows each place a name does not exist wherever the developer is looking in the file. And the compiler cannot know which side is wrong: the developer may fix the declaration (rename `updte` to `update`) or the uses, so every use is marked. Uses of a broken declaration are not places to fix. **No similar-name hint** ("'updte' is declared at line 3"): the developer's choice; a guess at the intended name can be wrong and is not worth the work. Built in the M23 follow-up.
- **108: errors are sorted by place**: the main file, then modules in the order first imported; by line and column within a file. `--max-errors n` keeps the first `n` after sorting. Reason: the checker's order (constants, functions, globals, bodies) put line 4 before line 2; people, agents and editors read top to bottom. Prior art: the C# compiler sorts its diagnostics by location. Replaces "in the order found" in spec 13. Built in the M23 follow-up.
- **109: as built.** The call graph (recursion, two roots, shared functions) runs only without errors, like allocation; spec 13 now lists it. Reason: it needs the whole program, and broken names would give false errors.
- **110: as built.** Text output ends with the count, "1 error" too, as spec 13 says.
- **111: as built.** The errors in `FixSentenceTests.NoFix` keep no fix sentence. Reason: they already say what is expected, or the fix depends on what was meant ("Expected u16, found bool"); a generic sentence would be noise.

## 2026-11-12: bank switching follows overlays; `implements` for overlays and banks

- **Banks follow the overlay model, with no generated stubs** (spec 14.1, replaces its "likely direction"). A module is placed in a bank on its import; `@ bank(n)` on single functions and `data` is removed from spec 4. A call into a bank is a plain `jsr`, and the program selects the bank through the platform library; calls and data uses between banks of one area are errors. Reason: a stub that switches banks and restores the previous one costs about 30 cycles behind a call that looks ordinary (rule 1), needs a shadow byte since mapper registers are often write-only, and makes every NMI that switches banks save and restore it. Hand-written NES code mostly switches banks once per phase, which the overlay model makes free. Accepted risk: a call into a bank that is not selected crashes, unchecked, as a call into an overlay that is not loaded does today. A far call can still be added later as a library function, if a program needs one.
- **`implements` on an overlay or bank import** (spec 8.5): `import level1 @ levels implements level_api` places a jump table for the interface's routines in the module, checked as `export level_api` is (M21). Resident code calls `level_api.update()` and whichever module is loaded answers. Reason (the developer's suggestion): levels, music and enemy sets in banks or overlays have the same entries; without an interface the resident code chooses `level1.update()` or `level2.update()` itself. Unlike M21 plug-ins, the modules are in the same build, so the call graph sees each implementation, frames are shared and the interface's helpers are compiled once. Plain calls stay, since they cost one `jmp` less when the code knows which module it wants. Works for overlays now, banks when they come.
- **No marker on an interface module.** An interface is any module with routines at a fixed address; its other declarations are ordinary. Reason: a routine at a fixed address means the same in every module (a `jsr` to that address), so a marker changes no code and no check; it would forbid keeping shared types and helpers next to the entries, and M21's `export level_api` has none. The explicit word is `implements`, where code is generated and checked; naming a module with no routines at a fixed address is an error.

## 2026-11-12: no copies of functions per root (spec 14.2)

- **A function with memory reachable from two roots stays an error; the compiler never copies it** (spec 8.1, 14.2; replaces the likely direction of a second copy for the interrupt side, as in XC8). The fixes are the developer's: `inline fn` (each caller has its own code and variables, M16), a function with no memory of its own, or a second function written for the handler. Reason (the developer's): this is the developer's problem to solve, and the compiler should not grow smarts for it. An automatic copy would grow ROM with no line in the source saying so (rule 1); an opt-in needs a sixth function modifier for a rare case. `inline fn` already covers the common case, a small helper, at the same cost as a copy, and a handler's version of a larger function is often simpler than main's, which a copy could not be.
- **The two-roots error names the fixes.** It ends today with "Use a copy for each"; it should say "Make it an 'inline fn', or write a second function for the handler" (after the `using X`/`using Y` advice for a loop counter). Built in the M23 follow-up.
- Also closes the note on one form per `interrupt fn` (question 45) waiting for 14.2's copies: a small handler calling the part stays the way.

## 2026-11-12: a function may be called from main and from a handler

- **The two-roots error for functions is removed** (spec 8.1; replaces decision 27 and the first two points of "no copies of functions per root" above, the same day). A function with memory reachable from two roots compiles, with one frame that shares memory with no frame of either root. If a handler calls it while main is inside it, main's call goes on with the handler's values; preventing that is the program's responsibility. The developer guide (3.8, and the list of behaviors) explains what happens and the ways out: interrupts off around main's calls, `inline fn`, no memory of its own, a second function. Reason (the developer's): Tern trusts the programmer (2026-11-07); the guide warns, the developer plans. The error also blocked safe code, such as calls in main between `sei` and `cli`, and made a function's acceptance depend on whether the compiler happened to put a loop counter in a register. Accepted risk: the bug it prevented is intermittent and hard to find, since it shows only when an interrupt lands inside the function. Still no copy per root (14.2).
- **Unchanged:** a module's `private` variable used from two roots stays an error (8.1), with its exception for the module's own handler; globals used from two roots are volatile.
- To weigh in the warnings analysis: a warning, which the developer can turn off, for a function with memory reached from two roots.

## 2026-11-12: encodings declared by the program; the build names its target (spec 14.5)

- **`encoding name { 'c' = $NN, 'a'..='z' = $NN }`**, optionally based on another (`encoding hud: screen { ... }`); the name is a literal prefix like a built-in one, and `pub encoding` is used as `module.name"..."` (spec 2.1). Reason: the NES has no character set and C64 games with their own fonts move digits and letters; today they write `\$NN` or build tables by hand. A declaration keeps text readable at no run-time cost. `encoding` and `default` are words only in their places, like `step` and `emit`.
- **`default encoding name` in the main file** sets the encoding of unprefixed literals in the program's own files, on any target. Reason (the developer's): the target decides the default, and where a platform has none, providing one is the developer's job; allowed on the C64 too, for a game whose font replaces PETSCII everywhere. Library modules keep the platform's default, so a program's line cannot change a library's text.
- **The build names its target**: `tern --target <platform>`, required once a second platform exists (M25), for `tern`, `tern run` and `tern bench`. Reason (the developer's): no platform is favoured; someone who only builds Atari 800 games would always have to name their target if the C64 were the default, so every build names it, the C64 included. The C64 came first because it is the developer's own machine and a good platform to test the compiler on, not because Tern is meant as a C64 language. A build is for one platform (decision 2026-11-09).

## 2026-11-12: no decorator syntax (spec 14.7)

- **Modifiers stay keywords; no `@inline` or `#[inline]` form**, and the trigger "if modifier keywords grow past five or six" is dropped. Reason (the developer's): keywords already do the job, so it is not needed. Function modifiers are five (`pub`, `inline`, `asm`, `interrupt`, `const`) and the sixth proposed today (`copied`) was turned down; words that are keywords only in their place keep new ones cheap; `@` means location only (rule 3), so `@inline` would give it a second meaning. The one thing decorators add, arguments, no modifier needs: alignment is planned as a location (`@ align(n)`). Reconsidered only if a modifier needs arguments.

## 2026-11-12: `pha`/`pla` temporaries are an optimizer choice (spec 14.3)

- **The optimizer may keep a temporary on the stack** when it is saved from A and restored into A with no `jsr` or `asm` block between, and the temporary would otherwise be outside zero page (2 bytes and 7 cycles against 6 and 8: better in both). A zero-page temporary stays (4 bytes and 6 cycles: 1 cycle faster). Reason (the developer's): it is an optimization, not something the language asks of the developer, so no syntax and no rule a program can see. A stack temporary also cannot be overwritten by an interrupt, which matters more now that functions may be shared with handlers. Built only if a count of the pattern in the benchmarks and examples shows enough cases; otherwise closed with the numbers.
