using Tern.Compiler.Syntax;

namespace Tern.Compiler.Tests;

/// <summary>
/// M20 follow-up 2, spec 3.3: struct literals as initial values of globals, arrays of structs and data.
/// Each layout is compared with bytes computed in C#, every byte of the tables.
/// </summary>
public class StructLiteralTests
{
    private static string Error(string source) => Assert.Throws<CompileError>(() => TernCompiler.Compile(source)).Description;

    // Rec: a @0, b @1, w @2-3, flag @4, _ @5, s @6-7: 8 bytes.
    private const string Rec = "struct Rec { a: u8, b: i8, w: u16, flag: bool, _: u8, s: i16 }\n";

    private const string Records = """
        [
            Rec { a = 255, b = -128, w = $1234, flag = true, s = -32768 },
            Rec { s = 32767, w = 65535, b = 127 },
            Rec { },
            Rec {
                a = 1
                b = -1
                s = -2
            },
        ]
        """;

    /// <summary>The four records of <see cref="Records"/>, each in its memory layout.</summary>
    private static readonly byte[][] RecordBytes =
    [
        [255, 0x80, 0x34, 0x12, 1, 0, 0x00, 0x80],
        [0, 127, 0xFF, 0xFF, 0, 0, 0xFF, 0x7F],
        [0, 0, 0, 0, 0, 0, 0, 0],
        [1, 0xFF, 0, 0, 0, 0, 0xFE, 0xFF],
    ];

    /// <summary>One table per record byte: byte k of record i at k * n + i.</summary>
    private static byte[] Tables(byte[][] records)
    {
        int n = records.Length, size = records[0].Length;
        var bytes = new byte[n * size];
        for (int i = 0; i < n; i++)
            for (int k = 0; k < size; k++) bytes[k * n + i] = records[i][k];
        return bytes;
    }

    private static byte[] Whole(byte[][] records) => records.SelectMany(r => r).ToArray();

    private static byte[] Memory(Run r, string name, int length) => r.Ram.Bytes[r.Address(name)..(r.Address(name) + length)];

    [Theory]
    [InlineData("var recs: [Rec; 4]", true)]
    [InlineData("data recs: [Rec; 4]", true)]
    [InlineData("var recs: [Rec; _]", true)]
    [InlineData("data recs: [Rec; _]", true)]
    [InlineData("linear var recs: [Rec; 4]", false)]
    [InlineData("linear data recs: [Rec; 4]", false)]
    [InlineData("indexed var recs: [Rec; 4]", false)]
    public void Every_layout_holds_the_bytes_of_the_records(string declaration, bool tables)
    {
        // The fields are read with a variable index too, so the code finds them where the bytes are.
        var r = Run.Program(Rec + $$"""
            {{declaration}} = {{Records}}
            var sum: u16
            var neg: u8
            fn main() {
                for i in 0..4 {
                    sum += recs[i].w
                    if recs[i].s < 0 { neg += 1 }
                }
            }
            """);
        var expected = tables ? Tables(RecordBytes) : Whole(RecordBytes);
        Assert.Equal(expected, Memory(r, "recs", 32));
        Assert.Equal(unchecked((ushort)(0x1234 + 0xFFFF)), r.U16("sum"));
        Assert.Equal(2, r.U8("neg"));
    }

    [Fact]
    public void A_single_struct_variable_and_data()
    {
        var r = Run.Program("""
            struct Point { x: u8, y: u8 }
            struct Enemy { pos: Point, hp: u8, vel: i16 }
            var player: Enemy = Enemy { hp = 3 }
            var other = Enemy { vel = -2, pos = Point { y = 7, x = 6 } }
            data start = Point { x = 160, y = 140 }
            var z: Point @ zp = Point { x = 5, y = 9 }
            var x: u8
            var y: u8
            var zx: u8
            fn main() {
                x = start.x
                y = start.y
                zx = z.x + z.y
            }
            """);
        Assert.Equal(new byte[] { 0, 0, 3, 0, 0 }, Memory(r, "player", 5));
        Assert.Equal(new byte[] { 6, 7, 0, 0xFE, 0xFF }, Memory(r, "other", 5));
        Assert.Equal(new byte[] { 160, 140 }, Memory(r, "start", 2));
        Assert.Equal(160, r.U8("x"));
        Assert.Equal(140, r.U8("y"));
        Assert.True(r.Address("z") < 0x100);
        Assert.Equal(14, r.U8("zx"));
    }

