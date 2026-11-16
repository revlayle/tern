using Tern.Compiler.Syntax;

namespace Tern.Compiler.Semantics;

/// <summary>Expressions: operators, pointers, addresses and calls.</summary>
public sealed partial class Checker
{
    // ---------- Expressions ----------

    private long Weight() => (long)Math.Pow(8, Math.Min(_body.LoopDepth, 5));

    private void CheckExpr(Expr e)
    {
        // A whole expression with a compile-time part is computed by the interpreter (spec 4.1).
        if (!InRuntimeCode && e is BinaryExpr or UnaryExpr or MemberExpr or IndexExpr && NeedsInterpreter(e))
        {
            EvaluateConstant(e);
            return;
        }
        switch (e)
        {
            case NumberExpr n:
                e.Type = TernType.IntLiteral;
                e.ConstValue = n.Value;
                break;

            case BoolExpr b:
                e.Type = TernType.Bool;
                e.ConstValue = b.Value ? 1 : 0;
                break;

            case CharExpr ch:
                e.Type = TernType.IntLiteral;
                e.ConstValue = Encode(ch.Value, EncodingOf(ch.Encoding, ch.Pos), ch.Pos);
                break;

            case StructLiteralExpr lit:
                throw StructLiteralInCode(lit, null);

            case RealExpr or ListForExpr:
                EvaluateConstant(e);
                break;

            case StringExpr:
                throw new CompileError(e.Pos, "A string can only be the value of data or a global array. "
                    + "Declare it as data, e.g. 'data HI = \"HI\"', and pass addr(HI), len(HI)");

            case NameExpr n: CheckName(n); break;
            case UnaryExpr u: CheckUnary(u); break;
            case BinaryExpr b: CheckBinary(b); break;
            case CallExpr c: CheckCall(c); break;
            case IndexExpr ix: CheckIndex(ix); break;
            case MemberExpr m: CheckMember(m); break;
            case CastExpr c: CheckCast(c); break;

            case ArrayLiteralExpr:
                throw new CompileError(e.Pos, "An array literal can only be used to initialize a global array. Declare the list as data, and read it from there");

            default:
                throw new InvalidOperationException(e.GetType().Name);
        }
    }

    /// <summary>A name: a constant, a variable, or an error for a function or type used as a value.</summary>
    private void CheckName(NameExpr n)
    {
        switch (Named(n.Bound, n.Name) ?? Lookup(n.Name))
        {
            case null when n.Name is "PI" or "TAU":
                EvaluateConstant(n);
                break;
            case null: throw NotDeclared(n.Name, n.Pos);
            case ConstFnSymbol:
                throw new CompileError(n.Pos, $"'{n.Name}' is a const fn; call it with ()");
            case ConstSymbol { Real: not null }:
                if (InRuntimeCode) throw InRuntime(n);
                EvaluateConstant(n);
                break;
            case ConstSymbol c:
                // An integer constant, typed or not, is computed exactly in constant expressions (spec 3.1).
                n.ConstValue = c.Value;
                if (c.Type.IsInteger) { n.Type = TernType.IntLiteral; n.ConstType = c.Type; n.ConstTypeFrom = c.Name; }
                else { n.Type = c.Type; n.IsBitPattern = c.IsBitPattern; }
                break;
            case VariableSymbol v:
                n.Symbol = v;
                UseGlobal(v, n.Pos);
                n.Type = v.Type;
                v.Weight += Weight();
                if (v.Type.IsStruct) n.Place = new StructPlace(PlaceKind.Memory, 0, Base: v);
                break;
            case FunctionSymbol:
                throw new CompileError(n.Pos, $"'{n.Name}' is a function; call it with ()");
            case StructSymbol:
                throw new CompileError(n.Pos, $"'{n.Name}' is a struct type, not a value. Use sizeof({n.Name}) for its size, or a variable of the type");
        }
    }

