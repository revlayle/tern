using System.Globalization;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;

namespace Tern.Compiler.CompileTime;

/// <summary>
/// Runs compile-time code (spec 4.1): one top-level evaluation, such as a constant's value, a data
/// table's initial value or one generator call in assembly. A tree-walking interpreter, so that errors
/// can name the position, the call chain and the step count.
/// </summary>
public sealed partial class Interpreter
{
    /// <summary>Statements run plus loop passes, for each top-level evaluation (spec 4.1).</summary>
    public const long StepLimit = 10_000_000;
    /// <summary>How deep const fn calls may nest, so a runaway recursion is an error and not a crash.</summary>
    public const int CallDepthLimit = 200;

    /// <summary>The functions that exist only at compile time (spec 4.1), unless the program declares the name.</summary>
    public static readonly HashSet<string> Builtins =
    [
        "sin", "cos", "tan", "atan2", "sqrt", "pow", "exp", "log", "log2", "abs", "min", "max",
        "round", "floor", "ceil", "trunc", "label", "repeat",
    ];

    private readonly ICompileTimeHost _host;
    /// <summary>The declaration or asm line that started this evaluation.</summary>
    private readonly SourcePos _origin;
    private long _steps;
    private readonly List<Frame> _frames = [];
    private int _labels;

    /// <summary>A const fn being run, or the top-level expression (no function).</summary>
    private sealed class Frame(ConstFnSymbol? function)
    {
        public ConstFnSymbol? Function { get; } = function;
        public Module? Module => Function?.Module;
        public List<Dictionary<string, Slot>> Scopes { get; } = [[]];
        public CtValue? Returned { get; set; }
    }

    /// <summary>A variable of a const fn.</summary>
    private sealed class Slot(CtValue value, CtType type, string? readOnly)
    {
        public CtValue Value { get; set; } = value;
        public CtType Type { get; } = type;
        /// <summary>Why it cannot be assigned ("the loop variable", "a constant"), or null.</summary>
        public string? ReadOnly { get; } = readOnly;
    }

    private enum Flow { Normal, Break, Continue, Return }

    private Interpreter(ICompileTimeHost host, SourcePos origin)
    {
        _host = host;
        _origin = origin;
        _frames.Add(new Frame(null));
    }

    /// <summary>Evaluates a compile-time expression that starts at <paramref name="origin"/>.</summary>
    public static CtValue Evaluate(Expr e, ICompileTimeHost host, SourcePos origin) => new Interpreter(host, origin).Eval(e);

    private Frame Top => _frames[^1];

    /// <summary>Where the evaluation started, as "main.tern:5", for error messages.</summary>
    private string OriginText => Where(_origin);

    /// <summary>A line as "main.tern:5", or "line 5" for source given without a file.</summary>
    public static string Where(SourcePos pos) => pos.File is null ? $"line {pos.Line}" : $"{Path.GetFileName(pos.File)}:{pos.Line}";

    /// <summary>Adds "(note)" after the first sentence of an error, so that the fix stays at the end.</summary>
    public static string WithNote(string message, string note)
    {
        int end = message.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? $"{message} ({note})" : $"{message[..end]} ({note}){message[end..]}";
    }

    /// <summary>
    /// An error in compile-time code. Inside a const fn it names the line that started the evaluation,
    /// after the first sentence, so that the fix stays at the end.
    /// </summary>
    private CompileError Error(SourcePos pos, string message)
    {
        if (Top.Function is null) return new CompileError(pos, message);
        return new CompileError(pos, WithNote(message, $"called from {OriginText}")) { CalledFrom = _origin };
    }

    private void Step()
    {
        if (++_steps <= StepLimit) return;
        if (_frames.LastOrDefault(f => f.Function is not null)?.Function is { } fn)
            throw new CompileError(fn.Pos,
                $"'{fn.Name}' did not finish after {StepLimit:N0} steps (called from {OriginText}). Check its loops for one that never ends")
            {
                CalledFrom = _origin,
            };
        throw new CompileError(_origin, $"This list did not finish after {StepLimit:N0} steps. Use a shorter range");
    }

    // ---------- Expressions ----------

    private CtValue Eval(Expr e)
    {
        switch (e)
        {
            case NumberExpr n: return new CtInt(n.Value);
            case RealExpr r: return new CtReal(r.Value);
            case BoolExpr b: return CtBool.Of(b.Value);
            case CharExpr c: return new CtInt(_host.Encode(Top.Module, c));
            case StringExpr:
                throw Error(e.Pos, "A string is not a compile-time value. Use a list of numbers, or read the bytes from data");
            case StructLiteralExpr s:
                throw Error(e.Pos, $"A struct literal is not a compile-time value. Use the value of each field, e.g. a list of numbers");
            case NameExpr n: return Read(n);
            case UnaryExpr u: return Unary(u);
            case BinaryExpr b: return Binary(b.Op, b.Left, b.Right, b.Pos);
            case CallExpr c: return Call(c, statement: false);
            case IndexExpr ix:
            {
                var (array, i) = Element(ix);
                return array.Items[i];
            }
            case MemberExpr { Member: "lo" or "hi" } m:
            {
                long v = Int(Eval(m.Target), m.Target.Pos, $"'.{m.Member}'");
                return new CtInt(m.Member == "lo" ? v & 0xFF : (v >> 8) & 0xFF);
            }
            case MemberExpr m:
                throw Error(m.Pos, $"Unknown member '.{m.Member}'. In a const fn, use .lo and .hi of a number");
            case CastExpr c:
                throw Error(c.Pos, "'as' converts runtime types, which a const fn does not have. Use '& $FF' for 8-bit behavior, or round(x), floor(x), ceil(x) or trunc(x) for a real");
            case ArrayLiteralExpr a: return MakeArray(a.Elements.Select(Eval).ToList(), a.Elements.Select(x => x.Pos).ToList(), a.Pos);
            case ListForExpr l: return ListFor(l);
            case AsmLabelExpr l:
                // Only as an argument on a generator's call line (spec 4.1).
                if (_blockLabels is null || Top.Function is not null)
                    throw Error(l.Pos, $"'{l.Name}' is a label of an asm block. Pass it as an argument on the generator's call line");
                if (!_blockLabels.Contains(l.Name))
                    throw Error(l.Pos, $"'{l.Name}' is not a label of this block. Use a label the block places, e.g. '{l.Name}:'");
                return new CtLabel(0, l.Pos) { BlockLabel = l.Name };
            default:
                throw Error(e.Pos, "This is not a compile-time value");
        }
    }

