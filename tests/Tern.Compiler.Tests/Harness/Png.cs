using System.IO.Compression;

namespace Tern.Compiler.Tests;

/// <summary>
/// Reads the PNG screenshots the emulators write (8-bit RGB or RGBA, not interlaced), for the checks on the real
/// emulators (<c>TERN_REAL</c>), and compares two of them pixel by pixel.
/// </summary>
public static class Png
{
    /// <summary>The image's size and its pixels as 0xRRGGBB, row by row.</summary>
    public static (int Width, int Height, int[] Pixels) Read(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        int width = 0, height = 0, bpp = 0;
        using var idat = new MemoryStream();
        for (int i = 8; i < d.Length;)
        {
            int length = d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3];
            string type = System.Text.Encoding.ASCII.GetString(d, i + 4, 4);
            if (type == "IHDR")
            {
                width = d[i + 8] << 24 | d[i + 9] << 16 | d[i + 10] << 8 | d[i + 11];
                height = d[i + 12] << 24 | d[i + 13] << 16 | d[i + 14] << 8 | d[i + 15];
                byte depth = d[i + 16], color = d[i + 17], interlace = d[i + 20];
                bpp = color switch { 2 => 3, 6 => 4, _ => 0 };
                if (depth != 8 || bpp == 0 || interlace != 0)
                    throw new NotSupportedException($"{path}: only 8-bit RGB or RGBA PNGs, not interlaced, are read");
            }
            if (type == "IDAT") idat.Write(d, i + 8, length);
            i += 12 + length;
        }
        idat.Position = 0;
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.CopyTo(raw);
        return (width, height, Unfilter(raw.ToArray(), width, height, bpp));
    }

    /// <summary>Undoes each row's filter (PNG specification, section 9).</summary>
    private static int[] Unfilter(byte[] raw, int width, int height, int bpp)
    {
        int stride = width * bpp;
        var pixels = new int[width * height];
        byte[] prev = new byte[stride], line = new byte[stride];
        for (int y = 0, p = 0; y < height; y++, p += 1 + stride)
        {
            byte filter = raw[p];
            for (int x = 0; x < stride; x++)
            {
                int a = x >= bpp ? line[x - bpp] : 0, b = prev[x], c = x >= bpp ? prev[x - bpp] : 0;
                int predicted = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"PNG filter {filter} in row {y}"),
                };
                line[x] = (byte)(raw[p + 1 + x] + predicted);
            }
            for (int x = 0; x < width; x++)
                pixels[y * width + x] = line[x * bpp] << 16 | line[x * bpp + 1] << 8 | line[x * bpp + 2];
            (prev, line) = (line, prev);
        }
        return pixels;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>The share of pixels (0 to 1) whose color differs; 1 when the sizes differ.</summary>
    public static double Difference(string a, string b)
    {
        var (wa, ha, pa) = Read(a);
        var (wb, hb, pb) = Read(b);
        if (wa != wb || ha != hb) return 1;
        return (double)pa.Zip(pb).Count(p => p.First != p.Second) / pa.Length;
    }
}
