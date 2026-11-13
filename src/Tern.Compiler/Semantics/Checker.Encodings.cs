using Tern.Compiler.Syntax;

namespace Tern.Compiler.Semantics;

/// <summary>An encoding the program declares (spec 2.1), a literal prefix like a built-in one.</summary>
public sealed class EncodingSymbol(string name, SourcePos pos, EncodingDecl declaration) : Symbol(name, pos)
{
    public EncodingDecl Declaration { get; } = declaration;
    /// <summary>Set once its lines and base are checked.</summary>
    public CharEncoding? Encoding { get; set; }
    public bool Resolving { get; set; }
    /// <summary>While <see cref="Resolving"/>: how many encoding lines were being checked when its check started.</summary>
    public int LinesBefore { get; set; }
    /// <summary>Its check had an error, which was reported: uses add none.</summary>
    public bool Failed { get; set; }
}

/// <summary>Encodings declared by the program and the program's default encoding (spec 2.1).</summary>
public sealed partial class Checker
{
    /// <summary>The main file, whose <c>default encoding</c> line sets the encoding of the program's unprefixed literals.</summary>
    private Module _mainModule = null!;
    private CharEncoding? _programDefault;
    /// <summary>0: the main file's default encoding is not worked out yet, 1: being worked out, 2: done, 3: failed (reported).</summary>
    private int _defaultState;
    /// <summary>While the default is worked out: how many encoding lines were being checked when it started.</summary>
    private int _defaultLinesBefore;
    /// <summary>The literal (or line) that started working out the default.</summary>
    private SourcePos _defaultUsedAt;
    /// <summary>The encoding lines whose byte is being checked, innermost last: a literal in a byte that needs its own encoding is found with them.</summary>
    private readonly List<(EncodingDecl Encoding, EncodingEntry Line)> _checkingLines = [];

    private void DeclareEncoding(EncodingDecl e)
    {
        if (CharEncoding.ByName.ContainsKey(e.Name))
            throw new CompileError(e.Pos, $"'{e.Name}' is a built-in encoding, so the prefix {e.Name}\"...\" would have two meanings. Rename the encoding");
        Declare(new EncodingSymbol(e.Name, e.Pos, e) { Module = _module }, e.Name);
    }

    /// <summary>The <c>default encoding</c> lines of a file: only the main file may have one.</summary>
    private void CheckDefaultEncodingLines(Module module)
    {
        var lines = module.Syntax.DefaultEncodings;
        if (lines.Count == 0) return;
        if (!module.IsMain)
            Recover(() => throw new CompileError(lines[0].Pos,
                $"'default encoding' sets the encoding of the whole program's text, so it goes in the main file; '{module.Name}' is a module. Move the line to the main file, or write the prefix on each literal ({lines[0].Name}\"...\")"));
        else
        {
            foreach (var extra in lines.Skip(1))
                Recover(() => throw new CompileError(extra.Pos, $"The program has one 'default encoding' line, at line {lines[0].Pos.Line}. Remove one"));
            // Worked out now, so that an error in it is reported even if no literal uses it.
            Recover(() => ProgramDefault(lines[0].Pos));
        }
    }

    /// <summary>The encoding of literals without a prefix in <paramref name="module"/>: the program's default, but never in a library module.</summary>
    /// <param name="pos">The literal, for the error when it is in a byte of the default encoding itself.</param>
    private CharEncoding DefaultEncodingFor(Module module, SourcePos pos) =>
        module.IsLibrary || _mainModule.Syntax.DefaultEncodings.Count == 0 ? _platform.DefaultEncoding : ProgramDefault(pos);

    private CharEncoding ProgramDefault(SourcePos pos)
    {
        var line = _mainModule.Syntax.DefaultEncodings[0];
        switch (_defaultState)
        {
            case 2: return _programDefault!;
            // Only an encoding's byte can use a literal while the default is worked out.
            case 1 when _checkingLines.Count > _defaultLinesBefore:
                throw ByteUsesDefault(pos);
            case 1 or 3: throw new FollowOnError();
        }
        _defaultState = 1;
        _defaultLinesBefore = _checkingLines.Count;
        _defaultUsedAt = pos;
        try
        {
            CharEncoding found = null!;
            InModule(_mainModule, () => found = NamedEncoding(line.Name, line.NamePos));
            _programDefault = found;
            _defaultState = 2;
            return found;
        }
        catch
        {
            _defaultState = 3;
            throw;
        }
    }

    /// <summary>The encoding a prefix names (<c>screen</c>, <c>tiles</c>, <c>font.tiles</c>), as seen from the file being checked.</summary>
    private CharEncoding NamedEncoding(string name, SourcePos pos)
    {
        if (CharEncoding.ByName.TryGetValue(name, out var builtIn)) return builtIn;
        int dot = name.IndexOf('.');
        Symbol? found;
        // The encodings an unknown name may have meant: the file's own, or those of the module named.
        IEnumerable<string> known = CharEncoding.ByName.Keys.Concat(_module.Scope.Values.OfType<EncodingSymbol>().Select(e => e.Name));
        if (dot > 0)
        {
            string moduleName = name[..dot], member = name[(dot + 1)..];
            if (!_module.Imports.TryGetValue(moduleName, out var other))
            {
                if (_module.Scope.GetValueOrDefault(moduleName) is ErrorSymbol) throw new FollowOnError();
                throw MissingImport(moduleName, pos);
            }
            _usedImports.Add((_module, moduleName));
            if (other.Syntax.PrivateNames.Contains(member) && other.Scope.GetValueOrDefault(member) is EncodingSymbol)
                throw new CompileError(pos, $"'{name}' is private to the module {other.Name}; only code in {other.Name} can use it. Remove 'private' there");
            found = other.Scope.GetValueOrDefault(member);
            known = other.Scope.Values.OfType<EncodingSymbol>().Where(e => !other.Syntax.PrivateNames.Contains(e.Name)).Select(e => $"{moduleName}.{e.Name}");
            if (found is null && !known.Any())
                throw new CompileError(pos, $"Unknown encoding '{name}': the module {other.Name} declares no encodings. Check the module name, or declare it in {other.Name}");
        }
        else
            found = _module.Scope.GetValueOrDefault(name);
        switch (found)
        {
            case ErrorSymbol: throw new FollowOnError();
            case EncodingSymbol e:
                Named(e, name);
                return Resolve(e, pos);
            case { } other:
                throw new CompileError(pos, $"'{name}' is a {KindName(other)}, not an encoding. Use an encoding's name before the quote");
        }
        throw new CompileError(pos, $"Unknown encoding '{name}'. Use one of: {string.Join(", ", known)}");
    }

