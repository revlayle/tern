namespace Tern.Compiler.Syntax;

public readonly record struct SourcePos(int Line, int Column)
{
    public override string ToString() => $"{Line}:{Column}";
}

public sealed class CompileError(SourcePos pos, string message) : Exception($"{pos}: {message}")
{
    public SourcePos Pos { get; } = pos;
    public string Description { get; } = message;
}

public enum TokenKind
{
    Identifier, Number, Newline, Eof,

    // Keywords
    Const, Var, Data, Fn, Return, If, Else, While, For, In, Step, Using, Break, Continue,
    True, False, As, Zp, Struct, Match, Bank, Linear, Inline, Interrupt, Asm, Noinit,

    // Punctuation and operators
    LParen, RParen, LBracket, RBracket, LBrace, RBrace, Comma, Colon, Semicolon,
    Dot, DotDot, DotDotEq, Arrow, FatArrow, At,
    Plus, Minus, Star, Slash, Percent, Amp, Pipe, Caret, Tilde, Bang,
    Shl, Shr, AmpAmp, PipePipe,
    Eq, EqEq, NotEq, Less, LessEq, Greater, GreaterEq,
    PlusEq, MinusEq, AmpEq, PipeEq, CaretEq, ShlEq, ShrEq,
}

public readonly record struct Token(TokenKind Kind, string Text, long Value, SourcePos Pos)
{
    public override string ToString() => Kind == TokenKind.Identifier || Kind == TokenKind.Number ? Text : Kind.ToString();
}

public static class Lexer
{
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
        (";", TokenKind.Semicolon), (".", TokenKind.Dot), ("@", TokenKind.At),
        ("+", TokenKind.Plus), ("-", TokenKind.Minus), ("*", TokenKind.Star), ("/", TokenKind.Slash),
        ("%", TokenKind.Percent), ("&", TokenKind.Amp), ("|", TokenKind.Pipe), ("^", TokenKind.Caret),
        ("~", TokenKind.Tilde), ("!", TokenKind.Bang), ("=", TokenKind.Eq),
        ("<", TokenKind.Less), (">", TokenKind.Greater),
    ];

    /// <summary>Tokens after which a line break ends the statement.</summary>
    private static bool EndsStatement(TokenKind kind) => kind is
        TokenKind.Identifier or TokenKind.Number or TokenKind.RParen or TokenKind.RBracket or
        TokenKind.RBrace or TokenKind.True or TokenKind.False or TokenKind.Return or
        TokenKind.Break or TokenKind.Continue or TokenKind.Zp;

    public static List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        int i = 0, line = 1, lineStart = 0;

        SourcePos Pos(int at) => new(line, at - lineStart + 1);

        void Add(TokenKind kind, string text, long value, int start) => tokens.Add(new Token(kind, text, value, Pos(start)));

        while (i < source.Length)
        {
            char c = source[i];

            if (c == '\n')
            {
                if (tokens.Count > 0 && EndsStatement(tokens[^1].Kind))
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

            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                string word = source[start..i];
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
                    value = value * radix + dv;
                    if (value > 0xFFFF_FFFF) throw new CompileError(Pos(start), "Number is too large");
                }
                Add(TokenKind.Number, source[start..i], value, start);
                continue;
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

    private static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';
    private static bool IsHexDigit(char c) => char.IsAsciiHexDigit(c);
}