    /// <summary><c>a[i]</c>: an element of an array, or the target of a pointer.</summary>
    private void CheckIndex(IndexExpr ix)
    {
        CheckExpr(ix.Target);
        CheckExpr(ix.Index);
        Coerce(ix.Index, TernType.U8);
        if (CheckArrayFieldIndex(ix)) return;
        var indexed = Unwrap(ix.Target);
        if (ix.Target.Type.IsPointer)
        {
            var element = ix.Target.Type.Element!;
            if (element.IsStruct)
                throw new CompileError(ix.Pos,
                    $"A pointer to a struct cannot be indexed: p[i] would need a multiply by the struct size. Move the pointer instead: p = p + sizeof({element})");
            if (element.Size == 2 && ix.Index.ConstValue is > 127)
                throw new CompileError(ix.Index.Pos,
                    $"Index {ix.Index.ConstValue} is too large: p[i] reaches 256 bytes, which is 128 {element} values. Move the pointer forward first: p + 256");
            if (indexed is NameExpr { Symbol: { AliasOf: null } pv }) pv.Dereferenced = true;
            ix.Type = element;
            return;
        }
        if (indexed is not NameExpr { Symbol: VariableSymbol arr } || !arr.Type.IsArray)
            throw new CompileError(ix.Pos, "Only arrays and pointers can be indexed. Use .lo or .hi for a byte of a 16-bit value");
        if (arr.Type.IsStructArray)
        {
            CheckRecordIndex(ix, arr);
            return;
        }
        if (arr.Type.Length > 256)
            throw new CompileError(ix.Pos,
                $"'{arr.Name}' has {arr.Type.Length} elements, too many for a u8 index. Read it through a pointer: addr({arr.Name})");
        if (ix.Index.ConstValue is long idx && idx >= arr.Type.Length)
            throw new CompileError(ix.Index.Pos, $"Index {idx} is past the end of '{arr.Name}' (length {arr.Type.Length}). Use an index below {arr.Type.Length}");
        CheckLoopIndex(ix.Index, arr.Name, arr.Type.Length);
        ix.Type = arr.Type.Element!;
        if (arr.Type.Linear) ix.Lowered = LinearIndex(ix);
    }

    /// <summary><c>.lo</c>, <c>.hi</c>, or a field of a struct.</summary>
    private void CheckMember(MemberExpr m)
    {
        // addr(x).lo / .hi are the bytes of the address, even for a struct with a field named lo (spec 3.4).
        if (m.Member is "lo" or "hi" && m.Target is CallExpr { Name: "addr" } address)
            m.Target = new CastExpr(address.Pos, address, new NamedTypeSyntax(address.Pos, "u16"));
        CheckMemberTarget(m);
        CheckExpr(m.Target);
        // So are those of addr(x) plus or minus a constant, the forms an initial value allows (question 84).
        if (m.Member is "lo" or "hi" && m.Target is BinaryExpr { Type.IsPointer: true } && Placed(m.Target) is not null)
            m.Target = new CastExpr(m.Target.Pos, m.Target, new NamedTypeSyntax(m.Target.Pos, "u16")) { Type = TernType.U16 };
        if (m.Target.Type.IsStruct || m.Target.Type is { IsPointer: true, Element.IsStruct: true })
        {
            CheckField(m);
            return;
        }
        if (m.Member is not ("lo" or "hi"))
            throw new CompileError(m.Pos, $"Unknown member '.{m.Member}'. Use .lo or .hi, or a field of a struct");
        // A typed 16-bit constant keeps its size (LINE.hi with LINE: u16 = 250 is 0), and so does an
        // untyped one, whatever its value (BASE.hi with BASE = $C0 is 0); a typed u8 or i8 is one byte.
        if (m.Target.Type.Kind == TypeKind.IntLiteral)
            Coerce(m.Target, m.Target.ConstType switch
            {
                { Size: 2 } wide => wide,
                null => m.Target.ConstValue < 0 ? TernType.I16 : TernType.U16,
                _ => LiteralType(m.Target),
            });
        if (m.Target.Type.IsPointer)
            throw new CompileError(m.Pos, $".{m.Member} needs a number; convert the pointer first: (p as u16).{m.Member}");
        if (m.Target.Type.Size != 2 || !m.Target.Type.IsInteger)
            throw new CompileError(m.Pos, $".{m.Member} needs a 16-bit value, not {m.Target.Type}. Remove '.{m.Member}': a {m.Target.Type} is one byte already");
        m.Type = TernType.U8;
        if (m.Target.ConstValue is long mv)
            m.ConstValue = m.Member == "lo" ? mv & 0xFF : (mv >> 8) & 0xFF;
    }

