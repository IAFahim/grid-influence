using Xunit;

namespace Gi.Tests;

public sealed unsafe class ChangedBytesTests
{
    private static int[] Drain(byte w, byte g, byte l)
    {
        var count = World.ChangedTiles(w, g, l, null);
        var tiles = new int[count];
        fixed (int* p = tiles)
        {
            Assert.Equal(count, World.ChangedTiles(w, g, l, p));
        }

        Array.Sort(tiles);
        return tiles;
    }

    private static int[] Since(byte w, byte g, byte l, int since)
    {
        var count = World.ChangedTiles(w, g, l, since, null);
        var tiles = new int[count];
        fixed (int* p = tiles)
        {
            Assert.Equal(count, World.ChangedTiles(w, g, l, since, p));
        }

        return tiles;
    }

    private static int[] TileDiff(short[] before, short[] after, int cells)
    {
        var perSide = cells >> 5;
        var tiles = new List<int>();
        for (var ty = 0; ty < perSide; ty++)
        for (var tx = 0; tx < perSide; tx++)
        {
            var differs = false;
            for (var y = 0; y < 32 && !differs; y++)
            {
                var row = (ty * 32 + y) * cells + tx * 32;
                for (var x = 0; x < 32; x++)
                    if (before[row + x] != after[row + x])
                    {
                        differs = true;
                        break;
                    }
            }

            if (differs) tiles.Add(ty * perSide + tx);
        }

        return tiles.ToArray();
    }

    [Fact]
    public void GainAndLossInTheSameFrame_LeavesTilesUnchanged()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var box = Stamp.Box(6, 6, 50);
        var s = World.Place(w, l, 20f, 20f, box, 5);
        World.Process(w);
        Assert.NotEmpty(Drain(w, g, l));

