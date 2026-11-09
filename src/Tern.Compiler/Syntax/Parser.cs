using Tern.Compiler.Semantics;
using Tern.Cpu;

namespace Tern.Compiler.Syntax;

public sealed class Parser
{
    private readonly List<Token> _tokens;
    private int _pos;

    private Parser(List<Token> tokens) => _tokens = tokens;

    /// <param name="file">The source file, for positions in error messages and the listing.</param>
    public static ProgramSyntax Parse(string source, string? file = null) => new Parser(Lexer.Tokenize(source, file)).ParseProgram();

    /// <summary>Modules imported by this file. <c>name.member</c> after one of them is a qualified name.</summary>
    private readonly HashSet<string> _modules = [];

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
        {
            // A keyword where a name is meant (fn step(), var data: u8), told by the token after it.
            if (kind == TokenKind.Identifier && Lexer.IsKeyword(Current.Kind) && PeekAt(1).Kind is
                    TokenKind.LParen or TokenKind.RParen or TokenKind.Colon or TokenKind.Eq or TokenKind.LBrace or TokenKind.Comma or TokenKind.In)
                throw new CompileError(Current.Pos, $"Expected {what}, found '{Current.Text}', which is a keyword. Use another name");
            throw new CompileError(Current.Pos, $"Expected {what}, found {Describe(Current)}");
        }
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
        var imports = new List<ImportDecl>();
        var structs = new List<StructDecl>();

        var asserts = new List<AssertDecl>();
        SkipTerminators();
        ModuleHeader? header = null;
        if (At(TokenKind.Module))
        {
            var kw = Advance();
            var hname = Expect(TokenKind.Identifier, "the module name after 'module'");
            var parameters = new List<ModuleParam>();
            Expect(TokenKind.LParen, "'(' after the module name; a module without parameters needs no header");
            while (!At(TokenKind.RParen))
            {
                var pname = Expect(TokenKind.Identifier, "a parameter name");
                Expect(TokenKind.Colon, "':' and a type after the parameter name");
                var ptype = ParseType();
                Expect(TokenKind.Eq, "'= default value' (every module parameter has a default)");
                parameters.Add(new ModuleParam(pname.Pos, pname.Text, ptype, ParseExpr()));
                if (!Accept(TokenKind.Comma)) break;
            }
            Expect(TokenKind.RParen, "')'");
            header = new ModuleHeader(kw.Pos, hname.Text, parameters);
            ExpectEndOfStatement();
            SkipTerminators();
        }
        while (At(TokenKind.Import))
        {
            Advance();
            var name = Expect(TokenKind.Identifier, "a module name after 'import'");
            if (!_modules.Add(name.Text))
                throw new CompileError(name.Pos, $"'{name.Text}' is imported twice");
            var args = new List<ImportArg>();
            if (Accept(TokenKind.LParen))
            {
                while (!At(TokenKind.RParen))
                {
                    var aname = Expect(TokenKind.Identifier, "a parameter name");
                    Expect(TokenKind.Eq, "'= value' after the parameter name");
                    args.Add(new ImportArg(aname.Pos, aname.Text, ParseExpr()));
                    if (!Accept(TokenKind.Comma)) break;
                }
                Expect(TokenKind.RParen, "')'");
            }
            string? area = null;
            var areaPos = default(SourcePos);
            if (Accept(TokenKind.At))
            {
                var areaName = Expect(TokenKind.Identifier, "the name of an overlay area after '@'");
                area = areaName.Text;
                areaPos = areaName.Pos;
            }
            imports.Add(new ImportDecl(name.Pos, name.Text) { Args = args, Area = area, AreaPos = areaPos });
            ExpectEndOfStatement();
            SkipTerminators();
        }
        // Constants may come before the memory block, so its ranges can use them (spec 8.4).
        while (At(TokenKind.Const))
        {
            consts.Add(ParseConst());
            SkipTerminators();
        }
        MemoryDecl? memory = null;
        if (IsMemoryBlock())
        {
            memory = ParseMemory();
            SkipTerminators();
        }
        var exports = new List<ExportName>();
        var privateNames = new HashSet<string>();
        while (!At(TokenKind.Eof))
        {
            if (At(TokenKind.Private))
            {
                var p = Advance();
                if (!(Current.Kind is TokenKind.Const or TokenKind.Var or TokenKind.Linear or TokenKind.Struct or TokenKind.Data or TokenKind.Fn
                      or TokenKind.Interrupt or TokenKind.Noinit or TokenKind.Volatile or TokenKind.Inline || IsIndexedWord() || (At(TokenKind.Asm) && PeekAt(1).Kind == TokenKind.Fn)))
                    throw new CompileError(p.Pos, $"'private' goes before a declaration (const, var, data, struct or fn); found {Describe(Current)}");
                privateNames.Add(ParseDeclaration());
            }
            else
                ParseDeclaration();
            SkipTerminators();
        }
        return new ProgramSyntax(consts, globals, functions) { Header = header, Asserts = asserts, Imports = imports, Memory = memory, Exports = exports, Structs = structs, PrivateNames = privateNames };

