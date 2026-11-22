# 10. How Tern programs behave

[Guide index](README.md) · previous: [9. NES](09-nes.md) · next: [11. Writing fast Tern](11-fast.md)

This is the developer guide: what the machine does with your code where a 6502 programmer might expect something else, or might rely on it. Tern trusts you and adds no hidden checks. None of these behaviors is warned about, because each is often exactly what you want; this chapter says how each one works so that none of them is a surprise.

## 10.1 Everything wraps

All arithmetic wraps at the width of its type, with no error and no flag you can test afterwards: the 6502's own behavior, and the cheapest.

```
var buf: [u8; 256]
var head: u8 = 255
var w: u16 = $FFFF
var s: i8 = 127

fn main() {
    buf[head + 1] = 7      // head + 1 is a u8: 0, so this is buf[0]
    w += 1                 // 0
    s += 1                 // -128
}
```

- **Index expressions wrap too.** `buf[head + 1]` computes `head + 1` as a `u8`, so a 256-element ring buffer needs no `& $FF`. The same holds for `a[i - 1]` with `i = 0`: it is `a[255]`.
- **When the optimizer folds an offset into the address.** `a[i + 1]` can be `lda a+1,x`, with no add, but only where that gives the same result as wrapping: when the bounds of a `for` loop prove that `i + 1` does not pass 255 (`for i in 0..last` with `last: u8` gives `i <= 254`), or when a wrapped index would be outside the array anyway. The proof comes from the loop header only, never from other lines, so the cost of an access can be read from the loop it is in. Otherwise the index is computed first (`lda i` / `clc` / `adc #1` / `tax`).
- **No promotion.** `u8 + u8` is a `u8` even when it is stored in a `u16`: `var t: u16 = x + y` adds in 8 bits, then widens. Widen one side first to add in 16 bits: `var t: u16 = (x as u16) + y`.
- **Constants do not wrap** (10.18): `const B: u8 = 200 + 100` is an error, not 44.

## 10.2 Indexes are not checked

An index past the end of an array reads or writes whatever is there, as in C. The program never checks it, and the optimizer assumes it does not happen: it treats `a[i] = v` as changing only `a`, so a value of another variable it holds in a register is not reloaded after such a write.

```
var a: [u8; 4]
var after: u8

fn main() {
    var i: u8 = 4
    a[i] = 1               // writes whatever follows a: perhaps 'after', perhaps not
}
```

- A constant index past the end is a compile error, and so is a `for` loop with constant bounds that goes past it (`for i in 0..20 { a[i] }` on a `[u8; 10]`).
- **To reach other memory on purpose, use a pointer.** A write through a pointer makes the optimizer forget what it holds for every variable, so nothing is read stale.
- **`tern run` checks every variable index** as a debugging aid, and the program runs the same with or without it. The run goes on after a bad index, so one run shows every bad access:

```
Index check (the program has no check; the emulator found these):
  game.tern:17: Index 4 is outside 'a', which has 4 elements
      a[i] = 1
```

Pointer accesses and `asm` blocks are not checked.

## 10.3 Flags after an `asm` block

A function with an empty body that returns `bool using carry` (or `zero`, `negative`, `overflow`) reads the flag the code before it left. The `cpu` module has one for each flag. That is defined only right after an `asm` block; after compiled Tern code the flags are unspecified, since the compiler may have reordered or removed the instructions you expect.

```
import cpu

var count: u8

fn main() {
    asm {
        lda count
        cmp #10              // carry set when count >= 10
    }
    while !cpu.carry() {
        count += 1
        asm {
            lda count
            cmp #10
        }
    }
}
```

- `if cpu.carry()` right after the block is a single `bcc`.
- In `while cpu.carry()`, the first test reads the flag of the block before the loop, and each later test reads the flag of the block that ends the body. If the body does not end with an `asm` block, the later tests are unspecified.
- `var done: bool = cpu.carry()` stores the flag as a value.

## 10.4 Loops with a step

