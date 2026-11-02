%zeropage basicsafe
%option no_sysinit

main {
    ubyte[100] values

    sub start() {
        ubyte x = 7
        ubyte i
        ubyte last
        ubyte a
        ubyte b
        for i in 0 to 99 {
            x = (x << 2) + x + 17
            values[i] = x
        }
        last = 99
        while last > 0 {
            for i in 0 to last - 1 {
                a = values[i]
                b = values[i + 1]
                if a > b {
                    values[i] = b
                    values[i + 1] = a
                }
            }
            last--
        }
    }
}
