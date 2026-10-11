#if NET
using System.Runtime.CompilerServices;
#endif

namespace Gi;

internal unsafe struct ResolveTask
{
    public GridCtx* Grid;
    public LayerData* Layer;
    public int Base;
    public int Count;
}

internal static unsafe class ResolvePool
{
    internal const int Threshold = 32;
    internal const int ApplyThreshold = 128;
    private const int Chunk = 8;
    private const int Buckets = 64;

    private static int _busy;
    private static int _workers;
    private static bool _created;
    private static AutoResetEvent[] _wake = null!;
    private static int* _prevPool;
    private static NativeBuffer<int> _dead;
    private static NativeBuffer<ResolveTask> _tasks;
    private static NativeBuffer<DepositFragment> _pooled;
    private static NativeBuffer<int> _bucketIndex;
    private static WorldCtx* _applyWorld;
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
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
        if (!_created) Create();
        return true;
    }

    internal static void Release() => Volatile.Write(ref _busy, 0);

    private static void Create()
    {
        _workers = Math.Clamp(Environment.ProcessorCount - 1, 1, 4);
        _prevPool = (int*)NativeHeap.AlignedAlloc((nuint)(_workers * TileBake.TileSize * sizeof(int)));
        _tasks.Ensure(World.MaxGrids * World.MaxLayers);
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
        PrepareTasks(w, total);
        for (var k = 0; k < _workers; k++) _wake[k].Set();
        RunShare(w->Prev);
        Drain();
        Finish();
    }

    internal static void ApplyAndResolveWorld(WorldCtx* w, int fragmentCount, int total)
    {
        ScatterFragments(w, fragmentCount);
        PrepareTasks(w, total);
        _applyRemaining = _workers + 1;
        Volatile.Write(ref _phase, 1);
        for (var k = 0; k < _workers; k++) _wake[k].Set();
        ApplyShare();
        Barrier(ref _applyRemaining);
        RunShare(w->Prev);
        Drain();
        Finish();
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

                tasks[taskCount] = new ResolveTask { Grid = g, Layer = ld, Base = taskBase, Count = count };
                taskCount++;
                taskBase += count;
            }
        }

        _taskCount = taskCount;
        _total = total;
        _deadCount = 0;
        _cursor = total;
        _remaining = _workers;
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
            _wake[index].WaitOne();
            if (Volatile.Read(ref _phase) != 0)
            {
                ApplyShare();
                Barrier(ref _applyRemaining);
            }

            RunShare(prev);
            Interlocked.Decrement(ref _remaining);
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
                var ld = tasks[task].Layer;
                var tile = ld->Dirty.Pointer[i - tasks[task].Base];
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
