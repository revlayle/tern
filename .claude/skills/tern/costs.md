# What Tern costs, and how to write it fast

Operators compile to a few fixed instructions; anything that needs a loop is a library call. The
listing (`.lst`) shows every line's instructions with the cycles of each (`2+`: a taken branch or a
page crossing adds a cycle). Sections 2 to 10 are rules measured with `tern bench`; they describe
this compiler, so measure your own code when it matters.

## 1. Code shapes

```tern
// shapes.tern
import math

struct Enemy { x: u8, y: u8, hp: u8 }

var a: [u8; 100]
var enemies: [Enemy; 16]
var x: u8 = 5
var y: u8
var i: u8 = 3
var w: u16
var b: u8 = 7
var p: ptr u8 @ zp
var big: u16

fn below(v: u8) -> bool { return v < 100 }

fn main() {
    x += 1
    w += 1
    w += b as u16
    w += 256
    a[i] = x
    enemies[i].hp -= 1
    p = $C000
    p[i] = x
    if x == 0 { y = 1 }
    if x < b { y = 2 }
    for j in 0..10 using X { a[j] = 0 }
    for _ in 0..10 { y += 1 }
    y = i & 7
    big = math.mul8(x, b)
    if below(y) { y = 0 }
}
```

```console
$ tern shapes.tern
Wrote shapes.prg
Code and data: $0801-$08D4 (212 bytes)
Variables after the program: 148 bytes, up to $0968
Zero page:
$02  1  math.w0
$03  1  math.w1
$04  1  x
$05  1  y
$06  1  i
$07  2  w
$09  1  b
$0A  2  p
$0C  2  big
$0E  1  below.v
```

The report says where the code and variables went; the variables used most are in zero page.

Memory, increments and adds (`w += 256` is one `inc` of the high byte):

```lst
                                ;   19: x += 1
$0844  E6 04     inc x                 ; 5
                                ;   20: w += 1
$0846  E6 07     inc w                 ; 5
$0848  D0 02     bne main.inc0         ; 2+
$084A  E6 08     inc w+1               ; 5
main.inc0:
                                ;   21: w += b as u16
$084C  A5 09     lda b                 ; 3
$084E  18        clc                   ; 2
$084F  65 07     adc w                 ; 3
$0851  85 07     sta w                 ; 3
$0853  90 02     bcc main.c1           ; 2+
$0855  E6 08     inc w+1               ; 5
main.c1:
                                ;   22: w += 256
$0857  E6 08     inc w+1               ; 5
```

Arrays are indexed with X or Y; an array of structs is one table per field, so a field is one
instruction; a pointer is `(p),y`:

```lst
                                ;   23: a[i] = x
$0859  A5 04     lda x                 ; 3
$085B  A6 06     ldx i                 ; 3
$085D  9D D5 08  sta a,x               ; 5
                                ;   24: enemies[i].hp -= 1
$0860  DE 59 09  dec enemies+32,x      ; 7
                                ;   25: p = $C000
$0863  A9 00     lda #$00              ; 2
$0865  85 0A     sta p                 ; 3
$0867  A9 C0     lda #$C0              ; 2
$0869  85 0B     sta p+1               ; 3
                                ;   26: p[i] = x
$086B  A4 06     ldy i                 ; 3
$086D  A5 04     lda x                 ; 3
$086F  91 0A     sta (p),y             ; 6
```

Compares branch on the flags directly; a `for` loop with a register counter, and a repeat loop that
counts down and keeps `y` in the Y register (its store after the loop is left out, since line 31
overwrites `y`):

