namespace Tern.Compiler.Tests;

/// <summary>
/// M20 follow-up 2, "done when": the HOW TO PLAY lines of Galaxy Stars' <c>pages.p8</c> (<c>how_row</c>,
/// <c>how_col</c>, <c>how_text</c>, three parallel tables) written as one array of struct literals give
/// the same bytes as the parallel arrays: an array of structs is one table per record byte.
/// </summary>
public class GalaxyStarsPagesTests
{
    private const string Texts = """
        const TEXT_COL: u8 = 9
        data H0 = screen"how to play\0"
        data H1 = screen"move with the stick\0"
        data H2 = screen"fire to boost\0"
        data H3 = screen"ram stars to make them go\0"
        data H4 = screen"supernova: 3 bumps or 1 boost\0"
        data H5 = screen"hit stars swell, then explode\0"
        data H6 = screen"get clear before they blow\0"
        data H7 = screen"hurl hostiles with a boost\0"
        data H8 = screen"don't let them touch you\0"
        data H9 = screen"grab a quasar for 8 seconds\0"
        data H10 = screen"of invincibility\0"
        data H11 = screen"clear all stars to reach the next galaxy\0"

        """;

    private const string Parallel = Texts + """
        data how_row: [u8; _] = [1, 3, 4, 6, 7, 11, 12, 16, 17, 19, 20, 23]
        data how_col: [u8; _] = [14, TEXT_COL, TEXT_COL, TEXT_COL, TEXT_COL, TEXT_COL, TEXT_COL, TEXT_COL, TEXT_COL,
                                 TEXT_COL, TEXT_COL, 0]
        data how_text: [ptr u8; _] = [
            addr(H0), addr(H1), addr(H2), addr(H3), addr(H4), addr(H5),
            addr(H6), addr(H7), addr(H8), addr(H9), addr(H10), addr(H11),
        ]
        var sum: u16
        fn main() {
            for i in 0..len(how_row) { sum += (how_row[i] + how_col[i]) as u16 + how_text[i][1] }
        }
        """;

    private const string Records = Texts + """
        struct HowLine { row: u8, col: u8, text: ptr u8 }
        data how: [HowLine; _] = [
            HowLine { row = 1, col = 14, text = addr(H0) },
            HowLine { row = 3, col = TEXT_COL, text = addr(H1) },
            HowLine { row = 4, col = TEXT_COL, text = addr(H2) },
            HowLine { row = 6, col = TEXT_COL, text = addr(H3) },
            HowLine { row = 7, col = TEXT_COL, text = addr(H4) },
            HowLine { row = 11, col = TEXT_COL, text = addr(H5) },
            HowLine { row = 12, col = TEXT_COL, text = addr(H6) },
            HowLine { row = 16, col = TEXT_COL, text = addr(H7) },
            HowLine { row = 17, col = TEXT_COL, text = addr(H8) },
            HowLine { row = 19, col = TEXT_COL, text = addr(H9) },
            HowLine { row = 20, col = TEXT_COL, text = addr(H10) },
            HowLine { row = 23, text = addr(H11) },
        ]
        var sum: u16
        fn main() {
            for i in 0..len(how) {
                var p: ptr u8 = how[i].text
                sum += (how[i].row + how[i].col) as u16 + p[1]
            }
        }
        """;

    [Fact]
    public void The_how_to_play_lines_as_struct_literals_give_the_parallel_tables()
    {
        var parallel = Run.Program(Parallel);
        var records = Run.Program(Records);

        byte[] Bytes(Run r, string name, int n) => r.Ram.Bytes[r.Address(name)..(r.Address(name) + n)];
        // The texts are placed at the same addresses in both programs only if the code has the same size,
        // so the pointers are compared with each program's own text addresses.
        byte[] Expected(Run r)
        {
            var text = Enumerable.Range(0, 12).Select(k => r.Address($"H{k}")).ToArray();
            return Bytes(r, "how_row", 12).Concat(Bytes(r, "how_col", 12))
                .Concat(text.Select(a => (byte)a)).Concat(text.Select(a => (byte)(a >> 8))).ToArray();
        }
        var tables = Expected(parallel);
        Assert.Equal(new byte[] { 1, 3, 4, 6, 7, 11, 12, 16, 17, 19, 20, 23 }, tables[..12]);
        Assert.Equal(tables[24..], Bytes(parallel, "how_text", 24));

        // The record array: rows, then columns, then the text pointers' low and high bytes.
        var how = Bytes(records, "how", 48);
        Assert.Equal(tables[..24], how[..24]);
        var recordTexts = Enumerable.Range(0, 12).Select(k => records.Address($"H{k}")).ToArray();
        Assert.Equal(recordTexts.Select(a => (byte)a).Concat(recordTexts.Select(a => (byte)(a >> 8))).ToArray(), how[24..]);

        Assert.Equal(parallel.U16("sum"), records.U16("sum"));
    }
}
