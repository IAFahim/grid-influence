#if NET
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Gi;

public static unsafe partial class World
{
    [MethodImpl(Jit.Hot)]
    private static int QuantizeLanes(in ColumnField f, float* xs, float* ys, ColumnBlock* b)
    {
        var originX = Vector256.Create(f.OriginX);
        var originY = Vector256.Create(f.OriginY);
        var scale = Vector256.Create(f.ScaleF);
        var outside = Vector256.Create(~(f.Size - 1));
        var last = Vector256.Create(f.Size - 1);
        var tileMask = Vector256.Create(TileBake.TileSize - 1);
        var origin = Vector256.Create(WindowOrigin);
        var rotate = Vector256.Create(7, 0, 1, 2, 3, 4, 5, 6);
        var shift = Vector128.CreateScalar(f.TileShift);
        var zero = Vector256<int>.Zero;
        var tiles = b->Tiles;
        var locals = b->Locals;
        var windows = b->Windows;
        var count = b->Count;
        ulong accepted = 0;
        ulong interior = 0;
        ulong moved = 0;
        var previous = zero;
        var i = 0;
        for (; i + 8 <= count; i += 8)
        {
            var cx = Vector256.ShiftRightArithmetic(
                Avx.ConvertToVector256Int32WithTruncation(Vector256.Floor((Vector256.Load(xs + i) - originX) * scale)),
                8);
            var cy = Vector256.ShiftRightArithmetic(
                Avx.ConvertToVector256Int32WithTruncation(Vector256.Floor((Vector256.Load(ys + i) - originY) * scale)),
                8);
            var inside = Vector256.Equals((cx | cy) & outside, zero);
            var border = Vector256.Equals(cx, zero) | Vector256.Equals(cx, last) | Vector256.Equals(cy, zero) |
                         Vector256.Equals(cy, last);
            var lx = cx & tileMask;
            var ly = cy & tileMask;
            var local = Vector256.ShiftLeft(ly, TileBake.TileBits) + lx;
            var tile = Avx2.ShiftLeftLogical(Vector256.ShiftRightArithmetic(cy, TileBake.TileBits), shift) +
                       Vector256.ShiftRightArithmetic(cx, TileBake.TileBits);
            var rotated = Avx2.PermuteVar8x32(tile, rotate);
            var shifted = Avx2.Blend(rotated, i == 0 ? tile : previous, 1);
            previous = rotated;
            accepted |= (ulong)inside.AsSingle().ExtractMostSignificantBits() << i;
            interior |= (ulong)Vector256.AndNot(inside, border).AsSingle().ExtractMostSignificantBits() << i;
            moved |= (ulong)(~Vector256.Equals(tile, shifted).AsSingle().ExtractMostSignificantBits() & 0xFFu) << i;
            tile.Store(tiles + i);
            local.Store(locals + i);
            (local + Vector256.ShiftLeft(ly, 3) + origin).Store(windows + i);
        }

        b->Accepted = accepted;
        b->Interior = interior;
        b->Moved = moved;
        return i;
    }

    [MethodImpl(Jit.Hot)]
    private static bool GradientWindowGroup(
        in ColumnField f, ColumnBlock* b, int at, int l, short* values, float* gradientXs, float* gradientYs,
        short* window, ref int shown)
    {
        if (!Avx2.IsSupported) return false;
        if (((b->Accepted >> l) & 0xFFul) != 0xFFul) return false;

        var tiles = b->Tiles;
        var tile = tiles[l];
        if (Vector256.Equals(Vector256.Load(tiles + l), Vector256.Create(tile)).AsSingle()
                .ExtractMostSignificantBits() != 0xFF) return false;

        if (tile != shown)
        {
            BuildWindow(f, tile, window);
            shown = tile;
        }

        var bytes = Vector256.ShiftLeft(Vector256.Load(b->Windows + l), 1);
        var pair = Vector256.Create(2);
        var rows = Vector256.Create(WindowStride << 1);
        var centre = WindowTap(window, bytes);
        var right = WindowTap(window, bytes + pair);
        var left = WindowTap(window, bytes - pair);
        var below = WindowTap(window, bytes + rows);
        var above = WindowTap(window, bytes - rows);
        if (values != null) Vector256.Narrow(centre, centre).GetLower().Store(values + at + l);
        var unit = Vector256.Create(f.Unit);
        (Vector256.ConvertToSingle(right - left) * unit).Store(gradientXs + at + l);
        (Vector256.ConvertToSingle(below - above) * unit).Store(gradientYs + at + l);
        return true;
    }

