namespace Tern.Compiler.Tests;

/// <summary>M19: the <c>cpu</c> library module, the flags after an asm block (spec 7, 10.1).</summary>
public class CpuModuleTests
{
    private static List<string> Code(Run r, string function)
    {
        var lines = r.Result.Listing.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        int start = lines.IndexOf($"{function}:");
        Assert.True(start >= 0, $"No function {function} in the listing");
        int end = lines.FindIndex(start + 1, l => l.EndsWith(':') && !l.Contains('.') && !l.StartsWith(' '));
        return lines[start..(end < 0 ? lines.Count : end)].Select(l => l.Trim()).Where(l => l.StartsWith('$')).ToList();
    }

    /// <summary>The flag that <c>lda #a / clc / adc #b</c> leaves.</summary>
    private static bool Flag(string name, int a, int b)
    {
        int sum = a + b, signed = (sbyte)(byte)a + (sbyte)(byte)b;
        return name switch
        {
            "carry" => sum > 255,
            "zero" => (sum & 0xFF) == 0,
            "negative" => (sum & 0x80) != 0,
            _ => signed < -128 || signed > 127,
        };
    }

    public static TheoryData<string, int, int> Cases()
    {
        var data = new TheoryData<string, int, int>();
        foreach (var flag in new[] { "carry", "zero", "negative", "overflow" })
            foreach (var (a, b) in new[] { (0, 0), (1, 1), (100, 28), (127, 1), (128, 128), (255, 1), (200, 100), (255, 255) })
                data.Add(flag, a, b);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_flag_is_read_by_if_not_and_a_stored_value(string flag, int a, int b)
    {
        string block = $"asm {{\n lda #{a}\n clc\n adc #{b}\n }}";
        var r = Run.Program($$"""
            import cpu
            var yes: u8
            var no: u8
            var stored: bool
            fn main() {
                {{block}}
                if cpu.{{flag}}() { yes = 1 }
                {{block}}
                if !cpu.{{flag}}() { no = 1 }
                {{block}}
                stored = cpu.{{flag}}()
            }
            """);
        bool set = Flag(flag, a, b);
        Assert.Equal(set ? 1 : 0, r.U8("yes"));
        Assert.Equal(set ? 0 : 1, r.U8("no"));
        Assert.Equal(set ? 1 : 0, r.U8("stored"));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_while_loop_reads_the_flag_of_the_block_before_it_and_at_the_end_of_its_body(string flag, int a, int b)
    {
        // The body's block leaves every flag clear, so the loop runs at most once.
        var r = Run.Program($$"""
            import cpu
            var n: u8
            fn main() {
                asm {
                    lda #{{a}}
                    clc
                    adc #{{b}}
                }
                while cpu.{{flag}}() {
                    n += 1
                    asm {
                        lda #1
                        clc
                        adc #1
                    }
                }
            }
            """);
        Assert.Equal(Flag(flag, a, b) ? 1 : 0, r.U8("n"));
    }

    [Theory]
    [InlineData("carry", "sec")]
    [InlineData("zero", "lda #0")]
    [InlineData("negative", "lda #$80")]
    [InlineData("overflow", "lda #$40\n clc\n adc #$40")]
    public void A_while_not_loop_stops_when_the_body_sets_the_flag(string flag, string setFlag)
    {
        var r = Run.Program($$"""
            import cpu
            var n: u8
            fn main() {
                asm {
                    lda #1
                    clc
                    adc #1
                }
                while !cpu.{{flag}}() {
                    n += 1
                    asm {
                        {{setFlag}}
                    }
                }
            }
            """);
        Assert.Equal(1, r.U8("n"));
    }

    [Theory]
    [InlineData("carry", "bcc")]
    [InlineData("zero", "bne")]
    [InlineData("negative", "bpl")]
    [InlineData("overflow", "bvc")]
    public void The_listing_shows_only_the_branch(string flag, string branch)
    {
        var r = Run.Program($$"""
            import cpu
            var yes: u8
            fn main() {
                asm {
                    lda #100
                    clc
                    adc #28
                }
                if cpu.{{flag}}() { yes = 1 }
            }
            """);
        var main = Code(r, "main");
        int adc = main.FindIndex(l => l.Contains(" adc "));
        Assert.Contains($" {branch} ", main[adc + 1]);
        Assert.DoesNotContain(main, l => l.Contains("jsr"));
    }
}
