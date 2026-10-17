using System.Diagnostics;
using Gi;

internal static class AreaTiming
{
    public static int Run()
    {
        var world = World.New();
        Grid.New(world, 10, 0f, 0f, 1024f);
        var layer = Layer.New(world, Layer.Precision.Cell);
        var box = Stamp.Box(4, 4, 7);
        var rng = new Random(5);
        for (var i = 0; i < 400_000; i++)
            World.Place(world, layer, (float)(rng.NextDouble() * 1000.0 + 12.0), (float)(rng.NextDouble() * 1000.0 + 12.0), box, 1);
        World.Process(world);

        var Count = int.Parse(Environment.GetEnvironmentVariable("GI_COUNT") ?? "4096");
        var xs = new float[Count];
        var ys = new float[Count];
        for (var i = 0; i < Count; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 850.0 + 90.0);
            ys[i] = (float)(rng.NextDouble() * 850.0 + 90.0);
        }

        var ok = true;
        foreach (var reach in new[] { 4f, 16f, 32f, 64f })
        {
            var best = double.MaxValue;
            long checksum = 0;
            for (var pass = 0; pass < 12; pass++)
            {
                var started = Stopwatch.GetTimestamp();
                long sum = 0;
                for (var i = 0; i < Count; i++)
                {
                    ok &= World.TrySenseArea(world, layer, xs[i], ys[i], reach, out var total);
                    sum += total;
                }

                var elapsed = Stopwatch.GetElapsedTime(started).TotalNanoseconds / Count;
                checksum = sum;
                if (pass > 1 && elapsed < best) best = elapsed;
            }

            Console.WriteLine($"area r={reach,3}: {best,7:F1} ns each, checksum {checksum}");

            var bestMax = double.MaxValue;
            var bestNearest = double.MaxValue;
            long maxSum = 0;
            long nearSum = 0;
            for (var pass = 0; pass < 12; pass++)
            {
                var started = Stopwatch.GetTimestamp();
                long m = 0;
                for (var i = 0; i < Count; i++)
                {
                    World.TrySenseMax(world, layer, xs[i], ys[i], reach, out var value, out var bx, out var by);
                    m += value + (long)bx + (long)by;
                }

                var middle = Stopwatch.GetTimestamp();
                long n = 0;
                for (var i = 0; i < Count; i++)
                {
                    World.TrySenseNearest(world, layer, xs[i], ys[i], reach, 14, out var value, out var nx, out var ny);
                    n += value + (long)nx + (long)ny;
                }

                var finished = Stopwatch.GetTimestamp();
                maxSum = m;
                nearSum = n;
                if (pass < 2) continue;
                bestMax = Math.Min(bestMax, Stopwatch.GetElapsedTime(started, middle).TotalNanoseconds / Count);
                bestNearest = Math.Min(bestNearest, Stopwatch.GetElapsedTime(middle, finished).TotalNanoseconds / Count);
            }

            Console.WriteLine($"  max  r={reach,3}: {bestMax,7:F1} ns each, checksum {maxSum}");
            Console.WriteLine($"  near r={reach,3}: {bestNearest,7:F1} ns each, checksum {nearSum}");
        }

        World.Free(world);
        Stamp.Free(box);
        return ok ? 0 : 1;
    }
}
