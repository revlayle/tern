# Tern user guide

A quick guide to the Tern language and compiler as built today (through milestone M22 and its follow-ups). The full rules are in `docs/spec-v0.md`; this guide shows how to use them. Every example here compiles with the current compiler.

## 1. What Tern is

Tern is a small language for the 6502 that compiles to compact, predictable machine code. Carefully hand-written assembly is still a little smaller and faster (on the benchmarks, Tern's code is about 2% larger and 5% slower), and Tern makes it cheap to write the routines that need it in assembly. The first target is the Commodore 64.

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
| `game.lines` | The bytes, cycles and addresses of each source line, for tools |
| `game_api.tern` | Only for a binary without `main`, or a program with an export table (section 3.11): the module others import |
| `level1.prg`, ... | One file per overlay module (section 3.12), named after the module |

It also prints a report: where code and variables went, the zero-page map, and the functions whose memory is reachable from two roots (main and an interrupt handler). Errors look like `game.tern:12:5: error: ...` and say how to fix the problem. `tern --json game.tern` gives the same result as JSON, for editors and coding agents: each diagnostic has its file, line, column, message, the fix as `suggestion`, and `calledFrom` for an error inside compile-time code (3.14). `tern run` and `tern bench` run a program in the built-in emulator (7.11).

The compiler also warns about code that does nothing: an unused local, an unused private or main-file declaration, an unused import, and a statement that is never reached (`game.tern:9:5: warning: 'temp' is never used. ...`). Code that runs is never warned about, however unusual. Warnings do not stop the build. To keep something unused on purpose, start its name with `_` (`fn _debug_dump()`); for a loop counter you do not read, write `for _ in ...`, which is never slower than a named counter. `--no-warnings` turns them off, `--warnings-as-errors` makes them errors (for CI); both work with `tern run` too, and `--json` reports them with `"severity": "warning"`.

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

### 3.2 Types and arithmetic

| Type | Size | Notes |
|---|---|---|
| `u8`, `i8` | 1 | The default numeric types |
| `u16`, `i16` | 2 | Little endian; `.lo` and `.hi` give the bytes |
| `bool` | 1 | Can also live in a CPU flag (3.7) |
| `ptr T` | 2 | Pointer to `T` |
| `[T; N]` | | Array, up to 256 elements |

Compile-time code has two more types, `int` and `real`, which never reach the 6502 (3.14).

- All arithmetic **wraps**, and there is no integer promotion: `u8 + u8` is a `u8`.
- `u8` mixed with `u16` widens to `u16`. Narrowing needs `as`: `x = big as u8` keeps the low byte.
- Mixing signed and unsigned is an error; cast one side.
- Constants follow their own rules: they are exact, and they take a type only where they are used (3.3, "Constants").
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
- Literals: `200`, `$C000`, `0b1010_0101`, `'A'`, `"TEXT"`, `true`. A literal takes its type from where it is used, and one that does not fit is an error. Real numbers (`1.5`, `2e3`) exist only in compile-time code (3.14).
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

#### Constants

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

A constant may also hold a real number (`const SCALE = 1.5`, `const K = PI / 128`), with no type written, for use in compile-time code only (3.14). A constant never holds an array: a table the program reads is `data`.

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

### 3.5 Control flow

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
- The counter is a `u8` unless the range needs more: `u16` for larger bounds, `i8` or `i16` when a bound is negative or signed. A step may be a `u8` variable when the counter is a `u8` that counts up (`for m in (n << 1)..=255 step n`); a step that is 0 at runtime never ends the loop.
- `match` works on `u8` and `i8`. A plain `match` is a chain of compares, tested in source order. `indexed match` is a jump table over the span from the lowest pattern value to the highest; a value outside the span is not checked, so list every value that can occur or mask the value (`op & 3`). There is no fall-through. Which to choose: section 7.8.
- `&&`, `||`, `!` are logical (on `bool`, short-circuit); `&`, `|`, `^`, `~` are bitwise. `x += 1` on memory is `inc x`.

### 3.6 Functions

```
fn add(a: u8, b: u8) -> u8 { return a + b }
fn plot(x: u8 using X, y: u8 using Y) { ... }
inline fn acknowledge() { c64.vic.irq_status = 1 }
```

- Parameters without `using` are fixed memory in the function; the caller stores the arguments straight into them and calls. `using A`, `X`, `Y` (or a pair `AX`, `AY`, `XY` for 16 bits) passes them in registers.
- Results come back in A (8 bits), A and X (16 bits) or the carry (`bool`). Other placements can be written: `-> u8 using Y`, `-> u16 using XY`, `-> bool using zero` or `negative`. `-> bool using overflow` (the V flag) is for `asm fn`s and routines at a fixed address only, since no instruction sets V on its own.
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

An empty body reads the flag that the code before it left, at no cost. That is defined only right after an `asm` block: the `cpu` module has `carry()`, `zero()`, `negative()` and `overflow()` for it.

```
asm { jsr read_byte }              // carry set at the end of the file
if cpu.carry() { finish() }        // bcc skip, nothing more
```

### 3.8 Interrupts

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

The listing's frame map shows what memory a function has. One `interrupt fn` called from another becomes a *part* (no saving, ends with `rts`), which is how the library's raster code is combined into one handler you install.

The `c64` module installs handlers: `kernal_off()`, `set_irq(addr(handler))`, `interrupts_on()` (see example 4.3). With the KERNAL left in (needed by the `io` library), `set_kernal_irq(addr(handler))` installs the same kind of handler through `$0314`; the KERNAL's keyboard scan then stops. For a raster interrupt there, call `cia_irqs_off()` first, which turns off the CIA timer interrupts and clears any that are pending.

A handler starts with `cld` only when it can do decimal arithmetic: when its code or asm blocks have `adc` or `sbc`, or it calls anything (`jsr`). A handler without them leaves decimal mode alone, which is safe since nothing in it depends on it.

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

- Operands may name Tern variables, constants, arrays, struct fields (`sta c64.vic.border`) and functions. `#name.lo` / `#name.hi` take the bytes of an address (not `<` / `>`). Through a pointer, `ldy #offset(Enemy.hp)` / `lda (e),y` names the field's offset instead of hard-coding it.
- A line may call a generator, a `const fn` that writes instructions in its place (3.14).
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

### 3.11 Memory layout and binaries without `main`

By default a C64 program loads at `$0801`, variables follow it up to `$9FFF`, and zero page `$02-$8F` is free. A `memory` block in the main file changes that:

```
memory {
    $2000..=$2FFF  reserved        // VIC graphics: the compiler places nothing here
    $C000..=$CFFF  region buffers  // used only on request: var map: [u8; 4096] @ buffers
}

data ball: [u8; 63] @ $2000 = [ ... ]   // loaded with the program, at that address
```

A `reserved` range is also the plain way to keep memory for your own use. Name its start once and point into it:

```
const MAP: u16 = $4000              // constants may stand above the block (or anywhere below)

memory {
    MAP..=MAP + $07FF  reserved     // 2 KB, never touched by the compiler or startup
}

var p: ptr u8 = (MAP + 256) as ptr u8
p[44] = 1                           // byte 300 of the map: sta (p),y
```

The range is not cleared at startup.

Other kinds are `program`, `zeropage`, `volatile` and `overlay` (3.12). `program` and `zeropage` lines replace the platform's areas; the others add to them. A function is never split across areas.

A main file with `export` and no `main` builds a **binary** for another program to call, such as a music player. It starts with a jump table (`setup`, then the exports in order), and the compiler also writes `name_api.tern` for the host to import (example 4.4).

An export table, like `data` at a fixed address, is part of the program file, so the file covers everything between the code and the table: `export add @ $C000` in a program that starts at `$0801` writes a 47 KB `.prg`. Put the table next to the code, or in its own small `program` area.

**Plug-ins.** A game that loads levels built separately, perhaps later or by someone else, describes them in a hand-written **interface module**: routines at fixed addresses (`fn update() -> bool using carry @ $6006`), `volatile` fixed variables for data every level provides, and constants for the slot. The game imports it and calls the routines; each level is a binary with `export level_api`, and the compiler checks the level against the module: a function for every routine, with the same registers, return and `keeps`, and a `data` for every `volatile` variable. The interface must declare `setup` at its first address (the level's startup code goes there), routines at least 3 bytes apart, and no fixed inputs. A level implements one interface; a game may import as many as it has slots.

The other direction: `export add_score, show @ $0900` in a program with `main` puts a jump table at `$0900` and writes `game_api.tern`, which plug-ins import to call the game. Example 4.6 has both.

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
- A fixed-address variable over an overlay's file is an error unless it is marked `volatile`, which reads whatever overlay is loaded (memory an overlay loads on purpose, e.g. a level header).

### 3.13 Built-ins

| Built-in | Meaning |
|---|---|
| `addr(x)` | Pointer to a variable, field or array; address of a function |
| `sizeof(T)` | Size of a type in bytes |
| `len(a)` | Length of an array or string, a constant |
| `offset(T.field)` | Offset of a field from the start of a struct, a constant: `offset(Enemy.pos.x)` |
| `include_bytes("file")` | A file's bytes as the initial value of `data`, path relative to the source |
| `repeat(v, n)` | An array of `n` copies of `v`, in compile-time code only: `data spaces: [u8; 40] = repeat($20, 40)` |

The math functions of compile-time code are in 3.14.

### 3.14 Compile-time code: `const fn` and generators

A `const fn` is a function the compiler runs while it compiles. Only its result goes into the program; the function never becomes 6502 code. Use it to build lookup tables, and, as a *generator*, to write assembly whose shape depends on parameters. From `examples/sine.tern`:

```
import c64

// n values of mid + amplitude * sin(angle), one full turn.
const fn wave(n: int, mid: int, amplitude: real) -> [int; _] {
    return [round(mid + amplitude * sin(i * TAU / n)) for i in 0..n]
}

data xs: [u8; 256] = wave(256, 140, 100.0)       // 40 to 240
data ys: [u8; _] = wave(256, 150, 80.0)          // [u8; _]: the length from the array
data rows: [ptr u8; 25] = [c64.SCREEN + i * 40 for i in 0..25]

fn main() {
    var a: u8 = 0
    while true {
        c64.vic.sprite0_x = xs[a]                // an indexed load from plain bytes
        a += 2
    }
}
```

The program has no sine code, no multiply and no reals: `xs` is 256 bytes in the `.prg`, as if typed in by hand.

**Types.** Compile-time code has its own types, which do not exist at runtime:

- `int`, a signed 64-bit whole number. It never wraps: going outside the range is an error. Write `& $FF` where you want 8-bit behavior.
- `real`, a 64-bit double. `1.5`, `0.25` and `2e3` are real literals (`0..25` is still a range). Functions: `sin`, `cos`, `tan`, `atan2`, `sqrt`, `pow`, `exp`, `log`, `log2`, `abs`, `min`, `max` (angles in radians), and the constants `PI` and `TAU`. Division by zero, and `sqrt` or `log` of a negative number, are errors, so no NaN reaches a table.
- `bool`, arrays of these, and `label` (for generators, below).

A runtime type (`u8`, a pointer, a struct) in a `const fn` is an error; arguments widen into `int`, so a `const COUNT: u8` passes in as an `int`. A value is checked where it leaves compile time: each element must fit its table's type, as a constant must fit (3.3).

An `int` becomes a `real` where one is expected (`i * 1.5`), but `int / int` stays whole-number division. A `real` never becomes an `int` by itself: write `round(x)` (halves away from zero), `floor`, `ceil` or `trunc`, so the rounding in a table is your choice. `as` does not round.

**Arrays** have a length fixed when they are made: a list built with `for`, or `repeat(value, count)`. `len(t)` gives the length, and `t[i] = v` sets an element. The list's range has the forms of a `for` loop (`0..n`, `9..=0 step -1`, `step k`); there is one `for`, no filter and no nesting, and anything more is a `const fn` with ordinary loops. A `const fn` returns an array as `[int; _]`, and a table takes it with a written length, which is checked, or with `[T; _]`. Lists and `repeat` also work directly in a declaration, as `rows` above shows. A `const fn` can use every statement:

```
// A filled circle of radius r in a 24 x 21 sprite: 3 bytes per row.
const fn disc(r: real) -> [int; _] {
    var t = repeat(0, 63)
    for row in 0..21 {
        for col in 0..24 {
            var dx = col - 11.5
            var dy = row - 10.0
            if dx * dx + dy * dy <= r * r {
                t[row * 3 + col / 8] = t[row * 3 + col / 8] | ($80 >> (col % 8))
            }
        }
    }
    return t
}

data ball: [u8; 63] @ $0C00 = disc(10.5)
```

**Where it runs.** Compile-time code runs in declarations only: a `const` (also one inside a function), `data`, and the initial value of a global `var`. It never runs in statements, so a `const fn` call, a list built with `for`, `repeat` or a real number in runtime code is an error. In an `asm` block too: `lda #round(1.5)` is an error; declare a constant and use its name.

**What it reads:** constants, module parameters, `sizeof`, `offset`, and `data` tables element by element, including `include_bytes` tables, so one table can be built from another or from a file. Not a `var`, whose value is known only at runtime, and not `addr()`, since addresses are decided after compile-time code runs (generators can still use them, below).

- A `const fn` may live in a module and be shared between programs: `import tables`, then `data sine: [u8; _] = tables.sine(256)`.
- A `const fn` may call itself, up to 200 calls deep. Runtime code has no recursion only because the 6502 has no room for stack frames; a `const fn` never runs there.
- Each declaration may take 10,000,000 steps (a statement or a loop pass). A loop that never ends is an error naming the function, not a hang.
- An error inside a `const fn` is reported at its line, with "(called from main.tern:5)", the declaration that ran it. `tern --json` gives that place as `calledFrom`.
- Structs and strings are not compile-time values, so a `const fn` cannot build a table of struct literals.

**Generators.** A `const fn` with no return value may `emit` instructions. It is called on a line of its own in an `asm` block or `asm fn`, and what it emits goes there, so loops, `if`s and parameters decide the code's shape: unrolled copies, plotters, sprite clearing. A shortened version of `examples/speedcode.tern`:

```
import c64

var saved: [u8; 8]

// Rows 1 to 8 of colour RAM move one column left; each row's first colour goes to the end.
const fn shift_rows(rows: [int; _], buffer: label) {
    for k in 0..len(rows) {
        emit {
            lda c64.COLOR + rows[k] * 40
            sta buffer + k
        }
    }
    var top = label()
    emit {
        ldx #0
    top:
    }
    for k in 0..len(rows) {
        emit {
            lda c64.COLOR + rows[k] * 40 + 1, x
            sta c64.COLOR + rows[k] * 40, x
        }
    }
    emit {
        inx
        cpx #39
        bne top
    }
    for k in 0..len(rows) {
        emit {
            lda buffer + k
            sta c64.COLOR + rows[k] * 40 + 39
        }
    }
}

asm fn shift_colors() {
    shift_rows([1, 2, 3, 4, 5, 6, 7, 8], addr(saved))
}
```

There is one loop over the columns, with a `lda` / `sta` pair per row inside and the row addresses as constants: no row loop, no pointer, no `(p),y`.

- **Instructions, not text.** An `emit` is parsed like an `asm` block when the `const fn` is read, so a typo is reported even if that `emit` never runs. Its operands are compile-time expressions that may use the generator's variables and parameters. Tern names in it (variables, `data`, functions, `c64.COLOR`) are bound in the generator's module.
- **Arguments** are compile-time values. A program variable is not one: name it in the `emit` text, or pass its address.
- **Labels.** A `.label` inside one `emit` is new each time that `emit` runs, so a loop may emit `.skip:` safely. A label shared between fragments is a `label` value, from one of three places: `label()` makes a new one, which `name:` in an `emit` places once (`top` above); `addr(name)` of a global, `data` or function passes an address the compiler places (`addr(saved)`); and a `.label` of the calling block, written on the call line (`cycle_rows(rows, addr(saved), .done)` in the example), lets the generated code jump to it. In an operand a label is used alone, plus or minus an `int`, or as `.lo` / `.hi`; its number cannot be read in the `const fn`, since it is decided when the program is placed.
- **The result is ordinary assembly**: the compiler works out its registers and never changes it. The listing shows the call line, then each generated instruction under its `emit` line, marked "(generated by line 38)". An error in the generated code, such as a branch out of range, is reported at the `emit` line with "(generated by the call at main.tern:38)".
- A generator may call other generators and may live in a module. A `const fn` that returns a value cannot emit, and a generator cannot return a value.
- There are no macros: Tern statements are never generated, only assembly.

When to unroll with a generator, and what it costs, is in 7.9.

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

### 4.6 Levels as plug-ins

The interface, `level_api.tern`, written by hand:

```
struct LevelHeader {
    border: u8          // the border color while the level runs
    goal: u8            // how many updates the level takes
}

const SLOT_FIRST = $6000
const SLOT_LAST = $6FFF
const ZP_FIRST = $FB    // zero page a level may use: the game leaves it alone
const ZP_LAST = $FE
const HEADER = $6009

fn setup() @ $6000                              // first, after loading
fn enter() @ $6003                              // the level starts
fn update() -> bool using carry @ $6006         // one step; true when the level is finished

volatile var header: LevelHeader @ HEADER       // every level's data at a fixed address
```

A level, `forest.tern`, built on its own. Its functions may be in any order: each goes where `level_api` puts it.

```
import game_api
import level_api

memory {
    level_api.SLOT_FIRST..=level_api.SLOT_LAST  program
    level_api.ZP_FIRST..=level_api.ZP_LAST      zeropage
}

export level_api

data header: level_api.LevelHeader @ level_api.HEADER = level_api.LevelHeader { border = 5, goal = 3 }
data NAME = "FOREST "
var steps: u8

fn enter() {
    for i in 0..len(NAME) { game_api.show(NAME[i]) }
}

fn update() -> bool using carry {
    steps += 1
    game_api.add_score(10)
    return steps == header.goal
}
```

The game reserves the slot, offers two services to its levels, and plays each level it loads:

```
import c64
import level_api

memory {
    $0801..=$5FFF                               program
    level_api.SLOT_FIRST..=level_api.SLOT_LAST  reserved
}

export add_score, show @ $0900

var score: u16

fn add_score(points: u8 using A) { score += points as u16 }
fn show(ch: u8 using A) { c64.chrout(ch) }

fn play(name: ptr u8, n: u8) {
    if c64.load(name, n, 8) { ... }          // load the level into the slot
    level_api.setup()
    c64.vic.border = level_api.header.border
    level_api.enter()
    while !level_api.update() { }
}
```

Building the game writes `game_api.tern`, so build it before the levels:

```
fn add_score(points: u8 using A) @ $0900 keeps X, Y
fn show(ch: u8 using A) @ $0903 keeps X, Y
```

The full example, with a second level and the commands for a disk image, is in `examples/plugins/`.

More in `examples/`: `library.tern` (KERNAL printing, numbers), `kernal.tern` (KERNAL routines at fixed addresses), `structs.tern`, `arcade.tern` (32-bit and BCD scores, `sin8`, random sound), `scroller.tern` (eight-way scrolling under 24 multiplexed sprites), `sprite_data.tern`, `kernal_irq.tern` (a raster split with the KERNAL left in), `highscore.tern` (saving and loading a file with `io`), `player.tern` with `player_host.tern`, `levels.tern` (overlays), `plugins/` (levels as plug-ins), `sine.tern` (sine tables and a sprite shape from `const fn`s, 3.14), `speedcode.tern` (a generator against a plain loop, 7.9).

## 5. Standard library

| Module | What it has |
|---|---|
| `math` | `mul8`, `mul8_fast` (table based), `mul16`, `div8`, `div16` (remainders in `math.remainder8/16`), variable shifts, signed `muls8`/`divs8`, `abs8`/`abs16`, inline `min8`/`max8`/`clamp8` (and signed), `sin8`/`cos8` |
| `mem` | `copy`, `copy_back`, `fill` |
| `text` | `put`, decimal (`dec8`, `dec16`, signed, fixed width with leading zeros), hex, `equal`, `find` |
| `wide` | 24- and 32-bit unsigned numbers (`U24`, `U32`): add, subtract, compare, multiply, divide, decimal and hex output |
| `bcd` | Packed decimal counters of 1-4 bytes, for scores: add, subtract, compare, digits |
| `random` | 8- and 16-bit xorshift, `below(n)` |
| `cpu` | `carry()`, `zero()`, `negative()`, `overflow()`: the flags an `asm` block left, at no cost (3.7) |
| `c64` | Screen and color addresses, the 16 colors, KERNAL `chrout`/`print`/`getin`/`cursor`, `load` (for overlays), register structs `vic`, `sid`, `cia1`, `cia2`, `sprite_xy` (the sprite positions as an array), interrupt setup (`kernal_off`, `set_irq`, `set_kernal_irq`, `cia_irqs_off`, `interrupts_on`) |
| `sprites` (C64) | Sprite multiplexer, 8 to 64 sprites, optional flicker |
| `scroll` (C64) | Smooth eight-way character scrolling with two screens |
| `io` (C64) | Device I/O through the KERNAL: load and save whole files, sequential files, the drive's command channel and status; needs the KERNAL banked in |

Strings and blocks are passed as a pointer and a length (`addr(s), len(s)`). Signatures and cycle counts are in spec section 10.1 and `docs/status.md`.

## 6. Behavior worth knowing

Tern trusts you and does not add hidden checks. These are deliberate and often useful:

- **Everything wraps, including indexes.** With `head = 255`, `buf[head + 1]` is `buf[0]`, so a 256-byte ring buffer needs no masking.
- **Indexes are not checked.** An index past the end of an array reads or writes whatever is there, and the optimizer assumes it does not happen. Write across variables on purpose through a pointer.
- **Signed `>>` rounds down**, not toward zero. On negative constants, `/` rounds toward zero and `%` takes the sign of the left operand (`-7 / 2` is -3, `-7 % 2` is -1), as `math.divs8` does at runtime.
- **A loop's direction comes from its step**, never its bounds (3.5). `for i in n..=0` counts up: it runs once when `n` is 0 and not at all otherwise, with no warning.
- **A fixed address outside the platform's I/O areas is ordinary memory.** `var reg: u8 @ $C000` may be kept in a register and a read of it removed; hardware the platform does not list, such as a cartridge register, needs `volatile`.
- **Calls in one statement run left to right**, as do reads of `volatile` memory: in `(rand() + 1) << 2 | rand()` the left `rand()` runs first, and in `a[f()] = g()` the index's call runs first. One case is left open: a variable that a call in the same statement changes. In `x = seed + next()` where `next` changes `seed`, either value of `seed` may be added; put the call or the variable in a local first.
- **A `for` loop with a variable step of 0 never ends.** Stepped loops stop when the step carries past the end of the type.
- **Flags after compiled code are unspecified.** An empty `inline asm fn ... -> bool using carry {}` reads the flag an `asm` block just left, when used right after that block.
- **`keeps` is a promise** from the routine's documentation; the compiler does not check it.
- **A function called from main and from a handler has one set of variables.** An interrupt that arrives while main is inside it overwrites them (3.8). Loop counters are in memory unless written `using X` or `using Y`, even if the compiler happens to put one in a register.
- **When `main` returns**, the program stops in an endless loop and interrupt handlers keep running. There is no return to BASIC, whose zero page the program has used.
- **A call into an overlay that is not loaded** runs whatever is in the area.
- **Loading an overlay resets its variables** to their initial values; keep anything that must survive in resident memory. Its zero-page bytes at fixed addresses are not in its file, so the overlay sets them itself. An interrupt handler it installed must be removed before another overlay replaces it.
- **A binary's entries are one root.** The host must not call one entry from an interrupt while another is running.
- **A plug-in's memory is checked only against its own `memory` block**, not against the game's or another slot's. Keep each slot, and each slot's zero page, apart in the interface modules, and reserve them in the game.
- **A game's export table has gaps of zeros** when its routines are not packed: a call into a gap stops at a `brk`.
- **The game's exported functions are called from outside**, so the compiler cannot see when they run (an unknown root, as for `addr()`). Globals they share with the rest of the game are always read from memory, and a plug-in must not call them from an interrupt while the game is in one of them.
- **Division by zero** in the library gives an unspecified result.
- **`indexed match` does not check its value.** Outside the span of its patterns it jumps through whatever bytes follow its tables; `_` catches only values inside the span. A plain `match` skips values it does not list.
- **Compile-time `int` never wraps; runtime numbers always do.** The same expression in a `const fn` and in a function can give different results: write `& $FF` in the `const fn` where you want the 8-bit one (3.14).

## 7. Writing fast Tern

Each rule here was measured with `tern bench` (7.11) on the current compiler; the percentages are cycles saved in the example or benchmark named (`tests/benchmarks/variants.md` has the benchmark pairs). They describe this compiler, not the language: as the optimizer improves, a rule may stop paying, so measure when it matters.

### 7.1 Arrays before pointers

An array indexed by a `u8` is `lda a,x`; a pointer access is `lda (p),y`, with the pointer to set up first. When the data fits in 256 elements, use an array, and for records an array of structs (one table per field) rather than a pointer to records:

```
struct Enemy { x: u8, y: u8, hp: u8 }
var enemies: [Enemy; 32]

for i in 0..32 { enemies[i].x += enemies[i].y }   // 45% faster than stepping a ptr Enemy
```

Copying 200 bytes, `dst[i] = src[i]` is 12% faster than `q[i] = p[i]` through pointers. Pointers are for blocks longer than 256 bytes and for data whose address is known only at runtime.

### 7.2 Through a pointer, index with Y and step by pages

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

### 7.3 Store a result before testing it

After `enemies[i].x = x`, A still holds `x`, so the tests that follow need no load. Testing first and storing afterwards loads it again: storing first saves 17% in the `enemies` benchmark (`enemies.first`).

```
var x: u8 = enemies[i].x + (enemies[i].dx as u8)
enemies[i].x = x                       // store first
if x < 8 || x > 200 { enemies[i].dx = -enemies[i].dx }
```

### 7.4 Leave a loop with `break` or `return`, not a flag

A `running` flag costs a store where it is set and a load and test on every pass. `while true` with `break` or `return` has neither: 5% in the `interp` benchmark (`interp.return`). `break` and `return` give the same code, also from inside a `match` arm (`HALT => break`).

### 7.5 Small functions in a hot loop: `inline fn`

A call costs `jsr` / `rts` (12 cycles) and a store for each argument. An `inline fn` puts the body in the caller, where the arguments often are already in registers, and a `bool` result becomes the caller's branch. Making three small functions inline saves 35% in the `calls` benchmark (`calls.inline`), and bytes too, since each is called once. Every call site gets its own copy, so a large function called from many places makes the program bigger.

### 7.6 `using` on a parameter, only where the body needs that register

A plain parameter is fixed memory in the function, and the caller stores the argument straight into it. `using` pays when the body needs the value in that register anyway: as an index, or used once in A.

```
var t: [u8; 200]
fn bump(i: u8 using X) { t[i] += 1 }     // inc t,x / rts: 9% faster than a plain i
```

A parameter used once in A (`n = n ^ v` with `v: u8 using A`) saves 16%. When the body compares or combines two parameters, one of them has to go to memory anyway, and plain parameters are as fast or faster: `fn bigger(a: u8 using A, b: u8 using X) -> bool { return a > b }` is 4% slower than with plain parameters.

### 7.7 Multiplying by a constant

`*` takes only constants; at runtime write shifts and adds, or use a table. Compute in `u8` while the value fits, then widen:

```
var w: u16 = (row << 2) + row          // row * 5 in 8 bits (row <= 51)
var offset: u16 = w << 3               // * 8 in 16 bits: row * 40
```

For `row * 40` over 25 rows, this is 58% faster than widening first (`(w << 5) + (w << 3)` with `w = row`) and 65% faster than `math.mul8(row, 40)`. A `data` table of the 25 results is faster still (`offset = rows[row]`, 57% faster than the shifts above), at two bytes per entry.

### 7.8 `match` or `indexed match`

A plain `match` tests its values in source order, so put the most common first. `indexed match` costs the same for every value (subtract the lowest value, then the table jump, about 25 cycles). With values spread evenly, a plain `match` is faster up to about 8 values (4 values: 26% faster than `indexed match`; 8 values: 7%); at 16 dense values `indexed match` is 26% faster and smaller. In the `interp` benchmark (10 opcodes) `indexed match` is 2% faster.

`indexed match` does not check its value (section 6): list every value that can occur, or mask it into the span (`indexed match op & 15` with patterns 0 to 15). `_` catches only the values inside the span that no pattern lists.

### 7.9 Unroll a per-frame loop with a generator

The compiler never unrolls a loop by itself. When a loop must finish within a frame and does not, a generator (3.14) can write it out: the work stays the same, and the loop counters, pointer setup and `(p),y` go. In `examples/speedcode.tern`, moving 22 rows of colour RAM one column left:

| Version | Bytes | Cycles |
|---|--:|--:|
| `_cycle_plain`: loops over rows and columns, through a pointer (kept for comparison; main does not call it, hence the `_`) | 66 | 29,537 |
| `cycle_unrolled`: a generator, one `lda` / `sta` pair per row in one column loop | 422 | 8,697 |

A PAL frame leaves the CPU about 19,000 cycles, so only the unrolled version moves the colours every frame, for 356 more bytes. Unroll only what must be fast: the bytes grow with every row. A branch reaches at most 128 bytes back, so the example emits a column loop for each group of up to 20 rows.

### 7.10 What not to rewrite

The optimizer already does these, so write the plain form:

- A local copy of an element (`var a: u8 = values[i]`, then `if values[i + 1] < a`) costs nothing: the compare reads the element directly (`cmp values,x`).
- `a[i + 1]` in a `for` loop whose bounds prove the sum does not wrap is `lda a+1,x`, with no add.
- Loop counters, and globals used by one function in its loop, stay in free registers across the loop.
- Variables used most go in zero page (spec 8.2); arrays are placed so their indexed range does not cross a page when there is room.
- `return a > b` from a `-> bool` function is `lda a / clc / sbc b / rts`.

The `bubble.direct` (comparing the elements, no locals) and `bubble.minus` (`i - 1` instead of `i + 1`) forms of the `bubble` benchmark are not faster than the plain one.

### 7.11 Measuring

`tern bench` runs two or more versions in the built-in emulator and prints the bytes and cycles of each (`tern` is `dotnet run --project src/Tern.Cli --`):

```
tern bench --decl "var n: u8" --code "for i in 0..200 { n += i }" --code "for i in 0..200 using X { n += i }" --dump n
tern bench old.tern new.tern --dump result        // whole programs; --fn f measures one function
tern run game.tern --profile                      // the cycles of each function and source line
```

`--dump` shows a value after each run, to check that the versions agree. `--set $DC00=$7B` writes a byte before the run starts, for inputs such as the joystick; it takes addresses only, since the program's startup sets its own globals after loading and would overwrite a variable set by name.

`tern run` also checks every variable array index (the program itself never does, section 6). An index outside its array is printed with the source line, and the run goes on, so one run shows every bad access. An `indexed match` value outside its span is printed and stops the run. The exit code is 0 when `main` returned with no error, 3 when `main` returned but the check reported an error, and 2 when `main` did not return (the cycle limit, or a stop after a bad `indexed match` value). Pointer accesses and `asm` are not checked.

## 8. Not supported yet

`match` on 16-bit values (match on `.lo` / `.hi`); arrays of structs inside structs; a function with a body at a fixed address; bank switching. Each gives a clear error rather than a confusing one.

## 9. The future

### Next

- **Overlay interfaces** (`implements`): overlays that provide the same routines, called through one interface whichever is loaded.
- **Encodings declared by the program**: your own character mapping for string literals, beside `screen` and `petscii`.

### Later highlights

- **Floats as a library** (M24), with its constants and tables built by `const fn`s.
- **A full developer guide**, growing out of this one, with an example for each machine-level behavior.
- Not scheduled yet: calls through function variables (`var handler: fn() targets [idle, walk]`), import paths with subfolders and aliases, `@ align(n)` placement, and a generator that also returns a value (such as the cycles of the code it emitted).

### New platforms

- Atari 8-bit
- Apple II
- NES, with ROM banks placed like overlays (on the import) and calls into them
- 65C02 CPU and the Commander X16
- Commodore 128 in native mode, after the first release (C64 programs already run in its C64 mode)

Last: **doc comments (`///`) and a language server** (M30) that shows errors, types and costs in your editor.

Far off, after the compiler is complete: a standalone assembler extracted from Tern, with its generators, memory block and listing.
