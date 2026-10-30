using Xunit;

namespace Gi.Tests;

public sealed class SweepTrackFallbackRectTests : IDisposable
{
    private const int Side = 256;

    private readonly byte _disk = Stamp.Disk(4, 3, 120);
    private readonly byte _grid;
    private readonly byte _layer;
    private readonly byte _raster;
    private readonly byte _world = World.New();

    public SweepTrackFallbackRectTests()
    {
        _grid = Grid.New(_world, 8, 0f, 0f, Side);
        _layer = Layer.New(_world, Layer.Precision.Exact);
        var data = new sbyte[64];
        for (var i = 0; i < data.Length; i++) data[i] = (sbyte)(i % 7 - 2);
        _raster = Stamp.New(data, 8, 8);
    }

    public void Dispose()
    {
        World.Free(_world);
        Assert.True(Stamp.Free(_raster));
        Assert.True(Stamp.Free(_disk));
    }

    private static unsafe void AssertFieldMatchesRebuild(byte world, byte grid, byte layer, byte stamp,
        (float X, float Y, sbyte Gain)[] live, int side, float[]? headings = null)
    {
        var twin = World.New();
        var twinGrid = Grid.New(twin, 8, 0f, 0f, side);
        var twinLayer = Layer.New(twin, Layer.Precision.Exact);
        try
        {
            for (var index = 0; index < live.Length; index++)
            {
                var source = live[index];
                var placed = World.Place(twin, twinLayer, source.X, source.Y, stamp, source.Gain);
                Assert.NotEqual(default, placed);
                if (headings != null) World.Turn(twin, placed, headings[index]);
            }

            World.Process(twin);

            Assert.Equal(World.Query(twin, twinGrid, twinLayer, 0, 0, side, side),
                World.Query(world, grid, layer, 0, 0, side, side));
            Assert.Equal(World.LayerHash(twin, twinGrid, twinLayer), World.LayerHash(world, grid, layer));
            var mine = stackalloc short[side * side];
            var theirs = stackalloc short[side * side];
            World.QueryRegion(world, grid, layer, 0, 0, side, side, mine);
            World.QueryRegion(twin, twinGrid, twinLayer, 0, 0, side, side, theirs);
            Assert.True(new Span<short>(mine, side * side).SequenceEqual(new Span<short>(theirs, side * side)));
        }
        finally
        {
            World.Free(twin);
        }
    }

    private static float Fraction(int index)
    {
        return (index % 13) * 0.037f + (index % 5) * 0.011f;
    }

    [Fact]
    public void DenseFallbackMoversWithUniquePhasesMatchAFieldRebuild()
    {
        const int count = 420;
        var ids = new World.SourceId[count];
        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            var row = i / 20;
            xs[i] = 12f + (i % 20) * 11 + Fraction(i);
            ys[i] = 12f + row * 11 + Fraction(i + 3);
            ids[i] = World.Place(_world, _layer, xs[i], ys[i], _raster, 2);
        }

        World.Process(_world);

        for (var frame = 0; frame < 12; frame++)
        {
            for (var i = 0; i < count; i++)
            {
                xs[i] += frame % 2 == 0 ? 1f : -1f;
                World.Move(_world, ids[i], xs[i], ys[i]);
            }

            World.Process(_world);
        }

        var live = new (float, float, sbyte)[count];
        for (var i = 0; i < count; i++) live[i] = (xs[i], ys[i], 2);
        AssertFieldMatchesRebuild(_world, _grid, _layer, _raster, live, Side);
    }

    [Fact]
    public void TurnedDiskFallbackMoversMatchAFieldRebuild()
    {
        const int count = 420;
        var ids = new World.SourceId[count];
        var xs = new float[count];
        var ys = new float[count];
        var headings = new float[count];
        for (var i = 0; i < count; i++)
        {
            var row = i / 18;
            xs[i] = 16f + (i % 18) * 12 + Fraction(i + 1);
            ys[i] = 16f + row * 12 + Fraction(i + 5);
            ids[i] = World.Place(_world, _layer, xs[i], ys[i], _disk, 1);
            headings[i] = i * 0.013f;
            World.Turn(_world, ids[i], headings[i]);
        }

        World.Process(_world);

        for (var frame = 0; frame < 12; frame++)
        {
            for (var i = 0; i < count; i++)
            {
                xs[i] += frame % 2 == 0 ? 1f : -1f;
                World.Move(_world, ids[i], xs[i], ys[i]);
                headings[i] = (i * 7 + frame * 13) % 6283 / 1000f;
                World.Turn(_world, ids[i], headings[i]);
            }

            World.Process(_world);
        }

        var live = new (float, float, sbyte)[count];
        for (var i = 0; i < count; i++) live[i] = (xs[i], ys[i], 1);
        AssertFieldMatchesRebuild(_world, _grid, _layer, _disk, live, Side, headings);
    }

    [Fact]
    public void SparseFallbackChurnKeepsQueriesAndHashesExact()
    {
        const int count = 512;
        var ids = new World.SourceId[count];
        var xs = new float[count];
        var ys = new float[count];
        var gains = new sbyte[count];
        for (var i = 0; i < count; i++)
        {
            var row = i / 22;
            xs[i] = 20f + (i % 22) * 10 + Fraction(i);
            ys[i] = 20f + row * 10 + Fraction(i + 2);
            gains[i] = (sbyte)(1 + i % 4);
            ids[i] = World.Place(_world, _layer, xs[i], ys[i], _raster, gains[i]);
        }

        World.Process(_world);

        for (var frame = 0; frame < 16; frame++)
        {
            for (var i = 0; i < count; i += 4)
            {
                xs[i] += 1f;
                World.Move(_world, ids[i], xs[i], ys[i]);
            }

            var victim = frame % count;
            World.Remove(_world, ids[victim]);
            var row = victim / 20;
            xs[victim] = 24f + (victim % 20) * 10 + Fraction(victim + frame);
            ys[victim] = 24f + row * 10 + Fraction(victim);
            gains[victim] = 3;
            ids[victim] = World.Place(_world, _layer, xs[victim], ys[victim], _raster, gains[victim]);
            World.Process(_world);
        }

        var live = new (float, float, sbyte)[count];
        for (var i = 0; i < count; i++) live[i] = (xs[i], ys[i], gains[i]);
        AssertFieldMatchesRebuild(_world, _grid, _layer, _raster, live, Side);
    }
}
