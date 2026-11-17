using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary><c>offset(T.field)</c> (spec 10, decision 2026-11-11, question 95): a field's offset in bytes, an untyped constant.</summary>
public sealed class OffsetTests : TempFolderTest
{

    private static CompileError Error(string source) => Assert.Throws<CompileError>(() => TernCompiler.Compile(source));

    private const string Structs = """
        struct Point { x: u8, y: u8 }
        struct Enemy { kind: u8, pos: Point, vel: i16, hp: u8 }
        struct Regs { a: u8 @ $00, b: u8 @ $20, c: u8 }

        """;

    [Fact]
    public void Offset_is_a_constant_with_nested_fields_and_fields_placed_with_at()
    {
        var r = Run.Program(Structs + """
            const HP = offset(Enemy.hp)
            const fn after(n: int) -> int {
                return n + offset(Enemy.pos.y)
            }
            data t: [u8; _] = [offset(Enemy.kind), offset(Enemy.pos), offset(Enemy.pos.x), offset(Enemy.pos.y), offset(Enemy.vel), HP,
                               offset(Regs.b), offset(Regs.c), after(10)]
            var big: u16 = offset(Point.y) * 300
            fn main() { }
            """);
        Assert.Equal([0, 1, 1, 2, 3, 5, 0x20, 0x21, 12], r.Ram.Bytes.AsSpan(r.Address("t"), 9).ToArray());
        Assert.Equal(300, r.U16("big"));
    }

    [Fact]
    public void Offset_of_a_module_struct_is_qualified()
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "shapes.tern"), "struct Box { w: u8, h: u8, depth: u16 }\n");
        var r = Run.Program("""
            import shapes
            var d: u8 = offset(shapes.Box.depth)
            fn main() { }
            """, sourcePath: Path.Combine(_dir.FullName, "main.tern"));
        Assert.Equal(2, r.U8("d"));
    }

    [Fact]
    public void Assembly_reads_a_field_through_a_pointer_with_offset()
    {
        var r = Run.Program(Structs + """
            var enemy: Enemy
            var p: ptr Enemy @ zp
            var hp: u8
            var y: u8
            fn main() {
                enemy.hp = 77
                enemy.pos.y = 9
                p = addr(enemy)
                asm {
                    ldy #offset(Enemy.hp)
                    lda (p),y
                    sta hp
                    ldy #offset(Enemy.pos.y)
                    lda (p),y
                    sta y
                }
            }
            """);
        Assert.Equal(77, r.U8("hp"));
        Assert.Equal(9, r.U8("y"));
    }

    [Fact]
    public void An_emit_operand_may_use_offset()
    {
        var r = Run.Program(Structs + """
            const fn read_field(field: int) {
                emit {
                    ldy #field + offset(Enemy.pos)
                    lda (p),y
                    sta out
                }
            }
            var enemy: Enemy
            var p: ptr Enemy @ zp
            var out: u8
            fn main() {
                enemy.pos.y = 42
                p = addr(enemy)
                asm {
                    read_field(offset(Point.y))
                }
            }
            """);
        Assert.Equal(42, r.U8("out"));
    }

    [Theory]
    [InlineData("const K = offset(u8.x)", "'u8' is not a struct type. Use offset() with a struct type and one of its fields, e.g. offset(Enemy.hp)")]
    [InlineData("const K = offset(N.x)", "'N' is not a struct type")]
    [InlineData("const K = offset(Thing.x)", "'Thing' is not declared")]
    [InlineData("const K = offset(Enemy.speed)", "Struct 'Enemy' has no field 'speed'. Use one of its fields: kind, pos, vel, hp")]
    [InlineData("const K = offset(Enemy.pos.z)", "Struct 'Point' has no field 'z'. Use one of its fields: x, y")]
    [InlineData("const K = offset(Enemy.hp.x)", "'Enemy.hp' is not a struct, so it has no field 'x'. Remove '.x'")]
    [InlineData("const K = offset(Enemy)", "offset() takes a struct type and one of its fields. Write e.g. offset(Enemy.hp)")]
    [InlineData("const K = offset(Enemy.hp, 1)", "offset() takes a struct type and one of its fields")]
    [InlineData("const fn f() -> int { return offset(Enemy.nope) }\nconst K = f()", "Struct 'Enemy' has no field 'nope'")]
    [InlineData("fn offset() { }", "'offset' is a built-in function; choose another name")]
    public void Errors(string source, string expected)
    {
        var e = Error(Structs + "const N = 3\n" + source + "\nfn main() { }");
        Assert.StartsWith(expected, e.Description);
    }

    [Fact]
    public void Offset_in_runtime_code_is_a_constant()
    {
        var r = Run.Program(Structs + """
            var x: u8
            fn main() {
                x = offset(Enemy.hp) + 1
            }
            """);
        Assert.Equal(6, r.U8("x"));
    }
}
