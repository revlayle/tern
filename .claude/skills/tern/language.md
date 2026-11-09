# The Tern language in brief

Each example is a whole program that compiles. The full rules are in the Tern repository's
`docs/spec-v0.md`; `docs/guide.md` is the user guide.

## 1. Source text

- Files are UTF-8, extension `.tern`. Comments `//` and `/* */`.
- A statement ends at a newline; `;` is optional and separates statements on one line. A newline
  inside `( )` or `[ ]` does not end a statement. A `{` may be on the header's line or the next.
- Literals: `200`, `$C000`, `0b1010_0101`, `'A'` (PETSCII), `"TEXT"`, `true`, `false`. A literal
  takes its type from where it is used; one that does not fit is an error (`300` into a `u8`).
- Strings are PETSCII; `screen"HI"` gives screen codes, `petscii_mixed"Hi"` the lower-case set. No
  terminator unless written: `"HI\0"`.

## 2. Types and arithmetic

| Type | Size | Notes |
|---|---|---|
| `u8`, `i8` | 1 | the default numeric types |
| `u16`, `i16` | 2 | little endian; `w.lo`, `w.hi` are the bytes |
| `bool` | 1 | 0 or 1 in memory; can live in a flag (section 9) |
| `ptr T` | 2 | an address; `ptr u8`, `ptr Enemy` |
| `[T; N]` | | array, N from 1 to 256 |

- Operators: `+ - & | ^ ~ << >>`, compares `== != < <= > >=`, logical `&& || !` (on `bool`, short
  circuit). Assignments `= += -= &= |= ^= <<= >>=`. No `++`, no `*p`, no `?:`.
- Precedence, tightest first: `as`; unary `- ~ !`; `* / %`; `+ -`; `<< >>`; `&`; `^`; `|`; the
  compares; `&&`; `||`. Unlike C, `flags & 4 != 0` is `(flags & 4) != 0`. As in C, `w << 5 + 3`
  is `w << 8`: write `(w << 5) + 3`. Compares do not chain (`a < b < c` is an error).
- Everything wraps; `u8 + u8` is a `u8`. `u8` mixed with `u16` widens to `u16`.
- `as` converts: `w as u8` (low byte), `b as u16`, `x as i8` (same bits), `p as u16`, `p as ptr Enemy`.
  `as` binds tighter than any operator. There is no `u8 as bool`: write `x != 0`.
- Signed and unsigned operands cannot be mixed: cast one side.
- `>>` on a signed value keeps the sign and rounds down (`-3 >> 1` is `-2`).
- `*`, `/`, `%` only in constant expressions. Shift counts are constants.
- A constant expression (literals and constants, typed or not) is computed exactly and checked only
  where it is used: with `const BOX: u8 = 13`, `const MAX_X: u16 = 320 - BOX` is 307, and
  `CHARSET + FIRST * 8` converts to a pointer. Mixed with a runtime value, a typed constant keeps its
  type (`x + BOX` is a `u8`). `~$0F` is as wide as what it is used with: `x & ~$0F` is `and #$F0`.

```tern
// arith.tern
import math

var row: u8 = 7
var col: u8 = 3
var offset: u16
var product: u16
var q: u8
var r: u8
var low: u8

fn main() {
    var w: u16 = row
    offset = (w << 5) + (w << 3) + col      // row * 40 + col
    product = math.mul8(row, col)            // a library call: its cost is visible
    q = math.div8(200, row)
    r = math.remainder8                      // div8 leaves the remainder here
    low = offset as u8
}
```

```console
$ tern run arith.tern --dump offset --dump product --dump q --dump r --dump low
Main returned: 606 cycles (554 in main)
A=$1B X=$00 Y=$00 SP=$FF PC=$0831
offset $0007 u16 = 283 ($011B)
product $0009 u16 = 21 ($0015)
q $000B u8 = 28 ($1C)
r $000C u8 = 4 ($04)
low $000D u8 = 27 ($1B)
```

## 3. Declarations

