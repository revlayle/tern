using Tern.Compiler.Syntax;

namespace Tern.Compiler.Semantics;

/// <summary><see cref="IntLiteral"/> is a number literal that has not yet taken a type from its context.</summary>
public enum TypeKind { Error, Void, U8, I8, U16, I16, Bool, Array, Ptr, Struct, IntLiteral }

public sealed class TernType
{
    public static readonly TernType Error = new(TypeKind.Error);
    public static readonly TernType Void = new(TypeKind.Void);
    public static readonly TernType U8 = new(TypeKind.U8);
    public static readonly TernType I8 = new(TypeKind.I8);
    public static readonly TernType U16 = new(TypeKind.U16);
    public static readonly TernType I16 = new(TypeKind.I16);
    public static readonly TernType Bool = new(TypeKind.Bool);
    public static readonly TernType IntLiteral = new(TypeKind.IntLiteral);

    public TypeKind Kind { get; }
    public TernType? Element { get; }
    public int Length { get; }
    /// <summary>For a struct type: its declaration and layout.</summary>
    public StructSymbol? Struct { get; }
    /// <summary>An array of structs declared <c>linear</c>: whole records one after another, instead of one table per field.</summary>
    public bool Linear { get; }

    private TernType(TypeKind kind, TernType? element = null, int length = 0, StructSymbol? @struct = null, bool linear = false)
    {
        Kind = kind;
        Element = element;
        Length = length;
        Struct = @struct;
        Linear = linear;
    }

    public static TernType ArrayOf(TernType element, int length, bool linear = false) => new(TypeKind.Array, element, length, linear: linear);
    public static TernType PointerTo(TernType target) => new(TypeKind.Ptr, target);
    public static TernType StructOf(StructSymbol s) => new(TypeKind.Struct, @struct: s);

    public bool IsInteger => Kind is TypeKind.U8 or TypeKind.I8 or TypeKind.U16 or TypeKind.I16;
    public bool IsSigned => Kind is TypeKind.I8 or TypeKind.I16;
    public bool IsArray => Kind == TypeKind.Array;
    public bool IsPointer => Kind == TypeKind.Ptr;
    public bool IsStruct => Kind == TypeKind.Struct;
    /// <summary>An array whose elements are structs.</summary>
    public bool IsStructArray => IsArray && Element!.IsStruct;
    /// <summary>A value that fits in one or two bytes and can be loaded, stored and compared.</summary>
    public bool IsScalar => IsInteger || Kind == TypeKind.Bool || Kind == TypeKind.Ptr;

    public int Size => Kind switch
    {
        TypeKind.U8 or TypeKind.I8 or TypeKind.Bool => 1,
        TypeKind.U16 or TypeKind.I16 or TypeKind.Ptr => 2,
        TypeKind.Array => Element!.Size * Length,
        TypeKind.Struct => Struct!.Size,
        _ => 0,
    };

    public long MinValue => Kind switch { TypeKind.I8 => -128, TypeKind.I16 => -32768, _ => 0 };
    public long MaxValue => Kind switch
    {
        TypeKind.U8 => 255, TypeKind.I8 => 127, TypeKind.U16 or TypeKind.Ptr => 65535, TypeKind.I16 => 32767, TypeKind.Bool => 1, _ => 0,
    };

    public bool SameAs(TernType other) =>
        Kind == other.Kind && Kind switch
        {
            TypeKind.Array => Length == other.Length && Linear == other.Linear && Element!.SameAs(other.Element!),
            TypeKind.Ptr => Element!.SameAs(other.Element!),
            TypeKind.Struct => Struct == other.Struct,
            _ => true,
        };

    public override string ToString() => Kind switch
    {
        TypeKind.Array => $"[{Element}; {Length}]",
        TypeKind.Ptr => $"ptr {Element}",
        TypeKind.Struct => Struct!.Name,
        TypeKind.IntLiteral => "number",
        _ => Kind.ToString().ToLowerInvariant(),
    };
}

/// <summary>A CPU register, register pair or flag named by <c>using</c>.</summary>
public enum Register { A, X, Y, AX, AY, XY, Carry, Zero, Negative }

public static class RegisterExtensions
{
    /// <summary>True if <paramref name="reg"/> is or contains <paramref name="part"/>: AX uses A and X.</summary>
    public static bool Uses(this Register reg, Register part) => reg switch
    {
        Register.AX => part is Register.A or Register.X,
        Register.AY => part is Register.A or Register.Y,
        Register.XY => part is Register.X or Register.Y,
        _ => reg == part,
    };
}

public abstract class Symbol(string name, SourcePos pos)
{
    public string Name { get; } = name;
    public SourcePos Pos { get; } = pos;
}

