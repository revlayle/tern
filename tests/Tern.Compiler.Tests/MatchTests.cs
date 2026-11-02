using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary><c>match</c> (spec 5): values, alternatives, ranges and <c>_</c>; compare chains and jump tables.</summary>
public class MatchTests
{
    /// <summary>The instructions of a function, without addresses, bytes and cycles.</summary>
    private static string Code(Run r, string function = "main")
    {
        var listing = r.Result.Listing;
        var start = listing.IndexOf($"\n{function}:", StringComparison.Ordinal);
        var code = new List<string>();
        foreach (var l in listing[(start + 1)..].Split('\n').Skip(1).Select(l => l.TrimEnd('\r')))
        {
            if (l.EndsWith(':') && !l.StartsWith(' ') && !l.StartsWith('$') && !l.StartsWith(';') && !l.StartsWith($"{function}.")) break;
            if (l.StartsWith('$') && l.Length > 17 && !l.Contains("; hi:") && !l.Contains("; lo:")) code.Add(l[17..].Split(';')[0].Trim());
            else if (l.StartsWith($"{function}.")) code.Add(l.Trim());
        }
        return string.Join("|", code);
    }

    /// <summary>Runs <paramref name="classify"/> (a function <c>f(v) -> u8</c>) on every byte and returns the results.</summary>
    private static byte[] Table(string classify, string type = "u8")
    {
        var r = Run.Program($$"""
            var out: [u8; 256]
            {{classify}}
            fn main() {
                for i in 0..=255 { out[i] = f(i as {{type}}) }
            }
            """, maxCycles: 20_000_000);
        return r.Ram.Bytes.AsSpan(r.Address("out"), 256).ToArray();
    }

    [Fact]
    public void A_compare_chain_matches_values_alternatives_and_ranges()
    {
        var res = Table("""
            fn f(v: u8) -> u8 {
                match v {
                    0 => return 10
                    1, 2 => return 20
                    3..=7 => return 30
                    200..=255 => return 40
                    100, 150..=160 => { return 60 }
                    _ => return 50
                }
            }
            """);
        for (int v = 0; v < 256; v++)
        {
            int expected = v == 0 ? 10 : v is 1 or 2 ? 20 : v is >= 3 and <= 7 ? 30 : v >= 200 ? 40
                : v is 100 or (>= 150 and <= 160) ? 60 : 50;
            Assert.True(expected == res[v], $"f({v}) = {res[v]}, expected {expected}");
        }
    }

    [Fact]
    public void A_dense_match_uses_a_jump_table()
    {
        var source = """
            fn f(v: u8) -> u8 {
                var r: u8 = 99
                match v {
                    10 => r = 0
                    11 => r = 1
                    12, 13 => r = 2
                    14 => r = 3
                    15 => r = 4
                    17 => r = 5
                    18 => r = 6
                    19..=20 => r = 7
                }
                return r
            }
            """;
        var res = Table(source);
        int[] expected = [0, 1, 2, 2, 3, 4, 99, 5, 6, 7, 7];
        for (int v = 0; v < 256; v++)
        {
            int e = v is >= 10 and <= 20 ? expected[v - 10] : 99;
            Assert.True(e == res[v], $"f({v}) = {res[v]}, expected {e}");
        }
        var r = Run.Program("var out: [u8; 256]\n" + source + "\nfn main() { out[0] = f(3) }");
        Assert.Contains("sec|sbc #$0A|cmp #$0B|bcs f.mend", Code(r, "f"));
        Assert.Contains("tax|lda f.jumphi", Code(r, "f"));
        Assert.Matches(@"lda f\.jumphi\d+,x\|pha\|lda f\.jumplo\d+,x\|pha\|rts", Code(r, "f"));
    }

    [Fact]
    public void A_jump_table_from_zero_with_a_default()
    {
        var res = Table("""
            fn f(v: u8) -> u8 {
                match v {
                    0 => return 7
                    1 => return 6
                    2 => return 5
                    3 => return 4
                    4 => return 3
                    5 => return 2
                    6 => return 1
                    7 => return 0
                    _ => return 200
                }
            }
            """);
        for (int v = 0; v < 256; v++) Assert.Equal(v < 8 ? 7 - v : 200, res[v]);
    }

