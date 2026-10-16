#if NET
using System.Runtime.CompilerServices;
#endif

namespace Gi;

internal unsafe struct ResolveTask
{
    public WorldCtx* World;
    public GridCtx* Grid;
    public LayerData* Layer;
    public int Base;
    public int Count;
    public int Slots;
    public int Chains;
}

internal static unsafe partial class ResolvePool
{
    internal const int Threshold = 32;
    internal const int ApplyThreshold = 128;
    private const int Chunk = 8;
    private const int Buckets = 64;
    internal const int ColumnThreshold = 8192;
    private const int ColumnWordsPerChunk = 16;
    private const int ColumnChunk = ColumnWordsPerChunk * 64;
    private const int ScatterChunk = 2048;

    private static int _busy;
    private static int _workers;
    private static bool _created;
    private static AutoResetEvent[] _wake = null!;
    private static int* _prevPool;
    private static int* _parked;
    private static NativeBuffer<int> _dead;
    private static NativeBuffer<byte> _outcome;
    private static NativeBuffer<ResolveTask> _tasks;
    private static NativeBuffer<DepositFragment> _pooled;
    private static NativeBuffer<int> _bucketIndex;
    private static WorldCtx* _applyWorld;
    private static WorldCtx* _columnWorld;
    private static byte _columnLayer;
    private static float* _columnXs;
    private static float* _columnYs;
    private static short* _columnValues;
    private static ulong* _columnWhole;
    private static ulong* _columnWords;
    private static float _columnReach;
    private static int _columnSince;
    private static int _columnMode;
    private static int _columnCount;
    private static int _columnCursor;
    private static int _columnGaps;
    private static NativeBuffer<int>* _scatterTails;
    private static NativeBuffer<Placement>* _scatterPlacements;
    private static NativeBuffer<RebuildChunk>* _scatterArenas;
    private static Placement** _rebuildPlacements;
    private static RebuildChunk** _rebuildArenas;
    private static WorldCtx* _scatterWorld;
    private static GridCtx* _scatterGrid;
    private static byte _scatterLayer;
    private static int _scatterChains;
    private static int _scatterDense;
    private static int _scatterCursor;
    private static int _scatterDone;
    private static int _phase;
    private static int _applyRemaining;
    private static int _taskCount;
    private static int _total;
    private static int _cursor;
    private static int _bucketCursor;
    private static int _remaining;
    private static int _deadCount;

    internal static bool TryAcquire()
    {
        if (SerialOnly) return false;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
        if (!_created) Create();
        return true;
    }

    internal static void Release() => Volatile.Write(ref _busy, 0);


