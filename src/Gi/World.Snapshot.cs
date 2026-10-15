using System.Runtime.CompilerServices;

namespace Gi;

internal struct SourceBin
{
    internal int Start;
    internal int Count;
    internal byte Planes;
}

internal struct PreparedFootprint
{
    internal ushort X0;
    internal ushort Y0;
    internal ushort X1;
    internal ushort Y1;
    internal byte Layer;
    internal sbyte Gain;
    internal byte Planes;
}

public static unsafe partial class World
{
#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    private static void ProcessSources(WorldCtx* w)
    {
        w->Tick++;
        if (w->Timers.Length != 0) RunTimers(w);
        var stamp = ProcessStages.Mark();
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            PrepareSnapshot(w, g);
            uint layers = 0;
            for (var layer = 0; layer < w->LayerCount; layer++)
                if (!IsDerived(w, layer) && g->Layers[layer].Dirty.Length != 0) layers |= 1u << layer;
            for (byte layer = 0; layer < w->LayerCount; layer++)
                if ((layers & (1u << layer)) != 0) BuildSourceBins(w, g, layer);
        }

        ProcessStages.Measure(ref ProcessStages.PrepareTicks, stamp);
        CommitSourceSnapshot(w);
        stamp = ProcessStages.Mark();
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            for (byte layer = 0; layer < w->LayerCount; layer++)
            {
                if (IsDerived(w, layer)) continue;
                var ld = g->Layers + layer;
                for (var i = 0; i < ld->Dirty.Length; i++) ResolveSourceTile(g, layer, ld->Dirty.Pointer[i]);
            }
        }

        ProcessStages.Measure(ref ProcessStages.ResolveTicks, stamp);
        if (w->DerivedCount != 0) Derive(w);
        PublishSnapshot(w);
        w->Snapshot = 1;
    }

    private static void PrepareSnapshot(WorldCtx* w, GridCtx* g)
    {
        var source = &w->Sources;
        var previous = g->Prepared.Length;
        g->Prepared.Resize(source->Count);
        g->Footprints.Resize(source->Count);
        if (source->Count > previous)
        {
            new Span<Placement>(g->Prepared.Pointer + previous, source->Count - previous).Clear();
            new Span<PreparedFootprint>(g->Footprints.Pointer + previous, source->Count - previous).Clear();
        }

        if (w->Snapshot == 0)
        {
            for (var slot = 0; slot < source->Count; slot++) PrepareSnapshotSource(w, g, slot);
            return;
        }

        for (var index = 0; index < w->Ops.Length; index++) PrepareSnapshotSource(w, g, w->Ops.Pointer[index].Slot);
    }

    private static void PrepareSnapshotSource(WorldCtx* w, GridCtx* g, int slot)
    {
        var sources = &w->Sources;
        var footprint = g->Footprints.Pointer + slot;
        var old = *footprint;
        var p = default(Placement);
        var next = default(PreparedFootprint);
        var gain = sources->Alive.Pointer[slot] != 0 ? (sbyte)sources->Gain.Pointer[slot] : (sbyte)0;
        if (gain != 0 && TileBake.Place(sources->X.Pointer[slot], sources->Y.Pointer[slot], g->OriginX, g->OriginY,
                g->ScaleQ8, g->Size, StampCatalog.Get(sources->Stamp.Pointer[slot]), sources->Angle.Pointer[slot],
                sources->Scale.Pointer[slot], out p))
        {
            var x0 = Math.Max(p.X0, 0);
            var y0 = Math.Max(p.Y0, 0);
            var x1 = Math.Min(p.X1, g->Size);
            var y1 = Math.Min(p.Y1, g->Size);
            if (x1 > x0 && y1 > y0)
            {
                next = new PreparedFootprint
                {
                    X0 = (ushort)(x0 >> TileBake.TileBits), Y0 = (ushort)(y0 >> TileBake.TileBits),
                    X1 = (ushort)(((x1 - 1) >> TileBake.TileBits) + 1), Y1 = (ushort)(((y1 - 1) >> TileBake.TileBits) + 1),
                    Layer = sources->Layer.Pointer[slot], Gain = gain, Planes = (byte)(1 << (int)p.Plane)
                };
            }
        }

        if (old.Gain == next.Gain && old.Layer == next.Layer && SamePlacement(g->Prepared.Pointer + slot, &p)) return;
        if (old.Gain != 0) MarkFootprint(g, g->Prepared.Pointer + slot, in old);
        g->Prepared.Pointer[slot] = p;
        *footprint = next;
        if (next.Gain != 0) MarkFootprint(g, &p, in next);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SamePlacement(Placement* a, Placement* b)
        => a->V == b->V && a->Px == b->Px && a->Py == b->Py && a->Fx == b->Fx && a->Fy == b->Fy &&
           a->ExtentX == b->ExtentX && a->ExtentY == b->ExtentY && a->Cos == b->Cos && a->Sin == b->Sin &&
           a->Sampling == b->Sampling && a->Kind == b->Kind && a->Turned == b->Turned;

    private static void MarkFootprint(GridCtx* g, Placement* placement, in PreparedFootprint footprint)
    {
        var ld = EnsureDirty(g, footprint.Layer);
        for (var ty = footprint.Y0; ty < footprint.Y1; ty++)
        for (var tx = footprint.X0; tx < footprint.X1; tx++)
        {
            if (!TileBake.Touches(placement, tx << TileBake.TileBits, ty << TileBake.TileBits)) continue;
            MarkDirty(ld, ty * g->TilesPerSide + tx);
        }
    }

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    private static void BuildSourceBins(WorldCtx* w, GridCtx* g, byte layer)
    {
        var ld = g->Layers + layer;
        ld->Bins.Resize(g->TileCount);
        ld->Bins.Span.Clear();
        var footprints = g->Footprints.Pointer;
        var prepared = g->Prepared.Pointer;
        var bins = ld->Bins.Pointer;
        for (var slot = 0; slot < g->Footprints.Length; slot++)
        {
            var f = footprints + slot;
            if (f->Gain == 0 || f->Layer != layer) continue;
            for (var ty = f->Y0; ty < f->Y1; ty++)
            for (var tx = f->X0; tx < f->X1; tx++)
            {
                if (!TileBake.Touches(prepared + slot, tx << TileBake.TileBits, ty << TileBake.TileBits)) continue;
                var bin = bins + ty * g->TilesPerSide + tx;
                bin->Count++;
                bin->Planes |= f->Planes;
            }
        }

        var total = 0;
        for (var tile = 0; tile < g->TileCount; tile++)
        {
            var bin = bins + tile;
            bin->Start = total;
            total += bin->Count;
            bin->Count = 0;
        }

        ld->BinEntries.Resize(total);
        for (var slot = 0; slot < g->Footprints.Length; slot++)
        {
            var f = footprints + slot;
            if (f->Gain == 0 || f->Layer != layer) continue;
            for (var ty = f->Y0; ty < f->Y1; ty++)
            for (var tx = f->X0; tx < f->X1; tx++)
            {
                if (!TileBake.Touches(prepared + slot, tx << TileBake.TileBits, ty << TileBake.TileBits)) continue;
                var bin = bins + ty * g->TilesPerSide + tx;
                ld->BinEntries.Pointer[bin->Start + bin->Count++] = slot;
            }
        }
    }

    private static void CommitSourceSnapshot(WorldCtx* w)
    {
        if (w->Recording != 0)
        {
            var start = w->Journal.Length;
            w->Journal.Resize(start + w->Ops.Length);
            w->Ops.Span.CopyTo(w->Journal.Span.Slice(start));
        }

        for (var index = 0; index < w->Ops.Length; index++) w->Pending.Pointer[w->Ops.Pointer[index].Slot] = 0;
        w->Ops.Resize(0);
    }

    private static void ResolveSourceTile(GridCtx* g, byte layer, int tile)
    {
        var ld = g->Layers + layer;
        var bin = ld->Bins.Pointer + tile;
        var exists = ld->Pages.TryGet(tile, out var block);
        if (bin->Count == 0)
        {
            if (exists)
            {
                ld->Pages.Remove(tile);
                FreeBlock(block);
            }

            ld->Max.Update(g->TilesPerSide, tile, 0);
            ld->InDirty[tile] = 0;
            return;
        }

        if (!exists) block = NewHeader();
        var live = bin->Count > TileBake.SmoothFastLimit && (bin->Planes & 12) != 0
            ? ResolveSourceTileWide(g, ld, bin, tile, block)
            : ResolveSourceTileNarrow(g, ld, bin, tile, block);
        if (live)
        {
            if (!exists) ld->Pages.Put(tile, block);
            ld->Max.Update(g->TilesPerSide, tile, *(short*)(block + MaxOffset));
        }
        else
        {
            if (exists) ld->Pages.Remove(tile);
            FreeBlock(block);
            ld->Max.Update(g->TilesPerSide, tile, 0);
        }

        ld->InDirty[tile] = 0;
    }

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    private static bool ResolveSourceTileNarrow(GridCtx* g, LayerData* ld, SourceBin* bin, int tile, byte* block)
    {
        var differenceCount = (bin->Planes & 1) != 0 ? TileBake.DiffRows * TileBake.DiffPitch : 0;
        var denseCount = (bin->Planes & 2) != 0 ? Cells : 0;
        var tentCount = (bin->Planes & 4) != 0 ? Cells : 0;
        var bellCount = (bin->Planes & 8) != 0 ? Cells : 0;
        var impulseCount = bin->Count >= 16 ? tentCount + bellCount : 0;
        var bytes = differenceCount + denseCount + tentCount + bellCount + impulseCount;
        var scratch = stackalloc long[bytes];
        new Span<long>(scratch, bytes).Clear();
        var difference = differenceCount != 0 ? scratch : null;
        var dense = denseCount != 0 ? scratch + differenceCount : null;
        var tent = tentCount != 0 ? scratch + differenceCount + denseCount : null;
        var bell = bellCount != 0 ? scratch + differenceCount + denseCount + tentCount : null;
        var tentImpulses = impulseCount != 0 && tentCount != 0 ? scratch + differenceCount + denseCount + tentCount + bellCount : null;
        var bellImpulses = impulseCount != 0 && bellCount != 0
            ? scratch + differenceCount + denseCount + tentCount + bellCount + (tentCount != 0 ? Cells : 0) : null;
        var x = (tile % g->TilesPerSide) << TileBake.TileBits;
        var y = (tile / g->TilesPerSide) << TileBake.TileBits;
        var entries = ld->BinEntries.Pointer + bin->Start;
        byte impulses = 0;
        for (var i = 0; i < bin->Count; i++)
        {
            var slot = entries[i];
            impulses |= TileBake.EmitTransient(g->Prepared.Pointer + slot, g->Footprints.Pointer[slot].Gain, x, y,
                difference, dense, tent, bell, tentImpulses, bellImpulses);
        }

        return TileBake.ResolveTransient((bin->Planes & 1) != 0 ? difference : null,
            (bin->Planes & 2) != 0 ? dense : null, (bin->Planes & 4) != 0 ? tent : null,
            (bin->Planes & 8) != 0 ? bell : null, (impulses & 1) != 0 ? tentImpulses : null,
            (impulses & 2) != 0 ? bellImpulses : null, (short*)(block + PageOffset),
            (long*)(block + SumOffset), (short*)(block + MaxOffset), RowSumOf(block), RowMaxOf(block), (short*)(block + MinOffset));
    }

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    private static bool ResolveSourceTileWide(GridCtx* g, LayerData* ld, SourceBin* bin, int tile, byte* block)
    {
        var coreCount = TileBake.DiffRows * TileBake.DiffPitch + Cells;
        var core = stackalloc long[coreCount];
        var smooth = stackalloc WideSum[Cells * 4];
        new Span<long>(core, coreCount).Clear();
        new Span<WideSum>(smooth, Cells * 4).Clear();
        var x = (tile % g->TilesPerSide) << TileBake.TileBits;
        var y = (tile / g->TilesPerSide) << TileBake.TileBits;
        var entries = ld->BinEntries.Pointer + bin->Start;
        byte impulses = 0;
        for (var i = 0; i < bin->Count; i++)
        {
            var slot = entries[i];
            impulses |= TileBake.EmitTransientWide(g->Prepared.Pointer + slot, g->Footprints.Pointer[slot].Gain, x, y,
                core, core + TileBake.DiffRows * TileBake.DiffPitch, smooth, smooth + Cells, smooth + Cells * 2, smooth + Cells * 3);
        }

        return TileBake.ResolveTransientWide((bin->Planes & 1) != 0 ? core : null,
            (bin->Planes & 2) != 0 ? core + TileBake.DiffRows * TileBake.DiffPitch : null,
            (bin->Planes & 4) != 0 ? smooth : null, (bin->Planes & 8) != 0 ? smooth + Cells : null,
            (impulses & 1) != 0 ? smooth + Cells * 2 : null, (impulses & 2) != 0 ? smooth + Cells * 3 : null,
            (short*)(block + PageOffset), (long*)(block + SumOffset), (short*)(block + MaxOffset), RowSumOf(block), RowMaxOf(block), (short*)(block + MinOffset));
    }

    private static void PublishSnapshot(WorldCtx* w)
    {
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            for (var layer = 0; layer < w->LayerCount; layer++)
            {
                var ld = g->Layers + layer;
                for (var i = 0; i < ld->Dirty.Length; i++)
                {
                    var tile = ld->Dirty.Pointer[i];
                    ld->Epochs[tile] = w->Tick;
                    if (ld->Pages.TryGet(tile, out var block)) RefreshRows(block);
                }

                var changed = ld->Dirty;
                ld->Dirty = ld->Changed;
                ld->Dirty.Resize(0);
                ld->Changed = changed;
            }
        }
    }

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    internal static short SnapshotExcluded(GridCtx* g, byte layer, int cx, int cy, int excludedSlot)
    {
        if ((uint)cx >= (uint)g->Size || (uint)cy >= (uint)g->Size) return 0;
        var ld = g->Layers + layer;
        if (ld->Bins.Length == 0) return 0;
        var bin = ld->Bins.Pointer + (cy >> TileBake.TileBits) * g->TilesPerSide + (cx >> TileBake.TileBits);
        var entries = ld->BinEntries.Pointer + bin->Start;
        var core = 0L;
        var tent = default(WideSum);
        var bell = default(WideSum);
        for (var i = 0; i < bin->Count; i++)
        {
            var slot = entries[i];
            if (slot == excludedSlot) continue;
            var placement = g->Prepared.Pointer + slot;
            if (!placement->Reaches(cx, cy)) continue;
            SourceAt(placement, g->Footprints.Pointer[slot].Gain, cx, cy, out var integer, out var smooth);
            core += integer;
            if (placement->Plane == Plane.Tent) tent.Add(smooth);
            else if (placement->Plane == Plane.Bell) bell.Add(smooth);
        }

        return Saturate(core + tent.RoundQ40() + bell.RoundQ40());
    }
}
