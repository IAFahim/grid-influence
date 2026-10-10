#if NET
using System.Runtime.CompilerServices;
#endif

namespace Gi;

internal static unsafe partial class TileBake
{
    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    internal static int BoxAt(int px, int py, int fx, int fy, int extentX, int extentY, StampVariant* v, int gain, int cx, int cy)
    {
        var xLo = stackalloc int[3];
        var xHi = stackalloc int[3];
        var xWeight = stackalloc int[3];
        var yLo = stackalloc int[3];
        var yHi = stackalloc int[3];
        var yWeight = stackalloc int[3];
        var liveX = ClipBands(px, fx, extentX, cx, cx + 1, xLo, xHi, xWeight);
        var liveY = ClipBands(py, fy, extentY, cy, cy + 1, yLo, yHi, yWeight);
        var value = 0;
        for (var y = 0; y < liveY; y++)
        for (var x = 0; x < liveX; x++)
            value += RoundQ16(v->Constant * xWeight[x] * yWeight[y]) * gain;
        return value;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    internal static void BoxCells(
        int* cells, int tileX0, int tileY0, int px, int py, int fx, int fy, int extentX, int extentY, StampVariant* v, int gain)
    {
        new Span<int>(cells, TileSize * TileSize).Clear();
        var xLo = stackalloc int[3];
        var xHi = stackalloc int[3];
        var xWeight = stackalloc int[3];
        var yLo = stackalloc int[3];
        var yHi = stackalloc int[3];
        var yWeight = stackalloc int[3];
        var liveX = ClipBands(px, fx, extentX, tileX0, tileX0 + TileSize, xLo, xHi, xWeight);
        var liveY = ClipBands(py, fy, extentY, tileY0, tileY0 + TileSize, yLo, yHi, yWeight);
        for (var y = 0; y < liveY; y++)
        for (var x = 0; x < liveX; x++)
        {
            var value = RoundQ16(v->Constant * xWeight[x] * yWeight[y]) * gain;
            if (value == 0) continue;

            for (var row = yLo[y]; row < yHi[y]; row++)
            for (var col = xLo[x]; col < xHi[x]; col++)
                cells[row * TileSize + col] += value;
        }
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    internal static void SmoothWeights(StampKind kind, int origin, int phase, int extent, long curve, int lo, int count, long* weights)
    {
        TentGeometry(origin, phase, extent, out var first, out var peakCell, out var last, out var up, out var down, out _);
        if (kind == StampKind.Tent)
        {
            for (var i = 0; i < count; i++) weights[i] = TentWeight(lo + i, first, peakCell, last, up, down);
            return;
        }

        var h = last - first + 1;
        for (var i = 0; i < count; i++) weights[i] = BellWeight(lo + i, first, last, curve, h * h);
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    internal static long Quadrant(int* difference, int lx, int ly)
    {
        var sum = 0;
        for (var y = 0; y <= ly; y++)
        {
            var row = difference + y * DiffPitch;
            for (var x = 0; x <= lx; x++) sum += row[x];
        }

        return sum;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    internal static long Quadrant(long* impulses, int lx, int ly, int order)
    {
        var sum = 0L;
        for (var y = 0; y <= ly; y++)
        {
            var row = impulses + y * DiffPitch;
            var rowSum = 0L;
            for (var x = 0; x <= lx; x++) rowSum += row[x] * Binomial(lx - x, order);
            sum += rowSum * Binomial(ly - y, order);
        }

        return sum;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    internal static void Integrate(long* impulses, int rows, int cols, int order, long* cells)
    {
        var p = stackalloc long[TileSize];
        var q = stackalloc long[TileSize];
        var r = stackalloc long[TileSize];
        new Span<long>(p, TileSize).Clear();
        new Span<long>(q, TileSize).Clear();
        new Span<long>(r, TileSize).Clear();
        for (var y = 0; y < rows; y++)
        {
            var row = impulses + y * DiffPitch;
            var target = cells + y * TileSize;
            var run = 0L;
            var run2 = 0L;
            var run3 = 0L;
            for (var x = 0; x < cols; x++)
            {
                run += row[x];
                p[x] += run;
                run2 += p[x];
                q[x] += run2;
                if (order == 2)
                {
                    target[x] = q[x];
                    continue;
                }

                run3 += q[x];
                r[x] += run3;
                target[x] = r[x];
            }
        }
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    internal static void IntegrateBoxes(int* difference, int* dense, int rows, int cols, int* cells)
    {
        var previous = stackalloc int[TileSize];
        new Span<int>(previous, TileSize).Clear();
        for (var y = 0; y < rows; y++)
        {
            var row = difference + y * DiffPitch;
            var carry = 0;
            for (var x = 0; x < cols; x++)
            {
                carry += row[x];
                previous[x] += carry;
                cells[y * TileSize + x] = previous[x] + dense[y * TileSize + x];
            }
        }
    }

    private static long Binomial(int distance, int order)
        => order == 2 ? distance + 1 : (long)(distance + 1) * (distance + 2) / 2;
}
