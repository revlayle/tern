# Review: M20 follow-up, branch `m20-followup`

## First review (`626a345`), 2026-11-09

**Not ready to merge: two must-fix items**, both in the constant-expression change (`4c96d78`). Each is a program that compiles and runs correctly on `main` and fails to compile on the branch. The rest of the milestone is good: `addr()` in initial values gives the right bytes in every form I tried, the tools and the library additions work, and the Galaxy Stars port (a copy with its `build/*.bin`) still compiles, `main.tern` and `check_player.tern`.

2121 tests pass (36 CPU, 2085 compiler). `BenchmarkTests` and `SkillTests` pass, with no baseline change (1254 / 350780).

### Findings

**1. Must fix: `.lo` / `.hi` of a typed `u16` constant below 256 is an error.** `Checker.cs:1395` converts a constant with `LiteralType`, the smallest type that holds the value, so a `u16` constant whose value fits in a byte becomes `u8`, and the size check that follows rejects it. On `main` the constant was `u16` and this worked.

```tern
const LINE: u16 = 250        // also: const ZP_VEC: u16 = $00FB
var r: u8
fn main() { r = LINE.hi }
```

Branch: "error: .hi needs a 16-bit value, not u8". `main`: `r` = 0. This is the usual way to set the raster line's bit 8 (`LINE.hi << 7`), so the port is likely to hit it. `LINE.lo` fails the same way. A fix: for `.lo` / `.hi`, use the constant's own type (`ConstType`) when it is 16-bit, and `LiteralType` only when it has none or is narrower. The reason `LiteralType` was chosen (`(BOX * 100).lo` with `BOX: u8` must not fail as `u8`) still holds with that rule. Add the case to `ConstantExpressionTests`.

**2. Must fix: a named `~` constant loses its bit pattern.** `const M = ~$0F` stores -16 in the `ConstSymbol`, but `IsBitPattern` is not kept on the symbol, so a use of `M` (`Checker.cs:1327`) is a plain -16 and `Coerce` rejects it in any unsigned context.

```tern
const M = ~$0F
var x: u8 = $FF
fn main() { x = x & M }
```

Branch: "error: -16 does not fit in u8 (0 to 255), the type of 'x', which it is combined with. Use a smaller value". `main`: `x` = `$F0`. With a `u16` (`var r: u16`, `r = r & M`) the branch also fails, where spec 3.1 says `~` takes the width of the type it is used as, so `$FFF0` is the expected result (`main` gave `$00F0`). The fix: carry `IsBitPattern` on `ConstSymbol` (as `ConstType` is) and set it on the `NameExpr`. Test both spellings (`x & ~M` with `const M = $0F`, which `Not_of_a_constant_takes_the_width_of_the_value_it_is_used_with` covers, and `x & N` with `const N = ~$0F`, which no test has), on `u8` and `u16`, and `const N = ~$0F << 4`. Galaxy Stars uses only the first spelling, so it compiles. The error's fix "Use a smaller value" does not help here either; once the pattern is kept, only `~` values wider than the type reach it.

**3. Suggestion: an inferred type from an untyped constant says nothing about where `u8` came from.** `Checker.cs:720`:

```tern
const BOX: u8 = 13
const E = BOX * 100
var r = E
```

"error: 1300 does not fit in u8 (0 to 255)". The `u8` comes from `BOX` through question 81, which the user cannot see. `main` silently gave 20, so the error is better, but it should name the constant as `CoerceNamed` does: "..., the type of the constant 'BOX' in 'E'. Write 'var r: u16 = E'". The same holds for the `CoerceNamed` message itself when the operand is a named constant: for `w + E` it says "the type of the constant 'BOX' in it" and suggests "give 'BOX' the type u16", while the source shows only `E`.

