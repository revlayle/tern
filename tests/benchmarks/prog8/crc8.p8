%zeropage basicsafe
%option no_sysinit

main {
    ubyte crc

    sub start() {
        ubyte b = 0
        ubyte i
        ubyte k
        for i in 0 to 255 {
            b += 37
            crc ^= b
            repeat 8 {
                if crc & $80 != 0
                    crc = (crc << 1) ^ $07
                else
                    crc = crc << 1
            }
        }
    }
}