    /// <summary><c>x as T</c> (spec 3.5).</summary>
    private void CheckCast(CastExpr c)
    {
        CheckExpr(c.Operand);
        var target = ResolveType(c.Target);
        var source = c.Operand.Type;
        if (!target.IsScalar) throw new CompileError(c.Pos, $"Cannot convert to {target}. Convert to a number, bool or pointer type");
        if (target.Kind == TypeKind.Bool)
            throw new CompileError(c.Pos, "Numbers cannot be converted to bool; compare instead, e.g. 'x != 0'");
        if (target.IsPointer || source.IsPointer)
        {
            // Pointers convert to other pointers and to and from u16, with no code.
            if (source.Kind == TypeKind.IntLiteral) Coerce(c.Operand, TernType.U16);
            else if (!(source.IsPointer || source.Kind == TypeKind.U16))
                throw new CompileError(c.Pos, $"Cannot convert {source} to {target}; only u16 and pointers convert to a pointer. Convert to u16 first");
            if (!(target.IsPointer || target.Kind == TypeKind.U16))
                throw new CompileError(c.Pos, $"Cannot convert {source} to {target}; a pointer converts only to u16 or another pointer. Convert to u16 first, then take .lo or .hi");
        }
        else if (source.Kind == TypeKind.IntLiteral && c.Operand.ConstValue is long big && (big < -32768 || big > 65535))
        {
            // 'as' never checks a value: it keeps the low bits, of a constant of any size too (spec 3.5).
            c.Operand.ConstValue = Normalize(big, target);
            c.Operand.Type = target;
        }
        else if (source.Kind == TypeKind.IntLiteral)
            Coerce(c.Operand, c.Operand.IsBitPattern && c.Operand.ConstValue < 0 ? target : LiteralType(c.Operand));
        else if (!source.IsScalar)
            throw new CompileError(c.Pos, $"Cannot convert {source} to {target}. Convert one element or field of it instead");
        c.Type = target;
        if (c.Operand.ConstValue is long cv) c.ConstValue = Normalize(cv, target);
    }

    private void CheckUnary(UnaryExpr u)
    {
        CheckExpr(u.Operand);
        var t = u.Operand.Type;
        long? v = u.Operand.ConstValue;
        switch (u.Op)
        {
            case TokenKind.Bang:
                if (t.Kind != TypeKind.Bool) throw new CompileError(u.Pos, "'!' needs a bool; use '~' to flip bits");
                u.Type = TernType.Bool;
                if (v is not null) u.ConstValue = v == 0 ? 1 : 0;
                return;
            case TokenKind.Minus or TokenKind.Plus:
                if (t.Kind == TypeKind.IntLiteral)
                {
                    u.Type = t;
                    u.ConstValue = u.Op == TokenKind.Minus ? ConstNegate(u.Pos, v!.Value) : v;
                    KeepConstType(u, u.Operand);
                    u.IsBitPattern = u.Op == TokenKind.Plus && u.Operand.IsBitPattern;
                    return;
                }
                if (!t.IsInteger) throw new CompileError(u.Pos, $"Unary '{(u.Op == TokenKind.Minus ? "-" : "+")}' needs a number. Use '!' to negate a bool");
                u.Type = t;
                if (v is not null) u.ConstValue = Normalize(u.Op == TokenKind.Minus ? -v.Value : v.Value, t);
                return;
            case TokenKind.Tilde:
                if (t.Kind == TypeKind.IntLiteral)
                {
                    // Exact, and negative: it takes the width of the type it is used as (x & ~$0F on a u8 is and #$F0).
                    u.Type = t;
                    u.ConstValue = ~v!.Value;
                    KeepConstType(u, u.Operand);
                    u.IsBitPattern = true;
                    return;
                }
                if (!t.IsInteger) throw new CompileError(u.Pos, "'~' needs a number; use '!' for bool");
                u.Type = t;
                if (v is not null) u.ConstValue = Normalize(~v.Value, t);
                return;
        }
        throw new InvalidOperationException();
    }

