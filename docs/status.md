# Status

Owned by the coding session. Updated at the end of every coding session.

## Current

- **Milestone:** M3 (assembly blocks and KERNAL calls), including the M2 follow-up. Complete.
- **Branch:** `m3-asm` (from `main` after the M2 merge).
- **Next step:** `/review` of `m3-asm`. After the merge, M4 (imports and the first standard library). The spec session should look at the questions below; the provisional choices are easy to change.

## Done

- **M1 core compiler**, merged to `main` (2026-11-01).
- **M2 pointers, data and strings**, merged to `main` (2026-11-01).
- **M2 follow-up** (branch `m3-asm`, 2026-11-01):
  - Encodings in `CharEncoding.cs`: `petscii`, `petscii_mixed`, `screen`, `screen_mixed`. The platform names its default (`Platform.DefaultEncoding`; C64: `petscii`). Any name right before a quote is an encoding prefix, for strings and chars: `screen'A'`, `petscii_mixed"Hello"`. Unknown prefixes are errors that list the known encodings.
  - `p - q` of two pointers of the same type gives the `u16` byte distance.
- **M3 assembly blocks** (branch `m3-asm`, 2026-11-01). 442 tests pass (34 CPU, 408 compiler).
  - `asm { }` statements and `asm fn` with typed signatures. One instruction per line; `//` comments; local `.labels:`, separate in each block.
  - Operands: `#imm`, `addr`, `addr,x`, `addr,y`, `(zp),y`, `(zp,x)`, `(addr)`, `asl a` / `asl`. An operand is a constant expression (`SIZE-1`, `len(t)`, `'A'`, `screen'A'`, `-1`), or the name of a variable, function or label plus or minus a constant (`score+1`, `buffer-1,x`). `.lo`/`.hi` give a byte of an address for immediates: `#msg.lo`, `#(buf+3).hi`.
  - The checker (`Checker.Asm.cs`) picks the addressing mode; the assembler uses the zero-page form when the address is below `$100`. Variables used with a zero-page-only mode (`(p),y`, `(p,x)`, `stx v,y`) are forced into zero page (`VariableSymbol.AsmZeroPage`, same rules as dereferenced pointers).
  - `AsmEffects` records the registers, flags and variables each block reads and writes; the listing shows it as a comment above the block (`; asm: reads A C D, changes A C Z N V, reads score, writes score`).
  - Working around blocks: a block in a `for ... using X/Y` loop may read the loop register, but changing it or any `jsr` is an error. A local used as a `for` bound that a block writes is copied before the loop. Writing `data` or a loop variable is an error. Functions named in a block (`jsr f`, `jmp f`, `#f.lo`) are added to the call graph, so they are compiled and their frames do not overlap the caller's.
  - `asm fn`: register parameters stay in their registers (no frame memory, not stored on entry); memory parameters are read by name. Return values as for any function (A, A/X, carry). `rts` is added unless the block ends with `rts`, `rti` or `jmp`.
  - Instructions in a block are emitted as written: the assembler never replaces them, and an out-of-range branch in a block is a compile error pointing at the branch.
  - `examples/kernal.tern`: `chrout` as an `asm fn` (`jmp $FFD2`), and an asm block calling `PLOT` between compiled statements. Checked in VICE; `AsmTests.Kernal_example_prints_through_chrout` runs it in the emulator with stand-ins for the ROM routines (`Run.Program(..., setup: ram => ...)`).
  - Roadmap "done when": the example prints text in VICE; asm blocks mixed with compiled code are tested in the emulator (`AsmTests`: spec examples, loops, calls in both directions, all addressing modes, 47 error messages plus branch range and error positions).

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
- A call to any function inside a `for ... using X` loop is an error, even an `asm fn` that never touches X. Spec 6.1 says the compiler may use each function's actual register use; `AsmEffects` makes this possible for `asm fn`.

## Questions for the spec session

The M2 questions were settled on 2026-11-01 (`docs/decisions.md`). New ones from M3; each is implemented as described and marked provisional:

1. **`;` in assembly.** Most assemblers use `;` for comments, but in Tern it separates statements. Implemented: `;` in an asm block is an error ("write one instruction per line, and comments with //"), so pasted assembly fails loudly instead of being misread. Alternative: treat `;` as a comment inside asm blocks only.
2. **Zero-page forms.** Spec 9 says the compiler never changes instructions in a block. Implemented: `lda v` becomes the zero-page form when `v` is in zero page, as every assembler does (it is the same instruction with a shorter encoding). Branches are never rewritten (out of range is an error). Is the zero-page choice acceptable, or should there be a way to force absolute addressing (e.g. for exact cycle counts)?
3. **`asl a`.** `a` or `A` after `asl`/`lsr`/`rol`/`ror` means the accumulator, even if a variable named `a` exists. `asl` alone also works.
4. **Immediate addresses.** `lda #name` for an address is an error ("write #name.lo or #name.hi"). Immediate numbers may be -128 to 255.
5. **Case.** Mnemonics and the index registers in `,x`/`,y` ignore case (`LDA t,X`). Labels and Tern names are case-sensitive.
6. **Calling Tern functions from assembly.** `jsr f` works for functions with register parameters or none. There is no way to write a memory parameter from assembly (`f.x` is not a name outside `f`, spec 6.1). Should asm be allowed to write `f.x`, or is this the intended limit?
7. **Function addresses in assembly.** `#f.lo` / `#f.hi` (e.g. to install a handler) makes `f` a callee of the function containing the block, which keeps it compiled and its frame separate. For interrupt handlers (M6) this will need the interrupt-root rule instead.
8. **`p - q`** requires both pointers to have the same type, like comparisons. Should different pointer types be allowed, since the result is only a byte count?
9. **`π` in `petscii_mixed`.** `$FF` shows a different character in the lower/upper-case set, so `petscii_mixed` has no code for `π`. `screen_mixed` is derived from `petscii_mixed` and also has none.

## Provisional choices made while coding

Listed as questions 1-9 above.
