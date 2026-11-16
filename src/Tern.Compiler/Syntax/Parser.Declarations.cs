using Tern.Compiler.Semantics;
using Tern.Cpu;

namespace Tern.Compiler.Syntax;

/// <summary>Declarations: the program, memory blocks, structs, variables, constants, functions and types.</summary>
public sealed partial class Parser
{
    // ---------- Declarations ----------

    /// <summary>The declarations of one file, filed by kind as they are parsed.</summary>
    private sealed class FileDeclarations
    {
        public ModuleHeader? Header;
        public MemoryDecl? Memory;
        public ExportTable? ExportTable;
        public readonly List<ConstDecl> Consts = [];
        public readonly List<VarDecl> Globals = [];
        public readonly List<FnDecl> Functions = [];
        public readonly List<ImportDecl> Imports = [];
        public readonly List<StructDecl> Structs = [];
        public readonly List<FnDecl> ConstFns = [];
        public readonly List<EncodingDecl> Encodings = [];
        public readonly List<DefaultEncoding> DefaultEncodings = [];
        public readonly List<AssertDecl> Asserts = [];
        public readonly List<ExportName> Exports = [];
        public readonly HashSet<string> PrivateNames = [];
        public readonly List<(string, SourcePos)> BrokenNames = [];

        /// <summary>Files a declaration under its kind and returns its name ("" for one without a name).</summary>
        public string Add(Node declaration)
        {
            switch (declaration)
            {
                case ConstDecl d: Consts.Add(d); return d.Name;
                case VarDecl d: Globals.Add(d); return d.Name;
                case FnDecl { IsConst: true } d: ConstFns.Add(d); return d.Name;
                case FnDecl d: Functions.Add(d); return d.Name;
                case StructDecl d: Structs.Add(d); return d.Name;
                case EncodingDecl d: Encodings.Add(d); return d.Name;
                case AssertDecl d: Asserts.Add(d); return "";
                default: throw new InvalidOperationException($"No list for {declaration.GetType().Name}");
            }
        }

        public ProgramSyntax Program(bool hasSyntaxErrors) => new(Consts, Globals, Functions)
        {
            Header = Header, Asserts = Asserts, Imports = Imports, Memory = Memory, Exports = Exports, ExportTable = ExportTable, Structs = Structs,
            PrivateNames = PrivateNames, ConstFns = ConstFns, Encodings = Encodings, DefaultEncodings = DefaultEncodings, BrokenNames = BrokenNames,
            HasSyntaxErrors = hasSyntaxErrors,
        };
    }

    /// <summary>
    /// A file: the module header, the imports, the constants, the memory block, then the other declarations (spec 2.2, 8.4).
    /// After an error in a declaration, its name (if it got that far) is broken, and parsing goes on at the next one (spec 13).
    /// </summary>
    private ProgramSyntax ParseProgram()
    {
        var file = new FileDeclarations();
        void Recover(Action parse)
        {
            int from = _pos;
            try { parse(); }
            catch (Exception e) when (Recovers(e))
            {
                Fail(e, from);
                if (DeclaredName(from) is { } broken) file.BrokenNames.Add(broken);
                SkipDeclaration(from);
            }
        }

        SkipTerminators();
        // The header and an import are kept when only their line's end is wrong.
        if (At(TokenKind.Module)) Recover(() =>
        {
            file.Header = ParseModuleHeader();
            ExpectEndOfStatement();
        });
        SkipTerminators();
        while (At(TokenKind.Import))
        {
            Recover(() =>
            {
                file.Imports.Add(ParseImport());
                ExpectEndOfStatement();
            });
            SkipTerminators();
        }
        // Constants may come before the memory block, so its ranges can use them (spec 8.4).
        while (At(TokenKind.Const))
        {
            Recover(() => file.Add(PeekAt(1).Kind == TokenKind.Fn ? ParseConstFnWhole() : ParseConst()));
            SkipTerminators();
        }
        if (IsMemoryBlock())
        {
            Recover(() => file.Memory = ParseMemory());
            SkipTerminators();
        }
        while (!At(TokenKind.Eof))
        {
            Recover(() =>
            {
                bool hidden = ParsePrivate();
                if (ParseDeclaration(file) is { } declaration)
                {
                    string name = file.Add(declaration);
                    if (hidden) file.PrivateNames.Add(name);
                }
            });
            SkipTerminators();
        }
        return file.Program(hasSyntaxErrors: _failures > 0);
    }

