using Tern.Cpu;

namespace Tern.Compiler.Tests;

/// <summary>lib/mem (spec 10.1). Cycle counts include the jsr and rts.</summary>
public class MemLibraryTests
{
    private static readonly Library Mem = new("import mem", """
                mem.copy(0 as ptr u8, 0 as ptr u8, 0)
                mem.fill(0 as ptr u8, 0, 0)
        """);

    private const int Dst = 0x4000;
    private const int Src = 0x6000;

    /// <summary>Fills $3E00-$7FFF with a pattern, so that stray writes show.</summary>
    private static void Pattern(Ram ram)
    {
        for (int a = 0x3E00; a < 0x8000; a++) ram.Bytes[a] = (byte)(a * 7 + 3);
    }

    private static readonly int[] Counts = [0, 1, 2, 255, 256, 257, 511, 512, 513, 1000];

    [Theory]
    [InlineData(Dst)]
    [InlineData(Dst + 0xFF)]
    public void Fill(int dst)
    {
        lock (Mem)
            foreach (int count in Counts)
            {
                Pattern(Mem.Ram);
                var before = Mem.Ram.Bytes.ToArray();
                Mem.Set16("mem.fill.dst", dst);
                Mem.Set8("mem.fill.value", 0xA5);
                Mem.Set16("mem.fill.count", count);
                Mem.Call("mem.fill");
                for (int a = 0x3E00; a < 0x8000; a++)
                {
                    int expected = a >= dst && a < dst + count ? 0xA5 : before[a];
                    if (Mem.Ram.Bytes[a] != expected) Assert.Fail($"fill count {count}: ${a:X4} = {Mem.Ram.Bytes[a]}");
                }
            }
    }

    [Theory]
    [InlineData(Dst, Src)]
    [InlineData(Dst + 0x80, Src + 0x01)]
    [InlineData(Dst, Dst + 0x10)]           // overlapping, dst below src
    public void Copy(int dst, int src)
    {
        lock (Mem)
            foreach (int count in Counts)
            {
                Pattern(Mem.Ram);
                var before = Mem.Ram.Bytes.ToArray();
                Mem.Set16("mem.copy.dst", dst);
                Mem.Set16("mem.copy.src", src);
                Mem.Set16("mem.copy.count", count);
                Mem.Call("mem.copy");
                for (int a = 0x3E00; a < 0x8000; a++)
                {
                    int expected = a >= dst && a < dst + count ? before[src + a - dst] : before[a];
                    if (Mem.Ram.Bytes[a] != expected) Assert.Fail($"copy count {count}: ${a:X4} = {Mem.Ram.Bytes[a]}, expected {expected}");
                }
            }
    }

    [Fact]
    public void Cycles()
    {
        lock (Mem)
        {
            Mem.Set16("mem.fill.dst", Dst);
            Mem.Set16("mem.fill.count", 1000);
            long fill = Mem.Call("mem.fill");
            Mem.Set16("mem.copy.dst", Dst);
            Mem.Set16("mem.copy.src", Src);
            Mem.Set16("mem.copy.count", 1000);
            long copy = Mem.Call("mem.copy");
            // About 15 cycles per byte for fill and 17 for copy (13 and 16 inside whole pages).
            Assert.Equal((14762, 16777), (fill, copy));
        }
    }
}

/// <summary>lib/text (spec 10.1). Digits are screen codes: '0'-'9' are $30-$39, 'A'-'F' are $01-$06.</summary>
public class TextLibraryTests
{
    private static readonly Library Text = new("import text", """
                text.put(0 as ptr u8, 0 as ptr u8, 0)
                text.dec8(0 as ptr u8, 0)
                text.dec16(0 as ptr u8, 0)
                text.hex8(0 as ptr u8, 0)
                text.hex16(0 as ptr u8, 0)
                text.equal(0 as ptr u8, 0 as ptr u8, 0)
                text.find(0 as ptr u8, 0, 0)
        """);