    private CtValue Read(NameExpr n)
    {
        if (FindSlot(n.Name) is { } slot) return slot.Value;
        if (_host.Read(Top.Module, n.Name, n.Pos, Top.Function is not null) is { } v) return v;
        return n.Name switch
        {
            "PI" => new CtReal(Math.PI),
            "TAU" => new CtReal(Math.Tau),
            _ => throw Error(n.Pos, $"'{n.Name}' is not declared"),
        };
    }

    private Slot? FindSlot(string name)
    {
        var scopes = Top.Scopes;
        for (int i = scopes.Count - 1; i >= 0; i--)
            if (scopes[i].TryGetValue(name, out var slot)) return slot;
        return null;
    }

    /// <summary>The array and the index of <c>a[i]</c>, checked against the array's length.</summary>
    private (CtArray Array, int Index) Element(IndexExpr ix)
    {
        if (Eval(ix.Target) is not CtArray array)
            throw Error(ix.Pos, "Only arrays can be indexed. Use an array, or a data table");
        long i = Int(Eval(ix.Index), ix.Index.Pos, "An index");
        if (i < 0 || i >= array.Items.Length)
            throw Error(ix.Index.Pos, $"Index {i} is outside the array (length {array.Items.Length}). Use an index from 0 to {array.Items.Length - 1}");
        return (array, (int)i);
    }

    /// <summary>An array from a list of values: numbers (ints become reals if one is a real), bools, labels or arrays.</summary>
    private CtArray MakeArray(List<CtValue> values, List<SourcePos> positions, SourcePos pos)
    {
        if (values.Count == 0) return new CtArray([], CtType.Int);
        var type = values[0].Type;
        for (int k = 1; k < values.Count; k++)
            type = Unify(type, values[k].Type) ?? throw Error(positions[k],
                $"This element is {Describe(values[k].Type)}, but the list's first one is {Describe(values[0].Type)}. Use one kind of value in a list");
        return new CtArray(values.Select((v, k) => Convert(v, type, positions[k], "This element")).ToArray(), type);
    }

    /// <summary>The type of a list with elements of both types: an int and a real make a real, also inside arrays. Null if they do not mix.</summary>
    private static CtType? Unify(CtType a, CtType b)
    {
        if (a.Kind is CtKind.Int or CtKind.Real && b.Kind is CtKind.Int or CtKind.Real) return a.Kind == b.Kind ? a : CtType.Real;
        if (a.Kind == CtKind.Array && b.Kind == CtKind.Array) return Unify(a.Element!, b.Element!) is { } e ? CtType.ArrayOf(e) : null;
        return a.Kind == b.Kind ? a : null;
    }

    private CtArray ListFor(ListForExpr l)
    {
        var values = new List<CtValue>();
        var positions = new List<SourcePos>();
        Top.Scopes.Add([]);
        try
        {
            foreach (long i in Range(l.From, l.To, l.Inclusive, l.Step, l.RangeText))
            {
                Step();
                if (l.Variable is not null) Top.Scopes[^1][l.Variable] = new Slot(new CtInt(i), CtType.Int, "the list's variable");
                values.Add(Eval(l.Element));
                positions.Add(l.Element.Pos);
                if (values.Count > 0x10000)
                    throw Error(l.Pos, "An array has at most 65536 elements. Use a shorter range");
            }
        }
        finally { Top.Scopes.RemoveAt(Top.Scopes.Count - 1); }
        return MakeArray(values, positions, l.Pos);
    }

    /// <summary>
    /// The values of a <c>for</c> range (spec 5.1): the bounds and step are evaluated once. Without a
    /// step the range counts up; constant bounds from a larger start are an error, as in runtime code
    /// (an int is signed, so a variable start with an end of 0 is not).
    /// </summary>
    private IEnumerable<long> Range(Expr fromExpr, Expr toExpr, bool inclusive, Expr? stepExpr, string text)
    {
        long from = Int(Eval(fromExpr), fromExpr.Pos, "A loop bound");
        long to = Int(Eval(toExpr), toExpr.Pos, "A loop bound");
        long step;
        if (stepExpr is not null)
        {
            step = Int(Eval(stepExpr), stepExpr.Pos, "The step");
            if (step == 0) throw Error(stepExpr.Pos, "The step is 0, so the loop would never end. Use a step other than 0");
        }
        else if (from > to && IsConstant(fromExpr) && IsConstant(toExpr))
            throw Error(fromExpr.Pos, Checker.CountsUp(text, fromExpr.Pos, "Write 'step -1' to count down").Description);
        else step = 1;
        return Values(from, to, inclusive, step);

        static IEnumerable<long> Values(long from, long to, bool inclusive, long step)
        {
            for (long i = from; step > 0 ? (inclusive ? i <= to : i < to) : (inclusive ? i >= to : i > to); i += step)
            {
                yield return i;
                if (step > 0 ? i > long.MaxValue - step : i < long.MinValue - step) yield break;
            }
        }
    }