**4. Suggestion: `.lo` / `.hi` of `addr()` means one thing in an initial value and is an error in code.** In an initial value `addr(map).lo` is accepted (spec 4 says so), while in code and in `addr(map).lo + 1` it is "'.lo needs a number; convert the pointer first: (p as u16).lo'" (spec 3.4). Following that advice in the initial value, `(addr(map) as u16).lo + 1`, gives the general "can only be used alone, plus or minus a constant, or as .lo / .hi" error, which is right but the first message sent the user the wrong way. Either make the initial-value message the one shown for `addr(x).lo + c` ("Write (addr(map) + 1).lo"), or ask the spec session whether `.lo` / `.hi` should apply to an `addr()` everywhere. Not blocking.

**5. Suggestion: grammar in the `.lo` / `.hi` table error.** `Checker.cs:788`: "but this value is a i8" (also `AddressInitTests` line 194). Write "an i8", or "this value is i8".

**6. Suggestion: no test for a zero-page global initialized with an address.** The zero-page startup path (`TernCompiler.cs`, `ImmVarByte` / `ImmLabelByte` with an offset) has no test of its own. I checked it by hand and it is right:

```tern
var map: [u8; 300]
var h: u8 @ zp = (addr(map) + 299).hi    // lda #(map+299).hi / sta h
var l: u8 @ zp = (addr(main) - 1).lo     // lda #(main-1).lo / sta l
var j: u16 @ zp = addr(main) + 2
```

A test with these three lines would guard it.

### Checked and correct

- `addr()` in initial values: a fixed-address variable, a struct field (`addr(pl.y)`, `+ 1`), `- 1`, a constant on the left (`2 + addr(map)`), split and `linear` `u16` arrays, a function, and `.lo` / `.hi` of each with an offset, in a split `[u16; _]` table, a `[u8; _]` table and a zero-page pointer. All bytes match the addresses the program uses. Errors for `addr + addr`, `addr - addr`, `addr * 2`, `addr & $FF00`, `addr as u8`, `addr(a[2])`, a field of a struct array, `.lo` into `u16` / `i8`, and a negative offset are clear.
- Typed constants with runtime values keep their old results (`1 << BIT`, `y + ROW`, `r + (NEG as u8)`, `r + C` with `C = -1`, `for i in 0..N`). A typed constant compared with or assigned to a pointer now works (`p == SCREEN`), as spec 3.4 says. `x + BOX * 30` into a `u16` and `r < A + 100` are now errors where `main` wrapped silently, as spec 3.1 says.
- `jmp (kernal_handler)` in `c64.set_kernal_irq` cannot read across a page end: the compiler pads a variable used by `jmp ()` (`JumpVectorOffsets`). The stub pulls Y, X, A in the order the KERNAL's `$FF48` pushes them.
- `--set` parsing, `tern bench`'s index errors, exit code and `ok: false`; `v += 2` / `v -= 2` held in X or Y.

### Checklist (roadmap, M20 follow-up)

- [x] Tools: `tern run --set` and `tern bench --set`; `tern bench` prints index errors and fails the variant (`RunToolTests`, `BenchToolTests`)
- [x] `v += 2` / `v -= 2` on a variable held in X or Y is `inx` / `inx` (`dex`, `iny`, `dey`) (`HeldVariableTests`)
- [x] `addr()` in initial values, with split and `linear` tables, `.lo` / `.hi` tables, fields, an overlay's data from resident code, and each error (`AddressInitTests`, `OverlayTests`)
- [ ] Constant expressions computed exactly, tested at the edges: done and tested as listed, but findings 1 and 2 are regressions in the same change
- [x] `[T; _]` with its errors (`DataTests`)
- [x] `c64.sprite_xy` and `c64.set_kernal_irq`, checked in VICE (by the coding session), skill notes on `cld` and the KERNAL (`LibraryTests`, `examples/kernal_irq.tern`)
- [x] `BenchmarkTests` and `SkillTests` unchanged
- [x] Done when: the gaps 1, 3, 4, 5, 7, 8, 9 and 10 compile in their plain form (`GalaxyStarsGapTests`); the port itself still compiles
