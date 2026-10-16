namespace Gi;

public static unsafe partial class World
{
    public readonly struct Token
    {
        public readonly byte WorldId;
        public readonly int RevisionAtCapture;

        internal Token(byte worldId, int revisionAtCapture)
        {
            WorldId = worldId;
            RevisionAtCapture = revisionAtCapture;
        }

        public bool IsValid => RevisionAtCapture != 0;
    }

    public static Token Capture(byte world)
    {
        Runtime.Ensure();
        return new Token(world, GetContext(world) == null ? 0 : Runtime.WorldRevisions[world]);
    }

    public static BatchStatus ApplyBatch(Token token, ReadOnlySpan<SourceCommand> commands, Span<SourceResult> results)
    {
        var status = ValidateToken(token, out var context);
        return status != BatchStatus.Applied ? status : ApplyBatchCommands(context, commands, results);
    }

    public static BatchStatus Process(Token token)
    {
        var status = ValidateToken(token, out _);
        if (status != BatchStatus.Applied) return status;

        Process(token.WorldId);
        return BatchStatus.Applied;
    }

    public static BatchStatus Sync(Token token, SourceId* sources, float* xs, float* ys, int count)
    {
        var status = ValidateToken(token, out var context);
        if (status != BatchStatus.Applied) return status;

        SyncColumns(context, sources, xs, ys, count);
        return BatchStatus.Applied;
    }

    private static BatchStatus ValidateToken(Token token, out WorldCtx* context)
    {
        context = null;
        if (token.RevisionAtCapture == 0 || token.WorldId >= _worldCount) return BatchStatus.InvalidWorld;

        var candidate = Runtime.Worlds + token.WorldId;
        if (candidate->Grids == null) return BatchStatus.InvalidWorld;
        if (Runtime.WorldRevisions[token.WorldId] != token.RevisionAtCapture) return BatchStatus.StaleRevision;

        context = candidate;
        return BatchStatus.Applied;
    }
}
