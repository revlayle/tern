using Tern.Compiler.Syntax;

namespace Tern.Compiler.Semantics;

/// <summary>Type rules, constant arithmetic and the call graph.</summary>
public sealed partial class Checker
{
    // ---------- Type rules ----------

    /// <summary>
    /// Type of a constant expression with no context: the type of its typed constants, or for one of
    /// literals and untyped constants the smallest type that holds it.
    /// </summary>
    private static TernType DefaultType(Expr e)
    {
        if (e.Type.Kind == TypeKind.IntLiteral && e.ConstType is { } typed) return typed;
        return LiteralType(e);
    }

    /// <summary>The smallest type that holds a constant's value, whatever its constants' types (for 'as', '.lo' and '.hi').</summary>
    private static TernType LiteralType(Expr e)
    {
        if (e.Type.Kind != TypeKind.IntLiteral) return e.Type;
        long v = e.ConstValue!.Value;
        if (v >= 0 && v <= 255) return TernType.U8;
        if (v >= 0 && v <= 65535) return TernType.U16;
        if (v >= -128 && v < 0) return TernType.I8;
        if (v >= -32768 && v < 0) return TernType.I16;
        throw new CompileError(e.Pos, $"{v} does not fit in 16 bits. Use a value from -32768 to 65535");
    }

    /// <summary>Makes two operands the same type, applying the automatic conversions.</summary>
    private TernType Unify(Expr left, Expr right, SourcePos pos)
    {
        if (left.Type.Kind == TypeKind.IntLiteral && right.Type.Kind == TypeKind.IntLiteral) return left.Type;
        if (left.Type.Kind == TypeKind.IntLiteral || right.Type.Kind == TypeKind.IntLiteral)
        {
            // A constant next to a runtime value: the operation's type is chosen as if the constant were a
            // variable of its written type, then the constant is computed exactly at that width (spec 3.1).
            var (constant, value) = left.Type.Kind == TypeKind.IntLiteral ? (left, right) : (right, left);
            var operation = value.Type;
            bool constantSetIt = false;
            if (constant.ConstType is { } typed && value.Type.IsInteger)
            {
                operation = TypedOperationType(value.Type, typed, pos);
                constantSetIt = !operation.SameAs(value.Type);
            }
            CoerceNamed(constant, operation, constantSetIt ? constant.ConstTypeFrom : Describe(value), constantSetIt);
            return operation;
        }
        return TypedOperationType(left.Type, right.Type, pos);
    }

    /// <summary>The type of an operation on two runtime types: the wider one; signed and unsigned do not mix.</summary>
    private static TernType TypedOperationType(TernType lt, TernType rt, SourcePos pos)
    {
        if (!lt.IsInteger || !rt.IsInteger)
            throw new CompileError(pos, $"Cannot combine {lt} and {rt}. Convert one side with 'as'");
        if (lt.IsSigned != rt.IsSigned)
            throw new CompileError(pos, $"Cannot mix signed and unsigned ({lt} and {rt}); convert one side with 'as'");
        return lt.Size >= rt.Size ? lt : rt;
    }

    /// <summary>Checks that <paramref name="e"/> can be used where <paramref name="target"/> is expected.</summary>
    private static void Coerce(Expr e, TernType target)
    {
        var source = e.Type;
        if (source.Kind == TypeKind.Error) return;
        if (source.Kind == TypeKind.Void)
            throw new CompileError(e.Pos, "This function call does not return a value. Call it as a statement on its own");
        if (source.IsStruct)
            throw new CompileError(e.Pos, $"A struct cannot be used as a value, since structs are never copied. Use one of its fields, or a pointer: addr(...) gives a ptr {source}");

        if (source.Kind == TypeKind.IntLiteral)
        {
            if (!target.IsInteger && !target.IsPointer)
                throw new CompileError(e.Pos, $"Expected {target}, found a number");
            long v = e.ConstValue!.Value;
            // ~$0F is the bit pattern ...1111_0000, as wide as the type it is used as.
            if (e.IsBitPattern && v < 0 && !target.IsSigned && v >= -(1L << (8 * target.Size)))
                e.ConstValue = v = Normalize(v, target);
            if (e.IsBitPattern && v < 0 && !target.IsSigned)
                throw new CompileError(e.Pos, $"'~' gives {v} here, a pattern wider than {target}: the value before '~' has bits above {target}. Use a value that fits in {target} before '~'");
            if (v < target.MinValue || v > target.MaxValue)
                throw new CompileError(e.Pos, $"{v} does not fit in {target} ({target.MinValue} to {target.MaxValue}). Use a value in that range, or a wider type");
            e.Type = target;
            return;
        }
        if (source.SameAs(target)) return;
        // The address as a number, where a u16 is expected (spec 3.5): no code.
        if (source.IsPointer && target.Kind == TypeKind.U16) return;
        if (target.IsPointer && source.IsPointer)
            throw new CompileError(e.Pos, $"Expected {target}, found {source}. Convert with 'as {target}'");
        if (source.IsInteger && target.IsInteger && source.IsSigned == target.IsSigned && source.Size < target.Size)
            return; // automatic widening; the code generator extends the value
        bool convertible = (source.IsInteger || source.IsPointer) && (target.IsInteger || target.IsPointer);
        throw new CompileError(e.Pos, $"Expected {target}, found {source}" + (convertible ? $". Convert with 'as {target}'" : ""));
    }

