using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Gi.Tests;

public sealed unsafe class AuditSortCliffTests
{
    private readonly ITestOutputHelper _output;

    public AuditSortCliffTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string Build =>
#if GI_NETSTANDARD
        "netstandard2.1";
#else
        "net10.0";
#endif

    private static void Shuffle(int[] values, int seed)
    {
        var rng = new Random(seed);
        for (var i = values.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    private (int Changed, double Milliseconds) MeasureProcess(int power, bool shuffled)
    {
        var world = World.New();
        var stamp = Stamp.Box(4, 4, 50);
        try
        {
            var size = 1 << power;
            Grid.New(world, power, 0f, 0f, size);
            var layer = Layer.New(world, Layer.Precision.Cell);
            var side = size >> 5;
            var tiles = side * side;
            var order = new int[tiles];
            for (var i = 0; i < tiles; i++) order[i] = i;
            if (shuffled) Shuffle(order, 17);

            var ids = new World.SourceId[tiles];
            var xs = new float[tiles];
            var ys = new float[tiles];
            for (var i = 0; i < tiles; i++)
            {
                var tile = order[i];
                var column = tile % side;
                var row = tile / side;
                xs[i] = column * 32 + 16.5f;
                ys[i] = row * 32 + 16.5f;
                ids[i] = World.Place(world, layer, xs[i], ys[i], stamp, 3);
            }

            World.Process(world);
            for (var i = 0; i < tiles; i++) xs[i] += 1f;
            fixed (World.SourceId* idPointer = ids)
            fixed (float* xPointer = xs)
            fixed (float* yPointer = ys)
            {
                World.Sync(world, idPointer, xPointer, yPointer, tiles);
            }

            var stopwatch = Stopwatch.StartNew();
            World.Process(world);
            stopwatch.Stop();
            var changed = World.ChangedTiles(world, 0, layer, null);
            return (changed, stopwatch.Elapsed.TotalMilliseconds);
        }
        finally
        {
            World.Free(world);
            Stamp.Free(stamp);
        }
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void ProcessCostWithRandomDirtyOrderVersusSortedDirtyOrder(int power)
    {
        for (var warm = 0; warm < 2; warm++) MeasureProcess(power, true);
        var random = double.MaxValue;
        var sorted = double.MaxValue;
        var changed = 0;
        for (var run = 0; run < 3; run++)
        {
            var r = MeasureProcess(power, true);
            var s = MeasureProcess(power, false);
            random = Math.Min(random, r.Milliseconds);
            sorted = Math.Min(sorted, s.Milliseconds);
            changed = r.Changed;
        }

        _output.WriteLine(
            $"AUDIT-SORT build={Build} power={power} dirtyTiles={changed} process_ms(random slot order)={random:F2} process_ms(sorted slot order)={sorted:F2} ratio={random / sorted:F2}");
        Assert.Equal(1 << (2 * (power - 5)), changed);
#if GI_NETSTANDARD
        if (power >= 12) Assert.True(random < sorted * 1.5, "insertion-sort SortSlots makes Process quadratic in dirty tiles");
#endif
    }

    private double MeasureExpiryWave(int count, bool shuffled)
    {
        var world = World.New();
        var stamp = Stamp.Box(2, 2, 10);
        try
        {
            Grid.New(world, 10, 0f, 0f, 1024f);
            var layer = Layer.New(world, Layer.Precision.Cell);
            var ids = new World.SourceId[count];
            for (var i = 0; i < count; i++)
            {
                var row = i / 1000 % 1000;
                ids[i] = World.Place(world, layer, 8f + i % 1000, 8f + row, stamp, 1);
            }

            World.Process(world);
            var order = new int[count];
            for (var i = 0; i < count; i++) order[i] = i;
            if (shuffled) Shuffle(order, 5);
            for (var i = 0; i < count; i++) World.Expire(world, ids[order[i]], 3);

            World.Process(world);
            World.Process(world);
            var stopwatch = Stopwatch.StartNew();
            World.Process(world);
            stopwatch.Stop();
            Assert.Equal(0, World.Query(world, 0, layer, 0, 0, 1024, 1024));
            return stopwatch.Elapsed.TotalMilliseconds;
        }
        finally
        {
            World.Free(world);
            Stamp.Free(stamp);
        }
    }

    [Theory]
    [InlineData(20000)]
    [InlineData(80000)]
    public void ExpiryWaveProcessCost(int count)
    {
        MeasureExpiryWave(count, true);
        var random = double.MaxValue;
        var sorted = double.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            random = Math.Min(random, MeasureExpiryWave(count, true));
            sorted = Math.Min(sorted, MeasureExpiryWave(count, false));
        }

        _output.WriteLine(
            $"AUDIT-EXPIRY build={Build} sources={count} expiry_tick_process_ms(random schedule order)={random:F2} ms(ascending schedule order)={sorted:F2}");
    }

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    public void ChangedTilesSinceNegativeCost(int power)
    {
        var world = World.New();
        var stamp = Stamp.Box(4, 4, 50);
        try
        {
            var size = 1 << power;
            Grid.New(world, power, 0f, 0f, size);
            var layer = Layer.New(world, Layer.Precision.Cell);
            var side = size >> 5;
            var tiles = side * side;
            for (var t = 0; t < tiles; t++)
            {
                var column = t % side;
                var row = t / side;
                World.Place(world, layer, column * 32 + 16.5f, row * 32 + 16.5f, stamp, 3);
            }
            World.Process(world);
            var destination = new int[tiles];
            double best = double.MaxValue;
            var count = 0;
            fixed (int* d = destination)
            {
                for (var run = 0; run < 5; run++)
                {
                    var stopwatch = Stopwatch.StartNew();
                    count = World.ChangedTiles(world, 0, layer, -1, d);
                    stopwatch.Stop();
                    best = Math.Min(best, stopwatch.Elapsed.TotalMilliseconds);
                }
            }

            _output.WriteLine(
                $"AUDIT-CHANGEDSINCE build={Build} power={power} tiles={count} ChangedTiles(since=-1)_ms={best:F3}");
            Assert.Equal(tiles, count);
        }
        finally
        {
            World.Free(world);
            Stamp.Free(stamp);
        }
    }
}
