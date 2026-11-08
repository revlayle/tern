# Gap analysis: Tern against hand-written assembly (M17)

Written by the coding session for the spec session, which schedules the fixes (M18). No language change is made here.

**Totals** (bytes / cycles, from `main` on, `comparison.md`): Tern 1221 / 486,802; assembly 947 / 324,419; llvm-mos 1359 / 493,092. The gap to assembly is **274 bytes and 162,383 cycles**.

**How it was counted.** Every number below is counted from a listing, not estimated as a share of the gap:

- `profiles/<task>.txt`: the Tern listing with how often each instruction ran and the cycles it took. `profiles/<task>.asm.txt`: the same for the assembly version (64tass listing, run from `start` to its return; the total at the end matches `comparison.md`). `profiles/<variant>.txt`: the same for the variants. Every profile's cycles add up to the measured total.
- Each benchmark was compared instruction by instruction with its `.asm` file. A cell is the sum of the differences of one class. Cycles are count times the cycles saved, taken from the two profiles; page crossings are the profile's cycles above the instruction's base time. Bytes are counted from the two listings.
- Class 2 "now" cells are measured by variants (`variants.md`): `copy.pages`, `enemies.first`, `interp.return`, and `bigger`'s part of `calls.regs`. Where a variant moves an array across a page boundary, that change is taken out (see each row).
- Regenerate with `TERN_COMPARE=1 dotnet test` (needs 64tass for the assembly profiles).

**Classes** (roadmap). 1 *optimizer*: the compiler could produce the better code from the same source. 2 *how the Tern is written*: another way of writing it gives the better code (advice for the guide). 3 *language*: a feature would be needed. 4 *assembly only*: a trick that should stay in `asm`.

Two more columns make every row add up without a remainder hidden in a class:

- **asm's choices**: differences that come from how the assembly version was written, not from anything Tern could do. Mostly negative (the assembly loses): its variables at `$c000` are absolute (4 cycles, 3 bytes) where Tern's are in zero page, and its own indexed loads cross pages. Positive where it relies on memory being zero at the start instead of setting a variable.
- **other / not explained**: what is left after the counted items. It is 0 in every row.

## 1. The gap by class

### Cycles

| task | gap | 1 optimizer | 2 now (measured) | 2 after a class 1 fix | 3 language | 4 asm only | asm's choices | other |
|---|--:|--:|--:|--:|--:|--:|--:|--:|
| bubble | 96,164 | 96,164 | | | | | | 0 |
| calls | 5,203 | 4,286 | 1,200 | 903 | | | −1,186 | 0 |
| copy | 16,682 | 6,236 | 10,174 | | | 273 | −1 | 0 |
| crc8 | 10,282 | 10,789 | | | | | −507 | 0 |
| enemies | 24,287 | 19,567 | 4,800 | | | | −80 | 0 |
| fib16 | 849 | 907 | | | | 12 | −70 | 0 |
| interp | 1,436 | 974 | 611 | | | | −149 | 0 |
| sieve | 256 | 255 | | | | | 1 | 0 |
| sum16 | 7,224 | 8,776 | | | | | −1,552 | 0 |
| **total** | **162,383** | **147,954 (91%)** | **16,785 (10%)** | **903 (0.6%)** | **0** | **285 (0.2%)** | **−3,544 (−2%)** | **0** |

### Bytes

| task | gap | 1 optimizer | 2 now (measured) | 2 after a class 1 fix | 3 language | 4 asm only | asm's choices | other |
|---|--:|--:|--:|--:|--:|--:|--:|--:|
| bubble | 27 | 27 | | | | | | 0 |
| calls | 19 | 13 | 4 | 5 | | | −3 | 0 |
| copy | 73 | 78 | −23 | | | 21 | −3 | 0 |
| crc8 | 12 | 10 | | | | | 2 | 0 |
| enemies | 28 | 30 | 4 | | | | −6 | 0 |
| fib16 | 42 | 47 | | | | 4 | −9 | 0 |
| interp | 55 | 36 | 18 | | | | 1 | 0 |
| sieve | 1 | 1 | | | | | | 0 |
| sum16 | 17 | 25 | | | | | −8 | 0 |
| **total** | **274** | **267 (97%)** | **3** | **5** | **0** | **25** | **−26** | **0** |

What the two tables say:

- **Class 1 is nine tenths of the cycle gap and almost all of the byte gap.** Bytes do not follow cycles: class 2 saves 16,785 cycles but only 3 bytes, because the faster forms (`copy.pages`) are longer. The largest byte items are F1 (64), F10's 16-bit `± 256` (55) and F11 (21); the largest cycle item, F2, is only 10 bytes.
- **Class 2 has two parts.** "Now" (16,785) is advice a programmer can follow today; each part is measured by a variant. "After a class 1 fix" (903, in `calls`) is a rewrite that only pays once F21 is built: write `bigger` so that its result is the compare's own carry.
- **`bubble` is all class 1.** The source as written can give the assembly's inner loop (F18); no rewrite is needed.
- **The assembly loses 3,544 cycles to its own layout** (absolute addresses, page crossings), so class 1 + class 2 is more than the gap: with all of it, Tern would be about 3,300 cycles below the assembly. With class 1 alone, Tern would be 338,848 cycles (assembly + 4%).
- **Class 3 is empty.** The register-held values in the benchmarks are all class 1 under spec 4 (section 4).

The order the cells are counted in matters where items overlap, and is fixed per row: in `bubble` F18 is counted after F2, F3 and F9; in `copy` the class 2 rewrite (pages) comes first and F1 is counted on the rewritten loops (8 bytes there, 4 on the original); in `enemies` F3 is counted after F4.

## 2. Findings

Each finding: the pattern, Tern's lines against the assembly's, and what it saves on each benchmark as written (cycles / bytes). The class is 1 unless stated.