    private const int Dst = 0x4000;
    private const int Src = 0x6000;

    private static byte[] Screen(string s) =>
        s.Select(c => (byte)(c is >= 'A' and <= 'F' ? c - 'A' + 1 : c)).ToArray();

    private static (long Min, long Max) Range(IEnumerable<long> cycles) => (cycles.Min(), cycles.Max());

    /// <summary>Calls dec8/dec16/hex8/hex16 and checks the digits, the returned count, and the byte after them.</summary>
    private static long Digits(string function, int value, string expected, bool returnsCount)
    {
        Text.Ram.Bytes.AsSpan(Dst, 8).Fill(0xEE);
        Text.Set16($"text.{function}.dst", Dst);
        if (function.EndsWith("16")) Text.Set16($"text.{function}.value", value);
        else Text.Set8($"text.{function}.value", value);
        long cycles = Text.Call($"text.{function}");
        var written = Text.Ram.Bytes.AsSpan(Dst, expected.Length).ToArray();
        if (!written.SequenceEqual(Screen(expected)) || Text.Ram.Bytes[Dst + expected.Length] != 0xEE
            || (returnsCount && Text.Cpu.A != expected.Length))
            Assert.Fail($"{function}({value}) wrote {Convert.ToHexString(written)}, returned {Text.Cpu.A}; expected \"{expected}\"");
        return cycles;
    }

    [Fact]
    public void Dec8_for_every_value()
    {
        var cycles = new List<long>();
        lock (Text)
            for (int v = 0; v < 256; v++) cycles.Add(Digits("dec8", v, v.ToString(), returnsCount: true));
        Assert.Equal((63, 290), Range(cycles));
    }

    [Fact]
    public void Dec16_on_edge_values()
    {
        int[] values = [0, 1, 9, 10, 11, 99, 100, 101, 255, 256, 999, 1000, 1001, 9999, 10000, 10001, 32767, 32768, 59999, 60000, 65534, 65535];
        var cycles = new List<long>();
        lock (Text)
            foreach (int v in values.Concat(Enumerable.Range(0, 1772).Select(i => i * 37)))
                cycles.Add(Digits("dec16", v, v.ToString(), returnsCount: true));
        Assert.Equal((285, 1521), Range(cycles));
    }

    [Fact]
    public void Hex8_for_every_value()
    {
        var cycles = new List<long>();
        lock (Text)
            for (int v = 0; v < 256; v++) cycles.Add(Digits("hex8", v, v.ToString("X2"), returnsCount: false));
        Assert.Equal((56, 56), Range(cycles));
    }

    [Fact]
    public void Hex16_on_edge_values()
    {
        var cycles = new List<long>();
        lock (Text)
            foreach (int v in new[] { 0, 1, 0x0F, 0x10, 0xFF, 0x100, 0x1234, 0xABCD, 0x7FFF, 0x8000, 0xFFFF })
                cycles.Add(Digits("hex16", v, v.ToString("X4"), returnsCount: false));
        Assert.Equal((100, 100), Range(cycles));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(40)]
    [InlineData(255)]
    public void Put(int n)
    {
        lock (Text)
        {
            for (int i = 0; i < 300; i++)
            {
                Text.Ram.Bytes[Src + i] = (byte)(i * 3 + 1);
                Text.Ram.Bytes[Dst + i] = 0xEE;
            }
            Text.Set16("text.put.dst", Dst);
            Text.Set16("text.put.src", Src);
            Text.Set8("text.put.n", n);
            long cycles = Text.Call("text.put");
            Assert.Equal(Text.Ram.Bytes.AsSpan(Src, n).ToArray(), Text.Ram.Bytes.AsSpan(Dst, n).ToArray());
            Assert.Equal(0xEE, Text.Ram.Bytes[Dst + n]);
            if (n == 40) Assert.Equal(778, cycles);    // 19 per byte
        }
    }

