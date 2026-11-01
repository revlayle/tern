using Tern.Compiler.Semantics;
using Tern.Cpu;

namespace Tern.Compiler.Syntax;

public abstract class Node(SourcePos pos)
{
    public SourcePos Pos { get; } = pos;
}

// ---------- Types ----------

public abstract class TypeSyntax(SourcePos pos) : Node(pos);
public sealed class NamedTypeSyntax(SourcePos pos, string name) : TypeSyntax(pos)
{
    public string Name { get; } = name;
}
public sealed class PointerTypeSyntax(SourcePos pos, TypeSyntax target) : TypeSyntax(pos)
{
    public TypeSyntax Target { get; } = target;
}
public sealed class ArrayTypeSyntax(SourcePos pos, TypeSyntax element, Expr length) : TypeSyntax(pos)
{
    public TypeSyntax Element { get; } = element;
    public Expr Length { get; } = length;
}

// ---------- Expressions ----------

public abstract class Expr(SourcePos pos) : Node(pos)
{
    /// <summary>Set by the checker.</summary>
    public TernType Type { get; set; } = TernType.Error;
    /// <summary>Set by the checker when the expression is a compile-time constant.</summary>
    public long? ConstValue { get; set; }
}

public sealed class NumberExpr(SourcePos pos, long value) : Expr(pos)
{
    public long Value { get; } = value;
}
/// <summary>A char literal such as <c>'A'</c> or <c>screen'A'</c>; the checker turns it into a number.</summary>
public sealed class CharExpr(SourcePos pos, char value, string? encoding) : Expr(pos)
{
    public char Value { get; } = value;
    /// <summary>The encoding prefix, or null for the target's default.</summary>
    public string? Encoding { get; } = encoding;
}
/// <summary>A string literal: <c>"..."</c> in the target's default encoding, or <c>screen"..."</c> in a named one.</summary>
public sealed class StringExpr(SourcePos pos, string text, string? encoding) : Expr(pos)
{
    /// <summary>The characters after escapes; see <see cref="Lexer.RawByteBase"/>.</summary>
    public string Text { get; } = text;
    /// <summary>The encoding prefix, or null for the target's default.</summary>
    public string? Encoding { get; } = encoding;
}
public sealed class BoolExpr(SourcePos pos, bool value) : Expr(pos)
{
    public bool Value { get; } = value;
}
public sealed class NameExpr(SourcePos pos, string name) : Expr(pos)
{
    public string Name { get; } = name;
    /// <summary>Set by the checker when the name is a variable (constants are folded instead).</summary>
    public VariableSymbol? Symbol { get; set; }
}
public sealed class UnaryExpr(SourcePos pos, TokenKind op, Expr operand) : Expr(pos)
{
    public TokenKind Op { get; } = op;
    public Expr Operand { get; } = operand;
}
public sealed class BinaryExpr(SourcePos pos, TokenKind op, Expr left, Expr right) : Expr(pos)
{
    public TokenKind Op { get; } = op;
    public Expr Left { get; } = left;
    public Expr Right { get; } = right;
}
public sealed class CallExpr(SourcePos pos, string name, List<Expr> args) : Expr(pos)
{
    public string Name { get; } = name;
    public List<Expr> Args { get; } = args;
    public FunctionSymbol? Function { get; set; }
    /// <summary>Set by the checker for <c>addr(name)</c>.</summary>
    public VariableSymbol? AddressOf { get; set; }
}
public sealed class IndexExpr(SourcePos pos, Expr target, Expr index) : Expr(pos)
{
    public Expr Target { get; } = target;
    public Expr Index { get; } = index;
}
/// <summary>Member access. In v0 only <c>.lo</c> and <c>.hi</c> on 16-bit values.</summary>
public sealed class MemberExpr(SourcePos pos, Expr target, string member) : Expr(pos)
{
    public Expr Target { get; } = target;
    public string Member { get; } = member;
}
public sealed class ArrayLiteralExpr(SourcePos pos, List<Expr> elements) : Expr(pos)
{
    public List<Expr> Elements { get; } = elements;
}
public sealed class CastExpr(SourcePos pos, Expr operand, TypeSyntax target) : Expr(pos)
{
    public Expr Operand { get; } = operand;
    public TypeSyntax Target { get; } = target;
}