    /// <summary>
    /// Coerce for a constant combined with a runtime value: when it does not fit, the error names what
    /// set the type, the other operand or the typed constant (spec 3.1).
    /// </summary>
    private static void CoerceNamed(Expr e, TernType target, string? setBy, bool isConstant)
    {
        try { Coerce(e, target); }
        catch (CompileError error) when (setBy is not null && target.IsInteger && error.Description.Contains(" does not fit in "))
        {
            long v = e.ConstValue!.Value;
            var wider = WiderFor(v, target);
            throw new CompileError(e.Pos, isConstant
                ? $"{v} does not fit in {target} ({target.MinValue} to {target.MaxValue}), the type of the constant '{setBy}' in it, which sets the operation's type. " +
                  (wider is null ? "Use a smaller value" : $"Give '{setBy}' the type {wider}")
                : $"{v} does not fit in {target} ({target.MinValue} to {target.MaxValue}), the type of '{setBy}', which it is combined with. " +
                  (wider is null ? "Use a smaller value" : $"Convert '{setBy}' with 'as {wider}'"));
        }
    }

    /// <summary>A type of the same signedness as <paramref name="t"/> that holds <paramref name="v"/>, if one is wider.</summary>
    private static TernType? WiderFor(long v, TernType t)
    {
        var wide = t.IsSigned ? TernType.I16 : TernType.U16;
        return t.Size < 2 && v >= wide.MinValue && v <= wide.MaxValue ? wide : null;
    }

    /// <summary>Source text for a simple operand, to name it in an error; null for anything else.</summary>
    private static string? Describe(Expr e) => e switch
    {
        NameExpr n => n.Name,
        MemberExpr m when Describe(m.Target) is { } t => $"{t}.{m.Member}",
        IndexExpr ix when Describe(ix.Target) is { } t => $"{t}[{Describe(ix.Index) ?? "..."}]",
        CallExpr c => $"{c.Name}({string.Join(", ", c.Args.Select(a => Describe(a) ?? "..."))})",
        NumberExpr n => n.Value.ToString(),
        BinaryExpr b when Describe(b.Left) is { } l && Describe(b.Right) is { } r => $"{l} {OpText(b.Op)} {r}",
        _ => null,
    };

    /// <summary>
    /// One step of constant arithmetic, in signed 64 bits (spec 3.1): a result outside that range is
    /// an error. <c>/</c> rounds toward zero, <c>%</c> has the left operand's sign, <c>&gt;&gt;</c>
    /// rounds down. The shift count is already checked (0 to 63).
    /// </summary>
    internal static long ConstArithmetic(SourcePos pos, TokenKind op, long l, long r)
    {
        try
        {
            return op switch
            {
                TokenKind.Plus => checked(l + r),
                TokenKind.Minus => checked(l - r),
                TokenKind.Star => checked(l * r),
                TokenKind.Slash => checked(l / r),
                TokenKind.Percent => r == -1 ? 0 : l % r,
                TokenKind.Amp => l & r,
                TokenKind.Pipe => l | r,
                TokenKind.Caret => l ^ r,
                TokenKind.Shl => (l << (int)r) >> (int)r == l ? l << (int)r : throw new OverflowException(),
                TokenKind.Shr => l >> (int)r,
                _ => throw new InvalidOperationException(),
            };
        }
        catch (OverflowException)
        {
            throw OutOfConstantRange(pos);
        }
    }

    /// <summary>Negates a constant, in signed 64 bits (spec 3.1).</summary>
    internal static long ConstNegate(SourcePos pos, long v) => v == long.MinValue ? throw OutOfConstantRange(pos) : -v;

