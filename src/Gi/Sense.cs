using System.Runtime.CompilerServices;
#if NET
using System.Runtime.Intrinsics;
#endif

namespace Gi;

public static unsafe partial class World
{
    private const long MaxReachQ8 = 1L << 24;
    private const float MaxCellQ8 = 1073741824f;

    public static bool Covers(byte world, float x, float y)
    {
        var w = GetContext(world);
        if (w == null) return false;

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
        if (!Pick(w, x, y, 0f, 0, false, out var gi, out var complete)) return false;

        var g = w->Grids + gi;
        value = Query(world, (byte)gi, layer,
            (int)(CellQ8(x, g->OriginX, g->ScaleQ8) >> 8), (int)(CellQ8(y, g->OriginY, g->ScaleQ8) >> 8));
        return complete;
    }

    private readonly struct SplatOwn
    {
        public readonly bool Active;
        public readonly byte Stamp;
        public readonly sbyte Gain;
        public readonly float X;
        public readonly float Y;

        public SplatOwn(byte stamp, sbyte gain, float x, float y)
        {
            Active = true;
            Stamp = stamp;
            Gain = gain;
            X = x;
            Y = y;
        }
    }

    private static bool Own(WorldCtx* w, GridCtx* g, SourceId id, in SplatOwn own, byte layer, out Placed source)
    {
        if (!own.Active) return Applied(w, g, id, layer, out source);

        source = default;
        var gain = Math.Clamp((int)own.Gain, -MaxGain, MaxGain);
        if (gain == 0 || !IsTransient(w, layer) || !StampCatalog.Valid(own.Stamp)) return false;
        if (!float.IsFinite(own.X) || !float.IsFinite(own.Y) || g->ScaleQ8 == 0) return false;

        var mask = w->Recipes[layer].AnchorMask;
        source.Ax = TileBake.AnchorQ8(own.X, g->OriginX, g->ScaleQ8, mask);
        source.Ay = TileBake.AnchorQ8(own.Y, g->OriginY, g->ScaleQ8, mask);
        source.Stamp = own.Stamp;
        source.Layer = layer;
        source.Gain = (sbyte)gain;
        source.Angle = 0;
        source.Size = UnitScale;
        return true;
    }

    public static bool TrySense(byte world, byte layer, float x, float y, byte stamp, sbyte gain, out short value)
        => SenseOwn(world, layer, x, y, default, new SplatOwn(stamp, gain, x, y), out value);

    public static bool TrySenseGradient(byte world, byte layer, float x, float y, byte stamp, sbyte gain, out float gx, out float gy)
        => SenseGradientOwn(world, layer, x, y, default, new SplatOwn(stamp, gain, x, y), out gx, out gy);

    public static bool TrySenseArea(byte world, byte layer, float x, float y, float reach, byte stamp, sbyte gain, out long total)
        => SenseArea(world, layer, x, y, reach, default, new SplatOwn(stamp, gain, x, y), out total);

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    public static bool TrySense(byte world, byte layer, float x, float y, SourceId exclude, out short value)
        => SenseOwn(world, layer, x, y, exclude, default, out value);

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static bool SenseOwn(byte world, byte layer, float x, float y, SourceId exclude, in SplatOwn own, out short value)
    {
        value = 0;
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;
        if (!Pick(w, x, y, 0f, 0, false, out var gi, out var complete)) return false;

        var g = w->Grids + gi;
        var cx = (int)(CellQ8(x, g->OriginX, g->ScaleQ8) >> 8);
        var cy = (int)(CellQ8(y, g->OriginY, g->ScaleQ8) >> 8);
        value = Query(world, (byte)gi, layer, cx, cy);
        if (!Own(w, g, exclude, own, layer, out var source)) return complete;

        var shape = ShapeOf(w, g, source);
        if (shape.P.Reaches(cx, cy)) value = ExcludedValue(w, g, layer, source.Layer, &shape, cx, cy, value);
        return complete;
    }

    public static bool TrySenseArea(byte world, byte layer, float x, float y, float reach, out long total)
        => SenseArea(world, layer, x, y, reach, default, default, out total);

