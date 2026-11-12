namespace Tern.Compiler.Syntax;

/// <summary>
/// The errors of one compile, in the order found (spec 13). The same message at the same place is
/// kept once, since a declaration used before its turn may be checked again at its turn.
/// </summary>
/// <param name="max">Stop after this many errors (<c>--max-errors</c>); null for no limit.</param>
public sealed class ErrorList(int? max = null)
{
    private readonly List<CompileError> _errors = [];
    private readonly HashSet<(SourcePos, string)> _seen = [];

    public int Count => _errors.Count;

    /// <summary>Records an error. Throws <see cref="ErrorLimitReached"/> for one more than the limit.</summary>
    public void Add(CompileError e)
    {
        if (!_seen.Add((e.Pos, e.Description))) return;
        if (max is int m && _errors.Count >= m) throw new ErrorLimitReached();
        _errors.Add(e);
    }

    /// <summary>Throws the first error, carrying all of them, if there is any.</summary>
    /// <param name="more">There were more errors than the limit allowed.</param>
    public void ThrowIfAny(bool more = false)
    {
        if (_errors.Count == 0) return;
        var first = _errors[0];
        first.All = _errors.ToList();
        first.MoreNotReported = more;
        throw first;
    }
}

/// <summary>Thrown when an error would go past <c>--max-errors</c>: the compile stops there.</summary>
public sealed class ErrorLimitReached : Exception;

/// <summary>
/// Stops checking a statement or declaration that uses a name whose declaration had an error (spec 13).
/// That error was reported already, so this one is not.
/// </summary>
public sealed class FollowOnError : Exception;