```lst
                                ;   27: if x == 0 { y = 1 }
$0871  A5 04     lda x                 ; 3
$0873  D0 04     bne main.endif2       ; 2+
$0875  A9 01     lda #$01              ; 2
$0877  85 05     sta y                 ; 3
main.endif2:
                                ;   28: if x < b { y = 2 }
$0879  A5 04     lda x                 ; 3
$087B  C5 09     cmp b                 ; 3
$087D  B0 04     bcs main.endif3       ; 2+
$087F  A9 02     lda #$02              ; 2
$0881  85 05     sta y                 ; 3
main.endif3:
                                ;   29: for j in 0..10 using X { a[j] = 0 }
$0883  A2 00     ldx #$00              ; 2
main.for6:
$0885  A9 00     lda #$00              ; 2
$0887  9D D5 08  sta a,x               ; 5
main.fnext5:
$088A  E8        inx                   ; 2
$088B  E0 0A     cpx #$0A              ; 2
$088D  D0 F6     bne main.for6         ; 2+
main.fend4:
                                ;   30: for _ in 0..10 { y += 1 }
                                ; repeat 10 times
$088F  A2 0A     ldx #$0A              ; 2
$0891  A4 05     ldy y                 ; 3
main.for9:
$0893  C8        iny                   ; 2
main.fnext8:
$0894  CA        dex                   ; 2
$0895  D0 FC     bne main.for9         ; 2+
main.fend7:
```

A library call loads its register arguments (A last) and is one `jsr`; a plain parameter is stored
straight into the function's memory; a `bool` comes back in the carry, and `return v < 100` is one
subtract:

```lst
                                ;   32: big = math.mul8(x, b)
$089D  A6 09     ldx b                 ; 3
$089F  A5 04     lda x                 ; 3
$08A1  20 BC 08  jsr math.mul8         ; 6
$08A4  85 0C     sta big               ; 3
$08A6  86 0D     stx big+1             ; 3
                                ;   33: if below(y) { y = 0 }
$08A8  A5 05     lda y                 ; 3
$08AA  85 0E     sta below.v           ; 3
$08AC  20 B6 08  jsr below             ; 6
$08AF  90 04     bcc main.endif10      ; 2+
$08B1  A9 00     lda #$00              ; 2
$08B3  85 05     sta y                 ; 3
main.endif10:
                                ;   18: fn main() {
$08B5  60        rts                   ; 6
                                ;   16: fn below(v: u8) -> bool { return v < 100 }
below:
$08B6  A9 64     lda #$64              ; 2
$08B8  18        clc                   ; 2
$08B9  E5 0E     sbc below.v           ; 3
$08BB  60        rts                   ; 6
```

Other shapes: `-x` is `lda #0 / sec / sbc x`; `x << 2` is `asl` twice; a 16-bit shift by 8 to 15
moves a byte first (`w = v << 8` is `lda v / sta w+1 / lda #0 / sta w`, `<< 10` adds two `asl`), while
a 16-bit shift by 1 to 7 costs an `asl` / `rol` pair per bit; a 16-bit compare is two byte
compares; a signed compare costs a few instructions more than an unsigned one; `as` between sizes
of the same signedness is free or one `lda #0`. A plain `match` is one `cmp` / `beq` per value, in
source order.

Library routines, roughly, in cycles per call (measure your own case with `tern bench`):

| Routine | Cycles |
|---|---|
| `math.mul8` | 150-185 |
| `math.mul8_fast` | about 70 (adds 1 KB of tables) |
| `math.div8` | 190-245 |
| `math.mul16` | up to about 850 |
| `math.div16` | 700-1050 |
| `text.dec8` / `text.dec16` | up to about 300 / 1600 |
| `text.hex8` | about 57 |
| `mem.copy` / `mem.fill` | 16-19 / 13-17 per byte |
| `random.next8` | about 57 |
| `wide.dec32` | about 4000 |
| `float.add` / `float.sub` | 110-960 |
| `float.mul` / `float.div` | up to about 2500 / 4400 |
| `float.dec` | 1250-7700 below 65536 |

## 2. Arrays before pointers

An array element is `lda a,x`; a pointer access is `lda (p),y` with the pointer set up first. When
the data fits in 256 elements, use an array, and for records an array of structs.

```tern
// arrays.tern
struct Enemy { x: u8, y: u8, hp: u8 }
var enemies: [Enemy; 32]
var last: u8

fn advance() {
    for i in 0..32 { enemies[i].x += enemies[i].y }
}

fn main() {
    for i in 0..32 { enemies[i].y = i }
    advance()
    last = enemies[31].x
}
```

```tern
// pointers.tern
struct Enemy { x: u8, y: u8, hp: u8 }
linear var enemies: [Enemy; 32]
var last: u8

fn advance() {
    var e: ptr Enemy = addr(enemies)
    for _ in 0..32 {
        e.x += e.y
        e = e + sizeof(Enemy)
    }
}

fn main() {
    var e: ptr Enemy = addr(enemies)
    for i in 0..32 {
        e.y = i
        e = e + sizeof(Enemy)
    }
    advance()
    last = enemies[31].x
}
```

