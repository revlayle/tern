# Review: M24 follow-up, `assert` inside a `const fn`

Branch `m24-followup` (`9cf52d2`, `716c0ab`, `fa12c41`), from `main` at `c4a1910`. Roadmap "M24 follow-up"; spec 4.1; decision 2026-11-13, question 128.

## Verdict

**Ready to merge.** No must-fix findings.

`dotnet test`: 3381 compiler tests and 36 CPU tests pass. No benchmark changed. `SkillTests` pass.

## What changed

- **Parser** (`Parser.Statements.cs:101`): inside a `const fn`, `assert cond, "message"` is an `AssertStmt`. It is parsed exactly like the top-level `AssertDecl` (`Parser.Declarations.cs:156`): the same expected tokens and the same messages. In runtime code it gives the error from the spec.
- **Interpreter** (`Interpreter.cs:669`): the condition goes through `Bool`, as an `if` condition does. A false condition throws the message through `Interpreter.Error`, so the "(called from ...)" note and `CalledFrom` come from the same path as every other const fn error.
- **Checker**: the static body walk accepts the statement (`Checker.CompileTime.cs:116`), and the warnings pass reads the condition (`Checker.Warnings.cs:202`).
- **`float.of`** uses `assert e + 128 <= 255, ...`, which is the spec's `e <= 127`. The skill's note about the old error is removed. The const fn rules now say how to reject a bad argument.

## Probes

| Program | Result |
|---|---|
| A generator (`emit`) with an assert, `nops(5)` called inside `asm { }` | error at the assert line, "nops: at most 4 (called from g.tern:9)", `calledFrom` line 9: correct |
| An assert inside a `match` arm, comparing against a local | error at the assert line, called from the constant's line: correct |
| A local read only by an assert | no "unused" warning |
| A const fn used only in an assert's condition (`assert ok(n), ...`) | compiles with no warning: the call counts as a use |

## Findings

1. **Suggestion: a condition that is not a bool gives a different error in a const fn than at the top level.** At the top level (`Checker.Declarations.cs:309`) the error is "An assert condition is a bool. Write a comparison, e.g. COUNT <= 64". In a const fn it comes from the interpreter's `Bool` helper: "An assert condition needs a bool, not an int (called from line 5). Compare instead, e.g. 'n != 0'". Both are correct and both have a fix sentence. The spec says the statement has "the same form and meaning" as the top-level `assert`, so one wording for both would read better. This is not required.

2. **Note: "`float.of`'s error names the value"** (done when). The message is the spec's own text, "float.of: the value is larger than the largest float, about 1.7e38". It does not contain the number itself, and cannot, since an assert's message is a string literal. The user's line is named by "(called from main.tern:N)" and by `calledFrom`, so the error now points at the value's line instead of at an index. I read the item as met.

3. **Note: `suggestion` is `null` for the `float.of` error**, as the status says, because the message has no fix sentence. This matches the spec's text. A library author who wants a suggestion in `tern --json` can add a second sentence that starts with a fix verb. The new skill bullet could mention that, but it is not needed.

## Tests

- `ConstFnTests` has everything the roadmap lists: an assert that holds, one that fails (message, line, `CalledFrom`), a condition on a parameter, one in a module const fn called by another (it names the main file's line), the runtime-code error (in `main` and in a nested block). It also tests a message with a fix sentence (the note goes after the first sentence), locals, a real and a loop variable in the condition, and the parse errors (no message, a message that is not a string, a condition that is not a bool).
- `DiagnosticTests`: `calledFrom` in `tern --json`.
- `FloatTests.Of_a_value_too_large_is_an_error`: the message, that the reported line is the assert, and `CalledFrom`.
- `ModuleParameterTests`: the old runtime-code message updated.
- Every new error has a test.

## Spec conformance

- Spec 4.1: the form, the message at the assert line with "(called from ...)" after its first sentence, `calledFrom` in `tern --json`, a condition on parameters and locals, a string literal message, and the runtime-code error text all match. Rule 3 holds: `assert` only ever means a compile-time check.
- Decision 2026-11-13, question 128: built as decided.

## Done when

- [x] `assert` as a statement inside a `const fn`, reported at its line with "(called from ...)".
- [x] In runtime code: "An assert is checked at compile time. Write it at the top level or in a const fn".
- [x] `float.of` uses it for a value too large; the skill's note about the old error is removed.
- [x] Tests: holds, fails (message, line, called-from, `calledFrom`), a parameter condition, a nested const fn call, the runtime-code error, `float.of` too large.
- [x] The tests pass and `float.of`'s error names the value (see note 2).
