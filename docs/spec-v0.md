# Tern language specification — v0 (draft)

*Tern is a working name.*

Tern is a programming language for 6502-family processors. It aims to be easy to read and write, and to compile to code close to what a skilled assembly programmer would write.

## 1. Goals and non-goals

**Goals**

1. **Efficient by construction.** The language is shaped around the 6502: 8-bit values, a tiny hardware stack, fast zero page and indexed addressing. A construct that the 6502 cannot do cheaply should look expensive in the source. **Rule: operators always compile to a short, fixed sequence of instructions. Any operation that needs a loop or a runtime routine (multiply, divide, variable shifts, block copies) is a library function call**, so developers decide each time whether to spend those cycles.
2. **Restriction is acceptable.** Assembly is restrictive too. If a feature cannot compile to efficient code, the language leaves it out and a library function covers the need.
3. **One meaning per symbol and keyword.** Each symbol and keyword means exactly one thing, which keeps the language easy to read and the compiler simple. There are two exceptions: unary `+` and `-` (`-5`, `-x`), as in ordinary mathematics, and assembly blocks (section 9), which use standard 6502 assembly syntax.
4. **Predictable output.** Each construct has a documented code shape (section 11). A reader can estimate the generated code from the source.
5. **Readable output.** The compiler writes a listing with source lines, addresses, bytes and cycle counts next to the binary.
6. **Cheap mixing with assembly.** Inline assembly and assembly functions are parsed by the compiler and can use Tern variables by name.
7. **Multiple targets.** CPU variant (which instructions exist) and platform (memory map, file format) are separate settings. Commodore 64 is the first platform; NES, Commander X16, Apple II and Atari 8-bit are planned.

**Non-goals**

- Recursion (see 6.4).
- C compatibility or a C-like standard library.

**Not in v0**

- Floating point.
- Heap allocation. All memory is static.

## 2. Source text

- UTF-8 files with the extension `.tern`.
- Comments: `// to end of line` and `/* block */`.
- Statements end at a newline or `;`.
- Identifiers: `[A-Za-z_][A-Za-z0-9_]*`. Case-sensitive.
- In v0 a program is a single source file. The standard library (10.1) is always available without an import, and only the functions a program uses are linked. A module system comes later.

### 2.1 Literals

| Form | Example | Meaning |
|---|---|---|
| Decimal | `200`, `-5`, `+5` | |
| Hex | `$C000` | |
| Binary | `0b1010_0101` | `_` is allowed as a separator in any number |
| Char | `'A'` | Converted with the platform's character encoding (C64: PETSCII) |
| String | `"HELLO"` | Byte array in the platform encoding. No terminator unless written: `"HI\0"` |
| Bool | `true`, `false` | |

## 3. Types

| Type | Size | Notes |
|---|---|---|
| `u8`, `i8` | 1 | The default numeric types |
| `u16`, `i16` | 2 | Little endian. `.lo` and `.hi` give the bytes as `u8` |
| `bool` | 1 | `0` or `1` in memory. Can live in a CPU flag (section 7) |
| `ptr T` | 2 | Pointer to `T` |
| `[T; N]` | varies | Array, `1 ≤ N ≤ 256` |
| `struct` | varies | See 3.3 |

### 3.1 Arithmetic rules

- All integer arithmetic **wraps**. There is no integer promotion: `u8 + u8` is `u8`.
- Mixing `u8` with `u16` widens the `u8` to `u16`, and the result is `u16`.
- Narrowing must be explicit: `x = big as u8`. `as` on integers keeps the low bits.
- Mixing signed and unsigned operands is a compile error. Cast one side.
- **`*`, `/` and `%` are allowed only in constant expressions**, which are evaluated at compile time: `const SIZE = 40 * 25` is fine, `y * 40` with a variable `y` is a compile error. The 6502 has no multiply or divide instruction, so runtime multiplication and division use library functions (section 10.1), which makes their cost visible in the source.
- `<<` and `>>` require a constant shift count and compile to `asl`/`lsr`/`rol`/`ror` sequences. Shifting by a variable amount uses the library functions `shl8`/`shr8` (section 10.1).
- `>>` on unsigned types is a logical shift (`lsr`). On signed types it keeps the sign (`cmp #$80` / `ror` per shift), which rounds down, not toward zero: `-1 >> 1` is `-1`, and `-3 >> 1` is `-2`.
- **A number literal takes its type from where it is used.** A literal that does not fit that type is a compile error: `300` or `-1` assigned to a `u8`.

