# 12. Running and measuring: `tern run` and `tern bench`

[Guide index](README.md)

The compiler has a built-in 6502 emulator. `tern run` compiles a program and runs it there, then shows what you ask for: memory, the text screen, where the cycles went. `tern bench` runs two or more versions of the same work and compares their bytes and cycles. Both take the same `--target` as a build, and both are exact and repeatable: the same program gives the same cycles every time.

The output in this chapter is real, from the programs shown, on the C64 target.

## 12.1 What the emulator is

A cycle-exact 6502 (the CPU of the target, `nmos6502` for all four machines) with plain RAM, and stand-ins for a few ROM routines on each machine. The chips are not emulated: no VIC-II, SID or CIA on the C64, no ANTIC, GTIA, POKEY or PIA on the Atari, no video or disk on the Apple II; on the NES, PPU memory and sprite memory are kept, but not pixels. Each machine's chapter lists its stand-ins: [C64](06-c64.md#67-tern-run-on-the-c64), [Atari](07-atari.md#76-tern-run-on-the-atari), [Apple II](08-apple2.md#85-tern-run-on-the-apple-ii), [NES](09-nes.md#97-tern-run-on-the-nes).

So `tern run` checks a program's logic and measures its code; what it looks and sounds like is checked on the machine's emulator (VICE, Altirra, AppleWin, Mesen, [1.4](01-getting-started.md#14-running-a-program)).

A run does not start from a cleared machine. Memory the program does not load is filled with bytes from a fixed seed, and A, X and Y are not zero, as on a machine after its ROM ran, so a program that reads memory it never wrote goes wrong here as it would on the machine. The text screen starts blank, and input reads as idle (nothing pressed). A stand-in takes no cycles; a call to any other ROM routine stops the run.

## 12.2 `tern run`

`sum.tern`:

```tern
import text

var table: [u8; 8] = [3, 1, 4, 1, 5, 9, 2, 6]
var total: u16

fn add_all() {
    for i in 0..len(table) {
        total += table[i] as u16
    }
}

fn main() {
    add_all()
    text.dec16($0400 as ptr u8, total)
}
```

```
> tern run --target c64 sum.tern --dump total --dump table:4 --screen
Main returned: 639 cycles (623 in main)
A=$02 X=$01 Y=$01 SP=$FF PC=$0817
total $0002 u16 = 31 ($001F)
table $08B3 [u8; 8] (4 bytes):
  $08B3  03 01 04 01
Screen ($0400, 40 x 25):
  |31                                      |
  |                                        |
  ...
```

- **The first line** says how the run ended and its cycles: all of them, and those from the call of `main` to its return (the rest is the startup code before `main`).
- **The registers** are as the run left them.
- **Each `--dump`** follows, then the screen.

The run ends when `main` returns, or after `--cycles` cycles (20,000,000 by default, about 20 seconds of a real machine), or when the program does something the emulator stops on: a call to a ROM routine with no stand-in, an undocumented opcode, a `brk` (except where a machine's chapter says otherwise), or an `indexed match` value outside its span.

| Option | What it does |
|---|---|
| `--dump <what>` | Show memory after the run. A variable (`total`, `main.count`, a module's `video.frames`), a variable with a byte count (`table:4`), a range (`$C000-$C0FF`) or a start and a count (`$C000:16`). May be given more than once. |
| `--screen` | Show the text screen: `$0400` on the C64, `SAVMSC` on the Atari, `$0400` in the Apple II's row order, nametable 0 on the NES (each tile shown as the character the program's default encoding gives it). |
| `--set <a>=<v>` | Write the byte `v` at address `a` before the run, such as a joystick value: `--set $DC00=$7B`. May be given more than once. It takes addresses, not variable names, since the program's startup sets its own globals after loading and would overwrite them. |
| `--irq <n>` | Raise an IRQ every `n` cycles, through the vector at `$FFFE` (12.4). |
| `--cycles <n>` | Stop after `n` cycles. |
| `--profile` | Show the cycles of each function and each source line (12.5). |
| `--json` | Print the result as JSON (12.8). |
| `--max-errors <n>`, `--no-warnings`, `--warnings-as-errors` | As for a build. |

The exit code says how the run went, so a script can test it:

| Code | Meaning |
|---|---|
| 0 | `main` returned, with no error |
| 1 | The program did not compile |
| 2 | `main` did not return: the cycle limit, or a stop |
| 3 | `main` returned, but the index check found an error (12.6) |
| 4 | An internal error in the compiler |

## 12.3 Input and frames

**Input.** Every input reads as idle unless you write it with `--set`, and the value stays for the whole run, since nothing else writes it. On the C64, joystick 2 is `$DC00`, active low: `$7F` is nothing pressed, `$7B` is left held (bit 2 clear), `$6F` fire. The idle values on each machine are in its chapter.

**Frames.** Without the video chip, nothing marks a frame by itself, so a loop that waits for a raster line (`while c64.vic.raster != 250 { }`) never ends: `$D012` is plain RAM here. A game whose frame is driven by an interrupt can run in `tern run` with `--irq`, which plays the part of the raster interrupt. `stick.tern` counts 50 frames and moves `x` left while the joystick is held:

```tern
import c64

var x: u8 = 100
var frames: u8

interrupt fn frame() {
    c64.vic.irq_status = 1          // acknowledge the raster interrupt
    frames += 1
}

fn main() {
    c64.interrupts_off()
    c64.kernal_off()
    c64.cia_irqs_off()
    c64.set_irq(addr(frame))
    c64.vic.raster = 250
    c64.vic.irq_enable = 1
    c64.interrupts_on()
    while frames < 50 {
        var seen: u8 = frames
        while frames == seen { }    // wait for the next frame
        if c64.cia1.port_a & 4 == 0 {   // joystick 2 left
            x -= 1
        }
    }
}
```

With no interrupt, the run waits for the first frame until the cycle limit:

```
> tern run --target c64 stick.tern --dump x --dump frames
Stopped after 20000000 cycles; main had not returned: 20000002 cycles (19999984 in main)
A=$00 X=$89 Y=$08 SP=$FD PC=$083E
x $0002 u8 = 100 ($64)
frames $0003 u8 = 0 ($00)
```

With an IRQ every PAL frame (63 cycles x 312 lines = 19,656 cycles; NTSC 65 x 263 = 17,095) and joystick 2 held left:

```
> tern run --target c64 stick.tern --irq 19656 --set $DC00=$7B --dump x --dump frames
Main returned: 983310 cycles (983292 in main), 50 interrupts
A=$32 X=$89 Y=$08 SP=$FF PC=$0819
x $0002 u8 = 50 ($32)
frames $0003 u8 = 50 ($32)
```

`--irq` goes through `$FFFE`, so on the C64 it reaches a handler installed with `c64.set_irq` (the KERNAL switched off), not one installed through `$0314` with `c64.set_kernal_irq`: in `tern run` there is no KERNAL ROM to pass the interrupt on. The NES needs no `--irq`: the emulator starts a frame every 29,781 cycles, sets the vblank flag and raises the NMI when bit 7 of `$2000` is set ([9.7](09-nes.md#97-tern-run-on-the-nes)).

## 12.4 Interrupts and the cycle count

The first line of a run with interrupts counts them (`50 interrupts`). The cycles in `main` include the interrupts that came while `main` ran, as the time a real frame takes would. To see a handler's own share, use `--profile`: for `stick.tern` the function table has `frame  50 calls  1200 cycles` beside `main`, and the handler's lines are listed with the others. A handler is checked like any code, index errors included.

## 12.5 `--profile`: where the cycles go

```
> tern run --target c64 sum.tern --profile
Main returned: 639 cycles (623 in main)
A=$02 X=$01 Y=$01 SP=$FF PC=$0817
Functions (cycles include the functions they call):
  main                            1 calls        623 cycles
  text.dec16                      1 calls        400 cycles
  add_all                         1 calls        183 cycles
; Per source line: bytes, the cycles of its instructions once each (+: a branch or page crossing adds more), how often its first instruction ran, and the cycles they took in the run.
; file       line bytes  cycles     runs run cycles  addresses                source
  sum.tern      6     1       6        1          6  $089A                    fn add_all() {
  sum.tern      7     7      8+        1         57  $0887-$0888 $0895-$0899  for i in 0..len(table) {
  sum.tern      8    12     19+        8        120  $0889-$0894              total += table[i] as u16
  sum.tern     12     1       6        1          6  $0830                    fn main() {
  sum.tern     13     3       6        1          6  $081A-$081C              add_all()
  sum.tern     14    19      28        1         28  $081D-$082F              text.dec16($0400 as ptr u8, total)
  text.tern    43     4       5        1          5  $0831-$0834              var n: u8 = 0
  ...
                -    13      19        1         16  $080D-$0819              (startup)
; total 639 cycles
```

The function table is sorted by cycles. In the line table, `cycles` is what the line's instructions take once each, the same figure as the listing, and `run cycles` is what they took in this run: `total += ...` ran 8 times for 120 cycles. The line a loop's header is on also holds the loop's own test and step, so its runs count the loop entered, and its cycles every pass. Library lines are listed too, under their own file. Read with the listing (`.lst`), this shows which lines to work on.

## 12.6 The index check

`tern run` checks every array index that is a variable, as a debugging aid; the program itself has no check, and runs the same with or without it ([10.2](10-behavior.md#102-indexes-are-not-checked)). A bad index is printed with its source line, and the run goes on, so one run shows every bad access:

```tern
var marks: [u8; 4]
var n: u8 = 5

fn main() {
    for i in 0..n {
        marks[i] = 1
    }
}
```

```
> tern run --target c64 bad.tern
Main returned: 143 cycles (87 in main)
A=$01 X=$05 Y=$C3 SP=$FF PC=$081F
Index check (the program has no check; the emulator found these):
  bad.tern:6: Index 4 is outside 'marks', which has 4 elements
      marks[i] = 1
```

The exit code is 3. Each place is reported once, with a count when it went wrong more than once (`... which has 4 elements (3 times)` with `n` = 7). An index the compiler can see is wrong is a compile error instead (`for i in 0..=4` here). Pointer accesses (`p[i]`) and `asm` blocks are not checked. An `indexed match` value outside its span is reported too, and stops the run, since its jump would land on whatever bytes follow the tables.

## 12.7 `tern bench`

`tern bench` compiles and runs each variant as `tern run` does, and prints one line per variant. A variant is a whole program (a `.tern` file), or statements given with `--code`, which become the body of `main`; `--decl` adds declarations before `main` for every `--code` variant (imports first, then globals and functions):

```
> tern bench --target c64 --decl "import mem" --decl "var buf: [u8; 200]" --code "for i in 0..200 { buf[i] = 0 }" --code "mem.fill(addr(buf), 0, 200)" --dump buf:4
variant   program  main bytes   calls     cycles  per call    zp  other
code 1         42          13       1       2807      2807     0    200
code 2         89          22       1       3455      3455     0    200
Memory (zp: zero page bytes, other: the rest; the measured function's variables and the program's globals):
  code 1: buf $082B [u8; 200]
  code 2: buf $085A [u8; 200]
Results:
  code 1: buf:4 = 00 00 00 00
  code 2: buf:4 = 00 00 00 00
```

The columns:

| Column | Meaning |
|---|---|
| `program` | The whole program's bytes |
| `main bytes` (or `f bytes` with `--fn f`) | The measured function's own bytes |
| `calls` | How often it was called |
| `cycles` | Its cycles in the run, including the functions it calls |
| `per call` | `cycles` / `calls` |
| `zp`, `other` | Bytes of memory for the measured function's variables and the program's globals: in zero page, and elsewhere |

Here the plain loop is smaller and faster than the library's `mem.fill` for 200 bytes: `fill` is written for any count up to 65,535, a page at a time, and pays for that on a short one.

`--dump` is shown for each variant, so you can check that the variants do the same work; a bench with versions that disagree is measuring nothing. **`--fn f`** measures one function of a whole program instead of `main`, called as the program calls it:

```
> tern bench --target c64 sum.tern --fn add_all --dump total
variant   program  add_all bytes   calls     cycles  per call    zp  other
sum.tern      186          20       1        183       183     2      8
...
Results:
  sum.tern: total = 31
```

To compare two versions of a function in a real program, make a copy of the program with the other version and bench both files with `--fn`. `--cycles`, `--irq`, `--set` and `--json` work as for `tern run`. Warnings are not shown, since a snippet often leaves a value unused. A variant that fails to compile, does not return, or has an index error fails the bench, and its errors are printed. The exit code is 0 when every variant compiled, returned and passed the index check, and each `--dump` named something; 1 otherwise.

## 12.8 JSON output

`--json` prints the result as one JSON object, for scripts and coding agents. For `tern run` it has how the run ended, the cycles, the registers, the index errors, each `--dump` with its address, type, value and bytes, the screen, the profile and the compile's diagnostics:

```
> tern run --json --target c64 sum.tern --dump total
{
  "ok": true,
  "returned": true,
  "stop": "main returned",
  "cycles": 639,
  "mainCycles": 623,
  "interrupts": 0,
  "interruptCycles": 0,
  "registers": { "a": 2, "x": 1, "y": 1, "sp": 255, "pc": 2071 },
  "output": "",
  "indexErrors": [],
  "memory": [
    { "what": "total", "address": 2, "size": 2, "type": "u16", "value": 31, "bytes": [31, 0] }
  ],
  "screen": null,
  "functions": null,
  "lines": null,
  "diagnostics": []
}
```

(shown here with the short arrays on one line). `screen`, `functions` and `lines` are filled with `--screen` and `--profile`. `tern bench --json` gives the same table as fields, one object per variant in `variants` (`programBytes`, `functionBytes`, `calls`, `cycles`, `cyclesPerCall`, `zeroPageBytes`, `otherBytes`, `memory`, `results`, `indexErrors`).

## 12.9 A run as a test

`tern run` makes a good test for game logic: put the logic in modules, write a small `main` that imports them, sets up a known state, runs for a set number of frames, and leaves the result in memory. Then check the result with `--dump` (or the JSON) and the exit code. A sketch, with a game's own `level` and `player` modules:

```tern
import level
import player

var steps: u8

fn main() {
    level.load(1)
    player.start()
    while steps < 200 {          // 200 frames of play
        player.update()
        steps += 1
    }
}
```

```
tern run --target c64 test_jump.tern --set $DC00=$6F --dump player.y --dump player.state
```

- **Fixed input:** `--set` holds a joystick value for the whole run. For input that changes, let the test's `main` write the input variable the game reads (for example `input.stick = ...` each frame) instead of the hardware register.
- **Frames:** call the frame's work directly, as above, or use `--irq` when the test runs the real interrupt-driven loop (12.3).
- **Random numbers:** a seed read from hardware (the raster line, a timer) is the seeded memory's byte here, the same every run; set the seed in the test for a known level.
- **Stop when done:** let `main` return, so the exit code is 0 and a run that hangs shows as 2.

## 12.10 What `tern run` does not show

- **Pictures and sound.** No chip is emulated. Sprites, colors, scrolling, display lists, PPU pixels and sound are checked on the machine's emulator.
- **Timing against the video beam.** Code that waits for a raster line, or changes colors mid-line, runs differently or never ends; the cycle counts of the code itself are still exact.
- **Ports that read something other than what was written.** I/O registers are plain RAM, so a value set with `--set` lasts only until the program writes that address. On the C64, `$DC00` is both joystick 2 and the keyboard's row select: a program that scans the keyboard writes `$DC00`, and the joystick held with `--set $DC00=$7B` is gone after the first scan, where a C64 would read the written value ANDed with the joystick. Check such a program's controls on the machine's emulator, or drive the logic from a test `main` that sets its input variables directly (12.9).
- **ROM routines without a stand-in.** Calling one stops the run, with its address. Each machine's chapter lists the stand-ins.
- **Disk writes.** Loading the program's own overlays works on the C64, the Atari and the Apple II; writing a file stops the run.
