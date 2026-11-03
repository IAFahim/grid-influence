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
        fixed (GiRoot* root = &Runtime.Root) return ApplyBatch(root, token, commands, results);
    }
    [MethodImpl(Jit.Hot)]
    internal static BatchStatus ApplyBatch(GiRoot* root, Token token, ReadOnlySpan<SourceCommand> commands, Span<SourceResult> results)
    {
        var status = ValidateToken(root, token, out var context);
        return status != BatchStatus.Applied ? status : ApplyBatchCommands(context, commands, results);
    }

    [MethodImpl(Jit.Hot)]
    public static BatchStatus Process(Token token)
    {
        fixed (GiRoot* root = &Runtime.Root) return Process(root, token);
    }
    [MethodImpl(Jit.Hot)]
    internal static BatchStatus Process(GiRoot* root, Token token)
    {
        var status = ValidateToken(root, token, out _);
        if (status != BatchStatus.Applied) return status;
        Process(root, token.WorldId);
        return BatchStatus.Applied;
    }

    [MethodImpl(Jit.Hot)]
    public static BatchStatus Sync(Token token, SourceId* sources, float* xs, float* ys, int count)
    {
        fixed (GiRoot* root = &Runtime.Root) return Sync(root, token, sources, xs, ys, count);
    }
    [MethodImpl(Jit.Hot)]
    internal static BatchStatus Sync(GiRoot* root, Token token, SourceId* sources, float* xs, float* ys, int count)
    {
        var status = ValidateToken(root, token, out var context);
        if (status != BatchStatus.Applied) return status;
        SyncColumns(context, sources, xs, ys, count);
        return BatchStatus.Applied;
    }

    [MethodImpl(Jit.Hot)]
    private static BatchStatus ValidateToken(Token token, out WorldCtx* context)
    {
        fixed (GiRoot* root = &Runtime.Root) return ValidateToken(root, token, out context);
    }
    [MethodImpl(Jit.Hot)]
    internal static BatchStatus ValidateToken(GiRoot* root, Token token, out WorldCtx* context)
    {
        context = null;
        if (token.RevisionAtCapture == 0 || token.WorldId >= root->WorldCount) return BatchStatus.InvalidWorld;
        var candidate = root->Worlds + token.WorldId;
        if (candidate->Grids == null) return BatchStatus.InvalidWorld;
        if (root->WorldRevisions[token.WorldId] != token.RevisionAtCapture) return BatchStatus.StaleRevision;
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
