using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// What a language server (M28) needs from the front end: where tokens and nodes end, doc comments,
/// declarations in source order (review code-quality-syntax 8).
/// Spec 13 (compiler design).
/// </summary>
public class LanguageServerTests
{
    private static string TextOf(string source, Node node) => source[node.Span.Start..node.Span.End];

    // ---------- Tokens ----------

    [Theory]
    [InlineData("name", "name")]
    [InlineData("$C000", "$C000")]
    [InlineData("\"a\\\"b\"", "\"a\\\"b\"")]          // the quotes and the escape, not the decoded text
    [InlineData("screen\"HI\"", "screen\"HI\"")]      // with its encoding prefix
    [InlineData("'\\n'", "'\\n'")]
    [InlineData("<<=", "<<=")]
    [InlineData("1.5e3", "1.5e3")]
    public void A_token_ends_after_its_last_character(string text, string expected)
    {
        var source = "  " + text + "  ";
        var token = Lexer.Tokenize(source)[0];
        Assert.Equal(expected, source[token.Start..token.End]);
        Assert.Equal(2, token.Start);
    }

    [Fact]
    public void Doc_comments_go_to_the_next_token()
    {
        var tokens = Lexer.Tokenize("/// First line.\n///Second.\n//// not a doc comment\n// nor this\nfn f() { }\nvar x: u8");
        Assert.Equal("First line.\nSecond.", tokens[0].Doc);
        Assert.All(tokens.Skip(1), t => Assert.Null(t.Doc));
    }

    [Fact]
    public void An_error_token_runs_from_the_error_to_the_end_of_its_line()
    {
        const string source = "var x: u8\nvar y = 0x10 + 1\nvar z: u8\n";
        var errors = new List<CompileError>();
        var error = Lexer.Tokenize(source, report: errors.Add).Single(t => t.Kind == TokenKind.Error);
        Assert.Equal("0x10 + 1", source[error.Start..error.End]);
        Assert.Single(errors);
    }

    // ---------- Spans ----------

    [Fact]
    public void Nodes_span_their_source_text()
    {
        const string source = "fn main() {\n    var x: u8 = a[i] + b.lo * 2   // comment\n    poke(x as u16, -x)\n}\n";
        var program = Parser.Parse(source);
        var main = program.Functions.Single();
        Assert.Equal(source.TrimEnd('\n'), TextOf(source, main));
        Assert.Equal("{\n    var x: u8 = a[i] + b.lo * 2   // comment\n    poke(x as u16, -x)\n}", TextOf(source, main.Body));

        var x = (VarDecl)main.Body.Statements[0];
        Assert.Equal("var x: u8 = a[i] + b.lo * 2", TextOf(source, x));
        Assert.Equal("u8", TextOf(source, x.TypeSyntax!));
        var sum = (BinaryExpr)x.Init!;
        Assert.Equal("a[i] + b.lo * 2", TextOf(source, sum));
        Assert.Equal("a[i]", TextOf(source, sum.Left));
        Assert.Equal("b.lo * 2", TextOf(source, sum.Right));
        Assert.Equal("b.lo", TextOf(source, ((BinaryExpr)sum.Right).Left));

        var call = (ExprStmt)main.Body.Statements[1];
        Assert.Equal("poke(x as u16, -x)", TextOf(source, call));
        var args = ((CallExpr)call.Expr).Args;
        Assert.Equal("x as u16", TextOf(source, args[0]));
        Assert.Equal("-x", TextOf(source, args[1]));
    }

    [Fact]
    public void A_declarations_span_starts_at_its_first_modifier()
    {
        const string source = "private noinit var buf: [u8; 4]\ninline fn f() { }\nfn main() { }\n";
        var program = Parser.Parse(source);
        Assert.Equal("private noinit var buf: [u8; 4]", TextOf(source, program.Globals[0]));
        Assert.Equal("[u8; 4]", TextOf(source, program.Globals[0].TypeSyntax!));
        Assert.Equal("inline fn f() { }", TextOf(source, program.Functions[0]));
    }

    // ---------- Declarations ----------

    [Fact]
    public void Top_level_declarations_keep_their_doc_comments()
    {
        const string source = """
            /// The width.
            const W = 40
            /// A point.
            struct P { x: u8 }
            /// The cursor.
            /// Two lines.
            private var cursor: u8
            /// Starts.
            fn main() { }
            fn other() { }
            """;
        var program = Parser.Parse(source);
        Assert.Equal("The width.", program.Consts[0].Doc);
        Assert.Equal("A point.", program.Structs[0].Doc);
        Assert.Equal("The cursor.\nTwo lines.", program.Globals[0].Doc);
        Assert.Equal("Starts.", program.Functions[0].Doc);
        Assert.Null(program.Functions[1].Doc);
    }

    [Fact]
    public void Declarations_are_listed_in_source_order()
    {
        const string source = """
            import math
            const A = 1
            memory {
                $C000..=$CFFF reserved
            }
            fn f() { }
            struct S { x: u8 }
            var v: u8
            export f
            fn main() { }
            """;
        var program = Parser.Parse(source);
        Assert.Equal(
            ["ImportDecl math", "ConstDecl A", "MemoryDecl", "FnDecl f", "StructDecl S", "VarDecl v", "ExportName f", "FnDecl main"],
            program.Declarations.Select(d => d switch
            {
                ImportDecl i => $"ImportDecl {i.Name}",
                ConstDecl c => $"ConstDecl {c.Name}",
                FnDecl fn => $"FnDecl {fn.Name}",
                StructDecl s => $"StructDecl {s.Name}",
                VarDecl vd => $"VarDecl {vd.Name}",
                ExportName e => $"ExportName {e.Name}",
                _ => d.GetType().Name,
            }));
    }
}
