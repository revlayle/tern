---
name: tern
description: Writing, checking and measuring programs in Tern, a small language that compiles to 6502 machine code for the Commodore 64 (files ending in .tern). The syntax and the restrictions a model would not guess (no recursion, no runtime multiply, arrays of at most 256, u8 indexes, `using` registers, roots and shared memory, flags as values), what each construct costs, how to write fast Tern, common compile errors and their fixes, inline assembly, and the compiler's tools - `tern --json` for errors, `tern run` to run a program in the built-in emulator and dump memory or the screen, `tern bench` to compare the bytes and cycles of two ways of writing the same code. Use when writing or changing .tern code, when a Tern program does not compile, or when deciding which way of writing Tern code is faster.
---

# Tern

Tern is a small language for the 6502 that compiles to compact, predictable machine code. The first
target is the Commodore 64. You have not seen Tern code before: it looks a little like Rust and Go,
but do not guess from them. Write what the examples here show, compile often, and read the errors,
which say how to fix the problem.

Every example in this skill compiles (or, where marked, gives the error shown), and every tool
output shown is what the tools print, checked by the compiler's test suite (`SkillTests`).

## Files of this skill

| File | Read it when |
|---|---|
| `SKILL.md` (this file) | always: the tools, a first program, the rules you would not guess |
| `language.md` | writing Tern: types, declarations, arrays, structs, pointers, control flow, functions, interrupts, modules, `asm`, compile-time tables and generators (`const fn`, `emit`) |
| `costs.md` | the code each construct compiles to, and how to write fast Tern (measured) |
| `errors.md` | a compile error you do not understand: the common ones, with fixes |
| `sprite.md` | a whole program worked through: a sprite moved by the joystick, checked without a C64 |

## 1. The tools

`tern` is the compiler. In the Tern repository it is `dotnet run --project src/Tern.Cli --`; after
`dotnet build`, `src/Tern.Cli/bin/Debug/net10.0/tern` starts faster. Elsewhere, ask the user where
the compiler is. The standard library is in `lib/` next to it.

