using System.Diagnostics;
using Gi;

internal static unsafe class RegionTiming
{
    public static int Run()
    {
        var world = World.New();
        var grid = Grid.New(world, 10, 0f, 0f, 1024f);
        var layer = Layer.New(world, Layer.Precision.Cell);
        var box = Stamp.Box(4, 4, 7);
        var rng = new Random(5);
        for (var i = 0; i < 400_000; i++)
            World.Place(world, layer, (float)(rng.NextDouble() * 1000.0 + 12.0), (float)(rng.NextDouble() * 1000.0 + 12.0), box, 1);
        World.Process(world);

        var buffer = new short[512 * 512];
        var xs = new int[512];
        var ys = new int[512];
        for (var i = 0; i < xs.Length; i++)
        {
            xs[i] = rng.Next(0, 700);
            ys[i] = rng.Next(0, 700);
        }

        fixed (short* destination = buffer)
        {
            foreach (var side in new[] { 32, 100, 256 })
            {
                var bestRegion = double.MaxValue;
                var bestRect = double.MaxValue;
                long guard = 0;
                for (var pass = 0; pass < 12; pass++)
                {
                    var started = Stopwatch.GetTimestamp();
                    for (var i = 0; i < xs.Length; i++)
                    {
                        World.QueryRegion(world, grid, layer, xs[i], ys[i], side, side, destination);
                        guard += destination[0] + destination[side * side - 1];
                    }

                    var middle = Stopwatch.GetTimestamp();
                    for (var i = 0; i < xs.Length; i++) guard += World.Query(world, grid, layer, xs[i], ys[i], side, side);
                    var finished = Stopwatch.GetTimestamp();
                    if (pass < 2) continue;
                    bestRegion = Math.Min(bestRegion, Stopwatch.GetElapsedTime(started, middle).TotalNanoseconds / xs.Length);
                    bestRect = Math.Min(bestRect, Stopwatch.GetElapsedTime(middle, finished).TotalNanoseconds / xs.Length);
                }

                Console.WriteLine($"region {side,3}x{side,-3}: QueryRegion {bestRegion,8:F0} ns ({bestRegion / (side * side):F3} ns/cell), Query(rect) {bestRect,7:F0} ns ({guard & 1})");
            }
        }

        World.Free(world);
        Stamp.Free(box);
        return 0;
    }
}
