using Tern.Compiler.Tools;

namespace Tern.Compiler.Tests;

/// <summary>The Apple II's file library (lib/apple2/io.tern, spec 10.1), run against a stand-in for the ProDOS MLI and a disk.</summary>
public class Apple2IoTests
{
    private const int NotFound = 0x46, Eof = 0x4C, Duplicate = 0x47, DiskFull = 0x48, BadRef = 0x43, BadBuffer = 0x56, Format = 0x4A;

    private sealed class DiskFile
    {
        public byte[] Bytes = [];
        public int Type = 0x06, Aux;
    }

    /// <summary>
    /// The MLI and a disk in C#: CREATE, OPEN, READ, WRITE, CLOSE, SET_EOF, GET_EOF, GET_FILE_INFO and
    /// SET_FILE_INFO on files kept by name, with the startup's GET_PREFIX (an empty prefix). Records
    /// every call but GET_PREFIX. As ProDOS does, it fails an OPEN whose buffer is not on a page or is
    /// in use, and a READ that finds nothing left gives $4C.
    /// </summary>
    private sealed class FakeMli
    {
        public readonly List<string> Calls = [];
        public readonly Dictionary<string, DiskFile> Files = [];
        public readonly Dictionary<int, (string Name, int Pos, int Buffer)> Open = [];
        public int DiskSpace = int.MaxValue;        // bytes a file may have; a write past it fails with $48
        public int? FailClose;                      // CLOSE fails with this code
        public int? FailRead;                       // READ fails with this code

        public IReadOnlyDictionary<int, Action<Run>> Traps => new Dictionary<int, Action<Run>> { [0xBF00] = Mli };

