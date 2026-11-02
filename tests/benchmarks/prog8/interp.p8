%zeropage basicsafe
%option no_sysinit

main {
    const ubyte PUSH = 0
    const ubyte ADD = 1
    const ubyte SUB = 2
    const ubyte DUP = 3
    const ubyte SWAP = 4
    const ubyte DROP = 5
    const ubyte JNZ = 6
    const ubyte DEC = 7
    const ubyte OVER = 8
    const ubyte HALT = 9

    ubyte[] code = [PUSH, 0, PUSH, 10, SWAP, OVER, ADD, SWAP, DEC, DUP, JNZ, 4, DROP, HALT]
    ubyte[16] stack
    ubyte sp
    ubyte result
    uword steps

    sub start() {
        ubyte pc = 0
        ubyte op
        ubyte t
        bool running = true
        while running {
            op = code[pc]
            pc++
            steps++
            when op {
                PUSH -> {
                    stack[sp] = code[pc]
                    sp++
                    pc++
                }
                ADD -> {
                    sp--
                    stack[sp - 1] = stack[sp - 1] + stack[sp]
                }
                SUB -> {
                    sp--
                    stack[sp - 1] = stack[sp - 1] - stack[sp]
                }
                DUP -> {
                    stack[sp] = stack[sp - 1]
                    sp++
                }
                SWAP -> {
                    t = stack[sp - 1]
                    stack[sp - 1] = stack[sp - 2]
                    stack[sp - 2] = t
                }
                DROP -> sp--
                JNZ -> {
                    sp--
                    if stack[sp] != 0
                        pc = code[pc]
                    else
                        pc++
                }
                DEC -> stack[sp - 1]--
                OVER -> {
                    stack[sp] = stack[sp - 2]
                    sp++
                }
                else -> running = false
            }
        }
        result = stack[0]
    }
}
