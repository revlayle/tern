# Review: R7 fixes, what no milestone owned

Branch `r7-fixes` at `4e03d43` (6 commits from `main` at `ff7b219`). Roadmap "Before the first release", R7 fixes; decision 2026-11-16, "R7's must-fix items"; review `docs/reviews/code-quality-followup.md`, "Must fix".

## Verdict

**Ready to merge.** No must-fix findings.

`dotnet test` in the coding worktree: 6181 compiler tests and 50 CPU tests passed, none skipped. `BenchmarkTests` and `SkillTests` pass with no baseline change.

The source change is 17 lines; the rest is tests. What I checked beyond reading the diff:

- **The precedence theory catches parser bugs, not only test bugs.** Status says one test row bracketed the other way fails the theory. I also changed the parser in a temporary worktree (now removed), three times, and ran `PrecedenceTests` against each:
  - `&` and `^` levels swapped: 4 failures (`a ^ b & c`, `a & b ^ c` on several value sets).
  - `<<`/`>>` tighter than `+`/`-`: 6 failures, runtime and constant rows.
  - `&&` and `||` levels swapped: fails.
- **The conversion rows from a variable really convert at runtime.** The compiler does not fold a never-written initialized variable into a constant: `r0 = s0 as u16` with `s0: i8 = -128` compiles to `lda s0` / `sta r0` / `ora #$7F` / `bmi` / `lda #$00` / `sta r0+1`, the sign-extend shape of spec 11.
- **The compiler emits only mappers 0 and 2** (`Cartridge.cs`), so the new `NesBus` check cannot reject a program the compiler built. `Runner.Run` is the only place a `NesBus` is made outside the tests.
- **Reverting the CLI's catch fails a test.** Put back `catch (InvalidOperationException)`, and `RunError` (not an `InvalidOperationException`) would fall through to the internal-error catch: `CliTests.Run_of_a_binary_without_main_is_an_error` expects exit code 1 and would get 4.
- **No user program reaches the other `InvalidOperationException`s of a run.** An undefined opcode ends the run with "Illegal opcode $02 at $0817", exit code 2, not an exception (tried with `jmp` into a one-byte `data` table). The remaining throws in `Cpu6502` are emulator bugs, which are internal errors now, as the roadmap asks.
- **The unreachable-walk test visits every kind.** `Unreachable.Check` calls `Inner` on every statement of a block before it stops at one that never finishes, and the walk includes `const fn` bodies (`Checker.Warnings.cs:41`), so the `assert` and `emit` in the test's `const fn` are reached. Every concrete `Stmt` except `ErrorStmt` appears in the program, and each is listed in `Inner`.

## Findings

All are suggestions.

### 1. Suggestion: the two run errors do not follow the error-message convention

`src/Tern.Compiler/Tools/NesBus.cs:83`, `src/Tern.Compiler/Tools/Runner.cs:86`.

CLAUDE.md asks for "what is wrong, then how to fix it in a sentence that starts with a verb from `Diagnostic.FixVerbs`".

- The new mapper message ends "Run the program in an emulator such as Mesen". `Run` is not in `FixVerbs` (`Tools/Diagnostics.cs:27`).
- The binary-without-main message joins its fix with a colon: "...cannot be run on its own: run the program that loads and calls it". `tern bench` gives the same fact as two sentences ("...on its own. Measure the function in ...").
- `Cli.Commands.cs:84` builds the `Diagnostic` with a `null` suggestion, so `tern run --json` reports no suggestion for either error.

These are run errors, not compile errors, and the second message is older than the branch. R6 (messages as data) is the natural place: add `Run` (and `Measure`, for the bench message) to `FixVerbs`, and build the run's diagnostic through the same split as a compile error.

### 2. Suggestion: small gaps in the precedence rows

`tests/Tern.Compiler.Tests/PrecedenceTests.cs`.

- `-7 / 2` (constant theory) cannot tell the two readings apart: truncating division commutes with negation, so `(-7) / 2` and `-(7 / 2)` are both -3. Levels 2 and 3 are still covered by `~1 * 2` (-4 against -3). Replace it with a row that differs, e.g. `~6 / 4` (-1 against -2), or drop it.
- Level 1's member access `s.x` has no row (e.g. `-s.x`, `s.x as u16 << 4`). `t[i]` and `as` are covered.
- `!` against a compare (`!p == q`, level 2 against 9) has no row; `!` is checked only against `&&` and `||`.

### 3. Suggestion: automatic widening is checked in assignments only

`tests/Tern.Compiler.Tests/ConversionTests.cs`.

Spec 3.5 lists where a destination gives the type: an assignment, an initial value or table element, an argument, a return value. `ConversionTests` checks the assignment (`a{i} = s{i}`). The other places go through other code paths in the checker. If other test files already cover `i8` to `i16` sign extension in an argument and a return value at -128 and -1, leave it.

## Done when

- [x] **Each item has its test.**
  - Tests 10: `PrecedenceTests` (42 runtime rows on six value sets, constant rows, `-x as i16` at -128, chained compares with the whole message) and `ConversionTests` (every integer pair at the review's values from a variable, a typed constant and automatically, untyped constants, `bool as u8`, pointers, the not-allowed line with whole messages). Each compares against C#. The one bug found, the "c.g." typo, is fixed with its test.
  - Tools 18, unknown mapper: `RunModelTests.Another_mapper_is_an_error_not_a_run_as_nrom` (mappers 1, 4, 18; fails before the fix) and `The_modelled_mappers_run`.
  - Tools 18, `InvalidOperationException` in `Cli`: `RunToolTests.A_binary_without_main_cannot_be_run` expects `RunError` (fails before), and `CliTests.Run_of_a_binary_without_main_is_an_error` (exit code 1; fails if the catch is reverted).
  - Backend 7: the parameter is gone; the suite builds.
  - Semantics 14: `WarningTests.The_unreachable_walk_knows_every_kind_of_statement`. As status says, it did not fail before the fix, since nothing was skipped then; it fails when a kind is missing from the list.
- [x] **The benchmarks and `SkillTests` are unchanged** (1254 / 350780; no baseline file in the diff).
- [x] **The status table lists each item as in R1-R6:** "Review findings handled by R7 fixes", with commit, tests and state for all four.
