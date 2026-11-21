namespace Tern.Compiler.Tests;

/// <summary>
/// A fact that runs only when an environment variable is "1", such as <c>TERN_COMPARE</c>; otherwise it is
/// reported as skipped, with the reason, instead of passing without doing anything (review code-quality-tests 14).
/// </summary>
public sealed class FactWhenAttribute : FactAttribute
{
    public FactWhenAttribute(string variable, string what)
    {
        if (Environment.GetEnvironmentVariable(variable) != "1") Skip = $"Runs with {variable}=1: {what}";
    }
}
