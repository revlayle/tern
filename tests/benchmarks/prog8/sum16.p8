%zeropage basicsafe
%option no_sysinit

main {
    uword[200] values
    uword total
    uword largest

    sub start() {
        uword v = 1000
        ubyte i
        for i in 0 to 199 {
            v = v + (v >> 3) + 77
            values[i] = v
        }
        for i in 0 to 199 {
            total += values[i]
            if values[i] > largest
                largest = values[i]
        }
    }
}
