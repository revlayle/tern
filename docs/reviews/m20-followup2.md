# Review: M20 follow-up 2, branch `m20-followup2`

## First review (`f3e3ef4`), 2026-11-09

**Ready to merge.** No must-fix items. All six roadmap items are built and tested, and the "done when" test passes. Struct literals give the right bytes in every layout and placement I tried. 16-bit shifts by 8 to 15 give the right results, signed and unsigned, and match the spec 11 rows. Constant arithmetic catches every overflow I tried. The four suggestions below are about error messages and one gap between the lexer and the new 64-bit rule.

2385 tests pass (36 CPU, 2349 compiler). `BenchmarkTests` and `SkillTests` pass with no baseline change (1254 / 350780).

### Findings

**1. Suggestion: number literals stop at 32 bits, while constant arithmetic is now 64-bit.** `Lexer.cs:226` rejects any literal above `$FFFF_FFFF` ("Number is too large"). So `const BIG = 5000000000` is an error even though the value fits the signed 64-bit range of spec 3.1, and `-9223372036854775807 - 1` cannot be written. `ConstantRangeTests` works around this by building `MAX` and `MIN` from shifts. Spec 2.1 and 3.1 say nothing about how large a literal may be. Either raise the lexer limit to 2^63 - 1 (with an accumulation that cannot overflow), or state the 32-bit limit in spec 2.1. This is a spec question. The message also has no fix sentence; it could say "Use a value up to $FFFFFFFF" or whatever limit is chosen.

**2. Suggestion: a struct literal in the wrong place *inside* an initial value gets the "in code" message.** `StructLiteralInCode` (`Checker.cs:855`, reached from `CheckExpr` at `Checker.cs:1443`) says the literal is "only the initial value of a global or of data", but these literals already are part of a global's initial value:

```tern
struct P { x: u8, y: u8, n: ptr P }
var a: P = P { n = P { x = 1 } }   // a pointer field given a record
var b: u8 = P { x = 1 }            // a struct literal for a u8
const K = P { x = 1 }              // a constant
```

All three print "A struct literal is only the initial value of a global or of data: ... Assign the fields one by one, e.g. 's.x = 1'". The variable `s` does not exist, and assigning fields does not fix any of these cases. Better messages:
- pointer field: "'n' is ptr P, an address, not a P. Declare the record as data and write 'n = addr(name)'"
- `u8`: "Expected u8, found a P literal"
- `const`: "A constant cannot be a struct. Declare it as data"

The first case is the most likely one, in linked records such as level headers.

**3. Suggestion: a missing `,` between list elements on separate lines gives "Expected ']', found 'L'".** (`Parser.cs:1002`.) Fields inside a literal may be separated by line breaks (question 86), but the literals in a list may not:

```tern
var tab: [L; 2] = [
  L { row = 1 }
  L { row = 2 }
]
```

People writing tables of records will hit this often, because the rule changes between the two levels. Say "Expected ',' or ']' after the element, found 'L'. Write ',' after the '}'". If the spec session keeps question 86 as built, it may also be worth asking whether list elements should allow line breaks the same way, so both levels follow one rule.

**4. Suggestion: an assembly shift count outside 0 to 63 gets the wrong message.** `Checker.Asm.cs:113`: `lda #1 << 64` says "Assembly operands can only use + - * / % & | ^ << >>", although it does use `<<`. Code outside assembly says "Shift count must be 0 to 63". Use that message in assembly too. This message existed before this branch (with 32); only the limit changed here.

### Checked and correct

- **Struct literals:** a single global (also `@ zp`), `data`, a nested struct, `bool`, `i16`, a `ptr` field with `addr()`, a split `[u16; 2]` field mixing a constant and `addr(B) + 2`, a string field, `.lo` / `.hi` of an address in a `u8` field, and a module struct (`wide.U32 { lo = 1000, hi = 2 }`). In a table array, the address halves (including the split array field) land in the right tables, read back with a variable index. Line-break separated fields, a trailing `,`, `P {}`, and lists that span lines inside a literal all work. A multi-line call after them still parses. Every error in the roadmap has a test, and the messages are clear.
- **Headers:** `if x == LIMIT {`, `while`, `for ... in 0..LIMIT {`, and `match n {` still parse as a name followed by a block (`A_name_before_a_block_in_a_header_is_not_a_literal`).
- **Shifts:** `u << 8`, `u >> 9`, `a[i] >> 8`, `a[i] << 15`, `(u + 1) >> 12`, `(b as u16) << 10`, `u + (u >> 8)`, `u - (a[0] << 9)`, `(u >> 8) & u`, signed `s >> 8`, `>> 15`, `(s + 1) >> 10`, `s << 13`, and in place `u >>= 11`, `s >>= 9`, `w <<= 8` all match the values computed by hand. The listing matches spec 11 (`lda v / sta w+1 / lda #0 / sta w`; signed: `ora #$7F / bmi`).
- **Constant arithmetic:** `(1 << 62) * 2`, `1 << 63` in assembly, and `3 << 64` are errors. `-1 << 63 >> 60` is -8. `MIN / -1` overflows (C# throws for it even unchecked, and that is caught). `MIN % -1` is 0. The error points at the step that leaves the range.
- **Local `const`:** nested blocks, after a `return`, inside a `while` body on the first line, and computed from another local const. Every walker over block statements (`Lowering`, `Children`, `ContainsBreak`, `ContainsCall`, `AssignsVar`) ignores a `ConstDecl` correctly. `GenBlock`, the F9 while shapes and `AlwaysReturns` use `BlockStmt.Code`.
- `LayoutIndex` / `ByteLayout` generalize the old two-table split correctly (the old code was the `size == 2` case). `indexed` and `linear` keep records whole.
- The `GalaxyStarsPagesTests` "done when" test compares every byte of the record tables with the parallel arrays, using each program's own text addresses, and checks that both programs compute the same sum.

### Checklist (roadmap, M20 follow-up 2)

- [x] Struct literals in initial values: a global struct, global arrays of structs, `data`, fixed-address `data` in an overlay module (8.5 example), every layout against C#, nested structs, 16-bit and pointer fields with `addr()`, array fields from a list and a string, `[T; _]`, each error (`StructLiteralTests`)
- [x] Constant arithmetic in signed 64 bits: errors at any step, shift count 0 to 63, edges, rounding of `/`, `%` and `>>` (`ConstantRangeTests`); see finding 1 for literals
- [x] `const` inside a function: use, scope, clashes, `for` bound, array length (`LocalConstTests`)
- [x] The string-literal error has a fix sentence, given as the suggestion in `tern --json` (`DiagnosticTests`)
- [x] 16-bit shifts by 8 to 15 move a byte first: unsigned and signed, in place and into another variable, every count at edge values against C# (`ByteShiftTests`); no benchmark moved; the skill's ring example went from 3905 to 3217 cycles
- [x] A listing test for each new spec 11 row: compares, `&&`, `||`, a compare stored in a `bool`, `i8 >> 1` (`CompareShapeTests`)
- [x] Done when: the `how_row` / `how_col` / `how_text` group compiles as an array of struct literals and gives the same tables (`GalaxyStarsPagesTests`)
