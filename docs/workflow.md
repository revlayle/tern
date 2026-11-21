# Development workflow

Work happens in three kinds of sessions, each in its own terminal. They communicate through files in the repository, not through conversation history, so any session can be closed and restarted.

| Session | Started with | Works on | Edits |
|---|---|---|---|
| **Spec** | `/spec` | Language design discussions with you | `docs/spec-v0.md`, `docs/decisions.md`, `docs/roadmap.md`, `docs/guide/` (and the `docs/guide.md` pointer), `docs/workflow.md`, `CLAUDE.md`, `.claude/commands/`, `.gitignore` |
| **Coding** | `/code` | One milestone (or part of one) per session | `src/`, `tests/`, `examples/`, `validation/`, `.claude/skills/`, `.claude-plugin/`, `docs/status.md` |
| **Review** | `/review` | Reviewing a milestone branch | `docs/reviews/` |
| **Merge** | `/merge` | Merging a branch whose review is clean | nothing (creates the merge commit on `main`) |

**Starting a session:** run `/clear` (or start a new `claude`), then the command. The commands cannot clear the session themselves. Clear whenever a session gets long: everything that matters is in the files, so nothing is lost.

A session never edits files owned by another. If a coding session finds a gap or a contradiction in the spec, it writes it under "Questions for the spec session" in `docs/status.md` and makes the smallest reasonable choice, marked as provisional. The spec session resolves it later.

## Git setup

The spec and review sessions use the main checkout. Coding happens in a separate checkout (a git worktree), so switching branches there never changes the files the other sessions are looking at.

One-time setup, from the main checkout:

```
git worktree add ../6502-language-dev main --detach
```

Start the coding terminal in `C:\game-dev\6502-language-dev`.

## The cycle for one milestone

1. **Spec session** makes sure the milestone's spec sections are settled, and commits on `main`.
2. **Coding session** (in the worktree):
   - creates the milestone branch from the latest `main`: `git switch -c m2-pointers main`
   - plans, implements and tests in small steps, committing as it goes
   - updates `docs/status.md` with what was done, what is left, and anything the next session needs to know
   - a large milestone can take several coding sessions; each one picks up from `docs/status.md`
3. **Review session** (main checkout) reviews the branch against the spec and the roadmap's "done when" list, runs the tests, and writes `docs/reviews/m2.md`.
4. **Coding session** fixes the findings marked "must fix" and notes the fixes in the review file's checklist (by commit message; it does not edit the review file).
5. **Merge session**: when the review is clean, run `/merge m2-pointers` in the main checkout. It checks the review verdict and the tests, merges, and tests the result. Alternatively, merge by hand:
   ```
   git merge --no-ff m2-pointers
   ```
   Then the coding worktree starts the next milestone from the new `main`.

If `main` gets new spec commits while a milestone is in progress, the coding session merges `main` into its branch at the start of its next session.

## Session handoff rules

- Each coding session ends with: tests passing, work committed, `docs/status.md` updated. No uncommitted work is left behind.
- `docs/status.md` must let a fresh session continue without asking you anything: current milestone, next step, open problems.
- The spec session records every decision in `docs/decisions.md` with the date and the reason, and updates the spec and roadmap in the same commit.
