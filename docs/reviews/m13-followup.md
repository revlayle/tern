# Review: M13 follow-up, branch `m13-followup`

## First review (`8d558de`), 2026-11-07

**Not ready to merge: three must-fix items.** Most of the follow-up is sound. Long byte buffers, `const` before the memory block, fixed zero-page pointers in overlays and the layout of fixed data in an overlay file all behave as spec 3.2, 8.4 and 8.5 say. The problems are in the optimizer change and the sibling-overlay checks:

- The optimizer change fixes a stale read, but no test catches it.
- The change misses a variable that is both declared `volatile` and in a `volatile` memory range.
- The sibling checks can be bypassed through an inline function.

1334 tests pass (36 CPU, 1298 compiler). `BenchmarkTests` is unchanged. The branch is based on the current `main` (`6d1eba9`).

### 1. Must fix: the optimizer change has no test, and the status note about it is wrong

`Optimizer.ForgetWrite` (`src/Tern.Compiler/Backend/Optimizer.cs:197`) makes the optimizer forget every variable after a write to a declared-volatile fixed variable. The roadmap asks for a test of this. `docs/status.md:20` says the test only checks behavior, "since the write's own load clobbers the register anyway". That is true for `hdr = 12` (`lda #$0C`), but not for a write that leaves A alone. I reverted the `Optimizer.cs` change and ran `OverlayTests`: all 44 tests still pass.

Here is a case where the change matters:

```
// ov.tern
var x: u8 = 7
fn get() -> u8 using A { return x }

// main.tern
import ov @ levels
memory {
    $0801..=$5FFF  program
    $6000..=$6FFF  overlay levels
}
fn load(n: u8 using A) @ $C000 keeps X, Y
volatile var hdr: u8 @ $6004      // ov.x is at $6004
var d: u8
var e: u8
fn main() { load(1); d = ov.get(); d = ov.x; hdr += 1; e = ov.x }
```

With the change, `e = ov.x` is `lda ov.x / sta e`. Without it, the code is `inc hdr / sta e`, which stores the stale value (7 instead of 8). The branch generates correct code here, but nothing would catch a regression.

Fix:

- Add this program, or one like it, to `OverlayTests`. Check `e` in the emulator, and check in the listing that `ov.x` is loaded again after `inc hdr`.
- Correct the note in `docs/status.md`.

### 2. Must fix: a `volatile` variable in a `volatile` memory range over an overlay is read stale

The condition `!written.Storage.InVolatileArea` (`Optimizer.cs:197`) skips the forget for a variable in a volatile area. The program in finding 1 has this extra line in its memory block:

```
memory {
    $0801..=$5FFF  program
    $6000..=$6FFF  overlay levels
    $6000..=$600F  volatile
}
```

With it, `volatile var hdr: u8 @ $6004` compiles to `inc hdr / sta e`, the stale read again. Without the keyword, the program gets the overlap error, which suggests adding `volatile`. Following that advice then gives wrong code.

Spec 8.5 says a write to such a variable "counts as a write through a pointer", whatever area it is in. The exclusion seems meant to keep writes to I/O registers (`$D020`) cheap. The test for that should be whether the variable can alias an overlay's memory, which means it lies in an overlay area. It should not be whether it is in a volatile area. One possible fix: forget everything when `written.FixedAddress` is inside one of the layout's overlay areas. That case needs no `DeclaredVolatile` test either, since only volatile variables may overlap an overlay's file. Add a test with a `volatile` range over the overlay area.

### 3. Must fix: an inline function bypasses the new sibling checks

Spec 8.5: using another overlay's `data`, and `addr()` of its functions or `data`, are errors. The new checks are in `CheckGlobalUse` and `TakeFunctionAddress` (`Checker.cs:1541`). Both test `_function`, which is the inline function itself while its body is checked. `CheckInlineCallsBetweenOverlays` checks inline bodies, but:

- It still skips fixed-address globals (`g.FixedAddress is null`, `Checker.cs:1680`). That was right for variables, but the `|| v.IsData` added to `CheckGlobalUse` is missing here.
- It does not look at `addr()` of functions at all.

This compiles without an error:

```
// level2.tern
data T: [u8; 2] @ $6100 = [5, 6]
fn five() -> u8 using A { return 5 }
inline fn peek() -> u8 using A { return T[1] }
inline fn where() -> u16 using AX { return addr(five) }

// level1.tern
import level2
fn go() -> u8 using A { return level2.peek() }       // lda level2.T+1 in level1's code
fn go2() -> u16 using AX { return level2.where() }   // address of level2.five, from level1

// main.tern: level1 and level2 both @ levels ($6000-$6FFF); main calls level1.go, level1.go2, level2.five
```

