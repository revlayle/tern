# Tern: a programming language for 6502 processors

Tern (working name) is a small, readable language that compiles to efficient 6502 machine code. The compiler is written in C# and has its own assembler and 6502 emulator. The first target is the Commodore 64; NES, Commander X16, Apple II and Atari 8-bit are planned.

## Key documents

| File | What it is | Owner |
|---|---|---|
| `docs/spec-v0.md` | The language specification. The source of truth for language behavior. | spec session |
| `docs/decisions.md` | Design decisions and their reasons. Read before proposing a language change. | spec session |
| `docs/roadmap.md` | Milestones: scope, spec sections, "done when" criteria. | spec session |
| `docs/status.md` | Current state, handoff notes, known limitations, questions for the spec session. | coding session |
| `docs/reviews/` | Review findings, one file per milestone. | review session |
| `docs/workflow.md` | How the spec, coding and review sessions work together. | spec session |

Each session edits only the files it owns (see `docs/workflow.md`). Start a session with `/spec`, `/code`, `/review` or `/merge`.

## Layout

```
src/Tern.Cpu/            instruction set table, encoder, cycle-exact 6502 emulator
src/Tern.Compiler/
  Syntax/                lexer, AST, parser
  Semantics/             symbols, types, checker (names, types, constant folding, call graph)
  Backend/               code generator, allocator (zero page, shared frames), assembler
  Platform.cs            target machine descriptions (C64)
  TernCompiler.cs        driver: source -> .prg, listing, labels, report
src/Tern.Cli/            `tern` command-line tool
src/lib/                 standard library source (.tern); platform modules in src/lib/<platform>/; copied to lib/ next to the compiler
tests/Tern.Cpu.Tests/    CPU tests, including Klaus Dormann's 6502 functional test
tests/Tern.Compiler.Tests/  end-to-end tests: compile, run in the emulator, check memory
examples/                example .tern programs
```

## Commands

```
dotnet build
dotnet test                                              # all tests, about 2 seconds
dotnet run --project src/Tern.Cli -- examples/border.tern  # writes .prg, .lst, .vs next to the source
```

To check a program on a real emulated C64 (VICE is installed at `C:\Vice\bin`):

```
C:\Vice\bin\x64sc.exe -default -warp -autostart file.prg -limitcycles 8000000 -exitscreenshot shot.png
```

## Design rules

These come from the spec and `docs/decisions.md`. Apply them to every change.

1. **Cost is visible.** Operators compile to a short, fixed instruction sequence. Anything that needs a loop or a runtime routine is a library function call, never an operator.
2. **Restriction is acceptable.** If a feature cannot compile to efficient code, leave it out.
3. **One meaning per symbol and keyword.** Exceptions: unary `+`/`-`, and assembly blocks.
4. **Predictable code shapes.** Spec section 11 lists the intended output for each construct. Generated code must match it or be shorter.
5. **No recursion, no stack frames.** Every variable has a fixed address; functions that never run at the same time share memory.

## Coding conventions

- Every language feature gets end-to-end tests in `tests/Tern.Compiler.Tests` that compile a program, run it in the emulator (`Run.Program`) and check memory. Test edge values (0, 255, 256, -128, wrap-around) and compare against results computed in C#.
- Every new compile error gets a test, in the feature's test file (`AsmTests`, `PointerTests`, ...), or in `ErrorTests.cs` for errors with no feature file (lexer, parser, general type errors). Error messages say what is wrong and how to fix it.
- When code shape matters, check the listing (`result.Listing`) in a test.
- A spec keyword that is not implemented yet must give "'x' is not supported yet", not a confusing parse error.
- No new NuGet dependencies without asking.
- Commit messages: a short summary line, then what changed and why.