        var since = World.Tick(w);
        World.Move(w, s, 40f, 40f);
        World.Move(w, s, 20f, 20f);
        World.Process(w);
        Assert.Empty(Drain(w, g, l));
        Assert.Empty(Since(w, g, l, since));
        Assert.False(World.Changed(w, l, 20f, 20f, 8f, since));
        Assert.Equal(250, World.Query(w, g, l, 20, 20));
        World.Free(w);
    }

    [Fact]
    public void RemovedAndReplacedSourceInTheSameFrame_LeavesTilesUnchanged()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var box = Stamp.Box(6, 6, 50);
        var s = World.Place(w, l, 20f, 20f, box, 5);
        World.Process(w);

        var since = World.Tick(w);
        World.Remove(w, s);
        var replacement = World.Place(w, l, 20f, 20f, box, 5);
        World.Process(w);
        Assert.True(replacement != default);
        Assert.Empty(Drain(w, g, l));
        Assert.Empty(Since(w, g, l, since));
        Assert.Equal(250, World.Query(w, g, l, 20, 20));
        World.Free(w);
    }

    [Fact]
    public void CancelledDepositOnALiveTile_LeavesItUnchanged()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var box = Stamp.Box(6, 6, 50);
        World.Place(w, l, 12f, 12f, box, 3);
        World.Process(w);
        var quiet = World.Query(w, g, l, 0, 0, 64, 64);

        var a = World.Place(w, l, 30f, 30f, box, 4);
        World.Process(w);
        var loaded = World.Query(w, g, l, 0, 0, 64, 64);
        Assert.NotEqual(quiet, loaded);

        var since = World.Tick(w);
        World.Remove(w, a);
        World.Place(w, l, 30f, 30f, box, 4);
        World.Process(w);
        Assert.Empty(Drain(w, g, l));
        Assert.Empty(Since(w, g, l, since));
        Assert.Equal(loaded, World.Query(w, g, l, 0, 0, 64, 64));
        World.Free(w);
    }

    [Fact]
    public void EmptiedTileWithAPublishedPage_StaysChanged()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var s = World.Place(w, l, 20f, 20f, Stamp.Box(6, 6, 50), 5);
        World.Process(w);
        Assert.NotEmpty(Drain(w, g, l));

        var since = World.Tick(w);
        World.Remove(w, s);
        World.Process(w);
        var emptied = Drain(w, g, l);
        Assert.NotEmpty(emptied);
        Assert.Equal(emptied, Since(w, g, l, since));
        Assert.Equal(0, World.Query(w, g, l, 0, 0, 64, 64));
        World.Free(w);
    }

    [Fact]
    public void MoveOutAndBack_AcrossFrames_IsChangedBothTimes()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var s = World.Place(w, l, 20f, 20f, Stamp.Box(6, 6, 50), 5);
        World.Process(w);

        var before = World.Tick(w);
        World.Move(w, s, 44f, 44f);
        World.Process(w);
        var first = Drain(w, g, l);
        Assert.NotEmpty(first);

        World.Move(w, s, 20f, 20f);
        World.Process(w);
        var second = Drain(w, g, l);
        Assert.Equal(first, second);
        Assert.Equal(first, Since(w, g, l, before));
        Assert.Equal(250, World.Query(w, g, l, 20, 20));
        World.Free(w);
    }

    [Fact]
    public void CrowdAndDeltaPaths_ReportTheSameChangedTiles()
    {
        {
            const int movers = 70;
            const int statics = 90;
            var box = Stamp.Box(6, 6, 60);
            var crowdWorld = World.New();
            var crowdGrid = Grid.New(crowdWorld, 8, 0f, 0f, 256f);
            var crowdLayer = Layer.New(crowdWorld);
            var deltaWorld = World.New();
            var deltaGrid = Grid.New(deltaWorld, 8, 0f, 0f, 256f);
            var deltaLayer = Layer.New(deltaWorld);
            var xs = new float[movers];
            var ys = new float[movers];
            var crowdIds = new World.SourceId[movers];
            var deltaIds = new World.SourceId[movers];
            for (var i = 0; i < movers; i++)
            {
                xs[i] = 16f + i * 3.71f % 88f;
                ys[i] = 16f + i * 5.37f % 88f;
                crowdIds[i] = World.Place(crowdWorld, crowdLayer, xs[i], ys[i], box, 7);
                deltaIds[i] = World.Place(deltaWorld, deltaLayer, xs[i], ys[i], box, 7);
            }

            for (var i = 0; i < statics; i++)
                World.Place(deltaWorld, deltaLayer, 170f + i * 0.9f, 170f + i * 0.7f, box, 7);

            World.Process(crowdWorld);
            World.Process(deltaWorld);
            var left = new short[128 * 128];
            var right = new short[128 * 128];
            for (var frame = 0; frame < 12; frame++)
            {
                for (var i = 0; i < movers; i++)
                {
                    xs[i] = 16f + (xs[i] * 1.00031f + i * 0.37f + frame * 1.13f) % 88f;
                    ys[i] = 16f + (ys[i] * 1.00017f + i * 0.11f + frame * 2.07f) % 88f;
                    World.Move(crowdWorld, crowdIds[i], xs[i], ys[i]);
                    World.Move(deltaWorld, deltaIds[i], xs[i], ys[i]);
                }

                var since = World.Tick(crowdWorld);
                World.Process(crowdWorld);
                World.Process(deltaWorld);
                Assert.Equal(Drain(crowdWorld, crowdGrid, crowdLayer), Drain(deltaWorld, deltaGrid, deltaLayer));
                Assert.Equal(Since(crowdWorld, crowdGrid, crowdLayer, since),
                    Since(deltaWorld, deltaGrid, deltaLayer, since));
                fixed (short* l = left, r = right)
                {
                    World.QueryRegion(crowdWorld, crowdGrid, crowdLayer, 0, 0, 128, 128, l);
                    World.QueryRegion(deltaWorld, deltaGrid, deltaLayer, 0, 0, 128, 128, r);
                }

                Assert.Equal(left, right);
            }

            World.Free(crowdWorld);
            World.Free(deltaWorld);
        }
    }

    [Fact]
    public void ChangedTiles_AlwaysMatchPublishedByteChanges()
    {
        const int cells = 128;
        var w = World.New();
        var g = Grid.New(w, 7, 0f, 0f, 128f);
        var l = Layer.New(w);
        var stamps = new[] { Stamp.Box(8, 8, 60), Stamp.Tent(7, 7, 50) };
        var rng = new Random(93);
        const int count = 120;
        var ids = new World.SourceId[count];
        var live = new bool[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = World.Place(w, l, 8f + (float)(rng.NextDouble() * 100), 8f + (float)(rng.NextDouble() * 100),
                stamps[i % stamps.Length], (sbyte)rng.Next(1, 12));
            live[i] = true;
        }

        var before = new short[cells * cells];
        var after = new short[cells * cells];
        for (var frame = 0; frame < 24; frame++)
        {
            fixed (short* p = before)
            {
                World.QueryRegion(w, g, l, 0, 0, cells, cells, p);
            }

            var crowdFrame = frame % 3 == 0;
            for (var i = 0; i < count; i++)
            {
                var x = 8f + (float)(rng.NextDouble() * 100);
                var y = 8f + (float)(rng.NextDouble() * 100);
                if (!live[i])
                {
                    ids[i] = World.Place(w, l, x, y, stamps[i % stamps.Length], (sbyte)rng.Next(1, 12));
                    live[i] = true;
                    continue;
                }

                if (!crowdFrame && i % 4 != 0) continue;
                if (rng.Next(12) == 0)
                {
                    World.Remove(w, ids[i]);
                    live[i] = false;
                }
                else
                {
                    World.Move(w, ids[i], x, y);
                }
            }

            World.Process(w);
            fixed (short* p = after)
            {
                World.QueryRegion(w, g, l, 0, 0, cells, cells, p);
            }

            var expected = TileDiff(before, after, cells);
            var reported = Drain(w, g, l);
            Assert.Equal(expected, reported);
            if (expected.Length != 0)
            {
                var probe = expected[0];
                var px = probe % 4 * 32 + 16;
                var py = probe / 4 * 32 + 16;
                Assert.True(World.Changed(w, l, px, py, 40f, World.Tick(w) - 1));
            }

            Assert.Equal(reported, Since(w, g, l, World.Tick(w) - 1));
        }

        World.Free(w);
    }

    [Fact]
    public void DerivedLayers_ReportOnlyPublishedByteChanges()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var a = Layer.New(w);
        var b = Layer.New(w);
        var box = Stamp.Box(6, 6, 50);
        var sa = World.Place(w, a, 20f, 20f, box, 5);
        World.Place(w, b, 20f, 20f, box, 5);
        World.Process(w);
        var sum = Layer.Sum(w, a, 1, b, -1);
        World.Process(w);
        Assert.Equal(0, World.Query(w, g, sum, 0, 0, 64, 64));
        Assert.Empty(Drain(w, g, sum));

        World.SetGain(w, sa, 6);
        World.Process(w);
        Assert.Equal(Drain(w, g, a), Drain(w, g, sum));
        Assert.Equal(1 * 50, World.Query(w, g, sum, 20, 20));

        var since = World.Tick(w);
        World.SetGain(w, sa, 5);
        World.Process(w);
        Assert.Equal(Drain(w, g, a), Drain(w, g, sum));
        Assert.Equal(Drain(w, g, a), Since(w, g, sum, since));
        Assert.Equal(0, World.Query(w, g, sum, 0, 0, 64, 64));
        Assert.Empty(Drain(w, g, b));
        World.Free(w);
    }
}