    [Fact]
    public void Nested_structs_in_a_table_array()
    {
        var r = Run.Program("""
            struct Point { x: u8, y: u8 }
            struct Enemy { pos: Point, hp: u8, vel: i16 }
            var es: [Enemy; 3] = [
                Enemy { pos = Point { x = 10, y = 20 }, hp = 1, vel = 300 },
                Enemy { hp = 2 },
                Enemy { pos = Point { y = 255 }, vel = -1 },
            ]
            fn main() { }
            """);
        byte[][] records = [[10, 20, 1, 0x2C, 0x01], [0, 0, 2, 0, 0], [0, 255, 0, 0xFF, 0xFF]];
        Assert.Equal(Tables(records), Memory(r, "es", 15));
    }

    [Fact]
    public void Addresses_in_fields_are_filled_in_when_the_program_is_placed()
    {
        var r = Run.Program("""
            struct Line { row: u8, col: u8, text: ptr u8 }
            struct Jump { lo: u8, hi: u8, to: u16 }
            data TITLE = "TITLE"
            data HOW = "HOW TO PLAY"
            data lines: [Line; _] = [
                Line { row = 1, col = 14, text = addr(TITLE) },
                Line { row = 3, col = 2, text = addr(HOW) + 4 },
            ]
            linear var chain: [Line; 2] = [
                Line { text = (addr(chain) + 4) as ptr u8 },
                Line { row = 9, text = addr(TITLE) },
            ]
            data jump = Jump { lo = (addr(main) - 1).lo, hi = (addr(main) - 1).hi, to = addr(main) }
            fn main() { }
            """);
        int title = r.Address("TITLE"), how = r.Address("HOW"), chain = r.Address("chain"), main = r.Address("main");
        byte[][] lineRecords = [[1, 14, (byte)title, (byte)(title >> 8)], [3, 2, (byte)(how + 4), (byte)((how + 4) >> 8)]];
        Assert.Equal(Tables(lineRecords), Memory(r, "lines", 8));
        byte[][] chainRecords = [[0, 0, (byte)(chain + 4), (byte)((chain + 4) >> 8)], [9, 0, (byte)title, (byte)(title >> 8)]];
        Assert.Equal(Whole(chainRecords), Memory(r, "chain", 8));
        Assert.Equal(new[] { (byte)(main - 1), (byte)((main - 1) >> 8), (byte)main, (byte)(main >> 8) }, Memory(r, "jump", 4));
    }

    [Fact]
    public void Array_fields_from_a_list_and_a_string()
    {
        // name: 4 bytes; pts: [u16; 2] split inside the record (two low bytes, two high bytes); ptrs too.
        var r = Run.Program("""
            struct Named { id: u8, name: [u8; 4], pts: [u16; 2], ptrs: [ptr u8; 2] }
            data TEXT = "XY"
            var one = Named { id = 7, name = "ABCD", pts = [1, $1234], ptrs = [addr(TEXT), addr(TEXT) + 1] }
            var two: [Named; 2] = [
                Named { name = [1, 2, 3, 4], ptrs = [0, addr(TEXT)] },
                Named { id = 2, pts = [$FFFF, 0] },
            ]
            fn main() { }
            """);
        int t = r.Address("TEXT");
        byte lo = (byte)t, hi = (byte)(t >> 8), lo1 = (byte)(t + 1), hi1 = (byte)((t + 1) >> 8);
        // PETSCII "ABCD" is $41-$44.
        Assert.Equal(new byte[] { 7, 0x41, 0x42, 0x43, 0x44, 0x01, 0x34, 0x00, 0x12, lo, lo1, hi, hi1 }, Memory(r, "one", 13));
        byte[][] records =
        [
            [0, 1, 2, 3, 4, 0, 0, 0, 0, 0, lo, 0, hi],
            [2, 0, 0, 0, 0, 0xFF, 0, 0xFF, 0, 0, 0, 0, 0],
        ];
        Assert.Equal(Tables(records), Memory(r, "two", 26));
    }