    /// <summary>A bound that names none of the const fn's variables (its constants count as constant).</summary>
    private bool IsConstant(Expr e) => e switch
    {
        NameExpr n => FindSlot(n.Name) is not { } s || s.ReadOnly == "a constant",
        UnaryExpr u => IsConstant(u.Operand),
        BinaryExpr b => IsConstant(b.Left) && IsConstant(b.Right),
        CallExpr c => c.Name != "label" && c.Args.All(IsConstant),
        IndexExpr ix => IsConstant(ix.Target) && IsConstant(ix.Index),
        MemberExpr m => IsConstant(m.Target),
        _ => true,
    };

    private CtValue Unary(UnaryExpr u)
    {
        var v = Eval(u.Operand);
        switch (u.Op)
        {
            case TokenKind.Bang:
                if (v is CtBool b) return CtBool.Of(!b.Value);
                throw Error(u.Pos, $"'!' needs a bool, not {Describe(v.Type)}. Use '~' to flip the bits of an int");
            case TokenKind.Minus when v is CtInt i: return new CtInt(Checker.ConstNegate(u.Pos, i.Value));
            case TokenKind.Minus when v is CtReal r: return new CtReal(-r.Value);
            case TokenKind.Plus when v is CtInt or CtReal: return v;
            case TokenKind.Tilde when v is CtInt i: return new CtInt(~i.Value);
            case TokenKind.Tilde:
                throw Error(u.Pos, $"'~' needs an int, not {Describe(v.Type)}");
            default:
                throw Error(u.Pos, $"'{(u.Op == TokenKind.Minus ? "-" : "+")}' needs a number, not {Describe(v.Type)}");
        }
    }

    private CtValue Binary(TokenKind op, Expr leftExpr, Expr rightExpr, SourcePos pos)
    {
        if (op is TokenKind.AmpAmp or TokenKind.PipePipe)
        {
            bool l = Bool(Eval(leftExpr), leftExpr.Pos, $"'{OpText(op)}'");
            if (op == TokenKind.AmpAmp ? !l : l) return CtBool.Of(l);
            return CtBool.Of(Bool(Eval(rightExpr), rightExpr.Pos, $"'{OpText(op)}'"));
        }
        return Binary(op, Eval(leftExpr), Eval(rightExpr), pos);
    }

    private CtValue Binary(TokenKind op, CtValue left, CtValue right, SourcePos pos)
    {
        string name = OpText(op);
        // A label plus or minus an int is a label with an offset (spec 4.1); nothing else reads its number.
        if (op == TokenKind.Plus && left is CtLabel l1 && right is CtInt r1) return l1.Plus(r1.Value);
        if (op == TokenKind.Plus && left is CtInt l2 && right is CtLabel r2) return r2.Plus(l2.Value);
        if (op == TokenKind.Minus && left is CtLabel l3 && right is CtInt r3) return l3.Plus(Checker.ConstNegate(pos, r3.Value));
        if (left is CtLabel || right is CtLabel)
            throw Error(pos, $"A label's address is decided when the program is placed, so the const fn cannot use it with '{name}'. "
                + "Use the label alone, plus or minus an int, or in an emit operand as .lo / .hi");
        if (op is TokenKind.EqEq or TokenKind.NotEq && left is CtBool lb && right is CtBool rb)
            return CtBool.Of((lb.Value == rb.Value) == (op == TokenKind.EqEq));
        if (left is not (CtInt or CtReal) || right is not (CtInt or CtReal))
        {
            var odd = left is CtInt or CtReal ? right : left;
            throw Error(pos, op is TokenKind.Amp or TokenKind.Pipe && odd is CtBool
                ? $"'{name}' works on the bits of ints. Use '{name}{name}' for bools"
                : $"'{name}' needs numbers, not {Describe(odd.Type)}. Use an int or a real");
        }
        if (left is CtInt li && right is CtInt ri)
        {
            long l = li.Value, r = ri.Value;
            switch (op)
            {
                case TokenKind.EqEq: return CtBool.Of(l == r);
                case TokenKind.NotEq: return CtBool.Of(l != r);
                case TokenKind.Less: return CtBool.Of(l < r);
                case TokenKind.LessEq: return CtBool.Of(l <= r);
                case TokenKind.Greater: return CtBool.Of(l > r);
                case TokenKind.GreaterEq: return CtBool.Of(l >= r);
                case TokenKind.Slash or TokenKind.Percent when r == 0:
                    throw Error(pos, "Division by zero");
                case TokenKind.Shl or TokenKind.Shr when r is < 0 or > 63:
                    throw Error(pos, $"The shift count is {r}. Use a count from 0 to 63");
            }
            try { return new CtInt(Checker.ConstArithmetic(pos, op, l, r)); }
            catch (CompileError e) { throw Error(e.Pos, e.Description); }
        }

        double a = Real(left), b = Real(right);
        double result;
        switch (op)
        {
            case TokenKind.EqEq: return CtBool.Of(a == b);
            case TokenKind.NotEq: return CtBool.Of(a != b);
            case TokenKind.Less: return CtBool.Of(a < b);
            case TokenKind.LessEq: return CtBool.Of(a <= b);
            case TokenKind.Greater: return CtBool.Of(a > b);
            case TokenKind.GreaterEq: return CtBool.Of(a >= b);
            case TokenKind.Plus: result = a + b; break;
            case TokenKind.Minus: result = a - b; break;
            case TokenKind.Star: result = a * b; break;
            case TokenKind.Slash:
                if (b == 0) throw Error(pos, "Division by zero");
                result = a / b;
                break;
            default:
                throw Error(pos, $"'{name}' works on ints, not reals. Use round(x), floor(x), ceil(x) or trunc(x) to make an int first");
        }
        return Finite(result, pos, $"'{name}'");
    }

