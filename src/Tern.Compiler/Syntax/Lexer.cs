namespace Tern.Compiler.Syntax;

/// <param name="File">The source file's path, or null for source text given without one.</param>
public readonly record struct SourcePos(int Line, int Column, string? File = null)
{
    public override string ToString() => File is null ? $"{Line}:{Column}" : $"{Path.GetFileName(File)}:{Line}:{Column}";
}

public sealed class CompileError(SourcePos pos, string message) : Exception($"{pos}: {message}")
{
    public SourcePos Pos { get; } = pos;
    public string Description { get; } = message;
    /// <summary>
    /// For an error while a const fn runs or in generated code: the line that started the evaluation or
    /// called the generator (spec 4.1), which the message names too. <c>tern --json</c> gives it as a field.
    /// </summary>
    public SourcePos? CalledFrom { get; init; }

    /// <summary>
    /// Every error of the compile, this one first (spec 13: one run reports every error it finds).
    /// Just this one for an error of a later stage, which are reported one at a time.
    /// </summary>
    public IReadOnlyList<CompileError> All { get => _all ?? [this]; internal set => _all = value; }
    private IReadOnlyList<CompileError>? _all;

    /// <summary><c>--max-errors</c> stopped the compile: more errors were not reported.</summary>
    public bool MoreNotReported { get; internal set; }

    /// <summary>
    /// What the error is about, for code that catches it to report it better where it knows more
    /// (review code-quality-semantics 17): read instead of the message's words. Null for most errors.
    /// </summary>
    public ErrorReason? Reason { get; init; }

    /// <summary>A warning (spec 13): code that does nothing. Never thrown; it does not stop the build.</summary>
    public bool IsWarning { get; init; }

    /// <summary>
    /// The errors in <see cref="All"/> and the warnings found before a later stage failed, sorted by
    /// place (spec 13). Just <see cref="All"/> when there are no warnings.
    /// </summary>
    public IReadOnlyList<CompileError> WithWarnings { get => _withWarnings ?? All; internal set => _withWarnings = value; }
    private IReadOnlyList<CompileError>? _withWarnings;
}

/// <summary>What a <see cref="CompileError"/> is about (<see cref="CompileError.Reason"/>).</summary>
public abstract record ErrorReason;

/// <summary>A name that is not declared.</summary>
public sealed record NotDeclared(string Name) : ErrorReason;

/// <summary>A constant that does not fit in the type it is given.</summary>
public sealed record DoesNotFit(long Value) : ErrorReason;

/// <summary>No program area, or no RAM for the variables, has room for what the layout places: another layout may fit.</summary>
public sealed record AreasFull : ErrorReason;

/// <summary>A fixed-address variable overlaps what the layout placed (the program, its variables, an overlay): another layout may not.</summary>
public sealed record OverlapsPlaced : ErrorReason;

public enum TokenKind
{
    Identifier, Number, Newline, Eof,
    /// <summary>Where the lexer found an error and skipped the rest of the line (spec 13): no rule accepts it.</summary>
    Error,
    /// <summary>A real literal such as <c>1.5</c> or <c>2e3</c> (spec 4.1): Text holds its digits.</summary>
    Real,
    /// <summary>Text holds the character, after escapes. <see cref="Token.Encoding"/> holds a prefix such as <c>screen</c>.</summary>
    Char,
    /// <summary>Text holds the characters, after escapes. <see cref="Token.Encoding"/> holds a prefix such as <c>screen</c>.</summary>
    String,

    // Keywords
    Const, Var, Data, Fn, Return, If, Else, While, For, In, Using, Break, Continue,
    True, False, As, Zp, Struct, Match, Linear, Inline, Interrupt, Asm, Noinit, Keeps, Import, Private, Volatile, Module, Assert, Export, Implements,