// ---------- Statements ----------

public abstract class Stmt(SourcePos pos) : Node(pos);

public enum PlacementKind { None, Address, ZeroPage }

public sealed class Placement(PlacementKind kind, Expr? address = null)
{
    public static readonly Placement None = new(PlacementKind.None);
    public PlacementKind Kind { get; } = kind;
    public Expr? Address { get; } = address;
}

public sealed class VarDecl(SourcePos pos, string name, TypeSyntax? type, Placement placement, Expr? init) : Stmt(pos)
{
    public string Name { get; } = name;
    public TypeSyntax? TypeSyntax { get; } = type;
    public Placement Placement { get; } = placement;
    public Expr? Init { get; } = init;
    /// <summary>Declared with <c>data</c> instead of <c>var</c>.</summary>
    public bool IsData { get; init; }
    public VariableSymbol? Symbol { get; set; }
}
public sealed class AssignStmt(SourcePos pos, Expr target, TokenKind op, Expr value) : Stmt(pos)
{
    public Expr Target { get; } = target;
    /// <summary><see cref="TokenKind.Eq"/> or a compound operator such as <see cref="TokenKind.PlusEq"/>.</summary>
    public TokenKind Op { get; } = op;
    public Expr Value { get; } = value;
}
public sealed class ExprStmt(SourcePos pos, Expr expr) : Stmt(pos)
{
    public Expr Expr { get; } = expr;
}
public sealed class BlockStmt(SourcePos pos, List<Stmt> statements) : Stmt(pos)
{
    public List<Stmt> Statements { get; } = statements;
}
public sealed class IfStmt(SourcePos pos, Expr condition, BlockStmt then, Stmt? @else) : Stmt(pos)
{
    public Expr Condition { get; } = condition;
    public BlockStmt Then { get; } = then;
    /// <summary>A <see cref="BlockStmt"/>, or an <see cref="IfStmt"/> for "else if".</summary>
    public Stmt? Else { get; } = @else;
}
public sealed class WhileStmt(SourcePos pos, Expr condition, BlockStmt body) : Stmt(pos)
{
    public Expr Condition { get; } = condition;
    public BlockStmt Body { get; } = body;
}
public sealed class ForStmt(
    SourcePos pos, string? variable, Expr from, Expr to, bool inclusive, Expr? step, Register? usingRegister, BlockStmt body)
    : Stmt(pos)
{
    /// <summary>Null for <c>_</c>.</summary>
    public string? Variable { get; } = variable;
    public Expr From { get; } = from;
    public Expr To { get; } = to;
    public bool Inclusive { get; } = inclusive;
    public Expr? Step { get; } = step;
    public Register? UsingRegister { get; } = usingRegister;
    public BlockStmt Body { get; } = body;
    /// <summary>The loop counter. For <c>_</c> the checker creates a hidden one.</summary>
    public VariableSymbol? Symbol { get; set; }
    /// <summary>Signed step, set by the checker: 1, -1, or the <c>step</c> value.</summary>
    public int StepValue { get; set; }
}
public sealed class BreakStmt(SourcePos pos) : Stmt(pos);

// ---------- Assembly ----------

/// <summary>An <c>asm { ... }</c> block, or the body of an <c>asm fn</c>.</summary>
public sealed class AsmStmt(SourcePos pos, List<AsmLine> lines) : Stmt(pos)
{
    public List<AsmLine> Lines { get; } = lines;
    /// <summary>Set by the checker: what the block reads and changes.</summary>
    public AsmEffects? Effects { get; set; }
}

public abstract class AsmLine(SourcePos pos) : Node(pos);

/// <summary>A local label such as <c>.loop:</c>. <see cref="Name"/> includes the dot.</summary>
public sealed class AsmLabel(SourcePos pos, string name) : AsmLine(pos)
{
    public string Name { get; } = name;
}

