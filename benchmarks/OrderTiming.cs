using System.Diagnostics;
using Gi;

internal static class OrderTiming
{
    public static int Run()
    {
        const int Count = 1_000_000;
        var world = World.New();
        var grid = Grid.New(world, 10, 0f, 0f, 1024f);
        var rng = new Random(13);
        var xs = new float[Count];
        var ys = new float[Count];
        for (var i = 0; i < Count; i++)
        {
            xs[i] = (float)rng.NextDouble() * 1023f;
            ys[i] = (float)rng.NextDouble() * 1023f;
        }

        var keys = new uint[Count];
        var permutation = new int[Count];
        var scratch = new ulong[2 * Count];
        var bestKeys = double.MaxValue;
        var bestSort = double.MaxValue;
        var disorder = 0f;
        for (var pass = 0; pass < 10; pass++)
        {
            var started = Stopwatch.GetTimestamp();
            Grid.SpatialKeys(world, grid, xs, ys, keys);
            var middle = Stopwatch.GetTimestamp();
            Grid.SortByKeys(keys, permutation, scratch);
            var finished = Stopwatch.GetTimestamp();
            disorder = Grid.Disorder(keys);
            if (pass < 2) continue;
            bestKeys = Math.Min(bestKeys, Stopwatch.GetElapsedTime(started, middle).TotalMilliseconds);
            bestSort = Math.Min(bestSort, Stopwatch.GetElapsedTime(middle, finished).TotalMilliseconds);
        }

        Console.WriteLine($"1M spatial keys {bestKeys:F2} ms, radix sort {bestSort:F2} ms, disorder of random order {disorder:F3}");
        World.Free(world);
        return 0;
    }
}
