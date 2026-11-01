# Status

Owned by the coding session. Updated at the end of every coding session.

## Current

- **Milestone:** M2 (pointers, data and strings), review findings fixed.
- **Branch:** `m2-pointers` (`main` with the review merged in).
- **Next step:** re-review of `m2-pointers` against `docs/reviews/m2.md`, then merge. The spec session should also answer the questions below; the provisional choices are easy to change.

## Done

- **M1 core compiler**, merged to `main` (2026-11-01).
- **M2 pointers, data and strings** (branch `m2-pointers`, 2026-11-01). 354 tests pass (34 CPU, 320 compiler).
  - `ptr T` for any number, bool or pointer type. Pointers work as globals, locals, parameters (also `using AX`), return values and array elements.
  - `p[i]` compiles to `(zp),y`. `p[i] = v` has the spec 11 shape (`ldy i` / `lda v` / `sta (p),y`) whenever computing the value does not change Y; `dst[i] = src[i]` loads Y once; `p[i] op= v` changes the element in place with one Y load. A pointer that is not a zero-page variable (a computed pointer such as `rows[j][i]`, or one at a fixed address of `$100` or more) is copied to a zero-page temporary first. A pointer at a fixed zero-page address (`@ $FB`) is used directly.
  - Dereferenced pointer variables are forced into zero page, after `@ zp` variables (spec 8.2 step 2). If zero page is full, the error lists every user and its size.
  - `p + n`, `n + p`, `p - n`, `p += n`, `p -= n` (offset `u8` or `u16`, in bytes); `p += 1` is `inc`/`bne`/`inc`. Comparisons between pointers of the same type. `as` between pointers and `u16`, no code.
  - `addr(name)` for globals, locals, parameters, arrays and data. Variables whose address is taken are never used directly as a `for` bound (a pointer could change them).
  - Char literals `'A'` (PETSCII), strings `"..."` (PETSCII) and `screen"..."` (screen codes), escapes `\0`, `\n`, `\\`, `\"`, `\'`, `\$NN`. Encodings live in `Petscii.cs` and are chosen by the platform.
  - `data` arrays and strings in the program image, read-only (writes are errors). Strings also initialize global `var` arrays.
  - `include_bytes("file")`, relative to the source file (`TernCompiler.Compile(source, sourcePath: ...)`; the CLI passes it).
  - Fixed: `len()` and `sizeof()` inside a `using X` loop were reported as function calls.
  - Review fixes (`docs/reviews/m2.md`): 1, `p[i] = v` with `i` in X no longer saves A to a temporary (commit "Load Y before the value in p[i] = v"); 2, the three missing error tests; 3, 16-bit stores into split arrays skip the temporary when the value needs no code; 4, a function named like a built-in (`addr`, `len`, `sizeof`, `include_bytes`) is an error; 5, clearer messages for `addr()` in initial values, untyped data arrays and `screen'A'`, and escape errors point at the escape (commit "Address M2 review suggestions").
  - `examples/hello.tern` clears the screen and prints a rainbow message through pointers; checked in VICE.
  - Roadmap "done when": string copied to the screen through a pointer (`DataTests.Copies_a_string_to_the_screen_through_a_pointer`, `examples/hello.tern`), data table read by index (`DataTests.Data_table_is_readable_by_index`), pointer arithmetic across pages (`PointerTests.Pointer_arithmetic_in_bytes`, `Index_crosses_pages`, `Walks_a_buffer_across_a_page_boundary`).

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
- `addr()` cannot be used in global or data initializers: addresses are known only at link time, and initial values must be compile-time constants. Tables of pointers to data must be filled at runtime.
- `VariableSymbol.AddressTaken` is set by `addr()`. The M7 optimizer must not cache such variables in registers across pointer writes.

## Questions for the spec session

1. **Type of `addr(name)`.** Spec 3.4 implies a pointer; the table in spec 10 says `u16`. Implemented: `ptr T` for a variable of type `T`, `ptr T` to the first element for an array of one-byte `T`, and `ptr u8` (the low-byte table) for a split array of 16-bit values. `addr()` of a function is "not supported yet" until there is something to do with the address (asm, interrupts, `fn()` variables).
2. **Number literals as pointers.** Implemented: a number literal (or an untyped `const`) converts to a pointer without `as`, as literals take their type from context: `var p: ptr u8 = $0400`, `p == 0`, `print(SCREEN + 40, ...)`. A typed `u16` value needs `as`. Is that right?
3. **`p[i]` on pointers to 16-bit values.** Implemented: the index counts elements, so `p[i]` reads bytes `2i` and `2i+1` (`asl` / `tay` / ... / `iny`). Constant indexes above 127 are an error; a variable index of 128 or more wraps silently within the 256 bytes `(zp),y` reaches (`asl` drops the top bit). The review asks that the spec state this in 3.4 if it is kept. Alternative: allow `p[i]` only on pointers to one-byte types.
4. **`.lo` / `.hi` on pointers.** Not allowed; the error suggests `(p as u16).lo`. Allowing it would make `p.hi += 1` (next page) easy to write, but `.lo`/`.hi` could clash with struct fields through pointers (M4).
5. **Pointer differences.** `p - q` is an error ("convert them to u16 first"). Should it give a `u16`?
6. **Arrays longer than 256.** Strings and `include_bytes` may produce more than 256 bytes (e.g. a 2 KB charset); written array types are still limited to 256, and indexing a longer array is an error that points to `addr()`. The spec limits arrays to 256; is this exception right, or should long tables have their own form?
7. **PETSCII for lower-case letters.** Implemented: `a`-`z` give the same codes as `A`-`Z` (`$41`-`$5A`), which the default character set shows as upper case. Programs that switch to the lower/upper case character set would want `A`-`Z` as `$C1`-`$DA`. Should there be a second encoding (e.g. `petscii_mixed"..."`) or a platform option?
8. **Screen-code char literals.** There is `screen"..."` but no `screen'A'`. Writing single characters to the screen needs the screen code as a number (`$01`). Add `screen'A'`?
9. **Escapes.** Implemented `\0`, `\n` (PETSCII `$0D`), `\\`, `\"`, `\'` and `\$NN` (a raw byte, not encoded). The spec only shows `\0`.
10. **`data` placement.** `data x @ $C000 = ...` is "not supported yet" (the program image is one block). Needed later for fixed-address tables and `@ bank(n)`.

## Provisional choices made while coding

All M2 choices are listed as questions above (1-10). Also:

- Pointers move only by unsigned offsets (`p + s` with `s: i8` is an error); use `p - n` to move back.
- Strings can initialize global `var` arrays as well as `data`; the string must have exactly the array's length.
- Only functions are stopped from using a built-in's name; a variable named `len` is allowed, since it cannot be confused with a call.
- Characters with no PETSCII code (`{`, `|`, `}`, `~`, `` ` ``, `\`, `^`, `_` and non-ASCII except `£ ↑ ← π`) are errors that suggest `\$NN`.