/// <summary>How an operand is written, before the checker picks the addressing mode.</summary>
public enum AsmForm
{
    /// <summary>No operand: <c>rts</c>, <c>asl</c>.</summary>
    None,
    /// <summary><c>asl a</c></summary>
    Accumulator,
    /// <summary><c>#value</c></summary>
    Immediate,
    /// <summary><c>address</c>, <c>address,x</c>, <c>address,y</c>, or a branch target.</summary>
    Direct,
    /// <summary><c>(address)</c>, only for <c>jmp</c>.</summary>
    Indirect,
    /// <summary><c>(zp,x)</c></summary>
    IndexedIndirect,
    /// <summary><c>(zp),y</c></summary>
    IndirectIndexed,
}

public sealed class AsmInstruction(SourcePos pos, Mnemonic mnemonic, AsmForm form, Expr? operand, Register? index)
    : AsmLine(pos)
{
    public Mnemonic Mnemonic { get; } = mnemonic;
    public AsmForm Form { get; } = form;
    public Expr? Operand { get; } = operand;
    /// <summary>X or Y for <c>address,x</c> and <c>address,y</c>.</summary>
    public Register? Index { get; } = index;

    /// <summary>Set by the checker. Absolute modes may still become zero-page modes in the assembler.</summary>
    public AddressingMode Mode { get; set; }
    /// <summary>Set by the checker: the operand's value.</summary>
    public AsmValue? Value { get; set; }
}

/// <summary>A local label used as an operand: <c>bne .loop</c>.</summary>
public sealed class AsmLabelExpr(SourcePos pos, string name) : Expr(pos)
{
    /// <summary>Includes the dot.</summary>
    public string Name { get; } = name;
}

/// <summary>
/// The value of an assembly operand: a number, or the address of a variable, function or local
/// label plus <see cref="Number"/> as an offset. <see cref="High"/> selects a byte of the address
/// (<c>.lo</c> is false, <c>.hi</c> is true).
/// </summary>
public sealed record AsmValue(
    long Number, VariableSymbol? Variable = null, FunctionSymbol? Function = null, AsmLabel? Label = null, bool? High = null)
{
    public bool IsNumber => Variable is null && Function is null && Label is null;
}
public sealed class ContinueStmt(SourcePos pos) : Stmt(pos);
public sealed class ReturnStmt(SourcePos pos, Expr? value) : Stmt(pos)
{
    public Expr? Value { get; } = value;
}

// ---------- Declarations ----------

public sealed class ConstDecl(SourcePos pos, string name, TypeSyntax? type, Expr value) : Node(pos)
{
    public string Name { get; } = name;
    public TypeSyntax? TypeSyntax { get; } = type;
    public Expr Value { get; } = value;
}

public sealed class ParamSyntax(SourcePos pos, string name, TypeSyntax type, Register? usingRegister) : Node(pos)
{
    public string Name { get; } = name;
    public TypeSyntax Type { get; } = type;
    public Register? UsingRegister { get; } = usingRegister;
    /// <summary>A fixed input (<c>true using carry</c>): the constant passed on every call. Name and type are then empty.</summary>
    public Expr? FixedValue { get; init; }
}

public sealed class FnDecl(
    SourcePos pos, string name, List<ParamSyntax> parameters, TypeSyntax? returnType, Register? returnUsing, BlockStmt body)
    : Node(pos)
{
    public string Name { get; } = name;
    public List<ParamSyntax> Parameters { get; } = parameters;
    public TypeSyntax? ReturnType { get; } = returnType;
    public Register? ReturnUsing { get; } = returnUsing;
    public BlockStmt Body { get; } = body;
    /// <summary>An <c>asm fn</c>: the body holds one <see cref="AsmStmt"/>.</summary>
    public bool IsAsm { get; init; }
    /// <summary>For a routine at a fixed address (<c>fn f() @ $FFD2</c>): the address. Such a function has no body.</summary>
    public Expr? Address { get; init; }
    /// <summary>Registers named after <c>keeps</c>, with their positions.</summary>
    public List<(Register Register, SourcePos Pos)> Keeps { get; init; } = [];
    public FunctionSymbol? Symbol { get; set; }
}

public sealed class ProgramSyntax(List<ConstDecl> consts, List<VarDecl> globals, List<FnDecl> functions)
{
    public List<ConstDecl> Consts { get; } = consts;
    public List<VarDecl> Globals { get; } = globals;
    public List<FnDecl> Functions { get; } = functions;
}
