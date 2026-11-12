namespace Tern.Compiler.Syntax;

/// <summary>
/// The errors of one compile, reported sorted by place (spec 13, decision 108): the main file first, then
/// each module in the order it is first imported; within a file by line and column, and errors at the same
/// place in the order found. The same message at the same place is kept once, since a declaration used
/// before its turn may be checked again at its turn.
/// </summary>
/// <param name="max">Report only the first this many after sorting (<c>--max-errors</c>); null for no limit.</param>
public sealed class ErrorList(int? max = null)
{
    private readonly List<CompileError> _errors = [];
    private readonly HashSet<(SourcePos, string)> _seen = [];
    /// <summary>Each file's place in the order: the main file first, then modules as they are first imported.</summary>
    private readonly Dictionary<string, int> _files = [];

    public int Count => _errors.Count;

    /// <summary>Notes a file as it is loaded, so its errors come after those of the files loaded before it.</summary>
    public void AddFile(string? path) => _files.TryAdd(path ?? "", _files.Count);

    public void Add(CompileError e)
    {
        if (_seen.Add((e.Pos, e.Description))) _errors.Add(e);
    }

    /// <summary>Throws the first error, carrying all of them in order, if there is any.</summary>
    public void ThrowIfAny()
    {
        if (_errors.Count == 0) return;
        // A file not noted (none today) goes last. OrderBy is stable: the same place keeps the order found.
        var sorted = _errors
            .OrderBy(e => _files.TryGetValue(e.Pos.File ?? "", out int i) ? i : int.MaxValue)
            .ThenBy(e => e.Pos.Line).ThenBy(e => e.Pos.Column)
            .ToList();
        bool more = max is int m && sorted.Count > m;
        if (more) sorted.RemoveRange(max!.Value, sorted.Count - max.Value);
        var first = sorted[0];
        first.All = sorted;
        first.MoreNotReported = more;
        throw first;
    }
}

/// <summary>
/// Stops checking a statement or declaration that uses a name whose declaration had an error (spec 13).
/// That error was reported already, so this one is not.
/// </summary>
public sealed class FollowOnError : Exception;
