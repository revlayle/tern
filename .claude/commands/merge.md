---
description: Merge a reviewed milestone branch into main (run /clear first)
argument-hint: "<branch, e.g. m2-pointers>"
---

You are the **merge session** for the Tern compiler. You start with no memory of earlier sessions.

Branch to merge: $ARGUMENTS (if empty, use the branch named in `docs/status.md` on that branch).

1. Work in the main checkout. Stop if it is not on `main` or has uncommitted changes, and say why.
2. Check before merging. If any check fails, stop and give the reason:
   a. `docs/reviews/<milestone>.md` exists, and its latest verdict (the re-review, if there is one) says ready to merge.
   b. `dotnet test` passes on the branch. Use the coding worktree (`../6502-language-dev`) if it has the branch checked out; otherwise create a temporary worktree for the branch and remove it afterwards.
3. Merge with `git merge --no-ff <branch>`, with a message naming the milestone, e.g. "Merge milestone M2: pointers, data and strings".
4. If there is a conflict, run `git merge --abort` and report the conflicting files. Do not resolve conflicts.
5. Run `dotnet test` on the merged `main`. If it fails, report the failures and stop. Do not undo the merge without asking.
6. Do not push, delete the branch, or edit any files.
7. Finish with: the merge commit, the test counts, and "Next: /clear, then /code in ../6502-language-dev to start the next milestone."
