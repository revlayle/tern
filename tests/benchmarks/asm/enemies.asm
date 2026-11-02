; Sixteen enemies move and bounce for 50 frames. Tables: x, y, dx, dy, hits, 16 bytes each.
enemies = $c000
ex      = enemies
ey      = enemies+16
edx     = enemies+32
edy     = enemies+48
ehits   = enemies+64
bounces = $c100             ; 16 bits
frames  = $10
tmp     = $11

* = $0800
start
        ldx #0
init    txa
        asl
        asl
        asl
        sta tmp
        txa
        adc tmp             ; carry is clear: i * 8 < 128
        adc #10
        sta ex,x
        txa
        asl
        asl
        sta tmp
        txa
        adc tmp
        adc #20
        sta ey,x
        lda #1
        sta edx,x
        lda #$fe
        sta edy,x
        inx
        cpx #16
        bne init
        lda #50
        sta frames
frame   ldx #0
loop    lda ex,x
        clc
        adc edx,x
        sta ex,x
        cmp #8
        bcc bx
        cmp #201
        bcc oky
bx      lda #0
        sec
        sbc edx,x
        sta edx,x
        inc ehits,x
        inc bounces
        bne oky
        inc bounces+1
oky     lda ey,x
        clc
        adc edy,x
        sta ey,x
        cmp #16
        bcc by
        cmp #181
        bcc next
by      lda #0
        sec
        sbc edy,x
        sta edy,x
        inc bounces
        bne next
        inc bounces+1
next    inx
        cpx #16
        bne loop
        dec frames
        bne frame
        rts
