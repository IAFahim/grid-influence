using System.Runtime.CompilerServices;

namespace Gi;

public static unsafe partial class World
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static DepositRecord* RecordOf(GridCtx* g, int slot) => g->Records.Pointer + slot;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SnapshotPending(WorldCtx* w, SourceColumns* s, int slot)
    {
        var n = w->Pending.Length;
        w->Pending.Resize(n + 1);
        w->Pending.Pointer[n] = new JournalEntry
        {
            Slot = slot,
            X = s->X[slot],
            Y = s->Y[slot],
            Stamp = s->Stamp[slot],
            Layer = s->Layer[slot],
            Gain = s->Gain[slot],
            Alive = s->Alive[slot],
            Angle = s->Angle[slot],
            Scale = s->Scale[slot],
            Gen = s->Gen[slot],
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BeforeWrite(WorldCtx* w, SourceColumns* s, int slot)
    {
        if (s->Mark[slot] != w->MarkEpoch) SnapshotPending(w, s, slot);
    }

    private static void BeforeWriteState(WorldCtx* w, SourceColumns* s, int slot, in SourceBatchState state)
    {
        if (s->Mark[slot] != w->MarkEpoch)
        {
            var n = w->Pending.Length;
            w->Pending.Resize(n + 1);
            w->Pending.Pointer[n] = new JournalEntry
            {
                Slot = slot,
                X = state.PositionX,
                Y = state.PositionY,
                Stamp = state.Stamp,
                Layer = state.Layer,
                Gain = state.Gain,
                Alive = state.Alive,
                Angle = state.Angle,
                Scale = state.Scale,
                Gen = state.Generation,
            };
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MarkChanged(WorldCtx* w, SourceColumns* s, int slot)
    {
        if (s->Mark[slot] == w->MarkEpoch) return;

        s->Mark[slot] = w->MarkEpoch;
        var n = w->Changed.Length;
        w->Changed.Resize(n + 1);
        w->Changed.Pointer[n] = slot;
    }

    private static void SweepChanged(WorldCtx* w)
    {
        var s = &w->Sources;
        var slots = w->Changed.Pointer;
        var count = w->Changed.Length;
        for (var i = 0; i < count; i++)
        {
#if NET
            if (i + PrefetchAhead < count) PrefetchTarget(w, s, slots[i + PrefetchAhead]);
#endif
            var slot = slots[i];
            var layer = s->Layer[slot];
            var stamp = s->Stamp[slot];
            var alive = s->Alive[slot];
            var gain = (sbyte)s->Gain[slot];
            var scale = s->Scale[slot];
            var x = s->X[slot];
            var y = s->Y[slot];
            var maskedAngle = (ushort)(s->Angle[slot] & w->Recipes[layer].TurnMask);

            DepositRecord target = default;
            if (alive != 0)
            {
                target.Layer = layer;
                target.Stamp = stamp;
                target.Gain = gain;
                target.Alive = alive;
                target.Angle = maskedAngle;
                target.Scale = scale;
            }

            for (var gi = 0; gi < w->GridCount; gi++)
            {
                var g = w->Grids + gi;
                if (g->ScaleQ8 == 0) continue;

                var rec = RecordOf(g, slot);
                var key = target;
                if (key.Alive != 0)
                {
                    var mask = w->Recipes[layer].AnchorMask;
                    key.Ax = TileBake.AnchorQ8(x, g->OriginX, g->ScaleQ8, mask);
                    key.Ay = TileBake.AnchorQ8(y, g->OriginY, g->ScaleQ8, mask);
                }

                if (*(ulong*)rec == *(ulong*)&key && ((ulong*)rec)[1] == ((ulong*)&key)[1]) continue;

                EmitDelta(w, gi, in *rec, -1);
                EmitDelta(w, gi, in key, 1);
                *rec = key;
            }
        }

        w->Changed.Resize(0);
    }

    private static void EmitDelta(WorldCtx* w, int gi, in DepositRecord record, int sign)
    {
        var gain = record.Gain * sign;
        if (gain == 0) return;

        var g = w->Grids + gi;
        var v = StampCatalog.Get(record.Stamp);
        var precision = LayerPrecision(w, record.Layer);
        if (!TileBake.PlaceAnchored(record.Ax, record.Ay, g->ScaleQ8, g->Size, v, record.Angle, record.Scale, precision, out var p))
            return;

        var cx0 = Math.Max(p.X0, 0);
        var cy0 = Math.Max(p.Y0, 0);
        var cx1 = Math.Min(p.X1, g->Size);
        var cy1 = Math.Min(p.Y1, g->Size);
        if (cx1 <= cx0 || cy1 <= cy0) return;

        var ld = EnsureDirty(g, record.Layer);
        if (ld->Epochs == null)
        {
            var placed = p;
            for (var ty = cy0 >> TileBake.TileBits; ty <= (cy1 - 1) >> TileBake.TileBits && ld->Epochs == null; ty++)
            for (var tx = cx0 >> TileBake.TileBits; tx <= (cx1 - 1) >> TileBake.TileBits; tx++)
                if (TileBake.Touches(&placed, tx << TileBake.TileBits, ty << TileBake.TileBits))
                {
                    EnsureEpochs(g, ld);
                    break;
                }
        }
        if (p.Plane == Plane.Difference)
        {
            EmitBoxDelta(g, ld, &p, gain);
            return;
        }

        if (KernelFactory.TryGetTaps(w, gi, record.Stamp, record.Angle, precision, &p, out var taps))
        {
            EmitKernelTaps(g, ld, in taps, p.Px, p.Py, gain);
            return;
        }

        EmitFallback(g, ld, &p, gain);
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static void EmitBoxDelta(GridCtx* g, LayerData* ld, Placement* p, int gain)
    {
        var xLo = stackalloc int[3];
        var xHi = stackalloc int[3];
        var xWeight = stackalloc int[3];
        var yLo = stackalloc int[3];
        var yHi = stackalloc int[3];
        var yWeight = stackalloc int[3];
        var cx0 = Math.Max(p->X0, 0);
        var cy0 = Math.Max(p->Y0, 0);
        var cx1 = Math.Min(p->X1, g->Size);
        var cy1 = Math.Min(p->Y1, g->Size);
        var tps = g->TilesPerSide;
        var constant = p->V->Constant;
        var lanes = stackalloc int[TileBake.LaneCount];
        for (var ty = cy0 >> TileBake.TileBits; ty <= (cy1 - 1) >> TileBake.TileBits; ty++)
        for (var tx = cx0 >> TileBake.TileBits; tx <= (cx1 - 1) >> TileBake.TileBits; tx++)
        {
            var tileX0 = tx << TileBake.TileBits;
            var tileY0 = ty << TileBake.TileBits;
            var liveX = TileBake.ClipBands(p->Px, p->Fx, p->ExtentX, tileX0, tileX0 + TileBake.TileSize, xLo, xHi, xWeight);
            var liveY = TileBake.ClipBands(p->Py, p->Fy, p->ExtentY, tileY0, tileY0 + TileBake.TileSize, yLo, yHi, yWeight);
            if (liveX == 0 || liveY == 0) continue;

            var tile = ty * tps + tx;
            var narrow = TileBake.LanesSupported && xHi[liveX - 1] - xLo[0] <= TileBake.LaneCount;
            var column = Math.Min(xLo[0], TileBake.TileSize - TileBake.LaneCount);
            var lead = xLo[0] - column;
            int* plane = null;
            for (var y = 0; y < liveY; y++)
            {
                var weightY = yWeight[y];
                if (narrow)
                {
                    new Span<int>(lanes, TileBake.LaneCount).Clear();
                    var any = 0;
                    for (var x = 0; x < liveX; x++)
                    {
                        var value = TileBake.RoundQ16(constant * xWeight[x] * weightY) * gain;
                        any |= value;
                        for (var lane = xLo[x] - xLo[0] + lead; lane < xHi[x] - xLo[0] + lead; lane++) lanes[lane] += value;
                    }

                    if (any == 0) continue;

                    if (plane == null)
                    {
                        var set = EnsureSet(g, ld, tile);
                        plane = ld->Store.Cells(*set).I;
                        DirtyIndex(g, ld, tile);
                    }

                    TileBake.AddRows(plane, yLo[y], yHi[y], column, lanes);
                    continue;
                }

                for (var x = 0; x < liveX; x++)
                {
                    var value = TileBake.RoundQ16(constant * xWeight[x] * weightY) * gain;
                    if (value == 0) continue;

                    if (plane == null)
                    {
                        var set = EnsureSet(g, ld, tile);
                        plane = ld->Store.Cells(*set).I;
                        DirtyIndex(g, ld, tile);
                    }

                    var row0 = yLo[y];
                    var row1 = yHi[y];
                    var col0 = xLo[x];
                    var col1 = xHi[x];
                    for (var row = row0; row < row1; row++)
                        TileBake.AddConstant(plane + row * TileBake.TileSize, col0, col1, value);
                }
            }

            if (plane != null)
            {
                var rect = &ld->Rects.Pointer[ld->InDirty[tile] - 1];
                if (xLo[0] < rect->X0) rect->X0 = xLo[0];
                if (yLo[0] < rect->Y0) rect->Y0 = yLo[0];
                if (xHi[liveX - 1] > rect->X1) rect->X1 = xHi[liveX - 1];
                if (yHi[liveY - 1] > rect->Y1) rect->Y1 = yHi[liveY - 1];
            }
        }
    }

    private static void EmitKernelTaps(GridCtx* g, LayerData* ld, in KernelTaps taps, int px, int py, int gain)
    {
        var kx0 = px + taps.OriginX;
        var ky0 = py + taps.OriginY;
        var kx1 = kx0 + taps.Width;
        var ky1 = ky0 + taps.Height;
        if (kx1 <= 0 || ky1 <= 0 || kx0 >= g->Size || ky0 >= g->Size) return;

        var tx0 = Math.Max(kx0, 0) >> TileBake.TileBits;
        var ty0 = Math.Max(ky0, 0) >> TileBake.TileBits;
        var tx1 = Math.Min(kx1 - 1, g->Size - 1) >> TileBake.TileBits;
        var ty1 = Math.Min(ky1 - 1, g->Size - 1) >> TileBake.TileBits;
        var tps = g->TilesPerSide;
        var size = TileBake.TileSize;
        var extents = taps.RowExtents;
        var ints = taps.Cells;
        var q40 = taps.Q40;
        var longPlane = taps.Plane == Plane.Tent || taps.Plane == Plane.Bell;

        for (var ty = ty0; ty <= ty1; ty++)
        {
            var ry0 = Math.Max((ty << TileBake.TileBits) - ky0, 0);
            var ry1 = Math.Min((ty << TileBake.TileBits) + size - ky0, taps.Height);
            for (var tx = tx0; tx <= tx1; tx++)
            {
                var tileX0 = tx << TileBake.TileBits;
                var tileY0 = ty << TileBake.TileBits;
                var tile = ty * tps + tx;
                TilePlanes* set = null;
                var dirty = -1;
                long* plane = null;
                int* ints32 = null;

                for (var ry = ry0; ry < ry1; ry++)
                {
                    var first = extents[2 * ry];
                    var last = extents[2 * ry + 1];
                    var kxA = Math.Max(first, tileX0 - kx0);
                    var kxB = Math.Min(last, tileX0 + size - kx0);
                    if (kxB <= kxA) continue;

                    var cy = ky0 + ry;
                    var ly = cy - tileY0;
                    if (longPlane)
                    {
                        var row = q40 + ry * taps.Width;
                        for (var kx = kxA; kx < kxB; kx++)
                        {
                            var value = row[kx];
                            if (value == 0) continue;

                            if (plane == null)
                            {
                                set = EnsureSet(g, ld, tile);
                                dirty = DirtyIndex(g, ld, tile);
                                plane = taps.Plane == Plane.Tent ? ld->Store.EnsureT(ref *set) : ld->Store.EnsureB(ref *set);
                            }

                            var lx = kx0 + kx - tileX0;
                            plane[ly * size + lx] += value * gain;
                            GrowRect(ld, dirty, lx, ly);
                        }
                    }
                    else
                    {
                        var row = ints + ry * taps.Width;
                        for (var kx = kxA; kx < kxB; kx++)
                        {
                            var value = row[kx];
                            if (value == 0) continue;

                            if (ints32 == null)
                            {
                                set = EnsureSet(g, ld, tile);
                                dirty = DirtyIndex(g, ld, tile);
                                ints32 = ld->Store.Cells(*set).I;
                            }

                            var lx = kx0 + kx - tileX0;
                            ints32[ly * size + lx] += value * gain;
                            GrowRect(ld, dirty, lx, ly);
                        }
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GrowRect(LayerData* ld, int dirty, int lx, int ly)
    {
        var rect = &ld->Rects.Pointer[dirty];
        if (lx < rect->X0) rect->X0 = lx;
        if (ly < rect->Y0) rect->Y0 = ly;
        if (lx + 1 > rect->X1) rect->X1 = lx + 1;
        if (ly + 1 > rect->Y1) rect->Y1 = ly + 1;
    }

    private static void EmitFallback(GridCtx* g, LayerData* ld, Placement* p, int gain)
    {
        var cx0 = Math.Max(p->X0, 0);
        var cy0 = Math.Max(p->Y0, 0);
        var cx1 = Math.Min(p->X1, g->Size);
        var cy1 = Math.Min(p->Y1, g->Size);
        var tps = g->TilesPerSide;
        for (var ty = cy0 >> TileBake.TileBits; ty <= (cy1 - 1) >> TileBake.TileBits; ty++)
        for (var tx = cx0 >> TileBake.TileBits; tx <= (cx1 - 1) >> TileBake.TileBits; tx++)
        {
            var tileX0 = tx << TileBake.TileBits;
            var tileY0 = ty << TileBake.TileBits;
            if (!TileBake.Touches(p, tileX0, tileY0)) continue;

            var tile = ty * tps + tx;
            var set = EnsureSet(g, ld, tile);
            var cells = ld->Store.Cells(*set);
            switch (p->Plane)
            {
                case Plane.Dense when p->Turned:
                    TileBake.EmitTurnedDense(cells.I, tileX0, tileY0, p, gain);
                    break;
                case Plane.Dense:
                    TileBake.EmitRaster(cells.I, tileX0, tileY0, p->Px, p->Py, p->Fx, p->Fy,
                        p->X1, p->Y1, p->Sampling, p->V, gain);
                    break;
                case Plane.Tent when p->Turned:
                    TileBake.EmitTurnedSmooth(ld->Store.EnsureT(ref *set), tileX0, tileY0, p, gain);
                    break;
                case Plane.Tent:
                    TileBake.EmitTent(ld->Store.EnsureT(ref *set), tileX0, tileY0, p->Px, p->Py, p->Fx, p->Fy,
                        p->ExtentX, p->ExtentY, p->V, gain);
                    break;
                case Plane.Bell when p->Turned:
                    TileBake.EmitTurnedSmooth(ld->Store.EnsureB(ref *set), tileX0, tileY0, p, gain);
                    break;
                default:
                    TileBake.EmitBell(ld->Store.EnsureB(ref *set), tileX0, tileY0, p->Px, p->Py, p->Fx, p->Fy,
                        p->ExtentX, p->ExtentY, p->V, gain);
                    break;
            }

            var dirty = DirtyIndex(g, ld, tile);
            var rect = &ld->Rects.Pointer[dirty];
            rect->X0 = 0;
            rect->Y0 = 0;
            rect->X1 = TileBake.TileSize;
            rect->Y1 = TileBake.TileSize;
        }
    }

    private static void PublishDirty(WorldCtx* w)
    {
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            var tps = g->TilesPerSide;
            for (var l = 0; l < w->LayerCount; l++)
            {
                if (IsDerived(w, l)) continue;

                var ld = g->Layers + l;
                if (ld->InDirty == null) continue;

                var dirty = ld->Dirty.Pointer;
                var rects = ld->Rects.Pointer;
                var kept = 0;
                for (var i = 0; i < ld->Dirty.Length; i++)
                {
                    var tile = dirty[i];
                    ld->InDirty[tile] = 0;
                    var set = ld->SetOf(tile);
                    if (set == null) continue;

                    var rect = rects + i;
                    var outcome = TileBake.Publish(&ld->Store, set, rect->X0, rect->Y0, rect->X1, rect->Y1, w->Tick);
                    if ((outcome & ResolveOutcome.Empty) != 0)
                    {
                        DropPage(ld, tile);
                        ld->Max.Update(tps, tile, 0);
                    }
                    else
                    {
                        ld->Max.Update(tps, tile, ld->Store.Cells(*set).Summary->Max);
                    }

                    if ((outcome & ResolveOutcome.Changed) != 0) dirty[kept++] = tile;
                }

                ld->Dirty.Resize(kept);
                ld->Rects.Resize(0);
            }
        }
    }
}
