using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// Every expression, statement, type and assembly line lists its children (<see cref="Node.Children"/>),
/// and the checker's walks go over <see cref="Node.Descendants"/> (review code-quality-semantics 14):
/// a child that one hand-written walk left out, such as a struct literal's field, is reached by all.
/// Spec 13 (compiler design).
/// </summary>
public class SyntaxTreeTests
{
    [Fact]
    public void Descendants_reach_every_child_in_source_order()
    {
        var program = Parser.Parse("""
            struct P { a: u8, b: u8 }
            var table: [u8; 4]
            fn main() {
                var p: P = P { a = n1, b = 2 }
                var list: [u8; 2] = [n2, 3]
                for i in n3..10 step n4 { table[i] = n5 }
                match n6 {
                    n7 => { }
                    _ => { }
                }
                asm { lda n8 }
                var c = n9 as u8
            }
            """);
        var body = program.Functions.Single(f => f.Name == "main").Body;
        var names = body.Descendants().OfType<NameExpr>().Select(n => n.Name).Where(n => n.StartsWith('n')).ToList();
        Assert.Equal(["n1", "n2", "n3", "n4", "n5", "n6", "n7", "n8", "n9"], names);
    }

    [Fact]
    public void A_walk_need_not_go_inside_a_node()
    {
        var program = Parser.Parse("""
            fn main() {
                x = 1
                asm { lda y }
            }
            """);
        var body = program.Functions.Single().Body;
        var names = body.Descendants(enter: n => n is not AsmStmt).OfType<NameExpr>().Select(n => n.Name);
        Assert.Equal(["x"], names);
    }
}
