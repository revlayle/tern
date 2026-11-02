; Sum and maximum of 200 16-bit values.
values  = $c000             ; 200 low bytes, then 200 high bytes
total   = $c200
largest = $c202
vl = $10
vh = $11
th = $12

* = $0800
start
        lda #<1000
        sta vl
        lda #>1000
        sta vh
        ldx #0
gen     lda vh
        sta th
        lda vl
        lsr th
        ror
        lsr th
        ror
        lsr th
        ror
        clc
        adc vl
        sta vl
        lda th
        adc vh
        sta vh
        lda vl
        clc
        adc #77
        sta vl
        bcc nc
        inc vh
nc      sta values,x
        lda vh
        sta values+200,x
        inx
        cpx #200
        bne gen
        ldx #0
sum     lda total
        clc
        adc values,x
        sta total
        lda total+1
        adc values+200,x
        sta total+1
        lda largest
        cmp values,x
        lda largest+1
        sbc values+200,x
        bcs next
        lda values,x
        sta largest
        lda values+200,x
        sta largest+1
next    inx
        cpx #200
        bne sum
        rts
