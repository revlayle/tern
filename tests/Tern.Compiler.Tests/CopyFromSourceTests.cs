namespace Tern.Compiler.Tests;

/// <summary>
/// M18 F18: a one-byte local that copies other memory is read from that memory while it is
/// unchanged, and kept in a free register where the memory is overwritten (Optimizer.ReadCopiesFromSource).
/// </summary>
public class CopyFromSourceTests
{
    /// <summary>main's instructions, joined with |.</summary>
    private static string Code(Run r)
    {
        var listing = r.Result.Listing;
        var start = listing.IndexOf("\nmain:", StringComparison.Ordinal);
        var code = new List<string>();
        foreach (var l in listing[(start + 1)..].Split('\n').Skip(1).Select(l => l.TrimEnd('\r')))
        {
            if (l.EndsWith(':') && !l.StartsWith(' ') && !l.StartsWith('$') && !l.StartsWith(';') && !l.StartsWith("main.")) break;
            if (l.StartsWith('$')) code.Add(l[17..].Split(';')[0].Trim());
        }
        return string.Join("|", code);
    }

    private static string Values(byte[] values) => string.Join(", ", values);

    [Fact]
    public void A_swap_compares_the_elements_and_keeps_one_in_y()
    {
        byte[] input = [5, 0, 255, 3, 3, 128, 1, 254, 0, 255, 127, 129];
        var r = Run.Program($$"""
            var values: [u8; 12] = [{{Values(input)}}]
            fn main() {
                var last: u8 = 11
                while last > 0 {
                    for i in 0..last {
                        var a: u8 = values[i]
                        var b: u8 = values[i + 1]
                        if a > b {
                            values[i] = b
                            values[i + 1] = a
                        }
                    }
                    last -= 1
                }
            }
            """);
        var sorted = input.Order().ToArray();
        for (int k = 0; k < sorted.Length; k++) Assert.Equal(sorted[k], r.U8("values", k));
        var code = Code(r);
        Assert.Contains("lda values+1,x|cmp values,x|bcs", code);
        Assert.Contains("ldy values,x|sta values,x|tya|sta values+1,x", code);
        Assert.DoesNotContain("main.a", code);
        Assert.DoesNotContain("main.b", code);
    }

    [Fact]
    public void A_copy_of_a_variable_is_loaded_into_a_register_before_the_variable_changes()
    {
        foreach (int start in new[] { 0, 1, 250, 255 })
        {
            var r = Run.Program($$"""
                var g: u8 = {{start}}
                var out: u8
                fn main() {
                    var a: u8 = g
                    g = 5
                    out = a
                }
                """);
            Assert.Equal(start, r.U8("out"));
            Assert.Equal(5, r.U8("g"));
            Assert.Equal("lda #$05|ldy g|sta g|tya|sta out|rts", Code(r));
        }
    }

    [Fact]
    public void A_call_keeps_the_copy_in_memory()
    {
        var r = Run.Program("""
            var values: [u8; 4] = [10, 20, 30, 40]
            var out: [u8; 4]
            fn g() { values[0] = 99 }
            fn main() {
                for i in 0..4 {
                    var a: u8 = values[i]
                    g()
                    out[i] = a
                }
            }
            """);
        Assert.Equal([10, 20, 30, 40], Enumerable.Range(0, 4).Select(k => (int)r.U8("out", k)));
        Assert.Contains("sta main.a", r.Result.Listing);
    }

    [Fact]
    public void A_change_of_the_index_keeps_the_copy_in_memory()
    {
        byte[] input = [1, 2, 3, 4, 5, 6, 7, 8];
        var r = Run.Program($$"""
            var values: [u8; 8] = [{{Values(input)}}]
            var out: [u8; 8]
            fn main() {
                for i in 0..8 {
                    var a: u8 = values[i]
                    out[7 - i] = a
                }
            }
            """);
        for (int k = 0; k < 8; k++) Assert.Equal(input[7 - k], r.U8("out", k));
    }

    [Fact]
    public void A_write_through_a_pointer_ends_the_copy()
    {
        var r = Run.Program("""
            var values: [u8; 4] = [10, 20, 30, 40]
            var out: [u8; 4]
            fn main() {
                var p: ptr u8 = addr(values)
                for i in 0..4 {
                    var a: u8 = values[i]
                    p[i] = 0
                    out[i] = a + values[i]
                }
            }
            """);
        Assert.Equal([10, 20, 30, 40], Enumerable.Range(0, 4).Select(k => (int)r.U8("out", k)));
        Assert.Equal([0, 0, 0, 0], Enumerable.Range(0, 4).Select(k => (int)r.U8("values", k)));
    }

    [Fact]
    public void A_volatile_variable_is_read_once()
    {
        var r = Run.Program("""
            volatile var port: u8 = 7
            var out: u8
            var big: u8
            fn main() {
                var a: u8 = port
                if a > 3 { big = 1 }
                out = a
            }
            """);
        Assert.Equal(7, r.U8("out"));
        Assert.Equal(1, r.U8("big"));
        Assert.Single(Code(r).Split('|'), i => i == "lda port");
    }

    [Fact]
    public void A_copy_read_in_an_inner_loop_stays_in_memory()
    {
        // cmp main.a in the loop is shorter and faster than cmp values+3.
        var r = Run.Program("""
            var values: [u8; 8] = [1, 2, 3, 4, 4, 4, 0, 4]
            var n: u8
            fn main() {
                var a: u8 = values[3]
                for j in 0..8 {
                    if values[j] == a { n += 1 }
                }
            }
            """);
        Assert.Equal(4, r.U8("n"));
        Assert.Contains("cmp main.a", Code(r));
    }

    [Fact]
    public void A_local_stored_twice_is_not_a_copy()
    {
        var r = Run.Program("""
            var values: [u8; 4] = [10, 200, 30, 250]
            var out: [u8; 4]
            fn main() {
                for i in 0..4 {
                    var a: u8 = values[i]
                    if a > 100 { a = 100 }
                    values[i] = 0
                    out[i] = a
                }
            }
            """);
        Assert.Equal([10, 100, 30, 100], Enumerable.Range(0, 4).Select(k => (int)r.U8("out", k)));
    }
}
