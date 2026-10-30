using Xunit;

namespace Gi.Tests;

public sealed class SweepTrackPairEmitTests : IDisposable
{
    private const int Side = 128;

    private readonly byte _grid;
    private readonly byte _layer;
    private readonly byte _stamp = Stamp.Box(4, 4, 12);
    private readonly byte _world = World.New();

    public SweepTrackPairEmitTests()
    {
        _grid = Grid.New(_world, 7, 0f, 0f, Side);
        _layer = Layer.New(_world, Layer.Precision.Cell);
    }

    public void Dispose()
    {
        World.Free(_world);
        Assert.True(Stamp.Free(_stamp));
    }

    private static unsafe void AssertFieldMatchesRebuild(byte world, byte grid, byte layer, byte stamp,
        (float X, float Y, sbyte Gain)[] live)
    {
        var twin = World.New();
        var twinGrid = Grid.New(twin, 7, 0f, 0f, Side);
        var twinLayer = Layer.New(twin, Layer.Precision.Cell);
        try
        {
            foreach (var source in live)
                Assert.NotEqual(default, World.Place(twin, twinLayer, source.X, source.Y, stamp, source.Gain));
            World.Process(twin);

            Assert.Equal(World.Query(twin, twinGrid, twinLayer, 0, 0, Side, Side),
                World.Query(world, grid, layer, 0, 0, Side, Side));
            Assert.Equal(World.LayerHash(twin, twinGrid, twinLayer), World.LayerHash(world, grid, layer));
            var mine = stackalloc short[Side * Side];
            var theirs = stackalloc short[Side * Side];
            World.QueryRegion(world, grid, layer, 0, 0, Side, Side, mine);
            World.QueryRegion(twin, twinGrid, twinLayer, 0, 0, Side, Side, theirs);
            Assert.True(new Span<short>(mine, Side * Side).SequenceEqual(new Span<short>(theirs, Side * Side)));
        }
        finally
        {
            World.Free(twin);
        }
    }

    [Fact]
    public unsafe void CellCrossingBoxMovesMatchAFieldRebuiltFromFinalSources()
    {
        const int count = 96;
        var ids = new World.SourceId[count];
        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = 6f + (i % 12) * 8 + (i / 12 % 2) * 0.5f;
            ys[i] = 6f + (i / 12) * 8;
            ids[i] = World.Place(_world, _layer, xs[i], ys[i], _stamp, 1);
        }

        World.Process(_world);

        for (var frame = 0; frame < 24; frame++)
        {
            for (var i = 0; i < count; i++)
            {
                xs[i] += 0.5f;
                ys[i] += 0.25f;
                World.Move(_world, ids[i], xs[i], ys[i]);
            }

            World.Process(_world);
        }

        var live = new (float, float, sbyte)[count];
        for (var i = 0; i < count; i++) live[i] = (xs[i], ys[i], (sbyte)1);
        AssertFieldMatchesRebuild(_world, _grid, _layer, _stamp, live);
    }

    [Fact]
    public unsafe void MixedMovesGainFadesScalesAndRemovalsMatchAFieldRebuild()
    {
        const int count = 120;
        var ids = new World.SourceId[count];
        var xs = new float[count];
        var ys = new float[count];
        var gains = new sbyte[count];
        var removed = new bool[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = 8f + (i % 15) * 7;
            ys[i] = 8f + (i / 15) * 7;
            gains[i] = (sbyte)(1 + i % 5);
            ids[i] = World.Place(_world, _layer, xs[i], ys[i], _stamp, gains[i]);
        }

        World.Process(_world);

        for (var frame = 0; frame < 20; frame++)
        {
            for (var i = 0; i < count; i++)
            {
                if (removed[i]) continue;
                xs[i] += 0.5f;
                ys[i] += (i % 3 == 0 ? 0.5f : 0.25f);
                World.Move(_world, ids[i], xs[i], ys[i]);
                if (i % 7 == frame % 7 && frame % 2 == 0)
                {
                    gains[i] = (sbyte)(gains[i] == 5 ? 1 : gains[i] + 1);
                    World.SetGain(_world, ids[i], gains[i]);
                }

                if (i % 11 == frame % 11 && frame % 3 == 0)
                {
                    removed[i] = true;
                    World.Remove(_world, ids[i]);
                }
            }

            World.Process(_world);
        }

        var live = new List<(float, float, sbyte)>();
        for (var i = 0; i < count; i++)
            if (!removed[i])
                live.Add((xs[i], ys[i], gains[i]));
        AssertFieldMatchesRebuild(_world, _grid, _layer, _stamp, live.ToArray());
    }

    [Fact]
    public unsafe void ZeroGainMoversAndCrossTileMoversLeaveTheFieldUnchangedOrExact()
    {
        var still = World.Place(_world, _layer, 32f, 32f, _stamp, 3);
        var walker = World.Place(_world, _layer, 60f, 60f, _stamp, 3);
        var muted = World.Place(_world, _layer, 96f, 96f, _stamp, 0);
        World.Process(_world);
        var before = World.Query(_world, _grid, _layer, 0, 0, Side, Side);
        var beforeHash = World.LayerHash(_world, _grid, _layer);

        World.Move(_world, muted, 97.5f, 97.75f);
        World.Process(_world);
        Assert.Equal(before, World.Query(_world, _grid, _layer, 0, 0, Side, Side));

        World.Move(_world, still, 32.5f, 32.25f);
        for (var step = 0; step < 8; step++)
        {
            World.Move(_world, walker, 60.5f + step * 0.5f, 60f);
            World.Process(_world);
        }

        var live = new (float, float, sbyte)[]
        {
            (32.5f, 32.25f, (sbyte)3), (60.5f + 7 * 0.5f, 60f, (sbyte)3), (97.5f, 97.75f, (sbyte)0)
        };
        AssertFieldMatchesRebuild(_world, _grid, _layer, _stamp, live);
        Assert.NotEqual(beforeHash, World.LayerHash(_world, _grid, _layer));
    }
}
