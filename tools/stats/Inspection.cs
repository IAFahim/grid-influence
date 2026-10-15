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
    public long MapSlots;
    public long Tombstones;
    public int Stamps;
    public int RasterStamps;
    public long GridBytes;
    public long LayerBytes;
    public long SourceBytes;
    public long ScratchBytes;
    public long PreparedBytes;
    public long BinBytes;
    public long ScheduleBytes;
    public long DirtyFlagBytes;
    public long DirtyQueueBytes;
    public long MapBytes;
    public long DirectoryBytes;
    public long PageBytes;
    public long PageSumBytes;
    public long RowSummaryBytes;
    public long PyramidBytes;
    public long DepositQueueBytes;
    public long WorldArenaBytes;
    public long WorldRevisionBytes;
    public long ZeroPageBytes;
    public long StampArenaBytes;
    public long StampRasterBytes;

    public readonly long WorldBytes => GridBytes + LayerBytes + SourceBytes + ScratchBytes +
        PreparedBytes + BinBytes + ScheduleBytes + DirtyFlagBytes + DirtyQueueBytes + MapBytes + DirectoryBytes +
        PageBytes + PageSumBytes + RowSummaryBytes + PyramidBytes + DepositQueueBytes;

    public readonly long SharedBytes => WorldArenaBytes + WorldRevisionBytes + ZeroPageBytes + StampArenaBytes + StampRasterBytes;

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
            SourceCapacity = s->X.Capacity,
            GridBytes = World.MaxGrids * sizeof(GridCtx),
            LayerBytes = World.MaxLayers * sizeof(LayerRecipe),
            ScratchBytes = (long)w->BatchSources.Capacity * sizeof(SourceBatchEntry) +
                (long)w->BatchCommands.Capacity * sizeof(SourceBatchLink) +
                (long)w->BatchLookup.Capacity * sizeof(int),
            ScheduleBytes = (long)w->Timers.Capacity * sizeof(Alarm) +
                ((long)w->TimerPositions.Capacity + w->TimerExpired.Capacity + w->TimerSort.Capacity) * sizeof(int) +
                (long)w->ScheduleJournal.Capacity * sizeof(ScheduleRecord),
            SourceBytes = (long)s->X.Capacity * sizeof(float) + (long)s->Y.Capacity * sizeof(float) +
                s->Stamp.Capacity + s->Layer.Capacity + s->Gain.Capacity + s->Alive.Capacity +
                ((long)s->Angle.Capacity + s->Scale.Capacity) * sizeof(ushort) +
                s->Timed.Capacity + s->FadeFrom.Capacity + s->FadeTo.Capacity +
                ((long)s->FadeStart.Capacity + s->FadeTicks.Capacity + s->ExpireAt.Capacity + s->Due.Capacity) * sizeof(int) +
                (long)s->Free.Capacity * sizeof(int) + s->Gen.Capacity,
            WorldArenaBytes = World.MaxWorlds * sizeof(WorldCtx),
            WorldRevisionBytes = World.MaxWorlds * sizeof(int),
            ZeroPageBytes = World.PageBytes,
            StampArenaBytes = StampCatalog.MaxStamps * sizeof(StampVariant),

            DepositQueueBytes = (long)w->Ops.Capacity * sizeof(DepositOp) + (long)w->Pending.Capacity * sizeof(int) +
                (long)w->Journal.Capacity * sizeof(DepositOp),
        };

        for (var i = 0; i < s->Count; i++)
            if (s->Alive.Pointer[i] != 0) result.LiveSources++;

        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            result.LayerBytes += World.MaxLayers * sizeof(LayerData);
            result.PreparedBytes += (long)g->Prepared.Capacity * sizeof(Placement) +
                (long)g->Footprints.Capacity * sizeof(PreparedFootprint);
            result.PossibleTiles += (long)g->TileCount * w->LayerCount;
            for (var li = 0; li < w->LayerCount; li++)
            {
                var ld = g->Layers + li;
                result.LiveTiles += ld->Pages.Count;
                if (World.IsDerived(w, li)) result.DerivedTiles += ld->Pages.Count;
                result.DirtyTiles += ld->Dirty.Length;
                result.MapSlots += ld->Pages.SlotCount;
                result.Tombstones += ld->Pages.TombstoneCount;
                result.MapBytes += (long)ld->Pages.SlotCount * (sizeof(int) + sizeof(byte*) + sizeof(byte));
                result.DirectoryBytes += ld->Pages.DirectoryBytes;
                result.BinBytes += (long)ld->Bins.Capacity * sizeof(SourceBin) + (long)ld->BinEntries.Capacity * sizeof(int);
                result.DirtyQueueBytes += (long)(ld->Dirty.Capacity + ld->Changed.Capacity) * sizeof(int);
                result.PyramidBytes += ld->Max.Bytes;
                if (ld->InDirty != null) result.DirtyFlagBytes += g->TileCount;
                if (ld->Epochs != null) result.DirtyFlagBytes += (long)g->TileCount * sizeof(int);
            }
        }

        result.PageBytes = result.LiveTiles * World.PageBytes;
        result.PageSumBytes = result.LiveTiles * (World.RowSumOffset - World.SumOffset);
        result.RowSummaryBytes = result.LiveTiles * (World.HeaderBytes - World.RowSumOffset);

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
