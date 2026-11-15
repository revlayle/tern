using Tern.Compiler.Syntax;

namespace Tern.Compiler.Semantics;

/// <summary>Function bodies: statements, assignments, loops and match.</summary>
public sealed partial class Checker
{
    // ---------- Function bodies ----------

    private void CheckFunctionBody(FnDecl decl)
    {
        _function = decl.Symbol!;
        _scopes.Add(new Dictionary<string, Symbol>());
        foreach (var p in _function.Parameters) Declare(p);

        // The var lines of an asm fn: locals in its frame, or in each caller's for an inline asm fn (spec 9).
        if (decl.IsAsm && ((AsmStmt)decl.Body.Statements[0]).Locals is { Count: > 0 } asmLocals)
            foreach (var local in asmLocals) CheckLocal(local, inAsmFn: true);

        CheckBlock(decl.Body, newScope: false);

        // Copied into the caller, an rts or rti would end the caller too.
        if (decl is { IsAsm: true, IsInline: true })
            foreach (var ins in ((AsmStmt)decl.Body.Statements[0]).Lines.OfType<AsmInstruction>())
                if (ins.Mnemonic is Tern.Cpu.Mnemonic.Rts or Tern.Cpu.Mnemonic.Rti)
                    throw new CompileError(ins.Pos,
                        $"'{ins.Mnemonic.ToString().ToLowerInvariant()}' in an inline asm fn would end the function that calls it, since the code is copied there. Remove it: the code simply runs on after the copy");

        if (_function.ReturnType.Kind != TypeKind.Void && !decl.IsAsm && !AlwaysReturns(decl.Body))
            throw new CompileError(decl.Pos, $"'{decl.Name}' can reach its end without returning a value. Add a 'return' at the end");

        _scopes.Clear();
        _function = null;
    }

    private void CheckBlock(BlockStmt block, bool newScope = true)
    {
        if (newScope) _scopes.Add(new Dictionary<string, Symbol>());
        foreach (var s in block.Statements)
        {
            // At most one error per statement (spec 13). A local whose declaration failed accepts every use.
            if (!Recover(() => CheckStmt(s)) && s is VarDecl or ConstDecl && DeclaredName(s) is var name && Lookup(name, demand: false) is null)
                _scopes[^1][name] = new ErrorSymbol(name, s.Pos);
        }
        if (newScope) _scopes.RemoveAt(_scopes.Count - 1);
    }

    private static string DeclaredName(Stmt s) => s switch { VarDecl v => v.Name, ConstDecl c => c.Name, _ => "" };

    private void CheckStmt(Stmt stmt)
    {
        switch (stmt)
        {
            case ErrorStmt e:
                // A syntax error, reported by the parser.
                if (e.Name is { } broken && Lookup(broken, demand: false) is null) _scopes[^1][broken] = new ErrorSymbol(broken, e.Pos);
                break;
            case VarDecl v: CheckLocal(v); break;
            case ConstDecl c: DeclareConst(c); break;
            case AssignStmt a: CheckAssign(a); break;
            case ExprStmt e:
                if (e.Expr is not CallExpr)
                    throw new CompileError(e.Pos, "Only a function call can be used as a statement. Assign the value to a variable, or remove it");
                CheckExpr(e.Expr);
                break;
            case BlockStmt b: CheckBlock(b); break;
            case IfStmt i:
            {
                // A constant condition compiles only the branch taken: calls, variables and addresses in the
                // other one are checked but not recorded, so what only it uses is left out of the program (spec 2.2).
                // After an error in the condition, the branches are still checked (spec 13).
                Recover(() => CheckCondition(i.Condition));
                bool wasDead = _dead;
                _dead = wasDead || i.Condition.ConstValue == 0;
                CheckBlock(i.Then);
                _dead = wasDead || i.Condition.ConstValue is not null and not 0;
                if (i.Else is not null) CheckStmt(i.Else);
                _dead = wasDead;
                break;
            }
            case WhileStmt w:
            {
                Recover(() => CheckCondition(w.Condition));
                bool wasDead = _dead;
                _dead = wasDead || w.Condition.ConstValue == 0;
                _loopDepth++;
                CheckBlock(w.Body);
                _loopDepth--;
                _dead = wasDead;
                break;
            }
            case ForStmt f: CheckFor(f); break;
            case MatchStmt m: CheckMatch(m); break;
            case AsmStmt a: CheckAsm(a); break;
            case BreakStmt or ContinueStmt:
                if (_loopDepth == 0) throw new CompileError(stmt.Pos, "'break' and 'continue' must be inside a loop. Use 'return' to leave the function");
                break;
            case ReturnStmt r:
                var ret = _function!.ReturnType;
                if (ret.Kind == TypeKind.Void)
                {
                    if (r.Value is not null) throw new CompileError(r.Pos, $"'{_function.Name}' does not return a value. Remove the value, or give the function a return type");
                }
                else
                {
                    if (r.Value is null) throw new CompileError(r.Pos, $"'{_function.Name}' must return a {ret} value. Write the value after 'return'");
                    CheckExpr(r.Value);
                    Coerce(r.Value, ret);
                }
                break;
            default:
                throw new InvalidOperationException(stmt.GetType().Name);
        }
    }

