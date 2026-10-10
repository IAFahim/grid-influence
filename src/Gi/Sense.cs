using System.Runtime.CompilerServices;
#if NET
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif

namespace Gi;

public static unsafe partial class World
{
    private const long MaxReachQ8 = 1L << 24;

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

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    public static bool TrySense(byte world, byte layer, float x, float y, int exclude, out short value)
    {
        value = 0;
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;
        if (!Pick(w, x, y, 0f, 0, false, out var gi, out var complete)) return false;

        var g = w->Grids + gi;
        var cx = (int)(CellQ8(x, g->OriginX, g->ScaleQ8) >> 8);
        var cy = (int)(CellQ8(y, g->OriginY, g->ScaleQ8) >> 8);
        value = Query(world, (byte)gi, layer, cx, cy);
        if (!Applied(w, exclude, layer, out var source)) return complete;

        var shape = ShapeOf(g, source);
        if (shape.P.Reaches(cx, cy)) value = ExcludedValue(w, g, layer, source.Layer, &shape, cx, cy, value);
        return complete;
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
        ScanDiskTile(g, pages, disk, top % tps, top / tps, lo, hi, ref found, ref best, ref bx, ref by);
        for (var ty = disk.Y0 >> TileBake.TileBits; ty <= disk.Y1 >> TileBake.TileBits; ty++)
        for (var tx = disk.X0 >> TileBake.TileBits; tx <= disk.X1 >> TileBake.TileBits; tx++)
        {
            var tile = ty * tps + tx;
            if (tile == top || Outside(disk, tx, ty)) continue;

            var m = pages->TryGet(tile, out var block) ? *(short*)(block + MaxOffset) : (short)0;
            if (m < best) continue;
            if (m == best && (ty << TileBake.TileBits) > by) continue;
            ScanDiskTile(g, pages, disk, tx, ty, lo, hi, ref found, ref best, ref bx, ref by);
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

                    var live = pages->TryGet(ty * tps + tx, out var block);
                    if ((live ? *(short*)(block + MaxOffset) : 0) < threshold) continue;
                    NearestInTile(disk, live ? (short*)(block + PageOffset) : null, tx, ty, threshold, ref best, ref bx, ref by);
                }
            }
        }

        if (best == long.MaxValue) return complete;

        value = Cell(g, layer, bx, by);
        nearestX = g->OriginX + ((bx << 8) + 128) / (float)g->ScaleQ8;
        nearestY = g->OriginY + ((by << 8) + 128) / (float)g->ScaleQ8;
        return complete;
    }

    private static void NearestInTile(in Circle disk, short* page, int tx, int ty, short threshold, ref long best, ref int bx, ref int by)
    {
        var tileX = tx << TileBake.TileBits;
        var tileY = ty << TileBake.TileBits;
        var rowEnd = Math.Min(tileY + TileBake.TileSize - 1, disk.Y1);
        for (var cy = Math.Max(tileY, disk.Y0); cy <= rowEnd; cy++)
        {
            var dy = ((long)cy << 8) + 128 - disk.Py;
            if (dy * dy > best || !Span(disk, cy, out var lo, out var hi)) continue;

            var end = Math.Min(hi, tileX + TileBake.TileSize);
            var row = page == null ? null : page + (cy - tileY) * TileBake.TileSize - tileX;
            for (var cx = Math.Max(lo, tileX); cx < end; cx++)
            {
                if (row != null && row[cx] < threshold) continue;

                var dx = ((long)cx << 8) + 128 - disk.Px;
                var d = dx * dx + dy * dy;
                if (d > best || (d == best && (cy > by || (cy == by && cx >= bx)))) continue;

                best = d;
                bx = cx;
                by = cy;
            }
        }
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static void ScanDiskTile(GridCtx* g, PageMap* pages, in Circle disk, int tx, int ty, int* lo, int* hi,
        ref bool found, ref short best, ref int bx, ref int by)
    {
        var tileX = tx << TileBake.TileBits;
        var tileY = ty << TileBake.TileBits;
        var rowLo = Math.Max(tileY, disk.Y0);
        var rows = Math.Min(tileY + TileBake.TileSize - 1, disk.Y1) - rowLo + 1;
        Spans(disk, rowLo, rows, lo, hi);
        short* page = null;
        if (pages->TryGet(ty * g->TilesPerSide + tx, out var block)) page = (short*)(block + PageOffset);
        for (var r = 0; r < rows; r++)
        {
            var a = Math.Max(lo[r], tileX);
            var b = Math.Min(hi[r], tileX + TileBake.TileSize);
            if (b <= a) continue;

            var cy = rowLo + r;
            var row = page == null ? null : page + (cy - tileY) * TileBake.TileSize - tileX;
            var m = row == null ? (short)0 : SegmentMax(row + tileX, a - tileX, b - tileX);
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
        if (Avx2.IsSupported)
        {
            var low = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);
            var high = low + Vector256.Create((short)16);
            var from = Vector256.Create((short)(a - 1));
            var to = Vector256.Create((short)b);
            var floor = Vector256.Create(short.MinValue);
            var v0 = Avx2.BlendVariable(floor, Avx.LoadVector256(row),
                (Avx2.CompareGreaterThan(low, from) & Avx2.CompareGreaterThan(to, low)).AsByte().AsInt16());
            var v1 = Avx2.BlendVariable(floor, Avx.LoadVector256(row + 16),
                (Avx2.CompareGreaterThan(high, from) & Avx2.CompareGreaterThan(to, high)).AsByte().AsInt16());
            var m = Avx2.Max(v0, v1);
            var h = Sse2.Max(m.GetLower(), m.GetUpper());
            h = Sse2.Max(h, Sse2.ShuffleHigh(Sse2.ShuffleLow(h, 0x4e), 0x4e));
            h = Sse2.Max(h, Sse2.Shuffle(h.AsInt32(), 0x4e).AsInt16());
            h = Sse2.Max(h, Sse2.ShuffleLow(Sse2.ShuffleHigh(h, 0xb1), 0xb1));
            return h.ToScalar();
        }
#endif
        var best = row[a];
        for (var i = a + 1; i < b; i++)
            if (row[i] > best) best = row[i];
        return best;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FirstEqual(short* row, int a, int b, short value)
    {
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
        var grid = (byte)gi;
        var cx = (int)(CellQ8(x, g->OriginX, g->ScaleQ8) >> 8);
        var cy = (int)(CellQ8(y, g->OriginY, g->ScaleQ8) >> 8);
        var perUnit = g->ScaleQ8 / 512f;
        gx = (Query(world, grid, layer, cx + 1, cy) - Query(world, grid, layer, cx - 1, cy)) * perUnit;
        gy = (Query(world, grid, layer, cx, cy + 1) - Query(world, grid, layer, cx, cy - 1)) * perUnit;
        return complete;
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
        if (!Pick(w, x, y, 0f, 1, false, out var gi, out var complete)) return false;

        var g = w->Grids + gi;
        var cx = (int)(CellQ8(x, g->OriginX, g->ScaleQ8) >> 8);
        var cy = (int)(CellQ8(y, g->OriginY, g->ScaleQ8) >> 8);
        var excluding = Applied(w, exclude, layer, out var source);
        var shape = excluding ? ShapeOf(g, source) : default;
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
                if (Outside(disk, tx, ty) || !pages->TryGet(ty * tps + tx, out var block)) continue;

                if (Inside(disk, tx, ty))
                {
                    sum += *(long*)(block + SumOffset);
                    continue;
                }

                if (!spans)
                {
                    Spans(disk, rowLo, rows, lo, hi);
                    spans = true;
                }

                sum += RaggedSum((short*)(block + PageOffset) + (rowLo - tileY) * TileBake.TileSize,
                    tx << TileBake.TileBits, rows, lo, hi);
            }
        }

        if (Applied(w, exclude, layer, out var source))
        {
            var shape = ShapeOf(g, source);
            sum += IsDerived(w, layer) ? ExcludedMix(w, g, layer, source.Layer, disk, &shape) : ExcludedArea(g, layer, disk, &shape);
        }

        total = WorldArea(sum, g->ScaleQ8);
        return complete;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static long RaggedSum(short* rows0, int tileX, int rows, int* lo, int* hi)
    {
#if NET
        if (Avx2.IsSupported)
        {
            var low = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);
            var high = low + Vector256.Create((short)16);
            var ones = Vector256.Create((short)1);
            var acc = Vector256<int>.Zero;
            for (var r = 0; r < rows; r++)
            {
                var a = Math.Max(lo[r], tileX) - tileX;
                var b = Math.Min(hi[r], tileX + TileBake.TileSize) - tileX;
                if (b <= a) continue;

                var from = Vector256.Create((short)(a - 1));
                var to = Vector256.Create((short)b);
                var row = rows0 + r * TileBake.TileSize;
                var v0 = Avx.LoadVector256(row) & Avx2.CompareGreaterThan(low, from) & Avx2.CompareGreaterThan(to, low);
                var v1 = Avx.LoadVector256(row + 16) & Avx2.CompareGreaterThan(high, from) & Avx2.CompareGreaterThan(to, high);
                acc += Avx2.MultiplyAddAdjacent(v0, ones) + Avx2.MultiplyAddAdjacent(v1, ones);
            }

            var lanes = acc.GetLower() + acc.GetUpper();
            return (long)lanes[0] + lanes[1] + lanes[2] + lanes[3];
        }
#endif
        var sum = 0L;
        for (var r = 0; r < rows; r++)
        {
            var a = Math.Max(lo[r], tileX) - tileX;
            var b = Math.Min(hi[r], tileX + TileBake.TileSize) - tileX;
            var row = rows0 + r * TileBake.TileSize;
            for (var i = a; i < b; i++) sum += row[i];
        }

        return sum;
    }

    private struct Shape
    {
        public Placement P;
        public int Gain;
    }

    private struct Placed
    {
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
        var shape = new Shape { Gain = source.Gain };
        TileBake.Place(source.X, source.Y, g->OriginX, g->OriginY, g->ScaleQ8, g->Size, StampCatalog.Get(source.Stamp),
            source.Angle, source.Size, out shape.P);
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
    private static short ExcludeCell(GridCtx* g, byte layer, Shape* s, int cx, int cy, short page)
    {
        SourceAt(s, cx, cy, out var core, out var smooth);
        var lx = cx & (TileBake.TileSize - 1);
        var ly = cy & (TileBake.TileSize - 1);
        var plane = s->P.Plane;
        if (!g->Layers[layer].Pages.TryGet((cy >> TileBake.TileBits) * g->TilesPerSide + (cx >> TileBake.TileBits), out var block))
            return Saturate(Absent(0, 0, 0, plane, core, smooth));

        var cell = ly * TileBake.TileSize + lx;
        var tent = TentOf(block);
        var bell = BellOf(block);
        var tents = tent == null ? 0 : tent[cell];
        var bells = bell == null ? 0 : bell[cell];
        if (page > short.MinValue && page < short.MaxValue)
        {
            if (plane == Plane.Tent) return Saturate(page - TileBake.RoundQ40(tents) + TileBake.RoundQ40(tents - smooth));
            if (plane == Plane.Bell) return Saturate(page - TileBake.RoundQ40(bells) + TileBake.RoundQ40(bells - smooth));
            return Saturate(page - core);
        }

        var boxes = (int)TileBake.Quadrant(DiffOf(block), lx, ly) + DenseOf(block)[cell];
        return Saturate(Absent(boxes, tents, bells, plane, core, smooth));
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static long LinearDelta(short* page, int* core, int top, int rows, int* lo, int* hi)
    {
        const int n = TileBake.TileSize;
        var delta = 0L;
        for (var r = 0; r < rows; r++)
        {
            var row = (top + r) * n;
            var c = lo[r];
#if NET
            if (Avx2.IsSupported && page != null)
            {
                var floor = Vector256.Create((int)short.MinValue);
                var ceiling = Vector256.Create((int)short.MaxValue);
                var acc = Vector256<int>.Zero;
                for (; c + 8 <= hi[r]; c += 8)
                {
                    var p = Avx2.ConvertToVector256Int32(page + row + c);
                    var absent = Avx2.Min(Avx2.Max(p - Avx.LoadVector256(core + row + c), floor), ceiling);
                    acc += absent - p;
                }

                var lanes = acc.GetLower() + acc.GetUpper();
                delta += (long)lanes[0] + lanes[1] + lanes[2] + lanes[3];
            }
#endif
            for (; c < hi[r]; c++)
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
        var boxes = stackalloc int[n * n];
        var wx = stackalloc long[n];
        var wy = stackalloc long[n];
        var plane = p->Plane;
        var smooth = plane is Plane.Tent or Plane.Bell;
        var separable = smooth && !p->Turned;
        var scale = separable ? (long)p->V->Constant * gain * p->Curve : 0;
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

            var live = pages->TryGet(ty * g->TilesPerSide + tx, out var block);
            var page = live ? (short*)(block + PageOffset) : null;
            var saturated = false;
            for (var r = 0; r < rows && live; r++)
            for (var c = lo[r]; c < hi[r]; c++)
            {
                var cell = page[(top + r) * n + c];
                saturated |= cell == short.MinValue || cell == short.MaxValue;
            }

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

            var tents = live ? TentOf(block) : null;
            var bells = live ? BellOf(block) : null;
            if (saturated) TileBake.IntegrateBoxes(DiffOf(block), DenseOf(block), top + rows, cols, boxes);

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
                    var own = !smooth ? 0 : separable ? dy * wx[c] : TileBake.TurnedSmoothAt(p, tileX + c, tileY + top + r) * gain;
                    long absent;
                    if (saturated && (cell == short.MinValue || cell == short.MaxValue))
                        absent = Absent(boxes[i], tq, br, plane, smooth ? 0 : core[i], own);
                    else if (plane == Plane.Tent)
                        absent = cell - TileBake.RoundQ40(tq) + TileBake.RoundQ40(tq - own);
                    else if (plane == Plane.Bell)
                        absent = cell - TileBake.RoundQ40(br) + TileBake.RoundQ40(br - own);
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
    private static bool Applied(WorldCtx* w, int id, byte layer, out Placed source)
    {
        source = default;
        if (id < 0) return false;

        var s = &w->Sources;
        var slot = id & SourceIndexMask;
        if ((uint)slot >= (uint)s->Count) return false;

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
