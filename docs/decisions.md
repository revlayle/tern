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