| Command | What it does |
|---|---|
| `tern game.tern` | compile: writes `game.prg`, `game.lst` (listing: every source line with its addresses, bytes and cycles), `game.vs` (VICE labels), `game.lines` (bytes and cycles per source line) and prints the memory report |
| `tern --json game.tern` | the same, with the result and every error and warning as JSON: `file`, `line`, `column`, `severity`, `message`, `suggestion` |
| `tern run game.tern` | compile and run in the built-in emulator until `main` returns; `--dump name` shows a variable, `--screen` the screen as text, `--profile` the cycles of each function and source line; `--set '$DC00=$7B'` writes a byte before the run |
| `tern bench a.tern b.tern` | run each version and compare bytes and cycles; `--fn f` measures one function (a module's function with its module: `--fn ship.move`); `--code '...'` measures statements as the body of `main`, after `--decl '...'` lines |

Work in this loop: write a small piece, compile, `tern run` it with `--dump` to check the values,
and `tern bench` two versions when speed matters. Quote `--code`, `--decl`, `--dump` and `--set` arguments
with single quotes: `$C000` in double quotes is a shell variable.

The emulator is a 6502 with plain RAM: there is no VIC-II, SID or CIA, so a hardware register reads
back what was last written (the joystick reads 0, which means every direction pressed, unless
`--set '$DC00=$FF'` gives it a value) and a wait for a raster line never ends. CHROUT, GETIN and PLOT work (printing goes to the screen at `$0400`);
any other KERNAL call stops the run. Keep game logic in functions that take their inputs as
parameters, so that it can be run and measured (`sprite.md` shows how). Check the finished program
in VICE: `x64sc -autostart game.prg`.

The program never checks an array index, but `tern run` does: an index outside its array, and an
`indexed match` value outside its patterns (which also stops the run), are printed with the source
line, and the exit code is 3 (2 when an `indexed match` value stopped the run). `tern bench` prints
them too, and a variant with any fails. Pointer accesses and `asm` are not checked.

```tern
// bad.tern
var hits: [u8; 8]
var last: u8 = 9

fn main() {
    for i in 0..last {
        hits[i] = 1
    }
}
```

```console
$ tern run bad.tern
Main returned: 249 cycles (153 in main)
...
Index check (the program has no check; the emulator found these):
  bad.tern:7: Index 8 is outside 'hits', which has 8 elements
      hits[i] = 1
```

## 2. A first program

```tern
// count.tern
import c64
import text

data title = screen"SUM OF 1..100:"

var total: u16

fn main() {
    for i in 1..=100 {
        total += i as u16
    }
    text.put(c64.SCREEN, addr(title), len(title))
    text.dec16(c64.SCREEN + 15, total)
}
```

```console
$ tern run count.tern --dump total --screen
Main returned: 3125 cycles (3109 in main)
A=$04 X=$00 Y=$03 SP=$FF PC=$0817
total $0002 u16 = 5050 ($13BA)
Screen ($0400, 40 x 25):
  |SUM OF 1..100: 5050                     |
...
```

- `import` loads a library module; its names are always used with the module name (`c64.SCREEN`).
- `data` is read-only bytes in the program; `screen"..."` gives screen codes, for writing straight to
  screen memory. `addr(x)` is a pointer to `x`, `len(x)` its length.
- Globals without an initial value are zeroed at startup.
- `i as u16` widens: `total += i` with `i: u8` and `total: u16` also works (widening is automatic),
  but there is no automatic narrowing.

An error gives the file, line and column, what is wrong and how to fix it. One compile reports
every error it finds, sorted by file (the main file first), line and column, then their number,
so fix them all before compiling again (`--max-errors n` reports the first `n`). A name whose
declaration has an error adds no errors where it is used. Errors found when the program is laid out (zero page full, recursion, an
overlap) come one at a time, once there are no others. With `--json` each error is an entry in
`diagnostics`, the fix is a separate `suggestion`, and for an error inside a `const fn` or
generated code, `calledFrom` is the line that called it (`null` otherwise):

```tern error
// broken.tern
var total: u16
var small: u8

fn main() {
    small = total
    total = true
}
```

```text
broken.tern:6:13: error: Expected u8, found u16. Convert with 'as u8'
broken.tern:7:13: error: Expected u16, found bool
2 errors
```

```console
$ tern --json broken.tern
{
  "ok": false,
  "files": [],
  "report": [],
  "diagnostics": [
    {
      "file": "broken.tern",
      "line": 6,
      "column": 13,
      "severity": "error",
      "message": "Expected u8, found u16.",
      "suggestion": "Convert with 'as u8'",
      "calledFrom": null
    },
    {
      "file": "broken.tern",
      "line": 7,
      "column": 13,
      "severity": "error",
      "message": "Expected u16, found bool",
      "suggestion": null,
      "calledFrom": null
    }
  ]
}
```

Warnings are for code that does nothing; they never stop the build:

```tern warning
// tidy.tern
import text

var score: u8

fn old_draw() { }

fn main() {
    var temp: u8 = 0
    for i in 0..3 { score += 1 }
    return
    score = 0
}
```

```text
tidy.tern:2:8: warning: 'text' is imported but not used. Remove the import
tidy.tern:6:1: warning: 'old_draw' is never used. Remove it, or start its name with '_' to keep it
tidy.tern:9:5: warning: 'temp' is never used. Remove it, or start its name with '_' to keep it
tidy.tern:10:5: warning: 'i' is never used. Write '_' as the loop variable
tidy.tern:12:5: warning: This statement is never reached, since the 'return' on line 11 ends the function. Remove it
5 warnings
...
```

- **Unused local**: a local, `const` or `for` variable that is never read. One that is only
  written (`x = 1`, `x += 1`, `buf[i] = 0`) counts as unused.
- **Unused declaration**: a function, global, constant, `data`, struct or `const fn` of the main
  file, or a `private` one of a module, named nowhere in the program (Tern code or assembly). A
  use inside an unused function counts, so removing one function can bring a warning for what only
  it used. `main`, exports, globals at a fixed address (`@ $D020`, `@ zp`), parameters, fields and
  public module declarations are never warned, nor is anything in the library.
- **Unused import**: a file imports a module and names nothing of it. An import that sets module
  parameters (`import sprites(COUNT = 40)`) is not warned.
- **Unreachable statement**: one after `return`, `break`, `continue`, a `while true` with no `break`
  of its own, or an `if`/`else` (a `match` with `_`) none of whose branches finishes. Conditions are
  not evaluated: `if DEBUG { return }` finishes, and so does an `asm` block. One warning per block.

A name starting with `_` (`var _spare: u8`, `fn _dump()`) is kept on purpose and never warned. Warnings
come only when there are no check errors, listed by place with any later error ("1 error, 2
warnings"); `--max-errors` counts errors only. With `--json` a warning has `"severity": "warning"`,
and `ok` stays true. `--no-warnings` turns them off, `--warnings-as-errors` makes each an error that
stops the build. `tern run` reports them; `tern bench` does not.

The report printed after a compile also lists the functions with memory reachable from two roots,
with their roots: information, not a warning (see "A function reached from `main` and from an
interrupt handler" in section 3).

Where the cycles go: `--profile` lists each function, and each source line with its bytes, the
cycles of its instructions once each (`+` when a branch or page crossing can add more), how often it
ran and the cycles it took in the run. The same table without the run counts is in `count.lines`.

```console
$ tern run count.tern --profile
Main returned: 3125 cycles (3109 in main)
A=$04 X=$00 Y=$03 SP=$FF PC=$0817
Functions (cycles include the functions they call):
  main                            1 calls       3109 cycles
  text.dec16                      1 calls        689 cycles
  text.put                        1 calls        278 cycles
; Per source line: bytes, the cycles of its instructions once each (+: a branch or page crossing adds more), how often its first instruction ran, and the cycles they took in the run.
; file        line bytes  cycles     runs run cycles  addresses                source
  count.tern     9     1       6        1          6  $0855                    fn main() {
  count.tern    10     7      8+        1        701  $081A-$081B $0826-$082A  for i in 1..=100 {
  count.tern    11    10     17+      100       1376  $081C-$0825              total += i as u16
  count.tern    13    23      31        1         31  $082B-$0841              text.put(c64.SCREEN, addr(title), len(title))
  count.tern    14    19      28        1         28  $0842-$0854              text.dec16(c64.SCREEN + 15, total)
...
                 -    13      19        1         16  $080D-$0819              (startup)
; total 3125 cycles
```

## 3. Rules you would not guess

- **No runtime `*`, `/` or `%`.** They work only on constants (`const SIZE = 40 * 25`). At runtime
  write shifts and adds (`(w << 5) + (w << 3)` is `w * 40`), use a table, or call `math.mul8`,
  `math.div8`, `math.mul16`, `math.div16`. Shifts take a constant count. Masking: `x & 7` for "mod 8".
- **No recursion**, ever: a cycle in the call graph is an error. Use an array as an explicit stack.
- **Every variable has a fixed address.** There are no stack frames: functions that never run at the
  same time share memory. So a local (other than an array) needs an initial value, and a function's
  parameters cannot be read from outside it.
- **Arrays have at most 256 elements and `u8` indexes**, compiled to `lda a,x`. Longer byte buffers
  exist but are reached through a pointer. An array of `u16` is stored as two tables (low and high
  bytes); an array of structs as one table per field. The program never checks an index; `tern run`
  reports one outside its array.
- **All arithmetic wraps, with no promotion:** `u8 + u8` is a `u8`. `u8` with `u16` widens. Narrowing
  is `as` (`big as u8` keeps the low byte). Mixing signed and unsigned is an error: cast one side.
- **No `*p`:** read through a pointer with `p[0]` or `p[i]` (compiled to `lda (p),y`); a pointer to a
  struct reads fields with `p.field`. `p + 1` is the next byte. Any pointer converts to any other with
  `as`.
- **Structs are never copied, passed, returned or compared.** Pass a pointer or an index.
- **`using` puts a value in a register or flag:** `fn plot(x: u8 using X)`, `for i in 0..8 using Y`,
  `-> bool using carry`. A plain parameter is fixed memory the caller stores into.
- **A function reached from `main` and from an interrupt handler** (two roots) compiles, with one
  frame for both: if the interrupt lands while main is inside it, main's call goes on with the
  handler's values. Call it in main with interrupts off, make it an `inline fn`, give it no memory
  of its own, or write a second one for the handler. The report lists such functions under
  "Functions with memory reachable from two roots". A global used by both becomes volatile. A
  module's `private` variable used from both is an error: `math.mul8`'s work bytes and `random`'s
  state are private, so a handler cannot call them while main does.
- **Inside `for ... using X`, only routines that keep X may be called** (`keeps X`); likewise Y.
- **A `bool` can live in a flag.** `-> bool using carry` returns in the carry; an `inline asm fn`
  with a flag result tests a flag its instructions set, with no call. After compiled code the flags
  are unspecified: `cpu.carry()` and friends are only for right after an `asm` block.
- **`match` is a chain of compares; `indexed match` is a jump table that does not check its value.**
  An `indexed match` must list every value that can occur (or mask it: `indexed match op & 3`).
- **Memory is ordinary unless it is volatile.** I/O registers are volatile by themselves; RAM that
  something else changes needs `volatile var`. The optimizer may keep ordinary memory in registers.
- **Only what is reached is compiled.** An `if` on a constant compiles only the branch taken, and an
  unused import costs nothing.
- Statements end at a newline (`;` is optional). Comments are `//` and `/* */`. Inside `asm` blocks
  `;` is an error, not a comment.

## 4. Writing it well

- Read `costs.md` before writing code that runs every frame: arrays before pointers, store a result
  before testing it, leave loops with `break`, `inline fn` for small functions in hot loops, `using`
  only where the body needs the register.
- Look at the listing (`.lst`) or the `.lines` table to see what a line costs; `tern run --profile`
  shows where the cycles go.
- When a rule in `costs.md` matters for your code, measure it with `tern bench` on your code: the
  rules describe this compiler, and the optimizer changes.
- Use `asm` only where Tern cannot say what you need (a flag test, a cycle-exact raster routine, a
  KERNAL call with an unusual interface), or where `tern bench` shows the Tern version is too slow.
  `language.md` section 10 has the rules.
