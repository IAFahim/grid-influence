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
    internal static long BoxSpans(Placement* p, int gain, int tileX, int tileY, int top, int rows, int* lo, int* hi)
    {
        var xLo = stackalloc int[3];
        var xHi = stackalloc int[3];
        var xWeight = stackalloc int[3];
        var yLo = stackalloc int[3];
        var yHi = stackalloc int[3];
        var yWeight = stackalloc int[3];
        var liveX = ClipBands(p->Px, p->Fx, p->ExtentX, tileX, tileX + TileSize, xLo, xHi, xWeight);
        var liveY = ClipBands(p->Py, p->Fy, p->ExtentY, tileY, tileY + TileSize, yLo, yHi, yWeight);
        var sum = 0L;
        for (var y = 0; y < liveY; y++)
        for (var x = 0; x < liveX; x++)
        {
            var value = (long)RoundQ16(p->V->Constant * xWeight[x] * yWeight[y]) * gain;
            if (value == 0) continue;

            var end = Math.Min(yHi[y], top + rows);
            for (var row = Math.Max(yLo[y], top); row < end; row++)
            {
                var width = Math.Min(xHi[x], hi[row - top]) - Math.Max(xLo[x], lo[row - top]);
                if (width > 0) sum += value * width;
            }
        }

        return sum;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    internal static void SmoothWeights(StampKind kind, int origin, int phase, int extent, int lo, int count, long* weights)
    {
        var axis = Axis(kind, origin, phase, extent);
        for (var i = 0; i < count; i++) weights[i] = axis.Weight(lo + i);
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
}