While `level1` runs, `$6101` holds whatever `level1`'s file has there, and `level2.five` points into `level1`'s code. Fix:

- Use the same condition as `CheckGlobalUse` at line 1680 (`(g.FixedAddress is null || g.IsData)`).
- Record the functions whose address an inline body takes, and check them like calls.
- Add an error test for each case.

### 4. Suggestion: the size in the "does not fit" error depends on where the fixed data is

The same module (a 4-byte `hdr`, a 12-byte `buf`, two small functions) in a 32-byte area is reported as needing 40, 45, 53 or 36 bytes. The number depends on whether `hdr` is at `$6000`, `$6008`, `$6010` or `$601C` (`Assembler.cs:202`, `Math.Max(at - area.First, size)`). The number includes gaps that were skipped, so it does not tell the user how much the module holds. Something like "needs N bytes of code, data and variables, plus M bytes of fixed data" would be steadier. The next-fit placement itself (later sections never go back into an earlier gap) matches what program areas do, so it is not a new problem.

### 5. Suggestion: repeated tests in `CheckFixedData`

`TernCompiler.cs:287` and `:295` test `g.FixedAddress is not null` again, inside a loop that already selects only globals with a fixed address. Both can go. For main-file data, the resident-module error message ("but its module is resident") reads a little oddly. "but it is in the resident program" would cover both cases.

### 6. Suggestion: long `data` from a long list literal

`data d: [u8; 300] = [ ...300 values... ]` is accepted. Spec 3.2 names only string literals and `include_bytes` as sources of long `data`. This is harmless, but either the spec or the checker should change so they agree. This is a question for the spec session, not a blocker.

### Probes that behaved correctly

- Volatile struct fields and array elements over an overlay (`volatile var hdr: H @ $6003`, `hdr.b += 1`; `volatile var arr: [u8; 2] @ $6003`, `arr[1] += 1`): `ov.x` is reloaded after each write.
- Long arrays: `[[u8; 300]; 2]`, `ptr [u8; 300]`, `a = b` on two long buffers, `for v in buf`, `x = len(buf)` into a `u8`, and `data d: [u16; 300]` are all errors with clear messages. The spec's example `var p: ptr u8 = addr(map) + 256` as a local compiles, and so does `p[44]`.
- `const` before the block: a typed constant, a comment and blank lines between constants, and a constant used in a range all work. An `import` after a constant gets the existing import error. A `var` between constants and the block gets the new message. A constant that refers to one declared later is "'B' is not declared", as it is everywhere else.
- `addr()` of sibling data or functions in an overlay's global initializer is already rejected by the general "addr() cannot be used in an initial value" error.
- Fixed data layout: with `hdr` at `$6010` in a 64-byte area, `small` goes at `$6000`, `go` after the data at `$6014`, and the file is 53 bytes from `$6000`. The report shows "lv.prg: 53 of 64 bytes ($6000-$6034)".

### Checklist (roadmap, M13 follow-up)

- [ ] `volatile` fixed variable over an overlay's file: allowed. The error without `volatile` gives the suggested message, overlap with the resident program is still an error, and a write makes the optimizer forget every variable. **Partly met:** the forget is missed in a volatile memory range (finding 2).
- [x] Fixed `data` in an overlay module: in the file at its address, the rest laid around it, gaps zero-filled, file starts at the area's start. Each of the errors has a test, and the roadmap's two-level test is there.
- [ ] Uses of a sibling overlay: `addr()` of its functions or `data`, and use of its `data`, are errors, and resident code may still take the addresses. **Partly met:** the checks can be bypassed through an inline function (finding 3).
- [x] Fixed zero-page addresses in an overlay: work, are not in the file, are shared with the main program, and the `@ zp` error suggests them.
- [x] Long byte buffers: zeroed or `noinit`, in a region or at a fixed address, `addr()` and `len()` work, and the errors for an initial value, multi-byte elements and indexing (also of long `data`) have tests. The region-full case has a test.
- [x] `const` before the memory block, with the new message and the three `MemoryTests`.
- [ ] `OverlayTests`: volatile read and write in both directions, the error without `volatile`, `volatile` over the resident program. **Missing:** the optimizer test (finding 1).
