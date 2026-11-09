using Tern.Compiler.Syntax;

namespace Tern.Compiler.Semantics;

/// <summary>
/// Structs: declarations and layout, and field accesses. A field access is lowered to plain
/// variables, arrays and pointers (<see cref="Expr.Lowered"/>), so the code generator needs no
/// struct support:
/// <list type="bullet">
/// <item>a field of a struct variable is a view of the variable's memory: <c>player.hp</c> is <c>player+2</c>;</item>
/// <item>a field of an array of structs is a view of that field's table, indexed: <c>enemies[i].x</c>;</item>
/// <item>a field through a pointer is <c>p[0]</c> with the field's offset added to Y.</item>
/// </list>
/// </summary>
public sealed partial class Checker
{
    private readonly Dictionary<(VariableSymbol, int, string, int?), VariableSymbol> _views = new();

    private void DeclareStruct(StructDecl decl)
    {
        if (decl.Name is "u8" or "i8" or "u16" or "i16" or "bool" or "ptr")
            throw new CompileError(decl.Pos, $"'{decl.Name}' is a built-in type; choose another name");
        Declare(new StructSymbol(_module.Qualify(decl.Name), decl.Pos, decl), decl.Name);
    }

    /// <summary>Works out the field offsets and the size, once. Nested structs are laid out first.</summary>
    private void LayOut(StructSymbol s)
    {
        if (s.LayoutState == 2) return;
        if (s.LayoutState == 1)
            throw new CompileError(s.Pos, $"Struct '{s.Name}' contains itself, so its size would be endless. Use a pointer: ptr {s.Name}");
        s.LayoutState = 1;
        var decl = s.Declaration;
        if (decl.Fields.Count == 0) throw new CompileError(decl.Pos, $"Struct '{s.Name}' has no fields");

        int next = 0;
        foreach (var f in decl.Fields)
        {
            if (f.Name != "_" && s.Field(f.Name) is { } twice)
                throw new CompileError(f.Pos, $"Field '{f.Name}' is declared twice in '{s.Name}' (first at {twice.Pos})");
            var type = ResolveType(f.Type);
            if (type.IsArray && type.Element!.IsStruct)
                throw new CompileError(f.Pos, "A field cannot be an array of structs yet. Use an array of structs outside the struct, or a pointer");
            int offset = next;
            if (f.Offset is not null)
            {
                CheckExpr(f.Offset);
                if (f.Offset.ConstValue is not long o)
                    throw new CompileError(f.Offset.Pos, "A field offset must be known at compile time");
                if (o < 0 || o > 0xFFFF) throw new CompileError(f.Offset.Pos, $"Field offset {o} is outside memory");
                offset = (int)o;
            }
            foreach (var other in s.Fields)
                if (offset < other.Offset + other.Type.Size && other.Offset < offset + type.Size)
                    throw new CompileError(f.Pos,
                        $"Field '{f.Name}' (offset {offset}, {type.Size} byte(s)) overlaps field '{other.Name}' (offset {other.Offset}, {other.Type.Size} byte(s))");
            s.Fields.Add(new StructField(f.Name, f.Pos, type, offset));
            next = offset + type.Size;
            s.Size = Math.Max(s.Size, next);
        }
        if (s.Size > 0xFFFF) throw new CompileError(decl.Pos, $"Struct '{s.Name}' is {s.Size} bytes, more than memory");
        s.LayoutState = 2;
    }

    /// <summary>
    /// A view: a variable for part of <paramref name="storage"/>'s memory, starting at
    /// <paramref name="offset"/>. The same part always gives the same view, so the code generator
    /// can see that two accesses are the same.
    /// </summary>
    private VariableSymbol View(VariableSymbol storage, int offset, TernType type, string name, int? highTable = null)
    {
        var key = (storage, offset, type.ToString(), highTable);
        if (_views.TryGetValue(key, out var view)) return view;
        view = new VariableSymbol(name, storage.Pos, type, storage.Kind, storage.Owner)
        {
            AliasOf = storage,
            AliasOffset = offset,
            HighTable = highTable,
            IsData = storage.IsData,
            FixedAddress = storage.FixedAddress + offset,
        };
        _views[key] = view;
        return view;
    }

    private static NameExpr ViewName(SourcePos pos, VariableSymbol view) => new(pos, view.Name) { Symbol = view, Type = view.Type };

