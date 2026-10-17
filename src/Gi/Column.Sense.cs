#if NET
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
#endif

namespace Gi;

public static unsafe partial class World
{
#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    public static bool SenseColumn(
        byte world, byte layer, float* xs, float* ys, SourceId* excludes, int count, short* values, ulong* whole)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return Unfilled(values, whole, count);
        if (count <= 0) return true;
        if (xs == null || ys == null || values == null) return Unfilled(values, whole, count);
        if (excludes == null) return SenseColumn(world, layer, xs, ys, count, values, whole);

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

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    public static bool SenseGradientColumn(
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
    public static bool SenseGradientColumn(
        byte world, byte layer, float* xs, float* ys, SourceId* excludes, int count,
        short* values, float* gradientXs, float* gradientYs, ulong* whole)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return UnfilledValueGradient(values, gradientXs, gradientYs, whole, count);
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
                filled &= TrySenseGradient(world, layer, xs[i], ys[i], excludes[i], out gradientXs[i], out gradientYs[i]);
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

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    internal static bool GradientRange(WorldCtx* w, byte layer, float* xs, float* ys,
        float* gradientXs, float* gradientYs, ulong* whole, int from, int to)
        => GradientRange(w, layer, xs, ys, null, gradientXs, gradientYs, whole, from, to);

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    internal static bool GradientRange(WorldCtx* w, byte layer, float* xs, float* ys, short* values,
        float* gradientXs, float* gradientYs, ulong* whole, int from, int to)
    {
        var complete = true;
        if (w->GridCount == 1)
        {
            var g = w->Grids;
            var scaleQ8 = g->ScaleQ8;
            if (scaleQ8 != 0)
            {
                var ox = g->OriginX;
                var oy = g->OriginY;
                var size = g->Size;
                var ld = g->Layers + layer;
                var perUnit = scaleQ8 / 512f;
                var done = from;
#if NET
                if (Vector256.IsHardwareAccelerated) done = GradientBlocks(g, ld, xs, ys, values, gradientXs, gradientYs, whole, from, to, ref complete);
#endif
                for (var i = done; i < to; i++)
                {
                    var x = xs[i];
                    var y = ys[i];
                    var qx = CellQ8(x, ox, scaleQ8);
                    var qy = CellQ8(y, oy, scaleQ8);
                    if (!Contains(g, qx, qy))
                    {
                        if (values != null) values[i] = 0;
                        gradientXs[i] = 0f;
                        gradientYs[i] = 0f;
                        complete = false;
                        continue;
                    }

                    var cx = (int)(qx >> 8);
                    var cy = (int)(qy >> 8);
                    var tile = (cy >> TileBake.TileBits) * g->TilesPerSide + (cx >> TileBake.TileBits);
                    var page = ld->PageOf(tile);
                    var lx = cx & (TileBake.TileSize - 1);
                    var ly = cy & (TileBake.TileSize - 1);
                    if (page != null && (uint)(lx - 1) < TileBake.TileSize - 2 && (uint)(ly - 1) < TileBake.TileSize - 2)
                    {
                        var at = page + ly * TileBake.TileSize + lx;
                        if (values != null) values[i] = *at;
                        gradientXs[i] = (at[1] - at[-1]) * perUnit;
                        gradientYs[i] = (at[TileBake.TileSize] - at[-TileBake.TileSize]) * perUnit;
                    }
                    else
                    {
                        if (values != null) values[i] = page == null ? (short)0 : page[ly * TileBake.TileSize + lx];
                        gradientXs[i] = (CellNear(g, ld, tile, page, cx + 1, cy) -
                            CellNear(g, ld, tile, page, cx - 1, cy)) * perUnit;
                        gradientYs[i] = (CellNear(g, ld, tile, page, cx, cy + 1) -
                            CellNear(g, ld, tile, page, cx, cy - 1)) * perUnit;
                    }

                    if (cx - 1 >= 0 && cy - 1 >= 0 && cx + 1 < size && cy + 1 < size)
                    {
                        if (whole != null) whole[i >> 6] |= 1ul << (i & 63);
                    }
                    else
                    {
                        complete = false;
                    }
                }

                return complete;
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

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    public static bool SenseAreaColumn(
        byte world, byte layer, float* xs, float* ys, float* reaches, SourceId* excludes, int count, long* totals, ulong* whole)
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

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    public static bool SenseMaxColumn(
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