    [Fact]
    public void The_spec_example_of_fixed_data_in_an_overlay_compiles()
    {
        var dir = Directory.CreateTempSubdirectory("tern");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "shared.tern"), "struct LevelHeader { width: u8, height: u8, name: ptr u8 }\n");
            File.WriteAllText(Path.Combine(dir.FullName, "level1.tern"), """
                import shared
                data NAME = "CAVES"
                data header: shared.LevelHeader @ $6000 = shared.LevelHeader { width = 40, height = 12, name = addr(NAME) }
                fn go() -> u8 { return header.width }
                """);
            string main = """
                import shared
                import level1 @ levels
                memory {
                    $0801..=$5FFF  program
                    $6000..=$6FFF  overlay levels
                }
                var w: u8
                fn main() { w = level1.go() }
                """;
            var path = Path.Combine(dir.FullName, "main.tern");
            File.WriteAllText(path, main);
            var result = TernCompiler.Compile(main, sourcePath: path);
            var file = result.Overlays.Single(o => o.Name == "level1");
            Assert.Equal(0x6000, file.LoadAddress);
            int name = result.Symbols["level1.NAME"];
            Assert.Equal(new byte[] { 40, 12, (byte)name, (byte)(name >> 8) }, file.Image[..4]);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void A_name_before_a_block_in_a_header_is_not_a_literal()
    {
        var r = Run.Program("""
            const LIMIT = 3
            var n: u8
            var m: u8
            fn main() {
                if n == LIMIT { m = 9 }
                while n < LIMIT { n += 1 }
                for i in 0..LIMIT { m += 1 }
                match n {
                    LIMIT => m += 10
                    _ => { }
                }
            }
            """);
        Assert.Equal(3, r.U8("n"));
        Assert.Equal(13, r.U8("m"));
    }

    private const string P = "struct Point { x: u8, y: u8 }\nstruct Pad { a: u8, _: u8, b: u16 }\n";

    [Theory]
    [InlineData(P + "var p = Point { z = 1 }\nfn main() { }", "'Point' has no field 'z'. Use one of its fields: x, y")]
    [InlineData(P + "var p = Point { x = 1, x = 2 }\nfn main() { }", "'x' is given twice in this Point literal. Remove one")]
    [InlineData(P + "var p = Pad { _ = 1 }\nfn main() { }", "'_' is padding, which has no value")]
    [InlineData(P + "var p: Point = Pad { a = 1 }\nfn main() { }", "This is a Pad literal, but the value here is a Point. Write 'Point { ... }'")]
    [InlineData(P + "var ps: [Point; 2] = [Point { }, Pad { }]\nfn main() { }", "This is a Pad literal, but the value here is a Point")]
    [InlineData(P + "var p = Point { x = 300 }\nfn main() { }", "300 does not fit in u8")]
    [InlineData(P + "var p = Pad { b = -1 }\nfn main() { }", "-1 does not fit in u16")]
    [InlineData(P + "var p = Point { x = true }\nfn main() { }", "Expected u8, found bool")]
    [InlineData(P + "var p = Nope { x = 1 }\nfn main() { }", "Unknown type 'Nope'")]
    [InlineData(P + "const C = 1\nvar p = C { x = 1 }\nfn main() { }", "'C' is not a type")]
    [InlineData(P + "var p = Point { x: 1 }\nfn main() { }", "Write 'x = ...'")]
    [InlineData(P + "var p = Point { x = 1 y = 2 }\nfn main() { }", "Expected ',' or '}' after the value of 'x'")]
    [InlineData(P + "var p = Point { x = 1,\nfn main() { }", "Expected field name")]
    [InlineData(P + "var p: Point\nfn main() { p = Point { x = 1 } }", "Assign the fields one by one, e.g. 'p.x = 1'")]
    [InlineData(P + "fn main() { var p: Point = Point { y = 2 } }", "Assign the fields one by one, e.g. 'p.y = 2'")]
    [InlineData(P + "fn main() { var p = Point { y = 2 } }", "A struct literal is only the initial value of a global or of data")]
    [InlineData(P + "fn f(p: ptr Point) { }\nfn main() { f(Point { x = 1 }) }", "Assign the fields one by one, e.g. 's.x = 1'")]
    [InlineData(P + "var x: u8\nfn main() { x = Point { x = 1 }.x }", "A struct literal is only the initial value")]
    [InlineData(P + "struct L { p: Point }\nvar l = L { p = 5 }\nfn main() { }", "A Point is initialized with a struct literal. Write 'Point { x = ... }'")]
    [InlineData(P + "struct N { t: [u8; 3] }\nvar n = N { t = [1, 2] }\nfn main() { }", "The field 't' is [u8; 3]: expected 3 values, found 2")]
    [InlineData(P + "struct N { t: [u8; 3] }\nvar n = N { t = \"ABCD\" }\nfn main() { }", "The field 't' is [u8; 3]: expected 3 bytes, found 4")]
    [InlineData(P + "struct N { t: [u16; 2] }\nvar n = N { t = \"AB\" }\nfn main() { }", "A string is a [u8; 2] array, but the field 't' is [u16; 2]")]
    [InlineData(P + "struct N { t: [u8; 2] }\nvar n = N { t = 5 }\nfn main() { }", "The field 't' is [u8; 2], so its value is a list")]
    [InlineData(P + "var ps: [Point; 2] = [Point { }]\nfn main() { }", "Expected 2 values, found 1")]
    [InlineData(P + "var v: u8 = 1\nvar p = Point { x = v }\nfn main() { }", "must be known at compile time")]
    [InlineData(P + "var p: Point @ $C000 = Point { x = 1 }\nfn main() { }", "A variable at a fixed address cannot have an initial value")]
    [InlineData(P + "var p = Pad { b = addr(main) + addr(main) }\nfn main() { }", "An address in an initial value is filled in when the program is placed")]
    [InlineData(P + "indexed data ps: [Point; 2] = [Point { }, Point { }]\nfn main() { }", "indexed")]
    // A literal in the wrong place inside an initial value (review M20 follow-up 2, suggestion 2).
    [InlineData("struct L { x: u8, n: ptr L }\nvar a: L = L { n = L { x = 1 } }\nfn main() { }",
        "'n' is ptr L, an address, not a L. Declare the record as data and write 'n = addr(name)'")]
    [InlineData("struct L { x: u8, n: ptr L }\ndata B: [u8; 1] = [1]\nvar a: L = L { n = L { n = addr(B) } }\nfn main() { }",
        "'n' is ptr L, an address, not a L")]
    [InlineData("struct L { x: u8, n: ptr L }\nvar p: ptr L = L { x = 1 }\nfn main() { }", "This value is ptr L, an address, not a L")]
    [InlineData("struct L { x: u8, ps: [ptr L; 2] }\nvar a: L = L { ps = [0, L { }] }\nfn main() { }", "This value is ptr L, an address, not a L")]
    [InlineData(P + "var b: u8 = Point { x = 1 }\nfn main() { }", "Expected u8, found a Point literal")]
    [InlineData(P + "var a = Pad { a = Point { } }\nfn main() { }", "Expected u8, found a Point literal")]
    [InlineData(P + "const K = Point { x = 1 }\nfn main() { }", "A constant cannot be a struct. Declare it as data: 'data K = Point { ... }'")]
    [InlineData(P + "fn main() { const K = Point { x = 1 } }", "A constant cannot be a struct")]
    // A missing ',' between list elements (suggestion 3).
    [InlineData(P + "var ps: [Point; 2] = [\n  Point { x = 1 }\n  Point { x = 2 }\n]\nfn main() { }",
        "Expected ',' or ']' after this element, found 'Point'. Write ',' after the '}'")]
    [InlineData("var a: [u8; 2] = [1 2]\nfn main() { }", "Expected ',' or ']' after this element, found '2'. Write ',' between the elements")]
    // No closing ']' (second review, suggestion 2).
    [InlineData("var a: [u8; 2] = [1, 2", "Expected ',' or ']' after this element, found end of file. Write ']' to end the list")]
    [InlineData("fn main() { f([1, 2) }", "Expected ',' or ']' after this element, found ')'. Write ']' to end the list")]
    // Question 86: inside [ ] a newline still does not separate elements.
    [InlineData("var a: [u8; 2] = [\n  1\n  2\n]\nfn main() { }", "Expected ',' or ']' after this element, found '2'. Write ',' between the elements")]
    public void Reports_errors(string source, string expected) => Assert.Contains(expected, Error(source));

    [Fact]
    public void Data_of_structs_is_read_only()
    {
        Assert.Contains("'start.x' is data, which is read-only",
            Error("struct Point { x: u8, y: u8 }\ndata start = Point { x = 1 }\nfn main() { start.x = 2 }"));
        Assert.Contains("is data, which is read-only",
            Error("struct Point { x: u8, y: u8 }\ndata ps: [Point; 1] = [Point { }]\nvar i: u8\nfn main() { ps[i].y = 2 }"));
    }

    [Fact]
    public void A_struct_literal_has_no_code()
    {
        var with = TernCompiler.Compile("struct Point { x: u8, y: u8 }\ndata start = Point { x = 160, y = 140 }\nvar a: u8\nfn main() { a = start.y }");
        var without = TernCompiler.Compile("data start: [u8; 2] = [160, 140]\nvar a: u8\nfn main() { a = start[1] }");
        Assert.Equal(without.Prg, with.Prg);
    }
}
