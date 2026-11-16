# Follow-up review of the code-quality fixes (R7)

Roadmap R7 (decisions 2026-11-15 and 2026-11-16): a verdict for each of the 93 findings of the code-quality review (`code-quality.md` and its five area files), for the findings R1-R5 handled. Done on `main` at `5c57180` (R1-R5 merged), 2026-11-16, by one review session on Claude Fable. Input: the area files, the finding tables in `docs/status.md`, each fix's diff and the code its finding names. Nothing else was read, and no new kind of finding was looked for.

Verdicts: **fixed**, **partly** (what is left is named, with who has it), **not fixed**, **left by decision** (with the date), **left for R6** (not read; R6's review checks it).

## Progress

The work is in the roadmap's four steps, committed after each. A session that continues starts at the first step not marked done.

| Step | State |
|---|---|
| 1. Ranked bugs 1-15, demonstration programs run again | done |
| 2. Other correctness findings marked fixed | done |
| 3. Rows marked "partly" | not started |
| 4. Construction searches (R4, R5) | not started |

## Step 1: the ranked bugs

Each demonstration program of the area file was compiled and run again with `tern` / `tern run` built from `main` (debug build). All 15 now behave as the finding says they should.

| # | Finding | Verdict | What was run, and the result |
|--:|---|---|---|
| 1 | backend 1 | fixed | The program prints `out = 18` (was 28). |
| 2 | backend 2 | fixed | `out = 77` (was 0). |
| 3 | backend 3 | fixed | Listing: `freq = w << 1` shifts in a temporary and writes each byte of `freq` once; `freq = w + k` is `lda w / adc k / sta freq / lda w+1 / adc k+1 / sta freq+1`, no read of `freq`; `port <<= 2` is `lda port / asl a / asl a / sta port`. |
| 4 | semantics 1 | fixed | The loop ends with `out = 5`. Also run with `step list[k].m` (a `linear` record array) and `step p.m` (a pointer to a record), field at offset 1: 5 each. |
| 5 | semantics 2 | fixed | Error: "The loop body calls a function, which can change X. Remove 'using X', ...". |
| 6 | backend 4 | fixed | `for i in f()..g()` gives `out = 10`; `return f() >= g()` into the carry gives false. |
| 7 | syntax 3 | fixed | The error for 150 bytes says "146 are free"; a program with 142 bytes and two pointers in `@ zp` builds. |
| 8 | tools 3 | fixed | `-o b.tern`, `-o B.TERN` and `-o ./x/../b.tern` are refused ("would overwrite the source file") and the source is unchanged; `-o c.lst`, `-o x.vs`, `-o x.lines` and a binary with `-o lib1_api.tern` give "Two outputs would be written to ...". |
| 9 | semantics 4 | fixed | With the data at `$9000` (inside the program area; `$C000` now gets the memory-map error first) the const fn reads 5 and the program prints `out = 6`. No crash. |
| 10 | syntax 4 | fixed | 200 bracket pairs compile; 300 pairs, a sum of 5,000 terms, 5,000 unary minus signs, 400 nested `if` blocks and 400 nested indexes each give "This is nested more than 256 deep ...", with JSON under `--json`. `Compile` and `Check` both run on the 64 MB stack (`TernCompiler.OnLargeStack`). |
| 11 | tools 2 | fixed | Every line of the finding now gives a one-line message and exit code 1, as JSON with `--json`: a missing output folder, `-o` a folder, a folder as source (compile, run, bench), an empty file name. `tern bench --fn bump` on a binary without `main` is a failed variant with the message. |
| 12 | semantics 5 | fixed | "Recursion is not allowed: a -> b -> a". The finding's "more generally" (a function no program reaches that calls itself is not reported) is unchanged, as the table says; an unreached function is not compiled, so no wrong code comes of it. |
| 13 | semantics 6 | fixed | `var v = BOX * 30` and `for i in 0..BOX * 30` compile; `out = 780` (390 + 390). |
| 14 | syntax 2 | fixed | Both errors carry the note: "(called from y2.tern:7)" and "(called from y2.tern:8)". |
| 15 | syntax 5, 17 | fixed | The label file has `NesChrRom:0000:bg` and `NesInternalRam:0000:out`; `tern run --dump bg` says the data is in the PPU's memory. The 169 padding bytes before `@ align(256)` data in the `.nes` file are `$FF`. |

## Step 2: the other correctness findings marked fixed

Demonstration programs run again where the area file has one; otherwise the fix's diff or its test was read or run, as the row says.

| Finding | Verdict | What was run, and the result |
|---|---|---|
| backend 5 | fixed | `a[a[0].lo] = b[k] + c[k]` gives `out = $0102` (was `$0002`). |
| backend 6 | fixed | Not reachable from source, so nothing to run; the diff (`9208e14`) adds the label's offset to the grown branch's `jmp`, as the finding asks. |
| semantics 3 | fixed (decision 2026-11-15) | The module's `noinit var buf: [u8; 16] @ buffers` compiles and the program prints `out = 7`. |
| semantics 7 | fixed | `70000 as u16` is `$1170`, `70000 as u8` is `$70`, `-70000 as u16` is `$EE90`. |
| semantics 8 | fixed | With `const BASE = $C0`: `BASE.hi` is 0 and `BASE.lo` is `$C0`. A typed `u8` constant's `.hi` is still the error, as intended. |
| semantics 9 | fixed | The program is now the error "An array of u8 has at most 256 elements" with the `const` line too, so the order of declarations no longer matters. |
| semantics 10 | fixed | "'lda (address),y' needs a pointer in zero page, but 'msg' is data, not a pointer. Declare one, ...". |
| semantics 11 | fixed | "'helpr' is not a function. Check the spelling, or declare it". |
| semantics 12 | fixed | All five messages now end in a fix sentence, and the pointer one compiles as written ("Convert with 'as u16' first, then 'as ptr u8'"); the two planned features say "is not supported yet". The fallback for types with no conversion is R6's, as the table says. |
| semantics 13 | fixed (decision 2026-11-15) | `for i in 0..256 using X` and `for i in 0..len(buf) using Y` over a 256-byte array compile and fill the array; a plain `0..256` loop runs 256 times. |
| syntax 12 | fixed (decision 2026-11-15) | `café`, `αβ` and a name with an Arabic-Indic digit each give "'é' (U+00E9) cannot be in a name: names use A-Z, a-z, 0-9 and '_'". |
| syntax 13 | partly | The program now has an error at the declaration: "'dec' is a 6502 instruction, so an asm line cannot call a generator of that name. Use another name". Left: the mnemonic set per CPU variant, for M29 (see step 3). |
| syntax 14 | fixed | "'a' is declared with 4 elements, but its value has 2 (called from y14.tern:5). Write [int; _], or give 4 values". |
| syntax 15 | fixed | The two statements compile; `a = 1`, `b = 2`. |
| syntax 16 | fixed | "The character U+E042 has no code in any encoding. Write the byte it stands for as \$NN". |
| syntax 18 | fixed | One error ("'noinit' is for variables; data is never zeroed. Remove 'noinit'"). |
| syntax 19 | partly | The three named messages are fixed (`ascii` "is not supported yet", `emit {` outside a const fn, "Tern has no '0x'"). Left: any other `name {` at the start of a statement (`foo { lda #1 }`) still gets "Expected '=' and the value of 'lda', found '#'", with no fix sentence; the table gives it to R6. |
| tools 8 | fixed | `tern bench --json --cycles 1000 --code "while true { }"` gives `"ok": false` and exit code 1; `--dump nosuch` gives exit code 1. |
| tools 9 | fixed (decision 2026-11-15) | The program gives `early = 0`, `stale = 0`, `late = 0`. The NMI at once and the `$2006` second write are covered by `RunModelTests` (run: 19 pass). |
| tools 10 | fixed | "; total 6492 cycles, and 84 taking 12 interrupts" under a run of 6576 cycles. |
| tools 11 | fixed | `$D800` holds `00 01`, `err = 4`, `flag = 1`. The two smaller ones (Apple II storage type, Atari cursor) by their tests in `RunModelTests`. |
| tools 12 | fixed (decision 2026-11-15) | On the NES, a program with `data irq_vector: u16 @ $FFFE = addr(on_brk)` runs its handler for both `brk`s (`breaks = 2`) and returns; on the C64 a `brk` stops the run with exit code 2. |
| tools 13 | fixed | "--cycles needs a whole number above 0, found 'abc'"; "--max-errors needs a whole number from 1 to 2147483647, found '0'"; `--json --target` answers in JSON. |
| tools 14 | fixed | The help names the Atari's overlay reads on IOCBs 1-7 and the MLI's calls, and says "a few ROM routines have stand-ins, which take no cycles". |
| tools 16 | fixed | Not run by hand; `RunModelTests.A_load_past_ffff_stops_the_run` and `The_atari_screen_scrolls_and_wraps_at_ffff` pass, and the CLI's last catch (tools 2) covers the rest. |
| tools 17 | fixed | The finding's fix was a table test of the cycle counts and the limits written down: `InstructionSetTests.Every_opcode_has_the_datasheets_cycles` checks all 256 opcodes, and `Cpu6502`'s comment and the help name the limits. The parts not modelled (invalid BCD flags, the `cli`/`sei` delay, dummy reads) stay not modelled, now stated. |
| tests 1 | fixed | `OperandMatrixTests` puts each operand form (field, element, indexed element, pointer target, call) in each place (loop start, end, step, match value, index, argument, return, into the carry), with `OperandMatrix16Tests` for `u16`. Ranked bugs 1, 2, 4, 5 and 6 are in it. |
| tests 2 | fixed | `Run` and `Library` start from `StartState` (seeded memory, registers not zero); no test asks for a zero start (`zeroStart: true` is used nowhere), and `tern run` starts the same way (question 171). |
| tests 3 | fixed | `BusLog` counts the CPU's reads and writes; `VolatileAccessTests` covers 16-bit volatile targets. |
| tests 8 | fixed | `Run.CompareWithPlain` runs a program without the optimizer and compares. |

## Must fix

None from steps 1 and 2.
