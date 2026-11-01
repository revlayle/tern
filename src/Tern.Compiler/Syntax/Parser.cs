using Tern.Compiler.Semantics;
using Tern.Cpu;

namespace Tern.Compiler.Syntax;

public sealed class Parser
{
    private readonly List<Token> _tokens;
    private int _pos;

    private Parser(List<Token> tokens) => _tokens = tokens;

    public static ProgramSyntax Parse(string source) => new Parser(Lexer.Tokenize(source)).ParseProgram();

    private Token Current => _tokens[_pos];
    private Token PeekAt(int offset) => _tokens[Math.Min(_pos + offset, _tokens.Count - 1)];
    private bool At(TokenKind kind) => Current.Kind == kind;

    private Token Advance() => _tokens[_pos++];

    private bool Accept(TokenKind kind)
    {
        if (!At(kind)) return false;
        _pos++;
        return true;
    }

    private Token Expect(TokenKind kind, string what)
    {
        if (!At(kind))
            throw new CompileError(Current.Pos, $"Expected {what}, found {Describe(Current)}");
        return Advance();
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

    // ---------- Declarations ----------

    private ProgramSyntax ParseProgram()
    {
        var consts = new List<ConstDecl>();
        var globals = new List<VarDecl>();
        var functions = new List<FnDecl>();

        SkipTerminators();
        while (!At(TokenKind.Eof))
        {
            switch (Current.Kind)
            {
                case TokenKind.Const: consts.Add(ParseConst()); break;
                case TokenKind.Var: globals.Add(ParseVar()); break;
                case TokenKind.Data: globals.Add(ParseData()); break;
                case TokenKind.Fn: functions.Add(ParseFn()); break;
                case TokenKind.Asm when PeekAt(1).Kind == TokenKind.Fn:
                    Advance();
                    functions.Add(ParseFn(isAsm: true));
                    break;
                case TokenKind.Asm:
                    throw new CompileError(Current.Pos,
                        "An asm block must be inside a function. Use 'asm fn name() { ... }' for a whole function in assembly");
                default:
                    RejectUnsupported();
                    throw new CompileError(Current.Pos, $"Expected 'const', 'var', 'data' or 'fn', found {Describe(Current)}");
            }
            SkipTerminators();
        }
        return new ProgramSyntax(consts, globals, functions);
    }

    /// <summary>Keywords from the spec that this compiler version does not implement yet.</summary>
    private void RejectUnsupported()
    {
        if (Current.Kind is TokenKind.Struct or TokenKind.Match or TokenKind.Inline
            or TokenKind.Interrupt or TokenKind.Noinit or TokenKind.Linear)
            throw new CompileError(Current.Pos, $"'{Current.Text}' is not supported yet");
    }

    private ConstDecl ParseConst()
    {
        var start = Expect(TokenKind.Const, "'const'");
        var name = Expect(TokenKind.Identifier, "constant name");
        TypeSyntax? type = Accept(TokenKind.Colon) ? ParseType() : null;
        Expect(TokenKind.Eq, "'='");
        var value = ParseExpr();
        ExpectEndOfStatement();
        return new ConstDecl(start.Pos, name.Text, type, value);
    }

    private VarDecl ParseVar()
    {
        var start = Expect(TokenKind.Var, "'var'");
        var name = Expect(TokenKind.Identifier, "variable name");
        TypeSyntax? type = Accept(TokenKind.Colon) ? ParseType() : null;

        var placement = Placement.None;
        if (Accept(TokenKind.At))
        {
            if (Accept(TokenKind.Zp))
                placement = new Placement(PlacementKind.ZeroPage);
            else if (At(TokenKind.Bank))
                throw new CompileError(Current.Pos, "'@ bank(n)' is not supported yet");
            else
                placement = new Placement(PlacementKind.Address, ParseExpr());
        }

        Expr? init = Accept(TokenKind.Eq) ? ParseExpr() : null;
        ExpectEndOfStatement();
        return new VarDecl(start.Pos, name.Text, type, placement, init);
    }

    private VarDecl ParseData()
    {
        var start = Expect(TokenKind.Data, "'data'");
        var name = Expect(TokenKind.Identifier, "data name");
        TypeSyntax? type = Accept(TokenKind.Colon) ? ParseType() : null;
        if (At(TokenKind.At)) throw new CompileError(Current.Pos, "'@' on data is not supported yet");
        Expect(TokenKind.Eq, "'=' and the data's value");
        var init = ParseExpr();
        ExpectEndOfStatement();
        return new VarDecl(start.Pos, name.Text, type, Placement.None, init) { IsData = true };
    }

    private FnDecl ParseFn(bool isAsm = false)
    {
        var start = Expect(TokenKind.Fn, "'fn'");
        var name = Expect(TokenKind.Identifier, "function name");
        Expect(TokenKind.LParen, "'('");
        var parameters = new List<ParamSyntax>();
        if (!At(TokenKind.RParen))
        {
            do
            {
                var pname = Expect(TokenKind.Identifier, "parameter name");
                Expect(TokenKind.Colon, "':'");
                var ptype = ParseType();
                Register? reg = Accept(TokenKind.Using) ? ParseRegister() : null;
                parameters.Add(new ParamSyntax(pname.Pos, pname.Text, ptype, reg));
            } while (Accept(TokenKind.Comma));
        }
        Expect(TokenKind.RParen, "')'");

        TypeSyntax? returnType = null;
        Register? returnUsing = null;
        if (Accept(TokenKind.Arrow))
        {
            returnType = ParseType();
            if (Accept(TokenKind.Using)) returnUsing = ParseRegister();
        }
        if (At(TokenKind.Keeps))
            throw new CompileError(Current.Pos, $"'keeps' is only for routines at a fixed address: fn {name.Text}(...) @ $addr keeps X");

        // A routine that already exists at a fixed address: no body.
        if (At(TokenKind.At))
        {
            var at = Advance();
            if (isAsm)
                throw new CompileError(at.Pos, "An asm fn at a fixed address is not supported yet");
            var address = ParseExpr();
            var keeps = new List<(Register, SourcePos)>();
            if (Accept(TokenKind.Keeps))
            {
                do keeps.Add((ParseRegister(), PeekAt(-1).Pos)); while (Accept(TokenKind.Comma));
            }
            if (At(TokenKind.LBrace))
                throw new CompileError(at.Pos, "A function with a body at a fixed address is not supported yet. Without a body, it declares a routine that is already there");
            ExpectEndOfStatement();
            return new FnDecl(start.Pos, name.Text, parameters, returnType, returnUsing, new BlockStmt(at.Pos, []))
            {
                Address = address,
                Keeps = keeps,
            };
        }

        var body = isAsm ? new BlockStmt(Current.Pos, [ParseAsmBody()]) : ParseBlock();
        return new FnDecl(start.Pos, name.Text, parameters, returnType, returnUsing, body) { IsAsm = isAsm };
    }

    private Register ParseRegister()
    {
        var t = Expect(TokenKind.Identifier, "a register or flag (A, X, Y, AX, AY, XY, carry, zero, negative)");
        return t.Text switch
        {
            "A" => Register.A, "X" => Register.X, "Y" => Register.Y,
            "AX" => Register.AX, "AY" => Register.AY, "XY" => Register.XY,
            "carry" => Register.Carry, "zero" => Register.Zero, "negative" => Register.Negative,
            _ => throw new CompileError(t.Pos, $"Unknown register or flag '{t.Text}'"),
        };
    }

    private TypeSyntax ParseType()
    {
        if (At(TokenKind.LBracket))
        {
            var start = Advance();
            var element = ParseType();
            Expect(TokenKind.Semicolon, "';'");
            var length = ParseExpr();
            Expect(TokenKind.RBracket, "']'");
            return new ArrayTypeSyntax(start.Pos, element, length);
        }
        var name = Expect(TokenKind.Identifier, "a type");
        if (name.Text == "ptr" && (At(TokenKind.Identifier) || At(TokenKind.LBracket)))
            return new PointerTypeSyntax(name.Pos, ParseType());
        return new NamedTypeSyntax(name.Pos, name.Text);
    }

    // ---------- Statements ----------

    private BlockStmt ParseBlock()
    {
        var start = Expect(TokenKind.LBrace, "'{'");
        var statements = new List<Stmt>();
        SkipTerminators();
        while (!At(TokenKind.RBrace))
        {
            if (At(TokenKind.Eof)) throw new CompileError(start.Pos, "Block is not closed with '}'");
            statements.Add(ParseStatement());
            SkipTerminators();
        }
        Advance();
        return new BlockStmt(start.Pos, statements);
    }

    private Stmt ParseStatement()
    {
        switch (Current.Kind)
        {
            case TokenKind.Var: return ParseVar();
            case TokenKind.If: return ParseIf();
            case TokenKind.While:
            {
                var start = Advance();
                var cond = ParseExpr();
                return new WhileStmt(start.Pos, cond, ParseBlock());
            }
            case TokenKind.For: return ParseFor();
            case TokenKind.Break:
            {
                var t = Advance();
                ExpectEndOfStatement();
                return new BreakStmt(t.Pos);
            }
            case TokenKind.Continue:
            {
                var t = Advance();
                ExpectEndOfStatement();
                return new ContinueStmt(t.Pos);
            }
            case TokenKind.Return:
            {
                var t = Advance();
                Expr? value = At(TokenKind.Newline) || At(TokenKind.Semicolon) || At(TokenKind.RBrace) ? null : ParseExpr();
                ExpectEndOfStatement();
                return new ReturnStmt(t.Pos, value);
            }
            case TokenKind.LBrace: return ParseBlock();
            case TokenKind.Data: throw new CompileError(Current.Pos, "'data' must be declared outside functions");
            case TokenKind.Asm when PeekAt(1).Kind == TokenKind.Fn:
                throw new CompileError(Current.Pos, "'asm fn' must be declared outside functions");
            case TokenKind.Asm:
                Advance();
                return ParseAsmBody();
        }
        RejectUnsupported();

        var expr = ParseExpr();
        if (Current.Kind is TokenKind.Eq or TokenKind.PlusEq or TokenKind.MinusEq or TokenKind.AmpEq
            or TokenKind.PipeEq or TokenKind.CaretEq or TokenKind.ShlEq or TokenKind.ShrEq)
        {
            var op = Advance();
            var value = ParseExpr();
            ExpectEndOfStatement();
            return new AssignStmt(op.Pos, expr, op.Kind, value);
        }
        ExpectEndOfStatement();
        return new ExprStmt(expr.Pos, expr);
    }

    private IfStmt ParseIf()
    {
        var start = Expect(TokenKind.If, "'if'");
        var cond = ParseExpr();
        var then = ParseBlock();

        // "else" may be on the line after the closing brace.
        int save = _pos;
        SkipTerminators();
        if (!Accept(TokenKind.Else))
        {
            _pos = save;
            return new IfStmt(start.Pos, cond, then, null);
        }
        Stmt elseStmt = At(TokenKind.If) ? ParseIf() : ParseBlock();
        return new IfStmt(start.Pos, cond, then, elseStmt);
    }

    private ForStmt ParseFor()
    {
        var start = Expect(TokenKind.For, "'for'");
        var name = Expect(TokenKind.Identifier, "loop variable name or '_'");
        Expect(TokenKind.In, "'in'");
        var from = ParseExpr();
        bool inclusive;
        if (Accept(TokenKind.DotDotEq)) inclusive = true;
        else if (Accept(TokenKind.DotDot)) inclusive = false;
        else throw new CompileError(Current.Pos, $"Expected '..' or '..=', found {Describe(Current)}");
        var to = ParseExpr();
        Expr? step = Accept(TokenKind.Step) ? ParseExpr() : null;
        Register? reg = Accept(TokenKind.Using) ? ParseRegister() : null;
        var body = ParseBlock();
        return new ForStmt(start.Pos, name.Text == "_" ? null : name.Text, from, to, inclusive, step, reg, body);
    }

    // ---------- Assembly ----------

    /// <summary>True while parsing assembly operands, where <c>.name</c> is a local label.</summary>
    private bool _inAsm;

    /// <summary>The <c>{ ... }</c> of an asm block or asm fn: one instruction per line, and <c>.labels:</c>.</summary>
    private AsmStmt ParseAsmBody()
    {
        var start = Expect(TokenKind.LBrace, "'{' after 'asm'");
        var lines = new List<AsmLine>();
        _inAsm = true;
        while (true)
        {
            while (Accept(TokenKind.Newline)) { }
            if (At(TokenKind.RBrace)) break;
            if (At(TokenKind.Eof)) throw new CompileError(start.Pos, "asm block is not closed with '}'");
            if (At(TokenKind.Dot))
            {
                var dot = Advance();
                var label = Expect(TokenKind.Identifier, "a label name after '.'");
                Expect(TokenKind.Colon, "':' after the label");
                lines.Add(new AsmLabel(dot.Pos, "." + label.Text));
                continue;
            }
            if (At(TokenKind.Semicolon)) throw SemicolonInAsm();
            var word = Expect(TokenKind.Identifier, "an instruction or a .label:");
            if (At(TokenKind.Colon))
                throw new CompileError(word.Pos, $"Labels in assembly start with '.', e.g. .{word.Text}:");
            if (!Enum.TryParse<Mnemonic>(word.Text, ignoreCase: true, out var mnemonic) || !Enum.IsDefined(mnemonic))
                throw new CompileError(word.Pos, $"Unknown instruction '{word.Text}'");
            lines.Add(ParseAsmInstruction(word.Pos, mnemonic));

            if (Accept(TokenKind.Newline) || At(TokenKind.RBrace)) continue;
            if (At(TokenKind.Semicolon)) throw SemicolonInAsm();
            throw new CompileError(Current.Pos, $"Expected the end of the line after the instruction, found {Describe(Current)}");
        }
        Advance();
        _inAsm = false;
        return new AsmStmt(start.Pos, lines);
    }

    private CompileError SemicolonInAsm() => new(Current.Pos,
        "';' is not used in assembly: write one instruction per line, and comments with //");

    private AsmInstruction ParseAsmInstruction(SourcePos pos, Mnemonic m)
    {
        if (At(TokenKind.Newline) || At(TokenKind.RBrace) || At(TokenKind.Semicolon) || At(TokenKind.Eof))
            return new AsmInstruction(pos, m, AsmForm.None, null, null);

        if (Accept(TokenKind.Hash))
        {
            if (At(TokenKind.Less) || At(TokenKind.Greater))
                throw new CompileError(Current.Pos, "Write the bytes of an address with .lo and .hi, e.g. #name.lo and #name.hi");
            return new AsmInstruction(pos, m, AsmForm.Immediate, ParseExpr(), null);
        }

        // "asl a": the accumulator, for the shifts and rotates.
        if (m is Mnemonic.Asl or Mnemonic.Lsr or Mnemonic.Rol or Mnemonic.Ror
            && At(TokenKind.Identifier) && Current.Text is "a" or "A"
            && PeekAt(1).Kind is TokenKind.Newline or TokenKind.RBrace)
        {
            Advance();
            return new AsmInstruction(pos, m, AsmForm.Accumulator, null, null);
        }

        if (Accept(TokenKind.LParen))
        {
            var inner = ParseExpr();
            if (Accept(TokenKind.Comma))
            {
                ExpectIndexRegister(Register.X, "(address,x)");
                Expect(TokenKind.RParen, "')'");
                return new AsmInstruction(pos, m, AsmForm.IndexedIndirect, inner, Register.X);
            }
            Expect(TokenKind.RParen, "')'");
            if (Accept(TokenKind.Comma))
            {
                ExpectIndexRegister(Register.Y, "(address),y");
                return new AsmInstruction(pos, m, AsmForm.IndirectIndexed, inner, Register.Y);
            }
            return new AsmInstruction(pos, m, AsmForm.Indirect, inner, null);
        }

        var operand = ParseExpr();
        Register? index = null;
        if (Accept(TokenKind.Comma))
        {
            var r = Expect(TokenKind.Identifier, "x or y after ','");
            index = r.Text switch
            {
                "x" or "X" => Register.X,
                "y" or "Y" => Register.Y,
                _ => throw new CompileError(r.Pos, $"Expected x or y after ',', found '{r.Text}'"),
            };
        }
        return new AsmInstruction(pos, m, AsmForm.Direct, operand, index);
    }

    private void ExpectIndexRegister(Register reg, string form)
    {
        var r = Expect(TokenKind.Identifier, $"{reg.ToString().ToLowerInvariant()} in {form}");
        if (!string.Equals(r.Text, reg.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new CompileError(r.Pos, $"Expected {reg.ToString().ToLowerInvariant()} in {form}, found '{r.Text}'");
    }

    // ---------- Expressions ----------

    private Expr ParseExpr() => ParseOr();

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

    private Expr ParsePrimary()
    {
        var t = Current;
        switch (t.Kind)
        {
            case TokenKind.Number:
                Advance();
                return new NumberExpr(t.Pos, t.Value);
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
                Advance();
                var elements = new List<Expr>();
                if (!At(TokenKind.RBracket))
                {
                    do elements.Add(ParseExpr()); while (Accept(TokenKind.Comma));
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
                if (!At(TokenKind.LParen)) return new NameExpr(t.Pos, t.Text);
                Advance();
                var args = new List<Expr>();
                if (!At(TokenKind.RParen))
                {
                    do args.Add(ParseExpr()); while (Accept(TokenKind.Comma));
                }
                Expect(TokenKind.RParen, "')'");
                return new CallExpr(t.Pos, t.Text, args);
            }
            default:
                throw new CompileError(t.Pos, $"Expected an expression, found {Describe(t)}");
        }
    }
}
