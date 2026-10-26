using System.Runtime.CompilerServices;
namespace Gi;

public static unsafe partial class World
{
    [MethodImpl(Jit.Hot)]
    public static ulong LayerHash(byte world, byte grid, byte layer)
    {
        var w = GetContext(world);
        if (w == null || grid >= w->GridCount || layer >= w->LayerCount) return 0;

        return w->Grids[grid].Layers[layer].Hash;
    }

    [MethodImpl(Jit.Hot)]
    private static void RefreshTileHash(GridCtx* g, LayerData* ld, int tile)
    {
        if (ld->TileHashes == null)
            ld->TileHashes = (ulong*)NativeHeap.AllocZeroed((nuint)g->TileCount * sizeof(ulong));
        var page = ld->PageOf(tile);
        var current = page != null && TileBake.PageAny(page) ? TileHash.Of(tile, page) : 0;
        var prior = ld->TileHashes[tile];
        if (prior == current) return;

        ld->Hash ^= prior ^ current;
        ld->TileHashes[tile] = current;
    }
}
