using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// Spec 3.6, operator precedence (review code-quality-tests 10, R7): one row per pair of neighbouring levels
/// and for left associativity, each written without brackets and compared with C# with explicit brackets,
/// on runtime values. All rows run in one program per set of values.
/// </summary>
public class PrecedenceTests
{
    /// <summary>The values of the program's variables.</summary>
    public sealed record Values(byte A, byte B, byte C, ushort W, sbyte X, bool P, bool Q, bool R);

    /// <summary>An expression, the type it is stored as, and its value computed in C# with brackets.</summary>
    private sealed record Row(string Expr, string Type, Func<Values, long> Expected);

    private static long B(bool v) => v ? 1 : 0;

    private static readonly Row[] Rows =
    [
        // 1 (postfix) and 2 (unary): as and [i] bind tighter.
        new("-x as i16", "i16", v => (short)-(short)v.X),
        new("~a as u16", "u16", v => (ushort)~(ushort)v.A),
        new("~t[1]", "u8", v => (byte)~v.B),
        new("-t[2]", "u8", v => (byte)-v.C),
        // 1 and 5: as binds tighter than a shift.
        new("a as u16 << 5", "u16", v => (ushort)(v.A << 5)),
        // 2 (unary) and 4 (+ -).
        new("-a + b", "u8", v => (byte)(-v.A + v.B)),
        new("~a - b", "u8", v => (byte)(~v.A - v.B)),
        new("a - -b", "u8", v => (byte)(v.A - -v.B)),
        // 3 (* / %, constants) and 4, with the constant part next to a runtime value.
        new("a + 2 * 3", "u8", v => (byte)(v.A + 6)),
        new("a - 12 / 4", "u8", v => (byte)(v.A - 3)),
        new("2 + 3 * 4 + a", "u8", v => (byte)(14 + v.A)),
        // 4 (+ -) and 5 (<< >>): shifts bind looser, as in C.
        new("w << 5 + 3", "u16", v => (ushort)(v.W << 8)),
        new("a + b << 1", "u8", v => (byte)((byte)(v.A + v.B) << 1)),
        new("a - b >> 1", "u8", v => (byte)(v.A - v.B) >> 1),
        new("a >> 1 + 1", "u8", v => v.A >> 2),
        // 5 (<< >>) and 6 (&).
        new("a & b << 1", "u8", v => (byte)(v.A & (v.B << 1))),
        new("a << 1 & b", "u8", v => (byte)((v.A << 1) & v.B)),
        new("a >> 2 & b", "u8", v => (v.A >> 2) & v.B),
        // 6 (&) and 7 (^).
        new("a ^ b & c", "u8", v => v.A ^ (v.B & v.C)),
        new("a & b ^ c", "u8", v => (v.A & v.B) ^ v.C),
        // 7 (^) and 8 (|).
        new("a | b ^ c", "u8", v => v.A | (v.B ^ v.C)),
        new("a ^ b | c", "u8", v => (v.A ^ v.B) | v.C),
        // 6 (&) and 8 (|).
        new("a | b & c", "u8", v => v.A | (v.B & v.C)),
        new("a & b | c", "u8", v => (v.A & v.B) | v.C),
        // 6-8 and 9 (compares): unlike C, the bitwise operators bind tighter.
        new("a & 4 != 0", "bool", v => B((v.A & 4) != 0)),
        new("a | b == c", "bool", v => B((v.A | v.B) == v.C)),
        new("a ^ b != c", "bool", v => B((v.A ^ v.B) != v.C)),
        new("a & b < c", "bool", v => B((v.A & v.B) < v.C)),
        // 4 and 5 against 9.
        new("a + b > c", "bool", v => B((byte)(v.A + v.B) > v.C)),
        new("a << 1 >= b", "bool", v => B((byte)(v.A << 1) >= v.B)),
        // 9 (compares) and 10 (&&).
        new("a < b && b < c", "bool", v => B(v.A < v.B && v.B < v.C)),
        new("a == b && p", "bool", v => B(v.A == v.B && v.P)),
        // 10 (&&) and 11 (||).
        new("p || q && r", "bool", v => B(v.P || (v.Q && v.R))),
        new("p && q || r", "bool", v => B((v.P && v.Q) || v.R)),
        new("a != b || q && r", "bool", v => B(v.A != v.B || (v.Q && v.R))),
        // 2 (!) and 10, 11.
        new("!p && q", "bool", v => B(!v.P && v.Q)),
        new("!p || q", "bool", v => B(!v.P || v.Q)),
        // Left associativity on one level.
        new("a - b - c", "u8", v => (byte)(v.A - v.B - v.C)),
        new("a - b + c", "u8", v => (byte)(v.A - v.B + v.C)),
        new("a >> 1 << 1", "u8", v => (byte)((v.A >> 1) << 1)),
        new("a << 1 >> 1", "u8", v => (byte)(v.A << 1) >> 1),
        new("w >> 4 << 2", "u16", v => (ushort)((v.W >> 4) << 2)),
    ];

