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
}

public enum TokenKind
{
    Identifier, Number, Newline, Eof,
    /// <summary>Text holds the character, after escapes. <see cref="Token.Encoding"/> holds a prefix such as <c>screen</c>.</summary>
    Char,
    /// <summary>Text holds the characters, after escapes. <see cref="Token.Encoding"/> holds a prefix such as <c>screen</c>.</summary>
    String,

    // Keywords
    Const, Var, Data, Fn, Return, If, Else, While, For, In, Step, Using, Break, Continue,
    True, False, As, Zp, Struct, Match, Bank, Linear, Inline, Interrupt, Asm, Noinit, Keeps, Import, Private, Volatile, Module, Assert, Export,

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
public readonly record struct Token(TokenKind Kind, string Text, long Value, SourcePos Pos, string? Encoding = null)
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

    public static bool IsKeyword(TokenKind kind) => kind >= TokenKind.Const && kind <= TokenKind.Export;

    private static readonly Dictionary<string, TokenKind> Keywords = new()
    {
        ["const"] = TokenKind.Const, ["var"] = TokenKind.Var, ["data"] = TokenKind.Data,
        ["fn"] = TokenKind.Fn, ["return"] = TokenKind.Return, ["if"] = TokenKind.If,
        ["else"] = TokenKind.Else, ["while"] = TokenKind.While, ["for"] = TokenKind.For,
        ["in"] = TokenKind.In, ["step"] = TokenKind.Step, ["using"] = TokenKind.Using,
        ["break"] = TokenKind.Break, ["continue"] = TokenKind.Continue,
        ["true"] = TokenKind.True, ["false"] = TokenKind.False, ["as"] = TokenKind.As,
        ["zp"] = TokenKind.Zp, ["struct"] = TokenKind.Struct, ["match"] = TokenKind.Match,
        ["bank"] = TokenKind.Bank, ["linear"] = TokenKind.Linear, ["inline"] = TokenKind.Inline,
        ["interrupt"] = TokenKind.Interrupt, ["asm"] = TokenKind.Asm, ["noinit"] = TokenKind.Noinit,
        ["keeps"] = TokenKind.Keeps, ["import"] = TokenKind.Import, ["private"] = TokenKind.Private,
        ["volatile"] = TokenKind.Volatile, ["module"] = TokenKind.Module, ["assert"] = TokenKind.Assert, ["export"] = TokenKind.Export,
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
        TokenKind.Identifier or TokenKind.Number or TokenKind.Char or TokenKind.String or TokenKind.RParen or TokenKind.RBracket or
        TokenKind.RBrace or TokenKind.True or TokenKind.False or TokenKind.Return or
        TokenKind.Break or TokenKind.Continue or TokenKind.Zp;

    /// <param name="file">The source file, for positions in error messages and the listing.</param>
    public static List<Token> Tokenize(string source, string? file = null)
    {
        var tokens = new List<Token>();
        int i = 0, line = 1, lineStart = 0;

        SourcePos Pos(int at) => new(line, at - lineStart + 1, file);

        // Line breaks inside ( ) and [ ] do not end a statement, so long argument lists and tables
        // can span lines. A brace resets the count, so an unclosed bracket is reported near it.
        int nesting = 0;

        void Add(TokenKind kind, string text, long value, int start, string? encoding = null)
        {
            tokens.Add(new Token(kind, text, value, Pos(start), encoding));
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
            Add(quote == '"' ? TokenKind.String : TokenKind.Char, text, 0, start, encoding);
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
            char c = source[i];

            if (c == '\n')
            {
                if (nesting == 0 && tokens.Count > 0 && EndsStatement(tokens[^1].Kind))
                    Add(TokenKind.Newline, "\\n", 0, i);
                i++;
                line++;
                lineStart = i;
                continue;
            }
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '/' && Peek(source, i + 1) == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
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
                if (i >= source.Length) throw new CompileError(start, "Unterminated block comment");
                i += 2;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                AddQuoted(i, null);
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                string word = source[start..i];
                // A name right before a quote is an encoding prefix: screen"HELLO". The checker
                // reports unknown encodings. Keywords are never prefixes: return'A'.
                if (Peek(source, i) is '"' or '\'' && !Keywords.ContainsKey(word))
                {
                    AddQuoted(start, word);
                    continue;
                }
                Add(Keywords.TryGetValue(word, out var kw) ? kw : TokenKind.Identifier, word, 0, start);
                continue;
            }

            if (char.IsDigit(c) || (c == '$' && IsHexDigit(Peek(source, i + 1))))
            {
                int start = i;
                int radix = 10;
                if (c == '$') { radix = 16; i++; }
                else if (c == '0' && (Peek(source, i + 1) is 'b' or 'B')) { radix = 2; i += 2; }

                int digitsStart = i;
                while (i < source.Length && (IsHexDigit(source[i]) || source[i] == '_')) i++;
                string digits = source[digitsStart..i].Replace("_", "");
                if (digits.Length == 0)
                    throw new CompileError(Pos(start), "Number has no digits");
                long value = 0;
                foreach (char d in digits)
                {
                    int dv = Convert.ToInt32(d.ToString(), 16);
                    if (dv >= radix) throw new CompileError(Pos(start), $"Invalid digit '{d}' in number {source[start..i]}");
                    // Spec 2.1: up to 2^63 - 1. Checked before the step, so the sum never overflows.
                    if (value > (long.MaxValue - dv) / radix)
                        throw new CompileError(Pos(start), "Number is too large. Use a value up to $7FFFFFFFFFFFFFFF (9223372036854775807)");
                    value = value * radix + dv;
                }
                Add(TokenKind.Number, source[start..i], value, start);
                continue;
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
                    Add(kind, text, 0, i);
                    i += text.Length;
                    matched = true;
                    break;
                }
            }
            if (!matched)
                throw new CompileError(Pos(i), $"Unexpected character '{c}'");
        }

        if (tokens.Count > 0 && EndsStatement(tokens[^1].Kind))
            Add(TokenKind.Newline, "\\n", 0, i);
        tokens.Add(new Token(TokenKind.Eof, "", 0, Pos(i)));
        return tokens;
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
                throw new CompileError(pos, quote == '"' ? "String is not closed with '\"'" : "Character literal is not closed with '''");
            char c = source[i++];
            if (c == quote) return sb.ToString();
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

    private static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';
    private static bool IsHexDigit(char c) => char.IsAsciiHexDigit(c);
}
