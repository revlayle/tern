# 4. Compile-time code

[Guide index](README.md) · previous: [3. Memory](03-memory.md) · next: [5. Standard library](05-library.md)

## 4.1 `const fn`

A `const fn` is a function the compiler runs while it compiles. Only its result goes into the program; the function never becomes 6502 code. Use it to build lookup tables, and, as a *generator*, to write assembly whose shape depends on parameters ([4.3](#43-generators)). From `examples/c64/sine.tern`:

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

- `int`, a signed 64-bit whole number. It never wraps: going outside the range is an error. Write `& $FF` where you want 8-bit behavior ([10.18](10-behavior.md#1018-compile-time-int-never-wraps)).
- `real`, a 64-bit double. `1.5`, `0.25` and `2e3` are real literals (`0..25` is still a range). Functions: `sin`, `cos`, `tan`, `atan2`, `sqrt`, `pow`, `exp`, `log`, `log2`, `abs`, `min`, `max` (angles in radians), and the constants `PI` and `TAU`. Division by zero, and `sqrt` or `log` of a negative number, are errors, so no NaN reaches a table.
- `bool`, arrays of these, and `label` (for generators, below).

A runtime type (`u8`, a pointer, a struct) in a `const fn` is an error; arguments widen into `int`, so a `const COUNT: u8` passes in as an `int`. A value is checked where it leaves compile time: each element must fit its table's type, as a constant must fit ([2.4](02-language.md#24-constants)).

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

Floats for the program to compute with at runtime are the `float` library; `float.of(9.81)` is a `const fn` that gives a constant's four bytes ([5.6](05-library.md#56-float)).

## 4.2 Where it runs and what it reads

**Where it runs.** Compile-time code runs in declarations only: a `const` (also one inside a function), `data`, and the initial value of a global `var`. It never runs in statements, so a `const fn` call, a list built with `for`, `repeat` or a real number in runtime code is an error. In an `asm` block too: `lda #round(1.5)` is an error; declare a constant and use its name.

**What it reads:** constants, module parameters, `sizeof`, `offset`, and `data` tables element by element, including `include_bytes` tables, so one table can be built from another or from a file. Not a `var`, whose value is known only at runtime, and not `addr()`, since addresses are decided after compile-time code runs (generators can still use them, below).

- A `const fn` may live in a module and be shared between programs: `import tables`, then `data sine: [u8; _] = tables.sine(256)`. The Atari library builds display lists this way: `atari.display_list(...)` ([Atari](07-atari.md#73-display-lists)).
- A `const fn` may call itself, up to 200 calls deep. Runtime code has no recursion only because the 6502 has no room for stack frames; a `const fn` never runs there.
- Each declaration may take 10,000,000 steps (a statement or a loop pass). A loop that never ends is an error naming the function, not a hang.
- An error inside a `const fn` is reported at its line, with "(called from main.tern:5)", the declaration that ran it. `tern --json` gives that place as `calledFrom`.
- `assert condition, "message"` in a `const fn` stops the compile with that message, for arguments that make no sense.
- Structs and strings are not compile-time values, so a `const fn` cannot build a table of struct literals.

## 4.3 Generators

A `const fn` with no return value may `emit` instructions. It is called on a line of its own in an `asm` block or `asm fn`, and what it emits goes there, so loops, `if`s and parameters decide the code's shape: unrolled copies, plotters, sprite clearing. A shortened version of `examples/c64/speedcode.tern`:

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

When to unroll with a generator, and what it costs, is in [11.9](11-fast.md#119-unroll-a-per-frame-loop-with-a-generator).
