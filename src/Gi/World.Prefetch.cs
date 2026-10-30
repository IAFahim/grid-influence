#if NET
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace Gi;

public static unsafe partial class World
{
    private const int PrefetchAhead = 8;
    private const int PrefetchRows = 12;

    [MethodImpl(Jit.Inline)]
    private static void PrefetchRecordTarget(WorldCtx* w, int slot)
    {
        if (!Sse.IsSupported) return;

        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            var rec = RecordOf(g, slot);
            if (rec->Alive == 0 || g->ScaleQ8 == 0) continue;

            var v = StampCatalog.Get(rec->Stamp);
            if (v->Kind != StampKind.ConstantRectangle) continue;

            var ld = g->Layers + rec->Layer;
            if (ld->Directory == null) continue;

            var sampling = ((long)g->ScaleQ8 * rec->Scale) >> 8;
            var cellX = (int)((rec->Ax + ((v->OriginQ8X * sampling) >> 8)) >> 8);
            var cellY = (int)((rec->Ay + ((v->OriginQ8Y * sampling) >> 8)) >> 8);
            if ((uint)cellX >= (uint)g->Size || (uint)cellY >= (uint)g->Size) continue;

            var set = ld->SetOf((cellY >> TileBake.TileBits) * g->TilesPerSide + (cellX >> TileBake.TileBits));
            if (set == null) continue;

            var column = cellX & (TileBake.TileSize - 1);
            var row = cellY & (TileBake.TileSize - 1);
            var rows =
                Math.Min(Math.Min(((v->Height * sampling + 255) >> 8) + 1, PrefetchRows), TileBake.TileSize - row);
            var span = Math.Min((int)((v->Width * sampling + 255) >> 8), TileBake.TileSize - 1 - column);
            var cells = ld->Store.Cells(*set).I + row * TileBake.TileSize + column;
            for (var r = 0; r < rows; r++)
            {
                Sse.Prefetch0(cells);
                Sse.Prefetch0(cells + span);
                cells += TileBake.TileSize;
            }
        }
    }
}
#endif
