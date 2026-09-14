using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GridInfluence;

[StructLayout(LayoutKind.Sequential)]
public readonly struct CellRect
{
    public readonly Int2 Min;
    public readonly Int2 Max;

    public CellRect(Int2 min, Int2 max)
    {
        Min = min;
        Max = max;
    }

    public static CellRect Empty => default;

    public bool IsEmpty => Max.X <= Min.X || Max.Y <= Min.Y;
}

public readonly struct ChunkRange
{
    public readonly Int2 Min;
    public readonly Int2 Max;

    public ChunkRange(Int2 min, Int2 max)
    {
        Min = min;
        Max = max;
    }
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct WeightedRect
{
    public readonly CellRect Bounds;
    public readonly int Weight;

    public WeightedRect(CellRect bounds, int weight)
    {
        Bounds = bounds;
        Weight = weight;
    }

    public static WeightedRect Empty => default;

    public bool IsEmpty => Bounds.IsEmpty;
}

public static class IntegerMath
{
    public static int DecayKeep(int value, int decayPerMille)
        => (int)((long)value * (1000 - decayPerMille) / 1000);

    public static int Outflow(int value, int decayPerMille, int spreadDenominator)
        => DecayKeep(value, decayPerMille) / (spreadDenominator < 1 ? 1 : spreadDenominator);

    public static long FloorSqrt(long value)
    {
        if (value <= 0) return 0;

        const ulong maxRoot = 3037000499ul;

        var v = (ulong)value;
        var root = (ulong)Math.Sqrt(value);
        if (root > maxRoot) root = maxRoot;

        while (root * root > v) root--;

        while (root < maxRoot && (root + 1) * (root + 1) <= v) root++;

        return (long)root;
    }

    public static int ClampToInt(long value)
    {
        if (value <= 0) return 0;

        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    public static int SaturatingAdd(int a, int b) => ClampToInt((long)a + b);

    public static long FloorDiv(long a, long b)
    {
        var q = a / b;
        if (a % b != 0 && a < 0 != b < 0) q--;
        return q;
    }

    public static long CeilDiv(long a, long b)
    {
        var q = a / b;
        if (a % b != 0 && a < 0 == b < 0) q++;
        return q;
    }
}
