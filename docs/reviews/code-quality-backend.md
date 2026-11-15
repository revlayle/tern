# Code quality review: Backend

Scope: `src/Tern.Compiler/Backend` (14 files, about 6,100 lines), read against spec sections 4 (volatile), 5 (evaluation order), 8 and 11. Reviewed at commit d223f12. Test programs are shown inline; each was run with `tern run --target c64 file.tern --dump out`.

## Overview

- **CodeGen** (6 partial files, 3,550 lines) walks the checked AST once per function and appends `AsmItem`s to a list. 8-bit values go through A (`GenA`), 16-bit values are computed into a memory location (`Gen16`), conditions compile to jumps (`GenCond`). Operands are symbolic (`VarOperand`, `Label`). It also decides which loop counters, parameters and variables live in X or Y.
- **Optimizer** (3 partial files, 1,250 lines) runs per function on the item list: jump threading, unreachable code, then nine passes repeated until nothing changes. The passes share one forward analysis (register contents) and several backward ones (live bytes, live registers).
- **Allocator** assigns zero page, regions and the RAM block. Frames of functions that cannot be active together overlap (`FrameOffsets`).
- **Assembler** places sections in areas, picks zero-page modes, grows out-of-range branches, encodes and writes the listing.
- **RegisterUse** and **BinaryInterface** write the `keeps` lists and the interface module of a binary.

Findings: 6 correctness (3 high, 1 medium, 2 low; 5 demonstrated, 1 suspected) and 11 construction (1 high, 6 medium, 4 low).

## Findings

### 1. A load of X or Y before an `indexed match` is removed as dead

- Severity: high. Kind: correctness (demonstrated).
- Where: `Optimizer.DeadStores.cs:589-592` (`RemoveDeadLoads`, `deadAtReturn`).
- What: the pass scans forward from a load and treats any compiler `rts` as the function's return, where only the return register is read. The dispatch of an `indexed match` also ends with `rts` (`pha / pha / rts`), and the arms it jumps to may read the register. The scan skips labels, so a counter's `ldy #5` before the loop label is removed.
- Evidence: prints `out = 28`, expected 18 (5+6+7). The listing has no `ldy #5`; the loop runs with whatever Y held.

```
var out: u8
var v: u8 = 1
fn main() {
    for i in 5..8 using Y {
        indexed match v {
            0 => { out += 1 }
            1 => { out += i }
            2 => { out += 2 }
        }
    }
}
```

- Why it matters: silent wrong code in a documented construct (`examples/c64/raster.tern` uses it). With a start of 0 the emulator's zero registers hide it.
- Fix: do not treat `rts` as a return when the code has a `LabelTable` (or any unknown entry). Better: use `RegistersLive`, which already handles this `rts` correctly (`:344`). `NzLiveAfter` (`Optimizer.Registers.cs:274-278`) makes the same assumption for N and Z; it is harmless today because arms never read flags first, but fix it the same way. See finding 9.

### 2. `p[i] = recs[j].f` with an `indexed` record array stores to the wrong address

- Severity: high. Kind: correctness (demonstrated).
- Where: `CodeGen.Calls.cs:133-149` (`UsesY`), `CodeGen.Expressions.cs:111-125` (`LoadIndex`).
- What: `StorePointerByteIndexFirst` loads Y with the pointer index, then computes the value, after `UsesY(value)` said the value leaves Y alone. `UsesY` assumes an array index uses X unless a loop holds X. `LoadIndex` uses Y for the offset table of an `indexed` array (`ldx j / ldy recs.offsets,x`). The value's code overwrites Y, and `RemoveDeadLoads` then removes the `ldy i`.
- Evidence: prints `out = 0`, expected 77. Listing: `ldx j / ldy recs.offsets,x / lda recs+1,y / sta (main.p),y`. The byte goes to `p[6]`.

```
struct Rec { a: u8, b: u8, c: u8 }
indexed var recs: [Rec; 4]
var buf: [u8; 16]
var out: u8
var i: u8 = 3
var j: u8 = 2
fn main() {
    var p: ptr u8 = addr(buf)
    recs[2].b = 77
    p[i] = recs[j].b
    out = buf[3]
}
```

- Fix: reserve Y (`_reserved.Add(Register.Y)`) while the value is generated. `LoadIndex` then skips the table path and picks X, and `UsesY` only has to reject pointer reads and calls. At least make `UsesY` return true for an index into an offset table. See finding 10 for the general problem.

### 3. A 16-bit volatile destination is used as scratch memory

