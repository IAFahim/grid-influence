#if NET
using System.Runtime.CompilerServices;
#endif

namespace Gi;

public static unsafe partial class World
{
    public static bool SenseColumn(byte world, byte layer, float* xs, float* ys, int count, short* values, ulong* whole)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return Unfilled(values, whole, count);
        if (count <= 0) return true;
        if (xs == null || ys == null || values == null) return Unfilled(values, whole, count);

        ClearBits(whole, count);

        return SenseRange(w, layer, xs, ys, values, whole, 0, count) == 0;
    }

    public static int ChangedMask(byte world, byte layer, float* xs, float* ys, float reach, int since, int count, ulong* words)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount || xs == null || ys == null || words == null || !float.IsFinite(reach))
        {
            ClearBits(words, count);
            return 0;
        }

        if (count <= 0) return 0;

        ClearBits(words, count);

        return ChangedRange(w, layer, xs, ys, reach, since, words, 0, count);
    }

    public static void Sync(byte world, SourceId* sources, float* xs, float* ys, int count)
    {
        var w = GetContext(world);
        if (w == null) return;

        SyncColumns(w, sources, xs, ys, count);
    }

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    internal static void SyncColumns(WorldCtx* w, SourceId* sources, float* xs, float* ys, int count)
    {
        if (count <= 0 || sources == null || xs == null || ys == null) return;

        var s = &w->Sources;
        if (w->GridCount == 1)
        {
            var g0 = w->Grids;
            var ox = g0->OriginX;
            var oy = g0->OriginY;
            var scaleQ8 = g0->ScaleQ8;
            if (scaleQ8 != 0)
            {
                var recipes = w->Recipes;
                for (var i = 0; i < count; i++)
                {
                    var id = sources[i];
                    var slot = (int)id.Slot;
                    if ((uint)slot >= (uint)s->Count || s->Alive[slot] == 0) continue;
                    if (id.Generation != s->Gen[slot]) continue;

                    var x = xs[i];
                    var y = ys[i];
                    var oldX = s->X[slot];
                    var oldY = s->Y[slot];
                    if (*(int*)&x == *(int*)&oldX && *(int*)&y == *(int*)&oldY) continue;
                    if (!float.IsFinite(x) || !float.IsFinite(y)) continue;
                    var mask = recipes[s->Layer[slot]].AnchorMask;
                    var dx = TileBake.AnchorQ8(oldX, ox, scaleQ8, mask) ^ TileBake.AnchorQ8(x, ox, scaleQ8, mask);
                    var dy = TileBake.AnchorQ8(oldY, oy, scaleQ8, mask) ^ TileBake.AnchorQ8(y, oy, scaleQ8, mask);
                    if ((dx | dy) != 0)
                        EnqueueMutation(w, s, slot, x, y, (sbyte)s->Gain[slot], s->Angle[slot], s->Scale[slot], true);
                    s->X[slot] = x;
                    s->Y[slot] = y;
                }

                return;
            }
        }

        for (var i = 0; i < count; i++)
        {
            var id = sources[i];
            var slot = (int)id.Slot;
            if ((uint)slot >= (uint)s->Count || s->Alive[slot] == 0) continue;
            if (id.Generation != s->Gen[slot]) continue;

            var x = xs[i];
            var y = ys[i];
            var oldX = s->X[slot];
            var oldY = s->Y[slot];
            if (*(int*)&x == *(int*)&oldX && *(int*)&y == *(int*)&oldY) continue;
            if (!float.IsFinite(x) || !float.IsFinite(y)) continue;
            if (PositionChanged(w, s->Layer[slot], oldX, oldY, x, y))
                EnqueueMutation(w, s, slot, x, y, (sbyte)s->Gain[slot], s->Angle[slot], s->Scale[slot], true);
            s->X[slot] = x;
            s->Y[slot] = y;
        }
    }

    internal static int SenseRange(WorldCtx* w, byte layer, float* xs, float* ys, short* values, ulong* whole, int from, int to)
    {
        var gaps = 0;
        if (w->GridCount == 1)
        {
            var only = w->Grids;
            var scaleQ8 = only->ScaleQ8;
            if (scaleQ8 != 0)
            {
                var ox = only->OriginX;
                var oy = only->OriginY;
                var ld = only->Layers + layer;
                for (var i = from; i < to; i++)
                {
                    var qx = CellQ8(xs[i], ox, scaleQ8);
                    var qy = CellQ8(ys[i], oy, scaleQ8);
                    if (!Contains(only, qx, qy))
                    {
                        values[i] = 0;
                        gaps++;
                        continue;
                    }

                    var cx = (int)(qx >> 8);
                    var cy = (int)(qy >> 8);
                    var block = ld->PageOf((cy >> TileBake.TileBits) * only->TilesPerSide + (cx >> TileBake.TileBits));
                    values[i] = block == null
                        ? (short)0
                        : ((short*)(block + PageOffset))[(cy & (TileBake.TileSize - 1)) * TileBake.TileSize + (cx & (TileBake.TileSize - 1))];
                    if (whole != null) whole[i >> 6] |= 1ul << (i & 63);
                }

                return gaps;
            }
        }

        for (var i = from; i < to; i++)
        {
            var x = xs[i];
            var y = ys[i];
            GridCtx* picked = null;
            var bestScale = 0;
            var qx = 0L;
            var qy = 0L;
            for (var gi = 0; gi < w->GridCount; gi++)
            {
                var g = w->Grids + gi;
                if (g->ScaleQ8 == 0) continue;

                var ax = CellQ8(x, g->OriginX, g->ScaleQ8);
                var ay = CellQ8(y, g->OriginY, g->ScaleQ8);
                if (!Contains(g, ax, ay)) continue;
                if (picked != null && g->ScaleQ8 <= bestScale) continue;

                picked = g;
                bestScale = g->ScaleQ8;
                qx = ax;
                qy = ay;
            }

            short value = 0;
            if (picked != null)
            {
                var cx = (int)(qx >> 8);
                var cy = (int)(qy >> 8);
                var block = picked->Layers[layer].PageOf(
                    (cy >> TileBake.TileBits) * picked->TilesPerSide + (cx >> TileBake.TileBits));
                if (block != null)
                    value = ((short*)(block + PageOffset))[(cy & (TileBake.TileSize - 1)) * TileBake.TileSize + (cx & (TileBake.TileSize - 1))];
            }

            values[i] = value;
            if (picked == null)
            {
                gaps++;
                continue;
            }

            if (whole != null) whole[i >> 6] |= 1ul << (i & 63);
        }

        return gaps;
    }

    internal static int ChangedRange(WorldCtx* w, byte layer, float* xs, float* ys, float reach, int since, ulong* words, int from, int to)
    {
        var hits = 0;
        for (var i = from; i < to; i++)
        {
            if (!ChangedOne(w, layer, xs[i], ys[i], reach, since)) continue;
            words[i >> 6] |= 1ul << (i & 63);
            hits++;
        }

        return hits;
    }

    private static bool ChangedOne(WorldCtx* w, byte layer, float x, float y, float reach, int since)
    {
        if (!Pick(w, x, y, reach, 0, true, out var gi, out _)) return false;

        var g = w->Grids + gi;
        var epochs = g->Layers[layer].Epochs;
        if (epochs == null) return false;

        var disk = Disk(g, x, y, reach);
        for (var ty = disk.Y0 >> TileBake.TileBits; ty <= disk.Y1 >> TileBake.TileBits; ty++)
        for (var tx = disk.X0 >> TileBake.TileBits; tx <= disk.X1 >> TileBake.TileBits; tx++)
            if (epochs[ty * g->TilesPerSide + tx] - since > 0 && HoldsDiskCell(disk, tx, ty)) return true;
        return false;
    }
}
