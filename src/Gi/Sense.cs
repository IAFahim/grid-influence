using System.Runtime.CompilerServices;
#if NET
using System.Runtime.Intrinsics;
#endif

namespace Gi;

public static unsafe partial class World
{
    private const long MaxReachQ8 = 1L << 24;

    public static bool Covers(byte world, float x, float y)
    {
        var w = GetContext(world);
        if (w == null || !float.IsFinite(x) || !float.IsFinite(y)) return false;

        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            if (g->ScaleQ8 == 0) continue;
            if (Contains(g, CellQ8(x, g->OriginX, g->ScaleQ8), CellQ8(y, g->OriginY, g->ScaleQ8))) return true;
        }

        return false;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    public static bool TrySense(byte world, byte layer, float x, float y, out short value)
    {
        value = 0;
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;
        return SensePointAt(w, layer, x, y, -1, out value);
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    public static bool TrySense(byte world, byte layer, float x, float y, int exclude, out short value)
    {
        value = 0;
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;
        return SensePointAt(w, layer, x, y, exclude, out value);
    }

    public static bool TrySenseArea(byte world, byte layer, float x, float y, float reach, out long total)
        => SenseArea(world, layer, x, y, reach, -1, out total);

    public static bool TrySenseArea(byte world, byte layer, float x, float y, float reach, int exclude, out long total)
        => SenseArea(world, layer, x, y, reach, exclude, out total);

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    public static bool TrySenseMax(byte world, byte layer, float x, float y, float reach,
        out short value, out float bestX, out float bestY)
    {
        value = 0;
        bestX = x;
        bestY = y;
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;
        if (!Pick(w, x, y, reach, 0, true, out var gi, out var complete)) return false;

        var g = w->Grids + gi;
        var disk = Disk(g, x, y, reach);
        var pages = &g->Layers[layer].Pages;
        var tps = g->TilesPerSide;
        var lo = stackalloc int[TileBake.TileSize];
        var hi = stackalloc int[TileBake.TileSize];
        var spanTile = -1;
        var top = -1;
        var topMax = short.MinValue;
        for (var ty = disk.Y0 >> TileBake.TileBits; ty <= disk.Y1 >> TileBake.TileBits; ty++)
        for (var tx = disk.X0 >> TileBake.TileBits; tx <= disk.X1 >> TileBake.TileBits; tx++)
        {
            if (Outside(disk, tx, ty)) continue;

            var m = pages->TryGet(ty * tps + tx, out var block) ? *(short*)(block + MaxOffset) : (short)0;
            if (top >= 0 && m <= topMax) continue;
            top = ty * tps + tx;
            topMax = m;
        }

        var found = false;
        var best = short.MinValue;
        var bx = 0;
        var by = 0;
        ScanDiskTile(g, pages, disk, top % tps, top / tps, lo, hi, ref spanTile, ref found, ref best, ref bx, ref by);
        for (var ty = disk.Y0 >> TileBake.TileBits; ty <= disk.Y1 >> TileBake.TileBits; ty++)
        for (var tx = disk.X0 >> TileBake.TileBits; tx <= disk.X1 >> TileBake.TileBits; tx++)
        {
            var tile = ty * tps + tx;
            if (tile == top || Outside(disk, tx, ty)) continue;

            var m = pages->TryGet(tile, out var block) ? *(short*)(block + MaxOffset) : (short)0;
            if (m < best) continue;
            if (m == best && (ty << TileBake.TileBits) > by) continue;
            ScanDiskTile(g, pages, disk, tx, ty, lo, hi, ref spanTile, ref found, ref best, ref bx, ref by);
        }

        value = best;
        bestX = g->OriginX + ((bx << 8) + 128) / (float)g->ScaleQ8;
        bestY = g->OriginY + ((by << 8) + 128) / (float)g->ScaleQ8;
        return complete;
    }

    public static bool Changed(byte world, byte layer, float x, float y, float reach, int since)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount || !Pick(w, x, y, reach, 0, true, out var gi, out _)) return false;

        var g = w->Grids + gi;
        var epochs = g->Layers[layer].Epochs;
        if (epochs == null) return false;

