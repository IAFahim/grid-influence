using System.Runtime.CompilerServices;
#if NET
using System.Runtime.Intrinsics;
#endif

namespace Gi;

public static unsafe partial class World
{
    private const int BlockLanes = 64;
    private const int WindowStride = 40;
    private const int WindowRows = TileBake.TileSize + 2;
    private const int WindowCells = WindowRows * WindowStride;
    private const int WindowOrigin = WindowStride + 4;
    private const int WindowAlign = 32;
    private const int RunChanges = 3;
    private const int ScatterChanges = 16;

    private struct ColumnField
    {
        public float OriginX;
        public float OriginY;
        public float Scale;
        public float Unit;
        public int ScaleQ8;
        public int Size;
        public int TilesPerSide;
        public int TileShift;
        public int* Table;
        public byte* Pages;

        [MethodImpl(Jit.Inline)]
        public readonly short* PageAt(int tile)
        {
            var table = Table;
            if (table == null) return null;

            var index = table[tile];
            return index < 0 ? null : (short*)(Pages + ((nuint)index << PageShift));
        }

        [MethodImpl(Jit.Inline)]
        public readonly short CellAt(int cx, int cy)
        {
            if ((uint)cx >= (uint)Size || (uint)cy >= (uint)Size) return 0;

            var page = PageAt(((cy >> TileBake.TileBits) << TileShift) + (cx >> TileBake.TileBits));
            return page == null
                ? (short)0
                : page[((cy & (TileBake.TileSize - 1)) << TileBake.TileBits) + (cx & (TileBake.TileSize - 1))];
        }
    }

    private struct ColumnBlock
    {
        public int* Tiles;
        public int* Locals;
        public int* Windows;
        public ulong Accepted;
        public ulong Interior;
        public ulong Moved;
        public int Count;
        public int Changes;
    }

    [MethodImpl(Jit.Hot)]
    private static ColumnField FieldOf(GridCtx* g, LayerData* ld)
    {
        var shift = 0;
        while (1 << shift < g->TilesPerSide) shift++;
        return new ColumnField
        {
            OriginX = g->OriginX,
            OriginY = g->OriginY,
            Scale = g->ScaleQ8,
            Unit = g->ScaleQ8 / 512f,
            ScaleQ8 = g->ScaleQ8,
            Size = g->Size,
            TilesPerSide = g->TilesPerSide,
            TileShift = shift,
            Table = ld->PageIndex,
            Pages = ld->Store.Pages.Region
        };
    }

    [MethodImpl(Jit.Inline)]
    private static short* AlignedWindow(short* raw)
    {
        return (short*)(((nuint)raw + (WindowAlign - 1)) & ~(nuint)(WindowAlign - 1));
    }

    [MethodImpl(Jit.Inline)]
    private static ulong LaneMask(int count)
    {
        return count >= 64 ? ulong.MaxValue : (1ul << count) - 1;
    }

    [MethodImpl(Jit.Inline)]
    private static void OrBits(ulong* words, int at, ulong bits)
    {
        var shift = at & 63;
        var word = at >> 6;
        words[word] |= bits << shift;
        if (shift == 0) return;

        var high = bits >> (64 - shift);
        if (high != 0) words[word + 1] |= high;
    }

    [MethodImpl(Jit.Inline)]
    private static void CopyRow(short* destination, short* source)
    {
#if NET
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256.Load(source).Store(destination);
            Vector256.Load(source + 16).Store(destination + 16);
            return;
        }
#endif
        var to = (ulong*)destination;
        var from = (ulong*)source;
        to[0] = from[0];
        to[1] = from[1];
        to[2] = from[2];
        to[3] = from[3];
        to[4] = from[4];
        to[5] = from[5];
        to[6] = from[6];
        to[7] = from[7];
    }

    [MethodImpl(Jit.Inline)]
    private static void ClearRow(short* destination)
    {
        var to = (ulong*)destination;
        to[0] = 0;
        to[1] = 0;
        to[2] = 0;
        to[3] = 0;
        to[4] = 0;
        to[5] = 0;
        to[6] = 0;
        to[7] = 0;
    }

    [MethodImpl(Jit.Hot)]
    private static void BuildWindow(in ColumnField f, int tile, short* window)
    {
        var perSide = f.TilesPerSide;
        var tx = tile & (perSide - 1);
        var ty = tile >> f.TileShift;
        var centre = f.PageAt(tile);
        var left = tx > 0 ? f.PageAt(tile - 1) : null;
        var right = tx + 1 < perSide ? f.PageAt(tile + 1) : null;
        var above = ty > 0 ? f.PageAt(tile - perSide) : null;
        var below = ty + 1 < perSide ? f.PageAt(tile + perSide) : null;

        var top = window + 4;
        if (above != null) CopyRow(top, above + (TileBake.TileSize - 1) * TileBake.TileSize);
        else ClearRow(top);

        var bottom = window + (WindowRows - 1) * WindowStride + 4;
        if (below != null) CopyRow(bottom, below);
        else ClearRow(bottom);

        var row = window + WindowStride + 4;
        if (centre != null)
            for (var r = 0; r < TileBake.TileSize; r++)
                CopyRow(row + r * WindowStride, centre + r * TileBake.TileSize);
        else
            for (var r = 0; r < TileBake.TileSize; r++)
                ClearRow(row + r * WindowStride);

        if (left != null)
            for (var r = 0; r < TileBake.TileSize; r++)
                row[r * WindowStride - 1] = left[r * TileBake.TileSize + TileBake.TileSize - 1];
        else
            for (var r = 0; r < TileBake.TileSize; r++)
                row[r * WindowStride - 1] = 0;

        if (right != null)
            for (var r = 0; r < TileBake.TileSize; r++)
                row[r * WindowStride + TileBake.TileSize] = right[r * TileBake.TileSize];
        else
            for (var r = 0; r < TileBake.TileSize; r++)
                row[r * WindowStride + TileBake.TileSize] = 0;
    }
}
