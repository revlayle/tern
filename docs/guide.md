# Tern user guide

A quick guide to the Tern language and compiler as built today (through milestone M13). The full rules are in `docs/spec-v0.md`; this guide shows how to use them. Every example here compiles with the current compiler.

## 1. What Tern is

Tern is a small language for the 6502 that compiles to code close to what you would write by hand in assembly. The first target is the Commodore 64.

A few rules shape everything else:

- **Cost is visible.** An operator always compiles to a few fixed instructions. Anything that needs a loop (multiply, divide, block copy, variable shift) is a library function call, so you see the cost in the source.
- **No recursion, no stack frames.** Every variable has a fixed address. Functions that can never run at the same time share memory, so total RAM use is the deepest call path, like a stack, but with fast absolute addressing.
- **Predictable output.** Each construct has a documented code shape (spec section 11), and the compiler writes a listing with the bytes and cycles of every line.
- **Assembly mixes in cheaply.** Inline assembly is parsed by the compiler and can name Tern variables, fields and functions.

## 2. Using the compiler

Build once, then compile a program:

```
dotnet build
dotnet run --project src/Tern.Cli -- examples/hello.tern
dotnet run --project src/Tern.Cli -- game.tern -o out/game.prg
```

For `game.tern` the compiler writes, next to the source (or the `-o` path):

| File | Contents |
|---|---|
| `game.prg` | The C64 program, loading at `$0801` with a `SYS` line |
| `game.lst` | Listing: every source line with its addresses, bytes and cycle counts |
| `game.vs` | VICE label file; load it in the VICE monitor with `ll "game.vs"` |
| `game_api.tern` | Only for a binary without `main` (section 3.11): the module a host imports |
| `level1.prg`, ... | One file per overlay module (section 3.12), named after the module |

It also prints a report: where code and variables went, and the zero-page map. Errors look like `game.tern:12:5: error: ...` and say how to fix the problem.

Run the result in VICE:

```
C:\Vice\bin\x64sc.exe -autostart game.prg
```

The listing is the best way to learn what Tern generates. From the first example below:

```
                                ;    7: c64.vic.border = c64.BLACK
$0814  A9 00     lda #$00              ; 2
$0816  8D 20 D0  sta c64.vic+32        ; 4
```

The standard library sits in `lib/` next to the compiler (source in `src/lib/`). Your own modules go next to your main file.

## 3. The language

### 3.1 Source text

- Files are UTF-8 with the extension `.tern`. Comments are `//` and `/* */`.
- A statement ends at a newline; `;` is optional. A newline inside `( )` or `[ ]` does not end a statement, so argument lists and tables can span lines.
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

### 3.2 Types and arithmetic

| Type | Size | Notes |
|---|---|---|
| `u8`, `i8` | 1 | The default numeric types |
| `u16`, `i16` | 2 | Little endian; `.lo` and `.hi` give the bytes |
| `bool` | 1 | Can also live in a CPU flag (3.7) |
| `ptr T` | 2 | Pointer to `T` |
| `[T; N]` | | Array, up to 256 elements |

- All arithmetic **wraps**, and there is no integer promotion: `u8 + u8` is a `u8`.
- `u8` mixed with `u16` widens to `u16`. Narrowing needs `as`: `x = big as u8` keeps the low byte.
- Mixing signed and unsigned is an error; cast one side.
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
- Literals: `200`, `$C000`, `0b1010_0101`, `'A'`, `"TEXT"`, `true`. A literal takes its type from where it is used, and one that does not fit is an error.
- Strings are PETSCII by default; a prefix picks another encoding: `screen"HELLO"` gives screen codes for writing straight to screen memory, `petscii_mixed"Hello"` the lower-case set. Strings have no terminator unless you write one (`"HI\0"`).

### 3.3 Declarations

```
const SCREEN = $0400               // compile-time constant, no memory
var score: u16                     // placed by the compiler, zeroed at startup
var lives: u8 = 3                  // set at startup
var p: ptr u8 @ zp                 // somewhere in zero page, guaranteed
var border: u8 @ $D020             // fixed address; I/O is volatile automatically
volatile var flag: u8 @ $C000      // RAM that something else changes
noinit var buffer: [u8; 256]       // not zeroed at startup
data msg = screen"GAME OVER"       // read-only bytes in the program
data ship = include_bytes("ship.bin")
```

