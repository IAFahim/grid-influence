using Xunit;

namespace Gi.Tests;

public unsafe class TransientLayerTests
{
    private static readonly Layer.Precision[] Precisions =
        [Layer.Precision.Exact, Layer.Precision.Quarter, Layer.Precision.Cell];

    [Theory]
    [InlineData(0, 71)]
    [InlineData(1, 72)]
    [InlineData(2, 73)]
    public void SplatsPublishTheFieldOfEquivalentPersistentSourcesFrameAfterFrame(int precisionIndex, int seed)
    {
        var rng = new Random(seed);
        var w = World.New();
        var stamps = new List<byte>
        {
            Stamp.Box(3, 3, 40), Stamp.Box(6, 4, -25), Stamp.Tent(6, 6, 35), Stamp.Bell(7, 7, -30),
            Stamp.Disk(4, 50, 360), Stamp.Cone(5, 60, 360), Stamp.Dome(6, 45, 360), Stamp.Disk(5, 55, 200),
        };
        ReadOnlySpan<sbyte> raster = [12, -25, 30, 45, -60, 15, 80, -10, 20, 35, -40, 50, -15, 25, -30, 60];
        stamps.Add(Stamp.New(raster, 4, 4));
        try
        {
            Grid.New(w, 6, 0f, 0f, 64f);
            Grid.New(w, 5, 12f, 8f, 32f);
            var persistent = Layer.New(w, Precisions[precisionIndex]);
            var transient = Layer.New(w, Layer.Residency.Transient, Precisions[precisionIndex]);
            var ids = new List<World.SourceId>();
            var xs = new float[400];
            var ys = new float[400];
            var splatStamps = new byte[400];
            var splatGains = new sbyte[400];
            for (var frame = 0; frame < 5; frame++)
            {
                foreach (var id in ids) World.Remove(w, id);
                ids.Clear();
                var count = frame == 3 ? 0 : 150 + rng.Next(200);
                for (var i = 0; i < count; i++)
                {
                    xs[i] = (float)(rng.NextDouble() * 80.0 - 8.0);
                    ys[i] = (float)(rng.NextDouble() * 80.0 - 8.0);
                    splatStamps[i] = stamps[rng.Next(stamps.Count)];
                    splatGains[i] = (sbyte)(rng.Next(1, 6) * (rng.Next(2) == 0 ? 1 : -1));
                    ids.Add(World.Place(w, persistent, xs[i], ys[i], splatStamps[i], splatGains[i]));
                }

                Assert.Equal(count, World.Splat(w, transient, xs.AsSpan(0, count), ys.AsSpan(0, count),
                    splatStamps.AsSpan(0, count), splatGains.AsSpan(0, count)));
                World.Process(w);
                CompareLayers(w, persistent, transient);
            }
        }
        finally
        {
            World.Free(w);
            foreach (var s in stamps) Stamp.Free(s);
        }
    }

    [Fact]
    public void AnEmptyFrameClearsTheLayerAndChangedReportsTheClearedTilesThenQuiets()
    {
        var w = World.New();
        var box = Stamp.Box(4, 4, 30);
        try
        {
            var g = Grid.New(w, 6, 0f, 0f, 64f);
            var layer = Layer.New(w, Layer.Residency.Transient, Layer.Precision.Cell);
            float[] xs = [10.5f, 40.5f];
            float[] ys = [10.5f, 12.5f];
            Assert.Equal(2, World.Splat(w, layer, xs, ys, box, 2));
            World.Process(w);
            Assert.Equal(60, World.QueryAt(w, g, layer, 10.5f, 10.5f));
            Assert.True(World.ChangedTiles(w, g, layer, null) > 0);

            Assert.Equal(2, World.Splat(w, layer, xs, ys, box, 2));
            World.Process(w);
            Assert.Equal(60, World.QueryAt(w, g, layer, 10.5f, 10.5f));
            Assert.Equal(0, World.ChangedTiles(w, g, layer, null));

            World.Process(w);
            Assert.Equal(0, World.QueryAt(w, g, layer, 10.5f, 10.5f));
            Assert.Equal(0, World.Query(w, g, layer, 0, 0, 64, 64));
            Assert.True(World.ChangedTiles(w, g, layer, null) > 0);

            World.Process(w);
            Assert.Equal(0, World.ChangedTiles(w, g, layer, null));
        }
        finally
        {
            World.Free(w);
            Stamp.Free(box);
        }
    }

