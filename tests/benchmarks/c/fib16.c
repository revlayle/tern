/* Fibonacci numbers below 65536, and a 16-bit multiply by shifting and adding. */
unsigned int fib[24];
unsigned int product;

static unsigned int mul(unsigned int a, unsigned int b) {
    unsigned int r = 0;
    while (b != 0) {
        if (b & 1) r += a;
        a <<= 1;
        b >>= 1;
    }
    return r;
}

void main(void) {
    unsigned char i;
    fib[0] = 0;
    fib[1] = 1;
    for (i = 2; i < 24; ++i) {
        fib[i] = fib[i - 1] + fib[i - 2];
    }
    product = mul(fib[20], 9) + mul(123, 45);
}
