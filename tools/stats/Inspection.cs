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
    public long DirtyFlagBytes;
    public long DirtyQueueBytes;
    public long MapBytes;
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
    public long StampArenaBytes;
    public long StampRasterBytes;

    public readonly long WorldBytes => GridBytes + LayerBytes + SourceBytes + ScratchBytes +
        DirtyFlagBytes + DirtyQueueBytes + MapBytes + DifferenceBytes + DenseBytes + DensePointerBytes +
        TentBytes + BellBytes + PageBytes + PageSumBytes + PyramidBytes + DepositQueueBytes;

    public readonly long SharedBytes => WorldArenaBytes + StampArenaBytes + StampRasterBytes;

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
            ScratchBytes = TileBake.TileSize * sizeof(int),
            SourceBytes = (long)s->X.Capacity * sizeof(float) + (long)s->Y.Capacity * sizeof(float) +
                s->Stamp.Capacity + s->Layer.Capacity + s->Gain.Capacity + s->Alive.Capacity +
                ((long)s->Angle.Capacity + s->Scale.Capacity) * sizeof(ushort) +
                (long)s->Free.Capacity * sizeof(int) + s->Gen.Capacity,
            WorldArenaBytes = World.MaxWorlds * sizeof(WorldCtx),
            StampArenaBytes = StampCatalog.MaxStamps * sizeof(StampVariant),

            DepositQueueBytes = (long)w->Ops.Capacity * sizeof(DepositOp) + (long)w->Pending.Capacity * sizeof(int) +
                (long)w->Fragments.Capacity * sizeof(DepositFragment) + (long)w->Placements.Capacity * sizeof(Placement) +
                (long)w->Journal.Capacity * sizeof(DepositOp),
        };

        for (var i = 0; i < s->Count; i++)
            if (s->Alive.Pointer[i] != 0) result.LiveSources++;

        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            result.LayerBytes += World.MaxLayers * sizeof(LayerData);
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
                result.DirtyQueueBytes += (long)(ld->Dirty.Capacity + ld->Changed.Capacity) * sizeof(int);
                result.PyramidBytes += ld->Max.Bytes;
                if (ld->InDirty != null) result.DirtyFlagBytes += g->TileCount;

                var used = ld->Pages.Used;
                var blocks = ld->Pages.Blocks;
                for (var slot = 0; slot < ld->Pages.SlotCount; slot++)
                {
                    if (used[slot] != PageMap.Live) continue;
                    if (*(byte**)(blocks[slot] + World.DensePtrOffset) != null)
                    {
                        result.RasterTiles++;
                        result.DenseBytes += World.DenseBytes;
                    }

                    if (*(byte**)(blocks[slot] + World.TentPtrOffset) != null)
                    {
                        result.TentTiles++;
                        result.TentBytes += World.TentBytes;
                    }

                    if (*(byte**)(blocks[slot] + World.BellPtrOffset) != null)
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
