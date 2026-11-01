using System.Text;
using Tern.Compiler.Syntax;
using Tern.Cpu;
using Encoder = Tern.Cpu.Encoder;

namespace Tern.Compiler.Backend;

public sealed class AssembledCode(int origin, byte[] bytes, string listing)
{
    public int Origin { get; } = origin;
    public byte[] Bytes { get; } = bytes;
    public string Listing { get; } = listing;
}

/// <summary>
/// The built-in assembler. Places instructions at addresses, picks zero-page addressing where
/// possible, turns out-of-range branches into an inverted branch over a <c>jmp</c>, and encodes.
/// </summary>
public static class Assembler
{
    private sealed class Slot(AsmItem item)
    {
        public AsmItem Item { get; } = item;
        public AddressingMode Mode;     // final mode for instructions
        public int Address;
        public int Size;
        public bool LongBranch;
    }

    /// <param name="beforeEncode">
    /// Called with the end address once layout is done, before encoding. Variables placed after the
    /// program get their addresses here. Variables without an address during layout are assumed to be
    /// outside zero page.
    /// </param>
    public static AssembledCode Assemble(IReadOnlyList<AsmItem> items, int origin, Action<int>? beforeEncode = null)
    {
        var slots = items.Select(i => new Slot(i)).ToList();

        foreach (var s in slots)
            if (s.Item is Instr instr)
                s.Mode = ChooseMode(instr);

        // Layout: grow branches that do not reach until nothing changes. Branches only ever
        // grow, so this always finishes.
        bool changed;
        do
        {
            int pc = origin;
            foreach (var s in slots)
            {
                s.Address = pc;
                s.Size = s.Item switch
                {
                    Instr i => s.LongBranch ? 5 : InstructionSet.OperandSize(s.Mode) + 1,
                    DataBytes d => d.Bytes.Length,
                    _ => 0,
                };
                if (s.Item is LabelDef l) l.Label.Address = pc;
                pc += s.Size;
            }
            if (pc > 0x10000) throw new InvalidOperationException($"Program does not fit in memory (ends at ${pc:X})");

            changed = false;
            foreach (var s in slots)
            {
                if (s.Item is Instr { Mode: AddressingMode.Relative, Operand: LabelOperand target } i && !s.LongBranch
                    && !Encoder.BranchInRange(Resolve(target.Label) + target.Offset, s.Address))
                {
                    if (i.AsmPos is { } pos)
                        throw new CompileError(pos,
                            $"Branch to {target} is out of range ({Resolve(target.Label) + target.Offset - s.Address - 2} bytes; a branch reaches -128 to 127). Use jmp");
                    s.LongBranch = true;
                    changed = true;
                }
            }
        } while (changed);

        beforeEncode?.Invoke(slots.Count == 0 ? origin : slots[^1].Address + slots[^1].Size);

        var bytes = new List<byte>();
        var listing = new StringBuilder();
        foreach (var s in slots)
        {
            switch (s.Item)
            {
                case Instr i:
                {
                    int start = bytes.Count;
                    if (s.LongBranch)
                    {
                        // bne far  ->  beq +3 / jmp far
                        Encoder.Encode(Invert(i.Mnemonic), AddressingMode.Relative, s.Address + 5, s.Address, bytes);
                        Encoder.Encode(Mnemonic.Jmp, AddressingMode.Absolute, Resolve(((LabelOperand)i.Operand!).Label), s.Address + 2, bytes);
                    }
                    else
                        Encoder.Encode(i.Mnemonic, s.Mode, OperandValue(i, s.Mode), s.Address, bytes);
                    ListInstruction(listing, s, i, bytes.Skip(start).ToArray());
                    break;
                }
                case DataBytes d:
                    bytes.AddRange(d.Bytes);
                    for (int k = 0; k < d.Bytes.Length; k += 8)
                    {
                        var chunk = d.Bytes.Skip(k).Take(8).ToArray();
                        listing.AppendLine($"${s.Address + k:X4}  {string.Join(" ", chunk.Select(b => b.ToString("X2")))}");
                    }
                    break;
                case LabelDef l:
                    listing.AppendLine($"{l.Label.Name}:");
                    break;
                case SourceLine src:
                    listing.AppendLine($"                                ; {src.Line,4}: {src.Text.Trim()}");
                    break;
                case Comment c:
                    listing.AppendLine($"                                ; {c.Text}");
                    break;
            }
        }
        return new AssembledCode(origin, bytes.ToArray(), listing.ToString());
    }