    private void CheckLocal(VarDecl decl, bool inAsmFn = false)
    {
        var type = DeclaredType(decl);
        var v = new VariableSymbol(decl.Name, decl.Pos, type, VariableKind.Local, _function) { IsIndexed = decl.IsIndexed };
        decl.Symbol = v;
        ApplyPlacement(decl, v, allowAddress: false);

        try
        {
            if (inAsmFn && decl.Init is not null)
                throw new CompileError(decl.Init.Pos, $"'{decl.Name}' in an asm fn has no initial value: the assembly sets it. Remove the value and store it in the assembly");
            if (decl.Init is null)
            {
                if (!type.IsArray && !type.IsStruct && !inAsmFn)
                    throw new CompileError(decl.Pos, $"Local variable '{decl.Name}' needs an initial value. Write one: var {decl.Name}: {type} = 0");
            }
            else
            {
                if (decl.Init is StructLiteralExpr lit) throw StructLiteralInCode(lit, decl.Name);
                if (type.IsStruct || type.IsStructArray)
                    throw new CompileError(decl.Init.Pos, "A struct cannot have an initial value. Assign its fields");
                if (type.IsArray && IsCompileTimeArray(decl.Init)) CheckExpr(decl.Init);
                if (type.IsArray) throw new CompileError(decl.Init.Pos, "An initial value for a local array is not supported yet. Assign the elements, or use a global or data");
                CheckExpr(decl.Init);
                Coerce(decl.Init, type);
                v.Weight += Weight();
            }
        }
        catch (Exception e) when (_errors is not null && decl.TypeSyntax is not null && e is CompileError or FollowOnError)
        {
            // Its type is written, so only the initial value is wrong: its uses still check (spec 13).
            if (Lookup(decl.Name, demand: false) is null or ErrorSymbol)
            {
                Declare(v);
                _function!.FrameVariables.Add(v);
            }
            throw;
        }
        Declare(v);
        _function!.FrameVariables.Add(v);
    }

