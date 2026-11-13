# Review: Encodings declared by the program (branch `encodings`)

Reviewed 2026-11-12, branch `encodings` at `2cf9acd` (4 commits from `main` at `077c649`). Spec 2.1 "Encodings declared by the program", decision 2026-11-12, roadmap "Encodings declared by the program (before M24)", plus question 123 (decision 123).

`dotnet test`: 3323 compiler tests pass (plus the CPU tests). No benchmark changed.

## Re-review (2026-11-12, `c5317e6`)

**Ready to merge.** All three findings are done in `a5b3f4b`; `dotnet test`: 3333 compiler tests pass, no benchmark changed.

- Finding 1: the checker keeps the encoding lines whose byte is being checked (`_checkingLines`), and a use of an encoding or of the default still being worked out from inside one of them is an error at the literal. Checked: the review's program and its module variant (one error each, no crash), the order with no data first (same error), a prefixed self-reference ("uses 'tiles' itself"), a byte of a non-default encoding using an unprefixed character while the default's byte uses that encoding (error at the inner literal), and an unprefixed character under a built-in base (`encoding tiles: screen { 'B' = 'Z' }` as the default: error). 10 new tests cover these.
- Finding 2: "The range 'A'..='♥' has 9765 characters, but an encoding has 256 bytes. Split the range".
- Finding 3: "Unknown encoding 'fm2.tilez'. Use one of: fm2.tiles"; for a module with none, "the module mem declares no encodings".
- Note, no change asked: when a constant is evaluated before the encoding (`const H: u8 = 'Z'`, `default encoding tiles`, `encoding tiles { 'A' = H }`), the error is the constant cycle "'H' depends on itself: H -> H". That is an error, not a crash, but the message does not name the encoding.

## Verdict (first review)

**Not ready to merge.** One must-fix: an encoding whose byte uses an unprefixed literal while it is the program's `default encoding` compiles with no error, and the compiler then crashes in code generation. The rest is in good shape: the syntax, checks, warnings, the `font.tiles"..."` prefix, the example and decision 123 match the spec, and the tests cover every roadmap item.

## Findings

### 1. must fix: a `default encoding` that depends on itself reports nothing, then the compiler crashes

`Semantics/Checker.Encodings.cs:58`. `ProgramDefault` throws `FollowOnError` when it is entered again while it is working out the default (`_defaultState == 1`). A `FollowOnError` adds no error, because it assumes an error was already reported. Here none was: the default is resolving the encoding, and the encoding's line asks for the default again. `Resolve` and `ProgramDefault` both catch the error, mark themselves failed and rethrow the same `FollowOnError`. So the literal that started it gets no bytes and no error, the checks finish with no errors, and code generation fails on an unchecked expression:

```
default encoding tiles
encoding tiles { 'A' = 1, 'B' = 'A' + 1 }    // 'A' has no prefix, so it is in the default: tiles
data X = "AB"
var out: [u8; 2] @ $C000
fn main() { out[0] = X[0]; out[1] = X[1] }
```

`tern` prints `Unhandled exception. System.InvalidOperationException: NumberExpr` at `CodeGen.GenA` (via `LoadIndex`). The same happens when the default is a module's encoding (`default encoding fontmod.tiles` with the line `'B' = 'A' + 1` in `fontmod`). The data comes first in the checks (encodings no literal used are resolved later, `Checker.cs:135`), so the default is entered first and the cycle ends in state 1.

When the encoding is the first to be resolved (no `data` before it), the same program gives an error, but a wrong one: `Resolve` (line 118) reports "The encoding 'tiles' is based on itself, through ''. Change the base of one of them, or remove it", with an empty base name, since the cycle goes through a line, not the base. The same message for a prefixed literal (`encoding tiles { 'A' = 1, 'B' = tiles'A' + 1 }`).

Expected: one error at the line, for example "The byte of 'B' uses the encoding 'tiles' itself (the 'A' in 'A' + 1). Write the byte as a number". Possible fix: in `ProgramDefault`, state 1 throws a `CompileError` (at the literal, or let `Resolve` report it) instead of `FollowOnError`; and the message in `Resolve` names the line when `BaseName` is null or the cycle did not come through the base. Tests: both programs above, the module variant, and a prefixed self-reference.

### 2. suggestion: the range error suggests a negative start for ranges longer than 256

`Semantics/Checker.Encodings.cs:150`. `encoding tiles { 'A'..='♥' = 0 }` (9765 characters) gives "... so from $00 its bytes pass $FF. Start it at $FFFFDADB or lower, or split the range". When `count > 256`, no start works; say "a range has at most 256 characters. Split the range" instead.

### 3. suggestion: "Unknown encoding 'font.tilez'" lists only the built-ins

`Semantics/Checker.Encodings.cs:107`. For a qualified name, the list is the built-ins plus the current file's own encodings, not `font`'s, which is where the typo is. List the public encodings of the named module instead (`fm2.tiles`), as the unqualified case lists the file's.

## Notes

- Question 124 (`pub encoding`): the branch builds "public unless `private`", consistent with spec 2.2; the spec text needs the change proposed in the question.
- Question 125: the details built are reasonable; each has a test.
- An `encoding` line inside a function body gives "Expected end of statement, found 'tiles'", since `encoding` is a word only at the top level (spec 2.1). Consistent with the spec, if not helpful; no change asked.
- The example: the program ends at `$09F8`, clear of the glyphs at `$2000`; `$D018 = $18` is right for screen `$0400` and characters `$2000`. Checked in VICE: "SCORE 01250" and "LIVES" with three red hearts in the hud font.
- No code generation changed; no spec 11 shape applies.

## Done when

Roadmap: "the tests and the example pass".

- [x] `encoding name { ... }` with single characters and ranges (`Strings_and_chars_take_the_bytes_of_a_declared_encoding`, `A_range_may_end_at_ff`)
- [x] `encoding name: base { ... }`, built-in and declared base, in any order (`A_base_encoding_gives_...`, `A_declared_base_may_come_later_...`, `A_base_may_be_a_modules_encoding`)
- [x] Public module encodings as `module.name` prefixes, `private encoding` (question 124) (`A_modules_encoding_is_used_with_the_module_name`, `A_private_encoding_cannot_be_used_outside_its_module`, asm operand)
- [x] `default encoding` in the main file; program modules follow it, library modules do not (`The_default_encoding_applies_to_...`)
- [x] Errors: a character listed twice, a range past `$FF`, an unknown base, a second `default encoding` line, `default encoding` outside the main file, a character the encoding lacks (suggesting `\$NN`), `\n` not mapped: each tested (`Reports_errors`, `Default_encoding_outside_the_main_file_is_an_error`)
- [x] No crash or missing error for an encoding that uses itself (finding 1, fixed in `a5b3f4b`)
- [x] Non-ASCII characters (`♥`, `é`)
- [x] Example: `examples/statusline.tern` with `hudfont.tern`, tested in `tern run` and checked in VICE
- [x] Question 123: an interface module placed in an overlay area is an error at the name after `implements` (two tests in `ImplementsTests`). There are no bank areas yet, so the "or bank area" part has nothing to check.
- [x] `dotnet test` passes; benchmarks and `SkillTests` unchanged
