using System.Runtime.CompilerServices;
#if NET
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Gi;

internal struct SourceColumns
{
    public NativeBuffer<float> X;
    public NativeBuffer<float> Y;
    public NativeBuffer<byte> Stamp;
    public NativeBuffer<byte> Layer;
    public NativeBuffer<byte> Gain;
    public NativeBuffer<ushort> Angle;
    public NativeBuffer<ushort> Scale;
    public NativeBuffer<byte> Timed;
    public NativeBuffer<sbyte> FadeFrom;
    public NativeBuffer<sbyte> FadeTo;
    public NativeBuffer<int> FadeStart;
    public NativeBuffer<int> FadeTicks;
    public NativeBuffer<int> ExpireAt;
    public NativeBuffer<int> Due;
    public NativeBuffer<byte> Alive;
    public NativeBuffer<int> Free;
    public NativeBuffer<byte> Gen;
    public int Count;

    public void Release()
    {
        X.Free();
        Y.Free();
        Stamp.Free();
        Layer.Free();
        Gain.Free();
        Angle.Free();
        Scale.Free();
        Timed.Free();
        FadeFrom.Free();
        FadeTo.Free();
        FadeStart.Free();
        FadeTicks.Free();
        ExpireAt.Free();
        Due.Free();
        Alive.Free();
        Free.Free();
        Gen.Free();
    }
}

internal unsafe struct LayerData
{
    public PageMap Pages;
    public byte* InDirty;
    public int* Epochs;
    public NativeBuffer<int> Dirty;
    public NativeBuffer<int> Changed;
    public MaxPyramid Max;
    public NativeBuffer<SourceBin> Bins;
    public NativeBuffer<int> BinEntries;
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
    public NativeBuffer<Placement> Prepared;
    public NativeBuffer<PreparedFootprint> Footprints;
    public WorldCtx* Owner;
}

internal struct DepositOp
{
    public int Slot;
    public float FromX;
    public float FromY;
    public float ToX;
    public float ToY;
    public byte FromStamp;
    public byte FromLayer;
    public byte ToStamp;
    public byte ToLayer;
    public sbyte FromGain;
    public sbyte ToGain;
    public byte AliveFrom;
    public byte AliveTo;
    public byte FromGen;
    public ushort FromAngle;
    public ushort ToAngle;
    public ushort FromScale;
    public ushort ToScale;
}

internal unsafe struct WorldCtx
{
    public const int MaxLayers = 32;

    public GridCtx* Grids;
    public int GridCount;
    public int LayerCount;
    public LayerRecipe* Recipes;
    public int DerivedCount;
    public SourceColumns Sources;
    public NativeBuffer<DepositOp> Ops;
    public NativeBuffer<int> Pending;
    public NativeBuffer<DepositOp> Journal;
    public NativeBuffer<ScheduleRecord> ScheduleJournal;
    public NativeBuffer<Alarm> Timers;
    public NativeBuffer<int> TimerPositions;
    public NativeBuffer<int> TimerExpired;
    public NativeBuffer<int> TimerSort;
    public int Tick;
    public int RecordTick;
    public byte Recording;
    public int FreeHead;
    public int FreeTail;
    public NativeBuffer<SourceBatchEntry> BatchSources;
    public NativeBuffer<SourceBatchLink> BatchCommands;
    public NativeBuffer<int> BatchLookup;
    public byte Snapshot;
}

