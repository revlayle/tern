using System.Text;

namespace Tern.Compiler.Tests;

/// <summary>The Atari's device I/O library (lib/atari/io.tern), run against a stand-in for CIO.</summary>
public class AtariIoTests
{
    private const int Ok = 1, Eof = 0x88, NotFound = 0xAA, AlreadyOpen = 0x81, NotOpen = 0x85, DiskFull = 0xA2;

    /// <summary>
    /// CIO and a disk in C#: open, get bytes, put bytes and close on any IOCB, files kept by name. Records
    /// every call. As on a real Atari, an IOCB stays taken after a failed open until it is closed.
    /// </summary>
    private sealed class FakeCio
    {
        public readonly List<string> Calls = [];
        public readonly Dictionary<string, byte[]> Files = [];
        public int DiskSpace = int.MaxValue;        // bytes a written file may have; more fails at close
        public bool EndOfFileNext;                  // a read that ends at the file's last byte gives 3, as Atari DOS's disk handler may

        private readonly Dictionary<int, (string Name, bool Write, int Pos)> _open = [];

        public IReadOnlyDictionary<int, Action<Run>> Traps => new Dictionary<int, Action<Run>> { [0xE456] = Cio };

        private void Cio(Run run)
        {
            var ram = run.Ram.Bytes;
            int x = run.Cpu.X, iocb = 0x0340 + x;
            int command = ram[iocb + 2];
            int buffer = ram[iocb + 4] | ram[iocb + 5] << 8;
            int length = ram[iocb + 8] | ram[iocb + 9] << 8;
            int status = Ok;
            switch (command)
            {
                case 0x03:
                {
                    int end = Array.IndexOf(ram, (byte)0x9B, buffer);
                    var name = Encoding.Latin1.GetString(ram, buffer, end - buffer);
                    int mode = ram[iocb + 10];
                    Calls.Add($"OPEN {x >> 4} {name} {mode} {ram[iocb + 11]}");
                    if (_open.ContainsKey(x)) { status = AlreadyOpen; break; }
                    _open[x] = (name, mode == 8, 0);
                    if (mode == 8) Files[name] = [];
                    else if (!Files.ContainsKey(name)) status = NotFound;
                    break;
                }
                case 0x07:
                {
                    Calls.Add($"GET {length}");
                    if (!_open.TryGetValue(x, out var f) || f.Write || !Files.ContainsKey(f.Name)) { status = NotOpen; break; }
                    var data = Files[f.Name];
                    int n = Math.Min(length, data.Length - f.Pos);
                    data.AsSpan(f.Pos, n).CopyTo(ram.AsSpan(buffer));
                    _open[x] = f with { Pos = f.Pos + n };
                    if (n < length) status = Eof;
                    else if (EndOfFileNext && n > 0 && f.Pos + n == data.Length) status = 3;
                    ram[iocb + 8] = (byte)n;
                    ram[iocb + 9] = (byte)(n >> 8);
                    break;
                }
                case 0x0B:
                {
                    Calls.Add($"PUT {length}");
                    if (!_open.TryGetValue(x, out var f) || !f.Write) { status = NotOpen; break; }
                    Files[f.Name] = [.. Files[f.Name], .. ram.AsSpan(buffer, length).ToArray()];
                    break;
                }
                case 0x0C:
                    Calls.Add("CLOSE");
                    if (_open.Remove(x, out var c) && c.Write && Files[c.Name].Length > DiskSpace)
                    {
                        Files.Remove(c.Name);
                        status = DiskFull;
                    }
                    break;
                default:
                    throw new InvalidOperationException($"CIO command ${command:X2}");
            }
            ram[iocb + 3] = (byte)status;
            run.Cpu.Y = (byte)status;
            run.Cpu.N = status >= 128;
            run.Cpu.Z = false;
        }
    }

    private const int Buf = 0x4000;

    /// <summary>Runs <paramref name="body"/> as main; BUF is a buffer at $4000, filled with <paramref name="fill"/> first.</summary>
    private static Run Go(string body, FakeCio cio, byte[]? fill = null) => Run.Program($$"""
        import io
        data NAME = "D1:SCORES.DAT"
        data LEVEL = "D1:LEVEL1.XEX"
        const BUF = $4000
        var failed: bool
        var n: u16
        var st: u8
        var results: [u8; 4]
        fn main() {
            {{body}}
        }
        """, traps: cio.Traps, platform: Platform.Atari, setup: ram => fill?.CopyTo(ram.Bytes, Buf));

