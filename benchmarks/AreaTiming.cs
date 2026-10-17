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

        if (Environment.GetEnvironmentVariable("GI_EXC_ONLY") == "1")
        {
            ExclusionTiming(world, layer, box, rng);
            return 0;
        }

        var count = int.Parse(Environment.GetEnvironmentVariable("GI_COUNT") ?? "4096");
        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
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
                for (var i = 0; i < count; i++)
                {
                    ok &= World.TrySenseArea(world, layer, xs[i], ys[i], reach, out var total);
                    sum += total;
                }

                var elapsed = Stopwatch.GetElapsedTime(started).TotalNanoseconds / count;
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
                for (var i = 0; i < count; i++)
                {
                    World.TrySenseMax(world, layer, xs[i], ys[i], reach, out var value, out var bx, out var by);
                    m += value + (long)bx + (long)by;
                }

                var middle = Stopwatch.GetTimestamp();
                long n = 0;
                for (var i = 0; i < count; i++)
                {
                    World.TrySenseNearest(world, layer, xs[i], ys[i], reach, 14, out var value, out var nx, out var ny);
                    n += value + (long)nx + (long)ny;
                }

                var finished = Stopwatch.GetTimestamp();
                maxSum = m;
                nearSum = n;
                if (pass < 2) continue;
                bestMax = Math.Min(bestMax, Stopwatch.GetElapsedTime(started, middle).TotalNanoseconds / count);
                bestNearest = Math.Min(bestNearest, Stopwatch.GetElapsedTime(middle, finished).TotalNanoseconds / count);
            }

            Console.WriteLine($"  max  r={reach,3}: {bestMax,7:F1} ns each, checksum {maxSum}");
            Console.WriteLine($"  near r={reach,3}: {bestNearest,7:F1} ns each, checksum {nearSum}");
        }

        ExclusionTiming(world, layer, box, rng);
        PlaceTiming(box);
        World.Free(world);
        Stamp.Free(box);
        return ok ? 0 : 1;
    }

    private static void ExclusionTiming(byte world, byte layer, byte box, Random rng)
    {
        const int probes = 2048;
        var ids = new World.SourceId[probes];
        var xs = new float[probes];
        var ys = new float[probes];
        for (var i = 0; i < probes; i++)
        {
            xs[i] = 100f + (float)rng.NextDouble() * 800f;
            ys[i] = 100f + (float)rng.NextDouble() * 800f;
            ids[i] = World.Place(world, layer, xs[i], ys[i], box, 1);
        }

        World.Process(world);
        if (Environment.GetEnvironmentVariable("GI_EXC_ONLY") == "1")
        {
            long total = 0;
            for (var rep = 0; rep < 3000; rep++)
            for (var i = 0; i < probes; i++)
            {
                World.TrySense(world, layer, xs[i], ys[i], ids[i], out var v);
                total += v;
            }

            Console.WriteLine(total);
            return;
        }

        foreach (var reach in new[] { 16f, 64f })
        {
            double plain = double.MaxValue, excluded = double.MaxValue, point = double.MaxValue, pointExcluded = double.MaxValue;
            double gradient = double.MaxValue, gradientExcluded = double.MaxValue;
            long guard = 0;
            for (var pass = 0; pass < 10; pass++)
            {
                var t0 = Stopwatch.GetTimestamp();
                for (var i = 0; i < probes; i++)
                {
                    World.TrySenseArea(world, layer, xs[i], ys[i], reach, out var a);
                    guard += a;
                }

                var t1 = Stopwatch.GetTimestamp();
                for (var i = 0; i < probes; i++)
                {
                    World.TrySenseArea(world, layer, xs[i], ys[i], reach, ids[i], out var a);
                    guard += a;
                }

                var t2 = Stopwatch.GetTimestamp();
                for (var i = 0; i < probes; i++)
                {
                    World.TrySense(world, layer, xs[i], ys[i], out var v);
                    guard += v;
                }

                var t3 = Stopwatch.GetTimestamp();
                for (var i = 0; i < probes; i++)
                {
                    World.TrySense(world, layer, xs[i], ys[i], ids[i], out var v);
                    guard += v;
                }

                var t4 = Stopwatch.GetTimestamp();
                for (var i = 0; i < probes; i++)
                {
                    World.TrySenseGradient(world, layer, xs[i], ys[i], out var gx, out var gy);
                    guard += (long)gx + (long)gy;
                }

                var t5 = Stopwatch.GetTimestamp();
                for (var i = 0; i < probes; i++)
                {
                    World.TrySenseGradient(world, layer, xs[i], ys[i], ids[i], out var gx, out var gy);
                    guard += (long)gx + (long)gy;
                }

                var t6 = Stopwatch.GetTimestamp();
                if (pass < 2) continue;
                plain = Math.Min(plain, Stopwatch.GetElapsedTime(t0, t1).TotalNanoseconds / probes);
                excluded = Math.Min(excluded, Stopwatch.GetElapsedTime(t1, t2).TotalNanoseconds / probes);
                point = Math.Min(point, Stopwatch.GetElapsedTime(t2, t3).TotalNanoseconds / probes);
                pointExcluded = Math.Min(pointExcluded, Stopwatch.GetElapsedTime(t3, t4).TotalNanoseconds / probes);
                gradient = Math.Min(gradient, Stopwatch.GetElapsedTime(t4, t5).TotalNanoseconds / probes);
                gradientExcluded = Math.Min(gradientExcluded, Stopwatch.GetElapsedTime(t5, t6).TotalNanoseconds / probes);
            }

            Console.WriteLine($"exclusion r={reach,3}: area {plain,7:F1} -> {excluded,7:F1} ns; point {point:F1} -> {pointExcluded:F1}; gradient {gradient:F1} -> {gradientExcluded:F1} ({guard & 1})");
        }
    }

    private static unsafe void PlaceTiming(byte box)
    {
        var v = StampCatalog.Get(box);
        var best = double.MaxValue;
        long guard = 0;
        for (var pass = 0; pass < 8; pass++)
        {
            var started = Stopwatch.GetTimestamp();
            for (var i = 0; i < 1_000_000; i++)
            {
                TileBake.PlaceAnchored(256 * (100 + (i & 511)) + (i & 255), 256 * 300 + (i & 255), 256, 1024, v, 0, 256, Layer.Precision.Cell, out var p);
                guard += p.Px;
            }

            var elapsed = Stopwatch.GetElapsedTime(started).TotalNanoseconds / 1_000_000;
            if (pass >= 2) best = Math.Min(best, elapsed);
        }

        Console.WriteLine($"PlaceAnchored box: {best:F1} ns ({guard & 1})");
    }
}