    [MethodImpl(Jit.Inline)]
    private static Vector256<int> WindowTap(short* window, Vector256<int> byteOffsets)
    {
        var raw = Avx2.GatherVector256((int*)window, byteOffsets, 1);
        return Vector256.ShiftRightArithmetic(Vector256.ShiftLeft(raw, 16), 16);
    }

    [MethodImpl(Jit.Hot)]
    private static bool GradientGatherLanes(
        in ColumnField f, ColumnBlock* b, int at, int l, short* values, float* gradientXs, float* gradientYs)
    {
        if (!Avx2.IsSupported || f.Wide == 0 || f.Table == null) return false;
        if (((b->Accepted >> l) & 0xFFul) != 0xFFul) return false;

        var tiles = Vector256.Load(b->Tiles + l);
        var local = Vector256.Load(b->Locals + l);
        var tileMask = Vector256.Create(TileBake.TileSize - 1);
        var one = Vector256.Create(1);
        var minusOne = Vector256.Create(-1);
        var zero = Vector256<int>.Zero;
        var edge = tileMask;
        var row = Vector256.Create(TileBake.TileSize);
        var lx = Vector256.BitwiseAnd(local, tileMask);
        var ly = Vector256.ShiftRightArithmetic(local, TileBake.TileBits);
        var tx = Vector256.BitwiseAnd(tiles, Vector256.Create(f.TilesPerSide - 1));
        var ty = Vector256.ShiftRightArithmetic(tiles, f.TileShift);
        var cx = Vector256.ShiftLeft(tx, TileBake.TileBits) + lx;
        var cy = Vector256.ShiftLeft(ty, TileBake.TileBits) + ly;
        var last = Vector256.Create(f.Size - 1);
        var baseBytes = PageTapBases(f, tiles, out var present);
        var centre = PageTap(f, baseBytes, Vector256.ShiftLeft(local, 1), present);

        var rightCross = Vector256.Equals(lx, edge);
        var rightInvalid = Vector256.Equals(cx, last);
        var rightBase = baseBytes;
        var rightPresent = present;
        if (rightCross.AsSingle().ExtractMostSignificantBits() != 0)
        {
            rightBase = PageTapBases(f, tiles + (Vector256.AndNot(rightCross, rightInvalid) & one),
                out rightPresent);
        }

        var right = PageTap(f, rightBase,
            Vector256.ShiftLeft(local + one + (rightCross & Vector256.Create(-TileBake.TileSize)), 1),
            Vector256.AndNot(rightPresent, rightInvalid));

        var leftCross = Vector256.Equals(lx, zero);
        var leftInvalid = Vector256.Equals(cx, zero);
        var leftBase = baseBytes;
        var leftPresent = present;
        if (leftCross.AsSingle().ExtractMostSignificantBits() != 0)
        {
            leftBase = PageTapBases(f, tiles + (Vector256.AndNot(leftCross, leftInvalid) & minusOne),
                out leftPresent);
        }

        var left = PageTap(f, leftBase,
            Vector256.ShiftLeft(local - one + (leftCross & row), 1),
            Vector256.AndNot(leftPresent, leftInvalid));

        var belowCross = Vector256.Equals(ly, edge);
        var belowInvalid = Vector256.Equals(cy, last);
        var belowBase = baseBytes;
        var belowPresent = present;
        if (belowCross.AsSingle().ExtractMostSignificantBits() != 0)
        {
            belowBase = PageTapBases(f, tiles + (Vector256.AndNot(belowCross, belowInvalid) & Vector256.Create(f.TilesPerSide)),
                out belowPresent);
        }

        var below = PageTap(f, belowBase,
            Vector256.ShiftLeft(local + row + (belowCross & Vector256.Create(-Cells)), 1),
            Vector256.AndNot(belowPresent, belowInvalid));

        var aboveCross = Vector256.Equals(ly, zero);
        var aboveInvalid = Vector256.Equals(cy, zero);
        var aboveBase = baseBytes;
        var abovePresent = present;
        if (aboveCross.AsSingle().ExtractMostSignificantBits() != 0)
        {
            aboveBase = PageTapBases(f, tiles - (Vector256.AndNot(aboveCross, aboveInvalid) & Vector256.Create(f.TilesPerSide)),
                out abovePresent);
        }

        var above = PageTap(f, aboveBase,
            Vector256.ShiftLeft(local - row + (aboveCross & Vector256.Create(Cells)), 1),
            Vector256.AndNot(abovePresent, aboveInvalid));

        if (values != null) Vector256.Narrow(centre, centre).GetLower().Store(values + at + l);
        var unit = Vector256.Create(f.Unit);
        (Vector256.ConvertToSingle(right - left) * unit).Store(gradientXs + at + l);
        (Vector256.ConvertToSingle(below - above) * unit).Store(gradientYs + at + l);
        return true;
    }

