# Gap analysis: Tern against hand-written assembly (M17)

Written by the coding session for the spec session, which schedules the fixes (M18). No language change is made here.

**Sources.** `comparison.md` (the nine tasks in Tern, assembly, Prog8, cc65 and llvm-mos), `profiles/<task>.txt` (the Tern listing with how often each instruction ran and the cycles it took: the counts below come from these), `variants.md` (the same task written another way in Tern, measured; sources in `variants/`), `library.md` (six library routines in assembly against plain Tern; sources in `library/`). Regenerate everything with `TERN_COMPARE=1 dotnet test`.

**Totals** (bytes / cycles, from `main` on): Tern 1221 / 486,802; assembly 947 / 324,419; llvm-mos 1359 / 493,092. The gap to assembly is **274 bytes and 162,383 cycles**.

**Classes.** 1 *optimizer*: better code from the same source, no language change. 2 *how the Tern is written*: another way of writing it already gives the better code (advice for the guide). 3 *language*: a feature would be needed. 4 *assembly only*: a trick that should stay in `asm`.

## 1. Where the gap is, by class

Cycles. Each benchmark's gap is split by what closes it; the numbers come from the profiles (instruction count times cycles saved) and from the measured variants. The columns add up to the measured gap.

| task | gap | 1 optimizer | 2 how written | 3 language | 4 asm only |
|---|--:|--:|--:|--:|--:|
| bubble | 96,164 | 73,259 | 22,905 | | |
| calls | 5,203 | | 5,203 | | |
| copy | 16,682 | 6,508 | 10,174 | | |
| crc8 | 10,282 | 10,282 | | | |
| enemies | 24,287 | 19,283 | 5,004 | | |
| fib16 | 849 | 799 | | | 50 |
| interp | 1,436 | 282 | 560 | 345 | 249 |
| sieve | 256 | | | | 256 |
| sum16 | 7,224 | 7,224 | | | |
| **total** | **162,383** | **117,637 (72%)** | **43,846 (27%)** | **345 (0.2%)** | **555 (0.3%)** |

If every class 1 item were done, the Tern total would be about 369,000 cycles (assembly 324,419) and class 2 advice would bring most of the rest. Class 2 items are *measured* (a variant); class 1 items are counted from the profile and are estimates until the fix exists. Two overlaps to know: `bubble`'s class 2 part (22,905) exists only after the class 1 items F1 and F2 are done (written as `if values[i] > values[i+1]`, the compare is `lda a+1,x / cmp a,x`, as in the assembly; today that form is slower, see `bubble.direct`); and `calls` is closed completely by `inline fn`, which ends 2,696 cycles below the assembly (23,855 against 26,551), so its class 1 items (F6) are headroom, not gap.