    [Theory]
    [InlineData("HELLO", "HELLO", 5, true)]
    [InlineData("HELLO", "JELLO", 5, false)]
    [InlineData("HELLO", "HELLX", 5, false)]
    [InlineData("HELLO", "HELLX", 4, true)]
    [InlineData("A", "B", 0, true)]
    public void Equal(string a, string b, int n, bool expected)
    {
        lock (Text)
        {
            Text.Ram.Load(Dst, System.Text.Encoding.ASCII.GetBytes(a));
            Text.Ram.Load(Src, System.Text.Encoding.ASCII.GetBytes(b));
            Text.Set16("text.equal.a", Dst);
            Text.Set16("text.equal.b", Src);
            Text.Set8("text.equal.n", n);
            Text.Call("text.equal");
            Assert.Equal(expected, Text.Cpu.C);
        }
    }

    [Theory]
    [InlineData("HELLO", 5, 'H', 0)]
    [InlineData("HELLO", 5, 'L', 2)]
    [InlineData("HELLO", 5, 'O', 4)]
    [InlineData("HELLO", 4, 'O', 0xFF)]
    [InlineData("HELLO", 5, 'X', 0xFF)]
    [InlineData("HELLO", 0, 'H', 0xFF)]
    public void Find(string s, int n, char ch, int expected)
    {
        lock (Text)
        {
            Text.Ram.Load(Dst, System.Text.Encoding.ASCII.GetBytes(s));
            Text.Set16("text.find.s", Dst);
            Text.Set8("text.find.n", n);
            Text.Set8("text.find.ch", ch);
            Text.Call("text.find");
            Assert.Equal(expected, Text.Cpu.A);
        }
    }

    [Fact]
    public void Writing_a_number_to_the_screen()
    {
        var r = Run.Program("""
            import text
            data title = screen"SCORE "
            var score: u16 = 4711
            var n: u8
            fn main() {
                text.put($0400, addr(title), len(title))
                n = text.dec16($0400 + len(title), score)
            }
            """);
        Assert.Equal(4, r.U8("n"));
        Assert.Equal([0x13, 0x03, 0x0F, 0x12, 0x05, 0x20, 0x34, 0x37, 0x31, 0x31], r.Ram.Bytes.AsSpan(0x0400, 10).ToArray());
    }
}

/// <summary>lib/c64 (spec 10.1), with stand-ins for the KERNAL routines.</summary>
public class C64LibraryTests
{
    /// <summary>
    /// $FFD2 CHROUT: appends A to $C500 (count at $C5FF), keeps X and Y.
    /// $FFE4 GETIN: returns $41.
    /// $FFF0 PLOT: carry set loads X and Y from $C5F0/$C5F1; carry clear stores them there.
    /// </summary>
    private static void Kernal(Ram ram)
    {
        ram.Load(0xFFD2, [0x4C, 0x40, 0xC0]);   // jmp $C040
        ram.Load(0xC040, [
            0x8C, 0xFE, 0xC5,   // sty $C5FE
            0xAC, 0xFF, 0xC5,   // ldy $C5FF
            0x99, 0x00, 0xC5,   // sta $C500,y
            0xEE, 0xFF, 0xC5,   // inc $C5FF
            0xAC, 0xFE, 0xC5,   // ldy $C5FE
            0x60,               // rts
        ]);
        ram.Load(0xFFE4, [0xA9, 0x41, 0x60]);   // lda #$41 / rts
        ram.Load(0xFFF0, [0x4C, 0x60, 0xC0]);   // jmp $C060
        ram.Load(0xC060, [
            0x90, 0x07,         // bcc set
            0xAE, 0xF0, 0xC5,   // ldx $C5F0
            0xAC, 0xF1, 0xC5,   // ldy $C5F1
            0x60,               // rts
            0x8E, 0xF0, 0xC5,   // set: stx $C5F0
            0x8C, 0xF1, 0xC5,   // sty $C5F1
            0x60,               // rts
        ]);
    }

