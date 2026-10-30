using System.Runtime.CompilerServices;

namespace Gi;

internal static unsafe partial class TileBake
{
    [MethodImpl(Jit.Hot)]
    internal static int BoxAt(int px, int py, int fx, int fy, int extentX, int extentY, StampVariant* v, int gain,
        int cx, int cy)
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

    [MethodImpl(Jit.Hot)]
    internal static void BoxCells(
        int* cells, int tileX0, int tileY0, int px, int py, int fx, int fy, int extentX, int extentY, StampVariant* v,
        int gain)
    {
        new Span<int>(cells, TileSize * TileSize).Clear();
        AccumulateBoxCells(cells, tileX0, tileY0, px, py, fx, fy, extentX, extentY, v, gain);
    }

    [MethodImpl(Jit.Hot)]
    private static void AccumulateBoxCells(
        int* cells, int tileX0, int tileY0, int px, int py, int fx, int fy, int extentX, int extentY, StampVariant* v,
        int gain)
    {
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

    [MethodImpl(Jit.Hot)]
    internal static void SmoothWeights(StampKind kind, int origin, int phase, int extent, int lo, int count,
        long* weights)
    {
        var axis = Axis(kind, origin, phase, extent);
        for (var i = 0; i < count; i++) weights[i] = axis.Weight(lo + i);
    }

}
