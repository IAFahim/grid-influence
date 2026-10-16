#if NET
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Gi;

public static unsafe partial class World
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int GradientBlocks(
        GridCtx* g, LayerData* ld, float* xs, float* ys, short* values, float* gradientXs, float* gradientYs,
        ulong* whole, int from, int to, ref bool complete)
    {
        var originX = Vector256.Create(g->OriginX);
        var originY = Vector256.Create(g->OriginY);
        var scale = Vector256.Create((float)g->ScaleQ8);
        var limit = Vector256.Create(g->Size - 1);
        var inner = Vector256.Create(g->Size - 2);
        var tilesPerSide = Vector256.Create(g->TilesPerSide);
        var tileMask = Vector256.Create(TileBake.TileSize - 1);
        var unit = Vector256.Create(g->ScaleQ8 / 512f);
        var one = Vector256.Create(1);
        var row = Vector256.Create(TileBake.TileSize);
        var minusRow = Vector256.Create(-TileBake.TileSize);
        var page = Vector256.Create(TileBake.TileSize * TileBake.TileSize);
        var minusPage = Vector256.Create(-TileBake.TileSize * TileBake.TileSize);
        var minusOne = Vector256.Create(-1);
        var edge = Vector256.Create(TileBake.TileSize - 1);
        var scalarUnit = g->ScaleQ8 / 512f;
        var size = g->Size;
        var cells = stackalloc int[8];
        var rows = stackalloc int[8];
        var tiles = stackalloc int[8];
        var allComplete = complete;
        var directory = ld->Directory;
        var sets = (int*)ld->Sets.Region;
        var pages = (int*)ld->Store.Pages.Region;
        var stride = Vector256.Create(sizeof(TilePlanes) / sizeof(int));
        var wide = directory != null && sets != null && pages != null && ld->Store.Pages.ByteCapacity <= int.MaxValue;
        var cachedTile = -1;
        short* cachedPage = null;
        var i = from;
        for (; i + 8 <= to; i += 8)
        {
            var cx = Vector256.ShiftRightArithmetic(
                Avx.ConvertToVector256Int32WithTruncation(Vector256.Floor((Vector256.Load(xs + i) - originX) * scale)), 8);
            var cy = Vector256.ShiftRightArithmetic(
                Avx.ConvertToVector256Int32WithTruncation(Vector256.Floor((Vector256.Load(ys + i) - originY) * scale)), 8);
            var inside = Vector256.Equals(Vector256.Min(Vector256.Max(cx, Vector256<int>.Zero), limit), cx)
                & Vector256.Equals(Vector256.Min(Vector256.Max(cy, Vector256<int>.Zero), limit), cy);
            var accepted = inside.AsSingle().ExtractMostSignificantBits();
            var tile = Vector256.ShiftRightArithmetic(cy, TileBake.TileBits) * tilesPerSide
                + Vector256.ShiftRightArithmetic(cx, TileBake.TileBits);
            if (accepted == 0xFF && wide)
            {
                var lx = cx & tileMask;
                var ly = cy & tileMask;
                var local = Vector256.ShiftLeft(ly, TileBake.TileBits) + lx;
                var zero = Vector256<int>.Zero;
                Vector256<int> present;
                Vector256<int> baseBytes;
                var first = tile.GetElement(0);
                if (Vector256.Equals(tile, Vector256.Create(first)).AsSingle().ExtractMostSignificantBits() == 0xFF)
                {
                    var setIndex = directory[first];
                    present = Vector256.Create(setIndex >> 31 ^ -1);
                    baseBytes = Vector256.Create(setIndex < 0 ? 0 : sets[setIndex * (sizeof(TilePlanes) / sizeof(int))] << 11);
                }
                else
                {
                    baseBytes = PageBases(directory, sets, stride, tile, out present);
                }

                var center = GatherShort(pages, baseBytes + Vector256.ShiftLeft(local, 1)) & present;
                var right = GatherTap(directory, sets, pages, stride, tile, Vector256.Equals(lx, edge),
                    Vector256.GreaterThan(cx, inner), one, local + one + (Vector256.Equals(lx, edge) & minusRow), baseBytes, present);
                var left = GatherTap(directory, sets, pages, stride, tile, Vector256.Equals(lx, zero),
                    Vector256.LessThan(cx, one), minusOne, local - one + (Vector256.Equals(lx, zero) & row), baseBytes, present);
                var below = GatherTap(directory, sets, pages, stride, tile, Vector256.Equals(ly, edge),
                    Vector256.GreaterThan(cy, inner), tilesPerSide, local + row + (Vector256.Equals(ly, edge) & minusPage), baseBytes, present);
                var above = GatherTap(directory, sets, pages, stride, tile, Vector256.Equals(ly, zero),
                    Vector256.LessThan(cy, one), -tilesPerSide, local - row + (Vector256.Equals(ly, zero) & page), baseBytes, present);
                if (values != null) Vector256.Narrow(center, center).GetLower().Store(values + i);
                (Vector256.ConvertToSingle(right - left) * unit).Store(gradientXs + i);
                (Vector256.ConvertToSingle(below - above) * unit).Store(gradientYs + i);

                var interior = Vector256.GreaterThanOrEqual(cx, one) & Vector256.LessThanOrEqual(cx, inner)
                    & Vector256.GreaterThanOrEqual(cy, one) & Vector256.LessThanOrEqual(cy, inner);
                var interiorBits = interior.AsSingle().ExtractMostSignificantBits();
                if (whole != null) OrBits(whole, i, interiorBits);
                if (interiorBits != 0xFF) allComplete = false;
                continue;
            }

            cx.Store(cells);
            cy.Store(rows);
            tile.Store(tiles);
            for (var lane = 0; lane < 8; lane++)
            {
                var at = i + lane;
                if ((accepted >> lane & 1) == 0)
                {
                    if (values != null) values[at] = 0;
                    gradientXs[at] = 0f;
                    gradientYs[at] = 0f;
                    allComplete = false;
                    continue;
                }

                var x = cells[lane];
                var y = rows[lane];
                var laneTile = tiles[lane];
                if (laneTile != cachedTile)
                {
                    cachedTile = laneTile;
                    cachedPage = ld->PageOf(laneTile);
                }

                var lx = x & (TileBake.TileSize - 1);
                var ly = y & (TileBake.TileSize - 1);
                if (cachedPage != null && (uint)(lx - 1) < TileBake.TileSize - 2 && (uint)(ly - 1) < TileBake.TileSize - 2)
                {
                    var cell = cachedPage + ly * TileBake.TileSize + lx;
                    if (values != null) values[at] = *cell;
                    gradientXs[at] = (cell[1] - cell[-1]) * scalarUnit;
                    gradientYs[at] = (cell[TileBake.TileSize] - cell[-TileBake.TileSize]) * scalarUnit;
                }
                else
                {
                    if (values != null) values[at] = cachedPage == null ? (short)0 : cachedPage[ly * TileBake.TileSize + lx];
                    gradientXs[at] = (CellNear(g, ld, laneTile, cachedPage, x + 1, y) - CellNear(g, ld, laneTile, cachedPage, x - 1, y)) * scalarUnit;
                    gradientYs[at] = (CellNear(g, ld, laneTile, cachedPage, x, y + 1) - CellNear(g, ld, laneTile, cachedPage, x, y - 1)) * scalarUnit;
                }

                if (x - 1 >= 0 && y - 1 >= 0 && x + 1 < size && y + 1 < size)
                {
                    if (whole != null) whole[at >> 6] |= 1ul << (at & 63);
                }
                else
                {
                    allComplete = false;
                }
            }
        }

        complete = allComplete;
        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> PageBases(
        int* directory, int* sets, Vector256<int> stride, Vector256<int> tile, out Vector256<int> present)
    {
        var setIndex = Avx2.GatherVector256(directory, tile, 4);
        present = Vector256.GreaterThanOrEqual(setIndex, Vector256<int>.Zero);
        var pageIndex = Avx2.GatherVector256(sets, Vector256.Max(setIndex, Vector256<int>.Zero) * stride, 4);
        return Vector256.ShiftLeft(Vector256.ConditionalSelect(present, pageIndex, Vector256<int>.Zero), 11);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> GatherTap(
        int* directory, int* sets, int* pages, Vector256<int> stride, Vector256<int> tile, Vector256<int> cross,
        Vector256<int> invalid, Vector256<int> tileStep, Vector256<int> local, Vector256<int> baseBytes,
        Vector256<int> present)
    {
        if (cross.AsSingle().ExtractMostSignificantBits() != 0)
        {
            baseBytes = PageBases(directory, sets, stride, tile + (Vector256.AndNot(cross, invalid) & tileStep), out present);
            present = Vector256.AndNot(present, invalid);
        }

        return GatherShort(pages, baseBytes + Vector256.ShiftLeft(local, 1)) & present;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> GatherShort(int* pages, Vector256<int> byteOffset)
    {
        var raw = Avx2.GatherVector256(pages, byteOffset, 1);
        return Vector256.ShiftRightArithmetic(Vector256.ShiftLeft(raw, 16), 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void OrBits(ulong* words, int at, uint bits)
    {
        var shift = at & 63;
        words[at >> 6] |= (ulong)bits << shift;
        if (shift > 56) words[(at >> 6) + 1] |= (ulong)bits >> (64 - shift);
    }
}
#endif
