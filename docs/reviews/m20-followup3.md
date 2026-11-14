# Review: M20 follow-up 3, branch `m20-followup3`

## First review (`16740d4`), 2026-11-09

**Not ready to merge.** Three must-fix items: one regression in module loading and two cases of "constants on demand" that still fail, with wrong messages. The rest is solid. Evaluation order holds in every case I tried (operators, arguments, indexes, compound assignments, volatile targets, `&&` / `||`, struct-array indexes), the `p = q + e` and sign-test shapes match the spec 11 rows and give correct results, `var` lines in an `asm fn` share frames correctly (also across a `jsr` from the assembly to a Tern function), and `step` works as a name.

2475 compiler tests and 36 CPU tests pass. `BenchmarkTests` and `SkillTests` pass with no baseline change (1254 / 350780).

### Findings

**1. Must fix: a main file named like a library module can no longer import it.** `Module.cs:62` compares `<importer's folder>/<name>.tern` with the main file's path *before* `Find`, but `Find` looks in the library first (`lib/<platform>`, `lib`, then the importer's folder). So a program file `random.tern` (or `text.tern`, `sprites.tern`, `math.tern`) in the user's folder:

```tern
import random
fn main() { random.seed(5) }
```

was a program that loads `lib/random.tern` on `main`, and on the branch is "'random' is the program's main file, which cannot be imported". Do the check on the path `Find` returns (`path` equals the main file's full path), so it fires only when the import would really load the main file. Add a test with a main file named after a library module.

**2. Must fix: `len()` of a global placed in a memory region, in a constant, fails with "'buffers' is not declared".** `Checker.cs:73` declares the main file's constants, and through `Demand` the globals they name, before `Checker.cs:78` evaluates the `memory` block. `ApplyPlacement` (`Checker.cs:823`) then does not know the region and checks `buffers` as an expression:

```tern
memory { $C000..=$CFFF region buffers }
const N = len(map)
var map: [u8; 2048] @ buffers
var r: u16
fn main() { r = N }
```

The order in the file does not matter (with `var map` first the error is the same, since all constants come before all globals). The decision says only a constant that depends on itself is an error, and a long buffer in a region with `len(map)` in a constant is a natural thing to write. The memory block itself may use constants (`MemoryTests`: `SPRITES..=SPRITES + $3FF`), so it cannot simply move first; one option is to look up a region name in `program.Memory`'s syntax when `_layout` is not evaluated yet, or to evaluate the block on demand the first time a placement needs it. Test: the program above, and the same with the constant after the variable.

**3. Must fix: in an import ring, a global demanded from the other module fails if its initial value takes `addr()` of a function or of a later global.** A global named by `len()` during another module's turn is declared then, and its address initial value goes to `_addressInits`. That list is worked out at the end of the *current* module's globals (`Checker.cs:83`), in the global's own module, but before that module's functions and remaining globals are declared. Files (main imports `xb` first, so `xa` is checked first):

```tern
// main.tern
import xb
import xa
var r: u8
fn main() { r = xa.K }

// xa.tern
import xb
const K = xb.N

// xb.tern
import xa
const N = len(tbl)
var tbl: [u16; 2] = [addr(fb), addr(fb)]
fn fb() { }
```

gives "addr(fb) is known only when the program is placed, so it cannot be part of a constant", which is wrong: `tbl` is a `var`, not a constant. With `addr(v)` and a `var v: u8` after `tbl`, it is "'v' is not declared". With the imports in the other order both programs compile, and in one module they compile too. Fix: keep each entry until its own module's turn (work out only the entries whose module is the current one, and leave the others in the list). Test: both variants, in this module order. This is the port's situation (a `len()` of a table across the `video` / `stars` / `explode` ring), so the "done when" test passes only because its tables hold no addresses of later declarations.

**4. Suggestion: `0 > w` on an `i16` still takes the full signed compare.** The one-byte swap (`CodeGen.cs:2004`, `c < x` becomes `x > c`) gives `0 > s` the `bmi` shape for an `i8`, but for an `i16` `if 0 > w` is `lda w / cmp #0 / lda w+1 / sbc #0 / bvc / eor #$80 / bpl` (14 bytes against 4: `lda w+1 / bpl`). Spec 11 lists only `s < 0` / `s >= 0`, so this is not a conformance problem; swapping a constant left side for 16-bit compares too would make the two spellings cost the same.

**5. Suggestion: the self-import message reads oddly from the main file.** `import main` in `main.tern` gives "Move what it needs from it into a module that both import": "it ... from it", and there is no second file that would "both" import. For the main file a better sentence is "Remove this import" or "Move the shared code into a module".

### Checked and correct

- Evaluation order, beyond `EvaluationOrderTests`: `a[vol & 7] += f(1)` (index read before the call), `recs[f(2)].y = f(3)`, `f(4) == 4 && f(5) == 5`, `f(6) == 0 || f(7) == 7` (log 1 to 7 in order), `vol += f(2)` with `f` changing `vol` (12, the old value), `arr[vol] -= f(3)`.
- `p = q + e`: `aw[i] + a[i]` with `i` in X, `(a[j] << 1) + aw[j]` with `j` in Y, `q + (a[k] << 1)` with `q` a pointer, `aw[k] + a[j]`, `aw[k + 1] + a[j - 1]`, `ss[k + 1].v + ss[k].b`, all at carry edges ($xxFF + 1, $FFFF + $80, $FFFF + 0) against hand-computed results. The port's shape is `lda a,x / asl / clc / adc q / sta p / lda q+1 / adc #0 / sta p+1`.
- Sign tests: `s < Z` with `Z: i16 = 0` and `s: i8` (sign-extended, correct), `neg() < 0` with an `i8` return in A (`cmp #0` added, since the `jsr` leaves no flags).
- An `asm fn` with `var` lines that `jsr`s a Tern function with locals: the frames do not overlap.

### Done when

- [x] Calls and `volatile` reads run left to right: tests for `|`, `+`, `-`, compares, nested arguments, `a[f()] = g()`, `a[i] op= f()`, two volatile reads, the port's speck expression. No benchmark moved.
- [ ] Constants resolved on demand across modules: `len()` in a constant, same and other module, a ring of three, the cycle error with its chain are tested. Findings 2 and 3 are cases that should work and do not.
- [x] `var` lines in a plain `asm fn`: frame sharing, same name in two functions, `(p),y` pointer in zero page, the errors.
- [x] `[u8; _]` from a string, with an encoding prefix and `\0`; the written length is checked.
- [x] `step` a keyword only after a `for` range: `fn step()`, a local and a field named `step`, `for i in 0..step step 2`; the keyword example moved to `match`.
- [x] A line starting with a binary operator: the error, a line ending with `&&` continues, `-1 =>` still an arm.
- [x] Spec 11 rows: listing tests and results at edge values; `BenchmarkTests` and `SkillTests` unchanged.
- [ ] Importing the main file is an error with the fix: built and tested, but finding 1 is a regression from the same check.
- [x] `c64.cia_irqs_off()`: a plain `asm fn`, used by `examples/kernal_irq.tern`, checked in VICE (per status).
- [x] The skill: the evaluation-order rule and its exception, `var` lines in `asm fn`, line continuation.
- [x] The port's workarounds for gaps 11, 12, 15, 17, 18, 19, 23 (the error) and 24 compile in their plain form (`GalaxyStarsGapTests`).

## Second review (`cf028a2`), 2026-11-09

**Ready to merge.** The three must-fix items and both suggestions from the first review are fixed and tested. Two new suggestions below, both small and in the new memory-block ordering; neither blocks the merge.

2480 compiler tests and 36 CPU tests pass. `BenchmarkTests` and `SkillTests` pass with no baseline change (1254 / 350780).

### First review findings

- **1 (main file named like a library module): fixed.** `Module.cs` compares the main file with the path `Find` returns. `random.tern` with `import random` loads `lib/random.tern` again; `import main` from a module and from the main file still gives the error. `ModuleTests.A_main_file_named_like_a_library_module_imports_the_library_module`.
- **2 (`len()` of a global in a region): fixed.** `EnsureMemoryLayout` evaluates the memory block the first time a main-file placement `@ name` needs it. My repro works with the constant before and after the variable (`r = 2048`). `ConstantOrderTests.Len_of_a_global_in_a_region_in_a_constant`.
- **3 (import ring with `addr()` in a demanded global): fixed.** `_addressInits` entries are worked out at their own module's turn. Both repros (`addr(fb)` with a later function, `addr(v)` with a later global) compile and run. `ConstantOrderTests.A_global_used_early_by_another_module_keeps_its_address_initial_value`.
- **Suggestion 4 (`0 > w` on `i16`): done.** `lda w+1 / bpl`. I also checked `0 < w`, `0 >= w` and `0 == w` at `w = -2` (only `0 >= w` holds).
- **Suggestion 5 (self-import message): done.** "Remove this import".

### Findings

**1. Suggestion: the memory block is now evaluated for any `@ name`, not only a region, which gives a false cycle.** `Checker.cs:842` calls `EnsureMemoryLayout` whenever the placement is a `NameExpr`, also for a constant address:

```tern
const SCREEN = $0400
const SZ = len(buf)
memory { $C000..=$C000 + SZ reserved }
var buf: [u8; 16] @ SCREEN
var r: u8
fn main() { r = SZ }
```

This compiles on `16740d4` (`r = 16`). On `cf028a2` it is "'SZ' depends on itself: SZ -> buf -> SZ", but `buf` does not depend on the memory block, since `SCREEN` is a constant. The program is unusual (a memory block sized by a `len()` of a variable at a fixed address), so this is a suggestion. Fix: call `EnsureMemoryLayout` only when the name is one of the regions in `_module.Syntax.Memory` (the syntax is there before evaluation), or only when `Lookup(name)` finds no symbol.

**2. Suggestion: the new error "The memory block depends on itself" has no test.** (`Checker.cs:219`.) The coding conventions ask for one. It can be reached:

```tern
const A = len(map)
memory { $C000..=$C000 + SZ region buffers }
var map: [u8; 16] @ buffers
var buf: [u8; 16] @ buffers
const SZ = len(buf)
var r: u8
fn main() { r = A }
```

The shorter case (`const SZ = len(buf)` used by the block, `buf @ buffers`) gives the constant-cycle error "'SZ' depends on itself: SZ -> buf -> SZ" instead. That error is also right, but its chain does not mention the memory block. Add the program above to `MemoryTests` or `ConstantOrderTests`.

### Done when

- [x] Calls and `volatile` reads run left to right.
- [x] Constants resolved on demand across modules (findings 2 and 3 of the first review fixed).
- [x] `var` lines in a plain `asm fn`.
- [x] `[u8; _]` from a string.
- [x] `step` a keyword only after a `for` range.
- [x] A line starting with a binary operator.
- [x] Spec 11 rows; `BenchmarkTests` and `SkillTests` unchanged.
- [x] Importing the main file is an error with the fix (the first review's finding 1 fixed).
- [x] `c64.cia_irqs_off()`.
- [x] The skill.
- [x] The port's workarounds for gaps 11, 12, 15, 17, 18, 19, 23 (the error) and 24 compile in their plain form.
