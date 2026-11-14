# 5. Standard library

[Guide index](README.md) · previous: [4. Compile-time code](04-compile-time.md) · next: [6. Commodore 64](06-c64.md)

The standard library is ordinary Tern and assembly in `lib/` next to the compiler (source in `src/lib/`), imported like your own modules. It gets no special treatment, so you can read it, copy it and change it. This chapter covers the modules that work on every target; each machine's own modules are in its chapter ([C64](06-c64.md), [Atari](07-atari.md), [Apple II](08-apple2.md), [NES](09-nes.md)). Every signature, with costs, is in spec section 10.1.

Two rules hold throughout:

- **Strings and blocks are a pointer and a length**: `text.put(dst, addr(s), len(s))`. There are no zero-terminated variants.
- **Larger values are passed as pointers, and the result goes into the first operand**, as in assembly: `wide.add32(addr(score), addr(bonus))` is `score += bonus`.

## 5.1 Overview

| Module | What it has |
|---|---|
| `math` | `mul8`, `mul8_fast` (table based), `mul16`, `div8`, `div16` (remainders in `math.remainder8/16`), variable shifts, signed `muls8`/`divs8`, `abs8`/`abs16`, inline `min8`/`max8`/`clamp8` (and signed), `sin8`/`cos8` |
| `mem` | `copy`, `copy_back`, `fill` |
| `text` | `put`, decimal (`dec8`, `dec16`, signed, fixed width with leading zeros), hex, `equal`, `find` |
| `wide` | 24- and 32-bit unsigned numbers (`U24`, `U32`): add, subtract, compare, multiply, divide, decimal and hex output |
| `bcd` | Packed decimal counters of any length, for scores: add, subtract, compare, digits |
| `float` | 4-byte floating point (`F32`): add, subtract, multiply, divide, compare, conversions, rounding, decimal text, and `float.of` for constants |
| `random` | 8- and 16-bit xorshift, `below(n)` |
| `cpu` | `carry()`, `zero()`, `negative()`, `overflow()`: the flags an `asm` block left, at no cost ([2.8](02-language.md#28-flags-as-values)) |

## 5.2 `math`

Multiply, divide and variable shifts are loops, so they are calls, never operators. `mul8_fast` uses 1 KB of quarter-square tables (only when called) and is much faster than `mul8`. Division leaves the remainder in `math.remainder8` or `math.remainder16` for the caller to read straight after; division by zero gives an unspecified result. `min8`, `max8`, `clamp8` and their signed forms are `inline asm fn`s with no `jsr`. `sin8(x)` and `cos8(x)` take 256 steps per circle and give -127 to 127.

Multiplying or dividing by a constant needs no call: write shifts and adds ([11.7](11-fast.md#117-multiplying-by-a-constant)).

## 5.3 `mem` and `text`

`mem.copy` copies forward (overlapping blocks only when the destination is below the source), `mem.copy_back` last byte first, `mem.fill` fills; counts are `u16`.

`text` writes numbers and text into memory: `put` copies text (`text.put(c64.SCREEN, addr(title), len(title))`), `dec8` and `dec16` write a number without leading zeros and return its length, `dec8z`/`dec16z` a fixed width with leading zeros, `dec8s`/`dec16s` signed numbers, `hex8`/`hex16` hex digits. Digits are in the codes the platform prints: screen codes (the same as PETSCII for `0`-`9`) on the C64, ATASCII on the Atari, `apple2` codes on the Apple II. Decimal output uses repeated subtraction, not division.

## 5.4 `wide`

`struct U32 { lo: u16, hi: u16 }` and `struct U24 { lo: u16, hi: u8 }`, little endian; each function exists for both widths (`add24`, `add32`, ...).

```
import wide

var score: wide.U32
var bonus: wide.U32
var digits: [u8; 10]
var n: u8

fn main() {
    wide.set32(addr(score), 60000)
    wide.set32(addr(bonus), 50000)
    wide.add32(addr(score), addr(bonus))          // score = 110000
    n = wide.dec32(addr(digits), addr(score))     // "110000", n = 6
}
```

Decimal output converts to BCD and costs thousands of cycles: convert when the value changes, or keep a score that is only displayed in `bcd`.

## 5.5 `bcd`

Packed decimal counters of `n` bytes, two digits per byte, low byte first: `bcd.add_byte(addr(points), 3, $25)` adds 25 to a 6-digit counter, and `bcd.digits` writes all its digits. Values wrap at 10^(2n). The routines use decimal mode and always leave it off.

## 5.6 `float`

`struct F32 { bytes: [u8; 4] }`: about 7 significant digits, from about 2.9e-39 to 1.7e38, in a format that suits the 6502 (as Microsoft BASIC's). No NaN or infinity: a result too large becomes the largest value and sets `float.overflow`, which stays set until the program clears it.

```
import float

var v: float.F32 = float.F32 { bytes = float.of(1.5) }
var g: float.F32 = float.F32 { bytes = float.of(9.81) }
var whole: i16

fn main() {
    float.mul(addr(v), addr(g))                   // v = 14.715
    whole = float.round_i16(addr(v))              // 15
}
```

`float.of` is a `const fn`: it computes a constant's four bytes at compile time. `float.dec(dst, p, decimals)` writes a value as text. With every routine used, the library is about 2,450 bytes; a program pays only for the routines it calls. Its work bytes are private, so use it from main or from one interrupt handler, not both. `examples/c64/floats.tern` shows more.

## 5.7 `random`

`random.seed(v)` sets the state, `next8()` gives 1-255 (period 255), `next16()` a 16-bit value (period 65535), and `below(n)` a value from 0 to `n - 1` (not exactly uniform). The state is private, so main and a handler cannot both use it; a handler that needs random numbers uses its own generator.

## 5.8 `cpu`

`cpu.carry()`, `cpu.zero()`, `cpu.negative()` and `cpu.overflow()` are empty-body `inline asm fn`s: `if cpu.carry()` right after an `asm` block is just `bcc`. They are defined only right after an `asm` block ([10.3](10-behavior.md#103-flags-after-an-asm-block)).
