namespace Gi;

internal static unsafe partial class ResolvePool
{
    internal const int MaximumSlots = 5;
    private const int SourceBatchChunk = 128;
    private static WorldCtx* _sourceBatchWorld;
    private static World.SourceCommand* _sourceBatchCommands;
    private static int _sourceBatchCount;
    private static int _sourceBatchCursor;

    internal static void PrepareSourceBatch(WorldCtx* context, World.SourceCommand* commands)
        => RunSourceBatch(context, commands, 6);

    internal static void ApplySourceBatch(WorldCtx* context) => RunSourceBatch(context, null, 7);

    private static void RunSourceBatch(WorldCtx* context, World.SourceCommand* commands, int phase)
    {
        WaitParked();
        _sourceBatchWorld = context;
        _sourceBatchCommands = commands;
        _sourceBatchCount = context->BatchSources.Length;
        _sourceBatchCursor = _sourceBatchCount;
        _remaining = _workers;
        Volatile.Write(ref _phase, phase);
        for (var index = 0; index < _workers; index++) _wake[index].Set();
        SourceBatchShare(phase);
        Drain();
        Volatile.Write(ref _phase, 0);
        _sourceBatchWorld = null;
        _sourceBatchCommands = null;
    }

    private static void SourceBatchShare(int phase)
    {
        while (true)
        {
            var claimed = Interlocked.Add(ref _sourceBatchCursor, -SourceBatchChunk);
            var last = claimed + SourceBatchChunk;
            if (last <= 0) return;
            if (last > _sourceBatchCount) last = _sourceBatchCount;
            var first = claimed < 0 ? 0 : claimed;
            if (phase == 6)
                World.PrepareSourceBatchRange(_sourceBatchWorld, _sourceBatchCommands, first, last);
            else World.ApplySourceBatchRange(_sourceBatchWorld, first, last);
        }
    }
}
