; Sieve of Eratosthenes: the primes below 256.
composite = $c000
count     = $10
n         = $11

* = $0800
start
        lda #2
        sta n
nloop   ldy n
        lda composite,y
        bne next
        tya
        asl                 ; m = 2n, below 32
        tay
mark    sta composite,y     ; any non-zero value marks: store m itself
        clc
        adc n
        tay
        bcc mark
next    inc n
        lda n
        cmp #16
        bne nloop
        ldx #0
        ldy #2
cloop   lda composite,y
        bne skip
        inx
skip    iny
        bne cloop
        stx count
        rts
