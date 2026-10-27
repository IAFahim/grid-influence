using System.Runtime.CompilerServices;
#if NET
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif

namespace Gi;

internal enum SplatStop : byte
{
    End,
    Rejected,
    Missing
}

internal unsafe struct HistClass
{
    public int* Directory;
    public NativeBuffer<int> Tiles;
}

internal unsafe struct HistState
{
    public const int PageBytes = TileBake.TileSize * TileBake.TileSize * sizeof(int);
    public const int PageShift = 10;
    public const int PageInts = 1 << PageShift;
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
    public int* ZeroPage;
    public NativeBuffer<FusedClass> FusedClasses;
    public NativeBuffer<FusedTile> FusedTiles;
    public NativeBuffer<int> Vanished;

    [MethodImpl(Jit.Hot)]
    public void Release()
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
        NativeHeap.Free(ZeroPage);
        FusedClasses.Free();
        FusedTiles.Free();
        Vanished.Free();
        this = default;
    }
}

public static unsafe partial class World
{
    [MethodImpl(Jit.Hot)]
    private static HistState* EnsureHist(LayerData* ld)
    {
        if (ld->Hist != null) return ld->Hist;

        var hist = (HistState*)NativeHeap.AllocZeroed((nuint)sizeof(HistState));
        hist->Classes = (HistClass*)NativeHeap.AllocZeroed((nuint)(256 * sizeof(HistClass)));
        hist->Window = (int*)NativeHeap.AllocZeroed(HistState.WindowStride * HistState.WindowSide * sizeof(int));
        hist->Strip = (int*)NativeHeap.AllocZeroed(HistState.WindowSide * TileBake.TileSize * sizeof(int));
        hist->Out = (int*)NativeHeap.AllocZeroed(TileBake.TileSize * TileBake.TileSize * sizeof(int));
        hist->ZeroPage = (int*)NativeHeap.AllocZeroed(HistState.PageBytes);
        ld->Hist = hist;
        return hist;
    }

    [MethodImpl(Jit.Hot)]
    private static bool HistAdd(GridCtx* g, LayerData* ld, byte stamp, int cx, int cy, int gain)
    {
        var hist = EnsureHist(ld);
        var total = hist->Total + Math.Abs(gain);
        if (total > PlaneFold.LimitI(&ld->Store)) return false;

        hist->Total = total;
        var cls = hist->Classes + stamp;
        if (cls->Directory == null) cls->Directory = (int*)NativeHeap.AllocZeroed((nuint)g->TileCount * sizeof(int));

        var tile = (cy >> TileBake.TileBits) * g->TilesPerSide + (cx >> TileBake.TileBits);
        HistPage(g, ld, hist, cls, tile)[
            (cy & (TileBake.TileSize - 1)) * TileBake.TileSize + (cx & (TileBake.TileSize - 1))] += gain;
        return true;
    }

    [MethodImpl(Jit.Inline)]
    private static int* HistPage(GridCtx* g, LayerData* ld, HistState* hist, HistClass* cls, int tile)
    {
        var slot = cls->Directory[tile] - 1;
        if (slot >= 0) return (int*)hist->Pages.Unit(slot);

        return TakeHistPage(g, ld, hist, cls, tile);
    }

    [MethodImpl(Jit.Hot)]
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
    [MethodImpl(Jit.Hot)]
    private static int SplatBlocks(
        WorldCtx* w, int gi, GridCtx* g, LayerData* ld, HistState* hist, HistClass* cls,
        ref DepositRecord key, int mask, float* xs, float* ys, int count, ref int finite)
    {
        var tiles = stackalloc int[8];
        var i = 0;
        while (i + 8 <= count)
        {
            var next = SplatRun(g, cls->Directory, (int*)hist->Pages.Region - HistState.PageInts, key.Gain, xs, ys, i,
                count, tiles, out var stop);
            finite += next - i;
            i = next;
            if (stop == SplatStop.End) break;

            if (stop == SplatStop.Missing)
            {
                for (var lane = 0; lane < 8; lane++) HistPage(g, ld, hist, cls, tiles[lane]);
                continue;
            }

            for (var lane = 0; lane < 8; lane++)
                if (SplatOne(w, gi, g, ld, hist, cls, ref key, mask, xs[i + lane], ys[i + lane]))
                    finite++;
            i += 8;
        }

        return i;
    }

