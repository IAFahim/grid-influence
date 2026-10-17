using System.Runtime.CompilerServices;
#if NET
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif

namespace Gi;

internal unsafe struct HistClass
{
    public int* Directory;
    public NativeBuffer<int> Tiles;
}

internal unsafe struct HistState
{
    public const int PageBytes = TileBake.TileSize * TileBake.TileSize * sizeof(int);
    public const int PageShift = 10;
    public const long DefaultConvolveCost = 400_000;
    public const long DirectBaseCost = 400;
    public const long DirectCellCost = 12;
    public const int WindowStride = WindowSide + 8;
    public const int WindowSide = 2 * TileBake.TileSize - 1;

    public HistClass* Classes;
    public FixedSlab Pages;
    public NativeBuffer<int> Active;
    public NativeBuffer<int> OutTiles;
    public int* OutMark;
    public int MarkStamp;
    public long Total;
    public long ConvolveCost;
    public int* Window;
    public int* Strip;
    public int* Out;

    public void Release(int tileCount)
    {
        if (Classes != null)
        {
            for (var c = 0; c < 256; c++)
            {
                NativeHeap.Free(Classes[c].Directory);
                Classes[c].Tiles.Free();
            }

            NativeHeap.Free(Classes);
        }

        Pages.Reset();
        Active.Free();
        OutTiles.Free();
        NativeHeap.Free(OutMark);
        NativeHeap.Free(Window);
        NativeHeap.Free(Strip);
        NativeHeap.Free(Out);
        this = default;
    }
}

public static unsafe partial class World
{
    private static HistState* EnsureHist(LayerData* ld)
    {
        if (ld->Hist != null) return ld->Hist;

        var hist = (HistState*)NativeHeap.AllocZeroed((nuint)sizeof(HistState));
        hist->Classes = (HistClass*)NativeHeap.AllocZeroed((nuint)(256 * sizeof(HistClass)));
        hist->Window = (int*)NativeHeap.AllocZeroed(HistState.WindowStride * HistState.WindowSide * sizeof(int));
        hist->Strip = (int*)NativeHeap.AllocZeroed(HistState.WindowSide * TileBake.TileSize * sizeof(int));
        hist->Out = (int*)NativeHeap.AllocZeroed(TileBake.TileSize * TileBake.TileSize * sizeof(int));
        ld->Hist = hist;
        return hist;
    }

