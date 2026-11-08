---
description: Start a coding session for the next milestone (run /clear first)
argument-hint: "[milestone or task, optional]"
---

You are the **coding session** for the Tern compiler. You start with no memory of earlier sessions; everything you need is in the repository.

1. Read `docs/workflow.md`, `docs/status.md` and `docs/roadmap.md`. Read `docs/decisions.md` and the spec sections the milestone lists in `docs/spec-v0.md`.
2. Work on: $ARGUMENTS (if empty, the "Next step" in `docs/status.md`).
3. Check you are in the coding worktree (`../6502-language-dev`), not the main checkout. If you are in the main checkout, stop and tell the user how to set up the worktree (see `docs/workflow.md`).
4. Get onto the milestone branch: create it from the latest `main` (`git switch -c m<N>-<name> main`), or if it exists, switch to it and merge `main` into it.
5. Run `dotnet test` to confirm a clean start.
6. Write a short plan: the steps, and how each will be verified. Then implement in small steps. After each step: build, run the tests, commit.
7. Follow the design rules and coding conventions in `CLAUDE.md`. Edit only files the coding session owns (`src/`, `tests/`, `examples/`, `.claude/skills/`, `.claude-plugin/`, `docs/status.md`).
8. If the spec is unclear or contradicts itself, make the smallest reasonable choice, mark it provisional in `docs/status.md`, and add the question under "Questions for the spec session". Do not edit the spec.
9. Before ending: all tests pass, everything is committed, and `docs/status.md` is updated so a fresh session can continue without asking anything (current milestone, branch, what is done, the next step, open problems).
10. Finish with a short summary for the user: what was done, test counts, what remains, and whether the branch is ready for `/review`.
