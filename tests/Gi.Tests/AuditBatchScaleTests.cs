using Xunit;

namespace Gi.Tests;

public sealed unsafe class AuditBatchScaleTests
{
    [Fact]
    public void BatchPlaceMustHonourTheFootprintCapForItsScale()
    {
        var world = World.New();
        var box = Stamp.Box(100, 100, 20);
        try
        {
            Grid.New(world, 8, 0f, 0f, 256f);
            var layer = Layer.New(world, Layer.Precision.Cell);
            var scalar = World.Place(world, layer, 128f, 128f, box, 1);
            Assert.NotEqual(default, scalar);
            Assert.Equal(World.WriteStatus.CapacityReached, World.Scale(world, scalar, 10f));

            var commands = new[] { World.SourceCommand.Place(layer, 128f, 128f, box, 1, 0f, 10f) };
            var results = new World.SourceResult[1];
            Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(world, commands, results));
            Assert.Equal(World.SourceResultStatus.InvalidStamp, results[0].Status);
            Assert.Equal(default, results[0].SourceId);
            World.Process(world);
            Assert.Equal(100 * 100 * 20, World.Query(world, 0, layer, 0, 0, 256, 256));
        }
        finally
        {
            World.Free(world);
            Stamp.Free(box);
        }
    }

    [Fact]
    public void BatchPlaceStoresAFittingScaleExactlyAsScalarScaleDoes()
    {
        var world = World.New();
        var box = Stamp.Box(4, 4, 20);
        try
        {
            Grid.New(world, 8, 0f, 0f, 256f);
            var layer = Layer.New(world, Layer.Precision.Cell);
            var commands = new[]
            {
                World.SourceCommand.Place(layer, 64f, 64f, box, 1, 0f, 2.5f),
                World.SourceCommand.Place(layer, 128f, 128f, box, 1, 0f, float.NaN)
            };
            var results = new World.SourceResult[2];
            Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(world, commands, results));
            Assert.Equal(World.SourceResultStatus.Applied, results[0].Status);
            Assert.Equal(World.SourceResultStatus.Applied, results[1].Status);
            World.TryGetSourceSpec(world, results[0].SourceId, out var scaled);
            World.TryGetSourceSpec(world, results[1].SourceId, out var unit);
            Assert.Equal(2.5f, scaled.Scale);
            Assert.Equal(1f, unit.Scale);
        }
        finally
        {
            World.Free(world);
            Stamp.Free(box);
        }
    }
}
