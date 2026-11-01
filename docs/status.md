# Status

Owned by the coding session. Updated at the end of every coding session.

## Current

- **Milestone:** M2 (pointers, data and strings), not started.
- **Branch:** none yet.
- **Next step:** start M2 per `docs/roadmap.md`.

## Done

- **M1 core compiler**, merged to `main` (2026-11-01). 257 tests pass (34 CPU, 223 compiler). `examples/border.tern` verified in VICE.

## Known limitations and technical debt

Not bugs; things to improve, mostly in M7 (optimizer).

- Register parameters are stored to memory on function entry, then reloaded on each use.
- An unreachable `rts` follows an endless `while true` at the end of a function.
- After the step of a memory loop counter, the counter is reloaded with `lda` right after it was stored.
- A `for` bound that is a global variable is copied to a temporary (only locals and parameters are used directly, since calls could change a global).
- Loop variables live in memory unless `using X`/`using Y` is written.
- Evaluation order: in `a + b` where `b` needs a temporary, `b` is evaluated before `a`. Matters only if both call functions with side effects.
- Only one zero-page range per platform is used (C64: `$02-$8F`; `$FB-$FE` is unused).

## Questions for the spec session

None open.

## Provisional choices made while coding

None open. (M1's provisional choices were confirmed: locals need initial values, `using` loops cannot contain calls, register parameters are stored on entry, signed loop variables are not supported yet.)
