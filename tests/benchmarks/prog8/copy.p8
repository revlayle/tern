%zeropage basicsafe
%option no_sysinit

main {
    const uword SRC = $4000
    const uword DST = $5000
    ubyte same

    sub copy(uword dest, uword from, uword n) {
        ubyte i
        while msb(n) > 0 {
            i = 0
            repeat 256 {
                dest[i] = from[i]
                i++
            }
            dest += 256
            from += 256
            n -= 256
        }
        i = 0
        while i != lsb(n) {
            dest[i] = from[i]
            i++
        }
    }

    sub start() {
        uword p = SRC
        uword a
        uword b
        ubyte k
        ubyte j
        for k in 0 to 249 {
            p[0] = k
            p[1] = k ^ $ff
            p[2] = k + 1
            p[3] = k >> 1
            p += 4
        }
        copy(DST, SRC, 1000)
        a = SRC
        b = DST
        same = 1
        repeat 250 {
            for j in 0 to 3 {
                if a[j] != b[j]
                    same = 0
            }
            a += 4
            b += 4
        }
    }
}