- **A step of 0 never ends the loop.** A `for` loop may take a `u8` variable as its step; if the value is 0 at runtime, the counter never moves.
- **A stepped loop stops when the step carries past the end of the type**, not only at the bound: `for t in 0..=255 step 16` runs for 0, 16, ..., 240 (16 times), then 240 + 16 carries past 255 and the loop ends, instead of wrapping to 0 and running for ever.
- **The direction comes from the step, never the bounds**: `for i in n..=0` counts up, so it runs once when `n` is 0 and not at all otherwise.

## 10.5 Signed shifts and division round differently

`>>` on a signed value rounds down, toward minus infinity: `-3 >> 1` is `-2`, `-1 >> 1` is `-1`. Division of negative constants, and `math.divs8` at runtime, round toward zero, and `%` takes the sign of the left operand: `-7 / 2` is -3, `-7 % 2` is -1, while `-7 >> 1` is -4. So `>> 1` is not "divide by 2" for negative numbers.

## 10.6 Promises the compiler trusts

- **`keeps` on a routine at a fixed address** (`fn chrout(ch: u8 using A) @ $FFD2 keeps X, Y`) says which registers the routine leaves alone. The compiler cannot see the routine's code, so it believes the promise and keeps values in those registers across the call. Take it from the routine's documentation; a wrong `keeps` breaks the caller. The same holds for `keeps` on an `asm fn`. On a `fn` or `inline fn` the compiler checks `keeps` against the body, so it is not a promise there ([2.7](02-language.md#27-functions)).
- **A function reached only through `addr()`** has no known root: the compiler cannot see when it runs. Its frame shares memory with no other, and globals it shares with other code are volatile. Calling it from main and from a handler is your responsibility (10.8).
- **An address passed as a handler is a plain `u16`**: `c64.set_irq(addr(plain_fn))` compiles, but a plain `fn` ends with `rts`, not `rti`. Install only `interrupt fn`s and `asm fn`s. (On the NES, vectors are checked: [9.3](09-nes.md#93-interrupt-vectors).)

## 10.7 Decimal mode in handlers

The interrupted code may be in decimal mode (the `bcd` library, or an `asm` block with `sed`). A handler starts with `cld` only when it can be affected: when its code or asm blocks have `adc` or `sbc`, or it calls anything (`jsr`).

An asm `jmp` out of a handler does not count as a call:

```
interrupt fn irq() {
    asm {
        inc $D019            // acknowledge
        jmp $EA31            // on to the KERNAL's handler: no cld is added
    }
}
```

The code it jumps to runs in decimal mode if main was in it. If that code adds or subtracts, put `cld` in the asm block before the `jmp`. The same block also restores registers itself, since the compiler cannot follow it.

## 10.8 A function called from main and a handler

The compiler allows it, but the function has one set of variables. An interrupt that arrives while main is inside the function overwrites its parameters, locals and temporaries, and main's call goes on with the handler's values. The report lists each such function with its roots; [2.9](02-language.md#29-interrupts) gives four ways to make it safe.

**Loop counters are in memory unless you say otherwise.** The optimizer may happen to keep a counter in a register, but only `using X` or `using Y` guarantees it, and a function shared with a handler may rely only on that:

```
var bar: [u8; 40]

fn clear_bar() {                       // no memory of its own: safe from both roots
    for i in 0..40 using X { bar[i] = 0 }
}
```

A pointer parameter is not such a case, even with `using AY`: to read through it, the function copies it to zero page, which is memory of its own.

A module's `private` variable used from two roots is an error, since the library's work bytes would be overwritten (`random`, `float`): the exception is a module whose own `interrupt fn` shares it on purpose.

## 10.9 Overlays and banks

- **A call into an overlay that is not loaded runs whatever is in the area.** The compiler cannot know which file is loaded, and a check on every call would cost every call. Load first, then call.
- **Which ROM bank is selected is not checked either.** On the NES, `level1.start()` with bank 1 selected jumps into bank 1's bytes at `level1.start`'s address. Select the bank before calling into it; a named constant used on the import and in `uxrom.select` keeps them in step.
- **Loading an overlay resets its variables** to their initial values, since they are in its file. Keep anything that must survive in resident memory.
- **An overlay's zero-page bytes at a fixed address** (`var p: ptr u8 @ $FB`) are not in its file: they hold whatever was there before it loaded, and the overlay sets them itself.
- **An interrupt handler installed from an overlay** must be removed before another overlay replaces it; otherwise the interrupt jumps into the new overlay's bytes.
- **A `volatile` fixed-address variable over an overlay** reads whatever overlay is loaded at that address. Addresses inside an overlay's file move when its code or data change, so such a variable is for data the overlays place on purpose, such as a level header at the area's start.

## 10.10 Binaries, plug-ins and exports

- **A binary's entries are one root.** `init` and `play` of a music player may share private state, so the host must not call one entry from an interrupt while another is running.
- **A plug-in's memory is checked only against its own `memory` block**, not against the game's or another slot's. Keep each slot, and each slot's zero page, apart in the interface modules, and reserve them in the game.
- **A game's export table has gaps of zeros** when its routines are not packed: a call into a gap stops at a `brk`.
- **The game's exported functions are called from outside**, so the compiler cannot see when they run (an unknown root, as for `addr()`). Globals they share with the rest of the game are always read from memory, and a plug-in must not call them from an interrupt while the game is in one of them.

## 10.11 Functions that never run together share memory

Every variable has a fixed address, and functions that can never be active at the same time (neither calls the other, directly or through others) share the same bytes. So a function's locals hold leftovers from another function on entry, which is why a local needs an initial value. A local array or struct has none, so set it before reading it. The report's frame map and the listing show which functions share.

## 10.12 Evaluation order

Function calls and reads of `volatile` memory in one statement happen left to right: in `(rand() + 1) << 2 | rand()` the left `rand()` runs first, in `f(g(), h())` `g` runs before `h`, and in `a[f()] = g()` the index's call runs first. One case is left open: a variable that a call in the same statement changes. In `x = seed + next()` where `next` changes `seed`, either value of `seed` may be added; put the call or the variable in a local first:

```
var n = next()
x = seed + n
```

## 10.13 A fixed address is ordinary memory unless it is I/O

`var reg: u8 @ $C000` outside the platform's I/O areas is ordinary memory: the optimizer may keep it in a register, and remove a read whose value is not used or that repeats. Each machine's chapter lists its volatile areas. Hardware the platform does not list, or RAM that something else changes (another program, a DMA, a card), needs `volatile`:

```
volatile var mailbox: u8 @ $C000   // RAM a loaded binary writes: every read and write happens
```

A global used by main and by an interrupt handler is volatile by itself, so a main loop that waits for a flag the handler sets reads it on every pass.

## 10.14 `indexed match` does not check its value

`indexed match` is a jump table over the span from its lowest pattern to its highest. A value outside that span jumps through whatever bytes follow the tables; `_` catches only the values inside the span that no pattern lists. Mask the value into the span (`indexed match op & 15` with patterns 0 to 15), or list every value that can occur. A plain `match` skips values it does not list. `tern run` reports a value outside the span and stops the run.

## 10.15 When `main` returns

The program stops in an endless loop (`jmp` to itself), and interrupt handlers keep running. There is no return to BASIC or DOS, whose zero page the program has used; `atari.exit()` and `apple2.exit()` leave on purpose.

## 10.16 Alignment leaves padding

`@ align(n)` can leave up to `n - 1` bytes unused before the declaration. The report shows the padding:

```
Aligned nes.oam: $0200 (@ align(256)), 0 bytes of padding before it
```

In `data`, or in an overlay, the padding is part of the file. The compiler puts aligned `noinit` variables at the start of the variables' block, so the variables leave no gaps among themselves; the padding comes once, before the block, and is less than the largest alignment.

## 10.17 Fields left out of a struct literal are 0

`Line { row = 3 }` sets `col` and `text` to 0. There is no error for a missing field, since a table of records usually sets only the fields that differ.

## 10.18 Compile-time `int` never wraps

The same expression can give different results in a `const fn` and in a function: in a `const fn`, `200 + 100` is 300, and an `int` outside the 64-bit range is an error; in runtime code on `u8`s it is 44. Write `& $FF` in the `const fn` where you want the 8-bit result.

## 10.19 Division by zero in the library

`math.div8`, `div16`, `divs8` and `wide`'s divisions do not trap on a zero divisor; the result is unspecified. `float.div` gives the largest value with the dividend's sign and sets `float.overflow`.
