# Tern user guide

<img src="../logo.svg" alt="Tern logo: a bird made of five purple triangles" width="120">

A guide to the Tern language and compiler as built for the first release (through milestone M27): the Commodore 64, the Atari 8-bit, the Apple II and the NES. The full rules are in [`docs/spec-v0.md`](../spec-v0.md), with the whole grammar in its [Appendix A](../spec-v0.md#appendix-a-grammar); this guide shows how to use them. Every example here compiles with the current compiler.

## What Tern is

Tern is a small language for the 6502 that compiles to compact, predictable machine code. Carefully hand-written assembly is still a little smaller and faster (on the benchmarks, Tern's code is about 2% larger and 5% slower), and Tern makes it cheap to write the routines that need it in assembly. It builds programs for the Commodore 64, the Atari 8-bit computers, the Apple II and the NES.

A few rules shape everything else:

- **Cost is visible.** An operator always compiles to a few fixed instructions. Anything that needs a loop (multiply, divide, block copy, variable shift) is a library function call, so you see the cost in the source.
- **No recursion, no stack frames.** Every variable has a fixed address. Functions that can never run at the same time share memory, so total RAM use is the deepest call path, like a stack, but with fast absolute addressing.
- **Predictable output.** Each construct has a documented code shape (spec section 11), and the compiler writes a listing with the bytes and cycles of every line.
- **Assembly mixes in cheaply.** Inline assembly is parsed by the compiler and can name Tern variables, fields and functions.
- **Tern trusts you.** It adds no hidden checks: arithmetic wraps, indexes are not checked, and a flag an `asm` block left can be a value. [10. How Tern programs behave](10-behavior.md) explains each of these with an example.

## Pages

Read chapters 1 to 3 in order, then the chapter for your machine; the others when you need them.

| Chapter | What it covers |
|---|---|
| [1. Getting started](01-getting-started.md) | Building, the targets, the files the compiler writes, running a program, errors and warnings |
| [2. The language](02-language.md) | Types, declarations, arrays, structs, pointers, control flow, functions, flags, interrupts, inline assembly, modules, encodings |
| [3. Memory](03-memory.md) | The memory map, the `memory` block, binaries without `main`, plug-ins, overlays and interfaces |
| [4. Compile-time code](04-compile-time.md) | `const fn` for tables, and generators that write assembly |
| [5. Standard library](05-library.md) | The platform-independent modules: `math`, `mem`, `text`, `wide`, `bcd`, `float`, `random`, `cpu` |
| [6. Commodore 64](06-c64.md) | The C64 target, its library (`c64`, `io`, `sprites`, `scroll`) and examples |
| [7. Atari 8-bit](07-atari.md) | The Atari target, its library (`atari`, `io`), display lists and interrupts |
| [8. Apple II](08-apple2.md) | The Apple II target under ProDOS, its library (`apple2`, `io`), disks |
| [9. NES](09-nes.md) | Cartridges, boards, tiles, interrupt vectors, ROM banks, the `nes` library |
| [10. How Tern programs behave](10-behavior.md) | The developer guide: what the machine does with your code, each behavior with an example |
| [11. Writing fast Tern](11-fast.md) | Measured advice, and measuring with `tern run` and `tern bench` |
| [12. Running and measuring](12-run-and-bench.md) | `tern run` and `tern bench` in full: options, output, input and frames, profiles, the index check, JSON, runs as tests |

## Not supported yet

`match` on 16-bit values (match on `.lo` / `.hi`); arrays of structs inside structs; a function with a body at a fixed address; cartridge images for the C64 and the Atari (on the NES every program is one). Each gives a clear error rather than a confusing one.

## After the first release

- **Doc comments (`///`) and a language server** (M28), which shows errors, types and costs in your editor.
- **Platform packages** (P1): each platform, the four built-in ones included, as a folder with a description file, startup code, `tern run` stand-ins in 6502 code and its library, through a published interface, so other developers can add 6502 targets.
- **The 65C02 CPU and the Commander X16** (M29), then **the Commodore 128 in native mode** (M30; C64 programs already run in its C64 mode).
- Not scheduled yet: calls through function variables (`var handler: fn() targets [idle, walk]`), import paths with subfolders and aliases, MMC1 for the NES, cartridges and RAM banks for the C64 and the Atari, a generator that also returns a value (such as the cycles of the code it emitted).
- Far off, after the compiler is complete: a standalone assembler extracted from Tern, with its generators, memory block and listing.
