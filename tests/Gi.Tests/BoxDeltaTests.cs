using Xunit;

namespace Gi.Tests;

public unsafe class BoxDeltaTests
{
    private static readonly (int Width, int Height, sbyte Value)[] Boxes =
    [
        (1, 1, 50), (3, 3, -40), (6, 4, 40), (5, 40, 33), (40, 5, -27),
        (70, 70, 9), (33, 31, -11), (2, 64, 100), (64, 2, -100), (31, 33, 7)
    ];

    public static TheoryData<int> PrecisionKinds => [0, 1, 2];

    private static Layer.Precision PrecisionOf(int kind)
    {
        return kind switch
        {
            0 => Layer.Precision.Exact,
            1 => Layer.Precision.Quarter,
            _ => Layer.Precision.Cell
        };
    }

    [Theory]
    [MemberData(nameof(PrecisionKinds))]
    public void BandedBoxDeltasMatchTheReferenceAcrossMovesScalesAndRemovals(int kind)
    {
        var precision = PrecisionOf(kind);
        var rng = new Random(11 + kind);
        var w = World.New();
        var rw = new ReferenceWorld();
        var stamps = new List<byte>();
        try
        {
            foreach (var (width, height, value) in Boxes) stamps.Add(Stamp.Box((ushort)width, (ushort)height, value));

            var g0 = Grid.New(w, 6, 0f, 0f, 64f);
            Assert.Equal(g0, rw.AddGrid(6, 0f, 0f, 64f));
            var g1 = Grid.New(w, 5, 16f, 16f, 32f);
            Assert.Equal(g1, rw.AddGrid(5, 16f, 16f, 32f));
            var layer = Layer.New(w, precision);
            Assert.Equal(layer, rw.AddLayer(precision));

            var ids = new List<World.SourceId>();
            for (var i = 0; i < 32; i++)
            {
                var stamp = stamps[rng.Next(stamps.Count)];
                var gain = (sbyte)(rng.Next(1, 9) * (rng.Next(2) == 0 ? 1 : -1));
                var x = (float)(rng.NextDouble() * 80.0 - 8.0);
                var y = (float)(rng.NextDouble() * 80.0 - 8.0);
                var id = World.Place(w, layer, x, y, stamp, gain);
                Assert.Equal(rw.Place(layer, x, y, stamp, gain), id);
                ids.Add(id);
            }

            Step(w, rw, g0, g1, layer, 64, 32);

            for (var round = 0; round < 6; round++)
            {
                foreach (var id in ids)
                {
                    var roll = rng.Next(10);
                    if (roll < 6)
                    {
                        var x = (float)(rng.NextDouble() * 80.0 - 8.0);
                        var y = (float)(rng.NextDouble() * 80.0 - 8.0);
                        World.Move(w, id, x, y);
                        rw.Move(id, x, y);
                    }
                    else if (roll < 8)
                    {
                        var scale = (float)(rng.NextDouble() * 2.5 + 0.25);
                        World.Scale(w, id, scale);
                        rw.Scale(id, scale);
                    }
                    else if (roll < 9)
                    {
                        var gain = (sbyte)(rng.Next(1, 9) * (rng.Next(2) == 0 ? 1 : -1));
                        World.SetGain(w, id, gain);
                        rw.SetGain(id, gain);
                    }
                }

                Step(w, rw, g0, g1, layer, 64, 32);
            }

            foreach (var id in ids.Take(ids.Count / 2))
            {
                World.Remove(w, id);
                rw.Remove(id);
            }

            Step(w, rw, g0, g1, layer, 64, 32);

            foreach (var id in ids.Skip(ids.Count / 2))
            {
                World.Remove(w, id);
                rw.Remove(id);
            }

            Step(w, rw, g0, g1, layer, 64, 32);
            Assert.Equal(0, World.Query(w, g0, layer, 0, 0, 64, 64));
            Assert.Equal(0, World.Query(w, g1, layer, 0, 0, 32, 32));
        }
        finally
        {
            World.Free(w);
            foreach (var s in stamps) Stamp.Free(s);
        }
    }

    private static void Step(byte w, ReferenceWorld rw, byte g0, byte g1, byte layer, int size0, int size1)
    {
        World.Process(w);
        rw.Process();
        VerifyGrid(w, rw, g0, layer, size0);
        VerifyGrid(w, rw, g1, layer, size1);
    }

    private static void VerifyGrid(byte w, ReferenceWorld rw, byte grid, byte layer, int size)
    {
        var actual = new short[size * size];
        var expected = new short[size * size];
        fixed (short* pa = actual)
        fixed (short* pe = expected)
        {
            World.QueryRegion(w, grid, layer, 0, 0, size, size, pa);
            rw.QueryRegion(grid, layer, 0, 0, size, size, pe);
        }

        Assert.Equal(expected, actual);
    }
}