public sealed class ConstSymbol(string name, SourcePos pos, TernType type, long value) : Symbol(name, pos)
{
    public TernType Type { get; } = type;
    public long Value { get; } = value;
    /// <summary>The module parameters this value was computed from, for reporting a failed assert at the import.</summary>
    public HashSet<string> UsedParameters { get; init; } = [];
}

/// <summary>A field of a struct: its type and its offset from the start of the struct.</summary>
public sealed record StructField(string Name, SourcePos Pos, TernType Type, int Offset);

public sealed class StructSymbol(string name, SourcePos pos, StructDecl declaration) : Symbol(name, pos)
{
    public StructDecl Declaration { get; } = declaration;
    /// <summary>Filled when the struct is laid out.</summary>
    public List<StructField> Fields { get; } = [];
    public int Size { get; set; }
    /// <summary>0 = not laid out, 1 = being laid out, 2 = done.</summary>
    public int LayoutState { get; set; }
    public TernType Type => _type ??= TernType.StructOf(this);
    private TernType? _type;

    public StructField? Field(string name) => Fields.FirstOrDefault(f => f.Name == name);
}

public enum PlaceKind
{
    /// <summary>In a variable: <see cref="StructPlace.Base"/> + offset.</summary>
    Memory,
    /// <summary>
    /// Record <see cref="StructPlace.Index"/> of an array of structs, stored one table per field:
    /// byte k of record i is at Base + k * length + i.
    /// </summary>
    Table,
    /// <summary>Where <see cref="StructPlace.Pointer"/> points, plus offset.</summary>
    Pointer,
}

/// <summary>Where a struct (or an array field of one) is: <see cref="Offset"/> is the byte offset inside the record.</summary>
public sealed record StructPlace(PlaceKind Kind, int Offset, VariableSymbol? Base = null, Syntax.Expr? Index = null, Syntax.Expr? Pointer = null);

public enum VariableKind { Global, Local, Parameter, Temp }

public sealed class VariableSymbol(string name, SourcePos pos, TernType type, VariableKind kind, FunctionSymbol? owner)
    : Symbol(name, pos)
{
    public TernType Type { get; } = type;
    public VariableKind Kind { get; } = kind;
    /// <summary>The function whose frame holds this variable; null for globals.</summary>
    public FunctionSymbol? Owner { get; } = owner;

    /// <summary>Set for <c>@ $addr</c> variables.</summary>
    public int? FixedAddress { get; set; }
    /// <summary>Set for <c>@ region</c> variables and data: the region's name. The allocator gives them an address in it.</summary>
    public string? Region { get; set; }
    public bool RequireZeroPage { get; set; }
    /// <summary>Set for loop variables that live in a register (<c>using X</c>). They have no memory.</summary>
    public Register? Register { get; set; }
    /// <summary>Set for parameters passed in a register. The function stores them into its frame on entry.</summary>
    public Register? PassedIn { get; set; }
    /// <summary>Loop variables cannot be assigned.</summary>
    public bool ReadOnly { get; set; }
    /// <summary>Initial bytes for globals (null means zero).</summary>
    public byte[]? InitialBytes { get; set; }
    /// <summary>A global declared <c>noinit</c>: not zeroed at startup.</summary>
    public bool NoInit { get; init; }
    /// <summary>Declared with <c>data</c>: read-only, stored in the program image.</summary>
    public bool IsData { get; set; }
    /// <summary>A pointer read or written through with <c>p[i]</c>. It must be in zero page.</summary>
    public bool Dereferenced { get; set; }
    /// <summary>Its address is taken with <c>addr()</c> or in assembly (<c>#name.lo</c>), so a pointer may change it.</summary>
    public bool AddressTaken { get; set; }
    /// <summary>Used in assembly with a zero-page-only mode, e.g. <c>lda (p),y</c>. It must be in zero page.</summary>
    public bool AsmZeroPage { get; set; }
    /// <summary>
    /// Offsets at which this variable is used as <c>jmp (name+offset)</c>. On the NMOS 6502 such a
    /// vector must not start at an address ending in $FF, so the variable is placed to avoid that.
    /// </summary>
    public HashSet<int> JumpVectorOffsets { get; } = [];
    /// <summary>
    /// Function addresses in the initial value (<c>data table: [u16; 2] = [addr(a), addr(b)]</c>): the
    /// index of the low byte in <see cref="InitialBytes"/>, which hold zeros there until the program is placed.
    /// </summary>
    public List<(int Index, FunctionSymbol Function)> FunctionAddresses { get; } = [];

    /// <summary>Estimated access count, used to rank zero-page candidates.</summary>
    public long Weight { get; set; }
    /// <summary>Assigned by the allocator.</summary>
    public int Address { get; set; } = -1;

    /// <summary>
    /// Set for a view: part of another variable's memory, such as the field <c>player.hp</c> or the
    /// table of one field in an array of structs. A view has no memory of its own; it is at
    /// <see cref="AliasOf"/> + <see cref="AliasOffset"/>.
    /// </summary>
    public VariableSymbol? AliasOf { get; init; }
    public int AliasOffset { get; init; }
    /// <summary>For an array view of 16-bit values: the distance from the low-byte table to the high-byte table (default: the length).</summary>
    public int? HighTable { get; init; }
    /// <summary>The variable that owns the memory: itself, or the variable a view is part of.</summary>
    public VariableSymbol Storage => AliasOf ?? this;

    /// <summary>Declared <c>volatile var</c>.</summary>
    public bool DeclaredVolatile { get; init; }
    /// <summary>At a fixed address in one of the platform's volatile areas, e.g. I/O registers.</summary>
    public bool InVolatileArea { get; set; }
    /// <summary>A global used by functions reached from two roots (main and a handler, or two handlers).</summary>
    public bool UsedFromTwoRoots { get; set; }

    /// <summary>
    /// Every read and write happens exactly once and in order; the optimizer never removes, merges
    /// or reorders them (spec 4). For a view, the variable it is part of decides.
    /// </summary>
    public bool IsVolatile => Storage is var s && (s.DeclaredVolatile || s.InVolatileArea || s.UsedFromTwoRoots);
    /// <summary>Must be placed in zero page; compilation fails if it does not fit.</summary>
    public bool ForcedZeroPage => RequireZeroPage || Dereferenced || AsmZeroPage;
    public bool InMemory => Register is null;

    /// <summary>Name used in listings and label files, unique per program.</summary>
    public string QualifiedName => Owner is null ? Name : $"{Owner.Name}.{Name}";

    public override string ToString() => QualifiedName;
}

