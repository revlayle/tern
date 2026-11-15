# Code quality review before the first release

A review of the whole C# codebase (not one milestone) for correctness and for how well the code is built for reading and maintenance, done 2026-11-14 and 2026-11-15 at `d223f12`, by five review sessions on Claude Fable, one per area. Each area's findings, with file and line, evidence and a suggested fix, are in its own file:

| Area | File | Findings | Correctness | Construction |
|---|---|--:|--:|--:|
| Backend: code generator, optimizer, allocator, assembler | [code-quality-backend.md](code-quality-backend.md) | 17 | 6 | 11 |
| Semantics: checker, types, constants, call graph | [code-quality-semantics.md](code-quality-semantics.md) | 22 | 13 | 9 |
| Syntax, compile-time interpreter, driver, platforms | [code-quality-syntax.md](code-quality-syntax.md) | 22 | 12 | 10 |
| Tools, CPU emulator, CLI | [code-quality-tools.md](code-quality-tools.md) | 18 | 10 | 8 |
| Tests | [code-quality-tests.md](code-quality-tests.md) | 14 | 9 (coverage) | 5 |
| **Total** | | **93** | **50** | **43** |

The test suite passed at the time of the review: 3,756 tests (3,720 compiler, 36 CPU). Nearly every correctness finding was demonstrated with a small program; the session that ran the reviews reproduced seven of them (backend 1 and 4, semantics 1 and 4, syntax 3 and 4, tools 3).

## The main conclusion

The bugs sit where two features meet: a `step` that is a struct field, a `using X` loop around an inner step with a call, an `indexed match` inside a Y loop, a pointer store from an `indexed` record array, a 16-bit operation into a volatile register. Each feature is tested well on its own (values, edges, wrap-around), but the tests do not vary the kind of operand or where it stands, the emulator starts with zero registers and zero memory (so a removed load or a missing clear passes), and volatile "exactly once" is checked only by counting listing lines for 8-bit reads.

The same cause shows in the construction findings: knowledge that must agree is written by hand in several places. What an instruction does to memory (eight places in the backend), which nodes a tree walk visits (nine walks in the checker), what each `tern run` stand-in is (four places), the CLI's options (three parsers and a help text). Two of the demonstrated backend bugs come from such copies disagreeing.

## Correctness: fix first

Bugs that give wrong code, crash the compiler or destroy files. Each needs an end-to-end test that fails before the fix.

| # | Bug | Where |
|---|---|---|
| 1 | A load of X or Y before an `indexed match` is removed as dead: a `using Y` loop around one gives wrong results | backend 1 |
| 2 | `p[i] = recs[j].f` with an `indexed` record array stores to the wrong address | backend 2 |
| 3 | A 16-bit volatile destination (`freq = w + k` on the SID) is used as scratch memory, writing in-between values to the hardware (spec 4) | backend 3 |
| 4 | A struct field as a `for` step reads the struct's address; the loop never ends | semantics 1 |
| 5 | A `using X` loop accepts a call in an inner loop's `step`, which overwrites X | semantics 2 |
| 6 | Calls in `for` bounds and in a `return` into the carry run right to left (spec 5) | backend 4 |
| 7 | The C64's free zero page lacks `$FB-$FE` (spec 12.2 and the guide promise it) | syntax 3 |
| 8 | `-o` can overwrite the source file, and one output can overwrite another | tools 3 |
| 9 | A `const fn` reading a single placed `data` value crashes the compiler (NullReferenceException) | semantics 4 |
| 10 | About 200 nested parentheses crash the compiler with an uncatchable stack overflow | syntax 4 |
| 11 | Paths that cannot be read or written end in a stack trace, with no JSON under `--json` (also `-o` into a missing folder) | tools 2 |
| 12 | Two `interrupt fn`s that call each other are dropped with no recursion error | semantics 5 |
| 13 | A constant expression with a typed constant is rejected where nothing gives a type (`var v = BOX * 30`, spec 3.1) | semantics 6 |
| 14 | A nested `const fn` error loses "(called from ...)" | syntax 2 |
| 15 | NES CHR data goes into the Mesen label file as RAM; `@ align` padding in NES ROM is `$00`, not `$FF` | syntax 5, 17 |

The other correctness findings are low: messages outside the convention, small CLI and stand-in differences, edge cases of constants and literals. They can go with the cleanup below.

## Construction: themes for a cleanup milestone

1. **One description per fact.** A single table of instruction effects (memory, registers, flags, control flow) used by the optimizer, the checker's assembly analysis and the assembler; one visitor for the syntax tree that every walk uses, so a new node is never skipped silently; one registry of stand-ins per machine; one option table for the CLI that also writes the help. (backend 8-10, semantics 14, tools 5-6)
2. **A CPU variant as a build setting.** Today nothing passes a variant to the backend, the checker or the emulator, and lists of branches, stores and transfers are hard-coded. This is the main obstacle to the 65C02 (M29). (backend 7, semantics 15, tools 4)
3. **Platforms as data, not tests on names.** "Has boards" stands for the NES, "cartridge" for NES output, an unknown platform gets the C64's stand-ins, and platform facts are spread over the driver. C64 cartridges, the X16 and the C128 need these in one place each. (semantics 16, syntax 6, tools 1)
4. **Long methods and state in fields.** `TernCompiler.Build` (450 lines, six jobs), `ParseProgram`, the checker's `Run`, CodeGen's per-function state over four files, parser and checker modes set and reset by hand. (syntax 1, 10, 20; semantics 18-19; backend 12, 14)
5. **Messages are not data.** Control flow decided by matching error text, an exception changed after it is thrown, the assembler reading meaning from strings. (semantics 17, syntax 7, 11, backend 13, 15)
6. **Ready for the language server (M28).** Tokens and nodes have no end position and comments are dropped; a compile reports through one exception with no check-only entry; deep nesting kills the process. (syntax 4, 7, 8; tools 7)
7. **Tests that find where features meet.** Vary the operand's kind and place, not only its value; start the emulator with random registers and memory; log bus accesses to check volatile reads and writes, 16-bit included; compare optimized against unoptimized runs; check error positions and whole messages; one shared listing reader instead of about 30 copies; run the feature suite on every platform. (tests 1-9)

## For the spec session

- **`@ region` in a module** gives "not declared"; spec 2.2 and 13 describe placed declarations in modules. Say whether a module may place declarations in the main file's regions. (semantics 3)
- **NES vblank flag timing** in `tern run` differs from hardware; spec 12.2 excludes timing within a frame, so confirm that, or model the flag's clearing. (tools 9)
- **Identifiers with Unicode letters** (`café`) are accepted and reach the label files. Say whether names are ASCII only. (syntax 12)
- **`brk` always stops `tern run`**, even with an IRQ handler set; say whether that is intended. (tools 12)
- **`for i in 0..256` counts with a `u16`**, though the counter only reaches 255, so `for i in 0..len(buf) using X` over a 256-byte array is an error. Spec 5.1 says the counter is `u8` "unless the range needs more"; say whether the type comes from the bounds as written or from the values the counter takes. (semantics 13)

## Done well

Each file ends with what to keep. In short: the emulator showed no instruction, flag or cycle bug on reading; code shapes and benchmarks are guarded by baselines; tests check values against C# computations across edge values; errors say how to fix the problem; the partial-class split keeps files under 1,000 lines; the documents (spec, decisions, status) make the reasons behind the code easy to find.