```console
$ tern bench arrays.tern pointers.tern --fn advance --dump last
variant        program  advance bytes   calls     cycles  per call    zp  other
arrays.tern         71          18       1        711       711     1     96
pointers.tern      108          35       1       1297      1297     3     96
Memory (zp: zero page bytes, other: the rest; the measured function's variables and the program's globals):
  arrays.tern: last $0002 u8, enemies $0900 [Enemy; 32]
  pointers.tern: last $0002 u8, enemies $086D [Enemy; 32], advance.e $0005 ptr Enemy
Results:
  arrays.tern: last = 31
  pointers.tern: last = 31
```

Pointers are for blocks longer than 256 bytes and data whose address is known only at runtime.

## 3. Through a pointer: index with Y, step by pages

Keep the pointer still while Y walks a page, then step it by 256 (`p += 256` is `inc p+1`). Stepping
the pointer for every byte costs far more:

```tern
// pages.tern
var r: bool

fn same(a: ptr u8, b: ptr u8, pages: u8) -> bool {
    for _ in 0..pages {
        for j in 0..=255 using Y {
            if a[j] != b[j] { return false }
        }
        a += 256
        b += 256
    }
    return true
}

fn main() {
    r = same($C000, $C800, 2)
}
```

```tern
// bytes.tern
var r: bool

fn same(a: ptr u8, b: ptr u8, count: u16) -> bool {
    while count != 0 {
        if a[0] != b[0] { return false }
        a += 1
        b += 1
        count -= 1
    }
    return true
}

fn main() {
    r = same($C000, $C800, 512)
}
```

```console
$ tern bench pages.tern bytes.tern --fn same --dump r
variant     program  same bytes   calls     cycles  per call    zp  other
pages.tern       85          35       1       9283      9283     7      0
bytes.tern       99          47       1      27703     27703     7      0
Memory (zp: zero page bytes, other: the rest; the measured function's variables and the program's globals):
  pages.tern: r $0002 bool, same.a $0003 ptr u8, same.b $0005 ptr u8, same.pages $0007 u8, same._loop0 $0008 u8
  bytes.tern: r $0002 bool, same.a $0003 ptr u8, same.b $0005 ptr u8, same.count $0007 u16
Results:
  pages.tern: r = 1
  bytes.tern: r = 1
```

## 4. Store a result before testing it

After `enemies[i].x = x`, A still holds `x`, so the tests after it need no load. Testing first and
storing afterwards loads it again.

```tern
// first.tern
struct Enemy { x: u8, dx: i8 }
var enemies: [Enemy; 16]

fn move() {
    for i in 0..16 {
        var x: u8 = enemies[i].x + (enemies[i].dx as u8)
        enemies[i].x = x                 // store first
        if x < 8 || x > 200 { enemies[i].dx = -enemies[i].dx }
    }
}

fn main() {
    for i in 0..16 { enemies[i].x = i << 4; enemies[i].dx = 3 }
    for _ in 0..50 { move() }
}
```

```tern
// later.tern
struct Enemy { x: u8, dx: i8 }
var enemies: [Enemy; 16]

fn move() {
    for i in 0..16 {
        var x: u8 = enemies[i].x + (enemies[i].dx as u8)
        if x < 8 || x > 200 { enemies[i].dx = -enemies[i].dx }
        enemies[i].x = x                 // store last
    }
}

fn main() {
    for i in 0..16 { enemies[i].x = i << 4; enemies[i].dx = 3 }
    for _ in 0..50 { move() }
}
```

```console
$ tern bench first.tern later.tern --fn move --dump 'enemies:32'
variant     program  move bytes   calls     cycles  per call    zp  other
first.tern       96          35      50      27508       550     1     32
later.tern      100          39      50      32308       646     1     32
Memory (zp: zero page bytes, other: the rest; the measured function's variables and the program's globals):
  first.tern: enemies $0900 [Enemy; 16], move.x $0003 u8
  later.tern: enemies $0900 [Enemy; 16], move.x $0003 u8
Results:
  first.tern: enemies:32 = 00 A6 B6 C6 BE B0 9C 8E 80 6C 5E 50 3C D0 E0 F0 03 03 03 03 FD FD FD FD FD FD FD FD FD 03 03 03
  later.tern: enemies:32 = 00 A6 B6 C6 BE B0 9C 8E 80 6C 5E 50 3C D0 E0 F0 03 03 03 03 FD FD FD FD FD FD FD FD FD 03 03 03
```

