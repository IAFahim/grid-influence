using System.Runtime.CompilerServices;
#if NET
using System.Numerics;
using System.Runtime.Intrinsics;
#endif

namespace Gi;

internal static unsafe partial class TileBake
{
    internal static void UpdateSummaries(PlaneCells t, int x0, int y0, int x1, int y1)
    {
        if (y1 <= y0) return;

        var summary = t.Summary;
        var page = t.Page;
        var sum = summary->Sum;
        var candidate = short.MinValue;
        var cy = y0;
        for (var y = y0; y < y1; y++)
        {
            RowStats(page + y * TileSize, out var rowSum, out var rowMax);
            sum += rowSum - summary->RowSums[y];
            summary->RowSums[y] = rowSum;
            summary->RowMaxima[y] = rowMax;
            if (rowMax > candidate)
            {
                candidate = rowMax;
                cy = y;
            }
        }

        summary->Sum = sum;
        var max = summary->Max;
        var ax = (int)summary->ArgMaxX;
        var ay = (int)summary->ArgMaxY;
        if (ay >= y0 && ay < y1 && ax >= x0 && ax < x1 && page[ay * TileSize + ax] < max)
        {
            RescanMax(t, out max, out ax, out ay);
            summary->Max = max;
            summary->ArgMaxX = (short)ax;
            summary->ArgMaxY = (short)ay;
            return;
        }

        if (candidate > max)
        {
            summary->Max = candidate;
            summary->ArgMaxX = (short)FirstEqual(page + cy * TileSize, candidate);
            summary->ArgMaxY = (short)cy;
        }
        else if (candidate == max)
        {
            var cx = FirstEqual(page + cy * TileSize, candidate);
            if (cy < ay || (cy == ay && cx < ax))
            {
                summary->ArgMaxX = (short)cx;
                summary->ArgMaxY = (short)cy;
            }
        }
    }

    private static void RescanMax(PlaneCells t, out short max, out int ax, out int ay)
    {
        var maxima = t.Summary->RowMaxima;
        max = maxima[0];
        ay = 0;
        for (var y = 1; y < TileSize; y++)
            if (maxima[y] > max)
            {
                max = maxima[y];
                ay = y;
            }

        ax = FirstEqual(t.Page + ay * TileSize, max);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RowStats(short* row, out int sum, out short max)
    {
#if NET
        if (Vector256.IsHardwareAccelerated)
        {
            var a = Vector256.Load(row);
            var b = Vector256.Load(row + 16);
            var (aLow, aHigh) = Vector256.Widen(a);
            var (bLow, bHigh) = Vector256.Widen(b);
            sum = Vector256.Sum(aLow + aHigh + (bLow + bHigh));
            max = MaxLane(Vector256.Max(a, b));
            return;
        }

        if (Vector128.IsHardwareAccelerated)
        {
            var a = Vector128.Load(row);
            var b = Vector128.Load(row + 8);
            var c = Vector128.Load(row + 16);
            var d = Vector128.Load(row + 24);
            var (aLow, aHigh) = Vector128.Widen(a);
            var (bLow, bHigh) = Vector128.Widen(b);
            var (cLow, cHigh) = Vector128.Widen(c);
            var (dLow, dHigh) = Vector128.Widen(d);
            sum = Vector128.Sum(aLow + aHigh + (bLow + bHigh) + (cLow + cHigh + (dLow + dHigh)));
            max = MaxLane(Vector128.Max(Vector128.Max(a, b), Vector128.Max(c, d)));
            return;
        }
#endif
        var total = 0;
        var best = short.MinValue;
        for (var x = 0; x < TileSize; x++)
        {
            total += row[x];
            if (row[x] > best) best = row[x];
        }

        sum = total;
        max = best;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FirstEqual(short* row, short value)
    {
#if NET
        if (Vector256.IsHardwareAccelerated)
        {
            var target = Vector256.Create(value);
            var low = Vector256.Equals(Vector256.Load(row), target).ExtractMostSignificantBits();
            var high = Vector256.Equals(Vector256.Load(row + 16), target).ExtractMostSignificantBits();
            return BitOperations.TrailingZeroCount(low | (high << 16));
        }

        if (Vector128.IsHardwareAccelerated)
        {
            var target = Vector128.Create(value);
            var bits = Vector128.Equals(Vector128.Load(row), target).ExtractMostSignificantBits()
                       | (Vector128.Equals(Vector128.Load(row + 8), target).ExtractMostSignificantBits() << 8)
                       | (Vector128.Equals(Vector128.Load(row + 16), target).ExtractMostSignificantBits() << 16)
                       | (Vector128.Equals(Vector128.Load(row + 24), target).ExtractMostSignificantBits() << 24);
            return BitOperations.TrailingZeroCount(bits);
        }
#endif
        var x = 0;
        while (row[x] != value) x++;
        return x;
    }

#if NET
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short MaxLane(Vector256<short> v)
    {
        return MaxLane(Vector128.Max(v.GetLower(), v.GetUpper()));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short MaxLane(Vector128<short> v)
    {
        v = Vector128.Max(v, Vector128.Shuffle(v, Vector128.Create(4, 5, 6, 7, 0, 1, 2, 3)));
        v = Vector128.Max(v, Vector128.Shuffle(v, Vector128.Create(2, 3, 0, 1, 6, 7, 4, 5)));
        v = Vector128.Max(v, Vector128.Shuffle(v, Vector128.Create(1, 0, 3, 2, 5, 4, 7, 6)));
        return v.ToScalar();
    }
#endif
}
