# Code quality review: Syntax, CompileTime and the driver

Scope: `src/Tern.Compiler/Syntax` (7 files, 2,600 lines), `src/Tern.Compiler/CompileTime` (2 files, 1,000 lines) and the files directly in `src/Tern.Compiler` (`TernCompiler*.cs`, `Platform.cs`, `Cartridge.cs`, `LabelFormat.cs`, `CharEncoding.cs`; 1,900 lines), read against spec sections 2, 3.6, 4.1, 5, 8.4, 12 and 13. Reviewed at commit d223f12. Test programs are shown inline; each was compiled with `tern --target <platform> file.tern` or run with `tern run --target c64 file.tern --dump out`. The test suite was not run, since nothing was changed.

## Overview

- **Lexer** (`Lexer.cs`) is one static method. It decides where statements end: it gives a `Newline` token only after a token that can end a statement and outside `( )` and `[ ]`. After an error it skips the line and leaves an `Error` token. `SourcePos`, `Token` and `CompileError` live in the same file.
- **Parser** (4 partial files) is recursive descent, one method per precedence level. With an `ErrorList` it goes on after an error: a broken statement becomes an `ErrorStmt`, a broken declaration a name in `BrokenNames`. `ParseProgram` sorts declarations into one list per kind.
- **AST** (`Ast.cs`) holds the syntax and the fields the checker and backend fill in later.
- **Interpreter** (`CompileTime`) walks the AST of a `const fn`. It reaches the program's names only through `ICompileTimeHost`, which the checker implements. `Generate` runs a generator call and returns the emitted lines as AST nodes.
- **Driver** (`TernCompiler.cs`): `Compile` loads, checks and calls `Build`. `Build` generates code, builds the startup code for the platform, assembles (repeating for three reasons), writes the file, the labels, the interface module and the report. `TernCompiler.Nes.cs` and `TernCompiler.Apple2.cs` hold those platforms' startup code and file writers.
- **Platform, Board, LabelFormat, CharEncoding** are descriptions as data.

Findings: 12 correctness (4 medium, 8 low; all demonstrated) and 10 construction (1 high, 5 medium, 4 low).

## Findings

### 1. `Build` is one 450-line method that does six jobs

- Severity: high. Kind: construction.
- Where: `TernCompiler.cs:134-584`.
- What: `Build` allocates, builds the startup code, assembles in a loop, writes the program file, fills the symbol tables, writes the interface module, checks `keeps` promises, cuts the overlay files and writes the report. Fifteen locals are declared before the loop and shared by all of it (`plan`, `items`, `code`, `imageLabels` start as `null!`; eleven `Label`s; `moves`, `passes`, `systemFile`). The loop repeats for three unrelated reasons: page placement did not fit (`byPage`), NROM's two areas must be joined (`CanJoin`), the Apple II's copies did not settle (`passes`). Each has its own `catch` or `continue`.
- Platform branches run through all of it: `system` (10 uses), `cartridge is not null` (9 uses), `withIo`, `platform.Format == OutputFormat.Prg`, `platform.Header.Length > 0`.
- Two user errors are thrown from the middle of the output code: the `keeps` promise of an exported function (`:399-408`) and of an implementation (`:456-465`), with almost the same message and the same `reg switch` twice. `RegisterUse` is built up to three times.
- Why it matters: this is the method every new output touches. A C64 `.crt` needs a new startup prelude, a new file writer, a new report line and a new label rule: four more branches in a method nobody can hold in their head. The Commander X16 and C128 need less, but still edit it. A retry added for one platform can change the others: the `catch (CompileError) when (CanJoin())` at `:329` retries on any error.
- Fix: split by job, with a small result record between the steps: `BuildItems` (startup, jump table, images), `AssembleWithRetries`, `WriteFile`, `SymbolTables`, `InterfaceModule`, `WriteReport`. Move the two `keeps` checks into one method that runs right after assembly. Then give the platform-specific parts one owner (finding 6).

### 2. An error in a nested const fn call has no "(called from ...)"

- Severity: medium. Kind: correctness (demonstrated).
- Where: `Interpreter.cs:551` (`catch (CompileError e) when (_frames.Count == 2 && ...)`).
- What: errors raised by the checker's helpers while a `const fn` runs (`ConstNegate`, `Types.Resolve`, `_host.Read`) get their note from this `catch`. Its filter wants exactly two frames. A C# exception filter runs before the `finally` blocks of the inner calls, so when the error comes from a `const fn` called by another one, the inner frames are still in `_frames`, the count is 3 or more, and no level adds the note. `CalledFrom` stays null too, so `tern --json` gives `calledFrom: null`.
- Evidence: the same error, once with the note and once without.

