using System.Runtime.CompilerServices;
namespace Gi;

public static unsafe partial class World
{
    private const int EpochFan = Pyramid<int, EpochMaxOp>.FanOut;

    [MethodImpl(Jit.Hot)]
    private static void StampEpoch(GridCtx* g, LayerData* ld, int tile, int epoch)
    {
        ld->Epochs[tile] = epoch;
        ld->EpochTree.Update(g->TilesPerSide, tile, epoch);
    }

    [MethodImpl(Jit.Hot)]
    private static int CollectNewer(GridCtx* g, LayerData* ld, int since, int* destination)
    {
        var tree = ld->EpochTree;
        if (tree.Levels == 0) return 0;

        var count = 0;
        CollectNode(tree, g->TilesPerSide, tree.Levels - 1, 0, 0, since, destination, ref count);
        if (destination != null) SortSlots(destination, count);
        return count;
    }

    [MethodImpl(Jit.Hot)]
    private static void CollectNode(
        in Pyramid<int, EpochMaxOp> tree, int tilesPerSide, int level, int nodeX, int nodeY, int since,
        int* destination, ref int count)
    {
        var node = tree.Node(level, nodeX, nodeY);
        var childSide = tree.ChildSide(level, tilesPerSide);
        var vx = Math.Min(EpochFan, childSide - (nodeX << 3));
        var vy = Math.Min(EpochFan, childSide - (nodeY << 3));
        for (var cy = 0; cy < vy; cy++)
        for (var cx = 0; cx < vx; cx++)
        {
            if (node[cy * EpochFan + cx] - since <= 0) continue;

            var childX = (nodeX << 3) + cx;
            var childY = (nodeY << 3) + cy;
            if (level != 0)
            {
                CollectNode(tree, tilesPerSide, level - 1, childX, childY, since, destination, ref count);
                continue;
            }

            if (destination != null) destination[count] = childY * tilesPerSide + childX;
            count++;
        }
    }

    [MethodImpl(Jit.Hot)]
    private static bool AnyNewerInDisk(GridCtx* g, LayerData* ld, in Circle disk, int since)
    {
        var tree = ld->EpochTree;
        if (tree.Levels == 0) return false;

        return DiskNode(tree, g->TilesPerSide, disk, tree.Levels - 1, 0, 0, since);
    }

    [MethodImpl(Jit.Hot)]
    private static bool DiskNode(
        in Pyramid<int, EpochMaxOp> tree, int tilesPerSide, in Circle disk, int level, int nodeX, int nodeY, int since)
    {
        var node = tree.Node(level, nodeX, nodeY);
        var childSide = tree.ChildSide(level, tilesPerSide);
        var vx = Math.Min(EpochFan, childSide - (nodeX << 3));
        var vy = Math.Min(EpochFan, childSide - (nodeY << 3));
        var shift = 3 * level;
        var tx0 = disk.X0 >> TileBake.TileBits;
        var ty0 = disk.Y0 >> TileBake.TileBits;
        var tx1 = disk.X1 >> TileBake.TileBits;
        var ty1 = disk.Y1 >> TileBake.TileBits;
        var loX = Math.Max(0, (tx0 >> shift) - (nodeX << 3));
        var loY = Math.Max(0, (ty0 >> shift) - (nodeY << 3));
        var hiX = Math.Min(vx - 1, (tx1 >> shift) - (nodeX << 3));
        var hiY = Math.Min(vy - 1, (ty1 >> shift) - (nodeY << 3));
        for (var cy = loY; cy <= hiY; cy++)
        for (var cx = loX; cx <= hiX; cx++)
        {
            if (node[cy * EpochFan + cx] - since <= 0) continue;

            var childX = (nodeX << 3) + cx;
            var childY = (nodeY << 3) + cy;
            if (level != 0)
            {
                if (DiskNode(tree, tilesPerSide, disk, level - 1, childX, childY, since)) return true;
                continue;
            }

            if (HoldsDiskCell(disk, childX, childY)) return true;
        }

        return false;
    }
}