    [MethodImpl(Jit.Hot)]
    private static bool SenseGatherLanes(in ColumnField f, ColumnBlock* b, int at, int l, short* values)
    {
        if (!Avx2.IsSupported || f.Wide == 0 || f.Table == null) return false;
        if (((b->Accepted >> l) & 0xFFul) != 0xFFul) return false;

        var tiles = Vector256.Load(b->Tiles + l);
        var offsets = Vector256.ShiftLeft(Vector256.Load(b->Locals + l), 1);
        var tile = tiles.GetElement(0);
        Vector256<int> centre;
        if (Vector256.Equals(tiles, Vector256.Create(tile)).AsSingle().ExtractMostSignificantBits() == 0xFF)
        {
            var page = f.PageAt(tile);
            if (page == null)
            {
                Vector256.Narrow(Vector256<int>.Zero, Vector256<int>.Zero).GetLower().Store(values + at + l);
                return true;
            }

            var raw = Avx2.GatherVector256((int*)page, offsets, 1);
            centre = Vector256.ShiftRightArithmetic(Vector256.ShiftLeft(raw, 16), 16);
        }
        else
        {
            centre = PageTap(f, PageTapBases(f, tiles, out var present), offsets, present);
        }

        Vector256.Narrow(centre, centre).GetLower().Store(values + at + l);
        return true;
    }

    [MethodImpl(Jit.Inline)]
    private static Vector256<int> PageTapBases(in ColumnField f, Vector256<int> tile, out Vector256<int> present)
    {
        var index = Avx2.GatherVector256(f.Table, tile, 4);
        present = Vector256.GreaterThanOrEqual(index, Vector256<int>.Zero);
        return Vector256.ShiftLeft(Vector256.ConditionalSelect(present, index, Vector256<int>.Zero), PageShift);
    }

    [MethodImpl(Jit.Inline)]
    private static Vector256<int> PageTap(in ColumnField f, Vector256<int> baseBytes, Vector256<int> byteOffsets,
        Vector256<int> present)
    {
        var raw = Avx2.GatherVector256((int*)f.Pages, baseBytes + byteOffsets, 1);
        return Vector256.BitwiseAnd(Vector256.ShiftRightArithmetic(Vector256.ShiftLeft(raw, 16), 16), present);
    }

    [MethodImpl(Jit.Hot)]
    private static void PrefetchBlock(in ColumnField f, ColumnBlock* b)
    {
        if (!Sse.IsSupported || b->Changes < ScatterChanges || f.Table == null) return;

        var tiles = b->Tiles;
        var locals = b->Locals;
        var accepted = b->Accepted;
        var count = b->Count;
        for (var l = 0; l < count; l++)
        {
            if (((accepted >> l) & 1) == 0) continue;

            var index = f.Table[tiles[l]];
            if (index < 0) continue;

            Sse.Prefetch0(f.Pages + ((nuint)index << PageShift) + (nuint)locals[l] * sizeof(short));
        }
    }
}
#endif
