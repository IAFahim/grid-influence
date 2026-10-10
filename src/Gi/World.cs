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
    public NativeBuffer<byte> Alive;
    public NativeBuffer<int> Free;
    public NativeBuffer<byte> Gen;
    public int Count;
}

internal unsafe struct LayerData
{
    public PageMap Pages;
    public byte* InDirty;
    public NativeBuffer<int> Dirty;
    public NativeBuffer<int> Changed;
    public MaxPyramid Max;
}

internal unsafe struct GridCtx
{
    public int Size;
    public float OriginX;
    public float OriginY;
    public float Scale;
    public int ScaleQ8;
    public int TilesPerSide;
    public int TileCount;
    public LayerData* Layers;
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
    public byte Fresh;
}

internal unsafe struct DepositFragment
{
    public byte* Block;
    public int Px;
    public int Py;
    public int Fx;
    public int Fy;
    public int ExtentX;
    public int ExtentY;
    public int X1;
    public int Y1;
    public int Gain;
    public int Tile;
    public byte Stamp;
    public byte Grid;
    public byte Layer;
}

internal unsafe struct WorldCtx
{
    public GridCtx* Grids;
    public int GridCount;
    public int LayerCount;
    public SourceColumns Sources;
    public NativeBuffer<DepositOp> Ops;
    public NativeBuffer<int> Pending;
    public NativeBuffer<DepositFragment> Fragments;
    public NativeBuffer<DepositOp> Journal;
    public byte Recording;
    public int* Prev;
    public int FreeHead;
}

public static unsafe class World
{
    internal const int MaxWorlds = 64;
    internal const int MaxGrids = 32;
    internal const int MaxLayers = 32;
    private const int MinPower = 5;
    private const int MaxPower = 14;
    private const int MaxGain = 16;
    internal const int MaxSourceSlots = 1 << 24;
    internal const int SourceIndexMask = MaxSourceSlots - 1;
    internal const int Cells = TileBake.TileSize * TileBake.TileSize;
    internal const int DenseBytes = Cells * sizeof(int);
    internal const int DiffBytes = TileBake.DiffRows * TileBake.DiffPitch * sizeof(int);
    internal const int PageBytes = Cells * sizeof(short);
    internal const int PageOffset = (DiffBytes + 31) & ~31;
    internal const int DensePtrOffset = PageOffset + PageBytes;
    internal const int DensePtrSlot = 8;
    internal const int TentPtrOffset = DensePtrOffset + DensePtrSlot;
    internal const int TentBytes = TileBake.DiffRows * TileBake.DiffPitch * sizeof(long);
    internal const int SumOffset = (DensePtrOffset + DensePtrSlot + 63) & ~63;
    internal const int SumSlotBytes = 64;
    internal const int MaxOffset = SumOffset + 8;
    internal const int BlockBytes = SumOffset + SumSlotBytes;

    private static int _worldCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static WorldCtx* GetContext(byte world) => world < _worldCount ? Runtime.Worlds + world : null;

    public static byte New()
    {
        Runtime.Ensure();
        if (_worldCount >= MaxWorlds) throw new InvalidOperationException("World limit reached.");

        var id = (byte)_worldCount++;
        var w = Runtime.Worlds + id;
        w->Grids = (GridCtx*)NativeHeap.AllocZeroed((nuint)(MaxGrids * sizeof(GridCtx)));
        w->Prev = (int*)NativeHeap.AlignedAlloc(TileBake.TileSize * sizeof(int));
        w->FreeHead = -1;
        return id;
    }

    internal static byte AddGrid(byte world, int power, float x, float y, float size)
    {
        if (power < MinPower || power > MaxPower) throw new ArgumentOutOfRangeException(nameof(power));
        if (size <= 0f) throw new ArgumentOutOfRangeException(nameof(size));

        var w = GetContext(world);
        if (w == null) throw new ArgumentOutOfRangeException(nameof(world));
        if (w->GridCount >= MaxGrids) throw new InvalidOperationException("Grid limit reached.");

        var id = (byte)w->GridCount++;
        var g = w->Grids + id;
        g->Size = 1 << power;
        g->OriginX = x;
        g->OriginY = y;
        g->Scale = g->Size / size;
        g->ScaleQ8 = (int)(g->Scale * 256f);
        g->TilesPerSide = g->Size >> TileBake.TileBits;
        g->TileCount = g->TilesPerSide * g->TilesPerSide;
        g->Layers = (LayerData*)NativeHeap.AllocZeroed((nuint)(MaxLayers * sizeof(LayerData)));
        return id;
    }