```tern
// decl.tern
const SCREEN = $0400               // a constant: no memory
const WIDTH: u8 = 40

var score: u16                     // placed by the compiler, zeroed at startup
var lives: u8 = 3                  // set at startup
var p: ptr u8 @ zp                 // somewhere in zero page, guaranteed
var border: u8 @ $D020             // a fixed address; I/O is volatile by itself
volatile var flag: u8 @ $C000      // RAM that something else changes
noinit var buffer: [u8; 256]       // not zeroed at startup
data msg = screen"GAME OVER"       // read-only bytes in the program
data table: [u8; 4] = [1, 2, 4, 8]

fn main() {
    var count: u8 = 0              // a local needs an initial value
    p = SCREEN
    for i in 0..len(msg) {
        p[i] = msg[i]
        count += 1
    }
    score = count as u16 + table[3]
    border = lives
}
```

- `@` always means "located at": an address, `zp`, or a region of the `memory` block.
- Globals with no initial value are zeroed at startup, so `x = 0` at the start of `main` costs nothing.
- A local's memory is shared with functions that never run at the same time, so it holds leftovers on
  entry: scalars need an initial value. Local arrays do not, and hold leftovers.
- `noinit` skips the zeroing (not on locals, `data` or fixed addresses).
- `data` cannot be written. `include_bytes("ship.bin")` loads a file: `data ship = include_bytes("ship.bin")`.
- `[u8; _]` takes the length from the list, for a global `var` or `data` with a list: `data script: [u8; _] = [3, 1, 4]`.
- An initial value may hold addresses, filled in when the program is placed: `addr()` of a global, data,
  a field of a struct global or a function, alone, plus or minus a constant, or its `.lo` / `.hi`:
  `data rows: [ptr u8; 2] = [addr(map), addr(map) + 40]`, `data jlo: [u8; 1] = [(addr(f) - 1).lo]`.

## 4. Arrays

- `a[i]` with `i: u8` is `lda a,x` or `lda a,y`. A constant index is a fixed address (`lda a+3`).
- An index wraps: with `head = 255`, `buf[head + 1]` is `buf[0]`. The program never checks it;
  `tern run` reports an index outside its array (section 1 of `SKILL.md`).
- Arrays of `u16` (and pointers) are stored as two tables, low bytes and high bytes, so `a[i]` is
  `lda a,x` / `lda a+n,x`. `linear var pts: [u16; 32]` stores them interleaved, for hardware or
  assembly that needs it, and costs an `asl` per index.
- A byte array may be longer than 256 (`var map: [u8; 2048]`, or `data` from `include_bytes`), but it
  cannot be indexed: use a pointer, `var p: ptr u8 = addr(map) + 256`, then `p[44]` is byte 300.
- `len(a)` is the length, a constant.

```tern
// ring.tern
var buf: [u8; 256]
var head: u8 = 255
var speeds: [u16; 8]
var total: u16

fn main() {
    buf[head + 1] = 42               // wraps to buf[0]
    for i in 0..8 { speeds[i] = (i as u16) << 8 }
    for i in 0..8 { total += speeds[i] }
}
```

```console
$ tern run ring.tern --dump 'buf:2' --dump total --dump speeds
Main returned: 3905 cycles (1160 in main)
A=$1C X=$08 Y=$00 SP=$FF PC=$082D
buf $0910 [u8; 256] (2 bytes):
  $0910  2A 00
total $0003 u16 = 7168 ($1C00)
speeds $0900 [u16; 8] (16 bytes):
  $0900  00 00 00 00 00 00 00 00 00 01 02 03 04 05 06 07
```

A dump of a `u16` array shows its memory: the 8 low bytes, then the 8 high bytes. Element `i` is
low byte `i` plus 256 times high byte `i`: here `speeds[3]` is `$00` + 256 × `$03` = 768. An
array of structs is dumped the same way, one table per field byte.

## 5. Structs

A struct is a memory layout, always used in place: there is no struct assignment, parameter, return
value, comparison or literal. Use it as a variable, at a fixed address (hardware registers), as an
array of structs (stored as one table per field: `enemies[i].x` is `lda enemies_x,x`) or through a
pointer (`e.hp` is `ldy #2 / lda (e),y`).

