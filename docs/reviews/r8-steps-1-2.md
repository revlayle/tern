# Review R8, steps 1 and 2: idle input, tests 9

Branch `r8-tests` at `281057c`, from `main` at `21ea644`: `798284b` (step 1, `tern run`'s idle input, question 175), `0a257ae`, `f580965`, `e777353` (step 2, tests 9, listing checks) and `281057c` (status). Roadmap R8, steps 1 and 2; steps 3-5 are not done yet and are not reviewed here.

## Verdict

**Steps 1 and 2 are ready, with one must-fix item before R8 goes to merge.** Idle input is built as decision 2026-11-21 and spec 13 state it, on all four platforms, with a test per value and the help and skill updated. The listing checks are scoped to one function, the startup code, the lines outside functions, or the section names, and none of the changed ones reads the whole listing. The must-fix item is step 2's second half: about a dozen `DoesNotContain` checks on a listing still stand alone, without the positive check of the expected shape that step 2 asks for; three of them are lines this branch rewrote (finding 1).

The suite passes in `../6502-language-dev` (11564 compiler tests, 1 skipped with a reason: the `TERN_COMPARE` comparison; CPU tests pass). No benchmark baseline or `SkillTests` file changed.

## How it was checked

- `git diff main...r8-tests` and `git log main..r8-tests`; the decision (2026-11-21, question 175), spec 13's paragraph, and roadmap R8.
- `dotnet test` in `../6502-language-dev` (`r8-tests`, clean): all pass.
- `tern run` on the branch: a program that reads `$DC00` and `$C000` on the C64 (`$FF`, and `$C6` from the seeded fill, as the decision leaves other addresses), with `--set '$DC00=$7B'` (reads `$7B`: `--set` still wins, since it is applied after `Machine.Start` and the load), and on the Apple II (`$C000` reads 0).
- `tern run` on `main`, to see that the new rows test a change: the Apple II's `$C061`-`$C063`, `$C000` read `$78 $A1 $8A $C6`, the Atari's `STICK0`, `STRIG0`, `CONSOL`, `CH` read `$E8 $CD $AC $2E`.
- A search of the branch's tests for `.Listing` outside `Listing.cs`: only `ComparisonTests` (each line's cycles), `LineTableTests` (every listed instruction is in the line map) and failure messages in `Run.cs` and `UnderscoreLoopTests` remain, as the status says. No `Assert.Contains` / `DoesNotContain` on a local copy of the whole listing is left (`Apple2Tests`, `NesTests`, `CompileTimeExampleTests`, `Apple2IoTests` now take `Listing.Text` / `Lines` of one section).
- Every `DoesNotContain` on listing text in the test project (about 80), read with the lines around it for a positive check (finding 1).
- The new `Listing` helpers (`Outside`, `Startup`, `Text`, `Header`, `Sections`), and whether a scoped negative check could have gone vacuous: `jumphi` (`MatchTests`) is an operand of `f`'s own `lda f.jumphi,x`, so scoping to `f` keeps it meaningful; the startup checks (`NoinitTests`, `Apple2IoTests`, `NesTests`) each have a positive check in the same section, so `Listing.Startup` is known to find the startup code on the C64, the Apple II and the NES.

## Findings

### 1. `DoesNotContain` checks without a positive check of the shape (must fix)

Step 2 asks for "each `DoesNotContain` paired with a positive check of the expected shape", and the status says the branch did this. A negative check alone passes when the code changes shape in a way it does not name (a renamed temporary, a different branch instruction, an empty function), so it can go vacuous without anyone seeing it. These stand alone:

Rewritten on this branch:

- `AParameterTests.cs:164`: `DoesNotContain(Listing.Instructions(r, "then"), i => i.Contains("then.v"))`, nothing about what `then` does instead (its other test at line 58 pairs it with `cmp test.t`).
- `MatchTests.cs:92`: `DoesNotContain("jumphi", Listing.Text(r, "f"))`, with only a second negative (the report). The test's name says the match "stays a chain"; nothing checks the chain (e.g. a `cmp #$0A` in `f`, as line 389 does for its test).
- `PagePlacementTests.cs:176`: `DoesNotContain("left free", Listing.Outside(r))`, with only a negative on the report. A positive check of where `buf` went (not placed: no `buf` section, or `out`'s address) would make it a check of the shape.

Already scoped before the branch, so not rewritten, but under the same rule:

- `InlineAsmTests.cs:64` (`"jsr"` in main), `OverflowTests.cs:35` (`jsr` in main): nothing shows the inlined body (as `InlineAsmTests.cs:31` does with `ora #$01`).
- `LoopControlTests.cs:102`: `DoesNotContain("dec n|bne", Listing.Code(r))`, the weakest of the set: any other ending of the loop passes. `LoopControlTests.cs:194`: `"cmp"`, with no check of the branch used instead.
- `HeldVariableTests.cs:196`: `"inc sp"`, with no check that `sp` is held (lines 69-73 of the other test show the shape).
- `OuterCounterTests.cs:53-54`: `"main.i"`, `"main.j"`, with no check of the registers used (lines 27-29 and 110-111 of its neighbours have them).
- `OptimizerTests.cs:372`: `"jmp main.endif"`, with no check of the jump to the final target that the test's name promises.

Fix: a positive check beside each, of the instruction that replaces what is absent. Tests only; no compiler change.

### 2. `--set '$DC00=$7B'` in the skill also clears bit 7 (suggestion)

`.claude/skills/tern/SKILL.md:46` gives `$7B` for "joystick left". With idle at `$FF`, left alone is `$FB`; `$7B` also clears bit 7 (a keyboard column on port A). A program that masks with `and #$1F` reads the same, but one that compares the whole byte (`$DC00 == $FB`) sees something other than "left only". `$FB` follows from the idle value the paragraph has just given. `SKILL.md` is checked by `SkillTests`, which this does not move.

### 3. The help test checks only the C64's part of the idle sentence (suggestion)

`CliTests.cs:275` checks "Input reads idle, nothing pressed: the C64's $DC00 and $DC01 are $FF"; the Atari's and the Apple II's values in the same sentence (`Cli.cs:162`) are not checked, so they can drift from `AtariMachine.Start` and `Apple2Machine.Start` unseen. Checking the whole sentence, as the other help checks do for their names, is one line.

## Done when (steps 1 and 2 only)

- [x] `tern run` starts with idle input on all four platforms: `C64Machine`, `AtariMachine`, `Apple2Machine` `Start` set the decision's values after the seeded fill and before the load; the NES's controllers read no button (`The_nes_controllers_read_no_button`).
- [x] Each idle value has a test that fails before it: `RunModelTests.Input_reads_idle`, 16 rows; the values on `main` above differ from each row.
- [x] `SkillTests` changes only by the idle input: no output moved (the sprite run's A is the seeded `$D012`, not the joystick, as the status explains); the prose in `SKILL.md` and `sprite.md` is updated.
- [x] No listing check reads the whole listing; the three left (`LineTableTests`, `ComparisonTests`, `SkillTests`' excerpts) check the listing as a whole, not a code shape, and the status names them.
- [ ] Each `DoesNotContain` paired with a positive check of the expected shape (step 2's text; finding 1).
- [x] The benchmarks are unchanged (`BenchmarkTests` pass; no baseline file changed).
- Not reviewed here: step 3 (the suite on every platform), step 4 (review R6's suggestions), step 5 (test folders).

## Verdicts per finding (R7's way)

| Finding | Verdict |
|---|---|
| question 175, idle input (step 1) | fixed |
| tests 9, listing checks scoped to their function (step 2) | fixed |
| tests 9, each `DoesNotContain` paired with a positive check (step 2) | partly: finding 1 |
| tests 6, the rest (step 3) | not started; not reviewed |
| review R6's suggestions (step 4) | not started; not reviewed |
| tests 11 (step 5) | not started; not reviewed |