    private CtReal Finite(double v, SourcePos pos, string what)
    {
        if (double.IsFinite(v)) return new CtReal(v);
        if (double.IsNaN(v)) throw Error(pos, $"{what} has no real value for these numbers. Use other values");
        throw Error(pos, $"{what} gives a real too large for 64 bits. Use smaller values");
    }

    private static double Real(CtValue v) => v is CtInt i ? i.Value : ((CtReal)v).Value;

    private long Int(CtValue v, SourcePos pos, string what) => v switch
    {
        CtInt i => i.Value,
        CtReal => throw Error(pos, $"{what} is a real here, but needs an int. Use round(x), floor(x), ceil(x) or trunc(x)"),
        _ => throw Error(pos, $"{what} is {Describe(v.Type)} here, but needs an int"),
    };

    private bool Bool(CtValue v, SourcePos pos, string what) => v is CtBool b ? b.Value
        : throw Error(pos, $"{what} needs a bool, not {Describe(v.Type)}. Compare instead, e.g. 'n != 0'");

    /// <summary>The kind of a value with its article: "an int", "a real", "an array".</summary>
    public static string Describe(CtType t) => t.Kind switch
    {
        CtKind.Array => "an array",
        CtKind.Int => "an int",
        _ => $"a {t}",
    };

    private static string OpText(TokenKind op) => op switch
    {
        TokenKind.AmpAmp => "&&", TokenKind.PipePipe => "||", TokenKind.Star => "*", TokenKind.Slash => "/",
        TokenKind.Percent => "%", TokenKind.Plus => "+", TokenKind.Minus => "-", TokenKind.Amp => "&", TokenKind.Pipe => "|",
        TokenKind.Caret => "^", TokenKind.Shl => "<<", TokenKind.Shr => ">>", TokenKind.EqEq => "==", TokenKind.NotEq => "!=",
        TokenKind.Less => "<", TokenKind.LessEq => "<=", TokenKind.Greater => ">", TokenKind.GreaterEq => ">=",
        _ => op.ToString(),
    };

    /// <summary>
    /// A value where <paramref name="type"/> is expected: an int becomes a real (spec 4.1), never the
    /// other way; an array's elements are converted the same way. Arrays are copied.
    /// </summary>
    private CtValue Convert(CtValue v, CtType type, SourcePos pos, string what)
    {
        switch (type.Kind)
        {
            case CtKind.Real when v is CtInt i: return new CtReal(i.Value);
            case CtKind.Int when v is CtReal:
                throw Error(pos, $"{what} is a real, but an int is expected. Use round(x), floor(x), ceil(x) or trunc(x)");
            case CtKind.Array when v is CtArray a:
                if (a.Element.Kind == type.Element!.Kind && type.Element.Kind != CtKind.Array) return a.Copy();
                return new CtArray(a.Items.Select(x => Convert(x, type.Element, pos, $"An element of {char.ToLowerInvariant(what[0])}{what[1..]}")).ToArray(), type.Element);
        }
        if (v.Type.Kind != type.Kind)
            throw Error(pos, $"{what} is {Describe(v.Type)}, but {Describe(type)} is expected");
        return v.Copy();
    }

    // ---------- Calls ----------

    private CtValue Call(CallExpr c, bool statement)
    {
        if (!c.Name.Contains('.') && FindSlot(c.Name) is not null)
            throw Error(c.Pos, $"'{c.Name}' is a variable, not a function");
        if (_host.ConstFn(Top.Module, c.Name, c.Pos) is { } fn)
        {
            var result = CallConstFn(fn, c, statement);
            if (result is null && !statement)
                throw Error(c.Pos, $"'{fn.Name}' returns no value. Give it a return type, or call it on a line of its own");
            return result ?? CtBool.False;
        }
        switch (c.Name)
        {
            case "len":
            {
                if (c.Args.Count != 1) throw Error(c.Pos, "len() takes one array. Pass the array, e.g. len(t)");
                return Eval(c.Args[0]) is CtArray a ? new CtInt(a.Items.Length) : throw Error(c.Pos, "len() needs an array");
            }
            case "sizeof":
                if (c.Args is not [NameExpr type]) throw Error(c.Pos, "sizeof() takes one type name, e.g. sizeof(Enemy)");
                return new CtInt(_host.SizeOf(Top.Module, type.Name, type.Pos));
            case "offset":
                return new CtInt(_host.OffsetOf(Top.Module, c));
            case "addr":
            {
                // A label placed by the compiler (spec 4.1): its number is known only after placement.
                if (c.Args is not [NameExpr name])
                    throw Error(c.Pos, "addr() in a const fn takes the name of a global, data or function. Write e.g. addr(buffer)");
                if (FindSlot(name.Name) is not null)
                    throw Error(name.Pos, $"'{name.Name}' is a variable of the const fn, which has no address. Use addr() with a global, data or a function");
                var symbol = _host.AddressOf(Top.Module, name.Name, name.Pos);
                return new CtLabel(0, c.Pos) { Address = symbol, AddressName = name.Name };
            }
            case "include_bytes":
                throw Error(c.Pos, "include_bytes() can only be the value of data. Declare the data, and read it in the const fn");
        }
        if (!Builtins.Contains(c.Name)) throw Error(c.Pos, $"'{c.Name}' is not declared");
        return Builtin(c);
    }