## 5. Leave a loop with `break` or `return`, not a flag

A flag costs a store where it is set and a load and test on every pass; `while true` with `break` has
neither. `break` and `return` also work from inside a `match` arm.

```tern
// flag.tern
var codes: [u8; 64]
var steps: u8

fn run() {
    var pc: u8 = 0
    var running: bool = true
    while running {
        match codes[pc] {
            0 => steps += 1
            1 => steps += 2
            _ => running = false
        }
        pc += 1
    }
}

fn main() {
    codes[60] = 9
    run()
}
```

```tern
// break.tern
var codes: [u8; 64]
var steps: u8

fn run() {
    var pc: u8 = 0
    while true {
        match codes[pc] {
            0 => steps += 1
            1 => steps += 2
            _ => break
        }
        pc += 1
    }
}

fn main() {
    codes[60] = 9
    run()
}
```

```console
$ tern bench flag.tern break.tern --fn run --dump steps
variant     program   run bytes   calls     cycles  per call    zp  other
flag.tern        88          46       1       1553      1553     3     64
break.tern       74          32       1       1527      1527     2     64
Memory (zp: zero page bytes, other: the rest; the measured function's variables and the program's globals):
  flag.tern: steps $0002 u8, codes $0900 [u8; 64], run.pc $0003 u8, run.running $0004 bool
  break.tern: steps $0002 u8, codes $0900 [u8; 64], run.pc $0003 u8
Results:
  flag.tern: steps = 60
  break.tern: steps = 60
```

## 6. Small functions in a hot loop: `inline fn`

A call costs `jsr` / `rts` (12 cycles) and a store per argument. An `inline fn` puts the body in the
caller, where the argument often is already in a register, and a `bool` result becomes the caller's
branch. Every call site gets a copy, so a large function called from many places makes the program
bigger.

```tern
// call.tern
var values: [u8; 200]
var count: u8

fn big(v: u8) -> bool { return v >= 128 }

fn main() {
    for i in 0..200 { values[i] = i + i }
    for i in 0..200 {
        if big(values[i]) { count += 1 }
    }
}
```

```tern
// inline.tern
var values: [u8; 200]
var count: u8

inline fn big(v: u8) -> bool { return v >= 128 }

fn main() {
    for i in 0..200 { values[i] = i + i }
    for i in 0..200 {
        if big(values[i]) { count += 1 }
    }
}
```

```console
$ tern bench call.tern inline.tern --dump count
variant      program  main bytes   calls     cycles  per call    zp  other
call.tern         80          42       1      13099     13099     2    200
inline.tern       72          39       1       9499      9499     3    200
Memory (zp: zero page bytes, other: the rest; the measured function's variables and the program's globals):
  call.tern: count $0002 u8, values $0900 [u8; 200], main.i $0003 u8
  inline.tern: count $0002 u8, values $0900 [u8; 200], main.i $0003 u8, main.big.v $0005 u8
Results:
  call.tern: count = 72
  inline.tern: count = 72
```

## 7. `using` on a parameter only where the body needs that register

A plain parameter is memory, and the caller stores the argument straight into it. `using` pays when
the body needs the value in that register anyway, as an index or once in A:

```console
$ tern bench --decl 'var t: [u8; 200]' --decl 'fn bump(i: u8) { t[i] += 1 }' --decl 'fn bumpx(i: u8 using X) { t[i] += 1 }' --code 'for i in 0..200 { bump(i) }' --code 'for i in 0..200 { bumpx(i) }' --dump 't:4'
variant   program  main bytes   calls     cycles  per call    zp  other
code 1         53          18       1       7610      7610     1    200
code 2         50          17       1       6810      6810     1    200
Memory (zp: zero page bytes, other: the rest; the measured function's variables and the program's globals):
  code 1: t $0836 [u8; 200], main.i $0002 u8
  code 2: t $0833 [u8; 200], main.i $0002 u8
Results:
  code 1: t:4 = 01 01 01 01
  code 2: t:4 = 01 01 01 01
```