    /// <summary><c>x.field</c> where <c>x</c> is a struct or a pointer to one.</summary>
    private void CheckField(MemberExpr m)
    {
        var target = Unwrap(m.Target);
        var targetType = m.Target.Type;
        StructPlace place;
        StructSymbol s;
        if (targetType.IsPointer && targetType.Element!.IsStruct)
        {
            s = targetType.Element.Struct!;
            if (target is NameExpr { Symbol: { AliasOf: null } pv }) pv.Dereferenced = true;
            place = new StructPlace(PlaceKind.Pointer, 0, Pointer: target);
        }
        else
        {
            s = targetType.Struct!;
            place = m.Target.Place!;
        }

        if (m.Member == "_")
            throw new CompileError(m.Pos, $"'_' is padding in struct '{s.Name}': it takes space but has no name, so it cannot be read or written");
        var field = s.Field(m.Member)
            ?? throw new CompileError(m.Pos, $"Struct '{s.Name}' has no field '{m.Member}'. Its fields: {string.Join(", ", s.Fields.Where(f => f.Name != "_").Select(f => f.Name))}");
        var type = field.Type;
        m.Type = type;
        var at = place with { Offset = place.Offset + field.Offset };
        string name = $"{DescribePlace(m.Target)}.{field.Name}";

        if (type.IsStruct || type.IsArray)
        {
            m.Place = at;
            // A struct or array field of a struct variable is memory too; arrays are then indexed as usual.
            if (at.Kind == PlaceKind.Memory && type.IsArray)
                m.Lowered = ViewName(m.Pos, View(at.Base!, at.Offset, type, name));
            return;
        }
        m.Lowered = LowerScalar(m.Pos, at, type, name, secondByte: 1);
    }

    /// <summary>A one- or two-byte value at a place: a view, an element of a field table, or a pointer access.</summary>
    private Expr LowerScalar(SourcePos pos, StructPlace at, TernType type, string name, int secondByte)
    {
        switch (at.Kind)
        {
            case PlaceKind.Memory:
            {
                var view = View(at.Base!, at.Offset, type, name);
                return ViewName(pos, view);
            }
            case PlaceKind.Table:
            {
                // Byte k of every record is one table: [type; n] at k * n, its high bytes at (k + secondByte) * n.
                int n = at.Base!.Type.Length;
                var table = View(at.Base, at.Offset * n, TernType.ArrayOf(type, n), name, highTable: secondByte * n);
                return new IndexExpr(pos, ViewName(pos, table), at.Index!) { Type = type };
            }
            case PlaceKind.Records:
            {
                // Y = the record's offset: byte k of the record is at Base + k + Y, in a table of 256 at most.
                var records = View(at.Base!, at.Offset, TernType.ArrayOf(type, at.Base!.Type.Size - at.Offset), name, highTable: secondByte);
                return new IndexExpr(pos, ViewName(pos, records), at.Index!) { Type = type };
            }
            default:
            {
                int last = at.Offset + (type.Size == 2 ? secondByte : 0);
                if (last > 255)
                    throw new CompileError(pos, $"'{name}' is at offset {last} of the struct; through a pointer, (p),y reaches offsets 0 to 255");
                var zero = new NumberExpr(pos, 0) { Type = TernType.U8, ConstValue = 0 };
                return new IndexExpr(pos, at.Pointer!, zero) { Type = type, Offset = at.Offset, SecondByte = secondByte };
            }
        }
    }

    /// <summary>
    /// <c>x.name[i]</c> for an array field of a struct in an array or behind a pointer; false for
    /// other indexing (an array field of a struct variable is a view, indexed as any array).
    /// </summary>
    private bool CheckArrayFieldIndex(IndexExpr ix)
    {
        if (ix.Target.Place is not { Kind: PlaceKind.Table or PlaceKind.Pointer or PlaceKind.Records } at || !ix.Target.Type.IsArray) return false;
        var array = ix.Target.Type;
        var element = array.Element!;
        string name = DescribePlace(ix.Target);
        ix.Type = element;
        // Element j of a split array field: the low byte at j, the high byte at length + j.
        if (at.Kind is PlaceKind.Table or PlaceKind.Records)
        {
            if (ix.Index.ConstValue is not long j)
                throw new CompileError(ix.Index.Pos, at.Kind == PlaceKind.Table
                    ? "An array field in an array of structs needs a constant index, since a variable one would need a multiply. Declare the array 'linear' and step through it with a pointer"
                    : "An array field in an array of structs needs a constant index, since the record's offset is already in the index register. Step through the array with a pointer");
            if (j >= array.Length)
                throw new CompileError(ix.Index.Pos, $"Index {j} is past the end of '{name}' (length {array.Length})");
            ix.Lowered = LowerScalar(ix.Pos, at with { Offset = at.Offset + (int)j }, element, $"{name}[{j}]", secondByte: array.Length);
            return true;
        }

        // Through a pointer: Y = field offset + i, the high bytes length further on.
        int last = at.Offset + (array.Length - 1) + (element.Size == 2 ? array.Length : 0);
        if (ix.Index.ConstValue is long ci)
        {
            if (ci >= array.Length)
                throw new CompileError(ix.Index.Pos, $"Index {ci} is past the end of '{name}' (length {array.Length})");
            last = at.Offset + (int)ci + (element.Size == 2 ? array.Length : 0);
        }
        CheckLoopIndex(ix.Index, name, array.Length);
        if (last > 255)
            throw new CompileError(ix.Pos, $"'{name}' reaches offset {last} of the struct; through a pointer, (p),y reaches offsets 0 to 255");
        ix.Lowered = new IndexExpr(ix.Pos, at.Pointer!, ix.Index)
        {
            Type = element,
            Offset = at.Offset,
            Stride = 1,
            SecondByte = array.Length,
        };
        return true;
    }

