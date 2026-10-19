using System.Buffers.Binary;

namespace Gi;

internal unsafe ref struct SnapshotWriter
{
    private Span<byte> _buffer;
    private int _cursor;

    public SnapshotWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        _cursor = 0;
    }

    public readonly int Written => _cursor;

    public void U32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.Slice(_cursor, 4), value);
        _cursor += 4;
    }

    public void U64(ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.Slice(_cursor, 8), value);
        _cursor += 8;
    }

    public void U16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(_cursor, 2), value);
        _cursor += 2;
    }

    public void I16(short value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(_buffer.Slice(_cursor, 2), value);
        _cursor += 2;
    }

    public void I32(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.Slice(_cursor, 4), value);
        _cursor += 4;
    }

    public void I64(long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.Slice(_cursor, 8), value);
        _cursor += 8;
    }

    public void F32(float value)
    {
        var bits = *(int*)&value;
        I32(bits);
    }

    public void Byte(byte value)
    {
        _buffer[_cursor] = value;
        _cursor++;
    }

    public void I8(sbyte value)
    {
        _buffer[_cursor] = *(byte*)&value;
        _cursor++;
    }

    public void Copy(byte* source, int bytes)
    {
        new Span<byte>(source, bytes).CopyTo(_buffer.Slice(_cursor, bytes));
        _cursor += bytes;
    }
}

internal unsafe ref struct SnapshotReader
{
    private ReadOnlySpan<byte> _buffer;
    private int _cursor;

    public SnapshotReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _cursor = 0;
    }

    public readonly bool AtEnd => _cursor == _buffer.Length;

    private readonly int Remaining => _buffer.Length - _cursor;

    public bool U32(out uint value)
    {
        value = 0;
        if (Remaining < 4) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Slice(_cursor, 4));
        _cursor += 4;
        return true;
    }

    public bool U64(out ulong value)
    {
        value = 0;
        if (Remaining < 8) return false;
        value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.Slice(_cursor, 8));
        _cursor += 8;
        return true;
    }

    public bool U16(out ushort value)
    {
        value = 0;
        if (Remaining < 2) return false;
        value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_cursor, 2));
        _cursor += 2;
        return true;
    }

    public bool I16(out short value)
    {
        value = 0;
        if (Remaining < 2) return false;
        value = BinaryPrimitives.ReadInt16LittleEndian(_buffer.Slice(_cursor, 2));
        _cursor += 2;
        return true;
    }

    public bool I32(out int value)
    {
        value = 0;
        if (Remaining < 4) return false;
        value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_cursor, 4));
        _cursor += 4;
        return true;
    }

    public bool I64(out long value)
    {
        value = 0;
        if (Remaining < 8) return false;
        value = BinaryPrimitives.ReadInt64LittleEndian(_buffer.Slice(_cursor, 8));
        _cursor += 8;
        return true;
    }

    public bool F32(out float value)
    {
        value = 0f;
        if (!I32(out var bits)) return false;
        value = *(float*)&bits;
        return true;
    }

    public bool Byte(out byte value)
    {
        value = 0;
        if (Remaining < 1) return false;
        value = _buffer[_cursor];
        _cursor++;
        return true;
    }

    public bool I8(out sbyte value)
    {
        value = 0;
        if (!Byte(out var raw)) return false;
        value = *(sbyte*)&raw;
        return true;
    }

    public bool Skip(int bytes)
    {
        if (Remaining < bytes) return false;
        _cursor += bytes;
        return true;
    }

    public bool Copy(byte* destination, int bytes)
    {
        if (Remaining < bytes) return false;
        _buffer.Slice(_cursor, bytes).CopyTo(new Span<byte>(destination, bytes));
        _cursor += bytes;
        return true;
    }
}

public static unsafe partial class World
{
    private const uint SnapshotMagic = 0x4E534947u;
    private const ushort SnapshotVersion = 1;
    private const int SnapshotHeaderBytes = 32;
    private const int SnapshotRecipeBytes = 32;
    private const int SnapshotGridBytes = 13;
    private const int SnapshotStampBytes = 9;
    private const int SnapshotSlotBytes = 35;
    private const int SnapshotRecordBytes = 16;
    private const int SnapshotTileBytes = 30;

