# Follow-up review of the code-quality fixes (R7)

Roadmap R7 (decisions 2026-11-15 and 2026-11-16): a verdict for each of the 93 findings of the code-quality review (`code-quality.md` and its five area files), for the findings R1-R5 handled. Done on `main` at `5c57180` (R1-R5 merged), 2026-11-16, by one review session on Claude Fable. Input: the area files, the finding tables in `docs/status.md`, each fix's diff and the code its finding names. Nothing else was read, and no new kind of finding was looked for.

Verdicts: **fixed**, **partly** (what is left is named, with who has it), **not fixed**, **left by decision** (with the date), **left for R6** (not read; R6's review checks it).

## Progress

The work is in the roadmap's four steps, committed after each. A session that continues starts at the first step not marked done.

| Step | State |
|---|---|
| 1. Ranked bugs 1-15, demonstration programs run again | done |
| 2. Other correctness findings marked fixed | not started |
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

## Must fix

None from step 1.
