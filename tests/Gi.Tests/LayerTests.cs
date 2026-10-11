using Xunit;

namespace Gi.Tests;

public sealed class LayerTests
{
    [Fact]
    public void Sum_Min_Max_Mask_ReadTheirCellFormulas()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var food = Layer.New(w);
        var threat = Layer.New(w);
        var best = Layer.Sum(w, food, 1, threat, -2);
        var half = Layer.Sum(w, food, 1, 1);
        var low = Layer.Min(w, food, threat);
        var high = Layer.Max(w, food, threat);
        var safe = Layer.Mask(w, food, threat, short.MinValue, 30);
        var box = Stamp.Box(8, 8, 50);
        World.Place(w, food, 16f, 16f, box, 2);
        World.Place(w, threat, 20f, 16f, box, 1);
        World.Process(w);

        Assert.Equal(100, World.Query(w, g, food, 13, 16));
        Assert.Equal(50, World.Query(w, g, threat, 17, 16));
        Assert.Equal(100, World.Query(w, g, best, 13, 16));
        Assert.Equal(0, World.Query(w, g, best, 17, 16));
        Assert.Equal(-100, World.Query(w, g, best, 21, 16));
        Assert.Equal(50, World.Query(w, g, half, 13, 16));
        Assert.Equal(0, World.Query(w, g, low, 13, 16));
        Assert.Equal(50, World.Query(w, g, low, 17, 16));
        Assert.Equal(100, World.Query(w, g, high, 17, 16));
        Assert.Equal(50, World.Query(w, g, high, 22, 16));
        Assert.Equal(100, World.Query(w, g, safe, 13, 16));
        Assert.Equal(0, World.Query(w, g, safe, 17, 16));
        Assert.Equal(100, World.QueryMax(w, g, best, out var bx, out _));
        Assert.True(bx < 16);
    }

    [Fact]
    public void Sum_RoundsShiftedTotalsHalfAwayFromZeroAndSaturates()
    {
        var w = World.New();
        var g = Grid.New(w, 5, 0f, 0f, 32f);
        var a = Layer.New(w);
        var thirds = Layer.Sum(w, a, 3, 2);
        var huge = Layer.Sum(w, a, short.MaxValue);
        var stamp = Stamp.Box(1, 1, 1);
        var plus = World.Place(w, a, 4.5f, 4.5f, stamp, 1);
        World.Place(w, a, 8.5f, 4.5f, stamp, -1);
        World.Place(w, a, 12.5f, 4.5f, stamp, 2);
        World.Process(w);
        Assert.Equal(1, World.Query(w, g, thirds, 4, 4));
        Assert.Equal(-1, World.Query(w, g, thirds, 8, 4));
        Assert.Equal(2, World.Query(w, g, thirds, 12, 4));
        Assert.Equal(short.MaxValue, World.Query(w, g, huge, 4, 4));
        Assert.Equal(-short.MaxValue, World.Query(w, g, huge, 8, 4));
        Assert.Equal(short.MaxValue, World.Query(w, g, huge, 12, 4));
        World.Remove(w, plus);
        World.Process(w);
        Assert.Equal(0, World.Query(w, g, thirds, 4, 4));
    }

    [Fact]
    public void DerivedLayers_RejectSourcesAndInvalidRecipes()
    {
        var w = World.New();
        Grid.New(w, 6, 0f, 0f, 64f);
        var a = Layer.New(w);
        var d = Layer.Max(w, a, a);
        Assert.Equal(-1, World.Place(w, d, 10f, 10f, Stamp.Box(2, 2, 10), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Layer.Min(w, a, 31));
        Assert.Throws<ArgumentOutOfRangeException>(() => Layer.Sum(w, a, short.MaxValue + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Layer.Sum(w, a, 1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Layer.Mask(w, a, d, 5, 4));
    }

    [Fact]
    public unsafe void ChainedLayers_FollowChangesAndReportChangedTiles()
    {
        var w = World.New();
        var g = Grid.New(w, 7, 0f, 0f, 128f);
        var a = Layer.New(w);
        var b = Layer.New(w);
        var sum = Layer.Sum(w, a, 1, b, 1);
        var top = Layer.Max(w, sum, b);
        var id = World.Place(w, a, 20.5f, 20.5f, Stamp.Tent(10, 10, 60), 3);
        World.Place(w, b, 100f, 100f, Stamp.Box(6, 6, 40), 2);
        World.Process(w);
        Assert.Equal(180, World.Query(w, g, top, 20, 20));
        Assert.Equal(80, World.Query(w, g, top, 100, 100));

        World.Move(w, id, 52.5f, 20.5f);
        World.Process(w);
        Assert.Equal(0, World.Query(w, g, top, 20, 20));
        Assert.Equal(180, World.Query(w, g, top, 52, 20));
        var tiles = stackalloc int[16];
        var count = World.ChangedTiles(w, g, top, tiles);
        Assert.Equal(World.ChangedTiles(w, g, a, null), count);
        Assert.Equal(0, World.ChangedTiles(w, g, b, null));
    }

    [Fact]
    public void Exclusion_OnDerivedLayer_MatchesRemoval()
    {
        var w = World.New();
        Grid.New(w, 8, 0f, 0f, 256f);
        var herd = Layer.New(w);
        var wolves = Layer.New(w);
        var prey = Layer.Sum(w, herd, 1, wolves, -3);
        var me = World.Place(w, herd, 100f, 100f, Stamp.Bell(12, 12, 80), 4);
        World.Place(w, herd, 104f, 101f, Stamp.Tent(10, 10, 60), 3);
        World.Place(w, wolves, 98f, 103f, Stamp.Box(6, 6, 40), 2);
        World.Process(w);
        World.TrySense(w, prey, 101f, 100.5f, me, out var point);
        World.TrySenseArea(w, prey, 101f, 100.5f, 9f, me, out var area);
        World.TrySenseGradient(w, prey, 101f, 100.5f, me, out var gx, out var gy);
        World.Remove(w, me);
        World.Process(w);
        World.TrySense(w, prey, 101f, 100.5f, out var removed);
        World.TrySenseArea(w, prey, 101f, 100.5f, 9f, out var removedArea);
        World.TrySenseGradient(w, prey, 101f, 100.5f, out var rx, out var ry);
        Assert.Equal(removed, point);
        Assert.Equal(removedArea, area);
        Assert.Equal(rx, gx);
        Assert.Equal(ry, gy);
    }

    [Fact]
    public void Inspection_CountsDerivedTilesAsHeaderOnly()
    {
        var w = World.New();
        Grid.New(w, 6, 0f, 0f, 64f);
        var a = Layer.New(w);
        var d = Layer.Sum(w, a, 2);
        World.Place(w, a, 16f, 16f, Stamp.Box(4, 4, 60), 2);
        World.Process(w);
        var snapshot = Stats.Inspection.Read(w);
        Assert.Equal(2, snapshot.LiveTiles);
        Assert.Equal(1, snapshot.DerivedTiles);
        Assert.Equal(World.BlockBytes - World.HeaderBytes, snapshot.DifferenceBytes);
        Assert.Equal(2 * World.PageBytes, snapshot.PageBytes);
        Assert.Equal(World.BlockBytes + World.HeaderBytes,
            snapshot.DifferenceBytes + snapshot.DensePointerBytes + snapshot.PageBytes + snapshot.PageSumBytes);
        Assert.Equal(240, World.Query(w, 0, d, 16, 16));
    }
}
