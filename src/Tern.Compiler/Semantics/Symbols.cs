using Tern.Compiler.Syntax;

namespace Tern.Compiler.Semantics;

/// <summary><see cref="IntLiteral"/> is a number literal that has not yet taken a type from its context.</summary>
public enum TypeKind { Error, Void, U8, I8, U16, I16, Bool, Array, Ptr, IntLiteral }

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

    private TernType(TypeKind kind, TernType? element = null, int length = 0)
    {
        Kind = kind;
        Element = element;
        Length = length;
    }

    public static TernType ArrayOf(TernType element, int length) => new(TypeKind.Array, element, length);
    public static TernType PointerTo(TernType target) => new(TypeKind.Ptr, target);

    public bool IsInteger => Kind is TypeKind.U8 or TypeKind.I8 or TypeKind.U16 or TypeKind.I16;
    public bool IsSigned => Kind is TypeKind.I8 or TypeKind.I16;
    public bool IsArray => Kind == TypeKind.Array;
    public bool IsPointer => Kind == TypeKind.Ptr;
    /// <summary>A value that fits in one or two bytes and can be loaded, stored and compared.</summary>
    public bool IsScalar => IsInteger || Kind == TypeKind.Bool || Kind == TypeKind.Ptr;

    public int Size => Kind switch
    {
        TypeKind.U8 or TypeKind.I8 or TypeKind.Bool => 1,
        TypeKind.U16 or TypeKind.I16 or TypeKind.Ptr => 2,
        TypeKind.Array => Element!.Size * Length,
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
            TypeKind.Array => Length == other.Length && Element!.SameAs(other.Element!),
            TypeKind.Ptr => Element!.SameAs(other.Element!),
            _ => true,
        };

    public override string ToString() => Kind switch
    {
        TypeKind.Array => $"[{Element}; {Length}]",
        TypeKind.Ptr => $"ptr {Element}",
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
}

public enum VariableKind { Global, Local, Parameter, Temp }

public sealed class VariableSymbol(string name, SourcePos pos, TernType type, VariableKind kind, FunctionSymbol? owner)
    : Symbol(name, pos)
{
    public TernType Type { get; } = type;
    public VariableKind Kind { get; } = kind;
    /// <summary>The function whose frame holds this variable; null for globals.</summary>
    public FunctionSymbol? Owner { get; } = owner;

    /// <summary>Set for <c>@ $addr</c> variables. These are volatile.</summary>
    public int? FixedAddress { get; set; }
    public bool RequireZeroPage { get; set; }
    /// <summary>Set for loop variables that live in a register (<c>using X</c>). They have no memory.</summary>
    public Register? Register { get; set; }
    /// <summary>Set for parameters passed in a register. The function stores them into its frame on entry.</summary>
    public Register? PassedIn { get; set; }
    /// <summary>Loop variables cannot be assigned.</summary>
    public bool ReadOnly { get; set; }
    /// <summary>Initial bytes for globals (null means zero).</summary>
    public byte[]? InitialBytes { get; set; }
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

    /// <summary>Estimated access count, used to rank zero-page candidates.</summary>
    public long Weight { get; set; }
    /// <summary>Assigned by the allocator.</summary>
    public int Address { get; set; } = -1;

    public bool IsVolatile => FixedAddress.HasValue;
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
    /// <summary>Set for a routine that already exists at this address (<c>fn f() @ $FFD2</c>). It has no body or frame.</summary>
    public int? FixedAddress { get; set; }
    /// <summary>Registers (A, X, Y) a routine at a fixed address promises to leave unchanged.</summary>
    public HashSet<Register> Keeps { get; } = [];
    /// <summary>Constants a routine at a fixed address receives on every call (<c>true using carry</c>, <c>3 using X</c>).</summary>
    public List<(Register Register, long Value)> FixedInputs { get; } = [];

    public string Label => Name;

    public override string ToString() => Name;
}
