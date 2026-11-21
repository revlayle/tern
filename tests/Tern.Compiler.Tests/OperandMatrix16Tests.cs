using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// The 16-bit column of <see cref="OperandMatrixTests"/> (review R1, suggestion 4): each kind of u16
/// operand in each place where a 16-bit value can stand (operators, compares, assignments, a pointer
/// store into a ptr u16, an argument, a return, a loop end), compared with the result computed in C#.
/// It guards the 16-bit fixes of review code-quality-backend 3 and 5.
/// Spec 3 (types).
/// </summary>
public class OperandMatrix16Tests
{
    /// <summary>The state the C# model of a case keeps: the operand's value and the calls made.</summary>
    private sealed class Model(int value, bool isCall)
    {
        public const int CallResult = 0x0103;
        public int V = value;
        public readonly int Original = value;
        public int Calls;

        public int Read()
        {
            if (!isCall) return V;
            Calls++;
            return CallResult;
        }
    }

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

    private sealed record Place(string Name, string Body, Func<Model, int> Delta, bool Writes = false, bool Calls = false,
        (string Type, string Expression)? Return = null);

    /// <summary>The other operand: its high byte is not zero, so the compares and the carry look at both bytes.</summary>
    private const int Q = 0x0105;

    private static readonly Place[] Places =
    [
        new("for end", "for _ in 0..X { r += 1 }", m => m.Read()),
        new("argument", "r += id(X)", m => m.Read(), Calls: true),
        new("if equal", "if X == V { r += 1 } else { r += 2 }", m => m.Read() == m.Original ? 1 : 2),
        new("if left of <", "if X < q { r += 1 } else { r += 2 }", m => m.Read() < Q ? 1 : 2),
        new("if right of <", "if q < X { r += 1 } else { r += 2 }", m => Q < m.Read() ? 1 : 2),
        new("left of +", "r += X + q", m => m.Read() + Q),
        new("right of -", "r += q - X", m => Q - m.Read()),
        new("and", "r += X & $0F0F", m => m.Read() & 0x0F0F),
        new("xor", "r += X ^ q", m => m.Read() ^ Q),
        new("shift", "r += X << 1", m => m.Read() << 1),
        new("bool", "b = X >= q\nif b { r += 1 }", m => m.Read() >= Q ? 1 : 0),
        new("pointer store", "p[1] = X\nr += p[1]", m => m.Read()),
        new("element store", "w[1] = X + q\nr += w[1]", m => m.Read() + Q),
        new("assigned", "X = q + 1\nr += X", m => m.V = Q + 1, Writes: true),
        new("assigned a sum", "X = q + j\nr += X", m => m.V = Q + 2, Writes: true),
        new("compound assigned", "X += 2\nr += X", m => m.V = (m.V + 2) & 0xFFFF, Writes: true),
        new("compound added", "X += q\nr += X", m => m.V = (m.V + Q) & 0xFFFF, Writes: true),
        new("compound shifted", "X <<= 2\nr += X", m => m.V = (m.V << 2) & 0xFFFF, Writes: true),
        new("return", "", m => 0, Return: ("u16", "X")),
        new("return a compare", "", m => 0, Return: ("bool", "X >= q")),
    ];

    private static readonly string[] Contexts = ["once", "using X", "using Y"];

    private static readonly int[] Values = [3, 0x1234];

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
                        foreach (var target in ExpectedError(form, place, context) is null ? Platform.All.Select(p => p.Name) : [Platform.C64.Name])
                            data.Add(form.Name, place.Name, context, v, target);
                }
        return data;
    }

    private static string Source(Form form, Place place, string context, int v, Platform platform)
    {
        string body = place.Body.Replace("X", form.Text).Replace("V", v.ToString());
        if (context != "once")
            body = $"for k in 1..3 {context} {{\n{body}\n}}";
        var ret = place.Return;
        string returnType = ret?.Type ?? "u16";
        string returnValue = ret is null ? "0" : ret.Value.Expression.Replace("X", form.Text);
        string call = returnType == "bool" ? $"if t({v}) {{ out = 1 }}" : $"out = t({v})";
        return $$"""
            struct S { pad: u8, f: u16 }
            const K: u16 = {{v}}
            var g: u16
            var z: u16 @ zp
            var s: S
            var recs: [S; 4]
            indexed var irecs: [S; 4]
            var arr: [u16; 4]
            var w: [u16; 4]
            var p: ptr u16 = ${{TestTarget.ScratchRam(platform) + 0x80:X4}}
            volatile var vv: u16 @ ${{TestTarget.ScratchRam(platform):X4}}
            var j: u8 = 2
            var q: u16 = {{Q}}
            var b: bool
            var r: u16
            var out: u16
            var calls: u8
            fn next() -> u16 {
                calls += 1
                asm {
                    ldx #$77
                    ldy #$77
                }
                return {{Model.CallResult}}
            }
            fn id(a: u16) -> u16 { return a }
            fn t(par: u16) -> {{returnType}} {
                var l: u16 = {{v}}
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

    /// <summary>A part of the error a case must give, or null when it must compile.</summary>
    private static string? ExpectedError(Form form, Place place, string context)
    {
        if (context != "once" && (form.IsCall || place.Calls)) return $"calls a function, which can change {context[^1]}";
        if (place.Writes && !form.Assignable) return "left side of an assignment";
        if (context == "using Y" && (form.Name == "pointer target" || place.Name == "pointer store")) return "needs the Y register";
        return null;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_16_bit_operand_gives_the_same_result_in_every_place(string formName, string placeName, string context, int v, string target)
    {
        var form = Forms.Single(f => f.Name == formName);
        var place = Places.Single(p => p.Name == placeName);
        var platform = Platform.ByName(target)!;
        string source = Source(form, place, context, v, platform);
        if (ExpectedError(form, place, context) is { } error)
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
            { Type: "u16" } => model.Read(),
            _ => model.Read() >= Q ? 1 : 0,
        };
        // Off the C64, results only: the comparison with a plain build is the C64's (it doubles the runs).
        var run = Run.Program(source, platform: platform, comparePlain: platform == Platform.C64);
        Assert.Equal(r & 0xFFFF, run.U16("r"));
        Assert.Equal(@out, run.U16("out"));
        Assert.Equal(model.Calls, run.U8("calls"));
    }
}
