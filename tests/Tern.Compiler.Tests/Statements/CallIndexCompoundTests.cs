namespace Tern.Compiler.Tests;

/// <summary>
/// V1 fixes 2 (smashy smashy gap 3): a compound assignment whose index calls a function, <c>a[f()] += 1</c>.
/// The index is computed once, first (spec 5), into the statement temporary, and the element changed as
/// <c>a[i] += 1</c> would be: <c>jsr f / tax / inc a,x</c>. For arrays, split 16-bit arrays, arrays of
/// structs and pointers.
/// </summary>
public class CallIndexCompoundTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void The_call_runs_once_and_changes_its_element(int k)
    {
        var r = Run.Program($$"""
            struct Enemy { hp: u8, x: u8 }
            var a: [u8; 8] = [0, 10, 20, 30, 40, 50, 60, 255]
            var w: [u16; 8] = [0, 255, 256, 65535, 1000, 65280, 7, 511]
            var e: [Enemy; 8]
            var b: [u8; 8] = [7, 6, 5, 4, 3, 2, 1, 0]
            var buf: [u8; 8] = [5, 5, 5, 5, 5, 5, 5, 5]
            var p: ptr u8
            var calls: u8
            var gs: u8
            var k: u8 = {{k}}
            fn f() -> u8 {
                calls += 1
                return k
            }
            fn g() -> u8 {
                gs += 1
                return 100
            }
            fn main() {
                for i in 0..8 { e[i].hp = 9 }
                p = addr(buf)
                a[f()] += 1
                a[f()] -= g()
                w[f()] += 1
                w[f()] += g() as u16
                e[f()].hp -= 1
                a[b[f()]] += 1
                p[f()] += 1
                a[f()] |= $80
            }
            """);
        Assert.Equal(8, r.U8("calls"));
        Assert.Equal(2, r.U8("gs"));
        byte[] a = [0, 10, 20, 30, 40, 50, 60, 255];
        a[k] = (byte)(a[k] + 1 - 100);
        a[7 - k] = (byte)(a[7 - k] + 1);
        a[k] |= 0x80;
        for (int i = 0; i < 8; i++) Assert.Equal(a[i], r.U8("a", i));
        ushort[] w = [0, 255, 256, 65535, 1000, 65280, 7, 511];
        Assert.Equal((ushort)(w[k] + 101), r.U8("w", k) | r.U8("w", 8 + k) << 8);   // a split array
        Assert.Equal(8, r.U8("e", k));   // the hp table
        Assert.Equal(6, r.U8("buf", k));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void The_code_is_the_code_of_the_index_in_a_variable()
    {
        var r = Run.Program("""
            var a: [u8; 8]
            var b: [u8; 8]
            var p: ptr u8
            fn f() -> u8 { return 2 }
            fn g() -> u8 { return 10 }
            fn inc1() { a[f()] += 1 }
            fn add_call() { a[f()] += g() }
            fn nested() { a[b[f()]] += 1 }
            fn pointer() { p[f()] += 1 }
            fn main() {
                p = addr(a)
                inc1()
                add_call()
                nested()
                pointer()
            }
            """);
        Assert.Equal("jsr f|tax|inc a,x|rts", Listing.Code(r, "inc1"));
        // Kept across the call to g in the statement temporary.
        Assert.Matches(@"^jsr f\|sta add_call\._t\d+\|jsr g\|clc\|ldx add_call\._t\d+\|adc a,x\|sta a,x\|rts$", Listing.Code(r, "add_call"));
        Assert.Equal("jsr f|tax|lda b,x|tax|inc a,x|rts", Listing.Code(r, "nested"));
        Assert.Equal("jsr f|tay|lda (p),y|clc|adc #$01|sta (p),y|rts", Listing.Code(r, "pointer"));
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void A_volatile_element_is_read_and_written_once()
    {
        var r = Run.Program("""
            volatile var regs: [u8; 4] @ $C000
            fn f() -> u8 { return 1 }
            fn main() { regs[f()] += 3 }
            """,
            setup: ram => ram.Bytes[0xC001] = 0x40,
            watch: _ => [0xC001]);
        Assert.Equal(1, r.Log!.Reads(0xC001));
        Assert.Equal([(byte)0x43], r.Log.Writes(0xC001));
    }
}
