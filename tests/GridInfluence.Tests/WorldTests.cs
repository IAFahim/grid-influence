using Xunit;

namespace GridInfluence.Tests;

public sealed class WorldTests
{
    private static (float x, float y)[] Scatter(int n, float worldSize, Random rng)
    {
        var p = new (float, float)[n];
        for (var i = 0; i < n; i++)
            p[i] = ((float)(rng.NextDouble() * (worldSize - 36) + 18),
                    (float)(rng.NextDouble() * (worldSize - 36) + 18));
        return p;
    }

    [Fact]
    public void Query_MatchesWorldSpaceOracle()
    {
        var w = World.New();
        var g = Grid.New(w, power: 8, x: 0f, y: 0f, size: 256f);
        var l = Layer.New(w);
        var s = Stamp.Box(100);
        var rng = new Random(42);
        var pos = Scatter(200, 256f, rng);
        foreach (var (x, y) in pos) World.Place(w, l, x, y, 8f, s);
        World.Process(w);
        for (var i = 0; i < 30; i++)
        {
            var px = (int)pos[i].x; var py = (int)pos[i].y;
            var expected = 0L;
            for (var j = 0; j < pos.Length; j++)
            {
                var mx = (int)pos[j].x; var my = (int)pos[j].y;
                if (Math.Abs(px - mx) <= 8 && Math.Abs(py - my) <= 8) expected += 100;
            }
            Assert.Equal((short)Math.Clamp(expected, short.MinValue, short.MaxValue),
                World.Query(w, g, l, px, py));
        }
    }

    [Fact]
    public void EdgeSources_BleedIntoNeighbourGrid()
    {
        var w = World.New();
        var g0 = Grid.New(w, power: 8, x: 0f, y: 0f, size: 256f);
        var g1 = Grid.New(w, power: 8, x: 256f, y: 0f, size: 256f);
        var l = Layer.New(w);
        var s = Stamp.Box(100);
        World.Place(w, l, 252f, 100f, 8f, s);
        World.Process(w);
        Assert.Equal(100, World.Query(w, g0, l, 252, 100));
        Assert.Equal(100, World.Query(w, g1, l, 0, 100));
        Assert.Equal(100, World.Query(w, g1, l, 4, 100));
        Assert.Equal(0, World.Query(w, g1, l, 20, 100));
    }

    [Fact]
    public void DifferentResolutions_ScaleBoundsIndependently()
    {
        var w = World.New();
        var g0 = Grid.New(w, power: 8, x: 0f, y: 0f, size: 256f);
        var g1 = Grid.New(w, power: 6, x: 256f, y: 0f, size: 256f);
        var l = Layer.New(w);
        var s = Stamp.Box(100);
        World.Place(w, l, 128f, 128f, 8f, s);
        World.Place(w, l, 300f, 128f, 8f, s);
        World.Process(w);
        Assert.Equal(100, World.Query(w, g0, l, 128, 128));
        Assert.Equal(0, World.Query(w, g0, l, 128 + 20, 128));
        Assert.Equal(100, World.Query(w, g1, l, 11, 32));
        Assert.Equal(0, World.Query(w, g1, l, 11 + 8, 32));
    }

    [Fact]
    public void NegativeWorldPositions_RouteAndConvert()
    {
        var w = World.New();
        var g = Grid.New(w, power: 8, x: -512f, y: -512f, size: 512f);
        var l = Layer.New(w);
        var s = Stamp.Box(100);
        World.Place(w, l, -256f, -256f, 8f, s);
        World.Process(w);
        Assert.Equal(100, World.Query(w, g, l, 128, 128));
    }

    [Fact]
    public void FadeStamp_DecaysContributionEachProcess()
    {
        var w = World.New();
        var g = Grid.New(w, power: 8, x: 0f, y: 0f, size: 256f);
        var l = Layer.New(w);
        var s = Stamp.Box(100);
        var f = Fade.Stamp(50);
        World.Place(w, l, 128f, 128f, 8f, s, f);
        World.Process(w);
        var first = World.Query(w, g, l, 128, 128);
        World.Process(w);
        var second = World.Query(w, g, l, 128, 128);
        Assert.Equal(100, first);
        Assert.True(second < first);
    }

