# Review V1 fixes 2: code from Galaxy Stars and smashy smashy

Branch `v1-fixes-2` at `065f7f9`, from `main` at `95bc4ab` (12 commits).

**Ready to merge.** No must-fix items. The suite passes in `../6502-language-dev` at `065f7f9`: 11,868 compiler tests (3 skipped: `TERN_COMPARE` and the two `TERN_REAL` checks) and the CPU tests. The new test classes (and `PointerTests`) pass with `TERN_TARGET=atari`, `apple2` and `nes` and `Platform!=c64` (180 each). The benchmark baseline did not move, and `BenchmarkTests` passes, so it has no rewrite to check.

Two things stay open, both handed to others as the roadmap allows: the shape `t = q + c as u16 + 3` (question 187, for the spec session), and committing the two games' `gaps.md` sections (waiting for the developer's approval, by those repositories' rule). Neither blocks the merge.

## What was checked

Each item was compiled and run in small programs of my own as well as through the branch's tests, at edge values, with the results checked against values worked out by hand:

- **16-bit compare with a constant whose low byte is 0 (or `$FF`).** `w < 256`, `>= 256`, `< $C000`, `> $1FF`, `<= $FF`, `<= $BFFF`, `> $FF`, `>= $200`, for `w` = 0, 255, 256, 257, 511, 512, `$BFFF`, `$C000`, as branches and as a `bool` value: all right. The shape is spec 11's (`lda w+1` / `cmp #>K` / branch; `lda w+1` / `beq`/`bne` for 256). Volatile operands keep the full compare.
- **`if c { break }` / `continue`.** With `&&`, `||`, `!`, in `for ... using X`, `while true`, a `step -1` loop and a `u16` loop: right counts. With 50 statements between the test and the loop's end, the branch becomes the assembler's `bne *+5 / jmp`, the same code as before. `InvertBranchesOverJumps` only fires when the `jmp` has no label and the branch's target label is right after it, and skips assembly.
- **The subtract is the compare.** `lda p / sec / sbc q / bcc endif / sta d`, and the `<` / `else` form with `bcs`: spec 11's shape. Destinations that are struct fields (`s.z`), struct-array fields with variable and constant indexes (`e[i].y`, `e[1].z`), and the `else` form give the right bytes. Calls and volatile reads in `a` or `b` are left out, as the doc comment says.
- **A sum of two widened bytes.** `(q as u16) + (q >> 1)` is spec 11's shape with no 16-bit temporary. An indexed 16-bit destination with indexed operands on both sides (`w[i] = (t[j] as u16) + u[k]`, `w[k] = (t[j + 1] as u16) + u[j - 1]`) gives the right values; X is reloaded before the store. `(vol as u16) + f()` with `vol` volatile keeps spec 5's order (`vol` first, through a temporary). `(x as u16) + bump()` where `bump` changes `x` reads `x` after the call, which spec 5 leaves unspecified, and the 8-bit form does the same.
- **A 16-bit local used for its bytes.** `var t: u16 = a + b`, `x = t as u8`, a test of the high byte: spec 11's shape (`adc b` / `sta x` / `adc b+1` / `beq`), `t` gone. `StoreWhereComputed` stops at labels, calls, branches, pointer and numbered memory, a write of the copied byte, and any write of the moved store's index register; it does not move a store into volatile, fixed or overlay memory. I found no case where the move changes what the code does.
- **The loop variable in its own register.** A `fn` with a body, an `inline fn` and an `asm fn`, each keeping the register, in `using X` and `using Y` loops, with another argument that needs an index (`put(i, t[j])`, which uses `ldy j`): right values, and the call loads nothing. Another argument there, or a function that does not keep the register, is still an error (tests).
- **`a[f()] += v`.** Beyond the tests: `a[f(2)] <<= 1`, `w[f(0)].hi += 1`, `w[f(2)] -= 1` on `$FFFF`, `w[f(1)] -= a[f(0)] as u16` and `a[a[f(3)]] += 100`. Each call runs once, the index's call before the value's (a call log), and the elements are right.
- **Platform folders.** A program with shared `logic.tern` and `score.tern`, `c64/` and `nes/` versions of `video.tern` that each import their own `tiles.tern`, built for the C64, the NES and the Apple II (which has no folder and uses the shared `video`): each finds its own modules and gives the right result. A main file in `c64/` finds `c64/video.tern`, and built for the NES it gives the main-file error. A module only in `atari/` is an error in a NES build and builds for the Atari. A library name in `nes/` is an error on the NES and not on the C64. The messages match spec 2.2's.
- **`q - v` from A, and frames without unnamed bytes.** `vol - f()` reads `vol` first (into a temporary), then `jsr f / eor #$FF / sec / adc _t0`. `DropUnnamedFrameVariables` sees `VarOperand` and `ImmVarByte`; the only other item that names a variable, `AddressByte`, comes from `addr()`, which sets `AddressTaken`, so nothing it drops is still referenced.