- `@` only ever means "located at": an address, `zp`, or a named region (3.11).
- Locals (other than arrays) need an initial value: their memory is shared with other functions, so it holds leftovers on entry.
- Volatile memory (I/O, `volatile` variables, anything shared with an interrupt, anything through a pointer) is read and written exactly as written; ordinary memory may be kept in registers by the optimizer.

### 3.4 Arrays, structs and pointers

**Arrays** are indexed with a `u8`, which compiles to `abs,X` or `abs,Y`. An array of 16-bit values is stored as two tables, low bytes and high bytes, so `a[i]` stays one `lda a_lo,x` / `lda a_hi,x`. Write `linear var` for the usual interleaved layout when hardware or assembly needs it.

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

**Pointers**: `addr(x)` gives a pointer to a variable, array or field; `p[i]` reads through it with `(zp),Y` (there is no `*p`, write `p[0]`). Arithmetic is in bytes: `p + sizeof(Enemy)` steps one record. Any pointer converts to any other pointer type or to `u16` with `as`, at no cost. Pointers that are dereferenced are placed in zero page automatically.

### 3.5 Control flow

```
if x < 10 { ... } else if x == 20 { ... } else { ... }

while lives != 0 { ... }
while true { ... }                 // the endless loop; leave with break or return

for i in 0..10 { ... }             // 0 to 9
for i in 9..=0 { ... }             // 9 down to 0
for x in 0..40 step 8 { ... }      // 0, 8, 16, 24, 32
for i in 0..len using X { ... }    // the counter lives in X
for _ in 0..10 { ... }             // repeat 10 times

match state {
    0 => idle()
    1, 2 => walk()
    3..=7 => attack()
    _ => {}
}
```

- `for` bounds are evaluated once, and the loop variable is read-only in the body. The compiler picks the loop shape (counting down with `dex` / `bne` when it can).
- `match` works on `u8` and `i8`. Dense cases compile to a jump table, others to a chain of compares. There is no fall-through.
- `&&`, `||`, `!` are logical (on `bool`, short-circuit); `&`, `|`, `^`, `~` are bitwise. `x += 1` on memory is `inc x`.

### 3.6 Functions

```
fn add(a: u8, b: u8) -> u8 { return a + b }
fn plot(x: u8 using X, y: u8 using Y) { ... }
inline fn acknowledge() { c64.vic.irq_status = 1 }
```

- Parameters without `using` are fixed memory in the function; the caller stores the arguments straight into them and calls. `using A`, `X`, `Y` (or a pair `AX`, `AY`, `XY` for 16 bits) passes them in registers.
- Results come back in A (8 bits), A and X (16 bits) or the carry (`bool`). Other placements can be written: `-> u8 using Y`, `-> bool using zero`.
- `inline fn` copies the body into each caller.
- Recursion is a compile error. Naturally recursive algorithms use an array as an explicit stack.

**Routines that already exist**, such as KERNAL calls, are declared without a body at their address. A call is a direct `jsr`:

```
fn chrout(ch: u8 using A) @ $FFD2 keeps X, Y
fn get_cursor(true using carry) -> u16 using XY @ $FFF0   // the carry is a fixed input

chrout('A')                        // lda #$41 / jsr $FFD2
```

`keeps` lists the registers the routine leaves alone; the compiler trusts it.

### 3.7 Flags as values

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

### 3.8 Interrupts

`interrupt fn` saves only the registers it changes and ends with `rti`. It is its own root: its memory never overlaps main's. A function called from both main and a handler is an error, unless it has no memory of its own (like `acknowledge` above). One `interrupt fn` called from another becomes a *part* (no saving, ends with `rts`), which is how the library's raster code is combined into one handler you install.

The `c64` module installs handlers: `kernal_off()`, `set_irq(addr(handler))`, `interrupts_on()` (see example 4.3).

### 3.9 Inline assembly

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

