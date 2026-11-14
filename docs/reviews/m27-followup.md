# Review: M27 follow-up, questions 160, 166 and 169

Branch `m27-followup` (`951eb17` to `b5865f9`, 4 commits), from `main` at `7a3535a`. Roadmap "M27: NES", last item (the three changes of questions 157-169); decision 2026-11-14; spec 8.5 and 12.2.

## Verdict

**Ready to merge.** No must-fix findings. Each of the three changes matches its decision and the spec's text, and each error has a test.

`dotnet test` (in `../6502-language-dev`, on `b5865f9`): 3720 compiler tests pass. No benchmark changed. `SkillTests` pass.

## What changed

- **Question 160** (`MemoryLayout.cs:157`): a `program` line on a cartridge gives "A 'program' line has no meaning on the NES: the board ... gives the code areas (...), and the variables are in RAM, $0000-$07FF. Remove the line, or use 'reserved' or 'region' ...". This matches spec 12.2 ("A `program` line is an error ... no meaning"). The old "not supported yet" case is removed. Both boards are tested, including a `program` line in RAM on `uxrom`. The board's code areas are listed without UxROM's bank area, which is correct: the bank area is not where the resident program goes.
- **Question 166** (`Checker.Implements.cs:51`): the key is (interface, unit), checked only for modules in a bank, so overlay areas are unchanged. Overlay modules in one area can still implement the same interface (spec 8.5). The error names both modules, the bank and the area, and points at the second import's interface name (test checks line and column).
- **Question 169** (`TernCompiler.Nes.cs:100`): checked after the existing "interrupt fn or asm fn" check, so `f` is never null there. Tested for NMI with an `interrupt fn` and for IRQ with an `asm fn`. The message gives both fixes the decision names.

## Cases I checked

Each was compiled with `tern --target nes` on the branch:

- **Two modules in one bank implementing different interfaces whose routines are at the same address.** The tables would share bytes, as in question 166, but the same-interface check does not cover this. It cannot happen: the interface modules are resident, so their fixed routines already clash ("'api1.value' ($8000-$8002) and 'api2.other' ($8000-$8002) are placed at overlapping addresses"). The same-interface check therefore covers every way two tables in one bank can land on the same bytes.
- **The same two modules with the second interface at `$8003`.** This builds. Bank 1 holds level1 and level2, and `tern run` returns the right value.
- **Vector `data` at `$FFFA` declared inside the bank module.** Already an error: fixed data of a bank module must lie in its own area.
- **A `[u16; 3]` vector table at `$FFFA` holding a bank function.** Already an error, because it covers the reset vector.
- **The new fix sentences.** They start with "Remove", "Put" and "Call", all of which are in `Diagnostic.FixVerbs`.

## Findings

None.

Outside this branch: the "placed at overlapping addresses" error in the first case above is reported at `main.tern:1:1`, not at either routine's declaration. It was there before this branch. If it is worth fixing, it belongs to a later milestone.

## Done when

The roadmap item: "Questions 157-169 (decision 2026-11-14): three changes from the build, before merging ... Each error gets a test."

- [x] An error for two modules in one bank implementing the same interface (spec 8.5). Test: `NesBankTests.Two_modules_in_one_bank_cannot_implement_the_same_interface`.
- [x] An error for an interrupt vector into a bank area (spec 12.2). Test: `NesBankTests.An_interrupt_vector_into_a_bank_is_an_error`, for NMI and IRQ.
- [x] A `program` line on the NES is a plain error, not "not supported yet" (spec 12.2). Test: `NesTests.Cartridge_line_errors`, for `nrom` and `uxrom`.
- [x] M27's own "done when" (the scrolling example in Mesen with code in more than one bank) was met and reviewed in `m27.md`. This branch does not touch the examples, and `RepositoryWarningTests` still builds them.

## Re-review

Branch `m27-followup` at `ccf6737` (`main` merged in at `b4af13d`, status updated in `ccf6737`).

**Verdict: ready to merge.** The first review had no findings, so there was nothing to fix. Since then the branch has only merged `main`, which brought in this review file, and updated `docs/status.md`. The status now gives the verdict and lists the error position at `main.tern:1:1`, which predates this branch, under known limitations. That is the right place for it. No code or tests changed.

`dotnet test` (on `ccf6737`): 3720 compiler tests and 36 CPU tests pass.