    // Punctuation and operators
    LParen, RParen, LBracket, RBracket, LBrace, RBrace, Comma, Colon, Semicolon,
    Dot, DotDot, DotDotEq, Arrow, FatArrow, At,
    /// <summary><c>#</c>, only used in assembly blocks (immediate operands).</summary>
    Hash,
    Plus, Minus, Star, Slash, Percent, Amp, Pipe, Caret, Tilde, Bang,
    Shl, Shr, AmpAmp, PipePipe,
    Eq, EqEq, NotEq, Less, LessEq, Greater, GreaterEq,
    PlusEq, MinusEq, AmpEq, PipeEq, CaretEq, ShlEq, ShrEq,
}

/// <param name="Encoding">The encoding prefix of a char or string literal (<c>screen"..."</c>), or null.</param>
/// <param name="Start">Where the token starts in the source text, as an offset; with a prefix, at the prefix.</param>
/// <param name="End">Where it ends: the offset after its last character (after the closing quote of a literal).</param>
/// <param name="Doc">The <c>///</c> comment lines right before the token, joined with line breaks, or null (for doc comments, M28).</param>
public readonly record struct Token(TokenKind Kind, string Text, long Value, SourcePos Pos, string? Encoding = null, int Start = 0, int End = 0, string? Doc = null)
{
    public override string ToString() => Kind == TokenKind.Identifier || Kind == TokenKind.Number ? Text : Kind.ToString();
}

public static class Lexer
{
    /// <summary>
    /// An escape such as <c>\$93</c> or <c>\0</c> stands for a byte that is not encoded. In the text
    /// of a char or string token it is stored as a character from this private-use range.
    /// </summary>
    public const char RawByteBase = '\uE000';

    public static bool IsRawByte(char c) => c >= RawByteBase && c <= RawByteBase + 0xFF;

    public static bool IsKeyword(TokenKind kind) => kind >= TokenKind.Const && kind <= TokenKind.Implements;

    private static readonly Dictionary<string, TokenKind> Keywords = new()
    {
        ["const"] = TokenKind.Const, ["var"] = TokenKind.Var, ["data"] = TokenKind.Data,
        ["fn"] = TokenKind.Fn, ["return"] = TokenKind.Return, ["if"] = TokenKind.If,
        ["else"] = TokenKind.Else, ["while"] = TokenKind.While, ["for"] = TokenKind.For,
        ["in"] = TokenKind.In, ["using"] = TokenKind.Using,
        ["break"] = TokenKind.Break, ["continue"] = TokenKind.Continue,
        ["true"] = TokenKind.True, ["false"] = TokenKind.False, ["as"] = TokenKind.As,
        ["zp"] = TokenKind.Zp, ["struct"] = TokenKind.Struct, ["match"] = TokenKind.Match,
        ["linear"] = TokenKind.Linear, ["inline"] = TokenKind.Inline,
        ["interrupt"] = TokenKind.Interrupt, ["asm"] = TokenKind.Asm, ["noinit"] = TokenKind.Noinit,
        ["keeps"] = TokenKind.Keeps, ["import"] = TokenKind.Import, ["private"] = TokenKind.Private,
        ["volatile"] = TokenKind.Volatile, ["module"] = TokenKind.Module, ["assert"] = TokenKind.Assert, ["export"] = TokenKind.Export,
        ["implements"] = TokenKind.Implements,
    };

