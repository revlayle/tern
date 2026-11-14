# Review: Overlay interfaces, `implements`

Branch `implements` (6 commits from `main` at `cf8041c`), reviewed 2026-11-12 against roadmap "Overlay interfaces: `implements`", spec 8.5 and decisions 2026-11-12 (questions 119-122).

## Verdict

**Ready to merge** (re-review, below). First review: **not ready to merge** until must-fix 1 is done. Decision 120 was settled (`443e968`, on `main`) after the branch's last commit, and the roadmap asks for it to be built on the branch before the review. The rest of the milestone is complete and correct: 3265 compiler tests and 36 CPU tests pass, no benchmark changed, and the example runs in `tern run`.

## Findings

### 1. Must fix: decision 120 is not built

Spec 8.5 ("The module names its own `data`") and decision 120: inside an implementing module, naming the interface's `volatile` variable that matches the module's `data` is an error. The branch accepts it:

```
// api.tern
fn enter(n: u8 using A) @ $6000
fn draw() @ $6003
volatile var title: [u8; 1] @ $6006

// l1.tern, imported with 'import l1 @ lv implements api'
import api
data title: [u8; 1] @ $6006 = [1]
var n: u8
fn enter(k: u8 using A) { n = api.title[0] }   // compiles; should be the error below
fn draw() { n += 1 }
```

Expected: "'api.title' is this module's data 'title'. Use 'title'" at the use of `api.title` in `l1`. Resident code and `api`'s own functions keep using `api.title` with no error. Each place that resolves a qualified name inside a module that implements the interface needs the check (reads, writes, `addr()`, an index). Add a test for the error, and one showing that resident code and a function of the interface module may still name it. Add the message to `FixSentenceTests` if needed (it starts with "Use").

### 2. Suggestion: an interface module placed in an overlay is not rejected

Spec 8.5 says the interface, "imported by the main file, is resident". With `import api @ lv` next to `import l1 @ lv implements api`, the program compiles with no error about the interface. `api`'s routines give no file, so nothing breaks in this case. If `api` has helper functions (which 8.5 allows), they go into an overlay file in the same area as the levels, and a level calling a helper gets the error "'l1.enter' calls 'api.draw', but the modules l1 and api are both in the overlay area 'lv'", which does not point at the cause. An error at the `implements` name ("'api' is placed in the overlay area 'lv'; an interface must be resident. Remove '@ lv' from its import") would be clearer. This is not in the spec's error list, so it is a question for the spec session.

### 3. Suggestion: the allocator's offset line is hard to read

`Allocator.cs:299` puts a nested conditional with a LINQ chain inside the existing chain of `?:`. A local function, such as `RoutineOffset(f)`, or an `if` would read better. No change in behavior.

## Checks done

- Read the diff: parser, checker (`Checker.Implements.cs`), call graph (`OrderRoots`, `MarkParts`), allocator, table generation in `TernCompiler.Build`, the `keeps` check, the `tern run` stand-ins.
- Probes with the branch's compiler:
  - An implementation that calls its own interface (`l1.enter` calls `api.draw`) compiles. Its `jsr $6003` reaches the loaded overlay's table, and no same-area error is reported. Correct.
  - The `keeps` check follows calls: `l1.draw` calling a helper with a `for` loop in X, under `fn draw() @ $6003 keeps X`, gives "'l1.draw' changes X, which 'api.draw' promises to keep".
  - The listing has one `jmp` per entry in each overlay at the routine's address, and a call is `jsr $6003` (spec 11's shape for a fixed-address routine).
  - `tern run examples/stages.tern --screen`: "FOREST   5" over trees, "LAKE     7" over water.
- `Runner` LOAD stand-in: SETLFS takes the secondary address from Y, secondary 0 loads to X/Y, X/Y return the end address, carry is set for an unknown name. This matches the KERNAL.

## Done when

- [x] `import m @ area implements i` and `implements a, b`: a jump table at each routine's address, with the module's code around it (`Each_overlay_file_has_a_jmp...`, `The_module_code_goes_around_table_entries...`, `A_module_implements_two_interfaces`).
- [x] The checks of 12.3 (signature, return, `keeps`, volatile data, 3 bytes apart, fixed inputs, inline and interrupt).
- [x] The call graph counts a call to an interface routine as a call to each implementation (frames, two roots, recursion tests).
- [x] Each error in 8.5 has a test: not placed in an overlay, routine or volatile variable outside the area, overlapping tables, no routines, plus 119's "not a module the main file imports".
- [x] Example: `stages.tern` with two stages loaded in turn and called through `stage_api`. It runs in `tern run` (test) and, per status, in VICE.
- [x] The skill mentions `implements` next to overlays (`language.md` section 12). The guide has it in section 3 (spec session).
- [x] Questions 119-122: 119, 121 and 122 as built; 120 built in the review fixes (`3ca447d`).
- [x] Tests: calls through the interface reach the loaded overlay, frames shared with other code, each check and error, a plain call next to an interface call.
- [x] Tests and example pass.

## Re-review (2026-11-12, `3ca447d`, `f26884d`, `a2ed633`)

**Ready to merge.** 3270 compiler tests and 36 CPU tests pass; no benchmark changed.

- **Must fix 1, done** (`3ca447d`). `CheckNotBinaryData`, called from `UseGlobal`, reports "'api.title' is this module's data 'title'. Use 'title'" when the module being checked implements the interface. Tests cover a read with an index, a write, `addr()` and an asm operand (each at the name), and resident code and an interface helper called from main and from a level. Probes with a rebuilt CLI: the example from finding 1 gives the error at `l1.tern:4:31`; an interface `inline fn first() -> u8 { return title[0] }` called from `l1` compiles, so a helper expanded inside the module is still checked as the interface's code.
- **Suggestion 2:** sent to the spec session as question 123 (`docs/status.md`). Nothing to build until it is settled.
- **Suggestion 3, done** (`f26884d`). `RoutineOffset` is a local function; behavior unchanged (benchmarks and allocator tests are the same).
- The done-when item for questions 119-122 is now met.