        private void Mli(Run run)
        {
            var b = run.Ram.Bytes;
            int sp = 0x100 + (byte)(run.Cpu.SP + 1);
            int at = (b[sp] | b[sp + 1] << 8) + 1;
            int command = b[at], list = b[at + 1] | b[at + 2] << 8;
            // The trap returns after the jsr; the MLI returns after the 3 bytes too.
            int back = at + 2;
            (b[sp], b[sp + 1]) = ((byte)back, (byte)(back >> 8));
            int Word(int o) => b[list + o] | b[list + o + 1] << 8;
            void Put(int o, int value, int size = 2) { for (int i = 0; i < size; i++) b[list + o + i] = (byte)(value >> 8 * i); }
            string Path() => new([.. Enumerable.Range(1, b[Word(1)]).Select(i => (char)b[Word(1) + i])]);
            int error = 0;
            switch (command)
            {
                case 0xC7:
                    b[Word(1)] = 0;
                    break;
                case 0xC0:
                    Calls.Add($"CREATE {Path()} {b[list]} access ${b[list + 3]:X2} type ${b[list + 4]:X2} aux ${Word(5):X4} storage {b[list + 7]}");
                    if (Files.ContainsKey(Path())) error = Duplicate;
                    else Files[Path()] = new DiskFile { Type = b[list + 4], Aux = Word(5) };
                    break;
                case 0xC8:
                {
                    int buffer = Word(3);
                    Calls.Add($"OPEN {Path()} {b[list]}");
                    if ((buffer & 0xFF) != 0 || Open.Values.Any(o => o.Buffer == buffer)) error = BadBuffer;
                    else if (!Files.ContainsKey(Path())) error = NotFound;
                    else
                    {
                        int reference = Enumerable.Range(1, 8).First(r => !Open.ContainsKey(r));
                        Open[reference] = (Path(), 0, buffer);
                        Put(5, reference, 1);
                    }
                    break;
                }
                case 0xCA or 0xCB:
                {
                    int reference = b[list + 1], data = Word(2), request = Word(4);
                    Calls.Add($"{(command == 0xCA ? "READ" : "WRITE")} {reference} {request} {b[list]}");
                    if (!Open.TryGetValue(reference, out var f)) { error = BadRef; break; }
                    var file = Files[f.Name];
                    int n;
                    if (command == 0xCA)
                    {
                        if (FailRead is int fail) { error = fail; Put(6, 0); break; }
                        n = Math.Min(request, file.Bytes.Length - f.Pos);
                        file.Bytes.AsSpan(f.Pos, n).CopyTo(b.AsSpan(data));
                        if (n == 0) error = Eof;
                    }
                    else
                    {
                        n = Math.Max(0, Math.Min(request, DiskSpace - f.Pos));
                        var bytes = file.Bytes.Length < f.Pos + n ? file.Bytes.Concat(new byte[f.Pos + n - file.Bytes.Length]).ToArray() : file.Bytes;
                        b.AsSpan(data, n).CopyTo(bytes.AsSpan(f.Pos));
                        file.Bytes = bytes;
                        if (n < request) error = DiskFull;
                    }
                    Open[reference] = f with { Pos = f.Pos + n };
                    Put(6, n);
                    break;
                }
                case 0xCC:
                {
                    int reference = b[list + 1];
                    Calls.Add($"CLOSE {reference}");
                    if (FailClose is int fail) error = fail;
                    else if (reference == 0) Open.Clear();
                    else if (!Open.Remove(reference)) error = BadRef;
                    break;
                }
                case 0xD0:
                {
                    int eof = Word(2) | b[list + 4] << 16;
                    Calls.Add($"SET_EOF {b[list + 1]} {eof} {b[list]}");
                    if (!Open.TryGetValue(b[list + 1], out var f)) { error = BadRef; break; }
                    Files[f.Name].Bytes = Files[f.Name].Bytes[..Math.Min(eof, Files[f.Name].Bytes.Length)];
                    break;
                }
                case 0xD1:
                {
                    Calls.Add($"GET_EOF {b[list + 1]} {b[list]}");
                    if (!Open.TryGetValue(b[list + 1], out var f)) { error = BadRef; break; }
                    Put(2, Files[f.Name].Bytes.Length, 3);
                    break;
                }
                case 0xC4:
                {
                    Calls.Add($"GET_FILE_INFO {Path()} {b[list]}");
                    if (!Files.TryGetValue(Path(), out var file)) { error = NotFound; break; }
                    Put(3, 0xC3, 1);
                    Put(4, file.Type, 1);
                    Put(5, file.Aux);
                    Put(7, 1, 1);
                    Put(8, file.Bytes.Length / 512 + 1);
                    for (int i = 10; i < 18; i++) b[list + i] = 0x11;
                    break;
                }
                case 0xC3:
                {
                    Calls.Add($"SET_FILE_INFO {Path()} {b[list]} type ${b[list + 4]:X2} aux ${Word(5):X4}");
                    if (!Files.TryGetValue(Path(), out var file)) { error = NotFound; break; }
                    file.Type = b[list + 4];
                    file.Aux = Word(5);
                    break;
                }
                default:
                    throw new InvalidOperationException($"MLI call ${command:X2}");
            }
            run.Cpu.A = (byte)error;
            run.Cpu.C = error != 0;
            run.Cpu.Z = error == 0;
        }
    }

    private const int Buf = 0x6000;

    /// <summary>Runs <paramref name="body"/> as main; BUF is a buffer at $6000, filled with <paramref name="fill"/> first.</summary>
    private static Run Go(string body, FakeMli mli, byte[]? fill = null) => Run.Program($$"""
        import io
        data NAME = "SCORES"
        data LEVEL = "LEVEL1.BIN"
        const BUF = $6000
        var failed: bool
        var n: u16
        var st: u8
        var results: [u8; 4]
        fn main() {
            {{body}}
        }
        """, traps: mli.Traps, platform: Platform.Apple2, setup: ram => fill?.CopyTo(ram.Bytes, Buf));

    private static byte[] Bytes(Run r, int at, int n) => r.Ram.Bytes.AsSpan(at, n).ToArray();

    private static byte[] Pattern(int n, int seed = 0x5A) => [.. Enumerable.Range(0, n).Select(i => (byte)(i ^ seed ^ i >> 8))];

    // ---------- Files ----------

    [Fact]
    public void Open_passes_the_name_with_its_length_and_bit_7_cleared_and_a_page_aligned_buffer()
    {
        var mli = new FakeMli();
        mli.Files["SCORES"] = new DiskFile { Bytes = [1] };
        var r = Go("failed = io.open(addr(NAME), len(NAME), io.READ)", mli);
        Assert.Equal(0, r.U8("failed"));
        Assert.Equal(["OPEN SCORES 3"], mli.Calls);
        int buffer = r.Address("io.buffer");
        Assert.Equal(0, buffer % 256);
        Assert.Equal(buffer, mli.Open.Single().Value.Buffer);
        Assert.Equal(6, r.U8("io.path"));
        Assert.Equal("SCORES"u8.ToArray(), Bytes(r, r.Address("io.path") + 1, 6));
    }

