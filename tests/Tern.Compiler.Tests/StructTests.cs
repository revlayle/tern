namespace Tern.Compiler.Tests;

public class StructTests
{
    /// <summary>The instructions of a function, without addresses, bytes and cycles.</summary>
    private static string Code(Run r, string function = "main")
    {
        var listing = r.Result.Listing;
        var start = listing.IndexOf($"\n{function}:", StringComparison.Ordinal);
        var lines = listing[(start + 1)..].Split('\n').Skip(1);
        var code = new List<string>();
        foreach (var l in lines)
        {
            if (l.EndsWith(':') && !l.StartsWith(' ') && !l.StartsWith('$') && !l.StartsWith(';')) break;
            if (l.StartsWith('$')) code.Add(l[17..].Split(';')[0].Trim());
        }
        return string.Join("|", code);
    }

    // ---------- Declarations and layout ----------

    [Fact]
    public void Sizeof_adds_up_the_fields()
    {
        var r = Run.Program("""
            struct Point { x: u8, y: u8 }
            struct Enemy { pos: Point, hp: u8, vel: i16, name: [u8; 8], next: ptr Enemy }
            struct Words { a: [u16; 3], flag: bool }
            var sizes: [u8; 3] = [sizeof(Point), sizeof(Enemy), sizeof(Words)]
            fn main() { }
            """);
        Assert.Equal(2, r.U8("sizes", 0));
        Assert.Equal(2 + 1 + 2 + 8 + 2, r.U8("sizes", 1));
        Assert.Equal(7, r.U8("sizes", 2));
    }

    [Fact]
    public void Field_offsets_with_at_leave_gaps_and_set_the_size()
    {
        var r = Run.Program("""
            struct Regs { a: u8 @ $00, b: u8 @ $20, c: u8, d: u16 @ $10 }
            const SIZE = sizeof(Regs)
            var size: u8 = SIZE
            fn main() { }
            """);
        Assert.Equal(0x22, r.U8("size"));
    }

    [Fact]
    public void Structs_may_be_declared_after_their_use_and_over_several_lines()
    {
        var r = Run.Program("""
            var e: Enemy
            fn main() {
                e.pos.y = 7
                e.hp = 3
            }
            struct Enemy {
                pos: Point
                hp: u8,
            }
            struct Point { x: u8, y: u8 }
            """);
        Assert.Equal(7, r.U8("e", 1));
        Assert.Equal(3, r.U8("e", 2));
    }

    // ---------- Struct variables ----------

    [Fact]
    public void Fields_of_a_struct_variable_are_read_and_written()
    {
        var r = Run.Program("""
            struct Point { x: u8, y: u8 }
            struct Player { pos: Point, hp: u8, score: u16, vel: i16, alive: bool, name: [u8; 4], pts: [u16; 2] }
            var p: Player
            var out: [u8; 8]
            var big: u16
            var neg: i16
            fn main() {
                p.pos.x = 10
                p.pos.y = p.pos.x + 5
                p.hp = 3
                p.hp -= 1
                p.hp += 1
                p.hp += 1
                p.score = 1000
                p.score += 300
                p.score += 1
                p.vel = -2
                p.alive = p.hp == 4
                for i in 0..4 { p.name[i] = i + 65 }
                p.pts[0] = $1234
                p.pts[1] = p.pts[0] + 1
                var k: u8 = 1
                out[0] = p.pos.y
                out[1] = p.name[k]
                out[2] = p.score.hi
                out[3] = p.pts[k].lo
                if p.alive { out[4] = 1 }
                p.score.lo = 9
                big = p.score
                neg = p.vel
            }
            """);
        int a = r.Address("p");
        byte[] m = r.Ram.Bytes;
        Assert.Equal(10, m[a]);
        Assert.Equal(15, m[a + 1]);
        Assert.Equal(4, m[a + 2]);
        Assert.Equal(1301 & 0xFF00 | 9, m[a + 3] | m[a + 4] << 8);
        Assert.Equal(-2, (short)(m[a + 5] | m[a + 6] << 8));
        Assert.Equal(1, m[a + 7]);
        Assert.Equal("ABCD", System.Text.Encoding.ASCII.GetString(m, a + 8, 4));
        // [u16; 2] is split inside the struct: low bytes, then high bytes.
        Assert.Equal(new byte[] { 0x34, 0x35, 0x12, 0x12 }, m[(a + 12)..(a + 16)]);
        Assert.Equal(new byte[] { 15, (byte)'B', 1301 >> 8, 0x35, 1 }, m[r.Address("out")..(r.Address("out") + 5)]);
        Assert.Equal(1301 & 0xFF00 | 9, r.U16("big"));
        Assert.Equal(-2, r.I16("neg"));
    }