    [Fact]
    public void Kernal_routines()
    {
        var r = Run.Program("""
            import c64
            data msg = "HELLO"
            var key: u8
            var pos: u16
            fn main() {
                c64.chrout(c64.WHITE)
                c64.print(addr(msg), len(msg))
                key = c64.getin()
                c64.cursor(12, 30)
                pos = c64.get_cursor()
            }
            """, setup: Kernal);
        Assert.Equal([1, .. "HELLO"u8], r.Ram.Bytes.AsSpan(0xC500, r.Ram.Bytes[0xC5FF]).ToArray());
        Assert.Equal(0x41, r.U8("key"));
        Assert.Equal(12 | (30 << 8), r.U16("pos"));
        // print: the character goes straight from (src),y to the KERNAL.
        Assert.Matches(@"lda \(c64\.print\.src\),y.*\n.*jsr \$FFD2", r.Result.Listing);
        Assert.Matches(@"ldx #\$0C.*\n.*ldy #\$1E.*\n.*clc.*\n.*jsr \$FFF0", r.Result.Listing);
    }

    [Fact]
    public void Library_example_prints_and_writes_to_the_screen()
    {
        var path = AsmTests.FindExample("library.tern");
        var r = Run.Program(File.ReadAllText(path), sourcePath: path, setup: Kernal);
        var printed = "HELLO FROM THE TERN LIBRARY.\nYEAR 1982".Select(c => (byte)CharEncoding.Petscii.Encode(c)!.Value);
        Assert.Equal([0x93, .. printed], r.Ram.Bytes.AsSpan(0xC500, r.Ram.Bytes[0xC5FF]).ToArray());
        var row = 0x0400 + 400;
        Assert.Equal("6 X 7 = 42"u8.ToArray().Select(c => (byte)CharEncoding.Screen.Encode((char)c)!.Value),
            r.Ram.Bytes.AsSpan(row, 10).ToArray());
        Assert.All(r.Ram.Bytes.AsSpan(0xD800 + 400, 40).ToArray(), b => Assert.Equal(7, b));
        Assert.Equal(0, r.Ram.Bytes[0xD800 + 399]);
        Assert.Equal(0, r.Ram.Bytes[0xD800 + 440]);
        // KERNAL routines are called in ROM, with no stand-in functions.
        Assert.DoesNotContain("\nc64.chrout:", r.Result.Listing);
        Assert.Contains("jsr $FFD2", r.Result.Listing);
    }

    [Fact]
    public void Constants()
    {
        var r = Run.Program("""
            import c64
            var screen: u16
            var color: u16
            var colors: [u8; 3]
            fn main() {
                screen = c64.SCREEN
                color = c64.COLOR
                colors[0] = c64.BLACK
                colors[1] = c64.LIGHT_BLUE
                colors[2] = c64.LIGHT_GREY
            }
            """);
        Assert.Equal(0x0400, r.U16("screen"));
        Assert.Equal(0xD800, r.U16("color"));
        Assert.Equal([0, 14, 15], r.Ram.Bytes.AsSpan(r.Address("colors"), 3).ToArray());
    }

    [Fact]
    public void An_unused_import_adds_no_bytes()
    {
        var with = TernCompiler.Compile("import c64\nimport math\nimport mem\nimport text\nfn main() { }");
        var without = TernCompiler.Compile("fn main() { }");
        Assert.Equal(without.Image, with.Image);
        Assert.Equal(without.Report, with.Report);
    }