    private static bool HistAdd(GridCtx* g, LayerData* ld, byte stamp, int cx, int cy, int gain)
    {
        var hist = EnsureHist(ld);
        var total = hist->Total + Math.Abs(gain);
        if (total > PlaneFold.LimitI(&ld->Store)) return false;

        hist->Total = total;
        var cls = hist->Classes + stamp;
        if (cls->Directory == null) cls->Directory = (int*)NativeHeap.AllocZeroed((nuint)g->TileCount * sizeof(int));

        var tile = (cy >> TileBake.TileBits) * g->TilesPerSide + (cx >> TileBake.TileBits);
        HistPage(g, ld, hist, cls, tile)[(cy & (TileBake.TileSize - 1)) * TileBake.TileSize + (cx & (TileBake.TileSize - 1))] += gain;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int* HistPage(GridCtx* g, LayerData* ld, HistState* hist, HistClass* cls, int tile)
    {
        var slot = cls->Directory[tile] - 1;
        if (slot >= 0) return (int*)hist->Pages.Unit(slot);

        return TakeHistPage(g, ld, hist, cls, tile);
    }

    private static int* TakeHistPage(GridCtx* g, LayerData* ld, HistState* hist, HistClass* cls, int tile)
    {
        EnsureEpochs(g, ld);
        var slot = hist->Pages.Take(HistState.PageBytes);
        var page = (int*)hist->Pages.Unit(slot);
        new Span<int>(page, TileBake.TileSize * TileBake.TileSize).Clear();
        cls->Directory[tile] = slot + 1;
        if (cls->Tiles.Length == 0)
        {
            var n = hist->Active.Length;
            hist->Active.Resize(n + 1);
            hist->Active.Pointer[n] = (int)(cls - hist->Classes);
        }

        var count = cls->Tiles.Length;
        cls->Tiles.Resize(count + 1);
        cls->Tiles.Pointer[count] = tile;
        return page;
    }

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int SplatBlocks(
        WorldCtx* w, int gi, GridCtx* g, LayerData* ld, HistState* hist, HistClass* cls,
        ref DepositRecord key, int mask, float* xs, float* ys, int count)
    {
        var originX = Vector256.Create(g->OriginX);
        var originY = Vector256.Create(g->OriginY);
        var scale = Vector256.Create((float)g->ScaleQ8);
        var limit = Vector256.Create(g->Size - 1);
        var infinity = Vector256.Create(float.PositiveInfinity);
        var tilesPerSide = Vector256.Create(g->TilesPerSide);
        var tileMask = Vector256.Create(TileBake.TileSize - 1);
        var one = Vector256.Create(1);
        var directory = cls->Directory;
        var tiles = stackalloc int[8];
        var offsets = stackalloc int[8];
        var gain = (int)key.Gain;
        var i = 0;
        for (; i + 8 <= count; i += 8)
        {
            var x = Vector256.Load(xs + i);
            var y = Vector256.Load(ys + i);
            var cx = Vector256.ShiftRightArithmetic(
                Avx.ConvertToVector256Int32WithTruncation(Vector256.Floor((x - originX) * scale)), 8);
            var cy = Vector256.ShiftRightArithmetic(
                Avx.ConvertToVector256Int32WithTruncation(Vector256.Floor((y - originY) * scale)), 8);
            var clampedX = Vector256.Min(Vector256.Max(cx, Vector256<int>.Zero), limit);
            var clampedY = Vector256.Min(Vector256.Max(cy, Vector256<int>.Zero), limit);
            var inside = Vector256.Equals(clampedX, cx) & Vector256.Equals(clampedY, cy)
                & (Vector256.LessThan(Vector256.Abs(x), infinity) & Vector256.LessThan(Vector256.Abs(y), infinity)).AsInt32();
            var accepted = inside.AsSingle().ExtractMostSignificantBits();
            var tileVector = Vector256.ShiftRightArithmetic(clampedY, TileBake.TileBits) * tilesPerSide
                + Vector256.ShiftRightArithmetic(clampedX, TileBake.TileBits);
            var local = Vector256.ShiftLeft(clampedY & tileMask, TileBake.TileBits) + (clampedX & tileMask);
            var firstTile = tileVector.GetElement(0);
            if (accepted == 0xFF && Vector256.Equals(tileVector, Vector256.Create(firstTile)).AsSingle().ExtractMostSignificantBits() == 0xFF)
            {
                var slot = directory[firstTile] - 1;
                int* page = slot >= 0 ? (int*)hist->Pages.Unit(slot) : HistPage(g, ld, hist, cls, firstTile);
                local.Store(offsets);
                page[offsets[0]] += gain;
                page[offsets[1]] += gain;
                page[offsets[2]] += gain;
                page[offsets[3]] += gain;
                page[offsets[4]] += gain;
                page[offsets[5]] += gain;
                page[offsets[6]] += gain;
                page[offsets[7]] += gain;
                continue;
            }

            var slots = Avx2.GatherVector256(directory, tileVector, 4);
            var missing = Vector256.Equals(slots, Vector256<int>.Zero).AsSingle().ExtractMostSignificantBits() & accepted;
            if (missing != 0)
            {
                tileVector.Store(tiles);
                for (var bits = missing; bits != 0; bits &= bits - 1)
                    HistPage(g, ld, hist, cls, tiles[BitOperations.TrailingZeroCount(bits)]);

                slots = Avx2.GatherVector256(directory, tileVector, 4);
            }

            var pages = (int*)hist->Pages.Region;
            (Vector256.ShiftLeft(slots - one, HistState.PageShift) + local).Store(offsets);
            if (accepted == 0xFF)
            {
                pages[offsets[0]] += gain;
                pages[offsets[1]] += gain;
                pages[offsets[2]] += gain;
                pages[offsets[3]] += gain;
                pages[offsets[4]] += gain;
                pages[offsets[5]] += gain;
                pages[offsets[6]] += gain;
                pages[offsets[7]] += gain;
                continue;
            }

            for (var bits = accepted; bits != 0; bits &= bits - 1)
                pages[offsets[BitOperations.TrailingZeroCount(bits)]] += gain;

            for (var lane = 0; lane < 8; lane++)
                if ((accepted >> lane & 1) == 0)
                    SplatOne(w, gi, g, ld, hist, cls, ref key, mask, xs[i + lane], ys[i + lane]);
        }

        return i;
    }
#endif

    private static void FlushHistogram(WorldCtx* w, int gi, GridCtx* g, LayerData* ld, byte layer)
    {
        var hist = ld->Hist;
        if (hist == null || hist->Active.Length == 0) return;

        EnsureDirty(g, layer);
        for (var a = 0; a < hist->Active.Length; a++)
        {
            var stamp = (byte)hist->Active.Pointer[a];
            var cls = hist->Classes + stamp;
            if (BoxKernel(w, gi, g, layer, stamp, out var value, out var ox, out var oy, out var width, out var height))
            {
                ReplaySparse(w, gi, g, hist, cls, layer, stamp, width * height);
                if (cls->Tiles.Length != 0) BoxFilterClass(g, ld, hist, cls, value, ox, oy, width, height);
            }
            else
            {
                ReplayClass(w, gi, g, hist, cls, layer, stamp);
            }

            for (var t = 0; t < cls->Tiles.Length; t++)
            {
                var tile = cls->Tiles.Pointer[t];
                hist->Pages.Give(cls->Directory[tile] - 1);
                cls->Directory[tile] = 0;
            }

            cls->Tiles.Resize(0);
        }

        hist->Active.Resize(0);
        hist->Total = 0;
    }

    private static void ResetHistogram(LayerData* ld)
    {
        var hist = ld->Hist;
        if (hist == null) return;

        for (var a = 0; a < hist->Active.Length; a++)
        {
            var cls = hist->Classes + hist->Active.Pointer[a];
            for (var t = 0; t < cls->Tiles.Length; t++)
            {
                var tile = cls->Tiles.Pointer[t];
                hist->Pages.Give(cls->Directory[tile] - 1);
                cls->Directory[tile] = 0;
            }

            cls->Tiles.Resize(0);
        }

        hist->Active.Resize(0);
        hist->Total = 0;
    }

    private static bool BoxKernel(
        WorldCtx* w, int gi, GridCtx* g, byte layer, byte stamp,
        out int value, out int ox, out int oy, out int width, out int height)
    {
        value = 0;
        ox = 0;
        oy = 0;
        width = 0;
        height = 0;
        var v = StampCatalog.Get(stamp);
        if (v->Kind != StampKind.ConstantRectangle) return false;

        var precision = LayerPrecision(w, layer);
        var anchorCell = g->Size >> 1;
        var anchor = anchorCell << 8;
        if (!TileBake.PlaceAnchored(anchor, anchor, g->ScaleQ8, g->Size, v, 0, UnitScale, precision, out var p)) return false;
        if (p.Plane != Plane.Difference || !TileBake.Unclipped(v, &p)) return false;
        if (!KernelFactory.TryGetTaps(w, gi, stamp, 0, precision, &p, out var taps) || taps.Cells == null) return false;
        if (taps.Width <= 0 || taps.Height <= 0 || taps.Width > TileBake.TileSize || taps.Height > TileBake.TileSize) return false;

        value = taps.Cells[0];
        if (value == 0) return false;

        for (var i = 1; i < taps.Width * taps.Height; i++)
            if (taps.Cells[i] != value) return false;

        ox = p.Px + taps.OriginX - anchorCell;
        oy = p.Py + taps.OriginY - anchorCell;
        width = taps.Width;
        height = taps.Height;
        return ox >= -TileBake.TileSize && ox + width <= TileBake.TileSize + 1 &&
               oy >= -TileBake.TileSize && oy + height <= TileBake.TileSize + 1;
    }

    private static void ReplayClass(WorldCtx* w, int gi, GridCtx* g, HistState* hist, HistClass* cls, byte layer, byte stamp)
    {
        var key = new DepositRecord { Layer = layer, Stamp = stamp, Alive = 1, Scale = UnitScale };
        for (var t = 0; t < cls->Tiles.Length; t++)
        {
            var tile = cls->Tiles.Pointer[t];
            ReplayTile(w, gi, g, ref key, tile, (int*)hist->Pages.Unit(cls->Directory[tile] - 1));
        }
    }

    private static void ReplaySparse(
        WorldCtx* w, int gi, GridCtx* g, HistState* hist, HistClass* cls, byte layer, byte stamp, int footprint)
    {
        var convolve = hist->ConvolveCost == 0 ? HistState.DefaultConvolveCost : hist->ConvolveCost;
        var direct = HistState.DirectBaseCost + HistState.DirectCellCost * footprint;
        var budget = convolve / direct;
        var active = 0L;
        for (var t = 0; t < cls->Tiles.Length; t++)
        {
            active += CountNonZero((int*)hist->Pages.Unit(cls->Directory[cls->Tiles.Pointer[t]] - 1));
            if (active > budget) return;
        }

        var key = new DepositRecord { Layer = layer, Stamp = stamp, Alive = 1, Scale = UnitScale };
        for (var t = 0; t < cls->Tiles.Length; t++)
        {
            var tile = cls->Tiles.Pointer[t];
            ReplayTile(w, gi, g, ref key, tile, (int*)hist->Pages.Unit(cls->Directory[tile] - 1));
            hist->Pages.Give(cls->Directory[tile] - 1);
            cls->Directory[tile] = 0;
        }

        cls->Tiles.Resize(0);
    }

    private static void ReplayTile(WorldCtx* w, int gi, GridCtx* g, ref DepositRecord key, int tile, int* page)
    {
        var tps = g->TilesPerSide;
        var baseX = (tile % tps) << TileBake.TileBits;
        var baseY = (tile / tps) << TileBake.TileBits;
        for (var cell = 0; cell < TileBake.TileSize * TileBake.TileSize; cell++)
        {
            var remaining = page[cell];
            if (remaining == 0) continue;

            key.Ax = (baseX + (cell & (TileBake.TileSize - 1))) << 8;
            key.Ay = (baseY + (cell >> TileBake.TileBits)) << 8;
            while (remaining != 0)
            {
                var step = Math.Clamp(remaining, -127, 127);
                key.Gain = (sbyte)step;
                EmitDelta(w, gi, in key, 1);
                remaining -= step;
            }
        }
    }

    private static int CountNonZero(int* page)
    {
        var count = 0;
        var i = 0;
#if NET
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 8 <= TileBake.TileSize * TileBake.TileSize; i += 8)
            {
                var zero = Vector256.Equals(Vector256.Load(page + i), Vector256<int>.Zero).ExtractMostSignificantBits();
                count += 8 - BitOperations.PopCount(zero);
            }
        }
#endif
        for (; i < TileBake.TileSize * TileBake.TileSize; i++)
            if (page[i] != 0) count++;

