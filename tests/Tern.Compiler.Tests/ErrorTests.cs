using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

public class ErrorTests
{
    [Theory]
    [InlineData("var x: u8 = 300\nfn main() { }", "300 does not fit in u8")]
    [InlineData("var x: u8 = -1\nfn main() { }", "-1 does not fit in u8")]
    [InlineData("var x: u8\nvar y: u8\nfn main() { x = x * y }", "'*' only works on constants")]
    [InlineData("var x: u8\nfn main() { x = x << x }", "shift count must be a constant")]
    [InlineData("fn a() { b() }\nfn b() { a() }\nfn main() { a() }", "Recursion is not allowed: a -> b -> a")]
    [InlineData("fn f() -> u8 { if true { return 1 } }\nfn main() { }", "can reach its end without returning")]
    [InlineData("var w: u16\nvar b: u8\nfn main() { b = w }", "Expected u8, found u16")]
    [InlineData("var s: i8\nvar u: u8\nvar r: u8\nfn main() { r = s + u }", "Cannot mix signed and unsigned")]
    [InlineData("fn g() { }\nfn main() { for i in 0..10 using X { g() } }", "calls a function")]
    [InlineData("fn main() { for i in 0..10 { i = 3 } }", "cannot be changed inside the loop")]
    [InlineData("var x: u8\nfn main() { if x { } }", "condition must be bool")]
    [InlineData("var a: [u8; 4]\nfn main() { a[4] = 1 }", "past the end")]
    [InlineData("var a: [u8; 10]\nvar v: u8\nfn main() { for i in 0..20 { v = a[i] } }", "The loop reaches 19, past the end of 'a' (length 10)")]
    [InlineData("var a: [u8; 10]\nfn main() { for i in 0..=10 { a[i] = 1 } }", "The loop reaches 10, past the end of 'a' (length 10)")]
    [InlineData("var a: [u8; 10]\nfn main() { for i in 15..=0 step -1 { a[i] = 1 } }", "The loop reaches 15, past the end of 'a' (length 10)")]
    [InlineData("struct E { x: u8, hp: u8 }\nvar e: [E; 4]\nfn main() { for i in 0..8 { e[i].hp = 1 } }", "The loop reaches 7, past the end of 'e' (length 4)")]
    [InlineData("struct E { x: u8, hp: u8 }\nvar e: [E; 4]\nvar v: u8\nfn main() { for i in 0..=4 { v = e[i].x } }", "The loop reaches 4, past the end of 'e' (length 4)")]
    [InlineData("struct S { name: [u8; 4] }\nvar p: ptr S\nfn main() { for i in 0..6 { p.name[i] = 1 } }", "The loop reaches 5, past the end of 'p.name' (length 4)")]
    [InlineData("fn main() { var x: u8 }", "needs an initial value")]
    [InlineData("fn main() { x = 1 }", "'x' is not declared")]
    [InlineData("fn other() { }", "no 'fn main()'")]
    [InlineData("fn main() { var b: bool = 1 as bool }", "cannot be converted to bool")]
    // Pointers
    [InlineData("var p: ptr\nfn main() { }", "'ptr' needs the type it points to")]
    [InlineData("var p: ptr u8\nvar q: ptr u8\nvar r: ptr u8\nfn main() { r = p + q }", "Two pointers cannot be added")]
    [InlineData("var p: ptr u8\nvar q: ptr u16\nvar w: u16\nfn main() { w = p - q }", "Cannot subtract ptr u16 from ptr u8")]
    [InlineData("var p: ptr u8\nvar q: ptr u8\nfn main() { q = p - q }", "Expected ptr u8, found u16")]
    [InlineData("var p: ptr u8\nfn main() { p = p & 7 }", "'&' does not work on pointers")]
    [InlineData("var p: ptr u8\nfn main() { p <<= 1 }", "only '+=' and '-=' do")]
    [InlineData("var p: ptr u8\nvar s: i8\nfn main() { p = p + s }", "moves by a u8 or u16, not i8")]
    [InlineData("var p: ptr u8\nfn main() { p = p + -1 }", "use '-' to move back")]
    [InlineData("var p: ptr u8\nvar q: ptr u8\nfn main() { q = 1 - p }", "cannot be subtracted from a number")]
    [InlineData("var p: ptr u8\nvar w: u16\nfn main() { p = w }", "Expected ptr u8, found u16. Convert with 'as ptr u8'")]
    // Review code-quality-semantics 12: every type mismatch says how to fix it, with a fix that compiles.
    [InlineData("var b: bool = 1\nfn main() { }", "Expected bool, found a number. Write true or false, or compare, as in 'x != 0'")]
    [InlineData("var b: bool\nvar x: u8\nfn main() { b = x }", "Expected bool, found u8. Compare instead, as in 'x != 0'")]
    [InlineData("var b: bool\nvar x: u8\nfn main() { x = b }", "Expected u8, found bool. Use 'if' to choose a number, as in 'if b { x = 1 }'")]
    [InlineData("var p: ptr u8\nvar x: u8\nfn main() { p = x }", "Expected ptr u8, found u8. Convert with 'as u16' first, then 'as ptr u8'")]
    [InlineData("var p: ptr u8\nvar x: u8\nfn main() { x = p }", "Expected u8, found ptr u8. Convert with 'as u16' first, then take .lo or .hi")]
    [InlineData("var p: ptr u8\nvar s: i16\nfn main() { s = p }", "Expected i16, found ptr u8. Convert with 'as u16' first, then 'as i16'")]
    [InlineData("fn main() { var a: [u8; 2] = [1, 2] }", "An initial value for a local array is not supported yet. Assign the elements, or use a global or data")]
    [InlineData("var p: ptr u8\nvar w: u16\nfn main() { w = w + p }", "A number before a pointer is not pointer arithmetic. Write 'p + w' to move the pointer, or convert the pointer with 'as u16' for a number")]
    [InlineData("var p: ptr u8\nvar q: ptr u16\nfn main() { q = p }", "Convert with 'as ptr u16'")]
    [InlineData("var p: ptr u8\nvar b: u8\nfn main() { b = p as u8 }", "a pointer converts only to u16 or another pointer")]
    [InlineData("var p: ptr u8\nvar b: u8\nfn main() { p = b as ptr u8 }", "only u16 and pointers convert to a pointer")]
    [InlineData("var p: ptr u8\nvar q: ptr u16\nfn main() { if p == q { } }", "Cannot compare ptr u8 with ptr u16")]
    [InlineData("var p: ptr u8\nvar w: u16\nfn main() { if p == w { } }", "Cannot compare ptr u8 with u16")]
    [InlineData("var p: ptr u8\nvar b: u8\nfn main() { b = p.lo }", "(p as u16).lo")]
    [InlineData("var p: ptr u16\nvar w: u16\nfn main() { w = p[128] }", "Index 128 is too large")]
    [InlineData("var b: u8\nvar c: u8\nfn main() { c = b[0] }", "Only arrays and pointers can be indexed")]
    [InlineData("var p: ptr u8\nfn main() { for i in 0..4 using Y { p[0] = i } }", "needs the Y register")]
    [InlineData("const C = 5\nvar p: ptr u8\nfn main() { p = addr(C) }", "is a constant, which has no address")]
    [InlineData("var p: ptr u8\nfn main() { p = addr(nothing) }", "'nothing' is not declared")]
    [InlineData("var p: ptr u8\nvar a: [u8; 2]\nfn main() { p = addr(a[1]) }", "addr() takes the name")]
    [InlineData("var p: ptr u8\nfn main() { for i in 0..4 using X { p = addr(i) } }", "lives in a register")]
    [InlineData("var p: ptr [u8; 4]\nfn main() { }", "A pointer must point to a number, bool, pointer or struct")]
    [InlineData("fn addr(x: u8) -> u8 { return x }\nfn main() { }", "'addr' is a built-in function; choose another name")]
    [InlineData("fn len() { }\nfn main() { }", "'len' is a built-in function")]
    public void Reports_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description, StringComparison.OrdinalIgnoreCase);
    }

    // Habits from C get the Tern spelling (spec 13), at the habit's own position.
    [Theory]
    [InlineData("var n: u8\nfn main() {\n    n++\n}", 3, 6, "Tern has no '++'. Write 'n += 1'")]
    [InlineData("var n: u8\nfn main() {\n    n--\n}", 3, 6, "Tern has no '--'. Write 'n -= 1'")]
    [InlineData("var n: u8\nfn main() { n++ }", 2, 14, "Write 'n += 1'")]
    [InlineData("var n: u8\nfn main() { n++; n = 2 }", 2, 14, "Write 'n += 1'")]
    [InlineData("var n: u8\nfn main() {\n    n++ // count\n}", 3, 6, "Write 'n += 1'")]
    [InlineData("var a: [u8; 4]\nvar i: u8\nfn main() { a[i]++ }", 3, 17, "Write 'a[i] += 1'")]
    [InlineData("struct P { x: u8 }\nvar e: [P; 4]\nvar i: u8\nfn main() { e[i + 1].x-- }", 4, 23, "Write 'e[i + 1].x -= 1'")]
    [InlineData("var n: u8\nfn main() { (n)++ }", 2, 16, "Tern has no '++'. Write '+= 1' instead")]
    [InlineData("fn f(x: u8) { }\nvar n: u8\nfn main() { f(n--) }", 3, 16, "Write 'n -= 1'")]
    [InlineData("var p: ptr u8\nfn main() {\n    *p = 1\n}", 3, 5, "Tern has no '*' to read through a pointer. Write 'p[0]' for the value p points to")]
    [InlineData("var p: ptr u8\nvar v: u8\nfn main() { v = *p }", 3, 17, "Write 'p[0]'")]
    [InlineData("var v: u8\nfn main() { v = %0000_1111 }", 2, 17, "'%' does not start a binary number. Write binary numbers as 0b0000_1111")]
    [InlineData("const M = %101\nfn main() { }", 1, 11, "Write binary numbers as 0b101")]
    [InlineData("const M = %2\nfn main() { }", 1, 11, "Expected an expression, found '%'")]
    [InlineData("const M = %0xFF\nfn main() { }", 1, 11, "Expected an expression, found '%'")]
    [InlineData("fn match() { }\nfn main() { }", 1, 4, "Expected function name, found 'match', which is a keyword. Use another name")]
    [InlineData("var data: u8\nfn main() { }", 1, 5, "'data', which is a keyword")]
    [InlineData("fn f(in: u8) { }\nfn main() { }", 1, 6, "'in', which is a keyword")]
    [InlineData("fn main() { for using in 0..3 { } }", 1, 17, "'using', which is a keyword")]
    [InlineData("export fn get() { }", 1, 8, "'export' lists functions declared elsewhere, so it is not followed by 'fn'. Remove 'fn' here")]
    [InlineData("export fn update() { }", 1, 8, "declare the function on its own: 'export update' and 'fn update() { }'")]
    public void Names_habits_from_c(string source, int line, int column, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
        Assert.Equal((line, column), (e.Pos.Line, e.Pos.Column));
    }

    // Review code-quality-syntax 12, 16 and 19: the whole message, at the character that causes it.
    [Theory]
    [InlineData("var café: u8\nfn main() { }", 1, 8, "'é' cannot be in a name: names use A-Z, a-z, 0-9 and '_'. Write the name with these")]
    [InlineData("var αβ: u8\nfn main() { }", 1, 5, "'α' cannot be in a name: names use A-Z, a-z, 0-9 and '_'. Write the name with these")]
    [InlineData("var x١: u8\nfn main() { }", 1, 6, "'١' cannot be in a name: names use A-Z, a-z, 0-9 and '_'. Write the name with these")]
    [InlineData("var v: u8\nfn main() { v = ١ }", 2, 17, "'١' cannot be in a name: names use A-Z, a-z, 0-9 and '_'. Write the name with these")]
    [InlineData("var v: u8\nfn main() { v = 0x1F }", 2, 17, "Tern has no '0x'. Write hex numbers with '$', as in $1F")]
    [InlineData("data t = \"AC\"\nfn main() { }", 1, 12, "The character U+E042 has no code in any encoding. Write the byte it stands for as \\$NN")]
    public void Reports_lexer_errors_at_the_character(string source, int line, int column, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Equal(expected, e.Description);
        Assert.Equal((line, column), (e.Pos.Line, e.Pos.Column));
    }

    // Review code-quality-syntax 4: deep nesting is an error, not a stack overflow that kills the process.
    [Theory]
    [InlineData("(", ")", 300)]
    [InlineData("(", ")", 100_000)]
    [InlineData("-", "", 1000)]
    [InlineData("f(", ")", 300)]
    [InlineData("t[", "]", 300)]
    public void Deep_nesting_is_an_error(string open, string close, int count)
    {
        string source = $"var t: [u8; 4]\nfn f(x: u8) -> u8 {{ return x }}\nvar out: u8\nfn main() {{ out = {Repeat(open, count)}1{Repeat(close, count)} }}";
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Equal($"This is nested more than {Syntax.Parser.MaxDepth} deep (brackets, operators in a row, blocks or types). Split it with a constant, a variable or a function", e.Description);
    }

    [Theory]
    [InlineData("+1", 1000)]
    [InlineData("|1", 100_000)]
    [InlineData(" as u8", 1000)]
    [InlineData(".lo", 1000)]
    public void A_long_row_of_operators_is_an_error(string term, int count)
    {
        string source = $"var out: u8\nfn main() {{ out = 1{Repeat(term, count)} }}";
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains($"nested more than {Syntax.Parser.MaxDepth} deep", e.Description);
    }

    [Fact]
    public void Deeply_nested_blocks_and_types_are_an_error()
    {
        var blocks = Assert.Throws<CompileError>(() => TernCompiler.Compile($"fn main() {{ {Repeat("if true { ", 300)}{Repeat("}", 300)} }}"));
        Assert.Contains("nested more than", blocks.Description);
        var types = Assert.Throws<CompileError>(() => TernCompiler.Compile($"var t: {Repeat("[", 300)}u8{Repeat("; 1]", 300)}\nfn main() {{ }}"));
        Assert.Contains("nested more than", types.Description);
    }

    [Fact]
    public void Nesting_up_to_the_limit_compiles_and_runs()
    {
        var r = Run.Program($$"""
            var a: u8
            var b: u8
            var c: u8
            fn main() {
                a = {{Repeat("(", 250)}}7{{Repeat(")", 250)}}
                b = 1{{Repeat("+1", 250)}}
                c = {{Repeat("- ", 250)}}3
            }
            """);
        Assert.Equal((7, 251, 3), (r.U8("a"), r.U8("b"), r.U8("c")));
    }

    private static string Repeat(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    // Review code-quality-syntax 15: a line break inside a block comment ends the statement.
    [Fact]
    public void A_line_break_inside_a_block_comment_ends_the_statement()
    {
        var r = Run.Program("""
            var a: u8
            var b: u8
            fn main() {
                a = 1 /* note
                */ b = 2
                a += 2 /* on one line */ + 3
            }
            """);
        Assert.Equal((6, 2), (r.U8("a"), r.U8("b")));
    }

    [Fact]
    public void A_keyword_where_no_name_was_meant_gets_no_rename_hint()
    {
        // The name is missing, not a keyword in its place (review M20, second review, suggestion 4).
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("fn main() { for in 0..3 { } }"));
        Assert.Equal("Expected loop variable name or '_', found 'in'", e.Description);
        e = Assert.Throws<CompileError>(() => TernCompiler.Compile("fn main() { }\nexport return"));
        Assert.Equal("Expected a function name after 'export', found 'return'", e.Description);
    }

    [Fact]
    public void The_fix_for_a_c_habit_is_the_suggestion()
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("var n: u8\nfn main() { n++ }"));
        Assert.Equal(("Tern has no '++'.", "Write 'n += 1'"), Tools.Diagnostic.Split(e.Description));
        e = Assert.Throws<CompileError>(() => TernCompiler.Compile("fn match() { }\nfn main() { }"));
        Assert.Equal("Use another name", Tools.Diagnostic.Split(e.Description).Suggestion);
    }

    [Fact]
    public void Plus_plus_and_minus_minus_between_operands_are_still_two_operators()
    {
        var run = Run.Program("""
            var a: u8 = 10
            var b: u8 = 3
            var r1: u8
            var r2: u8
            var r3: u8
            var r4: u8
            fn main() {
                r1 = a++b
                r2 = a--b
                r3 = a+ +b
                r4 = a - -b
            }
            """);
        Assert.Equal((13, 13, 13, 13), (run.U8("r1"), run.U8("r2"), run.U8("r3"), run.U8("r4")));
    }

    [Fact]
    public void Zero_page_overflow_is_an_error()
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("""
            var big: [u8; 200] @ zp
            fn main() { }
            """));
        Assert.Contains("Zero page is full", e.Description);
    }

    [Fact]
    public void Dereferenced_pointer_that_does_not_fit_in_zero_page_is_an_error()
    {
        // $02-$8F is 142 bytes; the array leaves one byte, too few for a pointer.
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("""
            var big: [u8; 141] @ zp
            var p: ptr u8
            fn main() { p[0] = 1 }
            """));
        Assert.Contains("Zero page is full: '@ zp' variables, dereferenced pointers and zero-page assembly operands need 143 bytes, 142 are free", e.Description);
        Assert.Contains("big (141), p (2)", e.Description);
    }

    [Fact]
    public void Pointers_that_are_not_dereferenced_are_not_forced_into_zero_page()
    {
        var r = TernCompiler.Compile("""
            var big: [u8; 141] @ zp
            var p: ptr u8
            fn main() { p = $1234 }
            """);
        Assert.True(r.Symbols["p"] >= 0x100);
    }

    [Theory]
    [InlineData("[u8; 10]", "for i in 0..10 { a[i] = 1 }")]
    [InlineData("[u8; 10]", "for i in 9..=0 step -1 { a[i] = 1 }")]
    [InlineData("[u8; 10]", "for i in 0..0 { a[i] = 1 }")]               // no passes
    [InlineData("[u8; 19]", "for i in 0..=20 step 3 { a[i] = 1 }")]       // reaches 18, not 20
    [InlineData("[u8; 10]", "for i in 0..=18 step 2 { a[i >> 1] = 1 }")]   // not the counter itself: unchecked
    public void A_loop_that_stays_inside_the_array_is_accepted(string type, string loop) =>
        TernCompiler.Compile($"var a: {type}\nfn main() {{ {loop} }}");

    // ---------- Line breaks in expressions (spec 2) ----------

    [Theory]
    [InlineData("var a: u8\nvar b: u8\nfn main() {\n    if a == 1\n        && b == 2 { a = 3 }\n}", 5, 9, "&&")]
    [InlineData("var a: u8\nvar b: u8\nfn main() {\n    while a < 3\n        || b > 4 { a += 1 }\n}", 5, 9, "||")]
    [InlineData("var a: u8\nvar b: u8\nfn main() {\n    a = b\n        | 4\n}", 5, 9, "|")]
    [InlineData("var a: u8\nvar b: u8\nfn main() {\n    a = b\n\n        << 1\n}", 6, 9, "<<")]
    [InlineData("var a: bool\nvar b: u8\nfn main() {\n    a = b\n        == 4\n}", 5, 9, "==")]
    [InlineData("const K = 10\n    / 2\nfn main() { }", 2, 5, "/")]
    public void A_line_starting_with_a_binary_operator_says_to_move_it(string source, int line, int column, string op)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Equal($"A line cannot start with '{op}': the line break before it ends the expression. Move '{op}' to the end of the line before, where the expression then continues", e.Description);
        Assert.Equal((line, column), (e.Pos.Line, e.Pos.Column));
        Assert.Equal($"Move '{op}' to the end of the line before, where the expression then continues", Tools.Diagnostic.Split(e.Description).Suggestion);
    }

    [Fact]
    public void A_line_ending_with_a_binary_operator_continues()
    {
        var r = Run.Program("""
            var a: u8 = 1
            var b: u8 = 2
            var c: u8
            var d: u8
            fn main() {
                if a == 1 &&
                        b == 2 {
                    c = a +
                        b |
                        8
                }
                d = (a
                    + b)
            }
            """);
        Assert.Equal(11, r.U8("c"));
        Assert.Equal(3, r.U8("d"));
    }

    [Fact]
    public void A_minus_at_the_start_of_a_line_is_still_a_value()
    {
        var r = Run.Program("""
            var s: i8 = -1
            var r: u8
            fn main() {
                match s {
                    1 => r = 1
                    -1 => r = 2
                    _ => r = 3
                }
            }
            """);
        Assert.Equal(2, r.U8("r"));
        // A line starting with '-' is a new statement, not a continuation.
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("var s: i8\nvar t: i8\nfn main() {\n    t = s\n    -2\n}"));
        Assert.StartsWith("Only a function call can be used as a statement", e.Description);
        Assert.Equal(5, e.Pos.Line);
    }
}
