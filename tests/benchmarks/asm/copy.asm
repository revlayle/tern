; Copy 1000 bytes through pointers, then compare them.
SRC  = $4000
DST  = $5000
same = $c000
p    = $10                  ; pointers
q    = $12

* = $0800
start
        lda #<SRC
        sta p
        lda #>SRC
        sta p+1
        ldx #0
fill    txa
        ldy #0
        sta (p),y
        eor #$ff
        iny
        sta (p),y
        txa
        clc
        adc #1
        iny
        sta (p),y
        txa
        lsr
        iny
        sta (p),y
        lda p
        clc
        adc #4
        sta p
        bcc nc
        inc p+1
nc      inx
        cpx #250
        bne fill

        lda #<SRC
        sta p
        lda #>SRC
        sta p+1
        lda #<DST
        sta q
        lda #>DST
        sta q+1
        ldx #3
page    ldy #0
cp      lda (p),y
        sta (q),y
        iny
        bne cp
        inc p+1
        inc q+1
        dex
        bne page
cp2     lda (p),y
        sta (q),y
        iny
        cpy #232
        bne cp2

        lda #1
        sta same
        lda #<SRC
        sta p
        lda #>SRC
        sta p+1
        lda #<DST
        sta q
        lda #>DST
        sta q+1
        ldx #3
cpage   ldy #0
cmp1    lda (p),y
        cmp (q),y
        beq eq1
        lda #0
        sta same
eq1     iny
        bne cmp1
        inc p+1
        inc q+1
        dex
        bne cpage
cmp2    lda (p),y
        cmp (q),y
        beq eq2
        lda #0
        sta same
eq2     iny
        cpy #232
        bne cmp2
        rts
