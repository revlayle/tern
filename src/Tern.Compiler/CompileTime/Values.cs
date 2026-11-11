using System.Globalization;
using Tern.Compiler.Semantics;
using Tern.Compiler.Syntax;

namespace Tern.Compiler.CompileTime;

/// <summary>The kinds of compile-time values (spec 4.1). None of them exists at run time.</summary>
public enum CtKind { Int, Real, Bool, Label, Array }

/// <summary>A compile-time type: <c>int</c>, <c>real</c>, <c>bool</c>, <c>label</c>, or an array of one of them.</summary>
public sealed class CtType
{
    public static readonly CtType Int = new(CtKind.Int), Real = new(CtKind.Real), Bool = new(CtKind.Bool), Label = new(CtKind.Label);

    public CtKind Kind { get; }
    /// <summary>For an array: the elements' type.</summary>
    public CtType? Element { get; }

    private CtType(CtKind kind, CtType? element = null) => (Kind, Element) = (kind, element);

    public static CtType ArrayOf(CtType element) => new(CtKind.Array, element);

    public override string ToString() => Kind == CtKind.Array ? $"[{Element}; _]" : Kind.ToString().ToLowerInvariant();
}

/// <summary>A value computed at compile time.</summary>
public abstract class CtValue
{
    public abstract CtType Type { get; }
    /// <summary>A copy that shares nothing that can change: arrays are values (spec 4.1).</summary>
    public virtual CtValue Copy() => this;
}

public sealed class CtInt(long value) : CtValue
{
    public long Value { get; } = value;
    public override CtType Type => CtType.Int;
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public sealed class CtReal(double value) : CtValue
{
    public double Value { get; } = value;
    public override CtType Type => CtType.Real;
    public override string ToString() => Value.ToString("R", CultureInfo.InvariantCulture);
}

public sealed class CtBool : CtValue
{
    public static readonly CtBool True = new(true), False = new(false);
    public bool Value { get; }
    private CtBool(bool value) => Value = value;
    public static CtBool Of(bool value) => value ? True : False;
    public override CtType Type => CtType.Bool;
    public override string ToString() => Value ? "true" : "false";
}

/// <summary>A label made by <c>label()</c> in a generator (spec 4.1), placed by <c>emit { name: }</c>.</summary>
public sealed class CtLabel(int id, SourcePos made) : CtValue
{
    public int Id { get; } = id;
    /// <summary>Where <c>label()</c> made it.</summary>
    public SourcePos Made { get; } = made;
    public override CtType Type => CtType.Label;
    public override string ToString() => $"label {Id}";
}

/// <summary>An array of fixed length (spec 4.1): its elements all have <see cref="Element"/>'s type.</summary>
public sealed class CtArray(CtValue[] items, CtType element) : CtValue
{
    public CtValue[] Items { get; } = items;
    public CtType Element { get; } = element;
    public override CtType Type => CtType.ArrayOf(Element);
    public override CtValue Copy() => new CtArray(Items.Select(i => i.Copy()).ToArray(), Element);
    public override string ToString() => $"an array of {Items.Length}";
}

/// <summary>What the interpreter needs from the checker: the program's names, outside the const fn's own variables.</summary>
public interface ICompileTimeHost
{
    /// <summary>
    /// The value of a top-level name in <paramref name="module"/> (null: where the evaluation started): a
    /// constant, a module parameter or a data table. Null if there is no such name; an error for a name
    /// compile-time code cannot read.
    /// </summary>
    /// <param name="inConstFn">The name is read in a const fn's body, for the wording of errors.</param>
    CtValue? Read(Module? module, string name, SourcePos pos, bool inConstFn);
    /// <summary>The const fn named, or null if there is none; an error for a function that runs on the 6502.</summary>
    ConstFnSymbol? ConstFn(Module? module, string name, SourcePos pos);
    long SizeOf(Module? module, string type, SourcePos pos);
    /// <summary>The symbol a name in an emitted instruction names in <paramref name="module"/>: a variable, data or function.</summary>
    Symbol? Symbol(Module? module, string name);
    long Encode(Module? module, CharExpr c);
}
