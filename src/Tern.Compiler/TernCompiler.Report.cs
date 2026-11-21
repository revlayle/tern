using System.Text;
using Tern.Compiler.Backend;
using Tern.Compiler.Semantics;

namespace Tern.Compiler;

/// <summary>The build's report: where everything went and how much room each area has left.</summary>
public static partial class TernCompiler
{
    private sealed partial class Builder
    {
        private string Report(Assembled assembled, ProgramFile file, List<(string Name, int Address)> entries, List<OverlayFile> overlayFiles)
        {
            var (plan, code) = (assembled.Plan, assembled.Code);
            var report = new StringBuilder();
            // A .prg holds the gaps between areas; a .xex and a system file hold only the runs of bytes.
            report.AppendLine(platform.Format.HoldsGaps
                ? $"Code and data: ${file.LoadAddress:X4}-${file.End - 1:X4} ({file.Image.Length} bytes)"
                : $"Code and data: {string.Join(", ", code.Segments.Select(s => $"${s.Address:X4}-${s.Address + s.Bytes.Length - 1:X4}"))} ({code.Segments.Sum(s => s.Bytes.Length)} bytes)");
            foreach (var line in Output.ReportLines(plan)) report.AppendLine(line);
            string variablesAt = platform.VariableRam is null ? "after the program" : "in RAM";
            report.AppendLine(code.Variables is { } vars
                ? $"Variables {variablesAt}: {plan.TotalSize} bytes, up to ${vars.Last:X4}"
                  + (plan.PageArrays.Count > 0
                      ? $", from ${vars.First:X4}, placed so that indexed reads do not cross a page: {string.Join(", ", plan.PageArrays.Select(a => $"{a.QualifiedName} ${a.Address:X4}"))}"
                      : "")
                : $"Variables {variablesAt}: 0 bytes");
            foreach (var g in program.Globals.Where(g => g.Alignment > 1).OrderBy(g => g.Address))
                report.AppendLine($"Aligned {g.Name}: ${g.Address:X4} (@ align({g.Alignment})), {Plural(AlignmentPadding(g, plan, code), "byte", "bytes")} of padding before it");
            foreach (var table in program.Globals.Where(g => g.IsOffsetTable))
                report.AppendLine($"Offset table {table.Name}: {table.Type.Size} bytes, in the program");
            // The low table follows the high one.
            foreach (var (function, pos, high, _, span) in gen.MatchTables)
                report.AppendLine($"Jump table of the indexed match at {pos} in {function.Name}: {new AddressRange(high.Address, high.Address + 2 * span - 1)}, {2 * span} bytes for {span} values");
            Areas(report, plan, code, file.Banks, overlayFiles);
            if (binary)
            {
                report.AppendLine($"Jump table at ${start.Address:X4}:");
                foreach (var (name, address) in entries) report.AppendLine($"  ${address:X4}  {name}");
            }
            else if (program.ExportTableAddress is int exportTable)
            {
                report.AppendLine($"Export table at ${exportTable:X4}:");
                foreach (var (name, address) in entries) report.AppendLine($"  ${address:X4}  {name}");
            }
            // Information, not a warning (spec 8.1): a call from one root can overwrite another root's call in progress.
            var shared = program.Functions.Where(f => f.Roots.Count > 1 && !f.IsInline && f.FrameVariables.Sum(v => v.Type.Size) > 0).ToList();
            if (shared.Count > 0)
            {
                report.AppendLine("Functions with memory reachable from two roots:");
                foreach (var f in shared)
                    report.AppendLine($"  {f.Name} ({Bytes(f.FrameVariables.Sum(v => v.Type.Size))}): {string.Join(", ", f.Roots.OrderBy(program.Functions.IndexOf).Select(r => r.Name))}");
            }
            report.AppendLine("Zero page:");
            report.Append(plan.ZeroPageMap);
            return report.ToString();
        }

        /// <summary>The report's lines for the program areas and regions, the banks, the chr areas and the overlay areas.</summary>
        private void Areas(StringBuilder report, RamPlan plan, AssembledCode code, List<Module> banks, List<OverlayFile> overlayFiles)
        {
            // The variables' own RAM (the NES) has its line above (review M27, suggestion 7).
            var programAreas = code.Areas.Where(a => !layout.Variables.Any(v => v.Contains(a.Free))).ToList();
            if (programAreas.Count > 1 || layout.Regions.Count > 0)
            {
                report.AppendLine("Program areas:");
                foreach (var a in programAreas)
                    report.AppendLine($"  {a.Free}: {a.UsedEnd - a.Free.First} of {a.Free.Size} bytes used");
                foreach (var (name, range) in layout.Regions)
                    report.AppendLine(plan.RegionUse.TryGetValue(name, out var use)
                        ? $"  region {name} {range}: {use.Size} of {range.Size} bytes used"
                        : $"  region {name} {range}: unused");
            }
            foreach (var (name, range) in layout.Banks)
            {
                report.AppendLine($"Bank area {name} {range}:");
                foreach (var bank in banks.GroupBy(m => m.Bank!.Value).OrderBy(g => g.Key))
                {
                    int used = code.Overlays.TryGetValue(bank.First().Unit!, out var placed) ? placed.Sum(s => s.Bytes.Length) : 0;
                    report.AppendLine($"  bank {bank.Key}: {string.Join(", ", bank.Select(m => m.Name))}, {used} of {range.Size} bytes");
                }
            }
            foreach (var (name, range) in layout.Chr)
                report.AppendLine($"Chr area {name} {range}: {plan.ChrUse.GetValueOrDefault(name)} of {range.Size} bytes used");
            foreach (var area in layout.Overlays)
            {
                var inArea = overlayFiles.Where(o => o.Area == area.Key).ToList();
                report.AppendLine($"Overlay area {area.Key} {area.Value}" + (inArea.Count == 0 ? ": no overlay" : ":"));
                foreach (var o in inArea)
                    report.AppendLine($"  {o.Name}{platform.BinaryExtension}: {o.Image.Length} of {area.Value.Size} bytes (${o.LoadAddress:X4}-${o.LoadAddress + o.Image.Length - 1:X4})");
            }
        }
    }
}
