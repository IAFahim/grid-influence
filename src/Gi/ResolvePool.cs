#if NET
using System.Runtime.CompilerServices;
#endif

namespace Gi;

internal static unsafe class ResolvePool
{
    internal const int Threshold = 32;
    private const int Chunk = 8;

    private static int _busy;
    private static int _workers;
    private static bool _created;
    private static AutoResetEvent[] _wake = null!;
    private static int* _prevPool;
    private static int* _dirty;
    private static byte* _inDirty;
    private static PageMap* _pages;
    private static NativeBuffer<int> _dead;
    private static int _count;
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
    internal static void ResolveLayer(WorldCtx* w, LayerData* ld, PageMap* pages, int count)
    {
        _dead.Ensure(count);
        _dirty = ld->Dirty.Pointer;
        _inDirty = ld->InDirty;
        _pages = pages;
        _count = count;
        _deadCount = 0;
        _cursor = count;
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
            var tile = dead[i];
            if (!pages->TryGet(tile, out var block)) continue;
            pages->Remove(tile);
            World.FreeBlock(block);
        }
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
        while (true)
        {
            var claimed = Interlocked.Add(ref _cursor, -Chunk);
            var hi = claimed + Chunk;
            if (hi <= 0) return;
            if (hi > _count) hi = _count;
            var lo = claimed < 0 ? 0 : claimed;
            for (var i = lo; i < hi; i++)
            {
                var tile = _dirty[i];
                _inDirty[tile] = 0;
                if (!_pages->TryGet(tile, out var block)) continue;

                new Span<int>(prev, TileBake.TileSize).Clear();
                if (!TileBake.Resolve((int*)block, World.DenseOf(block), prev,
                    (short*)(block + World.PageOffset), (long*)(block + World.SumOffset)))
                    _dead.Pointer[Interlocked.Increment(ref _deadCount) - 1] = tile;
            }
        }
    }
}
