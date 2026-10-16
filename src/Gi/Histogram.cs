using System.Runtime.CompilerServices;

namespace Gi;

internal unsafe struct HistClass
{
    public int* Directory;
    public NativeBuffer<int> Tiles;
}

internal unsafe struct HistState
{
    public const int PageBytes = TileBake.TileSize * TileBake.TileSize * sizeof(int);
    public const int WindowSide = 2 * TileBake.TileSize - 1;

    public HistClass* Classes;
    public FixedSlab Pages;
    public NativeBuffer<int> Active;
    public NativeBuffer<int> OutTiles;
    public int* OutMark;
    public int MarkStamp;
    public int* Window;
    public int* Strip;

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
        hist->Window = (int*)NativeHeap.AllocZeroed((nuint)(HistState.WindowSide * HistState.WindowSide * sizeof(int)));
        hist->Strip = (int*)NativeHeap.AllocZeroed((nuint)(HistState.WindowSide * TileBake.TileSize * sizeof(int)));
        ld->Hist = hist;
        return hist;
    }

    private static void HistAdd(GridCtx* g, LayerData* ld, byte stamp, int cx, int cy, int gain)
    {
        var hist = EnsureHist(ld);
        var cls = hist->Classes + stamp;
        if (cls->Directory == null) cls->Directory = (int*)NativeHeap.AllocZeroed((nuint)g->TileCount * sizeof(int));

        var tile = (cy >> TileBake.TileBits) * g->TilesPerSide + (cx >> TileBake.TileBits);
        HistPage(g, ld, hist, cls, tile)[(cy & (TileBake.TileSize - 1)) * TileBake.TileSize + (cx & (TileBake.TileSize - 1))] += gain;
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
                BoxFilterClass(g, ld, hist, cls, value, ox, oy, width, height);
            else
                ReplayClass(w, gi, g, hist, cls, layer, stamp);

            for (var t = 0; t < cls->Tiles.Length; t++)
            {
                var tile = cls->Tiles.Pointer[t];
                hist->Pages.Give(cls->Directory[tile] - 1);
                cls->Directory[tile] = 0;
            }

            cls->Tiles.Resize(0);
        }

        hist->Active.Resize(0);
    }

    private static void ResetHistogram(GridCtx* g, LayerData* ld)
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
        var tps = g->TilesPerSide;
        for (var t = 0; t < cls->Tiles.Length; t++)
        {
            var tile = cls->Tiles.Pointer[t];
            var page = (int*)hist->Pages.Unit(cls->Directory[tile] - 1);
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

            var window = hist->Window;
            var strip = hist->Strip;
            for (var wy = 0; wy < windowHeight; wy++)
            {
                var row = window + wy * HistState.WindowSide;
                var sum = 0;
                for (var i = 0; i < width; i++) sum += row[i];
                var outRow = strip + wy * TileBake.TileSize;
                outRow[0] = sum;
                for (var x = 1; x < TileBake.TileSize; x++)
                {
                    sum += row[x + width - 1] - row[x - 1];
                    outRow[x] = sum;
                }
            }

            int* plane = null;
            for (var y = 0; y < TileBake.TileSize; y++)
            {
                for (var x = 0; x < TileBake.TileSize; x++)
                {
                    var sum = 0;
                    for (var j = 0; j < height; j++) sum += strip[(y + j) * TileBake.TileSize + x];
                    if (sum == 0) continue;

                    if (plane == null)
                    {
                        var set = EnsureSet(g, ld, outTile);
                        plane = ld->Store.Cells(*set).I;
                        var dirty = DirtyIndex(g, ld, outTile);
                        ld->Rects.Pointer[dirty] = new TileRect { X0 = 0, Y0 = 0, X1 = TileBake.TileSize, Y1 = TileBake.TileSize };
                    }

                    plane[y * TileBake.TileSize + x] += sum * value;
                }
            }
        }
    }

    private static void GatherWindow(
        HistState* hist, HistClass* cls, int tps, int size, int x0, int y0, int windowWidth, int windowHeight)
    {
        var window = hist->Window;
        for (var wy = 0; wy < windowHeight; wy++)
        {
            var row = window + wy * HistState.WindowSide;
            var ay = y0 + wy;
            if ((uint)ay >= (uint)size)
            {
                new Span<int>(row, windowWidth).Clear();
                continue;
            }

            var x = 0;
            while (x < windowWidth)
            {
                var ax = x0 + x;
                if ((uint)ax >= (uint)size)
                {
                    row[x++] = 0;
                    continue;
                }

                var tileColumn = ax >> TileBake.TileBits;
                var span = Math.Min(windowWidth - x, ((tileColumn + 1) << TileBake.TileBits) - ax);
                var slot = cls->Directory[(ay >> TileBake.TileBits) * tps + tileColumn] - 1;
                if (slot < 0)
                {
                    new Span<int>(row + x, span).Clear();
                }
                else
                {
                    var page = (int*)hist->Pages.Unit(slot);
                    Buffer.MemoryCopy(
                        page + (ay & (TileBake.TileSize - 1)) * TileBake.TileSize + (ax & (TileBake.TileSize - 1)),
                        row + x, span * sizeof(int), span * sizeof(int));
                }

                x += span;
            }
        }
    }
}
