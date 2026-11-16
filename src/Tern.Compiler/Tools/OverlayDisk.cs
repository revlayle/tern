namespace Tern.Compiler.Tools;

/// <summary>
/// The build's own overlay files as the disk that <c>tern run</c>'s stand-ins load and read from
/// (review code-quality-tools 6), and the files open on it, each under the number the machine's ROM
/// gives it (an IOCB, a ProDOS reference number).
/// </summary>
/// <param name="extension">What follows the overlay's name in a file name on this machine's disk, in capitals ("" on the C64).</param>
internal sealed class OverlayDisk(IReadOnlyList<OverlayFile> overlays, string extension)
{
    /// <summary>The open files and the next byte to read of each.</summary>
    private readonly Dictionary<int, (byte[] File, int Pos)> _open = [];

    /// <summary>The overlay whose name in capitals, with the extension, is <paramref name="fileName"/>, or null.</summary>
    public OverlayFile? Find(string fileName) => overlays.FirstOrDefault(o => (o.Name + extension).ToUpperInvariant() == fileName);

    public int OpenCount => _open.Count;

    public bool IsOpen(int number) => _open.ContainsKey(number);

    public void Open(int number, byte[] file) => _open[number] = (file, 0);

    /// <summary>The length of the file open as <paramref name="number"/>, or null when none is.</summary>
    public int? Length(int number) => _open.TryGetValue(number, out var f) ? f.File.Length : null;

    /// <summary>The next bytes of the file open as <paramref name="number"/>, at most <paramref name="count"/>; null when none is open.</summary>
    public byte[]? Read(int number, int count)
    {
        if (!_open.TryGetValue(number, out var f)) return null;
        int n = Math.Min(count, f.File.Length - f.Pos);
        _open[number] = (f.File, f.Pos + n);
        return f.File[f.Pos..(f.Pos + n)];
    }

    /// <summary>Closes the file open as <paramref name="number"/>; false when none was.</summary>
    public bool Close(int number) => _open.Remove(number);

    public void CloseAll() => _open.Clear();
}