    private CtValue Builtin(CallExpr c)
    {
        var args = c.Args.Select(Eval).ToList();
        void Count(int n)
        {
            if (args.Count != n) throw Error(c.Pos, $"{c.Name}() takes {n} argument{(n == 1 ? "" : "s")}, not {args.Count}");
        }
        double Arg(int k) => args[k] is CtInt or CtReal ? Real(args[k])
            : throw Error(c.Args[k].Pos, $"{c.Name}() needs a number, not {Describe(args[k].Type)}");
        CtReal Result(double v) => Finite(v, c.Pos, $"{c.Name}()");

        switch (c.Name)
        {
            case "label":
                Count(0);
                return new CtLabel(++_labels, c.Pos);
            case "repeat":
            {
                // repeat(value, count): count copies of value (spec 4.1).
                Count(2);
                long count = Int(args[1], c.Args[1].Pos, "The count of repeat()");
                if (count < 0 || count > 0x10000)
                    throw Error(c.Args[1].Pos, $"An array has 0 to 65536 elements, not {count}. Use a smaller count");
                var items = new CtValue[count];
                for (int k = 0; k < count; k++) items[k] = args[0].Copy();
                return new CtArray(items, args[0].Type);
            }
            case "sin": Count(1); return Result(Math.Sin(Arg(0)));
            case "cos": Count(1); return Result(Math.Cos(Arg(0)));
            case "tan": Count(1); return Result(Math.Tan(Arg(0)));
            case "exp": Count(1); return Result(Math.Exp(Arg(0)));
            case "atan2": Count(2); return Result(Math.Atan2(Arg(0), Arg(1)));
            case "pow": Count(2); return Result(Math.Pow(Arg(0), Arg(1)));
            case "sqrt":
                Count(1);
                if (Arg(0) < 0) throw Error(c.Pos, $"sqrt() of a negative number ({Show(args[0])}) has no real value. Use a number of 0 or more");
                return Result(Math.Sqrt(Arg(0)));
            case "log" or "log2":
                Count(1);
                if (Arg(0) <= 0) throw Error(c.Pos, $"{c.Name}() of {Show(args[0])} has no real value. Use a number above 0");
                return Result(c.Name == "log" ? Math.Log(Arg(0)) : Math.Log2(Arg(0)));
            case "abs":
                Count(1);
                if (args[0] is CtInt ai) return new CtInt(ai.Value < 0 ? Checker.ConstNegate(c.Pos, ai.Value) : ai.Value);
                return Result(Math.Abs(Arg(0)));
            case "min" or "max":
            {
                if (args.Count < 2) throw Error(c.Pos, $"{c.Name}() takes two or more numbers");
                for (int k = 0; k < args.Count; k++) Arg(k);
                if (args.All(a => a is CtInt))
                {
                    var ints = args.Select(a => ((CtInt)a).Value);
                    return new CtInt(c.Name == "min" ? ints.Min() : ints.Max());
                }
                var reals = args.Select(Real);
                return new CtReal(c.Name == "min" ? reals.Min() : reals.Max());
            }
            default:
            {
                // round, floor, ceil, trunc: the only ways from a real to an int (spec 4.1).
                Count(1);
                if (args[0] is CtInt same) return same;
                double x = Arg(0);
                double r = c.Name switch
                {
                    "round" => Math.Round(x, MidpointRounding.AwayFromZero),
                    "floor" => Math.Floor(x),
                    "ceil" => Math.Ceiling(x),
                    _ => Math.Truncate(x),
                };
                if (r < -9.2233720368547758E18 || r >= 9.2233720368547758E18)
                    throw Error(c.Pos, $"{c.Name}() gives {Show(args[0])}, outside the signed 64-bit range of int. Use a smaller value");
                return new CtInt((long)r);
            }
        }
    }

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

    private static string Show(CtValue v) => v is CtReal r ? r.Value.ToString("G", CultureInfo.InvariantCulture) : v.ToString()!;

    /// <summary>Runs a const fn; null when it returns nothing.</summary>
    private CtValue? CallConstFn(ConstFnSymbol fn, CallExpr c, bool statement)
    {
        if (fn.Emits && !statement)
            throw Error(c.Pos, $"'{fn.Name}' emits assembly, so it returns no value. Call it on a line of its own in an asm block");
        if (c.Args.Count != fn.Parameters.Count)
            throw Error(c.Pos, $"'{fn.Name}' takes {Count(fn.Parameters.Count, "argument")}, not {c.Args.Count}. Pass {Count(fn.Parameters.Count, "argument")}");
        var args = new List<CtValue>();
        for (int k = 0; k < c.Args.Count; k++)
            args.Add(Convert(Eval(c.Args[k]), fn.Parameters[k].Type, c.Args[k].Pos, $"The argument '{fn.Parameters[k].Name}' of '{fn.Name}'"));
        if (_frames.Count > CallDepthLimit)
            throw Error(c.Pos, $"Const fn calls are nested more than {CallDepthLimit} deep here, through '{fn.Name}'. Use a loop instead of calling '{fn.Name}' from itself");

        var frame = new Frame(fn);
        for (int k = 0; k < args.Count; k++)
            frame.Scopes[0][fn.Parameters[k].Name] = new Slot(args[k], fn.Parameters[k].Type, null);
        _frames.Add(frame);
        try
        {
            Flow flow;
            try { flow = ExecBlock(fn.Declaration.Body, newScope: false); }
            catch (CompileError e) when (_frames.Count == 2 && !e.Description.Contains("(called from "))
            {
                // An error the checker raised (a name the const fn cannot read, a constant out of range):
                // it names the line that started the evaluation too (review M22, finding 2).
                throw new CompileError(e.Pos, WithNote(e.Description, $"called from {OriginText}")) { CalledFrom = _origin };
            }
            if (fn.ReturnType is null) return null;
            if (flow != Flow.Return)
                throw Error(fn.Pos, $"'{fn.Name}' ends without returning a value. Add a 'return' at its end");
            return Convert(frame.Returned!, fn.ReturnType, fn.Declaration.ReturnType!.Pos, $"The value '{fn.Name}' returns");
        }
        finally { _frames.RemoveAt(_frames.Count - 1); }
    }