When the body compares or combines two parameters, one has to go to memory anyway, and plain
parameters are as fast or faster:

```console
$ tern bench --decl 'var n: u8' --decl 'fn bigger(a: u8, b: u8) -> bool { return a > b }' --decl 'fn bigger_r(a: u8 using A, b: u8 using X) -> bool { return a > b }' --code 'for i in 0..200 { if bigger(i, 100) { n += 1 } }' --code 'for i in 0..200 { if bigger_r(i, 100) { n += 1 } }' --dump n
variant   program  main bytes   calls     cycles  per call    zp  other
code 1         55          26       1       9206      9206     2      0
code 2         56          22       1       9606      9606     2      0
Memory (zp: zero page bytes, other: the rest; the measured function's variables and the program's globals):
  code 1: n $0002 u8, main.i $0003 u8
  code 2: n $0002 u8, main.i $0003 u8
Results:
  code 1: n = 99
  code 2: n = 99
```

## 8. Multiplying by a constant

`*` takes only constants. At runtime write shifts and adds, computing in `u8` while the value fits
and widening after; or use a `data` table, which is fastest at two bytes per entry:

```console
$ tern bench --decl 'import math' --decl 'var offset: u16' --decl 'data rows: [u16; 25] = [0, 40, 80, 120, 160, 200, 240, 280, 320, 360, 400, 440, 480, 520, 560, 600, 640, 680, 720, 760, 800, 840, 880, 920, 960]' --code 'for row in 0..25 { var w: u16 = (row << 2) + row; offset += w << 3 }' --code 'for row in 0..25 { var w: u16 = row; offset += (w << 5) + (w << 3) }' --code 'for row in 0..25 { offset += math.mul8(row, 40) }' --code 'for row in 0..25 { offset += rows[row] }' --dump offset
variant   program  main bytes   calls     cycles  per call    zp  other
code 1        118          43       1       1707      1707     4      0
code 2        164          89       1       4032      4032     4      0
code 3        135          31       1       4860      4860     3      0
code 4         98          23       1        732       732     2      0
Memory (zp: zero page bytes, other: the rest; the measured function's variables and the program's globals):
  code 1: offset $0002 u16, main.w $0004 u16
  code 2: offset $0002 u16, main.w $0004 u16
  code 3: offset $0004 u16, main.row $0006 u8
  code 4: offset $0002 u16
Results:
  code 1: offset = 12000
  code 2: offset = 12000
  code 3: offset = 12000
  code 4: offset = 12000
```

`(row << 2) + row` is computed in 8 bits, so it needs `row <= 51`.

## 9. `match` or `indexed match`

A plain `match` tests its values in source order, so put the most common first. `indexed match`
costs the same for every value (subtract the lowest value, then a table jump through `rts`). With
values spread evenly, `indexed match` is faster at 16 values (the first comparison below), and the
plain `match` at 8 (the second); with fewer values the plain `match` wins by more.

```tern
// chain.tern
var ops: [u8; 64]
var acc: u8

fn run() {
    for i in 0..64 {
        match ops[i] {
            0 => acc += 1
            1 => acc += 2
            2 => acc += 3
            3 => acc += 4
            4 => acc ^= 5
            5 => acc ^= 6
            6 => acc ^= 7
            7 => acc ^= 8
            8 => acc += 9
            9 => acc += 10
            10 => acc += 11
            11 => acc += 12
            12 => acc ^= 13
            13 => acc ^= 14
            14 => acc ^= 15
            15 => acc ^= 16
        }
    }
}

fn main() {
    for i in 0..64 { ops[i] = i & 15 }
    run()
}
```

```tern
// table.tern
var ops: [u8; 64]
var acc: u8

fn run() {
    for i in 0..64 {
        indexed match ops[i] & 15 {
            0 => acc += 1
            1 => acc += 2
            2 => acc += 3
            3 => acc += 4
            4 => acc ^= 5
            5 => acc ^= 6
            6 => acc ^= 7
            7 => acc ^= 8
            8 => acc += 9
            9 => acc += 10
            10 => acc += 11
            11 => acc += 12
            12 => acc ^= 13
            13 => acc ^= 14
            14 => acc ^= 15
            15 => acc ^= 16
        }
    }
}

fn main() {
    for i in 0..64 { ops[i] = i & 15 }
    run()
}
```

