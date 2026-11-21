using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// Checks a compile error whole (review code-quality-tests, finding 4): its line, its column and its
/// full message, fix sentence included, as <c>tern --json</c> reports them.
/// </summary>
public static class Expect
{
    /// <summary>Compiles <paramref name="source"/> for the default target and checks the error it gives.</summary>
    public static CompileError Error(string source, int line, int column, string message) =>
        Error(() => TernCompiler.Compile(source, TestTarget.Default), line, column, message);

    /// <summary>Runs <paramref name="compile"/>, which must throw a <see cref="CompileError"/>, and checks it.</summary>
    public static CompileError Error(Func<object?> compile, int line, int column, string message)
    {
        var e = Assert.Throws<CompileError>(compile);
        Message(e, line, column, message);
        return e;
    }

    /// <summary>Checks that <paramref name="e"/> is at <paramref name="line"/> and <paramref name="column"/> with <paramref name="message"/>.</summary>
    public static void Message(CompileError e, int line, int column, string message)
    {
        Assert.Equal(message, e.Description);
        Assert.Equal((line, column), (e.Pos.Line, e.Pos.Column));
    }
}
