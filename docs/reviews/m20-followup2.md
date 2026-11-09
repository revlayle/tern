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

## Second review (`9db9de6`): review suggestions and questions 86, 87, 2026-11-09

Commits since the first review: `a8cd7e4` (suggestions 1-4), the merge of `main`, and `9db9de6` (question 87, and the question 86 test).

**Not ready to merge: one must fix.** The lexer change is right: the check `value > (long.MaxValue - dv) / radix` before each digit is exact, so every literal up to 2^63 - 1 is accepted and nothing above it, in decimal, hex and binary. But one place still turns a literal into an `int` without a range check, and with the wider literals it now silently gives a small array. The four suggestion fixes give the messages the first review asked for.

2410 tests pass (36 CPU, 2374 compiler). `BenchmarkTests` and `SkillTests` pass with no change.

### Findings

**1. Must fix: an array length above 2^32 wraps to a small length with no error.** `Checker.cs:992-996`: the length check skips its upper bound for a long byte array (`_allowLongBytes`, a global `var` or `data` of `u8` / `i8`), then `(int)len` keeps the low 32 bits:

```tern
var a: [u8; 4294967552]              // compiles: a 256-byte array
data d: [u8; 4294967298] = [1, 2]    // compiles: a 2-byte array
var z: [u8; 2147483648]              // compiles: 0 bytes
```

Before question 87 the lexer stopped at `$FFFFFFFF`, so only lengths 2^31 to 2^32 - 1 reached the cast (and gave a negative or zero length); now any length from 2^32 up gives a valid-looking small array. A length that does not fit in memory should be an error before the cast, e.g. `len > 0x10000`: "An array of u8 has at most 65536 elements" or the existing "no program area has room" message (70000 already gives that). One test with a length of 2^32 + 2 and one with 2^31.

**2. Suggestion: a list with no closing `]` says "Write ',' between the elements".** `Parser.cs:1001` (the suggestion 3 change): `var a: [u8; 2] = [1, 2` at the end of the file gives "Expected ',' or ']' after this element, found end of file. Write ',' between the elements". Before, it was "Expected ']', found end of file". The fix sentence only fits when the next token could start another element. At the end of the file (or before a `}` or `)`), say "Write ']' to end the list".

**3. Suggestion (from before this branch): an assembly operand `label + offset` is not range checked.** `CodeGen.cs:2492` casts the offset with `(int)`, and the assembler keeps 16 bits: `lda B + 65536` assembles as `lda B`, and `lda B + 4294967296` too (listed as `lda B`). `lda $1_0000_0001` alone is "Address ... is outside memory", so the label form should be checked the same way. `main` has the same gap for offsets up to `$FFFFFFFF`; question 87 only widens it. Not needed for this merge.

### Checked and correct

- **Literals (question 87):** `9223372036854775807`, `$7FFFFFFFFFFFFFFF`, `$7FFF_FFFF_FFFF_FFFF` and the 63-bit binary literal are accepted; one above each, `99999999999999999999` and `$FFFFFFFFFFFFFFFFFF` give the new message at the literal's start; `-9223372036854775808` is the same error at the digits (the literal has no sign). `-9223372036854775807 - 1` is the lowest value, `~0` is all bits set. A large literal in other places gives the right error: `x += 2^32` ("does not fit in u8 ..."), an index, a `match` arm, `as u8`, `.lo`, a shift count, an address `@ $1_0000_D020`, `addr(B) + 2^32` in an initial value, `lda #4294967297`, `lda $1_0000_0001`, a struct field array length (over 256). `ConstantRangeTests` writes `MAX` and `MIN` as literals.
- **Question 86:** `[10` / `-5]` on two lines is one element (`10 - 5`), as spec 2 now says; a newline between `1` and `2` is the missing-`,` error (test added).
- **Suggestion 2 messages:** a pointer field given a record, a record for a `ptr` global, for an element of a `[ptr L; 2]` field, for a `u8` / `bool` field, for an element of a `[u8; 2]` global, and a `const` (top level and local) all get the new messages. A wrong nested struct type keeps "This is a P literal, but the value here is a Q". Only a literal inside a constant expression (`const K = 1 + P { x = 1 }`) still gets the "in code" message; that is rare enough to leave.
- **Suggestions 3 and 4:** `[1 2]` and the multi-line list of literals give the new messages; `lda #1 << 64` and `>> -1` say "Shift count must be 0 to 63".
- The skill and guide do not mention the old 32-bit limit.

### Checklist (roadmap, M20 follow-up 2, "after the review, before the merge")