    private void CheckAssign(AssignStmt a)
    {
        if (a.Value is StructLiteralExpr lit) throw StructLiteralInCode(lit, Describe(a.Target));
        CheckExpr(a.Target);
        var target = a.Target;
        if (target.Type.IsStruct)
            throw new CompileError(target.Pos, "Structs are never copied, so a struct cannot be assigned. Assign its fields one by one, or copy the memory with mem.copy");
        if (!IsAssignable(target))
            throw new CompileError(target.Pos, "The left side of an assignment must be a variable, an array element, a field, or .lo/.hi of one. Assign to one of those");
        if (!target.Type.IsScalar)
            throw new CompileError(target.Pos, "Whole arrays cannot be assigned. Assign the elements in a loop, or copy the memory with mem.copy");

        CheckExpr(a.Value);
        if (a.Op == TokenKind.Eq)
        {
            Coerce(a.Value, target.Type);
            return;
        }
        if (target.Type.IsPointer)
        {
            if (a.Op is not (TokenKind.PlusEq or TokenKind.MinusEq))
                throw new CompileError(a.Pos, $"'{OpText(a.Op)}' does not work on pointers; only '+=' and '-=' do. Convert the pointer with 'as u16' for other operators");
            CheckPointerOffset(a.Value);
            return;
        }
        if (!target.Type.IsInteger)
            throw new CompileError(a.Pos, $"'{OpText(a.Op)}' needs a number on the left. Use a number variable, or '=' with the whole value");
        if (a.Op is TokenKind.ShlEq or TokenKind.ShrEq)
            CheckShiftCount(a.Value, target.Type);
        else
        {
            // Checked as the operation it stands for (spec 3.1): x op= e is x = x op e.
            if (a.Value is { Type.Kind: TypeKind.IntLiteral, ConstType: { } typed })
            {
                var operation = TypedOperationType(target.Type, typed, a.Pos);
                if (!operation.SameAs(target.Type))
                    throw new CompileError(a.Value.Pos, $"Expected {target.Type}, found {operation}: '{a.Value.ConstTypeFrom}' is {typed}. Convert with 'as {target.Type}'");
            }
            // Inside an operator a pointer still needs 'as' (spec 3.5), as in w = w + p.
            if (a.Value.Type.IsPointer && target.Type.Kind == TypeKind.U16)
                throw new CompileError(a.Value.Pos, $"Expected {target.Type}, found {a.Value.Type}. Convert with 'as u16'");
            try { Coerce(a.Value, target.Type); }
            catch (CompileError error) when (a.Value.ConstValue is long v && error.Description.Contains(" does not fit in "))
            {
                var wider = WiderFor(v, target.Type);
                var written = Describe(target);
                var name = written is not null ? $"'{written}'" : "the left side";
                // x += -1 on an unsigned x is x -= 1.
                var fix = v < 0 && !target.Type.IsSigned && -v <= target.Type.MaxValue && written is not null && a.Op is TokenKind.PlusEq or TokenKind.MinusEq
                    ? $"Write '{written} {(a.Op == TokenKind.PlusEq ? "-=" : "+=")} {-v}'"
                    : wider is not null && Unwrap(target) is NameExpr ? $"Declare {name} as {wider}, or use a smaller value" : "Use a smaller value";
                throw new CompileError(a.Value.Pos,
                    $"{v} does not fit in {target.Type} ({target.Type.MinValue} to {target.Type.MaxValue}), the type of {name}, which it is combined with. {fix}");
            }
        }
    }

    private bool IsAssignable(Expr e)
    {
        switch (Unwrap(e))
        {
            case NameExpr { Symbol: VariableSymbol v }:
                if (v.IsData) throw new CompileError(e.Pos, $"'{v.Name}' is data, which is read-only. Declare it with 'var' to change it");
                if (v.ReadOnly) throw new CompileError(e.Pos, $"Loop variable '{v.Name}' cannot be changed inside the loop. Use a 'while' loop to change the counter yourself");
                return true;
            case IndexExpr ix:
                if (Unwrap(ix.Target) is NameExpr { Symbol: { IsData: true } d })
                    throw new CompileError(e.Pos, $"'{d.Storage.Name}' is data, which is read-only. Declare it with 'var' to change it");
                return true;
            case MemberExpr m:
                return Unwrap(m.Target) is NameExpr or IndexExpr && IsAssignable(m.Target);
            default:
                return false;
        }
    }

