using System.Runtime.CompilerServices;
#if NET
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Gi;

internal unsafe struct SourceColumn<T> where T : unmanaged
{
    public T** Chunks;

    public ref T this[int slot]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref Chunks[slot >> SourceColumns.ChunkBits][slot & SourceColumns.ChunkMask];
    }
}

internal unsafe struct SourceColumns
{
    public const int ChunkSlots = 16384;
    public const int ChunkBits = 14;
    public const int ChunkMask = ChunkSlots - 1;
    public const int MaxChunks = World.MaxSourceSlots >> ChunkBits;

    public SourceColumn<float> X;
    public SourceColumn<float> Y;
    public SourceColumn<byte> Stamp;
    public SourceColumn<byte> Layer;
    public SourceColumn<byte> Gain;
    public SourceColumn<ushort> Angle;
    public SourceColumn<ushort> Scale;
    public SourceColumn<byte> Alive;
    public SourceColumn<uint> Gen;
    public SourceColumn<int> Mark;
    public NativeBuffer<int> HotStamp;
    public NativeBuffer<int> SchedStamp;
    public NativeBuffer<byte> Timed;
    public NativeBuffer<sbyte> FadeFrom;
    public NativeBuffer<sbyte> FadeTo;
    public NativeBuffer<int> FadeStart;
    public NativeBuffer<int> FadeTicks;
    public NativeBuffer<int> ExpireAt;
    public NativeBuffer<int> TimedAt;
    public FreeRing Free;
    public int Count;
    public int Chunks;

    public readonly int Slots => Chunks << ChunkBits;

    public void AddChunks(int chunks)
    {
        if (Chunks == 0)
        {
            var pointers = (nuint)(MaxChunks * sizeof(void*));
            X.Chunks = (float**)NativeHeap.AllocZeroed(pointers);
            Y.Chunks = (float**)NativeHeap.AllocZeroed(pointers);
            Stamp.Chunks = (byte**)NativeHeap.AllocZeroed(pointers);
            Layer.Chunks = (byte**)NativeHeap.AllocZeroed(pointers);
            Gain.Chunks = (byte**)NativeHeap.AllocZeroed(pointers);
            Angle.Chunks = (ushort**)NativeHeap.AllocZeroed(pointers);
            Scale.Chunks = (ushort**)NativeHeap.AllocZeroed(pointers);
            Alive.Chunks = (byte**)NativeHeap.AllocZeroed(pointers);
            Gen.Chunks = (uint**)NativeHeap.AllocZeroed(pointers);
            Mark.Chunks = (int**)NativeHeap.AllocZeroed(pointers);
        }

        for (var i = 0; i < chunks; i++)
        {
            var chunk = Chunks;
            X.Chunks[chunk] = (float*)NativeHeap.AllocZeroed((nuint)(ChunkSlots * sizeof(float)));
            Y.Chunks[chunk] = (float*)NativeHeap.AllocZeroed((nuint)(ChunkSlots * sizeof(float)));
            Stamp.Chunks[chunk] = (byte*)NativeHeap.AllocZeroed(ChunkSlots);
            Layer.Chunks[chunk] = (byte*)NativeHeap.AllocZeroed(ChunkSlots);
            Gain.Chunks[chunk] = (byte*)NativeHeap.AllocZeroed(ChunkSlots);
            Angle.Chunks[chunk] = (ushort*)NativeHeap.AllocZeroed((nuint)(ChunkSlots * sizeof(ushort)));
            Scale.Chunks[chunk] = (ushort*)NativeHeap.AllocZeroed((nuint)(ChunkSlots * sizeof(ushort)));
            Alive.Chunks[chunk] = (byte*)NativeHeap.AllocZeroed(ChunkSlots);
            Gen.Chunks[chunk] = (uint*)NativeHeap.AllocZeroed((nuint)(ChunkSlots * sizeof(uint)));
            Mark.Chunks[chunk] = (int*)NativeHeap.AllocZeroed((nuint)(ChunkSlots * sizeof(int)));
            Chunks++;
        }
    }

    public void Release()
    {
        ReleaseColumn(ref X);
        ReleaseColumn(ref Y);
        ReleaseColumn(ref Stamp);
        ReleaseColumn(ref Layer);
        ReleaseColumn(ref Gain);
        ReleaseColumn(ref Angle);
        ReleaseColumn(ref Scale);
        ReleaseColumn(ref Alive);
        ReleaseColumn(ref Gen);
        ReleaseColumn(ref Mark);
        Chunks = 0;
        HotStamp.Free();
        SchedStamp.Free();
        Timed.Free();
        FadeFrom.Free();
        FadeTo.Free();
        FadeStart.Free();
        FadeTicks.Free();
        ExpireAt.Free();
        TimedAt.Free();
        Free.Release();
    }

    private void ReleaseColumn<T>(ref SourceColumn<T> column) where T : unmanaged
    {
        if (column.Chunks == null) return;

        for (var i = 0; i < Chunks; i++) NativeHeap.Free(column.Chunks[i]);
        NativeHeap.Free(column.Chunks);
        column.Chunks = null;
    }
}