```tern
// structs.tern
struct Enemy { x: u8, y: u8, hp: u8 }

struct Vic { border: u8 @ $20, background: u8 @ $21 }   // @ is the field's offset
var vic: Vic @ $D000

const COUNT = 8
var enemies: [Enemy; COUNT]
var player: Enemy
var alive: u8

fn hurt(e: ptr Enemy) {
    if e.hp != 0 { e.hp -= 1 }
}

fn main() {
    for i in 0..COUNT {
        enemies[i].x = (i << 4) + 24
        enemies[i].hp = 2
    }
    player.hp = 3
    hurt(addr(player))
    for i in 0..COUNT {
        if enemies[i].hp != 0 { alive += 1 }
    }
    vic.border = player.hp
}
```

```console
$ tern run structs.tern --dump alive --dump 'player:3'
Main returned: 728 cycles (442 in main)
A=$02 X=$08 Y=$08 SP=$FF PC=$081F
alive $0002 u8 = 8 ($08)
player $0918 Enemy (3 bytes):
  $0918  00 00 02
```

- Fields may be nested structs and arrays: `name: [u8; 8]`, `pos: Point`. Padding fields are named `_`.
- `p[i]` on a struct pointer is not allowed (it would need a multiply); step it with
  `p = p + sizeof(Enemy)`, or use an array of structs.
- `linear var list: [Enemy; 20]` stores whole records (for a pointer to walk); a variable index into
  it needs a struct size of 1, 2, 4 or 8. `indexed var list: [Enemy; 20]` adds an offset table so any
  size works. Plain arrays of structs need neither.

## 6. Pointers

- `addr(x)` gives a pointer to a variable, array or field of a struct variable. A number converts
  to a pointer without `as`: `var p: ptr u8 = $0400`.
- `p[i]` reads and writes through it (`lda (p),y`); `p[0]` is the pointed-to byte. On a `ptr u16`
  the index counts elements.
- Arithmetic is in bytes: `p + 1`, `p += 256` (`inc p+1`), `p - q` gives a `u16`.
- `.lo` and `.hi` are not allowed on pointers (`p.name` is a struct field): write `(p as u16).hi`.
- A pointer that is dereferenced is placed in zero page automatically.

```tern
// fill.tern
fn fill(dst: ptr u8, value: u8, pages: u8) {
    for _ in 0..pages {
        for j in 0..=255 using Y { dst[j] = value }
        dst += 256
    }
}

fn main() {
    fill($C000, $AA, 2)
}
```

```console
$ tern run fill.tern --dump '$C000:4' --dump '$C1FC:4' --dump '$C200:1'
Main returned: 7248 cycles (7240 in main)
A=$AA X=$02 Y=$00 SP=$FF PC=$0811
$C000 (4 bytes):
  $C000  AA AA AA AA
$C1FC (4 bytes):
  $C1FC  AA AA AA AA
$C200 (1 bytes):
  $C200  00
```

## 7. Control flow

```tern
// flow.tern
var state: u8 = 3
var hits: u8
var log: [u8; 16]
var n: u8

fn main() {
    if state < 10 { hits = 1 } else if state == 20 { hits = 2 } else { hits = 3 }

    for i in 0..10 { log[i] = i }          // 0 to 9
    for i in 9..=0 { n += 1 }              // 9 down to 0: 10 times
    for x in 0..40 step 8 { n += 1 }       // 0, 8, 16, 24, 32
    for i in 0..8 using X { log[i] += 1 }  // the counter is in X
    for _ in 0..3 { n += 1 }               // no counter needed

    var k: u8 = 0
    while true {                           // the only endless loop; leave with break or return
        k += 1
        if k == 5 { continue }
        if k >= 7 { break }
    }

    match state {
        0 => hits += 10
        1, 2 => hits += 20
        3..=7 => hits += 30
        _ => {}
    }
}
```

```console
$ tern run flow.tern --dump hits --dump n --dump 'log:10'
Main returned: 802 cycles (618 in main)
A=$1F X=$00 Y=$0A SP=$FF PC=$0825
hits $0003 u8 = 31 ($1F)
n $0004 u8 = 18 ($12)
log $08BE [u8; 16] (10 bytes):
  $08BE  01 02 03 04 05 06 07 08 08 09
```