### 3.2 Arrays

- Array length is at most 256, so any index fits in the X or Y register.
- Array indexes are always `u8` and produce `abs,X`/`abs,Y` addressing. For memory larger than 256 bytes, use a `ptr`.
- **Arrays with multi-byte elements are split.** `[u16; 32]` is stored as two 32-byte tables, low bytes and high bytes. `a[i]` becomes `lda a_lo,x` / `lda a_hi,x`. Declare it `linear` to get a normal interleaved layout, e.g. for data shared with hardware or assembly: `linear var pts: [u16; 32]`.

### 3.3 Structs

```
struct Enemy { x: u8, y: u8, hp: u8, vel: i16 }
```

A struct describes a **memory layout**: field names, types and offsets. It works as an overlay on memory and is always used in place. **Structs are never copied:** there is no struct assignment, comparison, struct parameter or struct return value. To pass a struct to a function, pass a `ptr` or an array index. To copy one, call `copy()`.

A struct can be used in three ways:

1. **As a variable.** The fields get fixed addresses: `player.hp` is `lda player+2`.
2. **At a fixed address**, e.g. over hardware registers. A field can give its offset from the start of the struct with `@`, which leaves gaps for unused registers:
   ```
   struct Vic { sprite0_x: u8 @ $00, border: u8 @ $20, background: u8 @ $21 }
   var vic: Vic @ $D000
   vic.border = 0                 // sta $D020 (volatile, as with any fixed address)
   ```
3. **Through a pointer.** `p.field` reads or writes the field of the struct `p` points to:
   ```
   var e: ptr Enemy @ zp
   e.hp -= 1                      // ldy #2 / lda (e),y / sec / sbc #1 / sta (e),y
   e = e + sizeof(Enemy)          // step to the next record
   ```

**Nested structs.** A struct field can be another struct. It is a sub-layout inside the outer struct, and the compiler adds the offsets at compile time, so nesting costs nothing at runtime:

```
struct Point { x: u8, y: u8 }
struct Enemy { pos: Point, hp: u8, next: ptr Enemy }

var player: Enemy
player.pos.y = 5                   // sta player+1

var e: ptr Enemy @ zp
e.pos.y = 5                        // ldy #1 / sta (e),y
```

- A struct cannot contain itself directly (its size would be infinite). A pointer to its own type, like `next`, is fine.
- Following a pointer field, as in `e.next.hp`, first copies the pointer into a zero-page temporary, then reads through it.
- **Array fields** such as `name: [u8; 8]` work normally in a single struct (`player.name[i]` is `lda player+off,x`). Through a pointer, the index and the field offset are added: `lda i` / `clc` / `adc #off` / `tay` / `lda (e),y`.

**Arrays of structs are stored as one array per field** ("structure of arrays"). `enemies[i].x` is a single `lda enemies_x,x`, and multi-byte fields are split as in 3.2. Nested structs flatten all the way down: `enemies[i].pos.x` is one table, `enemies_pos_x`. An array field inside an array of structs, as in `enemies[i].name[j]`, can only be indexed with a constant `j`, because a variable `j` would need a multiply. Use a `linear` array and a pointer instead. A `linear` array of structs stores whole records one after another, so a `ptr Enemy` can step through it. Linear struct arrays cannot be indexed, since that would need a multiply by the struct size: use a pointer.

### 3.4 Pointers

- `ptr T` is a 16-bit address. `addr(name)` gives the address of a variable, array or function.
- `p[i]` with `i: u8` compiles to `(zp),Y` addressing. There is no separate dereference operator: use `p[0]`.
- For a pointer to a struct, `p.field` accesses a field (3.3). `p[i]` is not allowed on struct pointers, since it would need a multiply by the struct size.
- A pointer that is dereferenced must be in zero page. The compiler puts it there automatically (section 8.2) and reports an error if zero page runs out.
- Pointer arithmetic is in bytes: `p + 1` is the next byte. Use `p + sizeof(T)` to step by one element.
- **Any pointer converts to any other pointer type with `as`**, with no restrictions and no generated code: `p as ptr Enemy` reads the same memory as an `Enemy`. Pointers also convert to and from `u16` with `as`. The element type only tells the compiler how to read the memory; it never limits what the memory can be used as.

