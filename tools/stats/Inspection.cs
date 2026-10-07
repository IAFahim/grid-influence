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
    public long PageBytes;
    public long PageSumBytes;
    public long RasterTiles;
    public long WorldArenaBytes;
    public long StampArenaBytes;
    public long StampRasterBytes;

    public readonly long WorldBytes => GridBytes + LayerBytes + SourceBytes + ScratchBytes +
        DirtyFlagBytes + DirtyQueueBytes + MapBytes + DifferenceBytes + DenseBytes + DensePointerBytes +
        PageBytes + PageSumBytes;

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
            ScratchBytes = TileBake.TileSize * sizeof(int),
            SourceBytes = (long)s->X.Capacity * sizeof(float) + (long)s->Y.Capacity * sizeof(float) +
                s->Stamp.Capacity + s->Layer.Capacity + s->Gain.Capacity + s->Alive.Capacity,
            WorldArenaBytes = World.MaxWorlds * sizeof(WorldCtx),
            StampArenaBytes = StampCatalog.MaxStamps * sizeof(StampVariant),
            Stamps = StampCatalog.Count - 1,
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
                result.DirtyTiles += ld->Dirty.Length;
                result.MapSlots += ld->Pages.SlotCount;
                result.Tombstones += ld->Pages.TombstoneCount;
                result.MapBytes += (long)ld->Pages.SlotCount * (sizeof(int) + sizeof(byte*) + sizeof(byte));
                result.DirtyQueueBytes += (long)ld->Dirty.Capacity * sizeof(int);
                if (ld->InDirty != null) result.DirtyFlagBytes += g->TileCount;

                var used = ld->Pages.Used;
                var blocks = ld->Pages.Blocks;
                for (var slot = 0; slot < ld->Pages.SlotCount; slot++)
                {
                    if (used[slot] != PageMap.Live) continue;
                    if (*(byte**)(blocks[slot] + World.DensePtrOffset) == null) continue;
                    result.RasterTiles++;
                    result.DenseBytes += World.DenseBytes;
                }
            }
        }

        var cells = TileBake.TileSize * TileBake.TileSize;
        result.DifferenceBytes = result.LiveTiles * World.PageOffset;
        result.DensePointerBytes = result.LiveTiles * (World.SumOffset - World.DensePtrOffset);
        result.PageBytes = result.LiveTiles * cells * sizeof(short);
        result.PageSumBytes = result.LiveTiles * World.SumSlotBytes;

        for (var i = 1; i < StampCatalog.Count; i++)
        {
            var v = StampCatalog.Get((byte)i);
            if (v->Kind != StampKind.Raster) continue;
            result.RasterStamps++;
            result.StampRasterBytes += (long)v->Pitch * (v->Height + 2);
        }

        return result;
    }
}
