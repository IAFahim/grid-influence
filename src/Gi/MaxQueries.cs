namespace Gi;

internal static unsafe class MaxQueries
{
    private const int FanOut = Pyramid<short, ShortMaxOp>.FanOut;
    private const int NodeSlots = Pyramid<short, ShortMaxOp>.NodeSlots;

    public static short Best(in Pyramid<short, ShortMaxOp> pyramid, int tilesPerSide, LayerData* ld, out int x, out int y)
    {
        if (pyramid.Levels == 0)
        {
            x = 0;
            y = 0;
            return 0;
        }

        var root = pyramid.Levels - 1;
        var childSide = pyramid.ChildSide(root, tilesPerSide);
        var target = Pyramid<short, ShortMaxOp>.ReduceMax(
            pyramid.Node(root, 0, 0), Math.Min(FanOut, childSide), Math.Min(FanOut, childSide));
        var nodeX = 0;
        var nodeY = 0;
        for (var level = root; level >= 0; level--)
        {
            var node = pyramid.Node(level, nodeX, nodeY);
            var vx = Math.Min(FanOut, childSide - (nodeX << 3));
            var vy = Math.Min(FanOut, childSide - (nodeY << 3));
            var slot = FirstEqual(node, target, vx, vy);
            var childX = (nodeX << 3) + (slot & 7);
            var childY = (nodeY << 3) + (slot >> 3);
            if (level == 0)
            {
                x = childX << TileBake.TileBits;
                y = childY << TileBake.TileBits;
                var page = ld->PageOf(childY * tilesPerSide + childX);
                if (page == null) return target;

                var count = TileBake.TileSize * TileBake.TileSize;
                var index = FirstEqualFlat(page, target, count);
                if (index == count)
                {
                    index = 0;
                    var best = page[0];
                    for (var i = 1; i < count; i++)
                        if (page[i] > best)
                        {
                            best = page[i];
                            index = i;
                        }
                }

                x += index & (TileBake.TileSize - 1);
                y += index >> TileBake.TileBits;
                return target;
            }

            nodeX = childX;
            nodeY = childY;
            childSide = pyramid.ChildSide(level - 1, tilesPerSide);
        }

        x = 0;
        y = 0;
        return target;
    }

    public static short Best(in Pyramid<short, ShortMaxOp> pyramid, int tilesPerSide, LayerData* ld,
        int cellX0, int cellY0, int cellX1, int cellY1, out int x, out int y)
    {
        x = cellX0;
        y = cellY0;
        if (pyramid.Levels == 0) return 0;

        var tx0 = cellX0 >> TileBake.TileBits;
        var ty0 = cellY0 >> TileBake.TileBits;
        var tx1 = (cellX1 - 1) >> TileBake.TileBits;
        var ty1 = (cellY1 - 1) >> TileBake.TileBits;
        var best = int.MinValue;
        Visit(pyramid, tilesPerSide, ld, pyramid.Levels - 1, 0, 0, tx0, ty0, tx1, ty1,
            cellX0, cellY0, cellX1, cellY1, ref best, ref x, ref y);
        return best == int.MinValue ? (short)0 : (short)best;
    }

    private static void Visit(in Pyramid<short, ShortMaxOp> pyramid, int tilesPerSide, LayerData* ld,
        int level, int nodeX, int nodeY,
        int tx0, int ty0, int tx1, int ty1, int cellX0, int cellY0, int cellX1, int cellY1,
        ref int best, ref int x, ref int y)
    {
        var node = pyramid.Node(level, nodeX, nodeY);
        var childSide = pyramid.ChildSide(level, tilesPerSide);
        var vx = Math.Min(FanOut, childSide - (nodeX << 3));
        var vy = Math.Min(FanOut, childSide - (nodeY << 3));
        var shift = 3 * level;
        var loX = Math.Max(0, (tx0 >> shift) - (nodeX << 3));
        var loY = Math.Max(0, (ty0 >> shift) - (nodeY << 3));
        var hiX = Math.Min(vx - 1, (tx1 >> shift) - (nodeX << 3));
        var hiY = Math.Min(vy - 1, (ty1 >> shift) - (nodeY << 3));
        for (var cy = loY; cy <= hiY; cy++)
        for (var cx = loX; cx <= hiX; cx++)
        {
            if (node[cy * FanOut + cx] <= best) continue;
            var childX = (nodeX << 3) + cx;
            var childY = (nodeY << 3) + cy;
            if (level != 0)
            {
                Visit(pyramid, tilesPerSide, ld, level - 1, childX, childY,
                    tx0, ty0, tx1, ty1, cellX0, cellY0, cellX1, cellY1, ref best, ref x, ref y);
                continue;
            }

            var tileX0 = childX << TileBake.TileBits;
            var tileY0 = childY << TileBake.TileBits;
            var ox0 = Math.Max(cellX0, tileX0);
            var oy0 = Math.Max(cellY0, tileY0);
            var ox1 = Math.Min(cellX1 - 1, tileX0 + TileBake.TileSize - 1);
            var oy1 = Math.Min(cellY1 - 1, tileY0 + TileBake.TileSize - 1);
            var page = ld->PageOf(childY * tilesPerSide + childX);
            if (page == null)
            {
                if (0 > best)
                {
                    best = 0;
                    x = ox0;
                    y = oy0;
                }

                continue;
            }

            for (var row = oy0; row <= oy1; row++)
            {
                var line = page + (row - tileY0) * TileBake.TileSize;
                for (var col = ox0; col <= ox1; col++)
                    if (line[col - tileX0] > best)
                    {
                        best = line[col - tileX0];
                        x = col;
                        y = row;
                    }
            }
        }
    }

    private static int FirstEqual(short* slots, short target, int vx, int vy)
    {
#if NET
        if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated) return SimdOps.FirstEqualRows(slots, FanOut, target, vx, vy);
#endif
        for (var y = 0; y < vy; y++)
        {
            var row = slots + y * FanOut;
            for (var x = 0; x < vx; x++)
                if (row[x] == target) return y * FanOut + x;
        }

        return NodeSlots;
    }

    private static int FirstEqualFlat(short* values, short target, int count)
    {
#if NET
        if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated || System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated)
            return SimdOps.FirstEqualFlat(values, target, count);
#endif
        for (var i = 0; i < count; i++)
            if (values[i] == target) return i;
        return count;
    }
}
