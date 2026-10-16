using Xunit;

namespace Gi.Tests;

public sealed class TokenTests : IDisposable
{
    private readonly byte _world = World.New();
    private readonly byte _grid;
    private readonly byte _layer;
    private readonly byte _stamp;

    public TokenTests()
    {
        _grid = Grid.New(_world, 6, 0f, 0f, 64f);
        _layer = Layer.New(_world);
        _stamp = Stamp.Box(1, 1, 1);
    }

    public void Dispose()
    {
        World.Free(_world);
        Assert.True(Stamp.Free(_stamp));
    }

    [Fact]
    public void LiveToken_StaysValidAcrossMutationAndProcessing()
    {
        var source = World.Place(_world, _layer, 10.5f, 10.5f, _stamp, 4);
        World.Process(_world);

        var token = World.Capture(_world);
        Assert.True(token.IsValid);
        Assert.Equal(_world, token.WorldId);
        Assert.Equal(World.Revision(_world), token.RevisionAtCapture);

        World.Move(_world, source, 20.5f, 20.5f);
        World.Turn(_world, source, MathF.PI / 2f);
        World.Scale(_world, source, 2f);
        Assert.Equal(World.BatchStatus.Applied, World.Process(token));

        var commands = new[] { World.SourceCommand.Move(source, 30.5f, 30.5f) };
        var results = new World.SourceResult[1];
        Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(token, commands, results));
        Assert.Equal(World.SourceResultStatus.Applied, results[0].Status);
        Assert.Equal(World.BatchStatus.Applied, World.Process(token));
        Assert.True(token.IsValid);
    }

    [Fact]
    public void ClearedWorld_TokenIsStaleAndFreshTokenWorks()
    {
        var source = World.Place(_world, _layer, 10.5f, 10.5f, _stamp, 4);
        World.Process(_world);
        var token = World.Capture(_world);

        World.Clear(_world);
        var fresh = World.Capture(_world);

        var commands = new[] { World.SourceCommand.Move(source, 1.5f, 1.5f) };
        var results = new[] { new World.SourceResult(new World.SourceId(123456, 9), World.SourceResultStatus.Applied) };
        Assert.Equal(World.BatchStatus.StaleRevision, World.ApplyBatch(token, commands, results));
        Assert.Equal(new World.SourceId(123456, 9), results[0].SourceId);
        Assert.Equal(World.SourceResultStatus.Applied, results[0].Status);

        unsafe
        {
            var ids = stackalloc[] { source };
            var xs = stackalloc[] { 2.5f };
            var ys = stackalloc[] { 2.5f };
            Assert.Equal(World.BatchStatus.StaleRevision, World.Sync(token, ids, xs, ys, 1));
        }

        Assert.Equal(World.BatchStatus.StaleRevision, World.Process(token));

        var replacement = World.SourceCommand.Place(_layer, 5.5f, 5.5f, _stamp, 3);
        var freshResults = new World.SourceResult[1];
        Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(fresh, new[] { replacement }, freshResults));
        Assert.Equal(World.BatchStatus.Applied, World.Process(fresh));
        Assert.Equal(3, World.Query(_world, _grid, _layer, 0, 0, 64, 64));
    }

    [Fact]
    public void RewoundWorld_TokenIsStale()
    {
        World.Place(_world, _layer, 10.5f, 10.5f, _stamp, 4);
        World.Process(_world);
        World.Record(_world);
        var source = World.Place(_world, _layer, 30.5f, 30.5f, _stamp, 2);
        var token = World.Capture(_world);

        World.Rewind(_world);

        var commands = new[] { World.SourceCommand.Move(source, 1.5f, 1.5f) };
        var results = new World.SourceResult[1];
        Assert.Equal(World.BatchStatus.StaleRevision, World.ApplyBatch(token, commands, results));
        Assert.Equal(World.BatchStatus.StaleRevision, World.Process(token));

        var fresh = World.Capture(_world);
        Assert.Equal(World.BatchStatus.Applied, World.Process(fresh));
    }

    [Fact]
    public void FreedWorld_TokenIsInvalidWorldDespiteNonzeroRevision()
    {
        var scratch = World.New();
        var token = World.Capture(scratch);
        Assert.True(token.IsValid);

        Assert.True(World.Free(scratch));
        Assert.NotEqual(0, World.Revision(scratch));
        Assert.False(World.Capture(scratch).IsValid);

        var commands = new[] { World.SourceCommand.Place(0, 1f, 1f, _stamp, 1) };
        var results = new World.SourceResult[1];
        Assert.Equal(World.BatchStatus.InvalidWorld, World.ApplyBatch(token, commands, results));
        Assert.Equal(World.BatchStatus.InvalidWorld, World.Process(token));
        unsafe
        {
            Assert.Equal(World.BatchStatus.InvalidWorld, World.Sync(token, null, null, null, 0));
        }
    }

    [Fact]
    public void NeverAllocatedSlot_CaptureIsInvalid()
    {
        Assert.False(World.Capture(byte.MaxValue).IsValid);

        var commands = new[] { World.SourceCommand.Place(0, 1f, 1f, _stamp, 1) };
        var results = new World.SourceResult[1];
        Assert.Equal(World.BatchStatus.InvalidWorld, World.ApplyBatch(World.Capture(byte.MaxValue), commands, results));
        Assert.Equal(World.BatchStatus.InvalidWorld, World.Process(World.Capture(byte.MaxValue)));
    }

    [Fact]
    public void ReusedSlot_OldTokenIsStaleAndNeverTicksTheNewOccupant()
    {
        var scratch = World.New();
        Grid.New(scratch, 5, 0f, 0f, 32f);
        Layer.New(scratch);
        var token = World.Capture(scratch);
        Assert.True(World.Free(scratch));

        var keep = new List<byte>();
        byte reused;
        while (true)
        {
            var candidate = World.New();
            if (candidate == scratch)
            {
                reused = candidate;
                break;
            }

            keep.Add(candidate);
        }

        var commands = new[] { World.SourceCommand.Place(0, 1f, 1f, _stamp, 1) };
        var results = new World.SourceResult[1];
        Assert.Equal(World.BatchStatus.StaleRevision, World.ApplyBatch(token, commands, results));
        Assert.Equal(World.BatchStatus.StaleRevision, World.Process(token));
        Assert.Equal(0, World.Tick(reused));

        Assert.Equal(World.BatchStatus.Applied, World.Process(World.Capture(reused)));
        Assert.Equal(1, World.Tick(reused));

        Assert.True(World.Free(reused));
        foreach (var id in keep) Assert.True(World.Free(id));
    }

    [Fact]
    public void RejectedBatch_TwinWorldStaysByteIdentical()
    {
        var twin = World.New();
        var twinGrid = Grid.New(twin, 6, 0f, 0f, 64f);
        var twinLayer = Layer.New(twin);
        try
        {
            var first = World.Place(_world, _layer, 10.5f, 10.5f, _stamp, 4);
            var second = World.Place(_world, _layer, 40.5f, 12.5f, _stamp, 2);
            var twinFirst = World.Place(twin, twinLayer, 10.5f, 10.5f, _stamp, 4);
            var twinSecond = World.Place(twin, twinLayer, 40.5f, 12.5f, _stamp, 2);
            World.ApplyBatch(_world, new[]
            {
                World.SourceCommand.Fade(first, 0, 12),
                World.SourceCommand.Expire(second, 30),
            }, new World.SourceResult[2]);
            World.ApplyBatch(twin, new[]
            {
                World.SourceCommand.Fade(twinFirst, 0, 12),
                World.SourceCommand.Expire(twinSecond, 30),
            }, new World.SourceResult[2]);
            World.Process(_world);
            World.Process(twin);

            World.Record(_world);
            World.Record(twin);
            World.Move(_world, first, 20.5f, 20.5f);
            World.Move(twin, twinFirst, 20.5f, 20.5f);
            var token = World.Capture(_world);
            World.Rewind(_world);
            World.Rewind(twin);

            var stale = new[]
            {
                World.SourceCommand.Move(first, 1.5f, 1.5f),
                World.SourceCommand.Place(_layer, 8.5f, 8.5f, _stamp, 5),
            };
            var results = new[] { new World.SourceResult(new World.SourceId(77, 9), World.SourceResultStatus.InvalidSource) };
            Assert.Equal(World.BatchStatus.StaleRevision, World.ApplyBatch(token, stale, results));
            Assert.Equal(new World.SourceId(77, 9), results[0].SourceId);
            Assert.Equal(World.BatchStatus.StaleRevision, World.Process(token));
            unsafe
            {
                var ids = stackalloc[] { first, second };
                var xs = stackalloc[] { 3.5f, 3.5f };
                var ys = stackalloc[] { 3.5f, 3.5f };
                Assert.Equal(World.BatchStatus.StaleRevision, World.Sync(token, ids, xs, ys, 2));
            }

            for (var i = 0; i < 5; i++)
            {
                World.Process(_world);
                World.Process(twin);
            }

            Assert.Equal(World.Tick(twin), World.Tick(_world));
            Assert.Equal(
                World.Query(_world, _grid, _layer, 0, 0, 64, 64),
                World.Query(twin, twinGrid, twinLayer, 0, 0, 64, 64));
            unsafe
            {
                var mine = stackalloc short[64 * 64];
                var theirs = stackalloc short[64 * 64];
                World.QueryRegion(_world, _grid, _layer, 0, 0, 64, 64, mine);
                World.QueryRegion(twin, twinGrid, twinLayer, 0, 0, 64, 64, theirs);
                Assert.True(new Span<short>(mine, 64 * 64).SequenceEqual(new Span<short>(theirs, 64 * 64)));
                Assert.Equal(
                    World.ChangedTiles(_world, _grid, _layer, null),
                    World.ChangedTiles(twin, twinGrid, twinLayer, null));
            }

            Assert.Equal(
                World.QueryMax(_world, _grid, _layer, out _, out _),
                World.QueryMax(twin, twinGrid, twinLayer, out _, out _));
        }
        finally
        {
            World.Free(twin);
        }
    }

    [Fact]
    public void RevisionWrap_StaleTokenStillRejectedAndFreshCaptureWorks()
    {
        unsafe
        {
            Runtime.WorldRevisions[_world] = int.MaxValue;
            var token = World.Capture(_world);
            Assert.Equal(int.MaxValue, token.RevisionAtCapture);

            World.Clear(_world);
            Assert.Equal(int.MinValue, World.Revision(_world));

            var commands = new[] { World.SourceCommand.Place(_layer, 1.5f, 1.5f, _stamp, 1) };
            var results = new World.SourceResult[1];
            Assert.Equal(World.BatchStatus.StaleRevision, World.ApplyBatch(token, commands, results));
            Assert.Equal(World.BatchStatus.StaleRevision, World.Process(token));

            Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(World.Capture(_world), commands, results));
            Assert.Equal(World.BatchStatus.Applied, World.Process(World.Capture(_world)));
        }
    }

    [Fact]
    public void Tokens_CaptureFillConsumeProcessFromNativeStorage()
    {
        var source = World.Place(_world, _layer, 10.5f, 10.5f, _stamp, 4);
        World.Process(_world);
        var token = World.Capture(_world);

        unsafe
        {
            var commands = stackalloc World.SourceCommand[2];
            commands[0] = World.SourceCommand.Move(source, 20.5f, 20.5f);
            commands[1] = World.SourceCommand.SetGain(source, 6);
            var results = stackalloc World.SourceResult[2];

            Assert.Equal(World.BatchStatus.Applied,
                World.ApplyBatch(token, new Span<World.SourceCommand>(commands, 2), new Span<World.SourceResult>(results, 2)));
            Assert.Equal(World.BatchStatus.Applied, World.Process(token));
        }

        Assert.Equal(0, World.QueryAt(_world, _grid, _layer, 10.5f, 10.5f));
        Assert.Equal(6, World.QueryAt(_world, _grid, _layer, 20.5f, 20.5f));
    }
}