    private static void Create()
    {
        _workers = Math.Clamp(Environment.ProcessorCount - 1, 1, 4);
        var slots = (nuint)(_workers + 1);
        _prevPool = (int*)NativeHeap.AlignedAlloc((nuint)(_workers * TileBake.TileSize * sizeof(int)));
        _parked = (int*)NativeHeap.AllocZeroed((nuint)(_workers * sizeof(int)));
        _scatterTails = (NativeBuffer<int>*)NativeHeap.AllocZeroed(slots * 64);
        _scatterPlacements = (NativeBuffer<Placement>*)NativeHeap.AllocZeroed(slots * 64);
        _scatterArenas = (NativeBuffer<RebuildChunk>*)NativeHeap.AllocZeroed(slots * 64);
        _depositFragments = (NativeBuffer<DepositFragment>*)NativeHeap.AllocZeroed(slots * 64);
        _depositKeys = (NativeBuffer<PrepKey>*)NativeHeap.AllocZeroed(slots * 64);
        _prepScratch = (NativeBuffer<PrepKey>*)NativeHeap.AllocZeroed(slots * 64);
        AllocPrepPools();
        _rebuildPlacements = (Placement**)NativeHeap.AllocZeroed(slots * (nuint)sizeof(Placement*));
        _rebuildArenas = (RebuildChunk**)NativeHeap.AllocZeroed(slots * (nuint)sizeof(RebuildChunk*));
        _tasks.Ensure(World.MaxGrids * WorldCtx.MaxLayers);
        _wake = new AutoResetEvent[_workers];
        for (var k = 0; k < _workers; k++)
        {
            var index = k;
            _wake[k] = new AutoResetEvent(false);
            new Thread(() => WorkerLoop(index)) { IsBackground = true }.Start();
        }

        Volatile.Write(ref _created, true);
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    internal static void ResolveWorld(WorldCtx* w, int total)
    {
        var stamp = ProcessStages.Mark();
        WaitParked();
        PrepareTasks(w, total);
        Volatile.Write(ref _phase, 0);
        for (var k = 0; k < _workers; k++) _wake[k].Set();
        RunShare(w->Prev);
        Drain();
        Finish();
        CompactRebuilt();
        ProcessStages.Measure(ref ProcessStages.ResolveTicks, stamp);
    }

    internal static void ApplyAndResolveWorld(WorldCtx* w, int fragmentCount, int total)
    {
        var stamp = ProcessStages.Mark();
        WaitParked();
        ScatterFragments(w, fragmentCount);
        PrepareTasks(w, total);
        _applyRemaining = _workers + 1;
        Volatile.Write(ref _phase, 1);
        for (var k = 0; k < _workers; k++) _wake[k].Set();
        ApplyShare();
        Barrier(ref _applyRemaining);
        ProcessStages.Measure(ref ProcessStages.ApplyTicks, stamp);
        stamp = ProcessStages.Mark();
        RunShare(w->Prev);
        Drain();
        Finish();
        CompactRebuilt();
        ProcessStages.Measure(ref ProcessStages.ResolveTicks, stamp);
        Volatile.Write(ref _phase, 0);
    }

    private static void Barrier(ref int remaining)
    {
        Interlocked.Decrement(ref remaining);
        var spin = 0;
        while (Volatile.Read(ref remaining) != 0)
        {
            Thread.SpinWait(32);
            if ((++spin & 15) == 0) Thread.Yield();
        }
    }

    private static void WaitParked()
    {
        var spin = 0;
        while (true)
        {
            var parked = 0;
            for (var k = 0; k < _workers; k++)
                if (Volatile.Read(ref _parked[k]) != 0) parked++;

            if (parked == _workers) return;
            Thread.SpinWait(32);
            if ((++spin & 15) == 0) Thread.Yield();
        }
    }

    private static void Drain()
    {
        var spin = 0;
        while (Volatile.Read(ref _remaining) != 0)
        {
            Thread.SpinWait(32);
            if ((++spin & 15) == 0) Thread.Yield();
        }
    }

    private static void PrepareTasks(WorldCtx* w, int total)
    {
        _dead.Ensure(total);
        _outcome.Ensure(total);
        var tasks = _tasks.Pointer;
        var taskCount = 0;
        var taskBase = 0;
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            for (var l = 0; l < w->LayerCount; l++)
            {
                var ld = g->Layers + l;
                var count = ld->Dirty.Length;
                if (count == 0 || World.IsDerived(w, l)) continue;

                var task = tasks + taskCount;
                *task = new ResolveTask { World = w, Grid = g, Layer = ld, Base = taskBase, Count = count };
                var rebuildSlot = 2 * (gi * WorldCtx.MaxLayers + l);
                if (w->Rebuilds[rebuildSlot + 1] != 0)
                {
                    task->Slots = w->Rebuilds[rebuildSlot + 1];
                    task->Chains = w->Rebuilds[rebuildSlot];
                }

                taskCount++;
                taskBase += count;
            }
        }

        _taskCount = taskCount;
        _total = total;
        _deadCount = 0;
        _cursor = total;
        _remaining = _workers;
        for (var k = 0; k < _workers + 1; k++)
        {
            _rebuildPlacements[k] = ScatterPlacements(k)->Pointer;
            _rebuildArenas[k] = ScatterArenas(k)->Pointer;
        }
    }

    private static void CompactRebuilt()
    {
        var tasks = _tasks.Pointer;
        var outcome = _outcome.Pointer;
        for (var t = 0; t < _taskCount; t++)
        {
            var task = tasks + t;
            if (task->Slots == 0) continue;

            var dirty = task->Layer->Dirty.Pointer;
            var kept = 0;
            for (var i = 0; i < task->Count; i++)
                if (outcome[task->Base + i] != 0) dirty[kept++] = dirty[i];
            task->Layer->Dirty.Resize(kept);
        }
    }

    private static void Finish()
    {
        var dead = _dead.Pointer;
        var tasks = _tasks.Pointer;
        for (var i = 0; i < _deadCount; i++)
        {
            var index = dead[i];
            var t = tasks + TaskOf(index);
            var ld = t->Layer;
            var pages = &ld->Pages;
            var tile = ld->Dirty.Pointer[index - t->Base];
            if (!pages->TryGet(tile, out var block)) continue;
            pages->Remove(tile);
            World.FreeBlock(block);
        }

        for (var t = 0; t < _taskCount; t++)
        {
            var task = tasks + t;
            var ld = task->Layer;
            var pages = &ld->Pages;
            var dirty = ld->Dirty.Pointer;
            for (var i = 0; i < task->Count; i++)
            {
                var tile = dirty[i];
                var max = pages->TryGet(tile, out var block) ? *(short*)(block + World.MaxOffset) : (short)0;
                ld->Max.Update(task->Grid->TilesPerSide, tile, max);
            }
        }
    }

