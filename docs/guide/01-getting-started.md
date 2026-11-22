# 1. Getting started

[Guide index](README.md)

## 1.1 Building and compiling

Build once, then compile a program:

```
dotnet build
dotnet run --project src/Tern.Cli -- --target c64 examples/c64/hello.tern
dotnet run --project src/Tern.Cli -- --target c64 game.tern -o out/game.prg
```

In this guide, `tern` is short for `dotnet run --project src/Tern.Cli --`.

**A `tern` command of your own.** To call the compiler as `tern` from anywhere, publish it as one file that needs no .NET installed, then put the folder on your `PATH`:

```
dotnet publish src/Tern.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist/win-x64
dotnet publish src/Tern.Cli -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o dist/linux-x64
dotnet publish src/Tern.Cli -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o dist/osx-arm64
```

Each folder holds `tern.exe` (Windows) or `tern` (Linux, macOS) and the `lib/` folder with the standard library, which the compiler finds next to itself: copy them together. Any of the three can be built on any of the systems; on Linux and macOS, `chmod +x tern` after copying from another system. The file is about 70 MB, since it carries the .NET runtime. Do not add `-p:PublishTrimmed=true`: the trimmed file is smaller, but `--json` fails in it. `dist/` is not part of the repository. A release package with the executable, the library and what else it needs is planned for later (roadmap, "Not scheduled").

## 1.2 Targets

Every command names the machine it builds for, with `--target`:

| Target | Machine | Program file | Labels | Overlays and binaries |
|---|---|---|---|---|
| `c64` | Commodore 64 | `game.prg`, loading at `$0801` with a `SYS` line | `game.vs`: in the VICE monitor, `ll "game.vs"` | `.prg` |
| `atari` | Atari 8-bit with 48 KB or more (400/800, XL, XE) | `game.xex`, starting at `$2000` | `game.lbl`, which Altirra loads by itself with `game.xex` | `.xex` |
| `apple2` | Apple II with 64 KB under ProDOS 8 (II+ with a language card, IIe, IIc, IIgs) | `game.system`, a ProDOS system file at `$2000` | `game.sym`: in AppleWin's debugger, `symuser load "<path>"` | `.bin` (BIN files with the load address as aux type) |
| `nes` | NES | `game.nes`, an iNES cartridge image | `game.mlb`, which Mesen loads by itself | none: ROM banks are in the image ([NES](09-nes.md)) |

The target decides which library modules exist (`import io` is the C64's `io` in a C64 build and the Apple II's in an Apple II build), the default encoding of strings, and the memory map. Each machine has its own page: [C64](06-c64.md), [Atari](07-atari.md), [Apple II](08-apple2.md), [NES](09-nes.md).

## 1.3 What the compiler writes

For `game.tern` the compiler writes, next to the source (or the `-o` path), the program and label files above, and:

| File | Contents |
|---|---|
| `game.lst` | Listing: every source line with its addresses, bytes and cycle counts |
| `game.lines` | The bytes, cycles and addresses of each source line, for tools |
| `game_api.tern` | Only for a binary without `main`, or a program with an export table ([Memory](03-memory.md#33-binaries-without-main)): the module others import |
| `level1.prg`, ... | One file per overlay module ([Overlays](03-memory.md#35-overlays)), named after the module, with the target's extension |

It also prints a report: where code and variables went, the zero-page map, and the functions whose memory is reachable from two roots (main and an interrupt handler).

The listing is the best way to learn what Tern generates. From the first C64 example:

```
                                ;    7: c64.vic.border = c64.BLACK
$0814  A9 00     lda #$00              ; 2
$0816  8D 20 D0  sta c64.vic+32        ; 4
```

## 1.4 Running a program

```
x64sc -autostart game.prg                                 # C64: VICE
Altirra64 game.xex                                         # Atari: Altirra
pwsh tests/tools/apple2-disk.ps1 game.system game.po       # Apple II: a ProDOS disk, then boot game.po in AppleWin
Mesen game.nes                                             # NES: Mesen
```

The compiler makes no disk images: `apple2-disk.ps1` copies the ProDOS boot disk and puts the program on it as the first system file, and any `.bin` overlays after it with their load address (`level1.bin@$6000`).

`tern run --target c64 game.tern` runs a program in the compiler's built-in emulator, with stand-ins for a few ROM routines on each machine, and `tern bench` compares two ways of writing the same code ([12. Running and measuring](12-run-and-bench.md)).

## 1.5 Errors and warnings

Errors look like `game.tern:12:5: error: ...` and say how to fix the problem. One compile reports every error it finds, in file and line order (`--max-errors 5` stops after five); errors of the later stages, such as memory placement, come only once the others are fixed. `tern --json --target c64 game.tern` gives the same result as JSON, for editors and coding agents: each diagnostic has its file, line, column, message, the fix as `suggestion`, and `calledFrom` for an error inside compile-time code.

The compiler also warns about code that does nothing: an unused local, an unused private or main-file declaration, an unused import, and a statement that is never reached (`game.tern:9:5: warning: 'temp' is never used. ...`). Code that runs is never warned about, however unusual. Warnings do not stop the build. To keep something unused on purpose, start its name with `_` (`fn _debug_dump()`); for a loop counter you do not read, write `for _ in ...`, which is never slower than a named counter. `--no-warnings` turns them off, `--warnings-as-errors` makes them errors (for CI); both work with `tern run` too, and `--json` reports them with `"severity": "warning"`.

## 1.6 Where things are

The standard library sits in `lib/` next to the compiler (source in `src/lib/`), with each machine's modules in `lib/c64/`, `lib/atari/`, `lib/apple2/` and `lib/nes/`. Your own modules go next to your main file. Examples are in `examples/c64/`, `examples/atari/`, `examples/apple2/` and `examples/nes/`.