    [Fact]
    public void Register_structs_are_at_the_chip_addresses()
    {
        var r = Run.Program("""
            import c64
            fn main() {
                c64.vic.border = c64.BLUE
                c64.vic.background = c64.vic.border + 1
                c64.vic.sprite0_x = 1
                c64.vic.sprite7_y = 2
                c64.vic.sprite_enable = $81
                c64.vic.sprite_color[7] = c64.YELLOW
                c64.sid.voice1.freq = $1234
                c64.sid.voice3.sustain_release = 5
                c64.sid.cutoff = $0607
                c64.sid.volume = 15
                c64.cia1.timer_a = $4025
                c64.cia2.icr = $7F
            }
            """);
        byte[] m = r.Ram.Bytes;
        Assert.Equal(6, m[0xD020]);
        Assert.Equal(7, m[0xD021]);
        Assert.Equal(1, m[0xD000]);
        Assert.Equal(2, m[0xD00F]);
        Assert.Equal(0x81, m[0xD015]);
        Assert.Equal(7, m[0xD02E]);
        Assert.Equal(new byte[] { 0x34, 0x12 }, m[0xD400..0xD402]);
        Assert.Equal(5, m[0xD414]);
        Assert.Equal(new byte[] { 0x07, 0x06 }, m[0xD415..0xD417]);
        Assert.Equal(15, m[0xD418]);
        Assert.Equal(new byte[] { 0x25, 0x40 }, m[0xDC04..0xDC06]);
        Assert.Equal(0x7F, m[0xDD0D]);
        // Every field access is one absolute instruction: lda #6 / sta $D020.
        Assert.Contains("A9 06", r.Result.Listing);
        Assert.Contains("8D 20 D0", r.Result.Listing);
    }

    [Fact]
    public void Register_struct_fields_in_assembly()
    {
        var r = Run.Program("""
            import c64
            fn main() {
                asm {
                    lda #2
                    sta c64.vic.border
                    lda #c64.vic.background.hi
                    sta c64.vic.sprite_color+3
                }
            }
            """);
        Assert.Equal(2, r.Ram.Bytes[0xD020]);
        Assert.Equal(0xD0, r.Ram.Bytes[0xD02A]);
    }

    [Fact]
    public void Structs_example_moves_both_lists_the_same()
    {
        var path = AsmTests.FindExample("structs.tern");
        var r = Run.Program(File.ReadAllText(path), sourcePath: path);
        byte[] m = r.Ram.Bytes;
        // The same moves in C#.
        for (int i = 0; i < 8; i++)
        {
            int x = (i << 2) + 1, dx = (i & 1) == 1 ? 255 : 1;
            for (int f = 0; f < 100; f++)
            {
                x = (x + dx) & 0xFF;
                if (x == 0 || x == 39) dx = (0 - dx) & 0xFF;
            }
            Assert.Equal(i + 1, m[0x0400 + (i + 2) * 40 + x]);      // screen code of 'A' is 1
            Assert.Equal(i + 1, m[0x0400 + (i + 15) * 40 + x]);
        }
        Assert.Equal(0, m[0xD020]);
        Assert.Equal(6, m[0xD021]);
        Assert.Equal(1, m[0xD015]);
    }

    /// <summary>Memory that records the values written to one address.</summary>
    private sealed class WatchBus(Ram ram, int watched) : IBus
    {
        public List<byte> Writes { get; } = [];
        public byte Read(ushort address) => ram.Read(address);
        public void Write(ushort address, byte value)
        {
            if (address == watched) Writes.Add(value);
            ram.Write(address, value);
        }
    }

    [Fact]
    public void Interrupts_on_and_off_are_inline_without_a_call()
    {
        var r = Run.Program("""
            import c64
            fn main() {
                c64.interrupts_off()
                c64.interrupts_on()
            }
            """);
        Assert.Contains("sei", r.Result.Listing);
        Assert.Contains("cli", r.Result.Listing);
        Assert.DoesNotContain("jsr c64.interrupts", r.Result.Listing);
    }