    [MethodImpl(Jit.Hot)]
    private static int SplatRun(
        GridCtx* g, int* directory, int* rebased, int gain, float* xs, float* ys, int from, int count, int* tiles,
        out SplatStop stop)
    {
        var originX = Vector256.Create(g->OriginX);
        var originY = Vector256.Create(g->OriginY);
        var scale = Vector256.Create((float)g->ScaleQ8);
        var outside = Vector256.Create(~(g->Size - 1));
        var tileMask = Vector256.Create(TileBake.TileSize - 1);
        var tileShift = Vector256.Create((uint)BitOperations.TrailingZeroCount(g->TilesPerSide));
        var zero = Vector256<int>.Zero;
        var offsets = stackalloc int[8];
        var pairs = (ulong*)offsets;
        var pending = zero;
        var settled = false;
        var i = from;
        stop = SplatStop.End;
        for (; i + 8 <= count; i += 8)
        {
            var px = (Vector256.Load(xs + i) - originX) * scale;
            var py = (Vector256.Load(ys + i) - originY) * scale;
            var cx = Vector256.ShiftRightArithmetic(Avx.ConvertToVector256Int32WithTruncation(px), 8);
            var cy = Vector256.ShiftRightArithmetic(Avx.ConvertToVector256Int32WithTruncation(py), 8);
            var rejected = (Vector256.Equals((cx | cy) & outside, zero).AsSingle().ExtractMostSignificantBits() ^ 0xFFu)
                           | (px | py).ExtractMostSignificantBits();
            var next = zero;
            var missing = false;
            if (rejected == 0)
            {
                var tile = Avx2.ShiftLeftLogicalVariable(Vector256.ShiftRightArithmetic(cy, TileBake.TileBits).AsUInt32(),
                    tileShift).AsInt32() + Vector256.ShiftRightArithmetic(cx, TileBake.TileBits);
                var slots = Avx2.GatherVector256(directory, tile, 4);
                missing = Vector256.Equals(slots, zero).AsSingle().ExtractMostSignificantBits() != 0;
                if (missing) tile.Store(tiles);
                next = Vector256.ShiftLeft(slots, HistState.PageShift) +
                       (Vector256.ShiftLeft(cy & tileMask, TileBake.TileBits) + (cx & tileMask));
            }

            if (settled)
            {
                pending.Store(offsets);
                var a = pairs[0];
                var b = pairs[1];
                var c = pairs[2];
                var d = pairs[3];
                rebased[(uint)a] += gain;
                rebased[a >> 32] += gain;
                rebased[(uint)b] += gain;
                rebased[b >> 32] += gain;
                rebased[(uint)c] += gain;
                rebased[c >> 32] += gain;
                rebased[(uint)d] += gain;
                rebased[d >> 32] += gain;
            }

            if (rejected != 0 || missing)
            {
                stop = rejected != 0 ? SplatStop.Rejected : SplatStop.Missing;
                return i;
            }

            pending = next;
            settled = true;
        }

        if (settled)
        {
            pending.Store(offsets);
            for (var lane = 0; lane < 8; lane++) rebased[offsets[lane]] += gain;
        }

        return i;
    }
#endif

    [MethodImpl(Jit.Hot)]
    private static void ReleaseClass(HistState* hist, HistClass* cls)
    {
        for (var t = 0; t < cls->Tiles.Length; t++)
        {
            var tile = cls->Tiles.Pointer[t];
            hist->Pages.Give(cls->Directory[tile] - 1);
            cls->Directory[tile] = 0;
        }

        cls->Tiles.Resize(0);
    }

    [MethodImpl(Jit.Hot)]
    private static void ResetHistogram(LayerData* ld)
    {
        var hist = ld->Hist;
        if (hist == null) return;

        for (var a = 0; a < hist->Active.Length; a++) ReleaseClass(hist, hist->Classes + hist->Active.Pointer[a]);

        hist->Active.Resize(0);
        hist->Total = 0;
    }

