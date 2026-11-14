# Tern

Tern is a small, readable programming language for 6502 processors that compiles to compact, predictable machine code. It comes close to carefully hand-written assembly and makes it easy to drop into assembly where a routine needs it. The compiler is written in C# and has its own assembler and cycle-exact 6502 emulator. It builds programs for the Commodore 64, the Atari 8-bit computers, the Apple II and the NES.

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
- **Assembly mixes in cheaply.** Inline `asm` blocks and `asm fn` are parsed by the compiler, can name Tern variables and struct fields, and are never changed. Compile-time functions build tables and can generate assembly.
- **Whole-machine control.** Interrupt handlers that save only what they use, a `memory` block for the layout, binaries without `main` (a music player at `$C000` with a generated interface module), overlays loaded from disk, plug-ins, and ROM banks on NES cartridges.

On nine benchmark tasks, Tern's code is about 2% larger and 5% slower than hand-written assembly, and smaller and faster than Prog8, cc65 and llvm-mos (`tests/benchmarks/comparison.md`):

| | Tern | Assembly | Prog8 | cc65 | llvm-mos |
|---|--:|--:|--:|--:|--:|
| Bytes | 963 | 947 | 2,102 | 2,822 | 1,359 |
| Cycles | 341,619 | 324,419 | 698,705 | 1,453,115 | 493,092 |

## Getting started

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build
dotnet test
dotnet run --project src/Tern.Cli -- --target c64 examples/c64/hello.tern
```

The compiler writes `hello.prg` (load and run it on a C64 or in [VICE](https://vice-emu.sourceforge.io/)), a listing `hello.lst` and a VICE label file `hello.vs`, and prints a memory report. The other targets are `--target atari` (a `.xex` for Altirra), `--target apple2` (a ProDOS `.system` file for AppleWin) and `--target nes` (a `.nes` cartridge image for Mesen).

```
x64sc -autostart examples/c64/hello.prg
```

## Documentation

| File | Contents |
|---|---|
| [`docs/guide/`](docs/guide/README.md) | User guide in chapters: the language, the compiler, each machine, how programs behave, writing fast Tern |
| [`docs/spec-v0.md`](docs/spec-v0.md) | The language specification |
| [`docs/decisions.md`](docs/decisions.md) | Design decisions and their reasons |
| [`docs/roadmap.md`](docs/roadmap.md) | Milestones done and planned |
| [`examples/`](examples) | Example programs for each machine: raster interrupts, a sprite multiplexer with eight-way scrolling, overlays, display-list interrupts, hi-res graphics, files, a scrolling NES game with code in two ROM banks |

## Standard library

`math`, `mem`, `text`, `wide` (24/32-bit numbers), `bcd` (scores), `float`, `random` and `cpu` on every machine, and each machine's own modules: `c64` (KERNAL calls, VIC/SID/CIA register structs, interrupt setup), `io` (files), `sprites` (a multiplexer for up to 64 sprites) and `scroll` (eight-way smooth scrolling) on the C64; `atari` (chip and OS registers, display lists, interrupts) and `io` on the Atari; `apple2` (text, hi-res graphics, ProDOS calls) and `io` on the Apple II; `nes` (PPU, sprites, the frame loop, controllers) and `uxrom` on the NES. The library is plain Tern and assembly in `src/lib/`, so it can be read and changed.

## Status

The compiler is complete through milestone M27, the four platforms of the first release. After the release (`docs/roadmap.md`): doc comments and a language server, then the 65C02 and the Commander X16, then the Commodore 128 in native mode.

## How Tern was made

Tern started on 2026-11-01 with a question: how close can a readable language get to hand-written 6502 assembly if it is designed around the processor instead of around C? The first commit holds the specification's first draft and the 6502 CPU library; the first working compiler followed the same day. The four platforms of the first release were done two weeks later, in about 800 commits.

**Agentic development.** Tern was built with Claude, Anthropic's AI model, working in [Claude Code](https://claude.com/claude-code). The developer set the goals and the design rules, settled every language decision, chose what to build next, and tested the results on real emulators; Claude drafted the specification text, wrote the compiler, the tests, the library and the documentation, and reviewed each milestone. The work runs in three kinds of sessions, each starting with no memory of the others (`docs/workflow.md`):

- A **spec session** discusses design with the developer. Each settled question goes into the specification and into `docs/decisions.md` with its date and reason: 123 dated entries so far.
- A **coding session** builds one milestone on its own branch, with end-to-end tests that compile a program, run it in the built-in emulator and check memory. Questions it cannot settle go to the spec session through `docs/status.md`, with the choice it made in the meantime.
- A **review session** checks the branch against the specification and the roadmap and writes its findings to `docs/reviews/` (42 reviews); the milestone is merged once they are fixed.

The files are the project's memory: a session that starts fresh reads the spec, the decisions, the roadmap and the status, and nothing is lost between sessions.

**The road to the first release:**

- **The core language** (M1-M6): expressions and control flow, pointers and data, inline assembly and KERNAL calls, modules and the first standard library, structs, `match`, interrupts.
- **Measuring against assembly** (M7-M9): an optimizer, then the same nine tasks written in Tern, hand-written assembly, Prog8 and C, all run and checked in the emulator. In the first README (2026-11-03) Tern's code was about 30% larger and 54% slower than the assembly.
- **The whole machine** (M10-M16): a cross-platform library, a C64 sprite multiplexer and scroller, the memory layout and binaries without `main`, overlays, disk I/O.
- **Closing the gap** (M17-M18): an analysis of every difference from the hand-written versions, sorted into what the optimizer could fix, what needed a different code shape and what needed assembly; the fixes brought Tern to within a few percent of the assembly.
- **Tools for coding agents** (M19-M20): JSON diagnostics, `tern run` and `tern bench` for running and measuring code in the emulator, a skill that teaches Tern to Claude, and index checks in test runs.
- **A real game** (from M20): Galaxy Stars, a C64 game the developer designed and had built in Prog8 the same way ([its story](https://github.com/revlayle/galaxy-stars)), was ported to Tern. The port found gaps the roadmap's examples had not, and each was settled in a spec session and built in a follow-up milestone.
- **More language** (M21-M24): plug-ins against a fixed interface, compile-time functions and assembly generators, several errors per compile, warnings, overlay interfaces, encodings declared by the program, and floats as a library.
- **New platforms** (M25-M27): the Atari 8-bit, the Apple II under ProDOS and the NES with ROM banks, each checked in Altirra, AppleWin and Mesen as well as in the built-in emulator.

Today the compiler has about 3,700 end-to-end tests and the CPU 36, including Klaus Dormann's 6502 functional test.

## Project layout

```
src/Tern.Cpu/        instruction set table, encoder, cycle-exact 6502 emulator
src/Tern.Compiler/   lexer, parser, checker, compile-time interpreter, code generator, optimizer, assembler
src/Tern.Cli/        the `tern` command-line tool
src/lib/             standard library (.tern), platform modules in src/lib/<platform>/
tests/               CPU tests, end-to-end compiler tests run in the emulator, benchmarks
examples/            example programs, in examples/<platform>/
```

## Third-party files

`tests/Tern.Cpu.Tests/TestData/6502_functional_test.bin` is from [Klaus Dormann's 6502 functional tests](https://github.com/Klaus2m5/6502_65C02_functional_tests), licensed GPL-3.0.
