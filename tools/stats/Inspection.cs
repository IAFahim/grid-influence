namespace Gi.Stats;

internal struct Snapshot
{
    public bool Valid;
    public int Grids;
    public int Layers;
    public int SourceSlots;
    public int LiveSources;
    public int SourceCapacity;
    public long PossibleTiles;
    public long LiveTiles;
    public long DerivedTiles;
    public long DirtyTiles;
    public long Directories;
    public long SlabFreeUnits;
    public int Stamps;
    public int RasterStamps;
    public long GridBytes;
    public long LayerBytes;
    public long SourceBytes;
    public long ScratchBytes;
    public long DirtyFlagBytes;
    public long DirtyQueueBytes;
    public long DirectoryBytes;
    public long SlabBytes;
    public long Int32Bytes;
    public long TentBytes;
    public long TentTiles;
    public long BellBytes;
    public long BellTiles;
    public long PageBytes;
    public long SummaryBytes;
    public long SetBytes;
    public long PyramidBytes;
    public long EpochTreeBytes;
    public long QueueBytes;
    public long RecordBytes;
    public long KernelBytes;
    public long WorldArenaBytes;
    public long WorldRevisionBytes;
    public long ZeroDenseBytes;
    public long StampArenaBytes;
    public long StampRasterBytes;

    public readonly long WorldBytes => GridBytes + LayerBytes + SourceBytes + ScratchBytes +
        DirtyFlagBytes + DirtyQueueBytes + DirectoryBytes + SlabBytes + PyramidBytes + EpochTreeBytes + QueueBytes +
        RecordBytes + KernelBytes;

    public readonly long SharedBytes => WorldArenaBytes + WorldRevisionBytes + ZeroDenseBytes +
        StampArenaBytes + StampRasterBytes;

    public readonly long NativeBytes => WorldBytes + SharedBytes;
}

internal static unsafe class Inspection
{
    public static Snapshot Read(byte world)
    {
        var w = World.GetContext(world);
        if (w == null) return default;

        var s = &w->Sources;
        var result = new Snapshot
        {
            Valid = true,
            Grids = w->GridCount,
            Layers = w->LayerCount,
            SourceSlots = s->Count,
            SourceCapacity = s->Slots,
            GridBytes = World.MaxGrids * sizeof(GridCtx),
            LayerBytes = World.MaxLayers * sizeof(LayerRecipe),
            ScratchBytes = 0,
            KernelBytes = w->Kernels.Bytes,
            SourceBytes = (long)s->Chunks * SourceColumns.ChunkSlots *
                (2 * sizeof(float) + 4 * sizeof(byte) + 2 * sizeof(ushort) + sizeof(uint) + sizeof(int)) +
                (s->Chunks != 0 ? 10L * SourceColumns.MaxChunks * sizeof(void*) : 0) +
                ((long)s->HotStamp.Capacity + s->SchedStamp.Capacity) * sizeof(int) +
                s->Timed.Capacity + s->FadeFrom.Capacity + s->FadeTo.Capacity +
                ((long)s->FadeStart.Capacity + s->FadeTicks.Capacity + s->ExpireAt.Capacity + s->TimedAt.Capacity) * sizeof(int) +
                (long)s->Free.Capacity * sizeof(int),
            WorldArenaBytes = World.MaxWorlds * sizeof(WorldCtx),
            WorldRevisionBytes = World.MaxWorlds * sizeof(int),
            ZeroDenseBytes = World.PageBytes,
            StampArenaBytes = StampCatalog.MaxStamps * sizeof(StampVariant),

            QueueBytes = (long)w->Changed.Capacity * sizeof(int) +
                ((long)w->Pending.Capacity + w->Journal.Capacity) * sizeof(JournalEntry) +
                (long)w->ScheduleJournal.Capacity * sizeof(ScheduleRecord) +
                (long)w->TimedList.Capacity * sizeof(TimedEntry) + (long)w->TimedDue.Capacity * sizeof(int),
        };

        for (var i = 0; i < s->Count; i++)
            if (s->Alive[i] != 0) result.LiveSources++;

        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            result.LayerBytes += World.MaxLayers * sizeof(LayerData);
            result.RecordBytes += (long)g->Records.Capacity * sizeof(DepositRecord);
            result.PossibleTiles += (long)g->TileCount * w->LayerCount;
            for (var li = 0; li < w->LayerCount; li++)
            {
                var ld = g->Layers + li;
                result.LiveTiles += ld->LivePages;
                if (World.IsDerived(w, li)) result.DerivedTiles += ld->LivePages;
                result.DirtyTiles += ld->Dirty.Length;
                if (ld->Directory != null)
                {
                    result.Directories++;
                    result.DirectoryBytes += (long)g->TileCount * sizeof(int);
                }

                result.SlabBytes += ld->Store.Bytes + ld->Sets.Bytes;
                result.SlabFreeUnits += ld->Store.Pages.FreeCount + ld->Store.Int32s.FreeCount +
                    ld->Store.Int64s.FreeCount + ld->Store.Summaries.FreeCount + ld->Sets.FreeCount;
                result.DirtyQueueBytes += (long)(ld->Dirty.Capacity + ld->Changed.Capacity + ld->Stale.Capacity) * sizeof(int) +
                    (long)ld->Rects.Capacity * sizeof(TileRect);
                result.PyramidBytes += ld->Max.Bytes;
                result.EpochTreeBytes += ld->EpochTree.Bytes;
                if (ld->InDirty != null) result.DirtyFlagBytes += 2L * g->TileCount * sizeof(int);

                var directory = ld->Directory;
                if (directory == null) continue;
                for (var tile = 0; tile < g->TileCount; tile++)
                {
                    var index = directory[tile];
                    if (index < 0) continue;
                    var set = (TilePlanes*)ld->Sets.Unit(index);
                    if (set->T >= 0)
                    {
                        result.TentTiles++;
                        result.TentBytes += PlaneStore.Int64Unit;
                    }

                    if (set->B >= 0)
                    {
                        result.BellTiles++;
                        result.BellBytes += PlaneStore.Int64Unit;
                    }
                }
            }
        }

        result.PageBytes = result.LiveTiles * PlaneStore.PageUnit;
        result.Int32Bytes = (result.LiveTiles - result.DerivedTiles) * PlaneStore.Int32Unit;
        result.SummaryBytes = result.LiveTiles * TileSummary.Bytes;
        result.SetBytes = result.LiveTiles * sizeof(TilePlanes);

        for (var i = 1; i < StampCatalog.Count; i++)
        {
            var v = StampCatalog.Get((byte)i);
            if (v->Live == 0) continue;
            result.Stamps++;
            if (v->Data == null) continue;
            result.RasterStamps++;
            result.StampRasterBytes += (long)v->Pitch * (v->Height + 2);
            var mw = v->Width;
            var mh = v->Height;
            for (var level = 0; level < v->MipCount; level++)
            {
                mw = (mw + 1) >> 1;
                mh = (mh + 1) >> 1;
                result.StampRasterBytes += (long)(mw + 2) * (mh + 2);
            }
        }

        return result;
    }
}
