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
- Statements end at a newline or `;`. A newline inside `( )` or `[ ]` does not end a statement, so argument lists and tables can span lines. A `{` or `}` ends that rule.
- Identifiers: `[A-Za-z_][A-Za-z0-9_]*`. Case-sensitive.
- A program can be split into modules, one per file. `import name` loads `name.tern` (2.2).

### 2.1 Literals

| Form | Example | Meaning |
|---|---|---|
| Decimal | `200`, `-5`, `+5` | |
| Hex | `$C000` | |
| Binary | `0b1010_0101` | `_` is allowed as a separator in any number |
| Char | `'A'`, `screen'A'` | One byte in the target's default encoding, or in the named encoding |
| String | `"HELLO"`, `screen"HELLO"` | Byte array in the target's default encoding, or in the named encoding. No terminator unless written: `"HI\0"` |
| Bool | `true`, `false` | |

**Escapes** in char and string literals: `\0` (zero byte), `\n` (the encoding's line break; PETSCII `$0D`), `\\`, `\"`, `\'`, and `\$NN` (a raw byte, not converted by the encoding). A character the encoding has no code for is a compile error that suggests `\$NN`.

**Encodings** are built into the compiler. The compilation target sets the default for literals without a prefix; a prefix names another encoding: `petscii_mixed"Hello"`. Any encoding may be used on any target, since an encoding is only a mapping from characters to bytes. A prefix has one meaning: the encoding of the literal right after it.

| Encoding | Machine | Notes |
|---|---|---|
| `petscii` | C64, Commander X16 | Default on the C64. Upper case and graphics character set; `a`-`z` give the same codes as `A`-`Z` |
| `petscii_mixed` | C64, Commander X16 | For the lower/upper-case character set. No `π` (`$FF` shows another character in this set) |
| `screen` | C64 | Screen codes (bytes stored directly in screen memory), upper case and graphics set |
| `screen_mixed` | C64 | Screen codes for the lower/upper-case set |
| `ascii` | Commander X16 (ISO mode), general | Plain 7-bit ASCII |
| `apple` | Apple II | ASCII with the high bit set (normal text) |
| `atascii` | Atari 8-bit | Default on the Atari |
| `atari_screen` | Atari 8-bit | Atari internal screen codes |

Each encoding is implemented together with its platform. The NES has no character set (text is tile numbers chosen by each game), so it has no default encoding: a literal without a prefix is an error there. User-defined encodings are planned (14.5).

### 2.2 Modules and imports

```
import mem
import text
import c64                 // platform module: only when compiling for the C64

fn main() {
    text.put(c64.SCREEN, addr(title), len(title))
    mem.fill(c64.COLOR, 1, 1000)
}
```

- `import name` goes at the top of a file, before other declarations. It loads `name.tern` from, in order: the compiler's `lib/<platform>/` folder (e.g. `lib/c64/`), the compiler's `lib/` folder (the standard library, 10.1), then the folder of the importing file. The same mechanism splits your own program into files. A module name must match its file name exactly, including case.
- Everything declared at the top level of a module is used with the module name: `text.put`, `c64.SCREEN`. `.` still means "member of", as for fields and `.lo`/`.hi`. Names in different modules never clash.
- **`private`** before a declaration (`private var w0: u8`, `private data TABLE ...`, `private fn helper()`, `private const`, `private struct`) makes it usable only inside its own module, including from assembly. Without it, a top-level declaration is public.
- A module is loaded once, however many files import it. Modules may import each other.
- Only the program's main file has `fn main()`.
- Only functions reachable from `main` are compiled. A module's global variables and `data` are kept only if a compiled function names them (in Tern code or as an assembly operand). An imported module costs nothing for the parts that are not used. The main file's globals are always kept, since an unused one may reserve memory on purpose (e.g. `@ zp`).
- **Module parameters** are constants the program sets when it imports a module. The module declares them once, in a header that is the first line of the file (before its imports):
  ```
  // lib/c64/sprites.tern
  module sprites(COUNT: u8 = 32, FLICKER: bool = false)

  var list: [Sprite; COUNT]
  ```
  and the main file sets any of them by name: `import sprites(COUNT = 40)`. `import sprites` alone uses the defaults.
  - Only the main file may set parameters, since a module is loaded once. Other files import the module plainly and get the program's values.
  - Each parameter has a type (an integer type or `bool`) and a default. A value is a constant expression using literals and the main file's constants that do not depend on imported modules; it is checked against the type. An unknown name, a name given twice, or a value that does not fit is an error.
  - Inside the module a parameter is an ordinary constant; outside it is `sprites.COUNT`. Parameters cost nothing at runtime.
  - The name in the header must match the file name. A main file has no header; a module needs one only if it has parameters.
- Platform modules live in `lib/<platform>/` and can only be imported when compiling for that platform; importing one for another platform is an error naming the platform. A platform folder can also provide its own version of a general module.

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
- `<<` and `>>` require a constant shift count and compile to `asl`/`lsr`/`rol`/`ror` sequences. Shifting by a variable amount uses the library functions `math.shl8`/`math.shr8` (section 10.1).
- `>>` on unsigned types is a logical shift (`lsr`). On signed types it keeps the sign (`cmp #$80` / `ror` per shift), which rounds down, not toward zero: `-1 >> 1` is `-1`, and `-3 >> 1` is `-2`.
- **A number literal takes its type from where it is used.** A literal that does not fit that type is a compile error: `300` or `-1` assigned to a `u8`.

### 3.2 Arrays

- Array length is at most 256, so any index fits in the X or Y register.
- Exception: `data` from a string literal or `include_bytes` may be longer than 256 bytes (e.g. a 2 KB character set). It cannot be indexed; use it through a pointer (`addr(name)`).
- Array indexes are always `u8` and produce `abs,X`/`abs,Y` addressing. For memory larger than 256 bytes, use a `ptr`.
- **An index expression wraps like all arithmetic** (3.1): with `i = 255`, `a[i + 1]` is `a[0]`, so a 256-element ring buffer can use `buf[head + 1]`. The optimizer folds a constant offset into the address (`a[i - 1]` as `lda a-1,x`) only where the result is the same: when a wrapped index would be outside the array anyway, or when it can prove the sum does not wrap (e.g. a loop counter with constant bounds). Otherwise `a[i + c]` computes the index first (`lda i` / `clc` / `adc #c` / `tax`).
- **An index outside the array is not supported.** The compiler may assume it never happens: the optimizer treats `a[i] = v` as changing only `a`. There is never a check in the program. A constant index past the end is a compile error, and so is a `for` loop with constant bounds that indexes past the end (`for i in 0..20 { a[i] }` on a `[u8; 10]`). A variable index is checked by the built-in emulator in test runs (14.4). Code that writes across variables on purpose uses a pointer, whose writes the optimizer treats as changing any memory (4).
- **Arrays with multi-byte elements are split.** `[u16; 32]` is stored as two 32-byte tables, low bytes and high bytes. `a[i]` becomes `lda a_lo,x` / `lda a_hi,x`. Declare it `linear` to get a normal interleaved layout, e.g. for data shared with hardware or assembly: `linear var pts: [u16; 32]`.
- **`linear` arrays of 16-bit elements** (`u16`, `i16`, `ptr T`) store each value whole, low byte first, as in C. `linear` goes before `var` or `data`. Costs and rules:
  - A variable index is doubled first: `pts[i]` is `lda i` / `asl` / `tax` / `lda pts,x` / `lda pts+1,x` (section 11). A constant index is a fixed offset, with no extra code.
  - A linear 16-bit array has at most 128 elements, so the doubled index fits in a register.
  - `addr(pts)` gives a `ptr u16` (or `ptr i16`, ...) to the first element, which `p[i]` and `p + 2` use as usual (3.4).
  - `linear` on an array of one-byte elements is an error, since they are already stored in order. `linear` arrays of structs are described in 3.3.

### 3.3 Structs

```
struct Enemy { x: u8, y: u8, hp: u8, vel: i16 }

struct Voice {                     // one field per line also works
    freq: u16
    pulse: u16
    control: u8,                   // commas are optional at line ends; a trailing comma is allowed
}
```

A struct describes a **memory layout**: field names, types and offsets. It works as an overlay on memory and is always used in place. **Structs are never copied:** there is no struct assignment, comparison, struct parameter or struct return value. To pass a struct to a function, pass a `ptr` or an array index. To copy one, call `copy()`.

A struct can be used in three ways:

1. **As a variable.** The fields get fixed addresses: `player.hp` is `lda player+2`.
2. **At a fixed address**, e.g. over hardware registers. A field can give its offset from the start of the struct with `@`, which leaves gaps for unused registers:
   ```
   struct Vic { sprite0_x: u8 @ $00, border: u8 @ $20, background: u8 @ $21 }
   var vic: Vic @ $D000
   vic.border = 0                 // sta $D020 (volatile: in the platform's I/O area, 4)
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
- **Field offsets.** A field without `@` follows the previous field in the declaration, wherever that one is. Fields with `@` may be given in any order. Overlapping fields are an error (there are no unions).
- Struct names share the namespace of constants, variables and functions in their file. A struct cannot be named like a built-in type.
- Following a pointer field, as in `e.next.hp`, first copies the pointer into a zero-page temporary, then reads through it.
- Through a pointer, a field's offset must be 0-255, since that is what `(p),Y` reaches. A larger offset is an error at the access, not at the declaration, since the struct may still be used as a variable or at a fixed address.
- **Array fields** such as `name: [u8; 8]` work normally in a single struct (`player.name[i]` is `lda player+off,x`). Through a pointer, the index and the field offset are added: `lda i` / `clc` / `adc #off` / `tay` / `lda (e),y`. Array fields with 16-bit elements are split inside the record, as in 3.2: `pts: [u16; 2]` is two low bytes, then two high bytes.
- Array fields whose elements are structs (`parts: [Point; 4]`) are not supported yet (14.11).
- **No initial values.** There are no struct literals: struct variables and arrays of structs cannot have an initial value, and global ones are zeroed at startup. Local struct variables are allowed without one, like local arrays; their memory is shared (8.1), so it holds leftovers. `data` cannot hold structs.
- `addr(player)` gives a `ptr Enemy`. `addr(player.name)` gives the address of a field of a struct variable, or of a struct at a fixed address (3.4).

**Arrays of structs are stored as one array per field** ("structure of arrays"). `enemies[i].x` is a single `lda enemies_x,x`, and multi-byte fields are split as in 3.2. Nested structs flatten all the way down: `enemies[i].pos.x` is one table, `enemies_pos_x`. Precisely, there is one table per *byte* of the record: byte k of record i is at `base + k * n + i`, where `n` is the array length. A gap left by `@` offsets costs one table of `n` bytes per gap byte. An array field inside an array of structs, as in `enemies[i].name[j]`, can only be indexed with a constant `j`, because a variable `j` would need a multiply. Use a `linear` array and a pointer instead. A `linear` array of structs (`linear var list: [Enemy; 8]`; `linear` goes before `var`) stores whole records one after another, so a `ptr Enemy` can step through it; `addr(list)` points to the first record. Linear struct arrays cannot be indexed, since that would need a multiply by the struct size: use a pointer. `addr()` of an array of structs stored as tables is an error.

### 3.4 Pointers

- `ptr T` is a 16-bit address. `addr(name)` gives a pointer to a variable or array: `ptr T` for a variable of type `T`, a pointer to the first element for an array of one-byte elements, `ptr u8` to the low-byte table for a split array of 16-bit values, and `ptr u16` (or the element type) for a `linear` array of 16-bit values (3.2). A field of a struct variable or of a struct at a fixed address works too: `addr(player.name)` (no code beyond loading the constant address). Fields through a pointer and in an array of structs are errors. Use `as u16` for the address as a number. `addr(f)` of a function is a `u16` constant: its address, e.g. for a vector or a jump table (6.5).
- A number literal (or an untyped `const`) converts to a pointer without `as`, since literals take their type from context: `var p: ptr u8 = $0400`, `p == 0`. A typed `u16` value needs `as`.
- `p[i]` with `i: u8` compiles to `(zp),Y` addressing. There is no separate dereference operator: use `p[0]`.
- On a pointer to a 16-bit type, the index counts elements: `p[i]` reads the bytes at offsets `2*i` and `2*i+1`. The byte offset must fit in the 256 bytes `(zp),Y` reaches: a constant index above 127 is an error, and a variable index of 128 or more wraps within those 256 bytes.
- `.lo` and `.hi` cannot be used on pointers, since `p.name` means a field of the struct `p` points to (3.3). Write `(p as u16).hi`.
- For a pointer to a struct, `p.field` accesses a field (3.3). `p[i]` is not allowed on struct pointers, since it would need a multiply by the struct size.
- A pointer that is dereferenced must be in zero page. The compiler puts it there automatically (section 8.2) and reports an error if zero page runs out.
- Pointer arithmetic is in bytes: `p + 1` is the next byte. Use `p + sizeof(T)` to step by one element. Offsets are unsigned; use `p - n` to move back.
- `p - q` of two pointers of the same type gives the distance in bytes as a `u16`. For different types, convert one with `as` (no code).
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
var border: u8 @ $D020             // fixed address: memory-mapped I/O, volatile
volatile var flags: u8 @ $C000     // fixed-address RAM that something else changes

data sine: [u8; 256] = [ ... ]     // read-only table in memory (may be placed in ROM)
data msg = "GAME OVER"
```

- `const` always means "known at compile time": `const` values take no memory, and `const fn` functions (4.1) run only during compilation.
- `data` declares read-only arrays and strings stored in memory.
- **`@` gives a location**, and that is its only meaning. In a struct field it is the offset from the start of the struct (3.3). Elsewhere it is followed by one of:
  - a constant expression: a fixed address, e.g. `@ $D020` or `@ VIC + $20`,
  - `zp`: anywhere in zero page, chosen by the compiler. This is a guarantee: if zero page is full, compilation fails (8.2),
  - `bank(n)`: in ROM bank `n` (for functions and `data`; see 12.2).
- **Volatile memory.** Every read and write of volatile memory in the source happens exactly once and in order; the optimizer never removes, merges or reorders them. Memory is volatile when:
  - it lies in one of the platform's volatile areas (12.2), such as I/O registers. This needs no keyword: `var border: u8 @ $D020` is volatile,
  - the variable is marked `volatile`, a prefix like `noinit`: `volatile var flags: u8 @ $C000`. Use it for RAM that hardware or outside code changes behind the program's back. On a struct or array it covers every field and element. It is allowed only on global `var`s; writing it on a variable that is already volatile is allowed,
  - the variable is used by functions reachable from main and from an interrupt handler, or from two handlers (8.1). The compiler finds this from the call graph,
  - it is read or written through a pointer, since the compiler cannot know where a pointer points.

  All other memory is ordinary: its values may be kept in registers, and redundant reads and writes removed. An asm block, and a call to a routine at a fixed address, make the optimizer forget the values it holds for ordinary memory. There is no way to mark memory inside a volatile area as ordinary; a memory layout declaration may add one later (14.12).
- **A fixed-address variable must not overlap the program**, its data, or the variables the compiler places. That is a compile error that names the variable and both address ranges. Fixed-address variables are often graphics data that the VIC must find in a given 16 KB bank; move the data or make the program smaller. Fixed-address variables may overlap each other.
- Global variables without an initializer are zeroed at startup. A global variable can be marked `noinit` to skip that. It is a prefix like `linear`: `noinit var buf: [u8; 256]`. It is an error with an initial value, at a fixed address (never zeroed anyway), on `data` and on locals.
- Local variables (other than arrays) must have an initial value. Their memory is shared with other functions (8.1), so it holds no useful value on entry.

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
- A `const fn` can live in an imported module (2.2), so table generators can be shared between programs as Tern scripts: `import tables` … `data sine = tables.sine(256)`. Reading files from compile-time code (e.g. converting an image into sprite data) is a possible later extension.

**Compile-time `assert`**: a top-level declaration that generates no code and fails compilation with its message when the condition is false:

```
assert COUNT >= 8 && COUNT <= 64, "sprites.COUNT must be 8 to 64"
assert sizeof(Enemy) * 32 <= 256, "the enemy table must fit in a page"
```

The condition is a constant `bool` expression and the message a string literal. In a module, the error also names the main file's `import` line when a module parameter caused it, so a wrong setting is reported where it was made.

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
- `step` is a non-zero constant. Without `step`, the step is 1 or −1 depending on direction.
- **Variable step:** a loop that counts up with a `u8` counter may have a `u8` expression as its step, evaluated once like the bounds: `for m in (n << 1)..=255 step n`. It follows the rule for a variable end below: the loop stops when adding the step carries past 255, so `..=255` compiles to `adc` / `bcc` with no compare (section 11). A step that is 0 at runtime never ends the loop; the compiler adds no check (design rule 1). A negative or 16-bit variable step is an error. As in C, `for i in x..=y step 2` matches `for (i = x; i <= y; i += 2)`, and `x..y` matches `i < y`.
- With a step other than ±1, the counter can jump past the end. With constant bounds the compiler computes the last value and compares against the value after it, as usual. With a variable end, the loop tests "less than" (`cmp` / `bcc`) instead of "not equal", and stops if adding the step carries past the end of the type (e.g. 250, 254, then 258 would wrap to 2). This costs a few extra instructions per pass.
- **Constant ranges:** an empty range (`5..5`) generates no code and gives a warning. A range with one value (`5..=5`) generates the body once, with no loop instructions. The compiler never unrolls longer loops on its own.
- **Variable bounds:** if the range can be empty at runtime, the compiler adds one check before the loop. This costs a few cycles per loop, not per pass, and is left out when the compiler can prove the range is not empty.
- `_` as the loop variable means the value is not needed. The compiler may then count in either direction, e.g. `dex` / `bne`.
- The loop variable is `u8` unless the range needs more. `using X` or `using Y` keeps it in that register.

### 5.2 `match`

- The value is a `u8` or `i8`. A 16-bit value is not supported yet (match on `.lo` or `.hi`, or use `if`); `bool`, pointers and structs are errors.
- One arm per line: `patterns => body`, where the body is a block or one statement.
- Patterns are constant expressions (numbers, characters, `const`s), alternatives separated by `,`, and inclusive ranges `lo..=hi`. `lo..hi` is an error: a range in a match includes its end.
- `_` matches every other value. It must be alone in the last arm. Without `_`, a value that matches no pattern skips the match.
- A value in two patterns, or an empty range, is an error. There is no fall-through.
- Code shapes, including when a jump table is used: section 11.
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
- **Return values:** `u8`/`i8` in A; `u16`/`i16` in A (low) and X (high); `bool` in the carry flag. Other placements can be written out: `-> u8 using Y`, `-> u16 using XY` (any register pair `AX`, `AY`, `XY`, low byte first), `-> bool using zero` (the Z flag), `-> bool using negative` (the N flag).
- Registers not used for return values may be changed by any call. The compiler knows each function's actual register use and uses that instead where it can.

### 6.2 Inline functions

`inline fn` bodies are copied into each caller. An inline function keeps its frame: its parameters and locals have fixed addresses, as for a call, so inlining saves the `jsr`/`rts` but no memory. Calling an inline function counts as a call (e.g. in a `for ... using X` loop, and between roots, 8.1). `inline asm fn` copies its assembly into each caller in place of the `jsr`; its parameters must all be registers (fixed inputs and memory parameters are errors) and the compiler adds no `rts`. With a flag return it is a cheap way to test a flag (section 7). The optimizer inlines small functions with a single caller automatically.

### 6.3 Interrupt handlers

`interrupt fn raster() { ... }` saves and restores the registers it changes and ends with `rti`; the CPU saves the flags.

- A register is saved if any instruction in the handler changes it, including instructions in asm blocks. Any `jsr` counts as changing A, X and Y. X and Y are saved through A, so A is saved whenever X or Y is. The order is A, X, Y.
- The handler starts with `cld`, since main may be in decimal mode (the `bcd` library, or an asm block with `sed`) when the interrupt arrives. Decimal mode only changes `adc` and `sbc`, so `cld` is left out when the handler executes neither: no `adc` or `sbc` in its code or asm blocks, and no `jsr`.
- Code the compiler cannot follow, such as an asm `jmp` out of the handler, saves and restores registers itself.
- A handler cannot be called from ordinary code, neither from Tern nor with `jsr`. Naming it in assembly (`lda #raster.lo`) or with `addr(raster)` is not a call.
- **An `interrupt fn` may be called from another `interrupt fn`.** The called one is then compiled as a *part*: its body ending in `rts`, with no register saving and no `rti`; the installed (outer) handler saves registers once and ends with the one `rti`. Each `interrupt fn` is compiled in the forms that are used: as a handler if its address is taken, as a part if it is called. This lets a program combine library interrupt code (a sprite multiplexer, a scroller) in one handler it installs itself:
  ```
  interrupt fn frame() {
      if sprites.raster() { scroll.top() }
  }
  ```
  A part may have register parameters and a return value (e.g. `-> bool using carry`); such an `interrupt fn` cannot be installed (`addr()` of it is an error). A part reached from two handlers is an error, as for any function with memory (8.1).
- Every `interrupt fn` in the main file is compiled, even if nothing installs it. A module's handler is compiled only when a compiled function names it.
- **Installing a handler is platform specific**, done by platform library functions (e.g. in `c64`) or an asm block, using `addr()` (6.5).

### 6.4 No recursion

The compiler builds the full call graph. A cycle in the call graph (recursion) is a compile error. This is a permanent design decision, not a v0 limit: it is what allows every local variable to have a fixed address (section 8.1). Supporting recursion would need a software stack, which makes every local variable access slower, in every function.

### 6.5 Function addresses

`addr(f)` is the address of function `f`, a `u16` constant that costs nothing: `irq_vector = addr(raster)`, or a jump table in `data`.

- Allowed for functions without memory parameters (register parameters are fine), since a call through the address cannot set them. An error for `inline fn`, which has no single body.
- The compiler cannot see calls through an address, so a function whose address is taken, and every function it reaches, get frames that overlap no other frame (8.1). For an `interrupt fn` or a routine at a fixed address nothing changes.
- A handler that the CPU calls must be an `interrupt fn`; an ordinary function ends with `rts`. The compiler cannot check this: `addr()` gives a plain `u16`, so `c64.set_irq(addr(plain_fn))` compiles. It is a documented convention of the installing functions.
- In assembly, `#f.lo` / `#f.hi` take the address of `f` like `addr(f)`; `jsr f` and `jmp f` are calls. Assembly may take the address of a function with memory parameters, since it can set them itself.
- A function reached only through its address has no known root, so the two-roots check (8.1) cannot see it. Calling it through its address from two roots (main and a handler) is the program's responsibility, like `keeps` (6.6). Globals it uses count as used from an unknown root, so any it shares with other code are volatile.

Algorithms that are naturally recursive (flood fill, tree walks) use an explicit stack: an array plus an index, in a loop. The cost is then visible in the code.

### 6.5 Calls through pointers

A variable can hold a function address and be called like a function. The compiler cannot see which function runs, so the variable's type lists every possible target, which keeps the call graph complete:

```
var handler: fn() targets [idle, walk, attack] = idle
handler = walk
handler()                          // jsr to a "jmp (handler)" stub
```

### 6.6 Routines at a fixed address

A routine that already exists in memory, such as a KERNAL routine in ROM, is declared as a function with no body, placed with `@`:

```
fn chrout(ch: u8 using A) @ $FFD2 keeps X, Y
fn getin() -> u8 using A @ $FFE4

chrout('A')                        // lda #$41 / jsr $FFD2
```

- A call is a direct `jsr` to the address. No stand-in function is generated.
- The address is a constant expression, as for any `@`.
- **Parameters must be passed in registers** (`using A`, `X`, `Y`, `AX`, `AY`, `XY`). A memory parameter is an error, since the routine has no frame to write into. Return values follow 6.1, including flags: `-> bool using carry`.
- **Fixed inputs.** A constant in place of a parameter name is a value the compiler passes on every call; callers do not write it. It can go in a register (`3 using X`) or in the carry (`true using carry`, `false using carry`). Fixed inputs and ordinary parameters can be mixed. This lets one routine that takes a mode in a flag or register be declared under several names:
  ```
  fn get_cursor(true using carry) -> u16 using XY @ $FFF0       // KERNAL PLOT: read
  fn set_cursor(false using carry, row: u8 using X, col: u8 using Y) @ $FFF0

  set_cursor(5, 10)                // ldx #5 / ldy #10 / clc / jsr $FFF0
  var pos = get_cursor()           // sec / jsr $FFF0 (row in pos.lo, column in pos.hi)
  ```
  A fixed register value is loaded with `lda`/`ldx`/`ldy #n`, and a fixed carry is one `sec` or `clc` right before the `jsr`, after all other arguments. The carry is the only flag allowed, since it is the only one with both a set and a clear instruction. Fixed inputs are only for routines at a fixed address: an `asm fn` sets them up in its own code.
- **Registers:** the compiler cannot see inside the routine, so it assumes that a call changes A, X, Y and the flags. `keeps` after the address lists registers the routine leaves unchanged (`A`, `X`, `Y`; flags always count as changed). This is a promise taken from the routine's documentation; the compiler does not check it. A `for ... using X` loop may call a routine that keeps X (likewise Y); calls to other functions in such a loop are still an error.
- The routine is a leaf in the call graph: it has no frame and calls nothing the compiler knows about.
- In assembly the name stands for the address, as for any function: `jsr chrout`, `lda #chrout.lo`. For a routine with fixed inputs, `jsr get_cursor` is a plain `jsr $FFF0`: the block sets the carry or registers itself. `addr(chrout)` is the routine's address.
- A routine that returns values in more than the return registers of 6.1, or needs other setup, is wrapped in an `asm fn` (section 9).
- A function with a body and `@ $addr` (compiled code placed at a fixed address) is not supported yet.

## 7. Flags as values

A `bool` can live in a CPU flag instead of memory. This removes the "load 0 or 1, then compare" step common in compiled code.

```
fn collides(i: u8 using X) -> bool using carry { ... }

if collides(n) { ... }             // jsr collides / bcc skip
```

- Inside a function returning `bool using carry`, `return true` compiles to `sec`/`rts`, and `return cond` evaluates the condition straight into the carry where possible.
- Comparisons evaluate into flags directly: `if a < b` (unsigned) is `lda a / cmp b / bcs skip`.
- An `inline asm fn` with a flag return tests a flag set by its own instructions, with no `jsr`:
  ```
  inline asm fn fire_pressed() -> bool using zero {
      lda #$10
      bit $DC00          // joystick 2 fire, 0 when pressed
  }
  if fire_pressed() { shoot() }      // lda #$10 / bit $DC00 / bne skip
  ```
- With an empty body (`inline asm fn carry_set() -> bool using carry {}`) it reads the flag as the code before it left it, at no cost (`if carry_set()` is `bcc skip`). This is defined only right after an `asm` block: when the statement following the block is `if f()`, `while f()` or `v = f()` (also with `!`), the compiler puts nothing that changes flags between the block and the test. For `while f()`, the test after each pass is defined the same way when the loop body ends with an `asm` block; that block's flag is what the next test reads:
  ```
  asm { jsr read_byte }              // carry set at end of file
  while !carry_set() {
      process()
      asm { jsr read_byte }          // sets the flag for the next test
  }
  ```
  A test reached any other way (a body ending in Tern code, `continue`) reads whatever flags that code left. The compiler never checks where the flag came from: using it is the programmer's choice. After compiled Tern code the flags are unspecified (generated code makes no promise about them, and the optimizer may change it), with no error or warning. Prefer putting the instructions that set the flag inside the function.

## 8. Memory model

### 8.1 Static frames and overlays

- Every function has a static frame: its parameters, locals and compiler temporaries, at fixed addresses.
- **Two frames may share memory if the functions can never be active at the same time**, that is, neither is reachable from the other in the call graph. This is computed from the call graph, so total RAM use is the deepest call path, similar to a stack, but with absolute addressing.
- Interrupt handlers are separate roots. Their frames never overlap with frames reachable from the main program.
- **A function reachable from two roots** (main and a handler, or two handlers) is a compile error, unless it has no memory of its own: no memory parameters, no locals and no compiler temporaries, and it calls only such functions. Its only state is registers, which the handler saves. When the reason is a hidden temporary (e.g. for a 16-bit addition), the error says so. Routines at a fixed address may be called from any root.
- **A module's `private` variable used from two roots** is a compile error, e.g. the library's work bytes (`math.w0`-`w2`): the two sides would overwrite each other's values. Public variables and `data` may be shared.
  - Exception: when one of the two roots is an `interrupt fn` declared in the variable's own module, sharing is allowed. The module's author wrote both sides, so the sharing is by design, e.g. a `timer` module whose handler counts `private var ticks` and whose `now()` reads it. The variable is volatile, as below.
- A global variable used from two roots is volatile (4), so a main loop that waits for a flag set by a handler reads it on every pass.

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
- A field of a struct variable or of a struct at a fixed address is an address too: `sta c64.vic.border` is `sta $D020`, and `#c64.vic.background.hi` works. Fields through a pointer have no fixed address, so they are an error in assembly.
- Low and high bytes of an address are written `.lo` and `.hi`, as in Tern: `lda #msg.lo` / `ldx #msg.hi`. The assembler-style `<` and `>` are not used, since they are comparison operators in Tern.
- An immediate operand is a number from -128 to 255, or a byte of an address: `#name.lo`, `#name.hi`. `#name` alone is an error.
- Labels starting with `.` are local to the block.
- One instruction per line. Comments use `//` and `/* */` as elsewhere; `;` is an error in a block, so pasted assembly with `;` comments fails instead of being misread.
- Mnemonics and the index registers in `,x`/`,y` ignore case (`LDA t,X`). Labels and Tern names are case-sensitive. `asl a` (or `A`, or `asl` alone) means the accumulator, also for `lsr`, `rol` and `ror`, even if a variable `a` exists.
- The compiler computes which registers, flags and variables a block reads and writes, and works around them. It never changes instructions inside a block, except that an operand in zero page uses the zero-page form of the same instruction, as in any assembler. Branches are never rewritten: a branch out of range is an error. A way to force absolute addressing may be added if timing-exact code needs it.
- `jsr f` and `jmp f` can call Tern functions whose parameters are all registers (or that have none). For a function with memory parameters they are an error, since its parameters cannot be named outside it (6.1). `#f.lo` / `#f.hi` take a function's address; this counts as a call from the enclosing function, for the call graph and frame sharing.
- `asm fn` defines a whole function in assembly with a normal typed signature. The compiler adds the final `rts` unless the block ends in `rts`, `rti` or `jmp`.

## 10. Built-ins

| Built-in | Meaning |
|---|---|
| `addr(name)` | Pointer to a variable, array, or field of a struct variable; address of a function (3.4) |
| `sizeof(T)` | Size of a type in bytes |
| `len(a)` | Number of elements in an array (compile-time) |

### 10.1 Standard library

The standard library is ordinary Tern and `asm fn` source in the compiler's `lib/` folder, imported like any module (2.2). It is not part of the language: the compiler gives it no special treatment, so it can be read, copied and changed (e.g. a faster multiply for one program). Its purpose is to make common tasks easy from the first program, and to be an example of good Tern code.

**Strings and memory blocks are passed as a pointer plus a length**, matching `len()` on literals. The library has no zero-terminated variants.

The tables below describe the library as written in M4 and M5. Work variables and tables that callers should not use are `private` (2.2).

**`math`**: cycle-heavy arithmetic (the operators only work on constants, 3.1)

| Function | Signature | Method |
|---|---|---|
| `mul8` | `(a: u8 using A, b: u8 using X) -> u16` | Shift-and-add loop: small, slower |
| `mul8_fast` | `(a: u8 using A, b: u8 using X) -> u16` | Quarter-square tables: much faster, adds 1 KB of tables (four 256-byte tables), only when called |
| `mul16` | `(m: u16, n: u16) -> u16` | Shift-and-add loop, low 16 bits of the product |
| `div8` | `(a: u8 using A, b: u8 using X) -> u8` | Shift-and-subtract loop. Also sets `math.remainder8` |
| `div16` | `(n: u16, d: u16) -> u16` | Shift-and-subtract loop. Also sets `math.remainder16` |
| `shl8`, `shr8` | `(x: u8 using A, n: u8 using X) -> u8` | Loop of `n` shifts |
| `shl16`, `shr16` | `(x: u16 using AX, n: u8 using Y) -> u16` | Loop of `n` shifts |

- Division computes the quotient and the remainder together, as in assembly. The remainder is left in a library variable (`math.remainder8`, `math.remainder16`) for the caller to read straight after the call.
- Division by zero does not trap. The result is unspecified.
- Multiplying or dividing by a constant needs no library call: write the shifts and adds (`(w << 5) + (w << 3)` for `w × 40`), or `& 7` for "mod 8".
- Memory parameters of `asm fn` routines are not named `a`, since `asl a` means the accumulator (9). Arguments are positional, so callers are not affected.
- Signed versions (`muls8`, `divs8`, ...) will follow the same pattern; they are added when a program needs them.

**`mem`**: memory blocks

| Function | Signature | Purpose |
|---|---|---|
| `copy` | `(dst: ptr u8, src: ptr u8, count: u16)` | Copy forward. Overlapping blocks work only when `dst` is below `src` |
| `fill` | `(dst: ptr u8, value: u8, count: u16)` | Fill a block |

**`text`**: text in memory, platform independent

| Function | Signature | Purpose |
|---|---|---|
| `put` | `(dst: ptr u8, src: ptr u8, n: u8)` | Copy text to memory, e.g. a `screen"..."` string to screen memory |
| `dec8`, `dec16` | `(dst: ptr u8, value: u8 or u16) -> u8` | Write a number in decimal, without leading zeros; returns the number of digits. Uses repeated subtraction, no division |
| `hex8`, `hex16` | `(dst: ptr u8, value: u8 or u16)` | Write a number as 2 or 4 hex digits |
| `equal` | `(a: ptr u8, b: ptr u8, n: u8) -> bool` | Compare two texts |
| `find` | `(s: ptr u8, n: u8, ch: u8) -> u8` | Index of the first `ch`, or `$FF` if not found |

Digits are written as screen codes, which are the same as PETSCII for `0`-`9`. Hex letters are screen codes too.

**`c64`**: Commodore 64 only

| Name | Purpose |
|---|---|
| `SCREEN`, `COLOR` | Screen memory (`$0400`) and color memory (`$D800`) |
| `BLACK` ... `LIGHT_GREY` | The 16 color numbers |
| `chrout(ch: u8 using A)` | Print one PETSCII character through the KERNAL (`$FFD2`) |
| `print(src: ptr u8, n: u8)` | Print a PETSCII string through the KERNAL |
| `getin() -> u8 using A` | Read a key from the keyboard buffer; 0 if none (KERNAL `$FFE4`) |
| `cursor(row: u8 using X, col: u8 using Y)` | Move the text cursor (KERNAL `PLOT`, `$FFF0`, fixed carry clear) |
| `get_cursor() -> u16 using XY` | Read the cursor position: row in the low byte, column in the high byte (KERNAL `PLOT`, fixed carry set) |
| `vic: Vic @ $D000` | VIC-II registers: `sprite0_x`, `sprite0_y` ... `sprite7_y`, `sprite_x_msb`, `control1`, `raster`, `light_pen_x`, `light_pen_y`, `sprite_enable`, `control2`, `sprite_expand_y`, `memory`, `irq_status`, `irq_enable`, `sprite_priority`, `sprite_multicolor`, `sprite_expand_x`, `sprite_collision`, `sprite_bg_collision`, `border`, `background`, `background1`-`background3`, `sprite_mc0`, `sprite_mc1`, `sprite_color: [u8; 8]` |
| `sid: Sid @ $D400` | SID registers: `voice1`-`voice3: Voice` (`freq: u16`, `pulse: u16`, `control`, `attack_decay`, `sustain_release`), `cutoff: u16`, `resonance`, `volume`, `paddle_x`, `paddle_y`, `osc3`, `env3` |
| `cia1: Cia @ $DC00`, `cia2: Cia @ $DD00` | CIA registers: `port_a`, `port_b`, `ddr_a`, `ddr_b`, `timer_a: u16`, `timer_b: u16`, `tod_tenths`, `tod_seconds`, `tod_minutes`, `tod_hours`, `serial`, `icr`, `control_a`, `control_b` |

**Planned for M11** (C64; exact functions settled by the coding session):

- **`sprites`**, a sprite multiplexer: `module sprites(COUNT: u8 = 32, FLICKER: bool = false)`. A public table of `COUNT` virtual sprites (`x: u16`, `y`, `frame` (sprite pointer), `color`, flags for visible, multicolor and expand-X; no expand-Y), which main writes; `update()` once per frame sorts by Y (continuous insertion sort, kept between frames) into the next frame's table (double-buffered); `start()` turns the KERNAL off and installs its handler; `wait_frame()` waits for the handler's frame counter. Hardware sprite *i mod 8* shows sorted sprite *i*. More than 8 sprites on a line: the extra ones are skipped that frame, or, with `FLICKER`, which ones are skipped rotates each frame. Its raster code is an `interrupt fn raster() -> bool using carry` (true at the top of the frame), callable as a part from the program's own handler (6.3): `interrupt fn frame() { if sprites.raster() { scroll.top() } }`.
- **`scroll`**, smooth character scrolling in eight directions: fine scroll and 38-column/24-row helpers on `c64.vic`; two screen buffers in one VIC bank, the hidden one shifted by one character spread over the 8 fine steps, swapped with `$D018`; color RAM either fixed or shifted at the swap (in asm, racing the beam); new columns and rows filled from a character map (`ptr u8` plus width). Tile maps later. `interrupt fn top()` writes the scroll registers at the top of the frame, as a part of the program's handler; `scroll.start()` installs a minimal handler for programs without the multiplexer. The copy loops are unrolled asm.

**Planned for M10** (cross-platform; the coding session settles exact signatures following the rules above and records them in this section):

- **`wide`**: 24- and 32-bit unsigned numbers as structs, `wide.U32 { lo: u16, hi: u16 }` and `wide.U24 { lo: u16, hi: u8 }` (little-endian). The 16-bit halves are plain fields, so no cast or overlay is needed: `score.hi` is a `u16` (`lda score+2` / `ldx score+3`), and single bytes are `score.lo.hi` and so on. There are no 24- or 32-bit primitive types. Struct parameters are not allowed (3.3), so every function takes pointers, and the result goes into the first operand, as in assembly: `wide.add32(addr(score), addr(bonus))`. Functions for each width (`add24`, `add32`, ...): `set_u16`, `copy`, `add`, `sub`, `add_u16`, `sub_u16`, `inc`; `less`, `equal`, `is_zero` returning flags; `mul` (the low 24/32 bits, shift-and-add), `div` by shift-and-subtract (remainder in a library variable, as `math.div16`); `dec` (optionally fixed width with leading zeros) and `hex` into memory. Simple loops like `math`; nothing beyond these. Converting to decimal costs thousands of cycles: convert when the value changes, or keep a displayed-only score in `bcd`.
- **`bcd`**: packed decimal counters of 1 to 4 bytes (two digits per byte) for scores: add, subtract, compare, write as digits. Uses `sed`/`cld`; interrupt handlers clear decimal mode (6.3).
- **`math`**: signed `muls8`, `divs8`, `abs8`, `abs16`; `min`, `max`, `clamp` for `u8` and `i8` (`inline asm fn`, no `jsr`); `sin8`/`cos8` (256 steps per circle, results −127..127, one shared table).
- **`random`**: 8- and 16-bit xorshift generators, a seed function and a value below `n`. Its state is a private variable, so using it from main and a handler is an error (8.1); a handler that needs random numbers uses its own generator.
- **`text`**: signed decimal (`dec8s`, `dec16s`) and fixed-width decimal with leading zeros. **`mem`**: `copy_back` for overlapping blocks where `dst` is above `src`.

Example: `c64.vic.border = c64.BLACK` is `lda #0` / `sta $D020`.

KERNAL routines that fit 6.1 are declared as routines at a fixed address (6.6), so calls go straight to the ROM. The others are `asm fn` wrappers.

## 11. Code shapes

These are the intended translations. The optimizer may produce something shorter, never longer.

| Source | Generated code |
|---|---|
| `x = 0` | `lda #0` / `sta x` |
| `x += 1` (x in memory) | `inc x` |
| `w += 1` (u16) | `inc w` / `bne +` / `inc w+1` |
| `a[i] = v` | `ldx i` / `lda v` / `sta a,x` |
| `p[i] = v` | `ldy i` / `lda v` / `sta (p),y` |
| `w = pts[i]` (`linear [u16; n]`) | `lda i` / `asl` / `tax` / `lda pts,x` / `sta w` / `lda pts+1,x` / `sta w+1` |
| `for i in 9..=0 using X` | `ldx #9` … `dex` / `bpl loop` (start ≤ 127; otherwise one extra `cpx #$FF`) |
| `for i in n..0 using X` | `ldx n` / `beq done` / … `dex` / `bne loop` |
| `for i in 0..n using X` | `ldx #0` / `cpx n` / `beq done` / … `inx` / `cpx n` / `bne loop` |
| `for i in 0..=255 using X` | `ldx #0` … `inx` / `bne loop` |
| `for _ in 0..10` | `ldx #10` … `dex` / `bne loop` |
| `for m in s..=255 step n using Y` (n a `u8` variable) | `ldy s` … `tya` / `clc` / `adc n` / `tay` / `bcc loop` |
| `for i in 5..=5` | the body once, with `i` = 5 |
| `if a == 0 { }` | `lda a` / `bne skip` |
| `if a < b { }` unsigned | `lda a` / `cmp b` / `bcs skip` |
| `match` (compare chain) | the value is loaded into A once (`i8`: `eor #$80`, so signed order becomes unsigned order); per value `cmp #v` / `beq arm`; a range takes two compares, or one at either end of the byte; tests in source order, the last one inverted to branch to the default |
| `match` with a jump table | used when the chain would need at least 8 compares (a range counts 2) and the values span at most twice that: `sec` / `sbc #low` (if low > 0), `cmp #span` / `bcs default`, `tax` (`tay` if X holds a loop variable), `lda hi,x` / `pha` / `lda lo,x` / `pha` / `rts`; the tables hold each target minus 1 |
| `f(x)` with parameter `using A` | `lda x` / `jsr f` |
| `chrout(x)` with `fn chrout(...) @ $FFD2` | `lda x` / `jsr $FFD2` |
| `get_cursor()` with `fn get_cursor(true using carry) ... @ $FFF0` | `sec` / `jsr $FFF0` |
| `return true` with `-> bool using carry` | `sec` / `rts` |
| `-x` (u8) | `lda x` / `eor #$FF` / `clc` / `adc #1` |
| `x << 2` | `asl` / `asl` (on A or directly on memory) |
| `(w << 5) + (w << 3)` (w: u16, i.e. w × 40) | shifts and one 16-bit add, inline |
| `math.mul8(a, b)` | `ldx b` / `lda a` / `jsr math.mul8` (A is loaded last; result in A/X) |
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

A platform description gives: CPU variant, free zero-page ranges, RAM ranges for variables, volatile areas (4), code start address, output file format, startup code, and the default character encoding (2.1).

**C64 (first platform)**

- CPU: `nmos6502`. Output: `.prg` with load address `$0801`.
- Startup: a BASIC line `10 SYS 2061`, then the startup code (zero globals, copy initialized data, call `main`).
- Free zero page: `$02–$8F` (BASIC's work area; the program does not return to BASIC) and `$FB–$FE`.
- Variables: RAM after the program up to `$9FFF`. KERNAL and I/O stay mapped in v0.
- Volatile areas (4): memory the hardware or the KERNAL's interrupt changes. Fixed-address variables here are volatile with no keyword.

  | Range | What changes it |
  |---|---|
  | `$00–$01` | CPU port |
  | `$91` | STOP key flag |
  | `$A0–$A2` | jiffy clock |
  | `$C5–$C6` | last key pressed, keyboard buffer count |
  | `$CB–$CF` | current key, cursor blink |
  | `$F5–$F6` | keyboard decode table pointer |
  | `$0277–$0280` | keyboard buffer |
  | `$028D–$028E` | shift/Commodore/Ctrl key flags |
  | `$D000–$DFFF` | VIC, SID, color RAM, CIAs, cartridge I/O |
- Default encoding: `petscii` (2.1).

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
| `Tern.Compiler` | Front end, allocation, code generation, layout (optimizer planned) |
| `Tern.Cli` | Command-line tool: `tern file.tern` writes `.prg`, `.lst` and a VICE label file |
| `Tern.Cpu.Tests` | Encoder and emulator tests, including Klaus Dormann's functional test |

### 13.2 Testing approach

- Every language feature has test programs that run in the built-in emulator. Tests check results in memory and record bytes and cycles.
- Benchmarks compare the same task in Tern, hand-written assembly, and other compilers (cc65, Prog8). A size or speed regression fails the build.

## 14. Open questions

Each item is decided when the feature it belongs to is built.

1. **Bank switching** (NES mappers, C64 cartridges). Placement is `@ bank(n)`. Open: how calls between banks work. Likely direction: the compiler generates a small stub for each call into another bank, and direct access to another bank's data is an error.
2. **Functions called from both main and interrupts.** v0: a compile error, except for functions with no memory of their own (8.1). Likely direction: the compiler makes a second copy of the function for the interrupt side, which costs ROM but no speed and needs no stack.
3. **`pha`/`pla` for temporaries.** `pha`/`pla` is 2 bytes and 7 cycles, against 4 bytes and 6 cycles for a zero-page temporary. Likely direction: the optimizer chooses, but never across a function call. Settle with benchmarks.
4. **Overflow checks in debug builds.** Likely direction: no extra code in the program. The built-in emulator checks for overflow while running tests, using the compiler's debug information.
5. **User-defined encodings,** e.g. for the NES, where each game decides which tile shows which character: a character-to-byte table declared in source. Built-in encodings are settled (2.1).
6. **`using` on local variables** (`var count: u8 using Y`). Leave out until generated code shows a need. Deferred again 2026-11-02 (M8 question 40): `crc8` would keep `crc` in A, but A is needed by almost every expression, so the optimizer handles that case first (`x & $80 != 0` then `x << 1` as one `asl` and a carry test).
7. **A general decorator syntax.** Only if modifier keywords grow past five or six.
8. **Language name.** "Tern" is kept for now (2026-11-02). Rejected: "Moss"/"Mossy" (after MOS Technology), since "Moss" is already the name of several small languages and of Stanford's MOSS plagiarism checker.
9. **`data` at a fixed address** (`data x @ $C000 = ...`). Not supported yet, since the program image is one block. Needed for fixed-address tables and for `@ bank(n)` (item 1).
10. **Fixed-address RAM that is not volatile.** Settled 2026-11-02: platform volatile areas plus a `volatile` keyword (section 4).
11. **Arrays of structs as struct fields** (`parts: [Point; 4]`, or the VIC's interleaved sprite x/y). Not supported yet; a limited form is wanted eventually. Discuss after a first complete version. Notes so far: a constant index is always a fixed offset; stored as tables inside the record, a variable index costs nothing extra; a `linear` field (needed for hardware layouts) could allow a variable index only when the element size is 2, 4 or 8 (a shift, as for 16-bit pointers).
12. **Memory layout declaration.** A declaration in source that describes the machine's memory: areas for the program, reserved areas the compiler never fills (e.g. VIC graphics at `$2000–$3FFF`), volatile ranges, named regions for placement (`var map: [u8; 2048] @ buffers`, keeping `@` a location), and later banks. The platform gives the default map; a program declares what it changes. Likely direction for the syntax (accepted 2026-11-02):
    ```
    memory {
        $0801..=$1FFF  program
        $2000..=$3FFF  reserved              // VIC bank 0: sprites, character set
        $4000..=$9FFF  program
        $C000..=$CFFF  region buffers
        $D000..=$DFFF  volatile
    }
    ```
    Open: whether a function may cross a gap (or the compiler inserts a `jmp`), where the declaration lives (main file or a shared file), and whether it replaces or adjusts the platform's map. Decide together with items 1 and 9.
