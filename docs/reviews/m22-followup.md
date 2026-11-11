# Review: M22 follow-up, branch `m22-followup`

## First review (`e023754`), 2026-11-11

**Not ready to merge.** One must-fix item: the argument form that spec 4.1 uses as its example, `copy_rows(addr(back) + 1, addr(screen), 25, 12)`, is an error. Everything else is solid. Real constants are refused in every runtime position I tried (an expression, `m.K` from a module, a plain asm operand, an array length, an `@` address, a `match` arm, a `for` bound and step, a global's initial value) and work in constants, `const fn`s, lists and `bool` constants (`const B = K > 1.0`). `offset()` handles nested and module structs and gives clear errors. `repeat()` and the `[7; 4]` error match the spec. The loop-direction errors fire where spec 5.1 says and not for a signed `n..=0`. Generators take `addr()` of globals, data, functions and a module's data (`addr(m.buf)`), and branch to the calling block's `.label`; the listing shows `lda #back.lo`, `ldx #back.hi`, `lda back-1` as expected. `tern --json` gives `calledFrom` for an error in a const fn and in generated code, and `null` otherwise.

2823 compiler tests and 36 CPU tests pass. `BenchmarkTests` and `SkillTests` pass with no baseline change (1254 / 350780).

### Findings

**1. Must fix: `addr(name) + n` as a generator argument is an error, but spec 4.1 uses it as its example.** Spec 4.1, "Arguments": "a generator names the variable in its `emit` text, or takes its address as a `label` (below): `copy_rows(addr(back) + 1, addr(screen), 25, 12)`". The call line's arguments are evaluated by the interpreter, and `Interpreter.Binary` (`Interpreter.cs:292`) rejects any operator with a `CtLabel`:

```tern
var back: [u8; 8]
const fn copy(dst: label, n: int) {
    for i in 0..n { emit { sta dst + i } }
}
fn main() { asm {
    copy(addr(back) + 1, 3)
} }
```

gives "A label's address is decided when the program is placed, so the const fn cannot use it with '+'. Use the label in an emit operand, alone, plus or minus an int, or as .lo / .hi". The spec's rule ("its number cannot be read, compared or multiplied in the `const fn`") does not forbid adding an `int`, and the example needs it: without it, a generator cannot be told "one byte past `back`" except by an extra parameter. A fix: give `CtLabel` an `int` offset; `label ± int` (and `int + label`) makes a new `CtLabel` with the offset changed, and `Operand` emits `name + offset` (or `.label + offset`). Then `emit { name: }` of an offset label must stay an error, and `label - label`, `label * 2`, `==` stay errors. Test: the spec's example run in the emulator (a copy into `back + 1`), an offset `label()` value and an offset block label used as a branch target. If the spec session prefers to keep labels without offsets in a `const fn`, the example in 4.1 needs to change instead; either way the two must agree.

**2. Suggestion: the range text in the loop-direction error loses quotes and adds spaces.** `Parser.TokenText` (`Parser.cs:26`) rebuilds the range from tokens. A char literal's token text has no quotes or encoding prefix, and an index gets a space:

- `for c in 'Z'..='A'` gives "'Z..=A' counts up, so it never runs"
- `for c in a[1]..=0` gives "'a [1]..=0' counts up, so it never runs"

Taking the source text between the first and last token's positions would quote the range as written, with no spacing rules to keep up to date. The same function builds the `[7; 4]` message, so `['A'; 4]` would say "Write repeat(A, 4)", which does not compile.

**3. Suggestion: the listing test for `for i in n..0 step -1` does not check the spec 11 row (question 106).** `LoopDirectionTests.cs:93` checks only `ldx n|` at the start and `|dex|bne|` at the end, so it passes with the code the branch generates, `ldx n` / `cpx #$00` / `bcc done` / `beq done`, where spec 11 gives `ldx n` / `beq done` (4 bytes and 4 cycles more, once per loop). The extra code is on `main` too, and question 106 asks about it. Once the question is settled, check the full shape in this test, so the row is held by a test as the roadmap item says ("keep their code (listing tests)").

**4. Suggestion: the fix example for a real constant in runtime code declares the constant again.** `Checker.CompileTime.cs:196`: with `const K = 1.5` and `out = K`, the message is "'K' is a real number, which exists only at compile time. Use it in a constant or a const fn, e.g. 'const K = round(K * 40)'", which is a duplicate-name error if followed. Use another name in the example, e.g. `'const K_40 = round(K * 40)'` (or a fixed name such as `N` that differs from the constant's).

**5. Suggestion: a const fn returning `addr()` in a constant gets the `label()` message.** `Checker.CompileTime.cs:277`:

```tern
var back: [u8; 8]
const fn k() -> label { return addr(back) }
const A = k()
```

gives "A label() value is only for emit in a generator. Use it in a const fn that emits assembly". The value comes from `addr()`, not `label()`. Something like "This value is a label, an address decided when the program is placed, so a constant cannot hold it. Use addr(back) in the constant, or pass the label to a generator" fits all three kinds.

### For the spec session

- Spec 4.1, "What a `const fn` reads" (line 353 on the branch) still says "and not `addr()`, which is decided after compile-time evaluation", which the 2026-11-11 decision (question 100) and the "Labels" bullet in the same section now contradict.
- Finding 1: keep `addr(back) + 1` as an argument (and build it), or change the example.

### Done when (roadmap, M22 follow-up)

- [x] Real constants: `const SCALE = 1.5` used in a `const fn`, a `for` list and another constant; in runtime code the `real` literal's error; `const T = f()` with an array result is "'T' is an array, which a const cannot hold. Use data" (`ConstFnTests`).
- [x] `offset(T.field)`: an untyped constant, nested fields, in a constant, a `const fn`, an `emit` operand and a plain asm operand; `ldy #offset(Enemy.hp)` / `lda (p),y` in the emulator; errors for a type that is not a struct and a missing field (`OffsetTests`).
- [x] `repeat(value, count)` in `data`, a `var` initial value and a `const fn` (0 elements there); the runtime error; `[7; 4]` is "Write repeat(7, 4)" (`ConstFnTests`).
- [x] `for` direction from the step alone: the two errors, a signed `n..=0` counting up from a negative `n` (emulator and const fn); tests, skill and library updated; `BenchmarkTests` unchanged. The spec 11 listing test is partial (finding 3).
- [ ] Addresses as labels: `addr()` of a global, `data` or function as an argument and in a `const fn`, alone, with an `int` offset and as `.lo` / `.hi` in `emit` operands; a `.label` of the calling block; the errors; both emulator runs (`GeneratorTests`). Not met: the spec's argument form `addr(back) + 1` (finding 1).
- [x] `tern --json` gives the call's location as a field (`DiagnosticTests`; checked with the CLI for a const fn error and for generated code).
