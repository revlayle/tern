# Code quality review: the CPU, the agent tools and the command line

Scope: `src/Tern.Cpu` (3 files, 600 lines), `src/Tern.Compiler/Tools` (11 files, 2,100 lines) and `src/Tern.Cli` (1 file), read against spec sections 12.2, 13 and 14.4 and `tern --help`. Reviewed at commit d223f12. Test programs are shown inline; each was run with the built `tern` (`tern run --target <platform> file.tern ...`, `tern bench ...`). The test suite was not run, since nothing was changed.

## Overview

- **Tern.Cpu.** `InstructionSet` is one static table of the 151 documented opcodes (mnemonic, mode, base cycles, page-cross flag), used by the encoder, the emulator and the compiler's listing. `Encoder` turns one instruction into bytes. `Cpu6502` runs one instruction per `Step` against an `IBus` and counts cycles; `Irq` and `Nmi` are called between instructions.
- **Runner** (`Runner.cs`) loads a compiled program, steps the CPU until the halt address, and around each step does the work of `tern run`: IRQs, stand-ins for ROM calls, the index check, the profile, the per-function times.
- **Machines** (`Machine.cs`, `Apple2Machine.cs`, `NesMachine.cs`, `NesBus.cs`): per platform, memory at start, the stand-ins and the text screen. The NES is different: `NesBus` is the CPU's bus (PPU registers, OAM DMA, controllers, the UxROM latch) and `NesMachine` is nearly empty.
- **IndexChecker** checks marked instructions before they run. **LineTable** and **Profile** give bytes and cycles per source line. **RunReport** and **Bench** format text and JSON. **Diagnostics** turns a `CompileError` into a record for `--json`.
- **Cli** (`Cli.cs`) parses arguments and prints for the three commands; `Tern.Cli/Program.cs` is one line that calls it.

Findings: 10 correctness (2 medium, 8 low; 9 demonstrated, 1 suspected) and 8 construction (1 high, 4 medium, 3 low).

No instruction, flag or cycle-count bug was found in the emulator by reading: decimal mode follows Bruce Clark's appendix A, `jmp ($xxFF)` wraps, branches and indexed reads have the right penalties, stores and read-modify-write never get one, `brk` and interrupts push the right bytes. Finding 17 lists what no test covers.

## Findings

### 1. A platform's run model is split between `Runner`, `Machine` and `NesBus`

- Severity: high. Kind: construction.
- Where: `Runner.cs:86-96`, `:102`, `:117`, `:153-158`, `:168-173`, `:194-202`; `Machine.cs:13-19`; `NesMachine.cs:17-24`; `Bench.cs:69`.
- What:
  - `Machine` is meant to hold "what `tern run` knows of one platform", but `Runner.Run` loads the program itself with a branch on `Platform.Format` (`System`: at the load address; `Nes`: a `NesBus`; else the image).
  - The NES is handled by `nes is null` tests in eight places in `Runner`: the bus, `nes.Cpu = cpu`, `PlaceOf`, the index checker's place function, `nes.Tick`, `RunResult.Nes`, `Screen` and `ScreenText`. `NesMachine` overrides `Screen` and `ScreenChar` with values nothing uses, because the real work is `NesMachine.Text`, a static method that `Runner.ScreenText` calls directly.
  - `Machine.For` switches on the output format, not the platform, and its default arm is the C64. A new platform that writes a `.prg` (C128, X16) silently gets the C64's KERNAL stand-ins and a 40 x 25 screen at `$0400`.
  - `Machine.For` is called again after the run (`Runner.Screen`, `ScreenText`) and gives a fresh machine. This works only because the screens are read from RAM. `C64Machine._cursor` is not in the second one.
  - Defaults name the C64: `RunResult.Platform = Platform.C64`, `Bench.Measure(platform: null)`, `Runner.ScreenText(Ram)`.
