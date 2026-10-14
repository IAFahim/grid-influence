namespace Gi;

internal static unsafe partial class ResolvePool
{
    private const int DepositChunk = 64;
    private static NativeBuffer<DepositFragment>* _depositFragments;
    private static NativeBuffer<PrepKey>* _depositKeys;
    private static NativeBuffer<PrepKey>* _prepScratch;
    private static NativeBuffer<PrepKey>* _prepKeys;
    private static NativeBuffer<PrepTask>* _prepTasks;
    private static WorldCtx* _buildWorld;
    private static DepositOp* _buildOps;
    private static WorldCtx* _prepWorld;
    private static int _buildCount;
    private static int _buildCursor;
    private static int _buildJournalBase;
    private static int _prepTaskCount;
    private static int _prepCursor;
    internal static bool SerialOnly;

    internal static NativeBuffer<DepositFragment>* DepositFragments(int index)
        => (NativeBuffer<DepositFragment>*)((byte*)_depositFragments + (nuint)index * 64);

    internal static NativeBuffer<PrepKey>* DepositKeys(int index)
        => (NativeBuffer<PrepKey>*)((byte*)_depositKeys + (nuint)index * 64);

    internal static NativeBuffer<PrepKey>* PrepKeyPool() => _prepKeys;

    internal static NativeBuffer<PrepTask>* PrepTaskPool() => _prepTasks;

    internal static void AllocPrepPools()
    {
        _prepKeys = (NativeBuffer<PrepKey>*)NativeHeap.AllocZeroed((nuint)sizeof(NativeBuffer<PrepKey>));
        _prepTasks = (NativeBuffer<PrepTask>*)NativeHeap.AllocZeroed((nuint)sizeof(NativeBuffer<PrepTask>));
    }

    internal static NativeBuffer<PrepKey>* PrepScratch(int index)
        => (NativeBuffer<PrepKey>*)((byte*)_prepScratch + (nuint)index * 64);

    internal static void RunDepositBuild(WorldCtx* w, DepositOp* ops, int count, int journalBase)
    {
        WaitParked();
        var slots = _workers + 1;
        for (var k = 0; k < slots; k++)
        {
            DepositFragments(k)->Resize(0);
            DepositKeys(k)->Resize(0);
            ScatterPlacements(k)->Resize(0);
        }

        _buildWorld = w;
        _buildOps = ops;
        _buildCount = count;
        _buildJournalBase = journalBase;
        _buildCursor = count;
        _remaining = _workers;
        Volatile.Write(ref _phase, 8);
        for (var k = 0; k < _workers; k++) _wake[k].Set();
        BuildShare(_workers);
        Drain();
        Volatile.Write(ref _phase, 0);
        _buildWorld = null;
        _buildOps = null;
    }

    internal static void RunDepositPrep(WorldCtx* w)
    {
        WaitParked();
        _prepCursor = _prepTaskCount;
        _remaining = _workers;
        Volatile.Write(ref _phase, 9);
        for (var k = 0; k < _workers; k++) _wake[k].Set();
        PrepTaskShare(_workers);
        Drain();
        Volatile.Write(ref _phase, 0);
        _prepWorld = null;
    }

    internal static void BeginDepositPrep(WorldCtx* w, int taskCount)
    {
        _prepWorld = w;
        _prepTaskCount = taskCount;
    }

    private static void BuildShare(int slot)
    {
        var placements = ScatterPlacements(slot);
        var fragments = DepositFragments(slot);
        var keys = DepositKeys(slot);
        while (true)
        {
            var claimed = Interlocked.Add(ref _buildCursor, -DepositChunk);
            var last = claimed + DepositChunk;
            if (last <= 0) return;
            if (last > _buildCount) last = _buildCount;
            var first = claimed < 0 ? 0 : claimed;
            World.BuildDepositRange(_buildWorld, _buildOps, first, last, placements, fragments, keys, _buildJournalBase);
        }
    }

    private static void PrepTaskShare(int slot)
    {
        var scratch = PrepScratch(slot);
        while (true)
        {
            var task = Interlocked.Decrement(ref _prepCursor);
            if (task < 0) return;
            World.PrepareDepositTask(_prepWorld, _prepTasks->Pointer + task, scratch);
        }
    }
}