    private static byte[] Bytes(Run r, int at, int n) => r.Ram.Bytes.AsSpan(at, n).ToArray();

    private static byte[] Pattern(int n, int seed = 0x5A) => [.. Enumerable.Range(0, n).Select(i => (byte)(i ^ seed ^ i >> 8))];

    // ---------- Files ----------

    [Fact]
    public void Open_passes_the_name_with_an_eol_and_the_mode_on_iocb_1()
    {
        var cio = new FakeCio();
        cio.Files["D1:SCORES.DAT"] = [1];
        var r = Go("failed = io.open(addr(NAME), len(NAME), io.READ)", cio);
        Assert.Equal(0, r.U8("failed"));
        Assert.Equal(["OPEN 1 D1:SCORES.DAT 4 0"], cio.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(39)]
    [InlineData(40)]            // cut to 39
    [InlineData(255)]
    public void A_name_has_at_most_39_characters(int n)
    {
        var cio = new FakeCio();
        var name = string.Concat(Enumerable.Range(0, 255).Select(i => (char)('A' + i % 26)));
        var r = Run.Program($$"""
            import io
            data LONG = "{{name}}"
            var failed: bool
            fn main() { failed = io.open(addr(LONG), {{n}}, io.WRITE) }
            """, traps: cio.Traps, platform: Platform.Atari);
        Assert.Equal(0, r.U8("failed"));
        Assert.Equal([$"OPEN 1 {name[..Math.Min(n, 39)]} 8 0"], cio.Calls);
    }

    [Fact]
    public void Open_of_a_missing_file_fails_with_cios_status()
    {
        var cio = new FakeCio();
        var r = Go("""
            failed = io.open(addr(NAME), len(NAME), io.READ)
            st = io.status()
            """, cio);
        Assert.NotEqual(0, r.U8("failed"));
        Assert.Equal(NotFound, r.U8("st"));
    }

    [Fact]
    public void Read_returns_the_count_and_stops_at_the_end_of_the_file()
    {
        var cio = new FakeCio();
        cio.Files["D1:SCORES.DAT"] = Pattern(260);
        var r = Go("""
            failed = io.open(addr(NAME), len(NAME), io.READ)
            n = io.read(BUF, 256)
            st = io.status()
            results[0] = io.read(BUF + 256, 0).lo          // nothing: no call
            n += io.read(BUF + 256, 10) << 8               // the last 4 bytes
            results[1] = io.status()
            results[2] = io.read(BUF + 280, 10).lo         // after the end
            results[3] = io.status()
            failed = failed || io.close()
            """, cio);
        Assert.Equal(0, r.U8("failed"));
        Assert.Equal(256 | 4 << 8, r.U16("n"));
        Assert.Equal(Ok, r.U8("st"));
        Assert.Equal([0, Eof, 0, Eof], Bytes(r, r.Address("results"), 4));
        Assert.Equal(cio.Files["D1:SCORES.DAT"], Bytes(r, Buf, 260));
        Assert.Equal(["OPEN 1 D1:SCORES.DAT 4 0", "GET 256", "GET 10", "GET 10", "CLOSE"], cio.Calls);
    }

    [Fact]
    public void Read_returns_0_on_an_error()
    {
        var cio = new FakeCio();
        var r = Go("""
            n = io.read(BUF, 5)                         // no file open
            st = io.status()
            """, cio);
        Assert.Equal(0, r.U16("n"));
        Assert.Equal(NotOpen, r.U8("st"));
    }

    [Fact]
    public void Write_puts_the_bytes_and_returns_true_on_failure()
    {
        var cio = new FakeCio();
        var r = Go("""
            failed = io.open(addr(NAME), len(NAME), io.WRITE)
            failed = failed || io.write(BUF, 3)
            failed = failed || io.write(BUF, 0)          // nothing: no call
            failed = failed || io.close()
            results[0] = io.write(BUF, 3) as u8          // closed
            """, cio, fill: [0, 255, 0x9B]);
        Assert.Equal(0, r.U8("failed"));
        Assert.NotEqual(0, r.U8("results"));
        Assert.Equal([0, 255, 0x9B], cio.Files["D1:SCORES.DAT"]);
        Assert.Equal(["OPEN 1 D1:SCORES.DAT 8 0", "PUT 3", "CLOSE", "PUT 3"], cio.Calls);
    }

    // ---------- Whole files ----------

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(1000)]
    public void A_saved_file_loads_back(int size)
    {
        var cio = new FakeCio();
        var r = Go($"""
            failed = io.save(addr(NAME), len(NAME), BUF, {size})
            failed = failed || io.load(addr(NAME), len(NAME), BUF + $1000, 2000)   // a shorter file is no error
            st = io.status()
            """, cio, fill: Pattern(size));
        Assert.Equal(0, r.U8("failed"));
        Assert.Equal(Ok, r.U8("st"));
        Assert.Equal(Pattern(size), cio.Files["D1:SCORES.DAT"]);
        Assert.Equal(Pattern(size), Bytes(r, Buf + 0x1000, size));
        Assert.Equal(0, r.Ram.Bytes[Buf + 0x1000 + size]);
        Assert.Equal(["OPEN 1 D1:SCORES.DAT 8 0", $"PUT {size}", "CLOSE", "OPEN 1 D1:SCORES.DAT 4 0", "GET 2000", "CLOSE"], cio.Calls);
    }

