using Xunit;

namespace Gi.Tests;

public sealed unsafe class RootEntriesTests : IDisposable
{
    private readonly byte _world;
    private readonly byte _grid;
    private readonly byte _layer;
    private readonly byte _transient;
    private readonly byte _box;
    private World.SourceId _source;

    public RootEntriesTests()
    {
        _world = World.New();
        _grid = Grid.New(_world, 6, 0f, 0f, 64f);
        _layer = Layer.New(_world);
        _transient = Layer.New(_world, Layer.Residency.Transient, Layer.Precision.Exact);
        _box = Stamp.Box(1, 1, 1);
        _source = World.Place(_world, _layer, 10.5f, 10.5f, _box, 4);
        World.Process(_world);
        var xs = new float[] { 5f, 15f };
        var ys = new float[] { 5f, 15f };
        World.Splat(_world, _transient, xs, ys, _box, 4);
        World.Process(_world);
    }

    public void Dispose()
    {
        World.Free(_world);
        Assert.True(Stamp.Free(_box));
    }

    private GiRoot _captured;

    [Fact]
    public void EntriesMatchPublicWithoutTouchingStatic()
    {
        var token = World.Capture(_world);
        var beforeTick = World.Tick(_world);
        var beforeCalls = Runtime.Root.Calls;
        var captured = Runtime.Root;
        _captured = captured;
        Runtime.Root = default;
        try
        {
            GiRoot* root = &captured;
            {
                Assert.True(root->Worlds != null);
                var beforeRootCalls = captured.Calls;
                var status = World.ApplyBatch(root, token, ReadOnlySpan<World.SourceCommand>.Empty, Span<World.SourceResult>.Empty);
                Assert.Equal(World.BatchStatus.Applied, status);
                var move = new World.SourceCommand[] { World.SourceCommand.Move(World.SourceTarget.Existing(_source), 20.5f, 20.5f) };
                var results = new World.SourceResult[1];
                status = World.ApplyBatch(root, token, move, results);
                Assert.Equal(World.BatchStatus.Applied, status);
                var rangeSources = stackalloc World.SourceId[1];
                var rangeXs = stackalloc float[1];
                var rangeYs = stackalloc float[1];
                var rangeInvalid = stackalloc ulong[1];
                rangeSources[0] = _source;
                rangeXs[0] = 30.5f;
                rangeYs[0] = 30.5f;
                var ranges = stackalloc World.TransformRange[1];
                ranges[0] = new World.TransformRange { Sources = rangeSources, Xs = rangeXs, Ys = rangeYs, Invalid = rangeInvalid, InvalidWords = 1, Count = 1 };
                status = World.SyncTransforms(root, token, new ReadOnlySpan<World.TransformRange>(ranges, 1));
                Assert.Equal(World.BatchStatus.Applied, status);
                var sx = new float[] { 8f, 18f };
                var sy = new float[] { 8f, 18f };
                var applied = World.Splat(root, _world, _transient, sx, sy, _box, 4);
                Assert.Equal(2, applied);
                status = World.Process(root, token);
                Assert.Equal(World.BatchStatus.Applied, status);
                Assert.Equal(beforeTick + 1, World.Tick(root, _world));
                World.Process(root, _world);
                const int count = 4;
                var xs = stackalloc float[count];
                var ys = stackalloc float[count];
                var values = stackalloc short[count];
                var whole = stackalloc ulong[(count + 63) >> 6];
                for (var i = 0; i < count; i++)
                {
                    xs[i] = 10f + i * 5;
                    ys[i] = 10f + i * 5;
                }
                var read = World.SenseColumn(root, _world, _layer, xs, ys, count, values, whole);
                Assert.True(read == World.ReadStatus.Complete || read == World.ReadStatus.Partial || read == World.ReadStatus.Outside);
                var excludes = stackalloc World.SourceId[count];
                for (var i = 0; i < count; i++) excludes[i] = default;
                read = World.SenseColumn(root, _world, _layer, xs, ys, excludes, count, values, whole);
                Assert.True(read == World.ReadStatus.Complete || read == World.ReadStatus.Partial || read == World.ReadStatus.Outside);
                var gx = stackalloc float[count];
                var gy = stackalloc float[count];
                read = World.SenseGradientColumn(root, _world, _layer, xs, ys, excludes, count, gx, gy, whole);
                Assert.True(read == World.ReadStatus.Complete || read == World.ReadStatus.Partial || read == World.ReadStatus.Outside);
                var totals = stackalloc long[count];
                var reaches = stackalloc float[count];
                for (var i = 0; i < count; i++) reaches[i] = 8f;
                read = World.SenseAreaColumn(root, _world, _layer, xs, ys, reaches, excludes, count, totals, whole);
                Assert.True(read == World.ReadStatus.Complete || read == World.ReadStatus.Partial || read == World.ReadStatus.Outside);
                var bestXs = stackalloc float[count];
                var bestYs = stackalloc float[count];
                read = World.SenseMaxColumn(root, _world, _layer, xs, ys, reaches, count, values, bestXs, bestYs, whole);
                Assert.True(read == World.ReadStatus.Complete || read == World.ReadStatus.Partial || read == World.ReadStatus.Outside);
                var thresholds = stackalloc short[count];
                var nearestXs = stackalloc float[count];
                var nearestYs = stackalloc float[count];
                var found = stackalloc ulong[(count + 63) >> 6];
                read = World.SenseNearestColumn(root, _world, _layer, xs, ys, reaches, thresholds, count, values, nearestXs, nearestYs, found);
                Assert.True(read == World.ReadStatus.Complete || read == World.ReadStatus.Partial || read == World.ReadStatus.Outside);
                var words = stackalloc ulong[(count + 63) >> 6];
                var hits = World.ChangedMask(root, _world, _layer, xs, ys, 8f, 0, count, words);
                Assert.True(hits >= 0);
                Assert.Equal(beforeRootCalls, captured.Calls);
            }
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
