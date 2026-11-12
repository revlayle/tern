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
public sealed class ArrayTypeSyntax(SourcePos pos, TypeSyntax element, Expr? length) : TypeSyntax(pos)
{
    public TypeSyntax Element { get; } = element;
    /// <summary>The length, or null for <c>[T; _]</c>: taken from the list initial value (spec 4).</summary>
    public Expr? Length { get; } = length;
}

// ---------- Expressions ----------

public abstract class Expr(SourcePos pos) : Node(pos)
{
    /// <summary>Set by the checker.</summary>
    public TernType Type { get; set; } = TernType.Error;
    /// <summary>Set by the checker when the expression is a compile-time constant.</summary>
    public long? ConstValue { get; set; }
    /// <summary>
    /// For a constant expression (type <c>IntLiteral</c>, computed exactly, spec 3.1): the type its typed
    /// constants give it, which it takes when mixed with a runtime value or used with no context, and
    /// the constant that set it. Null for one made only of literals and untyped constants.
    /// </summary>
    public TernType? ConstType { get; set; }
    public string? ConstTypeFrom { get; set; }
    /// <summary>A constant made with <c>~</c>: a negative value takes the width of the type it is used as (spec 3.1).</summary>
    public bool IsBitPattern { get; set; }
    /// <summary>
    /// Set by the checker for a struct field: the same access written with plain variables, arrays
    /// and pointers (<c>player.hp</c> becomes a view of <c>player</c>'s memory). It replaces this
    /// expression before code generation.
    /// </summary>
    public Expr? Lowered { get; set; }
    /// <summary>Set by the checker on struct-typed expressions and array fields: where the memory is.</summary>
    public StructPlace? Place { get; set; }
}

public sealed class NumberExpr(SourcePos pos, long value) : Expr(pos)
{
    public long Value { get; } = value;
}
/// <summary>A real literal such as <c>1.5</c> (spec 4.1): only in compile-time code.</summary>
public sealed class RealExpr(SourcePos pos, double value, string text) : Expr(pos)
{
    public double Value { get; } = value;
    /// <summary>As written, for error messages.</summary>
    public string Text { get; } = text;
}
/// <summary>
/// A list built with <c>for</c> (spec 4.1): <c>[expr for i in from..to step k]</c>, computed at compile time.
/// </summary>
public sealed class ListForExpr(SourcePos pos, Expr element, string? variable, Expr from, Expr to, bool inclusive, Expr? step) : Expr(pos)
{
    public Expr Element { get; } = element;
    /// <summary>Null for <c>_</c>.</summary>
    public string? Variable { get; } = variable;
    public Expr From { get; } = from;
    public Expr To { get; } = to;
    public bool Inclusive { get; } = inclusive;
    public Expr? Step { get; } = step;
    /// <summary>The range as written, "9..=0", for messages.</summary>
    public string RangeText { get; init; } = "";
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
/// <summary>One <c>field = value</c> of a struct literal.</summary>
public sealed record FieldValue(SourcePos Pos, string Name, Expr Value);

/// <summary>
/// A struct literal, <c>Point { x = 1, y = 2 }</c>: only an initial value of a global or data (spec 3.3).
/// The checker turns it into bytes; it is never a runtime value.
/// </summary>
public sealed class StructLiteralExpr(SourcePos pos, string structName, List<FieldValue> fields) : Expr(pos)
{
    public string StructName { get; } = structName;
    public List<FieldValue> Fields { get; } = fields;
}
public sealed class BoolExpr(SourcePos pos, bool value) : Expr(pos)
{
    public bool Value { get; } = value;
}
public sealed class NameExpr(SourcePos pos, string name) : Expr(pos)
{
    public string Name { get; } = name;
    /// <summary>
    /// For a name in an instruction a generator emitted (spec 4.1): the symbol it names in the
    /// generator's module, which the asm block's module may not see by that name.
    /// </summary>
    public Symbol? Bound { get; init; }
    /// <summary>Set by the checker when the name is a variable (constants are folded instead).</summary>
    public VariableSymbol? Symbol { get; set; }
}
public sealed class UnaryExpr(SourcePos pos, TokenKind op, Expr operand) : Expr(pos)
{
    public TokenKind Op { get; } = op;
    public Expr Operand { get; set; } = operand;
}
public sealed class BinaryExpr(SourcePos pos, TokenKind op, Expr left, Expr right) : Expr(pos)
{
    public TokenKind Op { get; } = op;
    public Expr Left { get; set; } = left;
    public Expr Right { get; set; } = right;
    /// <summary>
    /// Set by the checker on the shift that scales an index into a linear array (by 2, or by a
    /// record's size): the array and its element count, so the emulator can check the index before
    /// the shift, where it cannot have wrapped yet (M20).
    /// </summary>
    public (string Array, int Elements)? ScalesIndexOf { get; init; }
}
public sealed class CallExpr(SourcePos pos, string name, List<Expr> args) : Expr(pos)
{
    public string Name { get; } = name;
    public List<Expr> Args { get; } = args;
    public FunctionSymbol? Function { get; set; }
    /// <summary>Set by the checker for <c>addr(name)</c>.</summary>
    public VariableSymbol? AddressOf { get; set; }
    /// <summary>Set by the checker for <c>addr(f)</c> of a function: a <c>u16</c> known when the program is placed.</summary>
    public FunctionSymbol? FunctionAddress { get; set; }
}
public sealed class IndexExpr(SourcePos pos, Expr target, Expr index) : Expr(pos)
{
    public Expr Target { get; set; } = target;
    public Expr Index { get; set; } = index;