```console
$ tern bench chain.tern table.tern --fn run --dump acc
variant     program   run bytes   calls     cycles  per call    zp  other
chain.tern      290         240       1       3946      3946     1     64
table.tern      249         199       1       3110      3110     1     64
Memory (zp: zero page bytes, other: the rest; the measured function's variables and the program's globals):
  chain.tern: acc $0002 u8, ops $0A00 [u8; 64]
  table.tern: acc $0002 u8, ops $0900 [u8; 64]
Results:
  chain.tern: acc = 208
  table.tern: acc = 208
```

The same with 8 values:

```tern
// chain8.tern
var ops: [u8; 64]
var acc: u8

fn run() {
    for i in 0..64 {
        match ops[i] {
            0 => acc += 1
            1 => acc += 2
            2 => acc += 3
            3 => acc += 4
            4 => acc ^= 5
            5 => acc ^= 6
            6 => acc ^= 7
            7 => acc ^= 8
        }
    }
}

fn main() {
    for i in 0..64 { ops[i] = i & 7 }
    run()
}
```

```tern
// table8.tern
var ops: [u8; 64]
var acc: u8

fn run() {
    for i in 0..64 {
        indexed match ops[i] & 7 {
            0 => acc += 1
            1 => acc += 2
            2 => acc += 3
            3 => acc += 4
            4 => acc ^= 5
            5 => acc ^= 6
            6 => acc ^= 7
            7 => acc ^= 8
        }
    }
}

fn main() {
    for i in 0..64 { ops[i] = i & 7 }
    run()
}
```

```console
$ tern bench chain8.tern table8.tern --fn run --dump acc
variant      program   run bytes   calls     cycles  per call    zp  other
chain8.tern      158         108       1       2623      2623     1     64
table8.tern      154         104       1       2951      2951     1     64
Memory (zp: zero page bytes, other: the rest; the measured function's variables and the program's globals):
  chain8.tern: acc $0002 u8, ops $0900 [u8; 64]
  table8.tern: acc $0002 u8, ops $0900 [u8; 64]
Results:
  chain8.tern: acc = 80
  table8.tern: acc = 80
```

`indexed match` does not check its value: list every value that can occur, or mask it into the span
as here (`& 15` with patterns 0 to 15). `_` catches only values inside the span that no pattern lists.

## 10. What not to rewrite

The optimizer already does these, so write the plain form:

- A local copy of an element costs no code: `var v: u8 = a[i]`, then `if a[i + 1] < v`, compares
  the element directly (`cmp a,x`). Below, both versions have the same bytes and cycles; the first
  keeps a byte of zero page for `v` (`main.v`), which nothing reads.
- `a[i + 1]` in a `for` loop whose bounds prove the sum does not wrap is `lda a+1,x`, with no add.
- Loop counters, and a global used by one function in its loop, stay in free registers across it.
- The variables used most go in zero page; arrays are placed so their indexed range does not cross
  a page when there is room.
- `return a > b` from a `-> bool` function is a subtract into the carry, with no branch.

```console
$ tern bench --decl 'var a: [u8; 100]' --decl 'var n: u8' --code 'for i in 0..100 { a[i] = i ^ 5 }; for i in 0..99 { var v: u8 = a[i]; if a[i + 1] < v { n += 1 } }' --code 'for i in 0..100 { a[i] = i ^ 5 }; for i in 0..99 { if a[i + 1] < a[i] { n += 1 } }' --dump n
variant   program  main bytes   calls     cycles  per call    zp  other
code 1         67          34       1       3458      3458     2    100
code 2         67          34       1       3458      3458     1    100
Memory (zp: zero page bytes, other: the rest; the measured function's variables and the program's globals):
  code 1: n $0002 u8, a $0900 [u8; 100], main.v $0003 u8
  code 2: n $0002 u8, a $0900 [u8; 100]
Results:
  code 1: n = 62
  code 2: n = 62
```