    private static void ScatterFragments(WorldCtx* w, int count)
    {
        _pooled.Ensure(count);
        _bucketIndex.Ensure(Buckets * 2);
        var counts = stackalloc int[Buckets];
        var cursor = stackalloc int[Buckets];
        for (var b = 0; b < Buckets; b++) counts[b] = 0;

        var fragments = w->Fragments.Pointer;
        for (var i = 0; i < count; i++) counts[BucketOf(fragments + i)]++;

        var index = _bucketIndex.Pointer;
        var start = 0;
        for (var b = 0; b < Buckets; b++)
        {
            index[b] = start;
            index[Buckets + b] = counts[b];
            cursor[b] = start;
            start += counts[b];
        }

        var pooled = _pooled.Pointer;
        for (var i = 0; i < count; i++)
        {
            var f = fragments + i;
            pooled[cursor[BucketOf(f)]++] = *f;
        }

        _applyWorld = w;
        _bucketCursor = 0;
    }

    private static int BucketOf(DepositFragment* f)
    {
        var h = (uint)f->Tile * 2654435761u + (uint)(f->Grid * 37 + f->Layer);
        return (int)((h >> 13) & (Buckets - 1));
    }

    private static void ApplyShare()
    {
        var index = _bucketIndex.Pointer;
        var pooled = _pooled.Pointer;
        var w = _applyWorld;
        int b;
        while ((b = Interlocked.Increment(ref _bucketCursor) - 1) < Buckets)
        {
            var end = index[b] + index[Buckets + b];
            for (var i = index[b]; i < end; i++) World.ApplyFragment(w, pooled + i);
        }
    }

    private static int TaskOf(int index)
    {
        var tasks = _tasks.Pointer;
        var lo = 0;
        var hi = _taskCount - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (tasks[mid].Base <= index) lo = mid;
            else hi = mid - 1;
        }

