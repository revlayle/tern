using Tern.Compiler.Syntax;

namespace Tern.Compiler.Semantics;

/// <summary>
/// Warnings for code that does nothing (spec 13): unused locals, declarations and imports, and
/// statements that are never reached. Worked out only when the checks found no error.
/// </summary>
public sealed partial class Checker
{
    /// <summary>Top-level symbols named anywhere: in code (reachable or not), declarations, types or assembly operands.</summary>
    private readonly HashSet<Symbol> _named = [];
    /// <summary>Main-file names used in module parameter values, which are checked in a copy of the main file.</summary>
    private readonly HashSet<string> _namedInParameterValues = [];
    /// <summary>Each file's imports that it names something of.</summary>
    private readonly HashSet<(Module File, string Import)> _usedImports = [];

    /// <summary>Records that a name was resolved to <paramref name="s"/>; returns it.</summary>
    private Symbol? Named(Symbol? s, string name)
    {
        if (s is null) return null;
        if (_parameterScope is not null && _module == _parameterScope) _namedInParameterValues.Add(name);
        else _named.Add(s);
        return s;
    }

    /// <param name="includeLibrary">Warn about library modules too (for the repository's own test of <c>src/lib</c>).</param>
    /// <param name="exports">The exported functions: named on the export line, or matched to the routines of <c>export level_api</c>.</param>
    private List<CompileError> FindWarnings(List<Module> modules, bool includeLibrary, List<FunctionSymbol> exports)
    {
        var warnings = new List<CompileError>();
        void Warn(SourcePos pos, string text) => warnings.Add(new CompileError(pos, text) { IsWarning = true });
        var mainModule = modules[^1];
        var exported = mainModule.Syntax.Exports.Select(e => e.Name).ToHashSet();

        foreach (var module in modules.Where(m => includeLibrary || !m.IsLibrary))
        {
            var syntax = module.Syntax;

            // First, since a const fn's body records the names it uses, which count even if it is never called.
            foreach (var f in syntax.Functions.Where(f => f.Address is null).Concat(syntax.ConstFns))
            {
                // Unused locals. A const fn's body is never resolved by the checks, so its names are found here.
                var uses = new LocalUses(f.IsConst ? name => NamedInConstFn(module, name) : null);
                uses.Function(f);
                foreach (var local in uses.Unread)
                    Warn(local.Pos, local.IsLoopVariable
                        ? $"'{local.Name}' is never used. Write '_' as the loop variable"
                        : $"'{local.Name}' is never used. Remove it, or start its name with '_' to keep it");

                Unreachable.Check(f.Body, Warn);
            }

            // Unused declarations: private ones of a module, any of the main file.
            var declarations = syntax.Consts.Select(c => (c.Name, c.Pos, Fixed: false))
                .Concat(syntax.Globals.Select(g => (g.Name, g.Pos, Fixed: g.Placement.Kind is not (PlacementKind.None or PlacementKind.Align))))
                .Concat(syntax.Functions.Select(f => (f.Name, f.Pos, Fixed: false)))
                .Concat(syntax.ConstFns.Select(f => (f.Name, f.Pos, Fixed: false)))
                .Concat(syntax.Structs.Select(s => (s.Name, s.Pos, Fixed: false)))
                .Concat(syntax.Encodings.Select(e => (e.Name, e.Pos, Fixed: false)));
            foreach (var (name, pos, isFixed) in declarations)
            {
                if (!module.IsMain && !syntax.PrivateNames.Contains(name)) continue;
                if (name.StartsWith('_') || isFixed || module.IsMain && name == "main") continue;
                if (module.Scope.GetValueOrDefault(name) is not { } symbol || symbol is ErrorSymbol || symbol.Pos != pos) continue;
                if (_named.Contains(symbol) || symbol is FunctionSymbol f && exports.Contains(f) || module.IsMain && _namedInParameterValues.Contains(name)) continue;
                Warn(pos, $"'{name}' is never used. Remove it, or start its name with '_' to keep it");
            }

            // Unused imports. One that sets module parameters is used by the module itself; one that
            // places a module in a bank (@ area[n]) puts its code there (provisional, question 163).
            foreach (var import in syntax.Imports)
            {
                if (import.Args.Count > 0 || import.Bank is not null || !module.Imports.ContainsKey(import.Name)) continue;
                if (_usedImports.Contains((module, import.Name)) || module.IsMain && exported.Contains(import.Name)) continue;
                Warn(import.Pos, $"'{import.Name}' is imported but not used. Remove the import");
            }
        }
        return warnings;
    }

    /// <summary>A name in a const fn's body that is not one of its locals: a top-level name of its module, or <c>m.name</c>.</summary>
    private void NamedInConstFn(Module module, string name)
    {
        int dot = name.IndexOf('.');
        if (dot > 0)
        {
            if (!module.Imports.TryGetValue(name[..dot], out var other)) return;
            _usedImports.Add((module, name[..dot]));
            if (other.Scope.GetValueOrDefault(name[(dot + 1)..]) is { } member) _named.Add(member);
        }
        else if (module.Scope.GetValueOrDefault(name) is { } s) _named.Add(s);
    }