    private static CompileError OutOfConstantRange(SourcePos pos) => new(pos,
        "This constant expression goes outside the signed 64-bit range of constant arithmetic "
        + "(-9223372036854775808 to 9223372036854775807). Use smaller values");

    /// <summary>Wraps a value to the range of <paramref name="t"/>, as the CPU would.</summary>
    public static long Normalize(long v, TernType t) => t.Kind switch
    {
        TypeKind.U8 => v & 0xFF,
        TypeKind.I8 => (sbyte)(v & 0xFF),
        TypeKind.U16 or TypeKind.Ptr => v & 0xFFFF,
        TypeKind.I16 => (short)(v & 0xFFFF),
        TypeKind.Bool => v != 0 ? 1 : 0,
        _ => v,
    };

    private static string OpText(TokenKind op) => op switch
    {
        TokenKind.AmpAmp => "&&", TokenKind.PipePipe => "||", TokenKind.Star => "*", TokenKind.Slash => "/",
        TokenKind.Percent => "%", TokenKind.PlusEq => "+=", TokenKind.MinusEq => "-=", TokenKind.AmpEq => "&=",
        TokenKind.PipeEq => "|=", TokenKind.CaretEq => "^=", TokenKind.ShlEq => "<<=", TokenKind.ShrEq => ">>=",
        TokenKind.Plus => "+", TokenKind.Minus => "-", TokenKind.Amp => "&", TokenKind.Pipe => "|", TokenKind.Caret => "^",
        TokenKind.Shl => "<<", TokenKind.Shr => ">>",
        _ => op.ToString(),
    };

    // ---------- Call graph ----------

    /// <summary>
    /// The functions reachable from main, then from each interrupt handler, then from each function
    /// whose address is taken, then from each function an overlay's interface table points to that no
    /// root reaches (spec 8.5); callers first. Handlers are roots: those of the main file, and those
    /// a compiled function names (to install them). Each function records the roots it is reached
    /// from (<see cref="FunctionSymbol.Roots"/>); one reached from two roots gets a frame that
    /// overlaps no other (spec 8.1).
    /// </summary>
    /// <param name="kept">The globals kept whether or not code uses them (<see cref="KeptGlobals"/>).</param>
    private static List<FunctionSymbol> OrderRoots(FunctionSymbol main, Module mainModule, List<FunctionSymbol> tableTargets, IReadOnlyList<VariableSymbol> kept)
    {
        var all = new List<FunctionSymbol>();
        var seen = new HashSet<FunctionSymbol>();
        var roots = new List<FunctionSymbol> { main };
        roots.AddRange(mainModule.Scope.Values.OfType<FunctionSymbol>().Where(f => f.IsInterrupt && !f.IsPart).OrderBy(f => f.Pos.Line));
        foreach (var h in roots.Skip(1)) CheckInstallable(h);
        var addressTaken = new List<FunctionSymbol>();

        void Named(FunctionSymbol f)
        {
            if (f.IsInterrupt)
            {
                if (f.IsPart)
                    throw new CompileError(f.Pos,
                        $"'{f.Name}' is called by another interrupt fn (so it is compiled as a part, ending with rts) and its address is also used to install it (which needs rti). Use it one way, or write a second interrupt fn for the other");
                CheckInstallable(f);
                if (!roots.Contains(f)) roots.Add(f);
            }
            else if (!addressTaken.Contains(f)) addressTaken.Add(f);
        }
        // The main file's globals and placed data are all kept, so the functions in their initial values are too.
        foreach (var g in kept)
            foreach (var f in GlobalsNamedBy(g).SelectMany(t => t.FunctionAddresses)) Named(f);

        void Add(FunctionSymbol root, bool isRoot)
        {
            foreach (var f in OrderReachable(root))
            {
                if (seen.Add(f)) all.Add(f);
                if (f.FixedAddress is not null) continue; // no frame; may be called from anywhere
                if (isRoot) f.Roots.Add(root);
                else f.Isolated = true;
                foreach (var h in f.UsedInterrupts) Named(h);
                foreach (var a in f.UsedAddresses) Named(a);
            }
        }
        // Main and the handlers first, then the address-taken functions, whose callers are unknown.
        int r = 0, a = 0;
        void AddAll()
        {
            while (r < roots.Count || a < addressTaken.Count)
            {
                if (r < roots.Count) Add(roots[r++], isRoot: true);
                else Add(addressTaken[a++], isRoot: false);
            }
        }
        AddAll();
        // An implementation that no call through its interface reaches is still in its table.
        foreach (var f in tableTargets.Where(f => !seen.Contains(f) && !addressTaken.Contains(f))) addressTaken.Add(f);
        AddAll();
        // Handlers that call each other are all parts, so no root reaches them; their cycle is still an error (spec 6.4).
        foreach (var h in mainModule.Scope.Values.OfType<FunctionSymbol>().Where(f => f.IsInterrupt && !seen.Contains(f)).OrderBy(f => f.Pos.Line))
            OrderReachable(h);
        return all;
    }

