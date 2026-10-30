using System.Runtime.CompilerServices;
#if NET
using System.Numerics;
using System.Runtime.Intrinsics.X86;
#endif

namespace Gi;

public static unsafe partial class World
{
    [MethodImpl(Jit.Inline)]
    private static int CountBits(ulong value)
    {
#if NET
        return BitOperations.PopCount(value);
#else
        var count = 0;
        for (; value != 0; value &= value - 1) count++;
        return count;
#endif
    }

    [MethodImpl(Jit.Hot)]
    private static void QuantizeBlock(in ColumnField f, float* xs, float* ys, ColumnBlock* b)
    {
        var i = 0;
        b->Accepted = 0;
        b->Interior = 0;
        b->Moved = 0;
#if NET
        if (Avx2.IsSupported) i = QuantizeLanes(f, xs, ys, b);
#endif
        var count = b->Count;
        var last = f.Size - 1;
        var accepted = b->Accepted;
        var interior = b->Interior;
        var moved = b->Moved;
        for (; i < count; i++)
        {
            var qx = CellQ8(xs[i], f.OriginX, f.ScaleQ8);
            var qy = CellQ8(ys[i], f.OriginY, f.ScaleQ8);
            if (qx < 0 || qy < 0 || qx >> 8 >= f.Size || qy >> 8 >= f.Size) continue;

            var cx = (int)(qx >> 8);
            var cy = (int)(qy >> 8);
            var lx = cx & (TileBake.TileSize - 1);
            var ly = cy & (TileBake.TileSize - 1);
            var tile = ((cy >> TileBake.TileBits) << f.TileShift) + (cx >> TileBake.TileBits);
            accepted |= 1ul << i;
            if (cx != 0 && cy != 0 && cx != last && cy != last) interior |= 1ul << i;
            if (i != 0 && tile != b->Tiles[i - 1]) moved |= 1ul << i;
            b->Tiles[i] = tile;
            b->Locals[i] = (ly << TileBake.TileBits) + lx;
            b->Windows[i] = (ly << TileBake.TileBits) + (ly << 3) + lx + WindowOrigin;
        }

        b->Accepted = accepted;
        b->Interior = interior;
        b->Changes = CountBits(moved);
    }

    [MethodImpl(Jit.Hot)]
    private static void PrepareBlock(in ColumnField f, float* xs, float* ys, ColumnBlock* b, int count, bool prefetch)
    {
        b->Count = count;
        QuantizeBlock(f, xs, ys, b);
#if NET
        if (prefetch) PrefetchBlock(f, b);
#endif
    }

    [MethodImpl(Jit.Hot)]
    private static void ArrangeBlocks(ColumnBlock* blocks, int* buffer)
    {
        for (var k = 0; k < 2; k++)
        {
            blocks[k].Tiles = buffer + k * 3 * BlockLanes;
            blocks[k].Locals = buffer + (k * 3 + 1) * BlockLanes;
            blocks[k].Windows = buffer + (k * 3 + 2) * BlockLanes;
        }
    }

    [MethodImpl(Jit.Hot)]
    private static bool GradientSingle(in ColumnField f, float* xs, float* ys, short* values, float* gradientXs,
        float* gradientYs, ulong* whole, int from, int to)
    {
        var buffer = stackalloc int[2 * 3 * BlockLanes];
        var blocks = stackalloc ColumnBlock[2];
        var raw = stackalloc short[WindowCells + WindowAlign / sizeof(short)];
        var window = AlignedWindow(raw);
        ArrangeBlocks(blocks, buffer);
        var current = blocks;
        var spare = blocks + 1;
        var complete = true;
        var windowTile = -1;
        var at = from;
        PrepareBlock(f, xs + at, ys + at, current, Math.Min(BlockLanes, to - at), false);
        while (true)
        {
            var next = at + current->Count;
            if (next < to) PrepareBlock(f, xs + next, ys + next, spare, Math.Min(BlockLanes, to - next), false);

            if (current->Changes <= RunChanges)
                GradientWindow(f, current, at, values, gradientXs, gradientYs, window, ref windowTile);
            else
                GradientDirect(f, current, at, values, gradientXs, gradientYs);

            if (whole != null) OrBits(whole, at, current->Interior);
            if (current->Interior != LaneMask(current->Count)) complete = false;
            if (next >= to) return complete;

            var swap = current;
            current = spare;
            spare = swap;
            at = next;
        }
    }

