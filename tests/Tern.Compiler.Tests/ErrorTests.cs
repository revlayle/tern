using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

public class ErrorTests
{
    [Theory]
    [InlineData("var x: u8 = 300\nfn main() { }", 1, 13, "300 does not fit in u8 (0 to 255). Use a value in that range, or a wider type")]
    [InlineData("var x: u8 = -1\nfn main() { }", 1, 13, "-1 does not fit in u8 (0 to 255). Use a value in that range, or a wider type")]
    [InlineData("var x: u8\nvar y: u8\nfn main() { x = x * y }", 3, 19, "'*' only works on constants. Use a library function at run time (mul8, div8, ...), or shifts for powers of two")]
    [InlineData("var x: u8\nfn main() { x = x << x }", 2, 22, "The shift count must be a constant. Use shl8/shr8 for a variable count")]
    [InlineData("fn a() { b() }\nfn b() { a() }\nfn main() { a() }", 1, 1, "Recursion is not allowed: a -> b -> a. Use a loop instead, with an array as the stack if needed")]
    [InlineData("fn f() -> u8 { if true { return 1 } }\nfn main() { }", 1, 1, "'f' can reach its end without returning a value. Add a 'return' at the end")]
    [InlineData("var w: u16\nvar b: u8\nfn main() { b = w }", 3, 17, "Expected u8, found u16. Convert with 'as u8'")]
    [InlineData("var s: i8\nvar u: u8\nvar r: u8\nfn main() { r = s + u }", 4, 19, "Cannot mix signed and unsigned (i8 and u8); convert one side with 'as'")]
    [InlineData("fn g() { }\nfn main() { for i in 0..10 using X { g() } }", 2, 13, "The loop body calls a function, which can change X. Remove 'using X', or call only routines that keep X")]
    [InlineData("fn main() { for i in 0..10 { i = 3 } }", 1, 30, "Loop variable 'i' cannot be changed inside the loop. Use a 'while' loop to change the counter yourself")]
    [InlineData("var x: u8\nfn main() { if x { } }", 2, 16, "A condition must be bool, not u8. Compare it, e.g. 'x != 0'")]
    [InlineData("var a: [u8; 4]\nfn main() { a[4] = 1 }", 2, 15, "Index 4 is past the end of 'a' (length 4). Use an index below 4")]
    [InlineData("var a: [u8; 10]\nvar v: u8\nfn main() { for i in 0..20 { v = a[i] } }", 3, 36, "The loop reaches 19, past the end of 'a' (length 10). Make the loop stop before 10")]
    [InlineData("var a: [u8; 10]\nfn main() { for i in 0..=10 { a[i] = 1 } }", 2, 33, "The loop reaches 10, past the end of 'a' (length 10). Make the loop stop before 10")]
    [InlineData("var a: [u8; 10]\nfn main() { for i in 15..=0 step -1 { a[i] = 1 } }", 2, 41, "The loop reaches 15, past the end of 'a' (length 10). Make the loop stop before 10")]
    [InlineData("struct E { x: u8, hp: u8 }\nvar e: [E; 4]\nfn main() { for i in 0..8 { e[i].hp = 1 } }", 3, 31, "The loop reaches 7, past the end of 'e' (length 4). Make the loop stop before 4")]
    [InlineData("struct E { x: u8, hp: u8 }\nvar e: [E; 4]\nvar v: u8\nfn main() { for i in 0..=4 { v = e[i].x } }", 4, 36, "The loop reaches 4, past the end of 'e' (length 4). Make the loop stop before 4")]
    [InlineData("struct S { name: [u8; 4] }\nvar p: ptr S\nfn main() { for i in 0..6 { p.name[i] = 1 } }", 3, 36, "The loop reaches 5, past the end of 'p.name' (length 4). Make the loop stop before 4")]
    [InlineData("fn main() { var x: u8 }", 1, 13, "Local variable 'x' needs an initial value. Write one: var x: u8 = 0")]
    [InlineData("fn main() { x = 1 }", 1, 13, "'x' is not declared. Check the spelling, or declare it")]
    [InlineData("fn other() { }", 1, 1, "The program has no 'fn main()'. Add 'fn main() { }', or an 'export' line for a binary that another program calls")]
    [InlineData("fn main() { var b: bool = 1 as bool }", 1, 29, "Numbers cannot be converted to bool; compare instead, e.g. 'x != 0'")]
    [InlineData("var b: u8\nvar c: u8\nfn main() { c = b[0] }", 3, 18, "Only arrays and pointers can be indexed. Use .lo or .hi for a byte of a 16-bit value")]
    [InlineData("fn addr(x: u8) -> u8 { return x }\nfn main() { }", 1, 1, "'addr' is a built-in function; choose another name")]
    [InlineData("fn len() { }\nfn main() { }", 1, 1, "'len' is a built-in function; choose another name")]
    public void Reports_errors(string source, int line, int column, string expected)
    {
        Expect.Error(source, line, column, expected);
    }

