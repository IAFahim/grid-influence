using Xunit;

namespace Gi.Tests;

public sealed class TimeTests
{
    [Fact]
    public void Fade_StepsTheGainOnSchedule()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var id = World.Place(w, l, 10.5f, 10.5f, Stamp.Box(1, 1, 1), 8);
        World.Process(w);
        World.Fade(w, id, 0, 4);
        var reads = new int[5];
        for (var k = 0; k < reads.Length; k++)
        {
            World.Process(w);
            reads[k] = World.Query(w, g, l, 10, 10);
        }

        Assert.Equal([6, 4, 2, 0, 0], reads);
    }

    [Fact]
    public void Expire_RemovesOnItsTickAndLeavesTheIdStale()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var id = World.Place(w, l, 20.5f, 20.5f, Stamp.Box(3, 3, 10), 2);
        World.Expire(w, id, 2);
        World.Process(w);
        Assert.Equal(20, World.Query(w, g, l, 20, 20));
        World.Process(w);
        Assert.Equal(0, World.Query(w, g, l, 20, 20));
        World.Move(w, id, 40.5f, 40.5f);
        World.Process(w);
        Assert.Equal(0, World.Query(w, g, l, 0, 0, 64, 64));
    }

    [Fact]
    public unsafe void Tick_CountsProcessAndChangedFollowsTiles()
    {
        var w = World.New();
        var g = Grid.New(w, 7, 0f, 0f, 128f);
        var l = Layer.New(w);
        Assert.Equal(0, World.Tick(w));
        var id = World.Place(w, l, 16f, 16f, Stamp.Box(4, 4, 30), 1);
        World.Process(w);
        var placed = World.Tick(w);
        Assert.Equal(1, placed);
        World.Process(w);
        Assert.False(World.Changed(w, l, 16f, 16f, 8f, placed));
        Assert.True(World.Changed(w, l, 16f, 16f, 8f, placed - 1));

        World.Move(w, id, 100f, 100f);
        World.Process(w);
        Assert.True(World.Changed(w, l, 16f, 16f, 2f, placed));
        Assert.True(World.Changed(w, l, 100f, 100f, 2f, placed));
        Assert.False(World.Changed(w, l, 100f, 16f, 2f, placed));
        var tiles = stackalloc int[16];
        Assert.Equal(2, World.ChangedTiles(w, g, l, placed, tiles));
        Assert.Equal(0, tiles[0]);
        Assert.Equal(15, tiles[1]);
        Assert.Equal(0, World.ChangedTiles(w, g, l, World.Tick(w), null));
    }

    [Fact]
    public void TrySenseNearest_FindsTheClosestCellAtThreshold()
    {
        var w = World.New();
        Grid.New(w, 7, 0f, 0f, 128f);
        var food = Layer.New(w);
        World.Place(w, food, 40.5f, 60.5f, Stamp.Box(1, 1, 50), 1);
        World.Place(w, food, 70.5f, 60.5f, Stamp.Box(1, 1, 90), 1);
        World.Process(w);
        Assert.True(World.TrySenseNearest(w, food, 60.5f, 60.5f, 40f, 40, out var v, out var x, out var y));
        Assert.Equal(90, v);
        Assert.Equal((70.5f, 60.5f), (x, y));
        Assert.True(World.TrySenseNearest(w, food, 60.5f, 60.5f, 40f, 95, out v, out x, out y));
        Assert.Equal(short.MinValue, v);
        Assert.Equal((60.5f, 60.5f), (x, y));
        Assert.True(World.TrySenseNearest(w, food, 50.5f, 60.5f, 40f, 40, out v, out x, out _));
        Assert.Equal(50, v);
        Assert.Equal(40.5f, x);
    }

    [Fact]
    public void Rewind_RestoresSchedulesAndResumesThem()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var id = World.Place(w, l, 5.5f, 5.5f, Stamp.Box(1, 1, 1), 10);
        World.Fade(w, id, 0, 10);
        World.Process(w);
        Assert.Equal(9, World.Query(w, g, l, 5, 5));
        World.Record(w);
        World.Fade(w, id, 16, 2);
        World.Process(w);
        World.Process(w);
        Assert.Equal(16, World.Query(w, g, l, 5, 5));
        World.Rewind(w);
        World.Process(w);
        Assert.Equal(8, World.Query(w, g, l, 5, 5));
        World.Process(w);
        Assert.Equal(7, World.Query(w, g, l, 5, 5));
    }
}
