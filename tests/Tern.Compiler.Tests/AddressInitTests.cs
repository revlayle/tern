using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// M20 follow-up, spec 4: addr() of a global, data, a field of a struct global or a function in the
/// initial value of a global or data, alone, plus or minus a constant, or as .lo / .hi. The compiler
/// fills the bytes in when the program is placed.
/// </summary>
public class AddressInitTests
{
    private static int Word(Run r, int at) => r.Ram.Bytes[at] | r.Ram.Bytes[at + 1] << 8;

    [Fact]
    public void A_split_table_of_row_pointers_reads_through_each_row()
    {
        var r = Run.Program("""
            var map: [u8; 120]
            data rows: [ptr u8; 3] = [addr(map), addr(map) + 40, addr(map) + 80]
            var out: [u8; 3]
            fn main() {
                for i in 0..120 { map[i] = i }
                for row in 0..3 {
                    var p: ptr u8 = rows[row]
                    out[row] = p[5]
                }
            }
            """);
        Assert.Equal([5, 45, 85], r.Ram.Bytes.AsSpan(r.Address("out"), 3).ToArray());
        int map = r.Address("map"), rows = r.Address("rows");
        for (int k = 0; k < 3; k++)
            Assert.Equal(map + 40 * k, r.Ram.Bytes[rows + k] | r.Ram.Bytes[rows + 3 + k] << 8);
    }

    [Fact]
    public void A_linear_table_and_a_u16_table_hold_whole_addresses()
    {
        var r = Run.Program("""
            var a: u8
            data b: [u8; 3] = [1, 2, 3]
            linear data ptrs: [ptr u8; 2] = [addr(a), addr(b) + 2]
            data words: [u16; 2] = [addr(b) as u16, (addr(b) - 1) as u16]
            var got: u8
            fn main() {
                var p: ptr u8 = ptrs[1]
                got = p[0]
            }
            """);
        Assert.Equal(3, r.U8("got"));
        int ptrs = r.Address("ptrs"), b = r.Address("b"), words = r.Address("words");
        Assert.Equal(r.Address("a"), Word(r, ptrs));
        Assert.Equal(b + 2, Word(r, ptrs + 2));
        Assert.Equal(b, r.Ram.Bytes[words] | r.Ram.Bytes[words + 2] << 8);
        Assert.Equal(b - 1, r.Ram.Bytes[words + 1] | r.Ram.Bytes[words + 3] << 8);
    }

    [Fact]
    public void One_byte_tables_hold_the_halves_of_addresses()
    {
        var r = Run.Program("""
            var screen: [u8; 200]
            data lo: [u8; 3] = [addr(screen).lo, (addr(screen) + 100).lo, (addr(screen) + 199).lo]
            data hi: [u8; 3] = [addr(screen).hi, (addr(screen) + 100).hi, (addr(screen) + 199).hi]
            var out: [u8; 3]
            fn main() {
                for i in 0..200 { screen[i] = i ^ $55 }
                for k in 0..3 {
                    var p: ptr u8 = (lo[k] as u16 | (hi[k] as u16 << 8)) as ptr u8
                    out[k] = p[0]
                }
            }
            """);
        Assert.Equal([0 ^ 0x55, 100 ^ 0x55, 199 ^ 0x55], r.Ram.Bytes.AsSpan(r.Address("out"), 3).ToArray());
    }

    [Fact]
    public void Fields_of_a_struct_global_and_a_struct_at_a_fixed_address()
    {
        var r = Run.Program("""
            struct Player { x: u8, y: u8, name: [u8; 4], hp: u8 }
            var player: Player
            struct Regs { a: u8, b: u8 @ $20 }
            var regs: Regs @ $C100
            data fields: [ptr u8; 3] = [addr(player.name), addr(player.hp), addr(regs.b)]
            data half: [u8; 1] = [addr(player.hp).lo]
            fn main() {
                var p: ptr u8 = fields[1]
                p[0] = 77
            }
            """);
        int player = r.Address("player"), fields = r.Address("fields");
        Assert.Equal(player + 2, r.Ram.Bytes[fields] | r.Ram.Bytes[fields + 3] << 8);
        Assert.Equal(player + 6, r.Ram.Bytes[fields + 1] | r.Ram.Bytes[fields + 4] << 8);
        Assert.Equal(0xC120, r.Ram.Bytes[fields + 2] | r.Ram.Bytes[fields + 5] << 8);
        Assert.Equal((player + 6) & 0xFF, r.U8("half"));
        Assert.Equal(77, r.Ram.Bytes[player + 6]);
    }

