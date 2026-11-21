using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// Each kind of operand in each place an expression can stand, compared with the result computed in C#
/// (review code-quality-tests, finding 1). Feature tests vary the values; the bugs sit where two features
/// meet, such as a struct field as a <c>for</c> step or an <c>indexed match</c> inside a <c>using Y</c> loop.
/// Each case runs a statement once, or twice inside a <c>using X</c> or <c>using Y</c> loop. A form a place
/// does not allow must give an error. Every run is also compared with the program compiled without the optimizer.
/// Spec 3 (types).
/// </summary>
public class OperandMatrixTests
{
    /// <summary>The state the C# model of a case keeps: the operand's value and the calls made.</summary>
    private sealed class Model(int value, bool isCall)
    {
        public const int CallResult = 3;
        public int V = value;
        public readonly int Original = value;
        public int Calls;
        public int Counter;   // the global c of the while place

        public int Read()
        {
            if (!isCall) return V;
            Calls++;
            return CallResult;
        }
    }

    /// <param name="Assignable">Whether the form may be written (a call and a constant may not).</param>
    private sealed record Form(string Name, string Text, bool Assignable = true, bool IsCall = false);

    private static readonly Form[] Forms =
    [
        new("global", "g"),
        new("zero page", "z"),
        new("local", "l"),
        new("parameter", "par"),
        new("field", "s.f"),
        new("struct array", "recs[j].f"),
        new("indexed struct array", "irecs[j].f"),
        new("array element", "arr[j]"),
        new("pointer target", "p[0]"),
        new("call", "next()", Assignable: false, IsCall: true),
        new("volatile", "vv"),
        new("constant", "K", Assignable: false),
    ];

    /// <param name="Body">The statements, with X for the operand.</param>
    /// <param name="Delta">What one pass adds to r, from the model.</param>
    /// <param name="Return">A place in the return statement: the function's return type and expression; such a place runs once, in no loop.</param>
    /// <param name="Writes">The place assigns to the operand.</param>
    /// <param name="Calls">The place calls a function itself.</param>
    private sealed record Place(string Name, string Body, Func<Model, int> Delta, bool Writes = false, bool Calls = false,
        (string Type, string Expression)? Return = null);

    private const int Q = 5;

    private static readonly Place[] Places =
    [
        new("for start", "for _ in X..10 { r += 1 }", m => Math.Max(0, 10 - m.Read())),
        new("for end", "for _ in 0..X { r += 1 }", m => m.Read()),
        new("for step", "for i in 0..20 step X { r += i }", m =>
        {
            int step = m.Read(), sum = 0;
            for (int i = 0; i < 20; i += step) sum += i;
            return sum;
        }),
        new("match", "match X {\n3 => r += 1\n200 => r += 2\n_ => r += 4\n}", m => m.Read() switch { 3 => 1, 200 => 2, _ => 4 }),
        new("indexed match", "indexed match X & 3 {\n0 => r += 1\n1 => r += 2\n2 => r += 4\n3 => r += 8\n}", m => 1 << (m.Read() & 3)),
        new("index read", "r += tbl[X]", m => Table(m.Read())),
        new("index write", "buf[X] = 9\nr += buf[V]", m => m.Read() == m.Original ? 9 : 0),
        new("argument", "r += id(X)", m => m.Read(), Calls: true),
        new("first of two arguments", "r += sub(X, q)", m => m.Read() - Q, Calls: true),
        new("second of two arguments", "r += sub(q, X)", m => Q - m.Read(), Calls: true),
        new("if equal", "if X == V { r += 1 } else { r += 2 }", m => m.Read() == m.Original ? 1 : 2),
        new("if left of <", "if X < q { r += 1 } else { r += 2 }", m => m.Read() < Q ? 1 : 2),
        new("if right of <", "if q < X { r += 1 } else { r += 2 }", m => Q < m.Read() ? 1 : 2),
        new("while", "while X > c {\nc += 1\nr += 1\n}", m =>
        {
            int n = 0;
            while (m.Read() > m.Counter) { m.Counter++; n++; }
            return n;
        }),
        new("left of +", "r += X + q", m => m.Read() + Q),
        new("right of -", "r += q - X", m => Q - m.Read()),
        new("and", "r += X & 6", m => m.Read() & 6),
        new("shift", "r += X << 1", m => m.Read() << 1),
        new("bool", "b = X >= q\nif b { r += 1 }", m => m.Read() >= Q ? 1 : 0),
        new("pointer store", "p[1] = X\nr += p[1]", m => m.Read()),
        new("assigned", "X = q + 1\nr += X", m => m.V = Q + 1, Writes: true),
        new("compound assigned", "X += 2\nr += X", m => m.V = (m.V + 2) & 0xFF, Writes: true),
        new("return", "", m => 0, Return: ("u8", "X")),
        new("return into the carry", "", m => 0, Return: ("bool", "X >= q")),
    ];

    private static readonly string[] Contexts = ["once", "using X", "using Y"];

    private static readonly int[] Values = [3, 200];

    private static int Table(int i) => (i * 7 + 1) & 0xFF;

    /// <summary>
    /// Every form in every place and context; the loops with one value, to keep the run short. A case that compiles runs
    /// on every platform (review code-quality-tests 5), an error is checked on the C64.
    /// </summary>
    public static TheoryData<string, string, string, int, string> Cases()
    {
        var data = new TheoryData<string, string, string, int, string>();
        foreach (var form in Forms)
            foreach (var place in Places)
                foreach (var context in Contexts)
                {
                    if (place.Return is not null && context != "once") continue;
                    foreach (int v in context == "once" ? Values : Values[..1])
                        foreach (var target in ExpectedError(form, place, context, v) is null ? Platform.All.Select(p => p.Name) : [Platform.C64.Name])
                            data.Add(form.Name, place.Name, context, v, target);
                }
        return data;
    }

