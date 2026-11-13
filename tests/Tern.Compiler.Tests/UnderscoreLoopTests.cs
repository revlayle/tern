namespace Tern.Compiler.Tests;

/// <summary>
/// The `for _` follow-up (spec 5.1, decision 118): a loop with `_` is never slower than the same loop
/// with a named counter the body does not read, and gives the same results.
/// </summary>
public class UnderscoreLoopTests
{
    private static Run Loop(string header, string body, int m) => Run.Program($$"""
        var buf: [u8; 256]
        var n: u8 = 7
        var k: u8 = 3
        var m: u8 = {{m}}
        var w: u16 = 0
        fn main() {
            for {{header}} {
                {{body}}
            }
        }
        """);

    public static TheoryData<string, string, int> Forms()
    {
        string[] ranges =
        [
            "0..10", "0..=255", "9..=0 step -1", "255..=0 step -1", "0..2", "0..=0", "0..20 step 3", "20..=0 step -2",
            "0..m", "1..=m", "m..=0 step -1", "m..0 step -1", "0..m step 2", "0..300", "0..=256",
        ];
        string[] bodies =
        [
            "n += 1",
            "n -= 2",
            "buf[n] = k\n n += 1",
            "buf[k] = buf[k] + 1\n k += 1",
            "k = buf[n] + k\n n += 1",
            "n += 1\n if n == 100 { break }",
            "if k == 5 { continue }\n k += 1\n w += 1",
            "w += 1",
        ];
        int[] counts = [0, 1, 255];
        var data = new TheoryData<string, string, int>();
        foreach (var range in ranges)
            foreach (var body in bodies)
                foreach (var count in range.Contains('m') ? counts : [0])
                    data.Add(range, body, count);
        return data;
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public void An_underscore_loop_is_never_slower_than_its_named_twin(string range, string body, int m)
    {
        var plain = Loop($"_ in {range}", body, m);
        var named = Loop($"i in {range}", body, m);
        foreach (var name in new[] { "n", "k", "m" })
            Assert.Equal(named.U8(name), plain.U8(name));
        Assert.Equal(named.U16("w"), plain.U16("w"));
        for (int i = 0; i < 256; i++)
            Assert.Equal(named.U8("buf", i), plain.U8("buf", i));
        Assert.True(plain.Cpu.Cycles <= named.Cpu.Cycles,
            $"for _ took {plain.Cpu.Cycles} cycles, for i {named.Cpu.Cycles}\n{plain.Result.Listing}\n{named.Result.Listing}");
    }

    /// <summary>The instructions of main, separated by '|'.</summary>
    private static string Code(string listing)
    {
        var start = listing.IndexOf("\nmain:", StringComparison.Ordinal);
        var code = new List<string>();
        foreach (var l in listing[(start + 1)..].Split('\n').Skip(1).Select(l => l.TrimEnd('\r')))
        {
            if (l.EndsWith(':') && !l.StartsWith(' ') && !l.StartsWith("main.")) break;
            if (l.StartsWith('$') && l.Length > 17) code.Add(l[17..].Split(';')[0].Trim());
        }
        return string.Join("|", code);
    }

    [Fact]
    public void A_repeat_loop_holds_a_variable_in_the_free_index_register()
    {
        // Decision 118: 107 cycles before (inc n in the loop), at most the named loop's 83 now.
        var plain = Loop("_ in 9..=0 step -1", "n += 1", 0);
        var named = Loop("i in 9..=0 step -1", "n += 1", 0);
        Assert.Equal(17, plain.U8("n"));
        Assert.Equal("ldx #$0A|ldy n|iny|dex|bne main.for2|sty n|rts", Code(plain.Result.Listing));
        Assert.True(plain.Cpu.Cycles <= named.Cpu.Cycles, $"{plain.Cpu.Cycles} > {named.Cpu.Cycles}");
    }
}
