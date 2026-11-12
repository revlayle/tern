using Tern.Compiler.Semantics;
using Tern.Cpu;

namespace Tern.Compiler.Syntax;

/// <summary>Expressions, by precedence.</summary>
public sealed partial class Parser
{
    // ---------- Expressions ----------

    private Expr ParseExpr() => ParseOr();

    /// <summary>
    /// Set while the expression of an <c>if</c>, <c>while</c>, <c>for</c> or <c>match</c> header is parsed:
    /// there a name followed by '{' is the name and the block, not a struct literal.
    /// </summary>
    private bool _inHeader;

    private Expr ParseHeaderExpr()
    {
        bool saved = _inHeader;
        _inHeader = true;
        try { return ParseExpr(); }
        finally { _inHeader = saved; }
    }

    private void SkipNewlines()
    {
        while (Accept(TokenKind.Newline)) { }
    }

    /// <summary>
    /// <c>Name { field = value, ... }</c> (spec 3.3): fields separated by ',' or line breaks, a trailing
    /// ',' allowed, values parsed as any expression (a nested literal too).
    /// </summary>
    private StructLiteralExpr ParseStructLiteral(SourcePos pos, string name)
    {
        var open = Expect(TokenKind.LBrace, "'{'");
        bool saved = _inHeader;
        _inHeader = false;
        try
        {
            var fields = new List<FieldValue>();
            SkipNewlines();
            while (!Accept(TokenKind.RBrace))
            {
                if (At(TokenKind.Eof)) throw new CompileError(open.Pos, $"The struct literal '{name} {{' is not closed with '}}'");
                var field = Expect(TokenKind.Identifier, "field name");
                if (At(TokenKind.Colon))
                    throw new CompileError(Current.Pos,
                        $"A struct literal gives a field its value with '=', since ':' means \"has type\". Write '{field.Text} = ...'");
                Expect(TokenKind.Eq, $"'=' and the value of '{field.Text}'");
                fields.Add(new FieldValue(field.Pos, field.Text, ParseExpr()));
                if (!Accept(TokenKind.Comma) && !At(TokenKind.Newline) && !At(TokenKind.RBrace))
                    throw new CompileError(Current.Pos, $"Expected ',' or '}}' after the value of '{field.Text}', found {Describe(Current)}");
                SkipNewlines();
            }
            return new StructLiteralExpr(pos, name, fields);
        }
        finally { _inHeader = saved; }
    }

    private Expr ParseOr() => ParseLeftAssoc(ParseAnd, TokenKind.PipePipe);
    private Expr ParseAnd() => ParseLeftAssoc(ParseComparison, TokenKind.AmpAmp);

    private Expr ParseComparison()
    {
        var left = ParseBitOr();
        if (Current.Kind is TokenKind.EqEq or TokenKind.NotEq or TokenKind.Less or TokenKind.LessEq
            or TokenKind.Greater or TokenKind.GreaterEq)
        {
            var op = Advance();
            var right = ParseBitOr();
            if (Current.Kind is TokenKind.EqEq or TokenKind.NotEq or TokenKind.Less or TokenKind.LessEq
                or TokenKind.Greater or TokenKind.GreaterEq)
                throw new CompileError(Current.Pos, "Comparisons cannot be chained; use '&&'");
            return new BinaryExpr(op.Pos, op.Kind, left, right);
        }
        return left;
    }

    private Expr ParseBitOr() => ParseLeftAssoc(ParseBitXor, TokenKind.Pipe);
    private Expr ParseBitXor() => ParseLeftAssoc(ParseBitAnd, TokenKind.Caret);
    private Expr ParseBitAnd() => ParseLeftAssoc(ParseShift, TokenKind.Amp);
    private Expr ParseShift() => ParseLeftAssoc(ParseAdditive, TokenKind.Shl, TokenKind.Shr);
    private Expr ParseAdditive() => ParseLeftAssoc(ParseMultiplicative, TokenKind.Plus, TokenKind.Minus);
    private Expr ParseMultiplicative() => ParseLeftAssoc(ParseUnary, TokenKind.Star, TokenKind.Slash, TokenKind.Percent);

