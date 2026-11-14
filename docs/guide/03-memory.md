# 3. Memory

[Guide index](README.md) · previous: [2. The language](02-language.md) · next: [4. Compile-time code](04-compile-time.md)

## 3.1 The memory map

Each target has a default memory map:

| Target | Code starts at | Variables up to | Free zero page |
|---|---|---|---|
| `c64` | `$0801` (BASIC line) | `$9FFF` | `$02-$8F`, `$FB-$FE` |
| `atari` | `$2000` | `$BC1F`, below the OS's screen | `$80-$FF` |
| `apple2` | `$2000` | `$BEFF`, below ProDOS's global page | `$50-$FF`, then `$00-$1F` |
| `nes` | `$8000` (NROM) or `$C000` (UxROM's fixed bank), in ROM | `$0200-$07FF` (RAM) | `$00-$FF` |

Code, `data` and variables go after each other from the code start; the compiler chooses which variables go in zero page, those used most first. Functions that never run at the same time share their variables' memory ([10.11](10-behavior.md#1011-functions-that-never-run-together-share-memory)). The Apple II's default program area covers hi-res page 1; a program that draws hi-res graphics moves itself out of the way ([Apple II](08-apple2.md)).

## 3.2 The memory block

A `memory` block in the main file changes the map:

```
memory {
    $2000..=$2FFF  reserved        // VIC graphics: the compiler places nothing here
    $C000..=$CFFF  region buffers  // used only on request: var map: [u8; 4096] @ buffers
}

data ball: [u8; 63] @ $2000 = [ ... ]   // loaded with the program, at that address
```

A `reserved` range is also the plain way to keep memory for your own use. Name its start once and point into it:

```
const MAP: u16 = $4000              // constants may stand above the block (or anywhere below)

memory {
    MAP..=MAP + $07FF  reserved     // 2 KB, never touched by the compiler or startup
}

var p: ptr u8 = (MAP + 256) as ptr u8
p[44] = 1                           // byte 300 of the map: sta (p),y
```

The range is not cleared at startup.

| Kind | Meaning |
|---|---|
| `program` | Code, `data` and variables; replaces the platform's areas |
| `zeropage` | Zero-page bytes the compiler may use; replaces the platform's |
| `reserved` | The compiler places nothing here, except what you put there with `@` |
| `region name` | Placed only on request: `var map: [u8; 2048] @ buffers`, or at an offset, `@ buffers + $100` |
| `volatile` | Memory that changes behind the program, in addition to the platform's I/O areas |
| `overlay name` | An overlay area ([3.5](#35-overlays)) |
| `bank name`, `chr name` | NES only: a bank area and the tile ROM ([NES](09-nes.md)) |

A function is never split across areas. `data` with a placement (an address, a region, an offset) is always kept, even when no code names it, since hardware may read it there (sprites, tiles, vectors).

## 3.3 Binaries without `main`

A main file with `export` and no `main` builds a **binary** for another program to call, such as a music player. It starts with a jump table (`setup`, then the exports in order), and the compiler also writes `name_api.tern` for the host to import.

`player.tern`, built on its own:

```
memory {
    $C000..=$CFFF  program
    $FB..=$FE      zeropage
}

export init, play

var frame: u8

fn init() { frame = 0 }
fn play() { frame += 1 }
```

The compiler writes `player.prg` (loading at `$C000`, no BASIC line) and `player_api.tern`:

```
const CODE_FIRST = $C000      // the program file: code, data and the gaps between
const CODE_LAST = $C016
const ZP_FIRST = $FB      // zero page used by the binary
const ZP_LAST = $FB

fn setup() @ $C000 keeps X, Y
fn init() @ $C003 keeps X, Y
fn play() @ $C006 keeps A, X, Y
```

A host reserves the binary's memory and calls it. Here it also carries the binary inside its own file:

```
import player_api

memory {
    player_api.CODE_FIRST - 2..=player_api.CODE_LAST  reserved
    player_api.ZP_FIRST..=player_api.ZP_LAST          reserved
}

// The .prg's two load-address bytes land just below the code.
data player @ player_api.CODE_FIRST - 2 = include_bytes("player.prg")

fn main() {
    player_api.setup()                   // once, before any other entry
    player_api.init()
    while true { player_api.play() }
}
```

An export table, like `data` at a fixed address, is part of the program file, so the file covers everything between the code and the table: `export add @ $C000` in a program that starts at `$0801` writes a 47 KB `.prg`. Put the table next to the code, or in its own small `program` area.

A binary's entries share their memory, so the host must not call one from an interrupt while another runs ([10.10](10-behavior.md#1010-binaries-plug-ins-and-exports)).

## 3.4 Plug-ins

A game that loads levels built separately, perhaps later or by someone else, describes them in a hand-written **interface module**: routines at fixed addresses, `volatile` fixed variables for data every level provides, and constants for the slot. The game imports it and calls the routines; each level is a binary with `export level_api`, and the compiler checks the level against the module: a function for every routine, with the same registers, return and `keeps`, and a `data` for every `volatile` variable. The interface must declare `setup` at its first address (the level's startup code goes there), routines at least 3 bytes apart, and no fixed inputs. A level implements one interface; a game may import as many as it has slots.

The interface, `level_api.tern`:

```
struct LevelHeader {
    border: u8          // the border color while the level runs
    goal: u8            // how many updates the level takes
}

const SLOT_FIRST = $6000
const SLOT_LAST = $6FFF
const ZP_FIRST = $FB    // zero page a level may use: the game leaves it alone
const ZP_LAST = $FE
const HEADER = $6009

fn setup() @ $6000                              // first, after loading
fn enter() @ $6003                              // the level starts
fn update() -> bool using carry @ $6006         // one step; true when the level is finished

volatile var header: LevelHeader @ HEADER       // every level's data at a fixed address
```

A level, `forest.tern`, built on its own. Its functions may be in any order: each goes where `level_api` puts it.

```
import game_api
import level_api

memory {
    level_api.SLOT_FIRST..=level_api.SLOT_LAST  program
    level_api.ZP_FIRST..=level_api.ZP_LAST      zeropage
}

export level_api

data header: level_api.LevelHeader @ level_api.HEADER = level_api.LevelHeader { border = 5, goal = 3 }
data NAME = "FOREST "
var steps: u8

fn enter() {
    for i in 0..len(NAME) { game_api.show(NAME[i]) }
}

fn update() -> bool using carry {
    steps += 1
    game_api.add_score(10)
    return steps == header.goal
}
```

The other direction: `export add_score, show @ $0900` in a program with `main` puts a jump table at `$0900` and writes `game_api.tern`, which plug-ins import to call the game. The game reserves the slot, offers two services to its levels, and plays each level it loads:

```
import c64
import level_api

memory {
    $0801..=$5FFF                               program
    level_api.SLOT_FIRST..=level_api.SLOT_LAST  reserved
}

export add_score, show @ $0900

var score: u16

fn add_score(points: u8 using A) { score += points as u16 }
fn show(ch: u8 using A) { c64.chrout(ch) }

fn play(name: ptr u8, n: u8) {
    if c64.load(name, n, 8) { ... }          // load the level into the slot
    level_api.setup()
    c64.vic.border = level_api.header.border
    level_api.enter()
    while !level_api.update() { }
}
```

Building the game writes `game_api.tern`, so build it before the levels:

```
fn add_score(points: u8 using A) @ $0900 keeps X, Y
fn show(ch: u8 using A) @ $0903 keeps X, Y
```

The full example, with a second level and the commands for a disk image, is in `examples/c64/plugins/`.

## 3.5 Overlays

An overlay is a module that is loaded from disk into a shared area when the program needs it, such as one level of a game. Overlays in the same area replace each other. The main file declares the area and places modules in it with `@`:

```
memory {
    $0801..=$5FFF  program
    $6000..=$7FFF  overlay levels
}

import level1 @ levels
import level2 @ levels
```

- **One build.** The whole program is compiled together, but each overlay module goes into its own file (`level1.prg`, `level1.xex`, `level1.bin`), loading at the start of its area. The report shows each area and how full it is.
- **Variables come with the file.** An overlay's global variables are stored in its file with their initial values, so every load resets them. They are never in zero page; an overlay that needs a zero-page pointer uses a local variable.
- **Loading** is a library call that returns `true` if loading failed. C64: `c64.load(name, len, device)`, a PETSCII name (`"LEVEL1"`). Atari: `io.load_xex(name, len)`, a name with its device (`"D1:LEVEL1.XEX"`). Apple II: `io.load_bin(name, len)`, a ProDOS path (`"LEVEL1.BIN"`), which loads the file at the address in its aux type. On the NES, ROM banks take the place of overlays ([9.5](09-nes.md#95-rom-banks)).
- **Calls:** resident code calls into an overlay with a plain `jsr`, and an overlay may call resident code and overlays in other areas. Two overlays of the same area cannot call each other or use each other's variables (they are never loaded together); that is a compile error.
- Making sure the right overlay is loaded before calling it is your job ([10.9](10-behavior.md#109-overlays-and-banks)).
- A fixed-address variable over an overlay's file is an error unless it is marked `volatile`, which reads whatever overlay is loaded (memory an overlay loads on purpose, e.g. a level header).

`level1.tern`, an overlay module (`level2.tern` is the same with `'B'` on the bottom half):

```
import mem

var lives: u8 = 3                  // set again by every load of the file

fn draw() {
    lives += 1
    mem.fill($0400 as ptr u8, screen'A', 480)
}
```

The main program:

```
import c64
import level1 @ levels
import level2 @ levels

memory {
    $0801..=$5FFF  program
    $6000..=$7FFF  overlay levels
}

data NAME1 = "LEVEL1"
data NAME2 = "LEVEL2"

fn load(name: ptr u8, n: u8) {
    if c64.load(name, n, 8) {
        c64.vic.border = c64.RED        // loading failed
        while true { }
    }
}

fn main() {
    load(addr(NAME1), len(NAME1))
    level1.draw()
    load(addr(NAME2), len(NAME2))
    level2.draw()
}
```

Compiling writes `levels.prg`, `level1.prg` and `level2.prg`. Put them on a disk image to run it; the commands are in the header of `examples/c64/levels.tern`. `examples/atari/files.tern` and `examples/apple2/files.tern` load an overlay on those machines.

## 3.6 Overlay interfaces

When several overlays play the same part, such as levels, resident code can call whichever is loaded without knowing which. Write an interface module with routines at fixed addresses, as for plug-ins ([3.4](#34-plug-ins)), and add `implements` to each overlay's import:

```
import level_api                              // fn init(n: u8 using A) @ $6000, fn update() @ $6003, ...
import level1 @ levels implements level_api
import level2 @ levels implements level_api

level_api.update()                            // jsr $6003: the loaded level's update
```

- The compiler puts a `jmp` at each routine's address to the module's function of the same name, and checks the module as it checks a plug-in: a function for every routine, with the same registers, return and `keeps`, and a `data` for every `volatile` variable.
- The main file must import the interface module itself.
- A call through the interface counts as a call to every level's function, so their memory is shared with other code as usual.
- Inside a level, use its own `data` name (`header`), not the interface's (`level_api.header`); the compiler says so.
- `implements` works the same way for NES ROM banks, with each implementing module in its own bank.
- `examples/c64/stages.tern` loads two stages in turn and calls them through `stage_api`; `tern run` can run it ([11.11](11-fast.md#1111-measuring)).
