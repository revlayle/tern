using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>A constant condition compiles only the branch taken (spec 2.2): what only the other branch uses is left out.</summary>
public class ConstantConditionTests
{
    private const string Functions = """
        var r: u8
        fn a() { r = 1 }
        fn b() { r = 2 }
        """;

    [Theory]
    [InlineData("const F = false", "F", "b", "a", 2)]
    [InlineData("const T = true", "T", "a", "b", 1)]
    [InlineData("const N = 3", "N > 5", "b", "a", 2)]
    [InlineData("const N = 3", "N < 5", "a", "b", 1)]
    [InlineData("const A = true\nconst B = false", "A && B", "b", "a", 2)]
    public void Only_the_branch_taken_is_compiled(string consts, string condition, string kept, string left, int expected)
    {
        var r = Run.Program($"{consts}\n{Functions}\nfn main() {{ if {condition} {{ a() }} else {{ b() }} }}");
        Assert.Equal(expected, r.U8("r"));
        Assert.Contains(kept, r.Result.Symbols.Keys);
        Assert.DoesNotContain(left, r.Result.Symbols.Keys);
        Assert.DoesNotContain($"\n{left}:", r.Result.Listing);
    }

    [Fact]
    public void An_if_without_else_on_a_false_constant_compiles_nothing()
    {
        var r = Run.Program($"const F = false\n{Functions}\nfn main() {{ r = 9\n if F {{ a() }} }}");
        Assert.Equal(9, r.U8("r"));
        Assert.DoesNotContain("a", r.Result.Symbols.Keys);
    }

    [Fact]
    public void A_nested_constant_condition_inside_a_branch_taken_is_folded_too()
    {
        var r = Run.Program($"const T = true\nconst F = false\n{Functions}\nfn c() {{ r = 3 }}\nfn main() {{ if T {{ if F {{ a() }} else {{ b() }} }} else {{ c() }} }}");
        Assert.Equal(2, r.U8("r"));
        Assert.DoesNotContain("a", r.Result.Symbols.Keys);
        Assert.DoesNotContain("c", r.Result.Symbols.Keys);
    }

    [Fact]
    public void A_function_used_by_both_branches_or_by_a_variable_condition_stays()
    {
        var r = Run.Program($"var v: bool\n{Functions}\nfn main() {{ if v {{ a() }} else {{ b() }} }}");
        Assert.Contains("a", r.Result.Symbols.Keys);
        Assert.Contains("b", r.Result.Symbols.Keys);
    }

    [Fact]
    public void A_function_called_from_the_dead_branch_and_from_elsewhere_stays()
    {
        var r = Run.Program($"const F = false\n{Functions}\nfn main() {{ if F {{ a() }}\n a() }}");
        Assert.Equal(1, r.U8("r"));
    }

    [Fact]
    public void The_address_of_a_function_in_the_branch_not_taken_does_not_keep_it()
    {
        var r = Run.Program($"const F = false\n{Functions}\nvar p: u16\nfn main() {{ if F {{ p = addr(a) }} else {{ p = addr(b) }} }}");
        Assert.Equal(r.Address("b"), r.U16("p"));
        Assert.DoesNotContain("a", r.Result.Symbols.Keys);
    }

    [Fact]
    public void A_while_on_a_false_constant_keeps_nothing_its_body_calls()
    {
        var r = Run.Program($"const F = false\n{Functions}\nfn main() {{ while F {{ a() }}\n b() }}");
        Assert.DoesNotContain("a", r.Result.Symbols.Keys);
        Assert.Equal(2, r.U8("r"));
    }

    [Fact]
    public void The_branch_not_taken_is_still_checked()
    {
        var e = Assert.Throws<CompileError>(() => TernCompiler.Compile($"const F = false\n{Functions}\nfn main() {{ if F {{ nothing() }} }}"));
        Assert.Contains("'nothing' is not a function", e.Description);
        e = Assert.Throws<CompileError>(() => TernCompiler.Compile($"const T = true\n{Functions}\nfn main() {{ if T {{ a() }} else {{ r = 300 }} }}"));
        Assert.Contains("300 does not fit in u8", e.Description);
    }

    [Fact]
    public void The_sprites_module_without_FLICKER_has_no_flicker_code_or_tables()
    {
        string Program(string parameters) => $"import c64\nimport sprites(COUNT = 24{parameters})\nfn main() {{ sprites.init()\n sprites.update() }}";
        var plain = TernCompiler.Compile(Program(""));
        Assert.DoesNotContain(plain.Symbols.Keys, k => k.StartsWith("sprites.build_flicker") || k is "sprites.shown" or "sprites.disp");
        Assert.DoesNotContain("sprites.build_flicker:", plain.Listing);
        var flicker = TernCompiler.Compile(Program(", FLICKER = true"));
        Assert.Contains("sprites.build_flicker", flicker.Symbols.Keys);
        Assert.Contains("sprites.shown", flicker.Symbols.Keys);
        Assert.True(flicker.Image.Length > plain.Image.Length);
    }
}