        return lo;
    }

    private static void WorkerLoop(int index)
    {
        var prev = _prevPool + index * TileBake.TileSize;
        while (true)
        {
            Volatile.Write(ref _parked[index], 1);
            _wake[index].WaitOne();
            Volatile.Write(ref _parked[index], 0);
            var phase = Volatile.Read(ref _phase);
            if (phase == 1)
            {
                ApplyShare();
                Barrier(ref _applyRemaining);
                RunShare(prev);
                Interlocked.Decrement(ref _remaining);
            }
            else if (phase == 5)
            {
                ScatterShare(index);
                Barrier(ref _scatterDone);
            }
            else if (phase == 6 || phase == 7)
            {
                SourceBatchShare(index, phase);
                Interlocked.Decrement(ref _remaining);
            }
            else if (phase == 8)
            {
                BuildShare(index);
                Interlocked.Decrement(ref _remaining);
            }
            else if (phase == 9)
            {
                PrepTaskShare(index);
                Interlocked.Decrement(ref _remaining);
            }
            else if (phase >= 2)
            {
                ColumnShare();
                Interlocked.Decrement(ref _remaining);
            }
            else
            {
                RunShare(prev);
                Interlocked.Decrement(ref _remaining);
            }
        }
    }

    internal static int SenseColumns(WorldCtx* w, byte layer, float* xs, float* ys, short* values, ulong* whole, int count)
    {
        _columnWorld = w;
        _columnLayer = layer;
        _columnXs = xs;
        _columnYs = ys;
        _columnValues = values;
        _columnWhole = whole;
        _columnWords = null;
        _columnCount = count;
        _columnGaps = 0;
        _columnMode = 2;
        RunColumns(2);
        return _columnGaps;
    }

    internal static int ChangedColumns(WorldCtx* w, byte layer, float* xs, float* ys, float reach, int since, ulong* words, int count)
    {
        _columnWorld = w;
        _columnLayer = layer;
        _columnXs = xs;
        _columnYs = ys;
        _columnValues = null;
        _columnWhole = null;
        _columnWords = words;
        _columnReach = reach;
        _columnSince = since;
        _columnCount = count;
        _columnGaps = 0;
        _columnMode = 3;
        RunColumns(3);
        return _columnGaps;
    }

    internal static int Slots => _workers + 1;

    private static NativeBuffer<int>* ScatterTails(int index)
        => (NativeBuffer<int>*)((byte*)_scatterTails + (nuint)index * 64);

    internal static NativeBuffer<Placement>* ScatterPlacements(int index)
        => (NativeBuffer<Placement>*)((byte*)_scatterPlacements + (nuint)index * 64);

    private static NativeBuffer<RebuildChunk>* ScatterArenas(int index)
        => (NativeBuffer<RebuildChunk>*)((byte*)_scatterArenas + (nuint)index * 64);

    internal static void BeginCrowdFrame(int stride)
    {
        var slots = _workers + 1;
        for (var k = 0; k < slots; k++)
        {
            ScatterTails(k)->Ensure(stride);
            ScatterPlacements(k)->Resize(0);
            ScatterArenas(k)->Resize(0);
        }
    }

    internal static void ScatterLayer(WorldCtx* w, GridCtx* g, byte layer, int chainsRegion, int denseRegion)
    {
        WaitParked();
        var slots = _workers + 1;
        var tiles = g->TileCount;
        for (var k = 0; k < slots; k++) new Span<int>(ScatterTails(k)->Pointer, tiles).Fill(-1);

        _scatterWorld = w;
        _scatterGrid = g;
        _scatterLayer = layer;
        _scatterChains = chainsRegion;
        _scatterDense = denseRegion;
        _scatterCursor = w->Sources.Count;
        _scatterDone = _workers + 1;
        Volatile.Write(ref _phase, 5);
        for (var k = 0; k < _workers; k++) _wake[k].Set();
        ScatterShare(_workers);
        Barrier(ref _scatterDone);
        Volatile.Write(ref _phase, 0);
    }

    private static void ScatterShare(int index)
    {
        var tails = ScatterTails(index)->Pointer;
        var arena = ScatterArenas(index);
        var placements = ScatterPlacements(index);
        var w = _scatterWorld;
        var g = _scatterGrid;
        var layer = _scatterLayer;
        var tiles = g->TileCount;
        var chainsRegion = _scatterChains;
        var denseRegion = _scatterDense;
        var count = w->Sources.Count;
        while (true)
        {
            var claimed = Interlocked.Add(ref _scatterCursor, -ScatterChunk);
            var hi = claimed + ScatterChunk;
            if (hi <= 0) return;
            if (hi > count) hi = count;
            var lo = claimed < 0 ? 0 : claimed;
            for (var i = lo; i < hi; i++)
                World.ScatterSource(w, g, layer, index, i, tiles, chainsRegion, denseRegion, tails, arena, placements);
        }
    }

    private static void RunColumns(int mode)
    {
        WaitParked();
        _columnCursor = 0;
        _remaining = _workers;
        Volatile.Write(ref _phase, mode);
        for (var k = 0; k < _workers; k++) _wake[k].Set();
        ColumnShare();
        Drain();
        Volatile.Write(ref _phase, 0);
    }

    private static void ColumnShare()
    {
        var count = _columnCount;
        while (true)
        {
            var first = (long)(Interlocked.Increment(ref _columnCursor) - 1) * ColumnChunk;
            if (first >= count) return;
            var lo = (int)first;
            var hi = (int)Math.Min(first + ColumnChunk, count);
            var gaps = _columnMode == 2
                ? World.SenseRange(_columnWorld, _columnLayer, _columnXs, _columnYs, _columnValues, _columnWhole, lo, hi)
                : World.ChangedRange(_columnWorld, _columnLayer, _columnXs, _columnYs, _columnReach, _columnSince, _columnWords, lo, hi);
            if (gaps != 0) Interlocked.Add(ref _columnGaps, gaps);
        }
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static void RunShare(int* prev)
    {
        var tasks = _tasks.Pointer;
        while (true)
        {
            var claimed = Interlocked.Add(ref _cursor, -Chunk);
            var hi = claimed + Chunk;
            if (hi <= 0) return;
            if (hi > _total) hi = _total;
            var lo = claimed < 0 ? 0 : claimed;
            var task = TaskOf(lo);
            for (var i = lo; i < hi; i++)
            {
                while (i >= tasks[task].Base + tasks[task].Count) task++;
                var t = tasks + task;
                var ld = t->Layer;
                var tile = ld->Dirty.Pointer[i - t->Base];
                if (t->Slots != 0)
                {
                    var rebuilt = World.RebuildTile(t->World, t->Grid, ld, tile, t->Chains, t->Slots,
                        _rebuildArenas, _rebuildPlacements, prev, _outcome.Pointer + i);
                    if (rebuilt == null) _dead.Pointer[Interlocked.Increment(ref _deadCount) - 1] = i;
                    continue;
                }

                ld->InDirty[tile] = 0;
                var pages = &ld->Pages;
                if (!pages->TryGet(tile, out var block)) continue;

                new Span<int>(prev, TileBake.TileSize).Clear();
                if (!TileBake.Resolve(World.DiffOf(block), World.DenseOf(block), prev, World.TentOf(block), World.BellOf(block),
                    (short*)(block + World.PageOffset), (long*)(block + World.SumOffset), (short*)(block + World.MaxOffset)))
                    _dead.Pointer[Interlocked.Increment(ref _deadCount) - 1] = i;
            }
        }
    }
}
