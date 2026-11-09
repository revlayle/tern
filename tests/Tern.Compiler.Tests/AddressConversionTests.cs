using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// Spec 3.4, 3.5 and 4 as settled on 2026-11-09 (question 83): .lo / .hi of addr() in code, a pointer
/// converting to u16 where a u16 is expected, and zero-page globals with addresses in their initial values.
/// </summary>
public class AddressConversionTests
{
    [Fact]
    public void Zero_page_globals_with_address_initial_values_are_set_at_startup()
    {
        // Review suggestion 6: the startup code loads #(map+299).hi and the others.
        var r = Run.Program("""
            var map: [u8; 300]
            var h: u8 @ zp = (addr(map) + 299).hi
            var l: u8 @ zp = (addr(main) - 1).lo
            var j: u16 @ zp = addr(main) + 2
            fn main() { }
            """);
        int map = r.Address("map"), main = r.Address("main");
        Assert.True(r.Address("h") < 0x100 && r.Address("l") < 0x100 && r.Address("j") < 0x100);
        Assert.Equal((map + 299) >> 8, r.U8("h"));
        Assert.Equal((main - 1) & 0xFF, r.U8("l"));
        Assert.Equal(main + 2, r.U16("j"));
    }

    [Fact]
    public void Lo_and_hi_of_an_address_in_code()
    {
        // Question 83: the address bytes in code too, also for a struct with a field named lo.
        var r = Run.Program("""
            struct S { lo: u8, hi: u8 }
            var s: S
            var map: [u8; 300]
            fn f() { }
            var a: u8
            var b: u8
            var c: u8
            var d: u8
            var e: u8
            data table: [u8; 2] = [addr(s).lo, addr(s).hi]
            fn main() {
                a = addr(map).lo
                b = addr(map).hi
                c = addr(s).lo
                d = addr(f).hi
                e = ((addr(map) + 299) as u16).hi
                s.lo = 7
            }
            """);
        int map = r.Address("map"), s = r.Address("s");
        Assert.Equal(map & 0xFF, r.U8("a"));
        Assert.Equal(map >> 8, r.U8("b"));
        Assert.Equal(s & 0xFF, r.U8("c"));
        Assert.Equal(r.Address("f") >> 8, r.U8("d"));
        Assert.Equal((map + 299) >> 8, r.U8("e"));
        Assert.Equal([(byte)s, (byte)(s >> 8)], r.Ram.Bytes.AsSpan(r.Address("table"), 2).ToArray());
        Assert.Equal(7, r.U8("s"));
    }

    [Fact]
    public void A_pointer_converts_to_u16_where_a_u16_is_expected()
    {
        // Spec 3.5: assignment, initial value, table element, argument, return value.
        var r = Run.Program("""
            var map: [u8; 100]
            var p: ptr u8 = addr(map) + 5
            var a: u16 = addr(map)
            data t: [u16; 3] = [addr(map), addr(map) + 40, addr(main) - 1]
            var assigned: u16
            var passed: u16
            var returned: u16
            fn take(n: u16) { passed = n }
            fn give() -> u16 { return p }
            fn main() {
                assigned = p
                take(p + 1)
                returned = give()
            }
            """);
        int map = r.Address("map"), t = r.Address("t");
        Assert.Equal(map, r.U16("a"));
        Assert.Equal(map + 5, r.U16("assigned"));
        Assert.Equal(map + 6, r.U16("passed"));
        Assert.Equal(map + 5, r.U16("returned"));
        Assert.Equal(map, r.Ram.Bytes[t] | r.Ram.Bytes[t + 3] << 8);
        Assert.Equal(map + 40, r.Ram.Bytes[t + 1] | r.Ram.Bytes[t + 4] << 8);
        Assert.Equal(r.Address("main") - 1, r.Ram.Bytes[t + 2] | r.Ram.Bytes[t + 5] << 8);
    }

    [Theory]
    [InlineData("var p: ptr u8\nvar w: u16\nfn main() { w = w + p }",
        "A number before a pointer is not pointer arithmetic. Write 'p + w' to move the pointer, or convert the pointer with 'as u16' for a number")]
    [InlineData("var p: ptr u8\nvar w: u16\nvar b: bool\nfn main() { b = p == w }", "Cannot compare ptr u8 with u16; convert one side with 'as'")]
    [InlineData("var p: ptr u8\nvar w: u16\nfn main() { p = w }", "Expected ptr u8, found u16. Convert with 'as ptr u8'")]
    [InlineData("fn take(n: ptr u8) { }\nvar w: u16\nfn main() { take(w) }", "Expected ptr u8, found u16. Convert with 'as ptr u8'")]
    [InlineData("var p: ptr u8\nvar x: u8\nfn main() { x = p }", "Expected u8, found ptr u8")]
    public void A_pointer_still_needs_as_in_an_operator(string source, string expected)
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
        Assert.Contains(expected, e.Description);
    }
}