    [Fact]
    public void Load_reads_at_most_max_bytes()
    {
        var cio = new FakeCio();
        cio.Files["D1:SCORES.DAT"] = Pattern(100);
        var r = Go("failed = io.load(addr(NAME), len(NAME), BUF, 60)", cio);
        Assert.Equal(0, r.U8("failed"));
        Assert.Equal(Pattern(60), Bytes(r, Buf, 60));
        Assert.Equal(0, r.Ram.Bytes[Buf + 60]);
    }

    [Fact]
    public void Load_of_a_missing_file_closes_the_iocb_and_keeps_the_error()
    {
        var cio = new FakeCio();
        var r = Go("""
            failed = io.load(addr(NAME), len(NAME), BUF, 10)
            st = io.status()
            results[0] = io.open(addr(NAME), len(NAME), io.WRITE) as u8   // the IOCB is free again
            """, cio);
        Assert.NotEqual(0, r.U8("failed"));
        Assert.Equal(NotFound, r.U8("st"));
        Assert.Equal(0, r.U8("results"));
        Assert.Equal(["OPEN 1 D1:SCORES.DAT 4 0", "CLOSE", "OPEN 1 D1:SCORES.DAT 8 0"], cio.Calls);
    }

    [Fact]
    public void Save_to_a_full_disk_fails_at_close()
    {
        var cio = new FakeCio { DiskSpace = 10 };
        var r = Go("""
            failed = io.save(addr(NAME), len(NAME), BUF, 11)
            st = io.status()
            """, cio);
        Assert.NotEqual(0, r.U8("failed"));
        Assert.Equal(DiskFull, r.U8("st"));
        Assert.False(cio.Files.ContainsKey("D1:SCORES.DAT"));
    }

    [Fact]
    public void Save_fails_when_the_iocb_is_taken_and_keeps_the_error()
    {
        var cio = new FakeCio();
        var r = Go("""
            failed = io.open(addr(NAME), len(NAME), io.WRITE)
            failed = io.save(addr(NAME), len(NAME), BUF, 1)
            st = io.status()
            """, cio);
        Assert.NotEqual(0, r.U8("failed"));
        Assert.Equal(AlreadyOpen, r.U8("st"));
        Assert.Equal(["OPEN 1 D1:SCORES.DAT 8 0", "OPEN 1 D1:SCORES.DAT 8 0", "CLOSE"], cio.Calls);
    }

    // ---------- Binary files ----------

    /// <summary>A .xex: $FF $FF, then each segment's first and last address and bytes.</summary>
    private static byte[] Xex(params (int At, byte[] Bytes, bool Header)[] segments)
    {
        var file = new List<byte> { 0xFF, 0xFF };
        foreach (var (at, bytes, header) in segments)
        {
            if (header) file.AddRange([0xFF, 0xFF]);
            int last = at + bytes.Length - 1;
            file.AddRange([(byte)at, (byte)(at >> 8), (byte)last, (byte)(last >> 8)]);
            file.AddRange(bytes);
        }
        return [.. file];
    }