    private void CheckBinary(BinaryExpr b)
    {
        CheckExpr(b.Left);
        CheckExpr(b.Right);
        long? l = b.Left.ConstValue, r = b.Right.ConstValue;
        if ((b.Left.Type.IsStruct || b.Right.Type.IsStruct)
            && b.Op is not (TokenKind.EqEq or TokenKind.NotEq or TokenKind.Less or TokenKind.LessEq or TokenKind.Greater or TokenKind.GreaterEq))
            throw new CompileError(b.Pos, $"A struct is not a number; use one of its fields, e.g. s.x {OpText(b.Op)} 1");

        switch (b.Op)
        {
            case TokenKind.AmpAmp or TokenKind.PipePipe:
                if (b.Left.Type.Kind != TypeKind.Bool || b.Right.Type.Kind != TypeKind.Bool)
                    throw new CompileError(b.Pos, $"'{OpText(b.Op)}' needs bool values; use '{(b.Op == TokenKind.AmpAmp ? "&" : "|")}' for bits");
                b.Type = TernType.Bool;
                if (l is not null && r is not null)
                    b.ConstValue = (b.Op == TokenKind.AmpAmp ? l != 0 && r != 0 : l != 0 || r != 0) ? 1 : 0;
                return;

            case TokenKind.EqEq or TokenKind.NotEq or TokenKind.Less or TokenKind.LessEq or TokenKind.Greater or TokenKind.GreaterEq:
            {
                if (b.Left.Type.IsStruct || b.Right.Type.IsStruct)
                    throw new CompileError(b.Pos, "Structs cannot be compared; compare their fields, e.g. a.x == b.x");
                if (b.Left.Type.IsPointer || b.Right.Type.IsPointer)
                {
                    if (b.Left.Type.Kind == TypeKind.IntLiteral) Coerce(b.Left, b.Right.Type);
                    else if (b.Right.Type.Kind == TypeKind.IntLiteral) Coerce(b.Right, b.Left.Type);
                    else if (!b.Left.Type.SameAs(b.Right.Type))
                        throw new CompileError(b.Pos, $"Cannot compare {b.Left.Type} with {b.Right.Type}; convert one side with 'as'");
                }
                else if (b.Left.Type.Kind == TypeKind.Bool || b.Right.Type.Kind == TypeKind.Bool)
                {
                    if (b.Left.Type.Kind != b.Right.Type.Kind || b.Op is not (TokenKind.EqEq or TokenKind.NotEq))
                        throw new CompileError(b.Pos, "bool values can only be compared with == and !=. Use one of those");
                }
                else
                {
                    var t = Unify(b.Left, b.Right, b.Pos);
                    // Two constants are compared as plain numbers: sizeof(T) * 32 <= 256 must not wrap to u8.
                    if (t.Kind == TypeKind.IntLiteral && !(l is not null && r is not null)) { Coerce(b.Left, DefaultType(b.Left)); Coerce(b.Right, b.Left.Type); }
                }
                b.Type = TernType.Bool;
                if (l is not null && r is not null)
                    b.ConstValue = b.Op switch
                    {
                        TokenKind.EqEq => l == r, TokenKind.NotEq => l != r, TokenKind.Less => l < r,
                        TokenKind.LessEq => l <= r, TokenKind.Greater => l > r, _ => l >= r,
                    } ? 1 : 0;
                return;
            }

            case TokenKind.Shl or TokenKind.Shr:
            {
                var t = b.Left.Type;
                if (!(t.IsInteger || t.Kind == TypeKind.IntLiteral))
                    throw new CompileError(b.Pos, "Only numbers can be shifted. Convert the value to a number with 'as'");
                CheckShiftCount(b.Right, t);
                b.Type = t;
                if (l is not null)
                {
                    long shifted = t.Kind == TypeKind.IntLiteral
                        ? ConstArithmetic(b.Pos, b.Op, l.Value, r!.Value)
                        : b.Op == TokenKind.Shl ? l.Value << (int)r!.Value : l.Value >> (int)r!.Value;
                    b.ConstValue = t.Kind == TypeKind.IntLiteral ? shifted : Normalize(shifted, t);
                    if (t.Kind == TypeKind.IntLiteral) { KeepConstType(b, b.Left); b.IsBitPattern = b.Left.IsBitPattern; }
                }
                return;
            }

            case TokenKind.Star or TokenKind.Slash or TokenKind.Percent:
            {
                if (l is null || r is null)
                    throw new CompileError(b.Pos,
                        $"'{OpText(b.Op)}' only works on constants. Use a library function at run time (mul8, div8, ...), or shifts for powers of two");
                var t = Unify(b.Left, b.Right, b.Pos);
                if (b.Op != TokenKind.Star && r == 0) throw new CompileError(b.Pos, "Division by zero. Check the divisor");
                long value = ConstArithmetic(b.Pos, b.Op, l.Value, r.Value);
                b.Type = t;
                b.ConstValue = t.Kind == TypeKind.IntLiteral ? value : Normalize(value, t);
                if (t.Kind == TypeKind.IntLiteral) KeepConstType(b, b.Left, b.Right);
                return;
            }

            default: // + - & | ^
            {
                if (b.Left.Type.IsPointer || b.Right.Type.IsPointer)
                {
                    CheckPointerArithmetic(b);
                    return;
                }
                var t = Unify(b.Left, b.Right, b.Pos);
                if (!(t.IsInteger || t.Kind == TypeKind.IntLiteral))
                    throw new CompileError(b.Pos, $"'{OpText(b.Op)}' needs numbers. Use '&&', '||' and '!' for bool values");
                b.Type = t;
                if (l is not null && r is not null)
                {
                    long value = ConstArithmetic(b.Pos, b.Op, l.Value, r.Value);
                    b.ConstValue = t.Kind == TypeKind.IntLiteral ? value : Normalize(value, t);
                    if (t.Kind == TypeKind.IntLiteral)
                    {
                        KeepConstType(b, b.Left, b.Right);
                        b.IsBitPattern = b.Op is TokenKind.Amp or TokenKind.Pipe or TokenKind.Caret && (b.Left.IsBitPattern || b.Right.IsBitPattern);
                    }
                }
                return;
            }
        }
    }