    // ---------- Statements ----------

    private Flow ExecBlock(BlockStmt block, bool newScope = true)
    {
        if (newScope) Top.Scopes.Add([]);
        try
        {
            foreach (var s in block.Statements)
            {
                var flow = Exec(s);
                if (flow != Flow.Normal) return flow;
            }
            return Flow.Normal;
        }
        finally { if (newScope) Top.Scopes.RemoveAt(Top.Scopes.Count - 1); }
    }

    private void Bind(string name, CtValue value, CtType type, string? readOnly) => Top.Scopes[^1][name] = new Slot(value, type, readOnly);

    private Flow Exec(Stmt s)
    {
        Step();
        switch (s)
        {
            case VarDecl v:
            {
                CtValue value;
                CtType type;
                if (v.TypeSyntax is not null)
                {
                    type = Types.Resolve(v.TypeSyntax);
                    if (v.Init is not null) value = Convert(Eval(v.Init), type, v.Init.Pos, $"The value of '{v.Name}'");
                    else if (v.TypeSyntax is ArrayTypeSyntax { Length: { } lengthExpr } at && type.Element!.Kind is CtKind.Int or CtKind.Real or CtKind.Bool)
                    {
                        long n = Int(Eval(lengthExpr), lengthExpr.Pos, "An array's length");
                        if (n < 0 || n > 0x10000) throw Error(lengthExpr.Pos, $"An array has 0 to 65536 elements, not {n}. Use a smaller length");
                        CtValue zero = type.Element.Kind switch { CtKind.Int => new CtInt(0), CtKind.Real => new CtReal(0), _ => CtBool.False };
                        value = new CtArray(Enumerable.Repeat(zero, (int)n).ToArray(), type.Element);
                    }
                    else throw Error(v.Pos, $"'{v.Name}' needs an initial value. Write 'var {v.Name}: {type} = ...'");
                }
                else
                {
                    value = Eval(v.Init!).Copy();
                    type = value.Type;
                }
                Bind(v.Name, value, type, null);
                return Flow.Normal;
            }
            case ConstDecl c:
            {
                var value = Eval(c.Value);
                if (c.TypeSyntax is not null) value = Convert(value, Types.Resolve(c.TypeSyntax), c.Value.Pos, $"The value of '{c.Name}'");
                Bind(c.Name, value.Copy(), value.Type, "a constant");
                return Flow.Normal;
            }
            case AssignStmt a: Assign(a); return Flow.Normal;
            case ExprStmt e:
                Call((CallExpr)e.Expr, statement: true);
                return Flow.Normal;
            case BlockStmt b: return ExecBlock(b);
            case IfStmt i:
                if (Bool(Eval(i.Condition), i.Condition.Pos, "An 'if' condition")) return ExecBlock(i.Then);
                return i.Else is null ? Flow.Normal : Exec(i.Else);
            case WhileStmt w:
                while (Bool(Eval(w.Condition), w.Condition.Pos, "A 'while' condition"))
                {
                    Step();
                    var flow = ExecBlock(w.Body);
                    if (flow == Flow.Break) break;
                    if (flow == Flow.Return) return flow;
                }
                return Flow.Normal;
            case ForStmt f:
                foreach (long i in Range(f.From, f.To, f.Inclusive, f.Step, f.RangeText))
                {
                    Step();
                    Top.Scopes.Add([]);
                    if (f.Variable is not null) Bind(f.Variable, new CtInt(i), CtType.Int, "the loop variable");
                    Flow flow;
                    try { flow = ExecBlock(f.Body); }
                    finally { Top.Scopes.RemoveAt(Top.Scopes.Count - 1); }
                    if (flow == Flow.Break) break;
                    if (flow == Flow.Return) return flow;
                }
                return Flow.Normal;
            case MatchStmt m:
            {
                long subject = Int(Eval(m.Subject), m.Subject.Pos, "A match value");
                foreach (var arm in m.Arms)
                    if (arm.IsDefault || arm.Patterns.Any(p => p.High is null
                            ? Int(Eval(p.Low), p.Low.Pos, "A pattern") == subject
                            : Int(Eval(p.Low), p.Low.Pos, "A pattern") <= subject && subject <= Int(Eval(p.High), p.High.Pos, "A pattern")))
                        return ExecBlock(arm.Body);
                return Flow.Normal;
            }
            case BreakStmt: return Flow.Break;
            case ContinueStmt: return Flow.Continue;
            case ReturnStmt r:
                if (r.Value is not null) Top.Returned = Eval(r.Value);
                return Flow.Return;
            case EmitStmt e:
                Emit(e);
                return Flow.Normal;
            default:
                throw Error(s.Pos, "This statement cannot run at compile time");
        }
    }