## Findings

### 1. The "Declare as a pointer" message misses `return` (suggestion)

`Checker.TypeRules.cs:503` (`CheckValueFor`) is used for arguments, `=` assignments and initial values, the three positions the roadmap names. A function returning a pointer gets the old two-step fix:

```
const SCREEN = $C000
var col: u8 = 3
fn get() -> ptr u8 { return SCREEN + col }
```

gives "49152 does not fit in u8 ..., which it is combined with. Convert 'col' with 'as u16'", which leads to the second error the gap was about. Fix: check `return` values with `CheckValueFor` and the function's return type too, with a test row.

### 2. A named constant folded with another constant is not found (suggestion)

`Checker.TypeRules.cs:515` (`AddressConstantAt`) looks for a `NameExpr` at the error's position. In `q2 = SCREEN + 1 + col` (`q2: ptr u16`) the error is at the folded `SCREEN + 1` (49153), a `BinaryExpr`, so the message falls back to "Convert 'col' with 'as u16'". `SCREEN + 40 * row + col` is a common form in screen code. Fix: when the error is at a constant `+` / `-` subexpression, look for the one untyped named constant inside it. Minor; a test row with `SCREEN + 1 + col` would guard it.

### 3. Notes for the spec session (no change on this branch)

- Spec 2.2 and the five spec 11 rows (`s = (a as u16) + e`, `var t: u16 = a + b`, `if w < K`, `if a >= b { d = a - b }`, `if c { break }`) still say "Planned"; they are built now.
- Questions 186 (may a volatile store move earlier past code that touches no shared memory) and 187 (what shape `t = q + c as u16 + 3` should have, or whether the roadmap note goes) are open in `docs/status.md`.
- Galaxy Stars went from 4,706 to 4,740 cycles per play frame though its code only got shorter. The status explains this as loop branches that now cross a page after the code moved. That is layout, not a slower shape: a branch over a `jmp` turned into one branch is never slower on either path.

## Done when

- [x] Each item has its listing test and matches its spec 11 row: `HighByteCompareTests`, `WidenedSumTests`, `StoreWhereComputedTests`, `LoopRegisterArgumentTests`, `BreakBranchTests`, `SubtractCompareTests`, `CallIndexCompoundTests` (the roadmap's cases: call count, edge indexes, `u16` carry, nested index, volatile element through the bus log, `inc a,x`), `PointerTests` (message, `--json` suggestion, argument, assignment, initial value, and the typed constant's plain pointer add), `PlatformFolderTests` (both targets, a platform module replacing a shared one, a main file in a platform folder, the errors), `LeftoverShapeTests`. Listings checked above against spec 11.
- [x] The removed error ("Compound assignment to an element whose index calls a function is not supported") is gone, and its `ErrorTests` row with it.
- [~] The shapes seen while building V1 fixes: three of four done (`q - v` from A, a parameter's frame byte, `r[3] = a - b` already direct). `t = q + c as u16 + 3` is not built; question 187 asks the spec session what shape is meant.
- [x] The benchmark baseline is rewritten with each change: no change moved it, and `BenchmarkTests` passes.
- [~] Galaxy Stars' and smashy smashy's plain routines measured again and the numbers recorded in their `gaps.md`: recorded (status: sections "V1 fixes 2" in both ports), not yet committed in the game repositories, waiting for the developer's approval; `validation/c64/*/gaps.md` will be copied again after that.
- [x] Routines within 5% move to plain Tern at the start of batch 3: none is within 5% (the closest, `explode.hits`, 2.6% / 3.0% average but 7.3% on the worst frame), so batch 3 moves none.
