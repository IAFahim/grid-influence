namespace Gi;

public static unsafe partial class World
{
    public static bool SenseColumn(byte world, byte layer, float* xs, float* ys, int count, short* values, ulong* whole)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;
        if (count <= 0) return true;

        if (whole != null) new Span<ulong>(whole, (count + 63) >> 6).Clear();

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

        new Span<ulong>(words, (count + 63) >> 6).Clear();

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
        if (count <= 0) return;

        var s = &w->Sources;
        for (var i = 0; i < count; i++)
        {
            var id = sources[i];
            var slot = id & SourceIndexMask;
            if ((uint)slot >= (uint)s->Count || s->Alive.Pointer[slot] == 0) continue;
            if ((id & ~SourceIndexMask) != (s->Gen.Pointer[slot] << 24)) continue;

            var x = xs[i];
            var y = ys[i];
            if (*(int*)&x == *(int*)&s->X.Pointer[slot] && *(int*)&y == *(int*)&s->Y.Pointer[slot]) continue;
            EnqueueMutation(w, s, slot, x, y, (sbyte)s->Gain.Pointer[slot], s->Angle.Pointer[slot], s->Scale.Pointer[slot], true);
            s->X.Pointer[slot] = x;
            s->Y.Pointer[slot] = y;
        }
    }

    internal static int SenseRange(WorldCtx* w, byte layer, float* xs, float* ys, short* values, ulong* whole, int from, int to)
    {
        var gaps = 0;
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
                if (picked->Layers[layer].Pages.TryGet(
                        (cy >> TileBake.TileBits) * picked->TilesPerSide + (cx >> TileBake.TileBits), out var block))
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