    [Fact]
    public void A_name_is_cut_at_64_characters()
    {
        var name = "/" + new string('A', 70);
        var mli = new FakeMli();
        var r = Run.Program($$"""
            import io
            data NAME = "{{name}}"
            var failed: bool
            fn main() { failed = io.open(addr(NAME), len(NAME), io.READ) }
            """, traps: mli.Traps, platform: Platform.Apple2);
        Assert.Equal(1, r.U8("failed"));
        Assert.Equal([$"OPEN {name[..64]} 3"], mli.Calls);
    }

    [Fact]
    public void Opening_a_missing_file_fails_with_not_found()
    {
        var mli = new FakeMli();
        var r = Go("failed = io.open(addr(NAME), len(NAME), io.READ)\n st = io.status()", mli);
        Assert.Equal(1, r.U8("failed"));
        Assert.Equal(NotFound, r.U8("st"));
    }

    [Fact]
    public void Write_creates_a_missing_file_as_bin_with_aux_type_0()
    {
        var mli = new FakeMli();
        var r = Go("failed = io.open(addr(NAME), len(NAME), io.WRITE)\n st = io.status()", mli);
        Assert.Equal(0, r.U8("failed"));
        Assert.Equal(0, r.U8("st"));
        Assert.Equal(["CREATE SCORES 7 access $C3 type $06 aux $0000 storage 1", "OPEN SCORES 3", "SET_EOF 1 0 2"], mli.Calls);
        Assert.Empty(mli.Files["SCORES"].Bytes);
    }

