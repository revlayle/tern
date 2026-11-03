# Tern

Tern is a small, readable programming language for 6502 processors that compiles to compact, predictable machine code. It does not match carefully hand-written assembly, but it narrows the gap and makes it easy to drop into assembly where a routine needs it. The compiler is written in C# and has its own assembler and cycle-exact 6502 emulator. The first target is the Commodore 64.

*Tern is a working name.*

```
import c64
import text

data title = screen"HELLO, TERN"

fn main() {
    c64.vic.border = c64.BLACK
    c64.vic.background = c64.BLUE
    text.put(c64.SCREEN + 12 * 40 + 14, addr(title), len(title))
    while true { }
}
```

Every line shows up in the listing with its bytes and cycles:

```
                                ;    7: c64.vic.border = c64.BLACK
$0814  A9 00     lda #$00              ; 2
$0816  8D 20 D0  sta c64.vic+32        ; 4
```

## Why Tern

- **Cost is visible.** Operators compile to a short, fixed instruction sequence. Anything that needs a loop (multiply, divide, block copies) is a library call, so you decide each time whether to spend those cycles.
- **No stack frames.** There is no recursion, so every variable has a fixed address. Functions that never run at the same time share memory, and zero page is allocated by how often variables are used.
- **Built for the 6502.** 8-bit types by default, arrays of 16-bit values and structs stored as separate byte tables for `abs,X` indexing, bools kept in CPU flags, register parameters, and loops that count down when they can.
- **Assembly mixes in cheaply.** Inline `asm` blocks and `asm fn` are parsed by the compiler, can name Tern variables and struct fields, and are never changed.
- **Whole-machine control.** Interrupt handlers that save only what they use, a `memory` block for the layout, binaries without `main` (a music player at `$C000` with a generated interface module), and overlays loaded from disk.

On nine benchmark tasks, Tern's code is about 30% larger and 54% slower than hand-written assembly, and smaller and faster than Prog8 and cc65 (`tests/benchmarks/comparison.md`). Hand-written assembly is still the way to go for the tightest inner loops; Tern lets you write those as `asm` inside a Tern program.

| | Tern | Assembly | Prog8 | cc65 |
|---|--:|--:|--:|--:|
| Bytes | 1,225 | 947 | 2,102 | 2,822 |
| Cycles | 500,014 | 324,419 | 698,705 | 1,453,115 |

## Getting started

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build
dotnet test
dotnet run --project src/Tern.Cli -- examples/hello.tern
```

The compiler writes `hello.prg` (load and run it on a C64 or in [VICE](https://vice-emu.sourceforge.io/)), a listing `hello.lst`, and a VICE label file `hello.vs`, and prints a memory report.

```
x64sc -autostart examples/hello.prg
```

## Documentation

| File | Contents |
|---|---|
| [`docs/guide.md`](docs/guide.md) | User guide: the language, the compiler, examples |
| [`docs/spec-v0.md`](docs/spec-v0.md) | The language specification |
| [`docs/decisions.md`](docs/decisions.md) | Design decisions and their reasons |
| [`docs/roadmap.md`](docs/roadmap.md) | Milestones done and planned |
| [`examples/`](examples) | Example programs: raster interrupts, structs, a sprite multiplexer with eight-way scrolling, overlays |

## Standard library

`math`, `mem`, `text`, `wide` (24/32-bit numbers), `bcd` (scores), `random`, and for the C64: `c64` (KERNAL calls, VIC/SID/CIA register structs, interrupt setup, loading), `sprites` (a multiplexer for up to 64 sprites) and `scroll` (eight-way smooth scrolling). The library is plain Tern and assembly in `src/lib/`, so it can be read and changed.

## Status

The compiler is complete through milestone M13 for the Commodore 64. Planned next (`docs/roadmap.md`): compile-time functions and code generators, a C64 disk and tape I/O library, emulator checks in test runs, a language server, and then new platforms: the 65C02 and Commander X16, the NES, the Apple II and Atari 8-bit computers.

## Project layout

```
src/Tern.Cpu/        instruction set table, encoder, cycle-exact 6502 emulator
src/Tern.Compiler/   lexer, parser, checker, code generator, optimizer, assembler
src/Tern.Cli/        the `tern` command-line tool
src/lib/             standard library (.tern), platform modules in src/lib/<platform>/
tests/               CPU tests, end-to-end compiler tests run in the emulator, benchmarks
examples/            example programs
```

## Third-party files

`tests/Tern.Cpu.Tests/TestData/6502_functional_test.bin` is from [Klaus Dormann's 6502 functional tests](https://github.com/Klaus2m5/6502_65C02_functional_tests), licensed GPL-3.0.