internal unsafe struct LayerData
{
    public int* Directory;
    public FixedSlab Sets;
    public PlaneStore Store;
    public int LivePages;
    public byte Derived;
    public int* InDirty;
    public int* Epochs;
    public NativeBuffer<int> Dirty;
    public NativeBuffer<int> Changed;
    public NativeBuffer<int> Stale;
    public HistState* Hist;
    public NativeBuffer<TileRect> Rects;
    public Pyramid<short, ShortMaxOp> Max;
    public Pyramid<int, EpochMaxOp> EpochTree;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly TilePlanes* SetOf(int tile)
    {
        var directory = Directory;
        if (directory == null) return null;

        var index = directory[tile];
        return index < 0 ? null : (TilePlanes*)Sets.Unit(index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly short* PageOf(int tile)
    {
        var set = SetOf(tile);
        return set == null ? null : (short*)Store.Pages.Unit(set->Page);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly PlaneCells CellsOf(int tile)
    {
        var set = SetOf(tile);
        return set == null ? default : Store.Cells(*set);
    }

    public readonly TileSummary* SummaryOf(int tile)
    {
        var set = SetOf(tile);
        return set == null ? null : (TileSummary*)Store.Summaries.Unit(set->Summary);
    }
}

internal unsafe struct GridCtx
{
    public int Size;
    public float OriginX;
    public float OriginY;
    public float WorldSize;
    public float Scale;
    public int ScaleQ8;
    public int TilesPerSide;
    public int TileCount;
    public LayerData* Layers;
    public NativeBuffer<DepositRecord> Records;
}

internal struct TileRect
{
    public int X0;
    public int Y0;
    public int X1;
    public int Y1;
}

internal struct DepositRecord
{
    public byte Layer;
    public byte Stamp;
    public sbyte Gain;
    public byte Alive;
    public ushort Angle;
    public ushort Scale;
    public int Ax;
    public int Ay;
}

internal struct JournalEntry
{
    public int Slot;
    public float X;
    public float Y;
    public byte Stamp;
    public byte Layer;
    public byte Gain;
    public byte Alive;
    public ushort Angle;
    public ushort Scale;
    public uint Gen;
}

internal unsafe struct WorldCtx
{
    public const int MaxLayers = 32;

    public GridCtx* Grids;
    public int GridCount;
    public int LayerCount;
    public long FoldLimitI;
    public long FoldLimitQ40;
    public LayerRecipe* Recipes;
    public int DerivedCount;
    public SourceColumns Sources;
    public NativeBuffer<int> Changed;
    public NativeBuffer<JournalEntry> Pending;
    public NativeBuffer<JournalEntry> Journal;
    public NativeBuffer<ScheduleRecord> ScheduleJournal;
    public NativeBuffer<TimedEntry> TimedList;
    public NativeBuffer<int> TimedDue;
    public int Tick;
    public int MarkEpoch;
    public int RecordEpoch;
    public int RecordTick;
    public byte Recording;
    public KernelCache Kernels;
}

public static unsafe partial class World
{
    public const int MaxLayers = ValidationRules.MaxLayers;
    internal const int MaxWorlds = ValidationRules.MaxWorlds;
    internal const int MaxGrids = ValidationRules.MaxGrids;
    private const int MaxGain = ValidationRules.MaxGain;
    private const ushort UnitScale = 256;
    internal const int MaxSourceSlots = 1 << 24;
    internal const int Cells = TileBake.TileSize * TileBake.TileSize;
    internal const int PageBytes = Cells * sizeof(short);

    private static int _worldCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static WorldCtx* GetContext(byte world)
    {
        if (world >= _worldCount) return null;

        var w = Runtime.Worlds + world;
        return w->Grids == null ? null : w;
    }

    private static byte ReserveSlot()
    {
        for (var i = 0; i < _worldCount; i++)
            if (Runtime.Worlds[i].Grids == null) return (byte)i;

        if (_worldCount >= MaxWorlds) throw new InvalidOperationException("World limit reached.");

        return (byte)_worldCount++;
    }

    public static byte New()
    {
        Runtime.Ensure();

        var id = ReserveSlot();
        Runtime.WorldRevisions[id]++;
        var w = Runtime.Worlds + id;
        w->Grids = (GridCtx*)NativeHeap.AllocZeroed((nuint)(MaxGrids * sizeof(GridCtx)));
        w->Recipes = (LayerRecipe*)NativeHeap.AllocZeroed((nuint)(MaxLayers * sizeof(LayerRecipe)));
        w->MarkEpoch = 1;
        return id;
    }

    public static bool Free(byte world)
    {
        var w = GetContext(world);
        if (w == null) return false;

        for (var gi = 0; gi < w->GridCount; gi++) FreeGrid(w->Grids + gi, w->LayerCount);
        NativeHeap.Free(w->Grids);
        NativeHeap.Free(w->Recipes);
        w->Sources.Release();
        w->Changed.Free();
        w->Pending.Free();
        w->Journal.Free();
        w->ScheduleJournal.Free();
        w->TimedList.Free();
        w->TimedDue.Free();
        w->Kernels.Release();
        *w = default;
        Runtime.WorldRevisions[world]++;
        return true;
    }

    public static int Revision(byte world) => world < _worldCount ? Runtime.WorldRevisions[world] : 0;

    public readonly struct SourceId : IEquatable<SourceId>
    {
        public readonly uint Slot;
        public readonly uint Generation;

        public SourceId(uint slot, uint generation)
        {
            Slot = slot;
            Generation = generation;
        }

        public bool Equals(SourceId other) => Slot == other.Slot && Generation == other.Generation;

        public override bool Equals(object? obj) => obj is SourceId other && Equals(other);

        public override int GetHashCode() => (int)(Slot * 31u ^ Generation);

        public override string ToString()
            => "slot " + Slot.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               " generation " + Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public static bool operator ==(SourceId left, SourceId right) => left.Equals(right);

        public static bool operator !=(SourceId left, SourceId right) => !left.Equals(right);
    }

    public readonly struct SourceSpec
    {
        public readonly float X;
        public readonly float Y;
        public readonly float HeadingRadians;
        public readonly float Scale;
        public readonly sbyte Gain;
        public readonly byte Layer;
        public readonly byte Stamp;

        internal SourceSpec(float x, float y, float headingRadians, float scale, sbyte gain, byte layer, byte stamp)
        {
            X = x;
            Y = y;
            HeadingRadians = headingRadians;
            Scale = scale;
            Gain = gain;
            Layer = layer;
            Stamp = stamp;
        }
    }

    public static bool TryGetSourceSpec(byte world, SourceId source, out SourceSpec spec)
    {
        spec = default;
        if (!TrySource(world, source, out _, out var s, out var i)) return false;

        spec = new SourceSpec(
            s->X[i],
            s->Y[i],
            s->Angle[i] * ((float)Math.PI * 2 / 65536),
            s->Scale[i] / 256f,
            (sbyte)s->Gain[i],
            s->Layer[i],
            s->Stamp[i]);
        return true;
    }

    public static int CopyWorldIds(Span<byte> destination)
    {
        var count = 0;
        for (var i = 0; i < _worldCount; i++)
        {
            if ((Runtime.Worlds + i)->Grids == null) continue;
            if (count < destination.Length) destination[count] = (byte)i;
            count++;
        }

        return count;
    }

    public static void ProcessAll()
    {
        for (var i = 0; i < _worldCount; i++)
            if ((Runtime.Worlds + i)->Grids != null) Process((byte)i);
    }

    internal static int GridCount(byte world)
    {
        var w = GetContext(world);
        return w == null ? 0 : w->GridCount;
    }

    internal static int CopyGridIds(byte world, Span<byte> destination)
    {
        var w = GetContext(world);
        if (w == null) return 0;

        var count = w->GridCount;
        for (var i = 0; i < count && i < destination.Length; i++) destination[i] = (byte)i;
        return count;
    }

    internal static bool TryGetGridSpec(byte world, byte grid, out int power, out float x, out float y, out float size)
    {
        power = 0;
        x = 0f;
        y = 0f;
        size = 0f;
        var w = GetContext(world);
        if (w == null || grid >= w->GridCount) return false;

        var g = w->Grids + grid;
        for (var s = g->Size; s > 1; s >>= 1) power++;
        x = g->OriginX;
        y = g->OriginY;
        size = g->WorldSize;
        return true;
    }

    internal static int LayerCount(byte world)
    {
        var w = GetContext(world);
        return w == null ? 0 : w->LayerCount;
    }

    internal static int CopyLayerIds(byte world, Span<byte> destination)
    {
        var w = GetContext(world);
        if (w == null) return 0;

        var count = w->LayerCount;
        for (var i = 0; i < count && i < destination.Length; i++) destination[i] = (byte)i;
        return count;
    }

    private static void FreeGrid(GridCtx* g, int layerCount)
    {
        for (var l = 0; l < layerCount; l++) FreeLayer(g, g->Layers + l);
        NativeHeap.Free(g->Layers);
        g->Records.Free();
    }

    private static void FreeLayer(GridCtx* g, LayerData* ld)
    {
        ld->Sets.Reset();
        ld->Store.Reset();
        if (ld->Directory != null) NativeHeap.Free(ld->Directory);
        ld->Directory = null;
        ld->LivePages = 0;
        NativeHeap.Free(ld->InDirty);
        NativeHeap.Free(ld->Epochs);
        if (ld->Hist != null)
        {
            ld->Hist->Release(g->TileCount);
            NativeHeap.Free(ld->Hist);
            ld->Hist = null;
        }

        ld->Dirty.Free();
        ld->Changed.Free();
        ld->Stale.Free();
        ld->Rects.Free();
        ld->Max.Reset();
        ld->EpochTree.Reset();
    }

    internal static byte AddGrid(byte world, int power, float x, float y, float size)
    {
        if (!ValidationRules.IsPower(power)) throw new ArgumentOutOfRangeException(nameof(power));
        if (!ValidationRules.IsGridSize(size)) throw new ArgumentOutOfRangeException(nameof(size));
        if (!ValidationRules.IsGridOrigin(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!ValidationRules.IsGridOrigin(y)) throw new ArgumentOutOfRangeException(nameof(y));
        if (!ValidationRules.TryGridScaleQ8(power, size, out var scaleQ8)) throw new ArgumentOutOfRangeException(nameof(size));

        var w = GetContext(world);
        if (w == null) throw new ArgumentOutOfRangeException(nameof(world));
        if (w->GridCount >= MaxGrids) throw new InvalidOperationException("Grid limit reached.");

        var id = (byte)w->GridCount++;
        var g = w->Grids + id;
        g->Size = 1 << power;
        g->OriginX = x;
        g->OriginY = y;
        g->WorldSize = size;
        g->Scale = g->Size / size;
        g->ScaleQ8 = scaleQ8;
        g->TilesPerSide = g->Size >> TileBake.TileBits;
        g->TileCount = g->TilesPerSide * g->TilesPerSide;
        g->Layers = (LayerData*)NativeHeap.AllocZeroed((nuint)(MaxLayers * sizeof(LayerData)));
        for (var l = 0; l < WorldCtx.MaxLayers; l++)
        {
            g->Layers[l].Store.LimitI = w->FoldLimitI;
            g->Layers[l].Store.LimitQ40 = w->FoldLimitQ40;
        }

        for (var l = 0; l < w->LayerCount; l++)
            if (IsDerived(w, l))
                g->Layers[l].Derived = 1;
        g->Records.Resize(w->Sources.Slots);
        return id;
    }

    internal static void DisableKernelInserts(byte world)
    {
        var w = GetContext(world);
        if (w != null) w->Kernels.InsertsDisabled = true;
    }

    internal static void ConfigureFoldLimits(byte world, long limitI, long limitQ40)
    {
        var w = GetContext(world);
        if (w == null) return;

        w->FoldLimitI = limitI;
        w->FoldLimitQ40 = limitQ40;
    }

    internal static byte AddLayer(byte world, Layer.Precision precision, bool transient = false)
    {
        if (!ValidationRules.IsPhaseBits(precision.PhaseBits) || !ValidationRules.IsTurnBits(precision.TurnBits))
            throw new ArgumentOutOfRangeException(nameof(precision));
        var w = GetContext(world);
        if (w == null) throw new ArgumentOutOfRangeException(nameof(world));
        if (w->LayerCount >= MaxLayers) throw new InvalidOperationException("Layer limit reached.");
        var id = w->LayerCount++;
        w->Recipes[id] = new LayerRecipe
        {
            PhaseBits = precision.PhaseBits,
            TurnBits = precision.TurnBits,
            TurnMask = precision.TurnMask,
            AnchorMask = precision.AnchorMask,
            Reads = 1u << id,
            Transient = (byte)(transient ? 1 : 0),
        };
        return (byte)id;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Layer.Precision LayerPrecision(WorldCtx* w, byte layer)
    {
        var r = w->Recipes + layer;
        return new Layer.Precision(r->PhaseBits, r->TurnBits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool PositionChanged(WorldCtx* w, byte layer, float oldX, float oldY, float newX, float newY)
    {
        var mask = w->Recipes[layer].AnchorMask;
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            if (g->ScaleQ8 == 0) continue;
            var dx = TileBake.AnchorQ8(oldX, g->OriginX, g->ScaleQ8, mask) ^ TileBake.AnchorQ8(newX, g->OriginX, g->ScaleQ8, mask);
            var dy = TileBake.AnchorQ8(oldY, g->OriginY, g->ScaleQ8, mask) ^ TileBake.AnchorQ8(newY, g->OriginY, g->ScaleQ8, mask);
            if ((dx | dy) != 0) return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool AngleChanged(WorldCtx* w, byte layer, ushort oldAngle, ushort newAngle)
        => w->GridCount > 0 && ((oldAngle ^ newAngle) & w->Recipes[layer].TurnMask) != 0;

    public static SourceId Place(byte world, byte layer, float x, float y, byte stamp, sbyte gain)
    {
        var w = GetContext(world);
        if (w == null) return default;

        PlaceSlot(w, layer, x, y, stamp, gain, out var id);
        return id;
    }

    public static void Move(byte world, SourceId source, float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) return;
        if (!TrySource(world, source, out var w, out var s, out var i)) return;

        WriteMove(w, s, i, x, y);
    }

    public static void SetGain(byte world, SourceId source, sbyte gain)
    {
        if (!TrySource(world, source, out var w, out var s, out var i)) return;

        WriteGain(w, s, i, gain);
    }

    public static void Turn(byte world, SourceId source, float angle)
    {
        if (!TrySource(world, source, out var w, out var s, out var i) || !float.IsFinite(angle)) return;

        WriteTurn(w, s, i, QuantizeAngle(angle));
    }

    public static void Scale(byte world, SourceId source, float scale)
    {
        if (!TrySource(world, source, out var w, out var s, out var i) || !float.IsFinite(scale)) return;

        WriteScale(w, s, i, QuantizeScale(scale));
    }

    public static void Remove(byte world, SourceId source)
    {
        if (!TrySource(world, source, out var w, out var s, out var i)) return;

        WriteRemove(w, s, i);
    }

    public static void Clear(byte world)
    {
        var w = GetContext(world);
        if (w == null) return;
        Runtime.WorldRevisions[world]++;
        var s = &w->Sources;
        for (var i = 0; i < s->Count; i++)
        {
            s->Alive[i] = 0;
            s->TimedAt.Pointer[i] = 0;
        }

        s->Count = 0;
        s->Free.Clear();
        w->Changed.Resize(0);
        w->Pending.Resize(0);
        w->Journal.Resize(0);
        w->ScheduleJournal.Resize(0);
        w->TimedList.Resize(0);
        w->Recording = 0;
        w->MarkEpoch++;

        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            g->Records.Span.Clear();
            for (var l = 0; l < w->LayerCount; l++)
            {
                var ld = g->Layers + l;
                var span = ld->Dirty.Span;
                foreach (var tile in span) ld->InDirty[tile] = 0;
                ld->Dirty.Resize(0);
                ld->Changed.Resize(0);
                ld->Stale.Resize(0);
                ResetHistogram(g, ld);
                ld->Rects.Resize(0);

                if (ld->Directory != null)
                    for (var tile = 0; tile < g->TileCount; tile++)
                    {
                        if (ld->Directory[tile] < 0) continue;

                        var published = TileBake.PageAny(ld->PageOf(tile));
                        DropPage(ld, tile);
                        if (published && ld->Epochs != null) StampEpoch(g, ld, tile, w->Tick + 1);
                    }

                ld->Max.Reset();
            }
        }
    }

    public static void Record(byte world)
    {
        var w = GetContext(world);
        if (w == null) return;
        w->RecordEpoch++;
        w->Journal.Resize(0);
        w->ScheduleJournal.Resize(0);
        w->RecordTick = w->Tick;
        w->Recording = 1;
        var s = &w->Sources;
        var pending = w->Pending.Pointer;
        for (var i = 0; i < w->Pending.Length; i++)
        {
            s->HotStamp.Pointer[pending[i].Slot] = w->RecordEpoch;
            var n = w->Journal.Length;
            w->Journal.Resize(n + 1);
            w->Journal.Pointer[n] = pending[i];
        }
    }

    public static void StopRecording(byte world)
    {
        var w = GetContext(world);
        if (w == null) return;
        w->Journal.Resize(0);
        w->ScheduleJournal.Resize(0);
        w->Recording = 0;
    }

    public static void Rewind(byte world)
    {
        var w = GetContext(world);
        if (w == null) return;
        Runtime.WorldRevisions[world]++;
        var s = &w->Sources;
        var recording = w->Recording != 0;
        w->Recording = 0;

        var pending = w->Pending.Pointer;
        for (var i = w->Pending.Length - 1; i >= 0; i--) RestoreColumns(s, pending + i);
        w->Pending.Resize(0);

        var journal = w->Journal.Pointer;
        for (var j = w->Journal.Length - 1; j >= 0; j--)
        {
            var entry = journal + j;
            var slot = entry->Slot;
            SnapshotPending(w, s, slot);
            if (s->Mark[slot] != w->MarkEpoch)
            {
                s->Mark[slot] = w->MarkEpoch;
                var n = w->Changed.Length;
                w->Changed.Resize(n + 1);
                w->Changed.Pointer[n] = slot;
            }

            RestoreColumns(s, entry);
        }

        w->Journal.Resize(0);
        RestoreSchedules(w, s);
        if (recording) ShiftSchedules(s, w->Tick - w->RecordTick);
        RebuildTimed(w);

        s->Free.Clear();
        for (var i = 0; i < s->Count; i++)
            if (s->Alive[i] == 0 && s->Gen[i] != uint.MaxValue)
                s->Free.Push(i);
    }

    private static void RestoreColumns(SourceColumns* s, JournalEntry* entry)
    {
        var slot = entry->Slot;
        s->X[slot] = entry->X;
        s->Y[slot] = entry->Y;
        s->Stamp[slot] = entry->Stamp;
        s->Layer[slot] = entry->Layer;
        s->Gain[slot] = entry->Gain;
        s->Angle[slot] = entry->Angle;
        s->Scale[slot] = entry->Scale;
        s->Alive[slot] = entry->Alive;
        if (entry->Alive != 0) s->Gen[slot] = entry->Gen;
    }

    internal static bool UsesStamp(byte stamp)
    {
        for (var wi = 0; wi < _worldCount; wi++)
        {
            var w = Runtime.Worlds + wi;
            var s = &w->Sources;
            for (var i = 0; i < s->Count; i++)
                if (s->Alive[i] != 0 && s->Stamp[i] == stamp) return true;
            if (References(w->Pending, stamp) || References(w->Journal, stamp)) return true;
        }

        return false;
    }

    internal static void FlushKernels()
    {
        for (var wi = 0; wi < _worldCount; wi++)
        {
            var w = Runtime.Worlds + wi;
            if (w->Grids != null) w->Kernels.Clear();
        }
    }

    private static bool References(NativeBuffer<JournalEntry> entries, byte stamp)
    {
        for (var i = 0; i < entries.Length; i++)
            if (entries.Pointer[i].Stamp == stamp) return true;
        return false;
    }

    private static bool TrySource(byte world, SourceId source, out WorldCtx* w, out SourceColumns* s, out int index)
    {
        w = GetContext(world);
        s = w == null ? null : &w->Sources;
        index = (int)source.Slot;
        return s != null && (uint)index < (uint)s->Count && s->Alive[index] != 0 && source.Generation == s->Gen[index];
    }

    private static LayerData* EnsureDirty(GridCtx* g, byte layer)
    {
        var ld = g->Layers + layer;
        if (ld->InDirty == null)
            ld->InDirty = (int*)NativeHeap.AllocZeroed((nuint)g->TileCount * sizeof(int));
        return ld;
    }

    private static void EnsureEpochs(GridCtx* g, LayerData* ld)
    {
        if (ld->Epochs != null) return;

        ld->Epochs = (int*)NativeHeap.AllocZeroed((nuint)g->TileCount * sizeof(int));
        ld->EpochTree.Ensure(g->TilesPerSide);
    }

    private static void EnsureDirectory(GridCtx* g, LayerData* ld)
    {
        if (ld->Directory != null) return;

        ld->Directory = (int*)NativeHeap.AllocZeroed((nuint)g->TileCount * sizeof(int));
        new Span<int>(ld->Directory, g->TileCount).Fill(-1);
    }

    private static TilePlanes* EnsureSet(GridCtx* g, LayerData* ld, int tile)
    {
        var set = ld->SetOf(tile);
        if (set != null) return set;

        EnsureDirectory(g, ld);
        var index = ld->Sets.Take(sizeof(TilePlanes));
        set = (TilePlanes*)ld->Sets.Unit(index);
        *set = ld->Derived != 0 ? ld->Store.TakeDerived() : ld->Store.Take();
        ld->Directory[tile] = index;
        ld->LivePages++;
        return set;
    }

    internal static void DropPage(LayerData* ld, int tile)
    {
        var index = ld->Directory[tile];
        if (index < 0) return;

        var set = (TilePlanes*)ld->Sets.Unit(index);
        ld->Store.Give(*set);
        ld->Sets.Give(index);
        ld->Directory[tile] = -1;
        ld->LivePages--;
    }

    internal static int DirtyIndex(GridCtx* g, LayerData* ld, int tile)
    {
        EnsureEpochs(g, ld);
        var index = ld->InDirty[tile];
        if (index != 0) return index - 1;

        var n = ld->Dirty.Length;
        ld->Dirty.Resize(n + 1);
        ld->Rects.Resize(n + 1);
        ld->Dirty.Pointer[n] = tile;
        ld->Rects.Pointer[n] = new TileRect
        {
            X0 = TileBake.TileSize,
            Y0 = TileBake.TileSize,
            X1 = 0,
            Y1 = 0,
        };
        ld->InDirty[tile] = n + 1;
        return n;
    }

    private static void MarkDirty(GridCtx* g, LayerData* ld, int tile) => DirtyIndex(g, ld, tile);

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    public static void Process(byte world)
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
        ProcessStages.Measure(ref ProcessStages.PrepareTicks, stamp);

        stamp = ProcessStages.Mark();
        PublishDirty(w);
        ProcessStages.Measure(ref ProcessStages.ApplyTicks, stamp);

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

    private static void EnsureSourceCapacity(SourceColumns* s, WorldCtx* w, int required)
    {
        var initialized = s->Slots;
        if (required <= initialized) return;
        var capacity = Math.Min(MaxSourceSlots, (required + SourceColumns.ChunkMask) & ~SourceColumns.ChunkMask);
        s->AddChunks((capacity - initialized) >> SourceColumns.ChunkBits);
        s->HotStamp.Resize(capacity);
        s->SchedStamp.Resize(capacity);
        s->Timed.Resize(capacity);
        s->FadeFrom.Resize(capacity);
        s->FadeTo.Resize(capacity);
        s->FadeStart.Resize(capacity);
        s->FadeTicks.Resize(capacity);
        s->ExpireAt.Resize(capacity);
        s->TimedAt.Resize(capacity);
        s->Free.Reserve(capacity);
        for (var gi = 0; gi < w->GridCount; gi++)
            if (w->Grids[gi].Records.Length < capacity)
                w->Grids[gi].Records.Resize(capacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short Query(byte world, byte grid, byte layer, int x, int y)
    {
        var w = GetContext(world);
        if (w == null || grid >= w->GridCount || layer >= w->LayerCount) return 0;

        var g = w->Grids + grid;
        if ((uint)x >= (uint)g->Size || (uint)y >= (uint)g->Size) return 0;

        var page = g->Layers[layer].PageOf((y >> TileBake.TileBits) * g->TilesPerSide + (x >> TileBake.TileBits));
        if (page == null) return 0;

        return page[(y & (TileBake.TileSize - 1)) * TileBake.TileSize + (x & (TileBake.TileSize - 1))];
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    public static long Query(byte world, byte grid, byte layer, int x, int y, int width, int height)
    {
        var w = GetContext(world);
        if (w == null || grid >= w->GridCount || layer >= w->LayerCount || width <= 0 || height <= 0) return 0;

        var g = w->Grids + grid;
        var x1 = (int)Math.Min((long)x + width, g->Size);
        var y1 = (int)Math.Min((long)y + height, g->Size);
        var x0 = Math.Max(x, 0);
        var y0 = Math.Max(y, 0);
        if (x1 <= x0 || y1 <= y0) return 0;

        var ld = g->Layers + layer;
        if (ld->LivePages == 0) return 0;
        var tps = g->TilesPerSide;
        var sum = 0L;
        if (x0 == 0 && y0 == 0 && x1 == g->Size && y1 == g->Size)
        {
            var directory = ld->Directory;
            for (var tile = 0; tile < g->TileCount; tile++)
            {
                var index = directory[tile];
                if (index < 0) continue;
                sum += ld->SummaryOf(tile)->Sum;
            }

            return sum;
        }

        for (var ty = y0 >> TileBake.TileBits; ty <= (y1 - 1) >> TileBake.TileBits; ty++)
        for (var tx = x0 >> TileBake.TileBits; tx <= (x1 - 1) >> TileBake.TileBits; tx++)
        {
            var page = ld->PageOf(ty * tps + tx);
            if (page == null) continue;

            var lx0 = Math.Max(x0 - tx * TileBake.TileSize, 0);
            var ly0 = Math.Max(y0 - ty * TileBake.TileSize, 0);
            var lx1 = Math.Min(x1 - tx * TileBake.TileSize, TileBake.TileSize);
            var ly1 = Math.Min(y1 - ty * TileBake.TileSize, TileBake.TileSize);
            if (lx0 == 0 && ly0 == 0 && lx1 == TileBake.TileSize && ly1 == TileBake.TileSize)
            {
                sum += ld->SummaryOf(ty * tps + tx)->Sum;
            }
            else if (lx0 == 0 && lx1 == TileBake.TileSize && ld->Derived == 0)
            {
                var rowSums = ld->SummaryOf(ty * tps + tx)->RowSums;
                for (var ly = ly0; ly < ly1; ly++) sum += rowSums[ly];
            }
            else
            {
                sum += PartialSum(page, lx0, ly0, lx1, ly1);
            }
        }

        return sum;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static long PartialSum(short* page, int lx0, int ly0, int lx1, int ly1)
    {
#if NET
        if (Avx2.IsSupported)
        {
            var acc = Vector256<int>.Zero;
            var acc128 = Vector128<int>.Zero;
            var tail = 0;
            for (var ly = ly0; ly < ly1; ly++)
            {
                var row = page + ly * TileBake.TileSize + lx0;
                var count = lx1 - lx0;
                var i = 0;
                for (; i + 16 <= count; i += 16)
                {
                    var widened = Vector256.Widen(Avx.LoadVector256(row + i));
                    acc += widened.Item1 + widened.Item2;
                }

                for (; i + 8 <= count; i += 8)
                {
                    var widened = Vector128.Widen(Sse2.LoadVector128(row + i));
                    acc128 += widened.Item1 + widened.Item2;
                }

                for (; i < count; i++) tail += row[i];
            }

            var lanes = acc.GetLower() + acc.GetUpper() + acc128;
            return (long)lanes[0] + lanes[1] + lanes[2] + lanes[3] + tail;
        }

        if (Sse2.IsSupported || AdvSimd.IsSupported)
        {
            var acc = Vector128<int>.Zero;
            var tail = 0;
            for (var ly = ly0; ly < ly1; ly++)
            {
                var row = page + ly * TileBake.TileSize + lx0;
                var count = lx1 - lx0;
                var i = 0;
                for (; i + 8 <= count; i += 8)
                {
                    var widened = Vector128.Widen(LoadShorts(row + i));
                    acc += widened.Item1 + widened.Item2;
                }

                for (; i < count; i++) tail += row[i];
            }

            return (long)(acc[0] + acc[1] + acc[2] + acc[3]) + tail;
        }
#endif
        var sum = 0;
        for (var ly = ly0; ly < ly1; ly++)
        {
            var row = page + ly * TileBake.TileSize + lx0;
            for (var lx = 0; lx < lx1 - lx0; lx++) sum += row[lx];
        }

        return sum;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    public static void QueryRegion(
        byte world, byte grid, byte layer, int x, int y, int width, int height, short* destination)
    {
        var w = GetContext(world);
        if (w == null || grid >= w->GridCount || layer >= w->LayerCount || width <= 0 || height <= 0) return;

        var g = w->Grids + grid;
        var ld = g->Layers + layer;
        var tps = g->TilesPerSide;
        var mask = TileBake.TileSize - 1;

        var row = 0;
        while (row < height)
        {
            var cy = y + row;
            if ((uint)cy >= (uint)g->Size)
            {
                var outside = cy < 0 ? (int)Math.Min(-(long)cy, height - row) : height - row;
                for (var r = 0; r < outside; r++) FillRun(destination + (long)(row + r) * width, width);
                row += outside;
                continue;
            }

            var ty = cy >> TileBake.TileBits;
            var band = Math.Min(((ty + 1) << TileBake.TileBits) - cy, height - row);
            var cx = x;
            var col = 0;
            while (col < width)
            {
                int run;
                short* page = null;
                if (cx < 0)
                {
                    run = (int)Math.Min(-(long)cx, width - col);
                }
                else if (cx >= g->Size)
                {
                    run = width - col;
                }
                else
                {
                    run = Math.Min((((cx >> TileBake.TileBits) + 1) << TileBake.TileBits) - cx, width - col);
                    page = ld->PageOf(ty * tps + (cx >> TileBake.TileBits));
                }

                for (var r = 0; r < band; r++)
                {
                    var dst = destination + (long)(row + r) * width + col;
                    if (page != null) CopyRun(dst, page + ((cy + r) & mask) * TileBake.TileSize + (cx & mask), run);
                    else FillRun(dst, run);
                }

                cx += run;
                col += run;
            }

            row += band;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyRun(short* destination, short* source, int count)
    {
#if NET
        if (Avx2.IsSupported)
        {
            var i = 0;
            for (; i + 16 <= count; i += 16) Avx.Store(destination + i, Avx.LoadVector256(source + i));
            for (; i < count; i++) destination[i] = source[i];
            return;
        }

        if (Sse2.IsSupported || AdvSimd.IsSupported)
        {
            var i = 0;
            for (; i + 8 <= count; i += 8) StoreShorts(destination + i, LoadShorts(source + i));
            for (; i < count; i++) destination[i] = source[i];
            return;
        }
#endif
        for (var i = 0; i < count; i++) destination[i] = source[i];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FillRun(short* destination, int count)
    {
#if NET
        if (Avx2.IsSupported)
        {
            var i = 0;
            for (; i + 16 <= count; i += 16) Avx.Store(destination + i, Vector256<short>.Zero);
            for (; i < count; i++) destination[i] = 0;
            return;
        }

        if (Sse2.IsSupported || AdvSimd.IsSupported)
        {
            var i = 0;
            for (; i + 8 <= count; i += 8) StoreShorts(destination + i, Vector128<short>.Zero);
            for (; i < count; i++) destination[i] = 0;
            return;
        }
#endif
        for (var i = 0; i < count; i++) destination[i] = 0;
    }

#if NET
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> LoadShorts(short* p)
    {
        if (Sse2.IsSupported) return Sse2.LoadVector128(p);
        return AdvSimd.LoadVector128(p);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreShorts(short* p, Vector128<short> v)
    {
        if (Sse2.IsSupported) Sse2.Store(p, v);
        else AdvSimd.Store(p, v);
    }
#endif

    public static short QueryAt(byte world, byte grid, byte layer, float x, float y)
    {
        var w = GetContext(world);
        if (w == null || grid >= w->GridCount || layer >= w->LayerCount) return 0;

        var g = w->Grids + grid;
        var cx = (int)(CellQ8(x, g->OriginX, g->ScaleQ8) >> 8);
        var cy = (int)(CellQ8(y, g->OriginY, g->ScaleQ8) >> 8);
        return Query(world, grid, layer, cx, cy);
    }

    public static short QueryMax(byte world, byte grid, byte layer, out int x, out int y)
    {
        x = 0;
        y = 0;
        var w = GetContext(world);
        if (w == null || grid >= w->GridCount || layer >= w->LayerCount) return 0;

        var g = w->Grids + grid;
        var ld = g->Layers + layer;
        return MaxQueries.Best(ld->Max, g->TilesPerSide, ld, out x, out y);
    }

    public static short QueryMax(byte world, byte grid, byte layer,
        int x, int y, int width, int height, out int bestX, out int bestY)
    {
        bestX = 0;
        bestY = 0;
        var w = GetContext(world);
        if (w == null || grid >= w->GridCount || layer >= w->LayerCount) return 0;

        var g = w->Grids + grid;
        var x0 = Math.Max(x, 0);
        var y0 = Math.Max(y, 0);
        var x1 = Math.Min((long)x + width, g->Size);
        var y1 = Math.Min((long)y + height, g->Size);
        if (x1 <= x0 || y1 <= y0) return 0;

        var ld = g->Layers + layer;
        return MaxQueries.Best(ld->Max, g->TilesPerSide, ld, x0, y0, (int)x1, (int)y1, out bestX, out bestY);
    }

    public static void QueryGradient(byte world, byte grid, byte layer, float x, float y, out int gx, out int gy)
    {
        gx = 0;
        gy = 0;
        var w = GetContext(world);
        if (w == null || grid >= w->GridCount || layer >= w->LayerCount) return;

        var g = w->Grids + grid;
        var cx = (int)(CellQ8(x, g->OriginX, g->ScaleQ8) >> 8);
        var cy = (int)(CellQ8(y, g->OriginY, g->ScaleQ8) >> 8);
        gx = Query(world, grid, layer, cx + 1, cy) - Query(world, grid, layer, cx - 1, cy);
        gy = Query(world, grid, layer, cx, cy + 1) - Query(world, grid, layer, cx, cy - 1);
    }

    public static int ChangedTiles(byte world, byte grid, byte layer, int since, int* destination)
    {
        var w = GetContext(world);
        if (w == null || grid >= w->GridCount || layer >= w->LayerCount) return 0;

        var g = w->Grids + grid;
        var epochs = g->Layers[layer].Epochs;
        if (epochs == null) return 0;

        return CollectNewer(g, g->Layers + layer, since, destination);
    }

    public static int ChangedTiles(byte world, byte grid, byte layer, int* destination)
    {
        var w = GetContext(world);
        if (w == null || grid >= w->GridCount || layer >= w->LayerCount) return 0;

        var changed = w->Grids[grid].Layers[layer].Changed;
        var count = changed.Length;
        if (destination != null && count > 0)
            Buffer.MemoryCopy(changed.Pointer, destination, (long)count * sizeof(int), (long)count * sizeof(int));
        return count;
    }
}
