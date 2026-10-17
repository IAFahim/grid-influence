using Xunit;

namespace Gi.Tests;

public sealed class SourceSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecycledSourcesUseRemovalOrderAcrossBatchBoundaries(bool batch)
    {
        var world = World.New();
        var stamp = Stamp.Box(1, 1, 12);
        try
        {
            var grid = Grid.New(world, 6, 0, 0, 64);
            var layer = Layer.New(world);
            var first = World.Place(world, layer, 8.5f, 8.5f, stamp, 1);
            var second = World.Place(world, layer, 16.5f, 16.5f, stamp, 1);
            var third = World.Place(world, layer, 24.5f, 24.5f, stamp, 1);
            World.Process(world);
            World.Remove(world, first);
            World.SourceId next;
            World.SourceId last;
            if (batch)
            {
                World.SourceCommand[] commands =
                [
                    World.SourceCommand.Remove(second),
                    World.SourceCommand.Place(layer, 32.5f, 32.5f, stamp, 2),
                    World.SourceCommand.Remove(third),
                    World.SourceCommand.Place(layer, 40.5f, 40.5f, stamp, 3)
                ];
                var results = new World.SourceResult[commands.Length];
                Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(world, commands, results));
                Assert.All(results, result => Assert.Equal(World.SourceResultStatus.Applied, result.Status));
                next = results[1].SourceId;
                last = results[3].SourceId;
            }
            else
            {
                World.Remove(world, second);
                next = World.Place(world, layer, 32.5f, 32.5f, stamp, 2);
                World.Remove(world, third);
                last = World.Place(world, layer, 40.5f, 40.5f, stamp, 3);
            }

            Assert.Equal(first.Slot, next.Slot);
            Assert.Equal(second.Slot, last.Slot);
            var remaining = World.Place(world, layer, 48.5f, 48.5f, stamp, 4);
            Assert.Equal(third.Slot, remaining.Slot);
            World.Move(world, first, 1.5f, 1.5f);
            World.Remove(world, second);
            World.Process(world);
            Assert.Equal(0, World.Query(world, grid, layer, 1, 1));
            Assert.Equal(24, World.Query(world, grid, layer, 32, 32));
            Assert.Equal(36, World.Query(world, grid, layer, 40, 40));
            Assert.Equal(48, World.Query(world, grid, layer, 48, 48));
            Assert.Equal(0, World.Query(world, grid, layer, 8, 8));
        }
        finally { World.Free(world); Stamp.Free(stamp); }
    }

    [Theory]
    [InlineData(67)]
    [InlineData(9001)]
    public unsafe void TransformRangesRejectNonfinitePositionsAndKeepTimers(int count)
    {
        var world = World.New();
        var stamp = Stamp.Box(1, 1, 16);
        try
        {
            Grid.New(world, 6, 0, 0, 64);
            var layer = Layer.New(world);
            var source = World.Place(world, layer, 8.5f, 8.5f, stamp, 8);
            World.Fade(world, source, 0, 20);
            World.Expire(world, source, 40);
            World.Process(world);
            var ids = Enumerable.Repeat(source, count).ToArray();
            var xs = Enumerable.Repeat(8.5f, count).ToArray();
            var ys = Enumerable.Repeat(8.5f, count).ToArray();
            var mask = new ulong[(count + 63) >> 6];
            xs[0] = float.NaN;
            xs[63] = float.PositiveInfinity;
            ys[64] = float.NegativeInfinity;
            fixed (World.SourceId* id = ids)
            fixed (float* x = xs, y = ys)
            fixed (ulong* invalid = mask)
            {
                var range = new World.TransformRange
                {
                    Sources = id, Xs = x, Ys = y, Invalid = invalid, InvalidWords = mask.Length, Count = count
                };
                Assert.Equal(World.BatchStatus.Applied, World.SyncTransforms(World.Capture(world), new ReadOnlySpan<World.TransformRange>(&range, 1)));
            }

            Assert.Equal((1UL << 63) | 1, mask[0]);
            Assert.Equal(1UL, mask[1]);
            Assert.True(World.TryGetSourceSpec(world, source, out var spec));
            Assert.Equal(8.5f, spec.X);
            Assert.Equal(8.5f, spec.Y);
            for (var tick = 0; tick < 39; tick++) World.Process(world);
            Assert.False(World.TryGetSourceSpec(world, source, out _));
        }
        finally { World.Free(world); Stamp.Free(stamp); }
    }
}
