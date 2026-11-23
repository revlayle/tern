namespace Tern.Compiler.Tests;

/// <summary>
/// V1 fixes 2, gap 32 (spec 6.6): a <c>for ... using X</c> / <c>using Y</c> loop may pass its variable itself
/// to a parameter in that register of a function that keeps it; the value is already there, so the call
/// loads nothing. Any other argument for that parameter is still an error.
/// </summary>
public class LoopRegisterArgumentTests
{
    [Theory]
    [InlineData("X")]
    [InlineData("Y")]
    public void The_loop_variable_is_passed_in_its_register(string reg)
    {
        var r = Run.Program($$"""
            var timer: [u8; 6] = [1, 0, 3, 4, 0, 6]
            var x: [u8; 6] = [10, 20, 30, 40, 50, 255]
            var count: u8
            var last: u8
            fn hits(s: u8 using {{reg}}) -> bool keeps {{reg}} {
                last = s
                return x[s] >= 30
            }
            fn main() {
                for s in 0..6 using {{reg}} {
                    if timer[s] != 0 && hits(s) {
                        count += 1
                    }
                }
            }
            """);
        Assert.Equal(3, r.U8("count"));
        Assert.Equal(5, r.U8("last"));
        string t = reg == "X" ? "tx" : "ty";
        var main = Listing.Instructions(r);
        Assert.Contains("jsr hits", main);
        Assert.DoesNotContain(main, i => i.StartsWith(t) || i.StartsWith("sta main."));
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void The_call_loads_nothing()
    {
        var r = Run.Program("""
            var timer: [u8; 6]
            var count: u8
            fn hits(s: u8 using Y) -> bool keeps Y { return s >= 3 }
            fn main() {
                for s in 0..6 using Y {
                    if timer[s] == 0 && hits(s) { count += 1 }
                }
            }
            """);
        Assert.Equal(3, r.U8("count"));
        Assert.Matches(@"^ldy #\$00\|lda timer,y\|bne main\.endif\d+\|jsr hits\|bcc main\.endif\d+\|inc count\|iny\|cpy #\$06\|bne main\.for\d+\|rts$", Listing.Code(r));
    }

    [Theory]
    // Another value would overwrite the loop variable.
    [InlineData("fn f(v: u8 using Y) keeps Y { }\nfn main() { for i in 0..3 using Y { f(i + 1) } }", 2, 37, "'f' takes 'v' in Y, which holds the loop variable. Remove 'using Y' from the loop")]
    // The function does not keep Y.
    [InlineData("fn f(v: u8 using Y) { }\nfn main() { for i in 0..3 using Y { f(i) } }", 2, 13, "The loop body calls a function, which can change Y. Remove 'using Y', or call only routines that keep Y")]
    // The loop's register, but another parameter's.
    [InlineData("fn f(v: u8 using X) keeps X, Y { }\nfn main() { for i in 0..3 using Y { f(i) } }", 2, 37, null)]
    public void Other_arguments_are_errors(string source, int line, int column, string? expected)
    {
        if (expected is null) { Run.Program(source); return; }
        Expect.Error(source, line, column, expected);
    }
}
