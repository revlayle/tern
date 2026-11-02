/* A small stack machine interpreter, dispatching with switch. */
enum { PUSH, ADD, SUB, DUP, SWAP, DROP, JNZ, DEC, OVER, HALT };

static const unsigned char code[14] = {PUSH, 0, PUSH, 10, SWAP, OVER, ADD, SWAP, DEC, DUP, JNZ, 4, DROP, HALT};
unsigned char stack[16];
unsigned char sp;
unsigned char result;
unsigned int steps;

void main(void) {
    unsigned char pc = 0, op, t, running = 1;
    while (running) {
        op = code[pc];
        ++pc;
        ++steps;
        switch (op) {
        case PUSH: stack[sp] = code[pc]; ++sp; ++pc; break;
        case ADD: --sp; stack[sp - 1] = stack[sp - 1] + stack[sp]; break;
        case SUB: --sp; stack[sp - 1] = stack[sp - 1] - stack[sp]; break;
        case DUP: stack[sp] = stack[sp - 1]; ++sp; break;
        case SWAP: t = stack[sp - 1]; stack[sp - 1] = stack[sp - 2]; stack[sp - 2] = t; break;
        case DROP: --sp; break;
        case JNZ: --sp; if (stack[sp] != 0) pc = code[pc]; else ++pc; break;
        case DEC: --stack[sp - 1]; break;
        case OVER: stack[sp] = stack[sp - 2]; ++sp; break;
        default: running = 0; break;
        }
    }
    result = stack[0];
}
