; Fibonacci numbers below 65536, and a 16-bit multiply by shifting and adding.
fib     = $c000             ; 24 low bytes, then 24 high bytes
product = $c040
al = $10
ah = $11
bl = $12
bh = $13
rl = $14
rh = $15

* = $0800
start
        lda #1
        sta fib+1
        ldx #2
f       lda fib-1,x
        clc
        adc fib-2,x
        sta fib,x
        lda fib+24-1,x
        adc fib+24-2,x
        sta fib+24,x
        inx
        cpx #24
        bne f
        ldx #20
        lda fib,x
        sta al
        lda fib+24,x
        sta ah
        lda #9
        sta bl
        lda #0
        sta bh
        jsr mul
        lda rl
        sta product
        lda rh
        sta product+1
        lda #123
        sta al
        lda #0
        sta ah
        sta bh
        lda #45
        sta bl
        jsr mul
        lda rl
        clc
        adc product
        sta product
        lda rh
        adc product+1
        sta product+1
        rts

mul     lda #0
        sta rl
        sta rh
        lda bl
        ora bh
        beq done
loop    lsr bh
        ror bl
        bcc skip
        clc
        lda rl
        adc al
        sta rl
        lda rh
        adc ah
        sta rh
skip    asl al
        rol ah
        lda bl
        ora bh
        bne loop
done    rts