    private void Assign(AssignStmt a)
    {
        // The variable the assignment changes, through any indexes: t[i][j] = v changes t.
        var root = a.Target;
        while (root is IndexExpr ix) root = ix.Target;
        if (root is not NameExpr name)
            throw Error(a.Target.Pos, "A const fn assigns only its own variables and their elements");
        var slot = FindSlot(name.Name);
        if (slot is null)
        {
            _host.Read(Top.Module, name.Name, name.Pos, true);
            throw Error(name.Pos, $"'{name.Name}' is not a variable of this const fn, which changes only its own variables. Declare a local copy: 'var my_{name.Name.Replace('.', '_')} = {name.Name}'");
        }
        if (slot.ReadOnly is { } why)
            throw Error(name.Pos, $"'{name.Name}' is {why}, which cannot be changed. Declare a variable with 'var' for a value that changes");

        CtValue Combine(CtValue old) => a.Op == TokenKind.Eq ? Eval(a.Value) : Binary(CompoundOp(a.Op), old, Eval(a.Value), a.Pos);

        if (a.Target is IndexExpr target)
        {
            var (array, i) = Element(target);
            array.Items[i] = Convert(Combine(array.Items[i]), array.Element, a.Value.Pos, "The value");
            return;
        }
        slot.Value = Convert(Combine(slot.Value), slot.Type, a.Value.Pos, $"The value of '{name.Name}'");
    }

    private static TokenKind CompoundOp(TokenKind op) => op switch
    {
        TokenKind.PlusEq => TokenKind.Plus, TokenKind.MinusEq => TokenKind.Minus, TokenKind.AmpEq => TokenKind.Amp,
        TokenKind.PipeEq => TokenKind.Pipe, TokenKind.CaretEq => TokenKind.Caret, TokenKind.ShlEq => TokenKind.Shl,
        _ => TokenKind.Shr,
    };

    // ---------- Generators ----------

    /// <summary>The most lines one generator call may emit: more would not fit in memory.</summary>
    public const int EmitLimit = 65536;

    /// <summary>The lines emitted so far, while a generator call runs; null for any other evaluation.</summary>
    private List<AsmLine>? _output;
    /// <summary>The .labels of the asm block, while a generator call runs.</summary>
    private HashSet<string>? _blockLabels;
    /// <summary>emit statements run, to give each one's local labels new names.</summary>
    private int _emits;
    /// <summary>Where each label() value was placed, by its id.</summary>
    private readonly Dictionary<int, SourcePos> _placed = [];
    /// <summary>Each label() value used in an operand, by its id, with the first use.</summary>
    private readonly Dictionary<int, SourcePos> _used = [];

    /// <summary>
    /// Runs a generator call written on a line of an asm block (spec 4.1) and returns the lines it
    /// emits, with their local labels renamed and their operands' compile-time parts computed.
    /// </summary>
    /// <param name="numbers">
    /// The last number given to a renamed label in this asm block: each call continues from it, so the
    /// labels of two calls in one block never clash (review M22, finding 1).
    /// </param>
    /// <param name="blockLabels">The .labels the calling asm block places, which the call line may pass.</param>
    public static List<AsmLine> Generate(CallExpr call, ICompileTimeHost host, ref int numbers, HashSet<string> blockLabels)
    {
        var interpreter = new Interpreter(host, call.Pos) { _output = [], _emits = numbers, _labels = numbers, _blockLabels = blockLabels };
        var fn = host.ConstFn(null, call.Name, call.Pos);
        if (fn is null)
            throw new CompileError(call.Pos, Builtins.Contains(call.Name) || call.Name is "len" or "sizeof" or "offset" or "addr"
                ? $"{call.Name}() is not a generator. Name a const fn that emits assembly on this line, or use the value in an operand"
                : $"'{call.Name}' is not declared");
        if (fn.ReturnType is not null)
            throw new CompileError(call.Pos, $"'{fn.Name}' returns a value, so it is not a generator, which emits assembly. Use its value in an operand, e.g. 'lda #{call.Name}(...)'");
        interpreter.CallConstFn(fn, call, statement: true);
        numbers = Math.Max(interpreter._emits, interpreter._labels);
        foreach (var (id, use) in interpreter._used)
            if (!interpreter._placed.ContainsKey(id))
                throw new CompileError(use, Note("This label() is used but never placed", call.Pos)
                    + ". Place it in an emit with its variable's name and ':', e.g. 'emit { top: }'") { CalledFrom = call.Pos };
        return interpreter._output;
    }

    private static string Note(string message, SourcePos call) => $"{message} (generated by the call at {Where(call)})";

    private void Emit(EmitStmt e)
    {
        if (_output is null)
            throw Error(e.Pos, "'emit' adds instructions to the asm block that calls the generator, but this evaluation is not a generator call. Call the generator on a line of its own in an asm block");
        if (_frames.FirstOrDefault(f => f.Function?.ReturnType is not null)?.Function is { } valued)
            throw Error(e.Pos, $"'{valued.Name}' returns a value, so the const fns it calls cannot emit assembly. Call the generator from an asm block instead");
        _emits++;
        var locals = new HashSet<string>();
        foreach (var l in e.Fragment.Lines.OfType<AsmLabel>().Where(l => l.Name.StartsWith('.')))
            if (!locals.Add(l.Name)) throw Error(l.Pos, $"Label '{l.Name}' is defined twice in this emit. Rename one");
        foreach (var line in e.Fragment.Lines)
        {
            switch (line)
            {
                case AsmLabel l when l.Name.StartsWith('.'):
                    _output.Add(new AsmLabel(l.Pos, $"{l.Name}#{_emits}") { CalledFrom = _origin });
                    break;
                case AsmLabel l:
                {
                    // name: places the label() value of a variable.
                    var slot = FindSlot(l.Name);
                    if (slot?.Value is not CtLabel label)
                        throw Error(l.Pos, slot is null
                            ? $"'{l.Name}' is not a label variable. Make one with 'var {l.Name} = label()', or write a local label '.{l.Name}:'"
                            : $"'{l.Name}' is not a label() value, so it cannot be placed. Make one with 'var {l.Name} = label()'");
                    if (label.AddressName is { } of)
                        throw Error(l.Pos, $"'{l.Name}' is the address of '{of}', which is already placed. Use label() for a new label");
                    if (label.BlockLabel is not null)
                        throw Error(l.Pos, $"'{l.Name}' is placed by the calling block. Use label() for a new label");
                    if (label.Offset != 0)
                        throw Error(l.Pos, $"'{l.Name}' is a label plus an offset of {label.Offset}, which cannot be placed. Place the label itself, made with label()");
                    if (_placed.TryGetValue(label.Id, out var first))
                        throw Error(l.Pos, $"This label() is placed twice, first at {Where(first)}. Place it once");
                    _placed[label.Id] = l.Pos;
                    _output.Add(new AsmLabel(l.Pos, LabelName(label)) { CalledFrom = _origin });
                    break;
                }
                case AsmInstruction ins:
                    _output.Add(new AsmInstruction(ins.Pos, ins.Mnemonic, ins.Form, ins.Operand is null ? null : Operand(ins.Operand, locals), ins.Index)
                    {
                        CalledFrom = _origin,
                    });
                    break;
            }
            if (_output.Count > EmitLimit)
                throw Error(e.Pos, $"The generator emitted more than {EmitLimit} lines, more than fit in memory. Check its loops");
        }
    }