    /// <summary>
    /// The type a constant expression takes from its typed constants (spec 3.1): the wider of the two
    /// operands' types. With signed and unsigned ones mixed it has none, and is a plain number.
    /// </summary>
    private static void KeepConstType(Expr result, Expr left, Expr? right = null)
    {
        var (a, b) = (left.ConstType, right?.ConstType);
        if (a is null || b is null || a.IsSigned == b.IsSigned)
        {
            bool fromLeft = b is null || (a is not null && a.Size >= b.Size);
            result.ConstType = fromLeft ? a : b;
            result.ConstTypeFrom = fromLeft ? left.ConstTypeFrom : right!.ConstTypeFrom;
        }
    }

    /// <summary>
    /// <c>p + n</c>, <c>n + p</c> and <c>p - n</c>: a pointer moved by a number of bytes.
    /// <c>p - q</c>: the distance between two pointers in bytes, a <c>u16</c>.
    /// </summary>
    private static void CheckPointerArithmetic(BinaryExpr b)
    {
        bool leftPtr = b.Left.Type.IsPointer;
        if (b.Op is not (TokenKind.Plus or TokenKind.Minus))
            throw new CompileError(b.Pos, $"'{OpText(b.Op)}' does not work on pointers. Convert to u16 first: (p as u16)");
        if (leftPtr && b.Right.Type.IsPointer)
        {
            if (b.Op == TokenKind.Plus)
                throw new CompileError(b.Pos, "Two pointers cannot be added. Convert them to u16 first: (p as u16)");
            if (!b.Left.Type.SameAs(b.Right.Type))
                throw new CompileError(b.Pos, $"Cannot subtract {b.Right.Type} from {b.Left.Type}; convert one side with 'as'");
            b.Type = TernType.U16;
            return;
        }
        if (!leftPtr && b.Op == TokenKind.Minus)
            throw new CompileError(b.Pos, "A pointer cannot be subtracted from a number. Convert the pointer with 'as u16'");
        // w + p would be a pointer while w + (p as u16) is a number: only a constant may come first (spec 3.5).
        if (!leftPtr && b.Left.ConstValue is null)
            throw new CompileError(b.Pos,
                $"A number before a pointer is not pointer arithmetic. Write '{Describe(b.Right) ?? "p"} + {Describe(b.Left) ?? "n"}' to move the pointer, or convert the pointer with 'as u16' for a number");
        CheckPointerOffset(leftPtr ? b.Right : b.Left);
        b.Type = leftPtr ? b.Left.Type : b.Right.Type;
    }

    /// <summary>The number of bytes a pointer moves by: an unsigned number.</summary>
    private static void CheckPointerOffset(Expr offset)
    {
        if (offset.Type.Kind == TypeKind.IntLiteral)
        {
            if (offset.ConstValue < 0)
                throw new CompileError(offset.Pos, "A pointer moves by an unsigned number; use '-' to move back");
            Coerce(offset, offset.ConstValue <= 255 ? TernType.U8 : TernType.U16);
            return;
        }
        if (!offset.Type.IsInteger || offset.Type.IsSigned)
            throw new CompileError(offset.Pos, $"A pointer moves by a u8 or u16, not {offset.Type}. Convert the offset with 'as u16'");
    }

    private void CheckShiftCount(Expr count, TernType shifted)
    {
        if (count.ConstValue is not long n)
            throw new CompileError(count.Pos, "The shift count must be a constant. Use shl8/shr8 for a variable count");
        int bits = shifted.Kind == TypeKind.IntLiteral ? 64 : shifted.Size * 8;
        if (n < 0 || n >= bits)
            throw new CompileError(count.Pos, $"Shift count must be 0 to {bits - 1}. Use a count in that range");
        if (count.Type.Kind == TypeKind.IntLiteral) Coerce(count, TernType.U8);
    }

    /// <summary>
    /// <c>pts[i]</c> on a linear array of 16-bit values: the index counts elements, so it is doubled.
    /// A variable index is <c>i &lt;&lt; 1</c> (one <c>asl</c>), a constant one a fixed offset.
    /// </summary>
    private static IndexExpr LinearIndex(IndexExpr ix)
    {
        var u8 = TernType.U8;
        Expr index = ix.Index.ConstValue is long c
            ? new NumberExpr(ix.Index.Pos, 2 * c) { Type = u8, ConstValue = 2 * c }
            : new BinaryExpr(ix.Index.Pos, TokenKind.Shl, ix.Index, new NumberExpr(ix.Index.Pos, 1) { Type = u8, ConstValue = 1 })
            {
                Type = u8,
                ScalesIndexOf = ix.Target is NameExpr { Symbol: VariableSymbol v } ? (v.QualifiedName, v.Type.Length) : null,
            };
        return new IndexExpr(ix.Pos, ix.Target, index) { Type = ix.Type };
    }