    private Expr ParseLeftAssoc(Func<Expr> next, params TokenKind[] ops)
    {
        var left = next();
        while (ops.Contains(Current.Kind))
        {
            var op = Advance();
            var right = next();
            left = new BinaryExpr(op.Pos, op.Kind, left, right);
        }
        return left;
    }

    private Expr ParseUnary()
    {
        if (Current.Kind is TokenKind.Minus or TokenKind.Plus or TokenKind.Tilde or TokenKind.Bang)
        {
            var op = Advance();
            return new UnaryExpr(op.Pos, op.Kind, ParseUnary());
        }
        return ParsePostfix();
    }

    private Expr ParsePostfix()
    {
        var expr = ParsePrimary();
        while (true)
        {
            if (At(TokenKind.LBracket))
            {
                var t = Advance();
                var index = ParseExpr();
                Expect(TokenKind.RBracket, "']'");
                expr = new IndexExpr(t.Pos, expr, index);
            }
            else if (At(TokenKind.Dot))
            {
                var t = Advance();
                var member = Expect(TokenKind.Identifier, "member name");
                expr = new MemberExpr(t.Pos, expr, member.Text);
            }
            else if (At(TokenKind.As))
            {
                var t = Advance();
                expr = new CastExpr(t.Pos, expr, ParseType());
            }
            else return expr;
        }
    }

    /// <summary>The rest of <c>[expr for i in from..to step k]</c>, after the element (spec 4.1).</summary>
    private ListForExpr ParseListFor(SourcePos pos, Expr element)
    {
        Expect(TokenKind.For, "'for'");
        var name = Expect(TokenKind.Identifier, "a variable name or '_' after 'for'");
        Expect(TokenKind.In, "'in'");
        int rangeStart = _pos;
        var from = ParseExpr();
        bool inclusive;
        if (Accept(TokenKind.DotDotEq)) inclusive = true;
        else if (Accept(TokenKind.DotDot)) inclusive = false;
        else throw new CompileError(Current.Pos, $"Expected '..' or '..=', found {Describe(Current)}");
        var to = ParseExpr();
        string range = TokenText(rangeStart, _pos);
        Expr? step = null;
        if (At(TokenKind.Identifier) && Current.Text == "step")
        {
            Advance();
            step = ParseExpr();
        }
        if (At(TokenKind.For))
            throw new CompileError(Current.Pos, "A list built with 'for' has one 'for'. Write a const fn with nested loops for more");
        if (At(TokenKind.If))
            throw new CompileError(Current.Pos, "A list built with 'for' has no filter. Write a const fn with a loop and an 'if' for that");
        Expect(TokenKind.RBracket, "']' after the range of the list");
        return new ListForExpr(pos, element, name.Text == "_" ? null : name.Text, from, to, inclusive, step) { RangeText = range };
    }