    /// <summary><c>module name(P: type = default, ...)</c>, the first line of a module with parameters, up to the end of its line.</summary>
    private ModuleHeader ParseModuleHeader()
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
        return new ModuleHeader(kw.Pos, hname.Text, parameters);
    }

    /// <summary><c>import name(P = value) @ area[bank] implements a, b</c> (spec 2.2, 8.5), up to the end of its line.</summary>
    private ImportDecl ParseImport()
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
        Expr? bank = null;
        if (Accept(TokenKind.At))
        {
            var areaName = Expect(TokenKind.Identifier, "the name of an overlay or bank area after '@'");
            area = areaName.Text;
            areaPos = areaName.Pos;
            // @ area[n]: bank n of a bank area (spec 8.5).
            if (Accept(TokenKind.LBracket))
            {
                bank = ParseExpr();
                Expect(TokenKind.RBracket, "']' after the bank's number");
            }
        }
        var implements = new List<(string, SourcePos)>();
        if (Accept(TokenKind.Implements))
            do
            {
                var iname = Expect(TokenKind.Identifier, "the name of an interface module after 'implements'");
                implements.Add((iname.Text, iname.Pos));
            } while (Accept(TokenKind.Comma));
        return new ImportDecl(name.Pos, name.Text) { Args = args, Area = area, AreaPos = areaPos, Bank = bank, Implements = implements };
    }

    /// <summary>Reads <c>private</c> before a declaration, if it is there (spec 2.2).</summary>
    private bool ParsePrivate()
    {
        if (!At(TokenKind.Private)) return false;
        var p = Advance();
        if (!(Current.Kind is TokenKind.Const or TokenKind.Var or TokenKind.Linear or TokenKind.Struct or TokenKind.Data or TokenKind.Fn
              or TokenKind.Interrupt or TokenKind.Noinit or TokenKind.Volatile or TokenKind.Inline || IsIndexedWord() || IsEncodingWord() || (At(TokenKind.Asm) && PeekAt(1).Kind == TokenKind.Fn)))
            throw new CompileError(p.Pos, $"'private' goes before a declaration (const, var, data, struct or fn); found {Describe(Current)}. Write it at the start of the declaration it hides");
        return true;
    }

    /// <summary>A const fn with an error in its body is dropped whole, since its body would run at compile time.</summary>
    private FnDecl ParseConstFnWhole()
    {
        int failures = _failures;
        var d = ParseConstFn();
        if (_failures > failures) throw new FollowOnError();
        return d;
    }

    /// <summary>
    /// One top-level declaration after the memory block, with its modifiers. An <c>export</c> or
    /// <c>default encoding</c> line is filed as it is read, so that what came before an error in it counts
    /// (spec 13); for those it returns null.
    /// </summary>
    private Node? ParseDeclaration(FileDeclarations file)
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
                return new AssertDecl(t.Pos, cond, msg.Text);
            }
            case TokenKind.Export:
                ParseExport(file);
                return null;
            case TokenKind.Identifier when IsMemoryBlock():
                throw new CompileError(Current.Pos, "The memory block goes after the imports and constants, before other declarations, and there is only one. Move it there, or join the two blocks");
            case TokenKind.Identifier when IsIndexedWord(): return ParseIndexedVar();
            case TokenKind.Identifier when IsEncodingWord(): return ParseEncoding();
            case TokenKind.Identifier when IsDefaultEncoding():
            {
                var word = Advance();
                Advance();
                var (name, pos) = ParseEncodingName("the name of an encoding after 'default encoding'");
                file.DefaultEncodings.Add(new DefaultEncoding(word.Pos, name, pos));
                ExpectEndOfStatement();
                return null;
            }
            case TokenKind.Const when PeekAt(1).Kind == TokenKind.Fn: return ParseConstFnWhole();
            case TokenKind.Const: return ParseConst();
            case TokenKind.Var: return ParseVar();
            case TokenKind.Linear: return ParseLinearVar();
            case TokenKind.Struct: return ParseStruct();
            case TokenKind.Data: return ParseData();
            case TokenKind.Fn: return ParseFn();
            case TokenKind.Noinit or TokenKind.Volatile: return ParseModifiedVar();
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
                return d;
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
                return d;
            }
            case TokenKind.Asm when PeekAt(1).Kind == TokenKind.Fn:
                Advance();
                return ParseFn(isAsm: true);
            case TokenKind.Asm:
                throw new CompileError(Current.Pos,
                    "An asm block must be inside a function. Use 'asm fn name() { ... }' for a whole function in assembly");
            default:
                if (LeadingOperator() is { } lead) throw lead;
                throw new CompileError(Current.Pos, $"Expected 'const', 'var', 'data', 'struct' or 'fn', found {Describe(Current)}. Check that statements are inside a function, and that each '{{' has its '}}'");
        }
    }

    /// <summary>
    /// <c>noinit</c> and <c>volatile</c> before a variable (spec 4), in any order: the words, then the
    /// variable, then the words' checks, from the one next to the variable outwards.
    /// </summary>
    private VarDecl ParseModifiedVar()
    {
        var words = new List<Token>();
        while (Current.Kind is TokenKind.Noinit or TokenKind.Volatile)
        {
            var t = Advance();
            if (!(Current.Kind is TokenKind.Var or TokenKind.Linear or TokenKind.Noinit or TokenKind.Volatile || IsIndexedWord()))
                throw new CompileError(t.Pos, t.Kind == TokenKind.Noinit
                    ? $"'noinit' goes before 'var', e.g. 'noinit var buffer: [u8; 256]'; found {Describe(Current)}. Write 'noinit var' or remove 'noinit'"
                    : $"'volatile' goes before 'var', e.g. 'volatile var flags: u8 @ $C000'; found {Describe(Current)}. Write 'volatile var' or remove 'volatile'");
            words.Add(t);
        }
        var d = At(TokenKind.Var) ? ParseVar() : At(TokenKind.Linear) ? ParseLinearVar() : ParseIndexedVar();
        // An error here drops the declaration, so it is not checked too (spec 13: one error per declaration; review code-quality-syntax 18).
        foreach (var t in Enumerable.Reverse(words))
        {
            if (t.Kind == TokenKind.Noinit)
            {
                if (d.IsData) throw new CompileError(t.Pos, "'noinit' is for variables; data is never zeroed. Remove 'noinit'");
                if (d.IsNoinit) throw new CompileError(t.Pos, "'noinit' is written twice. Remove one");
                d.IsNoinit = true;
            }
            else
            {
                if (d.IsData) throw new CompileError(t.Pos, "'volatile' is for variables; data is read-only, so nothing changes it. Remove 'volatile'");
                if (d.IsVolatile) throw new CompileError(t.Pos, "'volatile' is written twice. Remove one");
                d.IsVolatile = true;
            }
        }
        return d;
    }

    /// <summary><c>export f, g</c>, or <c>export f, g @ $0900</c> in a program with main (spec 12.3).</summary>
    private void ParseExport(FileDeclarations file)
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
            file.Exports.Add(new ExportName(f.Pos, f.Text));
        } while (Accept(TokenKind.Comma));
        // export f, g @ $0900: a program with main puts its jump table there (spec 12.3).
        if (At(TokenKind.At))
        {
            var at = Advance();
            if (file.ExportTable is not null)
                throw new CompileError(at.Pos, "A program has one export table. Write all its functions on the 'export' line with the '@'");
            file.ExportTable = new ExportTable(at.Pos, ParseExpr());
        }
        ExpectEndOfStatement();
    }

    private bool IsMemoryBlock()
    {
        if (!AtWord(Word.Memory)) return false;
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

    /// <summary><c>memory { [cartridge board option...] first..=last kind [name] ... }</c> (spec 8.4). The kinds are words only inside the block.</summary>
    private MemoryDecl ParseMemory()
    {
        var start = Advance();
        SkipBreaksBeforeBrace();
        Expect(TokenKind.LBrace, "'{'");
        var lines = new List<MemoryLine>();
        CartridgeLine? cartridge = null;
        SkipTerminators();
        while (!At(TokenKind.RBrace))
        {
            if (At(TokenKind.Eof)) throw new CompileError(start.Pos, "The memory block is not closed with '}'. Add '}' after its last range");
            // cartridge board option...: before the ranges, at most once (spec 8.4).
            if (AtWord(Word.Cartridge) && PeekAt(1).Kind == TokenKind.Identifier)
            {
                var word = Advance();
                if (cartridge is not null)
                    throw new CompileError(word.Pos, "The memory block has a second 'cartridge' line. Remove one: a build is an image for one board");
                if (lines.Count > 0)
                    throw new CompileError(word.Pos, "The 'cartridge' line comes before the ranges of the memory block. Move it to the top of the block");
                var board = Advance();
                var options = new List<(string, SourcePos)>();
                while (At(TokenKind.Identifier)) { var o = Advance(); options.Add((o.Text, o.Pos)); }
                cartridge = new CartridgeLine(word.Pos, board.Text, board.Pos, options);
                ExpectEndOfStatement();
                SkipTerminators();
                continue;
            }
            var first = ParseExpr();
            if (At(TokenKind.DotDot))
                throw new CompileError(Current.Pos, "A memory range includes its last address. Write first..=last");
            Expect(TokenKind.DotDotEq, "'..=' and the last address of the range");
            var last = ParseExpr();
            var kind = At(TokenKind.Volatile) ? Advance() : Expect(TokenKind.Identifier, $"the kind of memory: {MemoryKinds.List}");
            if (MemoryKinds.Named(kind.Text) is not { } memoryKind)
                throw new CompileError(kind.Pos, $"Unknown kind of memory '{kind.Text}'. Use {MemoryKinds.List}");
            string? name = memoryKind.HasName() ? Expect(TokenKind.Identifier, $"a name after '{kind.Text}'").Text : null;
            lines.Add(new MemoryLine(kind.Pos, first, last, memoryKind, name));
            // The lexer ends no statement after the keyword 'volatile'.
            if (kind.Kind == TokenKind.Volatile) Accept(TokenKind.Semicolon);
            else ExpectEndOfStatement();
            SkipTerminators();
        }
        Expect(TokenKind.RBrace, "'}'");
        ExpectEndOfStatement();
        return new MemoryDecl(start.Pos, lines) { Cartridge = cartridge };
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
            bool placed = Accept(TokenKind.At);
            if (placed && AtWord(Word.Align) && PeekAt(1).Kind == TokenKind.LParen)
                throw new CompileError(Current.Pos, "A struct field's '@' is its offset from the start of the struct, so it cannot be aligned. Write the offset, as in '@ 2'");
            Expr? offset = placed ? ParseExpr() : null;
            fields.Add(new FieldSyntax(field.Pos, field.Text, type, offset));
            if (Accept(TokenKind.Comma) || At(TokenKind.Newline) || At(TokenKind.RBrace)) continue;
            throw new CompileError(Current.Pos, $"Expected ',' or a line break after the field, found {Describe(Current)}. Separate the fields with ',' or a line break");
        }
        ExpectEndOfStatement();
        return new StructDecl(start.Pos, name.Text, fields);
    }

    /// <summary><c>encoding</c> is a word only at the start of a top-level declaration (spec 2.1), so it is still a usable name elsewhere.</summary>
    private bool IsEncodingWord() => AtWord(Word.Encoding) && PeekAt(1).Kind == TokenKind.Identifier;

    /// <summary><c>default</c> is a word only before <c>encoding</c> (spec 2.1).</summary>
    private bool IsDefaultEncoding() => AtWord(Word.Default) && Word.Is(PeekAt(1), Word.Encoding);

    /// <summary>An encoding's name, <c>tiles</c> or <c>font.tiles</c>, and where it starts.</summary>
    private (string Name, SourcePos Pos) ParseEncodingName(string what)
    {
        var first = Expect(TokenKind.Identifier, what);
        if (!Accept(TokenKind.Dot)) return (first.Text, first.Pos);
        var member = Expect(TokenKind.Identifier, $"the name of an encoding in {first.Text} after '.'");
        return ($"{first.Text}.{member.Text}", first.Pos);
    }

    /// <summary><c>encoding name: base { 'c' = value, 'a'..='z' = value }</c> (spec 2.1).</summary>
    private EncodingDecl ParseEncoding()
    {
        var start = Advance();
        var name = Expect(TokenKind.Identifier, "the encoding's name");
        string? baseName = null;
        var basePos = default(SourcePos);
        if (Accept(TokenKind.Colon)) (baseName, basePos) = ParseEncodingName("the name of the encoding it is based on after ':'");
        SkipBreaksBeforeBrace();
        Expect(TokenKind.LBrace, "'{' after the encoding's name");
        var entries = new List<EncodingEntry>();
        while (true)
        {
            while (Accept(TokenKind.Newline)) { }
            if (Accept(TokenKind.RBrace)) break;
            if (At(TokenKind.Eof) || StartsCurrentDeclaration()) throw new CompileError(start.Pos, $"Encoding '{name.Text}' is not closed with '}}'. Add '}}' after its last line");
            var first = EntryChar();
            char last = first.Text[0];
            if (At(TokenKind.DotDot))
                throw new CompileError(Current.Pos, "A range of characters includes its last one. Write '..=', as in 'a'..='z'");
            if (Accept(TokenKind.DotDotEq))
            {
                var lastToken = EntryChar();
                last = lastToken.Text[0];
                if (last < first.Text[0])
                    throw new CompileError(first.Pos, $"The range '{first.Text}'..='{lastToken.Text}' goes down. Write the lower character first: '{lastToken.Text}'..='{first.Text}'");
            }
            Expect(TokenKind.Eq, "'= byte' after the character");
            entries.Add(new EncodingEntry(first.Pos, first.Text[0], last, ParseExpr()));
            if (Accept(TokenKind.Comma) || At(TokenKind.Newline) || At(TokenKind.RBrace)) continue;
            throw new CompileError(Current.Pos, $"Expected ',' or a line break after the byte, found {Describe(Current)}. Separate the lines with ',' or a line break");
        }
        ExpectEndOfStatement();
        return new EncodingDecl(start.Pos, name.Text, baseName, basePos, entries);

        Token EntryChar()
        {
            var t = Expect(TokenKind.Char, "a character in quotes, such as 'A'");
            if (t.Encoding is not null)
                throw new CompileError(t.Pos, $"An encoding lists characters, not bytes of another encoding. Remove '{t.Encoding}' before the quote");
            if (Lexer.IsRawByte(t.Text[0]))
                throw new CompileError(t.Pos, "'\\0' and '\\$NN' are bytes, not characters, so an encoding has no code to give them. Write a character, such as 'A' or '\\n'");
            return t;
        }
    }

    /// <summary><c>indexed</c> is a word only before <c>var</c> (spec 3.3), so it is still a usable name elsewhere.</summary>
    private bool IsIndexedWord() => AtWord(Word.Indexed) && PeekAt(1).Kind is TokenKind.Var or TokenKind.Linear or TokenKind.Data;

    /// <summary><c>indexed var name: [T; n]</c></summary>
    private VarDecl ParseIndexedVar()
    {
        var word = Advance();
        if (At(TokenKind.Linear))
            throw new CompileError(word.Pos, "'indexed' replaces 'linear': it stores records the same way. Write 'indexed var', not 'indexed linear var'");
        if (At(TokenKind.Data))
            throw new CompileError(word.Pos, "'indexed' is for arrays of structs, which cannot be data. Write 'indexed var list: [Enemy; 8]'");
        var decl = ParseVar(word);
        decl.IsIndexed = true;
        return decl;
    }

    /// <summary><c>linear var name: [T; n]</c> or <c>linear data name: [T; n] = ...</c></summary>
    private VarDecl ParseLinearVar()
    {
        var linear = Expect(TokenKind.Linear, "'linear'");
        if (AtWord(Word.Indexed))
            throw new CompileError(Current.Pos, "'indexed' replaces 'linear': it stores records the same way. Write 'indexed var', not 'linear indexed var'");
        if (!At(TokenKind.Var) && !At(TokenKind.Data))
            throw new CompileError(Current.Pos, $"'linear' goes before 'var' or 'data', e.g. 'linear var list: [Enemy; 8]'; found {Describe(Current)}. Write 'linear var' or remove 'linear'");
        var decl = At(TokenKind.Data) ? ParseData(linear) : ParseVar(linear);
        decl.IsLinear = true;
        return decl;
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

    /// <param name="first">The declaration's first word, when a word such as <c>linear</c> comes before <c>var</c>.</param>
    private VarDecl ParseVar(Token? first = null)
    {
        var start = first ?? Current;
        Expect(TokenKind.Var, "'var'");
        var name = Expect(TokenKind.Identifier, "variable name");
        TypeSyntax? type = Accept(TokenKind.Colon) ? ParseType() : null;

        var placement = ParsePlacement(allowZeroPage: true);

        Expr? init = Accept(TokenKind.Eq) ? ParseExpr() : null;
        ExpectEndOfStatement();
        return new VarDecl(start.Pos, name.Text, type, placement, init);
    }

    /// <summary>The optional <c>@ zp</c>, <c>@ align(n)</c>, <c>@ address</c> or <c>@ region</c> after a variable's type.</summary>
    private Placement ParsePlacement(bool allowZeroPage)
    {
        if (!Accept(TokenKind.At)) return Placement.None;
        if (At(TokenKind.Zp))
        {
            if (!allowZeroPage) throw new CompileError(Current.Pos, "Data is read-only and lives in the program; '@ zp' is for variables. Remove '@ zp', or make it a var");
            Advance();
            return OneLocation(new Placement(PlacementKind.ZeroPage));
        }
        // 'align' is a word only after '@' (spec 4).
        if (AtWord(Word.Align))
        {
            Advance();
            Expect(TokenKind.LParen, "'(' and the alignment, as in '@ align(256)'");
            var n = ParseExpr();
            Expect(TokenKind.RParen, "')'");
            return OneLocation(new Placement(PlacementKind.Align, n));
        }
        return OneLocation(new Placement(PlacementKind.Address, ParseExpr()));
    }

    /// <summary>A second location after the first, such as <c>@ zp align(2)</c>, is an error: '@' takes one (spec 4).</summary>
    private Placement OneLocation(Placement placement)
    {
        bool second = placement.Kind == PlacementKind.Align
            ? At(TokenKind.Zp) || At(TokenKind.Identifier) || At(TokenKind.Number)
            : AtWord(Word.Align);
        if (second)
            throw new CompileError(Current.Pos,
                "'@' takes one location (an address, a region, 'zp' or 'align(n)'), and this has two. Remove 'align(n)', or the other location");
        return placement;
    }

    /// <param name="first">The declaration's first word, when <c>linear</c> comes before <c>data</c>.</param>
    private VarDecl ParseData(Token? first = null)
    {
        var start = first ?? Current;
        Expect(TokenKind.Data, "'data'");
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
        using var mode = new Mode<bool>(ref _inConstFn, true);
        var fn = Current.Pos;
        var f = ParseFn(first: start);
        if (f.Address is not null)
            throw new CompileError(fn, "A const fn runs at compile time and has no address. Remove '@' and the address, and give it a body");
        // A generator is called from asm lines, where a mnemonic is read as the instruction (review code-quality-syntax 13).
        if (f.ReturnType is null && Enum.GetNames<Mnemonic>().Contains(f.Name, StringComparer.OrdinalIgnoreCase))
            throw new CompileError(fn, $"'{f.Name}' is a 6502 instruction, so an asm line cannot call a generator of that name. Use another name");
        f.IsConst = true;
        return f;
    }

    /// <param name="first">The declaration's first word, when <c>const</c> comes before <c>fn</c>.</param>
    private FnDecl ParseFn(bool isAsm = false, Token? first = null)
    {
        var start = first ?? Current;
        Expect(TokenKind.Fn, "'fn'");
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
        return Registers.Named(t.Text)
            ?? throw new CompileError(t.Pos, $"Unknown register or flag '{t.Text}'. Use A, X, Y, AX, AY, XY, carry, zero, negative or overflow");
    }

    private TypeSyntax ParseType()
    {
        Enter();
        try { return ParseTypeBody(); }
        finally { _depth--; }
    }

    private TypeSyntax ParseTypeBody()
    {
        if (At(TokenKind.LBracket))
        {
            var start = Advance();
            var element = ParseType();
            Expect(TokenKind.Semicolon, "';'");
            Expr? length = null;
            if (AtWord(Word.Discard)) Advance();
            else length = ParseExpr();
            Expect(TokenKind.RBracket, "']'");
            return new ArrayTypeSyntax(start.Pos, element, length);
        }
        var name = Expect(TokenKind.Identifier, "a type");
        if (name.Text == Word.Ptr && (At(TokenKind.Identifier) || At(TokenKind.LBracket)))
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
