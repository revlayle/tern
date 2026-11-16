using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// Spec 3.5, conversions (review code-quality-tests 10, R7): every row of the table, at the review's values
/// -128, -1, 0, 127, 128, 255, 256, 32768, 65535 (and the ends of i16), from a variable, a typed constant and
/// an untyped constant, compared with C#'s unchecked casts, which extend by the source type and keep the low bits.
/// </summary>
public class ConversionTests
{
    private static readonly long[] Values = [-32768, -128, -1, 0, 127, 128, 255, 256, 32767, 32768, 65535];

    private static readonly string[] Integers = ["u8", "i8", "u16", "i16"];

    private static bool Fits(long v, string type) => type switch
    {
        "u8" => v is >= 0 and <= 255,
        "i8" => v is >= -128 and <= 127,
        "u16" => v is >= 0 and <= 65535,
        _ => v is >= -32768 and <= 32767,
    };

    /// <summary>C#'s unchecked conversion of a value to a type.</summary>
    private static long Cast(long v, string type) => type switch
    {
        "u8" => (byte)v,
        "i8" => (sbyte)v,
        "u16" => (ushort)v,
        _ => (short)v,
    };

    private static long Read(Run run, string name, string type) => type switch
    {
        "u8" => run.U8(name),
        "i8" => run.I8(name),
        "u16" => run.U16(name),
        _ => run.I16(name),
    };

    /// <summary>Automatic: widening with the same signedness.</summary>
    private static bool Widens(string from, string to) => (from, to) is ("u8", "u16") or ("i8", "i16");

