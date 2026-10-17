using Xunit;

namespace Gi.Tests;

public sealed class TransformCommandTests : IDisposable
{
    private readonly byte _world = World.New();
    private readonly byte _grid;
    private readonly byte _layer;
    private readonly byte _stamp;

    public TransformCommandTests()
    {
        _grid = Grid.New(_world, 6, 0f, 0f, 64f);
        _layer = Layer.New(_world);
        _stamp = Stamp.Box(2, 2, 1);
    }

    public void Dispose()
    {
        World.Free(_world);
        Assert.True(Stamp.Free(_stamp));
    }

    private static unsafe void AssertIdentical(byte a, byte gridA, byte layerA, byte b, byte gridB, byte layerB)
    {
        Assert.Equal(World.Tick(a), World.Tick(b));
        Assert.Equal(World.Query(a, gridA, layerA, 0, 0, 64, 64), World.Query(b, gridB, layerB, 0, 0, 64, 64));
        Assert.Equal(
            World.QueryMax(a, gridA, layerA, out var ax, out var ay),
            World.QueryMax(b, gridB, layerB, out var bx, out var by));
        Assert.Equal((ax, ay), (bx, by));
        var mine = stackalloc short[64 * 64];
        var theirs = stackalloc short[64 * 64];
        World.QueryRegion(a, gridA, layerA, 0, 0, 64, 64, mine);
        World.QueryRegion(b, gridB, layerB, 0, 0, 64, 64, theirs);
        Assert.True(new Span<short>(mine, 64 * 64).SequenceEqual(new Span<short>(theirs, 64 * 64)));
        Assert.Equal(
            World.ChangedTiles(a, gridA, layerA, null),
            World.ChangedTiles(b, gridB, layerB, null));
    }

