# Code quality review: Tests

Scope: `tests/Tern.Compiler.Tests` (118 files, about 34,000 lines) and `tests/Tern.Cpu.Tests` (4 files). Reviewed at commit d223f12. The helpers (`Run.cs`, `Library.cs`, `Results.cs`), `BenchmarkTests`, `VolatileTests`, `DeadLoadTests`, `FunctionalTest` and `InstructionSetTests` were read in full, `ComparisonTests`, `SkillTests`, `FixSentenceTests`, `RepositoryWarningTests`, `EvaluationOrderTests`, `VariableStepTests`, `ProgramTests`, `OptimizerTests`, `ErrorTests` and `NoinitTests` in part. For the other files the test names and the lines found by search were read. Counts come from `grep` over the whole suite.

Test run: `dotnet test` passed. 3,756 tests, 0 failed, 0 skipped (36 in `Tern.Cpu.Tests`, 1 s; 3,720 in `Tern.Compiler.Tests`, 8 s).

## Overview

- **`Run.Program`** (`Run.cs`) compiles a source string, loads the image, and runs the emulator until main returns. Options: a cycle limit, a memory setup action, IRQs every n cycles, traps that stand in for ROM routines, the platform, NES buttons. It fails a run when the emulator's index check reports an index outside its table. Results are read by symbol name (`r.U8("x")`). 615 calls in 96 files.
- **`Library`** (`Library.cs`) compiles the standard library once and calls single routines with given registers. **`Results`** reads benchmark results the same way for Tern, assembly, Prog8 and C.
- **Feature tests**: 1,152 facts and 268 theories (1,913 `InlineData` rows, 19 `MemberData` sources). Programs are raw string literals. Most files hold run tests, listing tests and an error theory for one feature or one milestone item.
- **Error tests**: mostly theories `Reports_errors(source, expected)` that check a part of the message (43 such methods).
- **Listing tests**: a private `Code(run, function)` helper per file returns the instructions of one function as one string, compared with an expected string.
- **Baselines**: `BenchmarkTests` (bytes and cycles of nine programs against `tests/benchmarks/baseline.txt`), `SkillTests` (the skill's examples and tool outputs), `RepositoryWarningTests` (every example compiles with no warning). `ComparisonTests` and `VariantTests` write reports when `TERN_COMPARE=1`.
- **CPU tests**: Klaus Dormann's functional test, 13 timing and behaviour tests, encoder round trips.

Findings: 9 coverage (3 high, 5 medium, 1 low) and 5 construction (2 medium, 3 low).

## Findings

### 1. Tests vary the values, not the kind of operand or where it stands

- Severity: high. Kind: coverage.
- Where: `VariableStepTests.cs:38-60`, `MatchTests.cs:347`, `EvaluationOrderTests.cs:42-158`, `ProgramTests.cs:206-290`; the pattern holds across the suite.
- What: a feature test is thorough in values (0, 255, wrap-around, three counter registers, constant and variable bounds) and uses one kind of operand, usually a global or a local. `VariableStepTests` runs 16 ranges with 6 loop forms each, and the step is always the local `n`. The suite has no `step` that is a struct field, an array element or a call (search for `step x.y`, `step a[i].f`, `step f()`: none). `MatchTests` has one `indexed match` inside a loop, `using X`, starting at 0. `EvaluationOrderTests` lists statements by hand; `for f()..g()` and `return f() >= g()` into the carry are not in the list. `indexed var` appears in 3 files and never with a pointer store.
- Why it matters: this is the main reason the other reviews found bugs. Backend 1, 2, 4, 5 and semantics 1, 2 all sit where two features meet. Each feature has good tests alone.
- Fix: write one table of operand forms (global, local, parameter, field at an offset above 0, element of an array of structs, element of an `indexed` array, pointer target, call with a side effect, volatile variable) and one table of places an expression can stand (`for` start, end and step, `match` value, index, argument, `return`, condition, both sides of an assignment and of a compare). A generated theory puts each form in each place and compares with the result computed in C#. Forms a place does not allow are checked to give an error, which also tests the checker's tree walks (semantics 14).

### 2. The emulated machine starts with zero registers and zero memory

- Severity: high. Kind: coverage.
- Where: `Run.cs:37-39` (`new Cpu6502(...) { PC, SP, I }`, a new `Ram`), `Library.cs:21-23`; `StructTests.cs:174-183`.
- What: A, X and Y are 0 and all memory outside the image is 0 at the start of every run. A removed load of 0, a variable the startup code forgot to clear and a read of a temporary before it is written all give the expected result. `Struct_variables_are_zeroed_at_startup` passes `setup: ram => { }` and then checks for 0, so it passes if the startup code clears nothing. 90 assertions are `Assert.Equal(0, r.U8(...))`. Some tests do it right: `NoinitTests.RunOverGarbage`, `LongBufferTests.cs:53`, `AlignTests.cs:28`, `PagePlacementTests.cs:150` fill memory first.
- Why it matters: a real C64 does not start with zero memory. Backend 1 notes that the zero registers hide the removed `ldy` for a loop that starts at 0.
- Fix: in `Run` and `Library`, fill memory outside the image with a fixed non-zero pattern and start with non-zero A, X, Y and a set carry by default. Give tests that need the old state a parameter. Expect some tests to fail on the first run; each failure is either a test that relied on zero or a compiler bug.

### 3. "Exactly once" for volatile memory is checked by counting listing lines, for 8-bit reads only

- Severity: high. Kind: coverage.
- Where: `VolatileTests.cs:17-52`, `SmallShapeTests.cs:392`, `MatchTests.cs:401`.
- What: `IndexReads` counts `ldy i` and `lda i` lines in the listing of `dst[i] = src[i]`. No test counts the reads and writes the CPU makes. No test writes a 16-bit volatile variable (search for `volatile var ...: u16` and `u16 @ $D...`: none). `Ram` has no hooks, and `Run` creates its own `Ram`.
- Why it matters: spec 4 states a property of the running program. Backend 3 (a 16-bit volatile destination used as scratch) breaks it in ordinary code such as `freq = w + k`.
- Fix: let `Run.Program` take a bus that wraps `Ram` and logs every read and write of given addresses (`FakeVic` is already an `IBus`). Add a theory over 8-bit and 16-bit statements (`=`, `+=`, `<<=`, unary, binary, shift, element and field targets) that asserts the exact sequence of values written to the volatile address and the number of reads.

### 4. Many error tests check part of the message and no position, and some errors have no test

- Severity: medium. Kind: coverage.
- Where: `ErrorTests.cs:52-56` and 42 other `Reports_errors(source, expected)` theories; `Checker.Bodies.cs:281`, `Checker.cs:231`, `Checker.Expressions.cs:217` and `:238` for errors with no test.
- What: the usual check is `Assert.Contains(expected, e.Description)`, often with `OrdinalIgnoreCase` and a short part such as `"past the end"` or `"calls a function"`. The fix sentence is usually outside the part. Line and column are checked in 18 files; the other error theories would pass with the error at any position. Four errors were confirmed to have no test: "'step' must be between -127 and 127", "'main' must have no parameters and no return value", "'~' needs a number", "'&&' needs bool values". A rough scan of the compiler's 460 message literals against the test sources suggests several dozen more; the scan matches text fragments and was not checked by hand beyond a sample of 18 (9 had no test).
- Why it matters: CLAUDE.md asks for a test per error. `tern --json` reports line, column and the suggestion, and agents act on them. Semantics 11 and 12 and syntax 18 and 19 are wrong or weak messages that a full-text check makes visible.
- Fix: one shared helper `Expect.Error(source, line, column, fullText)` and move the theories to it as they are touched. For the gap list, use what is already there: `FixSentenceTests.Messages()` finds every message in the source. Run the suite once with coverage (`coverlet.collector` is referenced) and list the `new CompileError` lines never reached.

### 5. The language is tested as C64 code; the other three platforms get platform tests only

- Severity: medium. Kind: coverage.
- Where: `Run.cs:86` (`platform` defaults to the C64); `AtariTests.cs:152`, `Apple2LibraryTests.cs:377`; `Nes*.cs`.
- What: the Atari files hold 91 test cases, the Apple II files 108, the NES files 85, out of about 3,700. They cover the file format, memory map, startup code and libraries. The nine benchmarks run on the Atari and the Apple II. They do not run on the NES. No feature test runs on a platform other than the C64 (`Platform.Atari`, `.Apple2`, `.Nes` appear only in the platform files and one `ModuleTests` case).
- Why it matters: the NES differs most: code in ROM, variables in RAM, initial values copied, another CPU variant, banks. Zero page ranges and volatile areas differ on all four. A code generator path that depends on where a variable lies is run on one layout.
- Fix: one theory over `Platform.All` that runs the benchmarks and a fixed set of feature programs (the matrix of finding 1 is a good set) and checks results only. The NES needs a `cartridge` line added to the source; add that in the helper.

### 6. Adding a platform or a CPU variant means touching most files

- Severity: medium. Kind: construction.
- Where: `Run.cs:86`, `Library.cs:20-23`, `ComparisonTests.cs:68-71`; fixed addresses in 35 files (`@ $C000`), 14 files (`$D0xx` as the volatile example), 26 files (`0xC0xx` in C#).
- What: the default target is written into `Run.Program`, and `Library` and `ComparisonTests` create `new Cpu6502(ram)` with no platform and no variant. Result tests and shape tests are mixed in the same classes, and about 160 expected instruction strings are NMOS shapes (`lda #$00 / sta x` where a 65C02 would use `stz`). Scratch addresses are C64 literals. Spec 12.1 plans `wdc65c02` for the Commander X16; running the suite as 65C02 code would fail every shape test and cannot be asked for from outside.
- Why it matters: M29 (X16) is the next milestone after the release. The result tests are the valuable check for a new CPU variant, and they cannot be run apart from the shape tests today.
- Fix: read the default target and CPU variant from one place (`TestTarget.Default`, set by an environment variable), used by `Run`, `Library` and the measuring code. Mark shape tests with a trait (`[Trait("Shape", "nmos6502")]`) so a variant run can leave them out. Replace the address literals with named constants per target (`Scratch.Ram`, `Scratch.Volatile`).

### 7. The listing reader is copied into about 30 files

- Severity: medium. Kind: construction.
- Where: `SmallShapeTests.cs:6-17`, `OptimizerTests.cs:10-23` and `:565`, `CpuModuleTests.cs:6-13`, `AParameterTests.cs:9`, `MatchTests.cs:9`, `StructTests.cs:8` and 25 more; `BenchmarkTests.cs:186`.
- What: each file has its own `Code`, `Main`, `MainCode`, `Body`, `Lines` or `ListingOf`. 27 files cut the instruction out of a listing line with `l[17..]`. The copies differ: the separator is `" / "` in one file and `"|"` in the others, some stop at a `;` line and some do not, some return a list. `Count` exists three times, the CLI helper `Tern(params string[])` three times, the temp folder with `Dispose` seven times.
- Why it matters: a change in the listing's columns breaks 27 files. The small differences decide where a function ends, so the same assertion can mean different things in two files.
- Fix: one `Listing` helper in the test project (`Listing.Code(result, function)`, `Listing.Count(code, instruction)`), one `TempFolder` fixture, one `Cli.Tern`. Better: have the compiler return the instructions per function in `CompileResult`, so tests do not parse text.

### 8. The optimizer has no test that compares optimized and plain code

- Severity: medium. Kind: coverage.
- Where: `DeadLoadTests.cs:26-32`, `OptimizerTests.cs`, `DeadStoreTests.cs`; `TernCompiler.cs:89` (no switch).
- What: optimizer tests are hand-written cases. `DeadLoadTests` feeds `Optimizer.Run` straight-line code that ends in `rts`; no case has a jump table or the `pha / pha / rts` dispatch. `TernCompiler.Compile` cannot leave the optimizer out, so no test can run a program both ways.
- Why it matters: backend 1 is a pass that removes a load it should keep. A hand-written case exists only for situations the author thought of.
- Fix: add an internal option that skips the optimizer passes. In `Run`, or in one theory over all benchmark, example and matrix programs, run both versions and compare every variable. Cost: one more compile and run per program.

### 9. Listing assertions against the whole listing are loose

- Severity: medium. Kind: coverage.
- Where: 91 `Assert.Contains(..., Listing)` and 42 `Assert.DoesNotContain(..., Listing)`; for example `DeadLoadTests.cs:106`, `ByteWriteTests.cs:71`, `ConstantCompareTests.cs:68`, `AParameterTests.cs:69`.
- What: the listing holds the startup code, every function and the library. `Assert.Contains("lda #$07", listing)` passes if any function has that instruction. `Assert.DoesNotContain("lda sum ", listing)` passes when the operand is spelled another way (`lda sum+1`, `lda main.sum`), so it cannot fail after a rename.
- Why it matters: these tests guard optimizations. A passing test that no longer looks at the code it was written for gives false comfort.
- Fix: scope each to its function with the shared helper of finding 7. Pair each `DoesNotContain` with a positive check of the shape that is expected. The exact-string tests are fine and use symbolic operands; no test depends on absolute addresses.

### 10. Spec 3.5 and 3.6 (conversions, precedence) have no test of their own

- Severity: medium. Kind: coverage.
- Where: `ProgramTests.cs:73` (widening), `PointerTests.cs:370`; no file for precedence.
- What: the cases the spec names are not tested: `w << 5 + 3` is `w << 8`; `-x as i16` is 128 for an `i8` of -128; `x as u16 << 5`; `a < b < c` is an error; `i8 as u16` sign extends; `bool as u8`. `flags & 4 != 0` is used inside other tests (5 places), which covers one row of the table by accident. `as i8` and `as i16` appear in 14 lines of 9 files. A shift by 0 appears once.
- Why it matters: precedence is the kind of rule a parser refactor breaks without notice (syntax 1, 9, 10 propose such refactors). Semantics 7 and 8 are bugs in `as` and `.lo`/`.hi` on constants.
- Fix: a `PrecedenceTests` theory with one row per pair of neighbouring levels, each compared with C# with explicit parentheses, and a `ConversionTests` theory with every row of the 3.5 table at -128, -1, 0, 127, 128, 255, 256, 32768, 65535, as a variable and as a constant.

### 11. Tests are filed by milestone item, so "is this tested" is hard to answer

- Severity: low. Kind: construction.
- Where: `for` loops are in `ProgramTests`, `VariableStepTests`, `LoopControlTests`, `LoopDirectionTests`, `LoopStoreTests`, `SignedLoopTests`, `UnderscoreLoopTests`, `OuterCounterTests`, `StepNameTests`; `SmallShapeTests`, `ElementValueTests`, `GalaxyStarsGapTests` are named after work items. 16 class comments start with a milestone tag.
- What: one flat folder of 118 files. A feature's tests are spread over the files of the milestones that touched it. Test names themselves are good sentences.
- Why it matters: nobody can see at a glance that `step` has no struct-field case. The gaps of finding 1 are easier to make in this layout.
- Fix: folders by spec section (`Statements/For`, `Types/Structs`, `Backend/Shapes`, `Platforms/Nes`, `Tools`). Keep the files; move them. Put the spec section in each class comment.

### 12. Library tests check the compiler against the tests' own model of each ROM

- Severity: low. Kind: coverage.
- Where: `IoTests.cs:11` (`FakeKernal`), `AtariIoTests.cs:14` (`FakeCio`), `Apple2IoTests.cs:22` (`FakeMli`), `FakeVic.cs`; `src/Tern.Compiler/Tools` has a second set of stand-ins for `tern run`.
- What: the platform libraries are tested against fakes written from the same reading of the documentation as the library. `tern run` has its own stand-ins. Checks on VICE, Altirra, AppleWin and Mesen are done by hand (CLAUDE.md) and leave no record.
- Why it matters: tools 11 (two C64 stand-ins differ from the KERNAL) and tools 9 (the vblank flag) are errors in the model, which no test against the model can find.
- Fix: a small set of programs per platform with an expected screen or memory dump, run on the real emulators by a script when asked (`TERN_REAL=1`, like `TERN_COMPARE`), using `tests/tools/mesen-shot.ps1` and `altirra-shot.ps1`. Use one set of stand-ins for tests and `tern run`.

### 13. `Run.Program` does everything in its constructor, behind ten optional parameters

- Severity: low. Kind: construction.
- Where: `Run.cs:22-74`, `:85-88`; `LongBufferTests.cs:57-59`, `NoinitTests.cs:9-18`.
- What: compiling, loading, platform set-up and the run loop are one constructor. `setup` gets only the `Ram`, so a test that needs a symbol's address before the run compiles the program twice. The Apple II ROM stubs and the MLI stub are inline byte arrays.
- Why it matters: every new platform adds a branch and parameters here. Tools 1 says the same of `Runner`; the two should share one run model.
- Fix: split into `Compile` and `Run(result, options)`; give `setup` the `CompileResult`. Move the platform set-up into the shared machine model that tools 1 proposes.

### 14. Small things

- Severity: low. Kind: construction.
- `ComparisonTests.cs:31` returns at once without `TERN_COMPARE=1` and is reported as passed. Use a skip with a reason.
- `BenchmarkTests.cs:126` is one fact for nine programs: the first wrong result hides the others. Make it a theory per program, with one fact for the baseline table.
- 159 of 294 `Assert.True`/`Assert.False` calls have no message. Where the condition is a comparison, use `Assert.Equal` or `Assert.InRange`.
- `Library.Call` adds 6 cycles for the `jsr` (`Library.cs:57`) by hand; name the constant.
- `tests/Tern.Cpu.Tests`: the cycle table has no independent check (tools 17 has the details and the fix). The 2A03 variant has one test.
- `ErrorTests.cs:8-51` is one theory of 49 rows mixing general type errors and pointer errors; CLAUDE.md puts pointer errors in `PointerTests`.

## Bugs the reviews found, and the tests that would have caught them

Backend:

- 1, `ldy` removed before an `indexed match`: a run test of `indexed match` in a `for ... using Y` loop that starts above 0 (finding 1); non-zero registers at the start (2); optimized against plain (8).
- 2, `p[i] = recs[j].f` with `indexed` records: the operand matrix (1), an `indexed` element as the value of a pointer store.
- 3, 16-bit volatile destination as scratch: a bus that logs writes, with 16-bit statements (3).
- 4, calls out of order in `for` bounds and `return` into the carry: the logger of `EvaluationOrderTests` used in every expression place (1).
- 5, 16-bit element store reloads its index: an index that reads the array being written, in the matrix (1). Unlikely to be found without it.
- 6, long branch drops the offset: not reachable; an assembler unit test with an offset label.

Semantics:

- 1, struct field as `step`: a field at an offset above 0 as `step` (1). The existing step tests use a local.
- 2, call in an inner loop's `step` under `using X`: a call in every place inside a `using` loop, expecting the error (1).
- 3, `@ region` in a module: a module test that places a declaration in a main-file region. `MemoryTests` tests regions in the main file only.
- 4, `const fn` reads a single placed `data` value: `ConstFnTests` reads data arrays only; a test per kind of `data` declaration.
- 5, interrupt fns that call each other: a recursion test with `interrupt fn` on both sides. The recursion tests use `fn`, `inline fn`, `asm fn` and `implements`.
- 6, 7, 8, typed constants, `as` and `.lo`/`.hi` on constants: the conversion theory of finding 10, run on constants as well as variables.
- 9, long byte array in a struct by declaration order: the same declarations in both orders. No test varies declaration order except `ConstantOrderTests`.
- 10, `(name),y` moves `data` to zero page: an asm test with `data` as the operand, checking the report.
- 11, misspelled call in a `using X` loop: an error test with the full text (4).
- 12, messages outside the convention: `FixSentenceTests` scans `new CompileError` and `Error(` calls; these messages are built another way. Extend the scan.
- 13, `0..256` counts with a `u16`: a shape test for `for i in 0..256`. The tests use `0..=255` (38 lines) and never `0..256`.

Syntax:

- 2, no "(called from ...)" in a nested const fn call: `DiagnosticTests.cs:124-162` test one level of call; add two levels.
- 3, C64 zero page lacks `$FB-$FE`: `The_memory_map_is_the_specs` exists for the Atari, Apple II and NES, not for the C64.
- 4, 200 nested parentheses crash: no stress tests (deep nesting, long lines, many declarations). One test per limit.
- 5, CHR data as RAM in the Mesen label file: `NesTests.cs:461` checks CPU labels; add a `chr` data item.
- 12, Unicode identifiers; 15, line break in a block comment; 16, private-use character: there is no lexer test file. A token-level theory from the spec 2 grammar, with characters outside it.
- 13, const fn named like a mnemonic: a generator test with a mnemonic name, expecting the call or a declared error.
- 14, const fn array ignores its length: a test with a length and an initial value of another length.
- 17, NES alignment padding is `$00`: `AlignTests` runs on the C64 only (5); check the file bytes on the NES.
- 18, two errors for `noinit linear data`: `NoinitTests.cs:81` checks that the message is present; assert the full list of errors (4).
- 19, messages that do not name the cause: full-text assertions (4) make a weak message visible in review.

Tools:

- 2, unreadable path ends in a stack trace; 3, `-o` overwrites the source; 13, bad option value: the CLI tests cover good command lines. Add a theory of bad ones (missing file, folder as file, output equal to input, bad value) that asserts exit code and one-line message.
- 8, `bench --json` says `ok: true` on failure: `BenchToolTests` should assert that `ok` agrees with the exit code in every case.
- 9, vblank flag; 11, C64 stand-ins: checks against the real emulators (12).
- 10, profile leaves out IRQ cycles: assert that the profile rows add up to `cpu.Cycles` in a run with interrupts.
- 14, help lists fewer stand-ins: a test that every trap address in the code appears in the help.

## Patterns

- **Depth in values, not in combinations.** A feature is tested with edge values against C#, as CLAUDE.md asks, in the form its author had in mind. The demonstrated code generation bugs are all at the meeting of two features.
- **Tests follow the work.** Each milestone and review finding added a file with its cases. That gives good regression tests and no map of what is covered.
- **Properties are tested through the listing.** Volatile access counts, evaluation cost and optimizer results are read from text. The emulator can observe them directly with a logging bus.
- **A clean machine.** Zero registers and zero memory make wrong code pass.
- **One target built in.** The C64 is the default in the helper and in the literals, and shape tests are not separable from result tests.
- **Each file is self-contained.** Easy to read alone; the price is 30 listing readers and several copies of every small helper.

## Done well

- Run tests compare with values computed in C#, with models where needed (`FloatModel`, `VariableStepTests.Expected`, the benchmark checks). `Comparisons_match_math` covers all four types at their limits.
- `Run` fails a test on an index outside its table and on a cycle limit, and prints the listing with the failure.
- Shape tests compare whole functions with symbolic operands. No test depends on an absolute code address, and only 9 lines name a compiler temporary.
- `EvaluationOrderTests` uses a logging function, which tests the order itself. Extending it is cheap.
- `FixSentenceTests` reads the compiler's source to enforce the message convention, and checks its own scan and its exception list for stale entries.
- `SkillTests` and `RepositoryWarningTests` keep the skill and every example compiling; the benchmark results are checked on three platforms.
- The baselines fail on any change and print how to accept it. The comparison with other compilers checks every version against the same C# result.
- Test names are full sentences that say what is tested. Programs are raw string literals; only 15 lines build a source by concatenation. Random inputs use fixed seeds.
- The suite runs in about 10 seconds, in parallel, with one collection (`Skill`) kept serial for a stated reason. No test depends on another.
