/* Many calls to small functions with parameters and return values. */
unsigned int total;
unsigned char clamped;

static unsigned char add3(unsigned char a, unsigned char b, unsigned char c) {
    return a + b + c;
}

static unsigned char clamp(unsigned char v, unsigned char top) {
    if (v > top) return top;
    return v;
}

static unsigned char bigger(unsigned char a, unsigned char b) {
    return a > b;
}

void main(void) {
    unsigned char i, s;
    for (i = 0; i < 200; ++i) {
        s = add3(i, i >> 1, 3);
        total += s;
        clamped = clamp(s, 150);
        if (bigger(s, 100)) ++total;
    }
}
