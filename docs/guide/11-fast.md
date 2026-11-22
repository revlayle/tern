# 11. Writing fast Tern

[Guide index](README.md) · previous: [10. How Tern programs behave](10-behavior.md)

Each rule here was measured with `tern bench` ([11.11](#1111-measuring)) on the current compiler; the percentages are cycles saved in the example or benchmark named (`tests/benchmarks/variants.md` has the benchmark pairs). They describe this compiler, not the language: as the optimizer improves, a rule may stop paying, so measure when it matters. The rules hold on every target, since all four have the same 6502 instruction set.

## 11.1 Arrays before pointers

An array indexed by a `u8` is `lda a,x`; a pointer access is `lda (p),y`, with the pointer to set up first. When the data fits in 256 elements, use an array, and for records an array of structs (one table per field) rather than a pointer to records:

```
struct Enemy { x: u8, y: u8, hp: u8 }
var enemies: [Enemy; 32]

for i in 0..32 { enemies[i].x += enemies[i].y }   // 45% faster than stepping a ptr Enemy
```

Copying 200 bytes, `dst[i] = src[i]` is 12% faster than `q[i] = p[i]` through pointers. Pointers are for blocks longer than 256 bytes and for data whose address is known only at runtime.

## 11.2 Through a pointer, index with Y and step by pages

Keep the pointer fixed while Y walks a page, then step the pointer by 256 (`p += 256` is `inc p+1`):

```
fn same(a: ptr u8, b: ptr u8, pages: u8) -> bool {
    for _ in 0..pages {
        for j in 0..=255 using Y {
            if a[j] != b[j] { return false }
        }
        a += 256
        b += 256
    }
    return true
}
```

Stepping the pointer for every byte (`p[0]`, then `p += 1`) takes nearly twice the cycles of `p[i]` in a `using Y` loop. Walking memory in small records (`p += 4`) instead of pages costs 15% in the `copy` benchmark (`copy.pages`).

## 11.3 Store a result before testing it

After `enemies[i].x = x`, A still holds `x`, so the tests that follow need no load. Testing first and storing afterwards loads it again: storing first saves 17% in the `enemies` benchmark (`enemies.first`).

```
var x: u8 = enemies[i].x + (enemies[i].dx as u8)
enemies[i].x = x                       // store first
if x < 8 || x > 200 { enemies[i].dx = -enemies[i].dx }
```

## 11.4 Leave a loop with `break` or `return`, not a flag

A `running` flag costs a store where it is set and a load and test on every pass. `while true` with `break` or `return` has neither: 5% in the `interp` benchmark (`interp.return`). `break` and `return` give the same code, also from inside a `match` arm (`HALT => break`).

## 11.5 Small functions in a hot loop: `inline fn`

A call costs `jsr` / `rts` (12 cycles) and a store for each argument. An `inline fn` puts the body in the caller, where the arguments often are already in registers, and a `bool` result becomes the caller's branch. Making three small functions inline saves 35% in the `calls` benchmark (`calls.inline`), and bytes too, since each is called once. Every call site gets its own copy, so a large function called from many places makes the program bigger.

## 11.6 `using` on a parameter, only where the body needs that register

A plain parameter is fixed memory in the function, and the caller stores the argument straight into it. `using` pays when the body needs the value in that register anyway: as an index, or used once in A.

```
var t: [u8; 200]
fn bump(i: u8 using X) { t[i] += 1 }     // inc t,x / rts: 9% faster than a plain i
```

A parameter used once in A (`n = n ^ v` with `v: u8 using A`) saves 16%. When the body compares or combines two parameters, one of them has to go to memory anyway, and plain parameters are as fast or faster: `fn bigger(a: u8 using A, b: u8 using X) -> bool { return a > b }` is 4% slower than with plain parameters.

## 11.7 Multiplying by a constant

`*` takes only constants; at runtime write shifts and adds, or use a table. Compute in `u8` while the value fits, then widen:

```
var w: u16 = (row << 2) + row          // row * 5 in 8 bits (row <= 51)
var offset: u16 = w << 3               // * 8 in 16 bits: row * 40
```

For `row * 40` over 25 rows, this is 58% faster than widening first (`(w << 5) + (w << 3)` with `w = row`) and 65% faster than `math.mul8(row, 40)`. A `data` table of the 25 results is faster still (`offset = rows[row]`, 57% faster than the shifts above), at two bytes per entry.

## 11.8 `match` or `indexed match`

A plain `match` tests its values in source order, so put the most common first. `indexed match` costs the same for every value (subtract the lowest value, then the table jump, about 25 cycles). With values spread evenly, a plain `match` is faster up to about 8 values (4 values: 26% faster than `indexed match`; 8 values: 7%); at 16 dense values `indexed match` is 26% faster and smaller. In the `interp` benchmark (10 opcodes) `indexed match` is 2% faster.

`indexed match` does not check its value ([10.14](10-behavior.md#1014-indexed-match-does-not-check-its-value)): list every value that can occur, or mask it into the span (`indexed match op & 15` with patterns 0 to 15). `_` catches only the values inside the span that no pattern lists.

## 11.9 Unroll a per-frame loop with a generator

The compiler never unrolls a loop by itself. When a loop must finish within a frame and does not, a generator ([4.3](04-compile-time.md#43-generators)) can write it out: the work stays the same, and the loop counters, pointer setup and `(p),y` go. In `examples/c64/speedcode.tern`, moving 22 rows of colour RAM one column left:

| Version | Bytes | Cycles |
|---|--:|--:|
| `_cycle_plain`: loops over rows and columns, through a pointer (kept for comparison; main does not call it, hence the `_`) | 66 | 29,537 |
| `cycle_unrolled`: a generator, one `lda` / `sta` pair per row in one column loop | 422 | 8,697 |

A PAL frame leaves the CPU about 19,000 cycles, so only the unrolled version moves the colours every frame, for 356 more bytes. Unroll only what must be fast: the bytes grow with every row. A branch reaches at most 128 bytes back, so the example emits a column loop for each group of up to 20 rows.

## 11.10 What not to rewrite

The optimizer already does these, so write the plain form:

- A local copy of an element (`var a: u8 = values[i]`, then `if values[i + 1] < a`) costs nothing: the compare reads the element directly (`cmp values,x`).
- `a[i + 1]` in a `for` loop whose bounds prove the sum does not wrap is `lda a+1,x`, with no add.
- Loop counters, and globals used by one function in its loop, stay in free registers across the loop.
- Variables used most go in zero page; arrays are placed so their indexed range does not cross a page when there is room.
- `return a > b` from a `-> bool` function is `lda a / clc / sbc b / rts`.

The `bubble.direct` (comparing the elements, no locals) and `bubble.minus` (`i - 1` instead of `i + 1`) forms of the `bubble` benchmark are not faster than the plain one.

## 11.11 Measuring

`tern bench` runs two or more versions in the built-in emulator and prints the bytes and cycles of each ([12. Running and measuring](12-run-and-bench.md) has every option, the output explained, and using a run as a test):

```
tern bench --target c64 --decl "var n: u8" --code "for i in 0..200 { n += i }" --code "for i in 0..200 using X { n += i }" --dump n
tern bench --target c64 old.tern new.tern --dump result        // whole programs; --fn f measures one function
tern run --target c64 game.tern --profile                      // the cycles of each function and source line
```

`--dump` shows a value after each run, to check that the versions agree. `--set $DC00=$7B` writes a byte before the run starts, for inputs such as the joystick; it takes addresses only, since the program's startup sets its own globals after loading and would overwrite a variable set by name.

`tern run` also checks every variable array index ([10.2](10-behavior.md#102-indexes-are-not-checked)). An index outside its array is printed with the source line, and the run goes on, so one run shows every bad access. An `indexed match` value outside its span is printed and stops the run. The exit code is 0 when `main` returned with no error, 3 when `main` returned but the check reported an error, and 2 when `main` did not return (the cycle limit, or a stop after a bad `indexed match` value). Pointer accesses and `asm` are not checked.

The emulator is a 6502 with plain RAM and stand-ins for a few ROM routines on each machine; the chips are not emulated. What each target has: [C64](06-c64.md#67-tern-run-on-the-c64), [Atari](07-atari.md#76-tern-run-on-the-atari), [Apple II](08-apple2.md#85-tern-run-on-the-apple-ii), [NES](09-nes.md#97-tern-run-on-the-nes). `--screen` shows the text screen (on the NES, nametable 0).

A run does not start from a cleared machine. Memory outside the program is filled with bytes from a fixed seed, and A, X, Y and the carry, overflow and negative flags are not zero (decimal mode is off), so a program that reads memory it never wrote, or a register it never loaded, goes wrong in `tern run` as it would on the machine, rather than getting a lucky 0. The seed is fixed, so every run repeats exactly. The text screen starts blank, and input reads as idle: no key pressed, joysticks centered, buttons up (each machine's section lists the values). Give input with `--set`. Other I/O registers and OS variables hold the seeded bytes.