- Severity: high. Kind: correctness (demonstrated, listing).
- Where: `CodeGen.Expressions.cs:664-676` (unary), `:682-690` (shift), `:727-770` (general binary), `:555` (`ShiftByBytes`); `CodeGen.Statements.cs:512-517` (`x <<= k` in place).
- What: `Gen16` computes into the destination: it stores the left operand there, then reads it back and writes again. Spec 4 says every read and write of volatile memory in the source happens exactly once. For a write-only register the read returns something else, so the value written is wrong.
- Evidence (C64, `$D400` is the SID frequency, write-only):

```
var freq: u16 @ $D400
volatile var port: u8 @ $C000
var w: u16 = $1234
var k: u16 = 3
fn main() {
    freq = w << 1     // lda w / sta freq / lda w+1 / sta freq+1 / asl freq / rol freq+1
    freq = w + k      // w stored to freq, then: lda freq / adc k / sta freq / lda freq+1 / adc k+1 / sta freq+1
    port <<= 2        // asl port / asl port: the value in between is written
}
```

- Why it matters: `sid.freq = base + offset` is ordinary C64 code. It writes the register twice per byte and adds to whatever the read returns.
- Fix: in `Assign`, when the target's storage is volatile and the value is not a plain copy, compute into a temporary and copy the two bytes. Exclude volatile bytes from the in-place shift, as `x += 2` already does (`Statements.cs:478`). The one-byte path (`GenA` then `StoreA`) is correct.

### 4. Calls run out of source order in `for` bounds and in `return` into the carry

- Severity: medium. Kind: correctness (demonstrated).
- Where: `CodeGen.Loops.cs:85-136` (end bound and step are computed before `SetCounter(c, f.From)`); `CodeGen.Statements.cs:385-424` (`CompareIntoCarry` computes the right side first and has no `InSourceOrder`).
- What: spec 5 says calls and volatile reads in one statement happen left to right.
- Evidence: with `fn f() -> u8 { n += 1; return n }` and `fn g() -> u8 { n += 10; return n }`:
  - `for i in f()..g() { out += 1 }` gives `out = 0`, expected 10 (`g` runs first, so the range is 11..10).
  - `fn ge() -> bool using carry { return f() >= g() }` returns true, expected false. Listing: `jsr g / sta _t0 / jsr f / cmp _t0`.
- Fix: call `InSourceOrder(b.Left, b.Right, ...)` at the top of `CompareIntoCarry`, as `GenCompare` does. In `GenForLoop`, when `From` and (`To` or `Step`) both have an effect, compute `From` first (into the counter if it is in memory, else a temporary).

### 5. A 16-bit element store reloads its index between the two bytes

- Severity: low. Kind: correctness (demonstrated).
- Where: `CodeGen.Statements.cs:609-611`, `CodeGen.Expressions.cs:436-441` (`InPlace16`), `:447-458`.
- What: the in-place path (`a[i] = b[j] + c[k]`) loads the target's index once per byte. `InPlace16` checks that the operands' indexes do not read the written array, but not the target's own index. If the index reads the array, the low-byte store changes it.
- Evidence: prints `out = $0002`, expected `$0102`.

```
var a: [u16; 4]
var b: [u16; 4] = [2, 0, 0, 0]
var c: [u16; 4] = [$0100, 0, 0, 0]
var k: u8
var out: u16
fn main() {
    a[a[0].lo] = b[k] + c[k]
    out = a[0]
}
```

- Fix: take the in-place path only when `!ReadsVar(ix.Index, arr)`.

### 6. A long branch drops the label's offset

- Severity: low. Kind: correctness (suspected, not reachable today).
- Where: `Assembler.cs:400`.
- What: the range test uses `Resolve(target.Label) + target.Offset` (`:329`), but the `jmp` of a grown branch uses `Resolve(label)` only. Compiler branches have offset 0 and asm branches are never grown, so nothing triggers it now.
- Fix: add `target.Offset`, or assert it is 0.

### 7. No CPU variant reaches the backend

