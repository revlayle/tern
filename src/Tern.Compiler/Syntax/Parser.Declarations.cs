using Tern.Compiler.Semantics;
using Tern.Cpu;

namespace Tern.Compiler.Syntax;

/// <summary>Declarations: the program, memory blocks, structs, variables, constants, functions and types.</summary>
public sealed partial class Parser
{
    // ---------- Declarations ----------

    private ProgramSyntax ParseProgram()
    {
        var consts = new List<ConstDecl>();
        var globals = new List<VarDecl>();
        var functions = new List<FnDecl>();
        var imports = new List<ImportDecl>();
        var structs = new List<StructDecl>();
        var constFns = new List<FnDecl>();
        var brokenNames = new List<(string, SourcePos)>();

        // Parses one declaration. After an error, its name (if it got that far) is broken, and parsing
        // goes on at the next declaration (spec 13).
        void Recover(Action parse)
        {
            int from = _pos;
            try { parse(); }
            catch (Exception e) when (Recovers(e))
            {
                Fail(e, from);
                if (DeclaredName(from) is { } broken) brokenNames.Add(broken);
                SkipDeclaration(from);
            }
        }

        var asserts = new List<AssertDecl>();
        SkipTerminators();
        ModuleHeader? header = null;
        if (At(TokenKind.Module)) Recover(() =>
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
        });
        SkipTerminators();
        while (At(TokenKind.Import))
        {
            Recover(() =>
            {
                Advance();
                var name = Expect(TokenKind.Identifier, "a module name after 'import'");
                if (!_modules.Add(name.Text))
                    throw new CompileError(name.Pos, $"'{name.Text}' is imported twice. Remove one of the imports");
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
            });
            SkipTerminators();
        }
        // Constants may come before the memory block, so its ranges can use them (spec 8.4).
        while (At(TokenKind.Const))
        {
            Recover(() =>
            {
                if (PeekAt(1).Kind == TokenKind.Fn) constFns.Add(ParseConstFnWhole());
                else consts.Add(ParseConst());
            });
            SkipTerminators();
        }
        MemoryDecl? memory = null;
        if (IsMemoryBlock())
        {
            Recover(() => memory = ParseMemory());
            SkipTerminators();
        }
        var exports = new List<ExportName>();
        ExportTable? exportTable = null;
        var privateNames = new HashSet<string>();
        while (!At(TokenKind.Eof))
        {
            Recover(() =>
            {
                if (At(TokenKind.Private))
                {
                    var p = Advance();
                    if (!(Current.Kind is TokenKind.Const or TokenKind.Var or TokenKind.Linear or TokenKind.Struct or TokenKind.Data or TokenKind.Fn
                          or TokenKind.Interrupt or TokenKind.Noinit or TokenKind.Volatile or TokenKind.Inline || IsIndexedWord() || (At(TokenKind.Asm) && PeekAt(1).Kind == TokenKind.Fn)))
                        throw new CompileError(p.Pos, $"'private' goes before a declaration (const, var, data, struct or fn); found {Describe(Current)}. Write it at the start of the declaration it hides");
                    privateNames.Add(ParseDeclaration());
                }
                else
                    ParseDeclaration();
            });
            SkipTerminators();
        }
        return new ProgramSyntax(consts, globals, functions) { Header = header, Asserts = asserts, Imports = imports, Memory = memory, Exports = exports, ExportTable = exportTable, Structs = structs, PrivateNames = privateNames, ConstFns = constFns, BrokenNames = brokenNames, HasSyntaxErrors = _failures > 0 };

        // A const fn with an error in its body is dropped whole, since its body would run at compile time.
        FnDecl ParseConstFnWhole()
        {
            int failures = _failures;
            var d = ParseConstFn();
            if (_failures > failures) throw new FollowOnError();
            return d;
        }

