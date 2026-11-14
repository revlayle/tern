# Review: Warnings, branch `warnings`

## First review (`fc0ee0d`), 2026-11-12

**Ready to merge.** No must-fix items. The four warnings, the switches, the tool output and the report's two-roots list match spec 13, 8.1 and the decision "warnings for code that does nothing". The repository compiles with no warnings.

- **What counts as a use.** Top-level names are recorded where the checker resolves them (`Lookup` with `demand`, and the bound names of generator lines in `Checker.Asm.cs` and `Checker.Expressions.cs`). Locals are found by a syntax walk (`LocalUses`) that covers every statement and expression kind in `Ast.cs`. I checked by hand that none of these gave a false warning: a constant used only in an `assert`, a struct field offset (`b: u8 @ K4`), a `data` length, or `sizeof`; a struct used only as a parameter's pointer type or in a cast; a function and a global used only as `addr()` in a `data` table; a module's struct used only as a global's type (the import counts as used); a const fn used only from inside another const fn that a generator line calls; and locals used only as asm operands (`lda x`, `#z.lo`). An indexed assignment inside a const fn (`t[1] = 5`), whose body the checker never types, does not trip `Write`'s `Target.Type` test.
- **Not warned, as the spec says.** `main`, `_` names, `@ zp` and `@ $C000` globals, exports, parameters, public module declarations and library modules get no warning. Private module declarations (`var`, `struct`) and a module's unused import do. An unused `import math` in the main file is warned. Only warnings inside library modules are left out, which is the spec's rule.
- **Unreachable.** `match` with a `break` in an arm under `while true`, `if`/`else` with `return` in both branches, and `while true` around a `for` whose `break` is the inner loop's all give the right result.
- **Driver and tools.** Warnings are found after `_errors.ThrowIfAny()`, so only when the checks found no error. A later error carries them in `WithWarnings`, sorted with `ErrorList.Sort`. `--warnings-as-errors` turns them into errors before the call graph, so a call-graph error is not reported with them. That fits "the build stops". The two-roots list counts temporaries and the variables an inline callee puts in the caller's frame. I checked this with a handler and `main` both calling a function with 16-bit temporaries (`temps (5 bytes)`) and one calling an `inline fn` with a local (`uses_inline (3 bytes)`).

2936 compiler tests and 36 CPU tests pass. `BenchmarkTests` passes with no baseline change (1254 / 350780). `SkillTests` passes, with `flow.tern`'s run output rewritten (see finding 3).

### Findings

**1. Suggestion: the unreachable warning's column is wrong for some statements.** `Checker.Warnings.cs:325` (`Start`) fixes the column for an assignment to a plain name, but an index or member target still has the operator's position. An `asm` statement's position is its `{`. With `return` on the line before:

| Statement | Reported column | Should be |
|---|---|---|
| `    buf[1] = 2` | 8 (`[`) | 5 |
| `    pt.a = 2` | 7 (`.`) | 5 |
| `    asm { nop }` | 9 (`{`) | 5 |

This matters for the language server (M30), which marks the statement from this position. One fix: walk an `IndexExpr` / `MemberExpr` target down to its leftmost expression, and give `AsmStmt` the position of `asm`. Add a test row for each.

**2. Suggestion: "(1 bytes)" in the report.** `TernCompiler.cs:413` writes `plain (1 bytes): main, h`. Write "1 byte" for one, as `Diagnostic.Count` already does for "1 error".

**3. Note (not caused by this branch): `for _` can be slower than `for i`, and the new warning now advises `_`.** `for i in 9..=0 step -1 { n += 1 }` compiles to `ldx #9` / `ldy n` / `iny` / `dex` / `bpl` / `sty n`, 11 bytes and 83 cycles: the optimizer keeps `n` in Y. `for _ in 9..=0 step -1 { n += 1 }` takes the "repeat 10 times" path, `ldx #10` / `inc n` / `dex` / `bne`. That is 8 bytes but 107 cycles, because `n` is not kept in a register. The skill's `flow.tern` got 24 cycles slower (802 to 826) when its two counters became `_`. The same gap was on `main` before this branch. Because the warning now tells people to write `_`, it would be worth a question to the spec session: should the repeat path also keep a variable in the free index register? Spec 11 rule 4 says the code must match spec 11's shapes or be shorter, and this is shorter in bytes, so the trade-off needs a decision.

