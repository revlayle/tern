using Tern.Compiler.Syntax;

namespace Tern.Compiler.Semantics;

/// <summary>Replaces every checked expression that has a <see cref="Expr.Lowered"/> form with that form.</summary>
public static class Lowering
{
    public static void Apply(Stmt stmt)
    {
        switch (stmt)
        {
            case VarDecl v: if (v.Init is not null) v.Init = Lower(v.Init); break;
            case AssignStmt a: a.Target = Lower(a.Target); a.Value = Lower(a.Value); break;
            case ExprStmt e: e.Expr = Lower(e.Expr); break;
            case BlockStmt b: foreach (var s in b.Statements) Apply(s); break;
            case IfStmt i:
                i.Condition = Lower(i.Condition);
                Apply(i.Then);
                if (i.Else is not null) Apply(i.Else);
                break;
            case WhileStmt w: w.Condition = Lower(w.Condition); Apply(w.Body); break;
            case ForStmt f:
                f.From = Lower(f.From);
                f.To = Lower(f.To);
                if (f.Step is not null) f.Step = Lower(f.Step);
                Apply(f.Body);
                break;
            case MatchStmt m:
                m.Subject = Lower(m.Subject);
                foreach (var arm in m.Arms) Apply(arm.Body);
                break;
            case ReturnStmt r: if (r.Value is not null) r.Value = Lower(r.Value); break;
            // Nothing to lower: compile-time code, assembly, and statements with no expression.
            case BreakStmt or ContinueStmt or ErrorStmt or ConstDecl or AsmStmt or EmitStmt or AssertStmt: break;
            default: throw new InvalidOperationException($"Lowering does not know a {stmt.GetType().Name}");
        }
    }

    private static Expr Lower(Expr e)
    {
        e = e.Lowered ?? e;
        switch (e)
        {
            case UnaryExpr u: u.Operand = Lower(u.Operand); break;
            case BinaryExpr b: b.Left = Lower(b.Left); b.Right = Lower(b.Right); break;
            case CallExpr c:
                for (int i = 0; i < c.Args.Count; i++) c.Args[i] = Lower(c.Args[i]);
                break;
            case IndexExpr ix: ix.Target = Lower(ix.Target); ix.Index = Lower(ix.Index); break;
            case MemberExpr m: m.Target = Lower(m.Target); break;
            case CastExpr c: c.Operand = Lower(c.Operand); break;
            // Leaves, and values the checker has already made constant.
            case NumberExpr or RealExpr or CharExpr or StringExpr or BoolExpr or NameExpr or AsmLabelExpr
                or ArrayLiteralExpr or StructLiteralExpr or ListForExpr: break;
            default: throw new InvalidOperationException($"Lowering does not know a {e.GetType().Name}");
        }
        return e;
    }
}