        var disk = Disk(g, x, y, reach);
        for (var ty = disk.Y0 >> TileBake.TileBits; ty <= disk.Y1 >> TileBake.TileBits; ty++)
        for (var tx = disk.X0 >> TileBake.TileBits; tx <= disk.X1 >> TileBake.TileBits; tx++)
            if (epochs[ty * g->TilesPerSide + tx] - since > 0 && HoldsDiskCell(disk, tx, ty)) return true;
        return false;
    }

    private static bool HoldsDiskCell(in Circle disk, int tx, int ty)
    {
        if (Outside(disk, tx, ty)) return false;

        var tileX = tx << TileBake.TileBits;
        var tileY = ty << TileBake.TileBits;
        var cx = Math.Clamp(disk.Cx, Math.Max(tileX, disk.X0), Math.Min(tileX + TileBake.TileSize - 1, disk.X1));
        var cy = Math.Clamp(disk.Cy, Math.Max(tileY, disk.Y0), Math.Min(tileY + TileBake.TileSize - 1, disk.Y1));
        return (cx == disk.Cx && cy == disk.Cy) || Within(disk, cx, cy);
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    public static bool TrySenseNearest(byte world, byte layer, float x, float y, float reach, short threshold,
        out short value, out float nearestX, out float nearestY)
    {
        value = short.MinValue;
        nearestX = x;
        nearestY = y;
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;
        if (!Pick(w, x, y, reach, 0, true, out var gi, out var complete)) return false;

        var g = w->Grids + gi;
        var disk = Disk(g, x, y, reach);
        var pages = &g->Layers[layer].Pages;
        var tps = g->TilesPerSide;
        var originX = disk.Cx >> TileBake.TileBits;
        var originY = disk.Cy >> TileBake.TileBits;
        var reachTiles = Math.Max(Math.Max(originX - (disk.X0 >> TileBake.TileBits), (disk.X1 >> TileBake.TileBits) - originX),
            Math.Max(originY - (disk.Y0 >> TileBake.TileBits), (disk.Y1 >> TileBake.TileBits) - originY));
        var best = disk.R * disk.R;
        var dx = ((long)disk.Cx << 8) + 128 - disk.Px;
        var dy = ((long)disk.Cy << 8) + 128 - disk.Py;
        if (best < dx * dx + dy * dy)
        {
            var cell = Cell(g, layer, disk.Cx, disk.Cy);
            if (cell < threshold) return complete;

            value = cell;
            nearestX = g->OriginX + ((disk.Cx << 8) + 128) / (float)g->ScaleQ8;
            nearestY = g->OriginY + ((disk.Cy << 8) + 128) / (float)g->ScaleQ8;
            return complete;
        }

        var bx = -1;
        var by = -1;
        for (var ring = 0; ring <= reachTiles; ring++)
        {
            var gap = (long)Math.Max(ring - 1, 0) << (TileBake.TileBits + 8);
            if (gap * gap > best) break;

            for (var ty = originY - ring; ty <= originY + ring; ty++)
            {
                if (ty < disk.Y0 >> TileBake.TileBits || ty > disk.Y1 >> TileBake.TileBits) continue;

                var edge = ty == originY - ring || ty == originY + ring;
                for (var tx = originX - ring; tx <= originX + ring; tx += edge ? 1 : 2 * Math.Max(ring, 1))
                {
                    if (tx < disk.X0 >> TileBake.TileBits || tx > disk.X1 >> TileBake.TileBits || Outside(disk, tx, ty)) continue;

                    var live = pages->TryGet(ty * tps + tx, out var block);
                    if ((live ? *(short*)(block + MaxOffset) : 0) < threshold) continue;
                    NearestInTile(disk, live ? (short*)(block + PageOffset) : null, live ? RowMaxOf(block) : null,
                        tx, ty, threshold, ref best, ref bx, ref by);
                }
            }
        }

        if (bx < 0) return complete;

        value = Cell(g, layer, bx, by);
        nearestX = g->OriginX + ((bx << 8) + 128) / (float)g->ScaleQ8;
        nearestY = g->OriginY + ((by << 8) + 128) / (float)g->ScaleQ8;
        return complete;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static void NearestInTile(in Circle disk, short* page, short* rowMax, int tx, int ty, short threshold,
        ref long best, ref int bx, ref int by)
    {
        var tileX = tx << TileBake.TileBits;
        var tileY = ty << TileBake.TileBits;
        var top = Math.Max(tileY, disk.Y0);
        var bottom = Math.Min(tileY + TileBake.TileSize - 1, disk.Y1);
        if (bottom < top) return;

        var start = Math.Clamp(disk.Cy, top, bottom);
        for (var step = 0; start - step >= top || start + step <= bottom; step++)
        {
            var above = start - step;
            var below = start + step;
            var reachable = false;
            if (above >= top) reachable |= NearestInRow(disk, page, rowMax, tileX, tileY, above, threshold, ref best, ref bx, ref by);
            if (step > 0 && below <= bottom) reachable |= NearestInRow(disk, page, rowMax, tileX, tileY, below, threshold, ref best, ref bx, ref by);
            if (!reachable) return;
        }
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static bool NearestInRow(in Circle disk, short* page, short* rowMax, int tileX, int tileY, int cy, short threshold,
        ref long best, ref int bx, ref int by)
    {
        var dy = ((long)cy << 8) + 128 - disk.Py;
        var dy2 = dy * dy;
        if (dy2 > best) return false;
        if (rowMax != null && rowMax[cy - tileY] < threshold) return true;

        var first = Math.Max(disk.X0, tileX);
        var end = Math.Min(disk.X1 + 1, tileX + TileBake.TileSize);
        if (end <= first) return true;

        var row = page == null ? null : page + (cy - tileY) * TileBake.TileSize - tileX;
        var middle = Math.Clamp(disk.Cx, first, end - 1);
        for (var cx = middle; cx >= first; cx--)
            if (!NearestCell(disk, row, cx, cy, dy2, threshold, ref best, ref bx, ref by)) break;
        for (var cx = middle + 1; cx < end; cx++)
            if (!NearestCell(disk, row, cx, cy, dy2, threshold, ref best, ref bx, ref by)) break;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NearestCell(in Circle disk, short* row, int cx, int cy, long dy2, short threshold,
        ref long best, ref int bx, ref int by)
    {
        var dx = ((long)cx << 8) + 128 - disk.Px;
        var d = dx * dx + dy2;
        if (d > best) return false;
        if (row != null && row[cx] < threshold) return true;
        if (d == best && bx >= 0 && (cy > by || (cy == by && cx >= bx))) return true;

        best = d;
        bx = cx;
        by = cy;
        return true;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static void ScanDiskTile(GridCtx* g, PageMap* pages, in Circle disk, int tx, int ty, int* lo, int* hi,
        ref int spanTile, ref bool found, ref short best, ref int bx, ref int by)
    {
        var tileX = tx << TileBake.TileBits;
        var tileY = ty << TileBake.TileBits;
        var rowLo = Math.Max(tileY, disk.Y0);
        var rows = Math.Min(tileY + TileBake.TileSize - 1, disk.Y1) - rowLo + 1;
        if (spanTile != ty)
        {
            Spans(disk, rowLo, rows, lo, hi);
            spanTile = ty;
        }
        short* page = null;
        short* rowMax = null;
        if (pages->TryGet(ty * g->TilesPerSide + tx, out var block))
        {
            page = (short*)(block + PageOffset);
            rowMax = RowMaxOf(block);
        }
        for (var r = 0; r < rows; r++)
        {
            var a = Math.Max(lo[r], tileX);
            var b = Math.Min(hi[r], tileX + TileBake.TileSize);
            if (b <= a) continue;

            var cy = rowLo + r;
            var row = page == null ? null : page + (cy - tileY) * TileBake.TileSize - tileX;
            var m = row == null ? (short)0 : rowMax[cy - tileY];
            if (found && (m < best || (m == best && (cy > by || (cy == by && a > bx))))) continue;
            if (row != null && (a != tileX || b != tileX + TileBake.TileSize))
                m = SegmentMax(row + tileX, a - tileX, b - tileX);
            if (found && (m < best || (m == best && (cy > by || (cy == by && a > bx))))) continue;

            var cx = row == null ? a : tileX + FirstEqual(row + tileX, a - tileX, b - tileX, m);
            if (found && m == best && (cy > by || (cy == by && cx > bx))) continue;

            found = true;
            best = m;
            bx = cx;
            by = cy;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short SegmentMax(short* row, int a, int b)
    {
#if NET
        if (Vector128.IsHardwareAccelerated || Vector256.IsHardwareAccelerated) return SimdOps.SegmentMax(row, a, b);
#endif
        var best = row[a];
        for (var i = a + 1; i < b; i++)
            if (row[i] > best) best = row[i];
        return best;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FirstEqual(short* row, int a, int b, short value)
    {
#if NET
        if (Vector128.IsHardwareAccelerated || Vector256.IsHardwareAccelerated) return SimdOps.FirstEqualSpan(row, a, b, value);
#endif
        for (var i = a; i < b; i++)
            if (row[i] == value) return i;
        return b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Spans(in Circle disk, int rowLo, int rows, int* lo, int* hi)
    {
        var rowHi = rowLo + rows - 1;
        var middle = Math.Clamp(disk.Cy, rowLo, rowHi);
        WalkSpans(disk, middle, rowHi, 1, rowLo, lo, hi);
        WalkSpans(disk, middle - 1, rowLo, -1, rowLo, lo, hi);
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static void WalkSpans(in Circle disk, int start, int end, int step, int rowLo, int* lo, int* hi)
    {
        var left = disk.X0;
        var right = disk.X1;
        var radiusSquared = disk.R * disk.R;
        for (var cy = start; (end - cy) * step >= 0; cy += step)
        {
            var dy = ((long)cy << 8) + 128 - disk.Py;
            var remaining = radiusSquared - dy * dy;
            var row = cy - rowLo;
            if (remaining < 0)
            {
                lo[row] = disk.Cx;
                hi[row] = disk.Cx + (cy == disk.Cy ? 1 : 0);
                continue;
            }

            while (true)
            {
                var dx = disk.Px - ((long)left << 8) - 128;
                if (dx <= 0 || dx * dx <= remaining) break;
                left++;
            }

            while (true)
            {
                var dx = ((long)right << 8) + 128 - disk.Px;
                if (dx <= 0 || dx * dx <= remaining) break;
                right--;
            }

            lo[row] = cy == disk.Cy ? Math.Min(left, disk.Cx) : left;
            hi[row] = Math.Max(lo[row], (cy == disk.Cy ? Math.Max(right, disk.Cx) : right) + 1);
        }
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    public static bool TrySenseGradient(byte world, byte layer, float x, float y, out float gx, out float gy)
    {
        gx = 0f;
        gy = 0f;
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;
        return SenseGradientAt(w, layer, x, y, -1, out gx, out gy);
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    public static bool TrySenseGradient(byte world, byte layer, float x, float y, int exclude, out float gx, out float gy)
    {
        gx = 0f;
        gy = 0f;
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;
        return SenseGradientAt(w, layer, x, y, exclude, out gx, out gy);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short Cell(GridCtx* g, byte layer, int cx, int cy)
    {
        if (!g->Layers[layer].Pages.TryGet((cy >> TileBake.TileBits) * g->TilesPerSide + (cx >> TileBake.TileBits), out var block))
            return 0;
        return ((short*)(block + PageOffset))[(cy & (TileBake.TileSize - 1)) * TileBake.TileSize + (cx & (TileBake.TileSize - 1))];
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static short ExcludedValue(WorldCtx* w, GridCtx* g, byte layer, byte source, Shape* s, int cx, int cy, short page)
    {
        var r = w->Recipes + layer;
        if (r->Op == LayerOp.Base) return layer == source ? ExcludeCell(g, layer, s, cx, cy, page) : page;
        if ((r->Reads & (1u << source)) == 0) return page;

        var a = ExcludedValue(w, g, r->A, source, s, cx, cy, Cell(g, r->A, cx, cy));
        var b = r->B == r->A ? a : ExcludedValue(w, g, r->B, source, s, cx, cy, Cell(g, r->B, cx, cy));
        return CombineCell(r, a, b);
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static long ExcludedMix(WorldCtx* w, GridCtx* g, byte layer, byte source, in Circle disk, Shape* s, int* lo, int* hi)
    {
        var fx0 = Math.Max(Math.Max(s->P.X0, 0), disk.X0);
        var fy0 = Math.Max(Math.Max(s->P.Y0, 0), disk.Y0);
        var fx1 = Math.Min(Math.Min(s->P.X1, g->Size) - 1, disk.X1);
        var fy1 = Math.Min(Math.Min(s->P.Y1, g->Size) - 1, disk.Y1);
        var delta = 0L;
        for (var cy = fy0; cy <= fy1; cy++)
        {
            var row = cy - disk.Y0;
            var end = Math.Min(hi[row], fx1 + 1);
            for (var cx = Math.Max(lo[row], fx0); cx < end; cx++)
            {
                var page = Cell(g, layer, cx, cy);
                delta += ExcludedValue(w, g, layer, source, s, cx, cy, page) - page;
            }
        }

        return delta;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static bool SenseArea(byte world, byte layer, float x, float y, float reach, int exclude, out long total)
    {
        total = 0;
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;
        if (!Pick(w, x, y, reach, 0, true, out var gi, out var complete)) return false;

        var g = w->Grids + gi;
        var disk = Disk(g, x, y, reach);
        var pages = &g->Layers[layer].Pages;
        var tps = g->TilesPerSide;
        var rowCount = disk.Y1 - disk.Y0 + 1;
        var lo = stackalloc int[rowCount];
        var hi = stackalloc int[rowCount];
        Spans(disk, disk.Y0, rowCount, lo, hi);
        var sum = 0L;
        for (var ty = disk.Y0 >> TileBake.TileBits; ty <= disk.Y1 >> TileBake.TileBits; ty++)
        {
            var tileY = ty << TileBake.TileBits;
            var rowLo = Math.Max(tileY, disk.Y0);
            var rows = Math.Min(tileY + TileBake.TileSize - 1, disk.Y1) - rowLo + 1;
            for (var tx = disk.X0 >> TileBake.TileBits; tx <= disk.X1 >> TileBake.TileBits; tx++)
            {
                if (Outside(disk, tx, ty) || !pages->TryGet(ty * tps + tx, out var block)) continue;

                if (Inside(disk, tx, ty))
                {
                    sum += *(long*)(block + SumOffset);
                    continue;
                }

                sum += RaggedSum((short*)(block + PageOffset) + (rowLo - tileY) * TileBake.TileSize,
                    RowSumOf(block) + rowLo - tileY, tx << TileBake.TileBits, rows,
                    lo + rowLo - disk.Y0, hi + rowLo - disk.Y0);
            }
        }

        if (Applied(w, exclude, layer, out var source))
        {
            var shape = ShapeOf(g, source);
            sum += IsDerived(w, layer)
                ? ExcludedMix(w, g, layer, source.Layer, disk, &shape, lo, hi)
                : ExcludedArea(g, layer, disk, &shape, lo, hi);
        }

        total = WorldArea(sum, g->ScaleQ8);
        return complete;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static long RaggedSum(short* rows0, int* rowSums, int tileX, int rows, int* lo, int* hi)
    {
#if NET
        if (Vector128.IsHardwareAccelerated || Vector256.IsHardwareAccelerated)
            return SimdOps.RaggedSum(rows0, rowSums, tileX, rows, lo, hi);
#endif
        var sum = 0L;
        for (var r = 0; r < rows; r++)
        {
            var a = Math.Max(lo[r], tileX) - tileX;
            var b = Math.Min(hi[r], tileX + TileBake.TileSize) - tileX;
            if (a == 0 && b == TileBake.TileSize)
            {
                sum += rowSums[r];
                continue;
            }

            var row = rows0 + r * TileBake.TileSize;
            for (var i = a; i < b; i++) sum += row[i];
        }

        return sum;
    }

    private struct Shape
    {
        public Placement P;
        public int Gain;
        public int Slot;
    }

    private struct Placed
    {
        public int Slot;
        public float X;
        public float Y;
        public byte Stamp;
        public byte Layer;
        public int Gain;
        public ushort Angle;
        public ushort Size;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static Shape ShapeOf(GridCtx* g, in Placed source)
    {
        var shape = new Shape { Gain = source.Gain, Slot = source.Slot };
        if (g->Owner->Snapshot != 0)
        {
            shape.P = g->Prepared.Pointer[source.Slot];
            return shape;
        }

        TileBake.Place(source.X, source.Y, g->OriginX, g->OriginY, g->ScaleQ8, g->Size, StampCatalog.Get(source.Stamp),
            source.Angle, source.Size, out shape.P);
        return shape;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static void SourceAt(Shape* s, int cx, int cy, out int core, out long smooth)
        => SourceAt(&s->P, s->Gain, cx, cy, out core, out smooth);

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    internal static void SourceAt(Placement* p, int gain, int cx, int cy, out int core, out long smooth)
    {
        core = 0;
        smooth = 0;
        switch (p->Plane)
        {
            case Plane.Difference:
                core = TileBake.BoxAt(p->Px, p->Py, p->Fx, p->Fy, p->ExtentX, p->ExtentY, p->V, gain, cx, cy);
                return;
            case Plane.Dense when p->Turned:
                core = TileBake.TurnedDenseAt(p, cx, cy) * gain;
                return;
            case Plane.Dense:
            {
                var cell = 0;
                TileBake.EmitRaster(&cell, cx, cy, p->Px, p->Py, p->Fx, p->Fy,
                    Math.Min(p->X1, cx + 1), Math.Min(p->Y1, cy + 1), p->Sampling, p->V, gain);
                core = cell;
                return;
            }
            default:
            {
                if (p->Turned)
                {
                    smooth = TileBake.TurnedSmoothAt(p, cx, cy) * gain;
                    return;
                }

                long wx;
                long wy;
                TileBake.SmoothWeights(p->Kind, p->Px, p->Fx, p->ExtentX, cx, 1, &wx);
                TileBake.SmoothWeights(p->Kind, p->Py, p->Fy, p->ExtentY, cy, 1, &wy);
                smooth = (long)p->V->Constant * gain * p->Curve * wx * wy;
                return;
            }
        }
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static short ExcludeCell(GridCtx* g, byte layer, Shape* s, int cx, int cy, short page)
    {
        if (g->Owner->Snapshot != 0)
        {
            if (s->P.Plane is Plane.Difference or Plane.Dense && page > short.MinValue && page < short.MaxValue)
            {
                SourceAt(s, cx, cy, out var integer, out _);
                return Saturate((long)page - integer);
            }

            return SnapshotExcluded(g, layer, cx, cy, s->Slot);
        }

        SourceAt(s, cx, cy, out var core, out var smooth);
        return Saturate(Absent(0, 0, 0, s->P.Plane, core, smooth));
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static long LinearDelta(short* page, int* core, int top, int rows, int* lo, int* hi)
    {
#if NET
        if (Vector128.IsHardwareAccelerated || Vector256.IsHardwareAccelerated) return SimdOps.LinearDelta(page, core, top, rows, lo, hi);
#endif
        const int n = TileBake.TileSize;
        var delta = 0L;
        for (var r = 0; r < rows; r++)
        {
            var row = (top + r) * n;
            for (var c = lo[r]; c < hi[r]; c++)
            {
                var p = page == null ? (short)0 : page[row + c];
                delta += Saturate((long)p - core[row + c]) - p;
            }
        }

        return delta;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Absent(int boxes, long tents, long bells, Plane plane, int core, long smooth)
        => boxes - core +
            TileBake.RoundQ40(tents - (plane == Plane.Tent ? smooth : 0)) +
            TileBake.RoundQ40(bells - (plane == Plane.Bell ? smooth : 0));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short Saturate(long value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static long ExcludedArea(GridCtx* g, byte layer, in Circle disk, Shape* s, int* diskLo, int* diskHi)
    {
        var p = &s->P;
        var gain = s->Gain;
        var fx0 = Math.Max(Math.Max(p->X0, 0), disk.X0);
        var fy0 = Math.Max(Math.Max(p->Y0, 0), disk.Y0);
        var fx1 = Math.Min(Math.Min(p->X1, g->Size) - 1, disk.X1);
        var fy1 = Math.Min(Math.Min(p->Y1, g->Size) - 1, disk.Y1);
        if (fx1 < fx0 || fy1 < fy0) return 0;
        if (g->Owner->Snapshot != 0)
            return SnapshotAreaDelta(g, layer, disk, s, diskLo, diskHi, fx0, fy0, fx1, fy1);

        const int n = TileBake.TileSize;
        var lo = stackalloc int[n];
        var hi = stackalloc int[n];
        var core = stackalloc int[n * n];
        var wx = stackalloc long[n];
        var wy = stackalloc long[n];
        var plane = p->Plane;
        var smooth = plane is Plane.Tent or Plane.Bell;
        var separable = smooth && !p->Turned;
        var scale = separable ? (long)p->V->Constant * gain * p->Curve : 0;
        var delta = 0L;
        for (var ty = fy0 >> TileBake.TileBits; ty <= fy1 >> TileBake.TileBits; ty++)
        for (var tx = fx0 >> TileBake.TileBits; tx <= fx1 >> TileBake.TileBits; tx++)
        {
            if (Outside(disk, tx, ty)) continue;

            var tileX = tx << TileBake.TileBits;
            var tileY = ty << TileBake.TileBits;
            var rowLo = Math.Max(tileY, fy0);
            var rows = Math.Min(tileY + n - 1, fy1) - rowLo + 1;
            var cols = 0;
            var top = rowLo - tileY;
            for (var r = 0; r < rows; r++)
            {
                var row = rowLo + r - disk.Y0;
                lo[r] = Math.Max(Math.Max(diskLo[row], tileX), fx0) - tileX;
                hi[r] = Math.Min(Math.Min(diskHi[row], tileX + n), fx1 + 1) - tileX;
                if (hi[r] > cols) cols = hi[r];
            }

            if (cols == 0) continue;

            if (plane == Plane.Difference)
            {
                TileBake.BoxCells(core, tileX, tileY, p->Px, p->Py, p->Fx, p->Fy, p->ExtentX, p->ExtentY, p->V, gain);
            }
            else if (plane == Plane.Dense)
            {
                new Span<int>(core, n * n).Clear();
                if (p->Turned) TileBake.EmitTurnedDense(core, tileX, tileY, p, gain);
                else TileBake.EmitRaster(core, tileX, tileY, p->Px, p->Py, p->Fx, p->Fy, p->X1, p->Y1, p->Sampling, p->V, gain);
            }
            else if (separable)
            {
                TileBake.SmoothWeights(p->Kind, p->Px, p->Fx, p->ExtentX, tileX, cols, wx);
                TileBake.SmoothWeights(p->Kind, p->Py, p->Fy, p->ExtentY, tileY, top + rows, wy);
            }

            if (!smooth)
            {
                delta += LinearDelta(null, core, top, rows, lo, hi);
                continue;
            }

            for (var r = 0; r < rows; r++)
            {
                var dy = separable ? scale * wy[top + r] : 0;
                for (var c = lo[r]; c < hi[r]; c++)
                {
                    var own = separable ? dy * wx[c] : TileBake.TurnedSmoothAt(p, tileX + c, tileY + top + r) * gain;
                    delta += Saturate(TileBake.RoundQ40(-own));
                }
            }
        }

        return delta;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static long SnapshotAreaDelta(GridCtx* g, byte layer, in Circle disk, Shape* s, int* diskLo, int* diskHi,
        int fx0, int fy0, int fx1, int fy1)
    {
        const int n = TileBake.TileSize;
        var lo = stackalloc int[n];
        var hi = stackalloc int[n];
        var p = &s->P;
        var pages = &g->Layers[layer].Pages;
        var delta = 0L;
        for (var ty = fy0 >> TileBake.TileBits; ty <= fy1 >> TileBake.TileBits; ty++)
        for (var tx = fx0 >> TileBake.TileBits; tx <= fx1 >> TileBake.TileBits; tx++)
        {
            if (Outside(disk, tx, ty)) continue;

            var tileX = tx << TileBake.TileBits;
            var tileY = ty << TileBake.TileBits;
            var rowLo = Math.Max(tileY, fy0);
            var rows = Math.Min(tileY + n - 1, fy1) - rowLo + 1;
            var top = rowLo - tileY;
            for (var r = 0; r < rows; r++)
            {
                var row = rowLo + r - disk.Y0;
                lo[r] = Math.Max(Math.Max(diskLo[row], tileX), fx0) - tileX;
                hi[r] = Math.Min(Math.Min(diskHi[row], tileX + n), fx1 + 1) - tileX;
            }

            var live = pages->TryGet(ty * g->TilesPerSide + tx, out var block);
            if (p->Plane == Plane.Difference)
            {
                var min = live ? *(short*)(block + MinOffset) : 0;
                var max = live ? *(short*)(block + MaxOffset) : 0;
                var own = p->V->Constant * s->Gain;
                if (min > short.MinValue && max < short.MaxValue &&
                    (own >= 0 ? min - own >= short.MinValue : max - own <= short.MaxValue))
                {
                    delta -= TileBake.BoxSpans(p, s->Gain, tileX, tileY, top, rows, lo, hi);
                    continue;
                }
            }

            var page = live ? (short*)(block + PageOffset) : null;
            for (var r = 0; r < rows; r++)
            for (var c = lo[r]; c < hi[r]; c++)
            {
                var cy = rowLo + r;
                var cell = page == null ? (short)0 : page[(top + r) * n + c];
                var absent = ExcludeCell(g, layer, s, tileX + c, cy, cell);
                delta += absent - cell;
            }
        }

        return delta;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static bool Applied(WorldCtx* w, int id, byte layer, out Placed source)
    {
        source = default;
        if (id < 0) return false;

        var s = &w->Sources;
        var slot = id & SourceIndexMask;
        if ((uint)slot >= (uint)s->Count) return false;
        source.Slot = slot;

        var generation = (byte)((uint)id >> 24);
        var reads = w->Recipes[layer].Reads;
        var pending = w->Pending.Pointer[slot];
        if (pending != 0)
        {
            var op = w->Ops.Pointer + pending - 1;
            if (op->AliveFrom == 0 || op->FromGen != generation || (reads & (1u << op->FromLayer)) == 0) return false;

            source.X = op->FromX;
            source.Y = op->FromY;
            source.Stamp = op->FromStamp;
            source.Layer = op->FromLayer;
            source.Gain = op->FromGain;
            source.Angle = op->FromAngle;
            source.Size = op->FromScale;
            return source.Gain != 0;
        }

        var sourceLayer = s->Layer.Pointer[slot];
        if (s->Alive.Pointer[slot] == 0 || s->Gen.Pointer[slot] != generation || (reads & (1u << sourceLayer)) == 0)
            return false;

        source.X = s->X.Pointer[slot];
        source.Y = s->Y.Pointer[slot];
        source.Stamp = s->Stamp.Pointer[slot];
        source.Layer = sourceLayer;
        source.Gain = (sbyte)s->Gain.Pointer[slot];
        source.Angle = s->Angle.Pointer[slot];
        source.Size = s->Scale.Pointer[slot];
        return source.Gain != 0;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static bool Pick(WorldCtx* w, float x, float y, float reach, int margin, bool disk,
        out int grid, out bool complete)
    {
        grid = -1;
        complete = false;
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(reach)) return false;
        var bestScale = 0;
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            if (g->ScaleQ8 == 0) continue;

            var qx = CellQ8(x, g->OriginX, g->ScaleQ8);
            var qy = CellQ8(y, g->OriginY, g->ScaleQ8);
            if (!Contains(g, qx, qy)) continue;

            bool whole;
            if (disk)
            {
                var c = Disk(g, x, y, reach);
                whole = c.X0 >= 0 && c.Y0 >= 0 && c.X1 < g->Size && c.Y1 < g->Size && !c.Clipped;
            }
            else
            {
                var cx = qx >> 8;
                var cy = qy >> 8;
                whole = cx - margin >= 0 && cy - margin >= 0 && cx + margin < g->Size && cy + margin < g->Size;
            }

            var better = grid < 0 || (whole && !complete) || (whole == complete && g->ScaleQ8 > bestScale);
            if (!better) continue;

            grid = gi;
            complete = whole;
            bestScale = g->ScaleQ8;
        }

        return grid >= 0;
    }

    private struct Circle
    {
        public long Px;
        public long Py;
        public long R;
        public int Cx;
        public int Cy;
        public int X0;
        public int Y0;
        public int X1;
        public int Y1;
        public bool Clipped;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static Circle Disk(GridCtx* g, float x, float y, float reach)
    {
        var c = new Circle
        {
            Px = CellQ8(x, g->OriginX, g->ScaleQ8),
            Py = CellQ8(y, g->OriginY, g->ScaleQ8),
            R = ReachQ8(reach, g->ScaleQ8),
        };
        c.Cx = (int)(c.Px >> 8);
        c.Cy = (int)(c.Py >> 8);
        var x0 = Math.Min(CeilCell(c.Px - c.R - 128), c.Cx);
        var y0 = Math.Min(CeilCell(c.Py - c.R - 128), c.Cy);
        var x1 = Math.Max((c.Px + c.R - 128) >> 8, c.Cx);
        var y1 = Math.Max((c.Py + c.R - 128) >> 8, c.Cy);
        c.Clipped = x0 < 0 || y0 < 0 || x1 >= g->Size || y1 >= g->Size;
        c.X0 = (int)Math.Max(x0, 0);
        c.Y0 = (int)Math.Max(y0, 0);
        c.X1 = (int)Math.Min(x1, g->Size - 1);
        c.Y1 = (int)Math.Min(y1, g->Size - 1);
        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Inside(in Circle c, int tx, int ty)
    {
        var x0 = tx << TileBake.TileBits;
        var y0 = ty << TileBake.TileBits;
        var x1 = x0 + TileBake.TileSize - 1;
        var y1 = y0 + TileBake.TileSize - 1;
        if (x0 < c.X0 || y0 < c.Y0 || x1 > c.X1 || y1 > c.Y1) return false;

        return Within(c, x0, y0) && Within(c, x1, y0) && Within(c, x0, y1) && Within(c, x1, y1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Outside(in Circle c, int tx, int ty)
    {
        var x0 = tx << TileBake.TileBits;
        var y0 = ty << TileBake.TileBits;
        var x1 = x0 + TileBake.TileSize - 1;
        var y1 = y0 + TileBake.TileSize - 1;
        if (x1 < c.X0 || y1 < c.Y0 || x0 > c.X1 || y0 > c.Y1) return true;
        if (c.Cx >= x0 && c.Cx <= x1 && c.Cy >= y0 && c.Cy <= y1) return false;

        var nx = Math.Clamp(c.Px, ((long)x0 << 8) + 128, ((long)x1 << 8) + 128) - c.Px;
        var ny = Math.Clamp(c.Py, ((long)y0 << 8) + 128, ((long)y1 << 8) + 128) - c.Py;
        return nx * nx + ny * ny > c.R * c.R;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Within(in Circle c, int cx, int cy)
    {
        var dx = ((long)cx << 8) + 128 - c.Px;
        var dy = ((long)cy << 8) + 128 - c.Py;
        return dx * dx + dy * dy <= c.R * c.R;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long CellQ8(float value, float origin, int scaleQ8)
        => (int)MathF.Floor((value - origin) * scaleQ8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Contains(GridCtx* g, long qx, long qy)
        => qx >= 0 && qy >= 0 && (qx >> 8) < g->Size && (qy >> 8) < g->Size;

    private static long ReachQ8(float reach, int scaleQ8)
    {
        if (!(reach > 0f)) return 0;

        var q = reach * scaleQ8;
        return q >= MaxReachQ8 ? MaxReachQ8 : (long)MathF.Floor(q);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long CeilCell(long q8) => -(-q8 >> 8);

    private static long WorldArea(long cells, int scaleQ8)
    {
        var den = (long)scaleQ8 * scaleQ8;
        var num = cells * 65536;
        var magnitude = ((num < 0 ? -num : num) + den / 2) / den;
        return num < 0 ? -magnitude : magnitude;
    }
}
