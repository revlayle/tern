namespace Tern.Compiler.Tests;

/// <summary>
/// V1 fixes, small shapes: a <c>for</c> loop's end is read in place, with no copy, when the body
/// cannot change it (a global too, when the body calls nothing and writes through no pointer), and
/// an inclusive range that starts at the counter's lowest value (<c>0..=n</c>) has no empty check.
/// </summary>
public class LoopBoundTests
{
    public static TheoryData<int, int> Bounds()
    {
        var data = new TheoryData<int, int>();
        foreach (var (a, b) in new[] { (0, 0), (0, 1), (2, 5), (5, 5), (6, 5), (0, 255), (255, 255), (200, 3) })
            data.Add(a, b);
        return data;
    }

    [Theory]
    [MemberData(nameof(Bounds))]
    public void Loops_run_as_often_as_their_range_says(int a, int b)
    {
        var r = Run.Program($$"""
            var r0: u8 = {{a}}
            var r1: u8 = {{b}}
            var n1: u16
            var n2: u16
            var n3: u16
            var n4: u16
            var n5: u16
            var p: ptr u8
            var seen: [u8; 256]
            fn shrink() { r1 = 0 }
            fn main() {
                for i in r0..=r1 { n1 += 1 }
                for i in 0..=r1 { n2 += 1; seen[i] = 1 }
                for i in r0..r1 { n3 += 1 }
                p = addr(r1) as ptr u8
                for i in r0..=r1 {           // the end was read before the loop: changing r1 does not stop it
                    n4 += 1
                    if i == r0 { shrink() }
                }
                r1 = {{b}}
                for i in r0..=r1 {
                    n5 += 1
                    if i == r0 { p[0] = 0 }
                }
            }
            """);
        int inclusive = a <= b ? b - a + 1 : 0;
        Assert.Equal(inclusive, r.U16("n1"));
        Assert.Equal(b + 1, r.U16("n2"));
        Assert.Equal(a < b ? b - a : 0, r.U16("n3"));
        Assert.Equal(inclusive, r.U16("n4"));
        Assert.Equal(inclusive, r.U16("n5"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void The_end_is_read_in_place_and_0_to_n_has_no_empty_check()
    {
        var r = Run.Program("""
            var r0: u8
            var r1: u8
            var m: [u8; 40]
            fn global() { for t in r0..=r1 { m[t] = 1 } }
            fn from_zero() { for t in 0..=r1 { m[t] = 2 } }
            fn writes() { for t in r0..=r1 { m[t] = 3; r1 = 3 } }
            fn calls() { for t in r0..=r1 { m[t] = 4; from_zero() } }
            fn main() {
                global()
                from_zero()
                writes()
                calls()
            }
            """);
        Assert.Matches(@"^ldx r0\|cpx r1\|beq global\.eq\d+\|bcs global\.fend\d+\|lda #\$01\|sta m,x\|cpx r1\|", Listing.Code(r, "global"));
        Assert.Matches(@"^ldx #\$00\|lda #\$02\|sta m,x\|cpx r1\|beq from_zero\.fend\d+\|inx\|bne from_zero\.for\d+\|rts$", Listing.Code(r, "from_zero"));
        // A body that assigns the end, or calls a function (which may), reads a copy.
        Assert.StartsWith("lda r1|sta writes._t", Listing.Code(r, "writes"));
        Assert.StartsWith("lda r1|sta calls._t", Listing.Code(r, "calls"));
    }
}
