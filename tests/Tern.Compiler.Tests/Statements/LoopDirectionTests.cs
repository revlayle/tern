using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// A <c>for</c> loop's direction comes from its step alone (spec 5.1, decision 2026-11-11): without
/// <c>step</c> it counts up, and a loop counting up that can never run is an error (decision 104).
/// </summary>
public class LoopDirectionTests
{
    private static CompileError Error(string source) => Assert.Throws<CompileError>(() => TernCompiler.Compile(source));

    [Theory]
    [InlineData("for i in 9..=0 { }", "'9..=0' counts up, so it never runs. Write 'step -1' to count down")]
    [InlineData("for i in 10..0 { }", "'10..0' counts up, so it never runs")]
    [InlineData("for i in N..=0 { }", "'N..=0' counts up, so it never runs")]
    [InlineData("for i in -5..=-10 { }", "'-5..=-10' counts up, so it never runs")]
    [InlineData("for i in 1000..=990 { }", "'1000..=990' counts up, so it never runs")]
    [InlineData("for i in n..0 { }", "'n..0' counts up, so it never runs. Write 'step -1' to count down")]
    [InlineData("for i in n..0 using X { }", "'n..0' counts up, so it never runs")]
    [InlineData("for i in w..0 { }", "'w..0' counts up, so it never runs")]
    [InlineData("for i in (n + 1)..ZERO { }", "'(n + 1)..ZERO' counts up, so it never runs")]
    [InlineData("for c in 'Z'..='A' { }", "''Z'..='A'' counts up, so it never runs")]
    [InlineData("for c in screen'Z'..=screen'A' { }", "'screen'Z'..=screen'A'' counts up, so it never runs")]
    [InlineData("for i in t[1]..0 { }", "'t[1]..0' counts up, so it never runs")]
    [InlineData("for i in n  ..  0 using X { }", "'n  ..  0' counts up, so it never runs")]
    [InlineData("for i in 9..=\n        0 { }", "'9..= 0' counts up, so it never runs")]
    public void A_loop_counting_up_that_can_never_run_is_an_error(string loop, string expected)
    {
        var e = Error($"const N = 9\nconst ZERO = 0\nvar n: u8 = 3\nvar w: u16 = 3\nvar t: [u8; 4]\nfn main() {{\n    {loop}\n}}");
        Assert.Equal(expected, e.Description[..Math.Min(e.Description.Length, expected.Length)]);
        Assert.Equal(7, e.Pos.Line);
        if (expected.EndsWith("count down"))
            Assert.Equal("Write 'step -1' to count down", Tern.Compiler.Tools.Diagnostic.From(e, "main.tern").Suggestion);
    }

    [Theory]
    [InlineData("[i for i in 9..=0]", "'9..=0' counts up, so it never runs. Write 'step -1' to count down")]
    [InlineData("f()", "'3..=0' counts up, so it never runs (called from line 4). Write 'step -1' to count down")]
    public void A_compile_time_range_follows_the_same_rule(string value, string expected)
    {
        var e = Error($"const fn f() -> [int; _] {{ var t = repeat(0, 1)\n for i in 3..=0 {{ t[0] = i }}\n return t }}\ndata d: [u8; _] = {value}\nfn main() {{ }}");
        Assert.Equal(expected, e.Description);
    }

    [Theory]
    [InlineData(-3, new[] { -3, -2, -1, 0 })]
    [InlineData(0, new[] { 0 })]
    [InlineData(4, new int[0])]
    public void A_signed_loop_ending_at_0_counts_up_from_a_negative_start(int start, int[] expected)
    {
        var r = Run.Program($$"""
            var n: i8 = {{start}}
            var seen: [i8; 8]
            var count: u8
            fn main() {
                for i in n..=0 using X {
                    seen[count] = i
                    count += 1
                }
            }
            """);
        Assert.Equal(expected.Length, r.U8("count"));
        for (int k = 0; k < expected.Length; k++) Assert.Equal(expected[k], r.I8("seen", k));
    }

    /// <summary>An unsigned n..=0 runs once when n is 0 and never otherwise, with no error (decision 104).</summary>
    [Theory]
    [InlineData("u8", "for i in n..=0 using X {", 0, 1)]
    [InlineData("u8", "for i in n..=0 using X {", 5, 0)]
    [InlineData("u8", "for i in n..=0 {", 0, 1)]
    [InlineData("u8", "for i in n..=0 {", 5, 0)]
    [InlineData("u8", "for i in n..=0 {", 255, 0)]
    [InlineData("u8", "for i in n..=0 step s {", 0, 1)]
    [InlineData("u8", "for i in n..=0 step s {", 5, 0)]
    [InlineData("u16", "for i in n..=0 {", 0, 1)]
    [InlineData("u16", "for i in n..=0 {", 5, 0)]
    [InlineData("u16", "for i in n..=0 {", 256, 0)]
    public void An_unsigned_loop_up_to_0_runs_once_when_its_start_is_0(string type, string loop, int start, int expected)
    {
        var r = Run.Program($$"""
            var n: {{type}} = {{start}}
            var s: u8 = 3
            var count: u8
            var last: {{type}} = 99
            fn main() {
                {{loop}}
                    last = i
                    count += 1
                }
            }
            """);
        Assert.Equal(expected, r.U8("count"));
        Assert.Equal(expected == 1 ? 0 : 99, type == "u8" ? r.U8("last") : r.U16("last"));
    }