    /// <summary>Uses a zero-page mode when the operand is a zero-page address and the instruction has one.</summary>
    private static AddressingMode ChooseMode(Instr i)
    {
        var mode = i.Mode;
        if (i.Operand is LabelOperand) return mode;
        if (mode is AddressingMode.Absolute or AddressingMode.AbsoluteX or AddressingMode.AbsoluteY
            && AddressOf(i.Operand!) is < 0x100)
        {
            var zp = mode switch
            {
                AddressingMode.Absolute => AddressingMode.ZeroPage,
                AddressingMode.AbsoluteX => AddressingMode.ZeroPageX,
                _ => AddressingMode.ZeroPageY,
            };
            if (InstructionSet.TryGet(i.Mnemonic, zp, out _)) return zp;
        }
        if (!InstructionSet.TryGet(i.Mnemonic, mode, out _))
            throw new InvalidOperationException($"Code generator produced {i.Mnemonic} {mode}");
        return mode;
    }

    private static int? AddressOf(Operand op) => op switch
    {
        VarOperand v => v.Variable.Address < 0 ? null : Address(v),
        AddrOperand a => a.Address,
        _ => null,
    };

    private static int Address(VarOperand v)
    {
        if (v.Variable.Address < 0)
            throw new InvalidOperationException($"Variable {v.Variable.QualifiedName} has no address");
        return v.Variable.Address + v.Offset;
    }

    private static int Resolve(Label label) =>
        label.Address >= 0 ? label.Address : throw new InvalidOperationException($"Label {label.Name} is not defined");

    private static int OperandValue(Instr i, AddressingMode mode) => i.Operand switch
    {
        null => 0,
        Imm imm => imm.Value & 0xFF,
        ImmLabelByte b => Byte(Resolve(b.Label) + b.Offset, b.High),
        ImmVarByte v => Byte(Address(new VarOperand(v.Variable, v.Offset)), v.High),
        VarOperand v => Address(v),
        AddrOperand a => a.Address,
        LabelOperand l => Resolve(l.Label) + l.Offset,
        _ => throw new InvalidOperationException(),
    };

    private static int Byte(int address, bool high) => high ? (address >> 8) & 0xFF : address & 0xFF;

    private static Mnemonic Invert(Mnemonic branch) => branch switch
    {
        Mnemonic.Bcc => Mnemonic.Bcs, Mnemonic.Bcs => Mnemonic.Bcc,
        Mnemonic.Beq => Mnemonic.Bne, Mnemonic.Bne => Mnemonic.Beq,
        Mnemonic.Bmi => Mnemonic.Bpl, Mnemonic.Bpl => Mnemonic.Bmi,
        Mnemonic.Bvc => Mnemonic.Bvs, Mnemonic.Bvs => Mnemonic.Bvc,
        _ => throw new InvalidOperationException($"{branch} is not a branch"),
    };

    private static void ListInstruction(StringBuilder listing, Slot s, Instr i, byte[] encoded)
    {
        string hex = string.Join(" ", encoded.Take(3).Select(b => b.ToString("X2")));
        string text = s.LongBranch
            ? $"{Invert(i.Mnemonic).ToString().ToLowerInvariant()} *+5 / jmp {i.Operand}"
            : $"{i.Mnemonic.ToString().ToLowerInvariant()} {FormatOperand(i.Operand, s.Mode)}".TrimEnd();
        InstructionSet.TryGet(i.Mnemonic, s.Mode, out var info);
        string cycles = s.LongBranch ? "2+3" : info.Mode == AddressingMode.Relative ? "2+" : info.PageCrossPenalty ? $"{info.Cycles}+" : $"{info.Cycles}";
        listing.AppendLine($"${s.Address:X4}  {hex,-9} {text,-22}; {cycles}");
    }

    private static string FormatOperand(Operand? op, AddressingMode mode)
    {
        string o = op?.ToString() ?? "";
        return mode switch
        {
            AddressingMode.Accumulator => "a",
            AddressingMode.AbsoluteX or AddressingMode.ZeroPageX => $"{o},x",
            AddressingMode.AbsoluteY or AddressingMode.ZeroPageY => $"{o},y",
            AddressingMode.IndirectIndexed => $"({o}),y",
            AddressingMode.IndexedIndirect => $"({o},x)",
            AddressingMode.Indirect => $"({o})",
            _ => o,
        };
    }
}