    [MethodImpl(Jit.Hot)]
    private static void GradientWindow(in ColumnField f, ColumnBlock* b, int at, short* values, float* gradientXs,
        float* gradientYs, short* window, ref int windowTile)
    {
        var count = b->Count;
        var accepted = b->Accepted;
        var tiles = b->Tiles;
        var windows = b->Windows;
        var unit = f.Unit;
        var shown = windowTile;
        for (var l = 0; l < count; l++)
        {
#if NET
            if ((l & 7) == 0 && l + 8 <= count)
            {
                if (GradientWindowGroup(f, b, at, l, values, gradientXs, gradientYs, window, ref shown))
                {
                    l += 7;
                    continue;
                }

                if (GradientGatherLanes(f, b, at, l, values, gradientXs, gradientYs))
                {
                    l += 7;
                    continue;
                }
            }
#endif
            var o = at + l;
            if (((accepted >> l) & 1) == 0)
            {
                if (values != null) values[o] = 0;
                gradientXs[o] = 0f;
                gradientYs[o] = 0f;
                continue;
            }

            var tile = tiles[l];
            if (tile != shown)
            {
                BuildWindow(f, tile, window);
                shown = tile;
            }

            var p = window + windows[l];
            if (values != null) values[o] = *p;
            gradientXs[o] = (p[1] - p[-1]) * unit;
            gradientYs[o] = (p[WindowStride] - p[-WindowStride]) * unit;
        }

        windowTile = shown;
    }

    [MethodImpl(Jit.Hot)]
    private static void GradientDirect(in ColumnField f, ColumnBlock* b, int at, short* values, float* gradientXs,
        float* gradientYs)
    {
        var count = b->Count;
        var accepted = b->Accepted;
        var tiles = b->Tiles;
        var locals = b->Locals;
        var unit = f.Unit;
        var cached = -1;
        short* page = null;
        var l = 0;
        while (l < count)
        {
#if NET
            if (l + 8 <= count && GradientGatherLanes(f, b, at, l, values, gradientXs, gradientYs))
            {
                l += 8;
                continue;
            }
#endif
            var end = Math.Min(l + 8, count);
            for (; l < end; l++)
            {
                var o = at + l;
            if (((accepted >> l) & 1) == 0)
            {
                if (values != null) values[o] = 0;
                gradientXs[o] = 0f;
                gradientYs[o] = 0f;
                continue;
            }

            var tile = tiles[l];
            if (tile != cached)
            {
                cached = tile;
                page = f.PageAt(tile);
            }

            var local = locals[l];
            var lx = local & (TileBake.TileSize - 1);
            var ly = local >> TileBake.TileBits;
            short centre;
            float dx;
            float dy;
            if ((uint)(lx - 1) < TileBake.TileSize - 2 & (uint)(ly - 1) < TileBake.TileSize - 2)
            {
                if (page == null)
                {
                    centre = 0;
                    dx = 0f;
                    dy = 0f;
                }
                else
                {
                    var cell = page + local;
                    centre = *cell;
                    dx = (cell[1] - cell[-1]) * unit;
                    dy = (cell[TileBake.TileSize] - cell[-TileBake.TileSize]) * unit;
                }
            }
            else
            {
                var cx = ((tile & (f.TilesPerSide - 1)) << TileBake.TileBits) + lx;
                var cy = ((tile >> f.TileShift) << TileBake.TileBits) + ly;
                centre = page == null ? (short)0 : page[local];
                dx = (f.CellAt(cx + 1, cy) - f.CellAt(cx - 1, cy)) * unit;
                dy = (f.CellAt(cx, cy + 1) - f.CellAt(cx, cy - 1)) * unit;
            }

            if (values != null) values[o] = centre;
            gradientXs[o] = dx;
            gradientYs[o] = dy;
            }
        }
    }

    [MethodImpl(Jit.Hot)]
    private static int SenseSingle(in ColumnField f, float* xs, float* ys, short* values, ulong* whole, int from,
        int to)
    {
        var buffer = stackalloc int[2 * 3 * BlockLanes];
        var blocks = stackalloc ColumnBlock[2];
        ArrangeBlocks(blocks, buffer);
        var current = blocks;
        var spare = blocks + 1;
        var gaps = 0;
        var at = from;
        PrepareBlock(f, xs + at, ys + at, current, Math.Min(BlockLanes, to - at), true);
        while (true)
        {
            var next = at + current->Count;
            if (next < to) PrepareBlock(f, xs + next, ys + next, spare, Math.Min(BlockLanes, to - next), true);

            SenseLanes(f, current, at, values);
            if (whole != null) OrBits(whole, at, current->Accepted);
            gaps += current->Count - CountBits(current->Accepted);
            if (next >= to) return gaps;

            var swap = current;
            current = spare;
            spare = swap;
            at = next;
        }
    }

    [MethodImpl(Jit.Hot)]
    private static void SenseLanes(in ColumnField f, ColumnBlock* b, int at, short* values)
    {
        var count = b->Count;
        var accepted = b->Accepted;
        var tiles = b->Tiles;
        var locals = b->Locals;
        var cached = -1;
        short* page = null;
        for (var l = 0; l < count; l++)
        {
#if NET
            if ((l & 7) == 0 && l + 8 <= count && SenseGatherLanes(f, b, at, l, values))
            {
                l += 7;
                continue;
            }
#endif
            var o = at + l;
            if (((accepted >> l) & 1) == 0)
            {
                values[o] = 0;
                continue;
            }

            var tile = tiles[l];
            if (tile != cached)
            {
                cached = tile;
                page = f.PageAt(tile);
            }

            values[o] = page == null ? (short)0 : page[locals[l]];
        }
    }
}