### 3.5 Conversions

**Automatic:** widening to a larger type of the same signedness: `u8` to `u16`, `i8` to `i16`.

**Explicit, with `as`:**

| Conversion | Meaning | Generated code |
|---|---|---|
| `u16 as u8` | keep the low byte | none |
| `u8 as i8`, `i8 as u8` | same bits, read as the other type | none |
| `i8 as i16` (or automatic) | sign extend | a few instructions: high byte becomes `$FF` or `$00` |
| `u8 as u16` (or automatic) | zero extend | high byte = 0 |
| `bool as u8` | `0` or `1` | e.g. from the carry flag: `lda #0` / `rol` |
| `ptr T as u16`, `u16 as ptr T` | address as a number and back | none |
| `ptr T as ptr U` | same memory, read as another type (3.4) | none |

- `as` binds tighter than every operator: `x as u16 << 5` is `(x as u16) << 5`.
- Converting between sizes and signedness extends according to the source type, then reinterprets: `i8 as u16` sign extends.
- Runtime `as` never checks a value; it keeps the low bits. (Compile-time values are checked, see 4.1.)
- **Not allowed:** narrowing without `as`; mixing signed and unsigned in one operation; `u8 as bool` (write `x != 0`).

## 4. Declarations

```
const SCREEN = $0400               // compile-time constant, no storage
const WIDTH: u8 = 40

var score: u16                     // static RAM, placed by the compiler
var lives: u8 = 3                  // initialized at program start
var p: ptr u8 @ zp                // somewhere in zero page
var border: u8 @ $D020             // fixed address: memory-mapped I/O

data sine: [u8; 256] = [ ... ]     // read-only table in memory (may be placed in ROM)
data msg = "GAME OVER"
```

- `const` always means "known at compile time": `const` values take no memory, and `const fn` functions (4.1) run only during compilation.
- `data` declares read-only arrays and strings stored in memory.
- **`@` gives a location**, and that is its only meaning. In a struct field it is the offset from the start of the struct (3.3). Elsewhere it is followed by one of:
  - a constant expression: a fixed address, e.g. `@ $D020` or `@ VIC + $20`,
  - `zp`: anywhere in zero page, chosen by the compiler. This is a guarantee: if zero page is full, compilation fails (8.2),
  - `bank(n)`: in ROM bank `n` (for functions and `data`; see 12.2).
- **Fixed-address variables (`@ addr`) are volatile.** Every read and write in the source happens exactly once and in order. The optimizer never removes, merges or reorders them. All other variables may be cached in registers.
- Global variables without an initializer are zeroed at startup. A variable can be marked `noinit` to skip that.

### 4.1 Compile-time evaluation

**v0:** constant expressions, which may use every operator including `*`, `/` and `%`, plus `include_bytes`, which loads a file into a `data` table: `data ship = include_bytes("ship.bin")`. Tables can be built with external tools and loaded this way.

**Planned: `const fn`.** A function the compiler runs during compilation. Only its result goes into the program; the function itself never becomes 6502 code. It is mainly for building lookup tables.

```
const fn row_addr(row: int) -> int {
    return $0400 + row * 40
}
data rows: [u16; 25] = [row_addr(i) for i in 0..25]
```

- Allowed: all statements, integer math including `*`, `/`, `%`, local arrays, and calls to other `const fn`.
- Not allowed: program variables, `@` addresses, assembly, and calls to normal functions.
- Callable only at compile time: from `const` declarations, `data` initializers and other `const fn`. Calling one from runtime code is an error.
- Compile time has its own number types: `int` (whole numbers with no size limit) and `real` (decimal numbers, with `sin`, `cos`, `sqrt`, `round` and similar). Neither exists at runtime. When a value leaves compile time into a `u8`, `u16` or `data` table, the compiler checks that it fits and reports an error if not.
- Array-building syntax: `[expr for i in range]`.
- A step limit stops a `const fn` that never finishes, with an error.