    [Fact]
    public void Write_empties_an_existing_file()
    {
        var mli = new FakeMli();
        mli.Files["SCORES"] = new DiskFile { Bytes = Pattern(300), Type = 0x04 };
        var r = Go("failed = io.open(addr(NAME), len(NAME), io.WRITE)\n st = io.status()", mli);
        Assert.Equal(0, r.U8("failed"));
        Assert.Equal(0, r.U8("st"));
        Assert.Equal(["CREATE SCORES 7 access $C3 type $06 aux $0000 storage 1", "OPEN SCORES 3", "SET_EOF 1 0 2"], mli.Calls);
        Assert.Empty(mli.Files["SCORES"].Bytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(1000)]
    public void Read_reads_the_bytes_asked_for(int count)
    {
        var mli = new FakeMli();
        mli.Files["SCORES"] = new DiskFile { Bytes = Pattern(2000) };
        var r = Go($"io.open(addr(NAME), len(NAME), io.READ)\n n = io.read(BUF as ptr u8, {count})\n st = io.status()", mli);
        Assert.Equal(count, r.U16("n"));
        Assert.Equal(0, r.U8("st"));
        Assert.Equal(Pattern(2000)[..count], Bytes(r, Buf, count));
        Assert.Equal($"READ 1 {count} 4", mli.Calls[1]);
    }

    [Fact]
    public void Read_at_the_end_returns_the_count_read_and_status_eof()
    {
        var mli = new FakeMli();
        mli.Files["SCORES"] = new DiskFile { Bytes = Pattern(10) };
        var r = Go("""
            io.open(addr(NAME), len(NAME), io.READ)
            n = io.read(BUF as ptr u8, 300)
            st = io.status()
            results[0] = io.read(BUF as ptr u8, 5) as u8
            results[1] = io.status()
            """, mli);
        Assert.Equal(10, r.U16("n"));
        Assert.Equal(Eof, r.U8("st"));
        Assert.Equal(0, r.U8("results"));
        Assert.Equal(Eof, r.U8("results", 1));
        Assert.Equal(Pattern(10), Bytes(r, Buf, 10));
    }

    [Fact]
    public void Read_of_0_bytes_makes_no_call()
    {
        var mli = new FakeMli();
        mli.Files["SCORES"] = new DiskFile { Bytes = Pattern(10) };
        var r = Go("io.open(addr(NAME), len(NAME), io.READ)\n n = io.read(BUF as ptr u8, 0)\n failed = io.write(BUF as ptr u8, 0)", mli);
        Assert.Equal(0, r.U16("n"));
        Assert.Equal(0, r.U8("failed"));
        Assert.Equal(["OPEN SCORES 3"], mli.Calls);
    }

    [Fact]
    public void A_read_error_returns_0()
    {
        var mli = new FakeMli { FailRead = 0x27 };
        mli.Files["SCORES"] = new DiskFile { Bytes = Pattern(10) };
        var r = Go("io.open(addr(NAME), len(NAME), io.READ)\n n = io.read(BUF as ptr u8, 5)\n st = io.status()", mli);
        Assert.Equal(0, r.U16("n"));
        Assert.Equal(0x27, r.U8("st"));
    }

    [Fact]
    public void Write_appends_to_the_open_file()
    {
        var mli = new FakeMli();
        var r = Go("""
            io.open(addr(NAME), len(NAME), io.WRITE)
            io.write(BUF as ptr u8, 300)
            failed = io.write((BUF + 300) as ptr u8, 200)
            io.close()
            """, mli, Pattern(500));
        Assert.Equal(0, r.U8("failed"));
        Assert.Equal(Pattern(500), mli.Files["SCORES"].Bytes);
        Assert.Contains("WRITE 1 300 4", mli.Calls);
        Assert.Equal("CLOSE 1", mli.Calls[^1]);
    }

    [Fact]
    public void Close_with_no_file_open_makes_no_call()
    {
        // CLOSE with reference 0 would close every open file, also those the program opened itself.
        var mli = new FakeMli();
        var r = Go("failed = io.close()\n st = io.status()\n results[0] = io.close() as u8", mli);
        Assert.Equal(0, r.U8("failed"));
        Assert.Equal(0, r.U8("st"));
        Assert.Empty(mli.Calls);
    }

    [Fact]
    public void A_failed_close_keeps_the_file_to_close_again()
    {
        var mli = new FakeMli();
        mli.Files["SCORES"] = new DiskFile { Bytes = [1] };
        var r = Go("io.open(addr(NAME), len(NAME), io.READ)\n failed = io.close()\n st = io.status()", mli);
        Assert.Equal(0, r.U8("failed"));
        Assert.Empty(mli.Open);
        var again = new FakeMli { FailClose = 0x27 };
        again.Files["SCORES"] = new DiskFile { Bytes = [1] };
        r = Go("io.open(addr(NAME), len(NAME), io.READ)\n failed = io.close()\n st = io.status()\n results[0] = io.close() as u8", again);
        Assert.Equal(1, r.U8("failed"));
        Assert.Equal(0x27, r.U8("st"));
        Assert.Equal(["OPEN SCORES 3", "CLOSE 1", "CLOSE 1"], again.Calls);
    }

    [Fact]
    public void A_second_open_without_a_close_fails_on_the_buffer_in_use()
    {
        var mli = new FakeMli();
        mli.Files["SCORES"] = new DiskFile { Bytes = [1] };
        var r = Go("io.open(addr(NAME), len(NAME), io.READ)\n failed = io.open(addr(NAME), len(NAME), io.READ)\n st = io.status()", mli);
        Assert.Equal(1, r.U8("failed"));
        Assert.Equal(BadBuffer, r.U8("st"));
    }

    // ---------- Whole files ----------

    [Theory]
    [InlineData(1)]
    [InlineData(256)]
    [InlineData(700)]
    public void Save_then_load_gives_the_same_bytes(int count)
    {
        var mli = new FakeMli();
        var r = Go($$"""
            failed = io.save(addr(NAME), len(NAME), BUF as ptr u8, {{count}})
            results[0] = io.status()
            results[1] = io.load(addr(NAME), len(NAME), (BUF + $1000) as ptr u8, {{count}}) as u8
            results[2] = io.status()
            """, mli, Pattern(count));
        Assert.Equal(0, r.U8("failed"));
        Assert.Equal(0, r.U8("results"));
        Assert.Equal(0, r.U8("results", 1));
        Assert.Equal(0, r.U8("results", 2));
        Assert.Equal(Pattern(count), mli.Files["SCORES"].Bytes);
        Assert.Equal(Pattern(count), Bytes(r, Buf + 0x1000, count));
        Assert.Empty(mli.Open);
    }

    [Fact]
    public void Save_makes_a_bin_file_with_the_source_address_as_its_aux_type()
    {
        var mli = new FakeMli();
        mli.Files["SCORES"] = new DiskFile { Bytes = Pattern(900), Type = 0x04, Aux = 0x1234 };
        var r = Go("failed = io.save(addr(NAME), len(NAME), BUF as ptr u8, 16)", mli, Pattern(16, 1));
        Assert.Equal(0, r.U8("failed"));
        Assert.Equal(Pattern(16, 1), mli.Files["SCORES"].Bytes);
        Assert.Equal(0x06, mli.Files["SCORES"].Type);
        Assert.Equal(Buf, mli.Files["SCORES"].Aux);
        Assert.Equal(["GET_FILE_INFO SCORES 10", "SET_FILE_INFO SCORES 7 type $06 aux $6000"], mli.Calls[^2..]);
    }

    [Fact]
    public void Load_of_a_shorter_file_is_no_error()
    {
        var mli = new FakeMli();
        mli.Files["SCORES"] = new DiskFile { Bytes = Pattern(5) };
        var r = Go("failed = io.load(addr(NAME), len(NAME), BUF as ptr u8, 100)\n st = io.status()", mli, Enumerable.Repeat((byte)0xEE, 8).ToArray());
        Assert.Equal(0, r.U8("failed"));
        Assert.Equal(0, r.U8("st"));
        Assert.Equal([.. Pattern(5), 0xEE, 0xEE, 0xEE], Bytes(r, Buf, 8));
    }

    [Fact]
    public void Load_reads_at_most_max_bytes()
    {
        var mli = new FakeMli();
        mli.Files["SCORES"] = new DiskFile { Bytes = Pattern(50) };
        var r = Go("failed = io.load(addr(NAME), len(NAME), BUF as ptr u8, 20)", mli, Enumerable.Repeat((byte)0xEE, 30).ToArray());
        Assert.Equal(0, r.U8("failed"));
        Assert.Equal(Pattern(50)[..20], Bytes(r, Buf, 20));
        Assert.Equal(0xEE, r.Ram.Bytes[Buf + 20]);
    }

    [Fact]
    public void Load_of_a_missing_file_fails_and_leaves_nothing_open()
    {
        var mli = new FakeMli();
        var r = Go("failed = io.load(addr(NAME), len(NAME), BUF as ptr u8, 16)\n st = io.status()", mli);
        Assert.Equal(1, r.U8("failed"));
        Assert.Equal(NotFound, r.U8("st"));
        Assert.Equal(["OPEN SCORES 3"], mli.Calls);
        Assert.Empty(mli.Open);
    }

    [Fact]
    public void A_read_error_in_load_closes_and_keeps_the_error()
    {
        var mli = new FakeMli { FailRead = 0x27 };
        mli.Files["SCORES"] = new DiskFile { Bytes = Pattern(5) };
        var r = Go("failed = io.load(addr(NAME), len(NAME), BUF as ptr u8, 16)\n st = io.status()", mli);
        Assert.Equal(1, r.U8("failed"));
        Assert.Equal(0x27, r.U8("st"));
        Assert.Equal("CLOSE 1", mli.Calls[^1]);
        Assert.Empty(mli.Open);
    }

    [Fact]
    public void A_full_disk_in_save_closes_and_keeps_the_error()
    {
        var mli = new FakeMli { DiskSpace = 100 };
        var r = Go("failed = io.save(addr(NAME), len(NAME), BUF as ptr u8, 300)\n st = io.status()", mli, Pattern(300));
        Assert.Equal(1, r.U8("failed"));
        Assert.Equal(DiskFull, r.U8("st"));
        Assert.Equal("CLOSE 1", mli.Calls[^1]);
        Assert.Empty(mli.Open);
    }

    // ---------- load_bin ----------

    [Fact]
    public void Load_bin_loads_a_bin_file_at_its_aux_type()
    {
        var mli = new FakeMli();
        mli.Files["LEVEL1.BIN"] = new DiskFile { Bytes = Pattern(600), Aux = 0x7000 };
        var r = Go("failed = io.load_bin(addr(LEVEL), len(LEVEL))\n st = io.status()", mli);
        Assert.Equal(0, r.U8("failed"));
        Assert.Equal(0, r.U8("st"));
        Assert.Equal(Pattern(600), Bytes(r, 0x7000, 600));
        Assert.Equal(["GET_FILE_INFO LEVEL1.BIN 10", "OPEN LEVEL1.BIN 3", "GET_EOF 1 2", "READ 1 600 4", "CLOSE 1"], mli.Calls);
    }

    [Theory]
    [InlineData(0x04, 0x7000)]      // a text file
    [InlineData(0x06, 0)]           // BIN with no load address
    public void Load_bin_fails_on_a_file_that_is_not_bin_or_has_aux_type_0(int type, int aux)
    {
        var mli = new FakeMli();
        mli.Files["LEVEL1.BIN"] = new DiskFile { Bytes = Pattern(10), Type = type, Aux = aux };
        var r = Go("failed = io.load_bin(addr(LEVEL), len(LEVEL))\n st = io.status()", mli);
        Assert.Equal(1, r.U8("failed"));
        Assert.Equal(Format, r.U8("st"));
        Assert.Equal(["GET_FILE_INFO LEVEL1.BIN 10"], mli.Calls);
    }

    [Fact]
    public void Load_bin_of_a_missing_file_fails_with_not_found()
    {
        var mli = new FakeMli();
        var r = Go("failed = io.load_bin(addr(LEVEL), len(LEVEL))\n st = io.status()", mli);
        Assert.Equal(1, r.U8("failed"));
        Assert.Equal(NotFound, r.U8("st"));
        Assert.Empty(mli.Open);
    }

    [Fact]
    public void Load_bin_fails_when_the_file_ends_early()
    {
        var mli = new FakeMli { FailRead = 0x27 };
        mli.Files["LEVEL1.BIN"] = new DiskFile { Bytes = Pattern(10), Aux = 0x7000 };
        var r = Go("failed = io.load_bin(addr(LEVEL), len(LEVEL))\n st = io.status()", mli);
        Assert.Equal(1, r.U8("failed"));
        Assert.Equal(0x27, r.U8("st"));
        Assert.Equal("CLOSE 1", mli.Calls[^1]);
        Assert.Empty(mli.Open);
    }

    [Fact]
    public void The_buffer_is_page_aligned_in_the_report()
    {
        var result = TernCompiler.Compile("import io\ndata NAME = \"SCORES\"\nvar failed: bool\nfn main() { failed = io.open(addr(NAME), len(NAME), io.READ) }", Platform.Apple2);
        int buffer = result.Symbols["io.buffer"];
        Assert.Equal(0, buffer % 256);
        Assert.Contains($"Aligned io.buffer: ${buffer:X4} (@ align(256)), ", result.Report);
    }

    // ---------- The startup's prefix ----------

    /// <summary>Runs a program whose startup finds <paramref name="prefix"/> as the prefix and <paramref name="started"/> at $0280.</summary>
    private static (Run Run, List<string> Prefixes) StartWith(string prefix, string started)
    {
        var set = new List<string>();
        void Mli(Run run)
        {
            var b = run.Ram.Bytes;
            int sp = 0x100 + (byte)(run.Cpu.SP + 1);
            int at = (b[sp] | b[sp + 1] << 8) + 1;
            int command = b[at], list = b[at + 1] | b[at + 2] << 8, path = b[list + 1] | b[list + 2] << 8;
            (b[sp], b[sp + 1]) = ((byte)(at + 2), (byte)(at + 2 >> 8));
            if (command == 0xC7)
            {
                b[path] = (byte)prefix.Length;
                for (int i = 0; i < prefix.Length; i++) b[path + 1 + i] = (byte)prefix[i];
            }
            else if (command == 0xC6) set.Add(new([.. Enumerable.Range(1, b[path]).Select(i => (char)b[path + i])]));
            else throw new InvalidOperationException($"MLI ${command:X2}");
            run.Cpu.A = 0;
            run.Cpu.C = false;
        }
        var run = Run.Program("var x: u8\nfn main() { x = 1 }", platform: Platform.Apple2,
            traps: new Dictionary<int, Action<Run>> { [0xBF00] = Mli },
            setup: ram =>
            {
                ram.Bytes[0x280] = (byte)started.Length;
                for (int i = 0; i < started.Length; i++) ram.Bytes[0x281 + i] = (byte)started[i];
            });
        return (run, set);
    }

    [Fact]
    public void An_empty_prefix_becomes_the_folder_of_the_programs_path()
    {
        var (run, set) = StartWith("", "/GAMES/TERN/HELLO.SYSTEM");
        Assert.Equal(1, run.U8("x"));
        Assert.Equal(["/GAMES/TERN/"], set);
    }

    [Fact]
    public void A_prefix_that_is_set_is_left_alone()
    {
        var (run, set) = StartWith("/GAMES/", "HELLO.SYSTEM");
        Assert.Equal(1, run.U8("x"));
        Assert.Empty(set);
    }

    [Fact]
    public void An_empty_prefix_and_a_partial_path_leave_the_prefix_empty()
    {
        var (run, set) = StartWith("", "HELLO.SYSTEM");
        Assert.Equal(1, run.U8("x"));
        Assert.Empty(set);
    }

    // ---------- The example ----------

    [Fact]
    public void Files_example_saves_loads_back_and_loads_its_overlay()
    {
        string path = AsmTests.FindExample("files.tern", "apple2");
        var compiled = TernCompiler.Compile(File.ReadAllText(path), Platform.Apple2, sourcePath: path);
        var greet = compiled.Overlays.Single();
        Assert.Equal("greet", greet.Name);
        Assert.Equal(0x6000, greet.LoadAddress);
        var mli = new FakeMli();
        mli.Files["GREET.BIN"] = new DiskFile { Bytes = greet.File, Aux = greet.LoadAddress };
        var printed = new System.Text.StringBuilder();
        var traps = new Dictionary<int, Action<Run>>(mli.Traps)
        {
            [0xFDED] = run => printed.Append(run.Cpu.A == 0x8D ? '\n' : (char)(run.Cpu.A & 0x7F)),
        };
        var r = Run.Program(File.ReadAllText(path), sourcePath: path, platform: Platform.Apple2, traps: traps);
        Assert.Equal("SAVED SCORES\nLOADED 0A 14 1E 28 FF 00 C8 9B\nSAME BYTES\nHELLO FROM AN OVERLAY\n", printed.ToString());
        Assert.Equal(new byte[] { 0x0A, 0x14, 0x1E, 0x28, 0xFF, 0x00, 0xC8, 0x9B }, mli.Files["SCORES"].Bytes);
        Assert.Equal(r.Address("scores"), mli.Files["SCORES"].Aux);
        Assert.Empty(mli.Open);
    }

    // ---------- tern run ----------

    [Fact]
    public void Tern_run_loads_the_builds_own_overlay_with_load_bin()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "level1.tern"), "var value: u8 = 42\nfn get() -> u8 using A { return value }\n");
            File.WriteAllText(Path.Combine(dir.FullName, "main.tern"), """
                import io
                import level1 @ levels
                memory {
                    $2000..=$5FFF  program
                    $6000..=$6FFF  overlay levels
                }
                data GOOD = "LEVEL1.BIN"
                data FULL = "/DISK/LEVEL1.BIN"
                data MISSING = "LEVEL2.BIN"
                data BAD = "1LEVEL.BIN"
                var results: [u8; 6]
                fn main() {
                    results[0] = io.load_bin(addr(GOOD), len(GOOD)) as u8
                    results[1] = level1.get()
                    results[2] = io.load_bin(addr(FULL), len(FULL)) as u8
                    io.load_bin(addr(MISSING), len(MISSING))
                    results[3] = io.status()
                    io.load_bin(addr(BAD), len(BAD))
                    results[4] = io.status()
                }
                """);
            string main = Path.Combine(dir.FullName, "main.tern");
            var compiled = TernCompiler.Compile(File.ReadAllText(main), Platform.Apple2, sourcePath: main);
            var run = Runner.Run(compiled);
            Assert.True(run.Returned, run.Stop);
            int results = compiled.Symbols["results"];
            Assert.Equal([0, 42, 0, NotFound, 0x40], run.Ram.Bytes[results..(results + 5)]);
        }
        finally { dir.Delete(recursive: true); }
    }
}
