using System.Runtime.CompilerServices;

namespace Gi;

public static unsafe partial class World
{
    public static bool SenseColumn(byte world, byte layer, float* xs, float* ys, int count, short* values, ulong* whole)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;
        if (count <= 0) return true;
        if (xs == null || ys == null || values == null) return false;

        ClearColumnMask(whole, count);

        return SenseColumn(w, layer, xs, ys, count, values, whole);
    }

    private static bool SenseColumn(WorldCtx* w, byte layer, float* xs, float* ys, int count, short* values, ulong* whole)
    {
        if (count >= ResolvePool.ColumnThreshold && ResolvePool.TryAcquire())
        {
            var gaps = ResolvePool.SenseColumns(w, layer, xs, ys, values, whole, count);
            ResolvePool.Release();
            return gaps == 0;
        }

        return SenseRange(w, layer, xs, ys, values, whole, 0, count) == 0;
    }

    public static int ChangedMask(byte world, byte layer, float* xs, float* ys, float reach, int since, int count, ulong* words)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount || count <= 0) return 0;
        if (xs == null || ys == null || words == null) return 0;

        ClearColumnMask(words, count);

        if (count >= ResolvePool.ColumnThreshold && ResolvePool.TryAcquire())
        {
            var hits = ResolvePool.ChangedColumns(w, layer, xs, ys, reach, since, words, count);
            ResolvePool.Release();
            return hits;
        }

        return ChangedRange(w, layer, xs, ys, reach, since, words, 0, count);
    }

    public static void Sync(byte world, int* sources, float* xs, float* ys, int count)
    {
        var w = GetContext(world);
        if (w == null) return;

        SyncColumns(w, sources, xs, ys, count);
    }

    internal static void SyncColumns(WorldCtx* w, int* sources, float* xs, float* ys, int count)
    {
        if (count <= 0 || sources == null || xs == null || ys == null) return;

        var s = &w->Sources;
        for (var i = 0; i < count; i++)
        {
            var id = sources[i];
            var slot = id & SourceIndexMask;
            if ((uint)slot >= (uint)s->Count || s->Alive.Pointer[slot] == 0) continue;
            if ((id & ~SourceIndexMask) != (s->Gen.Pointer[slot] << 24)) continue;

            var x = xs[i];
            var y = ys[i];
            if (!float.IsFinite(x) || !float.IsFinite(y)) continue;
            if (*(int*)&x == *(int*)&s->X.Pointer[slot] && *(int*)&y == *(int*)&s->Y.Pointer[slot]) continue;
            EnqueueMutation(w, s, slot, x, y, (sbyte)s->Gain.Pointer[slot], s->Angle.Pointer[slot], s->Scale.Pointer[slot], true);
            s->X.Pointer[slot] = x;
            s->Y.Pointer[slot] = y;
        }
    }

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    internal static int SenseRange(WorldCtx* w, byte layer, float* xs, float* ys, short* values, ulong* whole, int from, int to)
    {
        var gaps = 0;
        var single = w->GridCount == 1 ? w->Grids : null;
        for (var i = from; i < to; i++)
        {
            var picked = PickPointGrid(w, single, xs[i], ys[i], 0, out var cx, out var cy, out _);
            values[i] = picked == null ? (short)0 : Cell(picked, layer, cx, cy);
            if (picked == null)
            {
                gaps++;
                continue;
            }

            if (whole != null) whole[i >> 6] |= 1ul << (i & 63);
        }

        return gaps;
    }

    private static void ClearColumnMask(ulong* words, int count)
    {
        if (words != null) new Span<ulong>(words, ((count - 1) >> 6) + 1).Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool SensePointAt(WorldCtx* w, byte layer, float x, float y, int exclude, out short value)
        => SensePointAt(w, w->GridCount == 1 ? w->Grids : null, layer, x, y, exclude, out value);

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    private static bool SensePointAt(WorldCtx* w, GridCtx* single, byte layer, float x, float y, int exclude, out short value)
    {
        value = 0;
        var g = PickPointGrid(w, single, x, y, 0, out var cx, out var cy, out _);
        if (g == null) return false;

        value = Cell(g, layer, cx, cy);
        if (exclude >= 0) value = ExcludePointAt(w, g, layer, cx, cy, exclude, value);
        return true;
    }

    private static short ExcludePointAt(WorldCtx* w, GridCtx* g, byte layer, int cx, int cy, int exclude, short value)
    {
        if (!Applied(w, exclude, layer, out var source)) return value;

        var shape = ShapeOf(g, source);
        return shape.P.Reaches(cx, cy) ? ExcludedValue(w, g, layer, source.Layer, &shape, cx, cy, value) : value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool SenseGradientAt(WorldCtx* w, byte layer, float x, float y, int exclude, out float gx, out float gy)
        => SenseGradientAt(w, w->GridCount == 1 ? w->Grids : null, layer, x, y, exclude, out gx, out gy);

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    private static bool SenseGradientAt(WorldCtx* w, GridCtx* single, byte layer, float x, float y, int exclude, out float gx, out float gy)
    {
        gx = 0f;
        gy = 0f;
        var g = PickPointGrid(w, single, x, y, 1, out var cx, out var cy, out var complete);
        if (g == null) return false;

        GradientCells(g, layer, cx, cy, out var left, out var right, out var below, out var above);
        if (exclude >= 0) ExcludeGradientAt(w, g, layer, cx, cy, exclude, ref left, ref right, ref below, ref above);

        var perUnit = g->ScaleQ8 / 512f;
        gx = (right - left) * perUnit;
        gy = (above - below) * perUnit;
        return complete;
    }

    private static void ExcludeGradientAt(WorldCtx* w, GridCtx* g, byte layer, int cx, int cy, int exclude,
        ref short left, ref short right, ref short below, ref short above)
    {
        if (!Applied(w, exclude, layer, out var source)) return;

        var shape = ShapeOf(g, source);
        if (cx > 0 && shape.P.Reaches(cx - 1, cy))
            left = ExcludedValue(w, g, layer, source.Layer, &shape, cx - 1, cy, left);
        if (cx + 1 < g->Size && shape.P.Reaches(cx + 1, cy))
            right = ExcludedValue(w, g, layer, source.Layer, &shape, cx + 1, cy, right);
        if (cy > 0 && shape.P.Reaches(cx, cy - 1))
            below = ExcludedValue(w, g, layer, source.Layer, &shape, cx, cy - 1, below);
        if (cy + 1 < g->Size && shape.P.Reaches(cx, cy + 1))
            above = ExcludedValue(w, g, layer, source.Layer, &shape, cx, cy + 1, above);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GradientCells(GridCtx* g, byte layer, int cx, int cy,
        out short left, out short right, out short below, out short above)
    {
        var localX = cx & (TileBake.TileSize - 1);
        var localY = cy & (TileBake.TileSize - 1);
        var offset = localY * TileBake.TileSize + localX;
        var pages = &g->Layers[layer].Pages;
        var page = pages->TryGet((cy >> TileBake.TileBits) * g->TilesPerSide + (cx >> TileBake.TileBits), out var block)
            ? (short*)(block + PageOffset)
            : null;
        left = localX > 0 ? page != null ? page[offset - 1] : (short)0
            : cx > 0 ? Cell(g, layer, cx - 1, cy) : (short)0;
        right = localX < TileBake.TileSize - 1 ? page != null ? page[offset + 1] : (short)0
            : cx + 1 < g->Size ? Cell(g, layer, cx + 1, cy) : (short)0;
        below = localY > 0 ? page != null ? page[offset - TileBake.TileSize] : (short)0
            : cy > 0 ? Cell(g, layer, cx, cy - 1) : (short)0;
        above = localY < TileBake.TileSize - 1 ? page != null ? page[offset + TileBake.TileSize] : (short)0
            : cy + 1 < g->Size ? Cell(g, layer, cx, cy + 1) : (short)0;
    }

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    private static GridCtx* PickPointGrid(WorldCtx* w, GridCtx* single, float x, float y, int margin,
        out int cx, out int cy, out bool complete)
    {
        cx = 0;
        cy = 0;
        complete = false;
        if (single != null)
        {
            if (single->ScaleQ8 == 0) return null;
            var qx = (x - single->OriginX) * single->ScaleQ8;
            var qy = (y - single->OriginY) * single->ScaleQ8;
            var limit = single->Size * 256f;
            if (!(qx >= 0f && qy >= 0f && qx < limit && qy < limit)) return null;

            cx = (int)qx >> 8;
            cy = (int)qy >> 8;
            complete = cx >= margin && cy >= margin && cx + margin < single->Size && cy + margin < single->Size;
            return single;
        }

        GridCtx* picked = null;
        var bestScale = 0;
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            if (g->ScaleQ8 == 0) continue;
            var qx = (x - g->OriginX) * g->ScaleQ8;
            var qy = (y - g->OriginY) * g->ScaleQ8;
            var limit = g->Size * 256f;
            if (!(qx >= 0f && qy >= 0f && qx < limit && qy < limit)) continue;

            var ax = (int)qx >> 8;
            var ay = (int)qy >> 8;
            var whole = ax >= margin && ay >= margin && ax + margin < g->Size && ay + margin < g->Size;
            if (picked != null && ((!whole && complete) || (whole == complete && g->ScaleQ8 <= bestScale))) continue;

            picked = g;
            bestScale = g->ScaleQ8;
            cx = ax;
            cy = ay;
            complete = whole;
        }

        return picked;
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