    // Through a pointer (p[i] and struct fields), Y = Offset + i * Stride for the first byte,
    // and the second byte of a 16-bit value is SecondByte further on.
    /// <summary>A constant added to Y: the field's offset in the struct.</summary>
    public int Offset { get; init; }
    /// <summary>Bytes per index step; 0 means the element size.</summary>
    public int Stride { get; init; }
    /// <summary>The distance to the second byte of a 16-bit element: 1, or the length of a split array field.</summary>
    public int SecondByte { get; init; } = 1;
}
/// <summary>Member access. In v0 only <c>.lo</c> and <c>.hi</c> on 16-bit values.</summary>
public sealed class MemberExpr(SourcePos pos, Expr target, string member) : Expr(pos)
{
    public Expr Target { get; set; } = target;
    public string Member { get; } = member;
}
public sealed class ArrayLiteralExpr(SourcePos pos, List<Expr> elements) : Expr(pos)
{
    public List<Expr> Elements { get; } = elements;
}
public sealed class CastExpr(SourcePos pos, Expr operand, TypeSyntax target) : Expr(pos)
{
    public Expr Operand { get; set; } = operand;
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
    public Expr? Init { get; set; } = init;
    /// <summary>Declared with <c>data</c> instead of <c>var</c>.</summary>
    public bool IsData { get; init; }
    /// <summary>Declared <c>linear var</c>: an array of structs stored as whole records.</summary>
    public bool IsLinear { get; init; }
    /// <summary>Declared <c>indexed var</c>: an array of structs stored as whole records, with an offset table for variable indexes.</summary>
    public bool IsIndexed { get; init; }
    /// <summary>Declared <c>noinit var</c>: a global that is not zeroed at startup.</summary>
    public bool IsNoinit { get; set; }
    /// <summary>Declared <c>volatile var</c>: memory that something else changes (spec 4).</summary>
    public bool IsVolatile { get; set; }
    public VariableSymbol? Symbol { get; set; }
}
public sealed class AssignStmt(SourcePos pos, Expr target, TokenKind op, Expr value) : Stmt(pos)
{
    public Expr Target { get; set; } = target;
    /// <summary><see cref="TokenKind.Eq"/> or a compound operator such as <see cref="TokenKind.PlusEq"/>.</summary>
    public TokenKind Op { get; } = op;
    public Expr Value { get; set; } = value;
}
public sealed class ExprStmt(SourcePos pos, Expr expr) : Stmt(pos)
{
    public Expr Expr { get; set; } = expr;
}
public sealed class BlockStmt(SourcePos pos, List<Stmt> statements) : Stmt(pos)
{
    public List<Stmt> Statements { get; } = statements;
    /// <summary>The statements that may have code: all but local <c>const</c> declarations.</summary>
    public List<Stmt> Code => Statements.Where(s => s is not ConstDecl).ToList();
}
public sealed class IfStmt(SourcePos pos, Expr condition, BlockStmt then, Stmt? @else) : Stmt(pos)
{
    public Expr Condition { get; set; } = condition;
    public BlockStmt Then { get; } = then;
    /// <summary>A <see cref="BlockStmt"/>, or an <see cref="IfStmt"/> for "else if".</summary>
    public Stmt? Else { get; } = @else;
}
public sealed class WhileStmt(SourcePos pos, Expr condition, BlockStmt body) : Stmt(pos)
{
    public Expr Condition { get; set; } = condition;
    public BlockStmt Body { get; } = body;
}
public sealed class ForStmt(
    SourcePos pos, string? variable, Expr from, Expr to, bool inclusive, Expr? step, Register? usingRegister, BlockStmt body)
    : Stmt(pos)
{
    /// <summary>Null for <c>_</c>.</summary>
    public string? Variable { get; } = variable;
    public Expr From { get; set; } = from;
    public Expr To { get; set; } = to;
    public bool Inclusive { get; } = inclusive;
    public Expr? Step { get; } = step;
    /// <summary>The range as written, "9..=0", for messages.</summary>
    public string RangeText { get; init; } = "";
    public Register? UsingRegister { get; } = usingRegister;
    public BlockStmt Body { get; } = body;
    /// <summary>The loop counter. For <c>_</c> the checker creates a hidden one.</summary>
    public VariableSymbol? Symbol { get; set; }
    /// <summary>Signed step, set by the checker: 1, -1, or the <c>step</c> value.</summary>
    public int StepValue { get; set; }
    /// <summary>True for a step that is a u8 expression; the loop counts up and <c>StepValue</c> is 1.</summary>
    public bool VariableStep { get; set; }
}
public sealed class BreakStmt(SourcePos pos) : Stmt(pos);

/// <summary>
/// A statement with a syntax error, which was reported (spec 13). <see cref="Name"/> is the name it
/// declares (<c>var x = )</c>), or null: the checker declares it so that its uses add no errors.
/// </summary>
public sealed class ErrorStmt(SourcePos pos, string? name) : Stmt(pos)
{
    public string? Name { get; } = name;
}

/// <summary><c>match subject { 0 => a()  1, 2 => b()  3..=7 => { ... }  _ => c() }</c></summary>
public sealed class MatchStmt(SourcePos pos, Expr subject, List<MatchArm> arms) : Stmt(pos)
{
    public Expr Subject { get; set; } = subject;
    public List<MatchArm> Arms { get; } = arms;
    /// <summary>Written <c>indexed match</c>: always a jump table, with no range check (spec 5.2).</summary>
    public bool IsIndexed { get; init; }
}

/// <summary>One arm: its patterns (empty for <c>_</c>) and its body.</summary>
public sealed class MatchArm(SourcePos pos, List<MatchPattern> patterns, BlockStmt body)
{
    public SourcePos Pos { get; } = pos;
    /// <summary>Empty for the default arm <c>_</c>.</summary>
    public List<MatchPattern> Patterns { get; } = patterns;
    public bool IsDefault => Patterns.Count == 0;
    public BlockStmt Body { get; } = body;
}

/// <summary>A value <c>3</c>, or a range <c>3..=7</c> when <see cref="High"/> is set.</summary>
public sealed class MatchPattern(SourcePos pos, Expr low, Expr? high)
{
    public SourcePos Pos { get; } = pos;
    public Expr Low { get; } = low;
    public Expr? High { get; } = high;
    /// <summary>
    /// Set by the checker: the values matched, 0-255 in unsigned order. For an <c>i8</c> subject,
    /// 128 is added (the code compares after <c>eor #$80</c>), so -128 is 0 and 127 is 255.
    /// </summary>
    public int From { get; set; }
    public int To { get; set; }
}

// ---------- Assembly ----------

/// <summary>An <c>asm { ... }</c> block, or the body of an <c>asm fn</c>.</summary>
public sealed class AsmStmt(SourcePos pos, List<AsmLine> lines) : Stmt(pos)
{
    public List<AsmLine> Lines { get; } = lines;
    /// <summary>The <c>var</c> lines at the start of an <c>inline asm fn</c> body (spec 6.2): its local variables.</summary>
    public List<VarDecl> Locals { get; } = [];
    /// <summary>Set by the checker: what the block reads and changes.</summary>
    public AsmEffects? Effects { get; set; }
}

public abstract class AsmLine(SourcePos pos) : Node(pos)
{
    /// <summary>For a line a generator emitted (spec 4.1): the generator call in the asm block. <see cref="Node.Pos"/> is the emit line.</summary>
    public SourcePos? CalledFrom { get; init; }
}

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

/// <summary>A generator call on a line of its own in an asm block (spec 4.1); the checker replaces it with the instructions it emits.</summary>
public sealed class AsmGeneratorCall(SourcePos pos, CallExpr call) : AsmLine(pos)
{
    public CallExpr Call { get; } = call;
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
/// <summary><c>emit { ... }</c> in a <c>const fn</c> (spec 4.1): adds its instructions to the asm block that called the generator.</summary>
public sealed class EmitStmt(SourcePos pos, AsmStmt fragment) : Stmt(pos)
{
    public AsmStmt Fragment { get; } = fragment;
}
public sealed class ReturnStmt(SourcePos pos, Expr? value) : Stmt(pos)
{
    public Expr? Value { get; set; } = value;
}

// ---------- Declarations ----------

/// <summary>A <c>const</c> declaration: at the top level, or a statement scoped to its block (spec 4).</summary>
public sealed class ConstDecl(SourcePos pos, string name, TypeSyntax? type, Expr value) : Stmt(pos)
{
    public string Name { get; } = name;
    public TypeSyntax? TypeSyntax { get; } = type;
    public Expr Value { get; set; } = value;
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
    /// <summary>A <c>const fn</c> (spec 4.1): run only during compilation; it never becomes 6502 code.</summary>
    public bool IsConst { get; init; }
    /// <summary>An <c>asm fn</c>: the body holds one <see cref="AsmStmt"/>.</summary>
    public bool IsAsm { get; init; }
    /// <summary>An <c>interrupt fn</c>: saves the registers it changes and ends with <c>rti</c>.</summary>
    public bool IsInterrupt { get; set; }
    /// <summary>An <c>inline fn</c>: its body is copied into each caller instead of a <c>jsr</c>.</summary>
    public bool IsInline { get; set; }
    /// <summary>For a routine at a fixed address (<c>fn f() @ $FFD2</c>): the address. Such a function has no body.</summary>
    public Expr? Address { get; init; }
    /// <summary>Registers named after <c>keeps</c>, with their positions.</summary>
    public List<(Register Register, SourcePos Pos)> Keeps { get; init; } = [];
    public FunctionSymbol? Symbol { get; set; }
}

public sealed class ImportDecl(SourcePos pos, string name) : Node(pos)
{
    public string Name { get; } = name;
    /// <summary>Module parameters set by <c>import name(P = value)</c>.</summary>
    public List<ImportArg> Args { get; init; } = [];
    /// <summary>The overlay area in <c>import name @ area</c> (spec 8.5), or null.</summary>
    public string? Area { get; init; }
    public SourcePos AreaPos { get; init; }
}

/// <summary>One <c>P = value</c> in an import.</summary>
public sealed class ImportArg(SourcePos pos, string name, Expr value) : Node(pos)
{
    public string Name { get; } = name;
    public Expr Value { get; } = value;
}

/// <summary>A module parameter in the header: <c>COUNT: u8 = 32</c>.</summary>
public sealed class ModuleParam(SourcePos pos, string name, TypeSyntax type, Expr defaultValue) : Node(pos)
{
    public string Name { get; } = name;
    public TypeSyntax Type { get; } = type;
    public Expr Default { get; } = defaultValue;
}

/// <summary>The <c>module name(P: type = default, ...)</c> line at the top of a module.</summary>
public sealed class ModuleHeader(SourcePos pos, string name, List<ModuleParam> parameters) : Node(pos)
{
    public string Name { get; } = name;
    public List<ModuleParam> Parameters { get; } = parameters;
}

/// <summary>A top-level <c>assert condition, "message"</c>, checked at compile time.</summary>
public sealed class AssertDecl(SourcePos pos, Expr condition, string message) : Node(pos)
{
    public Expr Condition { get; } = condition;
    public string Message { get; } = message;
}

/// <summary>A struct field: <c>name: type</c>, optionally <c>@ offset</c>.</summary>
public sealed class FieldSyntax(SourcePos pos, string name, TypeSyntax type, Expr? offset) : Node(pos)
{
    public string Name { get; } = name;
    public TypeSyntax Type { get; } = type;
    public Expr? Offset { get; } = offset;
}

public sealed class StructDecl(SourcePos pos, string name, List<FieldSyntax> fields) : Node(pos)
{
    public string Name { get; } = name;
    public List<FieldSyntax> Fields { get; } = fields;
}

/// <summary>One source file: a program's main file or a module.</summary>
public sealed class ProgramSyntax(List<ConstDecl> consts, List<VarDecl> globals, List<FnDecl> functions)
{
    public ModuleHeader? Header { get; init; }
    public List<AssertDecl> Asserts { get; init; } = [];
    public List<ImportDecl> Imports { get; init; } = [];
    /// <summary>The <c>memory { ... }</c> block (spec 8.4), or null.</summary>
    public MemoryDecl? Memory { get; init; }
    /// <summary>Functions named by <c>export</c> lines, in order (spec 12.3).</summary>
    public List<ExportName> Exports { get; init; } = [];
    /// <summary>The address of a program's export table (<c>export f, g @ $0900</c>), or null.</summary>
    public ExportTable? ExportTable { get; init; }
    public List<StructDecl> Structs { get; init; } = [];
    /// <summary>Top-level names declared <c>private</c>: usable only inside this file.</summary>
    public HashSet<string> PrivateNames { get; init; } = [];
    public List<ConstDecl> Consts { get; } = consts;
    public List<VarDecl> Globals { get; } = globals;
    public List<FnDecl> Functions { get; } = functions;
    /// <summary>The <c>const fn</c> declarations (spec 4.1), apart from <see cref="Functions"/>: they are never compiled.</summary>
    public List<FnDecl> ConstFns { get; init; } = [];
    /// <summary>
    /// Top-level names whose declaration had a syntax error, and imports that did (spec 13): the checker
    /// declares them so that their uses add no errors. The declarations are not in the other lists.
    /// </summary>
    public List<(string Name, SourcePos Pos)> BrokenNames { get; init; } = [];
    /// <summary>The parser dropped a statement or declaration for an error (spec 13): a missing declaration may be in it.</summary>
    public bool HasSyntaxErrors { get; init; }
}

/// <summary>The <c>memory { ... }</c> block of the main file.</summary>
public sealed class MemoryDecl(SourcePos pos, List<MemoryLine> lines) : Node(pos)
{
    public List<MemoryLine> Lines { get; } = lines;
}

/// <summary>One line of the memory block: <c>first..=last kind [name]</c>.</summary>
public sealed class MemoryLine(SourcePos pos, Expr first, Expr last, string kind, string? name) : Node(pos)
{
    public Expr First { get; } = first;
    public Expr Last { get; } = last;
    /// <summary>program, zeropage, reserved, region, volatile or overlay.</summary>
    public string Kind { get; } = kind;
    /// <summary>The name after <c>region</c> or <c>overlay</c>.</summary>
    public string? Name { get; } = name;
}

/// <summary>The <c>@ address</c> of an <c>export</c> line in a program with <c>main</c> (spec 12.3).</summary>
public sealed class ExportTable(SourcePos pos, Expr address) : Node(pos)
{
    public Expr Address { get; } = address;
}

/// <summary>A function named in an <c>export</c> line, or the interface module of <c>export level_api</c>.</summary>
public sealed class ExportName(SourcePos pos, string name) : Node(pos)
{
    public string Name { get; } = name;
}
