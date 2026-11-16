using Tern.Cpu;

namespace Tern.Compiler.Tools;

/// <summary>
/// A text screen as the C64's, the Atari's and the Apple II's ROM print to it (review code-quality-tools 6): rows
/// of <see cref="Columns"/> bytes at <see cref="RowAddress"/>, a cursor, and a window from <see cref="Left"/> to
/// <see cref="Right"/> and <see cref="Top"/> to <see cref="Bottom"/> (exclusive) that lines wrap and scroll in.
/// Addresses wrap at $FFFF, since a program may point the screen anywhere (review code-quality-tools 16). Each
/// machine keeps the cursor where its ROM does and makes a screen for each call.
/// </summary>
internal sealed class TextScreen(Ram ram, Func<int, int> rowAddress, int columns, int rows, byte blank)
{
    public int Columns { get; } = columns;
    public int Rows { get; } = rows;
    public int Left { get; init; }
    public int Right { get; init; } = columns - 1;
    public int Top { get; init; }
    public int Bottom { get; init; } = rows;
    public int Row { get; set; }
    public int Column { get; set; }

    public int RowAddress(int row) => rowAddress(row);

    /// <summary>Stores <paramref name="code"/> at the cursor and moves it on.</summary>
    public void Put(byte code)
    {
        ram.Bytes[(rowAddress(Row) + Column) & 0xFFFF] = code;
        Advance();
    }

    /// <summary>Moves the cursor one column on, to the next line after the right edge.</summary>
    public void Advance()
    {
        if (++Column > Right) NewLine();
    }

    /// <summary>The cursor to the left edge of the next line.</summary>
    public void NewLine()
    {
        Column = Left;
        LineFeed();
    }

    /// <summary>The cursor one row down, scrolling the window up a row at its bottom.</summary>
    public void LineFeed()
    {
        if (Row < Bottom - 1) { Row++; return; }
        for (int r = Top; r < Bottom - 1; r++)
            for (int c = 0; c < Columns; c++)
                ram.Bytes[(rowAddress(r) + c) & 0xFFFF] = ram.Bytes[(rowAddress(r + 1) + c) & 0xFFFF];
        Clear(Bottom - 1, 1);
    }

    /// <summary>Blanks the window's rows (the cursor stays where it is).</summary>
    public void Clear() => Clear(Top, Bottom - Top);

    private void Clear(int first, int count)
    {
        for (int r = first; r < first + count; r++)
            for (int c = 0; c < Columns; c++) ram.Bytes[(rowAddress(r) + c) & 0xFFFF] = blank;
    }
}