    [Theory]
    // Spec 3.3: player.hp is lda player+2.
    [InlineData("x = player.hp", "lda player+2|sta x|rts")]
    [InlineData("player.pos.y = 5", "lda #$05|sta player+1|rts")]
    [InlineData("player.hp += 1", "inc player+2|rts")]
    [InlineData("player.score += 1", "inc player+3|bne main.inc0|inc player+4|rts")]
    // Spec 3.3: player.name[i] is lda player+off,x.
    [InlineData("x = player.name[i]", "ldx i|lda player+5,x|sta x|rts")]
    // Same order as a plain array store.
    [InlineData("player.name[i] = x", "lda x|ldx i|sta player+5,x|rts")]
    public void Struct_variable_code_shapes(string statement, string expected)
    {
        var r = Run.Program($$"""
            struct Point { x: u8, y: u8 }
            struct Player { pos: Point, hp: u8, score: u16, name: [u8; 8] }
            var player: Player
            var x: u8
            var i: u8
            fn main() { {{statement}} }
            """);
        Assert.Equal(expected, Code(r));
    }

    [Fact]
    public void Struct_variables_are_zeroed_at_startup()
    {
        var r = Run.Program("""
            struct S { a: u8, b: u16, c: [u8; 3] }
            var s: S
            var copy: u8
            fn main() { copy = s.a | s.b.lo | s.b.hi | s.c[2] }
            """, setup: ram => { });
        Assert.Equal(0, r.U8("copy"));
    }

    [Fact]
    public void A_local_struct_needs_no_initial_value()
    {
        var r = Run.Program("""
            struct Point { x: u8, y: u8 }
            var out: u8
            fn sum(a: u8, b: u8) -> u8 {
                var p: Point
                p.x = a
                p.y = b
                return p.x + p.y
            }
            fn main() { out = sum(3, 4) }
            """);
        Assert.Equal(7, r.U8("out"));
    }

    [Fact]
    public void A_struct_variable_in_zero_page()
    {
        var r = Run.Program("""
            struct Point { x: u8, y: u8 }
            var p: Point @ zp
            fn main() { p.y = 9 }
            """);
        Assert.True(r.Address("p") < 0x100);
        Assert.Equal(9, r.U8("p", 1));
        Assert.Contains("sta p+1", Code(r));
    }

    // ---------- Structs at a fixed address ----------

    [Fact]
    public void A_struct_at_a_fixed_address_uses_the_field_offsets()
    {
        var r = Run.Program("""
            struct Vic { sprite0_x: u8 @ $00, border: u8 @ $20, background: u8 @ $21, colors: [u8; 4] @ $27 }
            var vic: Vic @ $D000
            fn main() {
                vic.border = 6
                vic.background = vic.border + 1
                vic.sprite0_x = 100
                vic.colors[3] = 2
            }
            """);
        Assert.Equal(6, r.Ram.Bytes[0xD020]);
        Assert.Equal(7, r.Ram.Bytes[0xD021]);
        Assert.Equal(100, r.Ram.Bytes[0xD000]);
        Assert.Equal(2, r.Ram.Bytes[0xD02A]);
        // Spec 3.3: vic.border = 0 is sta $D020.
        Assert.StartsWith("lda #$06|sta vic+32|lda vic+32|clc|adc #$01|sta vic+33|", Code(r));
        Assert.Contains("8D 20 D0", r.Result.Listing); // sta $D020
    }

    [Fact]
    public void Fields_at_a_fixed_address_are_volatile()
    {
        // Every read and write happens: two reads of the same register are not merged.
        var r = Run.Program("""
            struct Regs { status: u8 @ $01 }
            var regs: Regs @ $C000
            var a: u8
            fn main() {
                a = regs.status + regs.status
                regs.status = 1
                regs.status = 2
            }
            """);
        Assert.Equal("lda regs+1|clc|adc regs+1|sta a|lda #$01|sta regs+1|lda #$02|sta regs+1|rts", Code(r));
    }
}
