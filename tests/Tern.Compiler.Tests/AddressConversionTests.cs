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
    public void Lo_and_hi_of_an_address_plus_or_minus_a_constant_in_code()
    {
        // Question 84: every address form an initial value allows, also with a struct field named hi.
        var r = Run.Program("""
            struct S { lo: u8, hi: u8 }
            var t: S
            var map: [u8; 300]
            fn f() { }
            const K = 40
            var a: u8
            var b: u8
            var c: u8
            var d: u8
            var e: u8
            var g: u8
            var h: u8
            data table: [u8; 2] = [(addr(map) + 299).hi, (2 + addr(map)).lo]
            fn main() {
                a = (addr(map) + 299).hi
                b = (addr(map) + 299).lo
                c = (2 + addr(map)).lo
                d = (addr(f) - 1).hi
                e = (addr(t) + 1).hi
                g = (addr(map) + K * 7 - 3).hi
                h = (addr(t) + 1).lo
                t.hi = 9
            }
            """);
        int map = r.Address("map"), t = r.Address("t");
        Assert.Equal((map + 299) >> 8, r.U8("a"));
        Assert.Equal((map + 299) & 0xFF, r.U8("b"));
        Assert.Equal((map + 2) & 0xFF, r.U8("c"));
        Assert.Equal((r.Address("f") - 1) >> 8, r.U8("d"));
        Assert.Equal((t + 1) >> 8, r.U8("e"));
        Assert.Equal((map + 277) >> 8, r.U8("g"));
        Assert.Equal((t + 1) & 0xFF, r.U8("h"));
        Assert.Equal(9, r.U8("t", 1));
        Assert.Equal([(byte)((map + 299) >> 8), (byte)(map + 2)], r.Ram.Bytes.AsSpan(r.Address("table"), 2).ToArray());
    }

    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Lo_and_hi_of_an_address_with_an_offset_is_one_load()
    {
        var r = Run.Program("""
            var map: [u8; 300]
            var a: u8
            var b: u8
            fn main() {
                a = (addr(map) + 299).hi
                b = (addr(map) - 1).lo
            }
            """);
        var main = r.Result.Listing[r.Result.Listing.IndexOf("main:")..];
        main = main[..main.IndexOf("rts")];
        Assert.Contains("lda #(map+299).hi", main);
        Assert.Contains("lda #(map-1).lo", main);
        Assert.DoesNotContain("adc", main);
        Assert.DoesNotContain("sbc", main);
        Assert.Equal((r.Address("map") - 1) & 0xFF, r.U8("b"));
    }

    [Theory]
    [InlineData("var p: ptr u8\nvar x: u8\nfn main() { x = p.hi }", 3, 18, ".hi needs a number; convert the pointer first: (p as u16).hi")]
    [InlineData("var map: [u8; 9]\nvar n: u8\nvar x: u8\nfn main() { x = (addr(map) + n).hi }", 4, 32, ".hi needs a number; convert the pointer first: (p as u16).hi")]
    public void Lo_and_hi_of_a_pointer_that_is_not_a_filled_in_address_is_an_error(string source, int line, int column, string expected)
    {
        Expect.Error(source, line, column, expected);
    }

    [Fact]
    public void A_number_before_a_pointer_names_the_operands_as_written()
    {
        // Question 85.
        Expect.Error(() => TernCompiler.Compile("var map: [u8; 9]\nvar p: ptr u8\nvar n: u8\nfn main() { p = n + addr(map) }"), 4, 19, "A number before a pointer is not pointer arithmetic. Write 'addr(map) + n' to move the pointer, or convert the pointer with 'as u16' for a number");
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
        3, 19, "A number before a pointer is not pointer arithmetic. Write 'p + w' to move the pointer, or convert the pointer with 'as u16' for a number")]
    [InlineData("var p: ptr u8\nvar w: u16\nvar b: bool\nfn main() { b = p == w }", 4, 19, "Cannot compare ptr u8 with u16; convert one side with 'as'")]
    [InlineData("var p: ptr u8\nvar w: u16\nfn main() { p = w }", 3, 17, "Expected ptr u8, found u16. Convert with 'as ptr u8'")]
    [InlineData("fn take(n: ptr u8) { }\nvar w: u16\nfn main() { take(w) }", 3, 18, "Expected ptr u8, found u16. Convert with 'as ptr u8'")]
    [InlineData("var p: ptr u8\nvar x: u8\nfn main() { x = p }", 3, 17, "Expected u8, found ptr u8. Convert with 'as u16' first, then take .lo or .hi")]
    // Review 2, finding 1: a compound assignment is the operation it stands for (spec 3.1).
    [InlineData("var p: ptr u8\nvar w: u16\nfn main() { w += p }", 3, 18, "Expected u16, found ptr u8. Convert with 'as u16'")]
    [InlineData("var p: ptr u8\nvar w: u16\nfn main() { w -= p }", 3, 18, "Expected u16, found ptr u8. Convert with 'as u16'")]
    [InlineData("var p: ptr u8\nvar w: u16\nfn main() { w &= p }", 3, 18, "Expected u16, found ptr u8. Convert with 'as u16'")]
    [InlineData("var map: [u8; 4]\nvar w: u16\nfn main() { w += addr(map) }", 3, 18, "Expected u16, found ptr u8. Convert with 'as u16'")]
    // Review 3, suggestion 1: a target that is not u16 gets the usual conversion error.
    [InlineData("var p: ptr u8\nvar x: u8\nfn main() { x += p }", 3, 18, "Expected u8, found ptr u8. Convert with 'as u16' first, then take .lo or .hi")]
    public void A_pointer_still_needs_as_in_an_operator(string source, int line, int column, string expected)
    {
        Expect.Error(source, line, column, expected);
    }
}
