using Tern.Compiler.Semantics;
using Tern.Cpu;

namespace Tern.Compiler.Syntax;

public sealed partial class Parser
{
    private readonly List<Token> _tokens;
    private int _pos;

    /// <summary>The source text and where each line starts in it, for quoting source in messages.</summary>
    private readonly string _source;
    private readonly List<int> _lineStarts = [0];

    private Parser(List<Token> tokens, string source)
    {
        _tokens = tokens;
        _source = source;
        for (int i = 0; i < source.Length; i++)
            if (source[i] == '\n') _lineStarts.Add(i + 1);
    }

    /// <param name="file">The source file, for positions in error messages and the listing.</param>
    public static ProgramSyntax Parse(string source, string? file = null) => new Parser(Lexer.Tokenize(source, file), source).ParseProgram();

    /// <summary>Modules imported by this file. <c>name.member</c> after one of them is a qualified name.</summary>
    private readonly HashSet<string> _modules = [];

    private Token Current => _tokens[_pos];
    private Token PeekAt(int offset) => _tokens[Math.Min(_pos + offset, _tokens.Count - 1)];
    private bool At(TokenKind kind) => Current.Kind == kind;

    private Token Advance() => _tokens[_pos++];

    /// <summary>
    /// The source of tokens <paramref name="from"/> up to <paramref name="to"/> as written, for messages:
    /// "'Z'..='A'", "a[1]..=0": the text from the first token to the start of token <paramref name="to"/>.
    /// </summary>
    private string TokenText(int from, int to)
    {
        int Offset(SourcePos pos) => _lineStarts[pos.Line - 1] + pos.Column - 1;
        // A range over two lines is quoted on one: line breaks and indentation become one space.
        return System.Text.RegularExpressions.Regex.Replace(_source[Offset(_tokens[from].Pos)..Offset(_tokens[to].Pos)].TrimEnd(), @"\s*\r?\n\s*", " ");
    }

    private bool Accept(TokenKind kind)
    {
        if (!At(kind)) return false;
        _pos++;
        return true;
    }

    private Token Expect(TokenKind kind, string what)
    {
        if (!At(kind))
        {
            // A keyword where a name is meant (fn step(), var data: u8), told by the token after it.
            if (kind == TokenKind.Identifier && Lexer.IsKeyword(Current.Kind) && PeekAt(1).Kind is
                    TokenKind.LParen or TokenKind.RParen or TokenKind.Colon or TokenKind.Eq or TokenKind.LBrace or TokenKind.Comma or TokenKind.In)
                throw new CompileError(Current.Pos, $"Expected {what}, found '{Current.Text}', which is a keyword. Use another name");
            if (LeadingOperator() is { } lead) throw lead;
            throw new CompileError(Current.Pos, $"Expected {what}, found {Describe(Current)}");
        }
        return Advance();
    }

    /// <summary>
    /// The error for a line that starts with a binary operator (spec 2), at the current token or at the
    /// first one after the current line break: the break ended the expression before it. Not for
    /// <c>+</c> and <c>-</c>, which also start a value (<c>-1 =&gt;</c> in a match), nor <c>*</c>, which has its
    /// own error (<c>*p = 1</c>). Null otherwise.
    /// </summary>
    private CompileError? LeadingOperator()
    {
        int at = _pos;
        bool afterBreak = at > 0 && _tokens[at - 1].Kind == TokenKind.Newline;
        while (at < _tokens.Count - 1 && _tokens[at].Kind == TokenKind.Newline) { at++; afterBreak = true; }
        var t = _tokens[at];
        if (!afterBreak || t.Kind is not (TokenKind.AmpAmp or TokenKind.PipePipe or TokenKind.Amp or TokenKind.Pipe or TokenKind.Caret
                or TokenKind.Shl or TokenKind.Shr or TokenKind.Slash or TokenKind.Percent or TokenKind.EqEq or TokenKind.NotEq
                or TokenKind.Less or TokenKind.LessEq or TokenKind.Greater or TokenKind.GreaterEq))
            return null;
        return new CompileError(t.Pos,
            $"A line cannot start with '{t.Text}': the line break before it ends the expression. Move '{t.Text}' to the end of the line before, where the expression then continues");
    }

    private static string Describe(Token t) => t.Kind switch
    {
        TokenKind.Eof => "end of file",
        TokenKind.Newline => "end of line",
        _ => $"'{t.Text}'",
    };

    private void SkipTerminators()
    {
        while (At(TokenKind.Newline) || At(TokenKind.Semicolon)) _pos++;
    }

    /// <summary>A simple statement must end with a line break, ';', '}' or end of file.</summary>
    private void ExpectEndOfStatement()
    {
        if (At(TokenKind.Newline) || At(TokenKind.Semicolon)) { _pos++; return; }
        if (At(TokenKind.RBrace) || At(TokenKind.Eof)) return;
        throw new CompileError(Current.Pos, $"Expected end of statement, found {Describe(Current)}");
    }
}
