namespace Tern.Compiler.Tests;

/// <summary>
/// Spec 4: every read and write of volatile memory in the source happens exactly once. Checked on the
/// bus (<see cref="BusLog"/>), not in the listing, for 8-bit and 16-bit statements into a volatile
/// variable, array element and struct field (review code-quality-tests, finding 3).
/// </summary>
public class VolatileAccessTests
{
    /// <summary>A statement with the volatile target written X, how often it reads X, and its result in C#.</summary>
    private sealed record Statement(string Text, int Reads, Func<int, int, int, int> Value);

    private static readonly Statement[] Statements =
    [
        new("X = a", 0, (v, a, b) => a),
        new("X = a + b", 0, (v, a, b) => a + b),
        new("X = a - b", 0, (v, a, b) => a - b),
        new("X = a + b - 7", 0, (v, a, b) => a + b - 7),
        new("X = a & b", 0, (v, a, b) => a & b),
        new("X = a | b", 0, (v, a, b) => a | b),
        new("X = a ^ b", 0, (v, a, b) => a ^ b),
        new("X = ~a", 0, (v, a, b) => ~a),
        new("X = a << 1", 0, (v, a, b) => a << 1),
        new("X = a << 3", 0, (v, a, b) => a << 3),
        new("X = a >> 1", 0, (v, a, b) => a >> 1),
        new("X = a >> 3", 0, (v, a, b) => a >> 3),
        new("X = (a << 1) + b", 0, (v, a, b) => (a << 1) + b),
        new("X += a", 1, (v, a, b) => v + a),
        new("X -= b", 1, (v, a, b) => v - b),
        new("X += 1", 1, (v, a, b) => v + 1),
        new("X -= 1", 1, (v, a, b) => v - 1),
        new("X += 2", 1, (v, a, b) => v + 2),
        new("X += 256", 1, (v, a, b) => v + 256),
        new("X -= 512", 1, (v, a, b) => v - 512),
        new("X &= b", 1, (v, a, b) => v & b),
        new("X |= b", 1, (v, a, b) => v | b),
        new("X ^= b", 1, (v, a, b) => v ^ b),
        new("X <<= 1", 1, (v, a, b) => v << 1),
        new("X <<= 2", 1, (v, a, b) => v << 2),
        new("X >>= 1", 1, (v, a, b) => v >> 1),
        new("X >>= 2", 1, (v, a, b) => v >> 2),
        new("X = X + a", 1, (v, a, b) => v + a),
        new("X += a + b", 1, (v, a, b) => v + a + b),
    ];

    private static readonly string[] Targets = ["v", "arr[i]", "s.f"];

    public static TheoryData<string, string, string> Cases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var type in new[] { "u8", "u16" })
            foreach (var target in Targets)
                foreach (var s in Statements)
                    if (type == "u16" || !s.Text.Contains("256") && !s.Text.Contains("512"))   // a whole page only fits a u16
                        data.Add(type, target, s.Text);
        return data;
    }

    // The statement's own bytes: v at $C000, element 2 of the split array at $C010, field f at offset 1 of s at $C020.
    private static (int Lo, int Hi) Place(string target) => target switch
    {
        "v" => (0xC000, 0xC001),
        "arr[i]" => (0xC012, 0xC016),
        _ => (0xC021, 0xC022),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_volatile_target_is_read_and_written_once(string type, string target, string statement)
    {
        bool wide = type == "u16";
        int mask = wide ? 0xFFFF : 0xFF, v0 = wide ? 0x80C1 : 0xC1, a = wide ? 0x01F0 : 0xF0, b = wide ? 0x0123 : 0x23;
        var s = Statements.Single(x => x.Text == statement);
        var (lo, hi) = Place(target);
        var r = Run.Program($$"""
            struct S { pad: u8, f: {{type}} }
            volatile var v: {{type}} @ $C000
            volatile var arr: [{{type}}; 4] @ $C010
            volatile var s: S @ $C020
            var a: {{type}} = {{a}}
            var b: {{type}} = {{b}}
            var i: u8 = 2
            fn main() {
                {{statement.Replace("X", target)}}
            }
            """,
            setup: ram =>
            {
                ram.Bytes[lo] = (byte)v0;
                ram.Bytes[hi] = (byte)(v0 >> 8);
            },
            watch: _ => wide ? [lo, hi] : [lo]);
        int expected = s.Value(v0, a, b) & mask;
        Assert.Equal([(byte)expected], r.Log!.Writes(lo));
        Assert.Equal(s.Reads, r.Log.Reads(lo));
        if (!wide) return;
        Assert.Equal([(byte)(expected >> 8)], r.Log.Writes(hi));
        Assert.Equal(s.Reads, r.Log.Reads(hi));
    }

    [Theory]
    [InlineData("u8", "w = v", 1)]
    [InlineData("u8", "w = v + v", 2)]
    [InlineData("u8", "w = v + a", 1)]
    [InlineData("u8", "w = (v << 2) | v", 2)]
    [InlineData("u16", "w = v", 1)]
    [InlineData("u16", "w = v + v", 2)]
    [InlineData("u16", "w = v + a", 1)]
    [InlineData("u16", "w = (v << 2) | v", 2)]
    public void A_volatile_source_is_read_once_for_each_use(string type, string statement, int reads)
    {
        bool wide = type == "u16";
        int mask = wide ? 0xFFFF : 0xFF, v0 = wide ? 0x80C1 : 0xC1, a = wide ? 0x01F0 : 0xF0;
        var r = Run.Program($$"""
            volatile var v: {{type}} @ $C000
            var a: {{type}} = {{a}}
            var w: {{type}}
            fn main() { {{statement}} }
            """,
            setup: ram =>
            {
                ram.Bytes[0xC000] = (byte)v0;
                ram.Bytes[0xC001] = (byte)(v0 >> 8);
            },
            watch: _ => [0xC000, 0xC001]);
        int expected = statement switch
        {
            "w = v" => v0,
            "w = v + v" => v0 + v0,
            "w = v + a" => v0 + a,
            _ => (v0 << 2) | v0,
        } & mask;
        Assert.Equal(expected, wide ? r.U16("w") : r.U8("w"));
        Assert.Equal(reads, r.Log!.Reads(0xC000));
        Assert.Equal(wide ? reads : 0, r.Log.Reads(0xC001));
        Assert.Empty(r.Log.Writes(0xC000));
    }
}