    /// <summary>Spec 11: 'for i in n..0 step -1 using X' is ldx n / beq done / ... dex / bne loop (decision 106).</summary>
    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(5, 15)]
    [InlineData(255, 32640)]
    public void An_unsigned_down_count_to_0_tests_only_its_start(int start, int sum)
    {
        var r = Run.Program($$"""
            var n: u8 = {{start}}
            var total: u16
            fn main() {
                for i in n..0 step -1 using X {
                    total += i
                }
            }
            """);
        Assert.Equal(sum, r.U16("total"));
        Assert.Equal("ldx n|beq main.fend0|txa|clc|adc total|sta total|bcc main.c3|inc total+1|dex|bne main.for2|rts", Listing.Code(r));
    }

    /// <summary>
    /// The other paths through the down-count's empty check: a counter in memory, in Y beside a
    /// variable held in X (the check comes before that load, and skips its store), a start that
    /// needs a compare, and a larger step. cnt starts at 7, so a skip that stored X would show.
    /// </summary>
    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData("for i in n..0 step -1 { add(i)\n cnt += 1 }", 0, 1, "lda n|sta main.i|beq")]
    [InlineData("for i in n..0 step -1 using Y { cnt += 1 }", 0, 1, "ldy n|beq main.fskip3|ldx cnt|")]
    [InlineData("for i in n..0 step -1 using Y { total += i\n cnt += 1 }", 0, 1, "ldy n|beq")]
    [InlineData("for i in (n + 1)..0 step -1 using X { total += i\n cnt += 1 }", 1, 1, "lda n|clc|adc #$01|tax|cpx #$00|beq")]
    [InlineData("for i in n..0 step -2 { total += i\n cnt += 1 }", 0, 2, "")]
    public void An_unsigned_down_count_to_0_skips_only_an_empty_range(string loop, int offset, int step, string start)
    {
        foreach (int n in new[] { 0, 1, 5, 254, 255 })
        {
            var r = Run.Program($$"""
                var n: u8 = {{n}}
                var total: u16
                var cnt: u8 = 7
                fn add(v: u8) { total += v }
                fn main() {
                    {{loop}}
                }
                """);
            int passes = 0, sum = 0;
            for (int i = (n + offset) & 255; i > 0; i -= step) { passes++; sum += i; }
            Assert.True((byte)(7 + passes) == r.U8("cnt"), $"n = {n}: cnt is {r.U8("cnt")}, not 7 + {passes}");
            if (!loop.Contains("{ cnt")) Assert.Equal(sum, r.U16("total"));
            Assert.StartsWith(start, Listing.Code(r));
            Assert.DoesNotContain("cpy", Listing.Code(r));
        }
    }

    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData(-3, 7)]
    [InlineData(0, 8)]
    [InlineData(4, 12)]
    public void A_signed_down_count_tests_its_sign_before_a_held_variable_is_loaded(int start, int expected)
    {
        var r = Run.Program($$"""
            var s: i8 = {{start}}
            var cnt: u8 = 7
            fn main() {
                for i in s..=0 step -1 using Y { cnt += 1 }
            }
            """);
        Assert.Equal(expected, r.U8("cnt"));
        Assert.StartsWith("ldy s|bmi main.fskip3|ldx cnt|", Listing.Code(r));
    }

    [Fact]
    public void A_const_fn_loop_ending_at_0_counts_up_from_a_negative_start()
    {
        var r = Run.Program("""
            const fn up(n: int) -> [int; _] {
                var t = repeat(0, 0 - n + 1)
                for i in n..=0 { t[i - n] = i + 10 }
                return t
            }
            data d: [u8; _] = up(-2)
            fn main() { }
            """);
        Assert.Equal([8, 9, 10], r.Ram.Bytes.AsSpan(r.Address("d"), 3).ToArray());
    }

    /// <summary>The spec 11 rows that count down keep their code with 'step -1'.</summary>
    [Trait("Shape", "nmos6502")]
    [Theory]
    [InlineData("for i in 9..=0 step -1 using X { out = i }", "ldx #$09|", "|dex|bpl|")]
    [InlineData("for i in n..0 step -1 using X { out = i }", "ldx n|", "|dex|bne|")]
    [InlineData("for i in s..=0 step -1 using X { sout = i }", "ldx s|bmi|", "|dex|bpl|")]
    [InlineData("for i in s..0 step -1 using X { sout = i }", "ldx s|beq|bmi|", "|dex|bne|")]
    public void Down_counts_with_step_minus_1_keep_their_code(string loop, string start, string step)
    {
        var r = Run.Program($$"""
            var n: u8 = 5
            var s: i8 = 5
            var out: u8
            var sout: i8
            fn main() {
                {{loop}}
            }
            """);
        // Branches without their targets.
        var code = string.Join("|", Listing.Code(r).Split('|').Select(i => i.Split(' ')[0] is "bpl" or "bne" or "beq" or "bmi" or "bcc" ? i.Split(' ')[0] : i));
        Assert.StartsWith(start, code);
        Assert.Contains(step, code);
    }
}