    public static bool TrySenseArea(byte world, byte layer, float x, float y, float reach, SourceId exclude, out long total)
        => SenseArea(world, layer, x, y, reach, exclude, default, out total);

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    public static bool TrySenseMax(byte world, byte layer, float x, float y, float reach,
        out short value, out float bestX, out float bestY)
    {
        value = 0;
        bestX = 0;
        bestY = 0;
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;
        if (!Pick(w, x, y, reach, 0, true, out var gi, out var complete)) return false;

        var g = w->Grids + gi;
        var disk = Disk(g, x, y, reach);
        var ld = g->Layers + layer;
        var tps = g->TilesPerSide;
        var rowMaxima = !IsDerived(w, layer);
        var found = false;
        var best = short.MinValue;
        var bx = 0;
        var by = 0;
        const int Pending = 64;
        var pendingTiles = stackalloc int[Pending];
        var pendingMaxima = stackalloc short[Pending];
        var pending = 0;
        var top = -1;
        for (var ty = disk.Y0 >> TileBake.TileBits; ty <= disk.Y1 >> TileBake.TileBits; ty++)
        for (var tx = disk.X0 >> TileBake.TileBits; tx <= disk.X1 >> TileBake.TileBits; tx++)
        {
            if (Outside(disk, tx, ty)) continue;

            if (rowMaxima && Inside(disk, tx, ty))
            {
                ConsiderTile(g, ld, disk, tx, ty, true, ref found, ref best, ref bx, ref by);
                continue;
            }

            var summary = ld->SummaryOf(ty * tps + tx);
            var m = summary != null ? summary->Max : (short)0;
            if (pending == Pending)
            {
                ScanDiskTile(g, ld, disk, tx, ty, rowMaxima, ref found, ref best, ref bx, ref by);
                continue;
            }

            pendingTiles[pending] = ty * tps + tx;
            pendingMaxima[pending] = m;
            if (top < 0 || m > pendingMaxima[top]) top = pending;
            pending++;
        }

        if (top >= 0 && (!found || pendingMaxima[top] >= best))
        {
            ScanDiskTile(g, ld, disk, pendingTiles[top] % tps, pendingTiles[top] / tps, rowMaxima, ref found, ref best, ref bx, ref by);
            pendingMaxima[top] = short.MinValue;
        }

        for (var i = 0; i < pending; i++)
        {
            if (i == top) continue;

            var m = pendingMaxima[i];
            var ty = pendingTiles[i] / tps;
            if (found && m < best) continue;
            if (found && m == best && (ty << TileBake.TileBits) > by) continue;
            ScanDiskTile(g, ld, disk, pendingTiles[i] % tps, ty, rowMaxima, ref found, ref best, ref bx, ref by);
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

        return AnyNewerInDisk(g, g->Layers + layer, Disk(g, x, y, reach), since);
    }

    private static bool HoldsDiskCell(in Circle disk, int tx, int ty)
    {
        if (Outside(disk, tx, ty)) return false;

        var tileX = tx << TileBake.TileBits;
        var tileY = ty << TileBake.TileBits;
        var rowEnd = Math.Min(tileY + TileBake.TileSize - 1, disk.Y1);
        for (var cy = Math.Max(tileY, disk.Y0); cy <= rowEnd; cy++)
            if (Span(disk, cy, out var lo, out var hi) && Math.Max(lo, tileX) < Math.Min(hi, tileX + TileBake.TileSize)) return true;
        return false;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    public static bool TrySenseNearest(byte world, byte layer, float x, float y, float reach, short threshold,
        out short value, out float nearestX, out float nearestY)
    {
        value = short.MinValue;
        nearestX = 0;
        nearestY = 0;
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;
        if (!Pick(w, x, y, reach, 0, true, out var gi, out var complete)) return false;

        var g = w->Grids + gi;
        var disk = Disk(g, x, y, reach);
        var ld = g->Layers + layer;
        var tps = g->TilesPerSide;
        var originX = disk.Cx >> TileBake.TileBits;
        var originY = disk.Cy >> TileBake.TileBits;
        var reachTiles = Math.Max(Math.Max(originX - (disk.X0 >> TileBake.TileBits), (disk.X1 >> TileBake.TileBits) - originX),
            Math.Max(originY - (disk.Y0 >> TileBake.TileBits), (disk.Y1 >> TileBake.TileBits) - originY));
        var best = long.MaxValue;
        var bx = 0;
        var by = 0;
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

                    var cells = ld->CellsOf(ty * tps + tx);
                    var live = cells.Page != null;
                    if ((live ? cells.Summary->Max : 0) < threshold) continue;
                    NearestInTile(disk, cells.Page, tx, ty, threshold, ref best, ref bx, ref by);
                }
            }
        }

