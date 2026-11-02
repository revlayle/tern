%zeropage basicsafe
%option no_sysinit

main {
    ubyte[16] ex
    ubyte[16] ey
    byte[16] edx
    byte[16] edy
    ubyte[16] ehits
    uword bounces

    sub start() {
        ubyte i
        ubyte x
        ubyte y
        for i in 0 to 15 {
            ex[i] = i * 9 + 10
            ey[i] = i * 5 + 20
            edx[i] = 1
            edy[i] = -2
        }
        repeat 50 {
            for i in 0 to 15 {
                x = ex[i] + edx[i] as ubyte
                if x < 8 or x > 200 {
                    edx[i] = -edx[i]
                    ehits[i]++
                    bounces++
                }
                ex[i] = x
                y = ey[i] + edy[i] as ubyte
                if y < 16 or y > 180 {
                    edy[i] = -edy[i]
                    bounces++
                }
                ey[i] = y
            }
        }
    }
}
