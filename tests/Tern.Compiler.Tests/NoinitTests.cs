using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary><c>noinit var</c> (spec 4): a global that the startup code does not zero.</summary>
public class NoinitTests
{
    /// <summary>Runs the program with every byte after the image (and zero page) set to $AA first, as leftovers would be.</summary>
    private static Run RunOverGarbage(string source)
    {
        var compiled = TernCompiler.Compile(source);
        int end = compiled.LoadAddress + compiled.Image.Length;
        return Run.Program(source, setup: ram =>
        {
            for (int a = end; a < 0xA000; a++) ram.Bytes[a] = 0xAA;
            for (int a = 0x02; a < 0x90; a++) ram.Bytes[a] = 0xAA;
        });
    }

    [Fact]
    public void Noinit_globals_keep_what_was_in_memory()
    {
        var r = RunOverGarbage("""
            var a: u8
            noinit var buf: [u8; 200]
            noinit var buf2: [u8; 200]
            var b: u16
            noinit var w: u16
            var after: [u8; 4]
            noinit var z: u8 @ zp
            var zz: u8 @ zp
            fn main() { }
            """);
        Assert.Equal(0, r.U8("a"));
        Assert.Equal(0, r.U16("b"));
        for (int i = 0; i < 4; i++) Assert.Equal(0, r.U8("after", i));
        Assert.Equal(0, r.U8("zz"));
        for (int i = 0; i < 200; i++) Assert.Equal(0xAA, r.U8("buf", i));
        for (int i = 0; i < 200; i++) Assert.Equal(0xAA, r.U8("buf2", i));
        Assert.Equal(0xAAAA, r.U16("w"));
        Assert.Equal(0xAA, r.U8("z"));
        Assert.True(r.Address("z") < 0x100);
    }

    [Fact]
    public void Noinit_globals_are_not_in_the_cleared_range()
    {
        // 400 bytes of noinit would otherwise add a page loop to the startup code.
        const string Buffers = "var a: u8\nnoinit var b1: [u8; 200]\nnoinit var b2: [u8; 200]\nfn main() {\n b1[0] = a\n b2[0] = a\n }";
        var with = TernCompiler.Compile(Buffers);
        var without = TernCompiler.Compile(Buffers.Replace("noinit ", ""));
        Assert.True(with.Image.Length < without.Image.Length);
        Assert.DoesNotContain("clear0", with.Listing);
    }

    [Fact]
    public void Noinit_works_with_structs_linear_arrays_and_private()
    {
        var r = RunOverGarbage("""
            struct P { x: u8, y: u8 }
            noinit var p: P
            noinit linear var pts: [u16; 2]
            private noinit var q: u8
            var out: u8
            fn main() {
                p.x = 1
                out = p.y
            }
            """);
        Assert.Equal(1, r.U8("p"));
        Assert.Equal(0xAA, r.U8("out"));
        Assert.Equal(0xAAAA, r.U8("pts") | r.U8("pts", 1) << 8);
        Assert.Equal(0xAA, r.U8("q"));
    }

    [Theory]
    [InlineData("noinit var x: u8 = 3\nfn main() { }", "'x' is noinit, so it cannot have an initial value")]
    [InlineData("noinit var x: u8 @ $C000\nfn main() { }", "'x' is at a fixed address, which is never zeroed; leave out 'noinit'")]
    [InlineData("fn main() { noinit var x: u8 = 1 }", "'noinit' is for global variables; local variables are never zeroed")]
    [InlineData("noinit data t: [u8; 2] = [1, 2]\nfn main() { }", "'noinit' goes before 'var'")]
    [InlineData("noinit linear data t: [u16; 2] = [1, 2]\nfn main() { }", "'noinit' is for variables; data is never zeroed")]
    [InlineData("noinit fn f() { }\nfn main() { }", "'noinit' goes before 'var'")]
    public void Reports_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
    }
}
