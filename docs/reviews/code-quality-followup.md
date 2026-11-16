# Follow-up review of the code-quality fixes (R7)

Roadmap R7 (decisions 2026-11-15 and 2026-11-16): a verdict for each of the 93 findings of the code-quality review (`code-quality.md` and its five area files), for the findings R1-R5 handled. Done on `main` at `5c57180` (R1-R5 merged), 2026-11-16, by one review session on Claude Fable. Input: the area files, the finding tables in `docs/status.md`, each fix's diff and the code its finding names. Nothing else was read, and no new kind of finding was looked for.

Verdicts: **fixed**, **partly** (what is left is named, with who has it), **not fixed**, **left by decision** (with the date), **left for R6** (not read; R6's review checks it).

## Verdict

The fixes hold. All 15 ranked bugs and the other correctness findings the tables mark fixed behave as their findings ask; no fix was found broken, and no bug was found in a diff read. Of the 93 findings, 64 are fixed, 11 partly, 10 are left for R6 and 8 are not fixed. The 8, and a few parts of others, are construction findings that the R2 and R3 tables handed to R5 and that no milestone took: they need an owner or a decision (see "Must fix"), and none of them is wrong behavior.

## Progress

The work is in the roadmap's four steps, committed after each. All four are done; the table of the 93 verdicts follows, then the steps' details, then the must-fix items.

| Step | State |
|---|---|
| 1. Ranked bugs 1-15, demonstration programs run again | done |
| 2. Other correctness findings marked fixed | done |
| 3. Rows marked "partly" | done |
| 4. Construction searches (R4, R5) | done |

## The 93 verdicts

| | fixed | partly | not fixed | left for R6 | Total |
|---|--:|--:|--:|--:|--:|
| Backend | 12 | 0 | 3 | 2 | 17 |
| Semantics | 18 | 0 | 2 | 2 | 22 |
| Syntax | 15 | 4 | 2 | 1 | 22 |
| Tools | 15 | 3 | 0 | 0 | 18 |
| Tests | 4 | 4 | 1 | 5 | 14 |
| **Total** | **64** | **11** | **8** | **10** | **93** |

No finding as a whole is "left by a decision". Decisions shape six that count as fixed: backend 10 (checks instead of reserved registers, 2026-11-16), and semantics 3 and 13, syntax 12, tools 9 and 12 (2026-11-15); and one part of another: the `cld` on the 2A03 (backend 7, 2026-11-16, until M29). Every "not fixed" is a construction finding that no milestone took (step 3); none was marked fixed by a table. "Left for R6" rows were not read.

| Finding | Verdict | Note |
|---|---|---|
| backend 1 | fixed | ranked 1; step 1 |
| backend 2 | fixed | ranked 2; step 1 |
| backend 3 | fixed | ranked 3; step 1 |
| backend 4 | fixed | ranked 6; step 1 |
| backend 5 | fixed | step 2 |
| backend 6 | fixed | step 2 (by the diff) |
| backend 7 | fixed | step 4; `cld` left by decision 2026-11-16 (M29); an unused parameter |
| backend 8 | fixed | step 4 |
| backend 9 | fixed | step 4 |
| backend 10 | fixed | step 4; as decided 2026-11-16 |
| backend 11 | not fixed | no owner (step 3) |
| backend 12 | fixed | step 4 |
| backend 13 | left for R6 |  |
| backend 14 | fixed | step 4 |
| backend 15 | left for R6 |  |
| backend 16 | not fixed | no owner (step 3) |
| backend 17 | not fixed | no owner (step 3) |
| semantics 1 | fixed | ranked 4; step 1 |
| semantics 2 | fixed | ranked 5; step 1 |
| semantics 3 | fixed | step 2; decision 2026-11-15 |
| semantics 4 | fixed | ranked 9; step 1 |
| semantics 5 | fixed | ranked 12; step 1 |
| semantics 6 | fixed | ranked 13; step 1 |
| semantics 7 | fixed | step 2 |
| semantics 8 | fixed | step 2 |
| semantics 9 | fixed | step 2 |
| semantics 10 | fixed | step 2 |
| semantics 11 | fixed | step 2 |
| semantics 12 | fixed | step 2; the fallback message is R6's |
| semantics 13 | fixed | step 2; decision 2026-11-15 |
| semantics 14 | fixed | step 4 |
| semantics 15 | fixed | step 4 |
| semantics 16 | left for R6 |  |
| semantics 17 | left for R6 |  |
| semantics 18 | fixed | step 4 |
| semantics 19 | fixed | step 4 |
| semantics 20 | fixed | step 4 |
| semantics 21 | not fixed | no owner (step 3) |
| semantics 22 | not fixed | no owner (step 3) |
| syntax 1 | fixed | step 4; the platform branches and the retry's catch are R6's |
| syntax 2 | fixed | ranked 14; step 1 |
| syntax 3 | fixed | ranked 7; step 1 |
| syntax 4 | fixed | ranked 10; step 1 |
| syntax 5 | fixed | ranked 15; step 1 |
| syntax 6 | left for R6 |  |
| syntax 7 | partly | step 3; the rest is R6's |
| syntax 8 | fixed | step 4; the rest is M28's |
| syntax 9 | not fixed | no owner (step 3) |
| syntax 10 | fixed | step 4 |
| syntax 11 | partly | step 3; the rest is R6's, one part with no owner |
| syntax 12 | fixed | step 2; decision 2026-11-15 |
| syntax 13 | partly | step 2; the rest is M29's |
| syntax 14 | fixed | step 2 |
| syntax 15 | fixed | step 2 |
| syntax 16 | fixed | step 2 |
| syntax 17 | fixed | ranked 15; step 1 |
| syntax 18 | fixed | step 2 |
| syntax 19 | partly | step 2; the rest is R6's |
| syntax 20 | fixed | step 4; one part waits for syntax 9 |
| syntax 21 | fixed | step 4 |
| syntax 22 | not fixed | no owner (step 3) |
| tools 1 | partly | step 3; the rest is R6's |
| tools 2 | fixed | ranked 11; step 1 |
| tools 3 | fixed | ranked 8; step 1 |
| tools 4 | fixed | step 4; cycles per variant are M29's |
| tools 5 | fixed | step 4 |
| tools 6 | fixed | step 4 |
| tools 7 | partly | step 3; the rest is R6's |
| tools 8 | fixed | step 2 |
| tools 9 | fixed | step 2; decision 2026-11-15 |
| tools 10 | fixed | step 2 |
| tools 11 | fixed | step 2 |
| tools 12 | fixed | step 2; decision 2026-11-15 |
| tools 13 | fixed | step 2 |
| tools 14 | fixed | step 2 |
| tools 15 | fixed | step 4 |
| tools 16 | fixed | step 2 (by its tests) |
| tools 17 | fixed | step 2 |
| tools 18 | partly | step 3; the rest has no owner |
| tests 1 | fixed | step 2 |
| tests 2 | fixed | step 2 |
| tests 3 | fixed | step 2 |
| tests 4 | left for R6 |  |
| tests 5 | left for R6 |  |
| tests 6 | partly | step 3; the rest is R6's |
| tests 7 | partly | step 3; the rest is R6's |
| tests 8 | fixed | step 2 |
| tests 9 | left for R6 |  |
| tests 10 | partly | step 3; the rest has no owner |
| tests 11 | left for R6 |  |
| tests 12 | partly | step 3; question 174 |
| tests 13 | not fixed | "R5 or R6" in R1's table; R5 did not take it and R6's scope does not name it (step 3) |
| tests 14 | left for R6 | the cycle table's check is done (tools 17) |

## Step 1: the ranked bugs

Each demonstration program of the area file was compiled and run again with `tern` / `tern run` built from `main` (debug build). All 15 now behave as the finding says they should.

| # | Finding | Verdict | What was run, and the result |
|--:|---|---|---|
| 1 | backend 1 | fixed | The program prints `out = 18` (was 28). |
| 2 | backend 2 | fixed | `out = 77` (was 0). |
| 3 | backend 3 | fixed | Listing: `freq = w << 1` shifts in a temporary and writes each byte of `freq` once; `freq = w + k` is `lda w / adc k / sta freq / lda w+1 / adc k+1 / sta freq+1`, no read of `freq`; `port <<= 2` is `lda port / asl a / asl a / sta port`. |
| 4 | semantics 1 | fixed | The loop ends with `out = 5`. Also run with `step list[k].m` (a `linear` record array) and `step p.m` (a pointer to a record), field at offset 1: 5 each. |
| 5 | semantics 2 | fixed | Error: "The loop body calls a function, which can change X. Remove 'using X', ...". |
| 6 | backend 4 | fixed | `for i in f()..g()` gives `out = 10`; `return f() >= g()` into the carry gives false. |
| 7 | syntax 3 | fixed | The error for 150 bytes says "146 are free"; a program with 142 bytes and two pointers in `@ zp` builds. |
| 8 | tools 3 | fixed | `-o b.tern`, `-o B.TERN` and `-o ./x/../b.tern` are refused ("would overwrite the source file") and the source is unchanged; `-o c.lst`, `-o x.vs`, `-o x.lines` and a binary with `-o lib1_api.tern` give "Two outputs would be written to ...". |
| 9 | semantics 4 | fixed | With the data at `$9000` (inside the program area; `$C000` now gets the memory-map error first) the const fn reads 5 and the program prints `out = 6`. No crash. |
| 10 | syntax 4 | fixed | 200 bracket pairs compile; 300 pairs, a sum of 5,000 terms, 5,000 unary minus signs, 400 nested `if` blocks and 400 nested indexes each give "This is nested more than 256 deep ...", with JSON under `--json`. `Compile` and `Check` both run on the 64 MB stack (`TernCompiler.OnLargeStack`). |
| 11 | tools 2 | fixed | Every line of the finding now gives a one-line message and exit code 1, as JSON with `--json`: a missing output folder, `-o` a folder, a folder as source (compile, run, bench), an empty file name. `tern bench --fn bump` on a binary without `main` is a failed variant with the message. |
| 12 | semantics 5 | fixed | "Recursion is not allowed: a -> b -> a". The finding's "more generally" (a function no program reaches that calls itself is not reported) is unchanged, as the table says; an unreached function is not compiled, so no wrong code comes of it. |
| 13 | semantics 6 | fixed | `var v = BOX * 30` and `for i in 0..BOX * 30` compile; `out = 780` (390 + 390). |
| 14 | syntax 2 | fixed | Both errors carry the note: "(called from y2.tern:7)" and "(called from y2.tern:8)". |
| 15 | syntax 5, 17 | fixed | The label file has `NesChrRom:0000:bg` and `NesInternalRam:0000:out`; `tern run --dump bg` says the data is in the PPU's memory. The 169 padding bytes before `@ align(256)` data in the `.nes` file are `$FF`. |

## Step 2: the other correctness findings marked fixed

Demonstration programs run again where the area file has one; otherwise the fix's diff or its test was read or run, as the row says.

| Finding | Verdict | What was run, and the result |
|---|---|---|
| backend 5 | fixed | `a[a[0].lo] = b[k] + c[k]` gives `out = $0102` (was `$0002`). |
| backend 6 | fixed | Not reachable from source, so nothing to run; the diff (`9208e14`) adds the label's offset to the grown branch's `jmp`, as the finding asks. |
| semantics 3 | fixed (decision 2026-11-15) | The module's `noinit var buf: [u8; 16] @ buffers` compiles and the program prints `out = 7`. |
| semantics 7 | fixed | `70000 as u16` is `$1170`, `70000 as u8` is `$70`, `-70000 as u16` is `$EE90`. |
| semantics 8 | fixed | With `const BASE = $C0`: `BASE.hi` is 0 and `BASE.lo` is `$C0`. A typed `u8` constant's `.hi` is still the error, as intended. |
| semantics 9 | fixed | The program is now the error "An array of u8 has at most 256 elements" with the `const` line too, so the order of declarations no longer matters. |
| semantics 10 | fixed | "'lda (address),y' needs a pointer in zero page, but 'msg' is data, not a pointer. Declare one, ...". |
| semantics 11 | fixed | "'helpr' is not a function. Check the spelling, or declare it". |
| semantics 12 | fixed | All five messages now end in a fix sentence, and the pointer one compiles as written ("Convert with 'as u16' first, then 'as ptr u8'"); the two planned features say "is not supported yet". The fallback for types with no conversion is R6's, as the table says. |
| semantics 13 | fixed (decision 2026-11-15) | `for i in 0..256 using X` and `for i in 0..len(buf) using Y` over a 256-byte array compile and fill the array; a plain `0..256` loop runs 256 times. |
| syntax 12 | fixed (decision 2026-11-15) | `café`, `αβ` and a name with an Arabic-Indic digit each give "'é' (U+00E9) cannot be in a name: names use A-Z, a-z, 0-9 and '_'". |
| syntax 13 | partly | The program now has an error at the declaration: "'dec' is a 6502 instruction, so an asm line cannot call a generator of that name. Use another name". Left: the mnemonic set per CPU variant, for M29 (see step 3). |
| syntax 14 | fixed | "'a' is declared with 4 elements, but its value has 2 (called from y14.tern:5). Write [int; _], or give 4 values". |
| syntax 15 | fixed | The two statements compile; `a = 1`, `b = 2`. |
| syntax 16 | fixed | "The character U+E042 has no code in any encoding. Write the byte it stands for as \$NN". |
| syntax 18 | fixed | One error ("'noinit' is for variables; data is never zeroed. Remove 'noinit'"). |
| syntax 19 | partly | The three named messages are fixed (`ascii` "is not supported yet", `emit {` outside a const fn, "Tern has no '0x'"). Left: any other `name {` at the start of a statement (`foo { lda #1 }`) still gets "Expected '=' and the value of 'lda', found '#'", with no fix sentence; the table gives it to R6. |
| tools 8 | fixed | `tern bench --json --cycles 1000 --code "while true { }"` gives `"ok": false` and exit code 1; `--dump nosuch` gives exit code 1. |
| tools 9 | fixed (decision 2026-11-15) | The program gives `early = 0`, `stale = 0`, `late = 0`. The NMI at once and the `$2006` second write are covered by `RunModelTests` (run: 19 pass). |
| tools 10 | fixed | "; total 6492 cycles, and 84 taking 12 interrupts" under a run of 6576 cycles. |
| tools 11 | fixed | `$D800` holds `00 01`, `err = 4`, `flag = 1`. The two smaller ones (Apple II storage type, Atari cursor) by their tests in `RunModelTests`. |
| tools 12 | fixed (decision 2026-11-15) | On the NES, a program with `data irq_vector: u16 @ $FFFE = addr(on_brk)` runs its handler for both `brk`s (`breaks = 2`) and returns; on the C64 a `brk` stops the run with exit code 2. |
| tools 13 | fixed | "--cycles needs a whole number above 0, found 'abc'"; "--max-errors needs a whole number from 1 to 2147483647, found '0'"; `--json --target` answers in JSON. |
| tools 14 | fixed | The help names the Atari's overlay reads on IOCBs 1-7 and the MLI's calls, and says "a few ROM routines have stand-ins, which take no cycles". |
| tools 16 | fixed | Not run by hand; `RunModelTests.A_load_past_ffff_stops_the_run` and `The_atari_screen_scrolls_and_wraps_at_ffff` pass, and the CLI's last catch (tools 2) covers the rest. |
| tools 17 | fixed | The finding's fix was a table test of the cycle counts and the limits written down: `InstructionSetTests.Every_opcode_has_the_datasheets_cycles` checks all 256 opcodes, and `Cpu6502`'s comment and the help name the limits. The parts not modelled (invalid BCD flags, the `cli`/`sei` delay, dummy reads) stay not modelled, now stated. |
| tests 1 | fixed | `OperandMatrixTests` puts each operand form (field, element, indexed element, pointer target, call) in each place (loop start, end, step, match value, index, argument, return, into the carry), with `OperandMatrix16Tests` for `u16`. Ranked bugs 1, 2, 4, 5 and 6 are in it. |
| tests 2 | fixed | `Run` and `Library` start from `StartState` (seeded memory, registers not zero); no test asks for a zero start (`zeroStart: true` is used nowhere), and `tern run` starts the same way (question 171). |
| tests 3 | fixed | `BusLog` counts the CPU's reads and writes; `VolatileAccessTests` covers 16-bit volatile targets. |
| tests 8 | fixed | `Run.CompareWithPlain` runs a program without the optimizer and compares. |

## Step 3: the rows marked "partly", and what the tables hand on

What is left of each finding and who has it, from the tables in `docs/status.md`. The done part was checked only where a row says so. "No owner" means no milestone's scope in the roadmap and no decision covers the rest; these are collected under "Must fix" below, as items that need an owner or a decision, not as bugs.

### Partly fixed

| Finding | Done | Left | Who has it |
|---|---|---|---|
| syntax 7 | `TernCompiler.Check` returns its errors and never throws a `CompileError` (R5). | `CompileError` still carries the other errors and the warnings (`All`, `WithWarnings`) when `Compile` throws it. | R6 (messages as data: "no exception changed after it is thrown"). |
| syntax 11 | The "(called from " text test is gone (syntax 2). | `WithNote` still places the note by the first `". "` of the message; `ReadOnly` as text; `CountsUp` builds an error to take its text (`Interpreter.cs:233`). | R6. The finding's last part, the interpreter's unchecked casts and `!` with no statement of what the checker guarantees, is named in no table: no owner. |
| syntax 13 | An error at the declaration of a generator named like a mnemonic (run in step 2). | The mnemonic set is the enum's, for every variant; a mnemonic the target lacks would get "has no ... mode". | M29, where a second variant first exists. |
| syntax 19 | The three messages (run in step 2). | Any other `name {` at the start of a statement. | R6. |
| tools 1 | The stand-in registry (R4). | The run model as one description per platform. | R6. |
| tools 7 | `CheckResult.Diagnostics` without a `catch` (R5). | The JSON shapes as records; `Diagnostic` with an end, a code and a severity; the suggestion cut out by `FixVerbs`; two copies of the conversion. | R6. |
| tools 18 | The four output bugs: "(1 byte)", `file::`, the empty line table, the MLI's return address across `$01FF` (their tests failed before; `--dump '$C000:1'` run here). | Bare opcodes in `Runner`; `InvalidOperationException` for a binary in `Cli`; `seen.At` by address in `IndexChecker`; types in the wrong files; an unknown mapper run as NROM; `SP = $FD` at construction. | No owner. |
| tests 6 | Every `Cpu6502` in the tests takes the platform's variant (R4). | The default target in the helpers, a shape-test trait, named scratch addresses. | R6. |
| tests 7 | One `Listing` reader and `Count` (R1). | A shared `TempFolder` and `Cli.Tern`. | R6. |
| tests 10 | Conversions of constants (`as`, `.lo`/`.hi`) have tests (`ConstantExpressionTests`, R2). | A precedence theory (spec 3.6) and the runtime conversion table (3.5): no `PrecedenceTests` or `ConversionTests` file exists. | No owner. |
| tests 12 | `Run.Program(standIns: true)` runs with the platform's `Machine`; the traps that copied a stand-in are gone (R4). | The three fake ROMs (`FakeKernal`, `FakeCio`, `FakeMli`); the real-emulator runs. | Question 174 in `docs/status.md`, open for the spec session. |

### Fixed, with a part handed on

| Finding | Left | Who has it |
|---|---|---|
| backend 7 | The unconditional `cld` in an `interrupt fn` on the 2A03. | Left by decision 2026-11-16, until M29. |
| backend 7 | The unused `platform` parameter of `Allocator.Allocate`. | No owner (one line). |
| tools 4 | A cycle count per variant. | M29. |
| syntax 1 | The platform branches in the builder; the retry's `catch` on any `CompileError`. | R6 (with syntax 6 and backend 15). |
| syntax 8 | `MatchArm`, `MatchPattern` and `FieldValue` are not nodes; parameters, fields and asm lines have no span. | M28, which is the user of the spans; not written in a table. |
| syntax 20 | The loosened statement end after an error lasts to the end of the file, not one statement. | Depends on syntax 9, which has no owner. |
| semantics 5 | A function no program reaches that calls itself is not reported. | No owner; no wrong code comes of it, since such a function is not compiled. |
| semantics 12 | The fallback "Expected X, found Y. Use a X here" for types with no conversion. | R6. |
| tests 8 | Programs with `addr(` or `noinit` and pointer variables are not compared. | By design: their addresses differ between the two builds. |

### Handed to R5 by the R2 and R3 tables, and taken by no milestone

The tables of R2 and R3 mark these "not fixed: R5". R5's roadmap scope does not list them, R5's table has no row for them, and R6's scope (platforms and messages as data, the rest of test theme 7) does not cover them. All are construction findings; none describes wrong behavior. They were not read again here.

| Finding | What it is |
|---|---|
| backend 11 | Backend state kept in checker symbols and the AST (`Ast.cs` depends on `Semantics`). |
| backend 16 | Small leftovers. |
| backend 17 | Unnamed limits, repeated work. |
| semantics 21 | The call graph built as a side effect of checking, in three files. |
| semantics 22 | Small things. |
| syntax 9 | Statement ends decided in the lexer and patched in the parser (syntax 15, its one demonstrated bug, is fixed). |
| syntax 22 | Small things. |
| tests 13 | `Run.Program` does everything in its constructor ("R5 or R6" in R1's table; with tools 1 it fits R6's run model, but R6's scope does not name it). |

## Step 4: the construction themes of R4 and R5

Checked by searching `src/` for what should be gone, not by reading the files. The guard tests the tables name were run (`InstructionsTests`, `MemoryUseTests`, `SyntaxTreeTests`, `CliTests`, `CheckTests`, `LanguageServerTests`, `BenchmarkTests`, `SkillTests`, the three tests of semantics 20, the DMA test of tools 15: all pass).

| Finding | Verdict | The search, and what it found |
|---|---|---|
| backend 7, semantics 15, tools 4 (the CPU variant) | fixed | No list of branches, stores, loads or transfers is left in the backend or the checker: a search for mnemonic lists (`X or Y or ...`, `case Mnemonic.`) over `src/Tern.Compiler` finds only peephole patterns that match one shape (`Lda or Txa or Tya` as "the next load", `Beq or Bne` in one rewrite) and `Jsr or Jmp` in the checker's call test. `AsmEffects.Of` is gone. Two small lists remain, both for M29 with syntax 13: the parser's `asl / lsr / rol / ror` for the `a` operand (`Parser.Statements.cs:345`; the 65C02 adds `inc a` and `dec a`), and `Inx or Iny` / `Dex or Dey` as plus and minus one in the register tracking (`Optimizer.Registers.cs:191`). |
| backend 8 (memory effects) | fixed | `MemoryUse.cs` is the one place; the optimizer files name `Sta` or `Lda` only inside rewrite patterns. |
| backend 9 (control flow) | fixed | `ExitsOf` is used by the analyses (9 places); `Dispatches` is gone. |
| backend 10 (predictions) | fixed (decision 2026-11-16) | `CodeGen.Checks.cs` holds the `[Conditional("DEBUG")]` checks, called from the generator's files. The review's other fix, reserving registers, is left by that decision. |
| semantics 14 (tree walks) | fixed | `Node.Children` and `Descendants` exist and the walks use them; `Lowering` throws on an unknown node. The walks left by hand are the ones the table lists, plus one it does not: `Unreachable`'s `Inner` (`Checker.Warnings.cs:310`), a switch over the statements that hold blocks, with no default. A new statement kind with a block would get no "never reached" warning inside it; no wrong code. |
| semantics 20 (rules written twice) | fixed | By its three tests, which pass. |
| syntax 21 (contextual words) | fixed | No comparison of a token's text with a memory or modifier word is left (`Text == "linear"` and the like: none). |
| tools 5 (option parsers) | fixed | Each option string is in `Cli.Options.cs` once, and read by name in `Cli.Commands.cs`. |
| tools 6 (stand-ins) | fixed | Each ROM address is in its machine's `Routines` once. (`TernCompiler.Apple2.cs:17` names the same Apple II addresses for the startup code's calls: a platform fact, R6's.) |
| syntax 1, 10; semantics 19; backend 12 (long methods) | fixed | Method lengths measured over `src/` by a script: none is over 300 lines, and the 12 over 100 are exactly those listed in `docs/status.md` (Known limitations), with the same lengths (`Lexer.Tokenize` 239 with its local `Next` 153, `Cpu6502.Step` 160, `InstructionSet.Nmos` 130, `CheckCall` 116, `GenCall` 116, `ParsePrimaryBody` 113, `CheckBinary` 108, `Interpreter.Exec` 105, `CheckFor` 103, `ReadCopiesFromSource` 103, `GenMatch` 101). |
| syntax 20 (parser modes) | fixed | `Mode<T>` exists; no `_inAsm =`, `_inHeader =` or `_inConstFn =` assignment is left outside it. |
| semantics 18 (context in fields) | fixed | `BodyContext` exists; `_allowLongBytes` and the list-length field are gone. |
| backend 14 (per-function state) | fixed | `FunctionContext` exists (`CodeGen.cs:36`). |
| syntax 8 (end positions) | fixed | `Node.Span`; `LanguageServerTests` pass. What is left is in step 3. |
| tools 15 (bus cannot add cycles) | fixed | `Cpu6502.Stall`, called by the NES bus for DMA; no `Cycles +=` from outside the CPU. |

## Must fix

No finding that R1-R5 marked fixed is not fixed or broken again: the four steps found no bug, in the fixes' behavior or in the diffs read.

What is open is ownership (step 3): findings and parts the tables handed on that no milestone or decision has. Each needs one of: a line in R6's scope, a later milestone, or a decision to leave it, recorded in `docs/decisions.md`. Most useful first:

1. **tests 10, the rest:** no precedence tests (spec 3.6) and no runtime conversion table (3.5). R3 and R5 changed the parser (the nesting count, the mode scopes, `ParseProgram`) with no test of this kind to guard it, and R6 does not add one. A coverage item for `r7-fixes`, small.
2. **tools 18, the rest:** an unknown mapper run as NROM without a word and `Cli` reading any `InvalidOperationException` as "a binary without main" are the two with a user-visible effect; the other four are tidying.
3. **syntax 9** (with the part of syntax 20 that waits for it): statement ends in the lexer. A design change in the lexer and parser; a decision to leave it until M28 needs it is reasonable.
4. **backend 11, semantics 21, tests 13:** structure findings R5 did not take.
5. **backend 16 and 17, semantics 22, syntax 22**, the unused `platform` parameter of `Allocator.Allocate` (backend 7), the interpreter's unstated guarantees (syntax 11), the unreached self-recursive function (semantics 5), and the one tree walk still without a default, `Inner` in `Checker.Warnings.cs` (semantics 14, step 4): small things.
