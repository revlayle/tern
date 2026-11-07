# Gap analysis: Tern against hand-written assembly (M17, draft)

Status: first pass. Covers `bubble`, `fib16`, `enemies`, `copy`, `sum16` (the five largest gaps). Still to do: `calls`, `interp`, `crc8`, `sieve`, the six library routines, the class totals and the ranked list. Numbers come from `comparison.md` (regenerated with llvm-mos: Tern 1221 bytes / 486,802 cycles, assembly 947 / 324,419, llvm-mos 1359 / 493,092) and `variants.md`. Savings marked "est." are counted from the listings, not measured.

Classes: 1 optimizer, 2 how the Tern is written, 3 language, 4 assembly only.

## Findings that recur

**F1. An indexed or indirect operand is copied to a temporary before `adc`/`sbc`/`cmp`** (class 1). `lda a,x` / `lda (p),y` is loaded, stored to `_t0`, and `_t0` is the operand, where the assembly writes `adc a,x` / `cmp (q),y`. Costs 6 cycles and 2 bytes per use, 16-bit operands twice that. Seen in `fib16` (`fib[i-1] + fib[i-2]`: 71 vs 35 cycles per iteration, 792 of the 849-cycle gap), `enemies` (`x + dx`, ~9,600 cycles), `copy` (compare loop, ~6,000), `sum16` (`total += values[i]` and `values[i] > largest`, most of the 7,224 gap). Est. total over the benchmarks: about 25,000 cycles and 80 bytes.

**F2. Constant offset folded into the address only for `a[i - c]`** (class 1, with a class 2 workaround). `a[i + 1]` is `txa / clc / adc #1 / tay / lda a,y` (8 cycles, 5 bytes more) because the u8 index wraps (decision 42). The loop bounds often prove no wrap: in `for i in 0..last` with `last: u8`, `i <= 254`. Range knowledge from the loop would allow `lda a+1,x`. Workaround (class 2, measured): `bubble.minus` writes the pair as `values[i-1], values[i]` over `1..=last`: 83 -> 77 bytes, 226,948 -> 179,532 cycles (-21%). The advice belongs in the guide.

**F3. `x > const` with x in A compiles to `lda #c / cmp x`** instead of `cmp #c+1 / bcs` (class 1). 3 cycles and 3 bytes when x is a local, more when it is an element (`enemies`: `lda e,x / sta t0 / lda #200 / cmp t0`). est. 4,800 cycles in `enemies`.

**F4. A local that is only stored and never read from memory keeps its store** (class 1): `var b = values[i]` then `cmp`/`sta` from A leaves `sta main.b` (3 cycles, 2 bytes per iteration in `bubble`, 4,950 iterations).

**F5. After `sta a,x` the compiler forgets that A holds the element** (class 1). `bubble`'s `enemies.inplace` variant (`e[i].x += d; if e[i].x < 8 ...`) reloads `lda e,x` straight after the store and is worse than the local-variable form: 169 / 78,372 against 161 / 70,076.

**F6. `inc a,x` is not used for `a[i] += 1`** (class 1): `lda / clc / adc #1 / sta` (13 cycles, 8 bytes) against `inc enemies+64,x` (7 / 3). Rare in the benchmarks.

**F7. Negating a memory operand** is `lda m / eor #$FF / clc / adc #1` (10 cycles, 8 bytes); `lda #0 / sec / sbc m` is 8 / 6 (class 1, small).

**F8. Test of bit 0 then shift right** (`fib16` `mul`): `lda b / and #1 / beq` then later `lsr b+1 / ror b`, against the assembly's `lsr bh / ror bl / bcc` (carry out of the shift is the test). 5 cycles per iteration. Needs the shift moved before the conditional add: class 4, or class 3 if a form that exposes the shifted-out bit is wanted (not proposed).

**F9. Zero tests of a 16-bit value** are `lda lo / cmp #0 / bne; lda hi / cmp #0 / bne` (16 cycles) against `lda lo / ora hi / bne` (9) (class 1, `fib16` `mul`, `copy`).

