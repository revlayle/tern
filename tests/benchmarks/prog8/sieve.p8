%zeropage basicsafe
%option no_sysinit

main {
    ubyte[256] composite
    ubyte count

    sub start() {
        ubyte n
        uword m
        ubyte i
        for n in 2 to 15 {
            if composite[n] == 0 {
                m = (n as uword) << 1
                while m < 256 {
                    composite[lsb(m)] = 1
                    m += n
                }
            }
        }
        for i in 2 to 255 {
            if composite[i] == 0 count++
        }
    }
}
