using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Gi;

public static unsafe partial class World
{
    internal static int ForcedWorkers;
    internal static long ParallelBakedTiles;

    internal static void ProcessParallel(byte world, int workers)
    {
        var w = GetContext(world);
        if (w == null) return;

        w->Tick++;
        w->Kernels.BeginFrame();
        if (w->TimedList.Length != 0) SweepTimed(w);

        var stamp = ProcessStages.Mark();
        SweepChanged(w);
        w->MarkEpoch++;
        w->Pending.Resize(0);
        ProcessStages.Measure(ref w->Stages.Prepare, stamp);

        stamp = ProcessStages.Mark();
        PublishDirtyParallel(w, workers);
        ProcessStages.Measure(ref w->Stages.Apply, stamp);

        if (w->DerivedCount != 0) Derive(w);
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            for (var l = 0; l < w->LayerCount; l++)
            {
                var ld = g->Layers + l;
                var dirty = ld->Dirty.Pointer;
                SortSlots(dirty, ld->Dirty.Length);
                for (var i = 0; i < ld->Dirty.Length; i++) StampEpoch(g, ld, dirty[i], w->Tick);
                var changed = ld->Dirty;
                ld->Dirty = ld->Changed;
                ld->Dirty.Resize(0);
                ld->Changed = changed;
            }
        }
    }

    [MethodImpl(Jit.Hot)]
    private static void PublishDirtyParallel(WorldCtx* w, int workers)
    {
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            for (var l = 0; l < w->LayerCount; l++)
            {
                if (IsDerived(w, l)) continue;

                var ld = g->Layers + l;
                var transient = IsTransient(w, l);
                if (transient) PlanTransient(w, gi, g, ld, (byte)l);
                if (ld->InDirty != null) PublishTilesParallel(w, g, ld, transient, workers);
                if (transient) CommitTransient(g, ld);
            }
        }
    }

    [MethodImpl(Jit.Hot)]
    private static void PublishTilesParallel(
        WorldCtx* w, GridCtx* g, LayerData* ld, bool transient, int workers)
    {
        var tps = g->TilesPerSide;
        var count = ld->Dirty.Length;
        if (count == 0)
        {
            ld->Rects.Resize(0);
            return;
        }

        ParallelBakedTiles += count;
        EnsureParallelOutcomes(count);
        var dirty = (nint)ld->Dirty.Pointer;
        var rects = (nint)ld->Rects.Pointer;
        var outcomes = (nint)ParallelOutcomes;
        var ldAddr = (nint)ld;
        var tick = w->Tick;
        if (workers <= 1 || count < 2)
        {
            BakeRange(ldAddr, dirty, rects, outcomes, 0, count, transient, tick);
        }
        else
        {
            if (workers > count) workers = count;
            Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, wi =>
            {
                var span = (count + workers - 1) / workers;
                var lo = wi * span;
                var hi = lo + span > count ? count : lo + span;
                BakeRange(ldAddr, dirty, rects, outcomes, lo, hi, transient, tick);
            });
        }

        var tiles = ld->Dirty.Pointer;
        var flags = ParallelOutcomes;
        var kept = 0;
        for (var i = 0; i < count; i++)
        {
            var tile = tiles[i];
            ld->InDirty[tile] = 0;
            var set = ld->SetOf(tile);
            if (set == null) continue;

            var outcome = (ResolveOutcome)flags[i];
            bool dropped;
            if ((outcome & ResolveOutcome.Empty) != 0 && (transient || ld->Store.PlanesZero(*set)))
            {
                DropPage(ld, tile);
                ld->Max.Update(tps, tile, 0);
                dropped = true;
            }
            else
            {
                ld->Max.Update(tps, tile, ld->Store.Cells(*set).Summary->Max);
                if (transient) RetireTransientTile(ld, set, tile);
                dropped = false;
            }

            if (dropped || (outcome & ResolveOutcome.Changed) != 0) RefreshTileHash(g, ld, tile);
            if ((outcome & ResolveOutcome.Changed) != 0) tiles[kept++] = tile;
        }

        ld->Dirty.Resize(kept);
        ld->Rects.Resize(0);
    }

    private static void BakeRange(
        nint ldAddr, nint dirtyAddr, nint rectsAddr, nint outcomesAddr,
        int lo, int hi, bool transient, int tick)
    {
        var ld = (LayerData*)ldAddr;
        var dirty = (int*)dirtyAddr;
        var rects = (TileRect*)rectsAddr;
        var outcomes = (byte*)outcomesAddr;
        var store = &ld->Store;
        for (var i = lo; i < hi; i++)
        {
            var tile = dirty[i];
            var set = ld->SetOf(tile);
            if (set == null)
            {
                outcomes[i] = 0;
                continue;
            }

            var rect = rects + i;
            if (transient) PrepareTransientSet(ld, set);
            outcomes[i] = (byte)TileBake.Publish(store, set, rect->X0, rect->Y0, rect->X1, rect->Y1, tick);
        }
    }

    private static void EnsureParallelOutcomes(int count)
    {
        if (count <= ParallelOutcomeCapacity) return;

        if (ParallelOutcomes != null) NativeHeap.Free(ParallelOutcomes);
        ParallelOutcomes = (byte*)NativeHeap.AllocZeroed((nuint)count);
        ParallelOutcomeCapacity = count;
    }
}
