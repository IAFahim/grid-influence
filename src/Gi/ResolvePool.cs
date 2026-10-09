#if NET
using System.Runtime.CompilerServices;
#endif

namespace Gi;

internal unsafe struct ResolveTask
{
    public LayerData* Layer;
    public int Base;
    public int Count;
}

internal static unsafe class ResolvePool
{
    internal const int Threshold = 32;
    private const int Chunk = 8;

    private static int _busy;
    private static int _workers;
    private static bool _created;
    private static AutoResetEvent[] _wake = null!;
    private static int* _prevPool;
    private static NativeBuffer<int> _dead;
    private static NativeBuffer<ResolveTask> _tasks;
    private static int _taskCount;
    private static int _total;
    private static int _cursor;
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
                if (count == 0) continue;

                tasks[taskCount] = new ResolveTask { Layer = ld, Base = taskBase, Count = count };
                taskCount++;
                taskBase += count;
            }
        }

        _taskCount = taskCount;
        _total = total;
        _deadCount = 0;
        _cursor = total;
        _remaining = _workers;
        for (var k = 0; k < _workers; k++) _wake[k].Set();
        RunShare(w->Prev);
        var spin = 0;
        while (Volatile.Read(ref _remaining) != 0)
        {
            Thread.SpinWait(32);
            if ((++spin & 15) == 0) Thread.Yield();
        }

        var dead = _dead.Pointer;
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

        for (var t = 0; t < taskCount; t++) tasks[t].Layer->Dirty.Resize(0);
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
                if (!TileBake.Resolve((int*)block, World.DenseOf(block), prev,
                    (short*)(block + World.PageOffset), (long*)(block + World.SumOffset)))
                    _dead.Pointer[Interlocked.Increment(ref _deadCount) - 1] = i;
            }
        }
    }
}