    public static TheoryData<string, string> Pairs()
    {
        var data = new TheoryData<string, string>();
        foreach (var from in Integers)
            foreach (var to in Integers)
                data.Add(from, to);
        return data;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void An_integer_converts_by_its_source_type_then_keeps_the_low_bits(string from, string to)
    {
        var values = Values.Where(v => Fits(v, from)).ToArray();
        var decl = new System.Text.StringBuilder();
        var body = new System.Text.StringBuilder();
        for (int i = 0; i < values.Length; i++)
        {
            decl.AppendLine($"var s{i}: {from} = {values[i]}\nconst C{i}: {from} = {values[i]}\nvar r{i}: {to}\nvar k{i}: {to}\nvar a{i}: {to}");
            body.AppendLine($"    r{i} = s{i} as {to}\n    k{i} = C{i} as {to}");
            if (Widens(from, to)) body.AppendLine($"    a{i} = s{i}");
        }
        var run = Run.Program($"{decl}fn main() {{\n{body}}}");

        var wrong = new List<string>();
        for (int i = 0; i < values.Length; i++)
        {
            long expected = Cast(values[i], to);
            void Check(string name, string how)
            {
                long got = Read(run, name, to);
                if (got != expected) wrong.Add($"{how} {values[i]}: {got}, expected {expected}");
            }
            Check($"r{i}", $"variable {from} as {to}");
            Check($"k{i}", $"constant {from} as {to}");
            if (Widens(from, to)) Check($"a{i}", $"automatic {from} to {to}");
        }
        Assert.Empty(wrong);
    }

    /// <summary>
    /// Automatic widening wherever the destination gives the type (spec 3.5): an initial value, an array element,
    /// an argument and a return value, besides the assignment above.
    /// </summary>
    [Theory]
    [InlineData("i8", "i16", -128)]
    [InlineData("i8", "i16", -1)]
    [InlineData("i8", "i16", 0)]
    [InlineData("i8", "i16", 127)]
    [InlineData("u8", "u16", 0)]
    [InlineData("u8", "u16", 128)]
    [InlineData("u8", "u16", 255)]
    public void Widening_is_automatic_wherever_the_destination_gives_the_type(string from, string to, long value)
    {
        var run = Run.Program($$"""
            var s: {{from}} = {{value}}
            var init: {{to}}
            var element: [{{to}}; 2]
            var argument: {{to}}
            var returned: {{to}}
            fn take(v: {{to}}) { argument = v }
            fn give() -> {{to}} { return s }
            fn main() {
                var local: {{to}} = s
                init = local
                element[1] = s
                take(s)
                returned = give()
            }
            """);
        long expected = Cast(value, to);
        long Element() => to == "u16" ? run.U16At("element", 1, 2) : (short)run.U16At("element", 1, 2);
        Assert.Equal([expected, expected, expected, expected],
            new[] { Read(run, "init", to), Element(), Read(run, "argument", to), Read(run, "returned", to) });
    }

    /// <summary>An untyped constant expression keeps its low bits with <c>as</c>, whatever its value (spec 3.5).</summary>
    [Theory]
    [InlineData("u8")]
    [InlineData("i8")]
    [InlineData("u16")]
    [InlineData("i16")]
    public void An_untyped_constant_keeps_its_low_bits(string to)
    {
        var decl = string.Join("\n", Values.Select((_, i) => $"var r{i}: {to}"));
        var body = string.Join("\n", Values.Select((v, i) => $"    r{i} = ({v}) as {to}"));
        var run = Run.Program($"{decl}\nfn main() {{\n{body}\n}}");
        var wrong = Values.Select((v, i) => (v, got: Read(run, $"r{i}", to)))
            .Where(x => x.got != Cast(x.v, to)).Select(x => $"({x.v}) as {to}: {x.got}, expected {Cast(x.v, to)}").ToList();
        Assert.Empty(wrong);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 255)]
    [InlineData(255, 0)]
    [InlineData(127, 128)]
    [InlineData(128, 128)]
    public void A_bool_as_u8_is_0_or_1(int a, int b)
    {
        var run = Run.Program($$"""
            var a: u8 = {{a}}
            var b: u8 = {{b}}
            var t: bool = true
            var f: bool = false
            var r: [u8; 6]
            fn main() {
                r[0] = t as u8
                r[1] = f as u8
                r[2] = (a < b) as u8
                r[3] = (a == b) as u8
                r[4] = true as u8
                r[5] = false as u8
            }
            """);
        Assert.Equal([1, 0, a < b ? 1 : 0, a == b ? 1 : 0, 1, 0], Enumerable.Range(0, 6).Select(i => (int)run.U8("r", i)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(32768)]
    [InlineData(65535)]
    public void A_pointer_and_a_u16_convert_with_the_same_bits(int address)
    {
        var run = Run.Program($$"""
            var w: u16 = {{address}}
            var p: ptr u8
            var k: ptr u8
            var back: u16
            var auto: u16
            var kback: u16
            fn main() {
                p = w as ptr u8
                back = p as u16
                auto = p
                k = {{address}} as ptr u8
                kback = k
            }
            """);
        Assert.Equal([address, address, address], new[] { run.U16("back"), run.U16("auto"), run.U16("kback") }.Select(x => (int)x));
    }

    [Fact]
    public void A_pointer_as_another_pointer_reads_the_same_memory()
    {
        var run = Run.Program("""
            var word: u16 = $1234
            var wp: ptr u16
            var bp: ptr u8
            var lo: u8
            var hi: u8
            var same: u16
            fn main() {
                wp = addr(word)
                bp = wp as ptr u8
                lo = bp[0]
                hi = bp[1]
                same = bp
            }
            """);
        Assert.Equal(0x34, run.U8("lo"));
        Assert.Equal(0x12, run.U8("hi"));
        Assert.Equal(run.Address("word"), run.U16("same"));
    }

    /// <summary>The table's "not allowed" line, with the whole message.</summary>
    [Theory]
    [InlineData("var a: u8 = 3\nvar r: bool\nfn main() { r = a as bool }",
        "Numbers cannot be converted to bool; compare instead, e.g. 'x != 0'")]
    [InlineData("var w: u16 = 300\nvar r: u8\nfn main() { r = w }",
        "Expected u8, found u16. Convert with 'as u8'")]
    [InlineData("var x: i8 = -1\nvar r: i16\nvar a: u8 = 1\nfn main() { r = a + x }",
        "Cannot mix signed and unsigned (u8 and i8); convert one side with 'as'")]
    public void What_the_table_does_not_allow_is_an_error(string source, string message)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Equal(message, e.Description);
    }
}