public static unsafe partial class World
{
    public const int MaxLayers = ValidationRules.MaxLayers;
    internal const int MaxWorlds = ValidationRules.MaxWorlds;
    internal const int MaxGrids = ValidationRules.MaxGrids;
    private const int MaxGain = ValidationRules.MaxGain;
    private const ushort UnitScale = 256;
    internal const int MaxSourceSlots = 1 << 24;
    internal const int SourceIndexMask = MaxSourceSlots - 1;
    internal const int Cells = TileBake.TileSize * TileBake.TileSize;
    internal const int PageBytes = Cells * sizeof(short);
    internal const int PageOffset = 0;
    internal const int SumOffset = PageOffset + PageBytes;
    private const int SumSlotBytes = 64;
    internal const int MaxOffset = SumOffset + 8;
    internal const int MinOffset = MaxOffset + sizeof(short);
    internal const int RowSumOffset = SumOffset + SumSlotBytes;
    internal const int RowMaxOffset = RowSumOffset + TileBake.TileSize * sizeof(int);
    internal const int HeaderBytes = RowMaxOffset + TileBake.TileSize * sizeof(short);

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
        w->FreeHead = -1;
        w->FreeTail = -1;
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
        w->Ops.Free();
        w->Pending.Free();
        w->Journal.Free();
        w->ScheduleJournal.Free();
        w->Timers.Free();
        w->TimerPositions.Free();
        w->TimerExpired.Free();
        w->TimerSort.Free();
        w->BatchSources.Free();
        w->BatchCommands.Free();
        w->BatchLookup.Free();
        *w = default;
        Runtime.WorldRevisions[world]++;
        return true;
    }

    public static int Revision(byte world) => world < _worldCount ? Runtime.WorldRevisions[world] : 0;

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

    public static bool TryGetSourceSpec(byte world, int source, out SourceSpec spec)
    {
        spec = default;
        if (!TrySource(world, source, out var w, out var s, out var i)) return false;

        spec = new SourceSpec(
            s->X.Pointer[i],
            s->Y.Pointer[i],
            s->Angle.Pointer[i] * ((float)Math.PI * 2 / 65536),
            s->Scale.Pointer[i] / 256f,
            (sbyte)s->Gain.Pointer[i],
            s->Layer.Pointer[i],
            s->Stamp.Pointer[i]);
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
        g->Prepared.Free();
        g->Footprints.Free();
        for (var l = 0; l < layerCount; l++) FreeLayer(g->Layers + l);
        NativeHeap.Free(g->Layers);
    }

    private static void FreeLayer(LayerData* ld)
    {
        var pages = &ld->Pages;
        var used = pages->Used;
        var blocks = pages->Blocks;
        for (var i = 0; i < pages->SlotCount; i++)
            if (used[i] == PageMap.Live) FreeBlock(blocks[i]);
        pages->Reset();
        NativeHeap.Free(ld->InDirty);
        NativeHeap.Free(ld->Epochs);
        ld->Dirty.Free();
        ld->Changed.Free();
        ld->Max.Reset();
        ld->Bins.Free();
        ld->BinEntries.Free();
    }

    internal static byte AddGrid(byte world, int power, float x, float y, float size)
    {
        if (!ValidationRules.IsPower(power)) throw new ArgumentOutOfRangeException(nameof(power));
        if (!ValidationRules.IsGrid(power, size)) throw new ArgumentOutOfRangeException(nameof(size));
        if (!float.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!float.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));

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
        g->ScaleQ8 = (int)(g->Scale * 256f);
        g->TilesPerSide = g->Size >> TileBake.TileBits;
        g->TileCount = g->TilesPerSide * g->TilesPerSide;
        g->Layers = (LayerData*)NativeHeap.AllocZeroed((nuint)(MaxLayers * sizeof(LayerData)));
        g->Owner = w;
        for (var layer = 0; layer < MaxLayers; layer++) g->Layers[layer].Pages.Configure(g->TileCount);
        return id;
    }

    internal static byte AddLayer(byte world)
    {
        var w = GetContext(world);
        if (w == null) throw new ArgumentOutOfRangeException(nameof(world));
        if (w->LayerCount >= MaxLayers) throw new InvalidOperationException("Layer limit reached.");
        var id = w->LayerCount++;
        w->Recipes[id] = new LayerRecipe { Reads = 1u << id };
        return (byte)id;
    }

    public static int Place(byte world, byte layer, float x, float y, byte stamp, sbyte gain)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount || !StampCatalog.Valid(stamp) || IsDerived(w, layer) ||
            !float.IsFinite(x) || !float.IsFinite(y)) return -1;

        var s = &w->Sources;
        int i;
        if (w->FreeHead >= 0)
        {
            i = w->FreeHead;
            w->FreeHead = s->Free.Pointer[i];
            if (w->FreeHead < 0) w->FreeTail = -1;
        }
        else
        {
            if (s->Count >= MaxSourceSlots) return -1;
            if (s->Count == s->X.Length) GrowSources(s, w);
            i = s->Count++;
        }

        var g = (byte)Math.Clamp((int)gain, -MaxGain, MaxGain);
        var gen = (byte)((s->Gen.Pointer[i] + 1) & 127);
        s->Gen.Pointer[i] = gen;
        s->X.Pointer[i] = x;
        s->Y.Pointer[i] = y;
        s->Stamp.Pointer[i] = stamp;
        s->Layer.Pointer[i] = layer;
        s->Gain.Pointer[i] = g;
        s->Angle.Pointer[i] = 0;
        s->Scale.Pointer[i] = UnitScale;
        JournalSchedule(w, s, i);
        s->Timed.Pointer[i] = 0;
        s->Alive.Pointer[i] = 1;
        EnqueuePlace(w, i, x, y, stamp, layer, (sbyte)g);
        return (gen << 24) | i;
    }

    public static void Move(byte world, int source, float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) return;
        if (!TrySource(world, source, out var w, out var s, out var i)) return;

        var oldX = s->X.Pointer[i];
        var oldY = s->Y.Pointer[i];
        if (oldX == x && oldY == y) return;
        EnqueueMutation(w, s, i, x, y, (sbyte)s->Gain.Pointer[i], s->Angle.Pointer[i], s->Scale.Pointer[i], true);
        s->X.Pointer[i] = x;
        s->Y.Pointer[i] = y;
    }

    public static void SetGain(byte world, int source, sbyte gain)
    {
        if (!TrySource(world, source, out var w, out var s, out var i)) return;

        var next = (byte)Math.Clamp((int)gain, -MaxGain, MaxGain);
        if ((s->Timed.Pointer[i] & Fading) != 0)
        {
            JournalSchedule(w, s, i);
            s->Timed.Pointer[i] &= unchecked((byte)~Fading);
            Schedule(w, s, i);
        }

        if (next == s->Gain.Pointer[i]) return;

        EnqueueMutation(w, s, i, s->X.Pointer[i], s->Y.Pointer[i], (sbyte)next, s->Angle.Pointer[i], s->Scale.Pointer[i], true);
        s->Gain.Pointer[i] = next;
    }

    public static void Turn(byte world, int source, float angle)
    {
        if (!TrySource(world, source, out var w, out var s, out var i) || !float.IsFinite(angle)) return;

        var turns = angle * (1.0 / (2 * Math.PI));
        turns -= Math.Floor(turns);
        var next = (ushort)((int)(turns * 65536.0) & 0xFFFF);
        if (next == s->Angle.Pointer[i]) return;

        EnqueueMutation(w, s, i, s->X.Pointer[i], s->Y.Pointer[i], (sbyte)s->Gain.Pointer[i], next, s->Scale.Pointer[i], true);
        s->Angle.Pointer[i] = next;
    }

    public static void Scale(byte world, int source, float scale)
    {
        if (!TrySource(world, source, out var w, out var s, out var i) || !float.IsFinite(scale)) return;

        var q8 = Math.Floor(scale * 256.0);
        var next = (ushort)(q8 < 1 ? 1 : q8 > ushort.MaxValue ? ushort.MaxValue : q8);
        if (next == s->Scale.Pointer[i]) return;

        EnqueueMutation(w, s, i, s->X.Pointer[i], s->Y.Pointer[i], (sbyte)s->Gain.Pointer[i], s->Angle.Pointer[i], next, true);
        s->Scale.Pointer[i] = next;
    }

    public static void Remove(byte world, int source)
    {
        if (!TrySource(world, source, out var w, out var s, out var i)) return;

        EnqueueMutation(w, s, i, s->X.Pointer[i], s->Y.Pointer[i], 0, s->Angle.Pointer[i], s->Scale.Pointer[i], false);
        if (s->Timed.Pointer[i] != 0)
        {
            JournalSchedule(w, s, i);
            s->Timed.Pointer[i] = 0;
            Schedule(w, s, i);
        }

        s->Alive.Pointer[i] = 0;
        EnqueueFree(w, s, i);
    }

    private static void EnqueueFree(WorldCtx* w, SourceColumns* s, int slot)
    {
        s->Free.Pointer[slot] = -1;
        if (w->FreeTail >= 0) s->Free.Pointer[w->FreeTail] = slot;
        else w->FreeHead = slot;
        w->FreeTail = slot;
    }

    private static void EnqueuePlace(WorldCtx* w, int slot, float x, float y, byte stamp, byte layer, sbyte gain)
    {
        var pending = w->Pending.Pointer[slot];
        if (pending != 0)
        {
            var op = w->Ops.Pointer + pending - 1;
            op->ToX = x;
            op->ToY = y;
            op->ToStamp = stamp;
            op->ToLayer = layer;
            op->ToGain = gain;
            op->ToAngle = 0;
            op->ToScale = UnitScale;
            op->AliveTo = 1;
            return;
        }

        var n = w->Ops.Length;
        w->Ops.Resize(n + 1);
        var freshOp = w->Ops.Pointer + n;
        freshOp->Slot = slot;
        freshOp->FromGain = 0;
        freshOp->FromX = x;
        freshOp->FromY = y;
        freshOp->FromStamp = stamp;
        freshOp->FromLayer = layer;
        freshOp->ToX = x;
        freshOp->ToY = y;
        freshOp->ToStamp = stamp;
        freshOp->ToLayer = layer;
        freshOp->ToGain = gain;
        freshOp->FromAngle = 0;
        freshOp->ToAngle = 0;
        freshOp->FromScale = UnitScale;
        freshOp->ToScale = UnitScale;
        freshOp->AliveFrom = 0;
        freshOp->AliveTo = 1;
        freshOp->FromGen = w->Sources.Gen.Pointer[slot];
        w->Pending.Pointer[slot] = n + 1;
    }

    private static void EnqueueMutation(
        WorldCtx* w, SourceColumns* s, int slot, float x, float y, sbyte gain, ushort angle, ushort scale, bool alive)
    {
        var pending = w->Pending.Pointer[slot];
        if (pending != 0)
        {
            var op = w->Ops.Pointer + pending - 1;
            op->ToX = x;
            op->ToY = y;
            op->ToGain = gain;
            op->ToAngle = angle;
            op->ToScale = scale;
            op->AliveTo = (byte)(alive ? 1 : 0);
            return;
        }

        var stamp = s->Stamp.Pointer[slot];
        var layer = s->Layer.Pointer[slot];
        var n = w->Ops.Length;
        w->Ops.Resize(n + 1);
        var op2 = w->Ops.Pointer + n;
        op2->Slot = slot;
        op2->FromX = s->X.Pointer[slot];
        op2->FromY = s->Y.Pointer[slot];
        op2->FromStamp = stamp;
        op2->FromLayer = layer;
        op2->FromGain = (sbyte)s->Gain.Pointer[slot];
        op2->FromAngle = s->Angle.Pointer[slot];
        op2->FromScale = s->Scale.Pointer[slot];
        op2->ToX = x;
        op2->ToY = y;
        op2->ToStamp = stamp;
        op2->ToLayer = layer;
        op2->ToGain = gain;
        op2->ToAngle = angle;
        op2->ToScale = scale;
        op2->AliveFrom = 1;
        op2->AliveTo = (byte)(alive ? 1 : 0);
        op2->FromGen = s->Gen.Pointer[slot];
        w->Pending.Pointer[slot] = n + 1;
    }


    public static void Clear(byte world)
    {
        var w = GetContext(world);
        if (w == null) return;
        Runtime.WorldRevisions[world]++;
        var s = &w->Sources;
        new Span<byte>(s->Alive.Pointer, s->Count).Clear();
        s->Count = 0;
        w->FreeHead = -1;
        w->FreeTail = -1;
        w->Ops.Resize(0);
        w->Journal.Resize(0);
        w->ScheduleJournal.Resize(0);
        w->Timers.Resize(0);
        w->TimerPositions.Span.Clear();
        w->TimerExpired.Resize(0);
        w->TimerSort.Resize(0);
        w->Recording = 0;
        w->Snapshot = 0;
        new Span<int>(w->Pending.Pointer, w->Pending.Capacity).Clear();

        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            g->Prepared.Resize(0);
            g->Footprints.Resize(0);
            for (var l = 0; l < w->LayerCount; l++)
            {
                var ld = g->Layers + l;
                var span = ld->Dirty.Span;
                foreach (var tile in span) ld->InDirty[tile] = 0;
                ld->Dirty.Resize(0);
                ld->Changed.Resize(0);

                var pages = &ld->Pages;
                var used = pages->Used;
                var blocks = pages->Blocks;
                var slots = pages->SlotCount;
                var keys = pages->Keys;
                for (var i = 0; i < slots; i++)
                {
                    if (used[i] != PageMap.Live) continue;
                    FreeBlock(blocks[i]);
                    ld->Epochs[keys[i]] = w->Tick + 1;
                }

                pages->Reset();
                ld->Max.Reset();
                ld->Bins.Span.Clear();
                ld->BinEntries.Resize(0);
            }
        }
    }

    public static void Record(byte world)
    {
        var w = GetContext(world);
        if (w == null) return;
        w->Journal.Resize(0);
        w->ScheduleJournal.Resize(0);
        w->RecordTick = w->Tick;
        w->Recording = 1;
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

        var ops = w->Ops.Pointer;
        for (var i = w->Ops.Length - 1; i >= 0; i--)
        {
            var op = ops + i;
            RestoreSlot(s, op);
            w->Pending.Pointer[op->Slot] = 0;
        }
        w->Ops.Resize(0);

        var journal = w->Journal.Pointer;
        for (var j = w->Journal.Length - 1; j >= 0; j--)
        {
            var op = journal + j;
            RestoreSlot(s, op);
            EnqueueRewind(w, op);
        }

        w->Journal.Resize(0);
        RestoreSchedules(w, s);
        if (w->Recording != 0) ShiftSchedules(s, w->Tick - w->RecordTick);
        w->Recording = 0;
        RebuildTimers(w);

        w->FreeHead = -1;
        w->FreeTail = -1;
        for (var i = 0; i < s->Count; i++)
            if (s->Alive.Pointer[i] == 0)
                EnqueueFree(w, s, i);
    }

    private static void RestoreSlot(SourceColumns* s, DepositOp* op)
    {
        var slot = op->Slot;
        s->X.Pointer[slot] = op->FromX;
        s->Y.Pointer[slot] = op->FromY;
        s->Stamp.Pointer[slot] = op->FromStamp;
        s->Layer.Pointer[slot] = op->FromLayer;
        s->Gain.Pointer[slot] = (byte)op->FromGain;
        s->Angle.Pointer[slot] = op->FromAngle;
        s->Scale.Pointer[slot] = op->FromScale;
        s->Alive.Pointer[slot] = op->AliveFrom;
        if (op->AliveFrom != 0) s->Gen.Pointer[slot] = op->FromGen;
    }

    private static void EnqueueRewind(WorldCtx* w, DepositOp* entry)
    {
        var pending = w->Pending.Pointer[entry->Slot];
        if (pending != 0)
        {
            var op = w->Ops.Pointer + pending - 1;
            op->ToX = entry->FromX;
            op->ToY = entry->FromY;
            op->ToStamp = entry->FromStamp;
            op->ToLayer = entry->FromLayer;
            op->ToGain = entry->FromGain;
            op->ToAngle = entry->FromAngle;
            op->ToScale = entry->FromScale;
            op->AliveTo = entry->AliveFrom;
            return;
        }

        var n = w->Ops.Length;
        w->Ops.Resize(n + 1);
        var op2 = w->Ops.Pointer + n;
        op2->Slot = entry->Slot;
        op2->FromX = entry->ToX;
        op2->FromY = entry->ToY;
        op2->FromStamp = entry->ToStamp;
        op2->FromLayer = entry->ToLayer;
        op2->FromGain = entry->ToGain;
        op2->ToX = entry->FromX;
        op2->ToY = entry->FromY;
        op2->ToStamp = entry->FromStamp;
        op2->ToLayer = entry->FromLayer;
        op2->ToGain = entry->FromGain;
        op2->FromAngle = entry->ToAngle;
        op2->FromScale = entry->ToScale;
        op2->ToAngle = entry->FromAngle;
        op2->ToScale = entry->FromScale;
        op2->AliveFrom = entry->AliveTo;
        op2->AliveTo = entry->AliveFrom;
        op2->FromGen = entry->FromGen;
        w->Pending.Pointer[entry->Slot] = n + 1;
    }

    internal static bool UsesStamp(byte stamp)
    {
        for (var wi = 0; wi < _worldCount; wi++)
        {
            var w = Runtime.Worlds + wi;
            var s = &w->Sources;
            for (var i = 0; i < s->Count; i++)
                if (s->Alive.Pointer[i] != 0 && s->Stamp.Pointer[i] == stamp) return true;
            if (References(w->Ops, stamp) || References(w->Journal, stamp)) return true;
        }

        return false;
    }

    private static bool References(NativeBuffer<DepositOp> ops, byte stamp)
    {
        for (var i = 0; i < ops.Length; i++)
            if (ops.Pointer[i].FromStamp == stamp || ops.Pointer[i].ToStamp == stamp) return true;
        return false;
    }

    private static bool TrySource(byte world, int source, out WorldCtx* w, out SourceColumns* s, out int index)
    {
        w = GetContext(world);
        s = w == null ? null : &w->Sources;
        index = source & SourceIndexMask;
        return s != null && (uint)index < (uint)s->Count && s->Alive.Pointer[index] != 0 &&
            (source & ~SourceIndexMask) == (s->Gen.Pointer[index] << 24);
    }

    private static LayerData* EnsureDirty(GridCtx* g, byte layer)
    {
        var ld = g->Layers + layer;
        if (ld->InDirty != null) return ld;

        ld->InDirty = (byte*)NativeHeap.AllocZeroed((nuint)g->TileCount);
        ld->Epochs = (int*)NativeHeap.AllocZeroed((nuint)g->TileCount * sizeof(int));
        return ld;
    }

    internal static int* RowSumOf(byte* block) => (int*)(block + RowSumOffset);

    internal static short* RowMaxOf(byte* block) => (short*)(block + RowMaxOffset);

    private static void RefreshRows(byte* block)
    {
        var page = (short*)(block + PageOffset);
        var sums = RowSumOf(block);
        var maxima = (short*)(block + RowMaxOffset);
        var min = short.MaxValue;
        for (var y = 0; y < TileBake.TileSize; y++)
        {
            var sum = 0;
            var max = short.MinValue;
            for (var x = 0; x < TileBake.TileSize; x++)
            {
                var value = page[y * TileBake.TileSize + x];
                sum += value;
                if (value > max) max = value;
                if (value < min) min = value;
            }

            sums[y] = sum;
            maxima[y] = max;
        }

        *(short*)(block + MinOffset) = min;
    }

    internal static void FreeBlock(byte* block) => NativeHeap.AlignedFree(block);

    private static void MarkDirty(LayerData* ld, int tile)
    {
        if (ld->InDirty[tile] != 0) return;

        ld->InDirty[tile] = 1;
        var n = ld->Dirty.Length;
        ld->Dirty.Resize(n + 1);
        ld->Dirty.Pointer[n] = tile;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    public static void Process(byte world)
    {
        var w = GetContext(world);
        if (w != null) ProcessSources(w);
    }

    private static void GrowSources(SourceColumns* s, WorldCtx* w)
    {
        EnsureSourceCapacity(s, w, Math.Max(64, s->X.Length * 2));
    }

    private static void EnsureSourceCapacity(SourceColumns* s, WorldCtx* w, int required)
    {
        var initialized = s->X.Length;
        if (required <= initialized) return;
        var capacity = Math.Min(MaxSourceSlots, Math.Max(required, Math.Max(64, initialized * 2)));
        s->X.Resize(capacity);
        s->Y.Resize(capacity);
        s->Stamp.Resize(capacity);
        s->Layer.Resize(capacity);
        s->Gain.Resize(capacity);
        s->Angle.Resize(capacity);
        s->Scale.Resize(capacity);
        s->Timed.Resize(capacity);
        s->FadeFrom.Resize(capacity);
        s->FadeTo.Resize(capacity);
        s->FadeStart.Resize(capacity);
        s->FadeTicks.Resize(capacity);
        s->ExpireAt.Resize(capacity);
        s->Due.Resize(capacity);
        s->Alive.Resize(capacity);
        s->Free.Resize(capacity);
        s->Gen.Resize(capacity);
        w->Pending.Resize(capacity);
        new Span<byte>(s->Gen.Pointer + initialized, capacity - initialized).Clear();
        new Span<byte>(s->Timed.Pointer + initialized, capacity - initialized).Clear();
        new Span<int>(w->Pending.Pointer + initialized, capacity - initialized).Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short Query(byte world, byte grid, byte layer, int x, int y)
    {
        var w = GetContext(world);
        if (w == null || grid >= w->GridCount || layer >= w->LayerCount) return 0;

        var g = w->Grids + grid;
        if ((uint)x >= (uint)g->Size || (uint)y >= (uint)g->Size) return 0;

        var pages = &g->Layers[layer].Pages;
        if (!pages->TryGet((y >> TileBake.TileBits) * g->TilesPerSide + (x >> TileBake.TileBits), out var block))
            return 0;

        var page = (short*)(block + PageOffset);
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

        var pages = &g->Layers[layer].Pages;
        if (pages->Count == 0) return 0;
        var tps = g->TilesPerSide;
        var sum = 0L;
        if (x0 == 0 && y0 == 0 && x1 == g->Size && y1 == g->Size)
        {
            var used = pages->Used;
            var blocks = pages->Blocks;
            var slots = pages->SlotCount;
            for (var slot = 0; slot < slots; slot++)
            {
                if (used[slot] != PageMap.Live) continue;
                sum += *(long*)(blocks[slot] + SumOffset);
            }

            return sum;
        }

        for (var ty = y0 >> TileBake.TileBits; ty <= (y1 - 1) >> TileBake.TileBits; ty++)
        for (var tx = x0 >> TileBake.TileBits; tx <= (x1 - 1) >> TileBake.TileBits; tx++)
        {
            if (!pages->TryGet(ty * tps + tx, out var block)) continue;

            var lx0 = Math.Max(x0 - tx * TileBake.TileSize, 0);
            var ly0 = Math.Max(y0 - ty * TileBake.TileSize, 0);
            var lx1 = Math.Min(x1 - tx * TileBake.TileSize, TileBake.TileSize);
            var ly1 = Math.Min(y1 - ty * TileBake.TileSize, TileBake.TileSize);
            if (lx0 == 0 && ly0 == 0 && lx1 == TileBake.TileSize && ly1 == TileBake.TileSize)
                sum += *(long*)(block + SumOffset);
            else
                sum += PartialSum((short*)(block + PageOffset), lx0, ly0, lx1, ly1);
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
        var pages = &g->Layers[layer].Pages;
        var tps = g->TilesPerSide;
        var mask = TileBake.TileSize - 1;

        for (var row = 0; row < height; row++)
        {
            var cy = y + row;
            var dst = destination + (long)row * width;
            if ((uint)cy >= (uint)g->Size)
            {
                FillRun(dst, width);
                continue;
            }

            var ty = cy >> TileBake.TileBits;
            var ly = cy & mask;
            var cx = x;
            var col = 0;
            while (col < width)
            {
                var run = Math.Min((((cx >> TileBake.TileBits) + 1) << TileBake.TileBits) - cx, width - col);
                if ((uint)cx < (uint)g->Size && pages->TryGet(ty * tps + (cx >> TileBake.TileBits), out var block))
                {
                    var page = (short*)(block + PageOffset);
                    var src = page + ly * TileBake.TileSize + (cx & mask);
                    CopyRun(dst + col, src, run);
                }
                else
                {
                    FillRun(dst + col, run);
                }

                cx += run;
                col += run;
            }
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
        var cx = (int)MathF.Floor((x - g->OriginX) * g->ScaleQ8) >> 8;
        var cy = (int)MathF.Floor((y - g->OriginY) * g->ScaleQ8) >> 8;
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
        return ld->Max.Best(g->TilesPerSide, &ld->Pages, out x, out y);
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
        return ld->Max.Best(g->TilesPerSide, &ld->Pages, x0, y0, (int)x1, (int)y1, out bestX, out bestY);
    }

    public static void QueryGradient(byte world, byte grid, byte layer, float x, float y, out int gx, out int gy)
    {
        gx = 0;
        gy = 0;
        var w = GetContext(world);
        if (w == null || grid >= w->GridCount || layer >= w->LayerCount) return;

        var g = w->Grids + grid;
        var cx = (int)MathF.Floor((x - g->OriginX) * g->ScaleQ8) >> 8;
        var cy = (int)MathF.Floor((y - g->OriginY) * g->ScaleQ8) >> 8;
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

        var count = 0;
        for (var tile = 0; tile < g->TileCount; tile++)
        {
            if (epochs[tile] - since <= 0) continue;
            if (destination != null) destination[count] = tile;
            count++;
        }

        return count;
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