- There is no C-style `for (;;)` and no `do`/`until`. `a..b` excludes `b`, `a..=b` includes it.
- `for` bounds are evaluated once, and the counter is read-only in the body. A counter is `u8`
  (`u16` if a bound needs it); with constant bounds the order gives the direction, an end of `0`
  counts down, a negative `step` too. A negative constant or a signed bound makes it `i8` (`i16` if
  the bounds do not fit): `for i in -10..10`, `for i in n..=0` with `n: i8`. A signed counter takes
  only a constant step, and an unsigned variable bound must be converted (`-5..n as i8`).
- `match` works on `u8` and `i8` (for a `u16`, match `.lo` or `.hi`). Patterns are constants, lists
  `1, 2` and inclusive ranges `3..=7`; `_` is last. One arm per line, no fall-through. A value no
  pattern lists skips the match.
- `indexed match` is a jump table over the span from the lowest to the highest pattern. A value
  outside the span is not checked and jumps to garbage: list every value that can occur, or mask it.
  `tern run` reports such a value and stops there.

```tern
// dispatch.tern
var acc: u8
var ops: [u8; 4] = [0, 1, 1, 3]

fn main() {
    for i in 0..4 {
        indexed match ops[i] & 3 {
            0 => acc = 1
            1 => acc += acc
            2 => acc = 0
            3 => acc += 100
        }
    }
}
```

```console
$ tern run dispatch.tern --dump acc
Main returned: 206 cycles (193 in main)
A=$68 X=$04 Y=$03 SP=$FF PC=$0815
acc $0002 u8 = 104 ($68)
```

## 8. Functions

```tern
// calls.tern
var t: [u8; 200]
var r: u8

fn add(a: u8, b: u8) -> u8 { return a + b }    // plain parameters: memory, the caller stores them
fn bump(i: u8 using X) { t[i] += 1 }            // a register parameter: inc t,x
fn above(y: u8) -> bool { return y >= 200 }     // a bool comes back in the carry
inline fn twice(v: u8) -> u8 { return v << 1 }  // copied into each caller

fn main() {
    r = add(40, 2)
    bump(r)
    if above(r) { r = 0 }
    r = twice(r)
}
```

```console
$ tern run calls.tern --dump r
Main returned: 2110 cycles (94 in main)
A=$54 X=$2A Y=$00 SP=$FF PC=$081F
r $0002 u8 = 84 ($54)
```

- Results: 8 bits in A, 16 bits in A (low) and X (high), `bool` in the carry. Other placements:
  `-> u8 using Y`, `-> u16 using XY`, `-> bool using zero`, `using negative`, `using overflow`.
- Parameters `using A`, `X` or `Y`; 16-bit ones `using AX`, `AY` or `XY` (low byte first).
- Recursion is an error. Function addresses: `addr(f)` is a `u16` constant (for vectors and tables).
- `inline fn` has no `jsr`/`rts`; its locals go in each caller's frame.
- **Routines at a fixed address** (the KERNAL) are declared without a body; a call is one `jsr`.
  Parameters must be registers. A constant in a parameter's place is passed on every call.
  `keeps` lists the registers the routine leaves alone (a promise the compiler trusts):

```tern
// kernal.tern
fn chrout(ch: u8 using A) @ $FFD2 keeps X, Y
fn plot(false using carry, row: u8 using X, col: u8 using Y) @ $FFF0

data hello = "HELLO"

fn main() {
    plot(2, 5)
    for i in 0..len(hello) using X { chrout(hello[i]) }
}
```

```console
$ tern run kernal.tern --screen
Main returned: 112 cycles (104 in main)
A=$4F X=$05 Y=$05 SP=$FF PC=$0811
Printed:
  HELLO
Screen ($0400, 40 x 25):
  |                                        |
  |                                        |
  |     HELLO                              |
...
```

The `c64` module already declares `c64.chrout`, `c64.print`, `c64.getin`, `c64.cursor`.

## 9. Flags as values