- Severity: high (for the 65C02 plan). Kind: construction.
- Where: `CodeGen` is built from `CheckedProgram` only (`CodeGen.cs:39`); `Optimizer.Run` and `Assembler.Assemble` take no target; `Allocator.Allocate` takes a `Platform` it never uses (`Allocator.cs:65`). `Platform.Cpu` exists but nothing in Backend reads it.
- What: the instruction set is assumed in fixed lists:
  - branches: `IsBranch` (`Optimizer.DeadStores.cs:735`), `Invert` (`Assembler.cs:572`), `FallsThrough` (`:742`), `Target` (`:738`). A `bra` must be added to each; `FallsThrough` would silently treat it as falling through.
  - loads, stores, transfers: `Loaded`, `Stored`, `Transfer` (`Optimizer.Registers.cs:82-87`), `FlagsMatchA` (`CodeGen.cs:144-149`).
  - `InstructionSet.TryGet(mnemonic, mode)` with no variant (`Assembler.cs:522`, `Optimizer.DeadStores.cs:467, 494`).
  - NMOS behaviour: `AvoidPageEnd` for `jmp ($xxFF)` (`Assembler.cs:483`), `txa / pha` saves and an unconditional `cld` in `GenInterrupt` (`CodeGen.cs:446-459`; the `cld` is a wasted byte on the NES's 2A03).
- Fix: pass one target object (CPU variant plus what the generator may use) to `CodeGen`, `Optimizer.Run` and `Assembler.Assemble`. Derive the classifications from the instruction table (a branch is a `Relative` instruction; a store is a table flag), so a new instruction is one table row. Remove the unused `platform` parameter or use it.

### 8. "What does this instruction do to memory" is answered in eight places

- Severity: medium. Kind: construction.
- Where: `Optimizer.Registers.cs:163-181` (`Step`), `:213-215`; `Optimizer.DeadStores.cs:116-136`, `:226-227`, `:444-445`, `:456-457`, `:486-488`, `:553-557`; `CodeGen.cs:228`.
- What: each site combines `Stored(m) is not null`, `AsmEffects.WritesMemory`, `AsmEffects.ReadsMemory`, the mode (`Accumulator`, the indirect modes) and the operand type (`VarOperand`, `AddrOperand`) by hand. `WritesMemory` already includes `sta/stx/sty`, yet most sites add `Stored(...)`; it returns true for `asl a`, so some sites add `Mode != Accumulator` and others rely on the operand being null.
- Why it matters: a new writing instruction (`stz`, `trb`, `tsb`) or a new mode (`(zp)`) needs the same edit in every site, and a missed one is a silent wrong optimization.
- Fix: one function, e.g. `MemoryAccess Of(Instr)`, returning reads/writes and the target: a byte of a variable, a whole variable (indexed), anywhere (pointer or number), or none. All passes switch on that.

### 9. `rts` has two meanings and control flow is walked by hand in seven places

- Severity: medium. Kind: construction (caused finding 1).
- Where: successors are worked out separately in `Analyse` (`Optimizer.Registers.cs:235-248`), `NzLiveAfter` (`:265-289`), `RemoveDeadStores.After` (`Optimizer.DeadStores.cs:143-160`), `RegistersLive` (`:333-352`), `HoldsBefore` (`:374-381`), `Reaches` (`:393-397`), `RemoveUnreachable` (`:779-786`), plus the straight-line scans in `RemoveDeadLoads` and `RemoveStoresOfStartupZeros`.
- What: each walker decides again what `rts`, `jmp (v)`, a jump out of the function, `brk`, a `LabelTable` and the end of the code mean. Three treat the jump-table `rts` correctly, two do not.
- Fix: one `Successors(code, k)` (targets, fall-through, "unknown entries" for a dispatching `rts`, "leaves the function") used by all analyses. Mark the dispatch `rts` when it is emitted (`CodeGen.Statements.cs:252`), e.g. a flag on `Instr` naming its table, instead of inferring it from `UnknownEntries`.

### 10. The generator predicts its own output with separate predicates

- Severity: medium. Kind: construction (caused finding 2).
- Where: `UsesY` (`CodeGen.Calls.cs:133`), `IndexIsCheap` (`CodeGen.Expressions.cs:82`), `PointerIndexIsCheap` (`CodeGen.Calls.cs:42`), `KeepsIndexRegistersFree` (`CodeGen.cs:261`), `IndexesHeld` and `Served` (`CodeGen.Loops.cs:249, 363`), `CarryClearAfter` (`CodeGen.Expressions.cs:293`), and the checks on emitted code: `FlagsMatchA`, `AHolds`, `zFromDecrement` (`CodeGen.Statements.cs:117-123`).
- What: each predicate restates, on the AST, what `GenA`, `LoadIndex` or `LoadPointerIndex` will emit. Nothing ties them together. A new path in `LoadIndex` (the offset table) was not mirrored in `UsesY`.
- Fix: prefer constraints over predictions (reserve the register, then let the generator fail or choose). Where a prediction stays, assert it in debug builds: after generating the value in `StorePointerByteIndexFirst`, check with `AsmEffects` that no emitted instruction writes Y. The same assertion fits `IndexIsCheap` (A unchanged) and `CarryClearAfter`.

### 11. The backend keeps working state in checker symbols and the AST

- Severity: medium. Kind: construction.
- Where: `VariableSymbol.Register` is set by `KeptRegister` (`CodeGen.cs:250`), `ChooseCounterRegister` (`CodeGen.Loops.cs:240`), `ChooseOuterCounterRegister` (`:290`), `HoldInRegister` (`:357`) and cleared only by `ReleaseHeld`. `FrameVariables` is edited in the same places and cleared for inline functions (`CodeGen.cs:73`). `InSourceOrder` swaps a temporary into the AST and back (`CodeGen.cs:506-508`). `Label.Address` and `VariableSymbol.Address` are written by the allocator and assembler, and the driver runs both again after `NoPageStart`.
- Why it matters: `Register` changes `InMemory`, which changes what `Simple1` returns everywhere, so the meaning of an expression depends on when it is generated. A counter's register is chosen while generating the first copy of an inline function and silently reused for later copies. It works today because calls are not allowed where a register is held, but that rule lives in other methods.
- Fix: keep register assignments in a CodeGen-owned map that is reset per function, and state in a comment on `VariableSymbol` which fields each stage owns.

### 12. Several methods are too long to review as a unit

- Severity: medium. Kind: construction.
- Where: `Assembler.Assemble` (`Assembler.cs:108-477`, 370 lines: section split, layout, overlays, variable placement, branch growth, overlap check, encoding, listing, ten error messages); `Allocator.Allocate` (`Allocator.cs:65-306`); `Gen16` (`CodeGen.Expressions.cs:584-774`); `GenForLoop` (`CodeGen.Loops.cs:37-213`); `GenAssign` (`CodeGen.Statements.cs:428-555`); `ReadCopiesFromSource` and `SinkStoresFromLoops` (about 100 lines each).
- Fix: split `Assemble` into layout, variable placement and encode-and-list; `Allocate` into zero page, regions and RAM block. In `GenAssign` and `Gen16`, make each special case a method that returns false when it does not apply (as `ShiftedOperand16` already does), so the main method is a list of attempts.

### 13. The assembler reads meaning out of strings

- Severity: medium. Kind: construction.
- Where: section names `"jump table"`, `"export table"`, `"the variables"` (`Assembler.cs:188, 198-200, 499`); a bank is an overlay key that ends in `]`, parsed in `CodePlace.BankOf` (`:43-44`) and again at `:254-260`; module names are cut from section names at the first `.` (`:256`).
- What: renaming a section in the driver changes an error message or a layout rule without a compile error. The message "BASIC line and startup code" (`:188`) is used for every platform, including those with no BASIC line.
- Fix: put a kind (enum) and an optional bank number on `SectionStart`; let the platform name its first section.

### 14. CodeGen's per-function state is spread over fields in four files

- Severity: low. Kind: construction.
- Where: fields are declared between methods: `_aParameter` (`CodeGen.cs:216`), `_keptRegisters` (`:231`), `_interruptExit`, `_inlineExit`, `_inlineTemps`, `_frameOwner`, `_instances` (`:324-332`), `_previous`, `_nonEmpty` (`CodeGen.Statements.cs:27-30`). `GenInline` saves and restores six of them by hand (`CodeGen.cs:392-420`).
- Fix: group what belongs to "the function being generated" in one context object; `GenInline` then pushes and pops one thing.

### 15. An exception steers the layout retry

- Severity: low. Kind: construction.
- Where: `Assembler.NoPageStart` (`Assembler.cs:83, 292`), caught in `TernCompiler.cs:334` together with any `CompileError`.
- What: "the page-aligned placement did not fit" is a normal outcome. Catching `CompileError` too means a real error in the first attempt is retried and may be reported differently.
- Fix: return the outcome (a result value) from `Assemble`.

### 16. Small leftovers

- Severity: low. Kind: construction.
- `Allocator.Allocate`'s `platform` parameter is unused (`Allocator.cs:65`).
- An orphan `<summary>` for `ChooseMode` sits on `Generated` (`Assembler.cs:503`).
- The label `positive` is the branch taken when the value is negative; the sign-extension sequence is written twice (`CodeGen.Expressions.cs:571-581, 598-608`).
- `Narrow` is defined twice (`CodeGen.Expressions.cs:466, 487`); the compound-operator to operator switch twice (`CodeGen.Statements.cs:548, 565`); the `(up, inclusive)` switch four times (`CodeGen.Loops.cs:180, 617, 702, 806`).
- `Flag(Reg)` exists (`Optimizer.DeadStores.cs:317`) but the same mapping is written inline at `:561`, `:582` and `Optimizer.Registers.cs:184`. `Positions` exists but `FoldShiftTests` rebuilds it (`Optimizer.Registers.cs:386-392`).
- `m is not (Cmp or Bit)` is always true where it stands (`Optimizer.Registers.cs:196`). `Byte(o)` in `RemoveDeadStores` adds `AliasOffset` to operands that are already storage (`Optimizer.DeadStores.cs:107`).
- `throw new InvalidOperationException(e.GetType().Name)` for an unexpected node carries no source position (`CodeGen.Expressions.cs:249, 773`, `CodeGen.Statements.cs:638, 668`).

### 17. Unnamed limits and repeated work

- Severity: low. Kind: construction.
- Magic numbers: 200 steps in `NzLiveAfter` (`Optimizer.Registers.cs:270`), 8 hops in `ThreadJumps` (`Optimizer.DeadStores.cs:730`), weight `2 * 8^min(depth, 5)` for temporaries (`CodeGen.cs:137`), 254 in `CounterMax` (`CodeGen.Loops.cs:33`).
- Repeated work: every pass in `Improve` rebuilds `Positions`, `UnknownEntries` and often `Analyse`, and the nine passes repeat until none changes (`Optimizer.cs:30-32`). `AHolds` rescans the function's code for each compare (`CodeGen.cs:223-228`). `FunctionOf` is a linear search per `jsr` (`CodeGen.cs:56`). `ZpNeeded` recomputes all frame offsets per candidate (`Allocator.cs:99-105, 125-129`).
- Fix: name the constants. Compute the shared tables once per round of `Improve`. None of this is urgent at today's program sizes.

## Patterns

- **Parallel knowledge.** The same fact is encoded in several places that must agree: instruction classes (finding 8), control flow (9), what the generator emits (10). Both demonstrated optimizer and generator bugs (1, 2) come from one copy falling behind. Examples: `Optimizer.DeadStores.cs:589` against `:344`; `CodeGen.Calls.cs:141` against `CodeGen.Expressions.cs:111`.
- **The destination as scratch.** 16-bit code computes into the target, then fixes it up in place (`Gen16` shift, unary, binary). It is correct for ordinary memory and relies on the optimizer to clean up; it is wrong for volatile memory (finding 3) and when the target's index depends on the target (finding 5).
- **Source order applied case by case.** `InSourceOrder` is called at four sites (`GenBinaryA`, `Gen16`, `GenCompare`, `Assign`) and `GenCall` has its own version. Constructs with two operands that do not pass through those sites miss it (finding 4). A single list of "statement forms with more than one evaluated part" would show the gaps.
- **Error text built inside logic.** Long messages are assembled inline in `Assembler.Assemble` (ten), `Allocator.Allocate` (five) and CodeGen (`CodeGen.cs:168`, `CodeGen.Statements.cs:241, 520`, `CodeGen.Calls.cs:60`). The CodeGen ones are user errors found after checking, so the checker cannot promise that a checked program compiles. Move them to the checker or to named factory methods.
- **Pass-specific copies of small helpers.** Register-to-flag mapping, label positions, the compound-operator switch (finding 16).

## Done well

- The item model (`Asm.cs`): symbolic operands, checks kept out of operand identity, one comment per record. It is easy to read a pass against it.
- Comments state the safety argument of each optimizer pass and each code shape, with spec and review references. The reasoning could be checked line by line, which is how the gaps above were found.
- Volatile memory is handled in one place for the optimizer (`Trackable`, `ElementOf`); no pass was found that caches or removes a volatile access. Asm instructions are never changed, self-modifying code and labels with unknown entries are treated conservatively.
- `RemoveDeadStores`: the `Live` lattice (`Union`, `Dead`) is correct, and the removal loop's index reasoning holds.
- `FrameOffsets` is short and matches spec 8.1. Checked: a handler's frame, main's frame and a helper declared after the handler do not overlap.
- The assembler decides zero-page modes before layout and only grows branches, so layout always ends.
- `InSourceOrder` is a good single mechanism; findings 4 only needs it applied in two more places.
- `RegisterUse` errs towards "changed".
- Checked and found correct: an asm store to a struct field makes the optimizer forget the field; `addr(s.field)` keeps the field's stores; a `for` counter cannot be assigned, so `_counterMax` holds; a call in a `using X` loop is rejected, so held registers survive; signed and unsigned loop exits at the type's limits (`..=255`, `..=127`, down to 0); 8- and 16-bit signed compares.
