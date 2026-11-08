# Common compile errors and their fixes

Each error says what is wrong and then how to fix it; `tern --json` gives the fix as `suggestion`.
These are the mistakes most likely when coming from C, Rust or Go. Each example is a program that
fails, the error `tern` prints, and the fix.

## Arithmetic and types

Multiplying at runtime:

```tern error
var row: u8 = 3
var offset: u16

fn main() {
    offset = row * 40
}
```

```text
example.tern:5:18: error: '*' only works on constants. At runtime use a library function (mul8, div8, ...), or shifts for powers of two
```

Fix: `var w: u16 = row` then `offset = (w << 5) + (w << 3)`, or `math.mul8(row, 40)`, or a table.

A shift by a variable:

```tern error
var x: u8 = 1
var n: u8 = 3

fn main() {
    x = x << n
}
```

```text
example.tern:5:14: error: The shift count must be a constant. Use shl8/shr8 for a variable count
```

Fix: `math.shl8(x, n)`, or a `match` on `n` with constant shifts.

Signed and unsigned together:

```tern error
var speed: i8 = -2
var x: u8 = 100

fn main() {
    x = x + speed
}
```

```text
example.tern:5:11: error: Cannot mix signed and unsigned (u8 and i8); convert one side with 'as'
```

Fix: `x = x + (speed as u8)` (the same bits, so this adds -2 with wrapping).

Narrowing without `as`:

```tern error
var big: u16 = 300
var small: u8

fn main() {
    small = big
}
```

```text
example.tern:5:13: error: Expected u8, found u16. Convert with 'as u8'
```

Fix: `small = big as u8` (the low byte) or `big.lo`.

A literal that does not fit:

```tern error
var x: u8 = 300

fn main() {
}
```

```text
example.tern:1:13: error: 300 does not fit in u8 (0 to 255)
```

Binary numbers are written `0b...`, not `%...`:

```tern error
var mask: u8 = %0000_1111

fn main() {
}
```

```text
example.tern:1:16: error: Expected an expression, found '%'
```

Fix: `0b0000_1111`. Hex is `$0F`.

A number is not a `bool`:

```tern error
var flags: u8 = 4
var n: u8

fn main() {
    if flags & 4 { n = 1 }
}
```

```text
example.tern:5:14: error: A condition must be bool, not u8. Compare it, e.g. 'x != 0'
```

Fix: `if flags & 4 != 0`. Unlike C, `&`, `|` and `^` bind tighter than the compares, so this is
`(flags & 4) != 0`.

No `++`:

```tern error
var n: u8

fn main() {
    n++
}
```

```text
example.tern:5:1: error: Expected an expression, found '}'
```

The error is at the token after `++`, and does not mention it. Fix: `n += 1` (one `inc` on memory).

## Variables and names

A local with no initial value:

```tern error
fn main() {
    var count: u8
    count += 1
}
```

```text
example.tern:2:5: error: Local variable 'count' needs an initial value
```

`step` is a keyword (of `for ... step`), and so are words like `data`, `match`, `using`, `indexed`
in some places:

```tern error
fn step() { }

fn main() {
    step()
}
```

```text
example.tern:1:4: error: Expected function name, found 'step'
```

A name from a module without the module name, or without the import:

```tern error
import c64

fn main() {
    vic.border = 0
}
```

```text
example.tern:4:5: error: 'vic' is not declared. To use the module vic, add 'import vic' at the top of the file
```

Fix: `c64.vic.border = 0`.

## Arrays, structs and pointers

An array of more than 256 elements:

```tern error
var points: [u16; 300]

fn main() {
}
```

```text
example.tern:1:19: error: An array of u16 has at most 256 elements. Only a global var or data of u8 or i8 may be longer (spec 3.2), and it is used through a pointer
```

Fix: two arrays, or a byte buffer reached through a pointer.

A long byte buffer cannot be indexed:

```tern error
var map: [u8; 1000]
var i: u8 = 5

fn main() {
    map[i] = 1
}
```

```text
example.tern:5:8: error: 'map' has 1000 elements, too many for a u8 index. Read it through a pointer: addr(map)
```

Fix: `var p: ptr u8 = addr(map) + 512` then `p[i]`.

No `*p`:

```tern error
var p: ptr u8 = $C000

fn main() {
    *p = 1
}
```

