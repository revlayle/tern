/* Sum and maximum of 200 16-bit values. */
#define N 200
unsigned int values[N];
unsigned int total;
unsigned int largest;

void main(void) {
    unsigned int v = 1000;
    unsigned char i;
    for (i = 0; i < N; ++i) {
        v = v + (v >> 3) + 77;
        values[i] = v;
    }
    for (i = 0; i < N; ++i) {
        total += values[i];
        if (values[i] > largest) largest = values[i];
    }
}
