using Tern.Compiler.Syntax;

namespace Tern.Compiler.Semantics;

/// <summary>An encoding the program declares (spec 2.1), a literal prefix like a built-in one.</summary>
public sealed class EncodingSymbol(string name, SourcePos pos, EncodingDecl declaration) : Symbol(name, pos)
{
    public EncodingDecl Declaration { get; } = declaration;
    /// <summary>Set once its lines and base are checked.</summary>
    public CharEncoding? Encoding { get; set; }
    public bool Resolving { get; set; }
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
            Recover(() => ProgramDefault());
        }
    }

    /// <summary>The encoding of literals without a prefix in <paramref name="module"/>: the program's default, but never in a library module.</summary>
    private CharEncoding DefaultEncodingFor(Module module) =>
        module.IsLibrary || _mainModule.Syntax.DefaultEncodings.Count == 0 ? _platform.DefaultEncoding : ProgramDefault();

    private CharEncoding ProgramDefault()
    {
        switch (_defaultState)
        {
            case 2: return _programDefault!;
            case 1 or 3: throw new FollowOnError();
        }
        _defaultState = 1;
        var line = _mainModule.Syntax.DefaultEncodings[0];
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
        }
        else
            found = _module.Scope.GetValueOrDefault(name);
        switch (found)
        {
            case ErrorSymbol: throw new FollowOnError();
            case EncodingSymbol e:
                Named(e, name);
                return Resolve(e);
            case { } other:
                throw new CompileError(pos, $"'{name}' is a {KindName(other)}, not an encoding. Use an encoding's name before the quote");
        }
        var known = CharEncoding.ByName.Keys.Concat(_module.Scope.Values.OfType<EncodingSymbol>().Select(e => e.Name));
        throw new CompileError(pos, $"Unknown encoding '{name}'. Use one of: {string.Join(", ", known)}");
    }

    /// <summary>Checks a declared encoding's base and lines, once.</summary>
    private CharEncoding Resolve(EncodingSymbol symbol)
    {
        if (symbol.Encoding is { } done) return done;
        if (symbol.Failed) throw new FollowOnError();
        var decl = symbol.Declaration;
        if (symbol.Resolving)
            throw new CompileError(decl.Pos, $"The encoding '{decl.Name}' is based on itself, through '{decl.BaseName}'. Change the base of one of them, or remove it");
        symbol.Resolving = true;
        try
        {
            CharEncoding result = null!;
            InModule(symbol.Module!, () => result = Build(decl, symbol.Module!.Qualify(decl.Name)));
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
            CheckExpr(entry.Value);
            if (entry.Value.ConstValue is not long first)
                throw new CompileError(entry.Value.Pos, "An encoding's byte must be known at compile time. Use a number or a constant");
            if (first is < 0 or > 0xFF)
                throw new CompileError(entry.Value.Pos, $"{first} is not a byte; an encoding gives each character one byte, $00 to $FF. Use a value in that range");
            int count = entry.Last - entry.First + 1;
            if (first + count - 1 > 0xFF)
                throw new CompileError(entry.Pos,
                    $"The range {Shown(entry.First)}..={Shown(entry.Last)} has {count} characters, so from ${first:X2} its bytes pass $FF. Start it at ${0x100 - count:X2} or lower, or split the range");
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

        static string Shown(char c) => c == '\n' ? "'\\n'" : $"'{c}'";
    }
}