        // Parses one top-level declaration and returns its name.
        string ParseDeclaration()
        {
            switch (Current.Kind)
            {
                case TokenKind.Import:
                    throw new CompileError(Current.Pos, "'import' must be at the top of the file, before other declarations");
                case TokenKind.Module:
                    throw new CompileError(Current.Pos, "'module' must be the first line of the file, before the imports");
                case TokenKind.Assert:
                {
                    var t = Advance();
                    var cond = ParseExpr();
                    Expect(TokenKind.Comma, "',' and a message string after the condition");
                    var msg = Expect(TokenKind.String, "a message string");
                    ExpectEndOfStatement();
                    asserts.Add(new AssertDecl(t.Pos, cond, msg.Text));
                    return "";
                }
                case TokenKind.Export:
                {
                    Advance();
                    // export fn f() { } (Rust's pub fn, JavaScript's export function): export only lists names.
                    if (At(TokenKind.Fn))
                    {
                        string name = PeekAt(1).Kind == TokenKind.Identifier ? PeekAt(1).Text : "f";
                        throw new CompileError(Current.Pos,
                            $"'export' lists functions declared elsewhere, so it is not followed by 'fn'. Remove 'fn' here and declare the function on its own: 'export {name}' and 'fn {name}() {{ }}'");
                    }
                    do
                    {
                        var f = Expect(TokenKind.Identifier, "a function name after 'export'");
                        exports.Add(new ExportName(f.Pos, f.Text));
                    } while (Accept(TokenKind.Comma));
                    ExpectEndOfStatement();
                    return "";
                }
                case TokenKind.Identifier when IsMemoryBlock():
                    throw new CompileError(Current.Pos, "The memory block goes after the imports and constants, before other declarations, and there is only one");
                case TokenKind.Identifier when IsIndexedWord():
                { var d = ParseIndexedVar(); globals.Add(d); return d.Name; }
                case TokenKind.Const: { var d = ParseConst(); consts.Add(d); return d.Name; }
                case TokenKind.Var: { var d = ParseVar(); globals.Add(d); return d.Name; }
                case TokenKind.Linear: { var d = ParseLinearVar(); globals.Add(d); return d.Name; }
                case TokenKind.Struct: { var d = ParseStruct(); structs.Add(d); return d.Name; }
                case TokenKind.Data: { var d = ParseData(); globals.Add(d); return d.Name; }
                case TokenKind.Fn: { var d = ParseFn(); functions.Add(d); return d.Name; }
                case TokenKind.Inline:
                {
                    var t = Advance();
                    bool isAsm = At(TokenKind.Asm) && PeekAt(1).Kind == TokenKind.Fn;
                    if (isAsm) Advance();
                    if (!At(TokenKind.Fn))
                        throw new CompileError(t.Pos, $"'inline' goes before 'fn', e.g. 'inline fn wait_frame() {{ ... }}'; found {Describe(Current)}");
                    var d = ParseFn(isAsm);
                    if (d.Address is not null)
                        throw new CompileError(d.Pos, "An inline function needs a body; '@ $addr' declares a routine that is already there");
                    d.IsInline = true;
                    functions.Add(d);
                    return d.Name;
                }
                case TokenKind.Noinit:
                {
                    var t = Advance();
                    if (!(Current.Kind is TokenKind.Var or TokenKind.Linear or TokenKind.Volatile or TokenKind.Noinit || IsIndexedWord()))
                        throw new CompileError(t.Pos, $"'noinit' goes before 'var', e.g. 'noinit var buffer: [u8; 256]'; found {Describe(Current)}");
                    var name = ParseDeclaration();
                    var d = globals[^1];
                    if (d.IsData) throw new CompileError(t.Pos, "'noinit' is for variables; data is never zeroed");
                    if (d.IsNoinit) throw new CompileError(t.Pos, "'noinit' is written twice");
                    d.IsNoinit = true;
                    return name;
                }
                case TokenKind.Volatile:
                {
                    var t = Advance();
                    if (!(Current.Kind is TokenKind.Var or TokenKind.Linear or TokenKind.Noinit or TokenKind.Volatile || IsIndexedWord()))
                        throw new CompileError(t.Pos, $"'volatile' goes before 'var', e.g. 'volatile var flags: u8 @ $C000'; found {Describe(Current)}");
                    var name = ParseDeclaration();
                    var d = globals[^1];
                    if (d.IsData) throw new CompileError(t.Pos, "'volatile' is for variables; data is read-only, so nothing changes it");
                    if (d.IsVolatile) throw new CompileError(t.Pos, "'volatile' is written twice");
                    d.IsVolatile = true;
                    return name;
                }
                case TokenKind.Interrupt:
                {
                    var t = Advance();
                    if (!At(TokenKind.Fn))
                        throw new CompileError(t.Pos, At(TokenKind.Asm)
                            ? "An interrupt handler in assembly is an 'asm fn' that saves what it changes and ends with rti; 'interrupt' is for handlers in Tern"
                            : $"'interrupt' goes before 'fn', e.g. 'interrupt fn raster() {{ ... }}'; found {Describe(Current)}");
                    var d = ParseFn();
                    if (d.Address is not null)
                        throw new CompileError(d.Pos, "An interrupt handler needs a body; '@ $addr' declares a routine that is already there");
                    d.IsInterrupt = true;
                    functions.Add(d);
                    return d.Name;
                }
                case TokenKind.Asm when PeekAt(1).Kind == TokenKind.Fn:
                {
                    Advance();
                    var d = ParseFn(isAsm: true);
                    functions.Add(d);
                    return d.Name;
                }
                case TokenKind.Asm:
                    throw new CompileError(Current.Pos,
                        "An asm block must be inside a function. Use 'asm fn name() { ... }' for a whole function in assembly");
                default:
                    throw new CompileError(Current.Pos, $"Expected 'const', 'var', 'data', 'struct' or 'fn', found {Describe(Current)}");
            }
        }
    }

    private bool IsMemoryBlock()
    {
        if (!At(TokenKind.Identifier) || Current.Text != "memory") return false;
        int i = 1;
        while (PeekAt(i).Kind == TokenKind.Newline) i++;
        return PeekAt(i).Kind == TokenKind.LBrace;
    }

    /// <summary>Where a block must follow a header, line breaks before the '{' are skipped (spec 2).</summary>
    private void SkipBreaksBeforeBrace()
    {
        int i = 0;
        while (PeekAt(i).Kind == TokenKind.Newline) i++;
        if (PeekAt(i).Kind == TokenKind.LBrace) _pos += i;
    }

    /// <summary><c>memory { first..=last kind [name] ... }</c> (spec 8.4). The kinds are words only inside the block.</summary>
    private MemoryDecl ParseMemory()
    {
        var start = Advance();
        SkipBreaksBeforeBrace();
        Expect(TokenKind.LBrace, "'{'");
        var lines = new List<MemoryLine>();
        SkipTerminators();
        while (!At(TokenKind.RBrace))
        {
            if (At(TokenKind.Eof)) throw new CompileError(start.Pos, "The memory block is not closed with '}'");
            var first = ParseExpr();
            if (At(TokenKind.DotDot))
                throw new CompileError(Current.Pos, "A memory range includes its last address: write first..=last");
            Expect(TokenKind.DotDotEq, "'..=' and the last address of the range");
            var last = ParseExpr();
            var kind = At(TokenKind.Volatile) ? Advance() : Expect(TokenKind.Identifier, "the kind of memory: program, zeropage, reserved, region, volatile or overlay");
            if (kind.Text is not ("program" or "zeropage" or "reserved" or "region" or "volatile" or "overlay"))
                throw new CompileError(kind.Pos, $"Unknown kind of memory '{kind.Text}'. Use program, zeropage, reserved, region, volatile or overlay");
            string? name = null;
            if (kind.Text is "region" or "overlay")
                name = Expect(TokenKind.Identifier, $"a name after '{kind.Text}'").Text;
            lines.Add(new MemoryLine(kind.Pos, first, last, kind.Text, name));
            // The lexer ends no statement after the keyword 'volatile'.
            if (kind.Kind == TokenKind.Volatile) Accept(TokenKind.Semicolon);
            else ExpectEndOfStatement();
            SkipTerminators();
        }
        Expect(TokenKind.RBrace, "'}'");
        ExpectEndOfStatement();
        return new MemoryDecl(start.Pos, lines);
    }

    /// <summary><c>struct Name { field: type, field: type @ offset }</c>; fields are separated by ',' or line breaks.</summary>
    private StructDecl ParseStruct()
    {
        var start = Expect(TokenKind.Struct, "'struct'");
        var name = Expect(TokenKind.Identifier, "struct name");
        SkipBreaksBeforeBrace();
        Expect(TokenKind.LBrace, "'{' after the struct name");
        var fields = new List<FieldSyntax>();
        while (true)
        {
            while (Accept(TokenKind.Newline)) { }
            if (Accept(TokenKind.RBrace)) break;
            if (At(TokenKind.Eof)) throw new CompileError(start.Pos, $"Struct '{name.Text}' is not closed with '}}'");
            var field = Expect(TokenKind.Identifier, "a field 'name: type'");
            Expect(TokenKind.Colon, "':' and the field's type");
            var type = ParseType();
            Expr? offset = Accept(TokenKind.At) ? ParseExpr() : null;
            fields.Add(new FieldSyntax(field.Pos, field.Text, type, offset));
            if (Accept(TokenKind.Comma) || At(TokenKind.Newline) || At(TokenKind.RBrace)) continue;
            throw new CompileError(Current.Pos, $"Expected ',' or a line break after the field, found {Describe(Current)}");
        }
        ExpectEndOfStatement();
        return new StructDecl(start.Pos, name.Text, fields);
    }

    /// <summary><c>indexed</c> is a word only before <c>var</c> (spec 3.3), so it is still a usable name elsewhere.</summary>
    private bool IsIndexedWord() => At(TokenKind.Identifier) && Current.Text == "indexed" && PeekAt(1).Kind is TokenKind.Var or TokenKind.Linear or TokenKind.Data;

    /// <summary><c>indexed var name: [T; n]</c></summary>
    private VarDecl ParseIndexedVar()
    {
        var word = Advance();
        if (At(TokenKind.Linear))
            throw new CompileError(word.Pos, "'indexed' replaces 'linear': it stores records the same way. Write 'indexed var', not 'indexed linear var'");
        if (At(TokenKind.Data))
            throw new CompileError(word.Pos, "'indexed' is for arrays of structs, which cannot be data. Write 'indexed var list: [Enemy; 8]'");
        var decl = ParseVar();
        return new VarDecl(word.Pos, decl.Name, decl.TypeSyntax, decl.Placement, decl.Init) { IsIndexed = true };
    }

    /// <summary><c>linear var name: [T; n]</c> or <c>linear data name: [T; n] = ...</c></summary>
    private VarDecl ParseLinearVar()
    {
        var linear = Expect(TokenKind.Linear, "'linear'");
        if (At(TokenKind.Identifier) && Current.Text == "indexed")
            throw new CompileError(Current.Pos, "'indexed' replaces 'linear': it stores records the same way. Write 'indexed var', not 'linear indexed var'");
        if (!At(TokenKind.Var) && !At(TokenKind.Data))
            throw new CompileError(Current.Pos, $"'linear' goes before 'var' or 'data', e.g. 'linear var list: [Enemy; 8]'; found {Describe(Current)}");
        var decl = At(TokenKind.Data) ? ParseData() : ParseVar();
        return new VarDecl(linear.Pos, decl.Name, decl.TypeSyntax, decl.Placement, decl.Init) { IsLinear = true, IsData = decl.IsData };
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

        var placement = ParsePlacement(allowZeroPage: true);

        Expr? init = Accept(TokenKind.Eq) ? ParseExpr() : null;
        ExpectEndOfStatement();
        return new VarDecl(start.Pos, name.Text, type, placement, init);
    }

    /// <summary>The optional <c>@ zp</c>, <c>@ address</c> or <c>@ region</c> after a variable's type.</summary>
    private Placement ParsePlacement(bool allowZeroPage)
    {
        if (!Accept(TokenKind.At)) return Placement.None;
        if (At(TokenKind.Zp))
        {
            if (!allowZeroPage) throw new CompileError(Current.Pos, "Data is read-only and lives in the program; '@ zp' is for variables");
            Advance();
            return new Placement(PlacementKind.ZeroPage);
        }
        if (At(TokenKind.Bank))
            throw new CompileError(Current.Pos, "'@ bank(n)' is not supported yet");
        return new Placement(PlacementKind.Address, ParseExpr());
    }

    private VarDecl ParseData()
    {
        var start = Expect(TokenKind.Data, "'data'");
        var name = Expect(TokenKind.Identifier, "data name");
        TypeSyntax? type = Accept(TokenKind.Colon) ? ParseType() : null;
        var placement = ParsePlacement(allowZeroPage: false);
        Expect(TokenKind.Eq, "'=' and the data's value");
        var init = ParseExpr();
        ExpectEndOfStatement();
        return new VarDecl(start.Pos, name.Text, type, placement, init) { IsData = true };
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
                // A fixed input: a constant in place of the name, e.g. "true using carry".
                if (!((At(TokenKind.Identifier) || Lexer.IsKeyword(Current.Kind)) && PeekAt(1).Kind == TokenKind.Colon))
                {
                    var value = ParseExpr();
                    if (!At(TokenKind.Using))
                        throw new CompileError(Current.Pos, $"Expected a parameter 'name: type', or a fixed input such as 'true using carry'; found {Describe(Current)}");
                    Advance();
                    parameters.Add(new ParamSyntax(value.Pos, "", new NamedTypeSyntax(value.Pos, ""), ParseRegister()) { FixedValue = value });
                    continue;
                }
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

        var body = isAsm ? new BlockStmt(Current.Pos, [ParseAsmBody(isFn: true)]) : ParseBlock();
        return new FnDecl(start.Pos, name.Text, parameters, returnType, returnUsing, body) { IsAsm = isAsm };
    }

    private Register ParseRegister()
    {
        var t = Expect(TokenKind.Identifier, "a register or flag (A, X, Y, AX, AY, XY, carry, zero, negative, overflow)");
        return t.Text switch
        {
            "A" => Register.A, "X" => Register.X, "Y" => Register.Y,
            "AX" => Register.AX, "AY" => Register.AY, "XY" => Register.XY,
            "carry" => Register.Carry, "zero" => Register.Zero, "negative" => Register.Negative,
            "overflow" => Register.Overflow,
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
            Expr? length = null;
            if (At(TokenKind.Identifier) && Current.Text == "_") Advance();
            else length = ParseExpr();
            Expect(TokenKind.RBracket, "']'");
            return new ArrayTypeSyntax(start.Pos, element, length);
        }
        var name = Expect(TokenKind.Identifier, "a type");
        if (name.Text == "ptr" && (At(TokenKind.Identifier) || At(TokenKind.LBracket)))
            return new PointerTypeSyntax(name.Pos, ParseType());
        // A struct type in a module: c64.Vic (the checker reports a missing import).
        if (At(TokenKind.Dot) && PeekAt(1).Kind == TokenKind.Identifier)
        {
            Advance();
            return new NamedTypeSyntax(name.Pos, name.Text + "." + Expect(TokenKind.Identifier, $"a type name in module '{name.Text}' after '.'").Text);
        }
        return new NamedTypeSyntax(name.Pos, name.Text);
    }

    // ---------- Statements ----------

    private BlockStmt ParseBlock()
    {
        SkipBreaksBeforeBrace();
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
                var cond = ParseExpr();
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

    /// <summary>
    /// <c>match x { arm... }</c>; one arm per line: patterns separated by ',', then '=>' and a block
    /// or a single statement.
    /// </summary>
    private MatchStmt ParseMatch(bool indexed = false)
    {
        var start = Expect(TokenKind.Match, "'match'");
        var subject = ParseExpr();
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
    private AsmStmt ParseAsmBody(bool isFn = false)
    {
        SkipBreaksBeforeBrace();
        var start = Expect(TokenKind.LBrace, "'{' after 'asm'");
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
                        ? "'var' lines go at the start of an inline asm fn, before the first instruction"
                        : "'var' is not allowed inside an asm block. Declare the variable before the block, or use 'var' lines at the start of an inline asm fn");
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
                string name = t.Text;
                // A name in an imported module: text.put
                // Also name.member(...) when name is not imported, so that the checker can say to import it.
                if (At(TokenKind.Dot) && (_modules.Contains(name)
                    || (PeekAt(1).Kind == TokenKind.Identifier && PeekAt(2).Kind == TokenKind.LParen)))
                {
                    Advance();
                    name += "." + Expect(TokenKind.Identifier, $"a name in module '{t.Text}' after '.'").Text;
                }
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
                throw new CompileError(t.Pos, $"Expected an expression, found {Describe(t)}");
        }
    }
}
