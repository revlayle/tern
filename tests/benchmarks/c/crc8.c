/* CRC-8 (polynomial $07) over 256 bytes, bit by bit. */
unsigned char crc;

void main(void) {
    unsigned char b = 0, i, k;
    i = 0;
    do {
        b += 37;
        crc ^= b;
        for (k = 0; k < 8; ++k) {
            if (crc & 0x80) crc = (crc << 1) ^ 0x07; else crc = crc << 1;
        }
        ++i;
    } while (i != 0);
}
