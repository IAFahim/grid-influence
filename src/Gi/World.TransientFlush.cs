using System.Runtime.CompilerServices;

namespace Gi;

public static unsafe partial class World
{
    [MethodImpl(Jit.Hot)]
    private static void PlanTransient(WorldCtx* w, int gi, GridCtx* g, LayerData* ld, byte layer)
    {
        var hist = ld->Hist;
        if (hist == null || hist->Active.Length == 0)
        {
            if (ld->Stale.Length != 0) RestartStale(g, ld);
            return;
        }

        EnsureDirty(g, layer);
        hist->FusedClasses.Resize(0);
        hist->FusedTiles.Resize(0);
        hist->Vanished.Resize(0);
        for (var a = 0; a < hist->Active.Length; a++)
        {
            var stamp = (byte)hist->Active.Pointer[a];
            var cls = hist->Classes + stamp;
            if (BoxKernel(w, gi, g, layer, stamp, out var value, out var ox, out var oy, out var width, out var height))
            {
                ReplaySparse(w, gi, g, hist, cls, layer, stamp, width * height);
                if (cls->Tiles.Length == 0) continue;

                var n = hist->FusedClasses.Length;
                hist->FusedClasses.Resize(n + 1);
                hist->FusedClasses.Pointer[n] = new FusedClass
                {
                    Directory = cls->Directory,
                    Value = value,
                    OffsetX = ox,
                    OffsetY = oy,
                    Width = width,
                    Height = height,
                    Stamp = stamp
                };
                continue;
            }

            ReplayClass(w, gi, g, hist, cls, layer, stamp);
            ReleaseClass(hist, cls);
        }

        var classes = hist->FusedClasses.Pointer;
        var supported = hist->FusedClasses.Length != 0;
        for (var c = 0; c < hist->FusedClasses.Length; c++) supported &= FusedBox.Supports(classes[c]);

        var tps = g->TilesPerSide;
        var mark = CollectOutputTiles(g, hist);
        for (var o = 0; o < hist->OutTiles.Length; o++)
        {
            var tile = hist->OutTiles.Pointer[o];
            var set = ld->SetOf(tile);
            var fused = false;
            if (supported)
            {
                if (set == null)
                {
                    set = EnsureSet(g, ld, tile);
                    set->Stale = 1;
                    fused = true;
                }
                else
                {
                    fused = set->Stale != 0 && set->T < 0 && set->B < 0 && !HasWide(ld, set);
                }
            }

            if (!fused)
            {
                BoxAddTile(g, ld, hist, tile);
                continue;
            }

            hist->OutMark[tile] = mark + 1;
            var task = hist->FusedTiles.Length;
            hist->FusedTiles.Resize(task + 1);
            hist->FusedTiles.Pointer[task] = new FusedTile { Tile = tile };
        }

        var tasks = hist->FusedTiles.Pointer;
        for (var t = 0; t < hist->FusedTiles.Length; t++)
        {
            tasks[t].Page = ld->PageOf(tasks[t].Tile);
            tasks[t].Summary = ld->SummaryOf(tasks[t].Tile);
        }

        var plan = new FusedPlan
        {
            Classes = classes,
            ClassCount = hist->FusedClasses.Length,
            Region = (int*)hist->Pages.Region,
            ZeroPage = hist->ZeroPage,
            TilesPerSide = tps,
            Tick = w->Tick
        };
        for (var t = 0; t < hist->FusedTiles.Length; t++) FusedBox.Run(&plan, tasks + t);

        for (var t = 0; t < hist->FusedTiles.Length; t++)
            if (tasks[t].Fallback != 0)
                BoxAddTile(g, ld, hist, tasks[t].Tile);

        for (var c = 0; c < hist->FusedClasses.Length; c++) ReleaseClass(hist, hist->Classes + classes[c].Stamp);

        hist->Active.Resize(0);
        hist->Total = 0;
        RetireStale(g, ld, hist, mark);
    }

    [MethodImpl(Jit.Hot)]
    private static int CollectOutputTiles(GridCtx* g, HistState* hist)
    {
        var tps = g->TilesPerSide;
        hist->MarkStamp += 2;
        var mark = hist->MarkStamp;
        if (hist->OutMark == null) hist->OutMark = (int*)NativeHeap.AllocZeroed((nuint)g->TileCount * sizeof(int));
        hist->OutTiles.Resize(0);
        for (var c = 0; c < hist->FusedClasses.Length; c++)
        {
            var f = hist->FusedClasses.Pointer + c;
            var cls = hist->Classes + f->Stamp;
            for (var t = 0; t < cls->Tiles.Length; t++)
            {
                var tile = cls->Tiles.Pointer[t];
                var hx0 = (tile % tps) << TileBake.TileBits;
                var hy0 = (tile / tps) << TileBake.TileBits;
                var txLo = Math.Max((hx0 + f->OffsetX) >> TileBake.TileBits, 0);
                var txHi = Math.Min((hx0 + TileBake.TileSize - 1 + f->OffsetX + f->Width - 1) >> TileBake.TileBits,
                    tps - 1);
                var tyLo = Math.Max((hy0 + f->OffsetY) >> TileBake.TileBits, 0);
                var tyHi = Math.Min((hy0 + TileBake.TileSize - 1 + f->OffsetY + f->Height - 1) >> TileBake.TileBits,
                    tps - 1);
                for (var ty = tyLo; ty <= tyHi; ty++)
                for (var tx = txLo; tx <= txHi; tx++)
                {
                    var index = ty * tps + tx;
                    if (hist->OutMark[index] == mark) continue;

                    hist->OutMark[index] = mark;
                    var n = hist->OutTiles.Length;
                    hist->OutTiles.Resize(n + 1);
                    hist->OutTiles.Pointer[n] = index;
                }
            }
        }

        return mark;
    }

