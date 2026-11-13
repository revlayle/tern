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
7. **Multiple targets.** CPU variant (which instructions exist) and platform (memory map, file format) are separate settings. Commodore 64 is the first platform; Atari 8-bit, Apple II, NES and Commander X16 are planned, in that order, then the Commodore 128 in native mode.

**Non-goals**

- Recursion (see 6.4).
- C compatibility or a C-like standard library.

**Not in v0**

- Floating point.
- Heap allocation. All memory is static.

## 2. Source text

- UTF-8 files with the extension `.tern`.
- Comments: `// to end of line` and `/* block */`.
- Statements end at a newline or `;`. A `;` is optional at the end of a line, so `x = 1;` and `x = 1` are the same, and an empty statement (`;;`) is ignored. A newline inside `( )` or `[ ]` does not end a statement, so argument lists and tables can span lines; there, elements are separated by `,` only. A `{` or `}` ends that rule: inside braces a newline separates again, which is why struct fields, in a declaration (3.3) or a literal, may be separated by `,` or a newline.
- A line that ends with a binary operator (`&&`, `||`, `+`, `==`, ...) continues on the next line, since nothing can end there: a long condition is split after `&&`. A line cannot start with a binary operator; that is an error that says to move the operator to the end of the line before. Reason: `-` and `+` are also unary, so `-1 => ...` on the next line of a `match` is a new arm, not a continuation.
- Where a block must follow (after the header of `fn`, `asm fn`, `interrupt fn`, `if`, `else`, `while`, `for`, `match`, `struct`, `asm`, `memory`), newlines before the `{` are skipped, so a brace may go on its own line (C# style). An `else` may also start the line after the `}`.
- Indentation has no meaning.
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

A number literal is at most 2^63 - 1 (`$7FFF_FFFF_FFFF_FFFF`, 9223372036854775807), the top of the range of constant arithmetic (3.1). A literal has no sign; `-` is the unary operator, so the lowest value is written `-9223372036854775807 - 1`, and all 64 bits set is `~0`.

**Escapes** in char and string literals: `\0` (zero byte), `\n` (the encoding's line break; PETSCII `$0D`; an error in an encoding that has none, such as `screen` and `screen_mixed`), `\\`, `\"`, `\'`, and `\$NN` (a raw byte, not converted by the encoding). A character the encoding has no code for is a compile error that suggests `\$NN`.

A string literal is only an initial value, of `data` or of a global array. It is not an expression: `c64.print("HI", 2)` is an error that suggests declaring the text (`data HI = "HI"`) and passing `addr(HI), len(HI)`. An unnamed table would be memory the source does not show.

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

Each encoding is implemented together with its platform. The NES has no character set (text is tile numbers chosen by each game), so it has no default encoding: the program declares one (below).

**Encodings declared by the program** (decisions 2026-11-12, 2026-11-13). A game with its own character set, on the C64 or the NES, declares which byte each character is. The name is then a prefix like a built-in one:

```
encoding tiles {
    '0'..='9' = $00          // a range: consecutive bytes from the one given
    'A'..='Z' = $0A
    ' '       = $24
    '♥'       = $25
}

data SCORE = tiles"SCORE 00"
screen[i] = tiles'0' + digit
```

- **Based on another encoding:** `encoding hud: screen { '♥' = $53 }` starts from `screen` (built-in or declared) and changes or adds the characters listed.
- The rules of built-in encodings apply: a character with no code is an error that suggests `\$NN`; `\n` is an error unless the encoding maps `'\n'`; `\$NN` is a raw byte. A character listed twice, or a range whose bytes pass `$FF`, is an error. Two characters may have the same byte.
- A byte is any constant expression with a value 0-255 (`'A'..='Z' = LETTERS`). A range is written with `..=` and goes up: `'a'..'z'` and `'Z'..='A'` are errors. `'\n'`, `'\''` and `'\\'` may be listed; `'\0'` and `'\$NN'` may not, since they are bytes, not characters.
- A module's encoding (not `private`) is used with the module's name: `font.tiles"HI"`, so a font and its encoding can live in one module. A `private encoding` is used only inside its module.
- An encoding's name shares the module's namespace with its other declarations (`var tiles` and `encoding tiles` in one file are "already declared"; encodings are declared first, so the error is at the other declaration). It may not be the name of a built-in encoding, since the prefix would have two meanings.
- `default encoding` may name a built-in encoding (`default encoding screen`) or a module's (`font.tiles`).
- **The program's default:** `default encoding tiles` (or `font.tiles`) in the main file makes it the encoding of literals without a prefix, in the main file and the program's own modules, on any target; at most one such line. Library modules always use the platform's default (on a platform without one they write a prefix on every literal), so a program's default never changes a library's text. On a platform with no default encoding, a literal without a prefix and no `default encoding` line is an error that says to declare one.
- `encoding` is a word only at the start of a top-level declaration (after `private`), and `default` only before `encoding`; elsewhere both are ordinary names, as `step` and `emit` are (13).
- Everything happens at compile time; the encoding takes no memory.

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
- A module is loaded once, however many files import it. Modules may import each other, in a ring too, and their declarations may use each other's names in any order, constants included (`const COLOR: u8 = video.MC` in a module that `video` imports through another). Only a real cycle, a constant whose value depends on itself, is an error.
- Only the program's main file has `fn main()`. A main file with `export` and no `main` builds a binary that another program calls (12.3). The main file cannot be imported; that is an error that says to move what the other module needs into a module both import.
- Only functions reachable from `main` (or from the exports, 12.3) are compiled. A module's global variables and `data` are kept only if a compiled function names them (in Tern code or as an assembly operand). An imported module costs nothing for the parts that are not used. The main file's globals are always kept, since an unused one may reserve memory on purpose (e.g. `@ zp`).
- **Module parameters** are constants the program sets when it imports a module. The module declares them once, in a header that is the first line of the file (before its imports):
  ```
  // lib/c64/sprites.tern
  module sprites(COUNT: u8 = 32, FLICKER: bool = false)

  var list: [Sprite; COUNT]
  ```
  and the main file sets any of them by name: `import sprites(COUNT = 40)`. `import sprites` alone uses the defaults.
  - Only the main file may set parameters, since a module is loaded once. Other files import the module plainly and get the program's values.
  - Each parameter has a type (an integer type or `bool`) and a default. A value is a constant expression using literals and the main file's constants that do not depend on imported modules; it is checked against the type. An unknown name, a name given twice, or a value that does not fit is an error.
  - Inside the module a parameter is an ordinary constant; outside it is `sprites.COUNT`. Parameters cost nothing at runtime. An `if` on a constant condition compiles only the branch taken, and a function called only from the other branch is not compiled and has no memory, so an option that is off costs nothing.
  - The name in the header must match the file name. A main file has no header; a module needs one only if it has parameters.
- Platform modules live in `lib/<platform>/` and can only be imported when compiling for that platform; importing one for another platform is an error naming the platform. A platform folder can also provide its own version of a general module. **The build's target decides which library modules exist**: every build names its platform, so `import io` is the C64's `io` in a C64 build and the Apple II's in an Apple II build, and the import line needs no platform name.
- **A module of your own may not have a library module's name.** When `import name` finds both a library module (of the target's folder or `lib/`) and `name.tern` in the importing file's folder, it is an error that names both files and says to rename yours. So each import has one meaning, never a library module that silently hides your file. A main file may still have a library module's name, since it is never imported.
- **Planned (not scheduled): quoted paths and aliases for your own modules.** `import "levels/level1.tern"` loads a file by its path, relative to the importing file and written with `/` on every system (no absolute paths, so a project can move), which allows subfolders. `import my_io = "./io.tern"` gives the module another name for use in code (`my_io.read`), so it can sit next to a library module of the same name. Not `as`, which already means a conversion (rule 3).

## 3. Types

| Type | Size | Notes |
|---|---|---|
| `u8`, `i8` | 1 | The default numeric types |
| `u16`, `i16` | 2 | Little endian. `.lo` and `.hi` give the bytes as `u8` |
| `bool` | 1 | `0` or `1` in memory. Can live in a CPU flag (section 7) |
| `ptr T` | 2 | Pointer to `T` |
| `[T; N]` | varies | Array, `1 ≤ N ≤ 256`; longer for one-byte elements, used through a pointer (3.2) |
| `struct` | varies | See 3.3 |

**`bool`** values are `true` and `false`. `==` and `!=` compare two bools; `&&`, `||` and `!` combine them (3.6). A `bool` in memory (a variable, an array element, a struct field) is one byte, 0 or 1. The condition of `if` and `while` must be a `bool`: a number is an error that suggests a compare such as `x != 0`.

### 3.1 Arithmetic rules

- All integer arithmetic **wraps**. There is no integer promotion: `u8 + u8` is `u8`.
- Mixing `u8` with `u16` widens the `u8` to `u16`, and the result is `u16`.
- Narrowing must be explicit: `x = big as u8`. `as` on integers keeps the low bits.
- Mixing signed and unsigned operands is a compile error. Cast one side.
- **`*`, `/` and `%` are allowed only in constant expressions**, which are evaluated at compile time: `const SIZE = 40 * 25` is fine, `y * 40` with a variable `y` is a compile error. The 6502 has no multiply or divide instruction, so runtime multiplication and division use library functions (section 10.1), which makes their cost visible in the source.
- `<<` and `>>` require a constant shift count, less than the width of the type (0 to 7 for 8-bit, 0 to 15 for 16-bit; a larger count is an error), and compile to `asl`/`lsr`/`rol`/`ror` sequences; a 16-bit shift by 8 or more moves a byte first (section 11). Shifting by a variable amount uses the library functions `math.shl8`/`math.shr8` (section 10.1).
- `>>` on unsigned types is a logical shift (`lsr`). On signed types it keeps the sign (`cmp #$80` / `ror` per shift), which rounds down, not toward zero: `-1 >> 1` is `-1`, and `-3 >> 1` is `-2`.
- **A number literal takes its type from where it is used.** A literal that does not fit that type is a compile error: `300` or `-1` assigned to a `u8`. Where nothing gives a type (`var v = 390`, a `for` bound), it takes the smallest that holds it: `u8` for 0 to 255, `u16` up to 65535, `i8` or `i16` for a negative value.
- **A constant has a type only when its declaration writes one.** `const AREA = BOX * 30` is the plain number 390 even with a typed `BOX`, and is used like the literal `390`: `w + AREA` adds 390 to a `u16`, and `var v = AREA` is a `u16`. Write `const AREA: u16 = BOX * 30` to give it a type.
- **A constant expression is computed exactly.** An expression made only of literals and constants, typed or not, is evaluated at compile time as an `int` (4.1), with no wrapping, and takes its type from where it is used, like a literal: only the final value is checked against that type. With `const BOX: u8 = 13`, `const MAX_X: u16 = 320 - BOX` is 307, and `const SECS: u8 = 400 / 50` is 8 with no `as`; `const B: u8 = BOX + 250` is an error (263 does not fit), where `(BOX + 250) as u8` keeps the low byte on purpose. That includes a typed constant on its own: `var s: i8 = BOX`, and `BOX` as the argument of an `i16` parameter, are allowed, since 13 fits. `~` and a negative value take the width of that type: `x & ~$0F` on a `u8` is `and #$F0`. Next to a runtime value, a typed constant takes part in choosing the operation's type as a variable of its type would: with `x: u8`, `x + BOX` is `u8`; with `w: u16`, `w + BOX` is `u16`, since `u8` widens. The constant part is then computed exactly at the operation's type and checked against it: `x + BOX * 2` adds 26, `w + BOX * 30` adds 390, and `x + BOX * 30` is an error. A constant's type never narrows the constant part below the operation's width, so `~` takes that width too: with `const M: u8 = $0F`, `w & ~M` is `w & $FFF0`, the same as `w & ~$0F`. A compound assignment is checked as the operation it stands for: `w &= ~M` is `w = w & ~M`.
- **Constant arithmetic uses signed 64-bit integers**; a value outside that range, at any step, is an error. `/` rounds toward zero and `%` has the sign of the left operand: `-7 / 2` is -3 and `-7 % 2` is -1, as `math.divs8` does at runtime. `>>` rounds down, as at runtime: `-7 >> 1` is -4. A shift count in a constant expression is 0 to 63.

### 3.2 Arrays

- Array length is at most 256, so any index fits in the X or Y register.
- **Long byte arrays.** An array of one-byte elements (`u8`, `i8`) may be longer than 256: a `var` buffer (`var map: [u8; 2048]`, zeroed at startup or `noinit`, with no initial value), or `data` from any initializer: a string literal, `include_bytes` (e.g. a 2 KB character set) or a list literal (e.g. a table printed by a tool). It cannot be indexed; indexing it is an error that says to use a pointer. `addr(name)` gives a `ptr u8` (or `ptr i8`) to its first byte and `len(name)` its length, a constant with no type (3.1), so it fits a `u16` and not a `u8`. To reach byte 300, step the pointer: `var p: ptr u8 = addr(map) + 256`, then `p[44]`. A long array can be placed like any variable or data (`@ addr`, `@ region`). Arrays with multi-byte elements stay at most 256 elements.
- Array indexes are always `u8` and produce `abs,X`/`abs,Y` addressing. For memory larger than 256 bytes, use a `ptr`.
- **An index expression wraps like all arithmetic** (3.1): with `i = 255`, `a[i + 1]` is `a[0]`, so a 256-element ring buffer can use `buf[head + 1]`. The optimizer folds a constant offset into the address (`a[i - 1]` as `lda a-1,x`) only where the result is the same: when a wrapped index would be outside the array anyway, or when the bounds of a `for` loop counter prove the sum does not wrap: constant bounds, or a variable end of the counter's type (`for i in 0..last` with `last: u8` gives `i <= 254`, so `a[i + 1]` is `lda a+1,x`). The proof comes from the loop header only, not from other accesses in the body, so the cost of an access can be read from the loop it is in. Otherwise `a[i + c]` computes the index first (`lda i` / `clc` / `adc #c` / `tax`).
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

A struct describes a **memory layout**: field names, types and offsets. It works as an overlay on memory and is always used in place. **Structs are never copied:** there is no struct assignment, comparison, struct parameter or struct return value. To pass a struct to a function, pass a `ptr` or an array index. To copy one, call `mem.copy(addr(dst), addr(src), sizeof(T))` (10.1).

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
- **Array fields whose elements are structs** (`parts: [Point; 4]`; planned, decision 2026-11-12; until built, "not supported yet"):
  - Stored as one table per byte of the element inside the record, as arrays of structs are (below): `parts: [Point; 4]` is four `x` bytes, then four `y` bytes. On a struct variable `boss.parts[i].x` is `lda boss+off,x`; through a pointer the index and offset are added, as for array fields above; inside an array of structs the index must be a constant, as for array fields there.
  - `linear parts: [Point; 4]` stores whole elements one after another, for hardware layouts such as interleaved x/y registers. A constant index is a fixed offset; a variable index needs an element size of 1, 2, 4 or 8 (a shift), otherwise it is an error that suggests padding the struct with `_` fields, as for `linear` arrays.
  - `addr()` of such a field works where it does for other array fields.
- **Struct literals in initial values.** A global struct variable, a global array of structs and `data` may have an initial value written as struct literals: the struct's name, then `field = value` for each field to set, in any order. Fields not listed are 0.
  ```
  struct Point { x: u8, y: u8 }
  struct Line { row: u8, col: u8, text: ptr u8 }

  data lines: [Line; _] = [
      Line { row = 1, col = 14, text = addr(TITLE) },
      Line { row = 3, col = 2, text = addr(HOW) },
  ]
  data start = Point { x = 160, y = 140 }
  var player: Enemy = Enemy { hp = 3 }
  ```
  - Fields are separated by `,` or a newline, with an optional trailing `,`, as in a struct declaration (spec 2: a newline separates inside braces). The literals of a list are separated by `,` only, as any list elements: inside `[ ]` a newline means nothing, so `[10` / `-5]` on two lines is `10 - 5`.
  - The name is always written, also where the destination gives the type, so a long table can be read line by line and a literal in the wrong table is an error. `field = value`, not `field: value`, since `:` means "has type" (as `import m(P = value)`, 2.2).
  - A value is what an initial value of the field's type allows (4): a constant, checked against the field's type; an address filled in when the program is placed (`addr(boss)`, `addr(map) + 40`, `.lo` / `.hi`); a list or string of exactly the field's length, or an array returned by a `const fn` (4.1) of that length, for an array field (`float.F32 { bytes = float.of(9.81) }`, 10.1); a struct literal of the nested struct's type for a nested struct.
  - An unknown field, a field given twice, and a `_` padding field are errors.
  - An array of structs gets the layout its declaration gives (one table per byte, `linear` or `indexed`), filled in at compile time. No code is generated beyond what any initial value costs: none for `data`, the startup copy for a `var`.
  - **Only initial values.** A struct literal is never a runtime value: in code, or as a local's initial value, it is an error that suggests assigning the fields one by one, since structs are never copied (above).
  - Global struct variables without an initial value are zeroed at startup. Local struct variables are allowed without one, like local arrays; their memory is shared (8.1), so it holds leftovers.
- `addr(player)` gives a `ptr Enemy`. `addr(player.name)` gives the address of a field of a struct variable, or of a struct at a fixed address (3.4).

**Indexing small linear struct arrays.** `list[i].field` with a variable index works on an array of structs stored as whole records whose whole size is at most 256 bytes, so every byte is reachable from one base address with an 8-bit offset in a register. The developer chooses how the record offset is found:

- **`linear var list: [Enemy; 20]`: shifts.** The struct size must be 1, 2, 4 or 8: `lda i` / `asl` / `asl` / `tax` / `lda list+off,x` for size 4; size 1 is `ldx i` / `lda list+off,x`. No extra memory.
- **`indexed var list: [Enemy; 20]`: an offset table**, for any struct size. The compiler adds a read-only table `list.offsets` of `i * sizeof(T)`, one byte per element (`n` bytes, built at compile time, included only when the array is indexed with a variable): `ldx i` / `ldy list.offsets,x` / `lda list+off,y`. That is 7 cycles to the offset, faster than shifting for sizes 4 and 8, at the cost of the table. The table's name cannot be written in source. The listing shows the table and the report gives its size.
- `indexed` stores records exactly as `linear` does (pointers, `addr()`, `sizeof` work the same), and replaces it: `indexed linear` is an error. It is a word only before `var` (it may follow `noinit`, `volatile`, `private`) and before `match` (5.2), not a reserved word; it works for globals and locals. It applies only to arrays of structs, whose whole size is at most 256 bytes; anything else (a non-struct array, a larger one, `data`) is an error at the declaration.
- **Errors at the index, not the declaration.** A `linear` array of any struct size stays valid for pointer use, and a constant index is always a fixed address with no code, whatever the struct and array size. A variable index into a `linear` array whose struct size is not 1, 2, 4 or 8 is an error that names the size and suggests padding the struct with `_` fields to the next of those sizes, or declaring the array `indexed` (giving the table's size). A variable index into a `linear` struct array larger than 256 bytes is an error that suggests a pointer. `addr()` of a field with a variable index, and a variable index into an array field of such a record, are errors.

**Padding fields.** A field named `_` takes space but has no name: `struct Enemy { x: u8, y: u8, hp: u8, vel: i16, _: [u8; 3] }` is 8 bytes. A struct may have several. They cannot be read or written, and they follow the usual offset rules (a `_` field may have an `@` offset). Uses: making a struct a power-of-two size for indexing, and matching a hardware or file layout. In an array of structs stored as tables, a padding field costs its tables like any field.

**Arrays of structs are stored as one array per field** ("structure of arrays"). `enemies[i].x` is a single `lda enemies_x,x`, and multi-byte fields are split as in 3.2. Nested structs flatten all the way down: `enemies[i].pos.x` is one table, `enemies_pos_x`. Precisely, there is one table per *byte* of the record: byte k of record i is at `base + k * n + i`, where `n` is the array length. A gap left by `@` offsets costs one table of `n` bytes per gap byte. An array field inside an array of structs, as in `enemies[i].name[j]`, can only be indexed with a constant `j`, because a variable `j` would need a multiply. Use a `linear` array and a pointer instead. A `linear` array of structs (`linear var list: [Enemy; 8]`; `linear` goes before `var`) stores whole records one after another, so a `ptr Enemy` can step through it; `addr(list)` points to the first record. A variable index into a linear struct array works as described above (shifts or `indexed`); otherwise use a pointer. `addr()` of an array of structs stored as tables is an error.

### 3.4 Pointers

- `ptr T` is a 16-bit address. `addr(name)` gives a pointer to a variable or array: `ptr T` for a variable of type `T`, a pointer to the first element for an array of one-byte elements, `ptr u8` to the low-byte table for a split array of 16-bit values, and `ptr u16` (or the element type) for a `linear` array of 16-bit values (3.2). A field of a struct variable or of a struct at a fixed address works too: `addr(player.name)` (no code beyond loading the constant address). Fields through a pointer and in an array of structs are errors. A pointer converts to `u16` by itself where a `u16` is expected (3.5): `var a: u16 = addr(map)`. `addr(f)` of a function is a `u16` constant: its address, e.g. for a vector or a jump table (6.5).
- A number literal, or any constant expression (3.1), converts to a pointer without `as`, since it takes its type from context: `var p: ptr u8 = $0400`, `p == 0`, `mem.copy(CHARSET + FIRST * 8, ...)`. A `u16` variable or other runtime value needs `as`.
- `p[i]` with `i: u8` compiles to `(zp),Y` addressing. There is no separate dereference operator: use `p[0]`.
- On a pointer to a 16-bit type, the index counts elements: `p[i]` reads the bytes at offsets `2*i` and `2*i+1`. The byte offset must fit in the 256 bytes `(zp),Y` reaches: a constant index above 127 is an error, and a variable index of 128 or more wraps within those 256 bytes.
- `.lo` and `.hi` cannot be used on pointers, since `p.name` means a field of the struct `p` points to (3.3). Write `(p as u16).hi`. The exception is an address the compiler fills in: `addr(...)` alone or plus or minus a constant, the forms an initial value allows (4). `addr(map).lo`, `addr(map).hi` and `(addr(map) + 299).hi` are bytes of the address, in code and in initial values, like the assembler's `#<map` / `#>(map+299)`, with no code beyond the load. On these forms they always mean the address bytes, even when the struct has a field named `lo` or `hi`.
- For a pointer to a struct, `p.field` accesses a field (3.3). `p[i]` is not allowed on struct pointers, since it would need a multiply by the struct size.
- A pointer that is dereferenced must be in zero page. The compiler puts it there automatically (section 8.2) and reports an error if zero page runs out.
- Pointer arithmetic is in bytes: `p + 1` is the next byte. It is written with the pointer first: `n + p` with a runtime `n` is an error (write `p + n`); a constant may come first (`2 + addr(map)`), since it takes its type from where it is used. Use `p + sizeof(T)` to step by one element. Offsets are unsigned; use `p - n` to move back.
- `p - q` of two pointers of the same type gives the distance in bytes as a `u16`. For different types, convert one with `as` (no code).
- **Any pointer converts to any other pointer type with `as`**, with no restrictions and no generated code: `p as ptr Enemy` reads the same memory as an `Enemy`. Pointers also convert to and from `u16` with `as`; to `u16` it is automatic where a `u16` is expected (3.5). The element type only tells the compiler how to read the memory; it never limits what the memory can be used as.

### 3.5 Conversions

**Automatic:** widening to a larger type of the same signedness: `u8` to `u16`, `i8` to `i16`.

**Automatic where a `u16` is expected:** a pointer to `u16`, the address as a number, with no code. "Expected" means the destination gives the type: an assignment, an initial value or table element, an argument, a return value: `var a: u16 = p`, `data t: [u16; 2] = [addr(a), addr(b)]`. Inside an operator a pointer still needs `as`: `w + p` and `p == w` are errors (write `p as u16`), since `p + w` is pointer arithmetic (3.4) and the order of the operands must not change the result's type. The other way, `u16` to a pointer, always needs `as` (a literal or constant converts by itself, 3.4).

**Explicit, with `as`:**

| Conversion | Meaning | Generated code |
|---|---|---|
| `u16 as u8` | keep the low byte | none |
| `u8 as i8`, `i8 as u8` | same bits, read as the other type | none |
| `i8 as i16` (or automatic) | sign extend | a few instructions: high byte becomes `$FF` or `$00` |
| `u8 as u16` (or automatic) | zero extend | high byte = 0 |
| `bool as u8` | `0` or `1` | e.g. from the carry flag: `lda #0` / `rol` |
| `ptr T as u16` (or automatic, above), `u16 as ptr T` | address as a number and back | none |
| `ptr T as ptr U` | same memory, read as another type (3.4) | none |

- `as` binds tighter than every operator: `x as u16 << 5` is `(x as u16) << 5`.
- Converting between sizes and signedness extends according to the source type, then reinterprets: `i8 as u16` sign extends.
- `as` never checks a value; it keeps the low bits, on a constant as at runtime: `(BOX + 250) as u8` keeps the low byte on purpose (3.1). Without `as`, a constant must fit where it is used (3.1), and so must a value computed by a `const fn` (4.1).
- **Not allowed:** narrowing without `as`; mixing signed and unsigned in one operation; `u8 as bool` (write `x != 0`).

### 3.6 Operator precedence

Tightest first. Binary operators on one line are left associative.

| Level | Operators |
|---|---|
| 1 | postfix: index `a[i]`, member `s.x`, conversion `x as T` |
| 2 | unary `-` `+` `~` `!` |
| 3 | `*` `/` `%` (constant expressions only, 3.1) |
| 4 | `+` `-` |
| 5 | `<<` `>>` |
| 6 | `&` |
| 7 | `^` |
| 8 | `\|` |
| 9 | `==` `!=` `<` `<=` `>` `>=` |
| 10 | `&&` |
| 11 | `\|\|` |

- Unlike C, `&`, `^` and `|` bind tighter than the compares: `flags & 4 != 0` is `(flags & 4) != 0`.
- As in C, `<<` and `>>` bind looser than `+`: `w << 5 + 3` is `w << 8`; write `(w << 5) + 3`.
- `as` is postfix, so it binds tighter than a unary operator: `-x as i16` is `-(x as i16)`. For an `i8` holding -128 that is 128, while `(-x) as i16` is -128.
- Compares do not chain: `a < b < c` is a compile error. Write `a < b && b < c`.
- Unary `-x` negates (two's complement, wrapping like all arithmetic). Unary `+x` has no effect.
- `&&`, `||` and `!` are logical operators on `bool`, with short-circuit evaluation. `&`, `|`, `^`, `~` are bitwise operators on integers. Each is a separate operator with one meaning (`!` and `!=` are different tokens).
- Assignment operators: `=`, `+=`, `-=`, `&=`, `|=`, `^=`, `<<=`, `>>=`. `x += 1` on memory compiles to `inc`. Assignment is a statement, not an expression.

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
- **`[T; _]` takes the length from the list**: `data script: [u8; _] = [1, 2, 3]` has length 3, as has a `var` global with a list initial value. A string initial value works the same way: `var text: [u8; _] = screen"GALAXY 00\0"` has length 10. A written length is still checked against the list or string. `_` is allowed only there, with a list or string literal, a list built with `for`, `repeat(value, count)`, or an array returned by a `const fn` (4.1): `data sine: [u8; _] = tables.sine(256)`.
- **`@` gives a location**, and that is its only meaning. In a struct field it is the offset from the start of the struct (3.3). Elsewhere it is followed by one of:
  - a constant expression: a fixed address, e.g. `@ $D020` or `@ VIC + $20`,
  - `zp`: anywhere in zero page, chosen by the compiler. This is a guarantee: if zero page is full, compilation fails (8.2).

  Code and `data` go into an overlay or a ROM bank by module, with `@` on the module's import (8.5, 14.1), not on single declarations.
- **Volatile memory.** Every read and write of volatile memory in the source happens exactly once and in order; the optimizer never removes, merges or reorders them. Memory is volatile when:
  - it lies in one of the platform's volatile areas (12.2), such as I/O registers. This needs no keyword: `var border: u8 @ $D020` is volatile,
  - the variable is marked `volatile`, a prefix like `noinit`: `volatile var flags: u8 @ $C000`. Use it for RAM that hardware or outside code changes behind the program's back. On a struct or array it covers every field and element. It is allowed only on global `var`s; writing it on a variable that is already volatile is allowed,
  - the variable is used by functions reachable from main and from an interrupt handler, or from two handlers (8.1). The compiler finds this from the call graph,
  - it is read or written through a pointer, since the compiler cannot know where a pointer points.

  All other memory is ordinary: its values may be kept in registers, and redundant reads and writes removed. That includes a fixed address outside the volatile areas: `var reg: u8 @ $C000` is ordinary memory, and the optimizer may remove a read of it whose value is not used, or a repeated read, though it need not. Hardware that the platform's table does not list, such as a cartridge register, is marked `volatile`. An asm block, and a call to a routine at a fixed address, make the optimizer forget the values it holds for ordinary memory. There is no way to mark memory inside a volatile area as ordinary; a memory layout declaration may add one later (14.12).
- **A fixed-address variable must not overlap the program**, its data, or the variables the compiler places. That is a compile error that names the variable and both address ranges. Fixed-address variables are often graphics data that the VIC must find in a given 16 KB bank; move the data or make the program smaller. Fixed-address variables may overlap each other. The one exception is the memory an overlay loads (8.5): a fixed-address variable marked `volatile` may overlap an overlay's file.
- **Addresses in initial values.** The initial value of a global or of `data` is known at compile time, with one exception: the address of anything the compiler places, filled in when the program is placed, at no runtime cost. `addr()` of a global variable, `data`, a field of a struct global or a function (6.5) may be an element of an initial value, alone, plus or minus a constant (`addr(screen) + 40`, `addr(handler) - 1` for an `rts` jump table), as `.lo` / `.hi` of either (one-byte tables of address halves), or converted with `as` to another pointer type (`addr(buf) as ptr Enemy`) or to `u16`: `data rows: [ptr u8; 3] = [addr(map), addr(map) + 40, addr(map) + 80]`. In a `[u16; n]` table the address converts by itself (3.5): `data t: [u16; 2] = [addr(a), addr(b)]`. Anything else built from an address, such as two addresses added or an address multiplied, is an error. Addresses in a sibling overlay are errors as everywhere (8.5).
- Global variables without an initializer are zeroed at startup. Storing 0 into such a variable at the start of `main`, before any label, branch, call or asm block and before `main` has written it, therefore generates no code. A global variable can be marked `noinit` to skip that. It is a prefix like `linear`: `noinit var buf: [u8; 256]`. It is an error with an initial value, at a fixed address (never zeroed anyway), on `data` and on locals.
- Local variables (other than arrays and structs) must have an initial value. Their memory is shared with other functions (8.1), so it holds no useful value on entry.
- A `const` may also be declared inside a function body. It follows the rules of a top-level `const` and takes no memory.
- **Scope.** A local variable or constant can be used from its declaration to the end of the block that contains it. Blocks that do not contain each other may use the same name. A name is never declared again where it is already visible: a local cannot take the name of a parameter, of a local in an enclosing block, or of a top-level declaration of its module (names from other modules are qualified, 2.2, so they never clash). Top-level declarations may be used before the line that declares them, in any order, including a constant in a struct field's `@` offset. That includes a constant computed from them, such as `const KINDS: u8 = len(star_char)` with `star_char` declared as `data` anywhere in the program (2.2).

### 4.1 Compile-time evaluation

**v0:** constant expressions, which may use every operator including `*`, `/` and `%`, plus `include_bytes`, which loads a file into a `data` table: `data ship = include_bytes("ship.bin")`. Tables can be built with external tools and loaded this way.

**Planned: `const fn`.** A function the compiler runs during compilation. Only its result goes into the program; the function itself never becomes 6502 code. It is mainly for building lookup tables.

```
const fn row_addr(row: int) -> int {
    return $0400 + row * 40
}
data rows: [u16; 25] = [row_addr(i) for i in 0..25]
```

- Allowed: all statements, integer math including `*`, `/`, `%`, local arrays, and calls to other `const fn`. A `const fn` may call itself (directly or through others), at most 200 calls deep; deeper is an error: "Const fn calls are nested more than 200 deep here, through 'f'. Use a loop instead of calling 'f' from itself". Recursion is left out of runtime code only because the 6502 has no room for stack frames (6.4); a `const fn` never runs there. Its variables follow the scope rule of 4 (no name of a parameter, an enclosing block's variable or a top-level declaration of its module), and its `for` loops follow 5.1 exactly, with the same direction rule.
- Not allowed: program variables, `@` addresses, running assembly, and calls to normal functions. A `const fn` may *emit* assembly (below).
- Callable only at compile time: from `const` declarations, `data` initializers, `var` globals' initial values and other `const fn`. A `const` inside a function body is a `const` declaration (4). Calling one from runtime code is an error, and an asm operand outside `emit` is runtime code here: `lda #round(1.5)` is an error; declare a constant and use its name. Constant built-ins such as `len`, `sizeof` and `offset` work in operands as before (9).
- **Compile-time types.** Parameters, locals and the return use only `int`, `real`, `bool`, arrays of these, and `label` (for generators, below). Neither `int` nor `real` exists at runtime, and a runtime type (`u8`, `i16`, a pointer, a struct) in a `const fn` is an error: "'u8' is a runtime type. Use int; the value is checked when it leaves the const fn". Arguments widen into these types (a `const COUNT: u8` passes into an `int` parameter). When a value leaves compile time into a `u8`, `u16` or `data` table, the compiler checks that it fits and reports an error if not, as for constant expressions (3.1). Strings and structs are not compile-time values.
  - `int` is a signed 64-bit whole number; leaving that range is an error, as in constant expressions (3.1). It never wraps: write `& $FF` for 8-bit behavior. `/` rounds toward zero, as in 3.1.
  - `real` is a 64-bit IEEE double. A decimal number with a `.` between digits or an exponent is a `real` literal (`1.5`, `0.25`, `2e3`; `0..25` is still a range), allowed only in compile-time code; in runtime code it is an error that says to compute the value with a `const fn`. Functions: `sin`, `cos`, `tan`, `atan2`, `sqrt`, `pow`, `exp`, `log`, `log2`, `abs`, `min`, `max` (angles in radians), and the constants `PI` and `TAU`. Division by zero, and `sqrt` or `log` of a negative number, are errors, so no NaN or infinity reaches a table.
  - **A `const` may hold a `real`**: `const SCALE = 1.5`, `const K = PI / 128`. It has no type written and is used only in compile-time code (a `const fn`, a list built with `for`, another constant, an initial value computed at compile time); in runtime code it is the error a `real` literal gets. A `const` holds an `int`, a `real` or a `bool`, never an array: an array the program reads is `data` ("'T' is an array, which a const cannot hold. Use data").
  - An `int` becomes a `real` where one is expected (`i * 1.5`); `int / int` stays whole-number division. A `real` never becomes an `int` by itself: `round(x)` (halves away from zero), `floor(x)`, `ceil(x)` and `trunc(x)` give an `int`, and a `real` where an `int` or a runtime type is expected is an error naming them. `as` does not round a `real`.
- **Arrays** have a length fixed when they are made: `repeat(value, count)` (`repeat(0, n)`, `n` may be a parameter) or a list built with `for`. Inside a `const fn` an array may have 0 elements; a table made from one needs at least 1 (3.2). `len(t)` is the length. A `const fn` may return one, with the type written `[int; _]`. A `data` table or a `var` global takes it with a written length, which is checked, or with `_`, which takes the array's length (4): `data sine: [u8; _] = tables.sine(256)`. Each element is checked against the table's type.
- **`repeat(value, count)`** is an array of `count` copies of `value`, wherever compile-time code runs: `data spaces: [u8; 40] = repeat($20, 40)`, `var row = repeat(0, n)` in a `const fn`. It is a compile-time built-in like the list below; in runtime code it is an error, since it would be a hidden loop: "'repeat' builds an array at compile time. Use a 'for' loop to fill the array at runtime". `[value; count]`, Rust's spelling, is an error: "Write repeat(7, 4)"; in Tern, `[T; n]` is only an array type.
- **Array-building syntax: `[expr for i in range]`**, in `const` and `data` declarations, `var` globals' initial values and `const fn`. The range has the forms and meanings of a `for` loop (5.1): `0..n`, `0..=n`, `9..=0 step -1`, `step k`, with compile-time bounds. The expression may use `i` and anything a `const fn` may: `real` values, the math functions and calls to `const fn` (`data sine: [u8; _] = [round(128 + 127 * sin(i * TAU / 256)) for i in 0..256]`). One `for`, no filter, no nesting; anything more is a `const fn` with ordinary loops. In runtime code it is an error, since it would be a hidden loop: "A list built with 'for' is computed at compile time. Use a 'for' loop to fill the array at runtime".
- **What a `const fn` reads:** constants (typed ones as their value), module parameters (2.2), `sizeof` and `offset(T.field)` (10), and `data` tables element by element, including `include_bytes` tables. Not a `var`, even one with an initial value ("'score' is a variable, which a const fn cannot read. Use a const or data"), not a `volatile` or fixed-address table over an overlay, and not `addr()`, which is decided after compile-time evaluation. (An `emit` operand may name addresses, since it is assembled after placement like any asm block.) Evaluation follows dependencies, as constants do; a cycle is an error.
- **A step limit** of 10,000,000 steps (a statement run or a loop pass) for each top-level evaluation: one `const`, `data` or initial value, or one generator call in assembly. The error names the `const fn`, its line, and the declaration or asm line that started it: "'sine' did not finish after 10,000,000 steps (called from main.tern:5). Check its loops for one that never ends". There is no setting.
- A `const fn` can live in an imported module (2.2), so table generators can be shared between programs as Tern scripts: `import tables` … `data sine: [u8; _] = tables.sine(256)`. Reading files from compile-time code (e.g. converting an image into sprite data) is a possible later extension; `include_bytes` tables can already be read.
- No built-in for the bits of a `real`: `floor`, `log2` and `pow` split it into sign, exponent and mantissa, enough for the float library (M24) to build its constants.
- **A module's own function hides a compile-time built-in of the same name** in that module's `const fn`s: in `float`, which declares `floor`, `trunc` and `abs`, `floor(x)` inside `float.of` names `float.floor`, and is the error "'floor' runs on the 6502, so compile-time code cannot call it". A name has one meaning inside a module (rule 3); such a module uses another built-in (`round`) or names its function differently. Other modules are not affected.
- **`assert` inside a `const fn`** is a statement with the form of the top-level `assert` (below): `assert e <= 127, "float.of: the value is larger than the largest float, about 1.7e38"`. When the condition is false, compilation fails with the message, at the `assert` line with "(called from main.tern:3)", like any error raised while a `const fn` runs. The condition is a compile-time `bool` and may use the function's parameters and locals; the message is a string literal. `assert` is a statement only in a `const fn`; in runtime code it is an error ("An assert is checked at compile time. Write it at the top level or in a const fn").

**Planned: code generators (`emit`).** A `const fn` may build assembly with all of its control flow (`while`, `if`, nested loops, its own variables). Each `emit { ... }` adds instructions to the asm block or `asm fn` that called the generator. This is for generated "speedcode": unrolled copies, plotters, sprite clearing, any repeated code whose shape depends on parameters.

```
const fn copy_rows(src: int, dst: int, rows: int, skip: int) {
    var r = 0
    while r < rows {
        if r != skip {
            emit {
                lda src + r * 40, x
                sta dst + r * 40, x
            }
        }
        r += 1
    }
}

asm fn shift_left() {
    ldx #0
.loop:
    copy_rows(SCREEN + 1, BACK, 25, 12)     // 24 row copies placed here
    inx
    cpx #39
    bne .loop
}
```

- **Instructions, not text.** An `emit` fragment is parsed like an asm block when the `const fn` is read, so a typo is reported at the `emit` line even if that line never runs. Its operands are compile-time expressions, which may use the generator's variables and parameters and call other `const fn`. No string pasting.
- **Called only inside assembly**: an asm block or `asm fn` body names the generator on a line of its own. A generator call in Tern code is an error, so a Tern statement never expands into generated code. A generator is any `const fn` without a return value named on an asm line, also one with no `emit` of its own that only calls other generators. A `const fn` that returns a value cannot emit, also through the `const fn`s it calls: "'h' returns a value, so the const fns it calls cannot emit assembly", an error when the `emit` runs. (A generator that also returns a value, such as the cycles of the code it emitted, is left out until a cycle-exact generator needs it; roadmap.)
- **Arguments** are compile-time values, as for any `const fn`. A program variable is not one ("'x' is a variable, whose value is not known at compile time"); a generator names the variable in its `emit` text, or takes its address as a `label` (below): `copy_rows(addr(back) + 1, addr(screen), 25, 12)`.
- The finished block is ordinary assembly: the compiler computes its register, flag and variable effects (9), and it is never changed afterwards.
- **Labels.** The local `.labels` of one `emit` are its own, so a loop emitting `.skip:` gets a fresh label each time; the generator's code cannot name them outside the fragment. Every other label is a `label` value: an address decided when the program is placed (a branch to it stores the distance, which the assembler works out). A `label` comes from one of three places:
  - `label()`, in a `const fn`: a new label. `var top = label()` makes one, `emit { top: }` places it, `emit { bne top }` refers to it, in any of the generator's fragments, and it can be passed to another generator. Placing it twice, or never when it is used, is an error.
  - `addr(name)` of a global, `data` or function, as an argument or in the `const fn`: already placed by the compiler.
  - a `.label` of the calling asm block, written as an argument on the call line: `clear_rows(0, 3, .done)`. It must be a label of that block ("'.done' is not a label of this block"), which places it. The generator sees only the labels named in the call.

  In an `emit` operand a `label` may be used alone, plus or minus an `int`, or as `.lo` / `.hi`, as addresses in initial values (4); its number cannot be read, compared or multiplied in the `const fn`, since it is not known yet. `name:` in an `emit` places only a `label()` value: a name that is not a `label` variable is an error, since an `emit` creates no named symbol ("'finish' is not a label variable. Make one with 'var finish = label()'"), as is placing a label from `addr()` ("'dst' is the address of 'buf', which is already placed. Use label() for a new label") or from the calling block ("'done' is placed by the calling block"). `label()` outside a `const fn` is an error: "A label() value is only for emit in a generator".
- **Listing and errors.** Each generated instruction shows the `emit` line it came from and the call that ran the generator: the listing shows the call line once, then each generated instruction under its `emit` line with "(generated by line N)". An error in the generated code (a branch out of range) is reported at the `emit` line with "(generated by the call at main.tern:12)", as module asserts name the import line (4.1). Every error raised while a `const fn` runs is reported at its line in the `const fn`, with "(called from main.tern:5)" after the first sentence. `tern --json` gives the call's location as a field of its own, `calledFrom`, besides the message; like `suggestion`, the field is in every diagnostic and is `null` when the error is not in a `const fn` or generated code.
- A generator may call other generators, and may live in an imported module (2.2), so generators can be shared between programs.
- Tern statements cannot be generated: there are no macros and no textual preprocessor. Generators, module parameters (2.2) and `assert` are the compile-time tools.

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
for i in 9..=0 step -1 { ... }     // 9 down to 0
for i in n..0 step -1 { ... }      // n down to 1
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
- **Evaluation order.** Function calls and reads of `volatile` memory in one statement happen in source order, left to right: in `(rand() + 1) << 2 | rand()` the left `rand()` runs first, in `f(g(), h())` `g` runs before `h`, and in `a[f()] = g()` the index's call runs first. In an assignment to an indexed place (`a[i] = v`, `a[i] op= v`, also with a record or pointer index), the index is evaluated before the value. The order of the other parts is unspecified, since it cannot be seen, with one exception: a variable that a call in the same statement changes. In `x = seed + next()` where `next` changes `seed`, whether the old or new `seed` is added is unspecified; write the call or the variable into a local first.

### 5.1 `for` loops

`for` is a counted loop. Unlike a `while` loop with a counter, it guarantees how the loop variable changes, which lets the compiler choose the best loop shape (section 11).

- `a..b` excludes `b`, `a..=b` includes it.
- **The bounds and step are evaluated once**, before the loop starts.
- **The loop variable is read-only inside the body.** It changes only by the step, at the end of each pass.
- **Direction comes from the step alone.** Without `step` the step is 1 and the loop counts up, whatever its bounds; a negative `step` counts down: `for i in 9..=0 step -1`, `for i in n..0 step -1`. The bounds never decide the direction, so it can be read from the loop header without knowing which bounds are constants. A loop that counts up and can never run, whatever its variables hold, is an error, since it is almost always a down-count without its step: both bounds constant with the start past the end (`9..=0`, `255..=0 step n`), or an unsigned counter ending before the constant 0 (`n..0`): "'9..=0' counts up, so it never runs. Write 'step -1' to count down" (with a variable step, "Use a constant negative step to count down", since a variable step cannot be negative). A loop whose variables decide whether it runs is allowed, with no warning: an unsigned `n..=0` runs once when `n` is 0 (also `a..=0 step n`), and a signed `n..=0` counts up from a negative `n`. (Before 2026-11-11 the bounds chose the direction; decisions.md.)
- `step` is a non-zero constant, 1 when not written.
- **Variable step:** a loop that counts up with a `u8` counter may have a `u8` expression as its step, evaluated once like the bounds: `for m in (n << 1)..=255 step n`. It follows the rule for a variable end below: the loop stops when adding the step carries past 255, so `..=255` compiles to `adc` / `bcc` with no compare (section 11). A step that is 0 at runtime never ends the loop; the compiler adds no check (design rule 1). A negative or 16-bit variable step is an error. As in C, `for i in x..=y step 2` matches `for (i = x; i <= y; i += 2)`, and `x..y` matches `i < y`.
- With a step other than ±1, the counter can jump past the end. With constant bounds the compiler computes the last value and compares against the value after it, as usual. With a variable end, the loop tests "less than" (`cmp` / `bcc`) instead of "not equal", and stops if adding the step carries past the end of the type (e.g. 250, 254, then 258 would wrap to 2). This costs a few extra instructions per pass.
- **Constant ranges:** an empty range (`5..5`) generates no code, with no warning (13), since a module parameter of 0 (`0..COUNT`) is how an option is turned off. A range with one value (`5..=5`) generates the body once, with no loop instructions. The compiler never unrolls longer loops on its own.
- **Variable bounds:** if the range can be empty at runtime, the compiler adds one check before the loop. This costs a few cycles per loop, not per pass, and is left out when the compiler can prove the range is not empty.
- `_` as the loop variable means the value is not needed. The compiler may then count in either direction, e.g. `dex` / `bne`. A loop with `_` is never slower than the same loop with a named counter that the body does not read, so writing `_` (as the unused-variable warning suggests, 13) never costs cycles.
- The loop variable is `u8` unless the range needs more. `using X` or `using Y` keeps it in that register.
- **Signed loop variables.** If a bound is a negative constant or has a signed type, the loop variable is `i8` when the bounds fit in -128..127, and `i16` otherwise: `for i in -10..10`, `for i in n..=0 step -1` (n: `i8`). A signed bound with an unsigned variable bound (`-5..n`, n: `u8`) is an error, as mixing signed and unsigned is anywhere (3.1); write `n as i8`. The rules above apply unchanged, with the order of values signed:
  - With a step of ±1 the code is the unsigned code: the counter steps through the same bytes and is tested for equality. Only where order matters does the code differ.
  - With variable bounds, the check for an empty range before the loop is a signed compare, a few bytes more than the unsigned check, once per loop. `n..=0 step -1` and `n..0 step -1` need none: the `ldx n` sets the sign flag, which is the check.
  - With a step other than ±1 and a variable end, the loop stops when adding the step overflows past the end of the type (the V flag, the signed form of the carry rule above), and the test against the end is a signed compare: the end is flipped (`eor #$80`) once before the loop, and the counter is flipped before each `cmp`.
  - An `i8` or `i16` counter takes only a constant step; a variable step is an error. `using X` / `using Y` is allowed on an `i8` counter, not on an `i16` one (as for `u16`).

### 5.2 `match`

- The value is a `u8` or `i8`. A 16-bit value is not supported yet (match on `.lo` or `.hi`, or use `if`); `bool`, pointers and structs are errors.
- One arm per line: `patterns => body`, where the body is a block or one statement.
- Patterns are constant expressions (numbers, characters, `const`s), alternatives separated by `,`, and inclusive ranges `lo..=hi`. `lo..hi` is an error: a range in a match includes its end.
- `_` matches every other value. It must be alone in the last arm. Without `_`, a value that matches no pattern skips the match.
- A value in two patterns, or an empty range, is an error. There is no fall-through.
- Code shapes: section 11.
- **`indexed match`.** A plain `match` is always a chain of compares; `indexed match` is always a jump table: the value, minus the lowest pattern value, indexes a table of arm addresses, 2 bytes per value from the lowest pattern value to the highest (the span; the report shows the tables). A value inside the span that no pattern lists goes to `_`, or skips the match without `_`. A value outside the span is not checked, as an array index is not (3.2): it jumps through whatever bytes follow the tables. In test runs the emulator reports it, with the source line, and stops the run (14.4). A value that cannot leave the span needs no care: `indexed match op & 15` with patterns 0 to 15. `indexed` is the word of 3.3, meaning a table the compiler generates; it is a word only before `var` and `match`.

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
- **`-> bool using overflow`** (the V flag), for testing what `bit` copies from bit 6 of memory, or the signed overflow of an `adc`/`sbc`. Allowed on an `inline asm fn`, an `asm fn` and a routine at a fixed address (6.6), where the instructions that set V are visible or documented; not on a plain `fn`, since the 6502 has no instruction that sets V (`clv` only clears it), so `return true` would need a trick such as `bit` on a byte with bit 6 set. A return value only, never a fixed input (6.6). `if f()` is `bvc skip`; an empty body reads the V flag left by the `asm` block before it, under the rules of section 7.
- Registers not used for return values may be changed by any call. The compiler knows each function's actual register use and uses that instead where it can.

### 6.2 Inline functions

`inline fn` bodies are copied into each caller. An inline function has no frame of its own: its parameters, locals and temporaries go in the frame of each function that calls it, as in C. All copies in one caller share one set; two callers have two sets. So inlining saves the `jsr`/`rts` but adds the inline function's variables to every caller's frame; frames are shared between functions that are never active at the same time (8.1), so this usually costs little. Because each caller has its own set, an inline function with locals may be called from main and from a handler.
- Calling an inline function counts as a call (e.g. in a `for ... using X` loop).
- A function that an inline function calls is still one function, with one frame: if it has memory of its own, calling it through the inline function from two roots shares that memory between the roots (8.1).
- A function reachable from two roots that calls an inline function with variables has those variables in its own frame, so they are shared between the roots (8.1).

`inline asm fn` copies its assembly into each caller in place of the `jsr`; its parameters must all be registers (fixed inputs and memory parameters are errors) and the compiler adds no `rts`. Its body may start with `var` lines for work variables (section 9), which go in each caller's frame like the variables of an `inline fn`. With a flag return it is a cheap way to test a flag (section 7). The optimizer inlines small functions with a single caller automatically.

### 6.3 Interrupt handlers

`interrupt fn raster() { ... }` saves and restores the registers it changes and ends with `rti`; the CPU saves the flags.

- A register is saved if any instruction in the handler changes it, including instructions in asm blocks. Any `jsr` counts as changing A, X and Y. X and Y are saved through A, so A is saved whenever X or Y is. The order is A, X, Y.
- The handler starts with `cld`, since main may be in decimal mode (the `bcd` library, or an asm block with `sed`) when the interrupt arrives. Decimal mode only changes `adc` and `sbc`, so `cld` is left out when the handler executes neither: no `adc` or `sbc` in its code or asm blocks, and no `jsr`.
- Code the compiler cannot follow, such as an asm `jmp` out of the handler, saves and restores registers itself, and clears decimal mode itself: a `jmp` does not count as a `jsr` for the `cld` rule. A handler that chains to other code (`jmp $EA31`) and may interrupt decimal-mode code puts `cld` in its asm block.
- A handler cannot be called from ordinary code, neither from Tern nor with `jsr`. Naming it in assembly (`lda #raster.lo`) or with `addr(raster)` is not a call.
- **An `interrupt fn` may be called from another `interrupt fn`.** The called one is then compiled as a *part*: its body ending in `rts`, with no register saving and no `rti`; the installed (outer) handler saves registers once and ends with the one `rti`. Each `interrupt fn` has one form: a part if it is called, otherwise a handler. Calling one and also installing it (its address taken) is an error. A library that offers both writes a small handler that calls the part (`interrupt fn handler() { top() }`), which costs one `jsr`/`rts`. This lets a program combine library interrupt code (a sprite multiplexer, a scroller) in one handler it installs itself:
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

Algorithms that are naturally recursive (flood fill, tree walks) use an explicit stack: an array plus an index, in a loop. The cost is then visible in the code.

### 6.5 Function addresses

`addr(f)` is the address of function `f`, a `u16` constant that costs nothing: `irq_vector = addr(raster)`, or a jump table in `data` (with a constant offset or `.lo` / `.hi`, as for any address in an initial value, 4).

- Allowed for functions without memory parameters (register parameters are fine), since a call through the address cannot set them. An error for `inline fn`, which has no single body.
- The compiler cannot see calls through an address, so a function whose address is taken, and every function it reaches, get frames that overlap no other frame (8.1). For an `interrupt fn` or a routine at a fixed address nothing changes.
- A handler that the CPU calls must be an `interrupt fn`; an ordinary function ends with `rts`. The compiler cannot check this: `addr()` gives a plain `u16`, so `c64.set_irq(addr(plain_fn))` compiles. It is a documented convention of the installing functions.
- In assembly, `#f.lo` / `#f.hi` take the address of `f` like `addr(f)`; `jsr f` and `jmp f` are calls. Assembly may take the address of a function with memory parameters, since it can set them itself.
- A function reached only through its address has no known root, so the compiler cannot see when it runs (8.1). Calling it through its address from two roots (main and a handler) is the program's responsibility, like `keeps` (6.6). Globals it uses count as used from an unknown root, so any it shares with other code are volatile.

**Planned: calls through pointers** (not built, not scheduled). A variable can hold a function address and be called like a function. The compiler cannot see which function runs, so the variable's type lists every possible target, which keeps the call graph complete:

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
- **A function reachable from two roots** (main and a handler, or two handlers) is allowed, with no error or warning. It can be active while any function of either root is, so its frame shares memory with no frame reachable from those roots. It has one frame, not one per root: if a handler calls it while main is inside it, the handler's call overwrites the memory main's call is using (memory parameters, locals, loop counters in memory, the compiler's temporaries), and main's call goes on with those values. Making that impossible is the program's responsibility (decision 2026-11-12), as is any timing between main and a handler. The ways (developer guide): call it in main only with interrupts off (`sei` ... `cli`); make it an `inline fn`, whose variables are in each caller's frame (6.2); give it no memory of its own (register parameters, `using X`/`using Y` loop counters, no locals or temporaries), since the handler saves the registers; or write a second function for the handler. The compiler never copies a function for a second root (14.2). The report (13) lists each function with memory reachable from two roots, with its roots; this is information, not a warning, since the safe ways above would have no way to silence one. Routines at a fixed address may be called from any root.
- **A module's `private` variable used from two roots** is a compile error, e.g. the library's work bytes (`math.w0`-`w2`): the two sides would overwrite each other's values. Public variables and `data` may be shared.
  - Exception: when one of the two roots is an `interrupt fn` declared in the variable's own module, sharing is allowed. A function of that module called only from the module's own `interrupt fn`s (directly or through other such functions) counts as that side too, so a handler's helpers need not be `interrupt fn`s themselves. The module's author wrote both sides, so the sharing is by design, e.g. a `timer` module whose handler counts `private var ticks` and whose `now()` reads it. The variable is volatile, as below.
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

### 8.4 Memory layout

The platform gives a default memory map (12.2). The main file may declare what it changes in one `memory` block, after its imports. Only `const` declarations may come between the imports and the block, so the constants its ranges use can stand above it (constants declared further down may be used too). Only the main file may have one, since it describes the whole program.

```
memory {
    $0801..=$1FFF  program
    $2000..=$3FFF  reserved              // VIC bank 0: sprites, character set
    $4000..=$9FFF  program
    $C000..=$CFFF  region buffers
    $D000..=$DFFF  volatile
    $FB..=$FE      zeropage
}
```

Each line is an inclusive range of constant expressions (constants from imported modules may be used) and a kind:

| Kind | Meaning |
|---|---|
| `program` | Code, `data` and variables |
| `zeropage` | Zero-page bytes the compiler may allocate (8.2) |
| `reserved` | The compiler places nothing here, except what the program puts there with `@` |
| `region name` | Placed only on request: `var map: [u8; 2048] @ buffers`, also for `data`. `@` stays a location |
| `volatile` | Volatile memory (4), in addition to the platform's volatile areas |
| `overlay name` | An overlay area (8.5) |

- **Replacing or adding.** If the block has any `program` line, the `program` lines replace the platform's program and variable areas; the same for `zeropage` lines and the platform's free zero page. The other kinds add to the platform's map and take their range out of any program area and out of the free zero page. A block with only `reserved` lines keeps the platform's areas.
- **Functions are placed whole.** A function never crosses from one area into another; the compiler never inserts a `jmp` to continue it elsewhere. Code that does not fit gives an error that lists the areas and what is in them.
- Ranges may not overlap, except that a `volatile` range may lie in any other kind.
- **Several `program` areas:** code and initialized `data` fill the areas in order; the variables, as one block, go in the first area that has room after its code. `@ region` places a variable elsewhere.
- **Zero page** is allocated in order through the `zeropage` ranges. A variable that would cross the gap between two ranges is an error.
- A region name may not be the name of a constant or other symbol of the main file; that is an error, so `@ name` has one meaning. Regions exist only in the main file, with the block.
- `memory`, and the kinds above, are words only inside the block, not reserved words.
- A program with `main` starts at the platform's code start (C64: `$0801`, with the BASIC line), so its first `program` area must begin there.

**`data` at a fixed address**: `data sprite @ $2000 = [...]` places the bytes there, in a `program` or `reserved` area (the usual case: VIC graphics in a reserved range), or, for a module placed in an overlay area, in that area (8.5). The program file covers every byte from its lowest to its highest used address, so a gap between areas is filled with zeros and makes the file longer; the report shows the file size.

### 8.5 Overlays

An overlay is a set of modules that is loaded into an overlay area when the program needs it, e.g. one level of a game from disk. Overlays in the same area replace each other. The main file places a module with `@` on its import:

```
memory {
    $0801..=$5FFF  program
    $6000..=$7FFF  overlay levels
}
import level1 @ levels
import level2 @ levels
```

- **One build.** The whole program, overlays included, is compiled together, so the call graph, frame sharing (8.1) and zero-page allocation work as everywhere else.
- **Output:** one file per overlay module, named after it (`level1.prg`), loading at the start of its area. It holds the module's code, `data` and global variables with their initial values (zeros included), so loading it also sets its variables. Startup code never touches an overlay's variables.
- **What is in an overlay:** only the placed modules. Modules they import stay resident unless placed too. The main file cannot be in an overlay.
- **Calls:**
  - An overlay may call resident code, its own code and overlays in other areas.
  - A call between two overlays in the same area is an error, since they are never loaded together.
  - A call from resident code into an overlay is a plain `jsr`. Having that overlay loaded at that moment is the program's responsibility; the compiler cannot check it, and a check at run time would cost every call.
- **Shared memory:** overlays in the same area are never present together, so their global variables may share memory, as frames do. They are in the overlay's file, so loading it sets them.
- **Zero page.** An overlay's global variables are never placed in zero page, since loading a file cannot set it. A global that must be there (`@ zp`, or a pointer that is dereferenced) is an error; the message suggests a local variable or a fixed address. Locals and parameters of overlay functions are in the usual frames, zero page included.
  - An overlay may name zero-page bytes at a fixed address (`var p: ptr u8 @ $FB`). Like any fixed-address variable it is a name for memory, not part of the file, and the overlay sets it itself. The main program may name the same address to share the byte; the same rules for overlapping apply as everywhere (4). A resident module's zero-page global, used by its qualified name, is the other way to share.
- **Other uses between overlays of one area** are errors too, since the other overlay is never loaded: using its global variables or `data`, and `addr()` of its functions or `data`. Resident code may take addresses in any overlay.
- An overlay may declare `interrupt fn`s. Installing one is the program's responsibility, and so is removing it before the overlay is replaced.
- **Fixed-address variables over an overlay.** A fixed-address variable that overlaps an overlay's file is an error, as for the resident program (4), unless it is marked `volatile`. The keyword states that the overlap is intended: the variable reads and writes whatever the loaded overlay holds at that address, e.g. a level header that every level puts in the same place:
  ```
  volatile var header: LevelHeader @ $6000
  ```
  - Without `volatile`, the error names the variable, the overlay and both ranges, and says to add `volatile` if the overlap is intended, or to name the overlay's variable (`level2.lives`), whose address follows the overlay when it changes.
  - Every access is volatile (4). A write to the variable counts as a write through a pointer: the optimizer forgets what it holds for every variable, so an overlay variable at the same address is never read stale from a register.
  - The compiler does not check what is at that address, which overlay is loaded, or whether one is loaded at all. Addresses inside an overlay's file move when its code or data change; a fixed layout is the program's responsibility.
  - A fixed-address variable in the part of an overlay area that no overlay's file covers needs no keyword.
  - Overlapping the resident program stays an error with or without `volatile`.
- **Fixed data in an overlay.** An overlay module may place `data` at a fixed address inside its own area, e.g. a level's header and tables at the addresses the resident code expects:
  ```
  // level1.tern, imported with `import level1 @ levels`
  data header: LevelHeader @ $6000 = LevelHeader { width = 40, height = 12 }
  ```
  - The bytes are part of that module's file. Its code, other `data` and variables go around them, as in a program area (8.4); a gap between them is zero-filled, and the file still starts at the area's start.
  - Overlays of the same area may put data at the same address, since they are never loaded together.
  - Errors: fixed data overlapping the module's other fixed data or fixed variables; an overlay module's fixed data outside its own area (in another overlay area, or in a program or reserved area: put resident data in the main file or a resident module); a resident module's fixed data in an overlay area.
- An overlay that does not fit its area gives an error with its size.
- **Loading** is a platform library function. C64: `c64.load(name: ptr u8, n: u8, device: u8) -> bool` loads a file to its own address through the KERNAL (logical file 1, secondary address 1) and returns the KERNAL's carry (true when loading failed). The drive's error channel is not read. An overlay's file is named after its module (`level1.prg`); the name on the disk is the program's choice, passed as a PETSCII string (`data NAME = "LEVEL1"`, then `c64.load(addr(NAME), len(NAME), 8)`).
- **Interfaces: `implements`.** Overlays that play the same part, such as levels, can offer the same entries at the same addresses, so resident code calls whichever one is loaded without knowing which:
  ```
  import level_api                              // fn init(n: u8 using A) @ $6000, fn update() @ $6003, ...
  import level1 @ levels implements level_api
  import level2 @ levels implements level_api

  level_api.update()                            // jsr $6003: the loaded level's update
  ```
  - **The name after `implements` is a module the main file imports**, like every name in the main file (a module imported only by another module does not count): "'x' is not a module the main file imports. Add 'import x' to the main file".
  - **An interface is an ordinary module.** Its routines at a fixed address (6.6) and its `volatile` fixed-address variables are what `implements` matches; it has no marker of its own. Its other declarations (constants, structs, functions with a body, other variables) are ordinary, so one module can hold the entries and the types and helpers the levels share. Imported by the main file, it is resident, and its functions are compiled once.
  - **The table.** For each routine, the compiler places a `jmp` at the routine's address to the module's function of the same name. The module's code, `data` and variables go around the table, as around fixed `data` (above). The checks are those of `export` naming a module (12.3): a function for each routine, the same register parameters and return, no register changed that the routine's `keeps` promises, `data` of the same name, type and address for each `volatile` variable, routines at least 3 bytes apart, no routine with a fixed input. A `setup` routine is matched like any other; with `implements` none is required, since the build's startup code and the overlay's file set everything up.
  - **Calls.** A call to an interface routine is a `jsr` to its address, as for any routine at a fixed address. The call graph counts it as a call to each implementing module's function, so their frames are shared (8.1) and the two-roots check sees them. Every implementing function is compiled, since its table points to it. Having one of the modules loaded is the program's responsibility, as for every call into an overlay. A plain call (`level1.update()`) stays allowed.
  - **An implementation no interface call reaches** (the interface is never called, or only from code that is not compiled) is still compiled, since its table points to it, as a function whose address is taken (6.5): its frame shares memory with no other, since a call from `asm` or from outside the build cannot be seen.
  - **The module names its own `data`**, not the interface's variable at the same address, as a binary does (12.3): inside `level1`, `level_api.header` is an error, "'level_api.header' is this module's data 'header'. Use 'header'", so the bytes have one name and the module's own reads are not volatile. Other code, resident code and the interface's own functions included, uses `level_api.header`.
  - Several modules in one area may implement the same interface: their tables are at the same addresses, which is allowed since they are never loaded together. A module may implement several interfaces, `implements a, b`, if their routines do not overlap.
  - Errors: `implements` on a module not placed in an overlay or bank (a resident program uses `export ... @ address`, 12.3); an interface routine or `volatile` variable outside the module's area; tables of two interfaces overlapping; an interface module with no routines at a fixed address ("'c64' has no routines at a fixed address to implement"); an interface module placed in an overlay or bank area, reported at the name after `implements` ("'api' is placed in the overlay area 'lv'; an interface must be resident. Remove '@ lv' from its import").
  - `implements` is a reserved word.
- **Bank switching** (14.1) uses the same model, with a bank area switched in by a mapper instead of loaded from disk: modules are placed on their import, a call into a bank is a plain `jsr`, the program selects the bank through the platform library, and `implements` works the same way.

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
- An `inline asm fn` body may start with `var name: type` lines: work variables of any type (scalar, array, struct), named in the instructions like any local, and placed in each caller's frame (6.2):
  ```
  inline asm fn min8(a: u8 using A, b: u8 using X) -> u8 {
      var tmp: u8
      stx tmp
      cmp tmp
      bcc .done
      lda tmp
  .done:
  }
  ```
  `var` lines come before the first instruction, have no initial value (the assembly sets it; an initial value would add code the block does not show) and no `@` (a fixed address would make the copies share it). Each of these is an error, as is a `var` line in an `asm { }` statement (declare the variable before the block).
- A plain `asm fn` body may start with `var` lines too, under the same rules. They are the function's locals, in its frame (8.1), shared with functions that never run at the same time; their names are local to the function. A pointer used as `(p),y` is placed in zero page, as for any pointer that is dereferenced (3.4).

## 10. Built-ins

| Built-in | Meaning |
|---|---|
| `addr(name)` | Pointer to a variable, array, or field of a struct variable; address of a function (3.4) |
| `sizeof(T)` | Size of a type in bytes |
| `len(a)` | Number of elements in an array or string, a constant with no type (3.1) |
| `offset(T.field)` | Offset of a field from the start of struct `T` in bytes, a constant with no type, like `sizeof`; nested fields too: `offset(Enemy.pos.x)`. For assembly through a pointer: `ldy #offset(Enemy.hp)` / `lda (p),y` (9) |
| `include_bytes("file")` | The bytes of a file, as the initial value of `data`; the path is relative to the source file (4.1) |

### 10.1 Standard library

The standard library is ordinary Tern and `asm fn` source in the compiler's `lib/` folder, imported like any module (2.2). It is not part of the language: the compiler gives it no special treatment, so it can be read, copied and changed (e.g. a faster multiply for one program). Its purpose is to make common tasks easy from the first program, and to be an example of good Tern code.

**Strings and memory blocks are passed as a pointer plus a length**, matching `len()` on literals. The library has no zero-terminated variants.

The tables below describe the library as built (M4, M5, M10, M11, M14, M19 and the M20 follow-up). Work variables and tables that callers should not use are `private` (2.2).

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
| `abs8` | `(x: i8 using A) -> u8 using A` | Absolute value; `abs8(-128)` is 128 |
| `abs16` | `(x: i16 using AX) -> u16 using AX` | Absolute value |
| `muls8` | `(a: i8 using A, b: i8 using X) -> i16` | Signed multiply |
| `divs8` | `(a: i8 using A, b: i8 using X) -> i8` | Signed divide, rounding towards zero. `math.remainder8` has the sign of `a`. -128 / -1 is -128 |
| `min8`, `max8` | `(a: u8 using A, b: u8 using X) -> u8` | `inline asm fn`: no `jsr` |
| `mins8`, `maxs8` | `(a: i8 using A, b: i8 using X) -> i8` | `inline asm fn` |
| `clamp8` | `(x: u8 using A, lo: u8 using X, hi: u8 using Y) -> u8` | `inline asm fn` |
| `clamps8` | `(x: i8 using A, lo: i8 using X, hi: i8 using Y) -> i8` | `inline asm fn` |
| `sin8`, `cos8` | `(x: u8 using A) -> i8` | `inline asm fn`; 256 steps per circle, results -127..127 (`round(127 * sin(2 pi x / 256))`); one shared 256-byte table, included only when used |

- Division computes the quotient and the remainder together, as in assembly. The remainder is left in a library variable (`math.remainder8`, `math.remainder16`) for the caller to read straight after the call.
- Division by zero does not trap. The result is unspecified.
- `min8`, `max8`, `clamp8` and the signed ones keep their work bytes in `var` lines, so it is in each caller's frame (6.2): they may be called from main and from a handler.
- Multiplying or dividing by a constant needs no library call: write the shifts and adds (`(w << 5) + (w << 3)` for `w × 40`), or `& 7` for "mod 8".
- Memory parameters of `asm fn` routines are not named `a`, since `asl a` means the accumulator (9). Arguments are positional, so callers are not affected.

**`mem`**: memory blocks

| Function | Signature | Purpose |
|---|---|---|
| `copy` | `(dst: ptr u8, src: ptr u8, count: u16)` | Copy forward. Overlapping blocks work only when `dst` is below `src` |
| `fill` | `(dst: ptr u8, value: u8, count: u16)` | Fill a block |
| `copy_back` | `(dst: ptr u8, src: ptr u8, count: u16)` | Copy last byte first, for overlapping blocks with `dst` above `src` |

**`text`**: text in memory, platform independent

| Function | Signature | Purpose |
|---|---|---|
| `put` | `(dst: ptr u8, src: ptr u8, n: u8)` | Copy text to memory, e.g. a `screen"..."` string to screen memory |
| `dec8`, `dec16` | `(dst: ptr u8, value: u8 or u16) -> u8` | Write a number in decimal, without leading zeros; returns the number of digits. Uses repeated subtraction, no division |
| `dec8s`, `dec16s` | `(dst: ptr u8, value: i8 or i16) -> u8` | Signed decimal, with `-` for negative values; returns the number of characters written |
| `dec8z`, `dec16z` | `(dst: ptr u8, value: u8 or u16, width: u8)` | Exactly `width` digits, with leading zeros. The value must fit, otherwise the digits are unspecified; nothing is written past `width` |
| `hex8`, `hex16` | `(dst: ptr u8, value: u8 or u16)` | Write a number as 2 or 4 hex digits |
| `equal` | `(a: ptr u8, b: ptr u8, n: u8) -> bool` | Compare two texts |
| `find` | `(s: ptr u8, n: u8, ch: u8) -> u8` | Index of the first `ch`, or `$FF` if not found |

Digits are written as screen codes, which are the same as PETSCII for `0`-`9`. Hex letters are screen codes too.

**`wide`**: 24- and 32-bit unsigned numbers

`struct U32 { lo: u16, hi: u16 }` and `struct U24 { lo: u16, hi: u8 }`, little-endian. The 16-bit halves are plain fields, so no cast or overlay is needed: `score.hi` is a `u16` (`lda score+2` / `ldx score+3`), and single bytes are `score.lo.hi` and so on. There are no 24- or 32-bit primitive types. Struct parameters are not allowed (3.3), so every function takes pointers, and the result goes into the first operand, as in assembly: `wide.add32(addr(score), addr(bonus))`.

Each function exists for both widths: `W` is `24` or `32`, and `UW` is `U24` or `U32`.

| Function | Signature | Purpose |
|---|---|---|
| `setW` | `(p: ptr UW, v: u16)` | Set from a `u16` |
| `copyW` | `(dst: ptr UW, src: ptr UW)` | Copy |
| `addW`, `subW` | `(p: ptr UW, q: ptr UW)` | `p = p + q`, `p = p - q`, wrapping |
| `addW_u16`, `subW_u16` | `(p: ptr UW, v: u16)` | Add or subtract a `u16` |
| `incW` | `(p: ptr UW)` | Add 1 |
| `lessW` | `(p: ptr UW, q: ptr UW) -> bool using carry` | `p < q` |
| `equalW` | `(p: ptr UW, q: ptr UW) -> bool using carry` | `p == q` |
| `is_zeroW` | `(p: ptr UW) -> bool using zero` | `p == 0` |
| `mulW` | `(p: ptr UW, q: ptr UW)` | Low W bits of the product; shift-and-add |
| `divW` | `(p: ptr UW, q: ptr UW)` | Quotient; shift-and-subtract. Also sets `wide.remainderW` |
| `decW` | `(dst: ptr u8, p: ptr UW) -> u8` | Decimal without leading zeros; returns the number of digits (up to 8 or 10) |
| `decWz` | `(dst: ptr u8, p: ptr UW, width: u8)` | Fixed-width decimal with leading zeros, as `text.dec16z` |
| `hexW` | `(dst: ptr u8, p: ptr UW)` | 6 or 8 hex digits |

Decimal output converts a copy to BCD (doubling in decimal mode) and costs thousands of cycles: convert when the value changes, or keep a displayed-only score in `bcd`.

**`bcd`**: packed decimal counters of `n` bytes (two digits per byte, low byte first), passed as pointers; for scores

| Function | Signature | Purpose |
|---|---|---|
| `clear` | `(p: ptr u8, n: u8)` | Set to 0 |
| `add`, `sub` | `(p: ptr u8, q: ptr u8, n: u8 using X)` | `p = p + q`, `p = p - q` |
| `add_byte`, `sub_byte` | `(p: ptr u8, n: u8 using X, v: u8 using A)` | Add or subtract `v`, two packed digits (`$25` is 25) |
| `inc`, `dec` | `(p: ptr u8, n: u8 using X)` | Add or subtract 1 |
| `less` | `(p: ptr u8, q: ptr u8, n: u8 using X) -> bool using carry` | `p < q` |
| `equal` | `(p: ptr u8, q: ptr u8, n: u8 using Y) -> bool using carry` | `p == q` |
| `is_zero` | `(p: ptr u8, n: u8 using Y) -> bool using zero` | `p == 0` |
| `digits` | `(dst: ptr u8, p: ptr u8, n: u8)` | All `2n` digits, with leading zeros |
| `text` | `(dst: ptr u8, p: ptr u8, n: u8) -> u8` | Without leading zeros; returns the number of digits. Needs room for `2n` at `dst` |

Values wrap modulo 10^(2n). The routines use `sed` and always leave decimal mode off; interrupt handlers clear decimal mode (6.3).

**`float`** (M24; decisions 2026-11-13): floating-point numbers in 4 bytes

`struct F32 { bytes: [u8; 4] }`. The format suits the 6502 rather than IEEE 754, whose exponent straddles two bytes:

| Byte | Contents |
|---|---|
| 0-2 | Mantissa, low byte first: 24 bits of precision. The leading 1 is not stored; bit 7 of byte 2 is the sign |
| 3 | Exponent plus `$80`; `$00` means the value is 0 |

As in Microsoft BASIC, the value is 0.1mmm… (binary) × 2^(byte 3 − $80): the mantissa, with its leading 1 restored, is from 1/2 to just under 1. So 1.0 is `00 00 00 81` and -1.0 is `00 00 80 81`; the smallest magnitude is 2^-128 (`00 00 00 01`, about 2.9e-39) and the largest (1 − 2^-24) × 2^127 (`FF FF 7F FF`, about 1.7e38).

About 7.2 significant digits; magnitudes from about 2.9e-39 to 1.7e38. There is no NaN, infinity or denormal. Results are rounded to nearest, halves away from zero (one guard byte in the work area). A result too large becomes the largest value with its sign and sets `float.overflow` (a `bool` that stays set until the program clears it); so does division by zero. A result too small becomes 0, with no flag.

- **Zero.** Any value whose byte 3 is `$00` is 0, whatever its other bytes; every routine reads it so. A routine that stores 0 writes four zero bytes, except that `add` and `sub` with a 0 operand store nothing when p is the result (p + 0 leaves p as it is). `neg` leaves 0 as 0, so no -0 is ever made.
- **Division by zero** gives the largest value with p's sign and sets `overflow`; 0 / 0 gives the positive largest.
- **Toward zero and rounding differ at small negatives:** `to_u16` of -0.5 is 0 without `overflow` (toward zero it is 0), while `round_u16` of -0.5 rounds to -1, so it gives 0 and sets `overflow`.

As in `wide`, every function takes pointers and the result goes into the first operand: `float.mul(addr(v), addr(dt))`. The work bytes are private: use the routines from main only, or from one interrupt handler only.

| Function | Signature | Purpose |
|---|---|---|
| `add`, `sub`, `mul`, `div` | `(p: ptr F32, q: ptr F32)` | `p = p op q` |
| `neg`, `abs` | `(p: ptr F32)` | Flip or clear the sign |
| `cmp` | `(p: ptr F32, q: ptr F32) -> i8` | -1, 0 or 1 |
| `is_zero` | `(p: ptr F32) -> bool using zero` | `p == 0` |
| `copy` | `(dst: ptr F32, src: ptr F32)` | Copy |
| `from_u16`, `from_i16` | `(p: ptr F32, v: u16)` / `(p: ptr F32, v: i16)` | Set from an integer (exact) |
| `to_u16`, `to_i16` | `(p: ptr F32) -> u16` / `-> i16` | Toward zero. Out of range gives the nearest limit and sets `overflow` |
| `round_u16`, `round_i16` | `(p: ptr F32) -> u16` / `-> i16` | Nearest, halves away from zero, as the compile-time `round` (4.1). Out of range as `to_` |
| `trunc` | `(p: ptr F32)` | Toward zero, kept a float: clears the fraction bits. Any value; never overflows |
| `floor` | `(p: ptr F32)` | Toward minus infinity, kept a float: `floor(-2.7)` is -3, so `x - floor(x)` is always in 0 to 1 |
| `dec` | `(dst: ptr u8, p: ptr F32, decimals: u8) -> u8` | Fixed-point decimal text in screen codes, as `wide.dec32`; returns the number of characters. Details below |
| `of` | `const fn (x: real) -> [int; _]` | The 4 bytes of `x`, rounded as above, for constants: `var g: float.F32 = float.F32 { bytes = float.of(9.81) }`. A value too large is an error |

`trunc` and `to_i16` round the same way; `trunc` keeps a float, works for any magnitude and only masks bits, while `to_i16` shifts the mantissa into place. Values of 2^23 or more have no fraction bits, so `trunc` and `floor` leave them unchanged.

`dec` writes the value rounded to `decimals` places (0 to 9; more is taken as 9). There is at least one digit before the point (`0.05`), no point when `decimals` is 0, and a `-` only when the text is not all zeros (-0.004 with 2 decimals is `0.00`). The text is at most 50 characters (39 digits for values near the largest): `dst` needs room for 50, or for the program's largest value. It costs up to about 7,700 cycles below 65536 and up to about 91,000 near the largest, since it converts a 128-bit integer.

Sizes and cycles (cycles include `jsr` and `rts`, best and worst over the test values). Bytes are each routine's own code; the shared steps (unpacking, normalizing, rounding, storing 0 or the largest: 214 bytes) come once with the first routine that needs them, and unpacking a second operand (32) with the first that takes two. With every routine used: about 2,450 bytes of code and tables, and 124 bytes of variables, 19 of them in zero page.

| Routine | Bytes | Cycles | Notes |
|---|---|---|---|
| `add` / `sub` | 22 / 28, + 329 shared | 108-961 / 116-951 | the most after cancelling, with a long normalization |
| `mul` | 32 + 128 | 153-2490 | 153 when an operand is 0 |
| `div` | 37 + 146 | 218-4418 | 32 steps of shift-and-subtract |
| `neg` / `abs` | 14 / 9 | 22-36 / 27 | |
| `cmp` | 94 | 58-138 | |
| `is_zero` | 5 | 19 | |
| `copy` | 10 | 77 | |
| `from_u16` / `from_i16` | 19 / 36, + 34 shared | 102-509 / 106-524 | |
| `to_u16` / `round_u16` | 13 / 13, + 55 + 130 shared | 158-461 / 158-487 | |
| `to_i16` / `round_i16` | 13 / 13, + 76, + the 130 shared with `to_u16` | 164-469 / 164-495 | |
| `trunc` | 31, + 68 shared with `floor` | 94-306 | |
| `floor` | 116, + 68 shared with `trunc` | 94-394 | |
| `dec` | 229 + 375 | 1245-7692 below 65536; up to 91222 | |
| `of` | none | none | compile time |

**`random`**: xorshift generators

| Function | Signature | Purpose |
|---|---|---|
| `seed` | `(v: u16)` | Set both generators; 0 becomes 1 |
| `next8` | `() -> u8` | 8-bit xorshift (3, 5, 4): values 1-255, period 255 |
| `next16` | `() -> u16` | 16-bit xorshift (7, 9, 8): period 65535 |
| `below` | `(n: u8) -> u8` | A value in `0..n`: the high byte of `next8() * n` (uses `math.mul8`). Not exactly uniform. 0 for `n = 0` |

The state is private and starts at 1, so using `random` from main and a handler is an error (8.1); a handler that needs random numbers uses its own generator.

**`cpu`**: the CPU flags after an `asm` block, platform independent

| Function | Signature | `if f()` compiles to |
|---|---|---|
| `carry` | `() -> bool using carry` | `bcc skip` |
| `zero` | `() -> bool using zero` | `bne skip` |
| `negative` | `() -> bool using negative` | `bpl skip` |
| `overflow` | `() -> bool using overflow` | `bvc skip` |

Each is an empty-body `inline asm fn` (section 7), so a call adds no instructions: the test branches on the flag that the instructions before it left. It is defined only directly after an `asm` block, under the rules of section 7: `asm { jsr read_byte }` then `if cpu.carry() { ... }`. After compiled Tern code the result is unspecified.

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
| `load(name: ptr u8, n: u8, device: u8) -> bool` | Load a file to its own address through the KERNAL, e.g. an overlay (8.5); true on failure |
| `kernal_off()` | Switch the KERNAL and BASIC ROMs off (`$01 = $35`, I/O stays), turn off the CIA interrupts, ignore NMIs (RESTORE); leaves interrupts disabled. KERNAL routines cannot be called afterwards |
| `set_irq(handler: u16 using XY)` | Install an `interrupt fn` at `$FFFE`, after `kernal_off()` |
| `set_nmi(handler: u16 using XY)` | Install an `interrupt fn` at `$FFFA`, after `kernal_off()` |
| `interrupts_on()`, `interrupts_off()` | `cli`, `sei` (`inline asm fn`, no `jsr`) |
| `cia_irqs_off()` | Turn off the interrupts of both CIAs (`icr = $7F`) and read each `icr` to clear any that are pending, e.g. before `set_kernal_irq` with a raster interrupt; the KERNAL's timer interrupt stops |
| `vic: Vic @ $D000` | VIC-II registers: `sprite0_x`, `sprite0_y` ... `sprite7_y`, `sprite_x_msb`, `control1`, `raster`, `light_pen_x`, `light_pen_y`, `sprite_enable`, `control2`, `sprite_expand_y`, `memory`, `irq_status`, `irq_enable`, `sprite_priority`, `sprite_multicolor`, `sprite_expand_x`, `sprite_collision`, `sprite_bg_collision`, `border`, `background`, `background1`-`background3`, `sprite_mc0`, `sprite_mc1`, `sprite_color: [u8; 8]` |
| `sprite_xy: [u8; 16] @ $D000` | The sprite position registers as an array: x of sprite n at `2n`, y at `2n + 1` (the same memory as `vic.sprite0_x` ... `vic.sprite7_y`) |
| `set_kernal_irq(handler: u16)` | Install an `interrupt fn` with the KERNAL left in: `$0314` points to a library stub that pulls the A, X and Y the KERNAL's entry pushed (`pla` / `tay` / `pla` / `tax` / `pla`) and jumps to the handler, which then runs as if called through `$FFFE` (about 20 cycles more than the KERNAL entry alone). The KERNAL's own IRQ work (keyboard scan, clock) no longer runs, so `getin()` sees no new keys; the handler acknowledges its interrupt source, and a CIA timer interrupt that is left on reaches it too |
| `sid: Sid @ $D400` | SID registers: `voice1`-`voice3: Voice` (`freq: u16`, `pulse: u16`, `control`, `attack_decay`, `sustain_release`), `cutoff: u16`, `resonance`, `volume`, `paddle_x`, `paddle_y`, `osc3`, `env3` |
| `cia1: Cia @ $DC00`, `cia2: Cia @ $DD00` | CIA registers: `port_a`, `port_b`, `ddr_a`, `ddr_b`, `timer_a: u16`, `timer_b: u16`, `tod_tenths`, `tod_seconds`, `tod_minutes`, `tod_hours`, `serial`, `icr`, `control_a`, `control_b` |

**`io`**: C64 device I/O through the KERNAL (M14). Needs the KERNAL banked in (not after `kernal_off()`). Addresses are `ptr u8` (a plain number converts); counts are `u8`, so larger blocks take several calls. Results that are `bool` are true on failure (the KERNAL's carry).

| Name | Signature | Purpose |
|---|---|---|
| `setlfs`, `setnam`, `kernal_open`, `close`, `chkin`, `chkout`, `clrchn`, `chrin`, `chrout`, `readst` | routines at a fixed address (6.6) | The raw KERNAL routines (`kernal_open` is `OPEN`; `io.chrout` is the same routine as `c64.chrout`) |
| `kernal_load` | `(0 using A, address: ptr u8 using XY) -> bool using carry @ $FFD5` | Raw `LOAD`; `SAVE` has no raw form (it needs a zero-page pointer) |
| `load` | `(name: ptr u8, n: u8, device: u8, address: ptr u8) -> bool` | Load a whole file to `address` |
| `save` | `(name: ptr u8, n: u8, device: u8, first: ptr u8, last: ptr u8) -> bool` | Save `first` to `last`, `last` included (like `..=`); `last` must be below `$FFFF` (a KERNAL limit) |
| `open` | `(lfn: u8, device: u8, channel: u8, name: ptr u8, n: u8) -> bool` | Open a sequential file |
| `read` | `(lfn: u8, dst: ptr u8, max: u8) -> u8` | Read up to `max` bytes; stops at end of file (the last byte counts) or a timeout (counts nothing, e.g. a missing file) |
| `write` | `(lfn: u8, src: ptr u8, n: u8) -> bool` | Write `n` bytes; true on carry or any status bit |
| `status` | `() -> u8` | `READST`, inline; constants `ST_EOF`, `ST_TIMEOUT`, `ST_NO_DEVICE` |
| `command_open`, `command_close` | `(device: u8) -> bool`, `()` | Open the command channel (15) once, close it last: closing it closes every file on the drive |
| `command` | `(cmd: ptr u8, n: u8) -> bool` | Send a drive command (scratch, rename, ...) and a CR through the open channel |
| `drive_status` | `(dst: ptr u8, max: u8) -> u8` | Read the drive's status message without the CR; 0 if the channel is not open or the drive does not answer |
| `command_once` | `(device: u8, cmd: ptr u8, n: u8) -> bool` | Open, send, close; only when no file is open on the drive |

`c64.load` (8.5) is built on the same routines. Fast loaders are out of scope.

**C64 graphics (M11):**

- **`sprites`**, a sprite multiplexer: `module sprites(COUNT: u8 = 32, FLICKER: bool = false, SCREEN: u16 = $0400, SCREEN2: u16 = $0400)`, `COUNT` 8 to 64.
  - The sprite table is public parallel arrays, which main writes: `xpos: [u16; COUNT]` (0-511), `ypos`, `frame` (sprite pointer), `color`, `flags` (`VISIBLE`, `MULTICOLOR`, `EXPAND_X`; no expand-Y). Arrays rather than an array of structs, because the sort and the copy are asm, which cannot name an array of structs. `frames` counts frames.
  - `init()` sets up the multiplexer for a program with its own handler; `start()` calls `init()`, turns the KERNAL off and installs the module's own handler (raster interrupt at line 0). `update()` once per frame sorts by y (stable insertion sort, starting from the last order) and builds the next frame's display list (double-buffered; taken at the top of the next frame, never half built). `wait_frame()` waits for the next frame. Hardware sprite *i mod 8* shows sorted sprite *i*.
  - `interrupt fn raster() -> bool using carry` (true at the top of the frame) is the raster code, callable as a part from the program's own handler (6.3): `interrupt fn frame() { if sprites.raster() { scroll.top() } }`.
  - `SCREEN` and `SCREEN2` are the screens whose sprite pointers (`+ $3F8`) are written; two, so the pointers follow `scroll`'s two screens.
  - **Limits.** A sprite must start at least 30 lines below the sprite 8 places earlier in y order (the one using the same hardware sprite); otherwise it is skipped that frame, or, with `FLICKER`, the skipped sprites take turns. The first eight sprites are written in raster lines 0-14, so their y should be at least 24. A y above 251 is not multiplexed reliably (the interrupt would run past line 255). Up to 5 sprites starting on the same line are drawn whole.
- **`scroll`**, smooth character scrolling in eight directions: `module scroll(SCREEN_A: u16 = $8000, SCREEN_B: u16 = $8400, COLORS: bool = false)`. The two screens are 1 KB aligned, different, and in the same 16 KB VIC bank (asserts). The defaults are in bank 2, since a program starting at `$0801` leaves no room for two screens in bank 0.
  - `set_map(m: ptr u8, w: u16)` sets the character map and its width; `set_color_map(m: ptr u8)` the color map (`COLORS`). `draw(x: u16, y: u16)` shows the map from cell (x, y) and selects the VIC bank. `move(dx: i8, dy: i8)`, each -1 to 1, moves one pixel per call. `finish()` (`COLORS`) redraws the color RAM after a coarse step, called straight after `wait_frame()`. `set_fine(x, y)`, `start()` (KERNAL off, own handler), `wait_frame()`. `interrupt fn top()` writes the scroll registers at the top of the frame, as a part of the program's handler. Public: `cell_x`, `cell_y`, `fine_x`, `fine_y`, `frames`.
  - The screen is 38 columns by 24 rows. While the camera moves, the hidden screen is prepared as the shown one moved by one character, spread over the pixels before the coarse step, then the new column or row is filled from the map; at the coarse step the screens swap. A diagonal move first moves straight (up to 7 frames) until both axes reach their coarse step in the same frame. The camera must stay inside the map.
  - **Color RAM** is either fixed, or, with `COLORS`, redrawn from a color map by `finish()` (about 12,000 cycles), not shifted. Limit: with the multiplexer's interrupts the redraw can fall behind the beam, so lower rows may show old colors for one frame at a coarse step. A copy that races the beam is a candidate for a code generator (4.1).

Example: `c64.vic.border = c64.BLACK` is `lda #0` / `sta $D020`.

KERNAL routines that fit 6.1 are declared as routines at a fixed address (6.6), so calls go straight to the ROM. The others are `asm fn` wrappers.

## 11. Code shapes

These are the intended translations. The optimizer may produce something shorter, never longer.

| Source | Generated code |
|---|---|
| `x = 0` | `lda #0` / `sta x` (no code at the start of `main` when x is a global zeroed at startup, 4) |
| `x += 1` (x in memory) | `inc x` |
| `x += 2`, `x -= 2` (one-byte variable in memory, `u8` or `i8`, not `volatile`) | `inc x` / `inc x`, `dec x` / `dec x` (4 bytes against 7, the same cycles, A unchanged; a `volatile` variable gets the add, since two `inc`s write the value in between) |
| `w += 1` (u16) | `inc w` / `bne +` / `inc w+1` |
| `w += b as u16` (b: u8) | `lda b` / `clc` / `adc w` / `sta w` / `bcc +` / `inc w+1` |
| `p = q + e` (`q` a pointer or `u16`, `e` a `u8` expression such as `col << 1`) | `e` in A / `clc` / `adc q` / `sta p` / `lda q+1` / `adc #0` / `sta p+1`: an 8-bit add with the carry into the high byte, never a 16-bit temporary |
| `p = q + e + K` (as above, `K` the constant parts of the sum, written anywhere in it, e.g. `COLOR_RAM - SCREEN`; planned, not scheduled) | the constants added together at compile time, the order of the terms does not change the code; `K` with a low byte of 0 goes into the high-byte add (`adc #>K` in place of `adc #0`), otherwise `K` is added once at the end |
| `v += 2`, `v -= 2` (one-byte variable held in X or Y, e.g. a loop counter) | `inx` / `inx`, `dex` / `dex` (`iny`, `dey` for Y) |
| `p += 256`, `w -= 256` | `inc p+1`, `dec w+1` |
| `b = a[i + 1]` in `for i in 0..last` (last: u8) | `lda a+1,x` / `sta b` (3.2) |
| `a[i] = v` | `ldx i` / `lda v` / `sta a,x` |
| `p[i] = v` | `ldy i` / `lda v` / `sta (p),y` |
| `v = list[i].f` (`linear`, struct size 4) | `lda i` / `asl` / `asl` / `tax` / `lda list+f,x` / `sta v` |
| `v = list[i].f` (`indexed`) | `ldx i` / `ldy list.offsets,x` / `lda list+f,y` / `sta v` |
| `w = pts[i]` (`linear [u16; n]`) | `lda i` / `asl` / `tax` / `lda pts,x` / `sta w` / `lda pts+1,x` / `sta w+1` |
| `for i in 9..=0 step -1 using X` | `ldx #9` … `dex` / `bpl loop` (start ≤ 127; otherwise one extra `cpx #$FF`) |
| `for i in n..0 step -1 using X` | `ldx n` / `beq done` / … `dex` / `bne loop` |
| `for i in 0..n using X` | `ldx #0` / `cpx n` / `beq done` / … `inx` / `cpx n` / `bne loop` |
| `for i in 0..=255 using X` | `ldx #0` … `inx` / `bne loop` |
| `for _ in 0..10` | `ldx #10` … `dex` / `bne loop` |
| `for m in s..=255 step n using Y` (n a `u8` variable) | `ldy s` … `tya` / `clc` / `adc n` / `tay` / `bcc loop` |
| `for i in -10..10 using X` | `ldx #$F6` … `inx` / `cpx #10` / `bne loop` (as unsigned) |
| `for i in n..=0 step -1 using X` (n: `i8`) | `ldx n` / `bmi done` / … `dex` / `bpl loop` |
| `for i in n..0 step -1 using X` (n: `i8`) | `ldx n` / `beq done` / `bmi done` / … `dex` / `bne loop` |
| `for i in a..b using X` (a, b: `i8`), check before the loop | `lda a` / `sec` / `sbc b` / `bvc +` / `eor #$80` / `+ bpl done`, then the unsigned loop |
| `for i in a..b step 3 using X` (a, b: `i8`) | before the loop `lda b` / `eor #$80` / `sta e`; each pass `txa` / `clc` / `adc #3` / `bvs done` / `tax` / `eor #$80` / `cmp e` / `bcc loop` |
| `for i in 5..=5` | the body once, with `i` = 5 |
| `if a == 0 { }` | `lda a` / `bne skip` |
| `if a < b { }` unsigned | `lda a` / `cmp b` / `bcs skip` |
| `match` (compare chain) | the value is loaded into A once (`i8`: `eor #$80`, so signed order becomes unsigned order); per value `cmp #v` / `beq arm`; a range takes two compares, or one at either end of the byte; tests in source order, the last one inverted to branch to the default |
| `indexed match` | `sec` / `sbc #low` (if the lowest value is not 0; for an `i8` the lowest value is subtracted as a byte, `sbc #$FC` for -4, with no `eor #$80`, since `v - low` is the same byte either way), `tax` (`tay` if X holds a loop variable), `lda hi,x` / `pha` / `lda lo,x` / `pha` / `rts`; the tables hold each target minus 1, and values in the span with no pattern go to `_` or past the match. No range check (5.2) |
| `f(x)` with parameter `using A` | `lda x` / `jsr f` |
| `chrout(x)` with `fn chrout(...) @ $FFD2` | `lda x` / `jsr $FFD2` |
| `get_cursor()` with `fn get_cursor(true using carry) ... @ $FFF0` | `sec` / `jsr $FFF0` |
| `return true` with `-> bool using carry` | `sec` / `rts` |
| `-x` (u8) | `lda #0` / `sec` / `sbc x` |
| `x << 2` | `asl` / `asl` (on A or directly on memory) |
| `(w << 5) + (w << 3)` (w: u16, i.e. w × 40) | shifts and one 16-bit add, inline |
| `math.mul8(a, b)` | `ldx b` / `lda a` / `jsr math.mul8` (A is loaded last; result in A/X) |
| `i & 7` (i.e. i mod 8) | `lda i` / `and #7` |
| `if a < b { }` signed (`i8`) | `lda a` / `sec` / `sbc b` / `bvc +` / `eor #$80` / `+ bpl skip` |
| `if s < 0 { }`, `if s >= 0 { }` (`i8`, also `(d as i8) < 0`) | `lda s` / `bpl skip`, `lda s` / `bmi skip` (`i16`: the same on `s+1`) |
| `if w < v { }` (u16) | `lda w` / `cmp v` / `lda w+1` / `sbc v+1` / `bcs skip` |
| `if w == v { }` (u16) | `lda w` / `cmp v` / `bne skip` / `lda w+1` / `cmp v+1` / `bne skip` |
| `if x < 10 && y != 0 { }` | `lda x` / `cmp #10` / `bcs skip` / `lda y` / `beq skip` |
| `if x < 10 \|\| y != 0 { }` | `lda x` / `cmp #10` / `bcc body` / `lda y` / `beq skip` / `body:` |
| `t = x < y` (t: `bool` in memory) | `lda x` / `cmp y` / `bcs +` / `lda #1` / `bne ++` / `+ lda #0` / `++ sta t` |
| `s = a >> 1` (`i8`) | `lda a` / `cmp #$80` / `ror` / `sta s` |
| `w = v << 8` (u16) | `lda v` / `sta w+1` / `lda #0` / `sta w`; `<< k` for k from 9 to 15 adds `k - 8` `asl` on the high byte before its store |
| `w = v >> 8` (u16) | `lda v+1` / `sta w` / `lda #0` / `sta w+1`; `>> k` for k from 9 to 15 adds `k - 8` `lsr` on the low byte before its store (signed: the high byte is `$00` or `$FF` from the sign, and the shifts are `cmp #$80` / `ror`) |

## 12. Targets

### 12.1 CPU variants

| Variant | Status | Notes |
|---|---|---|
| `nmos6502` | v0 | All 151 documented opcodes |
| `ricoh2a03` | v0 (encoder/emulator) | NES CPU. Same opcodes; decimal mode has no effect |
| `wdc65c02` | Planned | Adds `stz`, `bra`, `phx`/`phy`, `(zp)` mode, etc. Needed for the Commander X16 |
| `nmos6502+undoc` | Planned, opt-in | Stable undocumented opcodes such as `lax`, `sax` |

### 12.2 Platforms

A platform description gives: CPU variant, free zero-page ranges, RAM ranges for variables, volatile areas (4), code start address, output file format, startup code, and the default character encoding (2.1), if it has one.

**The build names its target** (decision 2026-11-12): `tern --target c64 game.tern`, and the same for `tern run` and `tern bench`. Once a second platform exists (M25) the switch is required; until then every build is for the C64. The target decides the platform's library modules (2.2) and the default encoding.

**C64 (first platform)**

- CPU: `nmos6502`. Output: `.prg` with load address `$0801`.
- Startup: a BASIC line `10 SYS 2061`, then the startup code (zero globals, copy initialized data, call `main`).
- When `main` returns, the program stops in an endless loop (`jmp` to itself); interrupt handlers keep running. It does not return to BASIC, whose zero page the program has used.
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

**Atari 8-bit (M25; the rest of its description is settled in the M25 spec session)**

- One target, `--target atari`, for every Atari 8-bit with at least 48 KB: the 800, a 400 expanded to 48 KB, and every XL/XE model, with the 800's OS (revision A or B) or the XL/XE OS. A 64 KB XL/XE gives a program no more usable RAM than a 48 KB 800, since its extra 16 KB lies under the OS ROM, and Tern programs keep the OS.
- The library calls only the OS's documented entry points and vectors (CIO at `$E456`, `SETVBV` at `$E45C`, `XITVBV` at `$E462`, the vectors in page 2), the same on every model; never the XL OS's internal routines, which moved between versions.
- The platform's library never writes PORTB (`$D301`): on the 400/800 it reads joystick ports 3 and 4, on the XL/XE it switches BASIC, the OS ROM and the 130XE's banks. Anything that needs it is an XL/XE-only module.
- The startup code does not switch BASIC off. On the 400/800 BASIC is a cartridge left out; on an XL/XE the user (or the loader: DOS, Altirra) boots without it.
- **Interrupts need no language change** (6.3). A display-list interrupt is a plain `interrupt fn`: the OS's NMI code jumps through `VDSLST` with only the CPU's push. `atari.set_dli(handler)` writes `VDSLST` while DLIs are off, then sets `NMIEN` to `$C0`. For the vertical blank the OS pushes A, X and Y before jumping through its vector, so, as with the C64's `set_kernal_irq`, a library stub pulls them and jumps to the `interrupt fn`, whose `rti` then does what `jmp XITVBV` would. `atari.set_vbi(handler)` sets the deferred vector (`VVBLKD`) through `SETVBV`, after the OS's frame work (shadow registers, clock, attract); `atari.set_vbi_immediate(handler)` sets the immediate vector (`VVBLKI`), and the OS's frame work then does not run, so shadow registers are no longer copied to the chips. An immediate handler that continues into the OS (`jmp SYSVBV`) is written in assembly.
- Machines with 16 KB (an unexpanded 400, the 600XL) are reached through the `memory` block (8.4), e.g. `$2000..=$3BFF program`; there is no separate target.
- Not in M25: the 130XE's extra 64 KB (four 16 KB banks at `$4000–$7FFF`, selected through PORTB) and cartridge images, including the XEGS's bank-switched cartridges. Both follow the bank model of 14.1, after M27.

**Planned**, in this order: Atari 8-bit (`.xex`), Apple II (binary for DOS 3.3/ProDOS), NES (iNES, `ricoh2a03`, PRG/CHR banks), Commander X16 (`wdc65c02`). After them, the Commodore 128 in native mode (`.prg` at `$1C01`).

### 12.3 Programs without `main`

A main file with no `fn main()` and an `export` line builds a binary that another program loads and calls, such as a music player or a disk loader. It may be called by a Tern program or by code written any other way.

```
memory {
    $C000..=$CFFF  program
    $FB..=$FE      zeropage
}
export init, play, stop
```

- **Jump table.** The binary starts with a jump table at the beginning of its first `program` area: entry 0 is `jmp setup`, the compiler's startup code without the call to `main` (zero globals, copy initial values, `rts`); then one `jmp` per exported function in the order listed. The order is the binary's interface, so changing it is a visible change in the source. The host calls `setup` once, before any other entry.
- **Exported functions** are functions of the main file with register parameters only (as for `addr(f)`, 6.5), since the caller cannot write their frames. Return values follow 6.1.
- **One root.** `setup` and the exports count as one root (8.1): their frames may share memory, and they may share private variables. The host must call one entry at a time, never an entry from an interrupt while another entry is running. The compiler cannot check this; the developer guide documents it. An `interrupt fn` in the binary is a separate root as usual.
- **Output:** a `.prg` loading at the start of the first `program` area, with no BASIC line.
- **Interface module.** The compiler also writes `name_api.tern` (for `player.tern`, `player_api.tern`). It declares each entry as a routine at a fixed address (6.6) with a `keeps` list taken from the code, and constants for every range the binary uses: `CODE_FIRST`/`CODE_LAST` (the whole program file, gaps included), `VARS_FIRST`/`VARS_LAST`, `<REGION>_FIRST`/`_LAST`, and `ZP_FIRST`/`ZP_LAST` (one range from the lowest to the highest zero-page byte used). Ranges inside the file's span are not repeated. `setup` cannot be an export's name. A Tern host imports it and names those constants in its own `memory` block as `reserved` ranges, and leaves the binary's zero-page bytes out of its `zeropage` lines.
- `export` is a reserved word.

**Plug-ins against a fixed interface.** Use: a game that loads any number of levels with the same entries, including levels built later or outside Tern (an add-on disk). The interface is an ordinary hand-written module of routines at fixed addresses (6.6), `volatile` fixed-address variables for data every level provides (`volatile var header: LevelHeader @ $6009`; loading a level changes them) and constants for the slot's memory; each level is a binary (above) whose `memory` block uses those constants, and the game reserves the slot, loads a file, calls `setup`, then the entries.

- **`export` naming a module** (`export level_api`) exports one function per routine in that module, matched by name and placed at the routine's address, instead of in the order of the line. Errors: a routine with no function; different register parameters or return; a function that changes a register the routine's `keeps` promises; a `volatile` variable of the module with no `data` of the same name, type and address in the binary (a plain fixed variable is the host's and needs none); no routine `setup`, or `setup` with parameters, a return, or not at entry 0; routines less than 3 bytes apart (a `jmp`); a routine with a fixed input (6.6), which a function cannot receive. Gaps between routines are allowed and filled with zeros (`brk`), so a host that calls one stops at once. The module's other declarations (constants, structs, functions with a body, variables without a fixed address) are not checked. The binary's memory is limited by its own `memory` block, not checked against the interface.
- **The binary names its own `data`**, not the interface's variable at the same address: `level_api.header` in a level that defines `data header` is an error, "'level_api.header' is this binary's data 'header'. Use 'header'", so the bytes have one name.
- **`export` in a program with `main`**, with the table's address given: `export play_sound, spawn @ $0900`. The table holds one `jmp` per function in the order listed (no `setup` entry), and lies in a `program` or `reserved` area, like fixed `data` (8.4). Several `export` lines make one table in the order of the lines; exactly one of them gives the address. `setup` may be an export's name here; `main` may not. The compiler writes `name_api.tern` for plug-in authors, with the entries and their `keeps` but no memory constants: the plug-in's memory is its slot, set by the interface module, not the program's ranges. The table stays valid across versions of the program while its address and order stay the same; new entries go at the end. The entries are called from the plug-in, so they count as an unknown root, as functions whose address is taken (6.5).

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
- **Errors for habits from other languages.** Where a C habit does not parse, the error names the habit and gives the Tern spelling, instead of a generic parse error at a later token: `n++` / `n--` at the end of a statement or before a closing bracket ("Write 'n += 1'"; `a++b` stays `a + (+b)`), a `*` dereference such as `*p = 1` ("Write 'p[0]'"), `%0000_1111` where a value is expected ("Write binary numbers as 0b0000_1111"), and a keyword used as a name, such as `fn match()`: the error says what was expected and names the keyword ("Expected function name, found 'match', which is a keyword. Use another name").
- **Several errors per compile.** One run reports every error it finds, sorted by place: the main file first, then each module in the order the compiler first loads it, depth first (a module's own imports come right after it, before the next import of the file that imports it: with `import a` and `import b` in the main file and `import c` in `a`, the order is main, a, c, b); within a file by line and column (errors at the same place in the order found). `--max-errors n` reports the first `n` of that list, and the output then says that more were not reported. After a syntax error the parser goes on at the next statement at the same brace depth (at the top level, the next declaration). The checker reports at most one error per statement or declaration, then goes on. A name whose declaration has an error is still declared, with a type that accepts every use, so its uses add no errors of their own: `var e: Enmy` is one error, not one per use of `e`. A name that is not declared at all is reported at every use, since each use is a place to fix: with `fn updte()` declared, three calls to `update()` are three errors. A module used without its import is such a name too: two uses of `text.put` with no `import text` are two errors, each suggesting the import. The call graph (recursion, private variables used from two roots), allocation, code generation and layout run only when the checks found no error, since they need the whole program; their errors (recursion, a private variable used from two roots, overlaps, a branch out of range) are reported one at a time. Text output ends with the number of errors ("1 error", "3 errors"); `tern --json` lists them in `diagnostics`, in the same order.
- **Warnings** are given only for code that does nothing: a declaration that is never used, or a statement that never runs. Never for what code does when it runs: a wrapping index, a flag loop, a loop whose variables decide whether it runs, an empty constant range (`for i in 0..COUNT` with `COUNT` 0) or a constant condition (`if FLICKER`) gets none, since a module parameter turning an option off is the usual cause (decisions 2026-11-07, 2026-11-12). The warnings:
  - **An unused local variable**: a local, a local `const`, a `for` variable or a `const fn`'s local that is never read. One that is only written counts as unused; a compound assignment (`x += 1`) is a write, since nothing reads its result. "'temp' is never used. Remove it, or start its name with '_' to keep it"; for a `for` variable, "Write '_' as the loop variable".
  - **An unused declaration**: a function, variable, constant, `data`, struct or encoding that is `private` in a module, or declared at the top level of the main file (which no file can import), and named nowhere in the program, in Tern code or as an assembly operand. Not warned: `main`; a global variable at a fixed address (`@ zp`, `@ $C000`, `@ region`), in the main file or `private` in a module, which may reserve memory on purpose (2.2); exports (12.3); parameters, struct fields and module parameters; and declarations of modules that are not `private`, which are the module's interface. A use counts even in a function that is itself unused, so removing an unused function can bring warnings for what only it used, at the next compile.
  - **An unused import**: a file that imports a module and names nothing of it, in Tern code or assembly. An import that sets module parameters (`import sprites(COUNT = 40)`) is not warned, since other modules use the values. "'text' is imported but not used. Remove the import".
  - **An unreachable statement**: a statement after one that never finishes: `return`, `break`, `continue`; a `while true` with no `break` of its own (a `break` of an inner loop does not count); an `if` with an `else`, or a `match` with `_`, whose every branch never finishes. Conditions are not evaluated, so `if DEBUG { return }` finishes, and an `asm` block and a call always finish. One warning per block, at the first such statement: "This statement is never reached, since the 'return' on line 12 ends the function. Remove it".
  - **A name that starts with `_`** (`var _spare: u8`, `fn _dump()`) is never warned as unused; it is otherwise an ordinary name. `_` alone keeps its meaning (5.1, 5.2).
  - **Library modules** (found in the compiler's `lib/` folders, 2.2) are never warned about, since the program cannot change them. The program's own modules are.
  - Warnings are reported only when the checks found no error, since a statement left unchecked after an error could make a used name look unused. They are sorted by place with any later error (allocation, layout). Text output writes `warning:` where an error writes `error:`, and ends with both counts ("2 warnings", "1 error, 2 warnings"). `tern --json` gives them in `diagnostics` with `"severity": "warning"`, and `ok` stays true when there are only warnings. Warnings do not stop the build. `--max-errors` counts errors only.
  - `--no-warnings` turns them off; `--warnings-as-errors` reports each as an error (it then stops the build and counts as one). Giving both is an error. `tern run` reports warnings and runs the program; `tern bench` does not report them, since compared snippets often leave values unused on purpose.
- **`tern run` loads overlays.** The emulator has no KERNAL, but SETNAM, SETLFS and LOAD have stand-ins (as CHROUT, GETIN and PLOT do) that load the build's own overlay files: the file name must be the module's name in capitals (`"LEVEL1"` loads `level1.prg`). Any other name fails with carry set, as a missing file does, so a program that names its files otherwise is checked in VICE. Secondary address 0 loads to the address in X/Y; otherwise the file loads to its own address.
- **`step` is a keyword only right after the range of a `for`** (5.1). Elsewhere it is an ordinary name: `fn step()`, `var step: u8`, and `for i in 0..step step 2` all work. This is unambiguous, since a name cannot continue an expression that is already complete. **`emit` is a keyword only at the start of a statement in a `const fn`, before `{`** (4.1); elsewhere it is a name (`lib/c64/sprites.tern` has a function `emit`). Other keywords stay reserved everywhere.
- **Branch range fixing.** A conditional branch reaches only −128 to +127 bytes. Layout repeats until every branch fits. Branches that do not fit become an inverted branch over a `jmp`.
- **Outputs:**
  - the program file (`.prg` for C64),
  - a listing (`.lst`) with source lines, addresses, bytes and cycles,
  - a VICE label file (`.vs`),
  - a report: bytes and cycle estimates per function, zero-page map, overlay map, and the functions with memory reachable from two roots (8.1).

### 13.1 Project layout (C#)

| Project | Contents |
|---|---|
| `Tern.Cpu` | Instruction set table, encoder, 6502 emulator with cycle counting |
| `Tern.Compiler` | Front end, allocation, code generation, optimizer, layout, agent tools (`tern --json`, `tern run`, `tern bench`) |
| `Tern.Cli` | Command-line tool: `tern file.tern` writes `.prg`, `.lst` and a VICE label file |
| `Tern.Cpu.Tests` | Encoder and emulator tests, including Klaus Dormann's functional test |

### 13.2 Testing approach

- Every language feature has test programs that run in the built-in emulator. Tests check results in memory and record bytes and cycles.
- Benchmarks compare the same task in Tern, hand-written assembly, and other compilers (cc65, Prog8). A size or speed regression fails the build.

## 14. Open questions

Each item is decided when the feature it belongs to is built.

1. **Bank switching** (NES mappers, C64 cartridges, X16 and C128 RAM banks). Direction settled 2026-11-12: banks follow the overlay model (8.5). A module is placed in a bank on its import; there is no `@ bank(n)` on single functions or `data`. A call into a bank is a plain `jsr`; the program selects the bank first through a platform library function (e.g. `nes.bank(3)`, which writes the mapper and a shadow byte), and the compiler does not check which bank is selected. The compiler generates no stubs that switch banks around a call. Calls and data uses between two banks of the same area are errors. Calls chosen at run time go through an interface (`implements`, 8.5). A ROM bank's global variables go in resident RAM. Left to the NES spec session: the syntax of bank areas in the `memory` block and in the import, the mappers, and the iNES layout; RAM banks are left to the X16 and C128 sessions. Possible later: `tern run` reports a `jsr` into a bank area whose selected bank does not hold the called function.
2. **Functions called from both main and interrupts.** Settled 2026-11-12: allowed, with one frame shared by both roots (8.1); the compiler makes no copy per root and gives no error. Keeping an interrupt from running the function while main is inside it is the program's responsibility; the developer guide explains the risk and the ways to avoid it.
3. **`pha`/`pla` for temporaries.** Settled 2026-11-12: an optimization, not a language rule. `pha`/`pla` is 2 bytes and 7 cycles, against 4 and 6 for a zero-page temporary and 6 and 8 for an absolute one. The optimizer may keep a temporary on the stack when it is saved from A and restored into A, with no `jsr` and no `asm` block between, and the temporary would otherwise be outside zero page; a zero-page temporary stays, being 1 cycle faster. A temporary used as an operand (`adc tmp`) cannot move to the stack. A stack temporary belongs to its call, so an interrupt cannot overwrite it (8.1). Built only if counting the pattern in the benchmarks and examples shows it is worth it.
4. **Checks in debug builds.** Settled 2026-11-07: there is no check in the program. In test runs the built-in emulator reports a variable array index outside its array, with the source line, as a debugging aid (3.2); the program behaves the same with or without it. The run goes on after a bad index, so one run reports every bad access (counted per instruction). An `indexed match` value outside its span (5.2) is reported too and stops the run, since its jump lands on whatever bytes follow the tables. Arithmetic is not checked: it wraps by definition (3.1), and wrapping is often intended.
5. **User-defined encodings.** Settled 2026-11-12: the `encoding` declaration and `default encoding` line (2.1).
6. **`using` on local variables** (`var count: u8 using Y`). Not planned (2026-11-07). Loop counters and register parameters already go in registers, the optimizer removes reloads, and A, X and Y are needed by most expressions and indexing, so a local held in one would mostly produce errors; a hidden save and reload would break rule 1. The M17 analysis found no gap that needs it (its class 3 is empty): values held in registers across loops are an optimizer change (M18).
7. **A general decorator syntax.** Settled 2026-11-12: none. Modifiers are keywords in front of the declaration (`private inline fn`), or words only in their place (13). Reconsidered only if a modifier needs arguments.
8. **Language name.** "Tern" is kept for now (2026-11-02). Rejected: "Moss"/"Mossy" (after MOS Technology), since "Moss" is already the name of several small languages and of Stanford's MOSS plagiarism checker.
9. **`data` at a fixed address.** Settled 2026-11-02: section 8.4.
10. **Fixed-address RAM that is not volatile.** Settled 2026-11-02: platform volatile areas plus a `volatile` keyword (section 4).
11. **Arrays of structs as struct fields.** Settled 2026-11-12: section 3.3 (array fields). Built after the first release, or when a program needs it.
12. **Memory layout declaration.** Settled 2026-11-02: sections 8.4 (memory layout), 8.5 (overlays) and 12.3 (programs without `main`).