- [x] Number literals up to 2^63 - 1, with an accumulation that cannot overflow (`Lexer.cs:225`)
- [x] The error says "Use a value up to $7FFFFFFFFFFFFFFF (9223372036854775807)"
- [x] `ConstantRangeTests` writes `MAX` and `MIN` as literals
- [x] Tests: the largest decimal and hex literals, one above each, `-9223372036854775807 - 1`
- [x] Line breaks between struct literal fields stay as built; a test that a newline between list elements is still the missing-`,` error
- [ ] No literal silently wraps where it is used: see finding 1

## Third review (`d72bf1d`): second review fixes, 2026-11-09

**Ready to merge.** The must fix is done, and both suggestions are in. 2420 tests pass (36 CPU, 2384 compiler). `BenchmarkTests` and `SkillTests` pass with no change.

### Findings

**1. Fixed (was must fix): array lengths above memory.** `Checker.cs:996`: a length above 65536 is "An array of u8 has at most 65536 elements, all of memory, not 4294967298. Use a smaller length", before the `(int)` cast. `DataTests.A_long_byte_array_fits_in_memory` covers 2^32 + 2 (`var` and `data`), 2^31 (`i8`) and 65537. A length of 65536 still gets the layout's "no program area has room" message.

**2. Fixed (was suggestion): a list with no `]`** before the end of the file, a `}` or a `)` says "Write ']' to end the list" (two tests).

**3. Suggestion: the assembly offset check catches the offset, not the address.** `Checker.Asm.cs:127` rejects an offset outside -65535..65535, so `lda B + 65536`, `jmp f + 70000` and `bne .x + 70000` are errors now. But the sum of a label and an allowed offset still wraps without an error: with `B` at `$081A`, `lda B + 65000` assembles as `lda $0600`, and `lda B + 65535` as `lda $0819` (`B-1`). The message "Use an offset from -65535 to 65535" suggests those are fine. The address is only known when the program is placed, so the full check belongs in the assembler: an absolute operand whose label plus offset is outside `$0000-$FFFF` is "The address B+65000 is $10402, outside memory" (and `.lo` / `.hi` of such a sum could stay allowed, as `lda #(v + 65536).hi` is rare). This is the code from before this branch; it can wait.

**4. Suggestion: a missing `]` at the end of a multi-line table is followed by the next declaration, not the end of the file.**

```tern
var a: [u8; 2] = [1, 2
fn main() { }
```

gives "Expected ',' or ']' after this element, found 'fn'. Write ',' between the elements". This is the common way to forget the `]`. A keyword that cannot start an expression (`fn`, `var`, `data`, `const`, `struct`, ...) could also get "Write ']' to end the list".

### Checklist (roadmap, M20 follow-up 2, "after the review, before the merge")

- [x] Number literals up to 2^63 - 1, with an accumulation that cannot overflow
- [x] The error says "Use a value up to $7FFFFFFFFFFFFFFF (9223372036854775807)"
- [x] `ConstantRangeTests` writes `MAX` and `MIN` as literals
- [x] Tests: the largest decimal and hex literals, one above each, `-9223372036854775807 - 1`
- [x] Line breaks between struct literal fields stay as built; a test that a newline between list elements is still the missing-`,` error
- [x] No literal silently wraps where it is used (finding 1; finding 3 is a smaller gap from before this branch)

## Fourth review (`a508a4a`): third review suggestions, 2026-11-09

**Ready to merge.** Both suggestions are done and tested. 2429 tests pass (36 CPU, 2393 compiler). `BenchmarkTests` and `SkillTests` pass with no change. Every example that is a program compiles (`level1`, `level2` and `player_api` are modules).

### Findings

**1. Done (was suggestion 3): the address in an assembly operand is checked when the program is placed.** `Assembler.cs:334`: an asm instruction (not a branch) whose label or variable plus offset is outside `$0000-$FFFF` is "The address v+16384 is $10000, outside memory. Use a smaller offset". `lda B + 65000`, `lda B + 65535`, `lda v - 5` at `$02`, `sta v + $4000, x`, `jmp f + 65000` and `jmp (p + 16)` with `p` at `$FFF0` are errors. `lda v - $FFFF` with `v` at `$FFFF` (address 0) still compiles. `.lo` / `.hi` of a sum past memory stay allowed (`A_byte_of_an_address_past_memory_is_allowed`). Code from the compiler has no `AsmPos`, so it is not checked, and nothing it emits changed.

**2. Done (was suggestion 4): a list cut off by a keyword** (`fn`, `var`, ... but not `true` / `false`, the only keywords that start a value) says "Write ']' to end the list". Three tests.

**3. Suggestion: the address error names a local asm label by its internal name.** `jmp .x + 65000` gives "The address main.x0+65000 is $105FC, outside memory". The source says `.x`; write that (the label's name as written, as the other asm errors do). Rare; it can wait.

### Checklist

- [x] Every item of the roadmap's M20 follow-up 2 section, including "after the review, before the merge" (see the earlier checklists)
- [x] No literal or assembly offset silently wraps where it is used
