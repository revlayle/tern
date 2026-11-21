using Tern.Cpu;
using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>Long byte buffers (spec 3.2): <c>var x: [u8; 2048]</c>, used through a pointer.</summary>
public class LongBufferTests
{
    private static string Message(string source) =>
        Assert.Throws<CompileError>(() => TernCompiler.Compile(source)).Description;

    private const string Fill = """
        var p: ptr u8
        var sum: u16
        var first: u8
        var at300: u8
        var last: u8
        var size: u16
        fn main() {
            size = len(buf)
            p = addr(buf)
            // Writes the low byte of the offset into every byte, in 256-byte steps.
            for page in 0..8 {
                for i in 0..=255 { p[i] = i + page }
                p += 256
            }
            p = addr(buf)
            first = p[0]
            p += 256
            at300 = p[44]
            p = addr(buf) + 1792
            last = p[255]
        }
        """;

    [Theory]
    [InlineData("var buf: [u8; 2048]")]
    [InlineData("noinit var buf: [u8; 2048]")]
    [InlineData("memory { $C000..=$CFFF region big }\nvar buf: [u8; 2048] @ big")]
    [InlineData("var buf: [u8; 2048] @ $C800")]
    public void A_long_buffer_is_filled_and_read_back_through_a_stepped_pointer(string declaration)
    {
        var r = Run.Program(declaration + "\n" + Fill, maxCycles: 20_000_000);
        Assert.Equal(2048, r.U16("size"));
        Assert.Equal(0, r.U8("first"));
        Assert.Equal((256 + 44 + 1) & 0xFF, r.U8("at300"));
        Assert.Equal((255 + 7) & 0xFF, r.U8("last"));
        for (int k = 0; k < 2048; k++)
            Assert.Equal((byte)((k & 0xFF) + (k >> 8)), r.U8("buf", k));
    }

    [Fact]
    public void A_long_buffer_is_zeroed_at_startup_unless_noinit()
    {
        void Dirty(Ram ram, int at) { for (int k = 0; k < 600; k++) ram.Bytes[at + k] = 0xEE; }
        const string source = "var buf: [u8; 600]\nnoinit var raw: [u8; 600]\nvar x: u8\nfn main() { x = len(buf) as u8 }";
        var compiled = Run.Compile(source);
        int buf = compiled.Symbols["buf"], raw = compiled.Symbols["raw"];
        var r = Run.Of(compiled, setup: ram => { Dirty(ram, buf); Dirty(ram, raw); });
        for (int k = 0; k < 600; k++)
        {
            Assert.Equal(0, r.U8("buf", k));
            Assert.Equal(0xEE, r.U8("raw", k));
        }
    }

    [Fact]
    public void A_long_i8_buffer_gives_a_pointer_to_i8()
    {
        var r = Run.Program("var buf: [i8; 300]\nvar q: ptr i8\nvar x: i8\nvar n: u16\nfn main() { q = addr(buf); q[0] = -5; x = q[0]; n = len(buf) }");
        Assert.Equal(-5, r.I8("x"));
        Assert.Equal(300, r.U16("n"));
    }

    [Fact]
    public void Long_data_cannot_be_indexed_either()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            File.WriteAllBytes(Path.Combine(dir.FullName, "big.bin"), new byte[600]);
            var path = Path.Combine(dir.FullName, "main.tern");
            var message = Assert.Throws<CompileError>(() => TernCompiler.Compile(
                "data tbl: [u8; 600] = include_bytes(\"big.bin\")\nvar x: u8\nfn main() { x = tbl[3] }", sourcePath: path)).Description;
            Assert.Contains("'tbl' has 600 elements, too many for a u8 index", message);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void A_long_buffer_in_a_full_region_is_an_error()
    {
        var message = Message("memory { $C000..=$C1FF region small }\nvar buf: [u8; 600] @ small\nfn main() { }");
        Assert.Contains("Region 'small'", message);
        Assert.Contains("is full", message);
    }

    [Fact]
    public void A_long_buffer_cannot_be_indexed_the_error_suggests_a_pointer()
    {
        var message = Message("var buf: [u8; 600]\nvar x: u8\nfn main() { x = buf[3] }");
        Assert.Contains("'buf' has 600 elements, too many for a u8 index", message);
        Assert.Contains("addr(buf)", message);
        Assert.Contains("too many for a u8 index", Message("var buf: [u8; 600]\nfn main() { buf[3] = 1 }"));
    }

    [Fact]
    public void Other_arrays_stay_at_256_elements_and_a_long_buffer_has_no_initial_value()
    {
        Assert.Contains("at most 256 elements", Message("var a: [u16; 300]\nfn main() { }"));
        Assert.Contains("at most 256 elements", Message("var a: [bool; 300]\nfn main() { }"));
        Assert.Contains("Array length must be 1 to 256", Message("var a: [u8; 0]\nfn main() { }"));
        Assert.Contains("has no initial value", Message("var a: [u8; 300] = [1, 2, 3]\nfn main() { }"));
        Assert.Contains("at most 256 elements", Message("fn main() { var a: [u8; 300] }"));
        Assert.Contains("at most 256 elements", Message("struct S { a: [u8; 300] }\nfn main() { }"));
    }
}