    /// <summary><c>enemies[i]</c>: a record of an array of structs.</summary>
    private void CheckRecordIndex(IndexExpr ix, VariableSymbol arr)
    {
        if (ix.Index.ConstValue is long idx && idx >= arr.Type.Length)
            throw new CompileError(ix.Index.Pos, $"Index {idx} is past the end of '{arr.Name}' (length {arr.Type.Length})");
        CheckLoopIndex(ix.Index, arr.Name, arr.Type.Length);
        var element = arr.Type.Element!;
        ix.Type = element;
        if (!arr.Type.Linear)
        {
            ix.Place = new StructPlace(PlaceKind.Table, 0, Base: arr, Index: ix.Index);
            return;
        }
        // Whole records one after another: a constant index is a fixed address.
        if (ix.Index.ConstValue is long c)
        {
            ix.Place = new StructPlace(PlaceKind.Memory, (int)c * element.Size, Base: arr);
            return;
        }
        int size = element.Size;
        if (arr.Type.Size > 256)
            throw new CompileError(ix.Pos,
                $"'{arr.Name}' is {arr.Type.Size} bytes, more than the 256 an 8-bit offset reaches, so it cannot be indexed with a variable. Step through it with a pointer: p = p + sizeof({element})");
        var u8 = TernType.U8;
        Expr offset;
        if (arr.IsIndexed)
        {
            var table = OffsetTable(arr);
            UseGlobal(table, ix.Pos);
            offset = new IndexExpr(ix.Pos, ViewName(ix.Pos, table), ix.Index) { Type = u8 };
        }
        else if (size is 1 or 2 or 4 or 8)
        {
            int shift = size switch { 1 => 0, 2 => 1, 4 => 2, _ => 3 };
            offset = shift == 0
                ? ix.Index
                : new BinaryExpr(ix.Index.Pos, TokenKind.Shl, ix.Index, new NumberExpr(ix.Index.Pos, shift) { Type = u8, ConstValue = shift })
                {
                    Type = u8,
                    ScalesIndexOf = (arr.QualifiedName, arr.Type.Length),
                };
        }
        else
        {
            int next = size < 4 ? 4 : size < 8 ? 8 : 16;
            throw new CompileError(ix.Pos,
                $"'{arr.Name}' has records of {size} bytes, and a variable index into a linear array needs 1, 2, 4 or 8. " +
                $"Pad {element} with '_' fields to {next} bytes (e.g. _: [u8; {next - size}]), or declare the array 'indexed var {arr.Name}' to use a table of {arr.Type.Length} bytes");
        }
        ix.Place = new StructPlace(PlaceKind.Records, 0, Base: arr, Index: offset);
    }

    /// <summary>The read-only table of <c>i * sizeof(T)</c> for an indexed array, made at its first variable index.</summary>
    private VariableSymbol OffsetTable(VariableSymbol arr)
    {
        if (arr.OffsetTable is { } made) return made;
        int size = arr.Type.Element!.Size, n = arr.Type.Length;
        var bytes = new byte[n];
        for (int i = 0; i < n; i++) bytes[i] = (byte)(i * size);
        var table = new VariableSymbol($"{arr.QualifiedName}.offsets", arr.Pos, TernType.ArrayOf(TernType.U8, n), VariableKind.Global, null)
        {
            IsData = true, IsOffsetTable = true, InitialBytes = bytes, Module = arr.Module ?? _module,
        };
        _globals.Add(table);
        return arr.OffsetTable = table;
    }

    /// <summary>A short name for messages and listings: <c>player.pos</c>, <c>enemies.x</c>.</summary>
    private static string DescribePlace(Expr e) => e switch
    {
        NameExpr n => n.Name,
        MemberExpr m => $"{DescribePlace(m.Target)}.{m.Member}",
        IndexExpr ix when ix.Place?.Kind is PlaceKind.Table or PlaceKind.Records => DescribePlace(ix.Target),
        IndexExpr ix => $"{DescribePlace(ix.Target)}[]",
        CastExpr c => DescribePlace(c.Operand),
        _ => "p",
    };

    /// <summary>The expression a struct field access stands for.</summary>
    private static Expr Unwrap(Expr e) => e.Lowered ?? e;
}
