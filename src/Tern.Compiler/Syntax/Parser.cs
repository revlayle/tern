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

    /// <summary>Where errors go; null: the first error is thrown and parsing stops.</summary>
    private readonly ErrorList? _errors;

    private Parser(List<Token> tokens, string source, ErrorList? errors)
    {
        _tokens = tokens;
        _source = source;
        _errors = errors;
        for (int i = 0; i < source.Length; i++)
            if (source[i] == '\n') _lineStarts.Add(i + 1);
    }

    /// <param name="file">The source file, for positions in error messages and the listing.</param>
    /// <param name="errors">
    /// Takes every error; after one, parsing goes on at the next statement or declaration (spec 13).
    /// Null: the first error is thrown.
    /// </param>
    public static ProgramSyntax Parse(string source, string? file = null, ErrorList? errors = null) =>
        new Parser(Lexer.Tokenize(source, file, errors is null ? null : errors.Add), source, errors).ParseProgram();

    // ---------- Nesting ----------

    /// <summary>
    /// How deep the syntax tree may be: brackets, unary operators, binary operators in a row, postfix
    /// operators, blocks and types each count one level. Every later pass walks the tree by recursion, and
    /// <see cref="TernCompiler.Compile"/> runs on a stack sized for this depth, so a deeper program is an
    /// error and not a stack overflow, which .NET cannot catch (review code-quality-syntax 4).
    /// </summary>
    public const int MaxDepth = 256;
    private int _depth;

    private void Enter()
    {
        if (_depth == MaxDepth)
            throw new CompileError(Current.Pos,
                $"This is nested more than {MaxDepth} deep (brackets, operators in a row, blocks or types). Split it with a constant, a variable or a function");
        _depth++;
    }

    // ---------- Spans ----------

    /// <summary>
    /// Sets <paramref name="node"/>'s span: from token <paramref name="first"/> to the last token read, leaving
    /// out line breaks before it and the line break or ';' that ended it (for a language server, M28).
    /// </summary>
    private T Finish<T>(T node, int first) where T : Node
    {
        int last = _pos - 1;
        while (first < last && _tokens[first].Kind == TokenKind.Newline) first++;
        while (last > first && _tokens[last].Kind is TokenKind.Newline or TokenKind.Semicolon) last--;
        int start = _tokens[first].Start;
        node.Span = new SourceSpan(start, last < first ? start : Math.Max(start, _tokens[last].End));
        return node;
    }

    // ---------- Modes ----------

    /// <summary>
    /// Sets a mode of the parse (<see cref="_inAsm"/>, <see cref="_inHeader"/>, ...) until the end of a
    /// <c>using</c> scope, and then puts back the value it had, also when the scope ends by an error.
    /// </summary>
    private ref struct Mode<T>
    {
        private readonly ref T _field;
        private readonly T _saved;

        public Mode(ref T field, T value)
        {
            _field = ref field;
            _saved = field;
            field = value;
        }

        public readonly void Dispose() => _field = _saved;
    }

    // ---------- Going on after an error (spec 13) ----------

    /// <summary>Statements and declarations dropped for an error, also one that was not reported.</summary>
    private int _failures;
    /// <summary>An error was found at the end of the file (a block not closed): the blocks around it are not reported too.</summary>
    private bool _unclosedReported;

    /// <summary>True for the errors the parser goes on after; false when there is no error list.</summary>
    private bool Recovers(Exception e) => _errors is not null && e is CompileError or FollowOnError;

    /// <summary>
    /// Reports the error of the statement or declaration that started at token <paramref name="from"/>.
    /// Not when its tokens include one where the lexer found an error, which was reported already.
    /// </summary>
    private void Fail(Exception e, int from)
    {
        _failures++;
        if (At(TokenKind.Eof)) _unclosedReported = true;
        for (int k = from; k <= Math.Min(_pos, _tokens.Count - 1); k++)
            if (_tokens[k].Kind == TokenKind.Error) return;
        if (e is CompileError error) _errors!.Add(error);
    }

    /// <summary>Braces opened and not closed from token <paramref name="from"/> to the current one.</summary>
    private int BracesOpen(int from)
    {
        int braces = 0;
        for (int k = from; k < _pos; k++)
            braces += _tokens[k].Kind switch { TokenKind.LBrace => 1, TokenKind.RBrace => -1, _ => 0 };
        return Math.Max(0, braces);
    }

    /// <summary>
    /// After an error in a statement that started at <paramref name="from"/>: skips to the end of it, the
    /// next line break or ';' outside the braces it opened, or to the '}' that closes the block.
    /// </summary>
    private void SkipStatement(int from)
    {
        int braces = BracesOpen(from);
        // A ';' inside ( ) or [ ] is part of the statement ([u8; 4]). The lexer gives no line breaks there.
        int brackets = 0;
        for (int k = from; k < _pos; k++)
            brackets = _tokens[k].Kind switch
            {
                TokenKind.LParen or TokenKind.LBracket => brackets + 1,
                TokenKind.RParen or TokenKind.RBracket => Math.Max(0, brackets - 1),
                TokenKind.LBrace or TokenKind.RBrace => 0,
                _ => brackets,
            };
        while (!At(TokenKind.Eof))
        {
            if (braces == 0 && At(TokenKind.RBrace)) return;
            // After an unclosed ( the lexer gives no line breaks: a line that starts no further right
            // than the statement did is the next statement.
            if (braces == 0 && _pos > from && StartsLineAt(_tokens[from].Pos.Column)) return;
            var t = Advance();
            if (t.Kind is TokenKind.LParen or TokenKind.LBracket) brackets++;
            else if (t.Kind is TokenKind.RParen or TokenKind.RBracket) brackets = Math.Max(0, brackets - 1);
            else if (t.Kind is TokenKind.LBrace or TokenKind.RBrace)
            {
                braces += t.Kind == TokenKind.LBrace ? 1 : -1;
                brackets = 0;
            }
            else if (braces == 0 && (t.Kind == TokenKind.Newline || t.Kind == TokenKind.Semicolon && brackets == 0)) return;
        }
    }

    /// <summary>
    /// After an error in a top-level declaration that started at <paramref name="from"/>: skips to the
    /// next line, outside any braces, that starts a declaration.
    /// </summary>
    private void SkipDeclaration(int from)
    {
        int braces = BracesOpen(from);
        while (!At(TokenKind.Eof))
        {
            if (braces == 0 && _pos > from && StartsCurrentDeclaration() && Current.Pos.Line != _tokens[_pos - 1].Pos.Line) return;
            var t = Advance();
            if (t.Kind == TokenKind.LBrace) braces++;
            else if (t.Kind == TokenKind.RBrace) braces = Math.Max(0, braces - 1);
        }
    }

    /// <summary>Whether the current token is the first of its line, at <paramref name="column"/> or further left.</summary>
    private bool StartsLineAt(int column) =>
        _pos > 0 && Current.Pos.Line != _tokens[_pos - 1].Pos.Line && Current.Pos.Column <= column;

    /// <summary>The column of the statement being parsed in a block, for going on after an unclosed ( (spec 13).</summary>
    private int _statementColumn = 1;

    /// <summary>The keywords of a named declaration (spec 2.2): <c>const</c>, <c>var</c>, <c>data</c>, <c>fn</c>, <c>struct</c>.</summary>
    private static bool IsDeclarationKeyword(TokenKind kind) =>
        kind is TokenKind.Const or TokenKind.Var or TokenKind.Data or TokenKind.Fn or TokenKind.Struct;

    /// <summary>
    /// The words that may come before a declaration's keyword: <c>private</c>, <c>noinit</c>, <c>volatile</c>,
    /// <c>inline</c>, <c>interrupt</c>, <c>linear</c>, <c>indexed</c> and <c>asm</c> (of <c>asm fn</c>).
    /// </summary>
    private static bool IsDeclarationModifier(Token t) =>
        t.Kind is TokenKind.Private or TokenKind.Noinit or TokenKind.Volatile or TokenKind.Inline or TokenKind.Interrupt
            or TokenKind.Linear or TokenKind.Asm || Word.Is(t, Word.Indexed);

    /// <summary>Whether the current token starts a top-level declaration, also with a word from another language (pub fn, let x).</summary>
    private bool StartsCurrentDeclaration()
    {
        Token t = Current, next = PeekAt(1);
        return IsDeclarationKeyword(t.Kind) || IsDeclarationModifier(t)
            || t.Kind is TokenKind.Assert or TokenKind.Export or TokenKind.Import or TokenKind.Module
            || Word.Is(t, Word.Memory)
            || Word.Is(t, Word.Encoding) && next.Kind == TokenKind.Identifier
            || Word.Is(t, Word.Default) && Word.Is(next, Word.Encoding)
            || t.Kind == TokenKind.Identifier && IsDeclarationKeyword(next.Kind)
            || t is { Kind: TokenKind.Identifier, Text: "let" } && next.Kind == TokenKind.Identifier;
    }

    /// <summary>The name declared (or the module imported) by the declaration or statement at token <paramref name="from"/>, or null.</summary>
    private (string Name, SourcePos Pos)? DeclaredName(int from)
    {
        int k = from;
        while (IsDeclarationModifier(_tokens[k])) k++;
        // A word from another language before the keyword (pub fn, async fn), or 'let name'.
        if (_tokens[k].Kind == TokenKind.Identifier && IsDeclarationKeyword(_tokens[k + 1].Kind))
            k++;
        else if (_tokens[k] is { Kind: TokenKind.Identifier, Text: "let" } && _tokens[k + 1].Kind == TokenKind.Identifier)
            return (_tokens[k + 1].Text, _tokens[k + 1].Pos);
        if (Word.Is(_tokens[k], Word.Encoding) && _tokens[k + 1].Kind == TokenKind.Identifier)
            return (_tokens[k + 1].Text, _tokens[k + 1].Pos);
        if (!IsDeclarationKeyword(_tokens[k].Kind) && _tokens[k].Kind != TokenKind.Import) return null;
        if (_tokens[k].Kind == TokenKind.Const && _tokens[k + 1].Kind == TokenKind.Fn) k++;
        var name = _tokens[k + 1];
        return name.Kind == TokenKind.Identifier ? (name.Text, name.Pos) : null;
    }

    /// <summary>Modules imported by this file. <c>name.member</c> after one of them is a qualified name.</summary>
    private readonly HashSet<string> _modules = [];

    private Token Current => _tokens[_pos];
    private Token PeekAt(int offset) => _tokens[Math.Min(_pos + offset, _tokens.Count - 1)];
    private bool At(TokenKind kind) => Current.Kind == kind;

    /// <summary>True if the current token is the word <paramref name="word"/> (<see cref="Word"/>).</summary>
    private bool AtWord(string word) => Word.Is(Current, word);

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
        // After an unclosed ( or [, which was reported, the lexer gives no line breaks: a declaration
        // starting a new line ends the statement (spec 13).
        if (_failures > 0 && StartsCurrentDeclaration() && Current.Pos.Line != _tokens[_pos - 1].Pos.Line) return;
        // So does a line that starts no further right than this statement.
        if (_failures > 0 && StartsLineAt(_statementColumn)) return;
        throw new CompileError(Current.Pos, $"Expected end of statement, found {Describe(Current)}. Put each statement on its own line, or separate them with ';'");
    }
}