    [MethodImpl(Jit.Hot)]
    private static void BoxAddTile(GridCtx* g, LayerData* ld, HistState* hist, int outTile)
    {
        var tps = g->TilesPerSide;
        var tx0 = (outTile % tps) << TileBake.TileBits;
        var ty0 = (outTile / tps) << TileBake.TileBits;
        for (var c = 0; c < hist->FusedClasses.Length; c++)
        {
            var f = hist->FusedClasses.Pointer + c;
            var windowHeight = TileBake.TileSize + f->Height - 1;
            GatherWindow(hist, hist->Classes + f->Stamp, tps, g->Size, tx0 - f->OffsetX - f->Width + 1,
                ty0 - f->OffsetY - f->Height + 1, TileBake.TileSize + f->Width - 1, windowHeight);
            var peak = SumWindow(hist, f->Width, f->Height, windowHeight);
            if (peak == 0) continue;

            var sums = hist->Out;
            var set = EnsureSet(g, ld, outTile);
            PrepareTransientSet(ld, set);
            var dirty = DirtyIndex(g, ld, outTile);
            ld->Rects.Pointer[dirty] = new TileRect { X0 = 0, Y0 = 0, X1 = TileBake.TileSize, Y1 = TileBake.TileSize };
            var amount = peak * Math.Abs((long)f->Value);
            if (amount > PlaneFold.LimitI(&ld->Store))
            {
                PlaneFold.AddWideI(&ld->Store, set, sums, f->Value);
                continue;
            }

            PlaneFold.ChargeI(&ld->Store, set, amount);
            AddScaled(ld->Store.Cells(*set).I, sums, f->Value);
        }
    }

    [MethodImpl(Jit.Hot)]
    private static void RestartStale(GridCtx* g, LayerData* ld)
    {
        for (var i = 0; i < ld->Stale.Length; i++)
        {
            var dirty = DirtyIndex(g, ld, ld->Stale.Pointer[i]);
            ld->Rects.Pointer[dirty] = new TileRect { X0 = 0, Y0 = 0, X1 = TileBake.TileSize, Y1 = TileBake.TileSize };
        }

        ld->Stale.Resize(0);
    }

    [MethodImpl(Jit.Hot)]
    private static void RetireStale(GridCtx* g, LayerData* ld, HistState* hist, int mark)
    {
        var outMark = hist->OutMark;
        for (var i = 0; i < ld->Stale.Length; i++)
        {
            var tile = ld->Stale.Pointer[i];
            if (outMark != null && outMark[tile] == mark + 1) continue;

            var set = ld->SetOf(tile);
            if (set == null) continue;

            if (set->Stale == 0)
            {
                var dirty = DirtyIndex(g, ld, tile);
                ld->Rects.Pointer[dirty] = new TileRect
                    { X0 = 0, Y0 = 0, X1 = TileBake.TileSize, Y1 = TileBake.TileSize };
                continue;
            }

            var n = hist->Vanished.Length;
            hist->Vanished.Resize(n + 1);
            hist->Vanished.Pointer[n] = tile;
        }

        ld->Stale.Resize(0);
    }

    [MethodImpl(Jit.Hot)]
    private static void CommitTransient(GridCtx* g, LayerData* ld)
    {
        var hist = ld->Hist;
        if (hist == null) return;

        var tps = g->TilesPerSide;
        for (var v = 0; v < hist->Vanished.Length; v++)
        {
            var tile = hist->Vanished.Pointer[v];
            var changed = TileBake.PageAny(ld->PageOf(tile));
            DropPage(ld, tile);
            ld->Max.Update(tps, tile, 0);
            RefreshTileHash(g, ld, tile);
            if (changed) AppendChanged(ld, tile);
        }

        for (var t = 0; t < hist->FusedTiles.Length; t++)
        {
            var task = hist->FusedTiles.Pointer + t;
            if (task->Fallback != 0) continue;

            var changed = (task->Outcome & ResolveOutcome.Changed) != 0;
            if ((task->Outcome & ResolveOutcome.Empty) != 0)
            {
                DropPage(ld, task->Tile);
                ld->Max.Update(tps, task->Tile, 0);
                ApplyTileHash(g, ld, task->Tile, task->Hash);
            }
            else
            {
                ld->Max.Update(tps, task->Tile, task->Max);
                RetireTransientTile(ld, ld->SetOf(task->Tile), task->Tile);
                ApplyTileHash(g, ld, task->Tile, task->Hash);
            }

            if (changed) AppendChanged(ld, task->Tile);
        }

        hist->FusedTiles.Resize(0);
        hist->Vanished.Resize(0);
    }

    [MethodImpl(Jit.Inline)]
    private static bool HasWide(LayerData* ld, TilePlanes* set)
    {
        var summary = (TileSummary*)ld->Store.Summaries.Unit(set->Summary);
        return (summary->CarryI | summary->HighT | summary->LowT | summary->HighB | summary->LowB) != 0;
    }

    [MethodImpl(Jit.Inline)]
    private static void AppendChanged(LayerData* ld, int tile)
    {
        var n = ld->Dirty.Length;
        ld->Dirty.Resize(n + 1);
        ld->Dirty.Pointer[n] = tile;
    }
}
