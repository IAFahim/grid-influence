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
}
