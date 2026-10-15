namespace Gi;

internal static unsafe partial class ResolvePool
{
    internal const int ColumnThreshold = 8192;
    private const int ColumnChunk = 1024;

    internal static bool SerialOnly;

    private static int _busy;
    private static int _workers;
    private static bool _created;
    private static AutoResetEvent[] _wake = null!;
    private static int* _parked;
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
    private static int _phase;
    private static int _remaining;

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
        _workers = Math.Clamp(Environment.ProcessorCount - 1, 1, MaximumSlots - 1);
        _parked = (int*)NativeHeap.AllocZeroed((nuint)(_workers * sizeof(int)));
        _wake = new AutoResetEvent[_workers];
        for (var k = 0; k < _workers; k++)
        {
            var index = k;
            _wake[k] = new AutoResetEvent(false);
            new Thread(() => WorkerLoop(index)) { IsBackground = true }.Start();
        }

        Volatile.Write(ref _created, true);
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

    private static void WorkerLoop(int index)
    {
        while (true)
        {
            Volatile.Write(ref _parked[index], 1);
            _wake[index].WaitOne();
            Volatile.Write(ref _parked[index], 0);
            var phase = Volatile.Read(ref _phase);
            if (phase == 6 || phase == 7)
            {
                SourceBatchShare(phase);
                Interlocked.Decrement(ref _remaining);
            }
            else
            {
                ColumnShare();
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
        while (true)
        {
            var chunk = Interlocked.Increment(ref _columnCursor) - 1;
            if (chunk > (_columnCount - 1) / ColumnChunk) return;
            var lo = chunk * ColumnChunk;
            var hi = (int)Math.Min((long)lo + ColumnChunk, _columnCount);
            var gaps = _columnMode == 2
                ? World.SenseRange(_columnWorld, _columnLayer, _columnXs, _columnYs, _columnValues, _columnWhole, lo, hi)
                : World.ChangedRange(_columnWorld, _columnLayer, _columnXs, _columnYs, _columnReach, _columnSince, _columnWords, lo, hi);
            if (gaps != 0) Interlocked.Add(ref _columnGaps, gaps);
        }
    }
}