    /// <summary>
    /// Which locals of a function body are read (spec 13): a local only written counts as unused. A name
    /// that is not a local goes to <c>outer</c> (for a const fn, whose body the checks do not resolve).
    /// </summary>
    private sealed class LocalUses(Action<string>? outer)
    {
        public sealed class Local(string name, SourcePos pos, bool isLoopVariable, bool warned)
        {
            public string Name { get; } = name;
            public SourcePos Pos { get; } = pos;
            public bool IsLoopVariable { get; } = isLoopVariable;
            public bool Warned { get; } = warned;
            public bool Read { get; set; }
            public VariableSymbol? Symbol { get; init; }
        }

        private readonly List<Dictionary<string, Local>> _scopes = [];
        private readonly List<Local> _all = [];

        /// <summary>The locals never read, in the order declared; names starting with '_' are kept on purpose.</summary>
        public IEnumerable<Local> Unread => _all.Where(l => l.Warned && !l.Read);

        public void Function(FnDecl f)
        {
            _scopes.Add([]);
            // Parameters are never warned, but they hide top-level names.
            foreach (var p in f.Parameters.Where(p => p.FixedValue is null)) Add(p.Name, p.Pos, warned: false);
            foreach (var p in f.Parameters) if (p.FixedValue is { } v) Read(v);
            foreach (var p in f.Parameters) Type(p.Type);
            if (f.ReturnType is { } r) Type(r);
            Block(f.Body, newScope: false);
            _scopes.Clear();
        }

        private void Add(string name, SourcePos pos, bool warned, bool loop = false, VariableSymbol? symbol = null)
        {
            var local = new Local(name, pos, loop, warned && !name.StartsWith('_')) { Symbol = symbol };
            _scopes[^1][name] = local;
            _all.Add(local);
        }

        private Local? Find(string name)
        {
            for (int i = _scopes.Count - 1; i >= 0; i--)
                if (_scopes[i].TryGetValue(name, out var local)) return local;
            return null;
        }

        private void Block(BlockStmt block, bool newScope = true)
        {
            if (newScope) _scopes.Add([]);
            foreach (var s in block.Statements) Stmt(s);
            if (newScope) _scopes.RemoveAt(_scopes.Count - 1);
        }

        /// <summary>Any node: what it reads, by its kind; the nodes inside it otherwise.</summary>
        private void Visit(Node node)
        {
            switch (node)
            {
                case Stmt s: Stmt(s); break;
                case Expr e: Read(e); break;
                case TypeSyntax t: Type(t); break;
                case AsmLine l: Children(l); break;
                default: throw new InvalidOperationException($"No reads known for a {node.GetType().Name}");
            }
        }

        private void Children(Node node)
        {
            foreach (var child in node.Children) Visit(child);
        }

        private void Stmt(Stmt s)
        {
            switch (s)
            {
                case VarDecl v:
                    Children(v);
                    Add(v.Name, v.Pos, warned: true, symbol: v.Symbol);
                    break;
                case ConstDecl c:
                    Children(c);
                    Add(c.Name, c.Pos, warned: true);
                    break;
                case AssignStmt a:
                    Write(a.Target);
                    Read(a.Value);
                    break;
                case BlockStmt b: Block(b); break;
                case ForStmt f:
                    Read(f.From);
                    Read(f.To);
                    if (f.Step is not null) Read(f.Step);
                    _scopes.Add([]);
                    if (f.Variable is not null) Add(f.Variable, f.VariablePos, warned: true, loop: true);
                    Block(f.Body);
                    _scopes.RemoveAt(_scopes.Count - 1);
                    break;
                case AsmStmt a:
                    // The var lines of an asm fn are its locals (spec 9).
                    foreach (var local in a.Locals)
                    {
                        if (local.TypeSyntax is not null) Type(local.TypeSyntax);
                        Add(local.Name, local.Pos, warned: true, symbol: local.Symbol);
                    }
                    foreach (var line in a.Lines) Visit(line);
                    break;
                default: Children(s); break;   // if, while, match (each block a scope), emit, return, ...
            }
        }

        private void Type(TypeSyntax t)
        {
            if (t is NamedTypeSyntax n) Name(n.Name);
            else Children(t);
        }

        private void Name(string name)
        {
            if (Find(name) is { } local) local.Read = true;
            else outer?.Invoke(name);
        }

        /// <summary>The target of an assignment: the variable it stores into is written, not read. A pointer stored through is read.</summary>
        private void Write(Expr target)
        {
            switch (target)
            {
                case NameExpr n:
                    if (Find(n.Name) is null) outer?.Invoke(n.Name);
                    break;
                case IndexExpr ix when !ix.Target.Type.IsPointer:
                    Read(ix.Index);
                    Write(ix.Target);
                    break;
                case MemberExpr m when !m.Target.Type.IsPointer:
                    Write(m.Target);
                    break;
                default:
                    Read(target);
                    break;
            }
        }