- Operands may name Tern variables, constants, arrays, struct fields (`sta c64.vic.border`) and functions. `#name.lo` / `#name.hi` take the bytes of an address (not `<` / `>`).
- Labels starting with `.` are local to the block. `;` is not a comment here; use `//`.
- The compiler works out which registers and variables a block uses and works around it. It never changes your instructions.

### 3.10 Modules

```
import c64
import text
import sprites(COUNT = 24)         // a module parameter, set by the main file

fn main() {
    text.put(c64.SCREEN, addr(title), len(title))
}
```

- `import name` loads `name.tern` from the platform library (`lib/c64/`), the standard library (`lib/`), then your program's folder. Names are always used with the module name: `text.put`.
- `private` before a declaration hides it from other modules.
- Only what your program reaches is compiled: an unused import adds no bytes.
- An `if` on a constant compiles only the branch taken, and a function called only from the other branch is left out entirely, with no code and no memory. So a module option that is off costs nothing:

```
const DEBUG = false

fn main() {
    if DEBUG { show_timings() }    // no code, and show_timings is not compiled
}
```
- A module can take parameters in a header line, `module sprites(COUNT: u8 = 32, FLICKER: bool = false)`, and check them with `assert COUNT >= 8, "message"` at compile time.

### 3.11 Memory layout and binaries without `main`

By default a C64 program loads at `$0801`, variables follow it up to `$9FFF`, and zero page `$02-$8F` is free. A `memory` block in the main file changes that:

```
memory {
    $2000..=$2FFF  reserved        // VIC graphics: the compiler places nothing here
    $C000..=$CFFF  region buffers  // used only on request: var map: [u8; 4096] @ buffers
}

data ball: [u8; 63] @ $2000 = [ ... ]   // loaded with the program, at that address
```

Other kinds are `program`, `zeropage`, `volatile` and `overlay` (3.12). `program` and `zeropage` lines replace the platform's areas; the others add to them. A function is never split across areas.

A main file with `export` and no `main` builds a **binary** for another program to call, such as a music player. It starts with a jump table (`setup`, then the exports in order), and the compiler also writes `name_api.tern` for the host to import (example 4.4).

### 3.12 Overlays

An overlay is a module that is loaded from disk into a shared area when the program needs it, such as one level of a game. Overlays in the same area replace each other. The main file declares the area and places modules in it with `@`:

```
memory {
    $0801..=$5FFF  program
    $6000..=$7FFF  overlay levels
}

import level1 @ levels
import level2 @ levels
```

- **One build.** The whole program is compiled together, but each overlay module goes into its own file (`level1.prg`), loading at the start of its area. The report shows each area and how full it is.
- **Variables come with the file.** An overlay's global variables are stored in its file with their initial values, so every load resets them. They are never in zero page; an overlay that needs a zero-page pointer uses a local variable.
- **Loading** is `c64.load(name, len, device)`, which returns `true` if loading failed.
- **Calls:** resident code calls into an overlay with a plain `jsr`, and an overlay may call resident code and overlays in other areas. Two overlays of the same area cannot call each other or use each other's variables (they are never loaded together); that is a compile error.
- Making sure the right overlay is loaded before calling it is your job: the compiler cannot check it, and a check on every call would cost every call.
- A fixed-address variable over an overlay's file is an error. The spec now allows it when the variable is marked `volatile` (to read memory an overlay loads on purpose, e.g. a level header); that is built in the next coding session.

### 3.13 Built-ins

`addr(x)` (pointer to a variable, field or array; address of a function), `sizeof(T)`, `len(a)` (array length, at compile time).

## 4. Examples

### 4.1 Text on the screen

```
import c64
import text

data title = screen"HELLO, TERN"

fn main() {
    c64.vic.border = c64.BLACK
    c64.vic.background = c64.BLUE
    text.put(c64.SCREEN + 12 * 40 + 14, addr(title), len(title))
    while true { }
}
```

82 bytes, including the BASIC line and startup code.

### 4.2 A list of enemies