    [MethodImpl(Jit.Hot)]
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
        if (!TileBake.PlaceAnchored(anchor, anchor, g->ScaleQ8, g->Size, v, 0, UnitScale, precision, out var p))
            return false;
        if (p.Plane != Plane.Difference || !TileBake.Unclipped(v, &p)) return false;
        if (!KernelFactory.TryGetTaps(w, gi, stamp, 0, precision, &p, out var taps) || taps.Cells == null) return false;
        if (taps.Width <= 0 || taps.Height <= 0 || taps.Width > TileBake.TileSize ||
            taps.Height > TileBake.TileSize) return false;

        value = taps.Cells[0];
        if (value == 0) return false;

        for (var i = 1; i < taps.Width * taps.Height; i++)
            if (taps.Cells[i] != value)
                return false;

        ox = p.Px + taps.OriginX - anchorCell;
        oy = p.Py + taps.OriginY - anchorCell;
        width = taps.Width;
        height = taps.Height;
        return ox >= -TileBake.TileSize && ox + width <= TileBake.TileSize + 1 &&
               oy >= -TileBake.TileSize && oy + height <= TileBake.TileSize + 1;
    }

    [MethodImpl(Jit.Hot)]
    private static void ReplayClass(WorldCtx* w, int gi, GridCtx* g, HistState* hist, HistClass* cls, byte layer,
        byte stamp)
    {
        var key = new DepositRecord { Layer = layer, Stamp = stamp, Alive = 1, Scale = UnitScale };
        for (var t = 0; t < cls->Tiles.Length; t++)
        {
            var tile = cls->Tiles.Pointer[t];
            ReplayTile(w, gi, g, ref key, tile, (int*)hist->Pages.Unit(cls->Directory[tile] - 1));
        }
    }

    [MethodImpl(Jit.Hot)]
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

    [MethodImpl(Jit.Hot)]
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

    [MethodImpl(Jit.Hot)]
    private static int CountNonZero(int* page)
    {
        var count = 0;
        var i = 0;
#if NET
        if (Vector256.IsHardwareAccelerated)
            for (; i + 8 <= TileBake.TileSize * TileBake.TileSize; i += 8)
            {
                var zero = Vector256.Equals(Vector256.Load(page + i), Vector256<int>.Zero).ExtractMostSignificantBits();
                count += 8 - BitOperations.PopCount(zero);
            }
#endif
        for (; i < TileBake.TileSize * TileBake.TileSize; i++)
            if (page[i] != 0)
                count++;

        return count;
    }

    [MethodImpl(Jit.Hot)]
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

    [MethodImpl(Jit.Hot)]
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

    [MethodImpl(Jit.Hot)]
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
                else
                    CopyBlock(target + x, (int*)hist->Pages.Unit(slot) + rowAt + (ax & (TileBake.TileSize - 1)), rows,
                        span);
                x += span;
            }

            wy += rows;
        }
    }

    [MethodImpl(Jit.Inline)]
    private static void CopyBlock(int* destination, int* source, int rows, int span)
    {
        for (var r = 0; r < rows; r++)
        {
            var to = destination + r * HistState.WindowStride;
            var from = source + r * TileBake.TileSize;
#if NET
            if (Vector256.IsHardwareAccelerated)
            {
                for (var k = 0; k < span; k += 8) Vector256.Load(from + k).Store(to + k);
                continue;
            }
#endif
            for (var k = 0; k < span; k++) to[k] = from[k];
        }
    }

    [MethodImpl(Jit.Inline)]
    private static void ClearBlock(int* destination, int rows, int span)
    {
        for (var r = 0; r < rows; r++)
        {
            var to = destination + r * HistState.WindowStride;
#if NET
            if (Vector256.IsHardwareAccelerated)
            {
                for (var k = 0; k < span; k += 8) Vector256<int>.Zero.Store(to + k);
                continue;
            }
#endif
            for (var k = 0; k < span; k++) to[k] = 0;
        }
    }
}