**F10. Loops**: the exclusive loop over a known non-empty range still has its pre-test (`cpx last / bcs` in `bubble`, the inclusive form `cpx / beq / inx / bne`, 2 cycles more per iteration than `iny / cpy / bne`); `while last > 0 { ... last -= 1 }` tests with `lda #0 / cmp last / bcc` where the assembly has `dec last / bne` (class 1).

## Per benchmark

### bubble (83 / 226,948 vs assembly 56 / 130,784, llvm-mos 65 / 181,935)
Gap 96,164 cycles, 27 bytes. Inner loop, no swap: Tern 36 cycles (`lda / sta a / txa clc adc tay / lda / sta b / cmp a / bcs / inx cpx bne`), assembly 19 (`lda x+1,y / cmp x,y / bcs / iny cpy bne`). Parts: F2 8 cycles (class 1; class 2 with `values[i-1]`, measured), F4 3, `sta a`+`cmp a` against `cmp values,y` 2-3 (needs F1 style operand and a reload of `a` on the swap path: assembly trick, class 4), F10 about 2. After the `bubble.minus` form the inner loop is 30 cycles. llvm-mos reaches 65 bytes by the same folding (C does not wrap the index).

### fib16 (183 / 2,246 vs 141 / 1,397, llvm-mos 105 / 1,297)
Gap 849 cycles, 42 bytes. 792 cycles and 24 bytes are the fib loop: F1. `mul`: F8 and F9, about 50 cycles in total. llvm-mos folds `mul(123, 45)` at compile time and expands `* 9` inline: constant evaluation through a call, which is `const fn` (M22) for the constant call; not an optimizer item.

### enemies (161 / 70,076 vs 133 / 45,789, llvm-mos 221 / 84,449)
Gap 24,287 cycles. F1 about 9,600 (twice per iteration, 800 iterations); the local `x`/`y` stored, then reloaded for `sta e,x` (6 cycles, 1,600 times, about 9,600: assembly stores the sum straight into the table and tests it; a different order of the source, but Tern's `x` is a local by design, so class 1 only if A is kept alive across the test); F3 about 4,800; F6 and F7 small (bounces are rare). Initialisation: `(i << 3) + i + 10` needs `clc` where the assembly knows the carry is clear (class 4, range of i).

### copy (227 / 68,281 vs 154 / 51,599, llvm-mos 240 / 71,295)
Gap 16,682 cycles. The copy loops are identical (16 cycles a byte). The compare phase: F1 (`lda (b),y / sta t0 / lda (a),y / cmp t0`, 16 cycles against `lda (p),y / cmp (q),y`, 10) about 6,000; the source loops 250 times over 4 bytes and bumps two pointers each time (about 40 cycles an outer iteration, 10,000), where the assembly compares in pages (class 2: write the compare as 3 pages of 256 and a tail, as `copy` does; to be measured as `copy.pages`). 16-bit `to += 256` is 17 cycles of `adc #0` chains where `inc to+1` is 5 (class 1, small).

### sum16 (130 / 34,654 vs 113 / 27,430, llvm-mos 224 / 43,795)
Gap 7,224 cycles. F1 on `total += values[i]` (12 cycles) and on `values[i] > largest` (reloads the element twice: 14 cycles), 200 times: about 5,200. The generator loop copies `v` into temporaries before shifting (`lda v / sta t0 / lda v+1 / sta t0+1 / 3 x (lsr / ror)`) where the assembly shifts one temporary and keeps the low byte in A: about 10 cycles an iteration (class 1: shift of a 16-bit copy that is only read).

## Measured variants (`variants.md`)
| variant | bytes | cycles | task |
|---|--:|--:|--:|
| bubble.direct (compare elements, no locals) | 94 | 253,936 | 83 / 226,948 |
| bubble.minus (`values[i-1]`, `values[i]`) | 77 | 179,532 | 83 / 226,948 |
| enemies.inplace (`+=` on the element) | 169 | 78,372 | 161 / 70,076 |

## llvm-mos techniques
No technique the others lack beyond inlining and constant evaluation of the call (`calls` 10,649 cycles against Tern's 31,754, `fib16`). It also folds `i + 1` into the address (F2) because C indexes do not wrap. It keeps small globals in zero page.