```
import c64

struct Enemy { x: u8, y: u8, hp: u8 }

const COUNT = 8
var enemies: [Enemy; COUNT]
var alive: u8

fn spawn() {
    for i in 0..COUNT {
        enemies[i].x = (i << 4) + 24
        enemies[i].y = 50
        enemies[i].hp = 3
    }
}

fn advance() {
    alive = 0
    for i in 0..COUNT {
        if enemies[i].hp == 0 { continue }
        enemies[i].y += 1
        alive += 1
    }
}

fn color_for(hp: u8) -> u8 {
    match hp {
        0 => return c64.BLACK
        1 => return c64.RED
        2, 3 => return c64.YELLOW
        _ => return c64.WHITE
    }
}

fn main() {
    spawn()
    advance()
    c64.vic.border = color_for(enemies[0].hp)
}
```

`spawn` and `advance` never run at the same time, so their loop counters share memory.

### 4.3 A raster interrupt

```
import c64

var band: u8

interrupt fn raster() {
    c64.vic.irq_status = 1               // acknowledge
    band += 1
    c64.vic.border = band & 7
    c64.vic.raster = 50 + ((band & 7) << 4)
}

fn main() {
    c64.kernal_off()                     // KERNAL out, interrupts held off
    c64.set_irq(addr(raster))
    c64.vic.control1 = c64.vic.control1 & $7F
    c64.vic.raster = 50
    c64.vic.irq_enable = 1
    c64.interrupts_on()
    while true { }
}
```

`band` is used by the handler only, so it is ordinary memory. If main also read it, it would become volatile automatically. See `examples/raster.tern` for a fuller version.

### 4.4 A binary and its host

`player.tern`, built on its own:

```
memory {
    $C000..=$CFFF  program
    $FB..=$FE      zeropage
}

export init, play

var frame: u8

fn init() { frame = 0 }
fn play() { frame += 1 }
```

The compiler writes `player.prg` (loading at `$C000`, no BASIC line) and `player_api.tern`:

```
const CODE_FIRST = $C000      // the program file: code, data and the gaps between
const CODE_LAST = $C016
const ZP_FIRST = $FB      // zero page used by the binary
const ZP_LAST = $FB

fn setup() @ $C000 keeps X, Y
fn init() @ $C003 keeps X, Y
fn play() @ $C006 keeps A, X, Y
```

A host reserves the binary's memory and calls it. Here it also carries the binary inside its own file:

```
import player_api

memory {
    player_api.CODE_FIRST - 2..=player_api.CODE_LAST  reserved
    player_api.ZP_FIRST..=player_api.ZP_LAST          reserved
}

// The .prg's two load-address bytes land just below the code.
data player @ player_api.CODE_FIRST - 2 = include_bytes("player.prg")

fn main() {
    player_api.setup()                   // once, before any other entry
    player_api.init()
    while true { player_api.play() }
}
```

### 4.5 Levels loaded from disk

`level1.tern`, an overlay module (`level2.tern` is the same with `'B'` on the bottom half):

```
import mem

var lives: u8 = 3                  // set again by every load of the file

fn draw() {
    lives += 1
    mem.fill($0400 as ptr u8, screen'A', 480)
}
```

The main program:

```
import c64
import level1 @ levels
import level2 @ levels

memory {
    $0801..=$5FFF  program
    $6000..=$7FFF  overlay levels
}

data NAME1 = "LEVEL1"
data NAME2 = "LEVEL2"

fn load(name: ptr u8, n: u8) {
    if c64.load(name, n, 8) {
        c64.vic.border = c64.RED        // loading failed
        while true { }
    }
}

fn main() {
    load(addr(NAME1), len(NAME1))
    level1.draw()
    load(addr(NAME2), len(NAME2))
    level2.draw()
}
```

Compiling writes `levels.prg`, `level1.prg` and `level2.prg`. Put them on a disk image to run it; the commands are in the header of `examples/levels.tern`.

More in `examples/`: `library.tern` (KERNAL printing, numbers), `structs.tern`, `arcade.tern` (32-bit and BCD scores, `sin8`, random sound), `scroller.tern` (eight-way scrolling under 24 multiplexed sprites), `sprite_data.tern`, `player.tern` with `player_host.tern`, `levels.tern` (overlays).

## 5. Standard library