```
const fn neg(x: int) -> int {
    return -x
}
const fn outer(x: int) -> int {
    return neg(x)
}
const A = neg(-9223372036854775807 - 1)
const B = outer(-9223372036854775807 - 1)
var out: u8
fn main() { out = 1 }
```

```
e.tern:2:12: error: This constant expression goes outside ... (called from e.tern:7). Use smaller values
e.tern:2:12: error: This constant expression goes outside ... . Use smaller values
```

- Why it matters: spec 4.1: "Every error raised while a const fn runs is reported at its line in the const fn, with (called from main.tern:5)". Without it the user cannot tell which of many calls failed. Library generators call helpers, so the nested case is the common one.
- Fix: drop the frame count from the filter and decide by the error: `when (e.CalledFrom is null)`. Better, wrap the three helper calls where they are made (`Unary` and `abs` call `ConstNegate` bare; `Binary` already wraps `ConstArithmetic` at `:324`). Add a test with the error two calls deep.

### 3. The C64's free zero page lacks `$FB-$FE`

- Severity: medium. Kind: correctness (demonstrated).
- Where: `Platform.cs:136` (`ZeroPage = [(0x02, 0x8F)]`).
- What: spec 12.2 and the guide (`docs/guide/03-memory.md:11`, `06-c64.md:9`) give the C64's free zero page as `$02-$8F` and `$FB-$FE`. The platform has only the first range.
- Evidence:

```
var big: [u8; 150] @ zp
fn main() { big[0] = 1 }
```

  The error says "'@ zp' variables need 150 bytes, 142 are free". 142 is `$02-$8F` alone; the documents promise 146.