    [Fact]
    public void Signed_values_in_a_compare_chain()
    {
        var res = Table("""
            fn f(v: i8) -> u8 {
                match v {
                    -128 => return 1
                    -127..=-1 => return 2
                    0 => return 3
                    1..=10 => return 4
                    127 => return 5
                    _ => return 6
                }
            }
            """, "i8");
        for (int i = 0; i < 256; i++)
        {
            int v = (sbyte)i;
            int expected = v == -128 ? 1 : v < 0 ? 2 : v == 0 ? 3 : v <= 10 ? 4 : v == 127 ? 5 : 6;
            Assert.True(expected == res[i], $"f({v}) = {res[i]}, expected {expected}");
        }
    }

    [Fact]
    public void Signed_values_in_a_jump_table()
    {
        var res = Table("""
            fn f(v: i8) -> u8 {
                match v {
                    -4 => return 1
                    -3 => return 2
                    -2 => return 3
                    -1 => return 4
                    0 => return 5
                    1 => return 6
                    2 => return 7
                    3 => return 8
                    _ => return 0
                }
            }
            """, "i8");
        for (int i = 0; i < 256; i++)
        {
            int v = (sbyte)i;
            Assert.Equal(v is >= -4 and <= 3 ? v + 5 : 0, res[i]);
        }
    }

    [Fact]
    public void The_value_is_evaluated_once()
    {
        var r = Run.Program("""
            var calls: u8
            var r: u8
            fn next() -> u8 {
                calls += 1
                return calls + 4
            }
            fn main() {
                match next() {
                    1 => r = 1
                    2..=4 => r = 2
                    5 => r = 3
                    _ => r = 4
                }
            }
            """);
        Assert.Equal(1, r.U8("calls"));
        Assert.Equal(3, r.U8("r"));
    }

    [Fact]
    public void Break_and_continue_inside_a_match_leave_the_loop()
    {
        var r = Run.Program("""
            var sum: u8
            var stop: u8
            fn main() {
                for i in 0..100 {
                    match i {
                        3 => continue
                        10 => {
                            stop = i
                            break
                        }
                    }
                    sum += i
                }
            }
            """);
        Assert.Equal(10, r.U8("stop"));
        Assert.Equal(45 - 3, r.U8("sum"));
    }

    [Fact]
    public void Nested_matches_and_a_match_without_a_default()
    {
        var r = Run.Program("""
            var a: u8 = 2
            var b: u8 = 7
            var r: u8 = 1
            var none: u8 = 5
            fn main() {
                match a {
                    1 => r = 10
                    2 => match b {
                        7 => r = 27
                        _ => r = 20
                    }
                }
                match a {
                    0 => none = 0
                    5 => none = 0
                }
            }
            """);
        Assert.Equal(27, r.U8("r"));
        Assert.Equal(5, r.U8("none"));
    }

