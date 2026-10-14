using System.Runtime.CompilerServices;

namespace Gi;

public static unsafe partial class World
{
#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    public static bool SenseColumn(
        byte world, byte layer, float* xs, float* ys, int* excludes, int count, short* values, ulong* whole)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;
        if (count <= 0) return true;
        if (xs == null || ys == null || values == null) return false;
        if (excludes == null) return SenseColumn(world, layer, xs, ys, count, values, whole);

        if (whole != null) new Span<ulong>(whole, (count + 63) >> 6).Clear();

        var complete = true;
        for (var i = 0; i < count; i++)
        {
            var filled = excludes[i] >= 0
                ? TrySense(world, layer, xs[i], ys[i], excludes[i], out values[i])
                : TrySense(world, layer, xs[i], ys[i], out values[i]);
            complete &= filled;
            if (filled && whole != null) whole[i >> 6] |= 1ul << (i & 63);
        }

        return complete;
    }

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    public static bool SenseGradientColumn(
        byte world, byte layer, float* xs, float* ys, int* excludes, int count,
        float* gradientXs, float* gradientYs, ulong* whole)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;
        if (count <= 0) return true;
        if (xs == null || ys == null || gradientXs == null || gradientYs == null) return false;

        if (whole != null) new Span<ulong>(whole, (count + 63) >> 6).Clear();

        var complete = true;
        for (var i = 0; i < count; i++)
        {
            bool filled;
            if (excludes != null && excludes[i] >= 0)
                filled = TrySenseGradient(world, layer, xs[i], ys[i], excludes[i], out gradientXs[i], out gradientYs[i]);
            else
                filled = TrySenseGradient(world, layer, xs[i], ys[i], out gradientXs[i], out gradientYs[i]);
            complete &= filled;
            if (filled && whole != null) whole[i >> 6] |= 1ul << (i & 63);
        }

        return complete;
    }

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    public static bool SenseAreaColumn(
        byte world, byte layer, float* xs, float* ys, float* reaches, int* excludes, int count, long* totals, ulong* whole)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;
        if (count <= 0) return true;
        if (xs == null || ys == null || reaches == null || totals == null) return false;

        if (whole != null) new Span<ulong>(whole, (count + 63) >> 6).Clear();

        var complete = true;
        for (var i = 0; i < count; i++)
        {
            bool filled;
            if (excludes != null && excludes[i] >= 0)
                filled = TrySenseArea(world, layer, xs[i], ys[i], reaches[i], excludes[i], out totals[i]);
            else
                filled = TrySenseArea(world, layer, xs[i], ys[i], reaches[i], out totals[i]);
            complete &= filled;
            if (filled && whole != null) whole[i >> 6] |= 1ul << (i & 63);
        }

        return complete;
    }

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    public static bool SenseMaxColumn(
        byte world, byte layer, float* xs, float* ys, float* reaches, int count,
        short* values, float* bestXs, float* bestYs, ulong* whole)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;
        if (count <= 0) return true;
        if (xs == null || ys == null || reaches == null || values == null || bestXs == null || bestYs == null) return false;

        if (whole != null) new Span<ulong>(whole, (count + 63) >> 6).Clear();

        var complete = true;
        for (var i = 0; i < count; i++)
        {
            var filled = TrySenseMax(world, layer, xs[i], ys[i], reaches[i], out values[i], out bestXs[i], out bestYs[i]);
            complete &= filled;
            if (filled && whole != null) whole[i >> 6] |= 1ul << (i & 63);
        }

        return complete;
    }

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    public static bool SenseNearestColumn(
        byte world, byte layer, float* xs, float* ys, float* reaches, short* thresholds, int count,
        short* values, float* nearestXs, float* nearestYs, ulong* found)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;
        if (count <= 0) return true;
        if (xs == null || ys == null || reaches == null || thresholds == null ||
            values == null || nearestXs == null || nearestYs == null) return false;

        if (found != null) new Span<ulong>(found, (count + 63) >> 6).Clear();

        var complete = true;
        for (var i = 0; i < count; i++)
        {
            var filled = TrySenseNearest(world, layer, xs[i], ys[i], reaches[i], thresholds[i],
                out values[i], out nearestXs[i], out nearestYs[i]);
            complete &= filled;
            if (values[i] != short.MinValue && found != null) found[i >> 6] |= 1ul << (i & 63);
        }

        return complete;
    }
}
