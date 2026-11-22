# Review V1 fixes: code from Galaxy Stars

Branch `v1-fixes` at `e253c01`, from `main` at `3625a31`: `ef0b4d5` (gap 27), `089387c` (gap 26), `0ed5385` and `2208eda` (gap 28), `59e91e4` (gap 29, `keeps` on a function with a body), `3d46254`, `e191a0a`, `f9e7a6d` (the small shapes) and `e253c01` (status). Roadmap "V1 fixes"; decision 2026-11-21 (questions 180, 181); spec 6.1, 6.6, 11.

## Verdict

**Not ready to merge.** The code shapes are built as spec 11's new rows state them, each with emulator tests over edge values and a listing test, and the suite passes. Two things stand in the way:

1. A miscompile: the new `MoveParameterToIndex` pass can put a `tax` into a function that `keeps X`, so a `for ... using X` loop that calls it never ends in a Release build (finding 1).
2. The last "done when" item is not done: Galaxy Stars' plain routines have not been measured again, and the port's `gaps.md` has no V1 fixes numbers (its last commit is `fb0db78`, batch 1). The status says so and names it as the next step.

The suite passes in `../6502-language-dev` (`v1-fixes`, clean): 11,736 compiler tests, 1 skipped (the `TERN_COMPARE` comparison), 50 CPU tests. The benchmark baseline is unchanged, which the status explains (no benchmark has these shapes).

## How it was checked

- `git diff main...v1-fixes` and `git log main..v1-fixes`; roadmap "V1 fixes", decision 2026-11-21, spec 6.1, 6.6 and the new spec 11 rows.
- `dotnet test` in `../6502-language-dev`.
- A listing of the spec 11 rows with the branch's compiler: `t = a + b`, `b + a`, `a - b`, `a + $300`, `$300 + a`, `q + e + COLOR_RAM - SCREEN` in both orders, `x -= 1` / `m[i] -= 1` then `== 0`, `d > 0` and `d <= 0` on `i8`, `w > 0` on `i16`. Each is the spec's shape (or shorter, where a dead store went).
- `tern run` of folded sums against values computed by hand: `q + e - 256`, `512 + q + e - 1`, `q - 256 + e + 768`, `s - 512` and `1024 + s` on an `i16`, `(q + 3) - (e + 3)`, `q - (5 - e) + 5` (the `u8` part `5 - e` wraps first, as it should), `q + e + $FF00`. All right.
- Whether the signed `> 0` path could see an unsigned left side (a `u8` against an `i16` zero): the checker rejects the mix, so it cannot.
- `BoundStays` on globals: compound assignments are `AssignStmt`, so `p[0] += 1` is seen; a pointer to the bound needs `addr()`, which sets `AddressTaken`.
- `keeps` with the new optimizer passes, Debug and Release builds of the branch (finding 1).

## Findings

### 1. `MoveParameterToIndex` can write a register the function keeps (must fix)

`Optimizer.cs:59` chooses X, then Y, for the copy of a `using A` parameter when that register is not live after the entry store. `RegistersLive` does not know about `keeps`, so in a function that keeps X, X is free by that measure and the pass inserts `tax`. `CheckKeeps` ran before the optimizer, on the statements, so it does not see it. `CheckKeptAfterOptimizer` (`CodeGen.Checks.cs:76`) would, but it is `[Conditional("DEBUG")]`, so a Release compiler emits the code silently:

```
var out: u8
var out2: u8
var count: u8
fn f(v: u8 using A) keeps X {
    out = 3
    out2 = v
}
fn main() {
    for i in 0..4 using X {
        f(9)
        count += 1
    }
}
```

- Debug build of the branch: `Internal error (InvalidOperationException): The optimizer changed 'f', which keeps X, so that 'tax' changes it`.
- Release build (`dotnet build src/Tern.Cli -c Release`): compiles; `f` is `tax / lda #3 / sta out / txa / sta out2 / rts`. `tern run` stops after 20,000,000 cycles with main not returned, `X=$09`: each call sets the counter to 9.

The suite misses it because no test has a `using A` parameter on a function with `keeps`. The same holds for `keeps Y` when X is live at entry, or for any `keeps X, Y`. Fix: leave out a register the function keeps (`function.Keeps`) in `MoveParameterToIndex`, or make `RegistersLive` count kept registers as live at `rts`, which would also cover any other pass that chooses a register by liveness. Add a test with this program (and the `keeps Y` variant) that runs the loop and checks `count`.

### 2. The check after the optimizer is Debug only (suggestion)

`CheckKeptAfterOptimizer` is the only guard between the optimizer and a broken promise, and the tests run a Debug build, so a Release compiler turns a compiler bug into wrong code with no message. The other `[Conditional("DEBUG")]` checks in `CodeGen.Checks.cs` follow the same convention, so this is consistent; but this one is cheap (one pass over one function's instructions, only for functions with `keeps`) and guards a promise other code relies on. Consider running it in every build.

### 3. The loop bound test's pointer case does not test the pointer rule (suggestion)

`LoopBoundTests.cs:37` sets `p = addr(r1)`, which makes `r1` address-taken, so `BoundStays` returns false on its first line and the rule it was written for (a body that writes through a pointer copies a global bound) is never reached. With `addr()` the only way to point at a global, that rule may be unreachable too; if so it can go, and if not the test needs a case that reaches it.

## Done when

- [x] 16-bit `t = a + b` / `a - b` directly, the same code for either order, a constant with low byte 0 adds the high byte only (gap 27): `DirectAddTests`, listing matches spec 11.
- [x] Constant parts of a 16-bit sum folded, into the high-byte add when the low byte is 0 (gap 26): `ConstantSumTests`, listing matches.
- [x] Flags reused after `inc` / `dec`; `d > 0` / `<= 0` on `i8` / `i16` with no compare; a `bool` just returned in the carry tested from the carry (gap 28): `FlagReuseTests`, listing matches.
- [ ] `keeps` on a function with a body, checked on `fn` and `inline fn`, a promise on `asm fn`; a `using X` / `using Y` loop may call a function that keeps its register (gap 29): built, with `KeepsTests` for each error, but the optimizer can break the promise (finding 1).
- [x] Small shapes: `jmp` to `jmp` threaded (`JumpThreadTests`); `for a..=b` reads its bound in place and `0..=n` has no empty check (`LoopBoundTests`); a `using A` parameter kept in X or Y instead of memory (`AParameterTests`).
- [x] Each item has its listing test and matches its spec 11 row.
- [x] The benchmark baseline is rewritten with each change: nothing moved, and the status says why.
- [ ] The plain versions of Galaxy Stars' assembly routines measured again and the numbers recorded in the port's `gaps.md`: not done (status, "Next step").
