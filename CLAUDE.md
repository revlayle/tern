# Tern: a programming language for 6502 processors

Tern (working name) is a small, readable language that compiles to efficient 6502 machine code. The compiler is written in C# and has its own assembler and 6502 emulator. The first target is the Commodore 64; Atari 8-bit, Apple II and NES follow, in that order, for the first release. The Commander X16 and the Commodore 128 (native mode) come after the release.

## Key documents

| File | What it is | Owner |
|---|---|---|
| `docs/spec-v0.md` | The language specification. The source of truth for language behavior. | spec session |
| `docs/decisions.md` | Design decisions and their reasons. Read before proposing a language change. | spec session |
| `docs/roadmap.md` | Milestones: scope, spec sections, "done when" criteria. | spec session |
| `docs/status.md` | Current state, handoff notes, known limitations, questions for the spec session. | coding session |
| `docs/reviews/` | Review findings, one file per milestone. | review session |
| `docs/guide.md` | User guide: how to use the language and compiler, with examples. | spec session |
| `docs/workflow.md` | How the spec, coding and review sessions work together. | spec session |

Each session edits only the files it owns (see `docs/workflow.md`). Start a session with `/spec`, `/code`, `/review` or `/merge`.

## Layout

```
src/Tern.Cpu/            instruction set table, encoder, cycle-exact 6502 emulator
src/Tern.Compiler/
  Syntax/                lexer, AST, parser
  Semantics/             symbols, types, checker (names, types, constant folding, call graph)
  CompileTime/           interpreter for const fn and code generators (values, emit)
  Backend/               code generator, allocator (zero page, shared frames), assembler
  Tools/                 agent tools: --json diagnostics, .lines table, tern run, tern bench
  Platform.cs            target machine descriptions (C64, Atari)
  TernCompiler.cs        driver: source -> .prg, listing, labels, report
src/Tern.Cli/            `tern` command-line tool
src/lib/                 standard library source (.tern); platform modules in src/lib/<platform>/; copied to lib/ next to the compiler
tests/Tern.Cpu.Tests/    CPU tests, including Klaus Dormann's 6502 functional test
tests/Tern.Compiler.Tests/  end-to-end tests: compile, run in the emulator, check memory
examples/c64/            example .tern programs for the C64
examples/atari/          example .tern programs for the Atari
.claude/commands/        session commands: /spec, /code, /review, /merge
.claude/skills/tern/     the Tern skill for coding agents (checked by SkillTests)
.claude-plugin/          plugin.json and marketplace.json: the repository as a Claude Code plugin
```

## Commands

```
dotnet build
dotnet test                                              # all tests, about 2 seconds
dotnet run --project src/Tern.Cli -- --target c64 examples/c64/border.tern  # writes .prg, .lst, .vs next to the source
```

To check a program on a real emulated C64 (VICE is installed at `C:\Vice\bin`):

```
C:\Vice\bin\x64sc.exe -default -warp -autostart file.prg -limitcycles 8000000 -exitscreenshot shot.png
```

To check a program on an emulated Atari 8-bit, use Altirra (4.40, installed at `C:\Altirra`). `C:\Altirra\Altirra64.exe file.xex` boots a program; `Altirra64.exe /?` lists the switches (machine, memory size, OS, debugger commands). Use it in portable mode, with settings in `C:\Altirra\Altirra.ini` rather than the registry (the `.ini` file is present). Name the machine, memory size and OS on the command line rather than relying on the settings. Several copies starting at once can conflict over the `.ini` file, so run Altirra checks one at a time. Check on an 800 with 48 KB and on an 800XL (spec 12.2). No OS ROM images are installed, so checks use AltirraOS (`/kernel:default`); once the OS-B and XL OS images are registered in Altirra (Configure System, Firmware), check with `/kernel:osb` on the 800 and the XL OS on the 800XL. Altirra cannot run without a window, so `tests/tools/altirra-shot.ps1` starts it, copies its window to a PNG after the given seconds and closes it:

```
pwsh tests/tools/altirra-shot.ps1 file.xex shot.png 8 /skipsetup /hardware:800 /kernel:default /memsize:48K /nobasic
pwsh tests/tools/altirra-shot.ps1 file.xex shot.png 8 /skipsetup /hardware:800xl /kernel:default /memsize:64K /nobasic
```