        // Parses one top-level declaration and returns its name.
        string ParseDeclaration()
        {
            switch (Current.Kind)
            {
                case TokenKind.Import:
                    throw new CompileError(Current.Pos, "'import' must be at the top of the file, before other declarations. Move it there");
                case TokenKind.Module:
                    throw new CompileError(Current.Pos, "'module' must be the first line of the file, before the imports. Move it there");
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
                    // export f, g @ $0900: a program with main puts its jump table there (spec 12.3).
                    if (At(TokenKind.At))
                    {
                        var at = Advance();
                        if (exportTable is not null)
                            throw new CompileError(at.Pos, "A program has one export table. Write all its functions on the 'export' line with the '@'");
                        exportTable = new ExportTable(at.Pos, ParseExpr());
                    }
                    ExpectEndOfStatement();
                    return "";
                }
                case TokenKind.Identifier when IsMemoryBlock():
                    throw new CompileError(Current.Pos, "The memory block goes after the imports and constants, before other declarations, and there is only one. Move it there, or join the two blocks");
                case TokenKind.Identifier when IsIndexedWord():
                { var d = ParseIndexedVar(); globals.Add(d); return d.Name; }
                case TokenKind.Const when PeekAt(1).Kind == TokenKind.Fn: { var d = ParseConstFnWhole(); constFns.Add(d); return d.Name; }
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
                        throw new CompileError(t.Pos, $"'inline' goes before 'fn', e.g. 'inline fn wait_frame() {{ ... }}'; found {Describe(Current)}. Write 'inline fn' or remove 'inline'");
                    var d = ParseFn(isAsm);
                    if (d.Address is not null)
                        throw new CompileError(d.Pos, "An inline function needs a body; '@ $addr' declares a routine that is already there. Remove 'inline' or the address");
                    d.IsInline = true;
                    functions.Add(d);
                    return d.Name;
                }
                case TokenKind.Noinit:
                {
                    var t = Advance();
                    if (!(Current.Kind is TokenKind.Var or TokenKind.Linear or TokenKind.Volatile or TokenKind.Noinit || IsIndexedWord()))
                        throw new CompileError(t.Pos, $"'noinit' goes before 'var', e.g. 'noinit var buffer: [u8; 256]'; found {Describe(Current)}. Write 'noinit var' or remove 'noinit'");
                    var name = ParseDeclaration();
                    var d = globals[^1];
                    if (d.IsData) throw new CompileError(t.Pos, "'noinit' is for variables; data is never zeroed. Remove 'noinit'");
                    if (d.IsNoinit) throw new CompileError(t.Pos, "'noinit' is written twice. Remove one");
                    d.IsNoinit = true;
                    return name;
                }
                case TokenKind.Volatile:
                {
                    var t = Advance();
                    if (!(Current.Kind is TokenKind.Var or TokenKind.Linear or TokenKind.Noinit or TokenKind.Volatile || IsIndexedWord()))
                        throw new CompileError(t.Pos, $"'volatile' goes before 'var', e.g. 'volatile var flags: u8 @ $C000'; found {Describe(Current)}. Write 'volatile var' or remove 'volatile'");
                    var name = ParseDeclaration();
                    var d = globals[^1];
                    if (d.IsData) throw new CompileError(t.Pos, "'volatile' is for variables; data is read-only, so nothing changes it. Remove 'volatile'");
                    if (d.IsVolatile) throw new CompileError(t.Pos, "'volatile' is written twice. Remove one");
                    d.IsVolatile = true;
                    return name;
                }
                case TokenKind.Interrupt:
                {
                    var t = Advance();
                    if (!At(TokenKind.Fn))
                        throw new CompileError(t.Pos, At(TokenKind.Asm)
                            ? "An interrupt handler in assembly is an 'asm fn' that saves what it changes and ends with rti; 'interrupt' is for handlers in Tern. Remove 'interrupt'"
                            : $"'interrupt' goes before 'fn', e.g. 'interrupt fn raster() {{ ... }}'; found {Describe(Current)}. Write 'interrupt fn' or remove 'interrupt'");
                    var d = ParseFn();
                    if (d.Address is not null)
                        throw new CompileError(d.Pos, "An interrupt handler needs a body; '@ $addr' declares a routine that is already there. Remove 'interrupt' or the address");
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
                    if (LeadingOperator() is { } lead) throw lead;
                    throw new CompileError(Current.Pos, $"Expected 'const', 'var', 'data', 'struct' or 'fn', found {Describe(Current)}. Check that statements are inside a function, and that each '{{' has its '}}'");
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
            if (At(TokenKind.Eof)) throw new CompileError(start.Pos, "The memory block is not closed with '}'. Add '}' after its last range");
            var first = ParseExpr();
            if (At(TokenKind.DotDot))
                throw new CompileError(Current.Pos, "A memory range includes its last address. Write first..=last");
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
            if (At(TokenKind.Eof)) throw new CompileError(start.Pos, $"Struct '{name.Text}' is not closed with '}}'. Add '}}' after its last field");
            var field = Expect(TokenKind.Identifier, "a field 'name: type'");
            Expect(TokenKind.Colon, "':' and the field's type");
            var type = ParseType();
            Expr? offset = Accept(TokenKind.At) ? ParseExpr() : null;
            fields.Add(new FieldSyntax(field.Pos, field.Text, type, offset));
            if (Accept(TokenKind.Comma) || At(TokenKind.Newline) || At(TokenKind.RBrace)) continue;
            throw new CompileError(Current.Pos, $"Expected ',' or a line break after the field, found {Describe(Current)}. Separate the fields with ',' or a line break");
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
            throw new CompileError(Current.Pos, $"'linear' goes before 'var' or 'data', e.g. 'linear var list: [Enemy; 8]'; found {Describe(Current)}. Write 'linear var' or remove 'linear'");
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
            if (!allowZeroPage) throw new CompileError(Current.Pos, "Data is read-only and lives in the program; '@ zp' is for variables. Remove '@ zp', or make it a var");
            Advance();
            return new Placement(PlacementKind.ZeroPage);
        }
        if (At(TokenKind.Bank))
            throw new CompileError(Current.Pos, "'@ bank(n)' is not supported yet. Remove it");
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

