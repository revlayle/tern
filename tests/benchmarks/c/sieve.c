/* Sieve of Eratosthenes: the primes below 256. */
unsigned char composite[256];
unsigned char count;

void main(void) {
    unsigned char n, i;
    unsigned int m;
    for (n = 2; n < 16; ++n) {
        if (composite[n] == 0) {
            m = (unsigned int)n << 1;
            while (m < 256) {
                composite[(unsigned char)m] = 1;
                m += n;
            }
        }
    }
    for (i = 2; i != 0; ++i) {
        if (composite[i] == 0) ++count;
    }
}