## 5. Statements

```
if x < 10 { ... } else if x == 20 { ... } else { ... }

while cond { ... }
while true { ... }                 // endless; leave with break or return

for i in 0..10 { ... }             // 0 to 9
for i in 0..=9 { ... }             // 0 to 9
for i in 9..=0 { ... }             // 9 down to 0
for i in n..0 { ... }              // n down to 1
for x in 0..40 step 8 { ... }      // 0, 8, 16, 24, 32
for i in a..=b step -1 { ... }     // variable bounds, counting down
for i in 0..len using X { ... }    // loop variable kept in the X register
for _ in 0..10 { ... }             // repeat 10 times, no loop variable

match state {
    0 => idle()
    1, 2 => walk()
    3..=7 => attack()
    _ => {}
}

break; continue; return value
```

- There is one endless loop form, `while true`. There is no C-style `for (init; cond; step)`.

### 5.1 `for` loops

`for` is a counted loop. Unlike a `while` loop with a counter, it guarantees how the loop variable changes, which lets the compiler choose the best loop shape (section 11).

- `a..b` excludes `b`, `a..=b` includes it.
- **The bounds and step are evaluated once**, before the loop starts.
- **The loop variable is read-only inside the body.** It changes only by the step, at the end of each pass.
- **Direction:**
  1. If both bounds are constants, their order gives the direction: `0..=9` counts up, `9..=0` counts down.
  2. If the end is the constant `0` (`n..0`, `n..=0`), the loop counts down.
  3. Otherwise the loop counts up. A negative `step` counts down: `for i in a..=b step -1`.
