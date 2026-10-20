using System.Diagnostics;
using Gi;

internal static unsafe class ChangedTiming
{
    public static int Run()
    {
        var world = World.New();
        var grid = Grid.New(world, 13, 0f, 0f, 8192f);
        var layer = Layer.New(world, Layer.Precision.Cell);
        var box = Stamp.Box(4, 4, 7);
        var rng = new Random(9);
        const int sources = 2000;
        var ids = new World.SourceId[sources];
        var xs = new float[sources];
        var ys = new float[sources];
        for (var i = 0; i < sources; i++)
        {
            xs[i] = 100f + (float)rng.NextDouble() * 7900f;
            ys[i] = 100f + (float)rng.NextDouble() * 7900f;
            ids[i] = World.Place(world, layer, xs[i], ys[i], box, 1);
        }

        World.Process(world);
        var tiles = stackalloc int[70_000];
        var best = double.MaxValue;
        var bestDisk = double.MaxValue;
        long checksum = 0;
        for (var frame = 0; frame < 60; frame++)
        {
            for (var i = 0; i < 20; i++)
            {
                var k = rng.Next(sources);
                xs[k] += 1f;
                World.Move(world, ids[k], xs[k], ys[k]);
            }

            World.Process(world);
            var tick = World.Tick(world);
            var started = Stopwatch.GetTimestamp();
            long count = 0;
            for (var rep = 0; rep < 50; rep++) count += World.ChangedTiles(world, grid, layer, tick - 1, tiles);
            var middle = Stopwatch.GetTimestamp();
            var hits = 0;
            for (var rep = 0; rep < 50; rep++)
            {
                var k = (frame * 50 + rep) % sources;
                if (World.Changed(world, layer, xs[k] + 600f, ys[k], 64f, tick - 3)) hits++;
            }

            var finished = Stopwatch.GetTimestamp();
            checksum += count + hits;
            if (frame < 10) continue;
            best = Math.Min(best, Stopwatch.GetElapsedTime(started, middle).TotalNanoseconds / 50);
            bestDisk = Math.Min(bestDisk, Stopwatch.GetElapsedTime(middle, finished).TotalNanoseconds / 50);
        }

        Console.WriteLine(
            $"ChangedTiles(since) on 65,536 tiles: {best / 1000.0:F1} µs; Changed(disk r=64): {bestDisk:F0} ns; checksum {checksum}");
        World.Free(world);
        Stamp.Free(box);
        return 0;
    }
}