**F1. An indexed or indirect operand is copied to a temporary before `adc`/`sbc`/`cmp`.** Tern: `lda a,x / sta _t0 / lda b,x / adc _t0`; assembly: `lda b,x / adc a,x`. 6 cycles and 4 bytes per operand byte. `enemies` 9,600 / 8 (`x + dx`, `y + dy`), `copy` 6,000 / 8 (`lda (b),y / sta t0 / lda (a),y / cmp t0` against `lda (p),y / cmp (q),y`), `sum16` 4,800 / 16, `fib16` 792 / 24, `interp` 60 / 8 (ADD, and SUB's bytes). **21,252 / 64.**

**F2. A constant offset is folded into the address only for `a[i - c]`.** `a[i + 1]` is `txa / clc / adc #1 / tay / lda a,y`, 8 cycles and 5 bytes more than `lda a+1,x`, because the `u8` index wraps (decision 42). Spec 3.2 already allows the fold "when it can prove the sum does not wrap (e.g. a loop counter with constant bounds)", but not even the constant-bounds case is built: `for i in 0..99 { b[i] = a[i + 1] }` still computes the index. In `bubble`, `for i in 0..last` with `last: u8` proves `i <= 254`, so `i + 1` does not wrap either. `bubble` 57,216 / 10 (4,950 reads and 2,202 swaps, 8 cycles each). **57,216 / 10**, 35% of the gap. Built in M18 (section 8); the workaround `bubble.minus` (`values[i - 1], values[i]` over `1..=last`) is now slower than the source as written (179,532 against 169,732 cycles), so it is not advice.

**F3. A store to a local that is never read from memory stays.** `var b = values[i]` then `cmp main.a` and `sta values,x` use A, but `sta main.b` stays. `bubble` 14,850 / 2 (`b`), `enemies` 4,800 / 4 (`sta main.x`, `sta main.y`: after F4 nothing reads them from memory), `interp` 222 / 2 (`op`). **19,872 / 8.**

**F4. `x > c` with x in A compiles to `lda #c / cmp x / bcs`** instead of `cmp #c+1 / bcc`. 3 cycles and 2 bytes, and the load of A throws away x. `enemies` (`x > 200`, `y > 180`, 1,584 times): **4,752 / 4.**

**F5. After `sta a,x` the optimizer does not know that A holds the element.** `enemies.inplace` (`e[i].x += d`, then tests on `e[i].x`) reloads `lda e,x` after the store and is slower than the locals form: 169 / 78,372 against 161 / 70,076. No benchmark is written that way, so it is in no row. It is also why `bubble.direct` keeps a slow swap.

**F6. `u16 += u8 as u16`** goes through a 2-byte temporary and a full 16-bit add: `sta _t0 / lda #0 / sta _t0+1 / clc / lda total / adc _t0 / sta total / lda total+1 / adc _t0+1 / sta total+1` (28 cycles, 19 bytes) where `clc / adc total / sta total / bcc / inc total+1` is 12.8 on average (9 bytes). `calls`: **3,048 / 10.**

**F7. A single-root global is kept in memory across a loop.** `crc8`: `crc` is stored in each of the 2,048 bit steps (`sta crc` in both branches), where the assembly keeps it in A for the eight steps and stores once: 6,144 / 4. `sieve`: `inc count` (54 times) where the assembly counts in X and stores once: 156 / −3 (the register version is 3 bytes longer). Spec 4 allows it: "its values may be kept in registers". **6,300 / 1.** F11 and F15 are the same change in other places.

**F8. A 16-bit shift by a constant works on a memory copy.** `v >> 3`: `lda v / sta t / lda v+1 / sta t+1`, three `lsr t+1 / ror t`, then `adc t` (62 cycles, 33 bytes), where the assembly shifts the high byte in memory and the low byte in A (`lsr th / ror a`) and never copies the low byte (47, 26). `sum16`: **3,000 / 7** (15 cycles an iteration, 200 times).

**F9. Loop control.**
- `bubble`: `for i in 0..last` keeps its empty-range test (`cpx last / bcs`) although `while last > 0` holds: 495 / 4. `while last > 0 { ... last -= 1 }` tests `lda #0 / cmp last / bcc` where the `dec last` already set Z (`dec / bne`): 503 / 4. The `jmp` into the `while` test, although `last = 99` makes the first test true: 3 / 3. `x` is stored before the first loop and at the end of each pass; the assembly stores at the top: 3 / 2. Together 1,004 / 13.
- `sieve`: the `while n < 16` entry `jmp` and one extra test (n = 2 is known): 11 / 3.
- `copy`: `while n.hi > 0` as `lda #0 / cmp n+1 / bcc` with an entry `jmp`, where `lda n+1 / beq` once and `dec n+1 / bne` would do: 21 / 3.

**1,036 / 19.**

**F10. Small shapes.**

| shape | Tern | better | where | cycles / bytes |
|---|---|---|---|--:|
| `p += 256`, `n -= 256` | `clc / lda p / adc #0 / sta p / lda p+1 / adc #1 / sta p+1` | `inc p+1` (`dec`) | `copy` (`to`, `from`, `n`, `a`, `b`) | 195 / 55 |
| carry known clear | `clc` before `adc`, after `asl` of a counter below 16 | no `clc` | `enemies` init | 128 / 4 |
| negation | `lda m / eor #$FF / clc / adc #1` | `lda #0 / sec / sbc m` | `enemies` | 32 / 4 |
| `cmp #0` after a load | `lda b / cmp #0 / bne` | `lda b / bne` | `fib16` | 28 / 4 |
| stores of 0 into cleared memory | `lda #0 / sta fib / sta fib+24 / ... sta fib+25` | nothing (the startup cleared it, main has not written it) | `fib16` | 16 / 13 |
| a bound that is a field | `lda n / sta _t1 / cpy _t1` | `cpy n` | `copy` | 6 / 4 |
| A known from X | `ldx n ... lda n / asl` | `txa / asl` | `sieve` | 6 / 1 |
| Y already 0 | `ldy #0` after a `0..=255` loop | nothing | `copy` | 2 / 2 |
| `#0` reloaded | `lda #0 / sta a+1 / lda #45 / sta b / lda #0 / sta b+1` | store the zeros together | `fib16` | 2 / 2 |
| `a[i] += 1` | `lda / clc / adc #1 / sta` | `inc a,x` | `enemies` (never runs) | 0 / 6 |
| `ldy #c` after `ldy #c-1` | `ldy #1`, `ldy #2`, `ldy #3` | `iny` | `copy` | 0 / 3 |

**415 / 98.** Small in cycles, but the second largest byte item: the 16-bit `± 256` alone is 55 bytes.

**F11. `interp`'s stack pointer in Y for the whole loop.** The assembly keeps `sp` in Y; Tern does `ldx sp` (3 cycles) in each handler and `inc sp` / `dec sp` (5) where `iny` / `dey` is 2. `sp` is a global used only by `main`, and the whole interpreter is `main`'s loop, so this is F7 across a loop that contains a jump table (the optimizer forgets everything at jump-table labels today, and Y must be free for the whole loop: it is, since Tern indexes with X). **342 / 21** (net of the `ldy sp` before the loop). The old report called this class 3; section 4 says why it is class 1.

**F12. Indexed loads cross a page.** Tern places data right after the code, so an `abs,x` load can cross a page (+1 cycle). The fix is in the allocator: place an array so its indexed range stays in one page when there is room (RAM only, no bytes in the file).

| task | load | crossings |
|---|---|--:|
| `sum16` | `lda values,x`, `lda values+200,x` (`values` at `$08B2`) | 376 |
| `enemies` | `lda enemies+48,x` (`$08F5`) | 255 |
| `sieve` | `lda composite,x` (`$0852`) | 82 |
| `fib16` | `lda fib+22,x`, `lda fib+23,x` | 19 |

**732 / 0.** The assembly loses 412 cycles the same way (`fib16` 44, `sum16` 288, `interp` 80; in its column). The variants show the effect: `enemies.first` moves the array 4 bytes down and gains 204 cycles from crossings alone; `interp.return` moves `code` across a page and loses 51.

**F13. The outer loop counter in memory.** `crc8`: the inner `repeat` takes X, so `i` is `inc main.i` (5) where the assembly uses Y inside and X outside (`inx`, 2): 769 / 1. `copy` (pages form): `dec _loop0` where `dex` would do: 12 / 3. Spec 7 lets the compiler choose any register. **781 / 4.**

**F14. A `match` range check on a value that cannot be out of range.** `interp`: `cmp #9 / bcc / jmp default` (5 cycles, 7 bytes each dispatch), and the `HALT` value goes to the default arm instead of the table. `op` comes from `code`, a `data` table whose values are all 0 to 9, so with a 10-entry table no check is needed. The assembly has none. **350 / 5** (net of the 10th table entry).

**F15. A `jmp` over an `else` that does the same store** (`crc8`: `eor #7 / sta crc / jmp` and `sta crc`). Once F7 removes the stores, the `else` is empty and the jump goes to the next instruction. **3,108 / 3.**

**F16. A `using A` parameter is stored on entry and reloaded.** `calls`' `clamp`: `sta v / lda top / cmp v / bcs / rts / lda v / rts` where `cmp top / bcc / beq / lda top / rts` keeps v in A. **1,238 / 3.**

**F17. A is forgotten after `bcc / inc hi`.** `sum16`: `sta v / bcc c3 / inc v+1 / c3: lda v`. `inc` does not change A, so A holds `v` on both paths. **600 / 2.**

**F18. A local copy of an element that is only needed later is kept in memory** (`bubble`; after F2). `var a = values[i]; var b = values[i + 1]; if a > b { values[i] = b; values[i + 1] = a }`: `values[i]` is not written between the read of `a` and the first store, so the compare can use it directly, and `a` is needed only on the swap path, where it can be loaded into a free register (Y, since Tern indexes with X) just before `values[i]` is overwritten. With F2 and F3 that is the assembly's inner loop: `lda values+1,x / cmp values,x / bcs / ldy values,x / sta values,x / tya / sta values+1,x`. Today: `lda values,x / sta a / ... / cmp a` on every pass (6 cycles more than `cmp values,x`) and `lda a` on a swap (3 less than `ldy values,x / tya`). **23,094 / 2.** The rewritten form `bubble.direct` gets only 5,478 of it after F1 and F2 (its swap is still `lda values,x / sta a / lda values+1,x / sta values,x / lda a / sta values+1,x`), so it is not advice worth giving if F18 is built.

**F19. A commutative operand order** (`crc8`: `lda crc / eor main.b` where A already holds `b`: `eor crc`). **768 / 2.**

**F20. A bit test where the next shift already gives the bit** (`fib16`'s `mul`; also `math.mul16`). `if b.lo & 1 != 0 { r += a }; a = a << 1; b = b >> 1` is `lda b / and #1 / beq`, then `lsr b+1 / ror b`. `b`'s shift does not depend on the `if`, so it can move before it and the branch can use the carry it leaves (`lsr bh / ror bl / bcc`, the assembly). A reordering inside one function, no source or language change. **50 / 4.**

**F21. A `bool` result is rebuilt from the carry with branches.** `return b >= a` after `cpx a` is `bcs t / clc / rts / t: sec / rts`, although the carry already is the result; `return a > b` likewise. Returning the carry of the compare directly saves 4.5 cycles a call on average (4 on the false path, 5 on the true one) and 5 bytes (`bcc`, `clc`, `rts`, `sec` go), where the condition matches the compare's carry. `calls`' `bigger(a, b)` returns `a > b`, the inverse of `cmp`'s carry, but `a > b` has a carry form without branches too: `clc / sbc b` leaves the carry set exactly when `a > b`. With `a` in A that is `stx t / clc / sbc t / rts`, 2 cycles more than the assembly's `sta t / cpx t / rts`, so an F21 that also handles `>` and `<=` would recover about 500 of the 903 cycles from the source as written; the rest needs the rewrite. The assembly's `bigger` returns the inverted flag ("carry clear when a > b") and its caller branches the other way. Written so in Tern (a function returning `b >= a`, the caller testing `!`), F21 would give the assembly's code: 903 / 5, counted in the "after a class 1 fix" column.

## 3. Per benchmark

Each table lists every difference; the rows add up to the gap. "asm" is the assembly's own choice (section 1).

### bubble: 83 / 226,948 against 56 / 130,784 (gap 27 / 96,164)

Inner loop, 4,950 passes and 2,202 swaps: Tern `lda values,x / sta a / txa / clc / adc #1 / tay / lda values,y / sta b / cmp a / bcs`, swap `sta values,x / txa / clc / adc #1 / tay / lda a / sta values,y`, then `inx / cpx last / bne`; assembly `lda values+1,y / cmp values,y / bcs`, swap `ldx values,y / sta values,y / txa / sta values+1,y`, then `iny / cpy last / bne`.

| item | cycles | bytes | class |
|---|--:|--:|---|
| F2: `txa / clc / adc #1 / tay` in the read and in the swap | 57,216 | 10 | 1 |
| F3: `sta b` | 14,850 | 2 | 1 |
| F9: outer loop control and the store of `x` | 1,004 | 13 | 1 |
| F18: `a` in memory: `lda values,x / sta a / cmp a` (10) against `cmp values,y` (4) on every pass, less 3 a swap (Tern's `lda a` against `ldx values,y / txa`) | 23,094 | 2 | 1 |

The original source with F2, F3, F9 and F18 is the assembly's code. `bubble.direct` with F1 and F2 would be the assembly plus 17,616 (its swap) plus F9, so once F18 exists it is the worse form. Variants in M17: `bubble.minus` 77 / 179,532 (the F2 workaround), `bubble.direct` 94 / 253,936. The first loop (`x = (x << 2) + x + 17`) is the same in both apart from the store of `x`.

### calls: 107 / 31,754 against 88 / 26,551 (gap 19 / 5,203)

| item | cycles | bytes | class |
|---|--:|--:|---|
| F6: `total += s as u16` | 3,048 | 10 | 1 |
| F16: `clamp` stores and reloads its `using A` parameter | 1,238 | 3 | 1 |
| `bigger`: `b` is a memory parameter (`lda #100 / sta b`, `lda b`) where the assembly passes X; measured by `calls.regs`, whose `bigger(a: u8 using A, b: u8 using X)` call and compare are the assembly's (`ldx #100 / lda s / jsr`, `sta a / cpx a`) | 1,200 | 4 | 2 now |
| `bigger`: `return a > b` makes a true carry with `bcc / clc / rts / sec / rts`; the assembly returns the inverted carry and the caller branches on it (`rts`) | 903 | 5 | 2 after F21 |
| `add3`: Tern's memory parameters are cheaper than the assembly's A/X/Y, which it spills to `t` | −400 | −1 | asm |
| absolute `total`, `clamped` in the assembly | −791 | −6 | asm |
| `i = 0`: the assembly relies on zeroed memory | 5 | 4 | asm |

`calls.regs` (`using` on every parameter): 101 / 30,104 in all. Inlining saves something the assembly pays too, the `jsr` / `rts`, so it goes beyond the gap rather than closing these items: `calls.bigger` (only `bigger` as `inline fn`) is 103 / 29,045 (−2,709 / −4), but it still stores both parameters and builds the carry with `clc` / `sec` (and a `jmp`) before the caller's `bcc`; `calls.inline` (all three) is 95 / 23,855 (−7,899 / −12), 2,696 cycles below the assembly. Advice: small functions in a hot loop are `inline fn`, and register parameters (`using`) for the others.

### copy: 227 / 68,281 against 154 / 51,599 (gap 73 / 16,682)

The fill loop takes the same cycles (`ldy #1` against `iny`: 3 bytes). The 1,000-byte copy loop is the same instruction for instruction (`lda (from),y / sta (to),y / iny / bne`, 16 cycles a byte). The rest:

| item | cycles | bytes | class |
|---|--:|--:|---|
| compare in 250 records of 4 (`ldy #0`, `cpy #4`, `a += 4`, `b += 4`, counter) against pages of 256 as the copy does: `copy.pages`, measured | 10,174 | −23 | 2 now |
| F1 (on the pages form): `lda (b),y / sta t0 / lda (a),y / cmp t0` against `lda (p),y / cmp (q),y` | 6,000 | 8 | 1 |
| F10: `to`, `from`, `a`, `b` `+= 256` and `n -= 256` as adds | 195 | 55 | 1 |
| F9: `while n.hi > 0` test and entry `jmp` | 21 | 3 | 1 |
| F13: the pages counter `_loop0` in memory | 12 | 3 | 1 |
| F10: `n.lo` copied to `_t1`; `ldy #0` when Y is 0; `ldy #c` for `iny` | 8 | 9 | 1 |
| the assembly is written for 1,000 bytes: no call (six `lda / sta` and `jsr / rts` against four), the page count in X (`ldx #3 / dex`), the tail bound as `cpy #232` against `cpy n` (232 times) | 273 | 21 | 4 |
| absolute `same` | −1 | −3 | asm |

The last but one is class 4 because it is a layout chosen for one task: the Tern task copies through a general `copy` function, the assembly has none.

### crc8: 41 / 37,949 against 29 / 27,667 (gap 12 / 10,282)

Inner loop (2,048 steps): Tern `asl / bcc / [eor #7 / sta crc / jmp] or [sta crc] / dex / bne`; assembly `asl / bcc / eor #7 / dey / bne`, with `sta crc` once after it.

| item | cycles | bytes | class |
|---|--:|--:|---|
| F7: `sta crc` in the bit loop (2,048) and after `eor` (256), against one store per byte | 6,144 | 4 | 1 |
| F15: the `jmp` over the `else` | 3,108 | 3 | 1 |
| F13: `inc main.i` where X is free outside the inner loop (`inx`) | 769 | 1 | 1 |
| F19: `lda crc / eor main.b` against `eor crc` | 768 | 2 | 1 |
| absolute `crc` (`eor`, `sta`) | −512 | −2 | asm |
| `b = 0`: the assembly relies on zeroed memory | 5 | 4 | asm |

### enemies: 161 / 70,076 against 133 / 45,789 (gap 28 / 24,287)

Per enemy and axis (800 times): Tern `lda dx,x / sta t0 / lda x,x / clc / adc t0 / sta main.x / cmp #8 / bcc / lda #200 / cmp main.x / bcs`, then `lda main.x / sta x,x`; assembly `lda ex,x / clc / adc edx,x / sta ex,x / cmp #8 / bcc / cmp #201 / bcc`.

| item | cycles | bytes | class |
|---|--:|--:|---|
| F1: `dx`, `dy` through `_t0` | 9,600 | 8 | 1 |
| store the element before the tests (`enemies[i].x = x` first): removes `lda main.x`; `enemies.first`, measured 5,004 of which 204 is page crossings | 4,800 | 4 | 2 now |
| F3: `sta main.x`, `sta main.y`, dead after F4 | 4,800 | 4 | 1 |
| F4: `lda #200 / cmp x` against `cmp #201` | 4,752 | 4 | 1 |
| F12: `lda enemies+48,x` crosses a page | 255 | 0 | 1 |
| F10: `clc` with a clear carry in the init loop; negation; `hits += 1` | 160 | 14 | 1 |
| init: the assembly adds `i` with an extra `txa` | −64 | −2 | asm |
| absolute `bounces` | −16 | −4 | asm |

### fib16: 183 / 2,246 against 141 / 1,397 (gap 42 / 849)

| item | cycles | bytes | class |
|---|--:|--:|---|
| F1: `fib[i - 1] + fib[i - 2]`, both 16-bit operands through temporaries (64 against 28 cycles a pass) | 792 | 24 | 1 |
| F10: `cmp #0` after `lda` in `while b != 0`; stores of 0 into cleared `fib`; `lda #0` twice | 46 | 19 | 1 |
| F12: `fib+22,x`, `fib+23,x` cross a page | 19 | 0 | 1 |
| F20: `mul`'s bit test `lda b / and #1 / beq` (7.4 a bit on average) where the assembly shifts `b` first and branches on the carry (`bcc`, 2.4) | 50 | 4 | 1 |
| `mul` returns in A/X (`ldx r+1 / lda r`); the assembly leaves the result in zero page for the caller | 12 | 4 | 4 |
| the assembly's own crossings (`fib-1,y`, `fib-2,y` at `$bfff`), `ldx #20 / lda fib,x` for `fib[20]`, absolute `product`, and `lda / ora / bne` for the zero test, which is shorter but slower than Tern's two tests once `cmp #0` is gone | −70 | −9 | asm |

`fib16.const` (the multiplies by constants written as shifts) is a different task and is not counted: 125 / 1,685. llvm-mos evaluates `mul(123, 45)` at compile time (`const fn`, M22).

### interp: 241 / 6,149 against 186 / 4,713 (gap 55 / 1,436)

| item | cycles | bytes | class |
|---|--:|--:|---|
| the `running` flag: setting it, the entry `jmp`, `lda running / beq / jmp` after every arm (75 times) and the default arm's store, against `HALT` returning; `interp.return`, measured 560 because the shorter program moves `code` across a page (−51) | 611 | 18 | 2 now |
| F14: `cmp #9 / bcc / jmp` before the table | 350 | 5 | 1 |
| F11: `ldx sp` in each handler, `inc sp`/`dec sp` against `iny`/`dey` | 342 | 21 | 1 |
| F3: `sta op` | 222 | 2 | 1 |
| F1: ADD (`stack[sp - 1] + stack[sp]`); SUB's bytes | 60 | 8 | 1 |
| the assembly's crossings (`stack-1,y` at `$bfff`), absolute `steps` and `result` | −155 | −3 | asm |
| `pc = 0` (the assembly relies on zeroed memory), `ldy #0` against `ldy sp` | 6 | 4 | asm |

The jump table itself (`tax / lda hi,x / pha / lda lo,x / pha / rts`) is the same in both. `DEC => stack[sp - 1] -= 1` is `lda / sec / sbc #1 / sta` on both sides (the assembly has no `dec abs,y`); with `sp` in X, Tern could use `dec stack-1,x` and be 60 cycles below the assembly there. That is not part of the gap and is not counted.

### sieve: 48 / 8,745 against 47 / 8,489 (gap 1 / 256)

| item | cycles | bytes | class |
|---|--:|--:|---|
| F7: `inc count` (54 times) against `inx` and one `stx count` | 156 | −3 | 1 |
| F12: `lda composite,x` crosses a page | 82 | 0 | 1 |
| F9: the `while` entry `jmp` and one extra test | 11 | 3 | 1 |
| F10: `lda n / asl / tay` against `tya / asl / tay` | 6 | 1 | 1 |
| `ldx #0` against `ldx count` | 1 | 0 | asm |

All class 1; the old report's "nothing to fix" was wrong.

### sum16: 130 / 34,654 against 113 / 27,430 (gap 17 / 7,224)

| item | cycles | bytes | class |
|---|--:|--:|---|
| F1: `total += values[i]` and `values[i] > largest` through `_t0` | 4,800 | 16 | 1 |
| F8: `v >> 3` on a memory copy, and the add from it | 3,000 | 7 | 1 |
| F17: `lda main.v` after `bcc / inc v+1` | 600 | 2 | 1 |
| F12: `values,x` and `values+200,x` cross a page | 376 | 0 | 1 |
| absolute `total` and `largest` | −1,264 | −8 | asm |
| the assembly's own crossings (`values+200,x`) | −288 | 0 | asm |

## 4. Register-held values and carry as data, against the design rules

The roadmap names both as examples of class 3. Each case in the benchmarks and the library:

**Values kept in a register across a loop** (`sieve`'s `count`, `crc8`'s `crc`, `interp`'s `sp`; and `crc8`'s outer counter, a local). Each is a global used by one root (or a local), and each is used inside one function's loop. Spec 4: "All other memory is ordinary: its values may be kept in registers, and redundant reads and writes removed"; spec 8.1: "A global variable used from two roots is volatile". So the compiler may already do it: **class 1, no language change.** Against the rules: rule 1 holds (no new construct, no hidden cost); rule 4 holds (the code is shorter than the section 11 shape, which is allowed); rule 5 holds (the variable keeps its fixed address; the register is a copy, stored back before the loop exits or before a call that may read it). What makes it work in the compiler: knowing the loop has no call that reads the global (`crc8`, `sieve`: none), and for `interp`, carrying the register's contents into every target of the jump table. The class 3 form, a global held in a register *across functions* (a register reserved for a variable in every function that runs while it is live), is not needed by any benchmark and is not proposed: it would take Y (or X) away from every pointer access and `using` loop in those functions.

**Carry as data** (`math.mul8`, `math.div8`, `wide.add32`, the bit test in `fib16`'s `mul` and in `math.mul16`). The assembly uses the bit that a shift or an add leaves in the carry as the input of the next instruction: `ror` chains the halves of a product, `rol` gives the ninth bit of a remainder, `adc` carries between the bytes of a 32-bit add, `lsr` / `bcc` tests the bit it shifted out.

- As a source construct (a carry that one statement leaves and the next reads), it fails **rule 4**: almost every statement changes the carry (`cmp`, `adc`, shifts, a comparison in an `if`), so the meaning of a statement would depend on the instructions the one before it compiled to, and section 11 would have to promise which shapes keep the carry. An "add with carry" or "rotate through carry" operator would also be a second meaning for `+` and `>>` or a new symbol for one instruction (**rule 3**). Tern already passes a carry where the boundary is explicit: `-> bool using carry` on a function result. So **class 4**: these are what `asm fn` is for, and the library has them (rule 2, restriction is acceptable).
- Inside one function the optimizer can use a carry without any language change: move `b = b >> 1` before the `if` in `mul` (it does not depend on the `if`) and branch on the shifted-out bit. That is class 1, counted as F20 (50 cycles in `fib16`, about 5 a bit in `mul16`). What stays class 4 is the carry as a value *between* statements in the source.

**Decimal mode** (`bcd.add`): `sed` changes what `adc` does. A decimal `+`, or a mode, would give `+` two meanings (**rule 3**). Class 4.

## 5. Library routines: assembly against plain Tern (`library.md`)

Same driver, same inputs, results checked against C#. Bytes are driver plus routine.

| routine | asm bytes / cycles | plain bytes / cycles | what the assembly does | class and rule |
|---|--:|--:|---|---|
| `math.mul8` | 67 / 55,565 | 98 / 107,021 | the product's high byte in A, the low byte and the multiplier shifting through `ror`; plain Tern adds 16 bits in memory | 4: carry as data (rules 4, 3) |
| `math.div8` | 98 / 70,914 | 176 / 246,990 | the ninth bit of the remainder is the carry of `rol`; plain Tern needs a 16-bit remainder | 4: carry as data (rules 4, 3) |
| `math.mul16` | 130 / 46,086 | 141 / 49,682 | the same algorithm as `fib16`'s `mul`: the bit test through the carry, and `ora` for the zero test | 1 (F9, F10 `cmp #0`, F20) |
| `math.abs16` | 72 / 22,300 | 103 / 29,574 | `using AX` and a sign test of X | 1: the parameter in memory (F16 for `using AX`), `x < 0` as a full signed compare (`cmp / sbc / bvc / eor / bpl`) where `lda x+1 / bpl` tests the sign, the negation through a copy (F10) |
| `wide.add32` | 64 / 13,734 | 195 / 27,228 | a byte loop with the carry kept from one byte to the next; plain Tern finds the carry by comparing | 4: carry as data (rule 4); Tern has no 32-bit type by design, `wide` is the library. Each `p.lo` access reloading Y is class 1 |
| `bcd.add` | 61 / 14,128 | 161 / 65,296 | decimal mode (`sed`) | 4: rule 3 |

None of the six should become plain Tern. Only `abs16` and `mul16` are class 1, and after F9, F10 and F16 they would be close to the assembly. None comes from `text` or `sprites`: `text` has no assembly routines (it is plain Tern already), and the `sprites` routines (`sort`, `build`, `emit`, the raster handler) are private, work on the module's state and need `FakeVic` to run; measuring one would mean rewriting part of the multiplexer. `sprites.sort` (750 cycles for 24 sorted sprites) is the candidate if one is wanted.

## 6. llvm-mos: what it does that the others do not

Nothing that is not already in the list: (1) it inlines every static function and removes the parameter passing (`calls` 65 / 10,649; Tern's `inline fn` gets to 23,855); (2) it evaluates a constant call (`fib16`'s `mul(123, 45)`) and expands `* 9` into shifts; (3) it folds `i + 1` into the address (F2) because a C index does not wrap; (4) it keeps small globals in zero page. It is slower than Tern in `sieve` (4.3 times the cycles), `crc8` (1.5 times), `sum16`, `interp` and `enemies` (C uses an array of structs, so each access multiplies by 5). Over all nine, llvm-mos is 1359 / 493,092 against Tern's 1221 / 486,802. With the class 1 items, Tern would be about 31% below llvm-mos in cycles. It was built with `-Os -fno-lto`, not the roadmap's `-Os`: with link-time optimization the SDK removes the result variables that only the test reads (`comparison.md`). With LTO its `calls` and `fib16` numbers would probably be lower.

## 7. Ranked list for the spec session

Largest saving first, in cycles over the nine benchmarks (of the 162,383-cycle gap), with bytes (of 274). Class 1 numbers are counted from the profiles and are estimates until each fix is built.

| # | change | class | cycles | bytes | notes |
|---|---|---|--:|--:|---|
| 1 | Fold `a[i + c]` into the address when the loop bounds prove no wrap (F2) | 1 | 57,216 | 10 | allowed by spec 3.2 already; the constant-bounds case is not built either |
| 2 | Keep a local copy of an element out of memory: compare against the element, load it into a free register only on the path that needs it (F18) | 1 | 23,094 | 2 | `bubble`; with F2 and F3, the assembly's inner loop from the source as written |
| 3 | Indexed and indirect operands used directly by `adc`/`sbc`/`cmp`/`and`/`ora`/`eor` (F1) | 1 | 21,252 | 64 | |
| 4 | No store to a local that is never read from memory (F3) | 1 | 19,872 | 8 | `enemies`' part needs F4 |
| 5 | Keep a single-root global in a register across a loop (F7), also across `interp`'s jump table (F11); the `jmp` over an empty `else` then goes (F15) | 1 | 9,750 | 25 | allowed by spec 4 and 8.1 |
| 6 | `x > c` as `cmp #c+1` (F4) | 1 | 4,752 | 4 | a peephole |
| 7 | `u16 += u8` as `clc / adc / sta / bcc / inc` (F6) | 1 | 3,048 | 10 | |
| 8 | A 16-bit shift by a constant with the low byte in A (F8) | 1 | 3,000 | 7 | |
| 9 | Keep a `using A` parameter in A (F16) | 1 | 1,238 | 3 | a known limitation |
| 10 | Loop control: entry `jmp` and empty-range test when the first test is known, `dec / bne` (F9) | 1 | 1,036 | 19 | |
| 11 | Choose a register for an outer loop counter when the inner loop takes X (F13) | 1 | 781 | 4 | |
| 12 | Commutative operand order (F19) | 1 | 768 | 2 | |
| 13 | Place indexed arrays so their range does not cross a page (F12) | 1 | 732 | 0 | allocator; RAM only |
| 14 | A is not changed by `inc` after `bcc` (F17) | 1 | 600 | 2 | |
| 15 | Small shapes (F10), led by `p += 256` as `inc p+1` | 1 | 415 | 98 | the `± 256` part is 195 / 55 |
| 16 | No `match` range check when the value's range is known (F14) | 1 | 350 | 5 | here from a `data` table |
| 17 | Branch on the carry a shift leaves, moving an independent shift before the test (F20) | 1 | 50 | 4 | also `math.mul16` |
| 18 | Guide advice, measured today: compare and copy in pages, store an element before testing it, register parameters (`using`), `return` instead of a loop flag | 2 now | 16,785 | 3 | `copy.pages` −15%, `enemies.first` −7%, `interp.return` −9%; beyond the gap, small hot functions as `inline fn` (`calls.inline` −25%, below the assembly) |
| 19 | Return a `bool` as the compare's own carry (F21), and write such functions so their condition is that carry | 1, then 2 | 903 | 5 | F21 for `cmp`'s own carry needs `bigger` rewritten; an F21 that also makes `>` with `clc / sbc` would get about 500 of it from the source as written |

Items 1 to 17 add up to the class 1 column: 147,954 cycles and 267 bytes. Class 3 is empty. Class 4 (285 cycles, 25 bytes: the assembly's copy written for one size, and `mul` leaving its result in zero page) and, in the library, carry as data and decimal mode stay assembly by design (section 4).

The guide advice in item 18 can be written now. The `bubble.minus` workaround is dropped: F2 is built (M18). `bubble.direct` (compare the elements, no locals) should not become advice: with F2 and F18 the source as written gives the assembly's loop, and `bubble.direct` would be 17,616 cycles slower.

Question for the spec session: F2 needs no rule change if "when it can prove the sum does not wrap" in spec 3.2 includes either of two proofs in `bubble`. (a) The loop bound: `for i in 0..last` with `last: u8` gives `i <= 254`, so `i + 1 <= 255`. (b) The other access: the same body reads `values[i]`, and spec 3.2 says an index outside the array "is not supported" and "the compiler may assume it never happens", so `i < 100` and `i + 1 <= 100`. (b) does not depend on the bounds, so it also covers `while` loops and the constant-bounds case, but it uses the out-of-range assumption for more than "a[i] = v changes only a". This report reads (a) as allowed; please decide (b).

## 8. M18: measured savings

Each fix as built, against the estimate above. Measured on `BenchmarkTests` (`baseline.txt`: the whole program, startup included), cycles / bytes over the nine benchmarks.

After part 1 (F2, F1, F4, F6, F8, F10, F19, F21), `comparison.md`: Tern 1036 / 394,956 against the assembly's 947 / 324,419 (M17: 1221 / 486,802), so the gap went from 274 / 162,383 to 89 / 70,537. `fib16` and `sum16` are now below the assembly in cycles and bytes. Variants (`variants.md`): `bubble.minus` is slower than `bubble` as written, so its advice is dropped; `bubble.direct` (149,404) is now faster than `bubble` (169,732), until F18 (part 2) gives `bubble` the assembly's inner loop.

| finding | estimate | measured | where |
|---|--:|--:|---|
| F2 | 57,216 / 10 | 57,216 / 10 | `bubble` |
| F1 | 21,252 / 64 | 21,590 / 60 | `enemies` 9,855 / 8 (of which 255 is F12: `enemies` no longer crosses a page), `copy` 6,000 / 4 (the estimate's 8 bytes were on the pages form), `sum16` 4,864 / 16, `fib16` 811 / 24, `interp` 60 / 8 |
| F4 | 4,752 / 4 | 4,752 / 4 | `enemies` |
| F6 | 3,048 / 10 | 3,048 / 10 | `calls` |
| F8 | 3,000 / 7 | 3,636 / 9 | `sum16`; beyond the estimate, the optimizer now drops `lda v+1` at the loop top (A holds it from the store at the end of the pass), 597, which the assembly does not do; the rest is page crossings |
| F19 | 768 / 2 | 768 / 2 | `crc8` |
| F21 | 903 / 5 (about 500 from the source as written) | 503 / 4 | `calls`: `bigger`'s `return a > b` is `lda a / clc / sbc b / rts`; the rest needs the rewrite (section 3) |
| F10 `± 256` | 195 / 55 | 117 / 33 | `copy`: `to`, `from`, `n`; the estimate also counted `a`, `b`, which step by 256 only in `copy.pages` |
| F10 `a[i] += 1` | 0 / 6 | 0 / 6 | `enemies` (`hits`, never runs); also `interp`'s `DEC` as `dec stack-1,x`, 6 bytes, but 22 cycles slower in all: the shorter program moves `code` across a page (F12) |
| F10 register transfers, `iny` | A known from X 6 / 1, `ldy #c` 0 / 3, Y known 0 2 / 2 | 22 / 6 | `sieve` 22 / 2 (`ldx n` as `tax` after the loop test, `lda n` as `txa`), `copy` 0 / 4 (`iny` for `ldy #2`, `#3`). `copy`'s `ldy #0` after the pages loop stays: the `while` can be skipped on entry, so Y is not known there (it would need F9) |
| F10 negation | 32 / 4 | 32 / 4 | `enemies` |
| F10 `cmp #0` | 28 / 4 | 28 / 4 | `fib16` (`while b != 0`, 16-bit) |
| F10 carry known clear | 128 / 4 | 128 / 4 | `enemies` init: `(i << 3) + i + 10` with `i < 16` |
| F10 a field as a bound | 6 / 4 | 6 / 4 | `copy`: `for i in 0..n.lo` is `cpy n` |
| F10 stores of 0 into cleared memory, zeros stored together | 16 / 13, 2 / 2 | 22 / 19 | `fib16` 18 / 15; `copy` 4 / 4 (the zeros of `copy(DST, SRC, 1000)`'s arguments) |

Part 2 (dataflow), as each fix is built:

| finding | estimate | measured | where |
|---|--:|--:|---|
| F3 | 19,872 / 8 | 15,083 / 4 | `bubble` 14,850 / 2 (`sta b`), `interp` 233 / 2 (`sta op`, with 11 cycles of page crossings). `enemies`' `sta main.x` / `main.y` stay: on the bounce path A is overwritten and `lda main.x` after the `if` reads memory, so "nothing reads them after F4" holds only on the other path; removing them needs x kept in a free register on the bounce path |
| F18 | 23,094 / 2 | 23,204 / 5 | `bubble` 23,094 / 2: the inner loop is the assembly's (`lda values+1,x / cmp values,x / bcs`, swap `ldy values,x / sta values,x / tya / sta values+1,x`); `interp` 110 / 3, beyond the estimate: `SWAP`'s `t` goes the same way |
| F16 | 1,238 / 3 | 1,238 / 2 | `calls`: `clamp` is `cmp top / bcc / beq / lda top / rts / rts`. `using AX` (review): an unsigned 16-bit compare of the parameter is `cpx t+1 / bcc / bne / cmp t / ...` (equality: `cmp t / bne / cpx t+1`), so A and X keep it; no benchmark has one. A 16-bit `clamp` goes from 16 bytes / 23 cycles (slow path) to 12 / 14. `math.abs16` written in Tern still keeps its parameter in memory: its gap is the signed compare and the negation |
| F17 | 600 / 2 | 612 / 5 | `sum16` 608 / 2 (the `lda v` after `bcc / inc v+1`); `fib16` 4 / 3, the same rule after a 16-bit add |
| F5 | none in the benchmarks | 0 / 0 in `baseline.txt` | `enemies.inplace` 139 / 58,445 -> 127 / 45,709: the reloads of `e[i].x` and `e[i].y` after the store and after the first `cmp` go. It is now the same code as `enemies.first`, and faster than `enemies` as written (135 / 55,309, whose `sta main.x` / `main.y` stay, see F3). `bubble.direct` now compiles to the same code as `bubble` (69 / 131,788) |

After part 2 (F3, F18, F16, F17, F5), `comparison.md`: Tern 1020 / 354,819 against the assembly's 947 / 324,419, so the gap went from 89 / 70,537 to 73 / 30,400. `bubble` is 1,004 cycles from the assembly (F9). Variants: `bubble.direct` and `bubble.minus` are no longer faster than `bubble`; `enemies.inplace` is no longer worse than the locals form, so neither should become advice.