    private static string LabelName(CtLabel label) => $".label#{label.Id}";

    /// <summary>
    /// An operand of an emitted instruction: compile-time parts become numbers, label() values and the
    /// emit's own .labels become labels, and names of variables, data and functions stay names, bound in
    /// the generator's module (they are placed later, like any asm operand).
    /// </summary>
    private Expr Operand(Expr e, HashSet<string> locals)
    {
        switch (e)
        {
            case AsmLabelExpr l:
                if (!locals.Contains(l.Name))
                    throw Error(l.Pos, $"Label '{l.Name}' is not defined in this emit. A generator cannot use the labels of the asm block that calls it; pass a label() value instead");
                return new AsmLabelExpr(l.Pos, $"{l.Name}#{_emits}");
            case NameExpr n when FindSlot(n.Name) is null && NamesAddress(n):
                return new NameExpr(n.Pos, n.Name) { Bound = _host.Symbol(Top.Module, n.Name) };
            case BinaryExpr b when NamesAddress(b):
                return new BinaryExpr(b.Pos, b.Op, Operand(b.Left, locals), Operand(b.Right, locals));
            case UnaryExpr u when NamesAddress(u):
                return new UnaryExpr(u.Pos, u.Op, Operand(u.Operand, locals));
            case MemberExpr m when NamesAddress(m):
                return new MemberExpr(m.Pos, Operand(m.Target, locals), m.Member);
        }
        switch (Eval(e))
        {
            case CtInt i: return new NumberExpr(e.Pos, i.Value);
            case CtLabel label:
            {
                Expr name;
                if (label.Address is { } symbol) name = new NameExpr(e.Pos, label.AddressName!) { Bound = symbol };
                else if (label.BlockLabel is { } blockLabel) name = new AsmLabelExpr(e.Pos, blockLabel);
                else
                {
                    _used.TryAdd(label.Id, e.Pos);
                    name = new AsmLabelExpr(e.Pos, LabelName(label));
                }
                return label.Offset == 0 ? name : new BinaryExpr(e.Pos, TokenKind.Plus, name, new NumberExpr(e.Pos, label.Offset));
            }
            case CtReal:
                throw Error(e.Pos, "This operand is a real. Use round(x), floor(x), ceil(x) or trunc(x)");
            case var v:
                throw Error(e.Pos, $"This operand is {Describe(v.Type)}, but an instruction takes a number, an address or a label");
        }
    }

    /// <summary>
    /// Whether an operand has a part placed after evaluation: a variable, data or function named as an
    /// address, or a label() value. Data read as a table (<c>t[i]</c>, <c>len(t)</c>) is a value.
    /// </summary>
    private bool NamesAddress(Expr e) => e switch
    {
        NameExpr n => FindSlot(n.Name) is { } slot ? slot.Value is CtLabel : _host.Symbol(Top.Module, n.Name) is VariableSymbol or FunctionSymbol,
        CallExpr { Name: "addr" } => true,
        BinaryExpr b => NamesAddress(b.Left) || NamesAddress(b.Right),
        UnaryExpr u => NamesAddress(u.Operand),
        MemberExpr m => NamesAddress(m.Target),
        _ => false,
    };
}

/// <summary>The compile-time types written in a const fn (spec 4.1).</summary>
public static class Types
{
    /// <summary>
    /// <c>int</c>, <c>real</c>, <c>bool</c>, <c>label</c> or an array of them. A runtime type is an error
    /// that names <c>int</c>. An array's length is checked when it is made, not here.
    /// </summary>
    public static CtType Resolve(TypeSyntax syntax)
    {
        switch (syntax)
        {
            case NamedTypeSyntax n:
                return n.Name switch
                {
                    "int" => CtType.Int,
                    "real" => CtType.Real,
                    "bool" => CtType.Bool,
                    "label" => CtType.Label,
                    "u8" or "i8" or "u16" or "i16" => throw new CompileError(n.Pos, $"'{n.Name}' is a runtime type. Use int; the value is checked when it leaves the const fn"),
                    _ => throw new CompileError(n.Pos, $"'{n.Name}' is not a compile-time type. Use int, real, bool, label or an array of them"),
                };
            case PointerTypeSyntax p:
                throw new CompileError(p.Pos, "A pointer is a runtime type. Use int for an address; the value is checked when it leaves the const fn");
            case ArrayTypeSyntax a:
                return CtType.ArrayOf(Resolve(a.Element));
            default:
                throw new InvalidOperationException();
        }
    }
}