```tern
// flags.tern
import cpu

var count: u8
var value: u8 = $41

inline asm fn ready() -> bool using overflow {
    bit value            // V is bit 6 of value
}

fn is_letter(c: u8) -> bool using carry {
    return c >= 'A' && c <= 'Z'
}

fn main() {
    if ready() { count = 1 }               // one bit and one bvc
    if is_letter(value) { count += 10 }

    asm {
        lda value
        cmp #$40
    }
    if cpu.carry() { count += 100 }   // the carry the asm block just left
}
```

```console
$ tern run flags.tern --dump count
Main returned: 95 cycles (77 in main)
A=$6F X=$00 Y=$00 SP=$FF PC=$0819
count $0002 u8 = 111 ($6F)
```

- An `inline asm fn` with a flag result is a test with no call: `if f()` is the instructions and one
  branch.
- `cpu.carry()`, `cpu.zero()`, `cpu.negative()`, `cpu.overflow()` read a flag at no cost, but only as
  `if f()`, `while f()` or `v = f()` (also with `!`) right after an `asm` block. After Tern code the
  flags are unspecified.

## 10. Inline assembly

```tern
// asm.tern
var score: u16 = 250
var buffer: [u8; 16]

asm fn fill(value: u8 using A, count: u8 using X) {
.loop:
    sta buffer-1,x
    dex
    bne .loop
}

inline asm fn min(a: u8 using A, b: u8 using X) -> u8 {
    var tmp: u8          // work variables come first, with no initial value
    stx tmp
    cmp tmp
    bcc .done
    lda tmp
.done:
}

fn main() {
    asm {
        lda score
        clc
        adc #10
        sta score
        bcc .done
        inc score+1
    .done:
    }
    fill(7, 16)
    buffer[0] = min(buffer[1], 3)
}
```

```console
$ tern run asm.tern --dump score --dump 'buffer:2'
Main returned: 400 cycles (219 in main)
A=$03 X=$03 Y=$00 SP=$FF PC=$0823
score $0002 u16 = 260 ($0104)
buffer $0850 [u8; 16] (2 bytes):
  $0850  03 07
```

- `asm { }` is a statement; `asm fn` is a whole function in assembly with a typed signature (an
  `rts` is added unless it ends in `rts`, `rti` or `jmp`); `inline asm fn` is copied in place of the
  `jsr`, with register parameters only.
- Operands may name Tern variables, constants, arrays, functions and struct fields (`sta c64.vic.border`).
  A name is its address: `score+1` is the high byte. `#name.lo` / `#name.hi` take an address's bytes
  (not `<` / `>`). An immediate is -128 to 255 or a byte of an address.
- Labels starting with `.` are local to the block. One instruction per line. `//` comments; `;` is
  an error. `asl a` (or `asl`) is the accumulator.
- `jsr f` works for a Tern function whose parameters are all registers.
- The compiler works out which registers and variables a block uses and works around it. It never
  changes your instructions; a branch out of range is an error.

## 11. Interrupts

```tern
// irq.tern
import c64

var band: u8

interrupt fn raster() {
    c64.vic.irq_status = 1                 // acknowledge
    band += 1
    c64.vic.border = band & 7
}

fn main() {
    c64.kernal_off()                       // KERNAL out, interrupts held off
    c64.set_irq(addr(raster))
    c64.vic.control1 = c64.vic.control1 & $7F
    c64.vic.raster = 50
    c64.vic.irq_enable = 1
    c64.interrupts_on()
    while band < 3 { }                     // band is volatile: main and the handler use it
}
```

```console
$ tern run irq.tern --irq 20000 --dump band
Main returned: 60076 cycles (60063 in main), 3 interrupts
A=$03 X=$64 Y=$08 SP=$FF PC=$0815
band $0002 u8 = 3 ($03)
```

- `interrupt fn` saves only the registers it changes and ends with `rti`. It is its own root: its
  memory never overlaps main's.
- A function called from main and from a handler must have no memory of its own; a global used by
  both is volatile.
- An `interrupt fn` called from another one is a *part* (no saving, `rts`): this is how library
  raster code (`sprites.raster()`, `scroll.top()`) goes into your own handler.