    [Fact]
    public void Load_xex_puts_each_segment_at_its_own_address()
    {
        var cio = new FakeCio();
        cio.Files["D1:SCORES.DAT"] = Xex(
            (0x4000, Pattern(1), false),
            (0x40FF, Pattern(2, 7), true),          // crosses a page; with its own $FF $FF
            (0x5000, Pattern(1000, 3), false),
            (0x02E0, [0x00, 0x60], false));         // RUNAD: loaded, not run
        var r = Go("""
            failed = io.load_xex(addr(NAME), len(NAME))
            st = io.status()
            """, cio);
        Assert.Equal(0, r.U8("failed"));
        Assert.Equal(Ok, r.U8("st"));
        Assert.Equal(Pattern(1), Bytes(r, 0x4000, 1));
        Assert.Equal(Pattern(2, 7), Bytes(r, 0x40FF, 2));
        Assert.Equal(Pattern(1000, 3), Bytes(r, 0x5000, 1000));
        Assert.Equal(0, r.Ram.Bytes[0x5000 + 1000]);
        Assert.Equal(0x6000, r.Ram.Bytes[0x02E0] | r.Ram.Bytes[0x02E1] << 8);
        Assert.Equal("CLOSE", cio.Calls[^1]);
    }

    [Theory]
    [InlineData(new byte[] { }, Eof)]                                   // empty
    [InlineData(new byte[] { 0x00, 0x40, 0x00, 0x40, 0x11 }, Ok)]       // no $FF $FF
    [InlineData(new byte[] { 0xFF, 0xFF, 0x00 }, Eof)]                  // ends inside the addresses
    [InlineData(new byte[] { 0xFF, 0xFF, 0x00, 0x40, 0x01, 0x40, 0x11 }, Eof)]   // ends inside the bytes
    [InlineData(new byte[] { 0xFF, 0xFF, 0x00, 0x40, 0x00, 0x40, 0x11, 0xFF, 0xFF, 0x00 }, Eof)]
    [InlineData(new byte[] { 0xFF, 0xFF, 0x00, 0x40, 0xFF, 0x3F, 0x11 }, Ok)]   // last = first - 1
    [InlineData(new byte[] { 0xFF, 0xFF, 0x00, 0x40, 0x00, 0x10, 0x11 }, Ok)]   // last far below first: would wrap
    public void Load_xex_fails_on_a_file_that_is_not_whole(byte[] file, int status)
    {
        var cio = new FakeCio();
        cio.Files["D1:SCORES.DAT"] = file;
        var r = Go("""
            failed = io.load_xex(addr(NAME), len(NAME))
            st = io.status()
            """, cio);
        Assert.NotEqual(0, r.U8("failed"));
        Assert.Equal(status, r.U8("st"));
        Assert.Equal("CLOSE", cio.Calls[^1]);
    }

    [Fact]
    public void Load_xex_of_a_missing_file_fails()
    {
        var cio = new FakeCio();
        var r = Go("""
            failed = io.load_xex(addr(NAME), len(NAME))
            st = io.status()
            """, cio);
        Assert.NotEqual(0, r.U8("failed"));
        Assert.Equal(NotFound, r.U8("st"));
    }

    [Fact]
    public void A_status_below_128_other_than_1_is_success()
    {
        // Review of the M25 follow-up, suggestion 3: the disk may answer 3, "end of file next".
        var cio = new FakeCio { EndOfFileNext = true };
        cio.Files["D1:SCORES.DAT"] = Pattern(10);
        cio.Files["D1:LEVEL1.XEX"] = Xex((0x5000, Pattern(10, 3), false));
        var r = Go("""
            failed = io.load(addr(NAME), len(NAME), BUF, 10)
            st = io.status()
            failed = failed || io.load_xex(addr(LEVEL), len(LEVEL))
            failed = failed || io.open(addr(NAME), len(NAME), io.READ)
            n = io.read(BUF + 100, 10)
            results[0] = io.status()
            failed = failed || io.close()
            """, cio);
        Assert.Equal(0, r.U8("failed"));
        Assert.Equal(Ok, r.U8("st"));                  // load ends with close's status
        Assert.Equal(10, r.U16("n"));
        Assert.Equal(3, r.U8("results"));
        Assert.Equal(Pattern(10), Bytes(r, Buf, 10));
        Assert.Equal(Pattern(10, 3), Bytes(r, 0x5000, 10));
    }
}