```text
example.tern:4:5: error: Expected an expression, found '*'
```

There is no dereference operator. Fix: `p[0] = 1`.

Structs are not copied:

```tern error
struct Point { x: u8, y: u8 }
var a: Point
var b: Point

fn main() {
    a = b
}
```

```text
example.tern:6:5: error: Structs are never copied, so a struct cannot be assigned. Assign its fields one by one, or copy the memory with mem.copy
```

Fix: copy the fields (`a.x = b.x`), or `mem.copy(addr(a), addr(b), sizeof(Point))`.

## Functions and loops

Recursion:

```tern error
var n: u8 = 5

fn count(v: u8) {
    if v != 0 { count(v - 1) }
}

fn main() {
    count(n)
}
```

```text
example.tern:3:1: error: Recursion is not allowed: count -> count
```

Fix: a loop, with an array and an index as the stack if the algorithm needs one.

A call inside a `for ... using X` loop:

```tern error
var t: [u8; 8]

fn twice(v: u8) -> u8 { return v << 1 }

fn main() {
    for i in 0..8 using X { t[i] = twice(i) }
}
```

```text
example.tern:6:5: error: The loop body calls a function, which can change X. Remove 'using X', or call only routines that keep X
```

Fix: leave out `using X` (the compiler still puts the counter in a register when it can), or write
the work in the loop without a call. A call to an `inline fn` counts as a call here too. Only
routines declared with `keeps X` (KERNAL routines) may be called in such a loop.

A function called from main and from an interrupt handler, with memory of its own:

```tern error
import c64

var a: u8
var b: u8

fn add(x: u8, y: u8) -> u8 { return x + y }

interrupt fn irq() {
    c64.vic.irq_status = 1
    b = add(b, 1)
}

fn main() {
    c64.set_irq(addr(irq))
    a = add(a, 2)
}
```

```text
example.tern:6:1: error: 'add' is called from main and from the interrupt handler 'irq'. An interrupt can stop it halfway and run it again, which would overwrite its memory: the parameter 'x' in memory. Only a function with no memory of its own can be shared between main and interrupt handlers. Use a copy for each
```

Fix: give each side its own function, make it an `inline fn` (its parameters then go in each
caller's frame), or pass the values in registers (`x: u8 using A, y: u8 using X`) so that the function has
no memory.

The same for a library routine whose work bytes are `private`:

```tern error
import c64
import math

var a: u16
var b: u16

interrupt fn irq() {
    c64.vic.irq_status = 1
    b = math.mul8(3, 4)
}

fn main() {
    c64.set_irq(addr(irq))
    a = math.mul8(5, 6)
}
```

```text
example.tern:7:11: error: 'math.mul8' (called from main) and 'math.mul8' (called from the interrupt handler 'irq') both use 'math.w0', a private variable of its module. An interrupt could change it while the other is using it. Call functions of this module from only main or only one interrupt handler
```

Fix: do the multiply on one side only (a table, or shifts), or copy the routine into your own
module for the handler.

## Control flow

A range in a `match` includes its end, so it is written `..=`:

```tern error
var state: u8
var n: u8

fn main() {
    match state {
        0..4 => n = 1
        _ => n = 2
    }
}
```

```text
example.tern:6:10: error: A range in a match includes its end: write 'low..=high'
```

`match` on a `u16`:

```tern error
var w: u16 = 500
var n: u8

fn main() {
    match w {
        500 => n = 1
        _ => n = 2
    }
}
```

```text
example.tern:5:11: error: 'match' on a 16-bit value is not supported yet. Match on .lo or .hi, or use if / else if
```

Fix: match on `w.hi` and `w.lo`, or use `if`.

## Assembly

`;` starts no comment in an `asm` block:

```tern error
var x: u8

fn main() {
    asm {
        lda #1      ; one
        sta x
    }
}
```

```text
example.tern:5:21: error: ';' is not used in assembly: write one instruction per line, and comments with //
```

Fix: `// one`.

An address as an immediate needs `.lo` or `.hi`:

```tern error
data msg = "HI"

fn main() {
    asm {
        lda #msg
    }
}
```

```text
example.tern:5:14: error: An address has two bytes: write #name.lo or #name.hi
```

Fix: `lda #msg.lo` / `ldx #msg.hi`.
