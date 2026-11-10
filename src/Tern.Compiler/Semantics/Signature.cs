namespace Tern.Compiler.Semantics;

/// <summary>A function's parameters and return as written in a declaration: <c>n: u8 using A</c>, <c> -> bool using carry</c>.</summary>
public static class Signature
{
    public static string Parameters(FunctionSymbol f, Func<TernType, string>? typeText = null) =>
        string.Join(", ", f.Parameters.Select(p => $"{p.Name}: {(typeText ?? Text)(p.Type)}" + (p.PassedIn is { } r ? $" using {RegisterText(r)}" : "")));

    public static string Return(FunctionSymbol f, Func<TernType, string>? typeText = null) =>
        f.ReturnType.Kind == TypeKind.Void ? "" : $" -> {(typeText ?? Text)(f.ReturnType)} using {RegisterText(f.ReturnRegister!.Value)}";

    /// <summary>The same types in the same registers, and the same return; parameter names may differ.</summary>
    public static bool Matches(FunctionSymbol f, FunctionSymbol g) =>
        f.Parameters.Count == g.Parameters.Count
        && f.Parameters.Zip(g.Parameters).All(p => p.First.PassedIn == p.Second.PassedIn && p.First.Type.SameAs(p.Second.Type))
        && f.ReturnType.Kind == g.ReturnType.Kind
        && (f.ReturnType.Kind == TypeKind.Void || f.ReturnType.SameAs(g.ReturnType) && f.ReturnRegister == g.ReturnRegister);

    private static string Text(TernType t) => t.ToString();

    private static string RegisterText(Register r) => r switch
    {
        Register.Carry => "carry", Register.Zero => "zero", Register.Negative => "negative", Register.Overflow => "overflow",
        var other => other.ToString(),
    };
}