    // Longest operators first, so "<<=" wins over "<<" and "<".
    private static readonly (string Text, TokenKind Kind)[] Operators =
    [
        ("<<=", TokenKind.ShlEq), (">>=", TokenKind.ShrEq), ("..=", TokenKind.DotDotEq),
        ("..", TokenKind.DotDot), ("->", TokenKind.Arrow), ("=>", TokenKind.FatArrow),
        ("<<", TokenKind.Shl), (">>", TokenKind.Shr), ("&&", TokenKind.AmpAmp), ("||", TokenKind.PipePipe),
        ("==", TokenKind.EqEq), ("!=", TokenKind.NotEq), ("<=", TokenKind.LessEq), (">=", TokenKind.GreaterEq),
        ("+=", TokenKind.PlusEq), ("-=", TokenKind.MinusEq), ("&=", TokenKind.AmpEq),
        ("|=", TokenKind.PipeEq), ("^=", TokenKind.CaretEq),
        ("(", TokenKind.LParen), (")", TokenKind.RParen), ("[", TokenKind.LBracket), ("]", TokenKind.RBracket),
        ("{", TokenKind.LBrace), ("}", TokenKind.RBrace), (",", TokenKind.Comma), (":", TokenKind.Colon),
        (";", TokenKind.Semicolon), (".", TokenKind.Dot), ("@", TokenKind.At), ("#", TokenKind.Hash),
        ("+", TokenKind.Plus), ("-", TokenKind.Minus), ("*", TokenKind.Star), ("/", TokenKind.Slash),
        ("%", TokenKind.Percent), ("&", TokenKind.Amp), ("|", TokenKind.Pipe), ("^", TokenKind.Caret),
        ("~", TokenKind.Tilde), ("!", TokenKind.Bang), ("=", TokenKind.Eq),
        ("<", TokenKind.Less), (">", TokenKind.Greater),
    ];

    /// <summary>Tokens after which a line break ends the statement.</summary>
    private static bool EndsStatement(TokenKind kind) => kind is
        TokenKind.Identifier or TokenKind.Number or TokenKind.Real or TokenKind.Char or TokenKind.String or TokenKind.RParen or TokenKind.RBracket or
        TokenKind.RBrace or TokenKind.True or TokenKind.False or TokenKind.Return or
        TokenKind.Break or TokenKind.Continue or TokenKind.Zp or TokenKind.Error;