        if (best == long.MaxValue)
        {
            nearestX = x;
            nearestY = y;
            return complete;
        }

        value = Cell(g, layer, bx, by);
        nearestX = g->OriginX + ((bx << 8) + 128) / (float)g->ScaleQ8;
        nearestY = g->OriginY + ((by << 8) + 128) / (float)g->ScaleQ8;
        return complete;
    }

    private static void NearestInTile(in Circle disk, short* page, int tx, int ty, short threshold, ref long best, ref int bx, ref int by)
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
            if (above >= top) reachable |= NearestInRow(disk, page, tileX, tileY, above, threshold, ref best, ref bx, ref by);
            if (step > 0 && below <= bottom) reachable |= NearestInRow(disk, page, tileX, tileY, below, threshold, ref best, ref bx, ref by);
            if (!reachable) return;
        }
    }

    private static bool NearestInRow(in Circle disk, short* page, int tileX, int tileY, int cy, short threshold,
        ref long best, ref int bx, ref int by)
    {
        var dy = ((long)cy << 8) + 128 - disk.Py;
        var dy2 = dy * dy;
        if (dy2 > best) return false;
        if (!Span(disk, cy, out var lo, out var hi)) return true;

        var first = Math.Max(lo, tileX);
        var end = Math.Min(hi, tileX + TileBake.TileSize);
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
        if (d == best && (cy > by || (cy == by && cx >= bx))) return true;

        best = d;
        bx = cx;
        by = cy;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConsiderTile(GridCtx* g, LayerData* ld, in Circle disk, int tx, int ty, bool summaries,
        ref bool found, ref short best, ref int bx, ref int by)
    {
        if (!summaries || !Inside(disk, tx, ty))
        {
            ScanDiskTile(g, ld, disk, tx, ty, summaries, ref found, ref best, ref bx, ref by);
            return;
        }

        var tileX = tx << TileBake.TileBits;
        var tileY = ty << TileBake.TileBits;
        var summary = ld->SummaryOf(ty * g->TilesPerSide + tx);
        var m = summary == null ? (short)0 : summary->Max;
        var ax = summary == null ? tileX : tileX + summary->ArgMaxX;
        var ay = summary == null ? tileY : tileY + summary->ArgMaxY;
        if (found && (m < best || (m == best && (ay > by || (ay == by && ax >= bx))))) return;

        found = true;
        best = m;
        bx = ax;
        by = ay;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static void ScanDiskTile(GridCtx* g, LayerData* ld, in Circle disk, int tx, int ty, bool rowMaxima,
        ref bool found, ref short best, ref int bx, ref int by)
    {
        var tileX = tx << TileBake.TileBits;
        var tileY = ty << TileBake.TileBits;
        var rowLo = Math.Max(tileY, disk.Y0);
        var rowHi = Math.Min(tileY + TileBake.TileSize - 1, disk.Y1);
        var cells = ld->CellsOf(ty * g->TilesPerSide + tx);
        var page = cells.Page;
        var maxima = rowMaxima && page != null ? cells.Summary->RowMaxima : null;
        for (var cy = rowLo; cy <= rowHi; cy++)
        {
            if (found && maxima != null)
            {
                var bound = maxima[cy - tileY];
                if (bound < best || (bound == best && cy > by)) continue;
            }

            if (!Span(disk, cy, out var lo, out var hi)) continue;

            var a = Math.Max(lo, tileX);
            var b = Math.Min(hi, tileX + TileBake.TileSize);
            if (b <= a) continue;

            var row = page == null ? null : page + (cy - tileY) * TileBake.TileSize - tileX;
            var m = row == null ? (short)0 : maxima != null && b - a == TileBake.TileSize ? maxima[cy - tileY] : SegmentMax(row + tileX, a - tileX, b - tileX);
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

    private static void Spans(in Circle disk, int rowLo, int rows, int* lo, int* hi)
    {
        for (var r = 0; r < rows; r++)
        {
            if (!Span(disk, rowLo + r, out lo[r], out hi[r])) hi[r] = lo[r];
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
        if (!Pick(w, x, y, 0f, 1, false, out var gi, out var complete)) return false;

        var g = w->Grids + gi;
        var ld = g->Layers + layer;
        var cx = (int)(CellQ8(x, g->OriginX, g->ScaleQ8) >> 8);
        var cy = (int)(CellQ8(y, g->OriginY, g->ScaleQ8) >> 8);
        var tile = (cy >> TileBake.TileBits) * g->TilesPerSide + (cx >> TileBake.TileBits);
        var page = ld->PageOf(tile);
        var perUnit = g->ScaleQ8 / 512f;
        gx = (CellNear(g, ld, tile, page, cx + 1, cy) - CellNear(g, ld, tile, page, cx - 1, cy)) * perUnit;
        gy = (CellNear(g, ld, tile, page, cx, cy + 1) - CellNear(g, ld, tile, page, cx, cy - 1)) * perUnit;
        return complete;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    public static bool TrySenseGradient(byte world, byte layer, float x, float y, SourceId exclude, out float gx, out float gy)
        => SenseGradientOwn(world, layer, x, y, exclude, default, out gx, out gy);

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static bool SenseGradientOwn(byte world, byte layer, float x, float y, SourceId exclude, in SplatOwn own, out float gx, out float gy)
    {
        gx = 0f;
        gy = 0f;
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;
        if (!Pick(w, x, y, 0f, 1, false, out var gi, out var complete)) return false;

        var g = w->Grids + gi;
        var cx = (int)(CellQ8(x, g->OriginX, g->ScaleQ8) >> 8);
        var cy = (int)(CellQ8(y, g->OriginY, g->ScaleQ8) >> 8);
        var excluding = Own(w, g, exclude, own, layer, out var source);
        var shape = excluding ? ShapeOf(w, g, source) : default;
        var perUnit = g->ScaleQ8 / 512f;
        gx = (Others(w, g, layer, excluding, source.Layer, &shape, cx + 1, cy) -
            Others(w, g, layer, excluding, source.Layer, &shape, cx - 1, cy)) * perUnit;
        gy = (Others(w, g, layer, excluding, source.Layer, &shape, cx, cy + 1) -
            Others(w, g, layer, excluding, source.Layer, &shape, cx, cy - 1)) * perUnit;
        return complete;
    }

    private static short Others(WorldCtx* w, GridCtx* g, byte layer, bool excluding, byte source, Shape* shape, int cx, int cy)
    {
        if ((uint)cx >= (uint)g->Size || (uint)cy >= (uint)g->Size) return 0;
        var value = Cell(g, layer, cx, cy);
        if (!excluding || !shape->P.Reaches(cx, cy)) return value;
        return ExcludedValue(w, g, layer, source, shape, cx, cy, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short Cell(GridCtx* g, byte layer, int cx, int cy)
    {
        var page = g->Layers[layer].PageOf((cy >> TileBake.TileBits) * g->TilesPerSide + (cx >> TileBake.TileBits));
        if (page == null) return 0;
        return page[(cy & (TileBake.TileSize - 1)) * TileBake.TileSize + (cx & (TileBake.TileSize - 1))];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short CellNear(GridCtx* g, LayerData* ld, int tile, short* page, int cx, int cy)
    {
        if ((uint)cx >= (uint)g->Size || (uint)cy >= (uint)g->Size) return 0;
        if (page != null && (cy >> TileBake.TileBits) * g->TilesPerSide + (cx >> TileBake.TileBits) == tile)
            return page[(cy & (TileBake.TileSize - 1)) * TileBake.TileSize + (cx & (TileBake.TileSize - 1))];
        return Cell(g, (byte)(ld - g->Layers), cx, cy);
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static short ExcludedValue(WorldCtx* w, GridCtx* g, byte layer, byte source, Shape* s, int cx, int cy, short page)
    {
        var r = w->Recipes + layer;
        if (r->Op == LayerOp.Base) return layer == source ? ExcludeCell(g, g->Layers + layer, s, cx, cy, page) : page;
        if ((r->Reads & (1u << source)) == 0) return page;

        var a = ExcludedValue(w, g, r->A, source, s, cx, cy, Cell(g, r->A, cx, cy));
        var b = r->B == r->A ? a : ExcludedValue(w, g, r->B, source, s, cx, cy, Cell(g, r->B, cx, cy));
        return CombineCell(r, a, b);
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static long ExcludedMix(WorldCtx* w, GridCtx* g, byte layer, byte source, in Circle disk, Shape* s)
    {
        var fx0 = Math.Max(Math.Max(s->P.X0, 0), disk.X0);
        var fy0 = Math.Max(Math.Max(s->P.Y0, 0), disk.Y0);
        var fx1 = Math.Min(Math.Min(s->P.X1, g->Size) - 1, disk.X1);
        var fy1 = Math.Min(Math.Min(s->P.Y1, g->Size) - 1, disk.Y1);
        var delta = 0L;
        for (var cy = fy0; cy <= fy1; cy++)
        {
            if (!Span(disk, cy, out var lo, out var hi)) continue;

            var end = Math.Min(hi, fx1 + 1);
            for (var cx = Math.Max(lo, fx0); cx < end; cx++)
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
    private static bool SenseArea(byte world, byte layer, float x, float y, float reach, SourceId exclude, in SplatOwn own, out long total)
    {
        total = 0;
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;
        if (!Pick(w, x, y, reach, 0, true, out var gi, out var complete)) return false;

        var g = w->Grids + gi;
        var disk = Disk(g, x, y, reach);
        var ld = g->Layers + layer;
        var tps = g->TilesPerSide;
        var derived = IsDerived(w, layer);
        var lo = stackalloc int[TileBake.TileSize];
        var hi = stackalloc int[TileBake.TileSize];
        var sum = 0L;
        for (var ty = disk.Y0 >> TileBake.TileBits; ty <= disk.Y1 >> TileBake.TileBits; ty++)
        {
            var tileY = ty << TileBake.TileBits;
            var rowLo = Math.Max(tileY, disk.Y0);
            var rows = Math.Min(tileY + TileBake.TileSize - 1, disk.Y1) - rowLo + 1;
            var spans = false;
            for (var tx = disk.X0 >> TileBake.TileBits; tx <= disk.X1 >> TileBake.TileBits; tx++)
            {
                if (Outside(disk, tx, ty)) continue;

                var set = ld->SetOf(ty * tps + tx);
                if (set == null) continue;

                var tile = ty * tps + tx;
                if (Inside(disk, tx, ty))
                {
                    sum += ld->SummaryOf(tile)->Sum;
                    continue;
                }

                if (!spans)
                {
                    Spans(disk, rowLo, rows, lo, hi);
                    spans = true;
                }

                var cells = ld->Store.Cells(*set);
                sum += RaggedSum(cells.Page + (rowLo - tileY) * TileBake.TileSize,
                    derived ? null : cells.Summary->RowSums + (rowLo - tileY), tx << TileBake.TileBits, rows, lo, hi);
            }
        }

        if (Own(w, g, exclude, own, layer, out var source))
        {
            var shape = ShapeOf(w, g, source);
            sum += derived ? ExcludedMix(w, g, layer, source.Layer, disk, &shape) : ExcludedArea(g, layer, disk, &shape);
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
        if (Vector128.IsHardwareAccelerated || Vector256.IsHardwareAccelerated) return SimdOps.RaggedSum(rows0, rowSums, tileX, rows, lo, hi);
#endif
        var sum = 0L;
        for (var r = 0; r < rows; r++)
        {
            var a = Math.Max(lo[r], tileX) - tileX;
            var b = Math.Min(hi[r], tileX + TileBake.TileSize) - tileX;
            if (b <= a) continue;

            if (rowSums != null && b - a == TileBake.TileSize)
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
        public KernelTaps Taps;
        public byte HasTaps;
    }

    private struct Placed
    {
        public int Ax;
        public int Ay;
        public byte Stamp;
        public byte Layer;
        public int Gain;
        public ushort Angle;
        public ushort Size;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static Shape ShapeOf(WorldCtx* w, GridCtx* g, in Placed source)
    {
        var shape = new Shape { Gain = source.Gain };
        var precision = LayerPrecision(w, source.Layer);
        if (!TileBake.PlaceAnchored(source.Ax, source.Ay, g->ScaleQ8, g->Size, StampCatalog.Get(source.Stamp),
            source.Angle, source.Size, precision, out shape.P))
            return shape;

        var grid = (int)(g - w->Grids);
        if (KernelFactory.TryFindTaps(w, grid, source.Stamp, source.Angle, precision, &shape.P, out shape.Taps))
            shape.HasTaps = 1;
        return shape;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static void SourceAt(Shape* s, int cx, int cy, out int core, out long smooth)
    {
        core = 0;
        smooth = 0;
        var p = &s->P;
        if (s->HasTaps != 0)
        {
            var i = cx - p->Px - s->Taps.OriginX;
            var j = cy - p->Py - s->Taps.OriginY;
            if ((uint)i >= (uint)s->Taps.Width || (uint)j >= (uint)s->Taps.Height) return;

            if (s->Taps.Cells != null) core = s->Taps.Cells[j * s->Taps.Width + i] * s->Gain;
            else smooth = s->Taps.Q40[j * s->Taps.Width + i] * s->Gain;
            return;
        }

        switch (p->Plane)
        {
            case Plane.Difference:
                core = TileBake.BoxAt(p->Px, p->Py, p->Fx, p->Fy, p->ExtentX, p->ExtentY, p->V, s->Gain, cx, cy);
                return;
            case Plane.Dense when p->Turned:
                core = TileBake.TurnedDenseAt(p, cx, cy) * s->Gain;
                return;
            case Plane.Dense:
            {
                var cell = 0;
                TileBake.EmitRaster(&cell, cx, cy, p->Px, p->Py, p->Fx, p->Fy,
                    Math.Min(p->X1, cx + 1), Math.Min(p->Y1, cy + 1), p->Sampling, p->V, s->Gain);
                core = cell;
                return;
            }
            default:
            {
                if (p->Turned)
                {
                    smooth = TileBake.TurnedSmoothAt(p, cx, cy) * s->Gain;
                    return;
                }

                long wx;
                long wy;
                TileBake.SmoothWeights(p->Kind, p->Px, p->Fx, p->ExtentX, cx, 1, &wx);
                TileBake.SmoothWeights(p->Kind, p->Py, p->Fy, p->ExtentY, cy, 1, &wy);
                smooth = (long)p->V->Constant * s->Gain * p->Curve * wx * wy;
                return;
            }
        }
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static short ExcludeCell(GridCtx* g, LayerData* ld, Shape* s, int cx, int cy, short page)
    {
        SourceAt(s, cx, cy, out var core, out var smooth);
        var lx = cx & (TileBake.TileSize - 1);
        var ly = cy & (TileBake.TileSize - 1);
        var plane = s->P.Plane;
        var cells = ld->CellsOf((cy >> TileBake.TileBits) * g->TilesPerSide + (cx >> TileBake.TileBits));
        if (cells.Page == null)
            return Saturate(Absent(0, 0, 0, 0, 0, plane, core, smooth));

        var cell = ly * TileBake.TileSize + lx;
        var tents = cells.T == null ? 0 : cells.T[cell];
        var bells = cells.B == null ? 0 : cells.B[cell];
        long carry = 0;
        long tentHigh = 0;
        long bellHigh = 0;
        var summary = cells.Summary;
        if ((summary->CarryI | summary->HighT | summary->HighB) != 0)
        {
            var store = &ld->Store;
            if (summary->CarryI != 0) carry = store->Wide(summary->CarryI)[cell];
            if (summary->HighT != 0)
            {
                tentHigh = store->Wide(summary->HighT)[cell];
                tents += store->Wide(summary->LowT)[cell];
            }

            if (summary->HighB != 0)
            {
                bellHigh = store->Wide(summary->HighB)[cell];
                bells += store->Wide(summary->LowB)[cell];
            }
        }

        if (page > short.MinValue && page < short.MaxValue)
        {
            if (plane == Plane.Tent) return Saturate(page - TileBake.RoundWide(tentHigh, tents) + TileBake.RoundWide(tentHigh, tents - smooth));
            if (plane == Plane.Bell) return Saturate(page - TileBake.RoundWide(bellHigh, bells) + TileBake.RoundWide(bellHigh, bells - smooth));
            return Saturate(page - core);
        }

        var boxes = (cells.I == null ? 0 : cells.I[cell]) + carry;
        return Saturate(Absent(boxes, tents, bells, tentHigh, bellHigh, plane, core, smooth));
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
    private static long Absent(long boxes, long tents, long bells, long tentHigh, long bellHigh, Plane plane, int core, long smooth)
        => boxes - core +
            TileBake.RoundWide(tentHigh, tents - (plane == Plane.Tent ? smooth : 0)) +
            TileBake.RoundWide(bellHigh, bells - (plane == Plane.Bell ? smooth : 0));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short Saturate(long value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);

    private static void FillCore(Shape* s, int* core, int tileX, int tileY, int top, int rows, int* lo, int* hi)
    {
        const int n = TileBake.TileSize;
        var taps = &s->Taps;
        var gain = s->Gain;
        for (var r = 0; r < rows; r++)
        {
            var j = tileY + top + r - s->P.Py - taps->OriginY;
            var row = (top + r) * n;
            if ((uint)j >= (uint)taps->Height)
            {
                for (var c = lo[r]; c < hi[r]; c++) core[row + c] = 0;
                continue;
            }

            var tap = taps->Cells + j * taps->Width;
            for (var c = lo[r]; c < hi[r]; c++)
            {
                var i = tileX + c - s->P.Px - taps->OriginX;
                core[row + c] = (uint)i < (uint)taps->Width ? tap[i] * gain : 0;
            }
        }
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static long ExcludedArea(GridCtx* g, byte layer, in Circle disk, Shape* s)
    {
        var p = &s->P;
        var gain = s->Gain;
        var fx0 = Math.Max(Math.Max(p->X0, 0), disk.X0);
        var fy0 = Math.Max(Math.Max(p->Y0, 0), disk.Y0);
        var fx1 = Math.Min(Math.Min(p->X1, g->Size) - 1, disk.X1);
        var fy1 = Math.Min(Math.Min(p->Y1, g->Size) - 1, disk.Y1);
        if (fx1 < fx0 || fy1 < fy0) return 0;

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
        var ld = g->Layers + layer;
        var delta = 0L;
        for (var ty = fy0 >> TileBake.TileBits; ty <= fy1 >> TileBake.TileBits; ty++)
        for (var tx = fx0 >> TileBake.TileBits; tx <= fx1 >> TileBake.TileBits; tx++)
        {
            if (Outside(disk, tx, ty)) continue;

            var tileX = tx << TileBake.TileBits;
            var tileY = ty << TileBake.TileBits;
            var rowLo = Math.Max(tileY, fy0);
            var rows = Math.Min(tileY + n - 1, fy1) - rowLo + 1;
            Spans(disk, rowLo, rows, lo, hi);
            var cols = 0;
            var top = rowLo - tileY;
            for (var r = 0; r < rows; r++)
            {
                lo[r] = Math.Max(Math.Max(lo[r], tileX), fx0) - tileX;
                hi[r] = Math.Min(Math.Min(hi[r], tileX + n), fx1 + 1) - tileX;
                if (hi[r] > cols) cols = hi[r];
            }

            if (cols == 0) continue;

            var cells = ld->CellsOf(ty * g->TilesPerSide + tx);
            var live = cells.Page != null;
            var page = cells.Page;
            var saturated = false;
            for (var r = 0; r < rows && live; r++)
            for (var c = lo[r]; c < hi[r]; c++)
            {
                var cell = page[(top + r) * n + c];
                saturated |= cell == short.MinValue || cell == short.MaxValue;
            }

            if (s->HasTaps != 0)
            {
                if (!smooth) FillCore(s, core, tileX, tileY, top, rows, lo, hi);
            }
            else if (plane == Plane.Difference)
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

            var tents = cells.T;
            var bells = cells.B;
            var boxes = cells.I;
            long* carryP = null;
            long* highTP = null;
            long* lowTP = null;
            long* highBP = null;
            long* lowBP = null;
            var summary = cells.Summary;
            if (summary != null && (summary->CarryI | summary->HighT | summary->HighB) != 0)
            {
                var store = &ld->Store;
                if (summary->CarryI != 0) carryP = store->Wide(summary->CarryI);
                if (summary->HighT != 0)
                {
                    highTP = store->Wide(summary->HighT);
                    lowTP = store->Wide(summary->LowT);
                }

                if (summary->HighB != 0)
                {
                    highBP = store->Wide(summary->HighB);
                    lowBP = store->Wide(summary->LowB);
                }
            }

            if (!saturated && !smooth)
            {
                delta += LinearDelta(page, core, top, rows, lo, hi);
                continue;
            }

            for (var r = 0; r < rows; r++)
            {
                var row = (top + r) * n;
                var dy = separable ? scale * wy[top + r] : 0;
                for (var c = lo[r]; c < hi[r]; c++)
                {
                    var i = row + c;
                    var cell = live ? page[i] : (short)0;
                    var tq = tents == null ? 0 : tents[i];
                    var br = bells == null ? 0 : bells[i];
                    long th = 0;
                    long bh = 0;
                    if (highTP != null)
                    {
                        th = highTP[i];
                        tq += lowTP[i];
                    }

                    if (highBP != null)
                    {
                        bh = highBP[i];
                        br += lowBP[i];
                    }

                    var own = 0L;
                    if (smooth)
                    {
                        if (s->HasTaps != 0) SourceAt(s, tileX + c, tileY + top + r, out _, out own);
                        else own = separable ? dy * wx[c] : TileBake.TurnedSmoothAt(p, tileX + c, tileY + top + r) * gain;
                    }

                    long absent;
                    if (saturated && (cell == short.MinValue || cell == short.MaxValue))
                        absent = Absent((boxes == null ? 0 : boxes[i]) + (carryP == null ? 0 : carryP[i]), tq, br, th, bh, plane, smooth ? 0 : core[i], own);
                    else if (plane == Plane.Tent)
                        absent = cell - TileBake.RoundWide(th, tq) + TileBake.RoundWide(th, tq - own);
                    else if (plane == Plane.Bell)
                        absent = cell - TileBake.RoundWide(bh, br) + TileBake.RoundWide(bh, br - own);
                    else
                        absent = cell - core[i];

                    delta += Saturate(absent) - cell;
                }
            }
        }

        return delta;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static bool Applied(WorldCtx* w, GridCtx* g, SourceId id, byte layer, out Placed source)
    {
        source = default;

        var s = &w->Sources;
        var slot = (int)id.Slot;
        if ((uint)slot >= (uint)s->Count) return false;

        var mark = s->Mark[slot];
        var deposited = (int)mark == w->MarkEpoch ? w->Pending.Pointer[(int)(mark >> 32)].Gen : s->Gen[slot];
        if (deposited != id.Generation) return false;

        var r = RecordOf(g, slot);
        if (r->Alive == 0 || r->Gain == 0) return false;
        if ((w->Recipes[layer].Reads & (1u << r->Layer)) == 0) return false;

        source.Ax = r->Ax;
        source.Ay = r->Ay;
        source.Stamp = r->Stamp;
        source.Layer = r->Layer;
        source.Gain = r->Gain;
        source.Angle = r->Angle;
        source.Size = r->Scale;
        return true;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static bool Pick(WorldCtx* w, float x, float y, float reach, int margin, bool disk,
        out int grid, out bool complete)
    {
        grid = -1;
        complete = false;
        if (disk && !float.IsFinite(reach)) return false;
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
                if (ReachQ8(reach, g->ScaleQ8) > (long)ValidationRules.MaxReachCells << 8) continue;

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
    private static bool Span(in Circle c, int cy, out int lo, out int hi)
    {
        var dy = ((long)cy << 8) + 128 - c.Py;
        var rem = c.R * c.R - dy * dy;
        long l;
        long h;
        if (rem < 0)
        {
            l = c.Cx;
            h = c.Cx;
            if (cy != c.Cy)
            {
                lo = 0;
                hi = 0;
                return false;
            }
        }
        else
        {
            var reach = SquareRoot(rem);
            l = CeilCell(c.Px - reach - 128);
            h = (c.Px + reach - 128) >> 8;
            if (cy == c.Cy)
            {
                l = Math.Min(l, c.Cx);
                h = Math.Max(h, c.Cx);
            }
        }

        lo = (int)Math.Max(l, c.X0);
        hi = (int)Math.Min(h, c.X1) + 1;
        return hi > lo;
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
    {
        var q = (value - origin) * scaleQ8;
        if (MathF.Abs(q) <= MaxCellQ8) return (int)MathF.Floor(q);

        return q > 0f ? (long)MaxCellQ8 : -(long)MaxCellQ8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Contains(GridCtx* g, long qx, long qy)
        => qx >= 0 && qy >= 0 && (qx >> 8) < g->Size && (qy >> 8) < g->Size;

    internal static long ReachQ8(float reach, int scaleQ8)
    {
        if (!(reach > 0f)) return 0;

        var q = reach * scaleQ8;
        return q >= MaxReachQ8 ? MaxReachQ8 : (long)MathF.Floor(q);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long CeilCell(long q8) => -(-q8 >> 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long SquareRoot(long value)
    {
        var root = (long)Math.Sqrt(value);
        while (root * root > value) root--;
        while ((root + 1) * (root + 1) <= value) root++;
        return root;
    }

    private static long WorldArea(long cells, int scaleQ8)
    {
        var den = (long)scaleQ8 * scaleQ8;
        var num = cells * 65536;
        var magnitude = ((num < 0 ? -num : num) + den / 2) / den;
        return num < 0 ? -magnitude : magnitude;
    }
}