    private void CheckFor(ForStmt f)
    {
        CheckExpr(f.From);
        CheckExpr(f.To);
        if (f.Step is not null) CheckExpr(f.Step);
        foreach (var bound in new[] { f.From, f.To })
        {
            if (!(bound.Type.IsInteger || bound.Type.Kind == TypeKind.IntLiteral))
                throw new CompileError(bound.Pos, "Loop bounds must be numbers. Use integer values, or convert with 'as'");
            // A typed constant gives the counter its type, as a variable of that type would, when its value fits it.
            if (bound is { Type.Kind: TypeKind.IntLiteral, ConstType: { } typed, ConstValue: long value }
                && value >= typed.MinValue && value <= typed.MaxValue) Coerce(bound, typed);
        }

        // The loop variable's type: the wider of the bounds, or u8/u16 for literals. A negative
        // literal or a signed bound makes it i8 if the bounds fit in -128..127, else i16 (spec 5.1).
        TernType type;
        bool fromLiteral = f.From.Type.Kind == TypeKind.IntLiteral, toLiteral = f.To.Type.Kind == TypeKind.IntLiteral;
        if (fromLiteral && toLiteral)
        {
            long a = f.From.ConstValue!.Value, b = f.To.ConstValue!.Value;
            if (a < 0 || b < 0)
                type = Math.Min(a, b) >= -128 && Math.Max(a, b) <= 127 ? TernType.I8 : TernType.I16;
            else
                type = CounterType(f, a, b) ?? (DefaultType(f.From).Size >= DefaultType(f.To).Size ? DefaultType(f.From) : DefaultType(f.To));
        }
        else if (fromLiteral != toLiteral)
        {
            var (literal, other) = fromLiteral ? (f.From, f.To) : (f.To, f.From);
            long v = literal.ConstValue!.Value;
            if (v < 0 && other.Type.IsInteger && !other.Type.IsSigned)
                throw new CompileError(literal.Pos,
                    $"A loop from a negative bound counts with a signed variable, but the other bound is {other.Type}. Convert it with 'as {(v >= -128 && other.Type.Size == 1 ? "i8" : "i16")}'");
            type = other.Type.IsSigned && other.Type.Size == 1 && (v < -128 || v > 127) ? TernType.I16 : Unify(f.From, f.To, f.Pos);
        }
        else
            type = Unify(f.From, f.To, f.Pos);
        Coerce(f.From, type);
        // An end the counter never reaches may lie past its type: 0..256 counts with a u8.
        if (type.Kind == TypeKind.U8 && f.To is { Type.Kind: TypeKind.IntLiteral, ConstValue: > 255 }) f.To.Type = type;
        else Coerce(f.To, type);

        long? from = f.From.ConstValue, to = f.To.ConstValue;
        if (f.Step is { ConstValue: null, Type.IsInteger: true })
            CheckVariableStep(f, type, from, to);
        else if (f.Step is not null)
        {
            if (f.Step.ConstValue is not long step || step == 0)
                throw new CompileError(f.Step.Pos, "'step' must be a non-zero constant, or a u8 variable in a loop that counts up with a u8 counter. Use one of those");
            if (Math.Abs(step) > 127)
                throw new CompileError(f.Step.Pos, "'step' must be between -127 and 127. Use a smaller step, or a 'while' loop");
            if (from is not null && to is not null && from != to && (to > from) != (step > 0))
                throw new CompileError(f.Step.Pos, $"A step of {step} goes the wrong way from {from} to {to}. Change the sign of the step, or swap the bounds");
            f.StepValue = (int)step;
        }
        else
        {
            // Without a step the loop counts up, whatever its bounds (spec 5.1).
            if (NeverRuns(f, type, from, to))
                throw CountsUp(f.RangeText, f.Pos, "Write 'step -1' to count down");
            f.StepValue = 1;
        }

        if (f.UsingRegister is { } reg)
        {
            if (reg is not (Register.X or Register.Y))
                throw new CompileError(f.Pos, "A loop variable can only use X or Y. Write 'using X' or 'using Y'");
            if (type.Size != 1)
                throw new CompileError(f.Pos,
                    $"{(type.IsSigned ? "An" : "A")} {type} loop variable does not fit in {reg}. Remove 'using {reg}', or use bounds that fit in {(type.IsSigned ? "an i8" : "a u8")}");
            if (!_loopRegisters.Add(reg))
                throw new CompileError(f.Pos, $"{reg} is already used by an enclosing loop. Use the other index register, or leave out 'using'");
            if (ContainsCall(f.Body, reg))
                throw new CompileError(f.Pos,
                    $"The loop body calls a function, which can change {reg}. Remove 'using {reg}', or call only routines that keep {reg}");
        }

        var counter = new VariableSymbol(f.Variable ?? $"_loop{_hiddenCounter++}", f.Pos, type, VariableKind.Local, _function)
        {
            ReadOnly = true,
            Register = f.UsingRegister,
            IsLoopCounter = true,
        };
        f.Symbol = counter;
        if (counter.InMemory) _function!.FrameVariables.Add(counter);

        _scopes.Add(new Dictionary<string, Symbol>());
        if (f.Variable is not null) Declare(counter);
        _loopDepth++;
        counter.Weight += Weight() * 3;   // load, step and compare on every pass
        if (from is not null && to is not null && LargestLoopValue(from.Value, to.Value, f.Inclusive, f.StepValue) is long largest)
            _loopMax[counter] = largest;
        CheckBlock(f.Body, newScope: false);
        _loopMax.Remove(counter);
        _loopDepth--;
        _scopes.RemoveAt(_scopes.Count - 1);

        if (f.UsingRegister is { } r) _loopRegisters.Remove(r);
    }

    /// <summary>
    /// A loop counting up that can never run, whatever its variables hold (spec 5.1, decision 104):
    /// constant bounds with the start past the end, or an unsigned counter ending before 0 (<c>n..0</c>).
    /// An unsigned <c>n..=0</c> runs once when n is 0, so it is allowed.
    /// </summary>
    private static bool NeverRuns(ForStmt f, TernType counter, long? from, long? to) =>
        from is not null && to is not null ? from > to : to == 0 && !f.Inclusive && !counter.IsSigned;

