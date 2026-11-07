namespace Tern.Compiler.Tests;

/// <summary>
/// Variable indexes into arrays of structs stored as whole records (spec 3.3): <c>linear var</c>
/// shifts the index, <c>indexed var</c> reads the record's offset from a compile-time table.
/// </summary>
public class RecordIndexTests
{
    /// <summary>The instructions of main, without addresses, bytes and cycles.</summary>
    private static string Code(Run r)
    {
        var listing = r.Result.Listing;
        var start = listing.IndexOf("\nmain:", StringComparison.Ordinal);
        var code = new List<string>();
        foreach (var l in listing[(start + 1)..].Split('\n').Skip(1))
        {
            if (l.EndsWith(':') && !l.StartsWith(' ') && !l.StartsWith('$') && !l.StartsWith(';')) break;
            if (!l.StartsWith('$')) continue;
            code.Add(l[17..].Split(';')[0].Trim());
            if (code[^1] == "rts") break;   // the offset table's bytes follow main
        }
        return string.Join("|", code);
    }

    private static string Shape(string declaration, string statement)
    {
        var r = Run.Program($$"""
            struct S1 { a: u8 }
            struct S2 { a: u8, b: u8 }
            struct S4 { a: u8, b: u8, c: u8, d: u8 }
            struct S8 { a: u8, b: u8, c: u8, w: u16, _: [u8; 3] }
            struct S5 { a: u8, b: u8, c: u8, w: u16 }
            {{declaration}}
            var i: u8
            var v: u8
            fn main() { {{statement}} }
            """);
        return Code(r);
    }

    [Theory]
    [InlineData("linear var list: [S1; 20]", "v = list[i].a", "ldx i|lda list,x|sta v|rts")]
    [InlineData("linear var list: [S2; 20]", "v = list[i].b", "lda i|asl a|tax|lda list+1,x|sta v|rts")]
    [InlineData("linear var list: [S4; 20]", "v = list[i].c", "lda i|asl a|asl a|tax|lda list+2,x|sta v|rts")]
    [InlineData("linear var list: [S8; 20]", "v = list[i].c", "lda i|asl a|asl a|asl a|tax|lda list+2,x|sta v|rts")]
    public void Linear_index_shape(string declaration, string statement, string expected) =>
        Assert.Equal(expected, Shape(declaration, statement));

    [Theory]
    [InlineData("indexed var list: [S5; 20]", "v = list[i].c", "ldx i|ldy list_offsets,x|lda list+2,y|sta v|rts")]
    public void Indexed_shape(string declaration, string statement, string expected) =>
        Assert.Equal(expected, Shape(declaration, statement));
}
