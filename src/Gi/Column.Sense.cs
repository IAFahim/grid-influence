using System.Runtime.CompilerServices;
namespace Gi;

public static unsafe partial class World
{
    [MethodImpl(Jit.Hot)]
    private static bool SenseColumnCore(
        byte world, byte layer, float* xs, float* ys, SourceId* excludes, int count, short* values, ulong* whole)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return Unfilled(values, whole, count);
        if (count <= 0) return true;
        if (xs == null || ys == null || values == null) return Unfilled(values, whole, count);
        if (excludes == null) return SenseColumnCore(world, layer, xs, ys, count, values, whole);

        ClearBits(whole, count);

        var complete = true;
        for (var i = 0; i < count; i++)
        {
            var filled = excludes[i].Generation != 0
                ? TrySense(world, layer, xs[i], ys[i], excludes[i], out values[i])
                : TrySense(world, layer, xs[i], ys[i], out values[i]);
            complete &= filled;
            if (filled && whole != null) whole[i >> 6] |= 1ul << (i & 63);
        }

        return complete;
    }

    [MethodImpl(Jit.Hot)]
    private static bool SenseGradientColumnCore(
        byte world, byte layer, float* xs, float* ys, SourceId* excludes, int count,
        float* gradientXs, float* gradientYs, ulong* whole)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return UnfilledGradient(gradientXs, gradientYs, whole, count);
        if (count <= 0) return true;
        if (xs == null || ys == null || gradientXs == null || gradientYs == null)
            return UnfilledGradient(gradientXs, gradientYs, whole, count);

        ClearBits(whole, count);
        if (excludes == null) return GradientRange(w, layer, xs, ys, gradientXs, gradientYs, whole, 0, count);

        var complete = true;
        for (var i = 0; i < count; i++)
        {
            bool filled;
            if (excludes[i].Generation != 0)
                filled = TrySenseGradient(world, layer, xs[i], ys[i], excludes[i], out gradientXs[i],
                    out gradientYs[i]);
            else
                filled = TrySenseGradient(world, layer, xs[i], ys[i], out gradientXs[i], out gradientYs[i]);
            complete &= filled;
            if (filled && whole != null) whole[i >> 6] |= 1ul << (i & 63);
        }

        return complete;
    }

    [MethodImpl(Jit.Hot)]
    private static bool SenseGradientColumnCore(
        byte world, byte layer, float* xs, float* ys, SourceId* excludes, int count,
        short* values, float* gradientXs, float* gradientYs, ulong* whole)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount)
            return UnfilledValueGradient(values, gradientXs, gradientYs, whole, count);
        if (count <= 0) return true;
        if (xs == null || ys == null || values == null || gradientXs == null || gradientYs == null)
            return UnfilledValueGradient(values, gradientXs, gradientYs, whole, count);

        ClearBits(whole, count);
        if (excludes == null) return GradientRange(w, layer, xs, ys, values, gradientXs, gradientYs, whole, 0, count);

        var complete = true;
        for (var i = 0; i < count; i++)
        {
            bool filled;
            if (excludes[i].Generation != 0)
            {
                filled = TrySense(world, layer, xs[i], ys[i], excludes[i], out values[i]);
                filled &= TrySenseGradient(world, layer, xs[i], ys[i], excludes[i], out gradientXs[i],
                    out gradientYs[i]);
            }
            else
            {
                filled = TrySense(world, layer, xs[i], ys[i], out values[i]);
                filled &= TrySenseGradient(world, layer, xs[i], ys[i], out gradientXs[i], out gradientYs[i]);
            }

            complete &= filled;
            if (filled && whole != null) whole[i >> 6] |= 1ul << (i & 63);
        }

        return complete;
    }

    [MethodImpl(Jit.Hot)]
    private static bool GradientRange(WorldCtx* w, byte layer, float* xs, float* ys,
        float* gradientXs, float* gradientYs, ulong* whole, int from, int to)
    {
        return GradientRange(w, layer, xs, ys, null, gradientXs, gradientYs, whole, from, to);
    }

    [MethodImpl(Jit.Hot)]
    private static bool GradientRange(WorldCtx* w, byte layer, float* xs, float* ys, short* values,
        float* gradientXs, float* gradientYs, ulong* whole, int from, int to)
    {
        var complete = true;
        if (w->GridCount == 1)
        {
            var g = w->Grids;
            if (g->ScaleQ8 != 0)
            {
                var field = FieldOf(g, g->Layers + layer);
                return GradientSingle(field, xs, ys, values, gradientXs, gradientYs, whole, from, to);
            }
        }

        var world = (byte)(w - Runtime.Worlds);
        for (var i = from; i < to; i++)
        {
            var filled = TrySenseGradient(world, layer, xs[i], ys[i], out gradientXs[i], out gradientYs[i]);
            if (values != null) filled &= TrySense(world, layer, xs[i], ys[i], out values[i]);
            complete &= filled;
            if (filled && whole != null) whole[i >> 6] |= 1ul << (i & 63);
        }

        return complete;
    }

    [MethodImpl(Jit.Hot)]
    private static bool SenseAreaColumnCore(
        byte world, byte layer, float* xs, float* ys, float* reaches, SourceId* excludes, int count, long* totals,
        ulong* whole)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return UnfilledArea(totals, whole, count);
        if (count <= 0) return true;
        if (xs == null || ys == null || reaches == null || totals == null) return UnfilledArea(totals, whole, count);

        ClearBits(whole, count);

        var complete = true;
        for (var i = 0; i < count; i++)
        {
            bool filled;
            if (excludes != null && excludes[i].Generation != 0)
                filled = TrySenseArea(world, layer, xs[i], ys[i], reaches[i], excludes[i], out totals[i]);
            else
                filled = TrySenseArea(world, layer, xs[i], ys[i], reaches[i], out totals[i]);
            complete &= filled;
            if (filled && whole != null) whole[i >> 6] |= 1ul << (i & 63);
        }

        return complete;
    }

    [MethodImpl(Jit.Hot)]
    private static bool SenseMaxColumnCore(
        byte world, byte layer, float* xs, float* ys, float* reaches, int count,
        short* values, float* bestXs, float* bestYs, ulong* whole)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return UnfilledMax(values, bestXs, bestYs, whole, count);
        if (count <= 0) return true;
        if (xs == null || ys == null || reaches == null || values == null || bestXs == null || bestYs == null)
            return UnfilledMax(values, bestXs, bestYs, whole, count);

        ClearBits(whole, count);

        var complete = true;
        for (var i = 0; i < count; i++)
        {
            var filled = TrySenseMax(world, layer, xs[i], ys[i], reaches[i], out values[i], out bestXs[i],
                out bestYs[i]);
            complete &= filled;
            if (filled && whole != null) whole[i >> 6] |= 1ul << (i & 63);
        }

        return complete;
    }

    [MethodImpl(Jit.Hot)]
    private static bool SenseNearestColumnCore(
        byte world, byte layer, float* xs, float* ys, float* reaches, short* thresholds, int count,
        short* values, float* nearestXs, float* nearestYs, ulong* found)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return UnfilledNearest(values, nearestXs, nearestYs, found, count);
        if (count <= 0) return true;
        if (xs == null || ys == null || reaches == null || thresholds == null ||
            values == null || nearestXs == null || nearestYs == null)
            return UnfilledNearest(values, nearestXs, nearestYs, found, count);

        ClearBits(found, count);

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
