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

- **v0:** constant expressions plus `include_bytes`. **Later:** `const fn`, a compile-time interpreter for a subset of Tern, with its own unbounded `int` and `real` types, range-checked when values enter the program.

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