    /// <summary>Makes <c>addr(...)</c> a constant address: the variable's, or a view's (its storage plus an offset).</summary>
    private static void TakeAddress(CallExpr c, VariableSymbol v)
    {
        v.AddressTaken = true;
        if (v.AliasOf is { } storage) storage.AddressTaken = true;
        c.AddressOf = v;
        // An array gives a pointer to its first element. A split array of 16-bit
        // values gives a pointer to its table of low bytes; a linear one, a pointer to its first value.
        c.Type = !v.Type.IsArray ? TernType.PointerTo(v.Type)
            : v.Type.Element!.IsStruct ? TernType.PointerTo(v.Type.Element)
            : v.Type.Element!.Size == 1 || v.Type.Linear ? TernType.PointerTo(v.Type.Element!)
            : TernType.PointerTo(TernType.U8);
    }

    /// <summary><c>addr(f)</c>: a <c>u16</c> known when the program is placed (spec 6.5).</summary>
    /// <summary>An overlay may not take the address of a function of another overlay in its area (spec 8.5).</summary>
    private void CheckSiblingAddress(FunctionSymbol f, SourcePos pos)
    {
        if (_body.Function is not null && NotPresentWith(f, _body.Function.OverlayArea, _body.Function.OverlayUnit))
            throw new CompileError(pos,
                $"addr({f.Name}) is in {f.Module!.Name}, which is {NeverTogether(f.Module, _body.Function.Module!)} as {_body.Function.Module!.Name}. They are never loaded together, so the address would point at the wrong code. " +
                "Take the address in resident code, or put one of them in another area");
    }

    private void TakeFunctionAddress(CallExpr c, FunctionSymbol f, SourcePos pos)
    {
        if (f.IsInline)
            throw new CompileError(pos, $"'{f.Name}' is inline: it is copied into each caller and has no address of its own. Remove 'inline'");
        if (f.Parameters.FirstOrDefault(p => p.PassedIn is null) is { } memoryParam)
            throw new CompileError(pos,
                $"'{f.Name}' takes '{memoryParam.Name}' in memory, which a call through an address cannot set. " +
                $"Pass it in a register ({memoryParam.Name}: {memoryParam.Type} using A)");
        CheckSiblingAddress(f, pos);
        if (!_body.Dead) f.AddressTaken = true;
        c.FunctionAddress = f;
        c.Type = TernType.U16;
        if (!_body.Dead) _body.Function?.UsedAddresses.Add(f);
    }

    /// <summary><c>addr(player.name)</c>: a field of a struct variable or of a struct at a fixed address.</summary>
    private void CheckAddrOfField(CallExpr c, MemberExpr field)
    {
        CheckExpr(field);
        string name = DescribePlace(field);
        var place = field.Place ?? Unwrap(field) switch
        {
            NameExpr { Symbol: { AliasOf: not null } } => null,
            IndexExpr { Target: NameExpr { Symbol.AliasOf.Type.Linear: true } } => new StructPlace(PlaceKind.Records, 0),
            IndexExpr { Target.Type.IsArray: true } => new StructPlace(PlaceKind.Table, 0),
            _ => new StructPlace(PlaceKind.Pointer, 0),
        };
        switch (place?.Kind)
        {
            case PlaceKind.Pointer:
                throw new CompileError(field.Pos,
                    $"'{name}' is reached through a pointer, so it has no fixed address. addr() works on fields of struct variables. Add the field's offset to the pointer: p + offset(T.field)");
            case PlaceKind.Table:
                throw new CompileError(field.Pos,
                    $"'{name}' is in an array of structs, which is stored as one table per field, so the record has no address. addr() works on fields of struct variables. Declare the array 'linear' to keep each record in one piece");
            case PlaceKind.Records:
                throw new CompileError(field.Pos,
                    $"'{name}' has a variable index, so its address is not known at compile time. Use a constant index, or add the record's offset to the array's address");
        }
        var view = place is { Kind: PlaceKind.Memory } at
            ? View(at.Base!, at.Offset, field.Type, name)
            : ((NameExpr)Unwrap(field)).Symbol!;
        TakeAddress(c, (VariableSymbol)view);
    }

