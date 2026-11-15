# Code quality review: Semantics

Scope: `src/Tern.Compiler/Semantics` (19 files, about 6,000 lines), read against spec sections 2 to 7, 8.1, 8.4, 8.5 and 13. Reviewed at commit d223f12. Test programs are shown inline; each was compiled with `tern --target c64 file.tern` or run with `tern run --target c64 file.tern --dump out`. The test suite was not run, since nothing was changed.

## Overview

- **Checker** (13 partial files, 5,100 lines) is one class that does everything between the parser and the code generator. `Run` (`Checker.cs`) declares names in a fixed order (const fns and encodings, structs and module parameters, then each file's constants, structs, functions, globals), with constants and globals declared early on demand (`Demand`). It then checks function bodies, finds warnings, lowers struct accesses (`Lowering`), and builds the call graph: roots, parts, the private-variable check, volatility.
- **Types and constants.** A constant expression has the type `IntLiteral` and an exact 64-bit value; `ConstType` remembers the type of its typed constants. `Coerce` gives it a type where it is used. `Unify` chooses an operation's type.
- **Structs** are lowered in the checker: a field access becomes a view variable, an indexed table or a pointer access (`Expr.Lowered`), so the backend has no struct support.
- **Errors.** `Recover` runs one statement or declaration, reports its error and restores the checker's state. A broken name becomes an `ErrorSymbol`, whose uses throw `FollowOnError`, which is not reported.
- **Symbols, Module, MemoryLayout, AsmEffects, Signature, Lowering** are small supporting classes. `ModuleLoader` finds and parses the files.

Findings: 13 correctness (2 high, 4 medium, 7 low; all demonstrated) and 9 construction (1 high, 7 medium, 1 low).

## Findings

### 1. A struct field as the `step` of a `for` loop reads the wrong memory

- Severity: high. Kind: correctness (demonstrated).
- Where: `Lowering.cs:22` (`case ForStmt f`).
- What: `Lowering.Apply` replaces `From`, `To` and the body of a `for` with their lowered forms, but not `Step`. A variable step that is a struct field (or an element of an array of structs, or a field through a pointer) reaches the code generator as the unlowered `MemberExpr`. The code generator loads the struct's base address.
- Evidence: the program never ends. The listing has `lda cfg / sta main._t0` where `lda cfg+1` is expected: the step is `cfg.n`, which is 0.

```
struct Cfg { n: u8, m: u8 }
var cfg: Cfg
var out: u8
fn main() {
    cfg.m = 50
    for i in 0..=200 step cfg.m {
        out += 1
    }
}
```

- Why it matters: silent wrong code. With a field at offset 0 it works, so a test with a one-field struct hides it.
- Fix: add `if (f.Step is not null) f.Step = Lower(f.Step);`. Add a test with the field at a non-zero offset, and one with `step list[i].n`. See finding 14 for the cause.

### 2. A `using X` loop accepts a function call in an inner loop's `step`

- Severity: high. Kind: correctness (demonstrated).
- Where: `Checker.Bodies.cs:435` (`ContainsCall`, `ForStmt` arm).
- What: `ContainsCall` looks at an inner loop's `From`, `To` and body, not its `Step`. A call there is not found, so the outer loop keeps its counter in X while the call changes X.
- Evidence: compiles with no error. The run stops after 20,000,000 cycles with `out = 187`; expected 9. `g` leaves 5 in X, so the outer `inx / cpx #3 / bne` never ends.

```
var out: u8
fn g() -> u8 {
    var k: u8 = 0
    for j in 0..5 using X { k += j }
    return 100
}
fn main() {
    for i in 0..3 using X {
        for j in 0..=200 step g() {
            out += 1
        }
    }
}
```

- Fix: add `|| (f.Step is not null && ContainsCall(f.Step, reg))`. See finding 14.

### 3. `@ region` in a module is "not declared"

- Severity: medium. Kind: correctness (demonstrated).
- Where: `Checker.Placement.cs:37-42`, `Checker.Declarations.cs:21` (`EnsureMemoryLayout`).
- What: `ApplyPlacement` asks for the memory block only when the file being declared has one (`_module.Syntax.Memory`), and `EnsureMemoryLayout` returns at once for a module. Modules are declared before the main file, so `_layout` is still the platform's default and has no regions. The name is then checked as a constant.
- Evidence: `m.tern:1:28: error: 'buffers' is not declared. Check the spelling, or declare it`.

```
// m.tern
noinit var buf: [u8; 16] @ buffers
fn fill() { buf[0] = 7 }

// main.tern
import m
memory {
    $C000..=$CFFF  region buffers
}
var out: u8
fn main() { m.fill()
 out = m.buf[0] }
```

- Why it matters: spec 2.2 and 13 both describe placed declarations "in the main file and in every imported module" (`@ region`, a `chr` area). A NES font module cannot hold `data font @ tiles`. Spec 8.4 says "Regions exist only in the main file", which can be read the other way; if modules may not use regions, the error must say so.
- Fix: in `ApplyPlacement`, evaluate the main file's block when the name is one of its `region` or `chr` lines, whichever file is being declared (run it with `InModule(_mainModule, ...)`). Or give the error "'buffers' is a region of the main file; a module cannot place declarations in it. Move the declaration to the main file". Ask the spec session which.

### 4. A `const fn` that reads a single placed `data` value crashes the compiler

- Severity: medium. Kind: correctness (demonstrated).
- Where: `Checker.CompileTime.cs:436-447` (`DataArray`).
- What: `DataArray` assumes the data is an array. `data v: u16 @ $C000 = 5` is a single value (allowed since decision 2026-11-14), so `v.Type.Element` is null.
- Evidence: `Unhandled exception. System.NullReferenceException` at `Checker.CompileTime.cs:447`.

```
data v: u16 @ $C000 = 5
const fn f() -> int { return v }
const Y = f()
var out: u8 = Y
fn main() { out += 1 }
```

- Fix: return a `CtInt` or `CtBool` for a scalar, or report "'v' is a single value, which compile-time code cannot read. Use a const". Add a test.

### 5. Interrupt fns that call each other are dropped with no error

- Severity: medium. Kind: correctness (demonstrated).
- Where: `Checker.TypeRules.cs:265-287` (`MarkParts`), `:212` (`OrderRoots`).
- What: `MarkParts` marks every called `interrupt fn` as a part. `OrderRoots` takes only handlers that are not parts as roots. When `a` calls `b` and `b` calls `a`, both are parts, neither is a root, `OrderReachable` never visits them, and the recursion is not found.
- Evidence: compiles with no error or warning. The listing has neither `a` nor `b`.

```
var out: u8
interrupt fn a() { out += 1
 b() }
interrupt fn b() { out += 2
 a() }
fn main() { out = 1 }
```

- Why it matters: spec 6.4 makes every cycle an error, and spec 6.3 says every `interrupt fn` in the main file is compiled.
- Fix: after `OrderRoots`, run `OrderReachable` from each main-file `interrupt fn` that is not in the result, so the cycle is reported. More generally, recursion is checked only in reachable functions; a library function that calls itself is reported only when a program first uses it. Check every function for cycles.

### 6. A constant expression with a typed constant is checked against that constant's type where nothing gives a type

- Severity: medium. Kind: correctness (demonstrated).
- Where: `Checker.TypeRules.cs:14-18` (`DefaultType`), `Checker.Bodies.cs:243` (loop bounds).
- What: `DefaultType` returns the type of the expression's typed constants, whatever the value. `var v = BOX * 30` is then a `u8` and 390 does not fit.
- Evidence: both give `390 does not fit in u8 (0 to 255)`.

```
const BOX: u8 = 13
var out: u16
fn main() {
    var v = BOX * 30
    out = v
    for i in 0..BOX * 30 { out += 1 }
}
```

- Why it matters: spec 3.1 says a constant expression is computed exactly and "only the final value is checked", and where nothing gives a type it "takes the smallest that holds it". A typed constant narrows only next to a runtime value. `const AREA = BOX * 30` followed by `var v = AREA` works, so the two spellings differ.
- Fix: in `DefaultType`, use `ConstType` only when the value fits it; otherwise fall back to `LiteralType`. Or state in the spec that `BOX * 30` alone is a `u8` expression. Ask the spec session.

### 7. `as` checks a constant against 16 bits

- Severity: low. Kind: correctness (demonstrated).
- Where: `Checker.TypeRules.cs:29` (`LiteralType`), `Checker.Expressions.cs:167`.
- What: a constant operand of `as` is first given the smallest type that holds it. A value outside -32768 to 65535 has none.
- Evidence: `const BIG = 70000` and `out = BIG as u16` give `70000 does not fit in 16 bits`.
- Why it matters: spec 3.5: "`as` never checks a value; it keeps the low bits, on a constant as at runtime".
- Fix: for a constant operand, skip `Coerce` and set `ConstValue = Normalize(value, target)`. Keep the sign-extension rule, which exact arithmetic already gives.

### 8. `.lo` and `.hi` of an untyped constant depend on its value

- Severity: low. Kind: correctness (demonstrated).
- Where: `Checker.Expressions.cs:140`.
- What: an untyped constant takes `LiteralType`, so one below 256 is a `u8` and `.hi` is an error. A negative one is an `i8`.
- Evidence: `const BASE = $C0` and `out = BASE.hi` give `.hi needs a 16-bit value, not u8`. With `const BASE = $C000` it compiles.
- Why it matters: code that takes the bytes of an address constant stops compiling when the constant changes to a zero-page address or a module parameter gets a small value.
- Fix: for an `IntLiteral` operand in 0 to 65535 (or -32768 up), compute the byte from the value directly: `.hi` of `$C0` is 0.

### 9. A long byte array in a struct is accepted or rejected depending on declaration order

- Severity: low. Kind: correctness (demonstrated).
- Where: `Checker.Declarations.cs:533` (`_allowLongBytes`), `:931`, `Checker.Structs.cs:32`.
- What: `_allowLongBytes` is a field set while a global's type is resolved. If that is the first use of a struct of the same module, `LayOut` resolves the struct's field types with the flag still set. `InModule` clears it only for a struct of another module.
- Evidence: this compiles (1,001 bytes of variables). Without the `const` line it is the error "An array of u8 has at most 256 elements".

```
struct S { buf: [u8; 1000], n: u8 }
const N = len(s.buf)
var s: S
var out: u16 = N
fn main() { s.n = 1 }
```

- Fix: pass "long bytes allowed" and the list length as parameters of `ResolveType` for the outermost array only, and remove both fields. See finding 18.

### 10. A zero-page-only assembly mode moves `data` into zero page

- Severity: low. Kind: correctness (demonstrated).
- Where: `Checker.Asm.cs:234-238` (`RequireZeroPage`).
- What: any variable used with `(name),y` gets `AsmZeroPage`, also `data`. The report then lists the table in zero page.
- Evidence: `data msg = "HI"` and `asm { ldy #0 / lda (msg),y / sta out }` compile; the zero page map shows `$02  2  msg`.
- Why it matters: the operand is almost always a mistake for a pointer to the data. On a cartridge, `data` is ROM and cannot be in zero page.
- Fix: for `IsData`, report "'msg' is data, not a pointer. Declare 'var p: ptr u8 = addr(msg)' and use (p),y".

### 11. A misspelled call inside a `using X` loop gets the wrong error

- Severity: low. Kind: correctness (demonstrated).
- Where: `Checker.Bodies.cs:303`, `:430`.
- What: `ContainsCall` runs before the body is checked and treats a name that is not declared as a call that changes the register.
- Evidence: `for i in 0..3 using X { out += helpr(i) }` gives "The loop body calls a function, which can change X" instead of "'helpr' is not declared".
- Fix: check the body first, then run `ContainsCall` on the resolved calls (`CallExpr.Function`). That also removes the second `Lookup` of each name.

### 12. Error messages outside the convention

- Severity: low. Kind: correctness (demonstrated with `--json`: `"suggestion": null`).
- Where and what:
  - `Checker.TypeRules.cs:77`: "Expected bool, found a number" has no fix (`var b: bool = 1`). Add "Write true or false, or compare: x != 0".
  - `Checker.TypeRules.cs:97`: "Expected X, found Y" has no fix when the types do not convert (a bool where a number is expected).
  - `Checker.TypeRules.cs:97`: for a `u8` where a pointer is expected, the fix "Convert with 'as ptr u8'" does not compile; only `u16` converts.
  - `Checker.Asm.cs:185`: "'bne' needs a label, e.g. bne .loop" has no fix verb. Write "Use a label: bne .loop".
  - `Checker.Structs.cs:52` and `Checker.Bodies.cs:138`: features the spec plans ("A field cannot be an array of structs yet", "Local arrays cannot have initial values yet") do not use the form "'x' is not supported yet" that CLAUDE.md asks for.
- Fix: change the texts. A test that compiles every program in `ErrorTests` and fails on a null suggestion would keep the convention.

### 13. `for i in 0..256` counts with a `u16`

- Severity: low. Kind: correctness (demonstrated; the spec is not explicit).
- Where: `Checker.Bodies.cs:256`.
- What: the counter's type comes from the bounds as written, so an exclusive end of 256 makes it `u16`, though the counter only reaches 255.
- Evidence: `for i in 0..256 using X { buf[i] = 1 }` on a `[u8; 256]` gives "A u16 loop variable does not fit in X". Without `using` it compiles to a 16-bit loop.
- Why it matters: spec 5.1: "The loop variable is `u8` unless the range needs more". `0..256` over a full page is the natural spelling, and `0..len(buf)` gives the same.
- Fix: with constant bounds, type the counter from the values it takes (use `LargestLoopValue`). Ask the spec session to state the rule.

### 14. Nine hand-written tree walks, each over its own subset of nodes

- Severity: high. Kind: construction.
- Where: `ContainsCall` (`Checker.Bodies.cs:428`), `AlwaysReturns` and `ContainsBreak` (`:449-468`), `CollectNames` (`Checker.Declarations.cs:329`), `UsesAddr` (`:873`), `Lowering.Apply` and `Lower` (`Lowering.cs`), `NeedsInterpreter` (`Checker.CompileTime.cs:206`), `Describe` (`Checker.TypeRules.cs:127`), `LocalUses` and `Unreachable` (`Checker.Warnings.cs`), `ConstFnBodyCheck` (`Checker.CompileTime.cs:69`).
- What: each is a `switch` over node types that ends in `_ => false` or no default. A node or child that a walk leaves out is skipped silently. Findings 1 and 2 are both `ForStmt.Step`, left out of two walks and present in a third (`LocalUses`). `CollectNames` leaves out lists and struct literals.
- Why it matters: every new node must be added to each walk by hand, and the compiler gives no help. A call through a function pointer (spec 6.5) is a new call node: `ContainsCall`, `Lowering`, `LocalUses` and `NeedsInterpreter` all need it, and a miss gives wrong code, not an error.
- Fix: give `Node` one method that lists (and for lowering, replaces) its children, in `Syntax/Ast.cs`. Write each walk as "handle the nodes I care about, then visit the children". At least, end every walk with a default that throws for an unknown node type.

### 15. The CPU's instruction effects and addressing rules are fixed to the NMOS 6502 inside the checker

- Severity: medium. Kind: construction.
- Where: `AsmEffects.cs:41-92`, `Checker.Asm.cs:156-222` (`AsmMode`), `:308` (the `jmp ($xxFF)` rule).
- What: `AsmEffects.Of` is a table by mnemonic whose default is "reads nothing, writes nothing". `Dec or Inc => (None, NZ)` is right only because the NMOS CPU has no `inc a`. `AsmMode` maps `(address)` to `jmp` alone and always applies the `$FF` vector rule.
- Why it matters: on a 65C02, `plx`, `ply`, `inc a`, `dec a`, `stz`, `trb` and `tsb` would fall into the default or the wrong arm, so the optimizer would believe they change nothing: a `using X` loop would accept `plx`. `lda (zp)` needs a new form, and the `jmp` rule must go.
- Fix: move the effects into `Tern.Cpu` next to the instruction table, one entry per instruction, so a variant's table carries them. Make the default throw. Pass the CPU variant to `AsmMode`.

### 16. "The platform has boards" stands for "this is the NES"

- Severity: medium. Kind: construction.
- Where: `Checker.cs:145` (a memory block is required), `Checker.Asm.cs:318` (a store to `data` is allowed), `MemoryLayout.cs:65` and `:99`, `:74` (the name `"apple2"`), `:119-172`.
- What: `_platform.Boards.Count > 0` decides four unrelated rules: every program is a cartridge, `data` is ROM, the default program area is the first board's, a block needs a `cartridge` line. The kinds of memory lines are strings (`"program"`, `"bank"`, `"chr"`), compared in 20 places.
- Why it matters: spec 8.4 plans C64 and Atari cartridges, where the line is optional. Giving the C64 a board would at once require a `cartridge` line in every C64 program and allow stores to `data`. The Commander X16 and C128 add banked RAM, another meaning again.
- Fix: give `Platform` the properties the rules need (`CartridgeRequired`, and "data is ROM" from the layout's cartridge, not the platform). Make the line kind an enum set by the parser.

### 17. Control flow decided by matching error text

- Severity: medium. Kind: construction.
- Where: `Checker.Declarations.cs:258` (a regex on "'x' is not declared"), `Checker.Bodies.cs:200` and `Checker.TypeRules.cs:107` (`Description.Contains(" does not fit in ")`).
- What: three places catch a `CompileError`, look for words in its message, and write a better one.
- Why it matters: rewording the first message turns the second off with no test failing unless it checks the exact text. It also mixes message text into logic.
- Fix: give `CompileError` a kind (an enum, or subclasses `NotDeclaredError` with the name, `DoesNotFitError` with value and type), and catch that.

### 18. Context is passed through fields, and two methods save different lists of them

- Severity: medium. Kind: construction.
- Where: `_allowLongBytes`, `_listLength` (`Checker.Declarations.cs:511-513`), `_dead`, `_loopDepth`, `_loopRegisters`, `_loopMax` (`Checker.cs:42-47`), `_constValues`, `_module`, `_function`, `_parameterScope`, `_checkingLines`, `_declaring`; `Recover` (`Checker.cs:468-487`), `InModule` (`Checker.Declarations.cs:113-126`).
- What: about twelve fields hold "where the checker is now". `Recover` saves and restores nine of them; `InModule` four. A new field must be added to both lists by memory. `ResolveType` reads `_listLength` and clears it as a side effect (`:924`). Finding 9 is such a field leaking into a nested check.
- Why it matters: the next contributor cannot tell from a method's signature what it depends on, and a missed field in `Recover` gives errors that depend on what failed before.
- Fix: put the per-function state in one `BodyContext` record (function, loop depth, loop registers, loop maxima, dead flag) that `Recover` copies as a whole. Pass type-resolution options as parameters.

### 19. Very long methods that do several jobs

- Severity: medium. Kind: construction.
- Where: `Checker.Run` (`Checker.cs:77-282`, 205 lines), `CheckExpr` (`Checker.Expressions.cs:12-180`), `AddEffects` (`Checker.Asm.cs:241-324`), `MemoryLayout.Build` (`MemoryLayout.cs:96-227`), `CheckFor` (`Checker.Bodies.cs:234-329`).
- What: `Run` declares names in six ordered passes, checks imports' areas, finds `main` or the exports, checks bodies and builds the call graph; the order of its loops is the design, explained only by comments between them. `AddEffects` computes register effects, checks loop registers, records calls, takes addresses, rejects memory parameters and records variable use. Lines of 250 to 330 characters are common.
- Fix: split `Run` into named steps (`DeclareNames`, `DeclareFile`, `CheckPlacedImports`, `FindEntry`, `BuildCallGraph`), each a few lines in `Run`. Split `AddEffects` by job. Move `CheckExpr`'s `IndexExpr`, `MemberExpr` and `CastExpr` arms into methods, as `CheckUnary` and `CheckBinary` already are.

### 20. The same rule written two or three times

- Severity: medium. Kind: construction.
- Where and what:
  - `MatchInterface` (`Checker.cs:375-432`) and `Implement` (`Checker.Implements.cs:83-141`): the same loop over routines and over volatile variables, with the same messages. One has the `setup` rule, the other the area check.
  - `AlwaysReturns` and `ContainsBreak` (`Checker.Bodies.cs:449-468`) against `Unreachable.Ends` and `Breaks` (`Checker.Warnings.cs:368-391`): "this statement never finishes", with small differences (`ConstValue == 1` against a literal `true`).
  - "another unit of the same overlay area" is spelled out three times with the same five conditions: `Checker.Declarations.cs:203`, `:842`, `Checker.Expressions.cs:622`.
  - The built-in names `addr`, `len`, `sizeof`, `offset`, `include_bytes` are listed in `Checker.Declarations.cs:380`, `Checker.Bodies.cs:430`, `Checker.CompileTime.cs:24` and `:215`, and the `switch` in `CheckCall`.
- Why it matters: a fix in one copy leaves the other wrong. A new built-in must be added in five places.
- Fix: one `MatchRoutines(module, api, ...)` used by both interface checks; one "never finishes" function; a method `NotPresentWith(VariableSymbol, FunctionSymbol)`; one `static readonly` set of built-in names.

### 21. The call graph is built as a side effect of checking, in three files

- Severity: medium. Kind: construction.
- Where: `AddCall` (`Checker.Expressions.cs:646`), `AddEffects` (`Checker.Asm.cs:273-290`), `CheckImplements` (`Checker.Implements.cs:72`), `TakeFunctionAddress` (`Checker.Expressions.cs:451-454`), `UseGlobal` (`Checker.Declarations.cs:211`), `Run` (`Checker.cs:218`, `:247`); read by `OrderRoots`, `MarkParts`, `HandlerSideFunctions` (`Checker.TypeRules.cs`).
- What: edges are four sets on `FunctionSymbol` (`Callees`, `UsedInterrupts`, `UsedAddresses`, `UsedGlobals`) written from six places, each guarded by `_dead` separately. `MarkParts` and `OrderRoots` walk them with two copies of the same rules ("a named handler is compiled", "an address-taken function is a root"), and must agree; finding 5 is where they do not. `OrderRoots` is three nested local functions sharing six captured variables. The file `Checker.TypeRules.cs` holds the call graph, which its name does not say.
- Why it matters: a call through a pointer with a `targets` list (spec 6.5) is a new kind of edge: the call site must add every target, and assigning the variable must not. `implements` already does this through a routine's `Callees`, which is a good model, but there is no one place to add it or to test it.
- Fix: a `CallGraph` class in its own file with `AddCall`, `AddAddress`, `AddHandler`, `AddGlobal` (which take the dead flag) and the queries. Compute "compiled" once and use it for both parts and roots.

### 22. Small things

- Severity: low. Kind: construction.
- States as numbers: `StructSymbol.LayoutState` 0 to 3 (`Symbols.cs:150`), `_memoryState` (`Checker.Declarations.cs:11`), `_defaultState` (`Checker.Encodings.cs:25`). Use enums.
- Stale or misplaced comments: "Reports the first error" (`Checker.cs:33`; it reports all); a `<summary>` with no member under it at `Checker.cs:284` and `Checker.Expressions.cs:432`; `FunctionSymbol.Roots` says more than one root "is allowed only for a function with no memory" (`Symbols.cs:321`) and `OrderRoots` says such a frame "overlaps no other" (`Checker.TypeRules.cs:203`), while spec 8.1 now allows any function from two roots. Spec 6.3 still says "A part reached from two handlers is an error"; that is a question for the spec session.
- `EncodingSymbol` is in `Checker.Encodings.cs` and `Implementation` in `Checker.Implements.cs`; the other symbols are in `Symbols.cs`.
- `View` keys its cache on `type.ToString()` (`Checker.Structs.cs:87`), which leaves out `Linear`. Use the type with `SameAs`.
- `ModuleLoader.Import` has a block in braces with no purpose (`Module.cs:79`, `:119`).
- `OwnHandler` and `OwnPart` search `Scope.Values` for every use of every private global (`Checker.TypeRules.cs:319`, `:324`); compare `f.Module` instead.

## Patterns

- **A walk that leaves out a child** (findings 1, 2, 14). Both high bugs are the same omission. Search every walk for `ForStmt` and check that it handles `Step`; then make omissions impossible.
- **State in fields that outlives its use** (findings 9, 18). Flags set "while X is resolved" reach whatever X calls.
- **Checks that run before names are resolved** (findings 11, 14). `ContainsCall`, `UsesAddr` and `NeedsInterpreter` look names up again on the unchecked tree. Running them after the check, on the symbols already found, is simpler and cannot disagree with it.
- **A type chosen from a constant's value** (findings 6, 7, 8, 13). `LiteralType` and `DefaultType` give the same expression different types as its value changes, so code breaks when a constant or module parameter changes. Wherever the spec says "exact" or "never checks", work on the value and leave the type out.
- **A rule keyed on an unrelated property** (finding 16), and **a rule copied** (finding 20).

## Done well

- **Exact constant arithmetic.** `ConstArithmetic` (`Checker.TypeRules.cs:143`) is checked, handles `MinValue / -1`, `% -1` and the shift overflow, and matches spec 3.1 on every case tried: `-7 / 2`, `-7 % 2`, `7 % -2`, `-7 >> 1`, `~0`, `w & ~M` with a typed `M`.
- **Error recovery.** `Recover`, `Declaring`, `ErrorSymbol` and `FollowOnError` give one error per statement and none for uses of a broken name, with little code. Keep the idea; finding 18 only asks for the saved state to be one object.
- **Struct lowering in the checker.** Views, tables and pointer accesses are made once (`Checker.Structs.cs`), so the backend sees only variables, arrays and pointers. The cache of views lets the backend see that two accesses are the same place.
- **Declaration on demand.** `Demand`, `StartDeclaring` and the `_declaring` chain let constants, globals and encodings be used in any order and across import rings, and name the whole cycle in the error.
- **Error messages.** Nearly all say what is wrong and how to fix it, with the user's own names, sizes and a line to write (`Checker.Structs.cs:266`, `Checker.Bodies.cs:206`). Few compilers do this.
- **The private-variable check and volatility from the call graph** (`CheckPrivateGlobalsPerRoot`, `MarkGlobalsUsedFromTwoRoots`) are short, commented with their reasons, and follow spec 8.1 including the module's-own-handler exception.
- **Comments give reasons and cite the spec section or decision**, so a rule can be traced. `Symbols.cs` documents every property.
- **`MemoryLayout`, `Signature`, `AddressRange`** are small, pure and easy to test.
