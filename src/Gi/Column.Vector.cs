#if NET
using System.Numerics;
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
        var scale = Vector256.Create(f.Scale);
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
    private static void PrefetchBlock(in ColumnField f, ColumnBlock* b, bool vertical)
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

            var at = f.Pages + ((nuint)index << PageShift) + (nuint)locals[l] * sizeof(short);
            Sse.Prefetch0(at);
            if (!vertical) continue;

            Sse.Prefetch0(at - TileBake.TileSize * sizeof(short));
            Sse.Prefetch0(at + TileBake.TileSize * sizeof(short));
        }
    }
}
#endif
