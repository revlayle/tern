using System.Numerics;

namespace Tern.Compiler.Tests;

/// <summary>
/// The format of lib/float (spec 10.1, decision 2026-11-13) computed exactly, as the reference for the
/// tests: each result is the exact rational result rounded once to 24 bits, halves away from zero.
/// Bytes 0-2 are the mantissa (low first, the leading 1 not stored, bit 7 of byte 2 the sign), byte 3
/// the exponent plus $80 ($00: the value is 0). The value is ±M * 2^(E - 152), M the 24-bit mantissa.
/// </summary>
public static class FloatModel
{
    public readonly record struct Result(byte[] Bytes, bool Overflow);

    private static byte[] Zero => [0, 0, 0, 0];

    public static byte[] Largest(bool negative) => [0xFF, 0xFF, (byte)(negative ? 0xFF : 0x7F), 0xFF];

    /// <summary>The exact value as a fraction, the sign in the numerator.</summary>
    public static (BigInteger Num, BigInteger Den) Value(byte[] b)
    {
        if (b[3] == 0) return (0, 1);
        BigInteger m = 0x800000 | (b[2] & 0x7F) << 16 | b[1] << 8 | b[0];
        if ((b[2] & 0x80) != 0) m = -m;
        int s = b[3] - 152;
        return s >= 0 ? (m << s, 1) : (m, BigInteger.One << -s);
    }

    public static bool Negative(byte[] b) => b[3] != 0 && (b[2] & 0x80) != 0;

    /// <summary>num / den rounded to the format.</summary>
    public static Result Encode(BigInteger num, BigInteger den)
    {
        if (den.Sign < 0) (num, den) = (-num, -den);
        if (num.IsZero) return new(Zero, false);
        bool negative = num.Sign < 0;
        num = BigInteger.Abs(num);
        // 2^(e-1) <= num/den < 2^e
        int e = (int)(num.GetBitLength() - den.GetBitLength());
        while (AtLeast(num, den, e)) e++;
        while (!AtLeast(num, den, e - 1)) e--;
        var t = Scaled(num, den, 25 - e);       // one bit below the mantissa
        var m = (t + 1) >> 1;
        if (m == 1 << 24) { m = 1 << 23; e++; }
        int exponent = e + 128;
        if (exponent > 255) return new(Largest(negative), true);
        if (exponent < 1) return new(Zero, false);
        return new([(byte)(m & 0xFF), (byte)(m >> 8 & 0xFF), (byte)((int)(m >> 16 & 0x7F) | (negative ? 0x80 : 0)), (byte)exponent], false);
    }

    /// <summary>num / den >= 2^k.</summary>
    private static bool AtLeast(BigInteger num, BigInteger den, int k) => k >= 0 ? num >= den << k : num << -k >= den;

    /// <summary>floor(num / den * 2^k), num and den positive.</summary>
    private static BigInteger Scaled(BigInteger num, BigInteger den, int k) => k >= 0 ? (num << k) / den : num / (den << -k);

    public static Result Add(byte[] p, byte[] q)
    {
        var (a, b) = (Value(p), Value(q));
        return Encode(a.Num * b.Den + b.Num * a.Den, a.Den * b.Den);
    }

    public static Result Sub(byte[] p, byte[] q)
    {
        var (a, b) = (Value(p), Value(q));
        return Encode(a.Num * b.Den - b.Num * a.Den, a.Den * b.Den);
    }

    public static Result Mul(byte[] p, byte[] q)
    {
        var (a, b) = (Value(p), Value(q));
        return Encode(a.Num * b.Num, a.Den * b.Den);
    }

    /// <summary>Division by zero gives the largest value with the sign of p, and overflow.</summary>
    public static Result Div(byte[] p, byte[] q)
    {
        if (q[3] == 0) return new(Largest((p[2] & 0x80) != 0), true);
        var (a, b) = (Value(p), Value(q));
        return Encode(a.Num * b.Den, a.Den * b.Num);
    }

    public static byte[] Neg(byte[] p) => p[3] == 0 ? (byte[])p.Clone() : [p[0], p[1], (byte)(p[2] ^ 0x80), p[3]];

    public static byte[] Abs(byte[] p) => [p[0], p[1], (byte)(p[2] & 0x7F), p[3]];

    public static int Cmp(byte[] p, byte[] q)
    {
        var (a, b) = (Value(p), Value(q));
        return (a.Num * b.Den).CompareTo(b.Num * a.Den);
    }

    public static Result FromInt(int v) => Encode(v, 1);

    /// <summary>Toward zero, or to nearest with halves away from zero.</summary>
    public static BigInteger Integer(byte[] p, bool round)
    {
        var (num, den) = Value(p);
        if (!round) return BigInteger.Divide(num, den);
        var magnitude = (BigInteger.Abs(num) * 2 + den) / (den * 2);
        return num.Sign < 0 ? -magnitude : magnitude;
    }

    /// <summary>A conversion to an integer type: out of range gives the nearest limit and overflow.</summary>
    public static (int Value, bool Overflow) ToInt(byte[] p, bool round, int min, int max)
    {
        var v = Integer(p, round);
        if (v < min) return (min, true);
        if (v > max) return (max, true);
        return ((int)v, false);
    }

    public static byte[] Trunc(byte[] p) => Encode(Integer(p, false), 1).Bytes;

    public static byte[] Floor(byte[] p)
    {
        var (num, den) = Value(p);
        var q = BigInteger.Divide(num, den);
        if (num.Sign < 0 && q * den != num) q -= 1;
        return Encode(q, 1).Bytes;
    }

    /// <summary>
    /// The text of float.dec: round(|p| * 10^d), d at most 9, halves away from zero, with the point d
    /// digits from the end and at least one digit before it; '-' when negative and not rounded to 0.
    /// </summary>
    public static string Dec(byte[] p, int decimals)
    {
        int d = Math.Min(decimals, 9);
        var (num, den) = Value(p);
        var n = (BigInteger.Abs(num) * BigInteger.Pow(10, d) * 2 + den) / (den * 2);
        string digits = n.ToString().PadLeft(d + 1, '0');
        string text = d == 0 ? digits : digits[..^d] + "." + digits[^d..];
        return (num.Sign < 0 && !n.IsZero ? "-" : "") + text;
    }

    /// <summary>float.of: the double's exact value rounded to the format; null when it is too large.</summary>
    public static byte[]? Of(double x)
    {
        if (x == 0) return Zero;
        long bits = BitConverter.DoubleToInt64Bits(x);
        int exponent = (int)(bits >> 52 & 0x7FF);
        long fraction = bits & 0xFFFFFFFFFFFFFL;
        BigInteger m = exponent == 0 ? fraction : fraction | 1L << 52;
        int shift = (exponent == 0 ? 1 : exponent) - 1075;
        if (x < 0) m = -m;
        var r = shift >= 0 ? Encode(m << shift, 1) : Encode(m, BigInteger.One << -shift);
        return r.Overflow ? null : r.Bytes;
    }

    /// <summary>The bytes nearest to a double, for building test values.</summary>
    public static byte[] Bytes(double x) => Of(x) ?? throw new ArgumentOutOfRangeException(nameof(x));

    public static double ToDouble(byte[] p)
    {
        var (num, den) = Value(p);
        return (double)num / (double)den;
    }
}