| Module | What it has |
|---|---|
| `math` | `mul8`, `mul8_fast` (table based), `mul16`, `div8`, `div16` (remainders in `math.remainder8/16`), variable shifts, signed `muls8`/`divs8`, `abs8`/`abs16`, inline `min8`/`max8`/`clamp8` (and signed), `sin8`/`cos8` |
| `mem` | `copy`, `copy_back`, `fill` |
| `text` | `put`, decimal (`dec8`, `dec16`, signed, fixed width with leading zeros), hex, `equal`, `find` |
| `wide` | 24- and 32-bit unsigned numbers (`U24`, `U32`): add, subtract, compare, multiply, divide, decimal and hex output |
| `bcd` | Packed decimal counters of 1-4 bytes, for scores: add, subtract, compare, digits |
| `random` | 8- and 16-bit xorshift, `below(n)` |
| `c64` | Screen and color addresses, the 16 colors, KERNAL `chrout`/`print`/`getin`/`cursor`, `load` (for overlays), register structs `vic`, `sid`, `cia1`, `cia2`, interrupt setup |
| `sprites` (C64) | Sprite multiplexer, 8 to 64 sprites, optional flicker |
| `scroll` (C64) | Smooth eight-way character scrolling with two screens |

Strings and blocks are passed as a pointer and a length (`addr(s), len(s)`). Signatures and cycle counts are in spec section 10.1 and `docs/status.md`.

## 6. Behavior worth knowing

Tern trusts you and does not add hidden checks. These are deliberate and often useful:

- **Everything wraps, including indexes.** With `head = 255`, `buf[head + 1]` is `buf[0]`, so a 256-byte ring buffer needs no masking.
- **Indexes are not checked.** An index past the end of an array reads or writes whatever is there, and the optimizer assumes it does not happen. Write across variables on purpose through a pointer.
- **Signed `>>` rounds down**, not toward zero.
- **A `for` loop with a variable step of 0 never ends.** Stepped loops stop when the step carries past the end of the type.
- **Flags after compiled code are unspecified.** An empty `inline asm fn ... -> bool using carry {}` reads the flag an `asm` block just left, when used right after that block.
- **`keeps` is a promise** from the routine's documentation; the compiler does not check it.
- **A call into an overlay that is not loaded** runs whatever is in the area.
- **Loading an overlay resets its variables** to their initial values; keep anything that must survive in resident memory.
- **A binary's entries are one root.** The host must not call one entry from an interrupt while another is running.
- **Division by zero** in the library gives an unspecified result.

## 7. Not supported yet

Signed loop variables; `match` on 16-bit values (match on `.lo` / `.hi`); arrays of structs inside structs; a function with a body at a fixed address; fixed-address `data` inside an overlay (under discussion); bank switching. Each gives a clear error rather than a confusing one.

## 8. The future

### Next

- **M13 follow-up**: a `volatile` fixed-address variable may overlap an overlay's file, so the resident program can read what an overlay loads on purpose (3.12).
- Nothing else is scheduled yet. The next milestone is picked from the list below.

### Later highlights

- **`const fn` and code generators.** Functions that run at compile time to build lookup tables (`data rows = [row_addr(i) for i in 0..25]`), with unlimited-size `int` and `real` math. A generator can `emit` assembly, to produce unrolled "speedcode" whose shape depends on parameters.
- **C64 device I/O library**: load and save files on disk or tape, sequential files, the drive's command channel and error status, through the KERNAL.
- **Inline helpers usable from any root**: an inline function's locals move into each caller's memory, so it can be called from both main and interrupts.
- **Checks in test runs**: overflow and array-index checks in the built-in emulator, at no cost in the real program.
- **Tooling**: doc comments (`///`) and a language server that shows them in your editor.
- **A full developer guide**, growing out of this one, with an example for each machine-level behavior.
- Signed loop variables, `using` on local variables, floats as a library (low priority).

### New platforms (last)

- 65C02 CPU and the Commander X16
- NES, with ROM banks (`@ bank(n)`) and calls between banks
- Apple II and Atari 8-bit

Far off, after the compiler is complete: a standalone assembler extracted from Tern, with its generators, memory block and listing.