**4. Suggestion (minor): a `for` variable's warning points at `for`, not at the variable** (`Checker.Warnings.cs:182` passes `f.Pos`). `c.tern:6:5` marks `for i in 0..4`, where an editor would mark `i`. `ForStmt` has no position for its variable, so this needs one more field. It can wait until the language server.

### Done when (roadmap, Warnings)

- [x] The four warnings of spec 13: unused local (with "Write '_' as the loop variable" for a `for` variable), unused private module declaration or main-file declaration, unused import (not one setting module parameters), unreachable statement (one per block). Uses count in Tern code and assembly operands, in any function, reachable or not.
- [x] Not warned: `_` names, `main`, exports, fixed-address globals of the main file, parameters, fields, module parameters, public (not `private`) module declarations, library modules.
- [x] Each message says what is unused or unreached, then a fix sentence starting with a verb from `Diagnostic.FixVerbs` (`Remove`, `Write`).
- [x] Reported only when the checks found no error, and sorted by place with later errors. Text gives `warning:` and "1 error, 2 warnings". `--json` gives `"severity": "warning"`, and `ok` stays true with only warnings. `--max-errors` counts errors only.
- [x] `--no-warnings` and `--warnings-as-errors` work, and giving both is an error. `tern run` reports warnings (text, and `diagnostics` in JSON). `tern bench` does not.
- [x] The report lists functions with memory reachable from two roots, with their roots.
- [x] The repository is clean. `RepositoryWarningTests` covers the examples and `src/lib` (with `warnLibrary`). `SkillTests` compiles the skill's programs with `--warnings-as-errors`. Raster's unused `BAND` is removed. Speedcode's comparison routine is `_cycle_plain`. The guide still says `cycle_plain` (question 117, spec session).
- [x] The skill explains each warning and `_`. The guide's mention belongs to the spec session.
- [x] `WarningTests` covers every case on the roadmap's list: `0..COUNT` with `COUNT` 0, `if DEBUG { return }`, an inner loop's `break` under `while true`, an `asm` block, a name used only in assembly, a variable only written, the unused-function chain, no warnings with a check error, warnings with an allocation error, the order with errors, the JSON severity, both switches and both together, `run` and `bench`, and the two-roots list.

## Second review (`48c6cb8`), 2026-11-12

**Ready to merge.** The fix commit takes care of suggestions 1, 2 and 4. Note 3 has gone to the spec session as question 118. The merge of `main` (`96771ad`) only brought in this review file.

- **Suggestion 1, done.** `Start` uses `Leftmost` for an assignment target, which walks `IndexExpr` / `MemberExpr` down to the first token. An `asm` statement uses the new `AsmStmt.KeywordPos`, which the parser sets only on the statement path. An `asm fn` body has no keyword position and keeps `Pos`, but its block is the whole body and never follows another statement. The block's own `Pos` stays its `{`, so asm error positions don't change. `An_unreachable_statement_is_reported_at_its_start` has a row for each kind, plus a nested `buf[pt.a].lo = 2` and a call. Minor: the `Start` summary still describes the old positions ("an assignment's position is its operator's") as a reason, which reads fine.
- **Suggestion 2, done.** `Bytes(n)` gives "1 byte", and the report test is updated.
- **Suggestion 4, done.** `ForStmt.VariablePos` comes from the name token. `An_unused_loop_variable_is_reported_at_its_name` checks 3:9. The skill's `tidy.tern` output moved to 10:9, and `SkillTests` passes.

2943 compiler tests and 36 CPU tests pass. Benchmarks are unchanged (1254 / 350780). Every "done when" item above still holds.
