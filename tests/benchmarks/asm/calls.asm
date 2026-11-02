; Many calls to small functions with parameters and return values.
total   = $c000             ; 16 bits
clamped = $c002
i       = $10
s       = $11
t       = $12

* = $0800
start
loop    lda i
        lsr
        tax                 ; b = i >> 1
        ldy #3              ; c = 3
        lda i               ; a = i
        jsr add3
        sta s
        clc
        adc total
        sta total
        bcc nc1
        inc total+1
nc1     ldx #150
        lda s
        jsr clamp
        sta clamped
        ldx #100
        lda s
        jsr bigger
        bcs notbig          ; carry clear: s > 100
        inc total
        bne notbig
        inc total+1
notbig  inc i
        lda i
        cmp #200
        bne loop
        rts

add3    stx t               ; A = a, X = b, Y = c
        clc
        adc t
        sty t
        clc
        adc t
        rts

clamp   stx t               ; A = v, X = top
        cmp t
        bcc clamp_ok
        beq clamp_ok
        lda t
clamp_ok
        rts

bigger  sta t               ; A = a, X = b; carry clear when a > b
        cpx t
        rts
