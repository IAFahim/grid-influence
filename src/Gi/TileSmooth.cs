using System.Runtime.CompilerServices;

namespace Gi;

internal struct SmoothAxis
{
    public long Half;
    public long Step;
    public long Base;
    public int First;
    public int Centre;
    public int Last;
    public bool Bell;

    public readonly long Peak => Bell ? Half * Half : Half;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly long Weight(int cell)
    {
        var d = cell * Step + Base;
        if (Bell)
        {
            var w = Half * Half - d * d;
            return w > 0 ? w : 0;
        }

        var t = Half - (d < 0 ? -d : d);
        return t > 0 ? t : 0;
    }
}

internal static unsafe partial class TileBake
{
    internal const int MaxSmoothImpulses = 20;
    internal const long BellExactHalf = 1L << 15;
    private const long MinSmoothHalf = 256;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static StampKind Effective(StampVariant* v, int extentX, int extentY)
    {
        if (v->Kind != StampKind.Bell) return v->Kind;
        return extentX >> 1 >= BellExactHalf || extentY >> 1 >= BellExactHalf ? StampKind.Raster : StampKind.Bell;
    }

    internal static SmoothAxis Axis(StampKind kind, int origin, int phase, int extent)
    {
        var bell = kind == StampKind.Bell;
        var half = Math.Max(MinSmoothHalf, extent >> 1);
        var centre = ((long)origin << 8) + phase + (extent >> 1);
        var shift = Math.Clamp(BitLength(half) - (bell ? 8 : 15), 0, 7);
        var axis = new SmoothAxis
        {
            Half = half >> shift,
            Step = 1L << (8 - shift),
            Base = (1L << (7 - shift)) - (centre >> shift),
            Bell = bell,
        };
        axis.First = (int)(FloorDiv(-axis.Half - axis.Base, axis.Step) + 1);
        axis.Last = (int)(-FloorDiv(-(axis.Half - axis.Base), axis.Step) - 1);
        axis.Centre = (int)FloorDiv(-axis.Base, axis.Step);
        return axis;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Normalizer(in SmoothAxis x, in SmoothAxis y)
    {
        var peak = x.Peak * y.Peak;
        return ((1L << 40) + peak / 2) / peak;
    }

    internal static void EmitSmooth(
        long* buffer, StampKind kind, int tileX0, int tileY0,
        int px, int py, int fx, int fy, int extentX, int extentY, StampVariant* v, int gain)
    {
        if (gain == 0) return;

        var ax = Axis(kind, px, fx, extentX);
        var ay = Axis(kind, py, fy, extentY);
        var order = kind == StampKind.Tent ? 2 : 3;
        var xCells = stackalloc int[MaxSmoothImpulses];
        var xDeltas = stackalloc long[MaxSmoothImpulses];
        var yCells = stackalloc int[MaxSmoothImpulses];
        var yDeltas = stackalloc long[MaxSmoothImpulses];
        var liveX = Impulses(ax, order, tileX0, xCells, xDeltas);
        var liveY = Impulses(ay, order, tileY0, yCells, yDeltas);

        var scale = (long)v->Constant * gain * Normalizer(ax, ay);
        for (var y = 0; y < liveY; y++)
        {
            var row = yCells[y] * DiffPitch;
            var dy = scale * yDeltas[y];
            for (var x = 0; x < liveX; x++)
            {
                var value = dy * xDeltas[x];
                if (value == 0) continue;

                buffer[row + xCells[x]] += value;
            }
        }
    }

    private static int Impulses(in SmoothAxis a, int order, int tileLo, int* cells, long* deltas)
    {
        var count = 0;
        var w0 = a.Weight(tileLo);
        var w1 = a.Weight(tileLo + 1);
        count = Push(cells, deltas, count, 0, w0);
        if (order == 2)
        {
            count = Push(cells, deltas, count, 1, w1 - 2 * w0);
        }
        else
        {
            var w2 = a.Weight(tileLo + 2);
            count = Push(cells, deltas, count, 1, w1 - 3 * w0);
            count = Push(cells, deltas, count, 2, w2 - 3 * w1 + 3 * w0);
        }

        var windows = stackalloc int[3];
        windows[0] = a.First;
        windows[1] = a.Centre;
        windows[2] = a.Last + 1;
        for (var k = 0; k < 3; k++)
        for (var c = windows[k] - 1; c <= windows[k] + order; c++)
        {
            var local = c - tileLo;
            if (local < order || local >= TileSize) continue;

            var seen = false;
            for (var i = 0; i < count; i++) seen |= cells[i] == local;
            if (seen) continue;

            count = Push(cells, deltas, count, local, Difference(a, c, order));
        }

        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Push(int* cells, long* deltas, int count, int local, long delta)
    {
        if (delta == 0) return count;
        cells[count] = local;
        deltas[count] = delta;
        return count + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Difference(in SmoothAxis a, int c, int order)
        => order == 2
            ? a.Weight(c) - 2 * a.Weight(c - 1) + a.Weight(c - 2)
            : a.Weight(c) - 3 * a.Weight(c - 1) + 3 * a.Weight(c - 2) - a.Weight(c - 3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long FloorDiv(long value, long divisor)
    {
        var q = value / divisor;
        return q * divisor > value ? q - 1 : q;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BitLength(long value)
    {
        var bits = 0;
        while (value > 0)
        {
            value >>= 1;
            bits++;
        }

        return bits;
    }

    internal static void BellSamples(sbyte* samples, int width, int height, sbyte value)
    {
        var w2 = (long)width * width;
        var h2 = (long)height * height;
        for (var y = 0; y < height; y++)
        {
            var ey = 2L * y + 1 - height;
            var wy = h2 - ey * ey;
            for (var x = 0; x < width; x++)
            {
                var ex = 2L * x + 1 - width;
                var wx = w2 - ex * ex;
                var numerator = value * wx * wy;
                var denominator = w2 * h2;
                var magnitude = ((numerator < 0 ? -numerator : numerator) + denominator / 2) / denominator;
                samples[y * width + x] = (sbyte)(numerator < 0 ? -magnitude : magnitude);
            }
        }
    }
}