- `tern run --irq n` raises an IRQ every n cycles through `$FFFE`.
- A handler starts with `cld` only when it can do decimal arithmetic: `adc` or `sbc` in its code or
  asm blocks, or any call (`jsr`). Without them it leaves decimal mode alone, which is safe.
- To keep the KERNAL in (needed by `io` and `chrout`), install the handler with
  `c64.set_kernal_irq(addr(raster))` instead of `kernal_off` / `set_irq`: it goes through `$0314`,
  and the same `interrupt fn` works. The KERNAL's keyboard scan stops (`getin` sees no keys), and its
  CIA 1 timer interrupt still reaches the handler: turn it off with `c64.cia1.icr = $7F`.
  `examples/kernal_irq.tern` reads the drive's status while a raster split runs. `tern run` has no
  KERNAL, so check such a program in VICE.

## 12. Modules

```tern
// shapes.tern
const SIZE: u8 = 3
private var calls: u8

fn area(w: u8) -> u8 {
    calls += 1
    return (w << SIZE) - w                 // w * 7
}
```

```tern
// app.tern
import shapes

const DEBUG = false
var a: u8

fn main() {
    a = shapes.area(shapes.SIZE)
    if DEBUG { a = 0 }                     // not compiled: DEBUG is false
}
```

```console
$ tern run app.tern --dump a
Main returned: 61 cycles (45 in main)
A=$15 X=$00 Y=$00 SP=$FF PC=$0817
a $0003 u8 = 21 ($15)
```

- `import name` looks in the platform library (`lib/c64/`), the standard library (`lib/`), then the
  program's folder. Names are used with the module name. `private` hides a declaration.
- Only what is reached is compiled. Module parameters: `import sprites(COUNT = 24)`; a module
  declares them with `module sprites(COUNT: u8 = 32)` and checks them with `assert`.
- A `memory { }` block in the main file reserves ranges (`$2000..=$2FFF reserved`), adds regions,
  overlay areas and zero page. A main file with `export` lines and no `main` is a binary for another
  program (`name_api.tern` is written for the host). See `docs/guide.md` sections 3.11 and 3.12.

## 13. Standard library

| Module | What it has |
|---|---|
| `math` | `mul8`, `mul8_fast` (tables), `mul16`, `div8`, `div16` (remainders in `math.remainder8/16`), `shl8`/`shr8`/`shl16`/`shr16` (variable count), `muls8`, `divs8`, `abs8`, `abs16`, inline `min8`/`max8`/`clamp8` and signed `mins8`/`maxs8`/`clamps8`, `sin8`/`cos8` |
| `mem` | `copy(dst, src, count: u16)`, `copy_back`, `fill(dst, value, count: u16)` |
| `text` | `put(dst, src, n)`, `dec8`/`dec16` (return the digit count), signed `dec8s`/`dec16s`, fixed width `dec8z`/`dec16z`, `hex8`/`hex16`, `equal`, `find` |
| `wide` | `U24`, `U32` structs; `add32(addr(a), addr(b))` and the rest take pointers |
| `bcd` | packed decimal counters of 1-4 bytes for scores |
| `random` | `seed`, `next8`, `next16`, `below(n)` |
| `cpu` | `carry()`, `zero()`, `negative()`, `overflow()` after an `asm` block |
| `c64` | `SCREEN`, `COLOR`, the 16 colors (`c64.BLACK` ... `c64.LIGHT_GREY`), `chrout`, `print`, `getin`, `cursor`, `load`; registers `vic`, `sid`, `cia1`, `cia2`; `sprite_xy` (the sprite positions as an array: x of sprite n at `2n`, y at `2n + 1`); `kernal_off`, `set_irq`, `set_kernal_irq`, `set_nmi`, `interrupts_on`, `interrupts_off` |
| `io` (C64) | disk files through the KERNAL: `load`, `save`, `open`, `read`, `write`, `close`, the command channel |
| `sprites` (C64) | a sprite multiplexer, 8 to 64 sprites |
| `scroll` (C64) | smooth eight-way character scrolling |

Strings and blocks are passed as a pointer and a length: `text.put(c64.SCREEN, addr(msg), len(msg))`.
The library source is readable Tern in `lib/`; read it for the exact signatures.