    /// <summary>True while the body of a <c>const fn</c> is parsed, where <c>emit { ... }</c> is a statement.</summary>
    private bool _inConstFn;

    /// <summary><c>const fn name(p: int, ...) -> type { ... }</c> (spec 4.1).</summary>
    private FnDecl ParseConstFn()
    {
        var start = Expect(TokenKind.Const, "'const'");
        _inConstFn = true;
        try
        {
            var f = ParseFn();
            if (f.Address is not null)
                throw new CompileError(f.Pos, "A const fn runs at compile time and has no address. Remove '@' and the address, and give it a body");
            return new FnDecl(start.Pos, f.Name, f.Parameters, f.ReturnType, f.ReturnUsing, f.Body) { IsConst = true };
        }
        finally { _inConstFn = false; }
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
                        throw new CompileError(Current.Pos, $"Expected a parameter 'name: type', or a fixed input such as 'true using carry'; found {Describe(Current)}. Write the parameter as 'name: type'");
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
            throw new CompileError(Current.Pos, $"'keeps' is only for routines at a fixed address: fn {name.Text}(...) @ $addr keeps X. Remove 'keeps' here");

        // A routine that already exists at a fixed address: no body.
        if (At(TokenKind.At))
        {
            var at = Advance();
            if (isAsm)
                throw new CompileError(at.Pos, "An asm fn at a fixed address is not supported yet. Remove 'asm' and the body to declare a routine that is already there, or remove the address");
            var address = ParseExpr();
            var keeps = new List<(Register, SourcePos)>();
            if (Accept(TokenKind.Keeps))
            {
                do keeps.Add((ParseRegister(), PeekAt(-1).Pos)); while (Accept(TokenKind.Comma));
            }
            if (At(TokenKind.LBrace))
                throw new CompileError(at.Pos, "A function with a body at a fixed address is not supported yet. Without a body, it declares a routine that is already there. Remove the address or the body");
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
            _ => throw new CompileError(t.Pos, $"Unknown register or flag '{t.Text}'. Use A, X, Y, AX, AY, XY, carry, zero, negative or overflow"),
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
}