        private void Read(Expr e)
        {
            switch (e)
            {
                case NameExpr n: Name(n.Name); break;
                // A const fn's literal names its encoding (spec 2.1).
                case CharExpr { Encoding: { } prefix }: outer?.Invoke(prefix); break;
                case StringExpr { Encoding: { } prefix }: outer?.Invoke(prefix); break;
                case CallExpr c:
                    Name(c.Name);
                    Children(c);
                    break;
                case StructLiteralExpr s:
                    Name(s.StructName);
                    Children(s);
                    break;
                case ListForExpr l:
                    Read(l.From);
                    Read(l.To);
                    if (l.Step is not null) Read(l.Step);
                    _scopes.Add([]);
                    if (l.Variable is not null) Add(l.Variable, l.Pos, warned: false);
                    Read(l.Element);
                    _scopes.RemoveAt(_scopes.Count - 1);
                    break;
                default: Children(e); break;
            }
        }
    }

    /// <summary>
    /// Statements after one that never finishes (spec 13): <c>return</c>, <c>break</c>, <c>continue</c>,
    /// a <c>while true</c> with no <c>break</c> of its own, an <c>if</c> with <c>else</c> or a <c>match</c>
    /// with <c>_</c> none of whose branches finishes. Conditions are not evaluated. One warning per block.
    /// </summary>
    private static class Unreachable
    {
        public static void Check(BlockStmt block, Action<SourcePos, string> warn)
        {
            var statements = block.Statements;
            for (int i = 0; i < statements.Count; i++)
            {
                Inner(statements[i], warn);
                if (Ends(statements[i]) is not { } why) continue;
                if (i + 1 < statements.Count)
                    warn(Start(statements[i + 1]), $"This statement is never reached, since {why}. Remove it");
                return;
            }
        }

        /// <summary>
        /// Where a statement starts: an assignment's position is its operator's, an asm block's its '{'.
        /// </summary>
        private static SourcePos Start(Stmt s) => s switch
        {
            AssignStmt a => Leftmost(a.Target),
            AsmStmt { KeywordPos: { } keyword } => keyword,
            _ => s.Pos,
        };

        /// <summary>The first token of an assignment target: <c>buf</c> in <c>buf[1]</c>, <c>pt</c> in <c>pt.a</c>.</summary>
        private static SourcePos Leftmost(Expr e) => e switch
        {
            IndexExpr ix => Leftmost(ix.Target),
            MemberExpr m => Leftmost(m.Target),
            _ => e.Pos,
        };

        /// <summary>Checks the blocks inside a statement.</summary>
        private static void Inner(Stmt s, Action<SourcePos, string> warn)
        {
            switch (s)
            {
                case BlockStmt b: Check(b, warn); break;
                case IfStmt i:
                    Check(i.Then, warn);
                    if (i.Else is not null) Inner(i.Else, warn);
                    break;
                case WhileStmt w: Check(w.Body, warn); break;
                case ForStmt f: Check(f.Body, warn); break;
                case MatchStmt m:
                    foreach (var arm in m.Arms) Check(arm.Body, warn);
                    break;
            }
        }

        /// <summary>Why the statement never finishes, for the message; null if it may.</summary>
        private static string? Ends(Stmt s) => s switch
        {
            ReturnStmt => $"the 'return' on line {s.Pos.Line} ends the function",
            BreakStmt => $"the 'break' on line {s.Pos.Line} leaves the loop",
            ContinueStmt => $"the 'continue' on line {s.Pos.Line} goes on with the next pass of the loop",
            BlockStmt b => b.Statements.Select(Ends).FirstOrDefault(why => why is not null),
            WhileStmt { Condition: BoolExpr { Value: true } } w when !Breaks(w.Body) =>
                $"the 'while true' loop on line {s.Pos.Line} has no 'break', so it never ends",
            IfStmt { Else: not null } i when Ends(i.Then) is not null && Ends(i.Else) is not null =>
                $"no branch of the 'if' on line {s.Pos.Line} finishes",
            MatchStmt m when m.Arms.Any(a => a.IsDefault) && m.Arms.All(a => Ends(a.Body) is not null) =>
                $"no arm of the 'match' on line {s.Pos.Line} finishes",
            _ => null,
        };

        /// <summary>A <c>break</c> of this loop: one not inside an inner loop.</summary>
        private static bool Breaks(Stmt s) => s switch
        {
            BreakStmt => true,
            BlockStmt b => b.Statements.Any(Breaks),
            IfStmt i => Breaks(i.Then) || i.Else is not null && Breaks(i.Else),
            MatchStmt m => m.Arms.Any(a => Breaks(a.Body)),
            _ => false,
        };
    }
}