- Why it matters: the documents and the compiler disagree on a number users plan with. A binary's interface module also lists the zero page a host must leave alone.
- Fix: add `(0xFB, 0xFE)` after the first range (the largest range comes first, as the property's comment asks), or change the spec and guide. The first costs a benchmark baseline update only if a benchmark fills zero page.

### 4. About 200 nested parentheses crash the compiler

- Severity: medium. Kind: correctness (demonstrated).
- Where: `Parser.Expressions.cs:233-238` (`ParsePrimary` calls `ParseExpr`), and every other recursive rule (`ParseUnary`, array literals, blocks, `ParseType`).
- What: one nesting level costs about 21 stack frames (one per precedence level and one `ParseLeftAssoc` each). The process dies with "Stack overflow" between 100 and 200 levels. There is no diagnostic and no exit through `CompileError`.
- Evidence: `out = ((( ... 1 ... )))` with 200 pairs prints `Stack overflow. Repeated 164 times: at Tern.Compiler.Syntax.Parser.ParsePrimary()`. 100 pairs compile.
- Why it matters: a stack overflow cannot be caught in .NET. For the command line it is an ugly failure on generated source. For a language server (M28) it kills the server on one bad buffer, for example while the user holds `(`.
- Fix: count depth in `ParseExpr`, `ParseUnary`, `ParseBlock` and `ParseType` and throw a `CompileError` past a limit (64 is generous): "This expression is nested more than 64 deep. Split it with a constant or a variable". The checker and interpreter recurse over the same tree, so the parser's limit protects them too. A precedence-climbing loop in place of ten `ParseLeftAssoc` levels would cut the frames per level to about four.

### 5. CHR data is written to the Mesen label file as RAM

- Severity: medium. Kind: correctness (demonstrated).
- Where: `TernCompiler.Nes.cs:186-194` (`NesLabelMap`), fed by `TernCompiler.cs:360-364` (every global goes into `symbols`).
- What: a `data` item in a `chr` area has a PPU address (`$0000-$1FFF`). `NesLabelMap` reads every address below `$2000` as CPU RAM and writes `NesInternalRam:`.
- Evidence:

```
import nes
memory {
    cartridge nrom vertical
    $0000..=$1FFF  chr tiles
}
data bg: [u8; 16] @ tiles = repeat($11, 16)
var out: u8
fn main() { out = 1 }
```

  `p.mlb` has `NesInternalRam:0000:bg` and `NesInternalRam:0000:out`: two labels on one RAM byte, one of them wrong.
- Why it matters: Mesen's debugger shows `bg` for zero-page address 0 and for every access to `out`. The same wrong entry is in `CompileResult.Symbols` and `Variables`, which tools such as `tern run --dump` read (suspected: `--dump bg` shows RAM; not run).
- Fix: leave CHR globals out of `symbols` for the label file, or write them as `NesChrRom:offset:name`, which Mesen supports. Mark them in `VariableInfo` so tools do not read CPU memory for them.

### 6. Platform facts and output formats are spread over the driver

- Severity: medium. Kind: construction.
- Where: `TernCompiler.cs:152, 156, 188-191, 196-219, 243-247, 355-356, 480-493, 566, 721`; `TernCompiler.Nes.cs:81, 142, 188-193`; `TernCompiler.Apple2.cs:26`; `Platform.cs:50, 65, 72, 83-103`; `Cartridge.cs:13, 26`.
- What:
  - A program file is written in three places, chosen by three different tests: `Platform.FileOf` (`.prg`, `.xex`, and the raw image for the Apple II's overlays, by `Format`), `SystemFile` (by `system`), `NesFile` (by `cartridge is not null`).
  - In the driver, "has a cartridge" stands for "is the NES": `sei`, `NesStart`, `NesVectors`, `CheckVectors`, RAM cleared at startup, "Variables in RAM" in the report. It is the driver's half of Semantics finding 16.
  - `Board` is described as general, but has `Mapper` ("the iNES mapper number") and a static `Mirroring` list.
  - Addresses of one machine are literals in shared code: `b.First > 0x07FF` in `Startup` (the NES's RAM), `0x8000` and `0xC000` in `NesFile`, `CheckVectors` and `NesLabelMap` (the board's areas, which `Board.CodeAreas` and `BankArea` already hold), `CopyFrom = 0x50` (the first byte of `Platform.Apple2.ZeroPage`), `Platform.Runad`.
  - `withIo` asks for a library module by name (`m.Name == "io"`). The BASIC-line error builds its text from `platform.Name.ToUpperInvariant()`.
- Why it matters: tested against the planned work. A C128 at `$1C01` is only data (`LoadAddress`, `Header`): good. The X16 is data plus a CPU variant. A C64 `.crt` is not: `layout.Cartridge` becomes non-null on the C64 and the driver then emits `sei`, NES startup, NES vectors and an iNES file. Every `cartridge is not null` must be found and split by hand.
- Fix: one object per output kind, chosen by the platform (and the board, when there is one), with the parts `Build` now branches on: `StartupPrelude(...)`, `ExtraItems(...)` (vectors, quit code), `WriteFile(...)`, `ReportLines(...)`, `LabelValue(...)`, and `RamClearedAtStartup`. `Prg`, `Xex`, `ProDosSystem` and `Ines` are the four today; `Crt` is the fifth. Move the iNES fields of `Board` to that object's board description. Take ROM ranges from `Board`.

### 7. A compile reports through one exception that is changed after it is thrown

- Severity: medium. Kind: construction.
- Where: `Lexer.cs:9-38` (`CompileError`), `ErrorList.cs:34-44`, `TernCompiler.cs:89-131`.
- What: `Compile` returns a result or throws the first `CompileError`. That exception carries the others: `All`, `WithWarnings` and `MoreNotReported` are set on it after it is made (`ThrowIfAny`, `AddWarnings`). Warnings are also `CompileError` objects, "never thrown", told apart by `IsWarning`. `--warnings-as-errors` is done by two local functions that re-add warnings as errors and throw from inside a `catch` filter path.
- A failed compile gives nothing else: no AST, no symbols, no partial checks. There is no entry point that stops after the checks. `ModuleLoader` reads modules from disk only, so only the main file can come from memory.
- Why it matters: a language server needs, on every keystroke, the diagnostics and whatever was understood (symbols for completion, positions for go-to-definition) of a program that usually has an error. Today it would call `Compile`, catch, and get messages only; it would also run allocation, code generation and layout for nothing when the program is valid. The mutable exception is also easy to get wrong: `All` on any error but the first is just itself.
- Fix: a `Diagnostic` record (position, message, severity, `CalledFrom`) and a `Check(source, ...)` entry that returns `(CheckedProgram?, IReadOnlyList<Diagnostic>)` without throwing. `Compile` calls it, then `Build`. Keep `CompileError` as the exception for a single error inside a pass. Let `ModuleLoader` take a function from path to source text.

### 8. Tokens and nodes have a start but no end, and comments are dropped

- Severity: medium. Kind: construction.
- Where: `Lexer.cs:68` (`Token`), `Lexer.cs:219-236` (comments), `Ast.cs:6-9` (`Node`), `Ast.cs:288, 298` (`MatchArm`, `MatchPattern`), `Ast.cs:98` (`FieldValue`), `Parser.Declarations.cs:143`.
- What:
  - A `Token` has `Pos` and `Text`, but for strings and chars `Text` is the decoded value, so the end of the token in the source is not known. A `Node` has only `Pos`. The parser rebuilds source text from line starts where a message needs it (`TokenText`, `RangeText`).
  - Positions differ in meaning between nodes: a `BinaryExpr` is at its operator, an `ExprStmt` at its expression (so a statement `a + b` is "at" the `+`), an `AsmStmt` at its `{` with a patched-in `KeywordPos`, an `IndexExpr` at the `[`.
  - The lexer skips comments. Nothing can attach a `///` comment to the declaration after it.
  - `MatchArm`, `MatchPattern` and `FieldValue` are not `Node`s. `ProgramSyntax` holds one list per kind of declaration, so source order between kinds is gone, and a declaration with a syntax error survives only as a name.
  - `Ast.cs` depends on `Semantics` and `Cpu` (types, symbols, `AsmEffects`, `Mnemonic`): the tree cannot be built or tested without the checker's classes. (The fields the checker fills are the Backend review's finding 11; not repeated here.)
- Why it matters: M28 needs doc comments kept by the parser, and a language server needs ranges (to underline an error, to find the node under the cursor) and declarations in order (outline). Each is a change to every node constructor if left until then.
- Fix: give `Token` an end offset and `Node` a `Span` (start of first token, end of last), set in one place by the parser (`Finish(node, firstToken)`). Have the lexer return `///` comments as trivia on the next token and store the text on `FnDecl`, `VarDecl`, `ConstDecl`, `StructDecl`. Keep a `Declarations` list in source order next to the per-kind lists.

### 9. Where a statement ends is decided in the lexer and patched in the parser

- Severity: medium. Kind: construction.
- Where: `Lexer.cs:120-123` (`EndsStatement`), `:138-146` (`nesting`), `:193-194`; `Parser.Declarations.cs:346-347`; `Parser.Expressions.cs:197-199, 222`; `Parser.cs:88-90, 238-242`; `Parser.Statements.cs:91`.
- What: the lexer emits `Newline` only after a fixed list of token kinds and only outside brackets. The list is grammar knowledge kept away from the grammar, and the parser works around it:
  - `volatile` ends a line only in a memory block, so `ParseMemory` has its own ending (`if (kind.Kind == TokenKind.Volatile) Accept(Semicolon)`), and junk after `volatile` on that line is read as the next line.
  - `zp`, `return`, `break` and `continue` are in the list because they can end a line; a new keyword that can end a line must be added there or statements run together with no error at the right place.
  - A brace resets the bracket count to 0 and it is not restored at the `}`, so the parser skips line breaks by hand inside array literals.
  - After an unclosed `(` there are no `Newline` tokens, so recovery guesses the next statement from columns (`StartsLineAt`, `_statementColumn`) and from `_failures > 0`.
- Why it matters: the rule of spec 2 ("a newline inside ( ) or [ ] does not end a statement; a brace ends that rule") is implemented in four places that must agree. Finding 15 is one case where they do not. Column-based recovery depends on indentation, which spec 2 says has no meaning.
- Fix: have the lexer emit every line break as a token (or a flag "first on its line" on each token) and let the parser decide: skip breaks while its own bracket depth is above 0 or after a binary operator. The bracket stack is then exact, the `volatile` case disappears, and recovery can find the next line without columns.

### 10. `ParseProgram` is 280 lines with a 130-line local function

- Severity: medium. Kind: construction.
- Where: `Parser.Declarations.cs:11-288`.
- What:
  - The method declares twelve lists, parses the header, imports, constants and memory block in fixed order, then loops over `ParseDeclaration`, a local function with a 25-arm switch that captures all the lists.
  - Modifiers are handled by recursion and by position: `noinit` and `volatile` call `ParseDeclaration()` again and then change `globals[^1]`, trusting that the inner call added a global last. `private` stores the returned name in a set of strings, apart from the declaration.
  - A declaration is built, then copied to add a flag: `ParseLinearVar` and `ParseIndexedVar` make a new `VarDecl` from the fields of the one `ParseVar` returned (`:457, :469`), and `ParseConstFn` a new `FnDecl` (`:555`). A property added to `VarDecl` or `FnDecl` is lost in the copy unless it is added there too; `IsData` is already copied by hand in one and not the other.
  - Checks that are not syntax sit between the parsing: "'noinit' is for variables; data is never zeroed", "An inline function needs a body".
- Why it matters: finding 18 comes from the `globals[^1]` pattern. Adding a modifier (or doc comments, which belong to the declaration after the modifiers) means touching the switch, the recursion and the copies.
- Fix: parse the modifiers first into a small set (`private`, `noinit`, `volatile`, `linear`, `indexed`, `inline`, `interrupt`, `asm`, `const`), then the declaration, then apply and check the set in one place. Make `VarDecl`'s flags settable so nothing is copied. Move `ParseDeclaration` out to a method that returns the declaration; let `ParseProgram` file it.

### 11. Message text is used as data, and the interpreter trusts the checker without saying so

- Severity: medium. Kind: construction.
- Where: `Interpreter.cs:72-76` (`WithNote`), `:233`, `:250`, `:551`, `:618`; `:560`, `:608`, `:623`.
- What:
  - `WithNote` puts the note after the first `". "` of a message. A message whose first sentence holds ". " (an abbreviation, "e.g. ", a file name) gets the note in the wrong place.
  - The filter at `:551` tests `!e.Description.Contains("(called from ")` to see whether a note was added.
  - `Slot.ReadOnly` is the text for an error ("a constant", "the loop variable"), and `IsConstant` compares it: `s.ReadOnly == "a constant"`.
  - `Range` builds a whole `CompileError` with `Checker.CountsUp(...)` only to take its `Description`.
  - This is the pattern of Semantics finding 17, in a second place.
  - The other direction: `Exec` casts `(CallExpr)e.Expr`, uses `v.Init!` and `frame.Returned!`. These hold only because the checker rejects the programs first (tested: an expression statement, `var x` with no value, a bare `return` and a `break` outside a loop in a `const fn` are all checker errors). Nothing in the interpreter says so, and its `default:` arms give user errors for the same kind of case, so a reader cannot tell which checks are real.
- Why it matters: rewording a message changes behavior with no test failing. If the checker's body check for `const fn` loses a rule, the compiler crashes with a cast error instead of reporting.
- Fix: `CompileError.CalledFrom` already says whether a note was added: test that. Make `ReadOnly` an enum with a `Describe`. Give `CountsUp` a text-only overload. State the checker's guarantees in one comment at the top of `Exec` and turn the three unchecked places into `InvalidOperationException` with a message, as `Types.Resolve` does.

### 12. Identifiers accept any Unicode letter or digit

- Severity: low. Kind: correctness (demonstrated).
- Where: `Lexer.cs:244-247` (`char.IsLetter`, `char.IsLetterOrDigit`), `:273` (`char.IsDigit`).
- What: spec 2: identifiers are `[A-Za-z_][A-Za-z0-9_]*`. The lexer accepts `é`, Greek, and non-ASCII digits inside names. A non-ASCII digit at the start of a token gives "Number has no digits. Write digits after '$' or '0b'".
- Evidence: this compiles, and `h.vs` holds `al C:0002 .café` and `al C:0003 .αβ`.

```
var café: u8
var αβ: u8
fn main() {
    café = 1
    αβ = 2
}
```

- Why it matters: the names go into label files for VICE, Altirra, AppleWin and Mesen, and into the generated interface module. Those tools read ASCII. Two names that look the same can differ (composed and decomposed `é`).
- Fix: use `char.IsAsciiLetter`, `char.IsAsciiLetterOrDigit` and `char.IsAsciiDigit`. A non-ASCII letter then gets "Unexpected character"; say "Names use A-Z, a-z, 0-9 and '_'" for it.

### 13. A const fn named like a mnemonic cannot be called as a generator

- Severity: low. Kind: correctness (demonstrated).
- Where: `Parser.Statements.cs:286` (`Enum.TryParse<Mnemonic>(word.Text, ignoreCase: true, ...)`).
- What: on an asm line the parser asks first whether the word is a mnemonic, in any case. A generator named `dec`, `inc`, `and`, `bit`, `Inc` is read as the instruction. Declaring such a `const fn` is not an error.
- Evidence:

```
const fn dec(n: int) {
    emit { ldx #n }
}
asm fn g() {
    dec(3)
    rts
}
fn main() { g() }
```

  gives "q.tern:6:5: error: 'dec' has no (address) mode. Write (pointer),y to read through a pointer".
- Why it matters: the error does not name the cause. The 65C02 adds mnemonics (`stz`, `bra`, `phx`, `trb`), and since the parser takes them from the enum and not from the target's CPU, adding them changes the meaning of existing programs that have generators or variables with those names, on every target.
- Fix: report a `const fn` without a return type whose name is a mnemonic at its declaration ("'dec' is a 6502 instruction, so an asm line cannot call it. Use another name"). When the CPU variant reaches the parser or checker (Backend finding 7), take the mnemonic set from it.

### 14. A const fn's array variable ignores its written length

- Severity: low. Kind: correctness (demonstrated).
- Where: `Interpreter.cs:593-596` (`VarDecl` with a type and a value), `Convert` at `:398-400`.
- What: the written length is used only when there is no initial value. With one, it is not compared.
- Evidence: `out` is 2.

```
const fn f() -> int {
    var a: [int; 4] = [1, 2]
    return len(a)
}
const A = f()
var out: u8
fn main() { out = A }
```

- Why it matters: `a[3]` then fails with "Index 3 is outside the array (length 2)", far from the cause. Spec 4.1 has a written length checked for `data` and globals; a local with a written length should behave the same.
- Fix: when `ArrayTypeSyntax.Length` is not null and there is a value, evaluate the length and compare: "'a' is declared with 4 elements, but its value has 2. Write [int; _], or give 4 values".

### 15. A line break inside a block comment does not end the statement

- Severity: low. Kind: correctness (demonstrated).
- Where: `Lexer.cs:224-236`.
- What: the comment loop counts the line but emits no `Newline`.
- Evidence: "k.tern:5:8: error: Expected end of statement, found 'b'".

```
var a: u8
var b: u8
fn main() {
    a = 1 /* note
    */ b = 2
}
```

- Why it matters: spec 2 says statements end at a newline, and the two statements are on two lines. It is rare; it shows the cost of finding 9.
- Fix: after a block comment that held a line break, apply the same rule as for `'\n'` (emit `Newline` if `nesting == 0` and the last token ends a statement).

### 16. A private-use character in a string becomes a raw byte

- Severity: low. Kind: correctness (demonstrated).
- Where: `Lexer.cs:79-81` (`RawByteBase`), `:351` (characters appended as they are).
- What: `\$NN` is stored in the token text as U+E000 + NN. A source file that holds a character U+E000-U+E0FF itself is not told apart.
- Evidence: the file holds `data t = "A<U+E042>C"`; `out = t[1]` gives `$42`, with no error.
- Why it matters: every other character without a code is an error that suggests `\$NN`. C64 fonts and PETSCII converters use this very range (U+E000 on) for graphics characters, so pasted text is the likely source.
- Fix: in `ReadQuoted`, reject a source character for which `IsRawByte` is true ("This character has no code in any encoding. Write it as \$NN"). Or keep the raw bytes out of the string: a list of `(char, isRaw)`.

### 17. Alignment padding in NES ROM is `$00`, not `$FF`

- Severity: low. Kind: correctness (demonstrated).
- Where: `TernCompiler.Align.cs:375-389` (`ImagePadding`), written as zeros by the assembler; `TernCompiler.Nes.cs:151, 162` fill only what no segment covers.
- What: spec 12.2: "Unused PRG bytes are $FF, the value of erased ROM: unused banks, and gaps inside NROM's ROM and inside banks". Padding before `@ align(n)` data is inside a segment, so it stays zero.
- Evidence: with `data a: [u8; 3] = [1, 2, 3]` and `data b: [u8; 4] @ align(256) = [9, 9, 9, 9]` on `nrom`, the report says "169 bytes of padding" and the image has `00` from `$C057` to `$C0FF`.
- Why it matters: small. A ROM compared against a build by another tool differs, and an erased-ROM check does not see the bytes as free. The jump table's gaps (`TernCompiler.cs:231`) and the gaps of `InitialValues` are zeros by intent.
- Fix: give `Padding` a fill byte and use `$FF` when the layout has a cartridge; or say in the spec that padding is zero.

### 18. `noinit linear data` gives two errors for one declaration

- Severity: low. Kind: correctness (demonstrated).
- Where: `Parser.Declarations.cs:240-243` and `:252-255`.
- What: the inner `ParseDeclaration()` adds the declaration to `globals`, then the modifier check throws. `Recover` adds the name to `BrokenNames`, but the declaration stays in `globals` and is checked.
- Evidence: `noinit linear data t: [u8; 2] = [1, 2]` reports "'noinit' is for variables; data is never zeroed" at 2:1 and "'linear' is only for arrays of 16-bit values or structs" at 2:8.
- Why it matters: spec 13 gives at most one error per declaration. The name is also both declared and broken, which only works because the checker meets the real declaration first.
- Fix: check the modifier before adding (finding 10's fix does this), or remove `globals[^1]` before throwing.

### 19. Three messages that do not name the cause

- Severity: low. Kind: correctness (demonstrated).
- Where: `Parser.Statements.cs:52` with `Parser.Expressions.cs:252`; `Lexer.cs:273-307`; `CharEncoding.cs:104`.
- What:
  - `emit { lda #1 }` in a normal `fn`: "Expected '=' and the value of 'lda', found '#'". `emit` is a keyword only in a `const fn`, so `emit {` is read as a struct literal. Any `name {` at the start of a statement gets this.
  - `out = 0x10`: "Expected end of statement, found 'x10'". Spec 13 asks for errors that name a C habit; `0x` is the most likely one.
  - `data t = ascii"HI"`: "Unknown encoding 'ascii'. Use one of: ...". Spec 2.1 lists `ascii`. CLAUDE.md asks for "'x' is not supported yet" for a spec word that is not built.
- Fix: in `ParseStatement`, for an identifier followed by `{`: "'emit' adds assembly only in a const fn" for `emit`, else "A struct literal is only an initial value". In the lexer, `0` followed by `x` or `X` and a hex digit: "Write hex numbers with '$': $10". For `ascii`: "The 'ascii' encoding is not supported yet".

### 20. Parser modes are fields set and reset by hand

- Severity: low. Kind: construction.
- Where: `Parser.cs:38-40, 52, 124`; `Parser.Statements.cs:242, 252, 307`; `Parser.Expressions.cs:17`; `Parser.Declarations.cs:543`.
- What: six fields steer the parse: `_inAsm`, `_inHeader`, `_inConstFn`, `_statementColumn`, `_failures`, `_unclosedReported`. `_inHeader` and `_inConstFn` are restored with `try/finally`. `_inAsm` is set to true in `ParseAsmBody`, set to false at its end, and reset on an error by `Fail`, a method about reporting. `_statementColumn` is restored only on the normal path of `ParseBlock`. `_failures > 0` changes what `ExpectEndOfStatement` accepts for the rest of the file, so one early error loosens the rules for every later statement.
- Why it matters: `_inAsm` decides whether `.name` is a label. A path that leaves it set (an exception with no `ErrorList` is fine today, since parsing stops) would change how later code parses. It is hard to see that the six stay consistent.
- Fix: one `using`-style scope helper for the three modes (`using (Mode(ref _inAsm, true))`). Limit the loosened ending to the statement that had the error.

### 21. Words that are not keywords are compared as strings in many places

- Severity: low. Kind: construction.
- Where: `Parser.cs:133-137, 145-152`; `Parser.Statements.cs:52, 73, 179, 229`; `Parser.Expressions.cs:153`; `Parser.Declarations.cs:292, 319, 340-343, 373, 385, 388, 446, 464, 507, 523, 646, 652`; registers at `Parser.Declarations.cs:628-635` and `Parser.Statements.cs:330, 360-365, 373`.
- What: `step`, `emit`, `indexed`, `memory`, `cartridge`, `encoding`, `default`, `align`, `ptr` and `_` are tested with `Current.Text == "..."` at 20 sites; `indexed` at five. The list of memory kinds is written twice in `ParseMemory` (the check and its message), and again in the checker. Registers are case-sensitive after `using` and `keeps` (`A`, `X`) and case-insensitive in assembly (`a`, `x`, `Y`), by two different pieces of code.
- Why it matters: a rename or a new contextual word is a text search. `StartsCurrentDeclaration` and `DeclaredName` repeat the declaration grammar for recovery and must follow every change to it.
- Fix: a static class of contextual words with `Is(Token, Word)`; one array of memory kinds used for the check and the message (and the enum Semantics finding 16 asks for); one register parser with a flag for the asm spelling.

### 22. Small things

- Severity: low. Kind: construction.
- `Interpreter` is `partial` with one file. `Types` (`Interpreter.cs:871`) is a general name for the compile-time types only; `CtTypes` fits.
- `Ast.cs:165`: "In v0 only .lo and .hi" on `MemberExpr` is out of date (structs, modules).
- `BlockStmt.Code` (`Ast.cs:230`) builds a new list on every read.
- A real literal is parsed twice: `Lexer.cs:285` (to check the range) and `Parser.Expressions.cs:176`. `Token.Value` is a `long`, so the lexer cannot pass it on.
- `ParamSyntax` marks a fixed input with an empty name and a `NamedTypeSyntax` named `""` (`Parser.Declarations.cs:577`).
- `SourcePos`, `Token`, `TokenKind` and `CompileError` are in `Lexer.cs`; `FollowOnError` is in `ErrorList.cs` and in the `Syntax` namespace, used mostly by the checker.
- `TernCompiler.cs:318, 595`: `program.Modules[^1]` is the main file, by the loader's order. `CheckedProgram.Main` exists for the function; add one for the module.
- `TernCompiler.cs:321`: the Apple II's passes use 4 and 8 with no names, and the end is an `InvalidOperationException`. Spec 12.2 gives the 4.
- `NesLabelMap` finds a label's bank from the text before the first dot of its name (`TernCompiler.Nes.cs:192`). Pass the symbol's module.
- `TernCompiler.cs:384`: the registers `setup` changes are found by scanning `items` from a label to the first `rts`.
- `ClearBlock` (`TernCompiler.cs:727`) and `CopyBlock` (`TernCompiler.Nes.cs:200`) are the same loop twice; `CopyBlock` is not NES code but lives in the NES file.
- `Platform.FileOf` takes `first`, `image` and `segments`, three views of the same bytes, and each format ignores one or two of them.

## Patterns

- **"Cartridge" means NES, "system" means Apple II.** The driver branches on the output kind through booleans computed at the top of `Build` (findings 1, 6). The checker does the same with `Boards.Count > 0` (Semantics 16). One description object per output kind would remove both.
- **Grammar knowledge kept outside the grammar.** The lexer's `EndsStatement` list and bracket count (9), the recovery helpers that re-read declarations token by token (`StartsCurrentDeclaration`, `DeclaredName`; 21), the mnemonic test that decides between instruction and generator (13).
- **Build, then copy to change.** `ParseLinearVar`, `ParseIndexedVar`, `ParseConstFn` (10) and `CtLabel.Plus` copy an object field by field to set one more. Each new field must be added to each copy.
- **Message text as data.** `WithNote`, the "(called from " test, `ReadOnly == "a constant"` (11), as in Semantics 17.
- **Positions without extent.** Tokens and nodes have a start only; text for messages is cut from the source again (8). Every tool that needs a range will rebuild it.
- **User errors found after the checks.** The driver throws eight kinds of `CompileError` after code generation (`TernCompiler.cs:185, 189, 404, 463`, `CheckFixedData`, `CheckFixedAddresses`, `CheckVectors`, `SystemFile`), with long messages built inline. Some need addresses and belong there; the `keeps` promises and the vectors' targets do not need layout and could be checker errors, reported with the others.

## Done well

- The precedence chain in `Parser.Expressions.cs` matches spec 3.6 level for level, with `as` postfix and compares not chaining. It reads as the table.
- Parser errors follow the convention: what is wrong, then a fix that starts with a verb. The errors for habits (`n++`, `*p`, `%0101`, `[v; n]`, `pub fn`, a keyword as a name) are specific and placed at the habit.
- Recovery design: `ErrorStmt` and `BrokenNames` carry the declared name so the checker adds no follow-on errors; a `const fn` with a broken body is dropped whole; an `Error` token stops the parser from reporting what the lexer already did.
- `ICompileTimeHost` is a narrow, well-commented boundary between interpreter and checker. The interpreter never touches the checker's state.
- Array value semantics in the interpreter were checked on every path (argument, return, `var`, assignment, element store, list, `repeat`): no path shares an array.
- Limits and arithmetic match spec 4.1: steps counted per evaluation, 200 calls, `int` overflow through the checker's own `ConstArithmetic`, `Finite` on every real result, `round` away from zero, range checks before the cast to `long`.
- `emit` fragments are parsed once as AST and rewritten as AST, with labels renamed per `emit`; there is no text pasting anywhere.
- `Platform`, `LabelFormat` and `CharEncoding` are plain data with comments that give the reason for each range. The encoding tables were checked against PETSCII, C64 screen codes, ATASCII, Atari internal codes and Apple II text: correct.
- Output formats checked by hand: the C64 BASIC line (`SYS 2061` = `$080D`), `.xex` segments and `RUNAD`, the iNES header (mapper nibbles, mirroring bit, sizes), UxROM bank order and power-of-two size, `$FF` fill, the Apple II's copy loops in both directions and the ordering of the copies. All agree with spec 12.2, apart from findings 3, 5 and 17.
- `ErrorList` is small and does what spec 13 says: file order, then line and column, duplicates once.