    private void CheckCall(CallExpr c)
    {
        switch (c.Name)
        {
            case "len":
            {
                if (c.Args.Count != 1) throw new CompileError(c.Pos, "len() takes one array. Write len(name)");
                if (IsCompileTimeArray(c.Args[0]))
                {
                    c.Type = TernType.IntLiteral;
                    c.ConstValue = CompileTimeArray(c.Args[0]).Items.Length;
                    return;
                }
                // Tile data (NES): its length, though the CPU cannot read it.
                if (c.Args[0] is NameExpr { Name: var tiles } && Lookup(tiles) is VariableSymbol { InChr: true } chrData)
                {
                    c.Type = TernType.IntLiteral;
                    c.ConstValue = chrData.Type.IsArray ? chrData.Type.Length : throw new CompileError(c.Pos, "len() needs an array. Use sizeof() for the size of a type");
                    return;
                }
                CheckExpr(c.Args[0]);
                if (!c.Args[0].Type.IsArray) throw new CompileError(c.Pos, "len() needs an array. Use sizeof() for the size of a type");
                c.Type = TernType.IntLiteral;
                c.ConstValue = c.Args[0].Type.Length;
                return;
            }
            case "include_bytes":
                throw new CompileError(c.Pos, "include_bytes() can only be the value of data or a global array. Declare data with it: data name = include_bytes(\"file.bin\")");
            case "addr":
            {
                if (c.Args.Count == 1 && c.Args[0] is MemberExpr { Member: not ("lo" or "hi") } field)
                {
                    CheckAddrOfField(c, field);
                    return;
                }
                if (c.Args.Count != 1 || c.Args[0] is not NameExpr name)
                    throw new CompileError(c.Pos, "addr() takes the name of a variable, array or data, or a field of a struct variable. Write addr(name)");
                switch (Lookup(name.Name))
                {
                    // Constants are declared before the file's functions.
                    case null when _module.Syntax.Functions.Any(f => f.Name == name.Name):
                        throw new CompileError(name.Pos,
                            $"addr({name.Name}) is known only when the program is placed, so it cannot be part of a constant. Use it in a var, data or code");
                    case null: throw NotDeclared(name.Name, name.Pos);
                    case ConstSymbol: throw new CompileError(name.Pos, $"'{name.Name}' is a constant, which has no address. Use data for a value in memory");
                    case FunctionSymbol f:
                        TakeFunctionAddress(c, f, name.Pos);
                        return;
                    case VariableSymbol { InChr: true } tiles:
                        // Its PPU address, a u16: not a pointer, since the CPU cannot read there (spec 12.2).
                        name.Symbol = tiles;
                        name.Type = tiles.Type;
                        if (!_body.Dead) _body.Function?.UsedGlobals.Add(tiles);
                        c.AddressOf = tiles;
                        c.Type = TernType.U16;
                        return;
                    case VariableSymbol v:
                        if (!v.InMemory) throw new CompileError(name.Pos, $"'{name.Name}' lives in a register and has no address. Remove 'using' to keep it in memory");
                        name.Symbol = v;
                        name.Type = v.Type;
                        UseGlobal(v, name.Pos);
                        if (v.Type.IsStructArray && !v.Type.Linear)
                            throw new CompileError(name.Pos,
                                $"'{name.Name}' is stored as one table per field, so there is no record to point to. Declare it 'linear var' to store whole records");
                        TakeAddress(c, v);
                        return;
                }
                throw new InvalidOperationException();
            }
            case "offset":
                c.Type = TernType.IntLiteral;
                c.ConstValue = OffsetOf(c);
                return;
            case "sizeof":
            {
                if (c.Args.Count != 1 || c.Args[0] is not NameExpr typeName)
                    throw new CompileError(c.Pos, "sizeof() takes one type name. Write e.g. sizeof(Enemy)");
                c.Type = TernType.IntLiteral;
                c.ConstValue = ResolveType(new NamedTypeSyntax(typeName.Pos, typeName.Name)).Size;
                return;
            }
        }

        var symbol = Lookup(c.Name);
        if (symbol is ConstFnSymbol || (symbol is null && CompileTime.Interpreter.Builtins.Contains(c.Name)))
        {
            EvaluateConstant(c);
            return;
        }
        if (symbol is null && c.Name.Contains('.')) throw NotDeclared(c.Name, c.Pos);
        if (symbol is not FunctionSymbol fn)
            throw new CompileError(c.Pos, symbol is null ? $"'{c.Name}' is not a function. Check the spelling, or declare it" : $"'{c.Name}' is not a function. Remove the ()");
        if (fn.IsInterrupt)
        {
            if (_body.Function is not { IsInterrupt: true })
                throw new CompileError(c.Pos,
                    $"'{c.Name}' is an interrupt fn, which only the CPU or another interrupt fn calls. Install its address in the interrupt vector, or call it from an interrupt fn");
        }
        if (c.Args.Count != fn.Parameters.Count)
            throw new CompileError(c.Pos, $"'{fn.Name}' takes {fn.Parameters.Count} argument(s), not {c.Args.Count}. Pass {fn.Parameters.Count}: {fn.Name}({Signature.Parameters(fn)})");
        for (int i = 0; i < c.Args.Count; i++)
        {
            CheckExpr(c.Args[i]);
            Coerce(c.Args[i], fn.Parameters[i].Type);
        }
        foreach (var p in fn.Parameters)
            foreach (var held in _body.LoopRegisters)
                if (p.PassedIn is { } pr && pr.Uses(held))
                    throw new CompileError(c.Pos, $"'{fn.Name}' takes '{p.Name}' in {held}, which holds the loop variable. Remove 'using {held}' from the loop");
        foreach (var (reg, _) in fn.FixedInputs)
            if (_body.LoopRegisters.Contains(reg))
                throw new CompileError(c.Pos, $"'{fn.Name}' takes a fixed input in {reg}, which holds the loop variable. Remove 'using {reg}' from the loop");
        c.Function = fn;
        c.Type = fn.ReturnType;
        if (_body.Function is not null) AddCall(_body.Function, fn, c.Pos);
    }

