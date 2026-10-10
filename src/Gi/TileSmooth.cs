using System.Runtime.CompilerServices;

namespace Gi;

internal struct SmoothAxis
{
    public long Half;
    public long Step;
    public long Base;
    public int First;
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
    private const long BellExactHalf = 1L << 15;
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
        var bits = 8 - shift;
        var axis = new SmoothAxis
        {
            Half = half >> shift,
            Step = 1L << bits,
            Base = (1L << (7 - shift)) - (centre >> shift),
            Bell = bell,
        };
        axis.First = (int)(((-axis.Half - axis.Base) >> bits) + 1);
        axis.Last = (int)(-(-(axis.Half - axis.Base) >> bits) - 1);
        return axis;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Normalizer(in SmoothAxis x, in SmoothAxis y)
    {
        var peak = x.Peak * y.Peak;
        return ((1L << 40) + peak / 2) / peak;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static void EmitSmooth(
        long* sums, StampKind kind, int tileX0, int tileY0,
        int px, int py, int fx, int fy, int extentX, int extentY, StampVariant* v, int gain)
    {
        if (gain == 0) return;

        var ax = Axis(kind, px, fx, extentX);
        var ay = Axis(kind, py, fy, extentY);
        var x0 = Math.Max(ax.First, tileX0);
        var x1 = Math.Min(ax.Last, tileX0 + TileSize - 1);
        var y0 = Math.Max(ay.First, tileY0);
        var y1 = Math.Min(ay.Last, tileY0 + TileSize - 1);
        if (x1 < x0 || y1 < y0) return;

        var cols = x1 - x0 + 1;
        var wx = stackalloc long[TileSize];
        for (var i = 0; i < cols; i++) wx[i] = ax.Weight(x0 + i);
        var scale = (long)v->Constant * gain * Normalizer(ax, ay);
        for (var y = y0; y <= y1; y++)
        {
            var dy = scale * ay.Weight(y);
            var row = sums + (y - tileY0) * TileSize + (x0 - tileX0);
            for (var i = 0; i < cols; i++) row[i] += dy * wx[i];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BitLength(long value)
    {
#if NET
        return value <= 0 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)value);
#else
        var bits = 0;
        while (value > 0)
        {
            value >>= 1;
            bits++;
        }

        return bits;
#endif
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