    /// <summary>A loop that counts up from a larger start, so it never runs.</summary>
    internal static CompileError CountsUp(string range, SourcePos pos, string fix) =>
        new(pos, $"'{range}' counts up, so it never runs. {fix}");

    /// <summary>A step that is not a constant (spec 5.1): a u8 value, in a loop that counts up with a u8 counter.</summary>
    private static void CheckVariableStep(ForStmt f, TernType counter, long? from, long? to)
    {
        var step = f.Step!;
        if (step.Type.IsSigned)
            throw new CompileError(step.Pos, $"A variable step must be a u8, but this is {step.Type}: a variable step cannot be negative. Use a constant negative step to count down");
        if (step.Type.Size != 1)
            throw new CompileError(step.Pos, $"A variable step must be a u8, but this is {step.Type}. Convert with 'as u8' if it always fits");
        if (counter.Size != 1 || counter.IsSigned)
            throw new CompileError(step.Pos, $"A variable step needs a u8 loop counter, but the bounds are {counter}. Use a constant step, or u8 bounds");
        if (NeverRuns(f, counter, from, to))
            throw CountsUp(f.RangeText, step.Pos, "Use a constant negative step to count down");
        f.VariableStep = true;
        f.StepValue = 1;
    }

    /// <summary>An index that is the counter of a constant-bounds loop must stay inside the array (spec 3.2).</summary>
    private void CheckLoopIndex(Expr index, string name, int length)
    {
        if (Unwrap(index) is NameExpr { Symbol: VariableSymbol counter } && _loopMax.TryGetValue(counter, out var last) && last >= length)
            throw new CompileError(index.Pos,
                $"The loop reaches {last}, past the end of '{name}' (length {length}). Make the loop stop before {length}");
    }

    /// <summary>
    /// The counter type of a loop over non-negative constant bounds, from the values it takes (spec 5.1,
    /// decision 2026-11-15): <c>0..256</c> and <c>0..=300 step 127</c> count with a u8, <c>1..257</c>
    /// with a u16. Null when the loop runs no passes or its step is not a usable constant.
    /// </summary>
    private static TernType? CounterType(ForStmt f, long from, long to)
    {
        long step = f.Step is null ? 1 : f.Step.ConstValue ?? 0;
        if (step == 0 || Math.Abs(step) > 127) return null;
        if (LargestLoopValue(from, to, f.Inclusive, (int)step) is not long last) return null;
        long largest = Math.Max(from, last);
        return largest <= 255 ? TernType.U8 : largest <= 65535 ? TernType.U16 : null;
    }

    /// <summary>The largest value a loop with constant bounds reaches, or null if it runs no passes. Also used by the code generator (spec 3.2's fold).</summary>
    internal static long? LargestLoopValue(long from, long to, bool inclusive, int step)
    {
        if (step > 0)
        {
            long limit = inclusive ? to : to - 1;
            return from > limit ? null : from + (limit - from) / step * step;
        }
        long floor = inclusive ? to : to + 1;
        return from < floor ? null : from;
    }

    private void CheckMatch(MatchStmt m)
    {
        CheckExpr(m.Subject);
        if (m.Subject.Type.Kind == TypeKind.IntLiteral) Coerce(m.Subject, DefaultType(m.Subject));
        var type = m.Subject.Type;
        if (type.Kind is TypeKind.U16 or TypeKind.I16)
            throw new CompileError(m.Subject.Pos, "'match' on a 16-bit value is not supported yet. Match on .lo or .hi, or use if / else if");
        if (type.Kind is not (TypeKind.U8 or TypeKind.I8))
            throw new CompileError(m.Subject.Pos, $"'match' needs a u8 or i8 value, not {type}. Convert it with 'as u8', or use if / else if");
        int bias = type.IsSigned ? 128 : 0;

        // Which arm matches each value, to find values matched twice.
        var owner = new MatchPattern?[256];
        for (int a = 0; a < m.Arms.Count; a++)
        {
            var arm = m.Arms[a];
            if (arm.IsDefault && a != m.Arms.Count - 1)
                throw new CompileError(m.Arms[a + 1].Pos, "This arm can never run: '_' above it matches every value. Put '_' last");
            foreach (var p in arm.Patterns)
            {
                long low = PatternValue(p.Low, type), high = p.High is null ? low : PatternValue(p.High, type);
                if (high < low)
                    throw new CompileError(p.Pos, $"The range {low}..={high} is empty; write the smaller value first");
                p.From = (int)low + bias;
                p.To = (int)high + bias;
                for (int v = p.From; v <= p.To; v++)
                {
                    if (owner[v] is { } first)
                        throw new CompileError(p.Pos, $"{v - bias} is already matched at {first.Pos}; each value may appear in one pattern only. Remove it from one of the patterns");
                    owner[v] = p;
                }
            }
            CheckBlock(arm.Body);
        }
        if (m.IsIndexed && m.Arms.All(a => a.IsDefault))
            throw new CompileError(m.Pos, "An 'indexed match' needs at least one pattern besides '_': its table covers the values from the lowest pattern to the highest. Add a pattern, or remove 'indexed'");
    }

