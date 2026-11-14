using Tern.Compiler.Syntax;

namespace Tern.Compiler.Semantics;

/// <summary>Bank switching (spec 8.5, 12.2): the bank numbers of imports, and the board's own library module.</summary>
public sealed partial class Checker
{
    /// <summary>The number in <c>import m @ area[n]</c>: a constant, one of the board's switchable banks.</summary>
    private int BankNumber(ImportDecl import)
    {
        var board = _layout.Cartridge!.Board;
        if (import.Bank is not { } n)
            throw new CompileError(import.AreaPos,
                $"'{import.Area}' is a bank area, so the import names the bank the module goes in. Write '@ {import.Area}[0]', or another bank's number");
        CheckExpr(n);
        if (n.ConstValue is not long bank)
            throw new CompileError(n.Pos, "The bank's number must be known at compile time. Use a number or a constant");
        int last = board.MaxBanks - 1;
        if (bank == last)
            throw new CompileError(n.Pos,
                $"Bank {bank} is the fixed bank when the image has {board.MaxBanks} banks, and it holds the resident program at {board.CodeAreas[0]}. Use a bank from 0 to {last - 1}");
        if (bank < 0 || bank > last)
            throw new CompileError(n.Pos, $"The board '{board.Name}' has at most {board.MaxBanks} banks, the last one fixed, so a module goes in bank 0 to {last - 1}, not {bank}. Use a number in that range");
        return (int)bank;
    }

    /// <summary>A board's library module (nes/uxrom) selects banks on that board only, so importing another board's is an error.</summary>
    private void CheckBoardModules(List<Module> modules)
    {
        var board = _layout.Cartridge?.Board;
        foreach (var other in _platform.Boards.Where(b => b.BankModule is not null && b != board))
        {
            string name = other.BankModule![(other.BankModule.LastIndexOf('/') + 1)..];
            foreach (var importer in modules)
                foreach (var import in importer.Syntax.Imports.Where(i => i.Name == name && importer.Imports.GetValueOrDefault(name)?.IsLibrary == true))
                    Recover(() => throw new CompileError(import.Pos,
                        $"'{name}' selects banks on the board '{other.Name}', but the memory block names " + (board is null ? "no board" : $"'{board.Name}'") +
                        $". Remove the import, or write 'cartridge {other.Name} ...' in the memory block"));
        }
    }

    /// <summary>Why two placed modules of one area are never there together: overlays share the area, banks are switched.</summary>
    private static string NeverTogether(Module a, Module b) =>
        a.InBank ? $"in banks {a.Bank} and {b.Bank} of the bank area '{a.OverlayArea}'" : $"in the same overlay area '{a.OverlayArea}'";
}