    [Fact]
    public void A_jump_table_inside_a_loop_using_x_uses_y()
    {
        var r = Run.Program("""
            var out: [u8; 12]
            fn main() {
                for i in 0..12 using X {
                    match i {
                        0 => out[i] = 9
                        1 => out[i] = 8
                        2 => out[i] = 7
                        3 => out[i] = 6
                        4 => out[i] = 5
                        5 => out[i] = 4
                        6 => out[i] = 3
                        7 => out[i] = 2
                        _ => out[i] = 1
                    }
                }
            }
            """);
        Assert.Equal(new byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 1, 1, 1 }, r.Ram.Bytes[r.Address("out")..(r.Address("out") + 12)]);
        Assert.Matches(@"tay\|lda main\.jumphi\d+,y", Code(r));
    }

    [Fact]
    public void A_match_where_every_arm_returns_ends_the_function()
    {
        var r = Run.Program("""
            var r: u8
            fn f(v: u8) -> u8 {
                match v {
                    0 => return 1
                    _ => return 2
                }
            }
            fn main() { r = f(0) + f(5) }
            """);
        Assert.Equal(3, r.U8("r"));
    }

    [Fact]
    public void Only_a_default_arm()
    {
        var r = Run.Program("""
            var calls: u8
            var r: u8
            fn next() -> u8 {
                calls += 1
                return 0
            }
            fn main() {
                match next() {
                    _ => r = 4
                }
                match r { }
            }
            """);
        Assert.Equal(1, r.U8("calls"));
        Assert.Equal(4, r.U8("r"));
    }

    [Theory]
    // The last test is inverted, so the last arm's body follows the chain.
    [InlineData("match x {\n 0 => r = 1\n }", "lda x|cmp #$00|bne main.mend0|main.case1:|lda #$01|sta r|main.mend0:|rts")]
    [InlineData("match x {\n 0 => r = 1\n 5 => r = 2\n _ => r = 3\n }",
        "lda x|cmp #$00|beq main.case1|cmp #$05|bne main.default3|main.case2:|lda #$02|sta r|jmp main.mend0|main.case1:|lda #$01|sta r|jmp main.mend0|main.default3:|lda #$03|sta r|main.mend0:|rts")]
    // Ranges: one compare at either end of the byte, two in the middle.
    [InlineData("match x {\n 0..=9 => r = 1\n 250..=255 => r = 2\n 20..=29 => r = 3\n }",
        "lda x|cmp #$0A|bcc main.case1|cmp #$FA|bcs main.case2|cmp #$14|bcc main.mend0|cmp #$1E|bcs main.mend0|main.case3:|lda #$03|sta r|jmp main.mend0|main.case1:|lda #$01|sta r|jmp main.mend0|main.case2:|lda #$02|sta r|main.mend0:|rts")]
    [InlineData("match s {\n -1 => r = 1\n }", "lda s|eor #$80|cmp #$7F|bne main.mend0|main.case1:|lda #$01|sta r|main.mend0:|rts")]
    public void Code_shapes(string statement, string expected)
    {
        var r = Run.Program($$"""
            var x: u8
            var s: i8
            var r: u8
            fn main() {
                {{statement}}
            }
            """);
        Assert.Equal(expected, Code(r));
    }

    [Theory]
    [InlineData("var w: u16\nfn main() { match w { 0 => w = 1 } }", "'match' on a 16-bit value is not supported yet")]
    [InlineData("var b: bool\nfn main() { match b { _ => b = true } }", "'match' needs a u8 or i8 value, not bool")]
    [InlineData("var x: u8\nvar y: u8\nfn main() { match x { y => x = 1 } }", "A match pattern must be a constant")]
    [InlineData("var x: u8\nfn main() { match x {\n 1 => x = 1\n 0, 1 => x = 2\n } }", "1 is already matched at 3:2; each value may appear in one pattern only")]
    [InlineData("var x: u8\nfn main() { match x {\n 1..=5 => x = 1\n 5..=9 => x = 2\n } }", "5 is already matched at 3:2")]
    [InlineData("var x: u8\nfn main() { match x {\n _ => x = 1\n 0 => x = 2\n } }", "This arm can never run: '_' above it matches every value. Put '_' last")]
    [InlineData("var x: u8\nfn main() { match x { 1, _ => x = 1 } }", "'_' matches every value, so it must be alone in its arm")]
    [InlineData("var x: u8\nfn main() { match x { _, 1 => x = 1 } }", "'_' matches every value, so it must be alone in its arm")]
    [InlineData("var x: u8\nfn main() { match x { 0..5 => x = 1 } }", "A range in a match includes its end: write 'low..=high'")]
    [InlineData("var x: u8\nfn main() { match x { 5..=3 => x = 1 } }", "The range 5..=3 is empty; write the smaller value first")]
    [InlineData("var x: u8\nfn main() { match x { 300 => x = 1 } }", "300 does not fit in u8 (0 to 255)")]
    [InlineData("var x: u8\nfn main() { match x { -1 => x = 1 } }", "-1 does not fit in u8")]
    [InlineData("var s: i8\nfn main() { match s { 200 => s = 1 } }", "200 does not fit in i8 (-128 to 127)")]
    [InlineData("var x: u8\nfn main() { match x { 0 x = 1 } }", "Expected '=>' after the match pattern")]
    [InlineData("var x: u8\nfn main() { match x { 0 => } }", "Expected a statement or '{ }' after '=>'")]
    [InlineData("var x: u8\nfn main() { match x {\n 0 => x = 1\n", "'match' is not closed with '}'")]
    [InlineData("var x: u8\nfn main() { match x 0 => x = 1 }", "Expected '{' after the match value")]
    [InlineData("var x: u8\nfn main() { match x { 0 => break } }", "'break' and 'continue' must be inside a loop")]
    [InlineData("var x: u8\nfn main() { match x { 0 => { var y: u8 = 1 }\n 1 => x = y } }", "'y' is not declared")]
    public void Reports_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
    }
}
