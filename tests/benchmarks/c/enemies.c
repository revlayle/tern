/* Sixteen enemies move and bounce for 50 frames. */
struct Enemy { unsigned char x, y; signed char dx, dy; unsigned char hits; };
struct Enemy enemies[16];
unsigned int bounces;

void main(void) {
    unsigned char i, f, x, y;
    for (i = 0; i < 16; ++i) {
        enemies[i].x = (i << 3) + i + 10;
        enemies[i].y = (i << 2) + i + 20;
        enemies[i].dx = 1;
        enemies[i].dy = -2;
    }
    for (f = 0; f < 50; ++f) {
        for (i = 0; i < 16; ++i) {
            x = enemies[i].x + (unsigned char)enemies[i].dx;
            if (x < 8 || x > 200) {
                enemies[i].dx = -enemies[i].dx;
                ++enemies[i].hits;
                ++bounces;
            }
            enemies[i].x = x;
            y = enemies[i].y + (unsigned char)enemies[i].dy;
            if (y < 16 || y > 180) {
                enemies[i].dy = -enemies[i].dy;
                ++bounces;
            }
            enemies[i].y = y;
        }
    }
}