    /// <summary>The program of one case: the operand forms, set to <paramref name="v"/> by main, and the place in t.</summary>
    private static string Source(Form form, Place place, string context, int v, Platform platform)
    {
        string body = place.Body.Replace("X", form.Text).Replace("V", v.ToString());
        if (context != "once")
            body = $"for k in 1..3 {context} {{\n{body}\n}}";
        var ret = place.Return;
        string returnType = ret?.Type ?? "u8";
        string returnValue = ret is null ? "0" : ret.Value.Expression.Replace("X", form.Text);
        string call = returnType == "bool" ? $"if t({v}) {{ out = 1 }}" : $"out = t({v})";
        return $$"""
            struct S { pad: u8, f: u8 }
            const K: u8 = {{v}}
            var g: u8
            var z: u8 @ zp
            var s: S
            var recs: [S; 4]
            indexed var irecs: [S; 4]
            var arr: [u8; 4]
            var p: ptr u8 = ${{TestTarget.ScratchRam(platform) + 0x80:X4}}
            volatile var vv: u8 @ ${{TestTarget.ScratchRam(platform):X4}}
            var j: u8 = 2
            var q: u8 = {{Q}}
            var c: u8
            var b: bool
            var r: u8
            var out: u8
            var calls: u8
            var buf: [u8; 256]
            data tbl: [u8; 256] = [(i * 7 + 1) & 255 for i in 0..256]
            fn next() -> u8 {
                calls += 1
                asm {
                    ldx #$77
                    ldy #$77
                }
                return {{Model.CallResult}}
            }
            fn id(a: u8) -> u8 { return a }
            fn sub(a: u8, x: u8) -> u8 { return a - x }
            fn t(par: u8) -> {{returnType}} {
                var l: u8 = {{v}}
                {{body}}
                return {{returnValue}}
            }
            fn main() {
                g = {{v}}
                z = {{v}}
                s.f = {{v}}
                recs[j].f = {{v}}
                irecs[j].f = {{v}}
                arr[j] = {{v}}
                p[0] = {{v}}
                vv = {{v}}
                {{call}}
            }
            """;
    }

    /// <summary>
    /// A part of the error a case must give, or null when it must compile. Where two rules apply, the
    /// first one here is the error the checker reports first.
    /// </summary>
    private static string? ExpectedError(Form form, Place place, string context, int v)
    {
        // A call in a using loop (spec 6.6).
        if (context != "once" && (form.IsCall || place.Calls)) return $"calls a function, which can change {context[^1]}";
        // A call or a constant on the left of an assignment.
        if (place.Writes && !form.Assignable) return "left side of an assignment";
        // A constant range that counts up and never runs (spec 5.1).
        if (form.Name == "constant" && place.Name == "for start" && v >= 10) return "never runs";
        // A pointer access needs Y, which holds the loop variable.
        if (context == "using Y" && (form.Name == "pointer target" || place.Name == "pointer store")) return "needs the Y register";
        if (form.Name == "constant" && v > 127 && place.Name == "for step") return "between -127 and 127";
        if (form.Name == "constant" && v > 127 && place.Name == "shift") return "does not fit in u8";
        return null;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void An_operand_gives_the_same_result_in_every_place(string formName, string placeName, string context, int v, string target)
    {
        var form = Forms.Single(f => f.Name == formName);
        var place = Places.Single(p => p.Name == placeName);
        var platform = Platform.ByName(target)!;
        string source = Source(form, place, context, v, platform);
        var error = ExpectedError(form, place, context, v);
        if (error is not null)
        {
            var e = Assert.Throws<CompileError>(() => TernCompiler.Compile(source));
            Assert.Contains(error, e.Description);
            return;
        }
        var model = new Model(v, form.IsCall);
        int r = 0, passes = context == "once" ? 1 : 2;
        for (int k = 0; k < passes; k++) r += place.Delta(model);
        int @out = place.Return switch
        {
            null => 0,
            { Type: "u8" } => model.Read(),
            _ => model.Read() >= Q ? 1 : 0,
        };
        // Off the C64, results only: the comparison with a plain build is the C64's (it doubles the runs).
        var run = Run.Program(source, platform: platform, comparePlain: platform == Platform.C64);
        Assert.Equal(r & 0xFF, run.U8("r"));
        Assert.Equal(@out, run.U8("out"));
        Assert.Equal(model.Calls, run.U8("calls"));
    }

    /// <summary>An element store whose index reads the array being written: the index is the one before the store.</summary>
    [Theory]
    [InlineData("u8", 2)]
    [InlineData("u16", 0x0102)]
    public void An_index_that_reads_the_written_array_is_read_before_the_store(string type, int expected)
    {
        var run = Run.Program($$"""
            var a: [{{type}}; 4]
            var b: [{{type}}; 4] = [2, 0, 0, 0]
            var c: [{{type}}; 4] = [{{(type == "u8" ? "0" : "$0100")}}, 0, 0, 0]
            var k: u8
            var out: {{type}}
            fn main() {
                a[a[0]{{(type == "u8" ? "" : ".lo")}}] = b[k] + c[k]
                out = a[0]
            }
            """);
        Assert.Equal(expected, type == "u8" ? run.U8("out") : run.U16("out"));
    }
}