    internal static byte AddLayer(byte world)
    {
        var w = GetContext(world);
        if (w == null) throw new ArgumentOutOfRangeException(nameof(world));
        if (w->LayerCount >= MaxLayers) throw new InvalidOperationException("Layer limit reached.");
        return (byte)w->LayerCount++;
    }

    public static int Place(byte world, byte layer, float x, float y, byte stamp, int gain)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount || stamp == 0 || stamp >= StampCatalog.Count) return -1;

        var s = &w->Sources;
        var fresh = w->FreeHead < 0;
        int i;
        if (!fresh)
        {
            i = w->FreeHead;
            w->FreeHead = s->Free.Pointer[i];
        }
        else
        {
            if (s->Count >= MaxSourceSlots) return -1;
            if (s->Count == s->X.Length) GrowSources(s, w);
            i = s->Count++;
        }

        var g = (byte)Math.Clamp(gain, -MaxGain, MaxGain);
        var gen = (byte)((s->Gen.Pointer[i] + 1) & 127);
        s->Gen.Pointer[i] = gen;
        s->X.Pointer[i] = x;
        s->Y.Pointer[i] = y;
        s->Stamp.Pointer[i] = stamp;
        s->Layer.Pointer[i] = layer;
        s->Gain.Pointer[i] = g;
        s->Alive.Pointer[i] = 1;
        EnqueuePlace(w, i, x, y, stamp, layer, (sbyte)g, fresh);
        return (gen << 24) | i;
    }

    public static void Move(byte world, int source, float x, float y)
    {
        if (!TrySource(world, source, out var w, out var s, out var i)) return;

        var oldX = s->X.Pointer[i];
        var oldY = s->Y.Pointer[i];
        if (oldX == x && oldY == y) return;
        var gain = (sbyte)s->Gain.Pointer[i];
        EnqueueMutation(w, s, i, x, y, gain, true);
        s->X.Pointer[i] = x;
        s->Y.Pointer[i] = y;
    }

    public static void SetGain(byte world, int source, int gain)
    {
        if (!TrySource(world, source, out var w, out var s, out var i)) return;

        var next = (byte)Math.Clamp(gain, -MaxGain, MaxGain);
        var current = s->Gain.Pointer[i];
        if (next == current) return;

        EnqueueMutation(w, s, i, s->X.Pointer[i], s->Y.Pointer[i], (sbyte)next, true);
        s->Gain.Pointer[i] = next;
    }

    public static void Remove(byte world, int source)
    {
        if (!TrySource(world, source, out var w, out var s, out var i)) return;

        EnqueueMutation(w, s, i, s->X.Pointer[i], s->Y.Pointer[i], 0, false);
        s->Alive.Pointer[i] = 0;
        s->Free.Pointer[i] = w->FreeHead;
        w->FreeHead = i;
    }

    private static void EnqueuePlace(
        WorldCtx* w, int slot, float x, float y, byte stamp, byte layer, sbyte gain, bool fresh)
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
        freshOp->AliveFrom = 0;
        freshOp->AliveTo = 1;
        freshOp->Fresh = (byte)(fresh ? 1 : 0);
        w->Pending.Pointer[slot] = n + 1;
    }

    private static void EnqueueMutation(
        WorldCtx* w, SourceColumns* s, int slot, float x, float y, sbyte gain, bool alive)
    {
        var pending = w->Pending.Pointer[slot];
        if (pending != 0)
        {
            var op = w->Ops.Pointer + pending - 1;
            op->ToX = x;
            op->ToY = y;
            op->ToGain = gain;
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
        op2->ToX = x;
        op2->ToY = y;
        op2->ToStamp = stamp;
        op2->ToLayer = layer;
        op2->ToGain = gain;
        op2->AliveFrom = 1;
        op2->AliveTo = (byte)(alive ? 1 : 0);
        op2->Fresh = 0;
        w->Pending.Pointer[slot] = n + 1;
    }

    private static int ApplyDeposits(WorldCtx* w)
    {
        var count = w->Ops.Length;
        if (count == 0) return 0;

        var ops = w->Ops.Pointer;
        var deferred = count * Math.Max(1, w->GridCount) >= DeferredOps;
        for (var i = 0; i < count && !deferred; i++)
        {
            var probe = ops + i;
            if (probe->FromGain != 0 && StampCatalog.Get(probe->FromStamp)->Kind != StampKind.ConstantRectangle) deferred = true;
            else if (probe->ToGain != 0 && StampCatalog.Get(probe->ToStamp)->Kind != StampKind.ConstantRectangle) deferred = true;
        }

        w->Fragments.Resize(0);
        for (var i = 0; i < count; i++)
        {
            var op = ops + i;
            w->Pending.Pointer[op->Slot] = 0;
            if (w->Recording != 0)
            {
                var journalIndex = w->Journal.Length;
                w->Journal.Resize(journalIndex + 1);
                w->Journal.Pointer[journalIndex] = *op;
            }

            if (op->FromGain != 0)
            {
                if (deferred) BuildDeposits(w, op->FromX, op->FromY, op->FromStamp, op->FromLayer, -op->FromGain);
                else Deposit(w, op->FromX, op->FromY, op->FromStamp, op->FromLayer, -op->FromGain);
            }

            if (op->ToGain != 0)
            {
                if (deferred) BuildDeposits(w, op->ToX, op->ToY, op->ToStamp, op->ToLayer, op->ToGain);
                else Deposit(w, op->ToX, op->ToY, op->ToStamp, op->ToLayer, op->ToGain);
            }
        }

        w->Ops.Resize(0);
        return w->Fragments.Length;
    }

    private const int DeferredOps = 512;

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static void Deposit(WorldCtx* w, float x, float y, byte stampId, byte layer, int gain)
    {
        if (gain == 0) return;

        var v = StampCatalog.Get(stampId);
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            if (g->ScaleQ8 == 0) continue;

            TileBake.Footprint(x, y, g->OriginX, g->OriginY, g->ScaleQ8, g->Size << 8, v,
                out var px, out var py, out var fx, out var fy,
                out var extentX, out var extentY,
                out var x0, out var y0, out var x1, out var y1);

            var cx0 = Math.Max(x0, 0);
            var cy0 = Math.Max(y0, 0);
            var cx1 = Math.Min(x1, g->Size);
            var cy1 = Math.Min(y1, g->Size);
            if (cx1 <= cx0 || cy1 <= cy0) continue;

            var ld = EnsureDirty(g, layer);
            var raster = v->Kind == StampKind.Raster;
            var tps = g->TilesPerSide;
            var tx0 = cx0 >> TileBake.TileBits;
            var tx1 = (cx1 - 1) >> TileBake.TileBits;
            var ty0 = cy0 >> TileBake.TileBits;
            var ty1 = (cy1 - 1) >> TileBake.TileBits;
            for (var ty = ty0; ty <= ty1; ty++)
            for (var tx = tx0; tx <= tx1; tx++)
            {
                var tile = ty * tps + tx;
                var block = TileBlock(ld, tile, raster);
                if (raster) block = EnsureDense(ld, tile, block);

                var tileX0 = tx * TileBake.TileSize;
                var tileY0 = ty * TileBake.TileSize;
                if (v->Kind == StampKind.ConstantRectangle)
                    TileBake.EmitBox((int*)block, tileX0, tileY0, px, py, fx, fy, extentX, extentY, v, gain);
                else if (v->Kind == StampKind.Tent)
                    TileBake.EmitTent((long*)EnsureTent(block), tileX0, tileY0, px, py, fx, fy, extentX, extentY, v, gain);
                else
                    TileBake.EmitRaster(DenseOf(block), tileX0, tileY0, px, py, fx, fy, x1, y1, g->ScaleQ8, v, gain);

                MarkDirty(ld, tile);
            }
        }
    }

    private static void BuildDeposits(WorldCtx* w, float x, float y, byte stampId, byte layer, int gain)
    {
        if (gain == 0) return;

        var v = StampCatalog.Get(stampId);
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            if (g->ScaleQ8 == 0) continue;

            TileBake.Footprint(x, y, g->OriginX, g->OriginY, g->ScaleQ8, g->Size << 8, v,
                out var px, out var py, out var fx, out var fy,
                out var extentX, out var extentY,
                out var x0, out var y0, out var x1, out var y1);

            var cx0 = Math.Max(x0, 0);
            var cy0 = Math.Max(y0, 0);
            var cx1 = Math.Min(x1, g->Size);
            var cy1 = Math.Min(y1, g->Size);
            if (cx1 <= cx0 || cy1 <= cy0) continue;

            var ld = EnsureDirty(g, layer);
            var raster = v->Kind == StampKind.Raster;
            var tps = g->TilesPerSide;
            var tx0 = cx0 >> TileBake.TileBits;
            var tx1 = (cx1 - 1) >> TileBake.TileBits;
            var ty0 = cy0 >> TileBake.TileBits;
            var ty1 = (cy1 - 1) >> TileBake.TileBits;
            for (var ty = ty0; ty <= ty1; ty++)
            for (var tx = tx0; tx <= tx1; tx++)
            {
                var tile = ty * tps + tx;
                var block = TileBlock(ld, tile, raster);
                if (raster)
                {
                    var grown = EnsureDense(ld, tile, block);
                    if (grown != block)
                    {
                        var built = w->Fragments.Pointer;
                        for (var k = 0; k < w->Fragments.Length; k++)
                            if (built[k].Block == block) built[k].Block = grown;
                        block = grown;
                    }
                }

                MarkDirty(ld, tile);
                var n = w->Fragments.Length;
                w->Fragments.Resize(n + 1);
                w->Fragments.Pointer[n] = new DepositFragment
                {
                    Block = block,
                    Px = px,
                    Py = py,
                    Fx = fx,
                    Fy = fy,
                    ExtentX = extentX,
                    ExtentY = extentY,
                    X1 = x1,
                    Y1 = y1,
                    Gain = gain,
                    Tile = tile,
                    Stamp = stampId,
                    Grid = (byte)gi,
                    Layer = layer,
                };
            }
        }
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    internal static void ApplyFragment(WorldCtx* w, DepositFragment* f)
    {
        var gain = f->Gain;
        if (gain == 0) return;

        var v = StampCatalog.Get(f->Stamp);
        var block = f->Block;
        var tps = w->Grids[f->Grid].TilesPerSide;
        var tileX0 = (f->Tile % tps) << TileBake.TileBits;
        var tileY0 = (f->Tile / tps) << TileBake.TileBits;
        if (v->Kind == StampKind.ConstantRectangle)
            TileBake.EmitBox((int*)block, tileX0, tileY0, f->Px, f->Py, f->Fx, f->Fy, f->ExtentX, f->ExtentY, v, gain);
        else if (v->Kind == StampKind.Tent)
            TileBake.EmitTent((long*)EnsureTent(block), tileX0, tileY0, f->Px, f->Py, f->Fx, f->Fy, f->ExtentX, f->ExtentY, v, gain);
        else
            TileBake.EmitRaster(DenseOf(block), tileX0, tileY0, f->Px, f->Py, f->Fx, f->Fy,
                f->X1, f->Y1, w->Grids[f->Grid].ScaleQ8, v, gain);
    }

    private static void ApplyFragments(WorldCtx* w, int count)
    {
        var fragments = w->Fragments.Pointer;
        for (var i = 0; i < count; i++) ApplyFragment(w, fragments + i);
    }

    public static void Clear(byte world)
    {
        var w = GetContext(world);
        if (w == null) return;
        var s = &w->Sources;
        new Span<byte>(s->Alive.Pointer, s->Count).Clear();
        s->Count = 0;
        w->FreeHead = -1;
        w->Ops.Resize(0);
        w->Fragments.Resize(0);
        w->Journal.Resize(0);
        new Span<int>(w->Pending.Pointer, w->Pending.Capacity).Clear();

        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
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
                for (var i = 0; i < slots; i++)
                    if (used[i] == PageMap.Live) FreeBlock(blocks[i]);
                pages->Reset();
                ld->Max.Reset();
            }
        }
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
        if (ld->InDirty == null)
            ld->InDirty = (byte*)NativeHeap.AllocZeroed((nuint)g->TileCount);
        return ld;
    }

    private static byte* TileBlock(LayerData* ld, int tile, bool dense)
    {
        var pages = &ld->Pages;
        if (pages->TryGet(tile, out var block)) return block;

        var bytes = (nuint)(dense ? BlockBytes + DenseBytes : BlockBytes);
        block = (byte*)NativeHeap.AlignedAlloc(bytes);
        new Span<byte>(block, (int)bytes).Clear();
        if (dense) *(byte**)(block + DensePtrOffset) = block + BlockBytes;
        pages->Put(tile, block);
        return block;
    }

    private static byte* EnsureDense(LayerData* ld, int tile, byte* block)
    {
        var dense = *(byte**)(block + DensePtrOffset);
        if (dense != null) return block;

        var grown = (byte*)NativeHeap.AlignedAlloc((nuint)(BlockBytes + DenseBytes));
        new Span<byte>(block, BlockBytes).CopyTo(new Span<byte>(grown, BlockBytes));
        NativeHeap.AlignedFree(block);
        dense = grown + BlockBytes;
        new Span<byte>(dense, DenseBytes).Clear();
        *(byte**)(grown + DensePtrOffset) = dense;
        ld->Pages.Put(tile, grown);
        return grown;
    }

    private static byte* EnsureTent(byte* block)
    {
        var tent = *(byte**)(block + TentPtrOffset);
        if (tent != null) return tent;

        tent = (byte*)NativeHeap.AlignedAlloc((nuint)TentBytes);
        new Span<byte>(tent, TentBytes).Clear();
        *(byte**)(block + TentPtrOffset) = tent;
        return tent;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int* DenseOf(byte* block)
    {
        var dense = *(byte**)(block + DensePtrOffset);
        return (int*)(dense == null ? Runtime.ZeroDense : dense);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long* TentOf(byte* block) => (long*)*(byte**)(block + TentPtrOffset);

    internal static void FreeBlock(byte* block)
    {
        var dense = *(byte**)(block + DensePtrOffset);
        if (dense != null && dense != block + BlockBytes) NativeHeap.AlignedFree(dense);
        var tent = *(byte**)(block + TentPtrOffset);
        if (tent != null) NativeHeap.AlignedFree(tent);
        NativeHeap.AlignedFree(block);
    }

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
        if (w == null) return;

        var fragmentCount = ApplyDeposits(w);

        var total = 0;
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            for (var l = 0; l < w->LayerCount; l++) total += g->Layers[l].Dirty.Length;
        }

        var pooled = total >= ResolvePool.Threshold && ResolvePool.TryAcquire();
        if (pooled && fragmentCount >= ResolvePool.ApplyThreshold)
        {
            ResolvePool.ApplyAndResolveWorld(w, fragmentCount, total);
            ResolvePool.Release();
        }
        else
        {
            ApplyFragments(w, fragmentCount);
            w->Fragments.Resize(0);

            if (pooled)
            {
                ResolvePool.ResolveWorld(w, total);
                ResolvePool.Release();
            }
            else
            {
                for (var gi = 0; gi < w->GridCount; gi++)
                {
                    var g = w->Grids + gi;
                    for (var l = 0; l < w->LayerCount; l++)
                    {
                        var ld = g->Layers + l;
                        var span = ld->Dirty.Span;
                        var pages = &ld->Pages;
                        foreach (var tile in span)
                        {
                            ld->InDirty[tile] = 0;
                            if (!pages->TryGet(tile, out var block)) continue;

                            new Span<int>(w->Prev, TileBake.TileSize).Clear();
                            if (!TileBake.Resolve((int*)block, DenseOf(block), w->Prev, TentOf(block),
                                (short*)(block + PageOffset), (long*)(block + SumOffset), (short*)(block + MaxOffset)))
                            {
                                pages->Remove(tile);
                                FreeBlock(block);
                                ld->Max.Update(g->TilesPerSide, tile, 0);
                            }
                            else
                            {
                                ld->Max.Update(g->TilesPerSide, tile, *(short*)(block + MaxOffset));
                            }
                        }
                    }
                }
            }
        }

        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            for (var l = 0; l < w->LayerCount; l++)
            {
                var ld = g->Layers + l;
                var changed = ld->Dirty;
                ld->Dirty = ld->Changed;
                ld->Dirty.Resize(0);
                ld->Changed = changed;
            }
        }
    }

    private static void GrowSources(SourceColumns* s, WorldCtx* w)
    {
        var capacity = Math.Max(64, s->X.Length * 2);
        var live = s->Count;
        s->X.Resize(capacity);
        s->Y.Resize(capacity);
        s->Stamp.Resize(capacity);
        s->Layer.Resize(capacity);
        s->Gain.Resize(capacity);
        s->Alive.Resize(capacity);
        s->Free.Resize(capacity);
        s->Gen.Resize(capacity);
        w->Pending.Resize(capacity);
        new Span<byte>(s->Gen.Pointer + live, capacity - live).Clear();
        new Span<int>(w->Pending.Pointer + live, capacity - live).Clear();
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
        var x1 = Math.Min((long)x + width, (long)g->Size);
        var y1 = Math.Min((long)y + height, (long)g->Size);
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