- Why it matters: the Commander X16 needs banked RAM and ROM (a bus, like the NES), a 80 x 60 screen in VERA memory (not CPU RAM, like the NES) and KERNAL stand-ins (like the C64). The C128 needs its MMU. Each would add a third or fourth kind of `is null` test to `Runner`, and nothing tells the contributor which places to touch.
- Fix: let `Machine` own the whole model. `IBus Load(CompileResult, Ram)` (returns the RAM or its own bus), `void AfterStep(Cpu6502)` returning whether an interrupt was taken (the NES's `Tick`), `int PlaceOf(int pc)`, `IReadOnlyList<string> ScreenText(...)`. Keep the machine in `RunResult` so the screen is read from the one that ran. Give `Platform` a factory (or key `Machine.For` on the platform and throw for an unknown one). `Runner` then has no platform test.

### 2. A path that cannot be read or written ends in a stack trace

- Severity: medium. Kind: correctness (demonstrated).
- Where: `Cli.cs:184-192` (no catch around the writes), `:320-327` and `:304-305` (catch `IOException` only), `:307` (`Bench.Measure` calls `Runner.Run`, which throws `InvalidOperationException`), `Tern.Cli/Program.cs:3` (no last catch).
- Evidence, with `a.tern` holding `var out: u8` and `fn main() { out = 1 }`, and `adir` a folder:

```
tern --target c64 a.tern -o nodir/a.prg     Unhandled exception. System.IO.DirectoryNotFoundException
tern --target c64 --json a.tern -o nodir/a.prg   the same; no JSON
tern --target c64 a.tern -o adir            Unhandled exception. System.UnauthorizedAccessException
tern --target c64 adir                      Unhandled exception. System.UnauthorizedAccessException
tern run --target c64 adir                  the same
tern bench --target c64 adir                the same
tern --target c64 ""                        Unhandled exception. System.ArgumentException
```

  A binary without `main`, measured with `--fn`:

```
// lib1.tern
var n: u8
fn bump() { n += 1 }
export bump
```

```
tern bench --target c64 --fn bump lib1.tern
Unhandled exception. System.InvalidOperationException: A binary without main cannot be run on its own ...
```

  `tern run` catches that one (`Cli.cs:249`) and prints the message; `tern bench` does not.
- Why it matters: an agent that passes `--json` gets no JSON and an exit code that is none of the documented ones. A wrong `-o` folder is an everyday mistake. The files are written one by one, so a failure half way leaves some of them.
- Fix: read and write through two helpers that catch `IOException`, `UnauthorizedAccessException`, `ArgumentException` and `NotSupportedException` and report through `Fail` ("Cannot write nodir/a.prg: the folder does not exist. Create it"). Check the output folder before compiling. In `Bench.Measure`, return `Failed(...)` for a program without `main`. Add one `catch (Exception)` in `Cli.Run` that prints "internal error" with the message, as JSON when `--json` is given, and returns a fixed code. Add tests for each line above.

### 3. `-o` can overwrite the source file, and one output can overwrite another

- Severity: medium. Kind: correctness (demonstrated).
- Where: `Cli.cs:177-187`.
- What: the listing, label and `.lines` paths come from `Path.ChangeExtension(outputPath, ...)`. Nothing compares them with the input or with each other.
- Evidence:

```
tern --target c64 b.tern -o b.tern      Wrote b.tern      (b.tern now holds the 30 program bytes; the source is gone)
tern --target c64 a.tern -o c.lst       Wrote c.lst       (c.lst holds the listing; the program was written and then overwritten)
```

  The same happens with `-o x.vs`, `-o x.lines`, and with an overlay or `_api.tern` file whose name is the output's.
- Why it matters: losing the source is the worst thing a compiler can do. `tern a.tern -o a.tern` is one mistaken tab-completion away.
- Fix: before writing, build the full list of paths, and stop with an error when one is the input file (compare full paths, ignoring case on Windows) or when two are the same.

### 4. The CPU project knows one instruction set

- Severity: medium. Kind: construction.
- Where: `InstructionSet.cs:47-193` (static table, `Decode(byte)`, `TryGet(mnemonic, mode)`), `Encoder.cs:12`, `Cpu6502.cs:89`, `:93-147`, `:151-235`, `:279`, `:313`.
- What:
  - `InstructionSet` is a static class with one table. `Decode`, `TryGet` and `Encoder.Encode` take no variant, so the assembler and the emulator cannot ask "is `stz` valid here" or "how many cycles on a 65C02".
  - `CpuVariant` is tested in two places only, both inside the decimal arithmetic.
  - `Step` has two `switch` statements with no `default`. A mnemonic or mode added to the enums and the table, but not to `Step`, runs as a `nop` with a zero address. No test would fail.
  - `OpcodeInfo` has one cycle count. The 65C02 differs per opcode (`jmp (abs)` 6, decimal `adc` +1, `asl abs,x` 6 with no crossing).
  - `Modify` takes a lambda that captures `this`, so every shift, `inc` and `dec` allocates a delegate. It is the only allocation in `Step`.
- Why it matters: the 65C02 (X16) is on the roadmap. It needs new mnemonics and modes (`(zp)`, `(abs,x)`, zero page relative), opcodes that replace NMOS ones, unused opcodes as nops of fixed sizes, a fixed `jmp ($xxFF)`, the D flag cleared on interrupts and no double write in read-modify-write. Today each is an `if (Variant == ...)` in a different place.
- Fix: make `InstructionSet` an instance, one per variant (`InstructionSet.For(CpuVariant)`), built from the NMOS list plus changes. Pass it to `Encoder` and `Cpu6502`. Put the variant's behavior flags in one record (`DecimalMode`, `IndirectJumpBug`, `ClearsDecimalOnInterrupt`, `WritesOldValueFirst`) that `Cpu6502` reads. Add `default: throw new InvalidOperationException` to both switches. Replace the lambdas with a small `switch` inside `Modify`.

### 5. Three hand-written option parsers and a hand-written help text

- Severity: medium. Kind: construction.
- Where: `Cli.cs:12-94` (`Usage`), `:139-368` (`Run`, with `CompileCommand`, `RunCommand`, `BenchCommand` as local functions).
- What:
  - `Run` is one 230-line method. The three commands are local functions that share `output` and `error` by capture.
  - `--json`, `--target`, `--max-errors`, `--set`, `--cycles`, `--irq` and `--dump` are parsed two or three times, with copied lines (`:224-228` and `:274-278`; `:232-234` and `:283-285`).
  - A value option is a chain of `&&` that parses into the live variable (`long.TryParse(arguments[i + 1], out cycles)`). On a bad value the variable is left at 0 and the chain falls through to "Unknown argument". This is safe only because that arm returns.
  - `Usage` is a separate text. An option exists in the parser, the help, the skill and the spec, with nothing to tie them. The help is already behind the code (finding 14).
  - `files.Skip(4)` (`:199`) means "the overlay and API files", by the order of the four `Write` calls above it.
- Why it matters: `tern lsp` is a fourth command, and a release adds options (`--cpu`, `--board`). Each new option is copied into every command that takes it.
- Fix: one small `Options` reader: `Flag("--json")`, `Value("--cycles", long, min: 1)`, `Many("--dump")`, which reports "--cycles needs a number above 0, found 'abc'" itself. Share one `RunOptions` reader between `run` and `bench`. Make each command a method on a class that holds `output` and `error`. Return the written files in two lists (main outputs, extra files) instead of `Skip(4)`.

### 6. Each stand-in is listed in four places, and three screens are written three times

- Severity: medium. Kind: construction.
- Where: `Machine.cs:68`, `:74`, `:82`, `:88-112` (C64); `:195-203`, `:217`, `:221-251` (Atari); `Apple2Machine.cs:25`, `:29`, `:45`, `:56-92`; the class comments; `Cli.cs:63-70`.
- What:
  - A stand-in is a constant, a term in `Has`, a `case` in `Call`, a name in the `StandIns` text, a sentence in the class comment and a sentence in the help. The C64's `Call` uses `default:` for PLOT, so the order of the `case`s is part of the logic, and the shared tail (`$D6`, `$D3`) runs for some cases and not for others (`return null` against `break`).
  - The three text screens (cursor, wrap, scroll, clear) are written from scratch three times, as local functions inside `Call` that capture `ram`, `output` and the cursor.
  - The overlay "disk" is written three times: the C64's LOAD, the Atari's `_open` and `File()`, the Apple II's `_open` and `FileCall`. Each compares names its own way (`o.Name.ToUpperInvariant()`, `(o.Name + ".xex")`, `(o.Name + ".bin")`), with the extension as a literal rather than `platform.BinaryExtension`.
  - CIO commands (`0x03`, `0x07`, `0x0B`, `0x0C`) and MLI calls (`0x65`, `0xC4`, `0xC8`, ...) are bare numbers, named only in comments.
  - `Machine.cs` holds the base class, the C64 and the Atari; the Apple II and the NES have their own files.
- Why it matters: the X16 and the C128 need CHROUT, PLOT, LOAD and a screen again, with a different size and screen address. Today that is a copy of `C64Machine`.
- Fix: a table per machine: `StandIn(int Address, string Name, Func<...> Run)`. `Has`, `StandIns` and the dispatch come from it. One `TextScreen` class (address or row function, columns, rows, blank, window) with `Put`, `NewLine`, `Clear`, used by all three. One `OverlayDisk` (find by name with the platform's extension, open, read, close). Name the CIO and MLI numbers as constants. One file per machine.

### 7. The JSON shapes and `Diagnostic` are not ready for a second consumer

- Severity: medium. Kind: construction.
- Where: `Diagnostics.cs:12-21`, `:27-32`, `:52-67`, `:69-88`; `RunReport.cs:126-163`; `Bench.cs:183-213`; `Cli.cs:246-249`, `:315-361`.
- What:
  - `tern run --json` has two shapes: the run's (`ok`, `returned`, `stop`, ...) and, for a compile error or a bad `--dump`, the compiler's (`ok`, `files`, `report`, `diagnostics`). `tern bench --json` has a third. All are anonymous types, so nothing names or tests the contract. `JsonSerializerOptions` is copied three times, the `indexErrors` projection twice.
  - A command-line problem becomes a `Diagnostic` at line 0, column 0 of the input file (`--dump 'out:0'`), or plain text on stderr even with `--json` (every `Usage1`).
  - `Diagnostic` has a start but no end, no error number and `Severity` as a string. The suggestion is cut out of the message text by a verb list (`FixVerbs`), so the split depends on wording (the pattern of Syntax finding 11 and Semantics finding 17).
  - Turning a `CompileError` into diagnostics is written twice (`Cli.cs:335-341`, `Bench.cs:78-91`), and reading the file and printing are inside `Cli.CompileFile`. There is no call that takes source text and returns diagnostics without throwing.
  - `MemoryDump.Value` switches on type names as text (`"u8"`, `"i16"`, `StartsWith("ptr ")`): a hidden link to how `VariableInfo.Type` is spelled. `MemorySet.Parse` and `MemoryDump.Number` are the same number parser twice.
- Why it matters: a language server needs ranges, codes, a severity enum, and diagnostics for unsaved text, on every keystroke. It would be the third copy of the `catch (CompileError)` block.
- Fix: named records for the three outputs, in one file, with one `JsonSerializerOptions`. One `Diagnostics.Check(source, path, platform, ...)` that returns `(CompileResult?, IReadOnlyList<Diagnostic>)` and never throws; `Cli` and `Bench` call it. Add `EndLine`, `EndColumn` and `Code` when the compiler has them (Syntax finding 8). Report command-line problems as JSON too when `--json` is present, with no file.

### 8. `tern bench --json` says `ok: true` when the exit code says failure

- Severity: low. Kind: correctness (demonstrated).
- Where: `Bench.cs:193` (`ok` leaves out `Returned`), `Cli.cs:310` (the exit code includes it), `Bench.cs:118-127`.
- Evidence:

```
tern bench --target c64 --json --cycles 1000 --code "while true { }"
  "ok": true,   ...   "returned": false,   "stop": "stopped after 1000 cycles; main had not returned"
exit code 1
```

  The other way round, a `--dump` that names no variable is not a failure at all:

```
tern bench --target c64 --code "var a: u8 = 1" --dump nosuch
  code 1: nosuch = (No variable 'nosuch'. Locals are written 'function.name')
exit code 0
```

  `tern run` treats the same `--dump` as an error with exit code 1.
- Why it matters: an agent reads `ok`. A variant that never returned has cycles that mean nothing.
- Fix: compute one `Failed` property on `BenchResult` (error, not returned, index errors, bad dump) and use it for `ok`, the exit code and the text. Document bench's exit codes in the help, as for `tern run`.

### 9. The NES's vblank flag stays set until it is read

- Severity: low. Kind: correctness (demonstrated).
- Where: `NesBus.cs:72-86` (`Tick`), `:96-100` (read of `$2002`).
- What: the flag is set at each frame start and cleared only by a read. On the NES the PPU also clears it at the end of vblank, about 2,273 CPU cycles later. So a read at any time in a frame gives `$80` if nothing read `$2002` since the frame started.
- Evidence: the second read comes about 11,000 cycles after a frame start, far past the end of vblank. A NES gives 0; `tern run` gives `$80`.

```
import nes
memory {
    cartridge nrom vertical
}
var early: u8
var late: u8
var stale: u8
fn main() {
    asm {
    .w: bit $2002
        bpl .w
        ldx #4          // about 5,100 cycles
        ldy #0
    .d1:dey
        bne .d1
        dex
        bne .d1
        lda $2002
        sta early
        ldx #28         // about 35,800 cycles more
        ldy #0
    .d2:dey
        bne .d2
        dex
        bne .d2
        lda $2002
        sta stale
        lda $2002
        sta late
    }
}
```

```
tern run --target nes nes1.tern --dump early --dump stale --dump late
early = 0   stale = 128 ($80)   late = 0
```

- Related, by reading (suspected): setting bit 7 of `$2000` while the flag is set raises an NMI at once on the NES; here the NMI waits for the next frame. The first write to `$2006` changes the address at once; the NES changes it on the second write.
- Why it matters: spec 12.2 says "timing within a frame" is not modelled, so this is inside the stated limits. But "is it vblank now" is a yes or no question, not timing, and code that tests the flag once (rather than waiting in a loop) passes in `tern run` and fails in Mesen.
- Fix: keep the cycle the flag was set and report it only for `VblankCycles` (2,273) after it. Or say in the help and in spec 12.2 that the flag means "a frame started since the last read".

### 10. The profile leaves out the cycles of taking an IRQ

- Severity: low. Kind: correctness (demonstrated).
- Where: `Runner.cs:123-128` (the IRQ is taken before `before` is read at `:149`), `:153-159` (the NMI is taken between `before` and `profile.Add`).
- What: the 7 cycles of an IRQ are in `cpu.Cycles` and in no profile row. The 7 cycles of an NMI are added to the instruction that ran before it. A function's time starts after the 7 cycles in both cases.
- Evidence:

```
var ticks: u8
var n: u16
var vec: u16 @ $FFFE
interrupt fn tick() { ticks += 1 }
fn main() {
    vec = addr(tick)
    asm { cli }
    while n < 300 { n += 1 }
    asm { sei }
}
```

```
tern run --target c64 irq.tern --irq 500 --profile
Main returned: 6576 cycles (6557 in main), 12 interrupts
...
; total 6492 cycles          (6576 - 6492 = 84 = 12 x 7)
```

- Why it matters: the table's total does not match the run's cycles, with no line that says why. On the NES the cost lands on a random line of the main loop.
- Fix: count interrupt entries on their own (`Profile.InterruptCycles`) and print "; total 6492 cycles, and 84 taking 12 interrupts". Take the NMI's cycles out of the instruction before it.

### 11. Two C64 stand-ins differ from the KERNAL

- Severity: low. Kind: correctness (demonstrated).
- Where: `Machine.cs:147` (CHROUT's color), `:99-101` (LOAD's error).
- What:
  - CHROUT writes the color in `$0286` to color RAM, but treats 0 as "not set" and writes 14. Color 0 is black. The KERNAL sets `$0286` to 14 at startup; `Start` does not.
  - LOAD of a missing file sets the carry and leaves A. The KERNAL returns the error number in A (4, file not found).
- Evidence:

```
fn chrout(ch: u8 using A) @ $FFD2 keeps X, Y
var color: u8 @ $0286
var err: u8
var flag: u8
data name = "NOFILE"
fn main() {
    color = 0
    chrout('A')
    color = 1
    chrout('B')
    asm {
        lda #6
        ldx #name.lo
        ldy #name.hi
        jsr $FFBD
        lda #1
        ldx #8
        ldy #0
        jsr $FFBA
        lda #0
        ldx #0
        ldy #$C0
        jsr $FFD5
        sta err
        lda #0
        rol a
        sta flag
    }
}
```

```
tern run --target c64 col.tern --dump '$D800:2' --dump err --dump flag
$D800  0E 01      (a C64: 00 01)
err = 0           (a C64: 4)
flag = 1
```

- Why it matters: black text is common. A program that shows the KERNAL's error number shows 0.
- Fix: set `$0286` to 14 in `Start` and write it as it is. Set `cpu.A = 4` when the file is missing. Smaller, by reading: the Apple II's GET_FILE_INFO always gives storage type 1 (a seedling), also for files over 512 bytes; the Atari's cursor up at row 0 stays instead of wrapping.

### 12. `brk` always stops the run

- Severity: low. Kind: correctness (by reading).
- Where: `Runner.cs:146`.
- What: any `$00` opcode ends the run with "brk at $xxxx (often a jump into empty memory)". Spec 12.2 gives `brk` as a use of the IRQ vector on the NES, and `Cpu6502` implements it. A program with its own handler cannot use it under `tern run`. The opcode is read from `ram.Bytes`, not through the bus, so on the NES code in a RAM mirror (`$0800-$1FFF`) is read from the wrong place; the same holds for the `jsr` test at `:160`.
- Why it matters: small, but the help does not say it, and the emulator's `brk` is never reached from `tern run`.
- Fix: stop only when the vector at `$FFFE` is 0 or points outside the program; otherwise step. Read the opcode with `bus.Read`-free access that the machine provides (finding 1). Say it in the help.

### 13. A bad option value is reported as an unknown option

- Severity: low. Kind: correctness (demonstrated).
- Where: `Cli.cs:168-170`, `:232-236`, `:283-287`.
- Evidence:

```
tern run --target c64 a.tern --cycles abc     Unknown or incomplete argument: --cycles. Run 'tern --help' for the options.
tern --target c64 a.tern --max-errors 0       Unknown argument: --max-errors. ...
tern --json --target                          Unknown argument: --target. ...        (text, though --json was given)
```

- Why it matters: the project's rule is "say what is wrong, then how to fix it". `--cycles` is a known option; the value is wrong.
- Fix: comes with finding 5: "--cycles needs a whole number above 0, found 'abc'".

### 14. The help lists fewer stand-ins than the code has

- Severity: low. Kind: correctness (demonstrated by `tern --help`).
- Where: `Cli.cs:63-70`.
- What: the help says "On the Atari: CIOV for put bytes on IOCB 0 (the screen editor), and SETVBV. On the Apple II: COUT, HOME, the startup code's ROM calls, and the MLI's CLOSE and QUIT." The code also has, on the Atari, open, get bytes and close on IOCBs 1-7 for overlay files, and on the Apple II GET_PREFIX, SET_PREFIX, GET_FILE_INFO, OPEN, GET_EOF and READ. The help does not say that a stand-in takes no cycles, which matters for `tern bench` of code that prints.
- Fix: build this paragraph from each machine's `StandIns` text (finding 6), and add "a stand-in takes no cycles".

### 15. The bus cannot add cycles, so OAM DMA reaches into the CPU

- Severity: low. Kind: construction.
- Where: `NesBus.cs:58`, `:149-155`; `Runner.cs:95-96`; `Cpu6502.cs:38`, `:237-238`.
- What: `IBus` has `Read` and `Write` only. For the 513 cycles of OAM DMA, `NesBus` holds a `Cpu` property, set after both objects exist (`nes.Cpu = cpu`), and adds to the public field `Cpu.Cycles` from inside `Step`. If `Cpu` is null the cycles are dropped silently. `Step` returns the instruction's cycles without them, so its return value and `Cycles` disagree. The odd-cycle test uses the count before the instruction, not the cycle of the write.
- Why it matters: every public register and flag of `Cpu6502` is a mutable field, so nothing shows which code changes the CPU from outside. The X16's and C128's buses will want the same thing.
- Fix: give the bus a way to say it: `int StallCycles()` on an optional interface that `Step` asks after the instruction, or a `Stall(int)` method on `Cpu6502` that adds to both the return value and `Cycles`. Pass the CPU to the bus's constructor, or the bus to the CPU's, not both by a property.

### 16. A stand-in that copies past `$FFFF` would crash the run

- Severity: low. Kind: correctness (suspected).
- Where: `Machine.cs:103` (`ram.Load((ushort)at, file.Image)`), `:339-343` (the Atari's scroll and clear, from SAVMSC), `Cli.cs:249` (catches `InvalidOperationException` only).
- What: C64 LOAD with secondary address 0 and X/Y near the top of memory copies an overlay with `Span.CopyTo`, which throws `ArgumentException` when it does not fit. The Atari's `Clear` and scroll use `AsSpan(screen + ...)` with `screen` read from SAVMSC, which the program can change; single bytes are masked with `& 0xFFFF`, the spans are not. Neither exception is caught. Not demonstrated: `--set 88=255 --set 89=255` with a program that prints did not reach the scroll.
- Fix: clip the copy to memory and stop the run with a message ("LOAD of 'LEVEL1' at $FF00 goes past $FFFF"). The last catch of finding 2 covers the rest.

### 17. What no test covers in the emulator

- Severity: low. Kind: construction.
- Where: `Cpu6502.cs`, `InstructionSet.cs`; `tests/Tern.Cpu.Tests`.
- What: Klaus Dormann's functional test checks results and flags of the documented opcodes, with valid BCD in decimal mode. It does not check:
  - **Cycle counts.** `CpuTests` checks page crossing, branches and stores; `InstructionSetTests` checks a handful of opcodes. The rest of the cycle column has no independent check, and the compiler's listing and `tern bench` read the same column, so a wrong number would agree with itself everywhere.
  - **N, V and Z in decimal mode with invalid BCD** (Bruce Clark's test).
  - **The one-instruction delay of `cli`, `sei` and `plp`** before an IRQ is seen: `Irq()` tests `I` at once. Not modelled.
  - **Dummy reads.** `lda abs,x` with a crossing and `sta abs,x` read an address first on the chip. Not modelled, so a register with read side effects (`$2002`, `$2007`, `$4016`) is not touched. The double write of read-modify-write is modelled.
  - **Undocumented opcodes** stop the run with "illegal opcode"; the help does not say so.
  - **An NMI during `brk`**, and interrupts taken in the middle of an instruction's cycles.
- Why it matters: `tern bench` is the tool users trust for cycle counts. The header comment of `Cpu6502` ("exact cycle counts for documented opcodes") is the only statement of the limits.
- Fix: add a table test of all 151 cycle counts against a published list (for example the timing table in the 64doc or a JSON opcode list checked in as test data). List the limits above in the class comment and one line in the help.

### 18. Small things

- Severity: low. Kind: construction.
- `Runner.cs:119-162`: `0x20` (jsr) and `0x00` (brk) are bare numbers; the stand-in's return (`:136-138`) is `rts` written by hand next to a CPU that has it.
- `Runner.cs:73-74`: the comment names `C64Machine` and `AtariMachine` only.
- `Runner.cs:249` side: `Cli` catches `InvalidOperationException` from `Runner.Run` to report "a binary without main". Any other `InvalidOperationException` (a `First()` on an empty list) is shown as a user error. Use an own exception type, or check `HaltAddress` in `Cli`.
- `IndexChecker.cs:67`: `seen.At == cpu.PC` compares addresses, the rest of the class places (with the bank).
- `IndexChecker.cs:15`: `IndexError.ToString` prints `file::` when `Line` is null.
- `LineTable.cs:61`: `rows.Max(r => r.File.Length)` throws on an empty list; three lines below, `width` guards the same case.
- `RunReport.cs:101`: "(1 bytes)".
- `RunReport.cs:13`, `LineTable.cs:84`, `Runner.cs:8-66`: `MemoryDump` lives in `RunReport.cs`, `Profile` in `LineTable.cs`, `RunOptions`, `MemorySet` and `RunResult` in `Runner.cs`.
- `Apple2Machine.cs:76-77`, `:86`: `ram.Bytes[sp + 1]` does not wrap at `$01FF`; `Runner.cs:136` does.
- `NesBus.cs:63-73`: a cartridge with another mapper number is run as NROM without a word. Throw for a mapper the model does not have.
- `Cpu6502.cs:33`: `SP = 0xFD` at construction, and `Runner` sets `0xFF`; `Reset()` is used by tests only.

## Patterns

- **The C64 is the default.** `Machine.For`'s `_` arm, `RunResult.Platform`, `Bench.Measure`, `Runner.ScreenText(Ram)`. Each was right when there was one platform. An unknown platform should be an error, not a C64.
- **Lists kept in step by hand.** Stand-ins (constant, `Has`, `Call`, `StandIns`, comment, help); options (three parsers, help); JSON shapes (three anonymous types); output files (`Skip(4)`).
- **Local functions that capture everything.** `Cli.Run`, `Runner.Run`, and each machine's `Call` are long methods whose parts share ten or more locals by capture. The parts cannot be tested or reused alone.
- **Catch one exception type, by luck of the call.** `IOException` for files, `InvalidOperationException` for the runner, `FormatException` for parsers. Each misses a neighbor (findings 2 and 16).
- **Limits of the model stated in a class comment only.** The NES's vblank flag, `brk`, stand-ins that take no cycles, undocumented opcodes: all known to the code, none in the help that the user reads.

## Done well

- `InstructionSet` is one table that the encoder, the emulator and the listing share, with a duplicate check at startup. Cycle counts in a listing and in a run cannot drift apart.
- `Cpu6502` is short and readable. The NMOS details are there and commented: the `jmp ($xxFF)` wrap, the double write of read-modify-write, decimal flags from the binary result, `brk`'s padding byte, B only on the stack.
- The index check adds nothing to the program: it reads marks the compiler left on instructions, so a checked run and a real run execute the same bytes. One report per access, with a count.
- A stand-in that cannot do what was asked stops the run and says what it has ("CIO command $05 on IOCB 2, which the emulator does not have (stand-ins: ...)"). It never guesses.
- The Atari and Apple II stand-ins give the real error codes (`$AA`, `$A5`, `$81`, `$85`; `$46`, `$40`, `$43`, `$4C`) and keep the real quirks (the IOCB stays taken after a failed open; READ gives `$4C` only with nothing read).
- `NesBus` models the parts that programs get wrong: the shared `$2005`/`$2006` latch, the `$2007` read buffer and its palette case, palette mirrors, nametable mirroring, UxROM bus conflicts.
- `MemoryDump.Parse` and `MemorySet.Parse` give exact messages with an example, and `--dump` suggests near names.
- `Cli` lives in the library and takes its writers as parameters, so tests run the same commands the skill shows.
- `tern run`'s exit codes (0, 2, 3) are documented in the help and match the code.
- `BenchVariant.Place` maps a snippet's line back to `--decl N` or the snippet, so errors and index reports point at what the user typed.
