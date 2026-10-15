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

        ClearColumnMask(whole, count);
        if (excludes == null) return SenseColumn(w, layer, xs, ys, count, values, whole);

        var complete = true;
        var single = w->GridCount == 1 ? w->Grids : null;
        for (var i = 0; i < count; i++)
        {
            var filled = SensePointAt(w, single, layer, xs[i], ys[i], excludes[i], out values[i]);
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

        ClearColumnMask(whole, count);

        var complete = true;
        var single = w->GridCount == 1 ? w->Grids : null;
        for (var i = 0; i < count; i++)
        {
            var filled = SenseGradientAt(w, single, layer, xs[i], ys[i], excludes == null ? -1 : excludes[i],
                out gradientXs[i], out gradientYs[i]);
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

        ClearColumnMask(whole, count);

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

        ClearColumnMask(whole, count);

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

        ClearColumnMask(found, count);

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