    /// <summary>A constant in a match pattern, as a value of the subject's type.</summary>
    private long PatternValue(Expr e, TernType type)
    {
        CheckExpr(e);
        if (e.ConstValue is not long v)
            throw new CompileError(e.Pos, "A match pattern must be a constant: a number, a character or a const. Use if / else if to compare with a variable");
        Coerce(e, type);
        return v;
    }

    /// <summary>True if the code calls a function that may change <paramref name="reg"/>: any function except a routine that keeps it.</summary>
    private bool ContainsCall(Node node, Register reg) => node switch
    {
        // A name that is not declared is not counted: checking the body reports it (review code-quality-semantics 11).
        CallExpr c => (c.Name is not ("addr" or "len" or "sizeof" or "offset") && Lookup(c.Name) is { } called && !(called is FunctionSymbol f && f.Keeps.Contains(reg)))
            || c.Args.Any(a => ContainsCall(a, reg)),
        BlockStmt b => b.Statements.Any(x => ContainsCall(x, reg)),
        IfStmt i => ContainsCall(i.Condition, reg) || ContainsCall(i.Then, reg) || (i.Else is not null && ContainsCall(i.Else, reg)),
        WhileStmt w => ContainsCall(w.Condition, reg) || ContainsCall(w.Body, reg),
        ForStmt f => ContainsCall(f.From, reg) || ContainsCall(f.To, reg) || (f.Step is not null && ContainsCall(f.Step, reg)) || ContainsCall(f.Body, reg),
        MatchStmt m => ContainsCall(m.Subject, reg) || m.Arms.Any(a => ContainsCall(a.Body, reg)),
        ReturnStmt r => r.Value is not null && ContainsCall(r.Value, reg),
        AssignStmt a => ContainsCall(a.Target, reg) || ContainsCall(a.Value, reg),
        ExprStmt e => ContainsCall(e.Expr, reg),
        VarDecl v => v.Init is not null && ContainsCall(v.Init, reg),
        UnaryExpr u => ContainsCall(u.Operand, reg),
        BinaryExpr b => ContainsCall(b.Left, reg) || ContainsCall(b.Right, reg),
        IndexExpr ix => ContainsCall(ix.Target, reg) || ContainsCall(ix.Index, reg),
        MemberExpr m => ContainsCall(m.Target, reg),
        CastExpr c => ContainsCall(c.Operand, reg),
        _ => false,
    };

    private static bool AlwaysReturns(Stmt stmt) => stmt switch
    {
        ReturnStmt => true,
        ErrorStmt => true,   // a syntax error, reported: maybe the return (spec 13)
        BlockStmt { Code: [.., var last] } => AlwaysReturns(last),
        IfStmt i => i.Else is not null && AlwaysReturns(i.Then) && AlwaysReturns(i.Else),
        WhileStmt w => w.Condition.ConstValue == 1 && !ContainsBreak(w.Body),
        MatchStmt m => m.Arms.Count > 0 && m.Arms[^1].IsDefault && m.Arms.All(a => AlwaysReturns(a.Body)),
        _ => false,
    };

    /// <summary>True if a break leaves this loop body (breaks inside nested loops do not count).</summary>
    private static bool ContainsBreak(Stmt stmt) => stmt switch
    {
        BreakStmt => true,
        BlockStmt b => b.Statements.Any(ContainsBreak),
        IfStmt i => ContainsBreak(i.Then) || (i.Else is not null && ContainsBreak(i.Else)),
        MatchStmt m => m.Arms.Any(a => ContainsBreak(a.Body)),
        _ => false,
    };

    private void CheckCondition(Expr cond)
    {
        CheckExpr(cond);
        if (cond.Type.Kind != TypeKind.Bool)
            throw new CompileError(cond.Pos, $"A condition must be bool, not {cond.Type}. Compare it, e.g. 'x != 0'");
    }
}
