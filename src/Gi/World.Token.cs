using System.Runtime.CompilerServices;
namespace Gi;

public static unsafe partial class World
{
    [MethodImpl(Jit.Hot)]
    public static Token Capture(byte world)
    {
        Runtime.Ensure();
        return new Token(world, GetContext(world) == null ? 0 : Runtime.WorldRevisions[world]);
    }

    [MethodImpl(Jit.Hot)]
    public static BatchStatus ApplyBatch(Token token, ReadOnlySpan<SourceCommand> commands, Span<SourceResult> results)
    {
        var status = ValidateToken(token, out var context);
        return status != BatchStatus.Applied ? status : ApplyBatchCommands(context, commands, results);
    }

    [MethodImpl(Jit.Hot)]
    public static BatchStatus Process(Token token)
    {
        var status = ValidateToken(token, out _);
        if (status != BatchStatus.Applied) return status;

        Process(token.WorldId);
        return BatchStatus.Applied;
    }

    [MethodImpl(Jit.Hot)]
    public static BatchStatus Sync(Token token, SourceId* sources, float* xs, float* ys, int count)
    {
        var status = ValidateToken(token, out var context);
        if (status != BatchStatus.Applied) return status;

        SyncColumns(context, sources, xs, ys, count);
        return BatchStatus.Applied;
    }

    [MethodImpl(Jit.Hot)]
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

    public readonly struct Token
    {
        public readonly byte WorldId;
        public readonly int RevisionAtCapture;

        [MethodImpl(Jit.Hot)]
        internal Token(byte worldId, int revisionAtCapture)
        {
            WorldId = worldId;
            RevisionAtCapture = revisionAtCapture;
        }

        public bool IsValid => RevisionAtCapture != 0;
    }
}