Bytes. The gap is 274: `bubble` 27, `calls` 19, `copy` 73, `crc8` 12, `enemies` 28, `fib16` 42, `interp` 55, `sieve` 1, `sum16` 17. Not split by class: the causes below save bytes in about the same proportion as cycles, except that `copy` (a generic function with memory parameters and 16-bit `+= 256` chains) and `interp` (the `match` range check, the jump table's `jmp`s, `sp` in memory) are mostly structure.

## 2. Findings (the recurring causes)

Each gives the pattern, the cost, and where it was counted. "Saves" are cycles over the whole benchmark.

**F1. An indexed or indirect operand is copied to a temporary before `adc`/`sbc`/`cmp`** (class 1). `lda a,x` (or `lda (p),y`) is stored to `_t0` and `_t0` is the operand, where the assembly writes `adc a,x` / `cmp (q),y`. 6 cycles and 4 bytes per operand byte (a 16-bit operand costs twice that). Saves: `fib16` 792 (71 against 35 cycles per iteration of the fib loop, which is the whole gap but 57 cycles), `enemies` 9,600 (`x + dx`, 1,600 times), `copy` 6,000 (compare loop: `lda (b),y / sta t0 / lda (a),y / cmp t0`, 16 cycles against 10), `sum16` 4,800 (`total += values[i]` and `values[i] > largest`), `interp` 60. Total **21,252 cycles**, about 80 bytes. It also unlocks class 2 on `bubble` (22,905).

**F2. A constant offset is folded into the address only for `a[i - c]`** (class 1). `a[i + 1]` compiles to `txa / clc / adc #1 / tay / lda a,y` (8 cycles and 5 bytes more than `lda a+1,x`), because the `u8` index wraps (decision 42). But the loop usually proves there is no wrap: in `for i in 0..last` with `last: u8`, `i <= 254`; in `for i in 0..N`, `i + 1 <= N`. Using that range would give `lda a+1,x` with the semantics unchanged. `bubble`: 57,216 cycles (4,950 reads and 2,202 swaps, 8 cycles each) and 10 bytes: **35% of the whole gap**. llvm-mos folds it too (65 bytes) because C does not wrap the index. The workaround today is class 2: write the pair as `values[i - 1], values[i]` over `1..=last` (`bubble.minus`: 83 -> 77 bytes, 226,948 -> 179,532 cycles, -21%; the inclusive loop costs 2 cycles an iteration that `0..last` would not).

**F3. A local that is stored and never read from memory keeps its store** (class 1). `var b = values[i]` then `cmp` and `sta values,x` use A, but `sta main.b` stays: `bubble` 14,850 cycles (3 a time, 2 bytes), `interp` 222 (`var op = code[pc]`). In `enemies` the store of the local `x`/`y` (`sta main.x`, read once for `cmp main.x`) becomes dead once F4 is done: 4,596. Total **19,668 cycles**.

**F4. `x > c` with x in A compiles to `lda #c / cmp x`** instead of `cmp #c+1 / bcs` (class 1). 3 cycles and 3 bytes when x is a local; the load of A also throws away the value of x that the next statement stores. `enemies`: 4,752 (1,584 times). A one-line peephole.

**F5. After `sta a,x` the optimizer does not know that A holds the element** (class 1). `enemies.inplace` (`e[i].x += d` and then tests on `e[i].x`) reloads `lda e,x` straight after the store and is worse than the locals form: 169 / 78,372 against 161 / 70,076. Not counted in the totals (no benchmark is written that way); it is why the "no local" style is not yet an option.

**F6. `u16 += u8 as u16`** (class 1): the widened operand goes through a 2-byte temporary and a full 16-bit add (28 cycles, `calls`) where the assembly has `clc / adc total / sta total / bcc / inc total+1` (14). 2,800 cycles over 200 calls.

**F7. A global kept in memory across a loop, and an `if`/`else` that stores in both branches** (class 1; `crc8`): `crc` is a global used by `main` only, so it is not volatile, yet each of the 2,048 bit steps stores it (`sta crc`: 6,144 cycles), and the `then` branch ends with a `jmp` over the `else` store (3,108). Keeping it in A for the loop and storing once at the end ("store sinking"), and merging the identical tails of the two branches, gives the assembly's loop. With the commutative `lda crc / eor b` written as `eor crc` when A already holds `b` (768) and small items: **10,282 cycles**, 6% of the gap. The largest of the "needs analysis" items.

**F8. A 16-bit shift by a constant is done on a memory copy** (class 1; `sum16`): `v >> 3` copies `v` into temporaries and shifts them in place (`lsr t+1 / ror t`, 5 + 5 cycles a step), where the assembly shifts the high byte in memory and the low byte in A (`lsr th / ror a`, 5 + 2) and never copies the low byte. 2,424 cycles.

**F9. Loop control** (class 1): `for i in 0..last` has its empty-range test (`cpx last / bcs`) even after `while last > 0`, `while last > 0 { ... last -= 1 }` tests with `lda #0 / cmp last / bcc` where `dec last / bne` would do, and the inclusive `for i in 1..=last` ends with `cpx / beq / inx / bne` (10 cycles) against `iny / cpy / bne` (8). `bubble` 1,193 cycles. Also 16-bit zero tests: `lda lo / cmp #0 / bne; lda hi / cmp #0 / bne` against `lda lo / ora hi / bne` (6 bytes, almost no cycles; `fib16`, `copy`).

**F10. Small shapes** (class 1, each under 1% of the gap): negating a memory operand is `lda m / eor #$FF / clc / adc #1` (10 cycles) where `lda #0 / sec / sbc m` is 8; `a[i] += 1` is `lda / clc / adc #1 / sta` (13) where `inc a,x` is 7; `to += 256` is a three-byte `adc #0 / adc #1` chain where `inc to+1` is 5; `bit 0 test then shift right` (`fib16`'s `mul`, 50 cycles) is `lda b / and #1 / beq` and later `lsr / ror`, where the assembly shifts first and branches on the carry (class 4: the shift has to move before the add).

**F11. A register that holds a global for the whole program** (class 3; `interp`): the assembly keeps the stack pointer in Y throughout; Tern reloads `ldx sp` (3 cycles) in 72 handlers and uses `inc sp` / `dec sp` (5) where `iny` / `dey` is 2. 345 cycles in this benchmark; the pattern (an index variable of an interpreter, a VM or a state machine, used by every handler) is common. Not proposed here; see section 6 for the question to the spec session.

## 3. Per benchmark

### bubble: 83 / 226,948 against assembly 56 / 130,784, llvm-mos 65 / 181,935
Profile (inner loop, 4,950 iterations, 2,202 swaps): `lda x / sta a / txa clc adc tay / lda y / sta b / cmp a / bcs` then `inx / cpx last / bne`: 36 cycles without a swap, against 19 for the assembly's `lda x+1,y / cmp x,y / bcs / iny / cpy last / bne`. Of the 96,164 cycles: F2 57,216 (the `txa clc adc tay` in both the read and the swap), F3 14,850 (`sta b`), F9 1,193 (outer loop control), and the rest, 22,905, is the `sta a` / `cmp a` pair: the assembly never copies an element into a variable, it compares in memory and reloads in the swap path. That part is class 2 and depends on F1 and F2. Variants: `bubble.minus` as above; `bubble.direct` (compare the elements, no locals): 94 / 253,936, worse today, F1 and F2 make it the best form.

### calls: 107 / 31,754 against 88 / 26,551, llvm-mos 65 / 10,649
Parameters and return values go through memory (`sta add3.a`, `sta add3.b`, ...) where the assembly passes A, X and Y, and `total += s as u16` is F6. Variants: `calls.regs` (`using A/X/Y` on every parameter) 101 / 30,104; **`calls.inline`** (the three functions are `inline fn`) 95 / 23,855, below the assembly. llvm-mos inlines every static function and keeps the loop in registers: 10,649. Advice: small leaf functions in a hot loop are `inline fn`. Note: in `calls.regs` the argument `i >> 1` goes `lsr a / sta t / ldx t` instead of `lsr a / tax` (4 cycles, a class 1 item for register parameters).

### copy: 227 / 68,281 against 154 / 51,599, llvm-mos 240 / 71,295
The 1,000-byte copy loop is identical to the assembly's (16 cycles a byte). The gap is the compare: F1 (6,000) and the structure: the Tern source compares 250 records of 4 bytes and bumps two pointers each time (about 40 cycles an outer iteration, 10,000 in all), the assembly compares in pages. Variant `copy.pages` (3 pages of 256 and a tail of 232, as the copy does): 250 / 58,107 (-10,174 cycles, +23 bytes). The rest, 508, is the 16-bit pointer steps of `copy` (F10). Bytes: the call setup of `copy` (six `lda #/sta`), three 16-bit `+= 256` chains and the second loop's `cpy t1`; the assembly inlines the copy for these constants (class 4). Not counted exactly.

### crc8: 41 / 37,949 against 29 / 27,667, llvm-mos 46 / 55,581
All F7. The inner loop (2,048 iterations) is `asl / bcc / [eor #7 / sta crc / jmp] or [sta crc] / dex / bne`; the assembly's is `asl / bcc / eor #7 / dey / bne` with one store after the loop. llvm-mos is slower than Tern here (55,581 cycles); its loop was not examined.

### enemies: 161 / 70,076 against 133 / 45,789, llvm-mos 221 / 84,449
F1 9,600 (twice an iteration), F4 4,752, the local round trip (`sta main.x ... lda main.x / sta e,x`: 6 cycles, 1,600 times, 9,600) of which 5,004 goes away by writing `enemies[i].x = x` before the tests, as the assembly does (variant `enemies.first`: 157 / 65,072; class 2) and the rest, 4,596, by F3 + F4 (the local becomes dead). Initialising `(i << 3) + i + 10` needs a `clc` where the assembly knows the carry is clear after `asl` on a value below 128: class 4. 335 cycles of other small items.

### fib16: 183 / 2,246 against 141 / 1,397, llvm-mos 105 / 1,297
792 of 849 cycles and 24 of 42 bytes are the fib loop: F1 (`fib[i-1] + fib[i-2]`, 16-bit, both operands indexed). `mul`: F10 (bit test then shift, 50) and 16-bit zero tests (6 bytes). llvm-mos expands `* 9` inline and evaluates `mul(123, 45)` at compile time. Variant `fib16.const` writes the multiplies at the constant level (shifts and one constant): 125 / 1,685 (a different task: it removes the multiply routine; shown only for comparison with llvm-mos). Constant evaluation of a call is `const fn` (M22).

### interp: 241 / 6,149 against 186 / 4,713, llvm-mos 253 / 6,891
F3 (`op`, 222), F1 (`stack[sp-1] + stack[sp]`, 60), the `match` range check (`cmp #9 / bcc / jmp`, 369 cycles; the assembly has none, a documented difference), the loop flag (`lda running / beq / jmp`, 376 cycles) and F11 (345). Variant `interp.return` (the `HALT` arm returns, `while true`): **223 / 5,589** (class 2; -18 bytes, -560 cycles). The jump table is shared by both.

### sieve: 48 / 8,745 against 47 / 8,489, llvm-mos 140 / 37,200
256 cycles: `n` is in memory where the assembly keeps it in Y between the tests. Nothing to fix. llvm-mos is 4.3 times slower and three times larger.

### sum16: 130 / 34,654 against 113 / 27,430, llvm-mos 224 / 43,795
F1 on `total += values[i]` (12 cycles) and `values[i] > largest` (12), 200 times each: 4,800. F8: 2,424.

## 4. Library routines: assembly against plain Tern (`library.md`)

Same driver, same inputs, results checked against C#. Bytes are driver plus routine.

| routine | asm bytes / cycles | plain bytes / cycles | what the assembly does that Tern cannot say |
|---|--:|--:|---|
| `math.mul8` | 67 / 55,565 | 98 / 107,021 | product kept in A with the multiplier's bits shifting out through `ror` (8-bit adds only); class 4 (carry through a shift) and F1 (16-bit adds on memory) |
| `math.div8` | 98 / 70,914 | 176 / 246,990 | the 9th bit of the remainder is the carry of `rol`; plain Tern needs a 16-bit remainder; class 4 |
| `math.mul16` | 130 / 46,086 | 141 / 49,682 | same algorithm: plain Tern is 8% slower; F1, F9 (16-bit zero test) |
| `math.abs16` | 72 / 22,300 | 103 / 29,574 | `using AX` registers; the plain function copies the argument and negates through temporaries (F10) |
| `wide.add32` | 64 / 13,734 | 195 / 27,228 | a byte loop with the carry kept across iterations; Tern has no 32-bit type (by design, `wide` is the library) and each `p.lo` access reloads Y; class 4 |
| `bcd.add` | 61 / 14,128 | 161 / 65,296 | decimal mode (`sed`); no Tern construct (design rule 3); class 4 |

None of the six should become plain Tern. Only `mul16` is close, and after F1 and F9 it should reach the assembly's size. The others use the carry as data (a bit shifted out into the next shift or add) or decimal mode; those are the cases `asm fn` is for.

## 5. llvm-mos: what it does that the others do not

Nothing that is not already in the list: (1) it inlines every static function and removes the parameter passing (`calls` 10,649 cycles, 65 bytes; Tern's `inline fn` gets to 23,855); (2) it folds a constant call (`fib16`'s `mul(123, 45)`) and expands `* 9` into shifts; (3) it folds `i + 1` into the address (F2) because C indexes do not wrap; (4) it keeps small globals in zero page. It is worse than Tern in `sieve` (4.3 times the cycles), `crc8` (1.5 times), `sum16`, `interp` and `enemies` (a struct array with a multiply by 5, the same layout as cc65; Tern uses one table per field, like the assembly). Over all nine, llvm-mos is 1359 bytes and 493,092 cycles against Tern's 1221 and 486,802: Tern is a little better on both, and F1-F9 would put it about 25% under llvm-mos in cycles.

## 6. Ranked list for the spec session

Largest saving first. "Saves" is in cycles over the nine benchmarks (of the 162,383-cycle gap) and is counted from the profiles; none is measured until built.

| # | change | class | saves | notes |
|---|---|---|--:|---|
| 1 | Fold `a[i + c]` into the address when the loop bounds prove the index does not wrap (F2) | 1 | 57,216 | needs the loop's maximum value, which `Checker` already computes for index checks (`_loopMax`); no language change, decision 42 stays true |
| 2 | Indexed and indirect operands used directly by `adc`/`sbc`/`cmp`/`and`/`ora`/`eor` (F1) | 1 | 21,252 | and it makes the natural "compare two elements" form the fast one (unlocks 22,905 of class 2 advice) |
| 3 | No store to a local that is never read from memory; a stored value that is read once in the next statement stays in A (F3) | 1 | 19,668 | needs a use count per local |
| 4 | `crc8` group: keep a single-root global in A across a loop and store once, merge the tails of `if`/`else`, commutative operand order (F7) | 1 | 10,282 | the largest analysis item; legal because a global used by one root is not volatile |
| 5 | `x > c` as `cmp #c+1` (F4) | 1 | 4,752 | a peephole |
| 6 | `u16 += u8` as `clc / adc / sta / bcc / inc` (F6) | 1 | 2,800 | |
| 7 | 16-bit shift by a constant with the low byte in A (F8) | 1 | 2,424 | |
| 8 | Loop control (F9) and the small shapes (F10) | 1 | about 3,000 | `inc a,x`, `0 - m`, `inc to+1`, 16-bit zero test with `ora` |
| 9 | Guide advice (class 2), measured: `a[i-1], a[i]` for adjacent pairs, `inline fn` for small hot functions, compare and copy in pages, store before testing, `return` instead of a loop flag | 2 | 43,846 | `bubble.minus` -21%, `calls.inline` -25% (and below the assembly), `copy.pages` -15%, `enemies.first` -7%, `interp.return` -9% |
| 10 | Question: a way to keep a global or local in a register across functions (F11) | 3 | 345 here | not proposed; checks against the design rules are open (rule 1: cost stays visible; rule 5: no stack frames) |

Class 3 has one candidate and class 4 only small items: the language is not what limits Tern here. The items under 4 and 10 of the library table (carry as data, decimal mode) stay assembly by design.

M18 (items 1 to 8) would remove about 117,000 cycles (72% of the gap). The guide advice (item 9) is independent of it and can be written now; items 1-2 change how some of its examples are best written (`bubble.direct` becomes the fast form), so the guide should wait for M18 or say so.

Questions for the spec session: (a) F2 touches no rule but depends on how loop maxima are defined for `while` loops and `step` loops; (b) F7 needs the statement that a global used by one root can be kept in a register across a loop, which is true of the current volatile rules but is not written in spec 7 or 11; (c) F11, as above.