    /// <summary>
    /// An interrupt fn is a part if a function that is compiled calls it (spec 6.3). Callers that are
    /// never compiled, such as a module's handler that nothing installs, do not count.
    /// </summary>
    private static void MarkParts(FunctionSymbol main, Module mainModule, List<FunctionSymbol> tableTargets, IReadOnlyList<VariableSymbol> kept)
    {
        var visited = new HashSet<FunctionSymbol>();
        var compiled = new HashSet<FunctionSymbol>();
        void Visit(FunctionSymbol f)
        {
            if (!visited.Add(f)) return;
            foreach (var c in f.Callees)
            {
                if (c.IsInterrupt) { c.IsPart = true; Compiled(c); }
                else Visit(c);
            }
            foreach (var h in f.UsedInterrupts) Compiled(h);
            foreach (var a in f.UsedAddresses) Named(a);
        }
        void Compiled(FunctionSymbol f) { if (compiled.Add(f)) Visit(f); }
        void Named(FunctionSymbol f) { if (f.IsInterrupt) Compiled(f); else Visit(f); }
        Visit(main);
        foreach (var f in tableTargets) Visit(f);
        foreach (var f in mainModule.Scope.Values.OfType<FunctionSymbol>().Where(f => f.IsInterrupt)) Compiled(f);
        foreach (var g in kept)
            foreach (var f in GlobalsNamedBy(g).SelectMany(t => t.FunctionAddresses)) Named(f);
    }

    /// <summary>A handler is entered by the CPU, with nothing to pass: only a part may have parameters or a return value.</summary>
    private static void CheckInstallable(FunctionSymbol f)
    {
        if (f.Parameters.Count > 0 || f.ReturnType.Kind != TypeKind.Void)
            throw new CompileError(f.Pos,
                $"'{f.Name}' has {(f.Parameters.Count > 0 ? "parameters" : "a return value")}, so it can only be called as a part of another interrupt fn. " +
                "An interrupt handler takes no parameters and returns no value: the CPU calls it, not your code. Remove them, or call it only from an interrupt fn");
    }

    private static string DescribeRoot(FunctionSymbol root) =>
        root.IsInterrupt ? $"the interrupt handler '{root.Name}'" : root.Name == EntriesRoot ? "the binary's entries" : "main";

    /// <summary>
    /// A module's private global used from two roots (main and a handler, or two handlers) is an
    /// error: an interrupt could change it while the other root is using it, and the caller cannot
    /// see it (review M6, finding 1). The library's work bytes are such globals. Public globals are
    /// how main and handlers talk to each other, and data is read-only, so both stay allowed.
    /// </summary>
    private static void CheckPrivateGlobalsPerRoot(List<Module> modules, List<FunctionSymbol> ordered)
    {
        var moduleOf = new Dictionary<VariableSymbol, Module>();
        foreach (var m in modules.Where(m => !m.IsMain))
            foreach (var v in m.Syntax.PrivateNames.Select(n => m.Scope.GetValueOrDefault(n)).OfType<VariableSymbol>())
                if (!v.IsData) // read-only tables are safe to share
                    moduleOf[v] = m;
        var privateGlobals = moduleOf.Keys.ToHashSet();
        if (privateGlobals.Count == 0) return;

        // A module's own handler may share the module's private variables (spec 8.1): its author wrote both sides.
        bool OwnHandler(FunctionSymbol root, VariableSymbol g) =>
            root.IsInterrupt && moduleOf[g].Scope.Values.Contains(root);
        // Or through an interrupt fn of the module that is called as a part (decision 34).
        // A function of the module that only the module's own interrupt fns call is on that side too (spec 8.1).
        var handlerSide = HandlerSideFunctions(ordered);
        bool OwnPart(FunctionSymbol f, VariableSymbol g) =>
            (f.IsInterrupt || handlerSide.Contains(f)) && moduleOf[g].Scope.Values.Contains(f);

        var firstUser = new Dictionary<VariableSymbol, (FunctionSymbol Function, FunctionSymbol Root)>();
        foreach (var f in ordered.Where(f => f.FixedAddress is null))
            foreach (var root in f.Roots.OrderBy(r => r.IsInterrupt))
                foreach (var used in f.UsedGlobals)
                {
                    var g = used.AliasOf ?? used;
                    if (!privateGlobals.Contains(g) || OwnPart(f, g) || (OwnHandler(root, g) && handlerSide.Contains(f))) continue;
                    if (!firstUser.TryGetValue(g, out var first))
                        firstUser[g] = (f, root);
                    else if (first.Root != root)
                    {
                        // Reported at the handler, which is in the program's own code.
                        var handler = root.IsInterrupt ? root : first.Root;
                        throw new CompileError(handler.Pos,
                            $"'{first.Function.Name}' (called from {DescribeRoot(first.Root)}) and '{f.Name}' (called from {DescribeRoot(root)}) both use '{g.Name}', " +
                            $"a private variable of its module. An interrupt could change it while the other is using it. " +
                            $"Call functions of this module from only main or only one interrupt handler");
                    }
                }
    }

