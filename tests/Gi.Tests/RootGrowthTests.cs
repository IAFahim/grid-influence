using Xunit;

namespace Gi.Tests;

public sealed unsafe class RootGrowthTests : IDisposable
{
    private readonly byte _world;
    private readonly byte _layer;
    private readonly byte _transient;
    private readonly byte _box;

    public RootGrowthTests()
    {
        _world = World.New();
        Grid.New(_world, 6, 0f, 0f, 64f);
        _layer = Layer.New(_world);
        _transient = Layer.New(_world, Layer.Residency.Transient, Layer.Precision.Exact);
        _box = Stamp.Box(1, 1, 1);
        World.Process(_world);
    }

    public void Dispose()
    {
        World.Free(_world);
        Assert.True(Stamp.Free(_box));
    }

    [Fact]
    public void WarmGrowthPathsRunWithoutStaticRoot()
    {
        const int count = 40000;
        var token = World.Capture(_world);
        var beforeTick = World.Tick(_world);
        var captured = Runtime.Root;
        Runtime.Root = default;
        try
        {
            GiRoot* root = &captured;
            Assert.True(root->Worlds != null);

            var commands = new World.SourceCommand[count];
            for (var i = 0; i < count; i++)
                commands[i] = World.SourceCommand.Place(_layer, (i % 64) + 0.5f, ((i / 64) % 64) + 0.5f, _box, 4,
                    scale: 2f);
            var results = new World.SourceResult[count];
            Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(root, token, commands, results));
            for (var i = 0; i < count; i++)
                Assert.Equal(World.SourceResultStatus.Applied, results[i].Status);

            for (var i = 0; i < count; i++)
            {
                var target = World.SourceTarget.Existing(results[i].SourceId);
                commands[i] = (i % 5) switch
                {
                    0 => World.SourceCommand.Move(target, (i % 64) + 1.5f, ((i / 64) % 64) + 1.5f),
                    1 => World.SourceCommand.Turn(target, 0.5f),
                    2 => World.SourceCommand.Scale(target, 2f),
                    3 => World.SourceCommand.Fade(target, 2, 4),
                    _ => World.SourceCommand.SetGain(target, 3),
                };
            }

            Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(root, token, commands, results));
            for (var i = 0; i < count; i++)
                Assert.Equal(World.SourceResultStatus.Applied, results[i].Status);

            var sources = stackalloc World.SourceId[count];
            var xs = stackalloc float[count];
            var ys = stackalloc float[count];
            for (var i = 0; i < count; i++)
            {
                sources[i] = results[i].SourceId;
                xs[i] = (i % 64) + 2.5f;
                ys[i] = ((i / 64) % 64) + 2.5f;
            }

            Assert.Equal(World.BatchStatus.Applied, World.Sync(root, token, sources, xs, ys, count));

            var headings = stackalloc float[count];
            var scales = stackalloc float[count];
            for (var i = 0; i < count; i++)
            {
                headings[i] = 0.25f;
                scales[i] = 2f;
            }

            var words = (count + 63) >> 6;
            var invalid = stackalloc ulong[words];
            var ranges = stackalloc World.TransformRange[1];
            ranges[0] = new World.TransformRange
            {
                Sources = sources,
                Xs = xs,
                Ys = ys,
                Headings = headings,
                Scales = scales,
                Invalid = invalid,
                InvalidWords = words,
                Count = count,
            };
            Assert.Equal(World.BatchStatus.Applied,
                World.SyncTransforms(root, token, new ReadOnlySpan<World.TransformRange>(ranges, 1)));

            var sx = new float[count];
            var sy = new float[count];
            for (var i = 0; i < count; i++)
            {
                sx[i] = (i % 64) + 0.5f;
                sy[i] = ((i / 64) % 64) + 0.5f;
            }

            Assert.Equal(count, World.Splat(root, _world, _transient, sx, sy, _box, 4));
            Assert.Equal(World.BatchStatus.Applied, World.Process(root, token));
            Assert.Equal(beforeTick + 1, World.Tick(root, _world));

            const int probes = 4;
            var px = stackalloc float[probes];
            var py = stackalloc float[probes];
            var values = stackalloc short[probes];
            var whole = stackalloc ulong[(probes + 63) >> 6];
            var excludes = stackalloc World.SourceId[probes];
            for (var i = 0; i < probes; i++)
            {
                px[i] = 10f + i * 5;
                py[i] = 10f + i * 5;
                excludes[i] = results[i].SourceId;
            }

            var read = World.SenseColumn(root, _world, _layer, px, py, excludes, probes, values, whole);
            Assert.True(read == World.ReadStatus.Complete || read == World.ReadStatus.Partial ||
                        read == World.ReadStatus.Outside);
            var mask = stackalloc ulong[(probes + 63) >> 6];
            Assert.True(World.ChangedMask(root, _world, _layer, px, py, 8f, 0, probes, mask) >= 0);
            Assert.Equal(World.BatchStatus.Applied, World.Process(root, token));
            Assert.Equal(beforeTick + 2, World.Tick(root, _world));

            Assert.True(Runtime.Root.Worlds == null);
            Assert.Equal(0, Runtime.Root.Calls);
            Assert.Equal(0, Runtime.Root.WorldCount);
        }
        finally
        {
            Runtime.Root = captured;
        }

        Assert.True(Runtime.Root.Worlds != null);
    }
}