    [Fact]
    public void WrongResidencyIsRejectedBothWays()
    {
        var w = World.New();
        var box = Stamp.Box(3, 3, 20);
        try
        {
            var g = Grid.New(w, 6, 0f, 0f, 64f);
            var persistent = Layer.New(w);
            var transient = Layer.New(w, Layer.Residency.Transient, Layer.Precision.Exact);
            Assert.Equal(default(World.SourceId), World.Place(w, transient, 10f, 10f, box, 1));
            Span<World.SourceResult> results = stackalloc World.SourceResult[1];
            ReadOnlySpan<World.SourceCommand> commands = [World.SourceCommand.Place(transient, 10f, 10f, box, 1)];
            Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(w, commands, results));
            Assert.Equal(World.SourceResultStatus.InvalidLayer, results[0].Status);

            float[] xs = [10f];
            float[] ys = [10f];
            Assert.Equal(0, World.Splat(w, persistent, xs, ys, box, 1));
            Assert.Equal(0, World.Splat(w, 99, xs, ys, box, 1));
            Assert.Equal(0, World.Splat(w, transient, xs, ys, 0, 1));
            World.Process(w);
            Assert.Equal(0, World.Query(w, g, persistent, 0, 0, 64, 64));
            Assert.Equal(0, World.Query(w, g, transient, 0, 0, 64, 64));
        }
        finally
        {
            World.Free(w);
            Stamp.Free(box);
        }
    }

    [Fact]
    public void WriterOverloadsAndInvalidInputsAgreeWithTheSpanOverload()
    {
        var w = World.New();
        var a = Stamp.Box(3, 3, 25);
        var b = Stamp.Tent(5, 5, 40);
        try
        {
            var g = Grid.New(w, 6, 0f, 0f, 64f);
            var viaSpan = Layer.New(w, Layer.Residency.Transient, Layer.Precision.Quarter);
            var viaWriter = Layer.New(w, Layer.Residency.Transient, Layer.Precision.Quarter);
            var persistent = Layer.New(w);
            var rng = new Random(5);
            var writer = World.BeginSplat(w, viaWriter);
            var accepted = 0;
            for (var i = 0; i < 200; i++)
            {
                var x = i % 40 == 0 ? float.NaN : (float)(rng.NextDouble() * 64.0);
                var y = (float)(rng.NextDouble() * 64.0);
                var stamp = i % 2 == 0 ? a : b;
                var gain = (sbyte)rng.Next(-4, 5);
                var one = World.Splat(w, viaSpan, new[] { x }, new[] { y }, stamp, gain);
                var added = writer.Add(x, y, stamp, gain);
                Assert.Equal(one == 1, added);
                if (added) accepted++;
            }

            Assert.Equal(accepted, writer.Count);
            Assert.False(writer.Add(1f, 1f, 0, 1));
            Assert.False(World.BeginSplat(w, persistent).Add(1f, 1f, a, 1));
            World.Process(w);
            var left = new short[64 * 64];
            var right = new short[64 * 64];
            fixed (short* pl = left)
            fixed (short* pr = right)
            {
                World.QueryRegion(w, g, viaSpan, 0, 0, 64, 64, pl);
                World.QueryRegion(w, g, viaWriter, 0, 0, 64, 64, pr);
            }

            Assert.Equal(left, right);
            Assert.Contains(left, v => v != 0);
        }
        finally
        {
            World.Free(w);
            Stamp.Free(a);
            Stamp.Free(b);
        }
    }

    [Fact]
    public void DerivedLayersReadTransientInputsAndWarmFramesAllocateNothing()
    {
        var w = World.New();
        var box = Stamp.Box(4, 4, 20);
        try
        {
            var g = Grid.New(w, 6, 0f, 0f, 64f);
            var persistent = Layer.New(w);
            var transient = Layer.New(w, Layer.Residency.Transient, Layer.Precision.Cell);
            var sum = Layer.Sum(w, persistent, 1, transient, 2, 0);
            World.Place(w, persistent, 20.5f, 20.5f, box, 1);
            float[] xs = [20.5f, 30.5f];
            float[] ys = [20.5f, 30.5f];
            World.Splat(w, transient, xs, ys, box, 1);
            World.Process(w);
            Assert.Equal(20 + 2 * 20, World.QueryAt(w, g, sum, 20.5f, 20.5f));
            Assert.Equal(2 * 20, World.QueryAt(w, g, sum, 30.5f, 30.5f));
            World.Process(w);
            Assert.Equal(20, World.QueryAt(w, g, sum, 20.5f, 20.5f));
            Assert.Equal(0, World.QueryAt(w, g, sum, 30.5f, 30.5f));

            var many = new float[2000];
            var manyY = new float[2000];
            var rng = new Random(9);
            for (var i = 0; i < many.Length; i++)
            {
                many[i] = (float)(rng.NextDouble() * 60.0 + 2.0);
                manyY[i] = (float)(rng.NextDouble() * 60.0 + 2.0);
            }

            for (var warm = 0; warm < 4; warm++)
            {
                World.Splat(w, transient, many, manyY, box, 1);
                World.Process(w);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var frame = 0; frame < 20; frame++)
            {
                World.Splat(w, transient, many, manyY, box, 1);
                World.Process(w);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        finally
        {
            World.Free(w);
            Stamp.Free(box);
        }
    }

    private static void CompareLayers(byte w, byte left, byte right)
    {
        foreach (byte grid in new byte[] { 0, 1 })
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
        }
    }
}
