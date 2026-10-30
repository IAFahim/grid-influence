using System.Runtime.CompilerServices;
namespace Gi;

public static unsafe partial class World
{
    [MethodImpl(Jit.Hot)]
    public static ulong LayerHash(byte world, byte grid, byte layer)
    {
        var w = GetContext(world);
        if (w == null || grid >= w->GridCount || layer >= w->LayerCount) return 0;

        var g = w->Grids + grid;
        var ld = g->Layers + layer;
        FlushTileHashes(g, ld);
        return ld->Hash;
    }

    [MethodImpl(Jit.Hot)]
    private static void RefreshTileHash(GridCtx* g, LayerData* ld, int tile)
    {
        if (ld->HashPending == null)
            ld->HashPending = (uint*)NativeHeap.AllocZeroed((nuint)(((g->TileCount + 31) >> 5) * sizeof(uint)));
        var word = ld->HashPending + (tile >> 5);
        var bit = 1u << (tile & 31);
        if ((*word & bit) != 0) return;

        *word |= bit;
        ld->HashPendingCount++;
    }

    [MethodImpl(Jit.Hot)]
    private static void FlushTileHashes(GridCtx* g, LayerData* ld)
    {
        var pending = ld->HashPending;
        if (pending == null || ld->HashPendingCount == 0) return;

        var words = (g->TileCount + 31) >> 5;
        for (var w = 0; w < words; w++)
        {
            var bits = pending[w];
            if (bits == 0) continue;

            var tile = w << 5;
            for (; bits != 0; bits >>= 1, tile++)
            {
                if ((bits & 1) == 0) continue;

                pending[w] &= ~(1u << (tile & 31));
                ld->HashPendingCount--;
                var page = ld->PageOf(tile);
                ApplyTileHash(g, ld, tile, page != null && TileBake.PageAny(page) ? TileHash.Of(tile, page) : 0);
            }
        }
    }

    [MethodImpl(Jit.Hot)]
    private static void ApplyTileHash(GridCtx* g, LayerData* ld, int tile, ulong current)
    {
        if (ld->TileHashes == null)
            ld->TileHashes = (ulong*)NativeHeap.AllocZeroed((nuint)g->TileCount * sizeof(ulong));
        var prior = ld->TileHashes[tile];
        if (prior == current) return;

        ld->Hash ^= prior ^ current;
        ld->TileHashes[tile] = current;
    }
}