    // Habits from C get the Tern spelling (spec 13), at the habit's own position.
    [Theory]
    [InlineData("var n: u8\nfn main() {\n    n++\n}", 3, 6, "Tern has no '++'. Write 'n += 1'")]
    [InlineData("var n: u8\nfn main() {\n    n--\n}", 3, 6, "Tern has no '--'. Write 'n -= 1'")]
    [InlineData("var n: u8\nfn main() { n++ }", 2, 14, "Tern has no '++'. Write 'n += 1'")]
    [InlineData("var n: u8\nfn main() { n++; n = 2 }", 2, 14, "Tern has no '++'. Write 'n += 1'")]
    [InlineData("var n: u8\nfn main() {\n    n++ // count\n}", 3, 6, "Tern has no '++'. Write 'n += 1'")]
    [InlineData("var a: [u8; 4]\nvar i: u8\nfn main() { a[i]++ }", 3, 17, "Tern has no '++'. Write 'a[i] += 1'")]
    [InlineData("struct P { x: u8 }\nvar e: [P; 4]\nvar i: u8\nfn main() { e[i + 1].x-- }", 4, 23, "Tern has no '--'. Write 'e[i + 1].x -= 1'")]
    [InlineData("var n: u8\nfn main() { (n)++ }", 2, 16, "Tern has no '++'. Write '+= 1' instead")]
    [InlineData("fn f(x: u8) { }\nvar n: u8\nfn main() { f(n--) }", 3, 16, "Tern has no '--'. Write 'n -= 1'")]
    [InlineData("var p: ptr u8\nfn main() {\n    *p = 1\n}", 3, 5, "Tern has no '*' to read through a pointer. Write 'p[0]' for the value p points to")]
    [InlineData("var p: ptr u8\nvar v: u8\nfn main() { v = *p }", 3, 17, "Tern has no '*' to read through a pointer. Write 'p[0]' for the value p points to")]
    [InlineData("var v: u8\nfn main() { v = %0000_1111 }", 2, 17, "'%' does not start a binary number. Write binary numbers as 0b0000_1111")]
    [InlineData("const M = %101\nfn main() { }", 1, 11, "'%' does not start a binary number. Write binary numbers as 0b101")]
    [InlineData("const M = %2\nfn main() { }", 1, 11, "Expected an expression, found '%'")]
    [InlineData("const M = %0xFF\nfn main() { }", 1, 11, "Expected an expression, found '%'")]
    [InlineData("fn match() { }\nfn main() { }", 1, 4, "Expected function name, found 'match', which is a keyword. Use another name")]
    [InlineData("var data: u8\nfn main() { }", 1, 5, "Expected variable name, found 'data', which is a keyword. Use another name")]
    [InlineData("fn f(in: u8) { }\nfn main() { }", 1, 6, "Expected parameter name, found 'in', which is a keyword. Use another name")]
    [InlineData("fn main() { for using in 0..3 { } }", 1, 17, "Expected loop variable name or '_', found 'using', which is a keyword. Use another name")]
    [InlineData("export fn get() { }", 1, 8, "'export' lists functions declared elsewhere, so it is not followed by 'fn'. Remove 'fn' here and declare the function on its own: 'export get' and 'fn get() { }'")]
    [InlineData("export fn update() { }", 1, 8, "'export' lists functions declared elsewhere, so it is not followed by 'fn'. Remove 'fn' here and declare the function on its own: 'export update' and 'fn update() { }'")]
    public void Names_habits_from_c(string source, int line, int column, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Expect.Message(e, line, column, expected);
        Assert.Equal((line, column), (e.Pos.Line, e.Pos.Column));
    }

