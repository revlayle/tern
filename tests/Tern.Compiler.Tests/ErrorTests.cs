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
    [InlineData("fn main() { var x: u8 }", "needs an initial value")]
    [InlineData("fn main() { x = 1 }", "'x' is not declared")]
    [InlineData("fn other() { }", "no 'fn main()'")]
    [InlineData("fn main() { match 1 { } }", "'match' is not supported yet")]
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
    [InlineData("var p: ptr u8\nvar w: u16\nfn main() { w = p }", "Expected u16, found ptr u8. Convert with 'as u16'")]
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
    [InlineData("var p: ptr u8\nfn main() { p = addr(main) }", "addr() of a function is not supported yet")]
    [InlineData("var p: ptr u8\nfn main() { p = addr(nothing) }", "'nothing' is not declared")]
    [InlineData("var p: ptr u8\nvar a: [u8; 2]\nfn main() { p = addr(a[1]) }", "addr() takes the name")]
    [InlineData("var p: ptr u8\nfn main() { for i in 0..4 using X { p = addr(i) } }", "lives in a register")]
    [InlineData("var p: ptr [u8; 4]\nfn main() { }", "A pointer must point to a number, bool, pointer or struct")]
    [InlineData("var b: [u8; 2]\nvar p: ptr u8 = addr(b)\nfn main() { }", "addr() cannot be used in an initial value")]
    [InlineData("var b: [u8; 2]\nvar w: u16 = (addr(b) + 1) as u16\nfn main() { }", "addr() cannot be used in an initial value")]
    [InlineData("fn addr(x: u8) -> u8 { return x }\nfn main() { }", "'addr' is a built-in function; choose another name")]
    [InlineData("fn len() { }\nfn main() { }", "'len' is a built-in function")]
    public void Reports_errors(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description, StringComparison.OrdinalIgnoreCase);
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
}
