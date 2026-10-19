#if NET
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif

namespace Gi;

public static unsafe partial class World
{
    internal static int SpatialKeys(byte world, byte grid, ReadOnlySpan<float> xs, ReadOnlySpan<float> ys,
        Span<uint> keys)
    {
        var count = Math.Min(Math.Min(xs.Length, ys.Length), keys.Length);
        var w = GetContext(world);
        if (w == null || grid >= w->GridCount || w->Grids[grid].ScaleQ8 == 0)
        {
            keys[..count].Fill(SpatialOrder.Outside);
            return count;
        }

        var g = w->Grids + grid;
        var done = 0;
#if NET
        if (Avx2.IsSupported) done = SpatialKeysBlocks(g, xs, ys, keys, count);
#endif
        for (var i = done; i < count; i++)
        {
            var x = xs[i];
            var y = ys[i];
            if (!float.IsFinite(x) || !float.IsFinite(y))
            {
                keys[i] = SpatialOrder.Outside;
                continue;
            }

            var qx = CellQ8(x, g->OriginX, g->ScaleQ8);
            var qy = CellQ8(y, g->OriginY, g->ScaleQ8);
            keys[i] = Contains(g, qx, qy) ? SpatialOrder.Key((int)(qx >> 8), (int)(qy >> 8)) : SpatialOrder.Outside;
        }

        return count;
    }

#if NET
    private static int SpatialKeysBlocks(GridCtx* g, ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, Span<uint> keys,
        int count)
    {
        var originX = Vector256.Create(g->OriginX);
        var originY = Vector256.Create(g->OriginY);
        var scale = Vector256.Create((float)g->ScaleQ8);
        var limit = Vector256.Create(g->Size - 1);
        var infinity = Vector256.Create(float.PositiveInfinity);
        var outside = Vector256.Create(SpatialOrder.Outside);
        var tileMask = Vector256.Create(TileBake.TileSize - 1);
        var i = 0;
        fixed (float* px = xs, py = ys)
        fixed (uint* pk = keys)
        {
            for (; i + 8 <= count; i += 8)
            {
                var x = Vector256.Load(px + i);
                var y = Vector256.Load(py + i);
                var cx = Vector256.ShiftRightArithmetic(
                    Avx.ConvertToVector256Int32WithTruncation(Vector256.Floor((x - originX) * scale)), 8);
                var cy = Vector256.ShiftRightArithmetic(
                    Avx.ConvertToVector256Int32WithTruncation(Vector256.Floor((y - originY) * scale)), 8);
                var inside = Vector256.Equals(Vector256.Min(Vector256.Max(cx, Vector256<int>.Zero), limit), cx)
                             & Vector256.Equals(Vector256.Min(Vector256.Max(cy, Vector256<int>.Zero), limit), cy)
                             & (Vector256.LessThan(Vector256.Abs(x), infinity) &
                                Vector256.LessThan(Vector256.Abs(y), infinity)).AsInt32();
                var tile = (Spread(Vector256.ShiftRightArithmetic(cx, TileBake.TileBits)) |
                            Vector256.ShiftLeft(Spread(Vector256.ShiftRightArithmetic(cy, TileBake.TileBits)), 1))
                    .AsUInt32();
                var local = (Spread(cx & tileMask) | Vector256.ShiftLeft(Spread(cy & tileMask), 1)).AsUInt32();
                var key = Vector256.ShiftLeft(tile, 2 * TileBake.TileBits) | local;
                Vector256.ConditionalSelect(inside.AsUInt32(), key, outside).Store(pk + i);
            }
        }

        return i;
    }

    private static Vector256<int> Spread(Vector256<int> value)
    {
        value &= Vector256.Create(0x0000FFFF);
        value = (value | Vector256.ShiftLeft(value, 8)) & Vector256.Create(0x00FF00FF);
        value = (value | Vector256.ShiftLeft(value, 4)) & Vector256.Create(0x0F0F0F0F);
        value = (value | Vector256.ShiftLeft(value, 2)) & Vector256.Create(0x33333333);
        return (value | Vector256.ShiftLeft(value, 1)) & Vector256.Create(0x55555555);
    }
#endif
}