    public static TheoryData<Values> Sets =>
    [
        new(0, 0, 0, 0, 0, false, false, false),
        new(1, 2, 3, 1, -1, true, false, true),
        new(200, 100, 55, 0x1234, -128, false, true, true),
        new(255, 255, 255, 0xFFFF, 127, true, true, false),
        new(128, 127, 4, 0x8001, -2, false, false, true),
        new(13, 250, 7, 300, 100, true, true, true),
    ];

    private static string Bool(bool v) => v ? "true" : "false";

    [Theory]
    [MemberData(nameof(Sets))]
    public void Each_row_is_its_bracketed_form(Values v)
    {
        var source = new System.Text.StringBuilder($"""
            var a: u8 = {v.A}
            var b: u8 = {v.B}
            var c: u8 = {v.C}
            var w: u16 = {v.W}
            var x: i8 = {v.X}
            var p: bool = {Bool(v.P)}
            var q: bool = {Bool(v.Q)}
            var r: bool = {Bool(v.R)}
            var t: [u8; 3]

            """);
        for (int i = 0; i < Rows.Length; i++) source.AppendLine($"var r{i}: {Rows[i].Type}");
        source.AppendLine("fn main() {\n    t[0] = a\n    t[1] = b\n    t[2] = c");
        for (int i = 0; i < Rows.Length; i++) source.AppendLine($"    r{i} = {Rows[i].Expr}");
        source.AppendLine("}");

        var run = Run.Program(source.ToString());
        var wrong = new List<string>();
        for (int i = 0; i < Rows.Length; i++)
        {
            var row = Rows[i];
            long got = row.Type switch
            {
                "u16" => run.U16($"r{i}"),
                "i16" => run.I16($"r{i}"),
                _ => run.U8($"r{i}"),
            };
            long expected = row.Expected(v);
            if (got != expected) wrong.Add($"{row.Expr}: {got}, expected {expected}");
        }
        Assert.Empty(wrong);
    }

    /// <summary>The spec's own example of <c>as</c> before unary minus: -128 as an i8.</summary>
    [Fact]
    public void Minus_x_as_i16_negates_after_widening()
    {
        var run = Run.Program("""
            var x: i8 = -128
            var after: i16
            var before: i16
            fn main() {
                after = -x as i16
                before = (-x) as i16
            }
            """);
        Assert.Equal(128, run.I16("after"));
        Assert.Equal(-128, run.I16("before"));
    }

    /// <summary>Spec 3.1: with constants, * / % bind tighter than + -, and one level is left associative.</summary>
    [Theory]
    [InlineData("2 + 3 * 4", "u8", 14)]
    [InlineData("20 - 12 / 4", "u8", 17)]
    [InlineData("17 % 5 * 3", "u8", 6)]
    [InlineData("100 / 10 / 5", "u8", 2)]
    [InlineData("~1 * 2", "i8", -4)]
    [InlineData("-7 / 2", "i8", -3)]
    [InlineData("1 << 2 + 1", "u8", 8)]
    [InlineData("3 + 1 << 2", "u8", 16)]
    [InlineData("12 & 6 | 1", "u8", 5)]
    [InlineData("1 | 6 & 12", "u8", 5)]
    public void Constant_expressions_follow_the_same_levels(string expr, string type, long expected)
    {
        var run = Run.Program($"const K: {type} = {expr}\nvar r: {type}\nfn main() {{ r = K }}");
        Assert.Equal(expected, type == "i8" ? run.I8("r") : run.U8("r"));
    }

    [Theory]
    [InlineData("a < b < c")]
    [InlineData("a == b == c")]
    [InlineData("a < b != c")]
    public void Compares_do_not_chain(string expr)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(
            $"var a: u8\nvar b: u8\nvar c: u8\nvar r: bool\nfn main() {{ r = {expr} }}"));
        Assert.Equal("Comparisons cannot be chained; use '&&'", e.Description);
    }
}
