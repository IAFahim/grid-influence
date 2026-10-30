using Xunit;

namespace Gi.Tests;

public sealed unsafe class AuditRewindTests
{
    [Fact]
    public void RewindMustRestoreSubAnchorPositionsOfTheSourceColumns()
    {
        var world = World.New();
        var box = Stamp.Box(4, 4, 10);
        try
        {
            Grid.New(world, 7, 0f, 0f, 128f);
            var layer = Layer.New(world, Layer.Precision.Cell);
            var id = World.Place(world, layer, 50.25f, 50.25f, box, 2);
            World.Process(world);
            World.Record(world);
            World.Move(world, id, 50.5f, 50.5f);
            World.Process(world);
            World.Rewind(world);
            World.Process(world);
            World.TryGetSourceSpec(world, id, out var spec);
            Assert.Equal(50.25f, spec.X);
            Assert.Equal(50.25f, spec.Y);
        }
        finally
        {
            World.Free(world);
            Stamp.Free(box);
        }
    }

    [Fact]
    public void RewindMustRestoreSubAnchorPositionsWrittenByColumnSync()
    {
        var world = World.New();
        var box = Stamp.Box(4, 4, 10);
        var ids = stackalloc World.SourceId[3];
        var xs = stackalloc float[3];
        var ys = stackalloc float[3];
        try
        {
            Grid.New(world, 7, 0f, 0f, 128f);
            var layer = Layer.New(world, Layer.Precision.Cell);
            for (var i = 0; i < 3; i++)
            {
                xs[i] = 20.25f + i * 10;
                ys[i] = 30.25f;
                ids[i] = World.Place(world, layer, xs[i], ys[i], box, 2);
            }

            World.Process(world);
            World.Record(world);
            xs[0] += 0.5f;
            xs[1] += 3f;
            xs[2] += 0.125f;
            World.Sync(world, ids, xs, ys, 3);
            World.Process(world);
            xs[2] += 0.125f;
            World.Sync(world, ids, xs, ys, 3);
            World.Process(world);
            World.Rewind(world);
            World.Process(world);
            for (var i = 0; i < 3; i++)
            {
                World.TryGetSourceSpec(world, ids[i], out var spec);
                Assert.Equal(20.25f + i * 10, spec.X);
                Assert.Equal(30.25f, spec.Y);
            }
        }
        finally
        {
            World.Free(world);
            Stamp.Free(box);
        }
    }

    [Fact]
    public void RewindMustRestoreSubTurnAnglesOfTheSourceColumns()
    {
        var world = World.New();
        var box = Stamp.Box(4, 4, 10);
        try
        {
            Grid.New(world, 7, 0f, 0f, 128f);
            var layer = Layer.New(world, default(Layer.Precision));
            var id = World.Place(world, layer, 50.25f, 50.25f, box, 2);
            World.Turn(world, id, 0.5f);
            World.Process(world);
            World.TryGetSourceSpec(world, id, out var before);
            World.Record(world);
            World.Turn(world, id, 0.5001f);
            World.Process(world);
            World.Rewind(world);
            World.Process(world);
            World.TryGetSourceSpec(world, id, out var after);
            Assert.Equal(before.HeadingRadians, after.HeadingRadians);
        }
        finally
        {
            World.Free(world);
            Stamp.Free(box);
        }
    }
}
