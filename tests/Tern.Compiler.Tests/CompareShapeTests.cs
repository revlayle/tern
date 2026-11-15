namespace Tern.Compiler.Tests;

/// <summary>
/// M20 follow-up 2, spec 11: the rows for signed and 16-bit compares, <c>&amp;&amp;</c>, <c>||</c>, a compare
/// stored in a <c>bool</c> and <c>i8 &gt;&gt; 1</c> describe the code as built. One listing test per row,
/// each with its results.
/// </summary>
public class CompareShapeTests
{
    /// <summary>The instructions of a function, with its labels named L1, L2, ... in order of appearance.</summary>
    private static string Code(Run r, string function)
    {
        var names = new Dictionary<string, string>();
        return System.Text.RegularExpressions.Regex.Replace(Listing.CodeWithLabels(r, function), $@"\b{function}\.\w+",
            m => names.TryGetValue(m.Value, out var n) ? n : names[m.Value] = $"L{names.Count + 1}");
    }

    [Theory]
    [InlineData(-128, 127)]
    [InlineData(127, -128)]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(5, 5)]
    [InlineData(100, -100)]
    public void Signed_compare(int a, int b)
    {
        var r = Run.Program($$"""
            var a: i8 = {{a}}
            var b: i8 = {{b}}
            var y: u8
            fn f() { if a < b { y = 1 } }
            fn main() { f() }
            """);
        Assert.Equal(a < b ? 1 : 0, r.U8("y"));
        Assert.Equal("lda a|sec|sbc b|bvc L1|eor #$80|L1:|bpl L2|lda #$01|sta y|L2:|rts", Code(r, "f"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0x00FF, 0x0100)]
    [InlineData(0x0100, 0x00FF)]
    [InlineData(0xFFFF, 0)]
    [InlineData(0x1234, 0x1235)]
    [InlineData(0x1234, 0x1334)]
    public void Sixteen_bit_less_and_equal(int w, int v)
    {
        var r = Run.Program($$"""
            var w: u16 = {{w}}
            var v: u16 = {{v}}
            var lt: u8
            var eq: u8
            fn less() { if w < v { lt = 1 } }
            fn equal() { if w == v { eq = 1 } }
            fn main() { less(); equal() }
            """);
        Assert.Equal(w < v ? 1 : 0, r.U8("lt"));
        Assert.Equal(w == v ? 1 : 0, r.U8("eq"));
        Assert.Equal("lda w|cmp v|lda w+1|sbc v+1|bcs L1|lda #$01|sta lt|L1:|rts", Code(r, "less"));
        Assert.Equal("lda w|cmp v|bne L1|lda w+1|cmp v+1|bne L1|lda #$01|sta eq|L1:|rts",
            Code(r, "equal"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(9, 0)]
    [InlineData(9, 1)]
    [InlineData(10, 0)]
    [InlineData(10, 255)]
    [InlineData(255, 1)]
    public void And_and_or(int x, int y)
    {
        var r = Run.Program($$"""
            var x: u8 = {{x}}
            var y: u8 = {{y}}
            var both: u8
            var either: u8
            fn f() { if x < 10 && y != 0 { both = 1 } }
            fn g() { if x < 10 || y != 0 { either = 1 } }
            fn main() { f(); g() }
            """);
        Assert.Equal(x < 10 && y != 0 ? 1 : 0, r.U8("both"));
        Assert.Equal(x < 10 || y != 0 ? 1 : 0, r.U8("either"));
        Assert.Equal("lda x|cmp #$0A|bcs L1|lda y|beq L1|lda #$01|sta both|L1:|rts", Code(r, "f"));
        Assert.Equal("lda x|cmp #$0A|bcc L1|lda y|beq L2|L1:|lda #$01|sta either|L2:|rts", Code(r, "g"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(254, 255)]
    [InlineData(255, 255)]
    public void A_compare_stored_in_a_bool(int x, int y)
    {
        var r = Run.Program($$"""
            var x: u8 = {{x}}
            var y: u8 = {{y}}
            var t: bool
            fn f() { t = x < y }
            fn main() { f() }
            """);
        Assert.Equal(x < y ? 1 : 0, r.U8("t"));
        Assert.Equal("lda x|cmp y|bcs L1|lda #$01|bne L2|L1:|lda #$00|L2:|sta t|rts", Code(r, "f"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(-3)]
    [InlineData(127)]
    [InlineData(-128)]
    public void Signed_shift_right_by_one(int a)
    {
        var r = Run.Program($$"""
            var a: i8 = {{a}}
            var s: i8
            fn f() { s = a >> 1 }
            fn main() { f() }
            """);
        Assert.Equal((sbyte)(a >> 1), r.I8("s"));
        Assert.Equal("lda a|cmp #$80|ror a|sta s|rts", Code(r, "f"));
    }
}
