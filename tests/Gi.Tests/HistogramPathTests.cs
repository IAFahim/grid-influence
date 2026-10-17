using Xunit;

namespace Gi.Tests;

public unsafe class HistogramPathTests
{
    [Theory]
    [InlineData(81)]
    [InlineData(82)]
    [InlineData(83)]
    [InlineData(84)]
    public void CellSplatsEqualPersistentSourcesForEveryBoxShapeGridScaleAndOffGridPosition(int seed)
    {
        var rng = new Random(seed);
        var w = World.New();
        var stamps = new List<byte>();
        for (var width = 1; width <= 9; width += 2)
        for (var height = 2; height <= 9; height += 3)
            stamps.Add(Stamp.Box((ushort)width, (ushort)height, (sbyte)(rng.Next(1, 40) * (rng.Next(2) == 0 ? 1 : -1))));
        stamps.Add(Stamp.Box(4, 4, 7));
        stamps.Add(Stamp.Box(6, 6, -9));
        stamps.Add(Stamp.Tent(5, 5, 30));
        stamps.Add(Stamp.Disk(4, 40));
        try
        {
            Grid.New(w, 6, 0f, 0f, 64f);
            Grid.New(w, 5, 8f, 8f, 16f);
            Grid.New(w, 5, 4f, 4f, 64f);
            var persistent = Layer.New(w, Layer.Precision.Cell);
            var transient = Layer.New(w, Layer.Residency.Transient, Layer.Precision.Cell);
            var ids = new List<World.SourceId>();
            for (var frame = 0; frame < 4; frame++)
            {
                foreach (var id in ids) World.Remove(w, id);
                ids.Clear();
                var count = frame == 2 ? 3 : 900;
                var xs = new float[count];
                var ys = new float[count];
                var stampRows = new byte[count];
                var gains = new sbyte[count];
                for (var i = 0; i < count; i++)
                {
                    var cluster = rng.Next(4) == 0;
                    xs[i] = cluster ? 20f + rng.Next(3) : (float)(rng.NextDouble() * 90.0 - 14.0);
                    ys[i] = cluster ? 31f + rng.Next(3) : (float)(rng.NextDouble() * 90.0 - 14.0);
                    stampRows[i] = stamps[rng.Next(stamps.Count)];
                    gains[i] = (sbyte)(rng.Next(1, 17) * (rng.Next(3) == 0 ? -1 : 1));
                    ids.Add(World.Place(w, persistent, xs[i], ys[i], stampRows[i], gains[i]));
                }

                Assert.Equal(count, World.Splat(w, transient, xs, ys, stampRows, gains));
                World.Process(w);
                for (byte grid = 0; grid < 3; grid++) Compare(w, grid, persistent, transient, expectContent: grid == 0 && count > 100);
            }
        }
        finally
        {
            World.Free(w);
            foreach (var s in stamps) Stamp.Free(s);
        }
    }

    private static void Compare(byte w, byte grid, byte left, byte right, bool expectContent)
    {
        Grid.TryGetSpec(w, grid, out var power, out _, out _, out _);
        var size = 1 << power;
        var a = new short[size * size];
        var b = new short[size * size];
        fixed (short* pa = a)
        fixed (short* pb = b)
        {
            World.QueryRegion(w, grid, left, 0, 0, size, size, pa);
            World.QueryRegion(w, grid, right, 0, 0, size, size, pb);
        }

        Assert.Equal(a, b);
        if (expectContent) Assert.Contains(a, v => v != 0);
    }
}
