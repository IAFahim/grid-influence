#if NET
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
#endif

namespace Gi;

public static unsafe partial class World
{
    private const int PrefetchAhead = 8;
    private const int PrefetchRows = 12;

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    #endif
    private static void PrefetchTarget(WorldCtx* w, SourceColumns* s, int slot)
    {
        #if NET
        if (!Sse.IsSupported || s->Alive[slot] == 0) return;

        var v = StampCatalog.Get(s->Stamp[slot]);
        if (v->Kind != StampKind.ConstantRectangle) return;

        var layer = s->Layer[slot];
        var mask = w->Recipes[layer].AnchorMask;
        var x = s->X[slot];
        var y = s->Y[slot];
        var scale = s->Scale[slot];
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            if (g->ScaleQ8 == 0) continue;

            var ld = g->Layers + layer;
            if (ld->Directory == null) continue;

            var sampling = (long)g->ScaleQ8 * scale >> 8;
            var cellX = (int)((TileBake.AnchorQ8(x, g->OriginX, g->ScaleQ8, mask) + ((long)v->OriginQ8X * sampling >> 8)) >> 8);
            var cellY = (int)((TileBake.AnchorQ8(y, g->OriginY, g->ScaleQ8, mask) + ((long)v->OriginQ8Y * sampling >> 8)) >> 8);
            if ((uint)cellX >= (uint)g->Size || (uint)cellY >= (uint)g->Size) continue;

            var set = ld->SetOf((cellY >> TileBake.TileBits) * g->TilesPerSide + (cellX >> TileBake.TileBits));
            if (set == null) continue;

            var column = cellX & (TileBake.TileSize - 1);
            var row = cellY & (TileBake.TileSize - 1);
            var rows = Math.Min(Math.Min((int)((v->Height * sampling + 255) >> 8) + 1, PrefetchRows), TileBake.TileSize - row);
            var span = Math.Min((int)((v->Width * sampling + 255) >> 8), TileBake.TileSize - 1 - column);
            var cells = ld->Store.Cells(*set).I + row * TileBake.TileSize + column;
            for (var r = 0; r < rows; r++)
            {
                Sse.Prefetch0(cells);
                Sse.Prefetch0(cells + span);
                cells += TileBake.TileSize;
            }
        }
        #endif
    }
}
