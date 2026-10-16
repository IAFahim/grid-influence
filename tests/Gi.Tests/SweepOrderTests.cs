using Xunit;

namespace Gi.Tests;

public unsafe class SweepOrderTests
{
    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    public void TileOrderedSweepMatchesTheReferenceAcrossKindsGridsAndRemovals(int seed)
    {
        var rng = new Random(seed);
        var w = World.New();
        var rw = new ReferenceWorld();
        var stamps = new List<byte>
        {
            Stamp.Box(3, 3, 40),
            Stamp.Box(8, 8, -25),
            Stamp.Tent(7, 5, 35),
            Stamp.Bell(9, 9, -30),
            Stamp.Disk(4, 50, 360),
            Stamp.Dome(6, 60, 270),
        };
        try
        {
            var g0 = Grid.New(w, 6, 0f, 0f, 64f);
            Assert.Equal(g0, rw.AddGrid(6, 0f, 0f, 64f));
            var g1 = Grid.New(w, 5, 8f, 8f, 32f);
            Assert.Equal(g1, rw.AddGrid(5, 8f, 8f, 32f));
            var l0 = Layer.New(w, Layer.Precision.Exact);
            Assert.Equal(l0, rw.AddLayer(Layer.Precision.Exact));
            var l1 = Layer.New(w, Layer.Precision.Cell);
            Assert.Equal(l1, rw.AddLayer(Layer.Precision.Cell));
            var layers = new[] { l0, l1 };

            var ids = new List<World.SourceId>();
            for (var i = 0; i < 6000; i++)
            {
                var layer = layers[rng.Next(2)];
                var stamp = stamps[rng.Next(stamps.Count)];
                var gain = (sbyte)(rng.Next(1, 4) * (rng.Next(2) == 0 ? 1 : -1));
                var x = (float)(rng.NextDouble() * 80.0 - 8.0);
                var y = (float)(rng.NextDouble() * 80.0 - 8.0);
                var id = World.Place(w, layer, x, y, stamp, gain);
                Assert.Equal(rw.Place(layer, x, y, stamp, gain), id);
                ids.Add(id);
            }

            Step(w, rw, new[] { g0, g1 }, layers, new[] { 64, 32 });

            for (var round = 0; round < 3; round++)
            {
                foreach (var id in ids)
                {
                    var x = (float)(rng.NextDouble() * 80.0 - 8.0);
                    var y = (float)(rng.NextDouble() * 80.0 - 8.0);
                    World.Move(w, id, x, y);
                    rw.Move(id, x, y);
                }

                Step(w, rw, new[] { g0, g1 }, layers, new[] { 64, 32 });
            }

            for (var i = 0; i < ids.Count; i += 2)
            {
                World.Remove(w, ids[i]);
                rw.Remove(ids[i]);
            }

            Step(w, rw, new[] { g0, g1 }, layers, new[] { 64, 32 });
        }
        finally
        {
            World.Free(w);
            foreach (var s in stamps) Stamp.Free(s);
        }
    }

    private static void Step(byte w, ReferenceWorld rw, byte[] grids, byte[] layers, int[] sizes)
    {
        World.Process(w);
        rw.Process();
        for (var gi = 0; gi < grids.Length; gi++)
        foreach (var layer in layers)
        {
            var size = sizes[gi];
            var actual = new short[size * size];
            var expected = new short[size * size];
            fixed (short* pa = actual)
            fixed (short* pe = expected)
            {
                World.QueryRegion(w, grids[gi], layer, 0, 0, size, size, pa);
                rw.QueryRegion(grids[gi], layer, 0, 0, size, size, pe);
            }

            Assert.Equal(expected, actual);
            var tileCount = (size / 32) * (size / 32);
            var actualTiles = new int[tileCount];
            var expectedTiles = new int[tileCount];
            int actualCount;
            int expectedCount;
            fixed (int* ta = actualTiles)
            fixed (int* te = expectedTiles)
            {
                actualCount = World.ChangedTiles(w, grids[gi], layer, ta);
                expectedCount = rw.ChangedTiles(grids[gi], layer, te);
            }

            Assert.Equal(expectedCount, actualCount);
            Assert.Equal(expectedTiles.Take(expectedCount).Order(), actualTiles.Take(actualCount).Order());
        }
    }
}
