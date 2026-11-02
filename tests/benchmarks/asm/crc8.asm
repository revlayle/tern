; CRC-8 (polynomial $07) over 256 bytes, bit by bit.
crc = $c000
b   = $10

* = $0800
start
        ldx #0
loop    lda b
        clc
        adc #37
        sta b
        eor crc
        ldy #8
bitloop asl
        bcc nox
        eor #$07
nox     dey
        bne bitloop
        sta crc
        inx
        bne loop
        rts
