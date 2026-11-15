using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>
/// A bus that passes every access on and logs the reads and writes of the watched addresses (review
/// code-quality-tests, finding 3). Spec 4 says every read and write of volatile memory in the source
/// happens exactly once and in order; this checks it on the running program, 16-bit variables included.
/// </summary>
public sealed class BusLog(IBus inner, IEnumerable<int> watched) : IBus
{
    /// <param name="Index">The number of the bus access, counting every access, watched or not.</param>
    public readonly record struct Access(long Index, int Address, byte Value, bool IsWrite);

    private readonly HashSet<int> _watched = [.. watched];
    private readonly List<Access> _log = [];
    private long _count;

    public IReadOnlyList<Access> Accesses => _log;

    public byte Read(ushort address)
    {
        byte value = inner.Read(address);
        if (_watched.Contains(address)) _log.Add(new(_count, address, value, false));
        _count++;
        return value;
    }

    public void Write(ushort address, byte value)
    {
        if (_watched.Contains(address)) _log.Add(new(_count, address, value, true));
        _count++;
        inner.Write(address, value);
    }

    /// <summary>The number of reads of an address.</summary>
    public int Reads(int address) => _log.Count(a => a.Address == address && !a.IsWrite);

    /// <summary>
    /// The values the program wrote to an address, in order. An NMOS read-modify-write instruction
    /// (<c>inc</c>, <c>asl</c> on memory) writes the old value back on the bus access right after its
    /// read and before the new value; that write-back is the chip's, not the program's, and is left out.
    /// </summary>
    public IReadOnlyList<byte> Writes(int address)
    {
        var mine = _log.Where(a => a.Address == address).ToList();
        var values = new List<byte>();
        for (int k = 0; k < mine.Count; k++)
        {
            var a = mine[k];
            if (!a.IsWrite) continue;
            bool writeBack = k > 0 && k + 1 < mine.Count
                && mine[k - 1] is { IsWrite: false } read && read.Index == a.Index - 1 && read.Value == a.Value
                && mine[k + 1] is { IsWrite: true } next && next.Index == a.Index + 1;
            if (!writeBack) values.Add(a.Value);
        }
        return values;
    }
}
