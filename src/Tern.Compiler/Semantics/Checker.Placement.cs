using Tern.Compiler.Syntax;

namespace Tern.Compiler.Semantics;

/// <summary>Placement with '@' (spec 4): zero page, an alignment, an address, a region or chr area, with an offset.</summary>
public sealed partial class Checker
{
    private void ApplyPlacement(VarDecl decl, VariableSymbol v, bool allowAddress)
    {
        switch (decl.Placement.Kind)
        {
            case PlacementKind.ZeroPage:
                v.RequireZeroPage = true;
                break;
            case PlacementKind.Align:
                if (!allowAddress)
                    throw new CompileError(decl.Pos, "Only global variables and data can be aligned: a local's memory is shared with other functions' (spec 8.1). Move the variable out of the function");
                var n = decl.Placement.Alignment!;
                CheckExpr(n);
                if (n.ConstValue is not long align)
                    throw new CompileError(n.Pos, "The alignment must be known at compile time. Use a number or a constant, as in '@ align(256)'");
                if (align < 2 || align > 32768 || (align & (align - 1)) != 0)
                    throw new CompileError(n.Pos, $"The alignment must be a power of two from 2 to 32768, not {align}. Use one such as 64, 256 or 2048");
                v.Alignment = (int)align;
                break;
            case PlacementKind.Address:
                if (!allowAddress) throw new CompileError(decl.Pos, "Only global variables can have a fixed address. Move the variable out of the function");
                var addr = decl.Placement.Address!;
                // '@ name' or '@ name + offset' for a region or a chr area (spec 4); 'name' is never a constant (8.4).
                var (areaName, offsetExpr) = addr switch
                {
                    NameExpr named => (named.Name, (Expr?)null),
                    BinaryExpr { Op: TokenKind.Plus, Left: NameExpr named } b => (named.Name, b.Right),
                    _ => ((string?)null, (Expr?)null),
                };
                // Only an area name needs the memory block; a constant address does not. The block is the
                // main file's, also for a declaration in a module (spec 8.4, decision 2026-11-15).
                if (areaName is not null && _mainModule.Syntax.Memory?.Lines.Any(l => l.Kind is MemoryKind.Region or MemoryKind.Chr && l.Name == areaName) == true)
                {
                    InModule(_mainModule, EnsureMemoryLayout);
                    if (_memoryFailed) throw new FollowOnError();
                    // In the main file a symbol with a region's name is reported at the region (Run).
                    if (!_module.IsMain && Lookup(areaName) is { } clash)
                        throw new CompileError(addr.Pos,
                            $"'{areaName}' is a {(_layout.Chr.ContainsKey(areaName) ? "chr area" : "region")} of the main file and a {KindName(clash)} of this module (line {clash.Pos.Line}), so '@ {areaName}' would be ambiguous. Rename the {KindName(clash)}");
                }
                if (areaName is not null && Lookup(areaName) is null && (_layout.Regions.TryGetValue(areaName, out var span) | _layout.Chr.TryGetValue(areaName, out var chrSpan)))
                {
                    v.Region = areaName;
                    v.InChr = _layout.Chr.ContainsKey(areaName);
                    var area = v.InChr ? chrSpan : span;
                    if (v.InChr && !decl.IsData)
                        throw new CompileError(decl.Pos,
                            $"'{decl.Name}' is a variable, but the chr area '{areaName}' is ROM the PPU reads, so it holds only tile data. Declare it with 'data'");
                    if (offsetExpr is not null)
                    {
                        CheckExpr(offsetExpr);
                        if (offsetExpr.ConstValue is not long offset)
                            throw new CompileError(offsetExpr.Pos, "The offset must be known at compile time. Use a number or a constant");
                        if (offset < 0 || offset + v.Type.Size > area.Size)
                            throw new CompileError(offsetExpr.Pos,
                                $"'{decl.Name}' ({v.Type.Size} bytes) at offset ${offset:X} does not fit in '{areaName}' ({area}, {area.Size} bytes). Use an offset up to ${Math.Max(0, area.Size - v.Type.Size):X}");
                        v.RegionOffset = (int)offset;
                    }
                    // A region that touches volatile memory is volatile as a whole: its addresses are not known yet.
                    v.InVolatileArea = !v.InChr && (_layout.IsVolatile(area.First, area.Last) || _platform.IsVolatile(area.First, area.Last));
                    break;
                }
                CheckExpr(addr);
                if (addr.ConstValue is not long a)
                    throw new CompileError(addr.Pos, "The address must be known at compile time. Use a number or a constant");
                if (a < 0 || a + v.Type.Size > 0x10000)
                    throw new CompileError(addr.Pos, $"Address ${a:X} is outside memory. Use an address up to $FFFF, less the variable's size");
                v.FixedAddress = (int)a;
                v.InVolatileArea = _platform.IsVolatile((int)a, (int)a + v.Type.Size - 1);
                break;
        }
    }
}