    /// <param name="file">The source file, for positions in error messages and the listing.</param>
    /// <param name="report">
    /// Takes each error, after which the rest of its line is skipped (spec 13). Null: the first error is thrown.
    /// </param>
    public static List<Token> Tokenize(string source, string? file = null, Action<CompileError>? report = null)
    {
        var tokens = new List<Token>();
        int i = 0, line = 1, lineStart = 0;

        SourcePos Pos(int at) => new(line, at - lineStart + 1, file);

        // Line breaks inside ( ) and [ ] do not end a statement, so long argument lists and tables
        // can span lines. A brace resets the count, so an unclosed bracket is reported near it.
        int nesting = 0;

        // The /// lines read since the last token, for the next one.
        string? doc = null;

        // A token from start to end (end: after its last character).
        void Add(TokenKind kind, string text, long value, int start, int end, string? encoding = null)
        {
            tokens.Add(new Token(kind, text, value, Pos(start), encoding, start, end, kind == TokenKind.Newline ? null : doc));
            if (kind != TokenKind.Newline) doc = null;
            if (kind is TokenKind.LParen or TokenKind.LBracket) nesting++;
            else if (kind is TokenKind.RParen or TokenKind.RBracket) nesting = Math.Max(0, nesting - 1);
            else if (kind is TokenKind.LBrace or TokenKind.RBrace) nesting = 0;
        }

        // A char or string literal whose quote is at i. Its encoding prefix, if any, started at start.
        void AddQuoted(int start, string? encoding)
        {
            char quote = source[i];
            string text = ReadQuoted(source, ref i, Pos);
            if (quote == '\'' && text.Length != 1)
                throw new CompileError(Pos(start), "A character literal holds exactly one character; use \"...\" for a string");
            Add(quote == '"' ? TokenKind.String : TokenKind.Char, text, 0, start, i, encoding);
        }

        // The source text of the operand before i on this line, such as n, a[i] or p.x: names joined by
        // dots, each with any [ ] after it. Null if there is none.
        string? OperandText()
        {
            int k = tokens.Count - 1, start = -1;
            while (k >= 0)
            {
                if (tokens[k].Kind == TokenKind.RBracket)
                {
                    int depth = 0;
                    for (; k >= 0; k--)
                    {
                        if (tokens[k].Kind == TokenKind.RBracket) depth++;
                        else if (tokens[k].Kind == TokenKind.LBracket && --depth == 0) break;
                    }
                    k--;
                    continue;
                }
                if (k < 0 || tokens[k].Kind != TokenKind.Identifier) break;
                start = k--;
                if (k >= 0 && tokens[k].Kind == TokenKind.Dot) { k--; continue; }
                break;
            }
            if (start < 0 || tokens[start].Pos.Line != line) return null;
            return source[(lineStart + tokens[start].Pos.Column - 1)..i].Trim();
        }

        while (i < source.Length)
        {
            try { Next(); }
            catch (CompileError e) when (report is not null)
            {
                report(e);
                // The token starts where the error is (for a language server's error range), on this line.
                int at = e.Pos.Line == line ? lineStart + e.Pos.Column - 1 : i;
                while (i < source.Length && source[i] != '\n') i++;
                // The parser drops the statement with this token, and its own errors there.
                tokens.Add(new Token(TokenKind.Error, "", 0, e.Pos, Start: at, End: i));
                nesting = 0;
            }
        }

        if (tokens.Count > 0 && EndsStatement(tokens[^1].Kind))
            Add(TokenKind.Newline, "\\n", 0, i, i);
        tokens.Add(new Token(TokenKind.Eof, "", 0, Pos(i), Start: i, End: i));
        return tokens;

        // Reads one token, or skips a line break, spaces or a comment.
        void Next()
        {
            char c = source[i];

            if (c == '\n')
            {
                if (nesting == 0 && tokens.Count > 0 && EndsStatement(tokens[^1].Kind))
                    Add(TokenKind.Newline, "\\n", 0, i, i + 1);
                i++;
                line++;
                lineStart = i;
                return;
            }
            if (char.IsWhiteSpace(c)) { i++; return; }

            if (c == '/' && Peek(source, i + 1) == '/')
            {
                int start = i;
                while (i < source.Length && source[i] != '\n') i++;
                // A doc comment: /// but not ////. Its text, without one space after the slashes, goes to the next token.
                if (string.CompareOrdinal(source, start, "///", 0, 3) == 0 && Peek(source, start + 3) != '/')
                {
                    var text = source[(start + 3)..i].TrimEnd('\r');
                    if (text.StartsWith(' ')) text = text[1..];
                    doc = doc is null ? text : doc + "\n" + text;
                }
                return;
            }
            if (c == '/' && Peek(source, i + 1) == '*')
            {
                var start = Pos(i);
                i += 2;
                while (i < source.Length && !(source[i] == '*' && Peek(source, i + 1) == '/'))
                {
                    if (source[i] == '\n') { line++; lineStart = i + 1; }
                    i++;
                }
                if (i >= source.Length) throw new CompileError(start, "Unterminated block comment. Add '*/' where the comment ends");
                // A line break inside the comment ends the statement, as one outside it does (spec 2).
                if (line > start.Line && nesting == 0 && tokens.Count > 0 && EndsStatement(tokens[^1].Kind))
                    Add(TokenKind.Newline, "\\n", 0, i, i);
                i += 2;
                return;
            }

            if (c == '"' || c == '\'')
            {
                AddQuoted(i, null);
                return;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                int start = i;
                while (i < source.Length && (char.IsAsciiLetterOrDigit(source[i]) || source[i] == '_')) i++;
                if (i < source.Length && char.IsLetterOrDigit(source[i])) throw NotAscii(i);
                string word = source[start..i];
                // A name right before a quote is an encoding prefix: screen"HELLO". The checker
                // reports unknown encodings. Keywords are never prefixes: return'A'.
                if (Peek(source, i) is '"' or '\'' && !Keywords.ContainsKey(word))
                {
                    AddQuoted(start, word);
                    return;
                }
                // A module's encoding: font.tiles"HI" (spec 2.1).
                if (Peek(source, i) == '.' && (char.IsAsciiLetter(Peek(source, i + 1)) || Peek(source, i + 1) == '_') && !Keywords.ContainsKey(word))
                {
                    int end = i + 1;
                    while (end < source.Length && (char.IsAsciiLetterOrDigit(source[end]) || source[end] == '_')) end++;
                    string member = source[(i + 1)..end];
                    if (Peek(source, end) is '"' or '\'' && !Keywords.ContainsKey(member))
                    {
                        i = end;
                        AddQuoted(start, $"{word}.{member}");
                        return;
                    }
                }
                Add(Keywords.TryGetValue(word, out var kw) ? kw : TokenKind.Identifier, word, 0, start, i);
                return;
            }

            // Spec 2: names are ASCII only, since they go into the emulators' label files.
            if (!char.IsAscii(c) && char.IsLetterOrDigit(c)) throw NotAscii(i);

            // C's 0x10 (spec 13).
            if (c == '0' && Peek(source, i + 1) is 'x' or 'X' && IsHexDigit(Peek(source, i + 2)))
            {
                int end = i + 2;
                while (end < source.Length && (IsHexDigit(source[end]) || source[end] == '_')) end++;
                throw new CompileError(Pos(i), $"Tern has no '0x'. Write hex numbers with '$', as in ${source[(i + 2)..end]}");
            }

            if (char.IsAsciiDigit(c) || (c == '$' && IsHexDigit(Peek(source, i + 1))))
            {
                int start = i;
                int radix = 10;
                if (c == '$') { radix = 16; i++; }
                else if (c == '0' && (Peek(source, i + 1) is 'b' or 'B')) { radix = 2; i += 2; }

                int digitsStart = i;
                // A real literal (spec 4.1): digits with a '.' between digits, or an exponent. 0..25 stays a range.
                if (radix == 10 && RealLength(source, i) is int realEnd)
                {
                    string text = source[start..realEnd];
                    double real = double.Parse(text.Replace("_", ""), System.Globalization.CultureInfo.InvariantCulture);
                    if (double.IsInfinity(real))
                        throw new CompileError(Pos(start), $"The real number {text} is too large. Use a value below 1.7e308");
                    i = realEnd;
                    Add(TokenKind.Real, text, 0, start, i);
                    return;
                }
                while (i < source.Length && (IsHexDigit(source[i]) || source[i] == '_')) i++;
                string digits = source[digitsStart..i].Replace("_", "");
                if (digits.Length == 0)
                    throw new CompileError(Pos(start), "Number has no digits. Write digits after '$' or '0b', e.g. $FF or 0b1010");
                long value = 0;
                foreach (char d in digits)
                {
                    int dv = Convert.ToInt32(d.ToString(), 16);
                    if (dv >= radix) throw new CompileError(Pos(start), $"Invalid digit '{d}' in number {source[start..i]}. Use 0-9 in a decimal number, 0-9 and A-F after '$', 0 and 1 after '0b'");
                    // Spec 2.1: up to 2^63 - 1. Checked before the step, so the sum never overflows.
                    if (value > (long.MaxValue - dv) / radix)
                        throw new CompileError(Pos(start), "Number is too large. Use a value up to $7FFFFFFFFFFFFFFF (9223372036854775807)");
                    value = value * radix + dv;
                }
                Add(TokenKind.Number, source[start..i], value, start, i);
                return;
            }

            // C's n++ / n-- at the end of a statement or before a closing bracket (spec 13). Elsewhere
            // they stay two operators: a++b is a + (+b).
            if (c is '+' or '-' && Peek(source, i + 1) == c && EndsAfter(source, i + 2))
            {
                string op = c == '+' ? "+=" : "-=";
                throw new CompileError(Pos(i), OperandText() is { } target
                    ? $"Tern has no '{c}{c}'. Write '{target} {op} 1'"
                    : $"Tern has no '{c}{c}'. Write '{op} 1' instead");
            }

            bool matched = false;
            foreach (var (text, kind) in Operators)
            {
                if (string.CompareOrdinal(source, i, text, 0, text.Length) == 0)
                {
                    Add(kind, text, 0, i, i + text.Length);
                    i += text.Length;
                    matched = true;
                    break;
                }
            }
            if (!matched)
                throw new CompileError(Pos(i), $"Unexpected character '{c}'. Remove it, or put it in a string or a comment");
        }

        CompileError NotAscii(int at) =>
            new(Pos(at), $"'{source[at]}' (U+{(int)source[at]:X4}) cannot be in a name: names use A-Z, a-z, 0-9 and '_'. Write the name with these");
    }

