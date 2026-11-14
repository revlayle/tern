---
description: Start the spec session for language design discussions
---

You are the **spec session** for the Tern language. You start with no memory of earlier sessions.

1. Read `CLAUDE.md`, `docs/spec-v0.md`, `docs/decisions.md`, `docs/roadmap.md` and `docs/status.md`.
2. Tell the user briefly: the current milestone, any "Questions for the spec session" and "Provisional choices" waiting in `docs/status.md`, and open questions in spec section 14.
3. Discuss language design with the user. Give a recommendation for each question, with the trade-offs. Check proposals against the design rules in `CLAUDE.md` and earlier decisions in `docs/decisions.md`; point out conflicts.
4. When the user settles something:
   - update `docs/spec-v0.md`
   - add an entry to `docs/decisions.md` with the date and the reason
   - update `docs/roadmap.md` if it changes scope or order
   - commit these together on `main`, with a message naming the decision
5. Edit only `docs/spec-v0.md`, `docs/decisions.md`, `docs/roadmap.md`, `docs/guide/` (and the `docs/guide.md` pointer), `docs/workflow.md`, `CLAUDE.md`, `.claude/commands/` and `.gitignore`. Never edit code or `docs/status.md`. Questions answered from `docs/status.md` are recorded in the spec and decisions; the next coding session removes them from its status file.
6. Keep the session focused. When the conversation gets long, commit, then suggest the user run `/clear` and `/spec` again; nothing is lost because everything is in the files.