    private Expr ParsePrimary()
    {
        var t = Current;
        switch (t.Kind)
        {
            case TokenKind.Number:
                Advance();
                return new NumberExpr(t.Pos, t.Value);
            case TokenKind.Real:
                Advance();
                return new RealExpr(t.Pos, double.Parse(t.Text.Replace("_", ""), System.Globalization.CultureInfo.InvariantCulture), t.Text);
            case TokenKind.Char:
                Advance();
                return new CharExpr(t.Pos, t.Text[0], t.Encoding);
            case TokenKind.String:
                Advance();
                return new StringExpr(t.Pos, t.Text, t.Encoding);
            case TokenKind.Dot when _inAsm:
            {
                Advance();
                var label = Expect(TokenKind.Identifier, "a label name after '.'");
                return new AsmLabelExpr(t.Pos, "." + label.Text);
            }
            case TokenKind.True:
                Advance();
                return new BoolExpr(t.Pos, true);
            case TokenKind.False:
                Advance();
                return new BoolExpr(t.Pos, false);
            case TokenKind.LBracket:
            {
                // A '}' of a struct literal ends the lexer's bracket rule, so line breaks may appear here.
                Advance();
                SkipNewlines();
                var elements = new List<Expr>();
                if (!At(TokenKind.RBracket))
                {
                    do
                    {
                        SkipNewlines();
                        if (At(TokenKind.RBracket)) break; // a trailing ','
                        int start = _pos;
                        var element = ParseExpr();
                        // [expr for i in range] (spec 4.1): one element, then ']'.
                        if (elements.Count == 0 && At(TokenKind.For)) return ParseListFor(t.Pos, element);
                        // [value; count], Rust's spelling, is repeat(value, count) in Tern (spec 4.1).
                        if (elements.Count == 0 && At(TokenKind.Semicolon))
                        {
                            string value = TokenText(start, _pos);
                            Advance();
                            int countStart = _pos;
                            ParseExpr();
                            string count = TokenText(countStart, _pos);
                            throw new CompileError(t.Pos, $"'[{value}; {count}]' is not an array in Tern, where [T; n] is only an array type. Write repeat({value}, {count})");
                        }
                        elements.Add(element);
                        SkipNewlines();
                        if (!At(TokenKind.Comma) && !At(TokenKind.RBracket))
                            throw new CompileError(Current.Pos, $"Expected ',' or ']' after this element, found {Describe(Current)}. "
                                + (At(TokenKind.Eof) || At(TokenKind.RBrace) || At(TokenKind.RParen)
                                   || Lexer.IsKeyword(Current.Kind) && !At(TokenKind.True) && !At(TokenKind.False) ? "Write ']' to end the list"
                                    : element is StructLiteralExpr ? "Write ',' after the '}'" : "Write ',' between the elements"));
                    } while (Accept(TokenKind.Comma));
                }
                Expect(TokenKind.RBracket, "']'");
                return new ArrayLiteralExpr(t.Pos, elements);
            }
            case TokenKind.LParen:
            {
                Advance();
                var e = ParseExpr();
                Expect(TokenKind.RParen, "')'");
                return e;
            }
            case TokenKind.Identifier:
            {
                Advance();
                string name = t.Text;
                // A name in an imported module: text.put
                // Also name.member(...) when name is not imported, so that the checker can say to import it.
                if (At(TokenKind.Dot) && (_modules.Contains(name)
                    || (PeekAt(1).Kind == TokenKind.Identifier && PeekAt(2).Kind == TokenKind.LParen)))
                {
                    Advance();
                    name += "." + Expect(TokenKind.Identifier, $"a name in module '{t.Text}' after '.'").Text;
                }
                if (At(TokenKind.LBrace) && !_inHeader) return ParseStructLiteral(t.Pos, name);
                if (!At(TokenKind.LParen)) return new NameExpr(t.Pos, name);
                Advance();
                var args = new List<Expr>();
                if (!At(TokenKind.RParen))
                {
                    do args.Add(ParseExpr()); while (Accept(TokenKind.Comma));
                }
                Expect(TokenKind.RParen, "')'");
                return new CallExpr(t.Pos, name, args);
            }
            // Habits from C, with the Tern spelling (spec 13).
            case TokenKind.Star:
            {
                string p = PeekAt(1).Kind == TokenKind.Identifier ? PeekAt(1).Text : "p";
                throw new CompileError(t.Pos, $"Tern has no '*' to read through a pointer. Write '{p}[0]' for the value {p} points to");
            }
            case TokenKind.Percent when PeekAt(1) is { Kind: TokenKind.Number } bits && bits.Pos.Column == t.Pos.Column + 1
                && bits.Text.All(ch => ch is '0' or '1' or '_')
                && !(PeekAt(2).Kind is TokenKind.Identifier or TokenKind.Number
                     && PeekAt(2).Pos.Line == bits.Pos.Line && PeekAt(2).Pos.Column == bits.Pos.Column + bits.Text.Length):
                throw new CompileError(t.Pos, $"'%' does not start a binary number. Write binary numbers as 0b{PeekAt(1).Text}");
            default:
                if (LeadingOperator() is { } lead) throw lead;
                throw new CompileError(t.Pos, $"Expected an expression, found {Describe(t)}");
        }
    }
}
