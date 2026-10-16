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
        if (w->Recording == 0 || s->HotStamp.Pointer[slot] == w->RecordEpoch) return;

        s->HotStamp.Pointer[slot] = w->RecordEpoch;
        var journaled = w->Journal.Length;
        w->Journal.Resize(journaled + 1);
        w->Journal.Pointer[journaled] = w->Pending.Pointer[n];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BeforeWrite(WorldCtx* w, SourceColumns* s, int slot)
    {
        if (s->Mark[slot] != w->MarkEpoch) SnapshotPending(w, s, slot);
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
            if (i + PrefetchAhead < count) PrefetchRecordTarget(w, slots[i + PrefetchAhead]);
#endif
            var entry = EntryOf(s, slots[i]);
            SweepEntryAt(w, &entry);
        }

        w->Changed.Resize(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static DepositRecord TargetOf(WorldCtx* w, SweepEntry* entry)
    {
        var layer = (byte)entry->Meta;
        var alive = (byte)(entry->Meta >> 24);
        DepositRecord target = default;
        if (alive != 0)
        {
            target.Layer = layer;
            target.Stamp = (byte)(entry->Meta >> 8);
            target.Gain = (sbyte)(entry->Meta >> 16);
            target.Alive = alive;
            target.Angle = (ushort)(entry->Shape & w->Recipes[layer].TurnMask);
            target.Scale = (ushort)(entry->Shape >> 16);
        }

        return target;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static DepositRecord KeyOf(WorldCtx* w, GridCtx* g, SweepEntry* entry, in DepositRecord target)
    {
        var key = target;
        if (key.Alive != 0)
        {
            var mask = w->Recipes[key.Layer].AnchorMask;
            key.Ax = TileBake.AnchorQ8(entry->X, g->OriginX, g->ScaleQ8, mask);
            key.Ay = TileBake.AnchorQ8(entry->Y, g->OriginY, g->ScaleQ8, mask);
        }

        return key;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Same(DepositRecord* rec, in DepositRecord key)
    {
        fixed (DepositRecord* k = &key)
            return *(ulong*)rec == *(ulong*)k && ((ulong*)rec)[1] == ((ulong*)k)[1];
    }

    private static void SweepEntryAt(WorldCtx* w, SweepEntry* entry)
    {
        var target = TargetOf(w, entry);
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            if (g->ScaleQ8 == 0) continue;

            var rec = RecordOf(g, entry->Slot);
            var key = KeyOf(w, g, entry, in target);
            if (Same(rec, in key)) continue;

            EmitDelta(w, gi, in *rec, -1);
            EmitDelta(w, gi, in key, 1);
            *rec = key;
        }
    }

    private static void EmitDelta(WorldCtx* w, int gi, in DepositRecord record, int sign)
    {
        var gain = record.Gain * sign;
        if (gain == 0) return;

        var g = w->Grids + gi;
        var v = StampCatalog.Get(record.Stamp);
        if (v->Kind == StampKind.ConstantRectangle && record.Angle == 0)
        {
            EmitBoxFast(g, in record, v, gain);
            return;
        }

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
        if (KernelFactory.TryGetTaps(w, gi, record.Stamp, record.Angle, precision, &p, out var taps))
        {
            EmitKernelTaps(g, ld, in taps, p.Px, p.Py, gain);
            return;
        }

        EmitFallback(g, ld, &p, gain);
    }

    private static void EmitBoxFast(GridCtx* g, in DepositRecord record, StampVariant* v, int gain)
    {
        var sampling = (int)Math.Min((long)g->ScaleQ8 * record.Scale >> 8, int.MaxValue);
        if (sampling == 0) return;

        TileBake.SpanAnchored(record.Ax, v->OriginQ8X, v->Width, sampling, g->Size, true, TileBake.MaxExtent,
            out var px, out var fx, out var extentX, out var x0, out var x1);
        TileBake.SpanAnchored(record.Ay, v->OriginQ8Y, v->Height, sampling, g->Size, true, TileBake.MaxExtent,
            out var py, out var fy, out var extentY, out var y0, out var y1);
        var cx0 = Math.Max(x0, 0);
        var cy0 = Math.Max(y0, 0);
        var cx1 = Math.Min(x1, g->Size);
        var cy1 = Math.Min(y1, g->Size);
        if (cx1 <= cx0 || cy1 <= cy0) return;

        var ld = EnsureDirty(g, record.Layer);
        if (ld->Epochs == null) EnsureEpochs(g, ld);
        if (cx0 == x0 && cy0 == y0 && cx1 == x1 && cy1 == y1 && TileBake.LanesSupported &&
            EmitBoxNarrow(g, ld, px, py, fx, fy, extentX, extentY, v->Constant, gain))
            return;

        EmitBoxDelta(g, ld, px, py, fx, fy, extentX, extentY, cx0, cy0, cx1, cy1, v->Constant, gain);
    }

    private static bool EmitBoxNarrow(
        GridCtx* g, LayerData* ld, int px, int py, int fx, int fy, int extentX, int extentY, int constant, int gain)
    {
        var endX = fx + extentX;
        var endY = fy + extentY;
        var fullX = endX >> 8;
        var fullY = endY >> 8;
        var tailX = endX & 255;
        var tailY = endY & 255;
        var cellsX = fullX + (tailX != 0 ? 1 : 0);
        var cellsY = fullY + (tailY != 0 ? 1 : 0);
        if ((fx != 0 && fullX == 0) || (fy != 0 && fullY == 0) || cellsX > TileBake.LaneCount) return false;

        var localX = px & (TileBake.TileSize - 1);
        var localY = py & (TileBake.TileSize - 1);
        if (localX + cellsX > TileBake.TileSize || localY + cellsY > TileBake.TileSize) return false;

        var column = Math.Min(localX, TileBake.TileSize - TileBake.LaneCount);
        var weights = stackalloc int[TileBake.LaneCount];
        var lanes = stackalloc int[TileBake.LaneCount];
        var leadX = fx != 0 ? 1 : 0;
        var leadWeightX = 256 - fx;
        TileBake.LaneWeights3(
            column,
            localX, localX + leadX, leadWeightX,
            localX + leadX, localX + fullX, 256,
            localX + fullX, localX + fullX + (tailX != 0 ? 1 : 0), tailX,
            weights);
        var tile = (py >> TileBake.TileBits) * g->TilesPerSide + (px >> TileBake.TileBits);
        var leadY = fy != 0 ? 1 : 0;
        int* plane = null;
        for (var band = 0; band < 3; band++)
        {
            int rowLo;
            int rowHi;
            int weightY;
            if (band == 0)
            {
                rowLo = localY;
                rowHi = localY + leadY;
                weightY = 256 - fy;
            }
            else if (band == 1)
            {
                rowLo = localY + leadY;
                rowHi = localY + fullY;
                weightY = 256;
            }
            else
            {
                rowLo = localY + fullY;
                rowHi = rowLo + (tailY != 0 ? 1 : 0);
                weightY = tailY;
            }

            if (rowHi <= rowLo || !TileBake.BandLanes(weights, constant * weightY, gain, lanes)) continue;

            if (plane == null)
            {
                var set = EnsureSet(g, ld, tile);
                plane = ld->Store.Cells(*set).I;
                DirtyIndex(g, ld, tile);
            }

            TileBake.AddRows(plane, rowLo, rowHi, column, lanes);
        }

        if (plane != null)
        {
            var rect = &ld->Rects.Pointer[ld->InDirty[tile] - 1];
            if (localX < rect->X0) rect->X0 = localX;
            if (localY < rect->Y0) rect->Y0 = localY;
            if (localX + cellsX > rect->X1) rect->X1 = localX + cellsX;
            if (localY + cellsY > rect->Y1) rect->Y1 = localY + cellsY;
        }

        return true;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static void EmitBoxDelta(
        GridCtx* g, LayerData* ld, int px, int py, int fx, int fy, int extentX, int extentY,
        int cx0, int cy0, int cx1, int cy1, int constant, int gain)
    {
        var xLo = stackalloc int[3];
        var xHi = stackalloc int[3];
        var xWeight = stackalloc int[3];
        var yLo = stackalloc int[3];
        var yHi = stackalloc int[3];
        var yWeight = stackalloc int[3];
        var tps = g->TilesPerSide;
        var lanes = stackalloc int[TileBake.LaneCount];
        var laneWeights = stackalloc int[TileBake.LaneCount];
        for (var ty = cy0 >> TileBake.TileBits; ty <= (cy1 - 1) >> TileBake.TileBits; ty++)
        for (var tx = cx0 >> TileBake.TileBits; tx <= (cx1 - 1) >> TileBake.TileBits; tx++)
        {
            var tileX0 = tx << TileBake.TileBits;
            var tileY0 = ty << TileBake.TileBits;
            var liveX = TileBake.ClipBands(px, fx, extentX, tileX0, tileX0 + TileBake.TileSize, xLo, xHi, xWeight);
            var liveY = TileBake.ClipBands(py, fy, extentY, tileY0, tileY0 + TileBake.TileSize, yLo, yHi, yWeight);
            if (liveX == 0 || liveY == 0) continue;

            var tile = ty * tps + tx;
            var narrow = TileBake.LanesSupported && xHi[liveX - 1] - xLo[0] <= TileBake.LaneCount;
            var column = Math.Min(xLo[0], TileBake.TileSize - TileBake.LaneCount);
            var lead = xLo[0] - column;
            var disjoint = narrow;
            if (narrow)
            {
                for (var x = 1; x < liveX; x++)
                    if (xLo[x] < xHi[x - 1]) disjoint = false;
                if (disjoint) TileBake.LaneWeights(xLo, xHi, xWeight, liveX, column, laneWeights);
            }

            int* plane = null;
            for (var y = 0; y < liveY; y++)
            {
                var weightY = yWeight[y];
                if (disjoint)
                {
                    if (!TileBake.BandLanes(laneWeights, constant * weightY, gain, lanes)) continue;

                    if (plane == null)
                    {
                        var set = EnsureSet(g, ld, tile);
                        plane = ld->Store.Cells(*set).I;
                        DirtyIndex(g, ld, tile);
                    }

                    TileBake.AddRows(plane, yLo[y], yHi[y], column, lanes);
                    continue;
                }

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
                                var set = EnsureSet(g, ld, tile);
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
                                var set = EnsureSet(g, ld, tile);
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
                var transient = IsTransient(w, l);
                if (transient)
                {
                    FlushHistogram(w, gi, g, ld, (byte)l);
                    RestartStale(g, ld);
                }

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
                    if ((outcome & ResolveOutcome.Empty) != 0 && (transient || ld->Store.PlanesZero(*set)))
                    {
                        DropPage(ld, tile);
                        ld->Max.Update(tps, tile, 0);
                    }
                    else
                    {
                        ld->Max.Update(tps, tile, ld->Store.Cells(*set).Summary->Max);
                        if (transient) RetireTransientTile(ld, set, tile);
                    }

                    if ((outcome & ResolveOutcome.Changed) != 0) dirty[kept++] = tile;
                }

                ld->Dirty.Resize(kept);
                ld->Rects.Resize(0);
            }
        }
    }

    private static void RestartStale(GridCtx* g, LayerData* ld)
    {
        for (var i = 0; i < ld->Stale.Length; i++)
        {
            var dirty = DirtyIndex(g, ld, ld->Stale.Pointer[i]);
            ld->Rects.Pointer[dirty] = new TileRect { X0 = 0, Y0 = 0, X1 = TileBake.TileSize, Y1 = TileBake.TileSize };
        }

        ld->Stale.Resize(0);
    }

    private static void RetireTransientTile(LayerData* ld, TilePlanes* set, int tile)
    {
        var cells = ld->Store.Cells(*set);
        new Span<int>(cells.I, TileBake.TileSize * TileBake.TileSize).Clear();
        if (cells.T != null) new Span<long>(cells.T, TileBake.TileSize * TileBake.TileSize).Clear();
        if (cells.B != null) new Span<long>(cells.B, TileBake.TileSize * TileBake.TileSize).Clear();
        var n = ld->Stale.Length;
        ld->Stale.Resize(n + 1);
        ld->Stale.Pointer[n] = tile;
    }
}
