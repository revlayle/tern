; Bubble sort of 100 bytes made by a linear congruential generator.
values = $c000
last   = $10
tmp    = $11

* = $0800
start
        ldy #0
        lda #7
gen     sta tmp
        asl
        asl
        clc
        adc tmp
        clc
        adc #17
        sta values,y
        iny
        cpy #100
        bne gen
        lda #99
        sta last
outer   ldy #0
inner   lda values+1,y
        cmp values,y
        bcs ordered         ; b >= a
        ldx values,y
        sta values,y
        txa
        sta values+1,y
ordered iny
        cpy last
        bne inner
        dec last
        bne outer
        rts
