using System.Text;

namespace Tern.Compiler.Tests;

/// <summary>The C64 device I/O library (lib/c64/io.tern), run against stand-ins for the KERNAL.</summary>
public class IoTests
{
    private const int Eof = 0x40;

    /// <summary>A KERNAL and a disk drive in C#: records every call, keeps files by name, and answers like the real thing.</summary>
    private sealed class FakeKernal
    {
        public readonly List<string> Calls = [];
        public readonly Dictionary<string, byte[]> Files = [];
        public string Name = "";
        public int Lfn = -1, Device = -1, Channel = -1;
        public int LoadA = -1, LoadX = -1, LoadY = -1, SaveStart = -1, SaveEnd = -1, SaveA = -1;
        public bool Fail;                       // OPEN, CHKIN, CHKOUT, LOAD and SAVE set the carry
        public byte Status;
        public byte StatusOnRead;               // bits ORed into the status by every CHRIN
        public string DriveMessage = "00, OK,00,00";
        public readonly List<byte> Written = [];
        public readonly List<byte> Screen = [];
        public readonly List<string> Commands = [];
        public IEnumerable<int> OpenFiles => _open.Keys;

        private readonly Dictionary<int, (string Name, int Pos)> _open = [];
        private readonly List<byte> _command = [];
        private int _in = -1, _out = -1;

        public IReadOnlyDictionary<int, Action<Run>> Traps => new Dictionary<int, Action<Run>>
        {
            [0xFFBD] = run =>
            {
                int at = run.Cpu.X | run.Cpu.Y << 8;
                Name = Encoding.Latin1.GetString(run.Ram.Bytes, at, run.Cpu.A);
                Calls.Add($"SETNAM {Name}");
            },
            [0xFFBA] = run =>
            {
                (Lfn, Device, Channel) = (run.Cpu.A, run.Cpu.X, run.Cpu.Y);
                Calls.Add($"SETLFS {Lfn},{Device},{Channel}");
            },
            [0xFFC0] = run =>
            {
                Calls.Add("OPEN");
                run.Cpu.C = Fail;
                if (Fail) return;
                Status = 0;
                if (Lfn == 15)
                {
                    _open[15] = ("", 0);
                    return;
                }
                bool write = Name.EndsWith(",S,W");
                var file = Name.Split(',')[0];
                if (write) Files[file] = [];
                else if (!Files.ContainsKey(file)) DriveMessage = "62, FILE NOT FOUND,00,00";   // as on a 1541: neither carry nor status shows it yet
                _open[Lfn] = (file, 0);
            },
            [0xFFC3] = run =>
            {
                Calls.Add($"CLOSE {run.Cpu.A}");
                if (run.Cpu.A == 15) _open.Clear();                         // closing the command channel closes every file on the drive
                else _open.Remove(run.Cpu.A);
            },
            [0xFFC6] = run =>
            {
                Calls.Add($"CHKIN {run.Cpu.X}");
                run.Cpu.C = Fail || !_open.ContainsKey(run.Cpu.X);
                _in = run.Cpu.X;
            },
            [0xFFC9] = run =>
            {
                Calls.Add($"CHKOUT {run.Cpu.X}");
                run.Cpu.C = Fail || !_open.ContainsKey(run.Cpu.X);
                _out = run.Cpu.X;
            },
            [0xFFCC] = run => { Calls.Add("CLRCHN"); _in = _out = -1; },
            [0xFFCF] = run =>
            {
                var (name, pos) = _open[_in];
                if (_in != 15 && !Files.ContainsKey(name))
                {
                    run.Cpu.A = 0;                                          // a missing file: end of file and timeout at the first byte
                    Status = (byte)(Eof | 0x02 | StatusOnRead);
                    return;
                }
                byte[] data = _in == 15 ? Encoding.Latin1.GetBytes(DriveMessage + "\r") : Files[name];
                run.Cpu.A = data[pos];
                _open[_in] = (name, _in == 15 && pos + 1 == data.Length ? 0 : pos + 1);
                Status = (byte)((pos + 1 == data.Length ? Eof : 0) | StatusOnRead);
            },
            [0xFFD2] = run =>
            {
                if (_out == -1) { Screen.Add(run.Cpu.A); return; }       // the default output: the screen
                if (_out == 15)
                {
                    if (run.Cpu.A != 13) _command.Add(run.Cpu.A);
                    else
                    {
                        var text = Encoding.Latin1.GetString([.. _command]);
                        Commands.Add(text);
                        _command.Clear();
                        if (text.StartsWith("S0:") && Files.Remove(text[3..])) DriveMessage = "01, FILES SCRATCHED,01,00";
                    }
                    return;
                }
                Status = 0;                                                 // a successful send clears the status, as on a real bus
                Written.Add(run.Cpu.A);
                var name = _open[_out].Name;
                Files[name] = [.. Files[name], run.Cpu.A];
            },
            [0xFFB7] = run => run.Cpu.A = Status,
            [0xFFD5] = run =>
            {
                Calls.Add("LOAD");
                (LoadA, LoadX, LoadY) = (run.Cpu.A, run.Cpu.X, run.Cpu.Y);
                if (Fail || !Files.TryGetValue(Name, out var data)) { run.Cpu.C = true; return; }
                run.Cpu.C = false;
                int at = run.Cpu.X | run.Cpu.Y << 8;                        // secondary address 0: the caller's address
                data.CopyTo(run.Ram.Bytes, at);
            },
            [0xFFD8] = run =>
            {
                Calls.Add("SAVE");
                SaveA = run.Cpu.A;
                SaveStart = run.Ram.Bytes[run.Cpu.A] | run.Ram.Bytes[run.Cpu.A + 1] << 8;
                SaveEnd = run.Cpu.X | run.Cpu.Y << 8;
                run.Cpu.C = Fail;
                if (Fail) return;
                Files[Name.StartsWith("@0:") ? Name[3..] : Name] = [.. run.Ram.Bytes.Skip(SaveStart).Take((SaveEnd - SaveStart) & 0xFFFF)];
            },
        };
    }