    [Fact]
    public void Move_RepositionsInfluence()
    {
        var w = World.New();
        var g = Grid.New(w, power: 8, x: 0f, y: 0f, size: 256f);
        var l = Layer.New(w);
        var s = Stamp.Box(100);
        var src = World.Place(w, l, 50f, 50f, 8f, s);
        World.Process(w);
        Assert.Equal(100, World.Query(w, g, l, 50, 50));
        World.Move(w, src, 200f, 200f);
        World.Process(w);
        Assert.Equal(0, World.Query(w, g, l, 50, 50));
        Assert.Equal(100, World.Query(w, g, l, 200, 200));
    }

    [Fact]
    public void Remove_RetractsInfluence()
    {
        var w = World.New();
        var g = Grid.New(w, power: 8, x: 0f, y: 0f, size: 256f);
        var l = Layer.New(w);
        var s = Stamp.Box(100);
        var src = World.Place(w, l, 50f, 50f, 8f, s);
        World.Process(w);
        Assert.Equal(100, World.Query(w, g, l, 50, 50));
        World.Remove(w, src);
        World.Process(w);
        Assert.Equal(0, World.Query(w, g, l, 50, 50));
    }

    [Fact]
    public void QueryAt_MapsWorldToCell()
    {
        var w = World.New();
        var g = Grid.New(w, power: 7, x: 0f, y: 0f, size: 256f);
        var l = Layer.New(w);
        var s = Stamp.Circle(40);
        World.Place(w, l, 128f, 128f, 8f, s);
        World.Process(w);
        Assert.Equal(40, World.QueryAt(w, g, l, 128f, 128f));
        Assert.Equal(0, World.QueryAt(w, g, l, -10f, 128f));
        Assert.Equal(0, World.QueryAt(w, g, l, 999f, 128f));
    }

    [Fact]
    public void RegionQuery_SumsCells()
    {
        var w = World.New();
        var g = Grid.New(w, power: 7, x: 0f, y: 0f, size: 128f);
        var l = Layer.New(w);
        var s = Stamp.Box(25);
        World.Place(w, l, 40f, 40f, 8f, s);
        World.Process(w);
        var cell = (int)(40f * 128 / 128);
        var total = 0L;
        for (var cy = cell - 9; cy <= cell + 9; cy++)
        for (var cx = cell - 9; cx <= cell + 9; cx++)
            total += World.Query(w, g, l, cx, cy);
        Assert.Equal(total, World.Query(w, g, l, cell - 9, cell - 9, 19, 19));
    }
}

public sealed class WorldParallelTests
{
    [Fact]
    public void SlicedProcess_MatchesSerialProcess()
    {
        const int n = 3000;
        var w1 = World.New();
        var g1 = Grid.New(w1, 8, 0f, 0f, 256f);
        for (var i = 1; i < 8; i++) Grid.New(w1, 8, i * 256f, 0f, 256f);
        var l1 = Layer.New(w1);
        var w2 = World.New();
        var g2 = Grid.New(w2, 8, 0f, 0f, 256f);
        for (var i = 1; i < 8; i++) Grid.New(w2, 8, i * 256f, 0f, 256f);
        var l2 = Layer.New(w2);
        var stamp = Stamp.Box(100);
        uint rng = 13;
        var pos = new (float, float)[n];
        for (var i = 0; i < n; i++)
        {
            rng = rng * 1664525u + 1013904223u;
            pos[i] = (rng % 2000u + 8f, (rng >> 8) % 220u + 8f);
            World.Place(w1, l1, pos[i].Item1, pos[i].Item2, 8f, stamp);
            World.Place(w2, l2, pos[i].Item1, pos[i].Item2, 8f, stamp);
        }

        World.Process(w1);
        World.BeginProcess(w2);
        var slice = n / 8;
        Parallel.For(0, 8, s => World.ProcessSlice(w2, s * slice, slice));

        for (var i = 0; i < 40; i++)
        {
            var gi = (int)(pos[i].Item1 / 256f);
            if (gi >= 8) continue;
            var cx = (int)(pos[i].Item1 - gi * 256f); var cy = (int)pos[i].Item2;
            Assert.Equal(World.Query(w1, (byte)gi, l1, cx, cy), World.Query(w2, (byte)gi, l2, cx, cy));
        }
    }
}
