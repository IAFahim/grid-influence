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
    public long DifferenceBytes;
    public long DenseBytes;
    public long DensePointerBytes;
    public long TentBytes;
    public long TentTiles;
    public long BellBytes;
    public long BellTiles;
    public long PageBytes;
    public long PageSumBytes;
    public long PyramidBytes;
    public long DepositQueueBytes;
    public long RasterTiles;
    public long WorldArenaBytes;
    public long WorldRevisionBytes;
    public long ZeroDenseBytes;
    public long StampArenaBytes;
    public long StampRasterBytes;

    public readonly long WorldBytes => GridBytes + LayerBytes + SourceBytes + ScratchBytes +
        DirtyFlagBytes + DirtyQueueBytes + DirectoryBytes + SlabBytes + PyramidBytes + DepositQueueBytes;

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
            ScratchBytes = TileBake.TileSize * sizeof(int) + w->Scratch.Bytes +
                (long)w->BatchSources.Capacity * sizeof(SourceBatchEntry) +
                (long)w->BatchCommands.Capacity * sizeof(SourceBatchLink) +
                (long)w->BatchLookup.Capacity * sizeof(int) +
                (long)w->BatchLayerDeltas.Capacity * sizeof(int) +
                (long)w->RebuildArena.Capacity * sizeof(RebuildChunk) +
                (long)w->RebuildPlacements.Capacity * sizeof(Placement),
            SourceBytes = (long)s->Chunks * SourceColumns.ChunkSlots *
                (2 * sizeof(float) + 4 * sizeof(byte) + 2 * sizeof(ushort) + sizeof(uint)) +
                (s->Chunks != 0 ? 9L * SourceColumns.MaxChunks * sizeof(void*) : 0) +
                s->Timed.Capacity + s->FadeFrom.Capacity + s->FadeTo.Capacity +
                ((long)s->FadeStart.Capacity + s->FadeTicks.Capacity + s->ExpireAt.Capacity + s->Due.Capacity) * sizeof(int) +
                (long)s->Free.Capacity * sizeof(int),
            WorldArenaBytes = World.MaxWorlds * sizeof(WorldCtx),
            WorldRevisionBytes = World.MaxWorlds * sizeof(int),
            ZeroDenseBytes = World.DenseBytes,
            StampArenaBytes = StampCatalog.MaxStamps * sizeof(StampVariant),

            DepositQueueBytes = (long)w->Ops.Capacity * sizeof(DepositOp) + (long)w->Pending.Capacity * sizeof(int) +
                (long)w->Fragments.Capacity * sizeof(DepositFragment) + (long)w->Placements.Capacity * sizeof(Placement) +
                (long)w->Journal.Capacity * sizeof(DepositOp) + (long)w->ScheduleJournal.Capacity * sizeof(ScheduleRecord) +
                (long)w->Timers.Capacity * sizeof(Alarm),
        };

        for (var i = 0; i < s->Count; i++)
            if (s->Alive[i] != 0) result.LiveSources++;

        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            result.LayerBytes += World.MaxLayers * sizeof(LayerData);
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

                result.SlabBytes += ld->Blocks.Bytes + ld->Headers.Bytes + ld->Denses.Bytes + ld->Planes.Bytes;
                result.SlabFreeUnits += ld->Blocks.FreeCount + ld->Headers.FreeCount +
                    ld->Denses.FreeCount + ld->Planes.FreeCount;
                result.DirtyQueueBytes += (long)(ld->Dirty.Capacity + ld->Changed.Capacity) * sizeof(int);
                result.PyramidBytes += ld->Max.Bytes;
                if (ld->InDirty != null) result.DirtyFlagBytes += (long)g->TileCount * (1 + sizeof(int));

                var directory = ld->Directory;
                if (directory == null) continue;
                for (var tile = 0; tile < g->TileCount; tile++)
                {
                    var index = directory[tile];
                    if (index < 0) continue;
                    var block = ld->Pages->Unit(index);
                    if (*(int*)(block + World.DensePtrOffset) >= 0)
                    {
                        result.RasterTiles++;
                        result.DenseBytes += World.DenseBytes;
                    }

                    if (*(int*)(block + World.TentPtrOffset) >= 0)
                    {
                        result.TentTiles++;
                        result.TentBytes += World.TentBytes;
                    }

                    if (*(int*)(block + World.BellPtrOffset) >= 0)
                    {
                        result.BellTiles++;
                        result.BellBytes += World.BellBytes;
                    }
                }
            }
        }

        result.DifferenceBytes = (result.LiveTiles - result.DerivedTiles) * (World.BlockBytes - World.HeaderBytes);
        result.DensePointerBytes = result.LiveTiles * (World.HeaderBytes - World.DensePtrOffset);
        result.PageBytes = result.LiveTiles * World.PageBytes;
        result.PageSumBytes = result.LiveTiles * (World.DensePtrOffset - World.SumOffset);

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