        return count;
    }

    private static void BoxFilterClass(
        GridCtx* g, LayerData* ld, HistState* hist, HistClass* cls, int value, int ox, int oy, int width, int height)
    {
        var tps = g->TilesPerSide;
        var size = g->Size;
        hist->MarkStamp++;
        if (hist->OutMark == null) hist->OutMark = (int*)NativeHeap.AllocZeroed((nuint)g->TileCount * sizeof(int));
        hist->OutTiles.Resize(0);
        for (var t = 0; t < cls->Tiles.Length; t++)
        {
            var tile = cls->Tiles.Pointer[t];
            var hx0 = (tile % tps) << TileBake.TileBits;
            var hy0 = (tile / tps) << TileBake.TileBits;
            var txLo = Math.Max((hx0 + ox) >> TileBake.TileBits, 0);
            var txHi = Math.Min((hx0 + TileBake.TileSize - 1 + ox + width - 1) >> TileBake.TileBits, tps - 1);
            var tyLo = Math.Max((hy0 + oy) >> TileBake.TileBits, 0);
            var tyHi = Math.Min((hy0 + TileBake.TileSize - 1 + oy + height - 1) >> TileBake.TileBits, tps - 1);
            for (var ty = tyLo; ty <= tyHi; ty++)
            for (var tx = txLo; tx <= txHi; tx++)
            {
                var index = ty * tps + tx;
                if (hist->OutMark[index] == hist->MarkStamp) continue;

                hist->OutMark[index] = hist->MarkStamp;
                var n = hist->OutTiles.Length;
                hist->OutTiles.Resize(n + 1);
                hist->OutTiles.Pointer[n] = index;
            }
        }

        var windowWidth = TileBake.TileSize + width - 1;
        var windowHeight = TileBake.TileSize + height - 1;
        for (var o = 0; o < hist->OutTiles.Length; o++)
        {
            var outTile = hist->OutTiles.Pointer[o];
            var tx0 = (outTile % tps) << TileBake.TileBits;
            var ty0 = (outTile / tps) << TileBake.TileBits;
            GatherWindow(hist, cls, tps, size, tx0 - ox - width + 1, ty0 - oy - height + 1, windowWidth, windowHeight);

            var peak = SumWindow(hist, width, height, windowHeight);
            if (peak == 0) continue;

            var sums = hist->Out;
            var set = EnsureSet(g, ld, outTile);
            PrepareTransientSet(ld, set);
            var dirty = DirtyIndex(g, ld, outTile);
            ld->Rects.Pointer[dirty] = new TileRect { X0 = 0, Y0 = 0, X1 = TileBake.TileSize, Y1 = TileBake.TileSize };
            var amount = peak * Math.Abs((long)value);
            if (amount > PlaneFold.LimitI(&ld->Store))
            {
                PlaneFold.AddWideI(&ld->Store, set, sums, value);
                continue;
            }

            PlaneFold.ChargeI(&ld->Store, set, amount);
            AddScaled(ld->Store.Cells(*set).I, sums, value);
        }
    }

    private static long SumWindow(HistState* hist, int width, int height, int windowHeight)
    {
        var window = hist->Window;
        var strip = hist->Strip;
        var sums = hist->Out;
#if NET
        if (Vector256.IsHardwareAccelerated)
        {
            for (var wy = 0; wy < windowHeight; wy++)
            {
                var row = window + wy * HistState.WindowStride;
                var outRow = strip + wy * TileBake.TileSize;
                for (var block = 0; block < TileBake.TileSize; block += 8)
                {
                    var acc = Vector256.Load(row + block);
                    for (var i = 1; i < width; i++) acc += Vector256.Load(row + block + i);
                    acc.Store(outRow + block);
                }
            }

            var peak = Vector256<int>.Zero;
            for (var y = 0; y < TileBake.TileSize; y++)
            for (var block = 0; block < TileBake.TileSize; block += 8)
            {
                var acc = Vector256.Load(strip + y * TileBake.TileSize + block);
                for (var j = 1; j < height; j++) acc += Vector256.Load(strip + (y + j) * TileBake.TileSize + block);
                acc.Store(sums + y * TileBake.TileSize + block);
                peak = Vector256.Max(peak, Vector256.Abs(acc));
            }

            var best = 0;
            for (var lane = 0; lane < 8; lane++) best = Math.Max(best, peak.GetElement(lane));
            return best;
        }
#endif
        for (var wy = 0; wy < windowHeight; wy++)
        {
            var row = window + wy * HistState.WindowStride;
            var outRow = strip + wy * TileBake.TileSize;
            var sum = 0;
            for (var i = 0; i < width; i++) sum += row[i];
            outRow[0] = sum;
            for (var x = 1; x < TileBake.TileSize; x++)
            {
                sum += row[x + width - 1] - row[x - 1];
                outRow[x] = sum;
            }
        }

        long largest = 0;
        for (var y = 0; y < TileBake.TileSize; y++)
        for (var x = 0; x < TileBake.TileSize; x++)
        {
            var sum = 0;
            for (var j = 0; j < height; j++) sum += strip[(y + j) * TileBake.TileSize + x];
            sums[y * TileBake.TileSize + x] = sum;
            if (Math.Abs((long)sum) > largest) largest = Math.Abs((long)sum);
        }

        return largest;
    }

    private static void AddScaled(int* plane, int* sums, int value)
    {
        var i = 0;
#if NET
        if (Vector256.IsHardwareAccelerated)
        {
            var scale = Vector256.Create(value);
            for (; i + 8 <= TileBake.TileSize * TileBake.TileSize; i += 8)
                (Vector256.Load(plane + i) + Vector256.Load(sums + i) * scale).Store(plane + i);
        }
#endif
        for (; i < TileBake.TileSize * TileBake.TileSize; i++) plane[i] += sums[i] * value;
    }

    private static void GatherWindow(
        HistState* hist, HistClass* cls, int tps, int size, int x0, int y0, int windowWidth, int windowHeight)
    {
        var window = hist->Window;
        var wy = 0;
        while (wy < windowHeight)
        {
            var ay = y0 + wy;
            int rows;
            if ((uint)ay >= (uint)size)
            {
                rows = ay < 0 ? Math.Min(-ay, windowHeight - wy) : windowHeight - wy;
                ClearBlock(window + wy * HistState.WindowStride, rows, windowWidth);
                wy += rows;
                continue;
            }

            var tileRow = ay >> TileBake.TileBits;
            rows = Math.Min(windowHeight - wy, ((tileRow + 1) << TileBake.TileBits) - ay);
            var rowTiles = tileRow * tps;
            var rowAt = (ay & (TileBake.TileSize - 1)) * TileBake.TileSize;
            var target = window + wy * HistState.WindowStride;
            var x = 0;
            while (x < windowWidth)
            {
                var ax = x0 + x;
                if ((uint)ax >= (uint)size)
                {
                    var outside = ax < 0 ? Math.Min(-ax, windowWidth - x) : windowWidth - x;
                    ClearBlock(target + x, rows, outside);
                    x += outside;
                    continue;
                }

                var tileColumn = ax >> TileBake.TileBits;
                var span = Math.Min(windowWidth - x, ((tileColumn + 1) << TileBake.TileBits) - ax);
                var slot = cls->Directory[rowTiles + tileColumn] - 1;
                if (slot < 0) ClearBlock(target + x, rows, span);
                else CopyBlock(target + x, (int*)hist->Pages.Unit(slot) + rowAt + (ax & (TileBake.TileSize - 1)), rows, span);
                x += span;
            }

            wy += rows;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyBlock(int* destination, int* source, int rows, int span)
    {
        for (var r = 0; r < rows; r++)
        {
            var to = destination + r * HistState.WindowStride;
            var from = source + r * TileBake.TileSize;
#if NET
            if (Vector256.IsHardwareAccelerated)
            {
                for (var k = 0; k < span; k += 8) Vector256.Store(Vector256.Load(from + k), to + k);
                continue;
            }
#endif
            for (var k = 0; k < span; k++) to[k] = from[k];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ClearBlock(int* destination, int rows, int span)
    {
        for (var r = 0; r < rows; r++)
        {
            var to = destination + r * HistState.WindowStride;
#if NET
            if (Vector256.IsHardwareAccelerated)
            {
                for (var k = 0; k < span; k += 8) Vector256.Store(Vector256<int>.Zero, to + k);
                continue;
            }
#endif
            for (var k = 0; k < span; k++) to[k] = 0;
        }
    }
}