To check a program on an emulated Apple II, use AppleWin (1.32.1.0, installed at `C:\AppleWin`; its ROMs are built in). Run it in portable mode with `-conf C:\AppleWin\AppleWin.ini`, so settings stay out of the registry, and name the machine on the command line. Check on an enhanced IIe (`-model apple2ee`) and on a II+ with a language card (`-model apple2p -s0 languagecard`; without `-s0` the `.ini` leaves slot 0 empty and ProDOS stops with "REQUIRES 64K"). `-s7 empty -d1 disk.po` boots the disk in slot 6, drive 1; `-m` turns sound off; `-no-printscreen-dlg` suppresses a message box. `C:\AppleWin\AppleWin.chm` documents the switches (CommandLine page). The ProDOS 2.4.3 boot disk is `C:\AppleWin\ProDOS_2_4_3.po` (shipped with AppleWin); booted as it is, it starts the Bitsy Bye menu, since ProDOS runs the first `.SYSTEM` file in the volume directory, so a test disk is a copy that holds `PRODOS` and the program's `.system` file as the first system file. CiderPress II's command-line tool, `C:\Program Files\CiderPress\cp2.exe`, edits ProDOS disk images (`Manual-cp2.md` next to it). AppleWin needs a window, like Altirra; the window-capture approach of `altirra-shot.ps1` works with it:

```
C:\AppleWin\AppleWin-x64.exe -conf C:\AppleWin\AppleWin.ini -model apple2ee -s7 empty -d1 disk.po -no-printscreen-dlg -m
C:\AppleWin\AppleWin-x64.exe -conf C:\AppleWin\AppleWin.ini -model apple2p -s0 languagecard -s7 empty -d1 disk.po -no-printscreen-dlg -m
```

To check a program on an emulated NES, use Mesen (2.1.1, installed at `C:\Mesen`, in portable mode: its settings are `C:\Mesen\settings.json`; it also unpacks its native libraries into `Documents\Mesen2` on every start, which holds no settings). The NES needs no ROM images. Mesen's test runner runs a `.nes` file with a Lua script and no window; the script stops the run with `emu.stop(code)`, which becomes Mesen's exit code, and can save a screenshot with `emu.takeScreenshot()` and `io.open` (scripts' file access, `AllowIoOsAccess`, is on in the settings). A script that fails never calls `emu.stop`, so run it under a timeout:

```
timeout 60 C:\Mesen\Mesen.exe --testRunner game.nes check.lua
```

A script that saves a screenshot after 30 frames:

```lua
local n = 0
emu.addEventCallback(function()
  n = n + 1
  if n == 30 then
    local f = io.open([[C:\path\shot.png]], "wb")
    f:write(emu.takeScreenshot()); f:close()
    emu.stop(0)
  end
end, emu.eventType.endFrame)
```

`tests/tools/mesen-shot.ps1` wraps this: it saves a screenshot after each given frame count (`shot-<frame>.png` when there are several) and stops after 60 seconds:

```
pwsh tests/tools/mesen-shot.ps1 game.nes shot.png 60 120
```

## Design rules

These come from the spec and `docs/decisions.md`. Apply them to every change.

1. **Cost is visible.** Operators compile to a short, fixed instruction sequence. Anything that needs a loop or a runtime routine is a library function call, never an operator.
2. **Restriction is acceptable.** If a feature cannot compile to efficient code, leave it out.
3. **One meaning per symbol and keyword.** Exceptions: unary `+`/`-`, and assembly blocks.
4. **Predictable code shapes.** Spec section 11 lists the intended output for each construct. Generated code must match it or be shorter.
5. **No recursion, no stack frames.** Every variable has a fixed address; functions that never run at the same time share memory.

## Coding conventions

- Every language feature gets end-to-end tests in `tests/Tern.Compiler.Tests` that compile a program, run it in the emulator (`Run.Program`) and check memory. Test edge values (0, 255, 256, -128, wrap-around) and compare against results computed in C#.
- Every new compile error gets a test, in the feature's test file (`AsmTests`, `PointerTests`, ...), or in `ErrorTests.cs` for errors with no feature file (lexer, parser, general type errors). Error messages say what is wrong, then how to fix it in a sentence that starts with a verb from `Diagnostic.FixVerbs` (Use, Write, Remove, ...); `tern --json` reports that sentence as the suggestion.
- When code shape matters, check the listing (`result.Listing`) in a test.
- A spec keyword that is not implemented yet must give "'x' is not supported yet", not a confusing parse error.
- No new NuGet dependencies without asking.
- Keep source files under about 1,000 lines. When a class grows past that, split it into partial class files by area, named `Class.Area.cs` (e.g. `Checker.Asm.cs`).
- `BenchmarkTests` fails on any change in bytes or cycles, better or worse. Rewrite the baseline (`TERN_UPDATE_BENCHMARKS=1 dotnet test`) in the same commit as the change that caused it, and give the old and new totals in the commit message.
- `SkillTests` fails when the skill's examples or tool outputs change (addresses, cycles). Rewrite them with `TERN_UPDATE_SKILL=1 dotnet test --filter SkillTests` in the same commit as the change that moved them, read the diff, and fix any prose that no longer matches. Listing excerpts (```` ```lst ````) are updated by hand.
- Commit messages: a short summary line, then what changed and why.