    /// <summary>Reads a quoted char or string literal starting at the quote, and decodes its escapes.</summary>
    /// <param name="posAt">Converts an index in <paramref name="source"/> to a source position.</param>
    private static string ReadQuoted(string source, ref int i, Func<int, SourcePos> posAt)
    {
        var pos = posAt(i);
        char quote = source[i++];
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            if (i >= source.Length || source[i] == '\n')
                throw new CompileError(pos, quote == '"' ? "String is not closed with '\"'. Add '\"' at its end, on the same line" : "Character literal is not closed with '''. Add ''' after the character");
            char c = source[i++];
            if (c == quote) return sb.ToString();
            // The compiler keeps \$NN bytes as these characters, so one in the source would become a raw byte.
            if (IsRawByte(c))
                throw new CompileError(posAt(i - 1), $"The character U+{(int)c:X4} has no code in any encoding. Write the byte it stands for as \\$NN");
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            var escapePos = posAt(i - 1);
            char e = Peek(source, i++);
            switch (e)
            {
                case '0': sb.Append(RawByteBase); break;
                case 'n': sb.Append('\n'); break;
                case '\\' or '"' or '\'': sb.Append(e); break;
                case '$' when IsHexDigit(Peek(source, i)) && IsHexDigit(Peek(source, i + 1)):
                    sb.Append((char)(RawByteBase + Convert.ToInt32(source.Substring(i, 2), 16)));
                    i += 2;
                    break;
                default:
                    throw new CompileError(escapePos, $"Unknown escape '\\{e}'. Use \\0, \\n, \\\\, \\\", \\' or \\$NN for a byte");
            }
        }
    }

    /// <summary>True if only spaces stand between <paramref name="i"/> and the end of the line, a comment, a closing bracket or ';'.</summary>
    private static bool EndsAfter(string s, int i)
    {
        while (i < s.Length && s[i] is ' ' or '\t' or '\r') i++;
        return i >= s.Length || s[i] is '\n' or ';' or ')' or ']' or '}' || (s[i] == '/' && Peek(s, i + 1) is '/' or '*');
    }

    /// <summary>
    /// The end of a real literal whose digits start at <paramref name="i"/>: digits, then '.' and digits,
    /// an exponent (<c>e3</c>, <c>E-2</c>), or both. Null if there is neither.
    /// </summary>
    private static int? RealLength(string s, int i)
    {
        static bool Digit(char c) => c is >= '0' and <= '9';
        while (Digit(Peek(s, i)) || Peek(s, i) == '_') i++;
        bool real = false;
        if (Peek(s, i) == '.' && Digit(Peek(s, i + 1)))
        {
            real = true;
            i++;
            while (Digit(Peek(s, i)) || Peek(s, i) == '_') i++;
        }
        if (Peek(s, i) is 'e' or 'E')
        {
            int j = i + 1;
            if (Peek(s, j) is '+' or '-') j++;
            if (Digit(Peek(s, j)))
            {
                real = true;
                i = j;
                while (Digit(Peek(s, i))) i++;
            }
        }
        return real ? i : null;
    }

    private static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';
    private static bool IsHexDigit(char c) => char.IsAsciiHexDigit(c);
}