    [Fact]
    public void Interrupt_handlers_are_installed_with_the_kernal_off()
    {
        var r = Run.Program("""
            import c64
            var ticks: u8
            var nmis: u8
            var held: u8
            var after: u8
            interrupt fn tick() { ticks += 1 }
            interrupt fn nmi() { nmis += 1 }
            fn main() {
                c64.kernal_off()
                c64.set_nmi(addr(nmi))
                c64.set_irq(addr(tick))
                c64.interrupts_on()
                for _ in 0..200 { after += 1 }
                c64.interrupts_off()
                held = ticks
                for _ in 0..200 { after += 1 }
                c64.set_irq(addr(tick))         // keeps interrupts held off
                for _ in 0..200 { after += 1 }
            }
            """, irqEvery: 500);
        byte[] m = r.Ram.Bytes;
        Assert.Equal(0x35, m[0x01]);
        Assert.Equal(0x7F, m[0xDC0D]);
        Assert.Equal(0x7F, m[0xDD0D]);
        Assert.Equal(r.Address("tick"), m[0xFFFE] | m[0xFFFF] << 8);
        Assert.Equal(r.Address("nmi"), m[0xFFFA] | m[0xFFFB] << 8);
        Assert.True(r.Interrupts > 2);
        Assert.Equal(r.Interrupts, r.U8("ticks"));
        Assert.Equal(r.U8("held"), r.U8("ticks"));   // none after interrupts_off, nor after set_irq
        Assert.True(r.Cpu.I);
        Assert.Equal(0, r.U8("nmis"));
        Assert.Equal(0x40, m[r.Address("c64.ignore_nmi")]);   // kernal_off's NMI handler: just rti
    }

    [Fact]
    public void Raster_example_cycles_the_border_colors_from_its_interrupt()
    {
        // The example never returns, so it is run for a fixed time, with an IRQ every 1000 cycles
        // standing in for the VIC-II's raster interrupt.
        var path = AsmTests.FindExample("raster.tern");
        var result = TernCompiler.Compile(File.ReadAllText(path), sourcePath: path);
        var ram = new Ram();
        ram.Load((ushort)result.LoadAddress, result.Image);
        var bus = new WatchBus(ram, 0xD020);
        var cpu = new Cpu6502(bus) { PC = (ushort)result.Entry, SP = 0xFF, I = true };
        long next = 1000;
        int taken = 0;
        while (cpu.Cycles < 400_000)
        {
            if (cpu.Cycles >= next && cpu.Irq())
            {
                taken++;
                next = cpu.Cycles + 1000;
            }
            cpu.Step();
        }

        byte[] colors = [2, 8, 7, 5, 3, 6, 4, 15];  // red, orange, yellow, green, cyan, blue, purple, light grey
        Assert.True(bus.Writes.Count > 100, $"{bus.Writes.Count} border writes");
        Assert.Equal(taken, bus.Writes.Count);
        for (int i = 0; i < bus.Writes.Count; i++)
            Assert.Equal(colors[i % 8], bus.Writes[i]);

        byte[] m = ram.Bytes;
        Assert.Equal(0x35, m[0x01]);
        Assert.Equal(result.Symbols["raster"], m[0xFFFE] | m[0xFFFF] << 8);
        Assert.Equal(result.Symbols["c64.ignore_nmi"], m[0xFFFA] | m[0xFFFB] << 8);
        Assert.Equal(0x40, m[result.Symbols["c64.ignore_nmi"]]);   // just rti
        Assert.Equal(1, m[0xD01A]);
        Assert.Equal(0x7F, m[0xDC0D]);
        Assert.Equal(0x7F, m[0xDD0D]);
        int frames = taken / 8;
        Assert.Equal(frames & 0xFF, m[result.Symbols["frames"]]);
        // Main keeps running between interrupts: the counter on screen is at most one frame behind.
        // Hex digits as screen codes: '0'-'9' are $30-$39, 'A'-'F' are 1-6.
        static int Digit(byte code) => code >= 0x30 ? code - 0x30 : code + 9;
        int at = 0x0400 + 12 * 40 + 29;
        int shown = Digit(m[at]) << 4 | Digit(m[at + 1]);
        Assert.InRange((frames - shown) & 0xFF, 0, 1);
    }
}