    [Fact]
    public void SmallBatch_MatchesOrderedMoveTurnScaleIncludingEdgesAndRetraction()
    {
        var twin = World.New();
        var twinGrid = Grid.New(twin, 6, 0f, 0f, 64f);
        var twinLayer = Layer.New(twin);
        try
        {
            var source = World.Place(_world, _layer, 10.5f, 10.5f, _stamp, 4);
            var twinSource = World.Place(twin, twinLayer, 10.5f, 10.5f, _stamp, 4);
            World.Turn(_world, source, 0.25f);
            World.Turn(twin, twinSource, 0.25f);
            World.Process(_world);
            World.Process(twin);
            AssertIdentical(_world, _grid, _layer, twin, twinGrid, twinLayer);

            var cases = new (float X, float Y, float Heading, float Scale)[]
            {
                (10.5f, 10.5f, 0.25f, 1f),
                (10.5f, 10.5f, float.NaN, float.NaN),
                (-0.0f, 10.5f, 0.25f, 1f),
                (12.5f, 14.5f, 0.25f + 2f * MathF.PI, 1f),
                (12.5f, 14.5f, -0.75f, 0.001f),
                (30.25f, 30.25f, float.PositiveInfinity, 300f),
                (30.25f, 30.25f, 1.5f, 2f),
            };
            for (var index = 0; index < cases.Length; index++)
            {
                var (x, y, heading, scale) = cases[index];
                var commands = new[] { World.SourceCommand.UpdateTransform(source, x, y, heading, scale) };
                var results = new World.SourceResult[1];
                Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(_world, commands, results));
                Assert.Equal(World.SourceResultStatus.Applied, results[0].Status);
                World.Move(twin, twinSource, x, y);
                World.Turn(twin, twinSource, heading);
                World.Scale(twin, twinSource, scale);
                World.Process(_world);
                World.Process(twin);
                AssertIdentical(_world, _grid, _layer, twin, twinGrid, twinLayer);
            }

            World.Remove(_world, source);
            World.Remove(twin, twinSource);
            World.Process(_world);
            World.Process(twin);
            AssertIdentical(_world, _grid, _layer, twin, twinGrid, twinLayer);
            Assert.Equal(0, World.Query(_world, _grid, _layer, 0, 0, 64, 64));
        }
        finally
        {
            World.Free(twin);
        }
    }

    [Fact]
    public void UnchangedTransforms_AreNoOpsAndReportNoChangedTiles()
    {
        var source = World.Place(_world, _layer, 10.5f, 10.5f, _stamp, 4);
        var zero = World.Place(_world, _layer, 0f, 20.5f, _stamp, 4);
        World.Turn(_world, source, 0.5f);
        World.Scale(_world, source, 1.5f);
        World.Process(_world);
        var before = World.Query(_world, _grid, _layer, 0, 0, 64, 64);

        var commands = new[]
        {
            World.SourceCommand.UpdateTransform(source, 10.5f, 10.5f, 0.5f, 1.5f),
            World.SourceCommand.UpdateTransform(source, 10.5f, 10.5f, float.NaN, float.NaN),
            World.SourceCommand.UpdateTransform(zero, -0.0f, 20.5f, 0f, 1f),
        };
        var results = new World.SourceResult[3];
        Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(_world, commands, results));
        World.Process(_world);

        Assert.Equal(before, World.Query(_world, _grid, _layer, 0, 0, 64, 64));
        unsafe
        {
            Assert.Equal(0, World.ChangedTiles(_world, _grid, _layer, null));
        }
    }

    [Fact]
    public void FadesAndExpirySurviveTransformUpdatesAndLandOnTheScheduledTick()
    {
        var twin = World.New();
        var twinGrid = Grid.New(twin, 6, 0f, 0f, 64f);
        var twinLayer = Layer.New(twin);
        try
        {
            var source = World.Place(_world, _layer, 8.5f, 8.5f, _stamp, 8);
            var twinSource = World.Place(twin, twinLayer, 8.5f, 8.5f, _stamp, 8);
            World.ApplyBatch(_world, new[]
            {
                World.SourceCommand.Fade(source, 0, 8),
                World.SourceCommand.Expire(source, 12),
            }, new World.SourceResult[2]);
            World.ApplyBatch(twin, new[]
            {
                World.SourceCommand.Fade(twinSource, 0, 8),
                World.SourceCommand.Expire(twinSource, 12),
            }, new World.SourceResult[2]);
            World.Process(_world);
            World.Process(twin);

            for (var tick = 0; tick < 14; tick++)
            {
                var x = 8.5f + tick * 1.25f;
                var y = 8.5f + (tick % 3) * 2.5f;
                var commands = new[]
                {
                    World.SourceCommand.UpdateTransform(source, x, y, 0.25f * tick, 1f + tick * 0.1f),
                };
                var results = new World.SourceResult[1];
                if (tick != 9)
                {
                    Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(_world, commands, results));
                    World.Move(twin, twinSource, x, y);
                    World.Turn(twin, twinSource, 0.25f * tick);
                    World.Scale(twin, twinSource, 1f + tick * 0.1f);
                }

                World.Process(_world);
                World.Process(twin);
                AssertIdentical(_world, _grid, _layer, twin, twinGrid, twinLayer);
            }

            Assert.Equal(0, World.Query(_world, _grid, _layer, 0, 0, 64, 64));
            Assert.Equal(0, World.Query(twin, twinGrid, twinLayer, 0, 0, 64, 64));
        }
        finally
        {
            World.Free(twin);
        }
    }

    [Fact]
    public void SetGainAndFullUpdate_StillCancelFadesAroundTransformUpdates()
    {
        var source = World.Place(_world, _layer, 8.5f, 8.5f, _stamp, 8);
        World.ApplyBatch(_world, new[] { World.SourceCommand.Fade(source, 0, 10) }, new World.SourceResult[1]);
        World.Process(_world);
        World.Process(_world);

        World.ApplyBatch(_world, new[]
        {
            World.SourceCommand.UpdateTransform(source, 12.5f, 12.5f, 0f, 1f),
        }, new World.SourceResult[1]);
        World.Process(_world);
        var fadedSum = World.Query(_world, _grid, _layer, 0, 0, 64, 64);
        Assert.True(fadedSum > 0);

        World.ApplyBatch(_world, new[]
        {
            World.SourceCommand.UpdateTransform(source, 14.5f, 14.5f, 0f, 1f),
            World.SourceCommand.SetGain(source, 6),
        }, new World.SourceResult[2]);
        World.Process(_world);
        var heldSum = World.Query(_world, _grid, _layer, 0, 0, 64, 64);
        World.Process(_world);
        Assert.Equal(heldSum, World.Query(_world, _grid, _layer, 0, 0, 64, 64));

        World.ApplyBatch(_world, new[]
        {
            World.SourceCommand.Update(source, 16.5f, 16.5f, 0f, 1f, 5),
        }, new World.SourceResult[1]);
        World.Process(_world);
        var updatedSum = World.Query(_world, _grid, _layer, 0, 0, 64, 64);
        World.Process(_world);
        Assert.Equal(updatedSum, World.Query(_world, _grid, _layer, 0, 0, 64, 64));
        Assert.NotEqual(heldSum, updatedSum);
    }

    [Fact]
    public void LargeTransformBatch_MatchesOrderedCalls_WarmWithoutManagedGrowth()
    {
        const int count = 9000;
        var twin = World.New();
        var twinGrid = Grid.New(twin, 6, 0f, 0f, 64f);
        var twinLayer = Layer.New(twin);
        try
        {
            var sources = new World.SourceId[count];
            var twinSources = new World.SourceId[count];
            for (var index = 0; index < count; index++)
            {
                var x = 1f + index % 60;
                var row = index / 60 % 60;
                var y = 1f + row;
                sources[index] = World.Place(_world, _layer, x, y, _stamp, 3);
                twinSources[index] = World.Place(twin, twinLayer, x, y, _stamp, 3);
            }

            World.Process(_world);
            World.Process(twin);

            var commands = new World.SourceCommand[count];
            var results = new World.SourceResult[count];
            for (var index = 0; index < count; index++)
            {
                var x = 1f + index % 63;
                var row = index / 63 % 60;
                var y = 1f + row;
                commands[index] = World.SourceCommand.UpdateTransform(
                    sources[index], x, y, 0.25f * (index % 4), 1f + (index % 5) * 0.25f);
            }

            Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(_world, commands, results));
            World.Process(_world);

            for (var index = 0; index < count; index++)
            {
                var x = 1f + index % 63;
                var row = index / 63 % 60;
                World.Move(twin, twinSources[index], x, 1f + row);
                World.Turn(twin, twinSources[index], 0.25f * (index % 4));
                World.Scale(twin, twinSources[index], 1f + (index % 5) * 0.25f);
            }

            World.Process(twin);
            AssertIdentical(_world, _grid, _layer, twin, twinGrid, twinLayer);

            var warm = GC.GetAllocatedBytesForCurrentThread();
            var status = World.ApplyBatch(_world, commands, results);
            World.Process(_world);
            var afterProcess = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(World.BatchStatus.Applied, status);
            Assert.Equal(warm, afterProcess);
        }
        finally
        {
            World.Free(twin);
        }
    }

    [Fact]
    public void TransformCommands_OrderWithCreationsDuplicatesRemovalsAndRewind()
    {
        var other = World.Place(_world, _layer, 40.5f, 40.5f, _stamp, 2);
        World.Process(_world);
        World.Record(_world);
        var before = World.Query(_world, _grid, _layer, 0, 0, 64, 64);

        var stale = new World.SourceId(900, 7);
        var commands = new[]
        {
            World.SourceCommand.Place(_layer, 12.5f, 12.5f, _stamp, 5),
            World.SourceCommand.UpdateTransform(World.SourceTarget.CreatedBy(0), 14.5f, 14.5f, MathF.PI / 2, 2f),
            World.SourceCommand.UpdateTransform(World.SourceTarget.CreatedBy(0), 16.5f, 16.5f, MathF.PI, 1f),
            World.SourceCommand.UpdateTransform(stale, 1.5f, 1.5f, 0f, 1f),
            World.SourceCommand.Fade(World.SourceTarget.CreatedBy(0), 0, 6),
            World.SourceCommand.Remove(other),
            World.SourceCommand.UpdateTransform(other, 20.5f, 20.5f, 0f, 1f),
        };
        var results = new World.SourceResult[7];
        Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(_world, commands, results));

        Assert.Equal(World.SourceResultStatus.Applied, results[0].Status);
        Assert.Equal(World.SourceResultStatus.Applied, results[1].Status);
        Assert.Equal(World.SourceResultStatus.Applied, results[2].Status);
        Assert.Equal(World.SourceResultStatus.InvalidSource, results[3].Status);
        Assert.Equal(World.SourceResultStatus.Applied, results[4].Status);
        Assert.Equal(World.SourceResultStatus.Applied, results[5].Status);
        Assert.Equal(World.SourceResultStatus.InvalidSource, results[6].Status);

        var created = results[0].SourceId;
        World.Process(_world);
        var fading = World.QueryAt(_world, _grid, _layer, 16.5f, 16.5f);
        Assert.True(fading > 0 && fading < 5);
        for (var tick = 0; tick < 6; tick++) World.Process(_world);
        Assert.Equal(0, World.QueryAt(_world, _grid, _layer, 16.5f, 16.5f));
        Assert.Equal(0, World.QueryAt(_world, _grid, _layer, 40.5f, 40.5f));

        World.Rewind(_world);
        World.Process(_world);
        Assert.Equal(before, World.Query(_world, _grid, _layer, 0, 0, 64, 64));
        Assert.Equal(2, World.QueryAt(_world, _grid, _layer, 40.5f, 40.5f));
        var reopened = World.Place(_world, _layer, 50.5f, 50.5f, _stamp, 1);
        Assert.NotEqual(created, reopened);
    }
}
