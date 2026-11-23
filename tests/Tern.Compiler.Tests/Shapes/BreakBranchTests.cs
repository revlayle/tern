namespace Tern.Compiler.Tests;

/// <summary>
/// V1 fixes 2, gap 33: <c>if c { break }</c> and <c>if c { continue }</c> are one branch to the loop's
/// end or its next pass, not a branch over a <c>jmp</c>; a target out of reach is a long branch. A
/// branch over a <c>jmp</c> left by other code becomes the opposite branch. Spec 11.
/// </summary>
public class BreakBranchTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(255)]
    public void Break_and_continue_leave_and_skip_as_written(int limit)
    {
        var r = Run.Program($$"""
            var limit: u8 = {{limit}}
            var vals: [u8; 16] = [3, 0, 7, 255, 1, 0, 9, 128, 0, 2, 5, 0, 4, 6, 0, 8]
            var passes: u8
            var sum: u16
            var stopped: u8 = 99
            fn main() {
                var k: u8 = 0
                while true {
                    if k == limit { break }
                    k += 1
                    passes += 1
                }
                for i in 0..16 {
                    if vals[i] == 0 { continue }
                    if vals[i] > limit { stopped = i; break }
                    sum += vals[i] as u16
                }
            }
            """);
        Assert.Equal(limit, r.U8("passes"));
        byte[] vals = [3, 0, 7, 255, 1, 0, 9, 128, 0, 2, 5, 0, 4, 6, 0, 8];
        int sum = 0, stopped = 99;
        for (int i = 0; i < 16; i++)
        {
            if (vals[i] == 0) continue;
            if (vals[i] > limit) { stopped = i; break; }
            sum += vals[i];
        }
        Assert.Equal(sum, r.U16("sum"));
        Assert.Equal(stopped, r.U8("stopped"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void The_code_is_the_spec_shape()
    {
        var r = Run.Program("""
            var tc: u8
            var c1: u8 = 5
            var vals: [u8; 8]
            var sum: u8
            fn leave() {
                while true {
                    vals[tc] = 1
                    if tc == c1 { break }
                    tc += 1
                }
            }
            fn skip() {
                for i in 0..8 {
                    if vals[i] == 0 { continue }
                    sum += 1
                }
            }
            fn main() {
                leave()
                skip()
            }
            """);
        Assert.Equal(5, r.U8("tc"));
        Assert.Equal(6, r.U8("sum"));
        Assert.Matches(@"^lda #\$01\|ldx tc\|sta vals,x|txa|cmp c1\|lda tc\|cmp c1\|beq leave\.wend\d+\|inc tc\|jmp leave\.while\d+\|rts$", Listing.Code(r, "leave"));
        var skip = Listing.Instructions(r, "skip");
        Assert.Contains(skip, i => i.StartsWith("beq skip."));
        Assert.DoesNotContain(skip, i => i.StartsWith("jmp"));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_break_out_of_reach_is_a_long_branch()
    {
        string body = string.Concat(Enumerable.Range(0, 40).Select(k => $"        vals[{k % 8}] = k + {k}\n"));
        var r = Run.Program($$"""
            var vals: [u8; 8]
            var k: u8
            fn main() {
                while true {
                    k += 1
                    if k == 3 { break }
            {{body}}
                }
            }
            """);
        Assert.Equal(3, r.U8("k"));
        Assert.Equal(2 + 39, r.U8("vals", 7));
        Assert.Contains(Listing.Instructions(r), i => i.StartsWith("bne *+5 / jmp main.wend"));
    }

    /// <summary>A loop whose last statement is <c>if c { break }</c> branches back to its top on the opposite test.</summary>
    [Trait("Shape", "nmos6502")]
    [Fact]
    public void A_break_at_the_end_of_an_endless_loop_branches_back_on_the_opposite_test()
    {
        var r = Run.Program("""
            var k: u8
            fn main() {
                while true {
                    k += 1
                    if k >= 7 { break }
                }
            }
            """);
        Assert.Equal(7, r.U8("k"));
        Assert.Matches(@"^inc k\|lda k\|cmp #\$07\|bcc main\.while\d+\|rts$", Listing.Code(r));
    }
}
