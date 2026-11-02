%zeropage basicsafe
%option no_sysinit

main {
    uword total
    ubyte clamped

    sub add3(ubyte a, ubyte b, ubyte c) -> ubyte {
        return a + b + c
    }

    sub clampv(ubyte v, ubyte top) -> ubyte {
        if v > top
            return top
        return v
    }

    sub bigger(ubyte a, ubyte b) -> bool {
        return a > b
    }

    sub start() {
        ubyte i
        ubyte s
        for i in 0 to 199 {
            s = add3(i, i >> 1, 3)
            total += s
            clamped = clampv(s, 150)
            if bigger(s, 100)
                total++
        }
    }
}