    /// <summary>Checks a declared encoding's base and lines, once.</summary>
    /// <param name="pos">Where it is used, for the error when that is in one of its own bytes.</param>
    private CharEncoding Resolve(EncodingSymbol symbol, SourcePos pos)
    {
        if (symbol.Encoding is { } done) return done;
        if (symbol.Failed) throw new FollowOnError();
        var decl = symbol.Declaration;
        if (symbol.Resolving)
        {
            // Through a byte when a line was entered since its check started, else through bases only.
            if (_checkingLines.Count > symbol.LinesBefore)
            {
                // The default, worked out for a literal without a prefix in the innermost line, led here.
                if (_defaultState == 1 && _defaultLinesBefore == _checkingLines.Count) throw ByteUsesDefault(_defaultUsedAt);
                var (encoding, entry) = _checkingLines[^1];
                throw new CompileError(pos, encoding == decl
                    ? $"The byte of {Shown(entry)} in the encoding '{decl.Name}' uses '{decl.Name}' itself. Write the byte as a number"
                    : $"The byte of {Shown(entry)} in the encoding '{encoding.Name}' uses the encoding '{decl.Name}', which needs '{encoding.Name}'. Write the byte as a number");
            }
            throw new CompileError(decl.Pos, $"The encoding '{decl.Name}' is based on itself, through '{decl.BaseName}'. Change the base of one of them, or remove it");
        }
        symbol.Resolving = true;
        symbol.LinesBefore = _checkingLines.Count;
        try
        {
            CharEncoding result = null!;
            // On the list of declarations being worked out, so a constant that needs this encoding names it in its cycle.
            _declaring.Add((symbol.Module!, decl.Name, decl));
            try { InModule(symbol.Module!, () => result = Build(decl, symbol.Module!.Qualify(decl.Name))); }
            finally { _declaring.RemoveAt(_declaring.Count - 1); }
            symbol.Encoding = result;
            return result;
        }
        catch
        {
            symbol.Failed = true;
            throw;
        }
        finally { symbol.Resolving = false; }
    }

    private CharEncoding Build(EncodingDecl decl, string name)
    {
        var baseEncoding = decl.BaseName is { } b ? NamedEncoding(b, decl.BasePos) : null;
        var codes = new Dictionary<char, int>();
        var listedAt = new Dictionary<char, SourcePos>();
        foreach (var entry in decl.Entries)
        {
            _checkingLines.Add((decl, entry));
            try { CheckExpr(entry.Value); }
            finally { _checkingLines.RemoveAt(_checkingLines.Count - 1); }
            if (entry.Value.ConstValue is not long first)
                throw new CompileError(entry.Value.Pos, "An encoding's byte must be known at compile time. Use a number or a constant");
            if (first is < 0 or > 0xFF)
                throw new CompileError(entry.Value.Pos, $"{first} is not a byte; an encoding gives each character one byte, $00 to $FF. Use a value in that range");
            int count = entry.Last - entry.First + 1;
            if (count > 0x100)
                throw new CompileError(entry.Pos, $"The range {Shown(entry)} has {count} characters, but an encoding has 256 bytes. Split the range");
            if (first + count - 1 > 0xFF)
                throw new CompileError(entry.Pos,
                    $"The range {Shown(entry)} has {count} characters, so from ${first:X2} its bytes pass $FF. Start it at ${0x100 - count:X2} or lower, or split the range");
            for (int i = 0; i < count; i++)
            {
                char c = (char)(entry.First + i);
                if (listedAt.TryGetValue(c, out var before))
                    throw new CompileError(entry.Pos, $"{Shown(c)} is listed twice in the encoding '{decl.Name}', also at line {before.Line}. Remove one");
                listedAt[c] = entry.Pos;
                codes[c] = (int)first + i;
            }
        }
        return CharEncoding.Declared(name, baseEncoding, codes);
    }

    /// <summary>The innermost line's byte has a literal without a prefix, whose encoding, the default, needs that byte.</summary>
    private CompileError ByteUsesDefault(SourcePos pos)
    {
        var (encoding, entry) = _checkingLines[^1];
        return new CompileError(pos,
            $"The byte of {Shown(entry)} in the encoding '{encoding.Name}' uses a character without a prefix, which is in the default encoding '{_mainModule.Syntax.DefaultEncodings[0].Name}', and that needs this byte. " +
            "Write the byte as a number, or write a prefix before the quote");
    }

    private static string Shown(char c) => c == '\n' ? "'\\n'" : $"'{c}'";

    /// <summary>A line's character, or its range: <c>'A'</c>, <c>'A'..='Z'</c>.</summary>
    private static string Shown(EncodingEntry e) => e.First == e.Last ? Shown(e.First) : $"{Shown(e.First)}..={Shown(e.Last)}";
}
