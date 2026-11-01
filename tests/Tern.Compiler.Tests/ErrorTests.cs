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
    [InlineData("struct S { x: u8 }\nfn main() { }", "'struct' is not supported yet")]
    [InlineData("fn main() { var b: bool = 1 as bool }", "cannot be converted to bool")]
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
}