    private static Run Go(string body, FakeKernal k) => Run.Program($$"""
        import io
        data NAME = "SCORES"
        data WNAME = "SCORES,S,W"
        data RNAME = "SCORES,S,R"
        data SCRATCH = "S0:SCORES"
        var buf: [u8; 20]
        var failed: bool
        var n: u8
        var st: u8
        fn main() {
            {{body}}
        }
        """, traps: k.Traps);

    private static byte[] Bytes(Run r, string name, int n) => [.. r.Ram.Bytes.Skip(r.Address(name)).Take(n)];

    // ---------- Whole files ----------

    [Trait("Platform", "c64")]
    [Fact]
    public void Load_sets_the_name_and_the_device_and_loads_to_the_given_address()
    {
        var k = new FakeKernal();
        k.Files["SCORES"] = [1, 2, 3, 4, 5];
        var r = Go("failed = io.load(addr(NAME), len(NAME), 9, addr(buf))", k);
        Assert.False(r.U8("failed") != 0);
        Assert.Equal(["SETNAM SCORES", "SETLFS 1,9,0", "LOAD"], k.Calls);
        Assert.Equal(0, k.LoadA);                                   // load, not verify
        Assert.Equal(r.Address("buf"), k.LoadX | k.LoadY << 8);     // the address, in X and Y
        Assert.Equal([1, 2, 3, 4, 5], Bytes(r, "buf", 5));
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Load_returns_the_carry()
    {
        var k = new FakeKernal();
        var r = Go("failed = io.load(addr(NAME), len(NAME), 8, addr(buf))", k);   // no such file
        Assert.True(r.U8("failed") != 0);
        k.Files["SCORES"] = [0];
        r = Go("failed = io.load(addr(NAME), len(NAME), 8, addr(buf))", k);
        Assert.False(r.U8("failed") != 0);
    }

    [Trait("Platform", "c64")]
    [Theory]
    [InlineData(0x2000, 0x2000)]          // one byte
    [InlineData(0x2000, 0x20FF)]
    [InlineData(0x20FF, 0x2100)]          // the end address crosses a page
    [InlineData(0x2000, 0x2200)]
    [InlineData(0x2000, 0x2FFF)]
    public void Save_passes_the_start_through_zero_page_and_the_end_plus_one(int first, int last)
    {
        var k = new FakeKernal();
        var r = Go($"failed = io.save(addr(NAME), len(NAME), 8, {first}, {last})", k);
        Assert.False(r.U8("failed") != 0);
        Assert.Equal(["SETNAM SCORES", "SETLFS 1,8,1", "SAVE"], k.Calls);
        Assert.Equal(first, k.SaveStart);
        Assert.Equal(last + 1, k.SaveEnd);
        Assert.True(k.SaveA < 256);                                 // a zero-page address
        Assert.Equal(last - first + 1, k.Files["SCORES"].Length);
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Save_returns_the_carry()
    {
        var r = Go("failed = io.save(addr(NAME), len(NAME), 8, 4096, 4099)", new FakeKernal { Fail = true });
        Assert.True(r.U8("failed") != 0);
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void A_saved_file_loads_back()
    {
        var k = new FakeKernal();
        var r = Go("""
            buf[0] = 7
            buf[1] = 250
            buf[2] = 0
            buf[3] = 255
            failed = io.save(addr(NAME), len(NAME), 8, addr(buf), addr(buf) + 3)
            buf[0] = 0
            buf[1] = 0
            buf[2] = 0
            buf[3] = 0
            failed = failed || io.load(addr(NAME), len(NAME), 8, addr(buf))
            """, k);
        Assert.False(r.U8("failed") != 0);
        Assert.Equal([7, 250, 0, 255], Bytes(r, "buf", 4));
    }

    // ---------- Sequential files ----------

    [Trait("Platform", "c64")]
    [Fact]
    public void Open_sets_the_name_then_the_file_then_opens()
    {
        var k = new FakeKernal();
        var r = Go("failed = io.open(3, 8, 4, addr(WNAME), len(WNAME))", k);
        Assert.False(r.U8("failed") != 0);
        Assert.Equal(["SETNAM SCORES,S,W", "SETLFS 3,8,4", "OPEN"], k.Calls);
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Open_returns_the_carry()
    {
        var r = Go("failed = io.open(3, 8, 4, addr(WNAME), len(WNAME))", new FakeKernal { Fail = true });
        Assert.True(r.U8("failed") != 0);
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Write_selects_the_channel_sends_every_byte_and_restores_the_default_channels()
    {
        var k = new FakeKernal();
        var r = Go("""
            buf[0] = 10
            buf[1] = 0
            buf[2] = 255
            buf[3] = 13
            failed = io.open(2, 8, 2, addr(WNAME), len(WNAME))
            failed = failed || io.write(2, addr(buf), 4)
            io.close(2)
            """, k);
        Assert.False(r.U8("failed") != 0);
        Assert.Equal(["SETNAM SCORES,S,W", "SETLFS 2,8,2", "OPEN", "CHKOUT 2", "CLRCHN", "CLOSE 2"], k.Calls);
        Assert.Equal([10, 0, 255, 13], k.Files["SCORES"]);
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Write_of_nothing_sends_nothing()
    {
        var k = new FakeKernal();
        var r = Go("""
            failed = io.open(2, 8, 2, addr(WNAME), len(WNAME))
            failed = io.write(2, addr(buf), 0)
            """, k);
        Assert.False(r.U8("failed") != 0);
        Assert.Empty(k.Written);
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Write_to_a_file_that_is_not_open_returns_true_without_sending()
    {
        var k = new FakeKernal();
        var r = Go("failed = io.write(5, addr(buf), 3)", k);
        Assert.True(r.U8("failed") != 0);                           // CHKOUT's carry
        Assert.Empty(k.Written);
        Assert.Equal(["CHKOUT 5"], k.Calls);
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Write_returns_true_when_the_status_shows_an_error()
    {
        var k = new FakeKernal();
        var traps = k.Traps.ToDictionary(t => t.Key, t => t.Value);
        var chrout = traps[0xFFD2];
        traps[0xFFD2] = run => { chrout(run); k.Status = 0x80; };   // the device drops out
        var r = Run.Program("""
            import io
            data WNAME = "SCORES,S,W"
            var buf: [u8; 4]
            var failed: bool
            fn main() {
                io.open(2, 8, 2, addr(WNAME), len(WNAME))
                failed = io.write(2, addr(buf), 3)
            }
            """, traps: traps);
        Assert.True(r.U8("failed") != 0);
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Read_stops_at_the_end_of_the_file_and_the_status_shows_it()
    {
        var k = new FakeKernal();
        k.Files["SCORES"] = [9, 8, 7, 6, 5];
        var r = Go("""
            failed = io.open(2, 8, 2, addr(RNAME), len(RNAME))
            n = io.read(2, addr(buf), 20)
            st = io.status()
            io.close(2)
            """, k);
        Assert.Equal(5, r.U8("n"));
        Assert.Equal(Eof, r.U8("st"));
        Assert.Equal([9, 8, 7, 6, 5, 0], Bytes(r, "buf", 6));
        Assert.Equal(["SETNAM SCORES,S,R", "SETLFS 2,8,2", "OPEN", "CHKIN 2", "CLRCHN", "CLOSE 2"], k.Calls);
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Read_stops_at_max_and_continues_where_it_left_off()
    {
        var k = new FakeKernal();
        k.Files["SCORES"] = [1, 2, 3, 4, 5, 6, 7];
        var r = Go("""
            failed = io.open(2, 8, 2, addr(RNAME), len(RNAME))
            n = io.read(2, addr(buf), 3)
            st = io.read(2, addr(buf) + 3, 3)
            """, k);
        Assert.Equal(3, r.U8("n"));
        Assert.Equal(3, r.U8("st"));
        Assert.Equal([1, 2, 3, 4, 5, 6, 0], Bytes(r, "buf", 7));
        Assert.Equal(0, k.Status);                                  // not at the end yet
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Read_of_zero_bytes_and_of_a_file_that_is_not_open()
    {
        var k = new FakeKernal();
        k.Files["SCORES"] = [1];
        var r = Go("""
            failed = io.open(2, 8, 2, addr(RNAME), len(RNAME))
            n = io.read(2, addr(buf), 0)
            st = io.read(6, addr(buf), 5)
            """, k);
        Assert.Equal(0, r.U8("n"));
        Assert.Equal(0, r.U8("st"));                                // CHKIN failed: nothing read
        Assert.Equal(0, r.U8("buf"));
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Read_stops_on_a_timeout_without_counting_the_byte()
    {
        var k = new FakeKernal { StatusOnRead = 0x02 };             // a device that times out
        k.Files["SCORES"] = [1, 2, 3, 4];
        var r = Go("""
            failed = io.open(2, 8, 2, addr(RNAME), len(RNAME))
            n = io.read(2, addr(buf), 4)
            """, k);
        Assert.Equal(0, r.U8("n"));
        Assert.Equal(0, r.U8("buf"));                               // and no junk byte stored
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void A_missing_file_gives_no_error_at_open_and_no_bytes_at_read()
    {
        var k = new FakeKernal();
        var r = Go("""
            failed = io.open(2, 8, 2, addr(RNAME), len(RNAME))
            st = io.status()
            n = io.read(2, addr(buf), 40)
            """, k);
        Assert.False(r.U8("failed") != 0);
        Assert.Equal(0, r.U8("st"));
        Assert.Equal(0, r.U8("n"));
        Assert.Equal(0, r.U8("buf"));
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void A_missing_file_shows_in_the_status_message()
    {
        var k = new FakeKernal();
        var r = Go("""
            failed = io.command_open(8)
            failed = failed || io.open(2, 8, 2, addr(RNAME), len(RNAME))
            n = io.drive_status(addr(buf), 40)
            """, k);
        Assert.Equal("62, FILE NOT FOUND,00,00", Encoding.Latin1.GetString(Bytes(r, "buf", r.U8("n"))));
    }

    // ---------- Command channel ----------

    [Trait("Platform", "c64")]
    [Fact]
    public void Command_open_opens_channel_15_without_a_name_and_close_closes_it()
    {
        var k = new FakeKernal();
        var r = Go("""
            failed = io.command_open(9)
            io.command_close()
            """, k);
        Assert.False(r.U8("failed") != 0);
        Assert.Equal(["SETNAM ", "SETLFS 15,9,15", "OPEN", "CLOSE 15"], k.Calls);
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Command_open_returns_the_carry()
    {
        var r = Go("failed = io.command_open(8)", new FakeKernal { Fail = true });
        Assert.True(r.U8("failed") != 0);
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Command_sends_the_text_and_a_carriage_return_on_channel_15()
    {
        var k = new FakeKernal();
        k.Files["SCORES"] = [1];
        var r = Go("""
            failed = io.command_open(8)
            failed = failed || io.command(addr(SCRATCH), len(SCRATCH))
            n = io.drive_status(addr(buf), 40)
            io.command_close()
            """, k);
        Assert.False(r.U8("failed") != 0);
        Assert.Equal(["S0:SCORES"], k.Commands);
        Assert.Equal(["SETNAM ", "SETLFS 15,8,15", "OPEN", "CHKOUT 15", "CLRCHN", "CHKIN 15", "CLRCHN", "CLOSE 15"], k.Calls);
        Assert.Equal("01, FILES SCRATCHED,01,00", Encoding.Latin1.GetString(Bytes(r, "buf", r.U8("n"))));
        Assert.False(k.Files.ContainsKey("SCORES"));
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Command_returns_true_when_the_channel_is_not_open()
    {
        var k = new FakeKernal();
        var r = Go("failed = io.command(addr(SCRATCH), len(SCRATCH))", k);
        Assert.True(r.U8("failed") != 0);
        Assert.Empty(k.Commands);
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Drive_status_leaves_the_channel_and_the_other_files_open()
    {
        var k = new FakeKernal();
        var r = Go("""
            failed = io.command_open(8)
            failed = failed || io.open(5, 8, 5, addr(WNAME), len(WNAME))
            n = io.drive_status(addr(buf), 40)
            failed = failed || io.write(5, addr(buf), 10)
            io.close(5)
            io.command_close()
            """, k);
        Assert.False(r.U8("failed") != 0);                          // the documented sequence works
        Assert.Equal(10, k.Files["SCORES"].Length);
        Assert.Equal(1, k.Calls.Count(c => c == "CLOSE 15"));       // only the last call closes the channel
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Closing_the_command_channel_closes_the_other_files_too()
    {
        var k = new FakeKernal();
        var r = Go("""
            failed = io.command_open(8)
            failed = failed || io.open(5, 8, 5, addr(WNAME), len(WNAME))
            io.command_close()
            failed = io.write(5, addr(buf), 3)
            """, k);
        Assert.Empty(k.OpenFiles);
        Assert.True(r.U8("failed") != 0);                           // the file is gone: CHKOUT fails
        Assert.Empty(k.Files["SCORES"]);
    }

    [Trait("Platform", "c64")]
    [Theory]
    [InlineData("00, OK,00,00", 20, 12)]
    [InlineData("62, FILE NOT FOUND,00,00", 20, 20)]            // cut at max
    [InlineData("73,CBM DOS V2.6 1541,00,00", 3, 3)]
    [InlineData("", 20, 0)]                                     // the message is only a carriage return
    public void Drive_status_reads_the_message_without_the_carriage_return(string message, int max, int length)
    {
        var k = new FakeKernal { DriveMessage = message };
        var r = Go($"""
            failed = io.command_open(8)
            n = io.drive_status(addr(buf), {max})
            """, k);
        Assert.Equal(length, r.U8("n"));
        Assert.Equal(message[..length], Encoding.Latin1.GetString(Bytes(r, "buf", length)));
        Assert.Equal(["SETNAM ", "SETLFS 15,8,15", "OPEN", "CHKIN 15", "CLRCHN"], k.Calls);
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Drive_status_gives_zero_when_the_channel_is_not_open()
    {
        var k = new FakeKernal();
        var r = Go("n = io.drive_status(addr(buf), 20)", k);
        Assert.Equal(0, r.U8("n"));
        Assert.Equal(["CHKIN 15"], k.Calls);
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Drive_status_gives_zero_when_the_device_is_not_present()
    {
        var k = new FakeKernal { DriveMessage = "XX", StatusOnRead = 0x80 };
        var r = Go("""
            failed = io.command_open(9)
            n = io.drive_status(addr(buf), 20)
            """, k);
        Assert.Equal(0, r.U8("n"));
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Command_once_opens_sends_and_closes()
    {
        var k = new FakeKernal();
        k.Files["SCORES"] = [1];
        var r = Go("failed = io.command_once(8, addr(SCRATCH), len(SCRATCH))", k);
        Assert.False(r.U8("failed") != 0);
        Assert.Equal(["S0:SCORES"], k.Commands);
        Assert.Equal(["SETNAM ", "SETLFS 15,8,15", "OPEN", "CHKOUT 15", "CLRCHN", "CLOSE 15"], k.Calls);
    }

    [Trait("Platform", "c64")]
    [Fact]
    public void Command_once_does_not_close_a_channel_it_failed_to_open()
    {
        var k = new FakeKernal { Fail = true };                     // e.g. logical file 15 is already open
        var r = Go("failed = io.command_once(8, addr(SCRATCH), len(SCRATCH))", k);
        Assert.True(r.U8("failed") != 0);
        Assert.Equal(["SETNAM ", "SETLFS 15,8,15", "OPEN"], k.Calls);
    }

    // ---------- Example ----------

    [Trait("Platform", "c64")]
    [Fact]
    public void The_highscore_example_saves_loads_and_prints_the_status()
    {
        var path = AsmTests.FindExample("highscore.tern");
        var k = new FakeKernal();
        Run.Program(File.ReadAllText(path), sourcePath: path, traps: k.Traps);
        Assert.Equal("\u0093SAVED: HIGH SCORE 12340\rLOADED: HIGH SCORE 12340\rSTATUS: 00, OK,00,00", Encoding.Latin1.GetString([.. k.Screen]));
        Assert.Equal("HIGH SCORE 12340", Encoding.Latin1.GetString(k.Files["SCORES"]));
    }

    // ---------- Code shape ----------

    [Trait("Platform", "c64")]
    [Trait("Shape", "nmos6502")]
    [Fact]
    public void KERNAL_routines_are_direct_calls()
    {
        var r = Go("""
            io.setlfs(1, 8, 0)
            io.close(1)
            st = io.status()
            """, new FakeKernal());
        var main = Listing.Instructions(r);
        Assert.Contains("jsr $FFBA", main);
        Assert.Contains("jsr $FFC3", main);
        Assert.Contains("jsr $FFB7", main);
    }

    [Trait("Platform", "c64")]
    [Trait("Shape", "nmos6502")]
    [Fact]
    public void Unused_functions_cost_nothing()
    {
        var r = Go("n = 1", new FakeKernal());
        Assert.DoesNotContain(r.Result.Instructions, i => i.Text == "jsr $FFBD");
        Assert.DoesNotContain(Listing.Sections(r), s => s.StartsWith("io."));
        Assert.Equal(["lda #$01", "sta n", "rts"], Listing.Instructions(r));
    }
}
