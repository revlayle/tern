# A whole program: a sprite moved by the joystick

A ship sprite that the joystick in port 2 moves around the screen, checked and measured without a
C64. The pattern: keep the logic in a module that takes its inputs as parameters, run it from a
small check program in the emulator, and keep the hardware in the main program.

## 1. The logic, without hardware

The joystick in port 2 is bits 0-4 of `c64.cia1.port_a` (`$DC00`): up, down, left, right, fire. A
bit is 0 while pressed. A sprite's x position has 9 bits (0-511), so `x` is a `u16`; the visible
area is about x 24 to 320 and y 50 to 229.

```tern
// ship.tern
const UP: u8 = 1
const DOWN: u8 = 2
const LEFT: u8 = 4
const RIGHT: u8 = 8
const FIRE: u8 = 16

const MIN_X: u16 = 24
const MAX_X: u16 = 320
const MIN_Y: u8 = 50
const MAX_Y: u8 = 229

var x: u16 = 160
var y: u8 = 140
var shots: u8

// One frame of movement from the joystick bits (0 = pressed).
fn move(joy: u8) {
    if joy & LEFT == 0 && x > MIN_X { x -= 1 }
    if joy & RIGHT == 0 && x < MAX_X { x += 1 }
    if joy & UP == 0 && y > MIN_Y { y -= 1 }
    if joy & DOWN == 0 && y < MAX_Y { y += 1 }
    if joy & FIRE == 0 { shots += 1 }
}
```

`ship.tern` has no `main`: it is a module, imported by the programs below.

## 2. Check it in the emulator

A check program calls `move` with joystick values a C64 would give and leaves the results in
memory. Right for 200 frames must stop at 320 (past 255, so the high byte is used), then up and left
together for 100 frames:

```tern
// check.tern
import ship

const NONE: u8 = 0b1111_1111

var after_right: u16
var after_up_left: u8

fn main() {
    for _ in 0..200 { ship.move(NONE ^ ship.RIGHT) }
    after_right = ship.x
    for _ in 0..100 { ship.move(NONE ^ ship.UP ^ ship.LEFT) }
    after_up_left = ship.y
    ship.move(NONE)
}
```

```console
$ tern run check.tern --dump after_right --dump after_up_left --dump ship.x --dump ship.shots
Main returned: 26414 cycles (26377 in main)
A=$10 X=$00 Y=$00 SP=$FF PC=$0827
after_right $0006 u16 = 320 ($0140)
after_up_left $0008 u8 = 50 ($32)
ship.x $0002 u16 = 220 ($00DC)
ship.shots $0005 u8 = 0 ($00)
```

The values agree with the rules: x stops at 320, y goes from 140 down 90 to the top limit 50, and x
moves left 100 from 320 to 220. No frame pressed fire.

How long does one call take? `tern bench` measures one function of a program:

```console
$ tern bench check.tern --fn ship.move
variant     program  ship.move bytes   calls     cycles  per call    zp  other
check.tern      174          83     301      20634        68     4      0
Memory (zp: zero page bytes, other: the rest; the measured function's variables and the program's globals):
  check.tern: after_right $0006 u16, after_up_left $0008 u8, ship.move.joy $000B u8
```

About 70 cycles a frame, of the about 19,700 in a PAL frame: there is no reason to write it in assembly.

## 3. The program

```tern
// game.tern
import c64
import ship

var shape: [u8; 63] @ $0340        // sprite block 13, free RAM below the program
var pointer0: u8 @ $07F8           // sprite 0's pointer, after the screen at $0400

data ship_shape: [u8; 63] = [
    $00, $18, $00,
    $00, $3C, $00,
    $00, $7E, $00,
    $00, $FF, $00,
    $01, $FF, $80,
    $03, $FF, $C0,
    $07, $FF, $E0,
    $0F, $FF, $F0,
    $1F, $FF, $F8,
    $3F, $FF, $FC,
    $7F, $FF, $FE,
    $FF, $FF, $FF,
    $FF, $E7, $FF,
    $FF, $C3, $FF,
    $FE, $00, $7F,
    $FC, $00, $3F,
    $F8, $00, $1F,
    $F0, $00, $0F,
    $E0, $00, $07,
    $C0, $00, $03,
    $80, $00, $01
]

// Wait until the beam has passed the bottom of the screen, once per frame.
fn wait_frame() {
    while c64.vic.raster != 250 { }
    while c64.vic.raster == 250 { }
}

fn show() {
    c64.vic.sprite0_x = ship.x.lo
    c64.vic.sprite_x_msb = ship.x.hi       // bit 0 is sprite 0's ninth bit
    c64.vic.sprite0_y = ship.y
    if ship.shots & 4 != 0 { c64.vic.border = c64.RED } else { c64.vic.border = c64.BLACK }
}

fn main() {
    for i in 0..63 { shape[i] = ship_shape[i] }
    pointer0 = $0340 / 64
    c64.vic.sprite_color[0] = c64.YELLOW
    c64.vic.sprite_enable = 1
    show()
    while true {
        wait_frame()
        ship.move(c64.cia1.port_a)
        show()
    }
}
```

```console
$ tern game.tern
Wrote game.prg
Code and data: $0801-$0915 (277 bytes)
Variables after the program: 0 bytes
Zero page:
$02  2  ship.x
$04  1  ship.y
$05  1  ship.shots
$06  1  ship.move.joy
```

- `shape` and `pointer0` are fixed addresses: the VIC finds sprite data in 64-byte blocks of its
  16 KB bank (block 13 is `$0340`), and the pointers at the end of the screen.
- I/O registers (`c64.vic`, `c64.cia1`) are volatile, so `wait_frame`'s loops read the raster
  register on every pass.
- In `tern run` this program never leaves `wait_frame` (there is no VIC to move the raster line),
  so check it in VICE: `x64sc -autostart game.prg`, and map a joystick to port 2 in the settings.
  To check what it put in memory without VICE, stop it after some cycles:

```console
$ tern run game.tern --cycles 20000 --dump pointer0 --dump '$D000:2' --dump '$D015:1'
Stopped after 20000 cycles; main had not returned: 20001 cycles (19973 in main)
A=$00 X=$3F Y=$00 SP=$FB PC=$08A7
pointer0 $07F8 u8 = 13 ($0D)
$D000 (2 bytes):
  $D000  A0 8C
$D015 (1 bytes):
  $D015  01
```

It stopped in `wait_frame` (the raster register is plain RAM there and stays 0), after setting the
pointer, the position 160, 140 (`$A0`, `$8C`) and sprite 0 on.

## 4. Writing it faster, if it mattered

`move` is called once a frame, so it does not matter here. In a loop over many objects, apply
`costs.md`: an array of structs for the objects, `inline fn` for a small helper, and `tern bench`
for each change.
