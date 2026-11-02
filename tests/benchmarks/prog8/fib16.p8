%zeropage basicsafe
%option no_sysinit

main {
    uword[24] fib
    uword product

    sub mul(uword a, uword b) -> uword {
        uword r = 0
        while b != 0 {
            if lsb(b) & 1 != 0
                r += a
            a = a << 1
            b = b >> 1
        }
        return r
    }

    sub start() {
        ubyte i
        fib[0] = 0
        fib[1] = 1
        for i in 2 to 23 {
            fib[i] = fib[i - 1] + fib[i - 2]
        }
        product = mul(fib[20], 9) + mul(123, 45)
    }
}
