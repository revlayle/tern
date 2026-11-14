# Review: M23 follow-up, branch `m23-followup`

## First review (`5aaf660`), 2026-11-12

**Ready to merge.** No must-fix items. The three changes do what decisions 107, 108, 112, 113 and "a function may be called from main and from a handler" say, and the skill matches.

- **Errors sorted by place (108, 112).** `ErrorList` now collects every error and sorts when it throws: file order from `AddFile` (the main file, then each module as the loader reads it), then line, then column. `OrderBy` is stable, so errors at the same place keep the order found. The file key is the same string the parser puts in `SourcePos.File` (`sourcePath` for the main file, the loader's `path` for a module), so no error falls into the "not noted" bucket. Errors with a position made without a file (`new SourcePos(1, 1)` in the assembler) come only from the later stages, which do not use the list. I compiled a main file importing `a` and `b`, with `a` importing `c`, and an error in each: the order is main, a, c, b (depth first, as 112 says). With `--max-errors 2` the first two after sorting are kept, with the note. Text, `--json`, `tern run` and `tern bench` all read `CompileError.All`, so they give the same order.
- **Undeclared names at every use (107, 113).** `CompileError.Undeclared` and the step that declared the name after its first error are gone. A name whose declaration failed still gives one error: I checked `struct S { a: Nope }` with `s.a = 1`, and `fn f(p: Nope)` called twice. Each gives one error, at the declaration. An undeclared `cnt` used in a `for` range, an assignment, a `while` condition and its body gives four errors, one per place. The condition and the body count as two places, since M23 checks a condition on its own.
- **A function called from two roots (spec 8.1).** `CheckSharedFunctions` is gone. `Allocator.FrameOffsets` places functions with `Roots.Count > 1` after all others, at `end`, as it already did for `Isolated` functions. Their callees are reached from the same roots, so they follow them, and no callee is placed before its caller, which `callers[f].Max(...)` needs. `FrameOffsets` serves both the zero-page and the RAM frames. The tests check zero page, so I checked RAM by hand: in a program where `main`, a function `m2`, the handler and a shared `s` each have a 40-byte local array, the arrays are at `$0864`, `$088C`, `$08B4` and `$08DC`, with no overlap. Fixed-address routines get no roots (`OrderRoots` skips them), so they are unchanged. The emulator test shows main's call going on with the handler's values, as the decision describes.

2895 compiler tests and 36 CPU tests pass. `BenchmarkTests` and `SkillTests` pass with no baseline change (1254 / 350780).

### Findings

**1. Suggestion: no test for the depth-first module order (question 112).** `OutOfOrderErrors` has two modules, both imported by the main file, so it would pass under either reading of "the order first imported". A third module imported by the first (`zeta` importing `beta`, with an error in each) would fix the order decision 112 chose. I checked it by hand (main, a, c, b).

**2. Suggestion: `AssertFrameOverlapsNoOther` checks only the zero page.** `FrameOffsets` places RAM frames the same way, so this works today, but no test covers the RAM half (a shared function with a local array, as in the case above). One row that reads the RAM addresses from the listing or the labels would cover it.

**3. Suggestion (style): `Symbols.cs` joined the `ErrorSymbol` summary into one 150-character line.** The rest of the file wraps summaries at about 100 characters.

### Done when (roadmap, M23 follow-up)

- [x] Errors sorted by place: the main file, then modules in the loader's order (depth first, decision 112), then line and column; the same place keeps the order found. `--max-errors n` keeps the first `n` after sorting, with the note. Text, `--json`, `run` and `bench` use the same list.
- [x] Tests whose first error changed are updated, and the error each one is about is still checked (`SeveralErrorsTests`, the CLI, `--json` and `bench` cases).
- [x] An undeclared name is reported at every use: three calls to `update()` give three errors. The same for an undeclared variable, type, module member and missing import. A failed declaration still gives one error.
- [x] A function may be called from main and from a handler, or from two handlers: no error. Its frame, temporaries included, overlaps no frame of either root. The former `InterruptTests` error rows are now compile-and-check rows (`A_function_with_memory_may_be_called_from_two_roots`), and the `InlineLocalsTests` and `InterruptPartTests` cases changed the same way.
- [x] An emulator run shows main's call getting the handler's values (`A_function_shared_with_a_handler_goes_on_with_the_handlers_values`).
- [x] The skill's two-roots examples (`errors.md`) and rules (`SKILL.md`, `language.md`) are updated. The private-variable error and its example stay.
- [x] `SeveralErrorsTests`: errors found out of source order (a global's type after a function's parameter, lexer and parser errors of one file, two modules), `--max-errors 2` on that program, and the three calls.
- [x] The tests pass, and the skill's error examples match the new order (`SkillTests`).

## Second review (`d584ce1`), 2026-11-12

**Ready to merge.** All three suggestions are done, with no code change besides a comment. `main` is merged into the branch. 2896 compiler tests and 36 CPU tests pass, and `BenchmarkTests` and `SkillTests` pass with no baseline change.

- **Suggestion 1** (`2cb7213`): `zeta` now imports `beta`, and the expected order is main, zeta, beta, alpha. That order holds only under the depth-first reading of decision 112.
- **Suggestion 2** (`e90681f`): `AssertFrameOverlapsNoOther` now also reads `CompileResult.Variables`, which lists named frame variables in RAM as well as zero page, under qualified names such as `s.a`, so the `function + "."` match finds them. A new test puts four 40-byte local arrays in RAM (it checks that each address is `$0100` or above) and checks that the shared function's frame overlaps none of them. A variable in zero page appears twice in the list. That is harmless: an entry is only compared with other functions' entries.
- **Suggestion 3** (`9850361`): the `ErrorSymbol` summary is wrapped.

No new findings.
