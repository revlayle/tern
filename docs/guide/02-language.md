# 2. The language

[Guide index](README.md) · previous: [1. Getting started](01-getting-started.md) · next: [3. Memory](03-memory.md)

## 2.1 Source text

- Files are UTF-8 with the extension `.tern`. Comments are `//` and `/* */`.
- A statement ends at a newline; `;` is optional. A newline inside `( )` or `[ ]` does not end a statement, so argument lists and tables can span lines. A line that ends with a binary operator continues on the next one, so split a long condition after `&&` or `||`, not before.
- Indentation has no meaning. A block's `{` may go on the same line as its header or on the next line, so both brace styles work:

```
fn main() {
    if x == 0 { reset() }
}

fn main()
{
    if x == 0
    {
        reset()
    }
}
```

## 2.2 Types and arithmetic

| Type | Size | Notes |
|---|---|---|
| `u8`, `i8` | 1 | The default numeric types |
| `u16`, `i16` | 2 | Little endian; `.lo` and `.hi` give the bytes |
| `bool` | 1 | Can also live in a CPU flag ([2.8](#28-flags-as-values)) |
| `ptr T` | 2 | Pointer to `T` |
| `[T; N]` | | Array, up to 256 elements |

Compile-time code has two more types, `int` and `real`, which never reach the 6502 ([4. Compile-time code](04-compile-time.md)). Larger numbers and floats are library structs: `wide.U32`, `float.F32` ([5. Standard library](05-library.md)).

- All arithmetic **wraps**, and there is no integer promotion: `u8 + u8` is a `u8` ([10.1](10-behavior.md#101-everything-wraps)).
- `u8` mixed with `u16` widens to `u16`. Narrowing needs `as`: `x = big as u8` keeps the low byte.
- Mixing signed and unsigned is an error; cast one side.
- A `bool` is not a number: `b as u8` gives 0 or 1, and a literal is written as the number (`1`, not `true`). A number is not a `bool` either: an `if` or `while` condition must be a `bool`, and `u8 as bool` is an error. Write the compare, `if x != 0`, which costs the same as a C-style test (`lda x` / `beq skip`). For signed types `x > 0` is not the same test, since a negative `x` is not zero.
- Constants follow their own rules: they are exact, and they take a type only where they are used ([2.4](#24-constants)).
- `*`, `/` and `%` work only on constants (`const SIZE = 40 * 25`). At runtime, use shifts or the `math` library:

```
import math

var row: u8 = 7
var col: u8 = 3

fn main() {
    var w: u16 = row
    var offset: u16 = (w << 5) + (w << 3) + col   // row * 40 + col, inline shifts
    var product: u16 = math.mul8(row, col)        // a library call: cost is visible
    var q: u8 = math.div8(200, row)
    var r: u8 = math.remainder8                   // division leaves the remainder here
}
```

- Shifts need a constant count. `>>` on a signed value keeps the sign and rounds down (`-3 >> 1` is `-2`).
- Literals: `200`, `$C000`, `0b1010_0101`, `'A'`, `"TEXT"`, `true`. A literal takes its type from where it is used, and one that does not fit is an error. Real numbers (`1.5`, `2e3`) exist only in compile-time code.
- Strings and characters are in the target's encoding by default: PETSCII on the C64, ATASCII on the Atari, `apple2` (ASCII with bit 7 set) on the Apple II; the NES has none, so a NES program declares one ([2.12](#212-encodings)). A prefix picks another encoding: `screen"HELLO"` gives C64 screen codes for writing straight to screen memory, `petscii_mixed"Hello"` the lower-case set, `atari_screen"HELLO"` the Atari's screen codes. Strings have no terminator unless you write one (`"HI\0"`).

## 2.3 Declarations

```
const SCREEN = $0400               // compile-time constant, no memory
var score: u16                     // placed by the compiler, zeroed at startup
var lives: u8 = 3                  // set at startup
var p: ptr u8 @ zp                 // somewhere in zero page, guaranteed
var border: u8 @ $D020             // fixed address; I/O is volatile automatically
volatile var flag: u8 @ $C000      // RAM that something else changes
noinit var buffer: [u8; 256]       // not zeroed at startup
noinit var page: [u8; 256] @ align(256)   // at an address that is a multiple of 256
data msg = screen"GAME OVER"       // read-only bytes in the program
data ship = include_bytes("ship.bin")
```

- `@` only ever means "located at": an address, `zp`, `align(n)`, or a named region ([3.2](03-memory.md#32-the-memory-block)).
- `@ align(n)`, with `n` a power of two from 2 to 32768, places a global `var` or `data` where the compiler would, but aligned: 256 for a page-sized buffer, so no indexed read crosses a page, 64 bytes for C64 sprite data, 2 KB for a character set. It costs no code; the padding before it shows in the report ([10.16](10-behavior.md#1016-alignment-leaves-padding)).
- Locals (other than arrays) need an initial value: their memory is shared with other functions, so it holds leftovers on entry.
- Volatile memory (I/O, `volatile` variables, anything shared with an interrupt, anything through a pointer) is read and written exactly as written; ordinary memory may be kept in registers by the optimizer ([10.13](10-behavior.md#1013-a-fixed-address-is-ordinary-memory-unless-it-is-io)).

## 2.4 Constants

A `const` is a number the compiler knows. It takes no memory and has no code of its own: wherever it is used, the compiler puts the number in, as if you had written it there. A `const` may also be declared inside a function; it is visible to the end of its block.

```
const SCREEN = $0400            // no type: a plain number
const BOX: u8 = 13              // a written type: checked to fit in u8
const MAX_X: u16 = 320 - BOX    // 307
const SECS: u8 = 400 / 50       // 8, though 400 does not fit in u8
const AREA = BOX * 30           // 390, a plain number
```

**Between constants, arithmetic is exact.** Literals and constants are computed at compile time as whole numbers of any size, with no wrapping, whatever types the parts have. Only the final value is checked, where it is used:

- `const B: u8 = BOX + 250` is an error: 263 does not fit in `u8`. Write `(BOX + 250) as u8` to keep the low byte on purpose.
- `*`, `/` and `%` are allowed here, since the compiler does the work.
- A constant built from typed constants is a plain number unless you write a type: `AREA` above is 390, not a `u8`.

**A constant gets a runtime type only where code needs one:**

1. **From where it goes**: a variable, an argument, an array element, a pointer. The value must fit there: `var s: u16 = AREA`, `p = SCREEN + 40` (no `as` needed for a pointer), `var t: i8 = BOX` (13 fits in `i8`).
2. **From the operand next to it**: with `x: u8`, `x + 3` is a `u8` addition, and `x + AREA` is an error because 390 does not fit in `u8`. With `w: u16`, `w + AREA` adds 390.
3. **With nothing to go by** (`var v = AREA`, a `for` bound): the smallest type that holds it, as for a literal: `u8` for 0 to 255, `u16` up to 65535, `i8` or `i16` for negative values. So `var v = AREA` is a `u16`.

**What a written type adds.** `const BOX: u8 = 13` is checked to fit in `u8` where it is declared, and then counts as a `u8` wherever a variable's type would matter:

- next to a runtime value, it takes part in choosing the operation's type: `x + BIG` with `x: u8` and `const BIG: u16 = 200` is a 16-bit addition, where `x + 200` would be an 8-bit one,
- signed and unsigned do not mix: with `const N: i8 = 5`, `x + N` is an error for `x: u8`, as with a variable,
- with nothing to go by, it is its type: `var v = BOX` is a `u8`, and an `i8` constant as a `for` bound makes an `i8` counter.

A written type never cuts a constant down. Next to a wider value, the operation is wide, and the constant is computed at that width.

**`~` and negative values take the width they are used at.** `~$0F` is "all bits except the low four", as wide as needed: `x & ~$0F` on a `u8` is `and #$F0`, and `w & ~$0F` on a `u16` clears the low four bits of both bytes. A typed constant behaves the same way, and a compound assignment is the operation it stands for:

```
const M: u8 = $0F
var w: u16 = $1234
var m: u8 = $0F

fn main() {
    w = w & ~M       // $1230: ~M is $FFF0 here, as ~$0F would be
    w &= ~M          // the same: 'w &= e' is 'w = w & e'
    w = w & ~m       // $0030: m is a variable, so ~m is the u8 $F0, widened to $00F0
}
```

The last line is the one place where a constant and a variable of the same type differ: a runtime `~` works on its operand's 8 bits and then widens. To clear bits of a `u16` with a variable mask, widen the mask first: `w & ~(m as u16)`.

A constant may also hold a real number (`const SCALE = 1.5`, `const K = PI / 128`), with no type written, for use in compile-time code only. A constant never holds an array: a table the program reads is `data`.

## 2.5 Arrays, structs and pointers

**Arrays** are indexed with a `u8`, which compiles to `abs,X` or `abs,Y`. An array of 16-bit values is stored as two tables, low bytes and high bytes, so `a[i]` stays one `lda a_lo,x` / `lda a_hi,x`. Write `linear var` for the usual interleaved layout when hardware or assembly needs it. An index is not checked ([10.2](10-behavior.md#102-indexes-are-not-checked)).

**Structs** describe a memory layout and are always used in place. They are never copied, passed or compared: pass a pointer or an index instead.

```
struct Vic { sprite0_x: u8 @ $00, border: u8 @ $20, background: u8 @ $21 }
var vic: Vic @ $D000               // a struct over hardware registers
vic.border = 0                     // sta $D020

struct Enemy { x: u8, y: u8, hp: u8 }
var enemies: [Enemy; 8]            // stored as one table per field
enemies[i].x = 10                  // lda #10 / ldx i / sta enemies_x,x

var e: ptr Enemy @ zp
e.hp -= 1                          // ldy #2 / lda (e),y / sec / sbc #1 / sta (e),y
```

**Struct literals** give a global struct, a global array of structs or `data` its initial bytes: the struct's name, then `field = value` for the fields to set; the others are 0. The tables are built at compile time in the array's layout, so a list of records costs the same as parallel arrays. `[T; _]` takes the length from the list, and `[u8; _]` from a string (`var text: [u8; _] = screen"GALAXY 00\0"` has 10 bytes). In code, assign the fields one by one.

```
struct Line { row: u8, col: u8, text: ptr u8 }
data TITLE = screen"TITLE"
data lines: [Line; _] = [
    Line { row = 1, col = 14, text = addr(TITLE) },
    Line { row = 3 },                              // col and text are 0
]
```

**Pointers**: `addr(x)` gives a pointer to a variable, array or field; `p[i]` reads through it with `(zp),Y` (there is no `*p`, write `p[0]`). Arithmetic is in bytes: `p + sizeof(Enemy)` steps one record. Any pointer converts to any other pointer type with `as`, at no cost. A pointer becomes a `u16` by itself where a `u16` is expected (`var a: u16 = addr(map)`, an argument, a `[u16; n]` table), but not inside an operator: write `p as u16 == w`. A `u16` variable needs `as` to become a pointer (`w as ptr u8`); a number or constant does not. Pointers that are dereferenced are placed in zero page automatically.

## 2.6 Control flow

```
if x < 10 { ... } else if x == 20 { ... } else { ... }

while lives != 0 { ... }
while true { ... }                 // the endless loop; leave with break or return

for i in 0..10 { ... }             // 0 to 9
for i in 9..=0 step -1 { ... }     // 9 down to 0
for i in n..0 step -1 { ... }      // n down to 1
for x in 0..40 step 8 { ... }      // 0, 8, 16, 24, 32
for i in -10..10 { ... }           // a signed counter: i8
for i in 0..len using X { ... }    // the counter lives in X
for _ in 0..10 { ... }             // repeat 10 times

match state {
    0 => idle()
    1, 2 => walk()
    3..=7 => attack()
    _ => {}
}

indexed match op & 3 {             // a jump table: the value is not checked
    0 => push()
    1 => pop()
    2 => add()
    3 => halt()
}
```

- `for` bounds are evaluated once, and the loop variable is read-only in the body. The compiler picks the loop shape (counting down with `dex` / `bne` when it can).
- **A loop counts up unless it has a negative `step`.** The bounds never decide the direction, so `for i in 9..=0` is an error ("'9..=0' counts up, so it never runs. Write 'step -1' to count down"), as is an unsigned `n..0`, which can never run either. A loop whose variables decide whether it runs is allowed: `for i in n..=0` runs once when `n` is 0, and not at all otherwise.
- The counter is a `u8` unless the values it takes need more (`0..256` counts 0 to 255 with a `u8`): `u16` for larger values, `i8` or `i16` when a bound is negative or signed. A step may be a `u8` variable when the counter is a `u8` that counts up (`for m in (n << 1)..=255 step n`); a step that is 0 at runtime never ends the loop ([10.4](10-behavior.md#104-loops-with-a-step)).
- `match` works on `u8` and `i8`. A plain `match` is a chain of compares, tested in source order. `indexed match` is a jump table over the span from the lowest pattern value to the highest; a value outside the span is not checked, so list every value that can occur or mask the value (`op & 3`). There is no fall-through. Which to choose: [11.8](11-fast.md#118-match-or-indexed-match).
- `&&`, `||`, `!` are logical (on `bool`, short-circuit); `&`, `|`, `^`, `~` are bitwise. `x += 1` on memory is `inc x`.

## 2.7 Functions

```
fn add(a: u8, b: u8) -> u8 { return a + b }
fn plot(x: u8 using X, y: u8 using Y) { ... }
inline fn acknowledge() { c64.vic.irq_status = 1 }
```

- Parameters without `using` are fixed memory in the function; the caller stores the arguments straight into them and calls. `using A`, `X`, `Y` (or a pair `AX`, `AY`, `XY` for 16 bits) passes them in registers.
- Results come back in A (8 bits), A and X (16 bits) or the carry (`bool`). Other placements can be written: `-> u8 using Y`, `-> u16 using XY`, `-> bool using zero` or `negative`. `-> bool using overflow` (the V flag) is for `asm fn`s and routines at a fixed address only, since no instruction sets V on its own.
- `inline fn` copies the body into each caller.
- Recursion is a compile error. Naturally recursive algorithms use an array as an explicit stack.

**Routines that already exist**, such as ROM calls, are declared without a body at their address. A call is a direct `jsr`:

```
fn chrout(ch: u8 using A) @ $FFD2 keeps X, Y
fn get_cursor(true using carry) -> u16 using XY @ $FFF0   // the carry is a fixed input

chrout('A')                        // lda #$41 / jsr $FFD2
```

`keeps` lists the registers the routine leaves alone; the compiler trusts it ([10.6](10-behavior.md#106-promises-the-compiler-trusts)).

A loop with its counter in a register (`for ... using Y`) may call only functions that keep that register. A function with a body says so after its return type, and the compiler checks it: the body indexes with the other register, and a statement that needs Y (`p[i]`), a call to a function that does not keep Y, or a result in Y is an error at that statement.

```
fn hits(o: u8) -> bool using carry keeps Y {
    return xs[o] >= limit            // indexes with X, leaves Y alone
}

for s in 0..SLOTS using Y {
    if hits(slot[s]) { ... }         // allowed: hits keeps Y
}
```

On an `asm fn`, `keeps` is a promise, as on a routine at a fixed address. An `interrupt fn` restores its registers anyway, so `keeps` on one is an error.

## 2.8 Flags as values

A `bool` can live in a CPU flag, which removes the "load 0 or 1, then compare" step:

```
fn below_line(y: u8 using A) -> bool using carry {
    return y >= 200
}

inline asm fn fire_pressed() -> bool using zero {
    lda #$10
    bit $DC00          // joystick 2 fire: bit 4 is 0 while pressed
}

if fire_pressed() { shoot() }      // lda #$10 / bit $DC00 / bne skip
```

An empty body reads the flag that the code before it left, at no cost. That is defined only right after an `asm` block: the `cpu` module has `carry()`, `zero()`, `negative()` and `overflow()` for it ([10.3](10-behavior.md#103-flags-after-an-asm-block)).

```
asm { jsr read_byte }              // carry set at the end of the file
if cpu.carry() { finish() }        // bcc skip, nothing more
```

## 2.9 Interrupts

`interrupt fn` saves only the registers it changes and ends with `rti`. It is its own root: its memory never overlaps main's.

**Functions called from main and from a handler.** The compiler allows this, but the function has one set of variables, not one per caller. If the interrupt arrives while main is inside the function, the handler's call overwrites main's parameters, locals and the compiler's hidden temporaries (a 16-bit sum needs one); when the handler returns, main's call goes on with the handler's values. Nothing fails at once: the bug shows only when the interrupt lands inside that function, perhaps once a minute, so plan for it when you write the code. A function with no memory of its own (like `acknowledge` above) is always safe. Otherwise pick one of these:

- **Interrupts off around main's calls.** `asm { sei }`, the call, `asm { cli }`. Simple, but the interrupt waits until `cli`; a raster interrupt delayed this way shows on screen.
- **`inline fn`.** Each caller gets its own copy of the code, and its variables go in that caller's frame, so main's copy and the handler's never touch each other. Best for small helpers:
  ```
  inline fn next_x(x: u8) -> u8 {      // one copy in main, one in the handler
      var t = x + 1
      if t >= 160 { t = 0 }
      return t
  }
  ```
- **No memory of its own.** Parameters in registers (`using A`), loop counters with `using X` or `using Y`, no locals: the handler saves the registers, so sharing is safe.
- **A second function for the handler.** For anything larger. The handler's version is often shorter, since it rarely needs everything main does.

The listing's frame map shows what memory a function has. One `interrupt fn` called from another becomes a *part* (no saving, ends with `rts`), which is how the library's raster and vblank code is combined into one handler you install.

A handler starts with `cld` only when it can do decimal arithmetic: when its code or asm blocks have `adc` or `sbc`, or it calls anything (`jsr`). A handler without them leaves decimal mode alone, which is safe since nothing in it depends on it ([10.7](10-behavior.md#107-decimal-mode-in-handlers)).

How a handler is installed depends on the machine: `c64.set_irq` ([C64](06-c64.md#64-interrupts)), `atari.set_dli` and `atari.set_vbi` ([Atari](07-atari.md#74-interrupts)), and on the NES `data` at the vector's address ([NES](09-nes.md#93-interrupt-vectors)). The Apple II has no interrupt source of its own, and its library has no interrupt helpers yet.

## 2.10 Inline assembly

```
asm {
    lda score
    clc
    adc #10
    sta score
    bcc .done
    inc score+1
.done:
}

asm fn fill(value: u8 using A, count: u8 using X) {
.loop:
    sta buffer-1,x
    dex
    bne .loop
}
```

- Operands may name Tern variables, constants, arrays, struct fields (`sta c64.vic.border`) and functions. `#name.lo` / `#name.hi` take the bytes of an address (not `<` / `>`). Through a pointer, `ldy #offset(Enemy.hp)` / `lda (e),y` names the field's offset instead of hard-coding it.
- A line may call a generator, a `const fn` that writes instructions in its place ([4.3](04-compile-time.md#43-generators)).
- Labels starting with `.` are local to the block. `;` is not a comment here; use `//`.
- The compiler works out which registers and variables a block uses and works around it. It never changes your instructions.
- An `asm fn` (or `inline asm fn`) body may start with `var` lines for its work bytes, with no initial value and no `@`. They are the function's own locals, in its frame, so they share memory with functions that never run at the same time; a pointer used as `(p),y` goes in zero page. An `asm { }` statement has none: declare the variable before the block.

```
asm fn clear_row(row: ptr u8 using AY) {
    var p: ptr u8
    sta p
    sty p+1
    lda #32
    ldy #39
.loop:
    sta (p),y
    dey
    bpl .loop
}
```

## 2.11 Modules

```
import c64
import text
import sprites(COUNT = 24)         // a module parameter, set by the main file

fn main() {
    text.put(c64.SCREEN, addr(title), len(title))
}
```

- `import name` loads `name.tern` from the target's library folder (`lib/c64/` in a C64 build), the standard library (`lib/`), then your program's folder. So `import io` is the C64's `io` in a C64 build and the Apple II's in an Apple II build. Names are always used with the module name: `text.put`.
- Your own module may not have a library module's name: a `random.tern` or `io.tern` next to your program is an error when you import it, naming both files. Rename yours. (A main file may have any name, since it is never imported.)
- `private` before a declaration hides it from other modules.
- Modules may import each other, and declarations may be used in any order, also across modules: `const KINDS: u8 = len(star_char)` works wherever `star_char` is declared. Only a constant that depends on itself is an error.
- The main file cannot be imported. Put what other modules need from it in a module that both import.
- Only what your program reaches is compiled: an unused import adds no bytes.
- An `if` on a constant compiles only the branch taken, and a function called only from the other branch is left out entirely, with no code and no memory. So a module option that is off costs nothing:

```
const DEBUG = false

fn main() {
    if DEBUG { show_timings() }    // no code, and show_timings is not compiled
}
```
- A module can take parameters in a header line, `module sprites(COUNT: u8 = 32, FLICKER: bool = false)`, and check them with `assert COUNT >= 8, "message"` at compile time.

## 2.12 Encodings

A game with its own character set says which byte each character is, and its text stays readable. The name of the encoding is then a prefix, like `screen`:

```
encoding hud {
    ' '       = 0
    '0'..='9' = 1               // a range: consecutive bytes from 1, so '0' + digit is the digit
    'S' = 11, 'C' = 12, 'O' = 13, 'R' = 14, 'E' = 15
    '♥'       = 16
}

data LABEL = hud"SCORE"         // 11, 12, 13, 14, 15
screen_row[i] = hud'0' + digit
```

- `encoding hud: screen { '♥' = $53 }` starts from another encoding, built in or your own, and changes or adds the characters listed.
- A character the encoding does not list is an error that suggests `\$NN`, a raw byte. A range is written with `..=` and goes up.
- `default encoding hud` in the main file makes it the encoding of every literal without a prefix, in the main file and your own modules. Library modules keep the platform's encoding, so a program's default never changes their text.
- An encoding in a module is used with the module's name: `font.hud"HI"`, so a font and its encoding can live in one file (`examples/c64/hudfont.tern`, used by `statusline.tern`). `private encoding` keeps one inside its module.
- An encoding's name may not be used for anything else in the file, a `chr` area included.
- The NES has no character set, so a NES program always declares one ([NES](09-nes.md)). An encoding takes no memory and costs nothing at runtime.

The built-in encodings:

| Encoding | Machine | Notes |
|---|---|---|
| `petscii` | C64 | The C64's default. Upper case and graphics; `a`-`z` give the same codes as `A`-`Z` |
| `petscii_mixed` | C64 | The lower/upper-case character set |
| `screen`, `screen_mixed` | C64 | Screen codes, for writing straight to screen memory |
| `atascii` | Atari | The Atari's default |
| `atari_screen` | Atari | The Atari's screen (internal) codes |
| `apple2` | Apple II | The Apple II's default: ASCII with bit 7 set, what COUT prints and screen memory shows |
| `ascii` | any | Plain 7-bit ASCII |

## 2.13 Built-ins

| Built-in | Meaning |
|---|---|
| `addr(x)` | Pointer to a variable, field or array; address of a function |
| `sizeof(T)` | Size of a type in bytes |
| `len(a)` | Length of an array or string, a constant |
| `offset(T.field)` | Offset of a field from the start of a struct, a constant: `offset(Enemy.pos.x)` |
| `include_bytes("file")` | A file's bytes as the initial value of `data`, path relative to the source |
| `repeat(v, n)` | An array of `n` copies of `v`, in compile-time code only: `data spaces: [u8; 40] = repeat($20, 40)` |

The math functions of compile-time code are in [4.1](04-compile-time.md#41-const-fn).
