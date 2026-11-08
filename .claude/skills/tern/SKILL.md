---
name: tern
description: Writing, checking and measuring programs in Tern, a small language that compiles to 6502 machine code for the Commodore 64 (files ending in .tern). The syntax and the restrictions a model would not guess (no recursion, no runtime multiply, arrays of at most 256, u8 indexes, `using` registers, roots and shared memory, flags as values), what each construct costs, how to write fast Tern, common compile errors and their fixes, inline assembly, and the compiler's tools - `tern --json` for errors, `tern run` to run a program in the built-in emulator and dump memory or the screen, `tern bench` to compare the bytes and cycles of two ways of writing the same code. Use when writing or changing .tern code, when a Tern program does not compile, or when deciding which way of writing Tern code is faster.
---

# Tern

Tern is a small language for the 6502 that compiles to compact, predictable machine code. The first
target is the Commodore 64. You have not seen Tern code before: it looks a little like Rust and Go,
but do not guess from them. Write what the examples here show, compile often, and read the errors,
which say how to fix the problem.

Every example in this skill compiles, and every tool output shown is what the tools print, checked
by the compiler's test suite (`SkillTests`).

## Files of this skill

| File | Read it when |
|---|---|
| `SKILL.md` (this file) | always: the tools, a first program, the rules you would not guess |
| `language.md` | writing Tern: types, declarations, arrays, structs, pointers, control flow, functions, interrupts, modules, `asm` |
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
| `tern --json game.tern` | the same, with the result and any error as JSON: `file`, `line`, `column`, `message`, `suggestion` |
| `tern run game.tern` | compile and run in the built-in emulator until `main` returns; `--dump name` shows a variable, `--screen` the screen as text, `--profile` the cycles of each function and source line |
| `tern bench a.tern b.tern` | run each version and compare bytes and cycles; `--fn f` measures one function; `--code '...'` measures statements as the body of `main`, after `--decl '...'` lines |

Work in this loop: write a small piece, compile, `tern run` it with `--dump` to check the values,
and `tern bench` two versions when speed matters. Quote `--code`, `--decl` and `--dump` arguments
with single quotes: `$C000` in double quotes is a shell variable.

The emulator is a 6502 with plain RAM: there is no VIC-II, SID or CIA, so a hardware register reads
back what was last written (the joystick reads 0, which means every direction pressed) and a wait
for a raster line never ends. CHROUT, GETIN and PLOT work (printing goes to the screen at `$0400`);
any other KERNAL call stops the run. Keep game logic in functions that take their inputs as
parameters, so that it can be run and measured (`sprite.md` shows how). Check the finished program
in VICE: `x64sc -autostart game.prg`.

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
  bytes); an array of structs as one table per field. Indexes are not checked.
- **All arithmetic wraps, with no promotion:** `u8 + u8` is a `u8`. `u8` with `u16` widens. Narrowing
  is `as` (`big as u8` keeps the low byte). Mixing signed and unsigned is an error: cast one side.
- **No `*p`:** read through a pointer with `p[0]` or `p[i]` (compiled to `lda (p),y`); a pointer to a
  struct reads fields with `p.field`. `p + 1` is the next byte. Any pointer converts to any other with
  `as`.
- **Structs are never copied, passed, returned or compared.** Pass a pointer or an index.
- **`using` puts a value in a register or flag:** `fn plot(x: u8 using X)`, `for i in 0..8 using Y`,
  `-> bool using carry`. A plain parameter is fixed memory the caller stores into.
- **A function reached from `main` and from an interrupt handler** (two roots) is an error unless it
  has no memory of its own (no memory parameters, locals or temporaries). A global used by both
  becomes volatile.
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
