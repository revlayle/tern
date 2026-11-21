namespace Tern.Compiler.Tests;

/// <summary>
/// M20 follow-up 3, spec 13: <c>step</c> is a keyword only right after the range of a <c>for</c>;
/// elsewhere it is an ordinary name (the port's <c>explode.step</c>, <c>sound.step</c> and locals).
/// </summary>
public class StepNameTests
{
    [Fact]
    public void Step_is_a_name_outside_a_for_header()
    {
        var r = Run.Program("""
            struct Anim { step: u8, frame: u8 }
            var anim: Anim
            var total: u8
            var count: u8
            var last: u8
            fn main() {
                var step: u8 = 2
                anim.step = step + 3
                for i in 0..10 step step { total += i }       // 0, 2, 4, 6, 8
                for i in 0..anim.step step 2 { count += 1 }   // 0, 2, 4
                for step2 in 0..=step { last = step2 }
            }
            """);
        Assert.Equal(5, r.U8("anim"));
        Assert.Equal(20, r.U8("total"));
        Assert.Equal(3, r.U8("count"));
        Assert.Equal(2, r.U8("last"));
    }

    [Fact]
    public void A_bound_named_step_and_a_step()
    {
        var r = Run.Program("""
            var step: u8 = 9
            var count: u8
            fn main() {
                for i in 0..step step 3 { count += 1 }   // 0, 3, 6
            }
            """);
        Assert.Equal(3, r.U8("count"));
    }

    [Fact]
    public void A_module_function_named_step()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "explode.tern"), "var n: u8\nfn step(s: u8) { n += s }");
            var r = Run.Program("import explode\nfn main() {\n explode.step(3)\n explode.step(4)\n}", sourcePath: Path.Combine(dir.FullName, "main.tern"));
            Assert.Equal(7, r.U8("explode.n"));
        }
        finally { dir.Delete(recursive: true); }
    }
}
