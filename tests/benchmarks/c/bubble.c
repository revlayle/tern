/* Bubble sort of 100 bytes made by a linear congruential generator. */
#define N 100
unsigned char values[N];

void main(void) {
    unsigned char x = 7, i, last, a, b;
    for (i = 0; i < N; ++i) {
        x = (x << 2) + x + 17;
        values[i] = x;
    }
    last = N - 1;
    while (last > 0) {
        for (i = 0; i < last; ++i) {
            a = values[i];
            b = values[i + 1];
            if (a > b) {
                values[i] = b;
                values[i + 1] = a;
            }
        }
        --last;
    }
}
