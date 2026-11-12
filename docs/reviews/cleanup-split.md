# Review: Cleanup, split the large classes, branch `cleanup-split`

## First review (`686aebc`), 2026-11-11

**Ready to merge.** No must-fix items. The diff is moves only: every line that left `CodeGen.cs`, `Checker.cs`, `Optimizer.cs` and `Parser.cs` is in one of the new files, unchanged, and the only new lines are each file's header and closing brace.

How I checked:

- **Moves only.** For each class I compared the lines of the old file on `main` with the lines of the new main file plus its new partial files, as multisets (exact text, whitespace included). Nothing is missing. The only extra lines are, per new file, its `using` lines, a blank line, the `namespace` line, the one-line `<summary>`, the class line and its two braces; the only changed line is `class` becoming `partial class` (`Checker` was already partial). Each new file's body is one contiguous run of the old file (for example `CodeGen.Loops.cs` is old lines 2594-3386, `Checker.Declarations.cs` old lines 359-1279), so the order of members inside each section is kept and no section was cut in two. `git diff --color-moved=plain main..cleanup-split -- src` marks all other lines as moved, as the status says (per-file diffs show the section markers at the cut points as removed, because their new home is outside the filtered path).
- **Same bytes.** I built `tern` from `main` and from the branch and compiled every example with each, the plug-in levels after `game.tern`: all 70 output files (`.prg`, `.lst`, `.vs`, `.lines`, and the `_api.tern` files) are identical. `level1`, `level2` and `level_api` fail on both, as expected (they only compile as part of another program).
- **Tests.** 2861 compiler tests and 36 CPU tests pass, the same count as `main`. `BenchmarkTests` (1254 / 350780) and `SkillTests` pass with no baseline change.
- **Sizes.** The largest file in `src/` is `Checker.Declarations.cs` at 931 lines; the next are `Interpreter.cs` (893, not part of this milestone) and `CodeGen.Loops.cs` (806).

### Findings

**1. Suggestion: generated `.lines` files in `examples/` are not ignored.** The coding worktree has untracked `examples/*.lines` files (from compiling the examples to compare bytes). `.gitignore` has `examples/*.prg`, `*.lst` and `*.vs` but not `*.lines`, so a later `git add examples` would pick them up. Adding `examples/*.lines` (and the same for `tests/benchmarks` if `.lines` are written there) would keep them out. This is not part of the branch's diff.

### Done when (roadmap, Cleanup)

- [x] No source file in `src/` is over about 1,000 lines (largest 931).
- [x] All tests pass unchanged (2861 + 36; no test file in the diff; benchmark and skill baselines unchanged).
- [x] A review confirms the diff is moves only (line multisets and contiguous blocks, above).
- [x] One commit per class (`c55e412` CodeGen, `c501e1d` Checker, `5e06934` Optimizer, `5fcc0ea` Parser).
- [x] Every example compiles to the same bytes (70 output files compared).