public sealed class FunctionSymbol(string name, SourcePos pos) : Symbol(name, pos)
{
    public List<VariableSymbol> Parameters { get; } = [];
    public TernType ReturnType { get; set; } = TernType.Void;
    /// <summary>Where the return value is passed.</summary>
    public Register? ReturnRegister { get; set; }
    /// <summary>All variables in this function's frame: parameters, locals and temporaries.</summary>
    public List<VariableSymbol> FrameVariables { get; } = [];
    public HashSet<FunctionSymbol> Callees { get; } = [];
    /// <summary>Global variables this function names; only globals used by reachable functions go into the program.</summary>
    public HashSet<VariableSymbol> UsedGlobals { get; } = [];
    public FnDecl? Declaration { get; set; }
    /// <summary>An <c>interrupt fn</c>: a root of the call graph, with frames of its own.</summary>
    public bool IsInterrupt { get; set; }
    /// <summary>An interrupt fn that another interrupt fn calls: compiled as a part (ends with rts, saves nothing). Set by the checker.</summary>
    public bool IsPart { get; set; }
    /// <summary>An <c>inline fn</c>: copied into each caller. It keeps its frame, but has no code of its own.</summary>
    public bool IsInline { get; set; }
    /// <summary>Assembly writes into this function's code (<c>sta f+1</c>): the optimizer trusts none of its instructions.</summary>
    public bool CodePatched { get; set; }
    /// <summary>Interrupt handlers this function names in assembly (to install them); they are kept in the program.</summary>
    public HashSet<FunctionSymbol> UsedInterrupts { get; } = [];
    /// <summary>Functions whose address this function takes (<c>addr(f)</c>, <c>#f.lo</c>); they are kept in the program as roots.</summary>
    public HashSet<FunctionSymbol> UsedAddresses { get; } = [];
    /// <summary>
    /// Its address is taken. The compiler cannot see calls through an address, so this function and
    /// everything it reaches get frames that overlap no other frame (spec 6.5).
    /// </summary>
    public bool AddressTaken { get; set; }
    /// <summary>Reached from an address-taken function: its frame overlaps no other frame.</summary>
    public bool Isolated { get; set; }
    /// <summary>
    /// The roots it is reached from: main and interrupt handlers (not address-taken functions, whose
    /// callers are unknown). More than one is allowed only for a function with no memory (spec 8.1).
    /// </summary>
    public HashSet<FunctionSymbol> Roots { get; } = [];
    /// <summary>Set for a routine that already exists at this address (<c>fn f() @ $FFD2</c>). It has no body or frame.</summary>
    public int? FixedAddress { get; set; }
    /// <summary>Registers (A, X, Y) a routine at a fixed address promises to leave unchanged.</summary>
    public HashSet<Register> Keeps { get; } = [];
    /// <summary>Constants a routine at a fixed address receives on every call (<c>true using carry</c>, <c>3 using X</c>).</summary>
    public List<(Register Register, long Value)> FixedInputs { get; } = [];

    public string Label => Name;

    public override string ToString() => Name;
}