    [Fact]
    public void An_rts_jump_table_of_function_addresses_minus_one()
    {
        var r = Run.Program("""
            var out: u8
            fn one() { out = 11 }
            fn two() { out = 22 }
            data jlo: [u8; 2] = [(addr(one) - 1).lo, (addr(two) - 1).lo]
            data jhi: [u8; 2] = [(addr(one) - 1).hi, (addr(two) - 1).hi]
            data words: [u16; 2] = [addr(one) - 1, addr(two) + 0]
            fn go(i: u8 using X) {
                asm {
                    lda jhi,x
                    pha
                    lda jlo,x
                    pha
                    rts
                }
            }
            fn main() { go(1) }
            """);
        Assert.Equal(22, r.U8("out"));
        int one = r.Address("one"), two = r.Address("two");
        Assert.Equal([(byte)(one - 1), (byte)(two - 1)], r.Ram.Bytes.AsSpan(r.Address("jlo"), 2).ToArray());
        Assert.Equal([(byte)((one - 1) >> 8), (byte)((two - 1) >> 8)], r.Ram.Bytes.AsSpan(r.Address("jhi"), 2).ToArray());
        int words = r.Address("words");
        Assert.Equal(one - 1, r.Ram.Bytes[words] | r.Ram.Bytes[words + 2] << 8);
        Assert.Equal(two, r.Ram.Bytes[words + 1] | r.Ram.Bytes[words + 3] << 8);
        Assert.Contains("; lo: (one-1)", r.Result.Listing);
        Assert.Contains("; hi: (two-1)", r.Result.Listing);
    }

    [Fact]
    public void A_variable_with_an_address_and_one_named_further_down()
    {
        // The pointer is dereferenced, so it is in zero page and the startup code sets it.
        var r = Run.Program("""
            var p: ptr u8 = addr(buffer) + 1
            var q: ptr u8 = addr(table)
            var self: u16 = addr(self) as u16
            noinit var buffer: [u8; 4]
            data table: [u8; 2] = [5, 6]
            var got: u8
            fn main() {
                p[0] = 9
                got = q[1]
            }
            """);
        Assert.Equal(9, r.U8("buffer", 1));
        Assert.Equal(6, r.U8("got"));
        Assert.True(r.Address("p") < 0x100);
        Assert.Equal(r.Address("self"), r.U16("self"));
    }

    [Fact]
    public void A_table_keeps_a_module_global_and_function_nothing_else_uses()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "level.tern"), """
                var unused: u8
                data name: [u8; 3] = [76, 86, 49]
                data inner: [ptr u8; 1] = [addr(name) + 1]
                fn start() { }
                data entries: [u16; 1] = [addr(start)]
                """);
            string main = Path.Combine(dir.FullName, "main.tern");
            var r = Run.Program("""
                import level
                data levels: [ptr u8; 3] = [addr(level.name), addr(level.inner), addr(level.entries)]
                var got: u8
                fn main() {
                    var p: ptr u8 = levels[0]
                    got = p[2]
                }
                """, sourcePath: main);
            int name = r.Address("level.name"), inner = r.Address("level.inner"), entries = r.Address("level.entries");
            Assert.Equal([76, 86, 49], r.Ram.Bytes.AsSpan(name, 3).ToArray());
            Assert.Equal(name + 1, r.Ram.Bytes[inner] | r.Ram.Bytes[inner + 1] << 8);
            Assert.Equal(r.Address("level.start"), r.Ram.Bytes[entries] | r.Ram.Bytes[entries + 1] << 8);
            Assert.False(r.Result.Symbols.ContainsKey("level.unused"));
            Assert.Equal(49, r.U8("got"));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("var a: u8\nvar b: u8\ndata t: [u16; 1] = [(addr(a) as u16) + (addr(b) as u16)]\nfn main() { }",
        "An address in an initial value is filled in when the program is placed, so it can only be used alone, plus or minus a constant, or as .lo / .hi. Assign other values at the start of main")]
    [InlineData("var a: u8\ndata t: [u16; 1] = [(addr(a) as u16) & $FF00]\nfn main() { }", "can only be used alone, plus or minus a constant")]
    [InlineData("var a: u8\ndata t: [u16; 1] = [1000 - (addr(a) as u16)]\nfn main() { }", "can only be used alone, plus or minus a constant")]
    [InlineData("fn f() { }\ndata t: [u16; 1] = [addr(f) << 1]\nfn main() { }", "can only be used alone, plus or minus a constant")]
    [InlineData("var a: u8\ndata t: [u16; 1] = [addr(a)]\nfn main() { }", "Convert with 'as u16'")]
    [InlineData("var a: u8\ndata t: [u16; 1] = [addr(a).lo]\nfn main() { }", "'.lo' of an address is one byte, a u8, but this value is a u16. Use a [u8; n] table, or the whole address")]
    [InlineData("var a: u8\ndata t: [i8; 1] = [addr(a).hi]\nfn main() { }", "'.hi' of an address is one byte, a u8, but this value is a i8")]
    [InlineData("var a: u8\ndata t: [u8; 1] = [addr(a)]\nfn main() { }", "Expected u8, found ptr u8")]
    [InlineData("const C = 5\ndata t: [u16; 1] = [addr(C)]\nfn main() { }", "'C' is a constant, which has no address")]
    [InlineData("data t: [u16; 1] = [addr(nothing)]\nfn main() { }", "'nothing' is not declared")]
    public void Reports_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
    }
}
