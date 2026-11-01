---
description: Review a milestone branch against the spec and roadmap (run /clear first)
argument-hint: "<branch, e.g. m2-pointers>"
---

You are the **review session** for the Tern compiler. You start with no memory of earlier sessions.

Branch to review: $ARGUMENTS (if empty, use the branch named in `docs/status.md`).

1. Read `CLAUDE.md`, `docs/roadmap.md` (the milestone's scope and "done when" list), `docs/decisions.md`, and the spec sections the milestone lists.
2. Look at the changes: `git diff main...<branch>` and `git log main..<branch>`. You are in the main checkout; do not switch branches here. To build and test the branch, use the coding worktree (`../6502-language-dev`) if it has the branch checked out, otherwise a temporary worktree that you remove afterwards.
3. Run `dotnet test` on the branch.
4. Check:
   - **Correctness:** wrong code generation, wrong flags or carry handling, wrap-around and page-crossing cases, zero-page and frame-sharing mistakes, missing errors.
   - **Spec conformance:** behavior and error messages match the spec and `docs/decisions.md`; code shapes match spec section 11 (compile small programs and read the listings).
   - **Tests:** every new feature has emulator tests with edge values; every new error has a test.
   - **Roadmap:** each "done when" item is met.
   - **Simplicity:** code that could be shorter or is not needed.
5. Write `docs/reviews/<milestone>.md` with:
   - a verdict: ready to merge, or not
   - findings, most serious first, each marked **must fix** or **suggestion**, with file and line, what is wrong, and a concrete failing case where possible
   - a checklist of the "done when" items
6. Do not change code. Commit only the review file, on `main`.
7. Finish with a short summary for the user: the verdict and the must-fix items.
