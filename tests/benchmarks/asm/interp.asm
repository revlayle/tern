; A small stack machine interpreter, dispatching through a table.
OP_PUSH = 0
OP_ADD  = 1
OP_SUB  = 2
OP_DUP  = 3
OP_SWAP = 4
OP_DROP = 5
OP_JNZ  = 6
OP_DEC  = 7
OP_OVER = 8
OP_HALT = 9

stack  = $c000
result = $c010
steps  = $c011              ; 16 bits
pc     = $10
tmp    = $11

* = $0800
start
        ldy #0              ; Y = sp
fetch   ldx pc
        lda code,x
        inc pc
        inc steps
        bne s1
        inc steps+1
s1      tax
        lda hi,x
        pha
        lda lo,x
        pha
        rts

h_push  ldx pc
        lda code,x
        sta stack,y
        iny
        inc pc
        jmp fetch
h_add   dey
        lda stack-1,y
        clc
        adc stack,y
        sta stack-1,y
        jmp fetch
h_sub   dey
        lda stack-1,y
        sec
        sbc stack,y
        sta stack-1,y
        jmp fetch
h_dup   lda stack-1,y
        sta stack,y
        iny
        jmp fetch
h_swap  lda stack-1,y
        sta tmp
        lda stack-2,y
        sta stack-1,y
        lda tmp
        sta stack-2,y
        jmp fetch
h_drop  dey
        jmp fetch
h_jnz   dey
        lda stack,y
        beq nojump
        ldx pc
        lda code,x
        sta pc
        jmp fetch
nojump  inc pc
        jmp fetch
h_dec   lda stack-1,y
        sec
        sbc #1
        sta stack-1,y
        jmp fetch
h_over  lda stack-2,y
        sta stack,y
        iny
        jmp fetch
h_halt  lda stack
        sta result
        rts

code    .byte OP_PUSH, 0, OP_PUSH, 10, OP_SWAP, OP_OVER, OP_ADD, OP_SWAP, OP_DEC, OP_DUP, OP_JNZ, 4, OP_DROP, OP_HALT
lo      .byte <(h_push-1), <(h_add-1), <(h_sub-1), <(h_dup-1), <(h_swap-1), <(h_drop-1), <(h_jnz-1), <(h_dec-1), <(h_over-1), <(h_halt-1)
hi      .byte >(h_push-1), >(h_add-1), >(h_sub-1), >(h_dup-1), >(h_swap-1), >(h_drop-1), >(h_jnz-1), >(h_dec-1), >(h_over-1), >(h_halt-1)
