/* Copy 1000 bytes through pointers, then compare them. */
#define SRC ((unsigned char *)0x4000)
#define DST ((unsigned char *)0x5000)
unsigned char same;

static void copy(unsigned char *to, unsigned char *from, unsigned int n) {
    unsigned char i;
    while (n >= 256) {
        i = 0;
        do { to[i] = from[i]; ++i; } while (i != 0);
        to += 256;
        from += 256;
        n -= 256;
    }
    for (i = 0; i < (unsigned char)n; ++i) to[i] = from[i];
}

void main(void) {
    unsigned char *p = SRC, *a, *b;
    unsigned char k, j;
    for (k = 0; k < 250; ++k) {
        p[0] = k;
        p[1] = k ^ 0xFF;
        p[2] = k + 1;
        p[3] = k >> 1;
        p += 4;
    }
    copy(DST, SRC, 1000);
    a = SRC;
    b = DST;
    same = 1;
    for (k = 0; k < 250; ++k) {
        for (j = 0; j < 4; ++j) {
            if (a[j] != b[j]) same = 0;
        }
        a += 4;
        b += 4;
    }
}
