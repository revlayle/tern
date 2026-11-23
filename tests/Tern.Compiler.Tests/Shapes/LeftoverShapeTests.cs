namespace Tern.Compiler.Tests;

/// <summary>
/// V1 fixes 2, the shapes seen while building V1 fixes: a 16-bit value stored into an element with a
/// constant index straight from the subtract; <c>u = q - v</c> with v in A as <c>eor #$FF / sec / adc q</c>;
/// a register parameter or temporary that no instruction names has no memory.
/// </summary>
public class LeftoverShapeTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(127, 128)]
    [InlineData(128, 127)]
    [InlineData(255, 1)]
    [InlineData(0, 255)]
    public void Gives_the_differences_as_written(int q, int v)
    {
        var r = Run.Program($$"""
            var q: u8 = {{q}}
            var a: u16 = {{q * 200}}
            var b: u16 = {{v * 7}}
            var arr: [u8; 4] = [0, {{v}}, 0, 0]
            var i: u8 = 1
            var rows: [u16; 8]
            var u1: u8
            var u2: u8
            var u3: u8
            fn sub(v: u8 using A) { u1 = q - v }
            fn main() {
                sub({{v}})
                u2 = q - (arr[i] << 1)
                u3 = q - arr[i]
                rows[3] = a - b
            }
            """);
        Assert.Equal((byte)(q - v), r.U8("u1"));
        Assert.Equal((byte)(q - (byte)(v << 1)), r.U8("u2"));
        Assert.Equal((byte)(q - v), r.U8("u3"));
        Assert.Equal((ushort)(q * 200 - v * 7), r.U8("rows", 3) | r.U8("rows", 8 + 3) << 8);
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void The_code_has_no_store_of_the_value_in_A()
    {
        var r = Run.Program("""
            var q: u8
            var u: u8
            var a: u16
            var b: u16
            var arr: [u8; 4]
            var i: u8
            var rows: [u16; 8]
            fn sub(v: u8 using A) { u = q - v }
            fn shifted() { u = q - (arr[i] << 1) }
            fn element() { u = q - arr[i] }
            fn row() { rows[3] = a - b }
            fn main() {
                sub(1)
                shifted()
                element()
                row()
            }
            """);
        Assert.Equal("eor #$FF|sec|adc q|sta u|rts", Listing.Code(r, "sub"));
        Assert.Equal("ldx i|lda arr,x|asl a|eor #$FF|sec|adc q|sta u|rts", Listing.Code(r, "shifted"));
        // An element is the sbc operand, as before.
        Assert.Equal("lda q|sec|ldx i|sbc arr,x|sta u|rts", Listing.Code(r, "element"));
        Assert.Equal("sec|lda a|sbc b|sta rows+3|lda a+1|sbc b+1|sta rows+11|rts", Listing.Code(r, "row"));
        // sub's parameter is read from A only, and shifted's operand needs no temporary: neither has memory.
        Assert.DoesNotContain("sub.v", r.Result.Symbols.Keys);
        Assert.DoesNotContain(r.Result.Symbols.Keys, k => k.StartsWith("shifted._t"));
    }

    [Fact]
    public void A_parameter_in_memory_and_a_local_keep_their_bytes()
    {
        var r = Run.Program("""
            var u: u8
            fn f(v: u8) { }
            fn g() {
                var t: u8 = 0
            }
            fn main() {
                f(3)
                g()
            }
            """);
        // Callers store into a parameter in memory; a run can dump a local.
        Assert.Contains("f.v", r.Result.Symbols.Keys);
        Assert.Contains("g.t", r.Result.Symbols.Keys);
    }
}