    /// <summary>Calls from an overlay's function to an inline function of another module, for <see cref="CheckInlineCallsBetweenOverlays"/>.</summary>
    private readonly List<(FunctionSymbol Caller, FunctionSymbol Callee, SourcePos Pos)> _inlineCalls = [];

    /// <summary>
    /// An inline function is copied into its caller, so what its body uses must be there when the caller
    /// runs: an inline function of another overlay in the same area may not use that overlay's variables
    /// or call its functions (spec 8.5).
    /// </summary>
    private void CheckInlineCallsBetweenOverlays()
    {
        foreach (var (caller, callee, pos) in _inlineCalls)
        {
            var area = caller.OverlayArea;
            if (callee.OverlayArea != area) continue;
            var seen = new HashSet<FunctionSymbol>();
            void Visit(FunctionSymbol inline)
            {
                if (!seen.Add(inline)) return;
                foreach (var g in inline.UsedGlobals.Where(g => NotPresentWith(g, area, caller.OverlayUnit)))
                    throw new CompileError(pos,
                        $"'{caller.Name}' calls the inline function '{callee.Name}', which is copied into it and uses '{g.Name}', {(g.IsData ? "data" : "a variable")} of {g.Module!.Name}. " +
                        $"{caller.Module!.Name} and {g.Module.Name} are {NeverTogether(caller.Module, g.Module)}, never loaded together, and share their memory. Keep shared state in a resident module or the main file");
                foreach (var f in inline.UsedAddresses.Where(f => NotPresentWith(f, area, caller.OverlayUnit)))
                    throw new CompileError(pos,
                        $"'{caller.Name}' calls the inline function '{callee.Name}', which is copied into it and takes addr({f.Name}). " +
                        $"{caller.Module!.Name} and {f.Module!.Name} are {NeverTogether(caller.Module, f.Module)}, never loaded together, so the address would point at the wrong code. Take the address in resident code");
                foreach (var next in inline.Callees)
                    if (next.IsInline) Visit(next);
                    else if (NotPresentWith(next, area, caller.OverlayUnit))
                        throw new CompileError(pos,
                            $"'{caller.Name}' calls the inline function '{callee.Name}', which is copied into it and calls '{next.Name}'. " +
                            $"{caller.Module!.Name} and {next.Module!.Name} are {NeverTogether(caller.Module, next.Module!)}, so they are never loaded together. Call between them from resident code");
            }
            Visit(callee);
        }
    }

    /// <summary>
    /// Records a call. Two overlays in the same area are never loaded together, so one cannot call
    /// the other (spec 8.5).
    /// </summary>
    private void AddCall(FunctionSymbol caller, FunctionSymbol callee, SourcePos pos)
    {
        if (!_body.Dead) caller.Callees.Add(callee);
        if (!_body.Dead && callee.IsInline && caller.OverlayArea is not null && callee.OverlayUnit != caller.OverlayUnit) _inlineCalls.Add((caller, callee, pos));
        if (NotPresentWith(callee, caller.OverlayArea, caller.OverlayUnit) && !callee.IsInline)
            throw new CompileError(pos, caller.Module!.InBank
                ? $"'{caller.Name}' calls '{callee.Name}', but the modules {caller.Module.Name} and {callee.Module!.Name} are {NeverTogether(caller.Module, callee.Module)}, and only one bank is selected at a time. " +
                  "Put both modules in the same bank, or call between them from resident code"
                : $"'{caller.Name}' calls '{callee.Name}', but the modules {caller.Module.Name} and {callee.Module!.Name} are both in the overlay area '{caller.OverlayArea}', so they are never loaded together. " +
                  "Call between them from resident code, or put one of them in another area");
    }
}