    /// <summary>
    /// The functions that only interrupt fns of their own module call, directly or through other such
    /// functions: they run on the handler side of their module (spec 8.1). A function whose address is taken
    /// may be called from anywhere, so it is not one of them.
    /// </summary>
    private static HashSet<FunctionSymbol> HandlerSideFunctions(List<FunctionSymbol> ordered)
    {
        var callers = ordered.ToDictionary(f => f, _ => new List<FunctionSymbol>());
        foreach (var f in ordered)
            foreach (var callee in f.Callees)
                if (callers.TryGetValue(callee, out var list)) list.Add(f);
        var side = new HashSet<FunctionSymbol>();
        bool changed;
        do
        {
            changed = false;
            foreach (var f in ordered)
            {
                if (f.IsInterrupt || f.FixedAddress is not null || f.AddressTaken || f.Module is null || side.Contains(f)) continue;
                var from = callers[f];
                if (from.Count == 0 || !from.All(c => c.Module == f.Module && (c.IsInterrupt || side.Contains(c)))) continue;
                side.Add(f);
                changed = true;
            }
        } while (changed);
        return side;
    }

    /// <summary>
    /// A global used from two roots is volatile (spec 4, 8.1), so that a main loop waiting for a flag
    /// set by a handler reads it on every pass. A function reached from an address-taken function
    /// may be called from any root, so it also counts as reached from an unknown one.
    /// </summary>
    private static void MarkGlobalsUsedFromTwoRoots(List<FunctionSymbol> ordered)
    {
        var unknown = new FunctionSymbol("(through an address)", default);
        var rootOf = new Dictionary<VariableSymbol, FunctionSymbol>();
        foreach (var f in ordered.Where(f => f.FixedAddress is null))
            foreach (var root in f.Isolated ? f.Roots.Append(unknown) : f.Roots)
                foreach (var used in f.UsedGlobals)
                {
                    var g = used.Storage;
                    if (rootOf.TryGetValue(g, out var other) && other != root) g.UsedFromTwoRoots = true;
                    else rootOf[g] = root;
                }
    }

    /// <summary>Functions reachable from main, callers before callees. Reports recursion.</summary>
    private static List<FunctionSymbol> OrderReachable(FunctionSymbol main)
    {
        var state = new Dictionary<FunctionSymbol, int>(); // 1 = visiting, 2 = done
        var postOrder = new List<FunctionSymbol>();
        var path = new Stack<FunctionSymbol>();

        void Visit(FunctionSymbol f)
        {
            state[f] = 1;
            path.Push(f);
            foreach (var callee in f.Callees)
            {
                if (state.GetValueOrDefault(callee) == 1)
                {
                    var cycle = path.Reverse().SkipWhile(x => x != callee).Select(x => x.Name).Append(callee.Name);
                    throw new CompileError(callee.Pos, $"Recursion is not allowed: {string.Join(" -> ", cycle)}. Use a loop instead, with an array as the stack if needed");
                }
                if (state.GetValueOrDefault(callee) == 0) Visit(callee);
            }
            path.Pop();
            state[f] = 2;
            postOrder.Add(f);
        }

        Visit(main);
        postOrder.Reverse();
        return postOrder;
    }
}