- `step` must be a non-zero constant. Without `step`, the step is 1 or −1 depending on direction. As in C, `for i in x..=y step 2` matches `for (i = x; i <= y; i += 2)`, and `x..y` matches `i < y`.
- With a step other than ±1, the counter can jump past the end. With constant bounds the compiler computes the last value and compares against the value after it, as usual. With a variable end, the loop tests "less than" (`cmp` / `bcc`) instead of "not equal", and stops if adding the step carries past the end of the type (e.g. 250, 254, then 258 would wrap to 2). This costs a few extra instructions per pass.
- **Constant ranges:** an empty range (`5..5`) generates no code and gives a warning. A range with one value (`5..=5`) generates the body once, with no loop instructions. The compiler never unrolls longer loops on its own.
- **Variable bounds:** if the range can be empty at runtime, the compiler adds one check before the loop. This costs a few cycles per loop, not per pass, and is left out when the compiler can prove the range is not empty.
- `_` as the loop variable means the value is not needed. The compiler may then count in either direction, e.g. `dex` / `bne`.
- The loop variable is `u8` unless the range needs more. `using X` or `using Y` keeps it in that register.
- Unary `-x` negates (two's complement, wrapping like all arithmetic). Unary `+x` has no effect.
- `&&`, `||` and `!` are logical operators on `bool`, with short-circuit evaluation. `&`, `|`, `^`, `~` are bitwise operators on integers. Each is a separate operator with one meaning (`!` and `!=` are different tokens).
- Assignment operators: `=`, `+=`, `-=`, `&=`, `|=`, `^=`, `<<=`, `>>=`. `x += 1` on memory compiles to `inc`.

## 6. Functions

```
fn add(a: u8, b: u8) -> u8 { return a + b }
fn plot(x: u8 using X, y: u8 using Y) { ... }
fn get_pos() -> u16 { ... }        // returned in A (lo) / X (hi)
fn collides(i: u8 using X) -> bool using carry { ... }
inline fn wait_frame() { ... }
```

### 6.1 Calling convention

- **Parameters** without an annotation are ordinary variables in the callee's frame. The caller stores the arguments directly into them, then calls: `move_to(10, 20)` is `lda #10` / `sta move_to.x` / `lda #20` / `sta move_to.y` / `jsr move_to`. No copying or stack is used.
- Function calls inside arguments, as in `f(a, g(b))`, run before any parameter of the outer call is written. `f` and `g` may share memory (8.1), so writing `f`'s parameters first could let `g` overwrite them.
- Tern code cannot read a function's parameters from outside the function. After the call, their memory may already be in use by another function.
- `using` always means "kept in this register or flag". A parameter written `using A`, `using X` or `using Y` is passed in that register. A `u16` parameter can use a register pair: `using AX`, `using AY` or `using XY` (low byte first).
- **Return values:** `u8`/`i8` in A; `u16`/`i16` in A (low) and X (high); `bool` in the carry flag. Other placements can be written out: `-> u8 using Y`, `-> bool using zero` (the Z flag), `-> bool using negative` (the N flag).
- Registers not used for return values may be changed by any call. The compiler knows each function's actual register use and uses that instead where it can.

### 6.2 Inline functions

`inline fn` bodies are copied into each caller. Small non-recursive functions with a single caller are inlined automatically.

### 6.3 Interrupt handlers

`interrupt fn raster() { ... }` saves and restores the registers it uses and ends with `rti`. How a handler is installed is platform specific.

### 6.4 No recursion

The compiler builds the full call graph. A cycle in the call graph (recursion) is a compile error. This is a permanent design decision, not a v0 limit: it is what allows every local variable to have a fixed address (section 8.1). Supporting recursion would need a software stack, which makes every local variable access slower, in every function.

Algorithms that are naturally recursive (flood fill, tree walks) use an explicit stack: an array plus an index, in a loop. The cost is then visible in the code.

### 6.5 Calls through pointers

A variable can hold a function address and be called like a function. The compiler cannot see which function runs, so the variable's type lists every possible target, which keeps the call graph complete:

```
var handler: fn() targets [idle, walk, attack] = idle
handler = walk
handler()                          // jsr to a "jmp (handler)" stub
```

## 7. Flags as values

A `bool` can live in a CPU flag instead of memory. This removes the "load 0 or 1, then compare" step common in compiled code.

```
fn collides(i: u8 using X) -> bool using carry { ... }

if collides(n) { ... }             // jsr collides / bcc skip
```

- Inside a function returning `bool using carry`, `return true` compiles to `sec`/`rts`, and `return cond` evaluates the condition straight into the carry where possible.
- Comparisons evaluate into flags directly: `if a < b` (unsigned) is `lda a / cmp b / bcs skip`.

## 8. Memory model

### 8.1 Static frames and overlays

- Every function has a static frame: its parameters, locals and compiler temporaries, at fixed addresses.
- **Two frames may share memory if the functions can never be active at the same time**, that is, neither is reachable from the other in the call graph. This is computed from the call graph, so total RAM use is the deepest call path, similar to a stack, but with absolute addressing.
- Interrupt handlers are separate roots. Their frames never overlap with frames reachable from the main program. A function reachable from both main and an interrupt handler is a compile error unless marked `reentrant` (not in v0).

### 8.2 Zero page allocation

The platform lists which zero-page bytes are free. The compiler fills them in this order:

1. Variables marked `@ zp`.
2. Pointers that are dereferenced (required for `(zp),Y`).
3. Other variables ranked by estimated access count: each use in the source counts 1, multiplied by 8 for each enclosing loop.

Steps 1 and 2 are guarantees: if they do not fit, compilation fails with an error that lists every zero-page user and its size. Step 3 is best effort: variables that do not fit go to normal RAM.

Frames may overlap in zero page in the same way as in normal RAM, so zero page is shared between functions that never run at the same time. The listing and report show the final zero-page map.

### 8.3 Hardware stack

The hardware stack is used only for return addresses, interrupt entry, and temporaries in a few runtime routines. Expression evaluation does not use it.

## 9. Inline assembly

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

- Assembly is parsed by the compiler into the same instruction objects it generates itself.
- Operands may name Tern variables, constants, arrays (use `enemies.x` for a field array) and functions. As usual in assembly, a name stands for its address: `lda score` reads the byte at `score`, and `score+1` is the next byte.
- Low and high bytes of an address are written `.lo` and `.hi`, as in Tern: `lda #msg.lo` / `ldx #msg.hi`. The assembler-style `<` and `>` are not used, since they are comparison operators in Tern.
- Labels starting with `.` are local to the block.
- The compiler computes which registers, flags and variables a block reads and writes, and works around them. It never changes instructions inside a block.
- `asm fn` defines a whole function in assembly with a normal typed signature. The compiler adds the final `rts` unless the block ends in `rts`, `rti` or `jmp`.

## 10. Built-ins

| Built-in | Meaning |
|---|---|
| `addr(name)` | Address of a variable, array or function, as `u16` |
| `sizeof(T)` | Size of a type in bytes |
| `len(a)` | Number of elements in an array (compile-time) |

### 10.1 Standard library: cycle-heavy operations

These are ordinary functions, written in Tern or as `asm fn`, and linked only if used. The compiler gives them no special treatment, so they can be read, replaced, or added to (e.g. a faster multiply for one program).

| Function | Signature | Method |
|---|---|---|
| `mul8` | `(a: u8 using A, b: u8 using X) -> u16` | Shift-and-add loop: small, slower |
| `mul8_fast` | `(a: u8 using A, b: u8 using X) -> u16` | Quarter-square tables: much faster, adds about 2 KB of tables |
| `mul16` | `(a: u16, b: u16) -> u16` | Shift-and-add loop, low 16 bits of the product |
| `div8` | `(a: u8 using A, b: u8 using X) -> u8` | Shift-and-subtract loop. Also sets `remainder8` |
| `div16` | `(a: u16, b: u16) -> u16` | Shift-and-subtract loop. Also sets `remainder16` |
| `shl8`, `shr8` | `(x: u8 using A, n: u8 using X) -> u8` | Loop of `n` shifts |
| `shl16`, `shr16` | `(x: u16 using AX, n: u8 using Y) -> u16` | Loop of `n` shifts |
| `copy` | `(dst: ptr u8, src: ptr u8, count: u16)` | Block copy |
| `fill` | `(dst: ptr u8, value: u8, count: u16)` | Block fill |

- Division computes the quotient and the remainder together, as in assembly. The remainder is left in a library variable (`remainder8`, `remainder16`) for the caller to read straight after the call.
- Division by zero does not trap. The result is unspecified.
- Multiplying or dividing by a constant needs no library call: write the shifts and adds (`(w << 5) + (w << 3)` for `w × 40`), or `& 7` for "mod 8".
- Signed versions (`muls8`, `divs8`, …) follow the same pattern.

## 11. Code shapes

These are the intended translations. The optimizer may produce something shorter, never longer.

| Source | Generated code |
|---|---|
| `x = 0` | `lda #0` / `sta x` |
| `x += 1` (x in memory) | `inc x` |
| `w += 1` (u16) | `inc w` / `bne +` / `inc w+1` |
| `a[i] = v` | `ldx i` / `lda v` / `sta a,x` |
| `p[i] = v` | `ldy i` / `lda v` / `sta (p),y` |
| `for i in 9..=0 using X` | `ldx #9` … `dex` / `bpl loop` (start ≤ 127; otherwise one extra `cpx #$FF`) |
| `for i in n..0 using X` | `ldx n` / `beq done` / … `dex` / `bne loop` |
| `for i in 0..n using X` | `ldx #0` / `cpx n` / `beq done` / … `inx` / `cpx n` / `bne loop` |
| `for i in 0..=255 using X` | `ldx #0` … `inx` / `bne loop` |
| `for _ in 0..10` | `ldx #10` … `dex` / `bne loop` |
| `for i in 5..=5` | the body once, with `i` = 5 |
| `if a == 0 { }` | `lda a` / `bne skip` |
| `if a < b { }` unsigned | `lda a` / `cmp b` / `bcs skip` |
| `match` with dense constant cases | jump table: low/high address tables indexed by the value |
| `f(x)` with parameter `using A` | `lda x` / `jsr f` |
| `return true` with `-> bool using carry` | `sec` / `rts` |
| `-x` (u8) | `lda x` / `eor #$FF` / `clc` / `adc #1` |
| `x << 2` | `asl` / `asl` (on A or directly on memory) |
| `(w << 5) + (w << 3)` (w: u16, i.e. w × 40) | shifts and one 16-bit add, inline |
| `mul8(a, b)` | `lda a` / `ldx b` / `jsr mul8` (result in A/X) |
| `i & 7` (i.e. i mod 8) | `lda i` / `and #7` |

## 12. Targets

### 12.1 CPU variants

| Variant | Status | Notes |
|---|---|---|
| `nmos6502` | v0 | All 151 documented opcodes |
| `ricoh2a03` | v0 (encoder/emulator) | NES CPU. Same opcodes; decimal mode has no effect |
| `wdc65c02` | Planned | Adds `stz`, `bra`, `phx`/`phy`, `(zp)` mode, etc. Needed for the Commander X16 |
| `nmos6502+undoc` | Planned, opt-in | Stable undocumented opcodes such as `lax`, `sax` |

### 12.2 Platforms

A platform description gives: CPU variant, free zero-page ranges, RAM ranges for variables, code start address, output file format, startup code, and character encoding.

**C64 (first platform)**

- CPU: `nmos6502`. Output: `.prg` with load address `$0801`.
- Startup: a BASIC line `10 SYS 2061`, then the startup code (zero globals, copy initialized data, call `main`).
- Free zero page: `$02–$8F` (BASIC's work area; the program does not return to BASIC) and `$FB–$FE`.
- Variables: RAM after the program up to `$9FFF`. KERNAL and I/O stay mapped in v0.
- Encoding: PETSCII for strings, with `screen"..."` for screen codes.

**Planned:** NES (iNES, `ricoh2a03`, PRG/CHR banks), Commander X16 (`wdc65c02`), Apple II (binary for DOS 3.3/ProDOS), Atari 8-bit (`.xex`).

## 13. Compiler design

```
source → lexer → parser → AST
       → semantic checks (names, types, constant evaluation)
       → call graph → frame overlay and zero-page allocation
       → lowering to instruction objects (symbolic labels and operands)
       → optimizer (peephole, redundant load removal, register tracking)
       → layout (addresses, branch range fixing, optional page alignment)
       → encoder → outputs
```

- **Built-in assembler.** There is no text assembly step. The compiler works on typed instruction objects, which the encoder turns into bytes. The instruction table includes sizes and cycle counts, so layout can pick short branches, avoid page-crossing penalties in marked loops, and report cycles.
- **Branch range fixing.** A conditional branch reaches only −128 to +127 bytes. Layout repeats until every branch fits. Branches that do not fit become an inverted branch over a `jmp`.
- **Outputs:**
  - the program file (`.prg` for C64),
  - a listing (`.lst`) with source lines, addresses, bytes and cycles,
  - a VICE label file (`.vs`),
  - a report: bytes and cycle estimates per function, zero-page map, overlay map.

### 13.1 Project layout (C#)

| Project | Contents |
|---|---|
| `Tern.Cpu` | Instruction set table, encoder, 6502 emulator with cycle counting |
| `Tern.Compiler` | Front end, allocation, code generation, optimizer, layout (planned) |
| `Tern.Cli` | Command-line tool (planned) |
| `Tern.Cpu.Tests` | Encoder and emulator tests, including Klaus Dormann's functional test |

### 13.2 Testing approach

- Every language feature has test programs that run in the built-in emulator. Tests check results in memory and record bytes and cycles.
- Benchmarks compare the same task in Tern, hand-written assembly, and other compilers (cc65, Prog8). A size or speed regression fails the build.

## 14. Open questions

Each item is decided when the feature it belongs to is built.

1. **Bank switching** (NES mappers, C64 cartridges). Placement is `@ bank(n)`. Open: how calls between banks work. Likely direction: the compiler generates a small stub for each call into another bank, and direct access to another bank's data is an error.
2. **Functions called from both main and interrupts.** v0: a compile error. Likely direction: the compiler makes a second copy of the function for the interrupt side, which costs ROM but no speed and needs no stack.
3. **`pha`/`pla` for temporaries.** `pha`/`pla` is 2 bytes and 7 cycles, against 4 bytes and 6 cycles for a zero-page temporary. Likely direction: the optimizer chooses, but never across a function call. Settle with benchmarks.
4. **Overflow checks in debug builds.** Likely direction: no extra code in the program. The built-in emulator checks for overflow while running tests, using the compiler's debug information.
5. **Character encodings** for platforms other than C64. Each platform file defines its own.
6. **`using` on local variables** (`var count: u8 using Y`). Leave out until generated code shows a need.
7. **A general decorator syntax.** Only if modifier keywords grow past five or six.
8. **Language name.** "Tern" is a placeholder.
