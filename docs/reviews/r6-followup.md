# Review R6, follow-up: tests 4

Branch `r6-data`, the commits after `4d9b896` (which review R6, `docs/reviews/r6.md`, found ready to merge): `5355cdd`, `6bb2a63`, `ecab2dd` (tests 4), the merge of `main` (`b6e7cb7`, docs only) and the status update (`be4231b`). Roadmap R6 as changed on 2026-11-21: tests 4 stays in R6, tests 9 and 11 move to R8.

## Verdict

**Ready to merge.** Tests 4 is done as the finding asks: one helper (`Expect.Error` / `Expect.Message`) that checks the whole message and the line and column, the error theories moved to it, and a coverage run's list of unreached error sites either reached by a new row or given a reason. No must-fix item. The suite passes (11538 compiler tests, 1 skipped with a reason: the `TERN_COMPARE` comparison). No benchmark baseline, `SkillTests` file or `.claude/` file changed since `4d9b896`, and both tests pass.

The four source changes are message fixes, each with a row: the asm label fix sentence (`'.name:'`, not `'..name:'`), the `include_bytes` read error's double period, a void call as an operator's operand, and a non-int index in a const fn. All four checked through `tern --json` below.

The suggestions are small. Two are weak messages that the whole-text work brings into view but that were on `main` already (findings 1 and 2). Review R6's seven suggestions are still open; the status leaves it to the developer whether they are R6's or R8's.

## How it was checked

- `dotnet test` in `../6502-language-dev` (on `r6-data`, clean): all pass.
- The diffs of the three commits: `Expect.cs`, the four source lines, and a sample of the rewritten rows in `ConstFnTests`, `ErrorTests`, `AsmTests`, `StructTests`, `AlignTests`, `NesBankTests`.
- Searches for what should be gone: `Assert.Contains` / `StartsWith` / `EndsWith` on a compile error's `Description` or `Message`, and each left one read against the status's stated exemptions (tool errors, the operand matrices, loops over cases, temporary paths).
- The four errors the finding named as untested: `'~' needs a number`, `'&&' needs bool values` and `'main' must have no parameters ...` now have whole-message rows in `ErrorTests`; `'step' must be between -127 and 127` is reached only by the operand matrix, as a fragment (finding 3).
- Demonstration programs through the branch's `tern --json` (and `main`'s, to tell new from old): a void call in `f() + 1`, `1 + f()`, `-f()`, `~f()`, `f() as u8`, `if f()`, `if f() == 1`, `a[f()]`, `f() && true`, `x += f()`; an undefined asm label; `true + 1` and `true + false`; `for ... step 128`.
- The status's list of 11 unreachable sites: read against the code for the two with the shortest reason (`'+' needs numbers`, `Expected T, found a number`). The first is reachable in spirit (finding 2).

## Findings

### 1. The void-call check covers binary operators only (suggestion)

`Checker.Expressions.cs:252-255` (`ecab2dd`) catches a void call on either side of a binary operator before `Unify`, so `f() + 1` now says "This function call does not return a value. Call it as a statement on its own", at the call. The other places that need a value still give a fix that does not apply:

```
x = -f()           Unary '-' needs a number. Use '!' to negate a bool
x = ~f()           '~' needs a number. Use '!' for bool
x = f() as u8      Cannot convert void to u8. Convert one element or field of it instead
if f() { }         A condition must be bool, not void. Compare it, e.g. 'x != 0'
```

All four are the same on `main`, so this is not a regression; it is the same bug the commit fixed, in its other places. The message text is also now written twice (here and `Checker.TypeRules.cs:72`, in `Coerce`). Fix: one helper (`RequireValue(expr)`) with the one message, called by the binary check, the unary operators, `as` and the condition check, with a row for each.

### 2. `true + false` gets "Cannot combine bool and bool"; the right message is listed as unreachable (suggestion)

`'+' needs numbers. Use '&&', '||' and '!' for bool values` (`Checker.Expressions.cs:344`) is in the status's list of unreachable sites, "since `Unify` says 'Cannot combine bool and bool' first". That is true, but the message `Unify` gives is the weak one:

```
x = true + false   Cannot combine bool and bool. Convert one side with 'as'
```

Both sides have the same type, and converting one side with `as` gives "Cannot combine u8 and bool". The unreachable message is the one the user needs. On `main` too. Fix: test for bool operands in the `+ - & | ^` arm before `Unify`, so the better message is reached; give it a row and drop it from the unreachable list.

### 3. A few compile-error tests still check fragments outside the stated exemptions (suggestion)

The status lists four kinds of test left with fragments on purpose. These are none of them; each is a plain compile of a fixed program, so the whole message and position are known:

- `AlignTests.cs:232-233, 251, 258-259, 273, 286-288`: five facts and one theory, `StartsWith` / `Contains` / `EndsWith` through the file's own `Message` helper, no position. (Line 286's message names an address range, but the program is fixed.)
- `LoopCounterTypeTests.cs:59` ("Expected u8, found u16") and `:144` ("does not fit in"): theories whose message depends on the range, but the range is the row's own data.
- `ModuleTests.cs:331`: the second "'random' is both a library module" case. Its message names a temporary path, but the line above builds that path for the first case with `Path.Combine`; the same works here.
- `'step' must be between -127 and 127`, one of the four errors the finding named, is checked only by `OperandMatrixTests.cs:196` as a fragment with no position. One `Expect.Error` row (as in the `step 128` program above: line 2, column 33) closes it.

None hides a bug I could find; they are the leftovers of a large rewrite. R8 (the rest of the test findings) is a natural place if they are not done before the merge.

### 4. The commit message's counts differ from the status (note)

`ecab2dd` says 63 new rows, 48 sites reached and 12 left; `docs/status.md` says 65 rows, 49 reached and 11 left. The status matches the code (65 `InlineData` lines and 2 facts added; 11 sites listed). Nothing to change; the status is the record.

## Verdict per finding

The findings review R6 judged keep their verdicts (`docs/reviews/r6.md`, "Verdict per finding"); the merge and the status commit change none of them. The status rows for tests 9 and 11 now say "moved to R8", as review R6 asked.

| Finding | Verdict | Note |
|---|---|---|
| tests 4 (error positions and whole messages; errors with no test) | fixed | `Expect.Error(source, line, column, message)` and `Expect.Message`; 55 theories (about 800 rows) and the fragment facts moved; the four named errors have tests (`step` by fragment only, finding 3); a coverage run's 60 unreached sites: 49 reached by 65 rows and 2 CLI facts, 11 listed with a reason. Four message bugs fixed on the way, each with its row. Left: a few fragment checks outside the exemptions (finding 3) |
| tests 9 (listing asserts on the whole listing) | moved to R8 | decision 2026-11-21; not read |
| tests 11 (tests filed by milestone) | moved to R8 | decision 2026-11-21; not read |

## Done when

- [x] Adding a platform needs no change outside its description and its library (review R6; unchanged since).
- [x] The feature suite passes on all four platforms (review R6; the suite passes on the branch).
- [x] Tests check error positions and whole messages, and every message has a test or a reason it cannot be reached (roadmap R6, third item): `Expect` used by the error theories; the coverage gap list reached or explained in `docs/status.md`. Leftovers in finding 3.
- [x] The benchmarks are unchanged (no baseline change since `4d9b896`; `BenchmarkTests` passes).
- [x] `SkillTests` is unchanged (no change under `.claude/`; the test passes).
- [x] The status table has a row per finding, with tests 9 and 11 marked "moved to R8".
