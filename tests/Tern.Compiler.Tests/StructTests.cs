using Tern.Compiler.Syntax;

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
    public void Sixteen_bit_operations_between_fields_need_no_temporary()
    {
        // Review M5, finding 3: different fields of one struct do not overlap, so s.b is used directly.
        var r = Run.Program("""
            struct S { a: u16, b: u16, w: u16, t: [u16; 2] }
            var s: S
            fn main() {
                s.a = $01FF
                s.b = $0101
                s.w = s.a + s.b
                s.b = s.a + s.b          // the target is read on the right: still correct
                s.t[1] = $0010
                s.t[0] = s.w + s.t[1]
            }
            """);
        int s = r.Address("s");
        int Word(int lo, int hi) => r.Ram.Bytes[s + lo] | r.Ram.Bytes[s + hi] << 8;
        Assert.Equal(0x0300, Word(4, 5));           // s.w
        Assert.Equal(0x0300, Word(2, 3));           // s.b
        Assert.Equal(0x0310, Word(6, 8));           // s.t[0]: [u16; 2] is split, low bytes at 6-7, high at 8-9

        // The same shape as z = x + y with plain variables.
        var shape = Run.Program("struct S { a: u16, b: u16, w: u16 }\nvar s: S\nfn main() { s.w = s.a + s.b }");
        Assert.Equal("lda s|sta s+4|lda s+1|sta s+5|clc|lda s+4|adc s+2|sta s+4|lda s+5|adc s+3|sta s+5|rts", Code(shape));
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

    // ---------- Through a pointer ----------

    [Fact]
    public void Fields_are_read_and_written_through_a_pointer()
    {
        var r = Run.Program("""
            struct Point { x: u8, y: u8 }
            struct Enemy { pos: Point, hp: u8, vel: i16, name: [u8; 4], pts: [u16; 2], next: ptr Enemy }
            var a: Enemy
            var b: Enemy
            var out: [u8; 8]
            var w: u16
            fn main() {
                var e: ptr Enemy = addr(a)
                e.pos.x = 1
                e.pos.y = e.pos.x + 1
                e.hp = 10
                e.hp -= 1
                e.vel = -300
                e.vel += 1
                for i in 0..4 { e.name[i] = i + 65 }
                e.pts[0] = $1234
                var k: u8 = 1
                e.pts[k] = e.pts[0] + $0101
                e.next = addr(b)
                e.next.hp = 77
                e.next.pos.y = e.hp
                out[0] = e.name[k]
                out[1] = e.next.hp
                out[2] = e.pts[k].hi
                out[3] = e.vel.lo
                w = e.pts[k]
                e = e.next
                out[4] = e.hp
            }
            """);
        int a = r.Address("a"), b = r.Address("b");
        byte[] m = r.Ram.Bytes;
        Assert.Equal(new byte[] { 1, 2, 9 }, m[a..(a + 3)]);
        Assert.Equal(-299, (short)(m[a + 3] | m[a + 4] << 8));
        Assert.Equal("ABCD", System.Text.Encoding.ASCII.GetString(m, a + 5, 4));
        Assert.Equal(new byte[] { 0x34, 0x35, 0x12, 0x13 }, m[(a + 9)..(a + 13)]);
        Assert.Equal(b, m[a + 13] | m[a + 14] << 8);
        Assert.Equal(77, m[b + 2]);
        Assert.Equal(9, m[b + 1]);
        Assert.Equal(new byte[] { (byte)'B', 77, 0x13, unchecked((byte)-299), 77 }, m[r.Address("out")..(r.Address("out") + 5)]);
        Assert.Equal(0x1335, r.U16("w"));
        Assert.True(r.Address("main.e") < 0x100, "a pointer used for fields must be in zero page");
    }

    [Theory]
    // Spec 3.3.
    [InlineData("e.hp -= 1", "ldy #$02|lda (e),y|sec|sbc #$01|sta (e),y|rts")]
    [InlineData("e.pos.y = 5", "ldy #$01|lda #$05|sta (e),y|rts")]
    [InlineData("x = e.name[i]", "lda i|clc|adc #$03|tay|lda (e),y|sta x|rts")]
    [InlineData("x = e.name[2]", "ldy #$05|lda (e),y|sta x|rts")]
    [InlineData("e = e + sizeof(Enemy)", "clc|lda e|adc #$0D|sta e|bcc main.c0|inc e+1|rts")]
    // Following a pointer field copies it to a zero-page temporary first.
    [InlineData("x = e.next.hp", "ldy #$0B|lda (e),y|sta main._t0|iny|lda (e),y|sta main._t0+1|ldy #$02|lda (main._t0),y|sta x|rts")]
    public void Pointer_field_code_shapes(string statement, string expected)
    {
        var r = Run.Program($$"""
            struct Point { x: u8, y: u8 }
            struct Enemy { pos: Point, hp: u8, name: [u8; 8], next: ptr Enemy }
            var e: ptr Enemy @ zp
            var x: u8
            var i: u8
            fn main() { {{statement}} }
            """);
        Assert.Equal(expected, Code(r));
    }

    [Fact]
    public void A_pointer_steps_through_a_linear_array_of_structs()
    {
        var r = Run.Program("""
            struct Enemy { x: u8, y: u8, hp: u8, score: u16 }
            linear var list: [Enemy; 5]
            fn main() {
                var e: ptr Enemy = addr(list)
                for i in 0..len(list) {
                    e.x = i
                    e.y = (i << 1) + 1
                    e.hp = 100 - i
                    e.score = 1000 + (i as u16)
                    e = e + sizeof(Enemy)
                }
                // Change every record through the pointer again.
                e = addr(list)
                for _ in 0..5 {
                    e.hp -= 1
                    e.score += 1
                    e += sizeof(Enemy)
                }
            }
            """);
        int a = r.Address("list");
        for (int i = 0; i < 5; i++)
        {
            int at = a + 5 * i;
            Assert.Equal(i, r.Ram.Bytes[at]);
            Assert.Equal(i * 2 + 1, r.Ram.Bytes[at + 1]);
            Assert.Equal(99 - i, r.Ram.Bytes[at + 2]);
            Assert.Equal(1001 + i, r.Ram.Bytes[at + 3] | r.Ram.Bytes[at + 4] << 8);
        }
    }

    [Fact]
    public void A_cast_pointer_reads_any_memory_as_a_struct()
    {
        var r = Run.Program("""
            struct Pair { lo: u8, hi: u8 }
            var raw: [u8; 2] = [7, 9]
            var out: u8
            fn main() {
                var p: ptr u8 = addr(raw)
                out = (p as ptr Pair).hi
            }
            """);
        Assert.Equal(9, r.U8("out"));
    }

    [Fact]
    public void A_pointer_field_of_a_struct_variable_is_followed()
    {
        var r = Run.Program("""
            struct Node { value: u8, next: ptr Node }
            var first: Node
            var second: Node
            var out: u8
            fn main() {
                first.next = addr(second)
                first.next.value = 42
                out = first.next.value + 1
            }
            """);
        Assert.Equal(42, r.U8("second"));
        Assert.Equal(43, r.U8("out"));
    }

    [Fact]
    public void A_chain_of_pointer_fields_is_followed()
    {
        var r = Run.Program("""
            struct Node { value: u8, next: ptr Node }
            var n1: Node
            var n2: Node
            var n3: Node
            var out: u8
            fn main() {
                var p: ptr Node = addr(n1)
                p.next = addr(n2)
                p.next.next = addr(n3)
                p.next.next.value = 5
                out = p.next.next.value + n1.next.next.value
            }
            """);
        Assert.Equal(5, r.U8("n3"));
        Assert.Equal(10, r.U8("out"));
    }

    // ---------- Arrays of structs: one table per field ----------

    [Fact]
    public void Each_field_of_an_array_of_structs_is_its_own_table()
    {
        var r = Run.Program("""
            struct Point { x: u8, y: u8 }
            struct Enemy { pos: Point, hp: u8, vel: i16, name: [u8; 2], pts: [u16; 2] }
            var enemies: [Enemy; 4]
            fn main() {
                for i in 0..len(enemies) {
                    enemies[i].pos.x = i
                    enemies[i].pos.y = i + 10
                    enemies[i].hp = 100
                    enemies[i].hp -= i
                    enemies[i].vel = -1 - (i as i8 as i16)
                    enemies[i].name[0] = 'A'
                    enemies[i].name[1] = i + 48
                    enemies[i].pts[1] = $1000 + (i as u16)
                }
                enemies[2].pts[0] = $ABCD
            }
            """);
        int a = r.Address("enemies");
        byte[] m = r.Ram.Bytes;
        // Byte k of record i is at enemies + k * 4 + i.
        byte At(int k, int i) => m[a + k * 4 + i];
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(i, At(0, i));
            Assert.Equal(i + 10, At(1, i));
            Assert.Equal(100 - i, At(2, i));
            Assert.Equal(-1 - i, (short)(At(3, i) | At(4, i) << 8));
            Assert.Equal((byte)'A', At(5, i));
            Assert.Equal(i + 48, At(6, i));
            // pts: [u16; 2] is split in the record (low bytes at 7-8, high bytes at 9-10).
            Assert.Equal(0x1000 + i, At(8, i) | At(10, i) << 8);
        }
        Assert.Equal(0xABCD, At(7, 2) | At(9, 2) << 8);
        Assert.Contains("Variables after the program: 44 bytes", r.Result.Report); // 4 records of 11 bytes
    }

    [Theory]
    // Spec 3.3: enemies[i].x is a single lda enemies_x,x. Byte k of the record is the table at k * 8.
    [InlineData("x = enemies[i].x", "ldx i|lda enemies,x|sta x|rts")]
    [InlineData("x = enemies[i].pos.y", "ldx i|lda enemies+32,x|sta x|rts")]
    [InlineData("enemies[i].hp += 1", "ldx i|lda enemies+40,x|clc|adc #$01|sta enemies+40,x|rts")]
    [InlineData("x = enemies[3].hp", "lda enemies+43|sta x|rts")]
    [InlineData("x = enemies[i].name[2]", "ldx i|lda enemies+80,x|sta x|rts")]
    [InlineData("w = enemies[i].vel", "ldx i|lda enemies+48,x|sta w|lda enemies+56,x|sta w+1|rts")]
    public void Struct_array_code_shapes(string statement, string expected)
    {
        var r = Run.Program($$"""
            struct Point { x: u8, y: u8 }
            struct Enemy { x: u8, y: u8, z: u8, pos: Point, hp: u8, vel: i16, name: [u8; 4] }
            var enemies: [Enemy; 8]
            var x: u8
            var w: i16
            var i: u8
            fn main() { {{statement}} }
            """);
        Assert.Equal(expected, Code(r));
    }

    [Fact]
    public void A_pointer_field_in_an_array_of_structs_is_followed()
    {
        var r = Run.Program("""
            struct Node { value: u8, next: ptr Node }
            var nodes: [Node; 3]
            var target: Node
            var out: u8
            fn main() {
                for i in 0..3 { nodes[i].next = addr(target) }
                var k: u8 = 2
                nodes[k].next.value = 5
                out = nodes[1].next.value
            }
            """);
        Assert.Equal(5, r.U8("target"));
        Assert.Equal(5, r.U8("out"));
    }

    [Fact]
    public void A_list_of_enemies_updates_through_indexing_and_pointers()
    {
        // The roadmap example: the same update, once by index and once through a pointer.
        var r = Run.Program("""
            struct Enemy { x: u8, y: u8, dx: u8, hp: u8 }
            var table: [Enemy; 6]
            linear var list: [Enemy; 6]
            fn main() {
                var e: ptr Enemy = addr(list)
                for i in 0..6 {
                    table[i].x = i << 2
                    table[i].dx = i + 1
                    table[i].hp = 3
                    e.x = i << 2
                    e.dx = i + 1
                    e.hp = 3
                    e += sizeof(Enemy)
                }
                for _ in 0..10 {
                    for i in 0..6 using X {
                        table[i].x += table[i].dx
                        if table[i].x >= 40 { table[i].hp -= 1 }
                    }
                    e = addr(list)
                    for _ in 0..6 {
                        e.x += e.dx
                        if e.x >= 40 { e.hp -= 1 }
                        e += sizeof(Enemy)
                    }
                }
            }
            """);
        int t = r.Address("table"), l = r.Address("list");
        for (int i = 0; i < 6; i++)
        {
            int x = i << 2, hp = 3;
            for (int f = 0; f < 10; f++)
            {
                x = (x + i + 1) & 0xFF;
                if (x >= 40) hp = (hp - 1) & 0xFF;
            }
            Assert.Equal(x, r.Ram.Bytes[t + i]);
            Assert.Equal(hp, r.Ram.Bytes[t + 3 * 6 + i]);
            Assert.Equal(x, r.Ram.Bytes[l + 4 * i]);
            Assert.Equal(hp, r.Ram.Bytes[l + 4 * i + 3]);
        }
    }

    // ---------- Errors ----------

    private const string P = "struct Point { x: u8, y: u8 }\n";
    private const string E = "struct Enemy { pos: Point, hp: u8, name: [u8; 4], next: ptr Enemy }\n" + P;

    [Theory]
    // Declarations
    [InlineData("fn main() { struct S { x: u8 } }", "A struct must be declared outside functions")]
    [InlineData("struct S { x: u8 y: u8 }\nfn main() { }", "Expected ',' or a line break after the field")]
    [InlineData("struct S { x: u8\nfn main() { }", "Expected a field 'name: type', found 'fn'")]
    [InlineData("struct S {", "Struct 'S' is not closed with '}'")]
    [InlineData("struct u8 { x: u8 }\nfn main() { }", "'u8' is a built-in type")]
    [InlineData("struct S { }\nfn main() { }", "Struct 'S' has no fields")]
    [InlineData("struct S { x: u8, x: u16 }\nfn main() { }", "Field 'x' is declared twice in 'S'")]
    [InlineData("struct S { inner: S }\nfn main() { }", "Struct 'S' contains itself")]
    [InlineData("struct A { b: B }\nstruct B { a: A }\nfn main() { }", "contains itself")]
    [InlineData(P + "struct S { ps: [Point; 2] }\nfn main() { }", "A field cannot be an array of structs yet")]
    [InlineData("struct S { x: u8 @ -1 }\nfn main() { }", "Field offset -1 is outside memory")]
    [InlineData("struct S { a: u16 @ $FFFF }\nfn main() { }", "Struct 'S' is 65537 bytes, more than memory")]
    [InlineData("struct S { a: u16, b: u8 @ 1 }\nfn main() { }", "Field 'b' (offset 1, 1 byte(s)) overlaps field 'a' (offset 0, 2 byte(s))")]
    [InlineData("var x: foo\nfn main() { }", "Unknown type 'foo'")]
    [InlineData("var y: u8\nvar x: y\nfn main() { }", "'y' is not a type")]
    [InlineData(P + "fn main() { var n: u8 = sizeof(3) }", "sizeof() takes one type name")]
    // Variables
    [InlineData(P + "var p: Point = 3\nfn main() { }", "A struct cannot have an initial value")]
    [InlineData(P + "fn main() { var p: Point = 3 }", "A struct cannot have an initial value")]
    [InlineData(P + "var ps: [Point; 2] = [1, 2]\nfn main() { }", "A struct cannot have an initial value")]
    [InlineData(P + "data ps: [Point; 2] = [1, 2]\nfn main() { }", "Data cannot hold structs yet")]
    [InlineData("linear var a: u8\nfn main() { }", "'linear' is for arrays of 16-bit values or structs")]
    [InlineData("linear fn main() { }", "'linear' goes before 'var' or 'data'")]
    [InlineData(P + "fn main() { var n: u8 = Point }", "'Point' is a struct type, not a value")]
    // Structs are never copied
    [InlineData(P + "var a: Point\nvar b: Point\nfn main() { a = b }", "a struct cannot be assigned")]
    [InlineData(P + "var a: Point\nvar b: Point\nfn main() { if a == b { } }", "Structs cannot be compared")]
    [InlineData(P + "var a: Point\nvar n: u8\nfn main() { n = a }", "A struct cannot be used as a value")]
    [InlineData(P + "fn f(p: Point) { }\nfn main() { }", "A struct cannot be passed to a function")]
    [InlineData(P + "fn f() -> Point { }\nfn main() { }", "A function cannot return a struct")]
    [InlineData(P + "var ps: [Point; 4]\nvar n: u8\nfn main() { n = ps[1] }", "A struct cannot be used as a value")]
    // Fields
    [InlineData(P + "var a: Point\nfn main() { a.z = 1 }", "Struct 'Point' has no field 'z'. Its fields: x, y")]
    [InlineData(P + "var p: ptr Point\nfn main() { p.z = 1 }", "Struct 'Point' has no field 'z'")]
    [InlineData(P + "var p: ptr u8\nfn main() { p.x = 1 }", "Unknown member '.x'")]
    [InlineData(E + "var a: Enemy\nfn main() { a.name[4] = 1 }", "Index 4 is past the end of 'a.name'")]
    [InlineData(E + "var p: ptr Enemy\nfn main() { p.name[4] = 1 }", "Index 4 is past the end of 'p.name'")]
    [InlineData("struct Big { pad: [u8; 255], w: u16 }\nvar p: ptr Big\nvar n: u16\nfn main() { n = p.w }", "'p.w' is at offset 256 of the struct")]
    [InlineData("struct Big { pad: [u8; 250], a: [u8; 8] }\nvar p: ptr Big\nvar n: u8\nvar i: u8\nfn main() { n = p.a[i] }", "'p.a' reaches offset 257 of the struct")]
    // Pointers and arrays of structs
    [InlineData(P + "var p: ptr Point\nvar n: u8\nfn main() { n = p[1].x }", "A pointer to a struct cannot be indexed")]
    [InlineData(E + "var es: [Enemy; 4]\nvar i: u8\nvar n: u8\nfn main() { n = es[0].name[i] }", "An array field in an array of structs needs a constant index")]
    [InlineData(E + "var es: [Enemy; 4]\nvar n: u8\nfn main() { n = es[0].name[4] }", "Index 4 is past the end of 'es.name'")]
    [InlineData(P + "var ps: [Point; 4]\nfn main() { ps[4].x = 1 }", "Index 4 is past the end of 'ps'")]
    [InlineData(P + "linear var ps: [Point; 4]\nfn main() { ps[0].x = 1 }", "'ps' is linear, so it cannot be indexed")]
    [InlineData(P + "var ps: [Point; 4]\nvar p: ptr Point\nfn main() { p = addr(ps) }", "Declare it 'linear var'")]
    [InlineData(E + "var p: ptr Enemy\nfn main() { for i in 0..4 using Y { p.name[i] = 0 } }", "p[i] needs the Y register, which holds a loop variable")]
    // Review M5, findings 1 and 2.
    [InlineData(P + "var s: Point\nfn main() { s.x() }", "'s.x' is a field, not a function; only functions can be called")]
    [InlineData(P + "var p: ptr Point\nfn main() { p.x() }", "'p.x' is a field, not a function")]
    [InlineData("var v: u8\nfn main() { v.x() }", "'v.x' is not a function: 'v' is a variable, not a module")]
    [InlineData(P + "var s: Point\nvar n: u8\nfn main() { n = s + 1 }", "A struct is not a number; use one of its fields, e.g. s.x + 1")]
    [InlineData(P + "var s: Point\nvar n: u8\nfn main() { n = s << 1 }", "A struct is not a number; use one of its fields, e.g. s.x << 1")]
    [InlineData(P + "var s: Point\nfn main() { var x = s }", "A struct cannot be used as a value")]
    [InlineData(P + "var s: Point\nvar x = s\nfn main() { }", "A struct cannot be used as a value")]
    public void Reports_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- addr() of fields ----------

    [Fact]
    public void Addr_of_a_field_points_into_the_struct_variable()
    {
        var r = Run.Program("""
            struct Point { x: u8, y: u8 }
            struct Player { pos: Point, hp: u8, score: u16, name: [u8; 4], pts: [u16; 2] }
            struct Regs { a: u8, b: u8, w: u16 }
            var player: Player
            var zpp: Player @ zp
            var regs: Regs @ $C000
            var at: [u16; 6]
            fn set(p: ptr u8, v: u8) { p[0] = v }
            fn main() {
                var pn: ptr u8 = addr(player.name)
                for i in 0..4 { pn[i] = 10 + i }
                set(addr(player.hp), 77)
                var ps: ptr u16 = addr(player.score)
                ps[0] = 1234
                var pp: ptr Point = addr(player.pos)
                pp.y = 5
                set(addr(zpp.hp), 66)
                set(addr(regs.b), 99)
                at[0] = addr(player.name) as u16
                at[1] = addr(player) as u16
                at[2] = addr(regs.w) as u16
                at[3] = addr(zpp.name) as u16
                at[4] = addr(player.pts) as u16
                at[5] = addr(player.pos.y) as u16
            }
            """);
        int player = r.Address("player");
        Assert.Equal(new byte[] { 10, 11, 12, 13 }, r.Ram.Bytes[(player + 5)..(player + 9)]);
        Assert.Equal(77, r.U8("player", 2));
        Assert.Equal(1234, r.U8("player", 3) | r.U8("player", 4) << 8);
        Assert.Equal(5, r.U8("player", 1));
        Assert.Equal(66, r.U8("zpp", 2));
        Assert.Equal(99, r.Ram.Bytes[0xC001]);
        Assert.Equal(player + 5, r.U16At("at", 0, 6));
        Assert.Equal(player, r.U16At("at", 1, 6));
        Assert.Equal(0xC002, r.U16At("at", 2, 6));
        Assert.Equal(r.Address("zpp") + 5, r.U16At("at", 3, 6));
        Assert.True(r.Address("zpp") < 0x100);
        Assert.Equal(player + 9, r.U16At("at", 4, 6)); // the low bytes of the split array field
        Assert.Equal(player + 1, r.U16At("at", 5, 6));
    }

    [Fact]
    public void Addr_of_a_field_is_a_constant_address()
    {
        var r = Run.Program("""
            struct Player { hp: u8, name: [u8; 8] }
            var player: Player
            var p: ptr u8
            fn main() { p = addr(player.name) }
            """);
        Assert.Equal("lda #(player+1).lo|sta p|lda #(player+1).hi|sta p+1|rts", Code(r));
    }

    [Fact]
    public void Addr_of_a_field_passes_it_to_the_library()
    {
        // The reason for addr() of fields (decisions 2026-11-01): text.put(dst, addr(player.name), n).
        var r = Run.Program("""
            import text
            struct Player { hp: u8, name: [u8; 3] }
            var player: Player
            var out: [u8; 3]
            fn main() {
                player.name[0] = 1
                player.name[1] = 2
                player.name[2] = 3
                text.put(addr(out), addr(player.name), len(player.name))
            }
            """);
        Assert.Equal(new byte[] { 1, 2, 3 }, r.Ram.Bytes[r.Address("out")..(r.Address("out") + 3)]);
    }

    [Fact]
    public void Addr_of_a_field_in_a_module()
    {
        var r = Run.Program("""
            import c64
            var p: ptr u8
            var q: ptr u8
            fn main() {
                p = addr(c64.vic.border)
                q = addr(c64.sid.voice2.control)
            }
            """);
        Assert.Equal(0xD020, r.U16("p"));
        Assert.Equal(0xD40B, r.U16("q"));
    }

    [Theory]
    [InlineData(E + "var p: ptr Enemy\nvar q: ptr u8\nfn main() { q = addr(p.hp) }", "'p.hp' is reached through a pointer, so it has no fixed address")]
    [InlineData(E + "var p: ptr Enemy\nvar q: ptr u8\nfn main() { q = addr(p.name) }", "'p.name' is reached through a pointer")]
    [InlineData(E + "var p: ptr Enemy\nvar q: ptr Point\nfn main() { q = addr(p.pos) }", "'p.pos' is reached through a pointer")]
    [InlineData(E + "var a: Enemy\nvar q: ptr u8\nfn main() { q = addr(a.next.hp) }", "'a.next.hp' is reached through a pointer")]
    [InlineData(E + "var es: [Enemy; 4]\nvar q: ptr u8\nfn main() { q = addr(es[1].hp) }", "'es.hp' is in an array of structs, which is stored as one table per field")]
    [InlineData(E + "var es: [Enemy; 4]\nvar q: ptr u8\nfn main() { q = addr(es[1].name) }", "'es.name' is in an array of structs")]
    [InlineData(E + "var es: [Enemy; 4]\nvar q: ptr Point\nfn main() { q = addr(es[1].pos) }", "'es.pos' is in an array of structs")]
    [InlineData(E + "var a: Enemy\nvar q: ptr u8\nfn main() { q = addr(a.nope) }", "Struct 'Enemy' has no field 'nope'")]
    [InlineData("var w: u16\nvar q: ptr u8\nfn main() { q = addr(w.lo) }", "addr() takes the name of a variable, array or data, or a field of a struct variable")]
    [InlineData(E + "var a: Enemy\nvar q: ptr u8\nfn main() { q = addr(a.name[1]) }", "addr() takes the name of a variable, array or data, or a field of a struct variable")]
    [InlineData(E + "var a: Enemy\nvar q: ptr u8\nfn main() { q = addr(a.pos) }", "Expected ptr u8, found ptr Point")]
    public void Addr_of_a_field_reports_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
    }

    [Fact]
    public void A_field_at_offset_255_is_reached_through_a_pointer()
    {
        var r = Run.Program("""
            struct Big { pad: [u8; 255], last: u8 }
            var big: Big
            fn main() {
                var p: ptr Big = addr(big)
                p.last = 9
            }
            """);
        Assert.Equal(9, r.U8("big", 255));
    }
}
