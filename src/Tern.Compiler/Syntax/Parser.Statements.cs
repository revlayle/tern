using Tern.Compiler.Semantics;
using Tern.Cpu;

namespace Tern.Compiler.Syntax;

/// <summary>Statements and assembly blocks.</summary>
public sealed partial class Parser
{
    // ---------- Statements ----------

    private BlockStmt ParseBlock()
    {
        SkipBreaksBeforeBrace();
        var start = Expect(TokenKind.LBrace, "'{'");
        var statements = new List<Stmt>();
        SkipTerminators();
        while (!At(TokenKind.RBrace))
        {
            if (At(TokenKind.Eof))
            {
                if (_unclosedReported) throw new FollowOnError();
                throw new CompileError(start.Pos, "Block is not closed with '}'");
            }
            int from = _pos;
            try { statements.Add(ParseStatement()); }
            catch (Exception e) when (Recovers(e))
            {
                Fail(e, from);
                // A line starting with '==' belongs to the statement before, which the checker would report too.
                if (_pos == from && statements.Count > 0 && LeadingOperator() is not null)
                    statements[^1] = new ErrorStmt(statements[^1].Pos, statements[^1] switch { VarDecl v => v.Name, ConstDecl c => c.Name, _ => null });
                statements.Add(new ErrorStmt(_tokens[from].Pos, DeclaredName(from)?.Name));
                SkipStatement(from);
            }
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
            case TokenKind.Const when PeekAt(1).Kind == TokenKind.Fn:
                throw new CompileError(Current.Pos, "'const fn' must be declared outside functions");
            case TokenKind.Const: return ParseConst();
            case TokenKind.Identifier when _inConstFn && Current.Text == "emit" && PeekAt(1).Kind == TokenKind.LBrace:
            {
                var t = Advance();
                return new EmitStmt(t.Pos, ParseAsmBody(emit: true));
            }
            case TokenKind.Linear when PeekAt(1).Kind == TokenKind.Data:
                throw new CompileError(PeekAt(1).Pos, "'data' must be declared outside functions");
            case TokenKind.Linear: return ParseLinearVar();
            case TokenKind.Identifier when IsIndexedWord(): return ParseIndexedVar();
            case TokenKind.Struct: throw new CompileError(Current.Pos, "A struct must be declared outside functions");
            case TokenKind.Private:
                throw new CompileError(Current.Pos, "'private' is only for top-level declarations; names inside a function are always private to it");
            case TokenKind.If: return ParseIf();
            case TokenKind.While:
            {
                var start = Advance();
                var cond = ParseHeaderExpr();
                return new WhileStmt(start.Pos, cond, ParseBlock());
            }
            case TokenKind.For: return ParseFor();
            case TokenKind.Match: return ParseMatch();
            case TokenKind.Identifier when Current.Text == "indexed" && PeekAt(1).Kind == TokenKind.Match:
                Advance();
                return ParseMatch(indexed: true);
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
            case TokenKind.Interrupt:
                throw new CompileError(Current.Pos, "'interrupt fn' must be declared outside functions");
            case TokenKind.Assert:
                throw new CompileError(Current.Pos, "'assert' is a top-level declaration checked at compile time; put it outside the function");
            case TokenKind.Module:
                throw new CompileError(Current.Pos, "'module' must be the first line of the file, outside functions");
            case TokenKind.Noinit:
                throw new CompileError(Current.Pos, "'noinit' is for global variables; local variables are never zeroed");
            case TokenKind.Volatile:
                throw new CompileError(Current.Pos, "'volatile' is for global variables; nothing else can change a local variable");
            case TokenKind.Inline:
                throw new CompileError(Current.Pos, "'inline fn' must be declared outside functions");
            case TokenKind.Asm:
                Advance();
                return ParseAsmBody();
        }
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
        var cond = ParseHeaderExpr();
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

    /// <summary>
    /// <c>match x { arm... }</c>; one arm per line: patterns separated by ',', then '=>' and a block
    /// or a single statement.
    /// </summary>
    private MatchStmt ParseMatch(bool indexed = false)
    {
        var start = Expect(TokenKind.Match, "'match'");
        var subject = ParseHeaderExpr();
        SkipBreaksBeforeBrace();
        var open = Expect(TokenKind.LBrace, "'{' after the match value");
        var arms = new List<MatchArm>();
        SkipTerminators();
        while (!Accept(TokenKind.RBrace))
        {
            if (At(TokenKind.Eof)) throw new CompileError(open.Pos, "'match' is not closed with '}'");
            var armPos = Current.Pos;
            var patterns = new List<MatchPattern>();
            bool isDefault = false;
            do
            {
                if (At(TokenKind.Identifier) && Current.Text == "_")
                {
                    var underscore = Advance();
                    if (patterns.Count > 0 || At(TokenKind.Comma))
                        throw new CompileError(underscore.Pos, "'_' matches every value, so it must be alone in its arm: '_ => ...'");
                    isDefault = true;
                    break;
                }
                var low = ParseExpr();
                Expr? high = null;
                if (Accept(TokenKind.DotDotEq)) high = ParseExpr();
                else if (At(TokenKind.DotDot))
                    throw new CompileError(Current.Pos, "A range in a match includes its end: write 'low..=high'");
                patterns.Add(new MatchPattern(low.Pos, low, high));
            } while (Accept(TokenKind.Comma));
            Expect(TokenKind.FatArrow, "'=>' after the match pattern");
            BlockStmt body;
            if (At(TokenKind.LBrace))
            {
                body = ParseBlock();
                ExpectEndOfStatement();
            }
            else
            {
                if (At(TokenKind.Newline) || At(TokenKind.RBrace))
                    throw new CompileError(Current.Pos, "Expected a statement or '{ }' after '=>'");
                var stmt = ParseStatement();
                body = new BlockStmt(stmt.Pos, [stmt]);
            }
            arms.Add(new MatchArm(armPos, isDefault ? [] : patterns, body));
            SkipTerminators();
        }
        return new MatchStmt(start.Pos, subject, arms) { IsIndexed = indexed };
    }

    private ForStmt ParseFor()
    {
        var start = Expect(TokenKind.For, "'for'");
        var name = Expect(TokenKind.Identifier, "loop variable name or '_'");
        Expect(TokenKind.In, "'in'");
        int rangeStart = _pos;
        var from = ParseHeaderExpr();
        bool inclusive;
        if (Accept(TokenKind.DotDotEq)) inclusive = true;
        else if (Accept(TokenKind.DotDot)) inclusive = false;
        else throw new CompileError(Current.Pos, $"Expected '..' or '..=', found {Describe(Current)}");
        var to = ParseHeaderExpr();
        string range = TokenText(rangeStart, _pos);
        // 'step' is a keyword only here, right after the range (spec 13); elsewhere it is a name.
        Expr? step = null;
        if (At(TokenKind.Identifier) && Current.Text == "step")
        {
            Advance();
            step = ParseHeaderExpr();
        }
        Register? reg = Accept(TokenKind.Using) ? ParseRegister() : null;
        var body = ParseBlock();
        return new ForStmt(start.Pos, name.Text == "_" ? null : name.Text, from, to, inclusive, step, reg, body) { RangeText = range };
    }

    // ---------- Assembly ----------

    /// <summary>True while parsing assembly operands, where <c>.name</c> is a local label.</summary>
    private bool _inAsm;

    /// <summary>The <c>{ ... }</c> of an asm block or asm fn: one instruction per line, and <c>.labels:</c>.</summary>
    /// <param name="emit">An <c>emit { ... }</c> fragment of a const fn, where <c>name:</c> places a <c>label()</c> value.</param>
    private AsmStmt ParseAsmBody(bool isFn = false, bool emit = false)
    {
        SkipBreaksBeforeBrace();
        var start = Expect(TokenKind.LBrace, emit ? "'{' after 'emit'" : "'{' after 'asm'");
        var lines = new List<AsmLine>();
        var locals = new List<VarDecl>();
        _inAsm = true;
        while (true)
        {
            while (Accept(TokenKind.Newline)) { }
            if (At(TokenKind.RBrace)) break;
            if (At(TokenKind.Eof)) throw new CompileError(start.Pos, "asm block is not closed with '}'");
            if (At(TokenKind.Var))
            {
                if (!isFn || lines.Count > 0)
                    throw new CompileError(Current.Pos, isFn
                        ? "'var' lines go at the start of an asm fn, before the first instruction. Move this line up"
                        : "'var' is not allowed inside an asm block. Declare the variable before the block, or use 'var' lines at the start of an asm fn");
                locals.Add(ParseVar());
                continue;
            }
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
            // In an emit fragment, 'top:' places the label() value in the variable top (spec 4.1).
            if (emit && At(TokenKind.Colon))
            {
                Advance();
                lines.Add(new AsmLabel(word.Pos, word.Text));
                continue;
            }
            if (At(TokenKind.Colon))
                throw new CompileError(word.Pos, $"Labels in assembly start with '.', e.g. .{word.Text}:");
            if (!Enum.TryParse<Mnemonic>(word.Text, ignoreCase: true, out var mnemonic) || !Enum.IsDefined(mnemonic))
            {
                // A generator call on a line of its own: copy_rows(...) or tables.copy_rows(...) (spec 4.1).
                if (At(TokenKind.LParen) || (At(TokenKind.Dot) && PeekAt(1).Kind == TokenKind.Identifier && PeekAt(2).Kind == TokenKind.LParen))
                {
                    // Its arguments may name the block's .labels (spec 4.1), so the parser stays in assembly mode.
                    _pos--;
                    var call = ParsePrimary();
                    lines.Add(new AsmGeneratorCall(call.Pos, (CallExpr)call));
                }
                else
                    throw new CompileError(word.Pos, $"Unknown instruction '{word.Text}'");
            }
            else
                lines.Add(ParseAsmInstruction(word.Pos, mnemonic));

            if (Accept(TokenKind.Newline) || At(TokenKind.RBrace)) continue;
            if (At(TokenKind.Semicolon)) throw SemicolonInAsm();
            throw new CompileError(Current.Pos, $"Expected the end of the line after the instruction, found {Describe(Current)}");
        }
        Advance();
        _inAsm = false;
        var block = new AsmStmt(start.Pos, lines);
        block.Locals.AddRange(locals);
        return block;
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
}