    // Review code-quality-syntax 12, 13, 16 and 19 (with R6: any other 'name {' statement; semantics 12, a whole array as a value): the whole message, at its place.
    [Theory]
    [InlineData("var café: u8\nfn main() { }", 1, 8, "'é' (U+00E9) cannot be in a name: names use A-Z, a-z, 0-9 and '_'. Write the name with these")]
    [InlineData("var αβ: u8\nfn main() { }", 1, 5, "'α' (U+03B1) cannot be in a name: names use A-Z, a-z, 0-9 and '_'. Write the name with these")]
    [InlineData("var x١: u8\nfn main() { }", 1, 6, "'١' (U+0661) cannot be in a name: names use A-Z, a-z, 0-9 and '_'. Write the name with these")]
    [InlineData("var v: u8\nfn main() { v = ١ }", 2, 17, "'١' (U+0661) cannot be in a name: names use A-Z, a-z, 0-9 and '_'. Write the name with these")]
    [InlineData("var v: u8\nfn main() { v = 0x1F }", 2, 17, "Tern has no '0x'. Write hex numbers with '$', as in $1F")]
    [InlineData("fn main() {\n    emit { lda #1 }\n}", 2, 5, "'emit' adds assembly to the caller only in a const fn, a code generator. Write the assembly in an 'asm { }' block here, or move it into a const fn")]
    [InlineData("fn main() {\n    foo { lda #1 }\n}", 2, 5, "'foo {' starts a struct literal, which is a value, not a statement. Write 'asm { ... }' for assembly, or use the literal as a variable's initial value")]
    [InlineData("struct P { x: u8 }\nfn main() {\n    P { x: 1 }\n}", 3, 5, "'P {' starts a struct literal, which is a value, not a statement. Write 'asm { ... }' for assembly, or use the literal as a variable's initial value")]
    [InlineData("var q: [u8; 3]\nfn main() {\n    var x: u8 = q\n}", 3, 17, "Expected u8, found [u8; 3]: 'q' is a whole array, which is never a value. Use one element, as in q[0], or addr(q) for its address")]
    [InlineData("data t = \"AC\"\nfn main() { }", 1, 12,"The character U+E042 has no code in any encoding. Write the byte it stands for as \\$NN")]
    [InlineData("const fn dec(n: int) {\n    emit { ldx #n }\n}\nfn main() { }", 1, 7, "'dec' is a 6502 instruction, so an asm line cannot call a generator of that name. Use another name")]
    [InlineData("const fn Inc() {\n    emit { nop }\n}\nfn main() { }", 1, 7, "'Inc' is a 6502 instruction, so an asm line cannot call a generator of that name. Use another name")]
    public void Reports_the_whole_message_at_its_place(string source, int line, int column, string expected)
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
        Expect.Error(() => TernCompiler.Compile($"fn main() {{ {Repeat("if true { ", 300)}{Repeat("}", 300)} }}"), 1, 2566, "This is nested more than 256 deep (brackets, operators in a row, blocks or types). Split it with a constant, a variable or a function");
        Expect.Error(() => TernCompiler.Compile($"var t: {Repeat("[", 300)}u8{Repeat("; 1]", 300)}\nfn main() {{ }}"), 1, 264, "This is nested more than 256 deep (brackets, operators in a row, blocks or types). Split it with a constant, a variable or a function");
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
        Expect.Message(e, 1, 1, "Zero page is full: '@ zp' variables need 200 bytes, 146 are free. Users: big (200). Remove '@ zp' from some of them");
    }

    [Fact]
    public void Dereferenced_pointer_that_does_not_fit_in_zero_page_is_an_error()
    {
        // $02-$8F and $FB-$FE are 146 bytes; the array fills the first, two pointers the second.
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile("""
            var big: [u8; 142] @ zp
            var p: ptr u8
            var q: ptr u8
            var r: ptr u8
            fn main() { p[0] = 1; q[0] = 1; r[0] = 1 }
            """));
        Expect.Message(e, 2, 1, "Zero page is full: '@ zp' variables, dereferenced pointers and zero-page assembly operands need 148 bytes, 146 are free. Users: big (142), p (2), q (2), r (2). Remove '@ zp' from some of them");
    }

    // Review code-quality-syntax 3: the C64's free zero page is $02-$8F and $FB-$FE (spec 12.2).
    [Fact]
    public void The_c64_uses_fb_to_fe_after_02_to_8f()
    {
        var r = Run.Program("""
            var big: [u8; 142] @ zp
            var p: ptr u8
            var q: ptr u8
            var out: u8
            data t: [u8; 2] = [7, 9]
            fn main() {
                p = addr(t)
                q = addr(t) + 1
                out = p[0] + q[0]
            }
            """);
        Assert.Equal(16, r.U8("out"));
        Assert.Equal([0xFB, 0xFD], new[] { r.Address("p"), r.Address("q") }.Order());
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
        Expect.Message(e, 5, 5, "Only a function call can be used as a statement. Assign the value to a variable, or remove it");
        Assert.Equal(5, e.Pos.Line);
    }

    // Errors the coverage run of R6 found no test for (review code-quality-tests, finding 4).
    [Theory]
    [InlineData("fn f() -> u8 { return 1 }\nvar a: [u8; 4]\nfn main() { a[f()] += 1 }", 3, 20, "Compound assignment to an element whose index calls a function is not supported. Store the index in a variable first")]
    [InlineData("var a: [u8; 2]\nvar b: [u8; 2]\nfn main() { a = b }", 3, 13, "Whole arrays cannot be assigned. Assign the elements in a loop, or copy the memory with mem.copy")]
    [InlineData("var b: bool\nfn main() { b += 1 }", 2, 15, "'+=' needs a number on the left. Use a number variable, or '=' with the whole value")]
    [InlineData("var b: bool\nfn main() { b |= true }", 2, 15, "'|=' needs a number on the left. Use a number variable, or '=' with the whole value")]
    [InlineData("var b: bool\nfn main() { for i in 0..b { } }", 2, 25, "Loop bounds must be numbers. Use integer values, or convert with 'as'")]
    [InlineData("fn main() { for i in 0..10 step 0 { } }", 1, 33, "'step' must be a non-zero constant, or a u8 variable in a loop that counts up with a u8 counter. Use one of those")]
    [InlineData("fn main() { for i in 0..10 step -1 { } }", 1, 33, "A step of -1 goes the wrong way from 0 to 10. Change the sign of the step, or swap the bounds")]
    [InlineData("fn main() { for i in 10..0 step 2 { } }", 1, 33, "A step of 2 goes the wrong way from 10 to 0. Change the sign of the step, or swap the bounds")]
    [InlineData("fn main() { for i in 0..10 using A { } }", 1, 13, "A loop variable can only use X or Y. Write 'using X' or 'using Y'")]
    [InlineData("fn main() { for i in 0..10 using X { for j in 0..10 using X { } } }", 1, 38, "X is already used by an enclosing loop. Use the other index register, or leave out 'using'")]
    [InlineData("var a: [u8; 4]\nfn main() { for i in 0 { a[i] = 1 } }", 2, 24, "Expected '..' or '..=', found '{'. Write a range as first..last, or first..=last to include the last value")]
    [InlineData("fn f(a: u8, a: u8) { }\nfn main() { f(1, 2) }", 1, 13, "Parameter 'a' is declared twice. Rename one of them")]
    [InlineData("fn f(a: u8 using A, b: u8 using A) { }\nfn main() { f(1, 2) }", 1, 21, "Register A is used by more than one parameter. Use another register for one of them")]
    [InlineData("fn f(a: u16 using AX, b: u8 using X) { }\nfn main() { f(1, 2) }", 1, 23, "Register X is used by more than one parameter. Use another register for one of them")]
    [InlineData("var a: [u8; 2] = 5\nfn main() { }", 1, 18, "An array must be initialized with [ ... ]. Write the elements in brackets")]
    [InlineData("struct P { x: u8 }\nvar p: P = u8 { x = 1 }\nfn main() { }", 2, 12, "'u8' is not a struct, so 'u8 { ... }' is not a struct literal. Check the spelling of the struct name")]
    [InlineData("var n: u8\nvar a: [u8; n]\nfn main() { }", 2, 13, "Array length must be known at compile time. Use a number or a constant")]
    [InlineData("var x: u8\nfn main() { x = [1, 2] }", 2, 17, "An array literal can only be used to initialize a global array. Declare the list as data, and read it from there")]
    [InlineData("const fn g() -> int { return 1 }\nvar x: u8\nfn main() { x = g }", 3, 17, "'g' is a const fn; call it with ()")]
    [InlineData("fn f() { }\nvar x: u16\nfn main() { x = f }", 3, 17, "'f' is a function; call it with ()")]
    [InlineData("var a: [u8; 2]\nvar x: u16\nfn main() { x = a as u16 }", 3, 19, "Cannot convert [u8; 2] to u16. Convert one element or field of it instead")]
    [InlineData("struct P { x: u8 }\nvar p: P\nvar x: u8\nfn main() { x = p as u8 }", 4, 19, "Cannot convert P to u8. Convert one element or field of it instead")]
    [InlineData("var a: u8\nvar b: bool\nfn main() { b = a && true }", 3, 19, "'&&' needs bool values; use '&' for bits")]
    [InlineData("var a: bool\nvar b: bool\nvar c: bool\nfn main() { c = a < b }", 4, 19, "bool values can only be compared with == and !=. Use one of those")]
    [InlineData("var a: bool\nvar x: u8\nfn main() { x = a << 1 }", 3, 19, "Only numbers can be shifted. Convert the value to a number with 'as'")]
    [InlineData("var a: bool\nvar x: u8\nfn main() { x = ~a }", 3, 17, "'~' needs a number; use '!' for bool")]
    [InlineData("var x: u8\nfn main() { x = len(x) }", 2, 17, "len() needs an array. Use sizeof() for the size of a type")]
    [InlineData("var n: u8\nvar x: u8 @ n\nfn main() { }", 2, 13, "The address must be known at compile time. Use a number or a constant")]
    [InlineData("var x: u16 @ $FFFF\nfn main() { }", 1, 14, "Address $FFFF is outside memory. Use an address up to $FFFF, less the variable's size")]
    [InlineData("var x: u8 @ $10000\nfn main() { }", 1, 13, "Address $10000 is outside memory. Use an address up to $FFFF, less the variable's size")]
    [InlineData("fn main() { for i in 0..70000 { } }", 1, 25, "70000 does not fit in 16 bits. Use a value from -32768 to 65535")]
    [InlineData("var x: u8\nfn main() { match 70000 { _ => { } } }", 2, 19, "70000 does not fit in 16 bits. Use a value from -32768 to 65535")]
    [InlineData("fn f() { }\nvar x: u8\nfn main() { x = f() }", 3, 17, "This function call does not return a value. Call it as a statement on its own")]
    [InlineData("fn f() { }\nvar x: u8\nfn main() { x = f() + 1 }", 3, 17, "This function call does not return a value. Call it as a statement on its own")]
    [InlineData("fn f() { }\nfn main() { if 1 == f() { } }", 2, 21, "This function call does not return a value. Call it as a statement on its own")]
    [InlineData("var b: bool\nvar p: ptr u8\nfn main() { p = b }", 3, 17, "Expected ptr u8, found bool, which does not convert to ptr u8. Use a ptr u8 here")]
    [InlineData("fn main(x: u8) { }", 1, 1, "'main' must have no parameters and no return value. Remove them")]
    [InlineData("fn main() -> u8 { return 1 }", 1, 1, "'main' must have no parameters and no return value. Remove them")]
    [InlineData("var x: u8 = 0b\nfn main() { }", 1, 13, "Number has no digits. Write digits after '$' or '0b', e.g. $FF or 0b1010")]
    [InlineData("var x: u8 = 0b_\nfn main() { }", 1, 13, "Number has no digits. Write digits after '$' or '0b', e.g. $FF or 0b1010")]
    [InlineData("var x: u8 = 1 ` 2\nfn main() { }", 1, 15, "Unexpected character '`'. Remove it, or put it in a string or a comment")]
    [InlineData("fn main() {\n    module m\n}", 2, 5, "'module' must be the first line of the file, outside functions. Move it there")]
    [InlineData("var b: bool\nvar x: u8\nfn main() { x = b | x }", 3, 19, "Cannot combine bool and u8. Convert one side with 'as'")]
    public void Reports_errors_no_other_test_reached(string source, int line, int column, string expected)
    {
        Expect.Error(source, line, column, expected);
    }
}