    private const byte SnapshotHasI = 1 << 0;
    private const byte SnapshotHasT = 1 << 1;
    private const byte SnapshotHasB = 1 << 2;
    private const byte SnapshotHasCarryI = 1 << 3;
    private const byte SnapshotHasHighT = 1 << 4;
    private const byte SnapshotHasLowT = 1 << 5;
    private const byte SnapshotHasHighB = 1 << 6;
    private const byte SnapshotHasLowB = 1 << 7;

    public static int SnapshotSize(byte world)
    {
        var w = GetContext(world);
        if (!SnapshotQuiescent(w)) return 0;

        var usage = stackalloc byte[StampCatalog.MaxStamps];
        var size = (long)SnapshotHeaderBytes;
        size += (long)SnapshotRecipeBytes * w->LayerCount;
        size += (long)SnapshotGridBytes * w->GridCount;
        size += 1 + (long)SnapshotStampBytes * SnapshotStampMap(w, usage);
        var s = &w->Sources;
        size += 4 + (long)SnapshotSlotBytes * s->Count;
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            size += 4 + (long)SnapshotRecordBytes * s->Count;
            for (var l = 0; l < w->LayerCount; l++) size += SnapshotLayerBytes(g, l);
        }

        return size > int.MaxValue ? 0 : (int)size;
    }

    public static bool TrySnapshot(byte world, Span<byte> destination, out int written)
    {
        written = 0;
        var size = SnapshotSize(world);
        if (size == 0 || destination.Length < size) return false;

        WriteSnapshot(GetContext(world), destination);
        written = size;
        return true;
    }

    public static bool TryRestore(byte world, ReadOnlySpan<byte> data)
    {
        var w = GetContext(world);
        if (w == null) return false;
        if (w->GridCount != 0 || w->LayerCount != 0 || w->Sources.Chunks != 0 || w->Recording != 0) return false;
        if (!SnapshotValidates(data)) return false;

        RestoreApply(world, w, data);
        Runtime.WorldRevisions[world]++;
        return true;
    }

    private static bool SnapshotQuiescent(WorldCtx* w)
        => w != null && w->Pending.Length == 0 && w->Changed.Length == 0 && w->Recording == 0;

    private static int SnapshotStampMap(WorldCtx* w, byte* usage)
    {
        new Span<byte>(usage, StampCatalog.MaxStamps).Clear();
        var s = &w->Sources;
        var count = 0;
        for (var i = 0; i < s->Count; i++)
        {
            if (s->Alive[i] == 0) continue;
            var stamp = s->Stamp[i];
            if (usage[stamp] != 0) continue;

            usage[stamp] = 1;
            count++;
        }

        return count;
    }

    private static long SnapshotLayerBytes(GridCtx* g, int layer)
    {
        var ld = g->Layers + layer;
        var derived = ld->Derived != 0;
        var size = 4L;
        if (ld->Directory != null)
            for (var tile = 0; tile < g->TileCount; tile++)
            {
                var index = ld->Directory[tile];
                if (index < 0) continue;

                var set = (TilePlanes*)ld->Sets.Unit(index);
                var summary = (TileSummary*)ld->Store.Summaries.Unit(set->Summary);
                size += SnapshotTileBytes + TileSummary.Bytes + PlaneStore.PageUnit;
                if (!derived) size += PlaneStore.Int32Unit;
                if (set->T >= 0) size += PlaneStore.Int64Unit;
                if (set->B >= 0) size += PlaneStore.Int64Unit;
                size += SnapshotWideCount(summary) * PlaneStore.Int64Unit;
            }

        size += 4 + 4L * ld->Changed.Length;
        size += 4 + 4L * ld->Stale.Length;
        size += 4;
        if (ld->Epochs != null && ld->Directory != null)
            for (var tile = 0; tile < g->TileCount; tile++)
                if (ld->Directory[tile] < 0 && ld->Epochs[tile] != 0)
                    size += 8;

        return size;
    }

    private static int SnapshotWideCount(TileSummary* summary)
    {
        var count = 0;
        if (summary->CarryI != 0) count++;
        if (summary->HighT != 0) count++;
        if (summary->LowT != 0) count++;
        if (summary->HighB != 0) count++;
        if (summary->LowB != 0) count++;
        return count;
    }

    private static void WriteSnapshot(WorldCtx* w, Span<byte> destination)
    {
        var writer = new SnapshotWriter(destination);
        var s = &w->Sources;

        writer.U32(SnapshotMagic);
        writer.U16(SnapshotVersion);
        writer.Byte((byte)w->LayerCount);
        writer.Byte((byte)w->GridCount);
        writer.I32(w->Tick);
        writer.I32(w->MarkEpoch);
        writer.I64(w->FoldLimitI);
        writer.I64(w->FoldLimitQ40);

        for (var l = 0; l < w->LayerCount; l++)
        {
            var r = w->Recipes + l;
            writer.Byte((byte)r->Op);
            writer.Byte(r->A);
            writer.Byte(r->B);
            writer.Byte(r->Shift);
            writer.Byte(r->PhaseBits);
            writer.Byte(r->TurnBits);
            writer.Byte(r->Transient);
            writer.Byte(0);
            writer.U16(r->TurnMask);
            writer.I16(r->Low);
            writer.I16(r->High);
            writer.U16(0);
            writer.I32(r->AnchorMask);
            writer.I32(r->WeightA);
            writer.I32(r->WeightB);
            writer.U32(r->Reads);
        }

        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            var power = 0;
            var size = g->Size;
            while ((size >> 1) != 0)
            {
                power++;
                size >>= 1;
            }

            writer.Byte((byte)power);
            writer.F32(g->OriginX);
            writer.F32(g->OriginY);
            writer.F32(g->WorldSize);
        }

        var usage = stackalloc byte[StampCatalog.MaxStamps];
        var stampCount = SnapshotStampMap(w, usage);
        writer.Byte((byte)stampCount);
        for (var id = 1; id < StampCatalog.MaxStamps; id++)
        {
            if (usage[id] == 0) continue;
            writer.Byte((byte)id);
            writer.U64(StampCatalog.Fingerprint((byte)id));
        }

        writer.I32(s->Count);
        for (var i = 0; i < s->Count; i++)
        {
            writer.F32(s->X[i]);
            writer.F32(s->Y[i]);
            writer.Byte(s->Stamp[i]);
            writer.Byte(s->Layer[i]);
            writer.Byte(s->Gain[i]);
            writer.Byte(s->Alive[i]);
            writer.U16(s->Angle[i]);
            writer.U16(s->Scale[i]);
            writer.U32(s->Gen[i]);
            writer.Byte(s->Timed.Pointer[i]);
            writer.I8(s->FadeFrom.Pointer[i]);
            writer.I8(s->FadeTo.Pointer[i]);
            writer.I32(s->FadeStart.Pointer[i]);
            writer.I32(s->FadeTicks.Pointer[i]);
            writer.I32(s->ExpireAt.Pointer[i]);
        }

        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            var records = g->Records.Pointer;
            writer.I32(s->Count);
            for (var i = 0; i < s->Count; i++)
            {
                writer.Byte(records[i].Layer);
                writer.Byte(records[i].Stamp);
                writer.I8(records[i].Gain);
                writer.Byte(records[i].Alive);
                writer.U16(records[i].Angle);
                writer.U16(records[i].Scale);
                writer.I32(records[i].Ax);
                writer.I32(records[i].Ay);
            }
        }

        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            for (var l = 0; l < w->LayerCount; l++) WriteLayerSnapshot(g, l, ref writer);
        }
    }

    private static void WriteLayerSnapshot(GridCtx* g, int layer, ref SnapshotWriter writer)
    {
        var ld = g->Layers + layer;
        var derived = ld->Derived != 0;
        var live = 0;
        if (ld->Directory != null)
            for (var tile = 0; tile < g->TileCount; tile++)
                if (ld->Directory[tile] >= 0)
                    live++;

        writer.I32(live);
        if (ld->Directory != null)
            for (var tile = 0; tile < g->TileCount; tile++)
            {
                var index = ld->Directory[tile];
                if (index < 0) continue;

                var set = (TilePlanes*)ld->Sets.Unit(index);
                var summary = (TileSummary*)ld->Store.Summaries.Unit(set->Summary);
                byte flags;
                if (derived) flags = 0;
                else flags = SnapshotHasI;
                if (set->T >= 0) flags |= SnapshotHasT;
                if (set->B >= 0) flags |= SnapshotHasB;
                if (summary->CarryI != 0) flags |= SnapshotHasCarryI;
                if (summary->HighT != 0) flags |= SnapshotHasHighT;
                if (summary->LowT != 0) flags |= SnapshotHasLowT;
                if (summary->HighB != 0) flags |= SnapshotHasHighB;
                if (summary->LowB != 0) flags |= SnapshotHasLowB;

                writer.U32((uint)tile);
                writer.Byte(flags);
                writer.Byte(set->Stale);
                writer.I32(set->BoundI);
                writer.I64(set->BoundT);
                writer.I64(set->BoundB);
                writer.I32(ld->Epochs != null ? ld->Epochs[tile] : 0);
                writer.Copy((byte*)summary, TileSummary.Bytes);
                writer.Copy(ld->Store.Pages.Unit(set->Page), PlaneStore.PageUnit);
                if (!derived) writer.Copy(ld->Store.Int32Planes.Unit(set->I), PlaneStore.Int32Unit);
                if (set->T >= 0) writer.Copy(ld->Store.Int64Planes.Unit(set->T), PlaneStore.Int64Unit);
                if (set->B >= 0) writer.Copy(ld->Store.Int64Planes.Unit(set->B), PlaneStore.Int64Unit);
                if (summary->CarryI != 0) writer.Copy((byte*)ld->Store.Wide(summary->CarryI), PlaneStore.Int64Unit);
                if (summary->HighT != 0) writer.Copy((byte*)ld->Store.Wide(summary->HighT), PlaneStore.Int64Unit);
                if (summary->LowT != 0) writer.Copy((byte*)ld->Store.Wide(summary->LowT), PlaneStore.Int64Unit);
                if (summary->HighB != 0) writer.Copy((byte*)ld->Store.Wide(summary->HighB), PlaneStore.Int64Unit);
                if (summary->LowB != 0) writer.Copy((byte*)ld->Store.Wide(summary->LowB), PlaneStore.Int64Unit);
            }

        writer.I32(ld->Changed.Length);
        for (var i = 0; i < ld->Changed.Length; i++) writer.U32((uint)ld->Changed.Pointer[i]);

        writer.I32(ld->Stale.Length);
        for (var i = 0; i < ld->Stale.Length; i++) writer.U32((uint)ld->Stale.Pointer[i]);

        var pairs = 0;
        if (ld->Epochs != null && ld->Directory != null)
            for (var tile = 0; tile < g->TileCount; tile++)
                if (ld->Directory[tile] < 0 && ld->Epochs[tile] != 0)
                    pairs++;

        writer.I32(pairs);
        if (ld->Epochs != null && ld->Directory != null)
            for (var tile = 0; tile < g->TileCount; tile++)
            {
                if (ld->Directory[tile] >= 0 || ld->Epochs[tile] == 0) continue;
                writer.U32((uint)tile);
                writer.I32(ld->Epochs[tile]);
            }
    }

    private static bool SnapshotValidates(ReadOnlySpan<byte> data)
    {
        var reader = new SnapshotReader(data);
        if (!reader.U32(out var magic) || magic != SnapshotMagic) return false;
        if (!reader.U16(out var version) || version != SnapshotVersion) return false;
        if (!reader.Byte(out var layerCount) || layerCount > WorldCtx.MaxLayers) return false;
        if (!reader.Byte(out var gridCount) || gridCount > MaxGrids) return false;
        if (!reader.I32(out _) || !reader.I32(out _)) return false;
        if (!reader.I64(out _) || !reader.I64(out _)) return false;

        var transient = stackalloc byte[WorldCtx.MaxLayers];
        var derivedLayers = stackalloc byte[WorldCtx.MaxLayers];
        for (var l = 0; l < layerCount; l++)
        {
            if (!reader.Byte(out var op) || op > (byte)LayerOp.Mask) return false;
            if (!reader.Byte(out var a)) return false;
            if (!reader.Byte(out var b)) return false;
            if (!reader.Byte(out var shift) || !ValidationRules.IsShift(shift)) return false;
            if (!reader.Byte(out var phaseBits) || !ValidationRules.IsPhaseBits(phaseBits)) return false;
            if (!reader.Byte(out var turnBits) || !ValidationRules.IsTurnBits(turnBits)) return false;
            if (!reader.Byte(out var residency)) return false;
            if (residency > 1) return false;
            if (!reader.Byte(out _) || !reader.U16(out _) || !reader.I16(out var low) || !reader.I16(out var high)) return false;
            if (!reader.U16(out _)) return false;
            if (!reader.I32(out _) || !reader.I32(out var weightA) || !reader.I32(out var weightB)) return false;
            if (!ValidationRules.IsWeight(weightA) || !ValidationRules.IsWeight(weightB)) return false;
            if (!reader.U32(out _)) return false;

            transient[l] = residency;
            derivedLayers[l] = (byte)(op == (byte)LayerOp.Base ? 0 : 1);
            if (op == (byte)LayerOp.Base) continue;
            if (a >= layerCount || b >= layerCount || residency != 0) return false;
            if (op == (byte)LayerOp.Mask && low > high) return false;
        }

        var powers = stackalloc int[MaxGrids];
        var tileCounts = stackalloc int[MaxGrids];
        for (var gi = 0; gi < gridCount; gi++)
        {
            if (!reader.Byte(out var power) || !ValidationRules.IsPower(power)) return false;
            if (!reader.F32(out var x) || !float.IsFinite(x)) return false;
            if (!reader.F32(out var y) || !float.IsFinite(y)) return false;
            if (!reader.F32(out var size) || !ValidationRules.IsGridSize(size)) return false;
            if (!ValidationRules.TryGridScaleQ8(power, size, out _)) return false;
            powers[gi] = power;
            var side = (1 << power) >> TileBake.TileBits;
            tileCounts[gi] = side * side;
        }

        if (!reader.Byte(out var stampCount)) return false;
        var usage = stackalloc byte[StampCatalog.MaxStamps];
        new Span<byte>(usage, StampCatalog.MaxStamps).Clear();
        for (var i = 0; i < stampCount; i++)
        {
            if (!reader.Byte(out var id) || id == 0 || usage[id] != 0) return false;
            if (!reader.U64(out var fingerprint)) return false;
            if (!StampCatalog.Valid(id) || StampCatalog.Fingerprint(id) != fingerprint) return false;
            usage[id] = 1;
        }

        if (!reader.I32(out var slots) || slots < 0 || slots > MaxSourceSlots) return false;
        for (var i = 0; i < slots; i++)
        {
            if (!reader.F32(out var x) || !float.IsFinite(x)) return false;
            if (!reader.F32(out var y) || !float.IsFinite(y)) return false;
            if (!reader.Byte(out var stamp) || !reader.Byte(out var layer) || layer >= layerCount) return false;
            if (!reader.Byte(out var gain) || !ValidationRules.IsGain((sbyte)gain)) return false;
            if (!reader.Byte(out var alive)) return false;
            if (alive > 1) return false;
            if (!reader.U16(out _) || !reader.U16(out _)) return false;
            if (!reader.U32(out _)) return false;
            if (!reader.Byte(out var timed) || timed > 3) return false;
            if (!reader.I8(out var fadeFrom) || !reader.I8(out var fadeTo)) return false;
            if (!reader.I32(out _) || !reader.I32(out var fadeTicks) || !reader.I32(out _)) return false;
            if (alive == 0) continue;

            if (transient[layer] != 0) return false;
            if (usage[stamp] == 0) return false;
            if ((timed & 1) != 0 && (!ValidationRules.IsGain(fadeFrom) || !ValidationRules.IsGain(fadeTo) || fadeTicks <= 0)) return false;
        }

        for (var gi = 0; gi < gridCount; gi++)
        {
            if (!reader.I32(out var records) || records != slots) return false;
            for (var i = 0; i < records; i++)
            {
                if (!reader.Byte(out var layer) || !reader.Byte(out var stamp) || !reader.I8(out var recordGain) ||
                    !reader.Byte(out var alive)) return false;
                if (!reader.U16(out var angle) || !reader.U16(out var scale)) return false;
                if (!reader.I32(out var ax) || !reader.I32(out var ay)) return false;
                if (layer >= layerCount) return false;
                if (alive == 0)
                {
                    if ((layer | stamp | (byte)recordGain | alive | angle | scale | ax | ay) != 0) return false;
                    continue;
                }

                if (!ValidationRules.IsGain(recordGain) || usage[stamp] == 0) return false;
            }
        }

        var summary = stackalloc byte[TileSummary.Bytes];
        for (var gi = 0; gi < gridCount; gi++)
        {
            var tileCount = tileCounts[gi];
            for (var l = 0; l < layerCount; l++)
            {
                if (!reader.I32(out var live) || live < 0 || live > tileCount) return false;
                var derived = derivedLayers[l] != 0;
                var previous = -1;
                for (var i = 0; i < live; i++)
                {
                    if (!reader.U32(out var tile) || (int)tile >= tileCount || (int)tile <= previous) return false;
                    previous = (int)tile;
                    if (!reader.Byte(out var flags) || !reader.Byte(out var stale)) return false;
                    if (stale != 0 && transient[l] == 0) return false;
                    if (!reader.I32(out _) || !reader.I64(out _) || !reader.I64(out _) || !reader.I32(out _)) return false;
                    if (!reader.Copy(summary, TileSummary.Bytes)) return false;
                    var handles = (TileSummary*)summary;
                    if (WideMismatch(flags, handles)) return false;
                    if (((flags & SnapshotHasI) != 0) == derived) return false;
                    var planes = PlaneStore.PageUnit;
                    if ((flags & SnapshotHasI) != 0) planes += PlaneStore.Int32Unit;
                    if ((flags & SnapshotHasT) != 0) planes += PlaneStore.Int64Unit;
                    if ((flags & SnapshotHasB) != 0) planes += PlaneStore.Int64Unit;
                    planes += SnapshotWideCount(handles) * PlaneStore.Int64Unit;
                    if (!reader.Skip(planes)) return false;
                }

                previous = -1;
                if (!reader.I32(out var changed) || changed < 0 || changed > tileCount) return false;
                for (var i = 0; i < changed; i++)
                {
                    if (!reader.U32(out var tile) || (int)tile >= tileCount || (int)tile <= previous) return false;
                    previous = (int)tile;
                }

                if (!reader.I32(out var staleCount) || staleCount < 0 || staleCount > tileCount) return false;
                for (var i = 0; i < staleCount; i++)
                    if (!reader.U32(out var tile) || (int)tile >= tileCount) return false;

                previous = -1;
                if (!reader.I32(out var pairs) || pairs < 0 || pairs > tileCount) return false;
                for (var i = 0; i < pairs; i++)
                {
                    if (!reader.U32(out var tile) || (int)tile >= tileCount || (int)tile <= previous) return false;
                    previous = (int)tile;
                    if (!reader.I32(out _)) return false;
                }
            }
        }

        return reader.AtEnd;
    }

    private static bool WideMismatch(byte flags, TileSummary* summary)
    {
        if (((flags & SnapshotHasCarryI) != 0) != (summary->CarryI != 0)) return true;
        if (((flags & SnapshotHasHighT) != 0) != (summary->HighT != 0)) return true;
        if (((flags & SnapshotHasLowT) != 0) != (summary->LowT != 0)) return true;
        if (((flags & SnapshotHasHighB) != 0) != (summary->HighB != 0)) return true;
        return ((flags & SnapshotHasLowB) != 0) != (summary->LowB != 0);
    }

    private static void RestoreApply(byte world, WorldCtx* w, ReadOnlySpan<byte> data)
    {
        var reader = new SnapshotReader(data);
        reader.U32(out _);
        reader.U16(out _);
        reader.Byte(out var layerCount);
        reader.Byte(out var gridCount);
        reader.I32(out var tick);
        reader.I32(out var markEpoch);
        reader.I64(out var foldI);
        reader.I64(out var foldQ40);

        var recipes = stackalloc LayerRecipe[WorldCtx.MaxLayers];
        for (var l = 0; l < layerCount; l++)
        {
            reader.Byte(out var op);
            reader.Byte(out var a);
            reader.Byte(out var b);
            reader.Byte(out var shift);
            reader.Byte(out var phaseBits);
            reader.Byte(out var turnBits);
            reader.Byte(out var residency);
            reader.Byte(out _);
            reader.U16(out var turnMask);
            reader.I16(out var low);
            reader.I16(out var high);
            reader.U16(out _);
            reader.I32(out var anchorMask);
            reader.I32(out var weightA);
            reader.I32(out var weightB);
            reader.U32(out var reads);
            recipes[l] = new LayerRecipe
            {
                Op = (LayerOp)op,
                A = a,
                B = b,
                Shift = shift,
                PhaseBits = phaseBits,
                TurnBits = turnBits,
                TurnMask = turnMask,
                AnchorMask = anchorMask,
                WeightA = weightA,
                WeightB = weightB,
                Low = low,
                High = high,
                Reads = reads,
                Transient = residency,
            };
        }

        w->Tick = tick;
        w->MarkEpoch = markEpoch;
        ConfigureFoldLimits(world, foldI, foldQ40);

        for (var gi = 0; gi < gridCount; gi++)
        {
            reader.Byte(out var power);
            reader.F32(out var x);
            reader.F32(out var y);
            reader.F32(out var size);
            AddGrid(world, power, x, y, size);
        }

        for (var l = 0; l < layerCount; l++)
        {
            AddLayer(world, new Layer.Precision(recipes[l].PhaseBits, recipes[l].TurnBits), recipes[l].Transient != 0);
            w->Recipes[l] = recipes[l];
        }

        for (var l = 0; l < layerCount; l++)
        {
            if (recipes[l].Op == LayerOp.Base) continue;
            w->DerivedCount++;
            for (var gi = 0; gi < gridCount; gi++) w->Grids[gi].Layers[l].Derived = 1;
        }

        reader.Byte(out var stampCount);
        for (var i = 0; i < stampCount; i++)
        {
            reader.Byte(out _);
            reader.U64(out _);
        }

        var s = &w->Sources;
        reader.I32(out var count);
        if (count > 0) EnsureSourceCapacity(s, w, count);
        s->Count = count;
        for (var i = 0; i < count; i++)
        {
            reader.F32(out var x);
            reader.F32(out var y);
            reader.Byte(out var stamp);
            reader.Byte(out var layer);
            reader.Byte(out var gain);
            reader.Byte(out var alive);
            reader.U16(out var angle);
            reader.U16(out var scale);
            reader.U32(out var generation);
            reader.Byte(out var timed);
            reader.I8(out var fadeFrom);
            reader.I8(out var fadeTo);
            reader.I32(out var fadeStart);
            reader.I32(out var fadeTicks);
            reader.I32(out var expireAt);

            s->X[i] = x;
            s->Y[i] = y;
            s->Stamp[i] = stamp;
            s->Layer[i] = layer;
            s->Gain[i] = gain;
            s->Alive[i] = alive;
            s->Angle[i] = angle;
            s->Scale[i] = scale;
            s->Gen[i] = generation;
            s->Timed.Pointer[i] = timed;
            s->FadeFrom.Pointer[i] = fadeFrom;
            s->FadeTo.Pointer[i] = fadeTo;
            s->FadeStart.Pointer[i] = fadeStart;
            s->FadeTicks.Pointer[i] = fadeTicks;
            s->ExpireAt.Pointer[i] = expireAt;
            if (alive == 0 && generation != uint.MaxValue) s->Free.Push(i);
        }

        for (var gi = 0; gi < gridCount; gi++)
        {
            var g = w->Grids + gi;
            var records = g->Records.Pointer;
            reader.I32(out var recordCount);
            for (var i = 0; i < recordCount; i++)
            {
                reader.Byte(out var layer);
                reader.Byte(out var stamp);
                reader.I8(out var recordGain);
                reader.Byte(out var recordAlive);
                reader.U16(out var angle);
                reader.U16(out var scale);
                reader.I32(out var ax);
                reader.I32(out var ay);
                records[i] = new DepositRecord
                {
                    Layer = layer,
                    Stamp = stamp,
                    Gain = recordGain,
                    Alive = recordAlive,
                    Angle = angle,
                    Scale = scale,
                    Ax = ax,
                    Ay = ay,
                };
            }
        }

        var summary = stackalloc byte[TileSummary.Bytes];
        for (var gi = 0; gi < gridCount; gi++)
        {
            var g = w->Grids + gi;
            for (var l = 0; l < layerCount; l++) RestoreLayer(g, l, summary, ref reader);
        }

        RebuildTimed(w);
    }

    private static void RestoreLayer(GridCtx* g, int layer, byte* summaryBytes, ref SnapshotReader reader)
    {
        var ld = g->Layers + layer;
        var derived = ld->Derived != 0;
        reader.I32(out var live);
        if (live > 0)
        {
            EnsureDirectory(g, ld);
            EnsureEpochs(g, ld);
            ld->TileHashes = (ulong*)NativeHeap.AllocZeroed((nuint)g->TileCount * sizeof(ulong));
        }

        for (var i = 0; i < live; i++)
        {
            reader.U32(out var tileU);
            reader.Byte(out var flags);
            reader.Byte(out var stale);
            reader.I32(out var boundI);
            reader.I64(out var boundT);
            reader.I64(out var boundB);
            reader.I32(out var epoch);
            reader.Copy(summaryBytes, TileSummary.Bytes);
            var summary = (TileSummary*)summaryBytes;

            var tile = (int)tileU;
            var set = EnsureSet(g, ld, tile);
            set->BoundI = boundI;
            set->BoundT = boundT;
            set->BoundB = boundB;
            set->Stale = stale;
            reader.Copy(ld->Store.Pages.Unit(set->Page), PlaneStore.PageUnit);
            if ((flags & SnapshotHasI) != 0) reader.Copy(ld->Store.Int32Planes.Unit(set->I), PlaneStore.Int32Unit);
            if ((flags & SnapshotHasT) != 0) reader.Copy((byte*)ld->Store.EnsureT(ref *set), PlaneStore.Int64Unit);
            if ((flags & SnapshotHasB) != 0) reader.Copy((byte*)ld->Store.EnsureB(ref *set), PlaneStore.Int64Unit);
            if (summary->CarryI != 0)
            {
                summary->CarryI = ld->Store.TakeWide();
                reader.Copy((byte*)ld->Store.Wide(summary->CarryI), PlaneStore.Int64Unit);
            }

            if (summary->HighT != 0)
            {
                summary->HighT = ld->Store.TakeWide();
                reader.Copy((byte*)ld->Store.Wide(summary->HighT), PlaneStore.Int64Unit);
            }

            if (summary->LowT != 0)
            {
                summary->LowT = ld->Store.TakeWide();
                reader.Copy((byte*)ld->Store.Wide(summary->LowT), PlaneStore.Int64Unit);
            }

            if (summary->HighB != 0)
            {
                summary->HighB = ld->Store.TakeWide();
                reader.Copy((byte*)ld->Store.Wide(summary->HighB), PlaneStore.Int64Unit);
            }

            if (summary->LowB != 0)
            {
                summary->LowB = ld->Store.TakeWide();
                reader.Copy((byte*)ld->Store.Wide(summary->LowB), PlaneStore.Int64Unit);
            }

            var target = (TileSummary*)ld->Store.Summaries.Unit(set->Summary);
            *target = *summary;
            if (!derived) TileBake.UpdateSummaries(ld->Store.Cells(*set), 0, 0, TileBake.TileSize, TileBake.TileSize);
            StampEpoch(g, ld, tile, epoch);
            var page = (short*)ld->Store.Pages.Unit(set->Page);
            var current = TileBake.PageAny(page) ? TileHash.Of(tile, page) : 0;
            ld->TileHashes[tile] = current;
            ld->Hash ^= current;
            ld->Max.Update(g->TilesPerSide, tile, target->Max);
        }

        reader.I32(out var changed);
        ld->Changed.Resize(changed);
        for (var i = 0; i < changed; i++)
        {
            reader.U32(out var tile);
            ld->Changed.Pointer[i] = (int)tile;
        }

        reader.I32(out var staleCount);
        if (staleCount > 0 && ld->InDirty == null)
            ld->InDirty = (int*)NativeHeap.AllocZeroed((nuint)g->TileCount * sizeof(int));
        ld->Stale.Resize(staleCount);
        for (var i = 0; i < staleCount; i++)
        {
            reader.U32(out var tile);
            ld->Stale.Pointer[i] = (int)tile;
        }

        reader.I32(out var pairs);
        if (pairs > 0) EnsureEpochs(g, ld);
        for (var i = 0; i < pairs; i++)
        {
            reader.U32(out var tile);
            reader.I32(out var epoch);
            StampEpoch(g, ld, (int)tile, epoch);
        }
    }
}
